using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
        private const int MaxCacheEntries = 500;

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
                    TranslationCache.Clear();
                TranslationCache[key] = value;
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

            string cacheKey = "p:" + (provider ?? string.Empty) + "|s:" + (sourceLanguage ?? string.Empty) + "|t:" + (targetLanguage ?? string.Empty)
                + "|m:" + (model ?? string.Empty) + "|y:" + (style ?? string.Empty) + "|q:" + (prompt ?? string.Empty) + "|" + text;
            string cached = GetCachedTranslation(cacheKey);
            if (cached != null)
                return cached;

            string result;
            if (string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase))
                result = TranslateWithDeepSeek(text, targetLanguage, sourceLanguage, apiKey, model, prompt, style);
            else
                result = Translate(text, targetLanguage, sourceLanguage);

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
                { "stream", false }
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
            return NormalizePunctuation(RestoreTokens(translated, protectedTokens));
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
    }
}
