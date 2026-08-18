using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Assistant.Controllers
{
    /// <summary>
    /// Translates text using the free Google Translate endpoint
    /// or a DeepSeek (OpenAI-compatible) chat model.
    /// </summary>
    public static class TranslationController
    {
        private const string GoogleTranslateUrl = "https://translate.googleapis.com/translate_a/single?client=gtx&sl={0}&tl={1}&dt=t&q={2}";
        private const string DeepSeekChatUrl = "https://api.deepseek.com/v1/chat/completions";
        private const string DeepLFreeApiUrl = "https://api-free.deepl.com/v2/translate";
        private const string DeepLProApiUrl = "https://api.deepl.com/v2/translate";
        private const string DoubaoChatUrl = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
        private const string ZoomTranslatorUrl = "https://api.zoom.us/v2/aiservices/translator/translate";

        static TranslationController()
        {
            // Force TLS 1.2 (required by DeepSeek / DeepL; without it every request
            // falls back through old protocols and stalls several seconds).
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // Allow more concurrent connections per host so the parallel translation
            // workers are not serialised on the default limit of 2.
            ServicePointManager.DefaultConnectionLimit = 8;
            // Load the on-disk translation cache so repeated chat lines (e.g. "on my
            // way") are answered from disk instead of burning API quota again.
            try
            {
                if (File.Exists(CacheFilePath))
                {
                    string json = File.ReadAllText(CacheFilePath, Encoding.UTF8);
                    Dictionary<string, string> saved = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                    if (saved != null)
                    {
                        lock (CacheLock)
                        {
                            foreach (KeyValuePair<string, string> entry in saved)
                            {
                                if (TranslationCache.Count >= MaxCacheEntries)
                                    break;
                                TranslationCache[entry.Key] = entry.Value;
                            }
                        }
                    }
                }
            }
            catch { /* corrupt or missing cache file is not fatal */ }
            CacheFlushTimer = new Timer(FlushCacheTimerCallback, null, 3000, 3000);
        }

        private static void FlushCacheTimerCallback(object state)
        {
            try
            {
                lock (CacheSaveLock)
                {
                    if (!cacheDirty)
                        return;
                    cacheDirty = false;
                }
                Dictionary<string, string> snapshot;
                lock (CacheLock)
                {
                    snapshot = new Dictionary<string, string>(TranslationCache);
                }
                Directory.CreateDirectory(CacheFileDirectory);
                string json = new JavaScriptSerializer().Serialize(snapshot);
                File.WriteAllText(CacheFilePath, json, new UTF8Encoding(false));
            }
            catch { /* never let cache persistence break translation */ }
        }

        /// <summary>
        /// Flushes any pending cached translations to disk immediately.
        /// Called on application shutdown so nothing is lost.
        /// </summary>
        public static void FlushCache()
        {
            lock (CacheSaveLock)
            {
                cacheDirty = true;
            }
            FlushCacheTimerCallback(null);
        }

        private static readonly Regex ProtectedPattern = new Regex(@"\[\d{1,2}:\d{2}(?::\d{2})?\]|https?://\S+|[()\[\]]");

        /// <summary>
        /// Proper nouns / special terms that must never be translated:
        /// common GTAW / RP abbreviations, organisations and locations.
        /// Matched case-insensitively and restored exactly as written.
        /// </summary>
        private static readonly string[] SpecialNouns =
        {
            "Los Santos", "San Andreas", "Blaine County", "Paleto Bay", "Sandy Shores",
            "Grove Street", "Vinewood", "Del Perro", "Vespucci", "Cypress Flats",
            "Grapeseed", "Chumash", "Harmony", "Mount Chiliad", "Fort Zancudo",
            "OOC", "IC", "RP", "RDM", "VDM", "AFK", "BRB", "LFG", "LFM",
            "EMS", "SASP", "LSPD", "LSSD", "SAHP", "DOJ", "DOC", "SADOC", "SAJD",
            "LEO", "FBI", "PD", "SD", "SAGOV", "GTAW"
        };

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, string> TranslationCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private const int MaxCacheEntries = 50000;
        private static readonly string CacheFileDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTAW-Log-Parser");
        private static readonly string CacheFilePath = Path.Combine(CacheFileDirectory, "translation-cache.json");
        public static readonly string ErrorLogPath = Path.Combine(CacheFileDirectory, "translation-errors.log");
        public static readonly string AppLogPath = Path.Combine(CacheFileDirectory, "app.log");
        private static readonly object CacheSaveLock = new object();
        private static bool cacheDirty;
        private static readonly Timer CacheFlushTimer;

        /// <summary>
        /// Appends a general event line (timestamped, categorised) to the
        /// application log that feeds the realtime log page. Never throws.
        /// </summary>
        public static void LogEvent(string category, string message)
        {
            try
            {
                Directory.CreateDirectory(CacheFileDirectory);
                TrimLogIfNeeded();
                string line = string.Format("{0:HH:mm:ss} [{1}] {2}{3}", DateTime.Now, category, message, Environment.NewLine);
                File.AppendAllText(AppLogPath, line, Encoding.UTF8);
            }
            catch { /* never let logging break the app */ }
        }

        /// <summary>
        /// Keeps app.log from growing without bound: once it exceeds 5 MB it is
        /// truncated to the newest 1 MB so the realtime log page stays fast.
        /// </summary>
        private static void TrimLogIfNeeded()
        {
            const long maxBytes = 5 * 1024 * 1024;
            const long keepBytes = 1 * 1024 * 1024;
            FileInfo info = new FileInfo(AppLogPath);
            if (!info.Exists || info.Length <= maxBytes)
                return;
            byte[] tail = new byte[keepBytes];
            using (FileStream stream = new FileStream(AppLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(-keepBytes, SeekOrigin.End);
                stream.Read(tail, 0, tail.Length);
            }
            File.WriteAllBytes(AppLogPath, tail);
        }

        /// <summary>
        /// Truncates a string for log lines, collapsing newlines.
        /// </summary>
        public static string Short(string s)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            if (s.Length <= 40)
                return s;
            return s.Substring(0, 40) + "...";
        }

        /// <summary>
        /// Appends a translation failure reason to the on-disk error log so the
        /// user (or support) can see why a chat line stayed in the original
        /// language instead of showing a translation.
        /// </summary>
        public static void LogTranslationError(string provider, string message)
        {
            try
            {
                Directory.CreateDirectory(CacheFileDirectory);
                string line = string.Format("{0:HH:mm:ss} [{1}] {2}{3}", DateTime.Now, provider ?? "?", message, Environment.NewLine);
                File.AppendAllText(ErrorLogPath, line, Encoding.UTF8);
                LogEvent("error", (provider ?? "?") + " " + message);
            }
            catch { /* never let logging break translation */ }
        }

        /// <summary>
        /// Number of entries currently held in the translation cache.
        /// </summary>
        public static int CacheCount
        {
            get
            {
                lock (CacheLock)
                    return TranslationCache.Count;
            }
        }

        /// <summary>
        /// Size of the on-disk translation cache file in bytes.
        /// </summary>
        public static long CacheSizeBytes
        {
            get
            {
                try
                {
                    return File.Exists(CacheFilePath) ? new FileInfo(CacheFilePath).Length : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Returns the most recently cached translations (newest first), limited
        /// to <paramref name="limit"/> entries.
        /// </summary>
        public static List<TranslationCacheEntry> GetRecentCacheEntries(int limit)
        {
            List<TranslationCacheEntry> result = new List<TranslationCacheEntry>();
            lock (CacheLock)
            {
                int skip = Math.Max(0, TranslationCache.Count - limit);
                int index = 0;
                foreach (KeyValuePair<string, string> pair in TranslationCache)
                {
                    if (index++ < skip)
                        continue;

                    int lastSeparator = pair.Key.LastIndexOf('|');
                    string source = lastSeparator >= 0 && lastSeparator < pair.Key.Length - 1
                        ? pair.Key.Substring(lastSeparator + 1)
                        : pair.Key;
                    result.Add(new TranslationCacheEntry { Source = source, Translation = pair.Value });
                    if (result.Count >= limit)
                        break;
                }
            }
            result.Reverse();
            return result;
        }

        /// <summary>
        /// Removes every cached translation from memory and disk.
        /// </summary>
        public static void ClearCache()
        {
            lock (CacheLock)
            {
                TranslationCache.Clear();
            }
            lock (CacheSaveLock)
            {
                cacheDirty = true;
            }
            FlushCacheTimerCallback(null);
        }

        private static string GetCachedTranslation(string key)
        {
            lock (CacheLock)
            {
                string value;
                return TranslationCache.TryGetValue(key, out value) ? value : null;
            }
        }

        private static void CacheTranslation(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            lock (CacheLock)
            {
                if (TranslationCache.Count >= MaxCacheEntries)
                {
                    // Evict the oldest ~20% of entries instead of wiping the whole
                    // cache, so frequently used translations survive cache pressure.
                    int removeCount = TranslationCache.Count / 5;
                    foreach (string oldKey in new List<string>(TranslationCache.Keys))
                    {
                        if (removeCount-- <= 0)
                            break;
                        TranslationCache.Remove(oldKey);
                    }
                }
                TranslationCache[key] = value;
            }
            lock (CacheSaveLock)
            {
                cacheDirty = true;
            }
        }

        /// <summary>
        /// Target languages selectable in the program settings
        /// </summary>
        public static readonly KeyValuePair<string, string>[] TargetLanguages = new[]
        {
            new KeyValuePair<string, string>("zh-CN", "简体中文 (Simplified Chinese)"),
            new KeyValuePair<string, string>("zh-TW", "繁體中文 (Traditional Chinese)"),
            new KeyValuePair<string, string>("en", "English"),
            new KeyValuePair<string, string>("ja", "日本語 (Japanese)"),
            new KeyValuePair<string, string>("ko", "한국어 (Korean)"),
            new KeyValuePair<string, string>("fr", "Français (French)"),
            new KeyValuePair<string, string>("de", "Deutsch (German)"),
            new KeyValuePair<string, string>("es", "Español (Spanish)"),
            new KeyValuePair<string, string>("ru", "Русский (Russian)")
        };

        /// <summary>
        /// DeepSeek chat models selectable in the program settings.
        /// The legacy deepseek-chat / deepseek-reasoner names were retired on 2026-07-24.
        /// </summary>
        public static readonly string[] DeepSeekModels = new[]
        {
            "deepseek-v4-flash",
            "deepseek-v4-pro"
        };

        /// <summary>
        /// Doubao models selectable in the program settings. Volcano Ark accepts
        /// either a public model ID or an inference endpoint ID (ep-...) created
        /// in the Ark console; the model box is editable so you can type either.
        /// </summary>
        public static readonly string[] DoubaoModels = new[]
        {
            "doubao-seed-2.0-lite",
            "doubao-seed-2.0-mini",
            "doubao-seed-2.0-pro"
        };

        public static string GetLanguageDisplayName(string code)
        {
            foreach (KeyValuePair<string, string> pair in TargetLanguages)
                if (pair.Key == code)
                    return pair.Value;
            return code;
        }

        /// <summary>
        /// Translates the text using the selected provider.
        /// Timestamps and links are preserved as-is; text inside brackets is
        /// translated while the bracket characters stay half-width.
        /// </summary>
        public static string Translate(string text, string targetLanguage, string sourceLanguage, string provider, string apiKey, string model, string prompt)
        {
            return Translate(text, targetLanguage, sourceLanguage, provider, apiKey, model, prompt, "casual");
        }

        /// <summary>
        /// Translates the text using the selected provider with a translation style.
        /// Supported styles: casual (口语), formal (正式), literary (书面).
        /// Only AI providers (DeepSeek) honour the style; Google ignores it.
        /// </summary>
        public static string Translate(string text, string targetLanguage, string sourceLanguage, string provider, string apiKey, string model, string prompt, string style)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            TranslationStats.RecordTranslation(text);

            string cacheKey = "p:" + (provider ?? string.Empty) + "|s:" + (sourceLanguage ?? string.Empty) + "|t:" + (targetLanguage ?? string.Empty)
                + "|m:" + (model ?? string.Empty) + "|y:" + (style ?? string.Empty) + "|q:" + (prompt ?? string.Empty) + "|" + text;
            string cached = GetCachedTranslation(cacheKey);
            if (cached != null)
                return cached;

            string result;
            if (string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase))
                result = TranslateWithDeepSeek(text, targetLanguage, sourceLanguage, apiKey, model, prompt, style);
            else if (string.Equals(provider, "DeepL", StringComparison.OrdinalIgnoreCase))
                result = TranslateWithDeepL(text, targetLanguage, sourceLanguage, apiKey);
            else if (string.Equals(provider, "Doubao", StringComparison.OrdinalIgnoreCase))
                result = TranslateWithDoubao(text, targetLanguage, sourceLanguage, apiKey, model, prompt, style);
            else if (string.Equals(provider, "Zoom", StringComparison.OrdinalIgnoreCase))
                result = TranslateWithZoom(text, targetLanguage, sourceLanguage, apiKey);
            else
                result = Translate(text, targetLanguage, sourceLanguage);

            LogEvent("翻译", (string.IsNullOrEmpty(provider) ? "Google" : provider) + " | " + Short(text) + " => " + Short(result));
            CacheTranslation(cacheKey, result);
            return result;
        }

        /// <summary>
        /// Translates the text using the free Google Translate endpoint.
        /// Timestamps and links are preserved as-is; text inside brackets is
        /// translated while the bracket characters stay half-width.
        /// </summary>
        public static string Translate(string text, string targetLanguage, string sourceLanguage)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string cacheKey = "g|s:" + (sourceLanguage ?? string.Empty) + "|t:" + (targetLanguage ?? string.Empty) + "|" + text;
            string cached = GetCachedTranslation(cacheKey);
            if (cached != null)
                return cached;

            List<string> protectedTokens = new List<string>();
            string protectedText = ProtectTokens(text, protectedTokens);

            string url = string.Format(
                GoogleTranslateUrl,
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage),
                Uri.EscapeDataString(targetLanguage),
                Uri.EscapeDataString(protectedText));

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Mozilla/5.0";
            request.Timeout = 10000;

            string translated;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                translated = ParseTranslation(reader.ReadToEnd());

            TranslationStats.RecordApiCallEstimated(text);

            string result = NormalizePunctuation(RestoreTokens(translated, protectedTokens));
            CacheTranslation(cacheKey, result);
            return result;
        }

        /// <summary>
        /// Translates the text using a DeepSeek chat model.
        /// </summary>
        public static string TranslateWithDeepSeek(string text, string targetLanguage, string sourceLanguage, string apiKey, string model, string prompt)
        {
            return TranslateWithDeepSeek(text, targetLanguage, sourceLanguage, apiKey, model, prompt, "casual");
        }

        /// <summary>
        /// Translates the text using a DeepSeek chat model with a translation style.
        /// </summary>
        public static string TranslateWithDeepSeek(string text, string targetLanguage, string sourceLanguage, string apiKey, string model, string prompt, string style)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("DeepSeek API key is missing.");
            if (string.IsNullOrWhiteSpace(model))
                model = "deepseek-v4-flash";

            List<string> protectedTokens = new List<string>();
            string protectedText = ProtectTokens(text, protectedTokens);

            string languageName = GetLanguageDisplayName(targetLanguage);
            string sourceNote = string.IsNullOrWhiteSpace(sourceLanguage)
                ? "Detect the source language automatically."
                : "The source language is " + GetLanguageDisplayName(sourceLanguage) + " (" + sourceLanguage + ").";
            string styleNote = "casual".Equals(style, StringComparison.OrdinalIgnoreCase)
                ? "Use a casual, conversational tone."
                : "formal".Equals(style, StringComparison.OrdinalIgnoreCase)
                    ? "Use a formal, polite and proper tone."
                    : "Use a literary, written and refined tone.";
            string systemPrompt = "You are a chat translator. " + sourceNote + " Translate the user's message into " + languageName + " (" + targetLanguage + "). Output only the translation without any explanations or quotes. Never translate or alter the GTB placeholder tokens. Never translate proper names, player names, place names, brand names, group names or common abbreviations (e.g. OOC, LFG, EMS, SASP); keep them exactly as written. Keep the overall meaning and tone of the original message. " + styleNote;
            if (!string.IsNullOrWhiteSpace(prompt))
                systemPrompt += " " + prompt.Trim();
            string payload = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "model", model },
                { "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", systemPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", protectedText } }
                    }
                },
                { "temperature", 0.3 },
                { "stream", false },
                { "max_tokens", 4096 }
            });

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(DeepSeekChatUrl);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = "Mozilla/5.0";
            request.Timeout = 30000;
            request.Headers["Authorization"] = "Bearer " + apiKey;

            byte[] body = Encoding.UTF8.GetBytes(payload);
            using (Stream requestStream = request.GetRequestStream())
                requestStream.Write(body, 0, body.Length);

            string responseJson;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                responseJson = reader.ReadToEnd();

            string translated = ParseDeepSeekResponse(responseJson);
            TranslationStats.RecordApiCall(ParseUsageTokens(responseJson));
            return NormalizePunctuation(RestoreTokens(translated, protectedTokens));
        }

        /// <summary>
        /// Translates the text using the DeepL API.
        /// The free endpoint is tried first, then the pro endpoint for paid keys.
        /// </summary>
        public static string TranslateWithDeepL(string text, string targetLanguage, string sourceLanguage, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("DeepL API key is missing.");

            List<string> protectedTokens = new List<string>();
            string protectedText = ProtectTokens(text, protectedTokens);

            string target = DeepLTargetLanguage(targetLanguage);
            string source = (string.IsNullOrWhiteSpace(sourceLanguage) || string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
                ? null
                : DeepLSourceLanguage(sourceLanguage);

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "text", new[] { protectedText } },
                { "target_lang", target }
            };
            if (!string.IsNullOrEmpty(source))
                payload["source_lang"] = source;

            string json = new JavaScriptSerializer().Serialize(payload);
            string responseJson = PostDeepL(json, apiKey);

            Dictionary<string, object> response = new JavaScriptSerializer().DeserializeObject(responseJson) as Dictionary<string, object>;
            object[] translations = response != null && response.ContainsKey("translations") ? response["translations"] as object[] : null;
            if (translations == null || translations.Length == 0)
                throw new InvalidOperationException("DeepL returned no translations.");

            Dictionary<string, object> first = translations[0] as Dictionary<string, object>;
            string translated = first != null && first.ContainsKey("text") ? first["text"] as string : null;
            if (string.IsNullOrEmpty(translated))
                return text;

            TranslationStats.RecordApiCallEstimated(text);
            return NormalizePunctuation(RestoreTokens(translated, protectedTokens));
        }

        /// <summary>
        /// Translates the text using the Doubao (Volcano Ark) API.
        /// The endpoint is OpenAI-compatible; new Ark users get free tokens
        /// (up to 500k per model) so chat translation is effectively free.
        /// </summary>
        public static string TranslateWithDoubao(string text, string targetLanguage, string sourceLanguage, string apiKey, string model, string prompt)
        {
            return TranslateWithDoubao(text, targetLanguage, sourceLanguage, apiKey, model, prompt, "casual");
        }

        /// <summary>
        /// Translates the text using the Doubao (Volcano Ark) API with a style.
        /// </summary>
        public static string TranslateWithDoubao(string text, string targetLanguage, string sourceLanguage, string apiKey, string model, string prompt, string style)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Doubao API key is missing.");
            if (string.IsNullOrWhiteSpace(model))
                model = "doubao-seed-2.0-lite";
            return PostOpenAiCompatible(DoubaoChatUrl, text, targetLanguage, sourceLanguage, apiKey, model, prompt, style);
        }

        /// <summary>
        /// Translates the text using the Zoom Translator API (fast mode).
        /// Zoom only translates between English and one of its supported
        /// languages, so the source side defaults to en-US when not given.
        /// </summary>
        public static string TranslateWithZoom(string text, string targetLanguage, string sourceLanguage, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Zoom API token is missing.");

            List<string> protectedTokens = new List<string>();
            string protectedText = ProtectTokens(text, protectedTokens);

            string target = ZoomTargetLanguage(targetLanguage);
            if (string.IsNullOrEmpty(target))
                throw new InvalidOperationException("Zoom Translator does not support the selected target language.");
            string source = ZoomSourceLanguage(sourceLanguage);

            Dictionary<string, object> config = new Dictionary<string, object>
            {
                { "target_languages", new[] { target } }
            };
            if (!string.IsNullOrEmpty(source))
                config["source_language"] = source;

            string payload = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "text", protectedText },
                { "config", config }
            });

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ZoomTranslatorUrl);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = "Mozilla/5.0";
            request.Timeout = 30000;
            request.Headers["Authorization"] = "Bearer " + apiKey;

            byte[] body = Encoding.UTF8.GetBytes(payload);
            using (Stream requestStream = request.GetRequestStream())
                requestStream.Write(body, 0, body.Length);

            string responseJson;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                responseJson = reader.ReadToEnd();

            string translated = ParseZoomResponse(responseJson, target);
            TranslationStats.RecordApiCallEstimated(text);
            return NormalizePunctuation(RestoreTokens(translated, protectedTokens));
        }

        /// <summary>
        /// Maps the internal language code to a Zoom BCP-47 locale code.
        /// Returns null when the language is not supported by Zoom.
        /// </summary>
        private static string ZoomTargetLanguage(string code)
        {
            switch ((code ?? string.Empty).ToLowerInvariant())
            {
                case "zh-cn": return "zh-CN";
                case "zh-tw": return "zh-TW";
                case "en": return "en-US";
                case "ja": return "ja-JP";
                case "ko": return "ko-KR";
                case "fr": return "fr-FR";
                case "de": return "de-DE";
                case "es": return "es-ES";
                default: return null;
            }
        }

        /// <summary>
        /// Maps the internal source language code to a Zoom BCP-47 locale code.
        /// Auto/empty sources default to en-US because this tool translates
        /// English GTAW chat and Zoom requires en-US on one side of a request.
        /// </summary>
        private static string ZoomSourceLanguage(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase))
                return "en-US";

            switch (code.ToLowerInvariant())
            {
                case "zh-cn": return "zh-CN";
                case "zh-tw": return "zh-TW";
                case "en": return "en-US";
                case "ja": return "ja-JP";
                case "ko": return "ko-KR";
                case "fr": return "fr-FR";
                case "de": return "de-DE";
                case "es": return "es-ES";
                default: return "en-US";
            }
        }

        /// <summary>
        /// Extracts the translated text from a Zoom Translator fast-mode
        /// response. The result is a map keyed by target locale code.
        /// </summary>
        private static string ParseZoomResponse(string json, string targetCode)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            IDictionary<string, object> root = serializer.DeserializeObject(json) as IDictionary<string, object>;
            if (root == null)
                throw new InvalidOperationException("Zoom returned an empty response.");

            IDictionary<string, object> result = root.ContainsKey("result") ? root["result"] as IDictionary<string, object> : null;
            IDictionary<string, object> translations = result != null && result.ContainsKey("translations")
                ? result["translations"] as IDictionary<string, object>
                : null;
            if (translations == null || translations.Count == 0)
                throw new InvalidOperationException("Zoom returned no translations.");

            if (translations.ContainsKey(targetCode))
            {
                string value = translations[targetCode] as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            // Fall back to the first available translation if the exact locale
            // key is not present.
            foreach (object value in translations.Values)
            {
                string textValue = value as string;
                if (!string.IsNullOrWhiteSpace(textValue))
                    return textValue.Trim();
            }

            throw new InvalidOperationException("Zoom returned an empty translation.");
        }

        /// <summary>
        /// Posts a chat completion request to any OpenAI-compatible endpoint
        /// (DeepSeek, Doubao/Ark, free reverse proxies) and returns the
        /// translation with protected tokens restored.
        /// </summary>
        private static string PostOpenAiCompatible(string url, string text, string targetLanguage, string sourceLanguage, string apiKey, string model, string prompt, string style)
        {
            List<string> protectedTokens = new List<string>();
            string protectedText = ProtectTokens(text, protectedTokens);

            string languageName = GetLanguageDisplayName(targetLanguage);
            string sourceNote = string.IsNullOrWhiteSpace(sourceLanguage)
                ? "Detect the source language automatically."
                : "The source language is " + GetLanguageDisplayName(sourceLanguage) + " (" + sourceLanguage + ").";
            string styleNote = "casual".Equals(style, StringComparison.OrdinalIgnoreCase)
                ? "Use a casual, conversational tone."
                : "formal".Equals(style, StringComparison.OrdinalIgnoreCase)
                    ? "Use a formal, polite and proper tone."
                    : "Use a literary, written and refined tone.";
            string systemPrompt = "You are a chat translator. " + sourceNote + " Translate the user's message into " + languageName + " (" + targetLanguage + "). Output only the translation without any explanations or quotes. Never translate or alter the GTB placeholder tokens. Never translate proper names, player names, place names, brand names, group names or common abbreviations (e.g. OOC, LFG, EMS, SASP); keep them exactly as written. Keep the overall meaning and tone of the original message. " + styleNote;
            if (!string.IsNullOrWhiteSpace(prompt))
                systemPrompt += " " + prompt.Trim();
            string payload = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "model", model },
                { "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", systemPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", protectedText } }
                    }
                },
                { "temperature", 0.3 },
                { "stream", false },
                { "max_tokens", 4096 }
            });

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = "Mozilla/5.0";
            request.Timeout = 30000;
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers["Authorization"] = "Bearer " + apiKey;

            byte[] body = Encoding.UTF8.GetBytes(payload);
            using (Stream requestStream = request.GetRequestStream())
                requestStream.Write(body, 0, body.Length);

            string responseJson;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                responseJson = reader.ReadToEnd();

            string translated = ParseDeepSeekResponse(responseJson);
            TranslationStats.RecordApiCall(ParseUsageTokens(responseJson));
            return NormalizePunctuation(RestoreTokens(translated, protectedTokens));
        }

        /// <summary>
        /// Posts the request to the DeepL API, trying the free endpoint first
        /// and then the pro endpoint for paid API keys.
        /// </summary>
        private static string PostDeepL(string json, string apiKey)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            WebException lastError = null;
            foreach (string url in new[] { DeepLFreeApiUrl, DeepLProApiUrl })
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.UserAgent = "Mozilla/5.0";
                request.Timeout = 30000;
                request.Headers["Authorization"] = "DeepL-Auth-Key " + apiKey;
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(body, 0, body.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch (WebException ex)
                {
                    lastError = ex;
                    // Try the next endpoint on auth / HTTP errors
                }
            }
            throw new InvalidOperationException("DeepL request failed.", lastError);
        }

        /// <summary>
        /// Maps the internal language codes to DeepL target language codes.
        /// </summary>
        private static string DeepLTargetLanguage(string code)
        {
            switch ((code ?? string.Empty).ToLowerInvariant())
            {
                case "zh-cn": return "ZH";
                case "zh-tw": return "ZH-HANT";
                case "en": return "EN";
                case "ja": return "JA";
                case "ko": return "KO";
                case "fr": return "FR";
                case "de": return "DE";
                case "es": return "ES";
                case "ru": return "RU";
                default: return "EN";
            }
        }

        /// <summary>
        /// Maps the internal language codes to DeepL source language codes.
        /// Returns null for unknown languages (DeepL auto-detects).
        /// </summary>
        private static string DeepLSourceLanguage(string code)
        {
            switch ((code ?? string.Empty).ToLowerInvariant())
            {
                case "zh-cn": return "ZH";
                case "zh-tw": return "ZH-HANT";
                case "en": return "EN";
                case "ja": return "JA";
                case "ko": return "KO";
                case "fr": return "FR";
                case "de": return "DE";
                case "es": return "ES";
                case "ru": return "RU";
                default: return null;
            }
        }

        /// <summary>
        /// Converts full-width punctuation in the translated text back to the
        /// half-width characters used in the original English message.
        /// </summary>
        private static string NormalizePunctuation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = text.Replace('，', ',').Replace('。', '.').Replace('？', '?').Replace('！', '!')
                       .Replace('：', ':').Replace('；', ';').Replace('（', '(').Replace('）', ')')
                       .Replace('、', ',').Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'');
            return text;
        }

        /// <summary>
        /// Replaces timestamps and links with placeholder tokens, protects the
        /// bracket characters ( ) [ ] themselves, and protects proper nouns /
        /// special terms, so the translator cannot convert or drop them while
        /// still translating the rest of the text.
        /// </summary>
        private static string ProtectTokens(string text, List<string> protectedTokens)
        {
            foreach (string noun in SpecialNouns)
            {
                Regex nounPattern = new Regex(@"\b" + Regex.Escape(noun) + @"\b", RegexOptions.IgnoreCase);
                text = nounPattern.Replace(text, match =>
                {
                    string token = "GTB" + protectedTokens.Count;
                    protectedTokens.Add(match.Value);
                    return token;
                });
            }

            // Protect runs of capitalized words - likely proper names
            // (e.g. "John Smith") so Google does not translate them.
            Regex capitalizedRun = new Regex(@"\b[A-Z][a-zA-Z]+(?:\s+[A-Z][a-zA-Z]+){1,3}\b");
            text = capitalizedRun.Replace(text, match =>
            {
                string token = "GTB" + protectedTokens.Count;
                protectedTokens.Add(match.Value);
                return token;
            });

            // Protect a single capitalized name after common speech verbs
            // (e.g. "saw John", "met Sarah").
            Regex nameAfterVerb = new Regex(@"(?i)\b(saw|met|said|called|asked|told|greeted|noticed|spotted)\s+([A-Z][a-z]+)\b");
            text = nameAfterVerb.Replace(text, match =>
            {
                string token = "GTB" + protectedTokens.Count;
                protectedTokens.Add(match.Groups[2].Value);
                return match.Groups[1].Value + " " + token;
            });

            return ProtectedPattern.Replace(text, match =>
            {
                string token = "GTB" + protectedTokens.Count;
                protectedTokens.Add(match.Value);
                return token;
            });
        }

        /// <summary>
        /// Restores the original tokens from their placeholder tokens.
        /// Replaced in reverse order so that GTB10+ is not partially
        /// matched by the shorter GTB1..GTB9 tokens.
        /// </summary>
        private static string RestoreTokens(string text, List<string> protectedTokens)
        {
            for (int i = protectedTokens.Count - 1; i >= 0; i--)
                text = text.Replace("GTB" + i, protectedTokens[i]);
            return text;
        }

        private static string ParseTranslation(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object root = serializer.DeserializeObject(json);
            object[] rootArray = root as object[];
            if (rootArray == null || rootArray.Length == 0)
                return string.Empty;

            object[] segments = rootArray[0] as object[];
            if (segments == null)
                return string.Empty;

            StringBuilder result = new StringBuilder();
            foreach (object segment in segments)
            {
                object[] part = segment as object[];
                if (part != null && part.Length > 0 && part[0] != null)
                    result.Append(part[0].ToString());
            }

            return result.ToString();
        }

        private static string ParseDeepSeekResponse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            IDictionary<string, object> root = serializer.DeserializeObject(json) as IDictionary<string, object>;
            if (root == null || root.ContainsKey("error"))
                throw new InvalidOperationException("DeepSeek returned an error response.");

            object[] choices = root["choices"] as object[];
            if (choices == null || choices.Length == 0)
                throw new InvalidOperationException("DeepSeek returned no choices.");

            IDictionary<string, object> firstChoice = choices[0] as IDictionary<string, object>;
            IDictionary<string, object> message = firstChoice != null && firstChoice.ContainsKey("message")
                ? firstChoice["message"] as IDictionary<string, object>
                : null;
            string content = message != null && message.ContainsKey("content") ? message["content"] as string : null;
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("DeepSeek returned an empty translation.");

            return content.Trim();
        }

        /// <summary>
        /// Extracts the total token usage from an OpenAI-compatible response.
        /// Returns 0 when the response does not carry a usage object.
        /// </summary>
        private static int ParseUsageTokens(string json)
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                IDictionary<string, object> root = serializer.DeserializeObject(json) as IDictionary<string, object>;
                if (root != null && root.ContainsKey("usage"))
                {
                    IDictionary<string, object> usage = root["usage"] as IDictionary<string, object>;
                    if (usage != null && usage.ContainsKey("total_tokens"))
                    {
                        object value = usage["total_tokens"];
                        if (value != null)
                            return Convert.ToInt32(value);
                    }
                }
            }
            catch
            {
                // usage is optional; fall through to 0
            }

            return 0;
        }

        /// <summary>
        /// A single cached translation shown in the overview page.
        /// </summary>
        public class TranslationCacheEntry
        {
            public string Source { get; set; }
            public string Translation { get; set; }
        }
    }
}