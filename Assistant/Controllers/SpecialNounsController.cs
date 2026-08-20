using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Assistant.Controllers
{
    /// <summary>
    /// A single special-noun entry: an English term with per-language
    /// translations (zh-CN, zh-TW, ...). Exposed as properties so the WPF
    /// data binding in the special-nouns list works.
    /// </summary>
    public class SpecialNounEntry
    {
        public string en { get; set; }
        public string zhCN { get; set; }
        public string zhTW { get; set; }
    }

    /// <summary>
    /// Manages special-noun dictionaries stored as JSON files in
    /// %LocalAppData%\GTAW-Log-Parser\special-nouns. Every file holds a list
    /// of terms with per-language translations. During translation the terms
    /// are replaced with placeholders and restored as the target-language
    /// translation afterwards, so place names like "Vespucci Boulevard" are
    /// always rendered in the selected language instead of being freely
    /// translated (or kept in English).
    /// </summary>
    public static class SpecialNounsController
    {
        public static readonly string SpecialNounsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GTAW-Log-Parser", "special-nouns");

        /// <summary>Language codes supported by the built-in dictionaries.</summary>
        public static readonly string[] KnownLanguages = { "zh-CN", "zh-TW" };

        // Version of the built-in gta5-streets dictionary. Existing files with
        // an older or missing version are replaced with the current data.
        private const int DefaultDictionaryVersion = 4;

        private const string PlaceholderPrefix = "\u25C6SN"; // ◇SN
        private const string PlaceholderSuffix = "\u25C6";   // ◇

        private static readonly object SyncRoot = new object();
        // file name without ".json" -> its entries
        private static readonly Dictionary<string, List<SpecialNounEntry>> FileEntries =
            new Dictionary<string, List<SpecialNounEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates the special-nouns folder, writes the default GTA5 street
        /// dictionary and the reference example when missing, then loads every
        /// JSON file. Call once at startup.
        /// </summary>
        public static void EnsureDefaults()
        {
            try
            {
                Directory.CreateDirectory(SpecialNounsDirectory);
                string defaultFile = Path.Combine(SpecialNounsDirectory, "gta5-streets.json");
                if (!File.Exists(defaultFile) || ReadVersion(defaultFile) < DefaultDictionaryVersion)
                    SaveFileEntries("gta5-streets", BuildDefaultEntries());
                EnsureReferenceExample();
                LoadAll();
            }
            catch
            {
                // The dictionary is optional; a failure must never crash the app.
            }
        }

        private static int ReadVersion(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return 0;
                Dictionary<string, object> root = ReadRootObject(path);
                object value;
                if (root != null && root.TryGetValue("_version", out value) && value != null)
                {
                    int version;
                    if (int.TryParse(value.ToString(), out version))
                        return version;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Reloads every JSON file from disk (call after manual edits).</summary>
        public static void LoadAll()
        {
            lock (SyncRoot)
            {
                FileEntries.Clear();
                try
                {
                    Directory.CreateDirectory(SpecialNounsDirectory);
                    foreach (string file in Directory.GetFiles(SpecialNounsDirectory, "*.json"))
                    {
                        try
                        {
                            string name = Path.GetFileNameWithoutExtension(file);
                            Dictionary<string, object> root = ReadRootObject(file);
                            List<SpecialNounEntry> entries = ParseEntries(root);
                            FileEntries[name] = entries;
                        }
                        catch
                        {
                            // Skip corrupt files.
                        }
                    }
                }
                catch
                {
                    // Folder unreadable - keep whatever was loaded.
                }
            }
        }

        public static bool HasNouns
        {
            get { lock (SyncRoot) return FileEntries.Values.Any(list => list.Count > 0); }
        }

        /// <summary>Returns the names of all dictionary files (without extension).</summary>
        public static List<string> ListFiles()
        {
            lock (SyncRoot)
                return new List<string>(FileEntries.Keys);
        }

        /// <summary>Returns a copy of the entries of one dictionary file.</summary>
        public static List<SpecialNounEntry> GetEntries(string fileName)
        {
            lock (SyncRoot)
            {
                List<SpecialNounEntry> list;
                if (!FileEntries.TryGetValue(fileName ?? string.Empty, out list))
                    return new List<SpecialNounEntry>();
                return list.Select(e => new SpecialNounEntry { en = e.en, zhCN = e.zhCN, zhTW = e.zhTW }).ToList();
            }
        }

        /// <summary>
        /// Adds or updates a term in the given dictionary file and persists it.
        /// </summary>
        public static void AddEntry(string fileName, string en, string zhCN, string zhTW)
        {
            if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(fileName))
                return;
            lock (SyncRoot)
            {
                List<SpecialNounEntry> list;
                if (!FileEntries.TryGetValue(fileName, out list))
                {
                    list = new List<SpecialNounEntry>();
                    FileEntries[fileName] = list;
                }
                SpecialNounEntry existing = list.FirstOrDefault(e => string.Equals(e.en, en, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.zhCN = zhCN ?? string.Empty;
                    existing.zhTW = zhTW ?? string.Empty;
                }
                else
                {
                    list.Add(new SpecialNounEntry { en = en, zhCN = zhCN ?? string.Empty, zhTW = zhTW ?? string.Empty });
                }
                SaveFileEntriesLocked(fileName, list);
            }
        }

        /// <summary>Removes a term from the given dictionary file and persists it.</summary>
        public static void RemoveEntry(string fileName, string en)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;
            lock (SyncRoot)
            {
                List<SpecialNounEntry> list;
                if (!FileEntries.TryGetValue(fileName, out list))
                    return;
                list.RemoveAll(e => string.Equals(e.en, en, StringComparison.OrdinalIgnoreCase));
                SaveFileEntriesLocked(fileName, list);
            }
        }

        /// <summary>
        /// Reads an external JSON file and returns the English terms found in
        /// it (either a plain string array, {"words": [...]} or an array of
        /// noun objects with an "en" field).
        /// </summary>
        public static List<string> ReadTermsFromFile(string path)
        {
            List<string> terms = new List<string>();
            string json = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object root = serializer.DeserializeObject(json);

            object[] array = root as object[];
            if (array != null)
            {
                foreach (object item in array)
                {
                    Dictionary<string, object> obj = item as Dictionary<string, object>;
                    if (obj != null)
                    {
                        object en;
                        if (obj.TryGetValue("en", out en) && en != null)
                            AddUnique(terms, en.ToString());
                    }
                    else if (item != null)
                    {
                        AddUnique(terms, item.ToString());
                    }
                }
                return terms;
            }

            Dictionary<string, object> dict = root as Dictionary<string, object>;
            if (dict != null)
            {
                object words;
                if (dict.TryGetValue("words", out words))
                {
                    object[] wordArray = words as object[];
                    if (wordArray != null)
                    {
                        foreach (object w in wordArray)
                        {
                            if (w != null)
                                AddUnique(terms, w.ToString());
                        }
                    }
                }
                object nouns;
                if (dict.TryGetValue("nouns", out nouns))
                {
                    object[] nounArray = nouns as object[];
                    if (nounArray != null)
                    {
                        foreach (object item in nounArray)
                        {
                            Dictionary<string, object> obj = item as Dictionary<string, object>;
                            if (obj != null)
                            {
                                object en;
                                if (obj.TryGetValue("en", out en) && en != null)
                                    AddUnique(terms, en.ToString());
                            }
                        }
                    }
                }
            }
            return terms;
        }

        /// <summary>Creates the reference example file when it does not exist.</summary>
        public static void EnsureReferenceExample()
        {
            string examplePath = Path.Combine(SpecialNounsDirectory, "reference-example.json");
            if (File.Exists(examplePath))
                return;
            try
            {
                List<SpecialNounEntry> entries = new List<SpecialNounEntry>
                {
                    new SpecialNounEntry { en = "Example Street", zhCN = "示例街道", zhTW = "示例街道" },
                    new SpecialNounEntry { en = "Del Perro", zhCN = "德尔佩罗", zhTW = "德爾佩羅" }
                };
                WriteJsonFile(examplePath, "reference-example", entries, true);
            }
            catch
            {
                // Optional helper file - ignore failures.
            }
        }

        /// <summary>Opens the special-nouns folder in Explorer.</summary>
        public static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(SpecialNounsDirectory);
                System.Diagnostics.Process.Start("explorer.exe", SpecialNounsDirectory);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Replaces every known term found in the text with a placeholder.
        /// The original term is appended to placeholders and the target-
        /// language translation (or the original when no translation exists)
        /// is appended to replacements, so Restore() can put them back.
        /// </summary>
        public static string Protect(string text, string targetLanguage, List<string> placeholders, List<string> replacements)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            List<SpecialNounEntry> all;
            lock (SyncRoot)
                all = FileEntries.Values.SelectMany(list => list)
                    .Where(e => !string.IsNullOrWhiteSpace(e.en))
                    .OrderByDescending(e => e.en.Length)
                    .ToList();

            foreach (SpecialNounEntry entry in all)
            {
                Regex pattern = new Regex(@"\b" + Regex.Escape(entry.en) + @"\b", RegexOptions.IgnoreCase);
                text = pattern.Replace(text, match =>
                {
                    string token = PlaceholderPrefix + placeholders.Count + PlaceholderSuffix;
                    placeholders.Add(match.Value);
                    string replacement = entry.en;
                    if (!string.IsNullOrWhiteSpace(targetLanguage))
                    {
                        string translated = null;
                        if (string.Equals(targetLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase))
                            translated = entry.zhCN;
                        else if (string.Equals(targetLanguage, "zh-TW", StringComparison.OrdinalIgnoreCase))
                            translated = entry.zhTW;
                        if (!string.IsNullOrWhiteSpace(translated))
                            replacement = translated;
                    }
                    replacements.Add(replacement);
                    return token;
                });
            }
            return text;
        }

        /// <summary>Restores placeholders created by Protect() using the stored replacements.</summary>
        public static string Restore(string text, List<string> placeholders, List<string> replacements)
        {
            if (string.IsNullOrEmpty(text) || placeholders == null)
                return text;
            for (int i = 0; i < placeholders.Count; i++)
            {
                string token = PlaceholderPrefix + i + PlaceholderSuffix;
                string replacement = replacements != null && i < replacements.Count ? replacements[i] : placeholders[i];
                text = text.Replace(token, replacement);
            }
            return text;
        }

        // ---------------------------------------------------------------- //

        private static void AddUnique(List<string> list, string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length > 0 && !list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                list.Add(trimmed);
        }

        private static Dictionary<string, object> ReadRootObject(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.DeserializeObject(json) as Dictionary<string, object>;
        }

        private static List<SpecialNounEntry> ParseEntries(Dictionary<string, object> root)
        {
            List<SpecialNounEntry> entries = new List<SpecialNounEntry>();
            if (root == null)
                return entries;
            object nouns;
            if (!root.TryGetValue("nouns", out nouns))
                return entries;
            object[] array = nouns as object[];
            if (array == null)
                return entries;
            foreach (object item in array)
            {
                Dictionary<string, object> obj = item as Dictionary<string, object>;
                if (obj == null)
                    continue;
                SpecialNounEntry entry = new SpecialNounEntry();
                object value;
                if (obj.TryGetValue("en", out value) && value != null)
                    entry.en = value.ToString();
                if (obj.TryGetValue("zh-CN", out value) && value != null)
                    entry.zhCN = value.ToString();
                if (obj.TryGetValue("zh-TW", out value) && value != null)
                    entry.zhTW = value.ToString();
                if (!string.IsNullOrWhiteSpace(entry.en))
                    entries.Add(entry);
            }
            return entries;
        }

        /// <summary>
        /// Replaces the in-memory entries of a dictionary file with the given
        /// list and persists them. Used both internally and by the UI to save
        /// pending edits after user confirmation.
        /// </summary>
        public static void SaveFileEntries(string fileName, List<SpecialNounEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;
            lock (SyncRoot)
            {
                List<SpecialNounEntry> copy = entries == null
                    ? new List<SpecialNounEntry>()
                    : entries.Select(e => new SpecialNounEntry { en = e.en, zhCN = e.zhCN, zhTW = e.zhTW }).ToList();
                FileEntries[fileName] = copy;
                SaveFileEntriesLocked(fileName, copy);
            }
        }

        private static void SaveFileEntriesLocked(string fileName, List<SpecialNounEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(SpecialNounsDirectory);
                WriteJsonFile(Path.Combine(SpecialNounsDirectory, fileName + ".json"), fileName, entries, false);
            }
            catch
            {
                // Persistence is best-effort.
            }
        }

        private static void WriteJsonFile(string path, string name, List<SpecialNounEntry> entries, bool includeNote)
        {
            List<Dictionary<string, object>> nounList = new List<Dictionary<string, object>>();
            foreach (SpecialNounEntry e in entries)
            {
                nounList.Add(new Dictionary<string, object>
                {
                    { "en", e.en },
                    { "zh-CN", e.zhCN },
                    { "zh-TW", e.zhTW }
                });
            }
            Dictionary<string, object> root = new Dictionary<string, object>
            {
                { "name", name },
                { "_version", DefaultDictionaryVersion },
                { "nouns", nounList }
            };
            if (includeNote)
                root["_comment"] = "en: English term; zh-CN: Simplified Chinese; zh-TW: Traditional Chinese. Copy this file to add more dictionaries. During translation a matched 'en' term is replaced with the translation of the target language.";
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            File.WriteAllText(path, serializer.Serialize(root), Encoding.UTF8);
        }

        private static List<SpecialNounEntry> BuildDefaultEntries()
        {
            // Districts / landmarks first (official GTA V Chinese names), then
            // the complete official GTA V street list exactly as it appears
            // in the game data (e.g. "Boulevard Del Perro"). No unofficial
            // variants are added.
            string[,] data = new string[,]
            {
                // ---- Regions / districts (official Chinese names) ----
                { "Los Santos", "洛圣都", "洛聖都" },
                { "San Andreas", "圣安地列斯", "聖安地列斯" },
                { "Blaine County", "布莱恩郡", "布萊恩郡" },
                { "Paleto Bay", "帕莱托湾", "帕萊托灣" },
                { "Sandy Shores", "桑迪海岸", "桑迪海岸" },
                { "Grapeseed", "葡萄籽", "葡萄籽" },
                { "Harmony", "和谐镇", "和諧鎮" },
                { "Mount Chiliad", "奇利亚德山", "奇利亞德山" },
                { "Fort Zancudo", "桑库多堡垒", "桑庫多堡壘" },
                { "Vinewood", "好麦坞", "好麥塢" },
                { "Vinewood Hills", "好麦坞山", "好麥塢山" },
                { "Del Perro", "佩罗", "佩羅" },
                { "Del Perro Beach", "佩罗海滩", "佩羅海灘" },
                { "Vespucci", "威斯普奇", "威斯普奇" },
                { "Vespucci Beach", "威斯普奇海滩", "威斯普奇海灘" },
                { "Cypress Flats", "柏树公寓", "柏樹公寓" },
                { "Chumash", "丘马什", "丘馬什" },
                { "Davis", "戴维斯", "戴維斯" },
                { "Strawberry", "斯卓贝利", "斯卓貝利" },
                { "Rancho", "蓝丘", "藍丘" },
                { "La Mesa", "梅萨", "梅薩" },
                { "El Burro Heights", "布罗高地", "布羅高地" },
                { "Murrieta Heights", "穆列塔高地", "穆列塔高地" },
                { "Richman", "里奇曼", "里奇曼" },
                { "Rockford Hills", "罗克福德山", "羅克福德山" },
                { "Burton", "伯顿", "伯頓" },
                { "Morningwood", "摩宁坞", "摩寧塢" },
                { "Downtown", "洛圣都市区", "洛聖都市區" },
                { "Little Seoul", "小首尔", "小首爾" },
                { "La Puerta", "洛波塔", "洛波塔" },
                { "Elysian Island", "极乐岛", "極樂島" },
                { "Banham Canyon", "班汉峡谷", "班漢峽谷" },
                { "Tongva Hills", "通瓦山丘", "通瓦山丘" },
                { "Palomino Highlands", "帕洛米诺高地", "帕洛米諾高地" },
                { "Tataviam Mountains", "塔塔维安山脉", "塔塔維安山脈" },
                { "Senora Desert", "塞诺拉沙漠", "塞諾拉沙漠" },
                { "Grand Senora Desert", "大塞诺拉沙漠", "大塞諾拉沙漠" },
                { "Great Chaparral", "大灌木丛", "大灌木叢" },
                { "Zancudo River", "桑库多河", "桑庫多河" },
                { "Raton Canyon", "拉顿峡谷", "拉頓峽谷" },
                { "North Chumash", "北丘马什", "北丘馬什" },
                { "Pacific Bluffs", "太平崖", "太平崖" },
                { "Legion Square", "军团广场", "軍團廣場" },
                { "Maze Bank", "花园银行", "花園銀行" },
                { "Pillbox Hill", "圆堡山", "圓堡山" },
                { "Mission Row", "密申罗", "密申羅" },
                { "Textile City", "纺织城", "紡織城" },
                { "Hawick", "霍威克", "霍威克" },
                { "Alta", "阿尔塔", "阿爾塔" },

                // ---- Official GTA V street names (game data, no variants) ----
                { "Abattoir Ave", "阿巴图瓦大道", "阿巴圖瓦大道" },
                { "Abe Milton Pkwy", "阿贝米尔顿大道", "阿貝米爾頓大道" },
                { "Ace Jones Dr", "艾斯琼斯大道", "艾斯瓊斯大道" },
                { "Adam's Apple Blvd", "亚当苹果大道", "亞當蘋果大道" },
                { "Aguja St", "阿古哈街", "阿古哈街" },
                { "Algonquin Blvd", "阿尔冈昆大道", "阿爾岡昆大道" },
                { "Alhambra Dr", "阿尔罕布拉大道", "阿爾罕布拉大道" },
                { "Alta Pl", "阿尔塔广场", "阿爾塔廣場" },
                { "Alta St", "阿尔塔街", "阿爾塔街" },
                { "Amarillo Vista", "阿马里洛观景道", "阿馬里洛觀景道" },
                { "Amarillo Way", "阿马里洛路", "阿馬里洛路" },
                { "Americano Way", "美式路", "美式路" },
                { "Armadillo Ave", "犰狳大道", "犰狳大道" },
                { "Atlee St", "阿特利街", "阿特利街" },
                { "Autopia Pkwy", "奥托皮亚大道", "奧托皮亞大道" },
                { "Bait St", "贝特街", "貝特街" },
                { "Banham Canyon Dr", "班汉峡谷大道", "班漢峽谷大道" },
                { "Barbareno Rd", "巴巴雷诺路", "巴巴雷諾路" },
                { "Bay City Ave", "海湾城大道", "海灣城大道" },
                { "Bay City Incline", "海湾城坡道", "海灣城坡道" },
                { "Baytree Canyon Rd", "贝特里峡谷路", "貝特里峽谷路" },
                { "Boulevard Del Perro", "德尔佩罗大道", "德爾佩羅大道" },
                { "Bridge St", "大桥街", "大橋街" },
                { "Brouge Ave", "布罗热大道", "布羅熱大道" },
                { "Buccaneer Way", "海盗路", "海盜路" },
                { "Buen Vino Rd", "好酒路", "好酒路" },
                { "Caesars Place", "凯撒广场", "凱撒廣場" },
                { "Calafia Rd", "卡拉菲亚路", "卡拉菲亞路" },
                { "Calais Ave", "加莱大道", "加萊大道" },
                { "Capital Blvd", "首都大道", "首都大道" },
                { "Carcer Way", "卡瑟路", "卡瑟路" },
                { "Carson Ave", "卡森大道", "卡森大道" },
                { "Cascabel Ave", "卡斯卡贝尔大道", "卡斯卡貝爾大道" },
                { "Cassidy Trail", "卡西迪小径", "卡西迪小徑" },
                { "Cat-Claw Ave", "猫爪大道", "貓爪大道" },
                { "Catfish View", "鲶鱼观景道", "鯰魚觀景道" },
                { "Cavalry Blvd", "骑兵大道", "騎兵大道" },
                { "Chianski Passage", "钱斯基通道", "錢斯基通道" },
                { "Cholla Rd", "乔拉路", "喬拉路" },
                { "Cholla Springs Ave", "乔拉泉大道", "喬拉泉大道" },
                { "Chum St", "丘姆街", "丘姆街" },
                { "Chupacabra St", "丘帕卡布拉街", "丘帕卡布拉街" },
                { "Clinton Ave", "克林顿大道", "克林頓大道" },
                { "Cockingend Dr", "科金恩德大道", "科金恩德大道" },
                { "Conquistador St", "征服者街", "征服者街" },
                { "Cortes St", "科尔特斯街", "科爾特斯街" },
                { "Cougar Ave", "美洲狮大道", "美洲獅大道" },
                { "Covenant Ave", "圣约大道", "聖約大道" },
                { "Cox Way", "考克斯路", "考克斯路" },
                { "Crusade Rd", "十字军路", "十字軍路" },
                { "Davis Ave", "戴维斯大道", "戴維斯大道" },
                { "Decker St", "德克尔街", "德克爾街" },
                { "Del Perro Fwy", "德尔佩罗高速", "德爾佩羅高速" },
                { "Didion Dr", "迪迪安大道", "迪迪安大道" },
                { "Dorset Dr", "多塞特大道", "多塞特大道" },
                { "Dorset Pl", "多塞特广场", "多塞特廣場" },
                { "Dry Dock St", "干船坞街", "幹船塢街" },
                { "Duluoz Ave", "杜卢奥兹大道", "杜盧奧茲大道" },
                { "Dunstable Dr", "邓斯特布尔大道", "鄧斯特布爾大道" },
                { "Dunstable Ln", "邓斯特布尔巷", "鄧斯特布爾巷" },
                { "Dutch London St", "荷兰伦敦街", "荷蘭倫敦街" },
                { "East Galileo Ave", "东伽利略大道", "東伽利略大道" },
                { "East Joshua Road", "东约书亚路", "東約書亞路" },
                { "East Mirror Dr", "东米罗大道", "東米羅大道" },
                { "Eastbourne Way", "伊斯特本路", "伊斯特本路" },
                { "Eclipse Blvd", "日蚀大道", "日蝕大道" },
                { "Edwood Way", "埃德伍德路", "埃德伍德路" },
                { "El Burro Blvd", "布罗大道", "布羅大道" },
                { "El Gordo Dr", "埃尔戈多大道", "埃爾戈多大道" },
                { "El Rancho Blvd", "牧场大道", "牧場大道" },
                { "Elgin Ave", "埃尔金大道", "埃爾金大道" },
                { "Elysian Fields Fwy", "极乐之地高速", "極樂之地高速" },
                { "Equality Way", "平等路", "平等路" },
                { "Exceptionalists Way", "精英路", "精英路" },
                { "Fantastic Pl", "梦幻广场", "夢幻廣場" },
                { "Fenwell Pl", "芬韦尔广场", "芬韋爾廣場" },
                { "Fort Zancudo Approach Rd", "桑库多堡垒引道路", "桑庫多堡壘引道路" },
                { "Forum Dr", "论坛大道", "論壇大道" },
                { "Fudge Ln", "福奇巷", "福奇巷" },
                { "Galileo Park", "伽利略公园", "伽利略公園" },
                { "Galileo Rd", "伽利略路", "伽利略路" },
                { "Gentry Lane", "绅士巷", "紳士巷" },
                { "Ginger St", "金格街", "金格街" },
                { "Glory Way", "荣耀路", "榮耀路" },
                { "Goma St", "戈马街", "戈馬街" },
                { "Grapeseed Ave", "葡萄籽大道", "葡萄籽大道" },
                { "Grapeseed Main St", "葡萄籽主街", "葡萄籽主街" },
                { "Great Ocean Hwy", "大洋高速公路", "大洋高速公路" },
                { "Greenwich Pkwy", "格林威治大道", "格林威治大道" },
                { "Greenwich Pl", "格林威治广场", "格林威治廣場" },
                { "Greenwich Way", "格林威治路", "格林威治路" },
                { "Grove St", "葛洛夫街", "葛洛夫街" },
                { "Hanger Way", "亨格路", "亨格路" },
                { "Hangman Ave", "绞刑者大道", "絞刑者大道" },
                { "Hardy Way", "哈迪路", "哈迪路" },
                { "Hawick Ave", "霍威克大道", "霍威克大道" },
                { "Heritage Way", "传承路", "傳承路" },
                { "Hillcrest Ave", "山顶大道", "山頂大道" },
                { "Hillcrest Ridge Access Rd", "山顶岭辅路", "山頂嶺輔路" },
                { "Imagination Court", "想象庭院", "想像庭院" },
                { "Imagination Ct", "想象庭院", "想像庭院" },
                { "Ineseno Road", "伊内塞诺路", "伊內塞諾路" },
                { "Innocence Blvd", "纯真大道", "純真大道" },
                { "Integrity Way", "诚信路", "誠信路" },
                { "Invention Court", "发明庭院", "發明庭院" },
                { "Invention Ct", "发明庭院", "發明庭院" },
                { "Jamestown St", "詹姆斯敦街", "詹姆斯敦街" },
                { "Joad Ln", "乔德巷", "喬德巷" },
                { "Joshua Rd", "约书亚路", "約書亞路" },
                { "Kimble Hill Dr", "金布尔山大道", "金布爾山大道" },
                { "Kortz Dr", "科茨大道", "科茨大道" },
                { "La Puerta Fwy", "洛波塔高速", "洛波塔高速" },
                { "Labor Pl", "劳工广场", "勞工廣場" },
                { "Laguna Pl", "拉古纳广场", "拉古納廣場" },
                { "Lake Vinewood Dr", "好麦坞湖大道", "好麥塢湖大道" },
                { "Lake Vinewood Est", "好麦坞湖庄园", "好麥塢湖莊園" },
                { "Las Lagunas Blvd", "拉斯拉古纳斯大道", "拉斯拉古納斯大道" },
                { "Lesbos Ln", "莱斯博斯巷", "萊斯博斯巷" },
                { "Liberty St", "自由街", "自由街" },
                { "Lindsay Circus", "林赛环道", "林賽環道" },
                { "Little Bighorn Ave", "小比格霍恩大道", "小比格霍恩大道" },
                { "Lolita Ave", "洛丽塔大道", "洛麗塔大道" },
                { "Los Santos Freeway", "洛圣都高速", "洛聖都高速" },
                { "Low Power St", "洛帕瓦街", "洛帕瓦街" },
                { "Macdonald St", "麦克唐纳街", "麥克唐納街" },
                { "Mad Wayne Thunder Dr", "马德韦恩桑德大道", "馬德韋恩桑德大道" },
                { "Magellan Ave", "麦哲伦大道", "麥哲倫大道" },
                { "Marathon Ave", "马拉松大道", "馬拉松大道" },
                { "Marina Dr", "码头大道", "碼頭大道" },
                { "Marlowe Dr", "马洛大道", "馬洛大道" },
                { "Melanoma St", "梅拉诺玛街", "梅拉諾瑪街" },
                { "Meringue Ln", "梅伦格巷", "梅倫格巷" },
                { "Meteor St", "流星街", "流星街" },
                { "Milton Rd", "米尔顿路", "米爾頓路" },
                { "Miriam Turner Overpass", "米里亚姆特纳立交", "米里亞姆特納立交" },
                { "Mirror Park Blvd", "米罗公园大道", "米羅公園大道" },
                { "Mirror Pl", "米罗广场", "米羅廣場" },
                { "Morningwood Blvd", "摩宁坞大道", "摩寧塢大道" },
                { "Mountain View Dr", "山景大道", "山景大道" },
                { "Movie Star Way", "影星路", "影星路" },
                { "Mt Haan Dr", "哈恩山大道", "哈恩山大道" },
                { "Mt Haan Rd", "哈恩山路", "哈恩山路" },
                { "Mt Vinewood Dr", "好麦坞山大道", "好麥塢山大道" },
                { "Mutiny Rd", "叛乱路", "叛亂路" },
                { "New Empire Way", "新帝国路", "新帝國路" },
                { "Nikola Ave", "尼古拉大道", "尼古拉大道" },
                { "Nikola Pl", "尼古拉广场", "尼古拉廣場" },
                { "Niland Ave", "尼兰德大道", "尼蘭德大道" },
                { "Normandy Dr", "诺曼底大道", "諾曼底大道" },
                { "North Archer Ave", "北阿彻大道", "北阿徹大道" },
                { "North Calafia Way", "北卡拉菲亚路", "北卡拉菲亞路" },
                { "North Conker Ave", "北康克大道", "北康克大道" },
                { "North Rockford Dr", "北罗克福德大道", "北羅克福德大道" },
                { "North Sheldon Ave", "北谢尔登大道", "北謝爾登大道" },
                { "Nowhere Rd", "无处路", "無處路" },
                { "Occupation Ave", "占领大道", "佔領大道" },
                { "Olympic Fwy", "奥林匹克高速", "奧林匹克高速" },
                { "O'Neil Way", "奥尼尔路", "奧尼爾路" },
                { "Orchardville Ave", "果园维尔大道", "果園維爾大道" },
                { "Paleto Blvd", "帕莱托大道", "帕萊托大道" },
                { "Palomino Ave", "帕洛米诺大道", "帕洛米諾大道" },
                { "Palomino Freeway", "帕洛米诺高速", "帕洛米諾高速" },
                { "Palomino Fwy", "帕洛米诺高速", "帕洛米諾高速" },
                { "Panorama Dr", "全景大道", "全景大道" },
                { "Peaceful St", "和平街", "和平街" },
                { "Perth St", "珀斯街", "珀斯街" },
                { "Picture Perfect Drive", "完美如画大道", "完美如畫大道" },
                { "Plaice Pl", "普莱斯广场", "普萊斯廣場" },
                { "Playa Vista", "海滩景观道", "海灘景觀道" },
                { "Popular St", "流行街", "流行街" },
                { "Portola Dr", "普托拉车道", "普托拉車道" },
                { "Power St", "电力街", "電力街" },
                { "Procopio Dr", "普罗科皮奥大道", "普羅科皮奧大道" },
                { "Procopio Promenade", "普罗科皮奥长廊", "普羅科皮奧長廊" },
                { "Prosperity St", "繁荣街", "繁榮街" },
                { "Prosperity Street Promenade", "繁荣街长廊", "繁榮街長廊" },
                { "Pyrite Ave", "派莱特大道", "派萊特大道" },
                { "Raton Pass", "拉顿山口", "拉頓山口" },
                { "Red Desert Ave", "红沙漠大道", "紅沙漠大道" },
                { "Richman St", "里奇曼街", "里奇曼街" },
                { "Rockford Dr", "罗克福德大道", "羅克福德大道" },
                { "Route 68", "68号公路", "68號公路" },
                { "Route 68 Approach", "68号公路引道", "68號公路引道" },
                { "Roy Lowenstein Blvd", "罗伊洛温斯坦大道", "羅伊洛溫斯坦大道" },
                { "Rub St", "鲁布街", "魯布街" },
                { "Runway1", "一号跑道", "一號跑道" },
                { "Sam Austin Dr", "萨姆奥斯汀大道", "薩姆奧斯汀大道" },
                { "San Andreas Ave", "圣安地列斯大道", "聖安地列斯大道" },
                { "San Vitus Blvd", "圣维特斯大道", "聖維特斯大道" },
                { "Sandcastle Way", "沙堡路", "沙堡路" },
                { "Seaview Rd", "海景路", "海景路" },
                { "Senora Fwy", "塞诺拉高速", "塞諾拉高速" },
                { "Senora Rd", "塞诺拉路", "塞諾拉路" },
                { "Senora Way", "塞诺拉路", "塞諾拉路" },
                { "Shank St", "尚克街", "尚克街" },
                { "Signal St", "信号街", "信號街" },
                { "Sinner St", "罪人街", "罪人街" },
                { "Sinners Passage", "罪人通道", "罪人通道" },
                { "Smoke Tree Rd", "烟树路", "煙樹路" },
                { "South Arsenal St", "南阿森纳街", "南阿森納街" },
                { "South Boulevard Del Perro", "南德尔佩罗大道", "南德爾佩羅大道" },
                { "South Mo Milton Dr", "南莫米尔顿大道", "南莫米爾頓大道" },
                { "South Rockford Dr", "南罗克福德大道", "南羅克福德大道" },
                { "South Shambles St", "南尚布尔斯街", "南尚布爾斯街" },
                { "Spanish Ave", "西班牙大道", "西班牙大道" },
                { "Steele Way", "斯蒂尔路", "斯蒂爾路" },
                { "Strangeways Dr", "斯特兰奇韦斯大道", "斯特蘭奇韋斯大道" },
                { "Strawberry Ave", "斯卓贝利大道", "斯卓貝利大道" },
                { "Supply St", "供应街", "供應街" },
                { "Sustancia Rd", "苏斯坦西亚路", "蘇斯坦西亞路" },
                { "Swiss St", "瑞士街", "瑞士街" },
                { "Tackle St", "塔克尔街", "塔克爾街" },
                { "Tangerine St", "橘街", "橘街" },
                { "Tongva Dr", "通瓦大道", "通瓦大道" },
                { "Tower Way", "塔路", "塔路" },
                { "Tug St", "拖船街", "拖船街" },
                { "Union Rd", "联合路", "聯合路" },
                { "Utopia Gardens", "乌托邦花园", "烏托邦花園" },
                { "Vespucci Blvd", "威斯普奇大道", "威斯普奇大道" },
                { "Vinewood Blvd", "好麦坞大道", "好麥塢大道" },
                { "Vinewood Park Dr", "好麦坞公园大道", "好麥塢公園大道" },
                { "Vitus St", "维特斯街", "維特斯街" },
                { "Voodoo Place", "巫毒广场", "巫毒廣場" },
                { "West Eclipse Blvd", "西日蚀大道", "西日蝕大道" },
                { "West Galileo Ave", "西伽利略大道", "西伽利略大道" },
                { "West Mirror Drive", "西米罗大道", "西米羅大道" },
                { "Whispymound Dr", "惠斯皮芒德大道", "惠斯皮芒德大道" },
                { "Wild Oats Dr", "野燕麦大道", "野燕麥大道" },
                { "York St", "约克街", "約克街" },
                { "Zancudo Ave", "桑库多大道", "桑庫多大道" },
                { "Zancudo Barranca", "桑库多峡谷", "桑庫多峽谷" },
                { "Zancudo Grande Valley", "大桑库多谷", "大桑庫多谷" },
                { "Zancudo Rd", "桑库多路", "桑庫多路" }
            };
            List<SpecialNounEntry> entries = new List<SpecialNounEntry>();
            for (int i = 0; i < data.GetLength(0); i++)
            {
                entries.Add(new SpecialNounEntry { en = data[i, 0], zhCN = data[i, 1], zhTW = data[i, 2] });
            }
            return entries;
        }
    }
}
