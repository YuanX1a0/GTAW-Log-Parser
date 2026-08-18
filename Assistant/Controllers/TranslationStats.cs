using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Assistant.Controllers
{
    /// <summary>
    /// Tracks cumulative translation statistics (total translations, translated
    /// characters, API calls and token usage) and persists them to disk so the
    /// numbers survive an application restart.
    /// </summary>
    public static class TranslationStats
    {
        private static long totalTranslations;
        private static long totalCharacters;
        private static long totalApiCalls;
        private static long totalTokens;

        private static readonly object Lock = new object();
        private static readonly string FileDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GTAW-Log-Parser");
        private static readonly string FilePath = Path.Combine(FileDirectory, "translation-stats.json");
        private static bool dirty;
        private static readonly Timer FlushTimer;

        private static readonly Dictionary<string, long> WordCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex WordPattern = new Regex(@"[A-Za-z][A-Za-z0-9'_]*");
        private const int MaxPersistedWords = 500;

        /// <summary>
        /// Common English function words and casual chat fillers that are
        /// excluded from the "most translated words" statistics.
        /// </summary>
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // articles & particles
            "a", "an", "the",
            // prepositions
            "about", "above", "across", "after", "against", "along", "among", "around", "at",
            "before", "behind", "below", "beneath", "beside", "between", "beyond", "by",
            "during", "for", "from", "in", "inside", "into", "near", "of", "off", "on",
            "onto", "out", "outside", "over", "past", "through", "throughout", "to", "toward",
            "towards", "under", "underneath", "until", "up", "upon", "within", "without",
            // conjunctions
            "and", "but", "or", "nor", "so", "yet", "because", "although", "though",
            "while", "when", "if", "unless", "since",
            // pronouns
            "i", "me", "my", "mine", "myself", "we", "us", "our", "ours", "ourselves",
            "you", "your", "yours", "yourself", "yourselves", "he", "him", "his", "himself",
            "she", "her", "hers", "herself", "it", "its", "itself", "they", "them", "their",
            "theirs", "themselves", "this", "that", "these", "those", "who", "whom", "whose",
            "which", "what", "whatever", "whoever",
            // auxiliary & modal verbs
            "is", "am", "are", "was", "were", "be", "been", "being", "have", "has", "had",
            "having", "do", "does", "did", "doing", "will", "would", "shall", "should",
            "can", "could", "may", "might", "must", "ought",
            // adverbs, quantifiers & fillers
            "all", "any", "both", "each", "either", "few", "more", "most", "much", "neither",
            "no", "none", "not", "only", "other", "others", "own", "same", "several", "some",
            "such", "than", "then", "there", "here", "too", "very", "just", "even", "still",
            "already", "again", "also", "always", "never", "often", "sometimes", "usually",
            "where", "why", "how",
            // common contractions (with and without apostrophes)
            "i'm", "im", "i've", "ive", "i'll", "ill", "i'd", "id", "you're", "youre",
            "you've", "youve", "you'll", "youll", "you'd", "youd", "he's", "hes", "she's",
            "shes", "it's", "we're", "we've", "weve", "we'll", "we'd", "wed",
            "they're", "theyre", "they've", "theyve", "they'll", "theyll", "they'd", "theyd",
            "don't", "dont", "doesn't", "doesnt", "didn't", "didnt", "won't", "wont",
            "wouldn't", "wouldnt", "couldn't", "couldnt", "shouldn't", "shouldnt", "isn't",
            "isnt", "aren't", "arent", "wasn't", "wasnt", "weren't", "werent", "hasn't",
            "hasnt", "haven't", "havent", "hadn't", "hadnt", "can't", "cant", "cannot",
            "ain't", "aint", "let's", "lets", "that's", "thats", "there's", "theres",
            "what's", "whats", "who's", "whos",
            // casual chat fillers
            "yeah", "yea", "yep", "yup", "nope", "ok", "okay", "hey", "hi", "hello",
            "lol", "haha", "lmao", "omg"
        };

        static TranslationStats()
        {
            Load();
            FlushTimer = new Timer(FlushTimerCallback, null, 3000, 3000);
        }

        public static long TotalTranslations
        {
            get { lock (Lock) return totalTranslations; }
        }

        public static long TotalCharacters
        {
            get { lock (Lock) return totalCharacters; }
        }

        public static long TotalApiCalls
        {
            get { lock (Lock) return totalApiCalls; }
        }

        public static long TotalTokens
        {
            get { lock (Lock) return totalTokens; }
        }

        /// <summary>
        /// Records one translation operation (including cache hits).
        /// </summary>
        public static void RecordTranslation(string text)
        {
            int chars = string.IsNullOrEmpty(text) ? 0 : text.Length;
            lock (Lock)
            {
                totalTranslations++;
                totalCharacters += chars;
                if (chars > 0)
                    CountWords(text);
            }
            MarkDirty();
        }

        /// <summary>
        /// Returns the most frequently translated words in descending order.
        /// </summary>
        public static List<KeyValuePair<string, long>> TopWords(int count)
        {
            lock (Lock)
            {
                List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>();
                foreach (KeyValuePair<string, long> pair in WordCounts)
                {
                    if (StopWords.Contains(pair.Key))
                        continue;
                    list.Add(pair);
                }
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                if (count > 0 && list.Count > count)
                    list.RemoveRange(count, list.Count - count);
                return list;
            }
        }

        /// <summary>
        /// Splits the translated text into English words and increments their
        /// counters. Must be called while holding <see cref="Lock"/>.
        /// </summary>
        private static void CountWords(string text)
        {
            foreach (Match match in WordPattern.Matches(text))
            {
                string word = match.Value.ToLowerInvariant();
                if (word.Length < 2)
                    continue;
                if (StopWords.Contains(word))
                    continue;

                long count;
                WordCounts.TryGetValue(word, out count);
                WordCounts[word] = count + 1;
            }
        }

        /// <summary>
        /// Records one real API call with a known token count.
        /// </summary>
        public static void RecordApiCall(int tokens)
        {
            lock (Lock)
            {
                totalApiCalls++;
                totalTokens += tokens < 0 ? 0 : tokens;
            }
            MarkDirty();
        }

        /// <summary>
        /// Records one real API call and estimates its token usage from the
        /// character count (used for providers that do not return usage).
        /// </summary>
        public static void RecordApiCallEstimated(string text)
        {
            lock (Lock)
            {
                totalApiCalls++;
                totalTokens += EstimateTokens(text);
            }
            MarkDirty();
        }

        private static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int cjk = 0;
            int other = 0;
            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                    cjk++;
                else if (!char.IsWhiteSpace(c))
                    other++;
            }

            return cjk + (int)Math.Ceiling(other / 4.0);
        }

        private static void MarkDirty()
        {
            lock (Lock)
            {
                dirty = true;
            }
        }

        /// <summary>
        /// Builds a JSON-friendly snapshot of the most frequent words.
        /// Must be called while holding <see cref="Lock"/>.
        /// </summary>
        private static Dictionary<string, long> BuildWordSnapshot()
        {
            List<KeyValuePair<string, long>> top = new List<KeyValuePair<string, long>>();
            foreach (KeyValuePair<string, long> pair in WordCounts)
            {
                if (StopWords.Contains(pair.Key))
                    continue;
                top.Add(pair);
            }
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (top.Count > MaxPersistedWords)
                top.RemoveRange(MaxPersistedWords, top.Count - MaxPersistedWords);

            Dictionary<string, long> result = new Dictionary<string, long>(top.Count);
            foreach (KeyValuePair<string, long> pair in top)
                result[pair.Key] = pair.Value;
            return result;
        }

        private static void FlushTimerCallback(object state)
        {
            try
            {
                bool needSave;
                lock (Lock)
                {
                    needSave = dirty;
                    dirty = false;
                }

                if (!needSave)
                    return;

                Dictionary<string, object> snapshot;
                lock (Lock)
                {
                    snapshot = new Dictionary<string, object>
                    {
                        { "translations", totalTranslations },
                        { "characters", totalCharacters },
                        { "apiCalls", totalApiCalls },
                        { "tokens", totalTokens },
                        { "words", BuildWordSnapshot() }
                    };
                }

                Directory.CreateDirectory(FileDirectory);
                string json = new JavaScriptSerializer().Serialize(snapshot);
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            }
            catch
            {
                // never let stats persistence break translation
            }
        }

        /// <summary>
        /// Flushes pending statistics to disk immediately (called on shutdown).
        /// </summary>
        public static void Flush()
        {
            FlushTimerCallback(null);
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (data == null)
                    return;

                lock (Lock)
                {
                    totalTranslations = GetLong(data, "translations");
                    totalCharacters = GetLong(data, "characters");
                    totalApiCalls = GetLong(data, "apiCalls");
                    totalTokens = GetLong(data, "tokens");
                    LoadWords(data);
                }
            }
            catch
            {
                // corrupt or missing stats file is not fatal
            }
        }

        private static void LoadWords(Dictionary<string, object> data)
        {
            object wordsObj;
            if (!data.TryGetValue("words", out wordsObj))
                return;

            Dictionary<string, object> words = wordsObj as Dictionary<string, object>;
            if (words == null)
                return;

            foreach (KeyValuePair<string, object> pair in words)
            {
                if (StopWords.Contains(pair.Key))
                    continue;
                long count;
                if (long.TryParse(Convert.ToString(pair.Value), out count) && count > 0)
                    WordCounts[pair.Key] = count;
            }
        }

        private static long GetLong(Dictionary<string, object> data, string key)
        {
            object value;
            if (data.TryGetValue(key, out value) && value != null)
            {
                if (value is int)
                    return (int)value;
                if (value is long)
                    return (long)value;
                if (value is decimal)
                    return (long)(decimal)value;

                long parsed;
                if (long.TryParse(value.ToString(), out parsed))
                    return parsed;
            }

            return 0;
        }
    }
}
