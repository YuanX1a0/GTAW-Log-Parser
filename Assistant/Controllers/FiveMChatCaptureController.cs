using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Globalization;
using Assistant.Localization;

namespace Assistant.Controllers
{
    /// <summary>
    /// Captures the visible GTAW chat from FiveM's local NUI DevTools endpoint.
    /// This is a localhost-only, read-only connection while FiveM is running.
    /// </summary>
    public static class FiveMChatCaptureController
    {
        private const string DevToolsTargetsUrl = "http://127.0.0.1:13172/json";
        private const string RootUiUrl = "nui://game/ui/root.html";
        private const string ClientFrameUrl = "https://cfx-nui-client/web/index.html";
        private const int PollIntervalMilliseconds = 500;

        private static readonly object SyncRoot = new object();
        private static readonly string SessionDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTAW-Log-Parser-FiveM");
        private static readonly string SessionFile = Path.Combine(SessionDirectory, "current-session.txt");
        private static readonly NuiChatReader Reader = new NuiChatReader();

        private static Thread captureThread;
        private static bool runCapture;
        private static bool wasFiveMRunning;
        private static DateTime sessionStartedAt;
        private static List<string> previousVisibleLines = new List<string>();
        private static readonly Regex TimestampPrefix = new Regex(@"^\[(?<time>\d{1,2}:\d{2}:\d{2})\]\s+");

        private static readonly object TranslationLock = new object();
        private static readonly Queue<PendingTranslation> PendingTranslations = new Queue<PendingTranslation>();
        private static readonly Queue<FinishedTranslation> FinishedTranslations = new Queue<FinishedTranslation>();
        private static readonly HashSet<string> InFlightTranslations = new HashSet<string>();
        private static readonly List<Thread> TranslationWorkers = new List<Thread>();
        private const int MaxTranslationWorkers = 4;

        public static string SessionFilePath { get { return SessionFile; } }
        public static DateTime SessionStartedAt { get { return sessionStartedAt == DateTime.MinValue ? DateTime.Now : sessionStartedAt; } }

        public static void Initialize()
        {
            lock (SyncRoot)
            {
                if (captureThread != null && captureThread.IsAlive)
                    return;

                Directory.CreateDirectory(SessionDirectory);
                runCapture = true;
                captureThread = new Thread(CaptureWorker) { IsBackground = true, Name = "FiveM chat capture" };
                captureThread.Start();
            }
        }

        public static void Stop()
        {
            runCapture = false;
            lock (SyncRoot)
            {
                try
                {
                    // Disable the in-game click-to-translate feature on shutdown
                    Reader.UninstallTranslationHookIfNeeded(true);
                }
                catch
                {
                    // FiveM may already be closed; nothing else to clean up
                }
                Reader.Close();
            }
        }

        public static string ReadCapturedChat(bool removeTimestamps)
        {
            try
            {
                string chat;
                lock (SyncRoot)
                {
                    if (!File.Exists(SessionFile))
                        return string.Empty;

                    using (FileStream stream = new FileStream(SessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader reader = new StreamReader(stream))
                        chat = reader.ReadToEnd();
                }

                if (removeTimestamps)
                    chat = System.Text.RegularExpressions.Regex.Replace(chat, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty);

                return chat;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void CaptureWorker()
        {
            while (runCapture)
            {
                try
                {
                    bool fiveMRunning = AppController.IsFiveMRunning();
                    if (!fiveMRunning)
                    {
                        if (wasFiveMRunning)
                        {
                            lock (SyncRoot)
                            {
                                Reader.Close();
                                previousVisibleLines.Clear();
                            }
                        }

                        wasFiveMRunning = false;
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (!wasFiveMRunning)
                    {
                        lock (SyncRoot)
                        {
                            sessionStartedAt = DateTime.MinValue;
                            previousVisibleLines.Clear();
                            File.WriteAllText(SessionFile, string.Empty, new UTF8Encoding(false));
                        }
                        wasFiveMRunning = true;
                    }

                    lock (SyncRoot)
                    {
                        AppendNewLines(Reader.GetChatLines());
                        if (Properties.Settings.Default.TranslationEnabled)
                        {
                            try
                            {
                                ProcessTranslations();
                            }
                            catch
                            {
                                // Translation issues must never break chat capture
                            }
                        }
                        else
                        {
                            try
                            {
                                Reader.UninstallTranslationHookIfNeeded();
                            }
                            catch
                            {
                                // NUI may be reloading; retry on the next poll
                            }
                        }
                    }
                }
                catch
                {
                    lock (SyncRoot)
                    {
                        Reader.Close();
                    }
                    // The HUD can reload while a server is connecting. Keep trying quietly.
                }

                Thread.Sleep(PollIntervalMilliseconds);
            }
        }

        private static void ProcessTranslations()
        {
            List<PendingTranslation> pending = Reader.GetPendingTranslations();
            lock (TranslationLock)
            {
                foreach (PendingTranslation item in pending)
                {
                    if (InFlightTranslations.Add(item.Id))
                        PendingTranslations.Enqueue(item);
                }
            }

            StartTranslationWorkerIfNeeded();

            List<FinishedTranslation> finished = new List<FinishedTranslation>();
            lock (TranslationLock)
            {
                while (FinishedTranslations.Count > 0)
                    finished.Add(FinishedTranslations.Dequeue());
            }

            foreach (FinishedTranslation item in finished)
            {
                try
                {
                    Reader.ShowTranslation(item.Id, item.Segments, item.Success);
                }
                catch
                {
                    // NUI may be reloading; the pending id will be dropped
                }
                finally
                {
                    lock (TranslationLock)
                        InFlightTranslations.Remove(item.Id);
                }
            }
        }

        private static void StartTranslationWorkerIfNeeded()
        {
            lock (TranslationLock)
            {
                int alive = TranslationWorkers.Count(thread => thread.IsAlive);
                for (int i = alive; i < MaxTranslationWorkers; i++)
                {
                    Thread worker = new Thread(TranslationWorker) { IsBackground = true, Name = "GTAW chat translation " + i };
                    worker.Start();
                    TranslationWorkers.Add(worker);
                }
            }
        }

        private static void TranslationWorker()
        {
            while (runCapture)
            {
                PendingTranslation item = null;
                lock (TranslationLock)
                {
                    if (PendingTranslations.Count > 0)
                        item = PendingTranslations.Dequeue();
                }

                if (item == null)
                {
                    Thread.Sleep(200);
                    continue;
                }

                try
                {
                    List<TranslationSegment> translated = TranslateSegments(item.Segments);
                    lock (TranslationLock)
                        FinishedTranslations.Enqueue(new FinishedTranslation { Id = item.Id, Segments = translated, Success = true });
                }
                catch
                {
                    lock (TranslationLock)
                        FinishedTranslations.Enqueue(new FinishedTranslation { Id = item.Id, Segments = item.Segments, Success = false });
                }
            }
        }

        /// <summary>
        /// Translates all translatable segments of a message in a single API
        /// call (joined with newlines) so that coloured messages translate
        /// quickly. Timestamps, links and leading player names are kept
        /// untranslated. If the joined translation does not split back into
        /// the same number of lines, it falls back to per-segment translation.
        /// </summary>
        private static List<TranslationSegment> TranslateSegments(List<TranslationSegment> segments)
        {
            List<TranslationSegment> result = new List<TranslationSegment>(segments.Count);
            List<int> translateIndexes = new List<int>();
            StringBuilder combined = new StringBuilder();
            bool bodyStarted = false;
            string apiKey = string.Equals(Properties.Settings.Default.TranslationProvider, "DeepL", StringComparison.OrdinalIgnoreCase)
                ? Properties.Settings.Default.DeepLApiKey
                : Properties.Settings.Default.DeepSeekApiKey;

            for (int i = 0; i < segments.Count; i++)
            {
                TranslationSegment segment = segments[i];
                if (IsNonTranslatable(segment.Text) || (!bodyStarted && LooksLikeName(segment.Text)))
                {
                    result.Add(segment);
                    continue;
                }

                bodyStarted = true;
                translateIndexes.Add(i);
                if (combined.Length > 0)
                    combined.Append('\n');
                combined.Append(segment.Text);
                result.Add(null); // placeholder filled below
            }

            if (translateIndexes.Count > 0)
            {
                string[] translatedLines = null;
                try
                {
                    string translated = TranslationController.Translate(
                        combined.ToString(),
                        Properties.Settings.Default.TargetLanguage,
                        "auto",
                        Properties.Settings.Default.TranslationProvider,
                        apiKey,
                        Properties.Settings.Default.DeepSeekModel,
                        Properties.Settings.Default.TranslationPrompt,
                        Properties.Settings.Default.TranslationStyle);
                    translatedLines = translated.Split('\n');
                }
                catch
                {
                    translatedLines = null;
                }

                if (translatedLines != null && translatedLines.Length == translateIndexes.Count)
                {
                    for (int k = 0; k < translateIndexes.Count; k++)
                    {
                        int idx = translateIndexes[k];
                        result[idx] = new TranslationSegment
                        {
                            Text = translatedLines[k].Trim(),
                            Styles = segments[idx].Styles
                        };
                    }
                }
                else
                {
                    // Line count changed after translating - fall back to
                    // translating each segment individually to stay correct.
                    for (int k = 0; k < translateIndexes.Count; k++)
                    {
                        int idx = translateIndexes[k];
                        string translatedText = segments[idx].Text;
                        try
                        {
                            translatedText = TranslationController.Translate(
                                segments[idx].Text,
                                Properties.Settings.Default.TargetLanguage,
                                "auto",
                                Properties.Settings.Default.TranslationProvider,
                                apiKey,
                                Properties.Settings.Default.DeepSeekModel,
                                Properties.Settings.Default.TranslationPrompt,
                                Properties.Settings.Default.TranslationStyle);
                        }
                        catch
                        {
                            translatedText = segments[idx].Text;
                        }
                        result[idx] = new TranslationSegment { Text = translatedText, Styles = segments[idx].Styles };
                    }
                }
            }

            return result;
        }

        private static bool IsNonTranslatable(string text)
        {
            return Regex.IsMatch(text, @"^\[\d{1,2}:\d{2}(?::\d{2})?\]$") || Regex.IsMatch(text, @"^https?://\S+$");
        }

        private static bool LooksLikeName(string text)
        {
            return Regex.IsMatch(text, @"^[^:：]{1,32}[:：]$");
        }

        internal sealed class PendingTranslation
        {
            public string Id;
            public List<TranslationSegment> Segments;
        }

        internal sealed class FinishedTranslation
        {
            public string Id;
            public List<TranslationSegment> Segments;
            public bool Success;
        }

        internal sealed class TranslationSegment
        {
            public string Text;
            public string[] Styles;
        }

        private static void AppendNewLines(IList<string> visibleLines)
        {
            List<string> current = visibleLines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToList();
            if (current.Count == 0)
                return;

            int overlap = FindOverlap(previousVisibleLines, current);
            List<string> newLines = current.Skip(overlap).ToList();
            if (newLines.Count == 0)
            {
                previousVisibleLines = current;
                return;
            }

            DateTime capturedAt = DateTime.Now;
            DateTime sessionTimestamp = GetTimestamp(newLines[0], capturedAt);
            bool startOfSession = !File.Exists(SessionFile) || new FileInfo(SessionFile).Length == 0;
            using (FileStream stream = new FileStream(SessionFile, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                if (startOfSession)
                {
                    sessionStartedAt = sessionTimestamp;
                    writer.WriteLine(CreateSessionHeader(sessionTimestamp));
                }

                foreach (string line in newLines)
                    writer.WriteLine(AddTimestamp(line, capturedAt));
            }

            previousVisibleLines = current;
        }

        private static string CreateSessionHeader(DateTime timestamp)
        {
            string date = timestamp.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
            return string.Format(CultureInfo.InvariantCulture, "[DATE: {0} | TIME: {1}]", date, timestamp.ToString("HH:mm:ss"));
        }

        private static string AddTimestamp(string line, DateTime capturedAt)
        {
            if (TimestampPrefix.IsMatch(line))
                return line;

            return string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", capturedAt.ToString("HH:mm:ss"), line);
        }

        private static DateTime GetTimestamp(string line, DateTime fallback)
        {
            Match match = TimestampPrefix.Match(line);
            DateTime parsed;
            if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value, "H:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return fallback;

            return fallback.Date.Add(parsed.TimeOfDay);
        }

        private static int FindOverlap(IList<string> oldLines, IList<string> newLines)
        {
            int max = Math.Min(oldLines.Count, newLines.Count);
            for (int length = max; length > 0; length--)
            {
                bool matches = true;
                for (int i = 0; i < length; i++)
                {
                    if (oldLines[oldLines.Count - length + i] != newLines[i])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return length;
            }

            return 0;
        }

        internal sealed class NuiChatReader
        {
            private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            private ClientWebSocket socket;
            private int contextId;
            private int requestId;
            private bool _translationHookInstalled;
            private bool _lastAutoTranslateState;

            public List<string> GetChatLines()
            {
                EnsureConnected();
                const string expression = "JSON.stringify(Array.from(document.querySelectorAll('.chat__messages > li'), el => { const text = (el.innerText || '').replace(/\\s+/g, ' ').trim(); if (!text) return ''; const nodes = [el].concat(Array.from(el.querySelectorAll('*'))); let timestamp = ''; for (const node of nodes) { for (const attribute of Array.from(node.attributes || [])) { const match = String(attribute.value).match(/\\b\\d{1,2}:\\d{2}:\\d{2}\\b/); if (match) { timestamp = match[0]; break; } } if (!timestamp) { const match = String(getComputedStyle(node, '::before').content || '').match(/\\b\\d{1,2}:\\d{2}:\\d{2}\\b/); if (match) timestamp = match[0]; } if (timestamp) break; } return (timestamp ? '[' + timestamp + '] ' : '') + text; }).filter(Boolean))";
                IDictionary<string, object> result = Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", true }
                });

                IDictionary<string, object> runtimeResult = DictionaryValue(result, "result");
                string value = runtimeResult != null && runtimeResult.ContainsKey("value") ? runtimeResult["value"] as string : "[]";
                object[] values = serializer.DeserializeObject(value ?? "[]") as object[];
                return values == null ? new List<string>() : values.OfType<string>().Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            }

            public void InstallTranslationHook(string toastAutoOn, string toastAutoOff, string toastBulkDone, bool showToasts)
            {
                EnsureConnected();
                string mode = Properties.Settings.Default.TranslationDisplayMode == "replace" ? "replace" : "append";
                string bulkHotkey = serializer.Serialize(Properties.Settings.Default.TranslationBulkHotkey);
                string autoHotkey = serializer.Serialize(Properties.Settings.Default.AutoTranslateHotkey);
                string autoInitial = Properties.Settings.Default.AutoTranslate ? "true" : "false";
                string toastOn = serializer.Serialize(toastAutoOn ?? string.Empty);
                string toastOff = serializer.Serialize(toastAutoOff ?? string.Empty);
                string toastBulk = serializer.Serialize(toastBulkDone ?? string.Empty);
                string toastFlag = showToasts ? "true" : "false";
                string hook = "(function(){ if(window.__gtawTranslatorVersion === 12){ window.__gtawBulkHotkey = " + bulkHotkey + "; window.__gtawAutoHotkey = " + autoHotkey + "; window.__gtawShowToasts = " + toastFlag + "; window.__gtawToastAutoOn = " + toastOn + "; window.__gtawToastAutoOff = " + toastOff + "; window.__gtawToastBulk = " + toastBulk + "; return; } window.__gtawTranslatorVersion = 12; "
                    + "window.__gtawBulkHotkey = " + bulkHotkey + "; window.__gtawAutoHotkey = " + autoHotkey + "; window.__gtawAutoTranslate = " + autoInitial + "; window.__gtawShowToasts = " + toastFlag + "; window.__gtawToastAutoOn = " + toastOn + "; window.__gtawToastAutoOff = " + toastOff + "; window.__gtawToastBulk = " + toastBulk + "; "
                    + "if(window.__gtawTranslatorHandler){ document.removeEventListener('click', window.__gtawTranslatorHandler, true); } "
                    + "if(window.__gtawBulkHandler){ document.removeEventListener('keydown', window.__gtawBulkHandler, true); } "
                    + "if(window.__gtawAutoHandler){ document.removeEventListener('keydown', window.__gtawAutoHandler, true); } "
                    + "if(window.__gtawAutoObs){ window.__gtawAutoObs.disconnect(); window.__gtawAutoObs = null; } "
                    + "if(window.__gtawAutoRetry){ clearInterval(window.__gtawAutoRetry); } "
                    + "document.querySelectorAll('.chat__messages > li [data-gtaw-tid]').forEach(function(el){ if(el.parentNode){ el.parentNode.removeChild(el); } }); "
                    + "document.querySelectorAll('.chat__messages > li[data-gtaw-tid]').forEach(function(li){ if(li.__gtawOriginalHtml){ li.innerHTML = li.__gtawOriginalHtml; } li.removeAttribute('data-gtaw-tid'); delete li.__gtawOriginalHtml; delete li.__gtawSegs; delete li.__gtawTranslated; delete li.__gtawPendingId; }); "
                    + "window.__gtawPendingTranslations = []; "
                    + "function showToast(msg){ if(window.__gtawShowToasts === false) return; var t = document.getElementById('gtaw-toast'); if(!t){ t = document.createElement('div'); t.id = 'gtaw-toast'; t.style.cssText = 'position:fixed;top:14px;left:50%;transform:translateX(-50%);background:rgba(20,20,24,0.9);color:#fff;border:1px solid rgba(255,255,255,0.3);border-radius:4px;padding:6px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;z-index:20000;pointer-events:none;box-shadow:0 2px 10px rgba(0,0,0,0.5);white-space:nowrap;'; document.body.appendChild(t); } t.textContent = msg; t.style.display = 'block'; clearTimeout(t.__gtawToastTimer); t.__gtawToastTimer = setTimeout(function(){ t.style.display = 'none'; }, 2500); } window.__gtawToast = showToast; "
                    + "window.__gtawMode = '" + mode + "'; "
                    + "function collect(li){ var segs = []; function styleOf(el){ var cs = getComputedStyle(el); return [cs.color, cs.fontFamily, cs.fontSize, cs.fontStyle, cs.fontWeight]; } function walk(node, el){ for(var i = 0; i < node.childNodes.length; i++){ var c = node.childNodes[i]; if(c.nodeType === 3){ var t = (c.textContent || '').replace(/\\s+/g, ' ').trim(); if(t) segs.push({ t: t, s: styleOf(el) }); } else if(c.nodeType === 1){ if(c.children.length){ walk(c, c); } else { var t2 = (c.innerText || c.textContent || '').replace(/\\s+/g, ' ').trim(); if(t2) segs.push({ t: t2, s: styleOf(c) }); } } } } walk(li, li); return segs; } "
                    + "function enqueue(li, rid){ if(window.__gtawMode === 'replace'){ if(li.__gtawTranslated || li.__gtawPendingId) return; li.__gtawOriginalHtml = li.innerHTML; li.__gtawSegs = collect(li); li.__gtawPendingId = rid; li.setAttribute('data-gtaw-tid', rid); li.textContent = '...'; window.__gtawPendingTranslations.push({ id: rid, segs: li.__gtawSegs }); return; } if(li.__gtawPendingId || li.querySelector('[data-gtaw-tid]')) return; li.__gtawSegs = collect(li); li.__gtawPendingId = rid; var div = document.createElement('div'); div.setAttribute('data-gtaw-tid', rid); div.style.cssText = 'margin-top:2px;white-space:pre-wrap;'; var bs = li.__gtawSegs.length ? li.__gtawSegs[li.__gtawSegs.length - 1].s : null; var ph = document.createElement('span'); if(bs){ ph.style.color = bs[0]; ph.style.fontFamily = bs[1]; ph.style.fontSize = bs[2]; ph.style.fontStyle = bs[3]; ph.style.fontWeight = bs[4]; } ph.textContent = '...'; div.appendChild(ph); li.appendChild(div); window.__gtawPendingTranslations.push({ id: rid, segs: li.__gtawSegs }); } "
                    + "function processAdded(muts){ var items = []; for(var m = 0; m < muts.length; m++){ var added = muts[m].addedNodes; for(var i = 0; i < added.length; i++){ var n = added[i]; if(n.nodeType === 1 && n.matches && n.matches('.chat__messages > li')) items.push(n); } } if(!items.length) return; items.forEach(function(li){ if(!li.__gtawPendingId && !li.querySelector('[data-gtaw-tid]')){ var rid = 'gtawa-' + Date.now() + '-' + Math.random().toString(36).substr(2,6); enqueue(li, rid); } }); } "
                    + "function ensureAutoObserver(){ if(window.__gtawAutoTranslate){ if(!window.__gtawAutoObs){ var container = document.querySelector('.chat__messages'); if(!container) return; window.__gtawAutoObs = new MutationObserver(function(muts){ setTimeout(function(){ processAdded(muts); }, 50); }); window.__gtawAutoObs.observe(container, { childList: true }); } } else { if(window.__gtawAutoObs){ window.__gtawAutoObs.disconnect(); window.__gtawAutoObs = null; } } } "
                    + "function toggleAuto(v){ if(typeof v !== 'undefined'){ window.__gtawAutoTranslate = !!v; } else { window.__gtawAutoTranslate = !window.__gtawAutoTranslate; } ensureAutoObserver(); } window.__gtawToggleAuto = toggleAuto; "
                    + "var autoHandler = function(e){ var hot = window.__gtawAutoHotkey || 'Ctrl+Shift+F9'; var parts = hot.split('+'); var keyName = (parts.pop() || 'F9').toUpperCase(); var needsCtrl = false, needsShift = false; for(var i = 0; i < parts.length; i++){ var p = parts[i].trim(); if(p === 'Ctrl') needsCtrl = true; else if(p === 'Shift') needsShift = true; } var pressed = (e.key || '').toUpperCase(); if(pressed !== keyName) return; if(needsCtrl !== (e.ctrlKey || e.metaKey)) return; if(needsShift !== e.shiftKey) return; if(e.repeat) return; toggleAuto(); showToast(window.__gtawAutoTranslate ? window.__gtawToastAutoOn : window.__gtawToastAutoOff); }; "
                    + "window.__gtawAutoHandler = autoHandler; document.addEventListener('keydown', autoHandler, true); "
                    + "var handler = function(e){ var li = (e.target && e.target.closest) ? e.target.closest('.chat__messages > li') : null; if(!li) return; var text = (li.innerText || '').replace(/\\s+/g, ' ').trim(); if(!text) return; "
                    + "if(window.__gtawMode === 'replace'){ if(li.__gtawTranslated){ li.innerHTML = li.__gtawOriginalHtml; li.__gtawTranslated = false; return; } if(li.__gtawPendingId){ window.__gtawPendingTranslations.push({ id: li.__gtawPendingId, segs: li.__gtawSegs }); return; } var rid = 'gtaw-' + Date.now() + '-' + Math.random().toString(36).substr(2,6); enqueue(li, rid); return; } "
                    + "var existing = li.querySelector('[data-gtaw-tid]'); if(existing){ if(existing.textContent && existing.textContent !== '...'){ existing.style.display = existing.style.display === 'none' ? '' : 'none'; } else { window.__gtawPendingTranslations.push({ id: existing.getAttribute('data-gtaw-tid'), segs: li.__gtawSegs }); } return; } "
                    + "var id = 'gtaw-' + Date.now() + '-' + Math.random().toString(36).substr(2,6); enqueue(li, id); }; "
                    + "window.__gtawTranslatorHandler = handler; document.addEventListener('click', handler, true); "
                    + "var bulkHandler = function(e){ var hot = window.__gtawBulkHotkey || 'Ctrl+F9'; var ctrl = hot.indexOf('Ctrl') === 0; var keyName = (hot.split('+').pop() || 'F9').toUpperCase(); var pressed = (e.key || '').toUpperCase(); if(pressed !== keyName) return; if(ctrl !== (e.ctrlKey || e.metaKey)) return; if(e.repeat) return; var lis = document.querySelectorAll('.chat__messages > li'); var start = Math.max(0, lis.length - 10); for(var i = start; i < lis.length; i++){ var li = lis[i]; var txt = (li.innerText || '').replace(/\\s+/g, ' ').trim(); if(!txt) continue; if(li.__gtawPendingId || li.querySelector('[data-gtaw-tid]')) continue; var rid = 'gtawb-' + Date.now() + '-' + i + '-' + Math.random().toString(36).substr(2,6); enqueue(li, rid); } showToast(window.__gtawToastBulk); }; "
                    + "window.__gtawBulkHandler = bulkHandler; document.addEventListener('keydown', bulkHandler, true); "
                    + "ensureAutoObserver(); window.__gtawAutoRetry = setInterval(function(){ if(window.__gtawAutoTranslate && !window.__gtawAutoObs){ ensureAutoObserver(); } }, 2000); })();";
                IDictionary<string, object> hookResult = Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", hook },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
                if (hookResult != null && hookResult.ContainsKey("exceptionDetails"))
                    throw new IOException("Translation hook failed to install.");
                _translationHookInstalled = true;
            }

            /// <summary>
            /// Keeps the in-game auto-translate switch in sync with the setting.
            /// The hotkey toggles it in-game (persisted back to settings); changing
            /// the setting in the UI pushes the value back into the game.
            /// </summary>
            public void SyncAutoTranslate()
            {
                try
                {
                    EnsureConnected();
                    const string readExpression = "window.__gtawAutoTranslate === true";
                    IDictionary<string, object> result = Request("Runtime.evaluate", new Dictionary<string, object>
                    {
                        { "expression", readExpression },
                        { "contextId", contextId },
                        { "returnByValue", true }
                    });
                    IDictionary<string, object> runtimeResult = DictionaryValue(result, "result");
                    bool jsValue = runtimeResult != null && runtimeResult.ContainsKey("value") && Convert.ToBoolean(runtimeResult["value"]);
                    bool settingValue = Properties.Settings.Default.AutoTranslate;

                    if (jsValue != settingValue)
                    {
                        // The in-game value changed (hotkey pressed): persist it.
                        // Otherwise the setting was changed in the UI: push it into the game.
                        if (jsValue != _lastAutoTranslateState)
                        {
                            Properties.Settings.Default.AutoTranslate = jsValue;
                            _lastAutoTranslateState = jsValue;
                        }
                        else
                        {
                            string expression = "window.__gtawAutoTranslate = " + (settingValue ? "true" : "false") + "; "
                                + "if(window.__gtawToggleAuto) window.__gtawToggleAuto(" + (settingValue ? "true" : "false") + ");";
                            Request("Runtime.evaluate", new Dictionary<string, object>
                            {
                                { "expression", expression },
                                { "contextId", contextId },
                                { "returnByValue", false }
                            });
                            _lastAutoTranslateState = settingValue;
                        }
                    }
                }
                catch
                {
                    // NUI may be reloading; retry on the next poll
                }
            }

            /// <summary>
            /// Translates the in-game GTAW settings page (and any other NUI UI
            /// hosted in the same frame) by replacing matching English text
            /// nodes with the Chinese map provided. A MutationObserver keeps
            /// translating content whenever the settings page opens again.
            /// </summary>
            public void InstallSettingsPageTranslationHook(string mapJson)
            {
                EnsureConnected();
                string expression = "(function(){ if(window.__gtawStlV === 1){ return; } window.__gtawStlV = 1; var MAP = " + mapJson + "; "
                    + "function tr(root){ if(!root) return; var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT); var ns = []; while(w.nextNode()){ ns.push(w.currentNode); } for(var i = 0; i < ns.length; i++){ var n = ns[i]; var txt = (n.nodeValue || '').trim(); if(!txt) continue; if(MAP.hasOwnProperty(txt)){ n.nodeValue = MAP[txt]; } } } "
                    + "tr(document.body || document.documentElement); "
                    + "var obs = new MutationObserver(function(muts){ for(var i = 0; i < muts.length; i++){ var m = muts[i]; if(m.type === 'childList' && m.addedNodes){ for(var j = 0; j < m.addedNodes.length; j++){ var node = m.addedNodes[j]; if(node.nodeType === 1){ tr(node); } else if(node.nodeType === 3){ var t3 = (node.nodeValue || '').trim(); if(t3 && MAP.hasOwnProperty(t3)){ node.nodeValue = MAP[t3]; } } } } } }); "
                    + "obs.observe(document.body || document.documentElement, { childList: true, subtree: true }); window.__gtawStlObs = obs; })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Removes the in-game click translation hook so that clicking a chat
            /// message no longer produces a translation while the feature is disabled.
            /// </summary>
            public void UninstallTranslationHookIfNeeded(bool force = false)
            {
                if (!force && !_translationHookInstalled)
                    return;

                EnsureConnected();
                const string uninstallExpression = "if(window.__gtawTranslatorHandler){ document.removeEventListener('click', window.__gtawTranslatorHandler, true); } "
                    + "if(window.__gtawBulkHandler){ document.removeEventListener('keydown', window.__gtawBulkHandler, true); } "
                    + "if(window.__gtawAutoHandler){ document.removeEventListener('keydown', window.__gtawAutoHandler, true); } "
                    + "if(window.__gtawAutoObs){ window.__gtawAutoObs.disconnect(); window.__gtawAutoObs = null; } "
                    + "if(window.__gtawAutoRetry){ clearInterval(window.__gtawAutoRetry); } "
                    + "delete window.__gtawTranslatorHandler; delete window.__gtawBulkHandler; delete window.__gtawAutoHandler; delete window.__gtawToggleAuto; delete window.__gtawPendingTranslations; delete window.__gtawTranslatorVersion; delete window.__gtawMode; delete window.__gtawBulkHotkey; delete window.__gtawAutoHotkey; delete window.__gtawAutoTranslate; "
                    + "document.querySelectorAll('.chat__messages > li [data-gtaw-tid]').forEach(function(el){ if(el.parentNode){ el.parentNode.removeChild(el); } }); "
                    + "document.querySelectorAll('.chat__messages > li[data-gtaw-tid]').forEach(function(li){ if(li.__gtawOriginalHtml){ li.innerHTML = li.__gtawOriginalHtml; } li.removeAttribute('data-gtaw-tid'); delete li.__gtawOriginalHtml; delete li.__gtawSegs; delete li.__gtawTranslated; delete li.__gtawPendingId; });";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", uninstallExpression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
                _translationHookInstalled = false;
            }

            public List<PendingTranslation> GetPendingTranslations()
            {
                EnsureConnected();
                InstallTranslationHook(
                    Strings.ToastPrefix + Strings.ToastAutoOn,
                    Strings.ToastPrefix + Strings.ToastAutoOff,
                    Strings.ToastPrefix + Strings.ToastBulkDone,
                    Properties.Settings.Default.ShowGameToasts);
                SyncAutoTranslate();
                if (Properties.Settings.Default.SettingsPageTranslation)
                    InstallSettingsPageTranslationHook(SettingsPageTranslator.GetMapJson());

                // Keep the in-game mode in sync with the current setting so that
                // changing it does not require reinstalling the hook.
                string mode = Properties.Settings.Default.TranslationDisplayMode == "replace" ? "replace" : "append";
                string readExpression = "window.__gtawMode = '" + mode + "'; JSON.stringify(window.__gtawPendingTranslations || [])";
                IDictionary<string, object> result = Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", readExpression },
                    { "contextId", contextId },
                    { "returnByValue", true }
                });

                IDictionary<string, object> runtimeResult = DictionaryValue(result, "result");
                string value = runtimeResult != null && runtimeResult.ContainsKey("value") ? runtimeResult["value"] as string : "[]";
                object[] values = serializer.DeserializeObject(value ?? "[]") as object[];

                const string clearExpression = "window.__gtawPendingTranslations = [];";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", clearExpression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });

                List<PendingTranslation> list = new List<PendingTranslation>();
                if (values != null)
                {
                    foreach (object item in values)
                    {
                        IDictionary<string, object> entry = item as IDictionary<string, object>;
                        if (entry == null)
                            continue;
                        string id = entry.ContainsKey("id") ? entry["id"] as string : null;
                        if (string.IsNullOrEmpty(id))
                            continue;

                        List<TranslationSegment> segments = new List<TranslationSegment>();
                        object[] rawSegments = entry.ContainsKey("segs") ? entry["segs"] as object[] : null;
                        if (rawSegments != null)
                        {
                            foreach (object rawSegment in rawSegments)
                            {
                                IDictionary<string, object> segmentEntry = rawSegment as IDictionary<string, object>;
                                if (segmentEntry == null)
                                    continue;
                                string segmentText = segmentEntry.ContainsKey("t") ? segmentEntry["t"] as string : null;
                                if (string.IsNullOrWhiteSpace(segmentText))
                                    continue;

                                object[] rawStyles = segmentEntry.ContainsKey("s") ? segmentEntry["s"] as object[] : null;
                                string[] styles = new string[5];
                                if (rawStyles != null)
                                {
                                    for (int i = 0; i < styles.Length && i < rawStyles.Length; ++i)
                                        styles[i] = rawStyles[i] as string;
                                }

                                segments.Add(new TranslationSegment { Text = segmentText, Styles = styles });
                            }
                        }

                        if (segments.Count > 0)
                            list.Add(new PendingTranslation { Id = id, Segments = segments });
                    }
                }
                return list;
            }

            public void ShowTranslation(string id, List<TranslationSegment> segments, bool success)
            {
                EnsureConnected();
                string segmentsJson = serializer.Serialize(segments.Select(segment => new { t = segment.Text, s = segment.Styles }));
                string idJson = serializer.Serialize(id);
                string successJs = success ? "true" : "false";
                string expression = "(function(){ var el = document.querySelector('.chat__messages > li > div[data-gtaw-tid=\\\"' + " + idJson + " + '\\\"]'); var li = el ? el.parentNode : document.querySelector('.chat__messages > li[data-gtaw-tid=\\\"' + " + idJson + " + '\\\"]'); if(!li) return; var segs = " + segmentsJson + "; "
                    + "function span(seg, color){ var sp = document.createElement('span'); var st = seg.s || []; sp.style.color = color || st[0] || ''; sp.style.fontFamily = st[1] || ''; sp.style.fontSize = st[2] || ''; sp.style.fontStyle = st[3] || ''; sp.style.fontWeight = st[4] || ''; sp.textContent = seg.t || ''; return sp; } "
                    + "if(!(" + successJs + ")){ if(window.__gtawMode === 'replace'){ li.innerHTML = li.__gtawOriginalHtml || ''; li.__gtawTranslated = false; li.removeAttribute('data-gtaw-tid'); li.__gtawPendingId = null; } else if(el){ el.innerHTML = ''; el.appendChild(span({ t: '(translation failed)', s: segs.length ? segs[segs.length - 1].s : null }, '#ff6b6b')); } return; } "
                    + "if(window.__gtawMode === 'replace'){ li.innerHTML = ''; for(var i = 0; i < segs.length; i++){ li.appendChild(span(segs[i])); } li.__gtawTranslated = true; li.removeAttribute('data-gtaw-tid'); li.__gtawPendingId = null; if(el && el.parentNode){ el.parentNode.removeChild(el); } return; } "
                    + "if(!el) return; el.innerHTML = ''; if(segs.length){ var pre = document.createElement('span'); var st0 = segs[0].s || []; pre.style.color = '#ff6b6b'; pre.style.fontFamily = st0[1] || ''; pre.style.fontSize = st0[2] || ''; pre.style.fontStyle = st0[3] || ''; pre.style.fontWeight = st0[4] || ''; pre.textContent = 'T: '; el.appendChild(pre); } for(var k = 0; k < segs.length; k++){ el.appendChild(span(segs[k])); } })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Evaluates a JavaScript expression in the GTAW NUI isolated world.
            /// </summary>
            public void Evaluate(string expression)
            {
                EnsureConnected();
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Installs the in-game translator hook: a configurable hotkey (and a
            /// button next to the chat input) that toggles the persistent translator
            /// window. The window follows the chat input: it appears while the chat
            /// box is open and hides when the chat box closes, until the hotkey is
            /// pressed again to switch it off.
            /// </summary>
            public void InstallSendHook(string hotkey, string toastPrefix, string toastOn, string toastOff, bool showToasts)
            {
                EnsureConnected();
                string keyJson = serializer.Serialize(string.IsNullOrWhiteSpace(hotkey) ? "F9" : hotkey);
                string toastOnJson = serializer.Serialize((toastPrefix ?? string.Empty) + (toastOn ?? string.Empty));
                string toastOffJson = serializer.Serialize((toastPrefix ?? string.Empty) + (toastOff ?? string.Empty));
                string toastFlag = showToasts ? "true" : "false";
                string hook = "(function(){ if(window.__gtawSendVersion === 6){ window.__gtawSendHotkey = " + keyJson + "; window.__gtawShowToasts = " + toastFlag + "; window.__gtawToastSendOn = " + toastOnJson + "; window.__gtawToastSendOff = " + toastOffJson + "; return; } window.__gtawSendVersion = 6; "
                    + "window.__gtawSendActive = false; window.__gtawSendVisible = false; window.__gtawSendResult = null; window.__gtawSendHotkey = " + keyJson + "; window.__gtawSendDiag = null; window.__gtawShowToasts = " + toastFlag + "; window.__gtawToastSendOn = " + toastOnJson + "; window.__gtawToastSendOff = " + toastOffJson + "; "
                    + "if(window.__gtawSendKeyHandler){ document.removeEventListener('keydown', window.__gtawSendKeyHandler, true); } "
                    + "if(window.__gtawSendPlaceTimer){ clearInterval(window.__gtawSendPlaceTimer); } "
                    + "if(window.__gtawSendVisTimer){ clearInterval(window.__gtawSendVisTimer); } "
                    + "if(window.__gtawSendFastTimer){ clearInterval(window.__gtawSendFastTimer); } "
                    + "if(window.__gtawSendBtn && window.__gtawSendBtn.parentNode){ window.__gtawSendBtn.parentNode.removeChild(window.__gtawSendBtn); } "
                    + "var ovx = document.getElementById('gtaw-send-overlay'); if(ovx && ovx.parentNode){ ovx.parentNode.removeChild(ovx); } "
                    + "if(window.__gtawSendDragMove){ document.removeEventListener('mousemove', window.__gtawSendDragMove); } if(window.__gtawSendDragUp){ document.removeEventListener('mouseup', window.__gtawSendDragUp); } if(window.__gtawSendResizeMove){ document.removeEventListener('mousemove', window.__gtawSendResizeMove); } if(window.__gtawSendResizeUp){ document.removeEventListener('mouseup', window.__gtawSendResizeUp); } "
                    + "function showToast(msg){ if(window.__gtawShowToasts === false) return; var t = document.getElementById('gtaw-toast'); if(!t){ t = document.createElement('div'); t.id = 'gtaw-toast'; t.style.cssText = 'position:fixed;top:14px;left:50%;transform:translateX(-50%);background:rgba(20,20,24,0.9);color:#fff;border:1px solid rgba(255,255,255,0.3);border-radius:4px;padding:6px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;z-index:20000;pointer-events:none;box-shadow:0 2px 10px rgba(0,0,0,0.5);white-space:nowrap;'; document.body.appendChild(t); } t.textContent = msg; t.style.display = 'block'; clearTimeout(t.__gtawToastTimer); t.__gtawToastTimer = setTimeout(function(){ t.style.display = 'none'; }, 2500); } window.__gtawToast = showToast; "
                    + "function getInput(){ var ae = document.activeElement; if(ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA' || ae.isContentEditable)) return ae; var el = document.querySelector('.chat__input input') || document.querySelector('.chat-input input') || document.querySelector('.chat__input textarea') || document.querySelector('.chat-input textarea'); if(el) return el; var inputs = document.querySelectorAll('input, textarea'); for(var i = 0; i < inputs.length; i++){ var tag = inputs[i].tagName; var t = (inputs[i].type || 'text').toLowerCase(); if(tag === 'TEXTAREA' || (t !== 'checkbox' && t !== 'radio' && t !== 'button' && t !== 'submit' && t !== 'hidden')) return inputs[i]; } var ce = document.querySelector('[contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"\"]'); if(ce) return ce; return null; } "
                    + "function readVal(el){ return el.isContentEditable ? (el.textContent || '') : (el.value || ''); } "
                    + "function chatOpen(){ var ov = document.getElementById('gtaw-send-overlay'); var el = getInput(); if(!el) return false; if(ov && ov.contains(el)) return false; var st = window.getComputedStyle(el); if(st.display === 'none' || st.visibility === 'hidden') return false; var op = parseFloat(st.opacity); if(!isNaN(op) && op <= 0.02) return false; var r = el.getBoundingClientRect(); if(!(r.width > 0 && r.height > 0)) return false; var inChat = !!(el.closest && el.closest('.chat__input, .chat-input, [class*=\"chat\" i], [id*=\"chat\" i]')); if(!inChat && r.bottom < window.innerHeight * 0.30) return false; return true; } "
                    + "function syncTranslator(){ var active = !!window.__gtawSendActive; var open = chatOpen(); var ov = document.getElementById('gtaw-send-overlay'); if(active && open){ if(ov){ ov.style.display = ''; } window.__gtawSendVisible = true; } else { if(ov){ ov.style.display = 'none'; } window.__gtawSendVisible = false; } } "
                    + "window.__gtawSendSync = syncTranslator; "
                    + "window.__gtawSendToggle = function(){ window.__gtawSendActive = !window.__gtawSendActive; syncTranslator(); showToast(window.__gtawSendActive ? window.__gtawToastSendOn : window.__gtawToastSendOff); }; "
                    + "var keyHandler = function(e){ if(e.key === window.__gtawSendHotkey || e.code === window.__gtawSendHotkey){ e.preventDefault(); e.stopPropagation(); window.__gtawSendToggle(); return; } if(e.key === 'Escape'){ setTimeout(function(){ syncTranslator(); }, 120); } }; "
                    + "window.__gtawSendKeyHandler = keyHandler; document.addEventListener('keydown', keyHandler, true); "
                    + "var btn = document.createElement('button'); btn.type = 'button'; btn.textContent = 'T'; btn.title = 'Translator'; btn.style.cssText = 'background:#3a3a3a;color:#fff;border:1px solid #777;border-radius:4px;padding:0 8px;cursor:pointer;font-size:12px;line-height:20px;margin-left:4px;'; "
                    + "var btnHandler = function(ev){ ev.preventDefault(); ev.stopPropagation(); window.__gtawSendToggle(); }; "
                    + "btn.addEventListener('click', btnHandler); window.__gtawSendBtn = btn; window.__gtawSendBtnHandler = btnHandler; "
                    + "function placeBtn(){ if(btn.parentNode) return; var input = getInput(); if(input && input.parentNode){ input.parentNode.appendChild(btn); } else if(document.body){ btn.style.position = 'fixed'; btn.style.right = '8px'; btn.style.bottom = '8px'; btn.style.zIndex = '9999'; document.body.appendChild(btn); } } "
                    + "placeBtn(); window.__gtawSendPlaceTimer = setInterval(placeBtn, 2000); "
                    + "window.__gtawSendVisTimer = setInterval(syncTranslator, 400); "
                    + "window.__gtawSendFastTimer = setInterval(function(){ if(window.__gtawSendActive && !document.getElementById('gtaw-send-overlay')){ var el = getInput(); if(!el) return; var st = window.getComputedStyle(el); if(st.display === 'none' || st.visibility === 'hidden') return; var op = parseFloat(st.opacity); if(!isNaN(op) && op <= 0.02) return; var r = el.getBoundingClientRect(); if(!(r.width > 0 && r.height > 0)) return; var inChat = !!(el.closest && el.closest('.chat__input, .chat-input, [class*=\"chat\" i], [id*=\"chat\" i]')); if(!inChat && r.bottom < window.innerHeight * 0.30) return; syncTranslator(); } }, 100); })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", hook },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Removes the translator hotkey, button and overlay from the NUI.
            /// </summary>
            public void UninstallSendHook()
            {
                const string uninstallExpression = "if(window.__gtawSendKeyHandler){ document.removeEventListener('keydown', window.__gtawSendKeyHandler, true); } "
                    + "if(window.__gtawSendPlaceTimer){ clearInterval(window.__gtawSendPlaceTimer); } "
                    + "if(window.__gtawSendVisTimer){ clearInterval(window.__gtawSendVisTimer); } "
                    + "if(window.__gtawSendFastTimer){ clearInterval(window.__gtawSendFastTimer); } "
                    + "if(window.__gtawSendBtn && window.__gtawSendBtn.parentNode){ window.__gtawSendBtn.parentNode.removeChild(window.__gtawSendBtn); } "
                    + "if(window.__gtawSendDragMove){ document.removeEventListener('mousemove', window.__gtawSendDragMove); } if(window.__gtawSendDragUp){ document.removeEventListener('mouseup', window.__gtawSendDragUp); } if(window.__gtawSendResizeMove){ document.removeEventListener('mousemove', window.__gtawSendResizeMove); } if(window.__gtawSendResizeUp){ document.removeEventListener('mouseup', window.__gtawSendResizeUp); } "
                    + "var ov = document.getElementById('gtaw-send-overlay'); if(ov && ov.parentNode){ ov.parentNode.removeChild(ov); } var tt = document.getElementById('gtaw-toast'); if(tt && tt.parentNode){ tt.parentNode.removeChild(tt); } "
                    + "delete window.__gtawSendKeyHandler; delete window.__gtawSendBtn; delete window.__gtawSendBtnHandler; delete window.__gtawSendResult; delete window.__gtawSendToggle; delete window.__gtawSendSync; delete window.__gtawSendActive; delete window.__gtawSendVisible; delete window.__gtawSendHotkey; delete window.__gtawSendVersion; delete window.__gtawSendPlaceTimer; delete window.__gtawSendVisTimer; delete window.__gtawSendFastTimer; delete window.__gtawSendPos; delete window.__gtawShowToasts; delete window.__gtawToast; delete window.__gtawSendResizeMove; delete window.__gtawSendResizeUp;";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", uninstallExpression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Ensures the persistent translator window exists and is visible.
            /// The window is created once with the given texts and labels; later
            /// content updates go through UpdateSendOverlayOriginal/Translated.
            /// The window is never destroyed here - hiding is controlled by the
            /// in-game hook when the chat box opens/closes or the hotkey toggles.
            /// </summary>
            public void EnsureSendOverlay(string original, string translated, string title, string sendLabel, string applyLabel, string closeLabel, int initLeft, int initTop)
            {
                EnsureConnected();
                string originalJson = serializer.Serialize(original ?? string.Empty);
                string translatedJson = serializer.Serialize(translated ?? string.Empty);
                string titleJson = serializer.Serialize(title);
                string sendJson = serializer.Serialize(sendLabel);
                string applyJson = serializer.Serialize(applyLabel);
                string closeJson = serializer.Serialize(closeLabel);
                string initLeftJs = initLeft >= 0 ? initLeft.ToString(CultureInfo.InvariantCulture) : "-1";
                string initTopJs = initTop >= 0 ? initTop.ToString(CultureInfo.InvariantCulture) : "-1";
                string expression = "(function(){ var old = document.getElementById('gtaw-send-overlay'); if(old){ old.style.display = ''; return; } "
                    + "if(window.__gtawSendDragMove){ document.removeEventListener('mousemove', window.__gtawSendDragMove); } if(window.__gtawSendDragUp){ document.removeEventListener('mouseup', window.__gtawSendDragUp); } if(window.__gtawSendResizeMove){ document.removeEventListener('mousemove', window.__gtawSendResizeMove); } if(window.__gtawSendResizeUp){ document.removeEventListener('mouseup', window.__gtawSendResizeUp); } "
                    + "function getInput(){ var ae = document.activeElement; if(ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA' || ae.isContentEditable)) return ae; var el = document.querySelector('.chat__input input') || document.querySelector('.chat-input input') || document.querySelector('.chat__input textarea') || document.querySelector('.chat-input textarea'); if(el) return el; var inputs = document.querySelectorAll('input, textarea'); for(var i = 0; i < inputs.length; i++){ var tag = inputs[i].tagName; var t = (inputs[i].type || 'text').toLowerCase(); if(tag === 'TEXTAREA' || (t !== 'checkbox' && t !== 'radio' && t !== 'button' && t !== 'submit' && t !== 'hidden')) return inputs[i]; } var ce = document.querySelector('[contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"\"]'); if(ce) return ce; return null; } "
                    + "var W = 380; var H = 280; var left, top; "
                    + "var il = " + initLeftJs + "; var it = " + initTopJs + "; "
                    + "if(il >= 0 && it >= 0){ left = Math.max(4, Math.min(il, window.innerWidth - W - 4)); top = Math.max(4, Math.min(it, window.innerHeight - H - 4)); } else { var input = getInput(); var rect = null; if(input){ rect = input.getBoundingClientRect(); } if(rect && rect.width){ left = Math.max(4, Math.min(rect.left, window.innerWidth - W - 4)); top = Math.max(4, rect.top - H - 8); if(top < 4){ top = Math.min(rect.bottom + 8, window.innerHeight - H - 4); } } else { left = Math.max(4, window.innerWidth - W - 4); top = Math.max(4, window.innerHeight - H - 4); } } "
                    + "var ov = document.createElement('div'); ov.id = 'gtaw-send-overlay'; ov.style.cssText = 'position:fixed;left:' + left + 'px;top:' + top + 'px;width:' + W + 'px;z-index:10000;background:rgba(28,28,30,0.88);color:#eee;border:1px solid rgba(255,255,255,0.22);border-radius:6px;padding:12px;box-shadow:0 4px 18px rgba(0,0,0,0.6);font-family:Segoe UI,Arial,sans-serif;box-sizing:border-box;display:flex;flex-direction:column;'; "
                    + "var title = document.createElement('div'); title.id = 'gtaw-send-title'; title.textContent = " + titleJson + "; title.style.cssText = 'font-size:14px;font-weight:bold;margin-bottom:8px;cursor:move;user-select:none;padding:2px 0;flex:0 0 auto;'; "
                    + "var orig = document.createElement('div'); orig.id = 'gtaw-send-orig'; orig.textContent = " + originalJson + "; orig.style.cssText = 'background:rgba(255,255,255,0.08);padding:6px;border-radius:4px;margin-bottom:8px;white-space:pre-wrap;max-height:70px;overflow:auto;font-size:12px;opacity:0.85;flex:0 0 auto;'; "
                    + "var ta = document.createElement('textarea'); ta.id = 'gtaw-send-ta'; ta.value = " + translatedJson + "; ta.style.cssText = 'width:100%;height:90px;min-height:60px;flex:1 1 auto;background:rgba(255,255,255,0.1);color:#eee;border:1px solid rgba(255,255,255,0.3);border-radius:4px;padding:6px;box-sizing:border-box;resize:none;font-family:inherit;'; "
                    + "var row = document.createElement('div'); row.style.cssText = 'margin-top:8px;text-align:right;flex:0 0 auto;'; "
                    + "var apply = document.createElement('button'); apply.type = 'button'; apply.textContent = " + applyJson + "; apply.style.cssText = 'background:#2d6cdf;color:#fff;border:1px solid #4a86e8;border-radius:4px;padding:4px 14px;margin-left:8px;cursor:pointer;'; "
                    + "var send = document.createElement('button'); send.type = 'button'; send.textContent = " + sendJson + "; send.style.cssText = 'background:#1a8a4a;color:#fff;border:1px solid #2aa85e;border-radius:4px;padding:4px 14px;margin-left:8px;cursor:pointer;'; "
                    + "var close = document.createElement('button'); close.type = 'button'; close.textContent = " + closeJson + "; close.style.cssText = 'background:#444;color:#eee;border:1px solid #777;border-radius:4px;padding:4px 14px;margin-left:8px;cursor:pointer;'; "
                    + "apply.onclick = function(){ window.__gtawSendResult = { action: 'apply', text: ta.value }; }; "
                    + "send.onclick = function(){ window.__gtawSendResult = { action: 'send', text: ta.value }; }; "
                    + "close.onclick = function(){ window.__gtawSendActive = false; if(window.__gtawSendSync){ window.__gtawSendSync(); } window.__gtawSendResult = { action: 'close' }; }; "
                    + "row.appendChild(apply); row.appendChild(send); row.appendChild(close); ov.appendChild(title); ov.appendChild(orig); ov.appendChild(ta); ov.appendChild(row); document.body.appendChild(ov); "
                    + "var dragging = false, startX = 0, startY = 0, origLeft = 0, origTop = 0; "
                    + "title.addEventListener('mousedown', function(ev){ dragging = true; startX = ev.clientX; startY = ev.clientY; origLeft = ov.offsetLeft; origTop = ov.offsetTop; ev.preventDefault(); }); "
                    + "window.__gtawSendDragMove = function(ev){ if(!dragging) return; var dx = ev.clientX - startX; var dy = ev.clientY - startY; ov.style.left = Math.max(4, Math.min(window.innerWidth - ov.offsetWidth - 4, origLeft + dx)) + 'px'; ov.style.top = Math.max(4, Math.min(window.innerHeight - 30, origTop + dy)) + 'px'; }; "
                    + "window.__gtawSendDragUp = function(){ if(dragging){ dragging = false; window.__gtawSendPos = { left: ov.offsetLeft, top: ov.offsetTop }; } }; "
                    + "var resizing = false, rsx = 0, rsy = 0, rsw = 0, rsh = 0; "
                    + "function hitResize(ev){ var r = ov.getBoundingClientRect(); var ex = ev.clientX, ey = ev.clientY; var rightZone = r.right - ex <= 10 && ey >= r.top + 22 && ey <= r.bottom; var bottomZone = r.bottom - ey <= 10 && ex >= r.left + 22 && ex <= r.right; var cornerZone = r.right - ex <= 10 && r.bottom - ey <= 10; return rightZone || bottomZone || cornerZone; } "
                    + "ov.addEventListener('mousedown', function(ev){ if(ev.target === ta || ev.target.tagName === 'BUTTON' || ev.target === title) return; if(hitResize(ev)){ resizing = true; rsx = ev.clientX; rsy = ev.clientY; rsw = ov.offsetWidth; rsh = ov.offsetHeight; ev.preventDefault(); } }); "
                    + "window.__gtawSendResizeMove = function(ev){ if(!resizing) return; var w = Math.max(240, rsw + (ev.clientX - rsx)); var h = Math.max(150, rsh + (ev.clientY - rsy)); ov.style.width = w + 'px'; ov.style.height = h + 'px'; }; "
                    + "window.__gtawSendResizeUp = function(){ if(resizing){ resizing = false; window.__gtawSendPos = { left: ov.offsetLeft, top: ov.offsetTop }; } }; "
                    + "var grip = document.createElement('div'); grip.style.cssText = 'position:absolute;right:2px;bottom:2px;width:12px;height:12px;cursor:nwse-resize;background:linear-gradient(135deg,transparent 45%,rgba(255,255,255,0.5) 50%,transparent 55%);'; ov.appendChild(grip); "
                    + "document.addEventListener('mousemove', window.__gtawSendDragMove); document.addEventListener('mouseup', window.__gtawSendDragUp); "
                    + "document.addEventListener('mousemove', window.__gtawSendResizeMove); document.addEventListener('mouseup', window.__gtawSendResizeUp); "
                    + "window.__gtawSendPos = null; "
                    + "ta.focus(); setTimeout(function(){ ta.focus(); ta.select(); }, 100); })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Reads the current translator state: whether the feature is active
            /// (hotkey toggled on) and whether the window is currently visible
            /// (active and the in-game chat box is open).
            /// </summary>
            public IDictionary<string, object> TakeSendTranslatorState()
            {
                EnsureConnected();
                const string expression = "var p = window.__gtawSendPos || null; window.__gtawSendPos = null; JSON.stringify({ active: !!window.__gtawSendActive, visible: !!window.__gtawSendVisible, created: !!document.getElementById('gtaw-send-overlay'), pos: p });";
                IDictionary<string, object> result = Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", true }
                });
                IDictionary<string, object> runtimeResult = DictionaryValue(result, "result");
                string value = runtimeResult != null && runtimeResult.ContainsKey("value") ? runtimeResult["value"] as string : null;
                if (string.IsNullOrEmpty(value))
                    return null;
                try
                {
                    return new JavaScriptSerializer().DeserializeObject(value) as IDictionary<string, object>;
                }
                catch
                {
                    return null;
                }
            }

            /// <summary>
            /// Clears the text shown in the translator window (after a send or
            /// when the chat input becomes empty).
            /// </summary>
            public void ClearSendOverlay()
            {
                EnsureConnected();
                const string expression = "(function(){ var ov = document.getElementById('gtaw-send-overlay'); if(!ov) return; var o = document.getElementById('gtaw-send-orig'); if(o){ o.textContent = ''; } var t = document.getElementById('gtaw-send-ta'); if(t){ t.value = ''; } })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Updates the original-text block of the send overlay to match the
            /// current content of the in-game chat input.
            /// </summary>
            public void UpdateSendOverlayOriginal(string text)
            {
                EnsureConnected();
                string textJson = serializer.Serialize(text ?? string.Empty);
                string expression = "(function(){ var el = document.getElementById('gtaw-send-orig'); if(el){ el.textContent = " + textJson + "; } })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Updates the translation textarea of the send overlay.
            /// </summary>
            public void UpdateSendOverlayTranslated(string text)
            {
                EnsureConnected();
                string textJson = serializer.Serialize(text ?? string.Empty);
                string expression = "(function(){ var el = document.getElementById('gtaw-send-ta'); if(el){ el.value = " + textJson + "; } })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Reads the current text of the in-game chat input. Returns null
            /// when the input cannot be found.
            /// </summary>
            public string GetChatInputText()
            {
                try
                {
                    EnsureConnected();
                    const string expression = "(function(){ var ae = document.activeElement; if(ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA' || ae.isContentEditable)){ return ae.isContentEditable ? ae.textContent : ae.value; } var el = document.querySelector('.chat__input input') || document.querySelector('.chat-input input') || document.querySelector('.chat__input textarea') || document.querySelector('.chat-input textarea'); if(el) return el.isContentEditable ? el.textContent : el.value; return null; })();";
                    IDictionary<string, object> result = Request("Runtime.evaluate", new Dictionary<string, object>
                    {
                        { "expression", expression },
                        { "contextId", contextId },
                        { "returnByValue", true }
                    });
                    IDictionary<string, object> runtimeResult = DictionaryValue(result, "result");
                    return runtimeResult != null && runtimeResult.ContainsKey("value") ? runtimeResult["value"] as string : null;
                }
                catch
                {
                    return null;
                }
            }

            /// <summary>
            /// Puts text into the in-game chat input without submitting it.
            /// </summary>
            public void SetChatInputText(string message)
            {
                EnsureConnected();
                string messageJson = serializer.Serialize(message ?? string.Empty);
                string expression = "(function(){ var input = document.querySelector('.chat__input input') || document.querySelector('.chat-input input') || document.querySelector('.chat__input textarea') || document.querySelector('.chat-input textarea'); if(!input){ var inputs = document.querySelectorAll('input, textarea'); for(var i = 0; i < inputs.length; i++){ var tag = inputs[i].tagName; var t = (inputs[i].type || 'text').toLowerCase(); if(tag === 'TEXTAREA' || (t !== 'checkbox' && t !== 'radio' && t !== 'button' && t !== 'submit' && t !== 'hidden')){ input = inputs[i]; break; } } } if(!input){ input = document.querySelector('[contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"\"]'); } if(!input) return; "
                    + "if(input.isContentEditable){ input.textContent = " + messageJson + "; } else { var proto = input.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype; var setter = Object.getOwnPropertyDescriptor(proto, 'value').set; setter.call(input, " + messageJson + "); } "
                    + "input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true })); })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            /// <summary>
            /// Reads and clears the overlay result (send/cancel).
            /// </summary>
            public IDictionary<string, object> TakeSendResult()
            {
                EnsureConnected();
                const string expression = "var r = window.__gtawSendResult || null; window.__gtawSendResult = null; r;";
                IDictionary<string, object> result = Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", true }
                });
                IDictionary<string, object> runtimeResult = DictionaryValue(result, "result");
                return runtimeResult != null && runtimeResult.ContainsKey("value") ? runtimeResult["value"] as IDictionary<string, object> : null;
            }

            /// <summary>
            /// Puts a message into the in-game chat input and submits it.
            /// </summary>
            public void SendChatMessage(string message)
            {
                EnsureConnected();
                string messageJson = serializer.Serialize(message);
                string expression = "(function(){ var input = document.querySelector('.chat__input input') || document.querySelector('.chat-input input') || document.querySelector('.chat__input textarea') || document.querySelector('.chat-input textarea'); if(!input){ var inputs = document.querySelectorAll('input, textarea'); for(var i = 0; i < inputs.length; i++){ var tag = inputs[i].tagName; var t = (inputs[i].type || 'text').toLowerCase(); if(tag === 'TEXTAREA' || (t !== 'checkbox' && t !== 'radio' && t !== 'button' && t !== 'submit' && t !== 'hidden')){ input = inputs[i]; break; } } } if(!input){ input = document.querySelector('[contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"\"]'); } if(!input) return; "
                    + "if(input.isContentEditable){ input.textContent = " + messageJson + "; } else { var proto = input.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype; var setter = Object.getOwnPropertyDescriptor(proto, 'value').set; setter.call(input, " + messageJson + "); } "
                    + "input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true })); "
                    + "input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true })); "
                    + "input.dispatchEvent(new KeyboardEvent('keypress', { key: 'Enter', code: 'Enter', bubbles: true })); "
                    + "input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', bubbles: true })); "
                    + "if(input.form && input.form.requestSubmit){ try { input.form.requestSubmit(); } catch(e){} } })();";
                Request("Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", contextId },
                    { "returnByValue", false }
                });
            }

            public void Close()
            {
                if (socket != null)
                {
                    try { socket.Abort(); } catch { }
                    socket.Dispose();
                }

                socket = null;
                contextId = 0;
                requestId = 0;
            }

            private void EnsureConnected()
            {
                if (socket != null && socket.State == WebSocketState.Open && contextId != 0)
                    return;

                Close();
                IDictionary<string, object> target = GetRootTarget();
                string socketUrl = target["webSocketDebuggerUrl"] as string;
                if (string.IsNullOrWhiteSpace(socketUrl))
                    throw new IOException("FiveM NUI DevTools is unavailable.");

                socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    socket.ConnectAsync(new Uri(socketUrl), timeout.Token).GetAwaiter().GetResult();

                IDictionary<string, object> tree = Request("Page.getFrameTree", new Dictionary<string, object>());
                IDictionary<string, object> clientFrame = FindClientFrame(DictionaryValue(tree, "frameTree"));
                if (clientFrame == null || !clientFrame.ContainsKey("id"))
                    throw new IOException("GTAW HUD is not ready.");

                IDictionary<string, object> world = Request("Page.createIsolatedWorld", new Dictionary<string, object>
                {
                    { "frameId", clientFrame["id"] },
                    { "worldName", "gtaw-log-parser-reader" },
                    { "grantUniveralAccess", true }
                });
                if (!world.ContainsKey("executionContextId"))
                    throw new IOException("GTAW HUD context is unavailable.");

                contextId = Convert.ToInt32(world["executionContextId"]);
            }

            private IDictionary<string, object> GetRootTarget()
            {
                string json;
                using (HttpClientHandler handler = new HttpClientHandler { UseProxy = false })
                using (HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) })
                    json = client.GetStringAsync(DevToolsTargetsUrl).GetAwaiter().GetResult();

                object[] targets = serializer.DeserializeObject(json) as object[];
                if (targets != null)
                {
                    foreach (object item in targets)
                    {
                        IDictionary<string, object> target = item as IDictionary<string, object>;
                        if (target != null && target.ContainsKey("url") && (target["url"] as string) == RootUiUrl)
                            return target;
                    }
                }

                throw new IOException("FiveM root UI was not found.");
            }

            private IDictionary<string, object> Request(string method, IDictionary<string, object> parameters)
            {
                int id = ++requestId;
                string message = serializer.Serialize(new Dictionary<string, object>
                {
                    { "id", id },
                    { "method", method },
                    { "params", parameters }
                });
                byte[] data = Encoding.UTF8.GetBytes(message);
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                    while (true)
                    {
                        IDictionary<string, object> response = Receive(timeout.Token);
                        if (!response.ContainsKey("id") || Convert.ToInt32(response["id"]) != id)
                            continue;
                        if (response.ContainsKey("error"))
                            throw new IOException("FiveM NUI DevTools returned an error.");
                        return DictionaryValue(response, "result") ?? new Dictionary<string, object>();
                    }
                }
            }

            private IDictionary<string, object> Receive(CancellationToken token)
            {
                ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[8192]);
                using (MemoryStream stream = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = socket.ReceiveAsync(buffer, token).GetAwaiter().GetResult();
                        if (result.MessageType == WebSocketMessageType.Close)
                            throw new IOException("FiveM NUI DevTools connection closed.");
                        stream.Write(buffer.Array, buffer.Offset, result.Count);
                    } while (!result.EndOfMessage);

                    return serializer.DeserializeObject(Encoding.UTF8.GetString(stream.ToArray())) as IDictionary<string, object>;
                }
            }

            private static IDictionary<string, object> DictionaryValue(IDictionary<string, object> source, string key)
            {
                if (source == null || !source.ContainsKey(key))
                    return null;
                return source[key] as IDictionary<string, object>;
            }

            private static IDictionary<string, object> FindClientFrame(IDictionary<string, object> frameTree)
            {
                if (frameTree == null)
                    return null;

                IDictionary<string, object> frame = DictionaryValue(frameTree, "frame");
                if (frame != null && frame.ContainsKey("url") && (frame["url"] as string) == ClientFrameUrl)
                    return frame;

                object childrenObject;
                if (!frameTree.TryGetValue("childFrames", out childrenObject))
                    return null;

                IEnumerable children = childrenObject as IEnumerable;
                if (children == null)
                    return null;

                foreach (object child in children)
                {
                    IDictionary<string, object> found = FindClientFrame(child as IDictionary<string, object>);
                    if (found != null)
                        return found;
                }

                return null;
            }
        }

        /// <summary>
        /// English -&gt; Simplified Chinese map used to translate the GTAW
        /// in-game settings page. Keys are exact text node values.
        /// </summary>
        public static class SettingsPageTranslator
        {
            private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            private static string cachedZhCn;
            private static string cachedZhTw;
            private static string cachedEs;

            /// <summary>
            /// Returns the English -&gt; target language map as JSON for the
            /// currently selected application language (zh-CN / zh-TW / es-ES).
            /// English (or any other language) returns an empty map.
            /// </summary>
            public static string GetMapJson()
            {
                string code = Properties.Settings.Default.LanguageCode;
                if (string.Equals(code, "zh-TW", StringComparison.OrdinalIgnoreCase))
                    return cachedZhTw ?? (cachedZhTw = Serializer.Serialize(MapZhTw));
                if (string.Equals(code, "es-ES", StringComparison.OrdinalIgnoreCase))
                    return cachedEs ?? (cachedEs = Serializer.Serialize(MapEs));
                if (string.Equals(code, "zh-CN", StringComparison.OrdinalIgnoreCase))
                    return cachedZhCn ?? (cachedZhCn = Serializer.Serialize(MapZhCn));
                return "{}";
            }

            private static readonly Dictionary<string, string> MapZhCn = new Dictionary<string, string>
            {
                { "INVENTORY", "背包" },
                { "Logged in as", "登录为" },
                { "General", "通用" },
                { "Audio", "音频" },
                { "Chat", "聊天" },
                { "HUD", "HUD" },
                { "Hotkeys", "按键绑定" },
                { "Mapping", "键位" },
                { "Admin", "管理" },
                { "SAVE", "保存" },
                { "All", "全部" },
                { "GENERAL", "通用" },
                { "UI", "界面" },
                { "Gameplay", "游戏玩法" },
                { "Animations", "动画" },
                { "Vehicle", "车辆" },
                { "Environment", "环境" },
                { "Nametag", "名牌" },
                { "TV", "电视" },
                { "GAMEPLAY", "游戏玩法" },
                { "ANIMATIONS", "动画" },
                { "VEHICLE", "车辆" },
                { "ENVIRONMENT", "环境" },
                { "NAMETAG", "名牌" },
                { "Chat Hand Gestures", "聊天手势" },
                { "Play upper-body hand animations when you speak in IC chat. None disables gestures.", "在 IC 聊天中说话时播放上半身手势动画。选择无可禁用。" },
                { "None", "无" },
                { "Default", "默认" },
                { "Aggressive", "激进" },
                { "Gentle", "温和" },
                { "24-hour format in the UI", "界面使用 24 小时制" },
                { "Admins list UI", "管理员列表界面" },
                { "Automatic Close of Tip UI", "自动关闭提示界面" },
                { "Enabling this option will allow Tip UI (Paychek, Bus Info, etc...) to automatically close.", "启用后提示界面（工资、公交信息等）将自动关闭。" },
                { "Click outside phone to close", "点击手机外部关闭" },
                { "Darkmode", "深色模式" },
                { "Toggle darkmode for some menus", "切换部分菜单的深色模式" },
                { "Death UI", "死亡界面" },
                { "Desktop Notifications (when tabbed)", "桌面通知（窗口后台时）" },
                { "Send some Desktop notifications when game is tabbed (PMs, Phone alerts, Alarm Alerts, Panic Button)", "游戏窗口在后台时发送桌面通知（私信、手机提醒、警报、紧急按钮）" },
                { "Disable tips UI", "禁用提示界面" },
                { "While this option being enabled, you will bring back old behaviour of certain scripts, like examine.", "启用后将恢复某些脚本的旧行为，例如检查。" },
                { "Display player ping in /id", "在 /id 中显示玩家延迟" },
                { "Faction info in chat", "聊天中显示组织信息" },
                { "When on, /fon prints the online list in chat. When off, /fon opens the faction online UI.", "开启时 /fon 在聊天中打印在线列表；关闭时 /fon 打开组织在线界面。" },
                { "Inventory in UI", "界面中显示背包" },
                { "When off, /inv and similar commands print your items in chat instead of opening the inventory panel.", "关闭时 /inv 等命令在聊天中打印物品，而不是打开背包面板。" },
                { "Phone Size", "手机尺寸" },
                { "Property notifications", "地产通知" },
                { "Show indexes for items", "显示物品序号" },
                { "UI Size", "界面尺寸" },
                { "Legacy clothing menu", "旧版服装菜单" },
                { "Use the old slider-based clothing store instead of the picture wardrobe.", "使用基于滑杆的旧版服装商店，而不是图片衣柜。" },
                { "Auto hairtie", "自动发圈" },
                { "Automatically apply a hairtie when wearing a hat or helmet.", "戴帽子或头盔时自动佩戴发圈。" },
                { "Hat (style 2)", "帽子（样式 2）" },
                { "Custom (style 1)", "自定义（样式 1）" },
                { "Custom (style 2)", "自定义（样式 2）" },
                { "Custom (style 3)", "自定义（样式 3）" },
                { "Custom (style 4)", "自定义（样式 4）" },
                { "Custom (style 5)", "自定义（样式 5）" },
                { "Custom (style 6)", "自定义（样式 6）" },
                { "Custom (style 7)", "自定义（样式 7）" },
                { "Custom (style 8)", "自定义（样式 8）" },
                { "Automatic callsign", "自动呼号" },
                { "Pastes your CAD callsign at the beginning of every message.", "在每条消息开头粘贴你的 CAD 呼号。" },
                { "Dark flashbangs", "深色闪光弹" },
                { "Helps players with photosensitive epilepsy; the effect lasts longer.", "帮助光敏性癫痫玩家；效果持续时间更长。" },
                { "Disable Air Traffic Control radio", "禁用空中交通管制电台" },
                { "When enabled, the ATC radio channel will be disabled.", "启用后 ATC 电台频道将被禁用。" },
                { "Drug Effects", "药物效果" },
                { "Enables or disables drugs visual effects.", "启用或禁用药物视觉效果。" },
                { "Focus Mode", "专注模式" },
                { "Focus Mode: confirm on release", "专注模式：松开确认" },
                { "Releasing the focus key selects the highlighted option. When disabled, options are selected by clicking.", "松开专注键选择高亮选项；禁用后通过点击选择。" },
                { "Phone keybind function", "手机按键功能" },
                { "Show money on screen", "在屏幕上显示金钱" },
                { "Show vehicle details automatically", "自动显示车辆详情" },
                { "Displays vehicle details when you enter a vehicle.", "进入车辆时显示车辆详情。" },
                { "Advanced Animations", "高级动画" },
                { "Head Movement", "头部动作" },
                { "If disabled, character's head will not move. May impact performance negatively on some systems.", "禁用后角色头部不会移动。在某些系统上可能影响性能。" },
                { "Lip Movement", "嘴唇动作" },
                { "Phone Animations", "手机动画" },
                { "Anti-reverse", "防倒溜" },
                { "Prevents the vehicle from rolling backward after braking until you press reverse again.", "防止车辆刹车后向后溜车，直到你再次挂入倒挡。" },
                { "Automatic Break Lights", "自动刹车灯" },
                { "Automatically turns on your vehicle's brake lights.", "自动开启车辆刹车灯。" },
                { "Autopilot function", "自动驾驶功能" },
                { "Smooth Throttle", "平稳油门" },
                { "Smoother vehicle acceleration to help prevent skidding.", "更平滑的车辆加速，帮助防止打滑。" },
                { "Vehicle Exit Safety", "下车安全" },
                { "Require a double-tap to confirm exit when moving over 20 MPH or when aircraft are airborne.", "速度超过 20 英里/小时或飞行器在空中时，需要双击确认下车。" },
                { "Disable posters in interiors", "禁用室内海报" },
                { "Disable posters in main dimension", "禁用主维度海报" },
                { "Disable posters in vehicles", "禁用车辆内海报" },
                { "Posters won't render while you're in a vehicle.", "在车辆内时海报不会渲染。" },
                { "Enable graffiti rendering", "启用涂鸦渲染" },
                { "Can ruin the graffiti preview while spraying new graffiti.", "喷涂新涂鸦时可能破坏涂鸦预览。" },
                { "Display admin nametags", "显示管理员名牌" },
                { "Display Emote over head", "头顶显示表情" },
                { "Display nametag above own head", "自己头顶显示名牌" },
                { "Display pet names", "显示宠物名字" },
                { "This option displays/hides pet nametags from your screen.", "此选项在屏幕上显示或隐藏宠物名牌。" },
                { "Player IDs in nametags", "名牌中显示玩家 ID" },
                { "Target Indicator", "目标指示器" },
                { "TV Brightness", "电视亮度" },
                { "Default TV brightness.", "默认电视亮度。" },
                { "TV Volume", "电视音量" },
                { "Default TV volume on login.", "登录时的默认电视音量。" },
                { "ALPR Volume", "车牌识别音量" },
                { "Audio Emitters Volume", "音频发射器音量" },
                { "Doorbell Volume", "门铃音量" },
                { "HQ Effect Volume", "总部特效音量" },
                { "Knock Volume", "敲门音量" },
                { "Microphone Indicator", "麦克风指示器" },
                { "Show the on-screen microphone indicator while you are transmitting.", "说话时在屏幕上显示麦克风指示器。" },
                { "Mute ambient sound", "静音环境音效" },
                { "Mutes world ambience (city effects, construction sites, ...). Same as /togambience.", "静音世界环境音（城市效果、施工现场等）。等同于 /togambience。" },
                { "Mute Interior Radios", "静音室内收音机" },
                { "When enabled, the radio will be muted in interiors.", "启用后，室内收音机将被静音。" },
                { "Panic Effect Volume", "紧急按钮特效音量" },
                { "PCAD Effect Volume", "PCAD 特效音量" },
                { "Radio Volume", "电台音量" },
                { "Receive volume for radio voice, independent of proximity voice.", "电台语音接收音量，与近距离语音独立。" },
                { "Voice: Push-to-talk", "语音：按键说话" },
                { "Hold GTA's Push-to-Talk key (N by default, rebindable in Settings > Key Bindings) to transmit proximity voice. Disabled, the mic is voice-activated (open mic).", "按住 GTA 的按键说话键（默认为 N，可在设置 > 按键绑定中更改）以传输近距离语音。禁用后，麦克风为语音激活（开放麦克风）。" },
                { "New Chat", "新聊天" },
                { "Brighter OOC", "更亮的 OOC" },
                { "Chat Font", "聊天字体" },
                { "Chat Font Size", "聊天字号" },
                { "Chat Size", "聊天大小" },
                { "Chat Width", "聊天宽度" },
                { "Chatbox Opacity", "聊天框不透明度" },
                { "Fade inactive chat", "淡出非活动聊天" },
                { "Highlight full /me", "高亮完整 /me" },
                { "Timestamps", "时间戳" },
                { "Toggle Faction in chat", "聊天中切换组织" },
                { "NEW CHAT", "新聊天" },
                { "Autopunctuate bold", "自动标点加粗" },
                { "Autopunctuate italic", "自动标点斜体" },
                { "Focus background", "聚焦背景" },
                { "Mention notifications", "提及通知" },
                { "How @playerID mentions are expanded in your outgoing chat.", "你的外发聊天中 @玩家ID 提及如何展开。" },
                { "First Name", "名字" },
                { "Last Name", "姓氏" },
                { "Full Name", "全名" },
                { "Show highlights", "显示高亮" },
                { "Use new chat", "使用新聊天" },
                { "GTAW HUD", "GTAW HUD" },
                { "Alternative Speedometer", "备选速度表" },
                { "Advanced address", "高级地址" },
                { "Advanced HUD", "高级 HUD" },
                { "Show the minimap menu bar (map / vehicle / GPS / audio) over the minimap.", "在小地图上方显示小地图菜单栏（地图 / 车辆 / GPS / 音频）。" },
                { "Fade inactive HUD", "淡出非活动 HUD" },
                { "Fades the entire HUD out after about 20 seconds without money, weather, address, or tip updates.", "约 20 秒没有金钱、天气、地址或提示更新后，整个 HUD 淡出。" },
                { "HUD highlight color", "HUD 高亮颜色" },
                { "Minimalist HUD", "极简 HUD" },
                { "Removes the dark backgrounds from HUD elements so cash, location, compass, and notifications show as transparent text and icons. Reposition widgets with /hudlayout.", "移除 HUD 元素的深色背景，使金钱、位置、指南针和通知显示为透明文字和图标。使用 /hudlayout 重新调整小部件位置。" },
                { "Show address", "显示地址" },
                { "Show bank", "显示银行余额" },
                { "Show the bank balance (top right).", "显示银行余额（右上角）。" },
                { "Show cash", "显示现金" },
                { "Show the cash amount (top right).", "显示现金金额（右上角）。" },
                { "Show compass", "显示指南针" },
                { "Show the compass (cardinal direction).", "显示指南针（方位方向）。" },
                { "Show health bars", "显示生命条" },
                { "Show the health / armour / helmet / oxygen bars.", "显示生命 / 护甲 / 头盔 / 氧气条。" },
                { "Show location", "显示位置" },
                { "Show the area & street bar.", "显示区域与街道栏。" },
                { "Show minimap", "显示小地图" },
                { "Show the minimap (radar).", "显示小地图（雷达）。" },
                { "Show money", "显示金钱" },
                { "Show server info", "显示服务器信息" },
                { "Show speedometer", "显示速度表" },
                { "Show the speedometer.", "显示速度表。" },
                { "Show weapon", "显示武器" },
                { "Show the equipped weapon icon, ammo & name (top right).", "显示已装备武器的图标、弹药和名称（右上角）。" },
                { "Temperature in Fahrenheit", "温度显示为华氏" },
                { "Weather placement", "天气显示位置" },
                { "Where time & weather appear: above the minimap, inside the location bar, or hidden.", "时间和天气显示位置：小地图上方、位置栏内或隐藏。" },
                { "Above minimap", "小地图上方" },
                { "Location bar", "位置栏" },
                { "Hidden", "隐藏" },
                { "Damage screen flash", "受伤屏幕闪烁" },
                { "Red edge flash on your screen when you take damage.", "受到伤害时屏幕边缘闪红。" },
                { "ALTERNATIVE SPEEDOMETER", "备选速度表" },
                { "Background opacity", "背景不透明度" },
                { "Max Altitude (ft)", "最大高度（英尺）" },
                { "Show dashboard icons", "显示仪表盘图标" },
                { "Show the dashboard icon row (indicators, lights, engine, lock, handbrake\u2026).", "显示仪表盘图标行（指示灯、车灯、引擎、锁、手刹等）。" },
                { "Show fuel gauge", "显示油量表" },
                { "Show the fuel gauge on the speedometer.", "在速度表上显示油量表。" },
                { "Show gear", "显示档位" },
                { "Show the gear box on the speedometer. When off, speed unit + odometer move up in line with the speed.", "在速度表上显示档位框。关闭后，速度单位和里程表随速度上移对齐。" },
                { "Show RPM meter", "显示转速表" },
                { "Show the RPM bar on the speedometer.", "在速度表上显示转速条。" },
                { "Use Imperial units", "使用英制单位" },
                { "Action Key", "互动按键" },
                { "Animation UI Key", "动画界面按键" },
                { "Animation Wheel Key", "动画轮盘按键" },
                { "Blindfold Key", "蒙眼按键" },
                { "CAD Key", "CAD 按键" },
                { "Close Tip Key", "关闭提示按键" },
                { "Crawl Key", "匍匐按键" },
                { "Crouch Key", "蹲伏按键" },
                { "Focus Mode Key", "专注模式按键" },
                { "Garage Key", "车库按键" },
                { "K9 Door Key", "K9 车门按键" },
                { "Lock Key", "锁车按键" },
                { "Open Inventory Key", "打开背包按键" },
                { "Personal Assistant Key", "私人助理按键" },
                { "Phone Key", "手机按键" },
                { "Playerlist Key", "玩家列表按键" },
                { "Ragdoll Key", "布娃娃按键" },
                { "Tackle Key", "扑倒按键" },
                { "Voice: talk range key", "语音：说话范围按键" },
                { "Auto Pilot", "自动驾驶" },
                { "Cruise Control", "定速巡航" },
                { "Engine", "引擎" },
                { "Handbrake", "手刹" },
                { "Headlights", "车灯" },
                { "Left Indicator", "左转向灯" },
                { "Right Indicator", "右转向灯" },
                { "Seatbelt", "安全带" },
                { "Siren", "警笛" },
                { "Siren ELS Down", "警笛 ELS 降低" },
                { "Siren ELS Up", "警笛 ELS 升高" },
                { "Throw Gun", "扔枪" },
                { "Transit Vehicle Door", "摆渡车辆车门" }
            };

            private static readonly Dictionary<string, string> MapZhTw = new Dictionary<string, string>
            {
                { "INVENTORY", "背包" },
                { "Logged in as", "登入為" },
                { "General", "通用" },
                { "Audio", "音訊" },
                { "Chat", "聊天" },
                { "HUD", "HUD" },
                { "Hotkeys", "按鍵綁定" },
                { "Mapping", "鍵位" },
                { "Admin", "管理" },
                { "SAVE", "儲存" },
                { "All", "全部" },
                { "GENERAL", "通用" },
                { "UI", "介面" },
                { "Gameplay", "遊戲玩法" },
                { "Animations", "動畫" },
                { "Vehicle", "車輛" },
                { "Environment", "環境" },
                { "Nametag", "名牌" },
                { "TV", "電視" },
                { "GAMEPLAY", "遊戲玩法" },
                { "ANIMATIONS", "動畫" },
                { "VEHICLE", "車輛" },
                { "ENVIRONMENT", "環境" },
                { "NAMETAG", "名牌" },
                { "Chat Hand Gestures", "聊天手勢" },
                { "Play upper-body hand animations when you speak in IC chat. None disables gestures.", "在 IC 聊天中說話時播放上半身手勢動畫。選擇無可禁用。" },
                { "None", "無" },
                { "Default", "預設" },
                { "Aggressive", "激進" },
                { "Gentle", "溫和" },
                { "24-hour format in the UI", "介面使用 24 小時制" },
                { "Admins list UI", "管理員列表介面" },
                { "Automatic Close of Tip UI", "自動關閉提示介面" },
                { "Enabling this option will allow Tip UI (Paychek, Bus Info, etc...) to automatically close.", "啟用後提示介面（工資、公車資訊等）將自動關閉。" },
                { "Click outside phone to close", "點擊手機外部關閉" },
                { "Darkmode", "深色模式" },
                { "Toggle darkmode for some menus", "切換部分選單的深色模式" },
                { "Death UI", "死亡介面" },
                { "Desktop Notifications (when tabbed)", "桌面通知（視窗背景時）" },
                { "Send some Desktop notifications when game is tabbed (PMs, Phone alerts, Alarm Alerts, Panic Button)", "遊戲視窗在背景時傳送桌面通知（私訊、手機提醒、警報、緊急按鈕）" },
                { "Disable tips UI", "禁用提示介面" },
                { "While this option being enabled, you will bring back old behaviour of certain scripts, like examine.", "啟用後將恢復某些腳本的舊行為，例如檢查。" },
                { "Display player ping in /id", "在 /id 中顯示玩家延遲" },
                { "Faction info in chat", "聊天中顯示組織資訊" },
                { "When on, /fon prints the online list in chat. When off, /fon opens the faction online UI.", "開啟時 /fon 在聊天中列印線上列表；關閉時 /fon 開啟組織線上介面。" },
                { "Inventory in UI", "介面中顯示背包" },
                { "When off, /inv and similar commands print your items in chat instead of opening the inventory panel.", "關閉時 /inv 等指令在聊天中列印物品，而不是開啟背包面板。" },
                { "Phone Size", "手機尺寸" },
                { "Property notifications", "房地產通知" },
                { "Show indexes for items", "顯示物品序號" },
                { "UI Size", "介面尺寸" },
                { "Legacy clothing menu", "舊版服裝選單" },
                { "Use the old slider-based clothing store instead of the picture wardrobe.", "使用基於滑桿的舊版服裝商店，而不是圖片衣櫃。" },
                { "Auto hairtie", "自動髮圈" },
                { "Automatically apply a hairtie when wearing a hat or helmet.", "戴帽子或頭盔時自動佩戴髮圈。" },
                { "Hat (style 2)", "帽子（樣式 2）" },
                { "Custom (style 1)", "自訂（樣式 1）" },
                { "Custom (style 2)", "自訂（樣式 2）" },
                { "Custom (style 3)", "自訂（樣式 3）" },
                { "Custom (style 4)", "自訂（樣式 4）" },
                { "Custom (style 5)", "自訂（樣式 5）" },
                { "Custom (style 6)", "自訂（樣式 6）" },
                { "Custom (style 7)", "自訂（樣式 7）" },
                { "Custom (style 8)", "自訂（樣式 8）" },
                { "Automatic callsign", "自動呼號" },
                { "Pastes your CAD callsign at the beginning of every message.", "在每條訊息開頭貼上你的 CAD 呼號。" },
                { "Dark flashbangs", "深色閃光彈" },
                { "Helps players with photosensitive epilepsy; the effect lasts longer.", "幫助光敏性癲癇玩家；效果持續時間更長。" },
                { "Disable Air Traffic Control radio", "禁用空中交通管制電台" },
                { "When enabled, the ATC radio channel will be disabled.", "啟用後 ATC 電台頻道將被禁用。" },
                { "Drug Effects", "藥物效果" },
                { "Enables or disables drugs visual effects.", "啟用或禁用藥物視覺效果。" },
                { "Focus Mode", "專注模式" },
                { "Focus Mode: confirm on release", "專注模式：放開確認" },
                { "Releasing the focus key selects the highlighted option. When disabled, options are selected by clicking.", "放開專注鍵選擇高亮選項；禁用後透過點擊選擇。" },
                { "Phone keybind function", "手機按鍵功能" },
                { "Show money on screen", "在螢幕上顯示金錢" },
                { "Show vehicle details automatically", "自動顯示車輛詳情" },
                { "Displays vehicle details when you enter a vehicle.", "進入車輛時顯示車輛詳情。" },
                { "Advanced Animations", "進階動畫" },
                { "Head Movement", "頭部動作" },
                { "If disabled, character's head will not move. May impact performance negatively on some systems.", "禁用後角色頭部不會移動。在某些系統上可能影響效能。" },
                { "Lip Movement", "嘴唇動作" },
                { "Phone Animations", "手機動畫" },
                { "Anti-reverse", "防倒溜" },
                { "Prevents the vehicle from rolling backward after braking until you press reverse again.", "防止車輛煞車後向後溜車，直到你再次排入倒檔。" },
                { "Automatic Break Lights", "自動煞車燈" },
                { "Automatically turns on your vehicle's brake lights.", "自動開啟車輛煞車燈。" },
                { "Autopilot function", "自動駕駛功能" },
                { "Smooth Throttle", "平穩油門" },
                { "Smoother vehicle acceleration to help prevent skidding.", "更平順的車輛加速，幫助防止打滑。" },
                { "Vehicle Exit Safety", "下車安全" },
                { "Require a double-tap to confirm exit when moving over 20 MPH or when aircraft are airborne.", "速度超過 20 英里/小時或飛行器在空中時，需要雙擊確認下車。" },
                { "Disable posters in interiors", "禁用室內海報" },
                { "Disable posters in main dimension", "禁用主維度海報" },
                { "Disable posters in vehicles", "禁用車輛內海報" },
                { "Posters won't render while you're in a vehicle.", "在車輛內時海報不會渲染。" },
                { "Enable graffiti rendering", "啟用塗鴉渲染" },
                { "Can ruin the graffiti preview while spraying new graffiti.", "噴塗新塗鴉時可能破壞塗鴉預覽。" },
                { "Display admin nametags", "顯示管理員名牌" },
                { "Display Emote over head", "頭頂顯示表情" },
                { "Display nametag above own head", "自己頭頂顯示名牌" },
                { "Display pet names", "顯示寵物名字" },
                { "This option displays/hides pet nametags from your screen.", "此選項在螢幕上顯示或隱藏寵物名牌。" },
                { "Player IDs in nametags", "名牌中顯示玩家 ID" },
                { "Target Indicator", "目標指示器" },
                { "TV Brightness", "電視亮度" },
                { "Default TV brightness.", "預設電視亮度。" },
                { "TV Volume", "電視音量" },
                { "Default TV volume on login.", "登入時的預設電視音量。" },
                { "ALPR Volume", "車牌識別音量" },
                { "Audio Emitters Volume", "音訊發射器音量" },
                { "Doorbell Volume", "門鈴音量" },
                { "HQ Effect Volume", "總部特效音量" },
                { "Knock Volume", "敲門音量" },
                { "Microphone Indicator", "麥克風指示器" },
                { "Show the on-screen microphone indicator while you are transmitting.", "說話時在螢幕上顯示麥克風指示器。" },
                { "Mute ambient sound", "靜音環境音效" },
                { "Mutes world ambience (city effects, construction sites, ...). Same as /togambience.", "靜音世界環境音（城市效果、施工現場等）。等同於 /togambience。" },
                { "Mute Interior Radios", "靜音室內收音機" },
                { "When enabled, the radio will be muted in interiors.", "啟用後，室內收音機將被靜音。" },
                { "Panic Effect Volume", "緊急按鈕特效音量" },
                { "PCAD Effect Volume", "PCAD 特效音量" },
                { "Radio Volume", "電台音量" },
                { "Receive volume for radio voice, independent of proximity voice.", "電台語音接收音量，與近距離語音獨立。" },
                { "Voice: Push-to-talk", "語音：按鍵說話" },
                { "Hold GTA's Push-to-Talk key (N by default, rebindable in Settings > Key Bindings) to transmit proximity voice. Disabled, the mic is voice-activated (open mic).", "按住 GTA 的按鍵說話鍵（預設為 N，可在設定 > 按鍵綁定中更改）以傳輸近距離語音。停用後，麥克風為語音啟動（開放麥克風）。" },
                { "New Chat", "新聊天" },
                { "Brighter OOC", "更亮的 OOC" },
                { "Chat Font", "聊天字型" },
                { "Chat Font Size", "聊天字型大小" },
                { "Chat Size", "聊天大小" },
                { "Chat Width", "聊天寬度" },
                { "Chatbox Opacity", "聊天框不透明度" },
                { "Fade inactive chat", "淡出非活動聊天" },
                { "Highlight full /me", "高亮完整 /me" },
                { "Timestamps", "時間戳" },
                { "Toggle Faction in chat", "聊天中切換組織" },
                { "NEW CHAT", "新聊天" },
                { "Autopunctuate bold", "自動標點粗體" },
                { "Autopunctuate italic", "自動標點斜體" },
                { "Focus background", "聚焦背景" },
                { "Mention notifications", "提及通知" },
                { "How @playerID mentions are expanded in your outgoing chat.", "你的外發聊天中 @玩家ID 提及如何展開。" },
                { "First Name", "名字" },
                { "Last Name", "姓氏" },
                { "Full Name", "全名" },
                { "Show highlights", "顯示高亮" },
                { "Use new chat", "使用新聊天" },
                { "GTAW HUD", "GTAW HUD" },
                { "Alternative Speedometer", "備選速度表" },
                { "Advanced address", "進階地址" },
                { "Advanced HUD", "進階 HUD" },
                { "Show the minimap menu bar (map / vehicle / GPS / audio) over the minimap.", "在小地圖上方顯示小地圖選單列（地圖 / 車輛 / GPS / 音訊）。" },
                { "Fade inactive HUD", "淡出非活動 HUD" },
                { "Fades the entire HUD out after about 20 seconds without money, weather, address, or tip updates.", "約 20 秒沒有金錢、天氣、地址或提示更新後，整個 HUD 淡出。" },
                { "HUD highlight color", "HUD 高亮顏色" },
                { "Minimalist HUD", "極簡 HUD" },
                { "Removes the dark backgrounds from HUD elements so cash, location, compass, and notifications show as transparent text and icons. Reposition widgets with /hudlayout.", "移除 HUD 元素的深色背景，使金錢、位置、指南針和通知顯示為透明文字和圖示。使用 /hudlayout 重新調整小工具位置。" },
                { "Show address", "顯示地址" },
                { "Show bank", "顯示銀行餘額" },
                { "Show the bank balance (top right).", "顯示銀行餘額（右上角）。" },
                { "Show cash", "顯示現金" },
                { "Show the cash amount (top right).", "顯示現金金額（右上角）。" },
                { "Show compass", "顯示指南針" },
                { "Show the compass (cardinal direction).", "顯示指南針（方位方向）。" },
                { "Show health bars", "顯示生命條" },
                { "Show the health / armour / helmet / oxygen bars.", "顯示生命 / 護甲 / 頭盔 / 氧氣條。" },
                { "Show location", "顯示位置" },
                { "Show the area & street bar.", "顯示區域與街道欄。" },
                { "Show minimap", "顯示小地圖" },
                { "Show the minimap (radar).", "顯示小地圖（雷達）。" },
                { "Show money", "顯示金錢" },
                { "Show server info", "顯示伺服器資訊" },
                { "Show speedometer", "顯示速度表" },
                { "Show the speedometer.", "顯示速度表。" },
                { "Show weapon", "顯示武器" },
                { "Show the equipped weapon icon, ammo & name (top right).", "顯示已裝備武器的圖示、彈藥和名稱（右上角）。" },
                { "Temperature in Fahrenheit", "溫度顯示為華氏" },
                { "Weather placement", "天氣顯示位置" },
                { "Where time & weather appear: above the minimap, inside the location bar, or hidden.", "時間和天氣顯示位置：小地圖上方、位置欄內或隱藏。" },
                { "Above minimap", "小地圖上方" },
                { "Location bar", "位置欄" },
                { "Hidden", "隱藏" },
                { "Damage screen flash", "受傷螢幕閃爍" },
                { "Red edge flash on your screen when you take damage.", "受到傷害時螢幕邊緣閃紅。" },
                { "ALTERNATIVE SPEEDOMETER", "備選速度表" },
                { "Background opacity", "背景不透明度" },
                { "Max Altitude (ft)", "最大高度（英尺）" },
                { "Show dashboard icons", "顯示儀表板圖示" },
                { "Show the dashboard icon row (indicators, lights, engine, lock, handbrake\u2026).", "顯示儀表板圖示行（指示燈、車燈、引擎、鎖、手煞車等）。" },
                { "Show fuel gauge", "顯示油量表" },
                { "Show the fuel gauge on the speedometer.", "在速度表上顯示油量表。" },
                { "Show gear", "顯示檔位" },
                { "Show the gear box on the speedometer. When off, speed unit + odometer move up in line with the speed.", "在速度表上顯示檔位框。關閉後，速度單位和里程表隨速度上移對齊。" },
                { "Show RPM meter", "顯示轉速表" },
                { "Show the RPM bar on the speedometer.", "在速度表上顯示轉速條。" },
                { "Use Imperial units", "使用英制單位" },
                { "Action Key", "互動按鍵" },
                { "Animation UI Key", "動畫介面按鍵" },
                { "Animation Wheel Key", "動畫輪盤按鍵" },
                { "Blindfold Key", "蒙眼按鍵" },
                { "CAD Key", "CAD 按鍵" },
                { "Close Tip Key", "關閉提示按鍵" },
                { "Crawl Key", "匍匐按鍵" },
                { "Crouch Key", "蹲伏按鍵" },
                { "Focus Mode Key", "專注模式按鍵" },
                { "Garage Key", "車庫按鍵" },
                { "K9 Door Key", "K9 車門按鍵" },
                { "Lock Key", "鎖車按鍵" },
                { "Open Inventory Key", "開啟背包按鍵" },
                { "Personal Assistant Key", "私人助理按鍵" },
                { "Phone Key", "手機按鍵" },
                { "Playerlist Key", "玩家列表按鍵" },
                { "Ragdoll Key", "布娃娃按鍵" },
                { "Tackle Key", "撲倒按鍵" },
                { "Voice: talk range key", "語音：說話範圍按鍵" },
                { "Auto Pilot", "自動駕駛" },
                { "Cruise Control", "定速巡航" },
                { "Engine", "引擎" },
                { "Handbrake", "手煞車" },
                { "Headlights", "車燈" },
                { "Left Indicator", "左轉向燈" },
                { "Right Indicator", "右轉向燈" },
                { "Seatbelt", "安全帶" },
                { "Siren", "警笛" },
                { "Siren ELS Down", "警笛 ELS 降低" },
                { "Siren ELS Up", "警笛 ELS 升高" },
                { "Throw Gun", "扔槍" },
                { "Transit Vehicle Door", "接駁車輛車門" }
            };

            private static readonly Dictionary<string, string> MapEs = new Dictionary<string, string>
            {
                { "INVENTORY", "Inventario" },
                { "Logged in as", "Conectado como" },
                { "General", "General" },
                { "Audio", "Audio" },
                { "Chat", "Chat" },
                { "HUD", "HUD" },
                { "Hotkeys", "Teclas rápidas" },
                { "Mapping", "Teclado" },
                { "Admin", "Admin" },
                { "SAVE", "GUARDAR" },
                { "All", "Todos" },
                { "GENERAL", "GENERAL" },
                { "UI", "Interfaz" },
                { "Gameplay", "Jugabilidad" },
                { "Animations", "Animaciones" },
                { "Vehicle", "Vehículo" },
                { "Environment", "Entorno" },
                { "Nametag", "Nombre en pantalla" },
                { "TV", "TV" },
                { "GAMEPLAY", "JUGABILIDAD" },
                { "ANIMATIONS", "ANIMACIONES" },
                { "VEHICLE", "VEHÍCULO" },
                { "ENVIRONMENT", "ENTORNO" },
                { "NAMETAG", "NOMBRE EN PANTALLA" },
                { "Chat Hand Gestures", "Gestos de chat" },
                { "Play upper-body hand animations when you speak in IC chat. None disables gestures.", "Reproduce gestos de la parte superior del cuerpo al hablar en el chat IC. Ninguno desactiva los gestos." },
                { "None", "Ninguno" },
                { "Default", "Predeterminado" },
                { "Aggressive", "Agresivo" },
                { "Gentle", "Suave" },
                { "24-hour format in the UI", "Formato de 24 horas en la interfaz" },
                { "Admins list UI", "Interfaz de lista de administradores" },
                { "Automatic Close of Tip UI", "Cierre automático de la interfaz de avisos" },
                { "Enabling this option will allow Tip UI (Paychek, Bus Info, etc...) to automatically close.", "Al activar esta opción, la interfaz de avisos (nómina, información del autobús, etc.) se cerrará automáticamente." },
                { "Click outside phone to close", "Haga clic fuera del teléfono para cerrar" },
                { "Darkmode", "Modo oscuro" },
                { "Toggle darkmode for some menus", "Activa el modo oscuro en algunos menús" },
                { "Death UI", "Interfaz de muerte" },
                { "Desktop Notifications (when tabbed)", "Notificaciones de escritorio (al minimizar)" },
                { "Send some Desktop notifications when game is tabbed (PMs, Phone alerts, Alarm Alerts, Panic Button)", "Envía notificaciones de escritorio al minimizar el juego (mensajes privados, alertas del teléfono, alarmas, botón de pánico)" },
                { "Disable tips UI", "Desactivar interfaz de avisos" },
                { "While this option being enabled, you will bring back old behaviour of certain scripts, like examine.", "Al activar esta opción, volverás al comportamiento antiguo de ciertos scripts, como examinar." },
                { "Display player ping in /id", "Mostrar el ping del jugador en /id" },
                { "Faction info in chat", "Información de facción en el chat" },
                { "When on, /fon prints the online list in chat. When off, /fon opens the faction online UI.", "Cuando está activado, /fon muestra la lista de conectados en el chat. Cuando está desactivado, /fon abre la interfaz en línea de la facción." },
                { "Inventory in UI", "Inventario en la interfaz" },
                { "When off, /inv and similar commands print your items in chat instead of opening the inventory panel.", "Cuando está desactivado, /inv y comandos similares muestran tus objetos en el chat en lugar de abrir el panel de inventario." },
                { "Phone Size", "Tamaño del teléfono" },
                { "Property notifications", "Notificaciones de propiedades" },
                { "Show indexes for items", "Mostrar índices de objetos" },
                { "UI Size", "Tamaño de la interfaz" },
                { "Legacy clothing menu", "Menú de ropa clásico" },
                { "Use the old slider-based clothing store instead of the picture wardrobe.", "Usa la antigua tienda de ropa con deslizadores en lugar del armario con imágenes." },
                { "Auto hairtie", "Goma de pelo automática" },
                { "Automatically apply a hairtie when wearing a hat or helmet.", "Aplica automáticamente una goma de pelo al usar sombrero o casco." },
                { "Hat (style 2)", "Sombrero (estilo 2)" },
                { "Custom (style 1)", "Personalizado (estilo 1)" },
                { "Custom (style 2)", "Personalizado (estilo 2)" },
                { "Custom (style 3)", "Personalizado (estilo 3)" },
                { "Custom (style 4)", "Personalizado (estilo 4)" },
                { "Custom (style 5)", "Personalizado (estilo 5)" },
                { "Custom (style 6)", "Personalizado (estilo 6)" },
                { "Custom (style 7)", "Personalizado (estilo 7)" },
                { "Custom (style 8)", "Personalizado (estilo 8)" },
                { "Automatic callsign", "Indicativo automático" },
                { "Pastes your CAD callsign at the beginning of every message.", "Pega tu indicativo CAD al inicio de cada mensaje." },
                { "Dark flashbangs", "Flashbangs oscuras" },
                { "Helps players with photosensitive epilepsy; the effect lasts longer.", "Ayuda a jugadores con epilepsia fotosensible; el efecto dura más." },
                { "Disable Air Traffic Control radio", "Desactivar radio de control de tráfico aéreo" },
                { "When enabled, the ATC radio channel will be disabled.", "Cuando está activado, el canal de radio ATC se desactivará." },
                { "Drug Effects", "Efectos de drogas" },
                { "Enables or disables drugs visual effects.", "Activa o desactiva los efectos visuales de las drogas." },
                { "Focus Mode", "Modo de enfoque" },
                { "Focus Mode: confirm on release", "Modo de enfoque: confirmar al soltar" },
                { "Releasing the focus key selects the highlighted option. When disabled, options are selected by clicking.", "Soltar la tecla de enfoque selecciona la opción resaltada. Cuando está desactivado, las opciones se seleccionan haciendo clic." },
                { "Phone keybind function", "Función de tecla del teléfono" },
                { "Show money on screen", "Mostrar dinero en pantalla" },
                { "Show vehicle details automatically", "Mostrar detalles del vehículo automáticamente" },
                { "Displays vehicle details when you enter a vehicle.", "Muestra los detalles del vehículo al entrar en uno." },
                { "Advanced Animations", "Animaciones avanzadas" },
                { "Head Movement", "Movimiento de cabeza" },
                { "If disabled, character's head will not move. May impact performance negatively on some systems.", "Si se desactiva, la cabeza del personaje no se moverá. Puede afectar negativamente el rendimiento en algunos sistemas." },
                { "Lip Movement", "Movimiento de labios" },
                { "Phone Animations", "Animaciones del teléfono" },
                { "Anti-reverse", "Anti-retroceso" },
                { "Prevents the vehicle from rolling backward after braking until you press reverse again.", "Evita que el vehículo retroceda después de frenar hasta que vuelvas a poner la marcha atrás." },
                { "Automatic Break Lights", "Luces de freno automáticas" },
                { "Automatically turns on your vehicle's brake lights.", "Enciende automáticamente las luces de freno de tu vehículo." },
                { "Autopilot function", "Función de piloto automático" },
                { "Smooth Throttle", "Acelerador suave" },
                { "Smoother vehicle acceleration to help prevent skidding.", "Aceleración más suave para evitar derrapes." },
                { "Vehicle Exit Safety", "Seguridad al salir del vehículo" },
                { "Require a double-tap to confirm exit when moving over 20 MPH or when aircraft are airborne.", "Requiere doble toque para confirmar la salida cuando superas 20 MPH o cuando las aeronaves están en el aire." },
                { "Disable posters in interiors", "Desactivar pósteres en interiores" },
                { "Disable posters in main dimension", "Desactivar pósteres en la dimensión principal" },
                { "Disable posters in vehicles", "Desactivar pósteres en vehículos" },
                { "Posters won't render while you're in a vehicle.", "Los pósteres no se renderizarán mientras estés en un vehículo." },
                { "Enable graffiti rendering", "Activar renderizado de grafitis" },
                { "Can ruin the graffiti preview while spraying new graffiti.", "Puede arruinar la vista previa del grafiti al pintar uno nuevo." },
                { "Display admin nametags", "Mostrar nombres de administradores" },
                { "Display Emote over head", "Mostrar emotes sobre la cabeza" },
                { "Display nametag above own head", "Mostrar tu propio nombre sobre la cabeza" },
                { "Display pet names", "Mostrar nombres de mascotas" },
                { "This option displays/hides pet nametags from your screen.", "Esta opción muestra u oculta los nombres de las mascotas en tu pantalla." },
                { "Player IDs in nametags", "IDs de jugadores en los nombres" },
                { "Target Indicator", "Indicador de objetivo" },
                { "TV Brightness", "Brillo del TV" },
                { "Default TV brightness.", "Brillo predeterminado del TV." },
                { "TV Volume", "Volumen del TV" },
                { "Default TV volume on login.", "Volumen predeterminado del TV al iniciar sesión." },
                { "ALPR Volume", "Volumen ALPR" },
                { "Audio Emitters Volume", "Volumen de emisores de audio" },
                { "Doorbell Volume", "Volumen del timbre" },
                { "HQ Effect Volume", "Volumen de efectos de la base" },
                { "Knock Volume", "Volumen de llamadas a la puerta" },
                { "Microphone Indicator", "Indicador de micrófono" },
                { "Show the on-screen microphone indicator while you are transmitting.", "Muestra el indicador de micrófono en pantalla mientras transmites." },
                { "Mute ambient sound", "Silenciar sonido ambiental" },
                { "Mutes world ambience (city effects, construction sites, ...). Same as /togambience.", "Silencia el ambiente del mundo (efectos de la ciudad, obras, ...). Igual que /togambience." },
                { "Mute Interior Radios", "Silenciar radios en interiores" },
                { "When enabled, the radio will be muted in interiors.", "Cuando está activado, la radio se silenciará en interiores." },
                { "Panic Effect Volume", "Volumen de efectos de pánico" },
                { "PCAD Effect Volume", "Volumen de efectos PCAD" },
                { "Radio Volume", "Volumen de radio" },
                { "Receive volume for radio voice, independent of proximity voice.", "Volumen de recepción de la voz de radio, independiente de la voz de proximidad." },
                { "Voice: Push-to-talk", "Voz: pulsar para hablar" },
                { "Hold GTA's Push-to-Talk key (N by default, rebindable in Settings > Key Bindings) to transmit proximity voice. Disabled, the mic is voice-activated (open mic).", "Mantén pulsada la tecla de pulsar para hablar de GTA (N por defecto, reasignable en Ajustes > Teclas) para transmitir voz de proximidad. Si está desactivado, el micrófono se activa con la voz (micrófono abierto)." },
                { "New Chat", "Nuevo chat" },
                { "Brighter OOC", "OOC más brillante" },
                { "Chat Font", "Fuente del chat" },
                { "Chat Font Size", "Tamaño de fuente del chat" },
                { "Chat Size", "Tamaño del chat" },
                { "Chat Width", "Ancho del chat" },
                { "Chatbox Opacity", "Opacidad del cuadro de chat" },
                { "Fade inactive chat", "Atenuar chat inactivo" },
                { "Highlight full /me", "Resaltar /me completo" },
                { "Timestamps", "Marca de tiempo" },
                { "Toggle Faction in chat", "Alternar facción en el chat" },
                { "NEW CHAT", "NUEVO CHAT" },
                { "Autopunctuate bold", "Puntuación automática en negrita" },
                { "Autopunctuate italic", "Puntuación automática en cursiva" },
                { "Focus background", "Fondo de enfoque" },
                { "Mention notifications", "Notificaciones de menciones" },
                { "How @playerID mentions are expanded in your outgoing chat.", "Cómo se expanden las menciones @playerID en tu chat saliente." },
                { "First Name", "Nombre" },
                { "Last Name", "Apellido" },
                { "Full Name", "Nombre completo" },
                { "Show highlights", "Mostrar resaltados" },
                { "Use new chat", "Usar nuevo chat" },
                { "GTAW HUD", "GTAW HUD" },
                { "Alternative Speedometer", "Velocímetro alternativo" },
                { "Advanced address", "Dirección avanzada" },
                { "Advanced HUD", "HUD avanzado" },
                { "Show the minimap menu bar (map / vehicle / GPS / audio) over the minimap.", "Muestra la barra de menú del minimapa (mapa / vehículo / GPS / audio) sobre el minimapa." },
                { "Fade inactive HUD", "Atenuar HUD inactivo" },
                { "Fades the entire HUD out after about 20 seconds without money, weather, address, or tip updates.", "Atenúa todo el HUD después de unos 20 segundos sin actualizaciones de dinero, clima, dirección o avisos." },
                { "HUD highlight color", "Color de resaltado del HUD" },
                { "Minimalist HUD", "HUD minimalista" },
                { "Removes the dark backgrounds from HUD elements so cash, location, compass, and notifications show as transparent text and icons. Reposition widgets with /hudlayout.", "Elimina los fondos oscuros de los elementos del HUD para que el dinero, la ubicación, la brújula y las notificaciones se muestren como texto e iconos transparentes. Reubica los widgets con /hudlayout." },
                { "Show address", "Mostrar dirección" },
                { "Show bank", "Mostrar banco" },
                { "Show the bank balance (top right).", "Muestra el saldo del banco (arriba a la derecha)." },
                { "Show cash", "Mostrar efectivo" },
                { "Show the cash amount (top right).", "Muestra la cantidad de efectivo (arriba a la derecha)." },
                { "Show compass", "Mostrar brújula" },
                { "Show the compass (cardinal direction).", "Muestra la brújula (dirección cardinal)." },
                { "Show health bars", "Mostrar barras de vida" },
                { "Show the health / armour / helmet / oxygen bars.", "Muestra las barras de vida / armadura / casco / oxígeno." },
                { "Show location", "Mostrar ubicación" },
                { "Show the area & street bar.", "Muestra la barra de área y calle." },
                { "Show minimap", "Mostrar minimapa" },
                { "Show the minimap (radar).", "Muestra el minimapa (radar)." },
                { "Show money", "Mostrar dinero" },
                { "Show server info", "Mostrar información del servidor" },
                { "Show speedometer", "Mostrar velocímetro" },
                { "Show the speedometer.", "Muestra el velocímetro." },
                { "Show weapon", "Mostrar arma" },
                { "Show the equipped weapon icon, ammo & name (top right).", "Muestra el icono, munición y nombre del arma equipada (arriba a la derecha)." },
                { "Temperature in Fahrenheit", "Temperatura en Fahrenheit" },
                { "Weather placement", "Ubicación del clima" },
                { "Where time & weather appear: above the minimap, inside the location bar, or hidden.", "Dónde aparecen la hora y el clima: sobre el minimapa, dentro de la barra de ubicación u ocultos." },
                { "Above minimap", "Sobre el minimapa" },
                { "Location bar", "Barra de ubicación" },
                { "Hidden", "Oculto" },
                { "Damage screen flash", "Destello de pantalla al recibir daño" },
                { "Red edge flash on your screen when you take damage.", "Destello rojo en el borde de la pantalla al recibir daño." },
                { "ALTERNATIVE SPEEDOMETER", "VELOCÍMETRO ALTERNATIVO" },
                { "Background opacity", "Opacidad del fondo" },
                { "Max Altitude (ft)", "Altitud máxima (pies)" },
                { "Show dashboard icons", "Mostrar iconos del tablero" },
                { "Show the dashboard icon row (indicators, lights, engine, lock, handbrake\u2026).", "Muestra la fila de iconos del tablero (indicadores, luces, motor, bloqueo, freno de mano...)." },
                { "Show fuel gauge", "Mostrar indicador de combustible" },
                { "Show the fuel gauge on the speedometer.", "Muestra el indicador de combustible en el velocímetro." },
                { "Show gear", "Mostrar marcha" },
                { "Show the gear box on the speedometer. When off, speed unit + odometer move up in line with the speed.", "Muestra la caja de marchas en el velocímetro. Cuando está desactivado, la unidad de velocidad y el cuentakilómetros suben en línea con la velocidad." },
                { "Show RPM meter", "Mostrar tacómetro" },
                { "Show the RPM bar on the speedometer.", "Muestra la barra de RPM en el velocímetro." },
                { "Use Imperial units", "Usar unidades imperiales" },
                { "Action Key", "Tecla de acción" },
                { "Animation UI Key", "Tecla de interfaz de animaciones" },
                { "Animation Wheel Key", "Tecla de rueda de animaciones" },
                { "Blindfold Key", "Tecla de venda en los ojos" },
                { "CAD Key", "Tecla de CAD" },
                { "Close Tip Key", "Tecla de cerrar avisos" },
                { "Crawl Key", "Tecla de gatear" },
                { "Crouch Key", "Tecla de agacharse" },
                { "Focus Mode Key", "Tecla de modo de enfoque" },
                { "Garage Key", "Tecla de garaje" },
                { "K9 Door Key", "Tecla de puerta K9" },
                { "Lock Key", "Tecla de bloqueo" },
                { "Open Inventory Key", "Tecla de abrir inventario" },
                { "Personal Assistant Key", "Tecla de asistente personal" },
                { "Phone Key", "Tecla de teléfono" },
                { "Playerlist Key", "Tecla de lista de jugadores" },
                { "Ragdoll Key", "Tecla de ragdoll" },
                { "Tackle Key", "Tecla de placaje" },
                { "Voice: talk range key", "Voz: tecla de rango de voz" },
                { "Auto Pilot", "Piloto automático" },
                { "Cruise Control", "Control de crucero" },
                { "Engine", "Motor" },
                { "Handbrake", "Freno de mano" },
                { "Headlights", "Faros" },
                { "Left Indicator", "Intermitente izquierdo" },
                { "Right Indicator", "Intermitente derecho" },
                { "Seatbelt", "Cinturón de seguridad" },
                { "Siren", "Sirena" },
                { "Siren ELS Down", "Sirena ELS abajo" },
                { "Siren ELS Up", "Sirena ELS arriba" },
                { "Throw Gun", "Lanzar arma" },
                { "Transit Vehicle Door", "Puerta de vehículo de tránsito" }
            };
        }
    }
}
