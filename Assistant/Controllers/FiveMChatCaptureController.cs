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
        private const int MaxTranslationWorkers = 2;

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
                        Properties.Settings.Default.DeepSeekApiKey,
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
                                Properties.Settings.Default.DeepSeekApiKey,
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

            public void InstallTranslationHook()
            {
                EnsureConnected();
                string mode = Properties.Settings.Default.TranslationDisplayMode == "replace" ? "replace" : "append";
                string bulkHotkey = serializer.Serialize(Properties.Settings.Default.TranslationBulkHotkey);
                string autoHotkey = serializer.Serialize(Properties.Settings.Default.AutoTranslateHotkey);
                string autoInitial = Properties.Settings.Default.AutoTranslate ? "true" : "false";
                string hook = "(function(){ if(window.__gtawTranslatorVersion === 10){ window.__gtawBulkHotkey = " + bulkHotkey + "; window.__gtawAutoHotkey = " + autoHotkey + "; return; } window.__gtawTranslatorVersion = 10; "
                    + "window.__gtawBulkHotkey = " + bulkHotkey + "; window.__gtawAutoHotkey = " + autoHotkey + "; window.__gtawAutoTranslate = " + autoInitial + "; "
                    + "if(window.__gtawTranslatorHandler){ document.removeEventListener('click', window.__gtawTranslatorHandler, true); } "
                    + "if(window.__gtawBulkHandler){ document.removeEventListener('keydown', window.__gtawBulkHandler, true); } "
                    + "if(window.__gtawAutoHandler){ document.removeEventListener('keydown', window.__gtawAutoHandler, true); } "
                    + "if(window.__gtawAutoObs){ window.__gtawAutoObs.disconnect(); window.__gtawAutoObs = null; } "
                    + "document.querySelectorAll('.chat__messages > li [data-gtaw-tid]').forEach(function(el){ if(el.parentNode){ el.parentNode.removeChild(el); } }); "
                    + "document.querySelectorAll('.chat__messages > li[data-gtaw-tid]').forEach(function(li){ if(li.__gtawOriginalHtml){ li.innerHTML = li.__gtawOriginalHtml; } li.removeAttribute('data-gtaw-tid'); delete li.__gtawOriginalHtml; delete li.__gtawSegs; delete li.__gtawTranslated; delete li.__gtawPendingId; }); "
                    + "window.__gtawPendingTranslations = []; "
                    + "window.__gtawMode = '" + mode + "'; "
                    + "function collect(li){ var segs = []; function styleOf(el){ var cs = getComputedStyle(el); return [cs.color, cs.fontFamily, cs.fontSize, cs.fontStyle, cs.fontWeight]; } function walk(node, el){ for(var i = 0; i < node.childNodes.length; i++){ var c = node.childNodes[i]; if(c.nodeType === 3){ var t = (c.textContent || '').replace(/\\s+/g, ' ').trim(); if(t) segs.push({ t: t, s: styleOf(el) }); } else if(c.nodeType === 1){ if(c.children.length){ walk(c, c); } else { var t2 = (c.innerText || c.textContent || '').replace(/\\s+/g, ' ').trim(); if(t2) segs.push({ t: t2, s: styleOf(c) }); } } } } walk(li, li); return segs; } "
                    + "function enqueue(li, rid){ if(window.__gtawMode === 'replace'){ if(li.__gtawTranslated || li.__gtawPendingId) return; li.__gtawOriginalHtml = li.innerHTML; li.__gtawSegs = collect(li); li.__gtawPendingId = rid; li.setAttribute('data-gtaw-tid', rid); li.textContent = '...'; window.__gtawPendingTranslations.push({ id: rid, segs: li.__gtawSegs }); return; } if(li.__gtawPendingId || li.querySelector('[data-gtaw-tid]')) return; li.__gtawSegs = collect(li); li.__gtawPendingId = rid; var div = document.createElement('div'); div.setAttribute('data-gtaw-tid', rid); div.style.cssText = 'margin-top:2px;white-space:pre-wrap;'; var bs = li.__gtawSegs.length ? li.__gtawSegs[li.__gtawSegs.length - 1].s : null; var ph = document.createElement('span'); if(bs){ ph.style.color = bs[0]; ph.style.fontFamily = bs[1]; ph.style.fontSize = bs[2]; ph.style.fontStyle = bs[3]; ph.style.fontWeight = bs[4]; } ph.textContent = '...'; div.appendChild(ph); li.appendChild(div); window.__gtawPendingTranslations.push({ id: rid, segs: li.__gtawSegs }); } "
                    + "function processAdded(muts){ var items = []; for(var m = 0; m < muts.length; m++){ var added = muts[m].addedNodes; for(var i = 0; i < added.length; i++){ var n = added[i]; if(n.nodeType === 1 && n.matches && n.matches('.chat__messages > li')) items.push(n); } } if(!items.length) return; items.forEach(function(li){ if(!li.__gtawPendingId && !li.querySelector('[data-gtaw-tid]')){ var rid = 'gtawa-' + Date.now() + '-' + Math.random().toString(36).substr(2,6); enqueue(li, rid); } }); } "
                    + "function ensureAutoObserver(){ if(window.__gtawAutoTranslate){ if(!window.__gtawAutoObs){ var container = document.querySelector('.chat__messages'); if(!container) return; window.__gtawAutoObs = new MutationObserver(function(muts){ setTimeout(function(){ processAdded(muts); }, 50); }); window.__gtawAutoObs.observe(container, { childList: true }); } } else { if(window.__gtawAutoObs){ window.__gtawAutoObs.disconnect(); window.__gtawAutoObs = null; } } } "
                    + "function toggleAuto(v){ if(typeof v !== 'undefined'){ window.__gtawAutoTranslate = !!v; } else { window.__gtawAutoTranslate = !window.__gtawAutoTranslate; } ensureAutoObserver(); } window.__gtawToggleAuto = toggleAuto; "
                    + "var autoHandler = function(e){ var hot = window.__gtawAutoHotkey || 'Ctrl+Shift+F9'; var parts = hot.split('+'); var keyName = (parts.pop() || 'F9').toUpperCase(); var needsCtrl = false, needsShift = false; for(var i = 0; i < parts.length; i++){ var p = parts[i].trim(); if(p === 'Ctrl') needsCtrl = true; else if(p === 'Shift') needsShift = true; } var pressed = (e.key || '').toUpperCase(); if(pressed !== keyName) return; if(needsCtrl !== (e.ctrlKey || e.metaKey)) return; if(needsShift !== e.shiftKey) return; if(e.repeat) return; toggleAuto(); }; "
                    + "window.__gtawAutoHandler = autoHandler; document.addEventListener('keydown', autoHandler, true); "
                    + "var handler = function(e){ var li = (e.target && e.target.closest) ? e.target.closest('.chat__messages > li') : null; if(!li) return; var text = (li.innerText || '').replace(/\\s+/g, ' ').trim(); if(!text) return; "
                    + "if(window.__gtawMode === 'replace'){ if(li.__gtawTranslated){ li.innerHTML = li.__gtawOriginalHtml; li.__gtawTranslated = false; return; } if(li.__gtawPendingId){ window.__gtawPendingTranslations.push({ id: li.__gtawPendingId, segs: li.__gtawSegs }); return; } var rid = 'gtaw-' + Date.now() + '-' + Math.random().toString(36).substr(2,6); enqueue(li, rid); return; } "
                    + "var existing = li.querySelector('[data-gtaw-tid]'); if(existing){ if(existing.textContent && existing.textContent !== '...'){ existing.style.display = existing.style.display === 'none' ? '' : 'none'; } else { window.__gtawPendingTranslations.push({ id: existing.getAttribute('data-gtaw-tid'), segs: li.__gtawSegs }); } return; } "
                    + "var id = 'gtaw-' + Date.now() + '-' + Math.random().toString(36).substr(2,6); enqueue(li, id); }; "
                    + "window.__gtawTranslatorHandler = handler; document.addEventListener('click', handler, true); "
                    + "var bulkHandler = function(e){ var hot = window.__gtawBulkHotkey || 'Ctrl+F9'; var ctrl = hot.indexOf('Ctrl') === 0; var keyName = (hot.split('+').pop() || 'F9').toUpperCase(); var pressed = (e.key || '').toUpperCase(); if(pressed !== keyName) return; if(ctrl !== (e.ctrlKey || e.metaKey)) return; if(e.repeat) return; var lis = document.querySelectorAll('.chat__messages > li'); var start = Math.max(0, lis.length - 10); for(var i = start; i < lis.length; i++){ var li = lis[i]; var txt = (li.innerText || '').replace(/\\s+/g, ' ').trim(); if(!txt) continue; if(li.__gtawPendingId || li.querySelector('[data-gtaw-tid]')) continue; var rid = 'gtawb-' + Date.now() + '-' + i + '-' + Math.random().toString(36).substr(2,6); enqueue(li, rid); } }; "
                    + "window.__gtawBulkHandler = bulkHandler; document.addEventListener('keydown', bulkHandler, true); "
                    + "ensureAutoObserver(); })();";
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
                InstallTranslationHook();
                SyncAutoTranslate();

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
            public void InstallSendHook(string hotkey)
            {
                EnsureConnected();
                string keyJson = serializer.Serialize(string.IsNullOrWhiteSpace(hotkey) ? "F9" : hotkey);
                string hook = "(function(){ if(window.__gtawSendVersion === 4){ window.__gtawSendHotkey = " + keyJson + "; return; } window.__gtawSendVersion = 4; "
                    + "window.__gtawSendActive = false; window.__gtawSendVisible = false; window.__gtawSendResult = null; window.__gtawSendHotkey = " + keyJson + "; window.__gtawSendDiag = null; "
                    + "if(window.__gtawSendKeyHandler){ document.removeEventListener('keydown', window.__gtawSendKeyHandler, true); } "
                    + "if(window.__gtawSendPlaceTimer){ clearInterval(window.__gtawSendPlaceTimer); } "
                    + "if(window.__gtawSendVisTimer){ clearInterval(window.__gtawSendVisTimer); } "
                    + "if(window.__gtawSendBtn && window.__gtawSendBtn.parentNode){ window.__gtawSendBtn.parentNode.removeChild(window.__gtawSendBtn); } "
                    + "var ovx = document.getElementById('gtaw-send-overlay'); if(ovx && ovx.parentNode){ ovx.parentNode.removeChild(ovx); } "
                    + "if(window.__gtawSendDragMove){ document.removeEventListener('mousemove', window.__gtawSendDragMove); } if(window.__gtawSendDragUp){ document.removeEventListener('mouseup', window.__gtawSendDragUp); } "
                    + "function getInput(){ var ae = document.activeElement; if(ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA' || ae.isContentEditable)) return ae; var el = document.querySelector('.chat__input input') || document.querySelector('.chat-input input') || document.querySelector('.chat__input textarea') || document.querySelector('.chat-input textarea'); if(el) return el; var inputs = document.querySelectorAll('input, textarea'); for(var i = 0; i < inputs.length; i++){ var tag = inputs[i].tagName; var t = (inputs[i].type || 'text').toLowerCase(); if(tag === 'TEXTAREA' || (t !== 'checkbox' && t !== 'radio' && t !== 'button' && t !== 'submit' && t !== 'hidden')) return inputs[i]; } var ce = document.querySelector('[contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"\"]'); if(ce) return ce; return null; } "
                    + "function readVal(el){ return el.isContentEditable ? (el.textContent || '') : (el.value || ''); } "
                    + "function chatOpen(){ var el = getInput(); if(!el) return false; var st = window.getComputedStyle(el); if(st.display === 'none' || st.visibility === 'hidden') return false; var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; } "
                    + "function syncTranslator(){ var active = !!window.__gtawSendActive; var open = chatOpen(); var ov = document.getElementById('gtaw-send-overlay'); if(active && open){ if(ov){ ov.style.display = ''; } window.__gtawSendVisible = true; } else { if(ov){ ov.style.display = 'none'; } window.__gtawSendVisible = false; } } "
                    + "window.__gtawSendSync = syncTranslator; "
                    + "window.__gtawSendToggle = function(){ window.__gtawSendActive = !window.__gtawSendActive; syncTranslator(); }; "
                    + "var keyHandler = function(e){ if(e.key === window.__gtawSendHotkey || e.code === window.__gtawSendHotkey){ e.preventDefault(); e.stopPropagation(); window.__gtawSendToggle(); } }; "
                    + "window.__gtawSendKeyHandler = keyHandler; document.addEventListener('keydown', keyHandler, true); "
                    + "var btn = document.createElement('button'); btn.type = 'button'; btn.textContent = 'T'; btn.title = 'Translator'; btn.style.cssText = 'background:#3a3a3a;color:#fff;border:1px solid #777;border-radius:4px;padding:0 8px;cursor:pointer;font-size:12px;line-height:20px;margin-left:4px;'; "
                    + "var btnHandler = function(ev){ ev.preventDefault(); ev.stopPropagation(); window.__gtawSendToggle(); }; "
                    + "btn.addEventListener('click', btnHandler); window.__gtawSendBtn = btn; window.__gtawSendBtnHandler = btnHandler; "
                    + "function placeBtn(){ if(btn.parentNode) return; var input = getInput(); if(input && input.parentNode){ input.parentNode.appendChild(btn); } else if(document.body){ btn.style.position = 'fixed'; btn.style.right = '8px'; btn.style.bottom = '8px'; btn.style.zIndex = '9999'; document.body.appendChild(btn); } } "
                    + "placeBtn(); window.__gtawSendPlaceTimer = setInterval(placeBtn, 2000); "
                    + "window.__gtawSendVisTimer = setInterval(syncTranslator, 400); })();";
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
                    + "if(window.__gtawSendBtn && window.__gtawSendBtn.parentNode){ window.__gtawSendBtn.parentNode.removeChild(window.__gtawSendBtn); } "
                    + "if(window.__gtawSendDragMove){ document.removeEventListener('mousemove', window.__gtawSendDragMove); } if(window.__gtawSendDragUp){ document.removeEventListener('mouseup', window.__gtawSendDragUp); } "
                    + "var ov = document.getElementById('gtaw-send-overlay'); if(ov && ov.parentNode){ ov.parentNode.removeChild(ov); } "
                    + "delete window.__gtawSendKeyHandler; delete window.__gtawSendBtn; delete window.__gtawSendBtnHandler; delete window.__gtawSendResult; delete window.__gtawSendToggle; delete window.__gtawSendSync; delete window.__gtawSendActive; delete window.__gtawSendVisible; delete window.__gtawSendHotkey; delete window.__gtawSendVersion; delete window.__gtawSendPlaceTimer; delete window.__gtawSendVisTimer; delete window.__gtawSendPos;";
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
                    + "if(window.__gtawSendDragMove){ document.removeEventListener('mousemove', window.__gtawSendDragMove); } if(window.__gtawSendDragUp){ document.removeEventListener('mouseup', window.__gtawSendDragUp); } "
                    + "function getInput(){ var ae = document.activeElement; if(ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA' || ae.isContentEditable)) return ae; var el = document.querySelector('.chat__input input') || document.querySelector('.chat-input input') || document.querySelector('.chat__input textarea') || document.querySelector('.chat-input textarea'); if(el) return el; var inputs = document.querySelectorAll('input, textarea'); for(var i = 0; i < inputs.length; i++){ var tag = inputs[i].tagName; var t = (inputs[i].type || 'text').toLowerCase(); if(tag === 'TEXTAREA' || (t !== 'checkbox' && t !== 'radio' && t !== 'button' && t !== 'submit' && t !== 'hidden')) return inputs[i]; } var ce = document.querySelector('[contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"\"]'); if(ce) return ce; return null; } "
                    + "var W = 380; var H = 280; var left, top; "
                    + "var il = " + initLeftJs + "; var it = " + initTopJs + "; "
                    + "if(il >= 0 && it >= 0){ left = Math.max(4, Math.min(il, window.innerWidth - W - 4)); top = Math.max(4, Math.min(it, window.innerHeight - H - 4)); } else { var input = getInput(); var rect = null; if(input){ rect = input.getBoundingClientRect(); } if(rect && rect.width){ left = Math.max(4, Math.min(rect.left, window.innerWidth - W - 4)); top = Math.max(4, rect.top - H - 8); if(top < 4){ top = Math.min(rect.bottom + 8, window.innerHeight - H - 4); } } else { left = Math.max(4, window.innerWidth - W - 4); top = Math.max(4, window.innerHeight - H - 4); } } "
                    + "var ov = document.createElement('div'); ov.id = 'gtaw-send-overlay'; ov.style.cssText = 'position:fixed;left:' + left + 'px;top:' + top + 'px;width:' + W + 'px;z-index:10000;background:rgba(28,28,30,0.88);color:#eee;border:1px solid rgba(255,255,255,0.22);border-radius:6px;padding:12px;box-shadow:0 4px 18px rgba(0,0,0,0.6);font-family:Segoe UI,Arial,sans-serif;box-sizing:border-box;'; "
                    + "var title = document.createElement('div'); title.id = 'gtaw-send-title'; title.textContent = " + titleJson + "; title.style.cssText = 'font-size:14px;font-weight:bold;margin-bottom:8px;cursor:move;user-select:none;padding:2px 0;'; "
                    + "var orig = document.createElement('div'); orig.id = 'gtaw-send-orig'; orig.textContent = " + originalJson + "; orig.style.cssText = 'background:rgba(255,255,255,0.08);padding:6px;border-radius:4px;margin-bottom:8px;white-space:pre-wrap;max-height:70px;overflow:auto;font-size:12px;opacity:0.85;'; "
                    + "var ta = document.createElement('textarea'); ta.id = 'gtaw-send-ta'; ta.value = " + translatedJson + "; ta.style.cssText = 'width:100%;height:90px;background:rgba(255,255,255,0.1);color:#eee;border:1px solid rgba(255,255,255,0.3);border-radius:4px;padding:6px;box-sizing:border-box;resize:vertical;font-family:inherit;'; "
                    + "var row = document.createElement('div'); row.style.cssText = 'margin-top:8px;text-align:right;'; "
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
                    + "window.__gtawSendDragUp = function(){ dragging = false; window.__gtawSendPos = { left: ov.offsetLeft, top: ov.offsetTop }; }; "
                    + "document.addEventListener('mousemove', window.__gtawSendDragMove); document.addEventListener('mouseup', window.__gtawSendDragUp); "
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
    }
}
