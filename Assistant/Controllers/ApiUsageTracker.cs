using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Assistant.Controllers
{
    /// <summary>
    /// Tracks API usage per model and per calendar day (request count and
    /// input / output tokens) and persists it to disk so the API usage page
    /// can render charts. Values are kept in memory and flushed on a timer,
    /// mirroring TranslationStats.
    /// </summary>
    public static class ApiUsageTracker
    {
        /// <summary>
        /// One model's aggregated counters for a single day.
        /// </summary>
        public sealed class DayUsage
        {
            public long Requests;
            public long InputTokens;
            public long OutputTokens;
        }

        /// <summary>
        /// One model's usage series used by the UI charts.
        /// </summary>
        public sealed class ModelSeries
        {
            public string Model;
            public long TotalRequests;
            public long TotalTokens;
            public List<DayPoint> Days = new List<DayPoint>();
        }

        /// <summary>
        /// A single point of a daily usage series.
        /// </summary>
        public sealed class DayPoint
        {
            public DateTime Date;
            public long Requests;
            public long InputTokens;
            public long OutputTokens;

            public long TotalTokens
            {
                get { return InputTokens + OutputTokens; }
            }
        }

        // key: "yyyy-MM-dd" -> key: model name -> counters
        private static readonly Dictionary<string, Dictionary<string, DayUsage>> Usage =
            new Dictionary<string, Dictionary<string, DayUsage>>(StringComparer.Ordinal);

        private static readonly object Lock = new object();
        private static readonly string FileDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GTAW-Log-Parser");
        private static readonly string FilePath = Path.Combine(FileDirectory, "api-usage.json");
        private static bool dirty;
        private static readonly Timer FlushTimer;

        static ApiUsageTracker()
        {
            Load();
            FlushTimer = new Timer(FlushTimerCallback, null, 3000, 3000);
        }

        /// <summary>
        /// Normalizes a model label for display and grouping. Empty or null
        /// values become "Unknown"; provider prefixes are kept as-is.
        /// </summary>
        private static string NormalizeModel(string model)
        {
            string value = string.IsNullOrWhiteSpace(model) ? "Unknown" : model.Trim();
            return value.Length > 48 ? value.Substring(0, 48) : value;
        }

        /// <summary>
        /// Records a real API call with known input/output token counts.
        /// </summary>
        public static void RecordUsage(string model, long inputTokens, long outputTokens)
        {
            string key = NormalizeModel(model);
            long input = inputTokens < 0 ? 0 : inputTokens;
            long output = outputTokens < 0 ? 0 : outputTokens;
            lock (Lock)
            {
                DayUsage usage = GetDayUsage(key, DateTime.Today);
                usage.Requests++;
                usage.InputTokens += input;
                usage.OutputTokens += output;
                dirty = true;
            }
        }

        /// <summary>
        /// Records an API call whose usage is estimated from the source text
        /// (providers that do not report usage). The estimate is counted as
        /// input tokens; the output is left at zero.
        /// </summary>
        public static void RecordUsageEstimated(string model, string text)
        {
            long input = EstimateTokens(text);
            lock (Lock)
            {
                DayUsage usage = GetDayUsage(NormalizeModel(model), DateTime.Today);
                usage.Requests++;
                usage.InputTokens += input;
                dirty = true;
            }
        }

        /// <summary>
        /// Returns one series per model for the last <paramref name="days"/>
        /// days (including today). Days with no usage are filled with zeroes
        /// so the charts render continuous axes. Models are ordered by total
        /// token consumption, descending.
        /// </summary>
        public static List<ModelSeries> GetSeries(int days)
        {
            if (days < 1)
                days = 7;

            DateTime today = DateTime.Today;
            Dictionary<string, DayUsage>[] buckets = new Dictionary<string, DayUsage>[days];
            Dictionary<string, ModelSeries> byModel = new Dictionary<string, ModelSeries>(StringComparer.Ordinal);

            lock (Lock)
            {
                for (int i = 0; i < days; i++)
                {
                    string dayKey = today.AddDays(i - days + 1).ToString("yyyy-MM-dd");
                    Dictionary<string, DayUsage> models;
                    Usage.TryGetValue(dayKey, out models);
                    buckets[i] = models;
                }

                foreach (Dictionary<string, DayUsage> models in buckets)
                {
                    if (models == null)
                        continue;
                    foreach (KeyValuePair<string, DayUsage> pair in models)
                    {
                        ModelSeries series;
                        if (!byModel.TryGetValue(pair.Key, out series))
                        {
                            series = new ModelSeries { Model = pair.Key };
                            byModel[pair.Key] = series;
                        }
                    }
                }
            }

            List<ModelSeries> result = new List<ModelSeries>(byModel.Count);
            foreach (KeyValuePair<string, ModelSeries> pair in byModel)
            {
                ModelSeries series = pair.Value;
                for (int i = 0; i < days; i++)
                {
                    DayPoint point = new DayPoint { Date = today.AddDays(i - days + 1) };
                    Dictionary<string, DayUsage> models = buckets[i];
                    DayUsage usage;
                    if (models != null && models.TryGetValue(series.Model, out usage))
                    {
                        point.Requests = usage.Requests;
                        point.InputTokens = usage.InputTokens;
                        point.OutputTokens = usage.OutputTokens;
                        series.TotalRequests += usage.Requests;
                        series.TotalTokens += usage.InputTokens + usage.OutputTokens;
                    }
                    series.Days.Add(point);
                }
                result.Add(series);
            }

            result.Sort((a, b) => b.TotalTokens.CompareTo(a.TotalTokens));
            return result;
        }

        /// <summary>
        /// Flushes pending usage to disk immediately (called on shutdown).
        /// </summary>
        public static void Flush()
        {
            FlushTimerCallback(null);
        }

        private static DayUsage GetDayUsage(string model, DateTime date)
        {
            string dayKey = date.ToString("yyyy-MM-dd");
            Dictionary<string, DayUsage> models;
            if (!Usage.TryGetValue(dayKey, out models))
            {
                models = new Dictionary<string, DayUsage>(StringComparer.Ordinal);
                Usage[dayKey] = models;
            }
            DayUsage usage;
            if (!models.TryGetValue(model, out usage))
            {
                usage = new DayUsage();
                models[model] = usage;
            }
            return usage;
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

                Dictionary<string, object> snapshot = new Dictionary<string, object>();
                lock (Lock)
                {
                    foreach (KeyValuePair<string, Dictionary<string, DayUsage>> day in Usage)
                    {
                        Dictionary<string, object> models = new Dictionary<string, object>();
                        foreach (KeyValuePair<string, DayUsage> pair in day.Value)
                        {
                            models[pair.Key] = new Dictionary<string, object>
                            {
                                { "r", pair.Value.Requests },
                                { "i", pair.Value.InputTokens },
                                { "o", pair.Value.OutputTokens }
                            };
                        }
                        snapshot[day.Key] = models;
                    }
                }

                Directory.CreateDirectory(FileDirectory);
                string json = new JavaScriptSerializer().Serialize(snapshot);
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            }
            catch
            {
                // never let usage persistence break translation
            }
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
                    foreach (KeyValuePair<string, object> day in data)
                    {
                        Dictionary<string, object> models = day.Value as Dictionary<string, object>;
                        if (models == null)
                            continue;
                        Dictionary<string, DayUsage> modelMap = new Dictionary<string, DayUsage>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, object> pair in models)
                        {
                            Dictionary<string, object> counters = pair.Value as Dictionary<string, object>;
                            if (counters == null)
                                continue;
                            DayUsage usage = new DayUsage
                            {
                                Requests = GetLong(counters, "r"),
                                InputTokens = GetLong(counters, "i"),
                                OutputTokens = GetLong(counters, "o")
                            };
                            modelMap[pair.Key] = usage;
                        }
                        if (modelMap.Count > 0)
                            Usage[day.Key] = modelMap;
                    }
                }
            }
            catch
            {
                // corrupt or missing usage file is not fatal
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
