using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Web.Script.Serialization;

namespace Assistant.Localization
{
    /// <summary>
    /// A ResourceManager that reads localized strings from editable JSON files
    /// located in the "Languages" directory next to the executable.
    /// To add a new language, drop a new "&lt;code&gt;.json" file (e.g. "zh-CN.json")
    /// into that directory. Missing keys fall back to the default language
    /// and finally to the embedded .resx resources
    /// </summary>
    public class JsonResourceManager : ResourceManager
    {
        private const string DefaultLanguage = "en-US";
        private const string DisplayNameKey = "_DisplayName";
        private static readonly object Lock = new object();
        private static Dictionary<string, Dictionary<string, string>> _cache;

        public JsonResourceManager(string baseName, Assembly assembly) : base(baseName, assembly)
        {
        }

        private static string LanguagesDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages");

        private static Dictionary<string, Dictionary<string, string>> Cache
        {
            get
            {
                if (_cache == null)
                {
                    lock (Lock)
                    {
                        if (_cache == null)
                            _cache = LoadLanguages();
                    }
                }
                return _cache;
            }
        }

        /// <summary>
        /// Loads all language dictionaries.
        /// Embedded JSON resources are loaded first, then any JSON files
        /// found in the "Languages" directory next to the executable
        /// override the embedded ones (allows editing without recompiling)
        /// </summary>
        private static Dictionary<string, Dictionary<string, string>> LoadLanguages()
        {
            Dictionary<string, Dictionary<string, string>> languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Assembly assembly = typeof(JsonResourceManager).Assembly;

            // 1. Load embedded resources
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith("Assistant.Localization.Languages.", StringComparison.OrdinalIgnoreCase) ||
                    !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                string code = resourceName.Substring("Assistant.Localization.Languages.".Length);
                code = code.Substring(0, code.Length - ".json".Length);

                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null)
                            continue;
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            Dictionary<string, string> entries = serializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
                            if (entries != null)
                                languages[code] = entries;
                        }
                    }
                }
                catch
                {
                    // Ignore malformed language files
                }
            }

            // 2. External files (if any) override embedded ones
            try
            {
                if (Directory.Exists(LanguagesDirectory))
                {
                    foreach (string file in Directory.GetFiles(LanguagesDirectory, "*.json"))
                    {
                        try
                        {
                            Dictionary<string, string> entries = serializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                            if (entries != null)
                                languages[Path.GetFileNameWithoutExtension(file)] = entries;
                        }
                        catch
                        {
                            // Ignore malformed language files
                        }
                    }
                }
            }
            catch
            {
                // Directory access failed, fall back to embedded resources
            }

            return languages;
        }

        /// <summary>
        /// Looks up a key in the JSON file of a given language code
        /// </summary>
        /// <param name="name"></param>
        /// <param name="code"></param>
        /// <returns></returns>
        private static string Lookup(string name, string code)
        {
            if (string.IsNullOrEmpty(code))
                return null;

            Dictionary<string, string> entries;
            if (!Cache.TryGetValue(code, out entries))
                return null;

            string value;
            return entries.TryGetValue(name, out value) ? value : null;
        }

        /// <summary>
        /// Returns the display name ("_DisplayName" key) of a given
        /// language code. Defaults to the code itself
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static string GetDisplayName(string code)
        {
            return Lookup(DisplayNameKey, code) ?? code;
        }

        /// <summary>
        /// Returns all language codes that have a JSON
        /// file in the Languages directory
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<string> GetAvailableLanguages()
        {
            return Cache.Keys;
        }

        /// <summary>
        /// Returns the localized string for the given name and culture.
        /// Falls back to the parent culture, the default language and
        /// finally the embedded .resx resources
        /// </summary>
        /// <param name="name"></param>
        /// <param name="culture"></param>
        /// <returns></returns>
        public override string GetString(string name, CultureInfo culture)
        {
            if (culture == null)
                culture = CultureInfo.CurrentUICulture;

            string value = Lookup(name, culture.Name);
            if (value == null && culture.Parent != null && !string.IsNullOrEmpty(culture.Parent.Name))
                value = Lookup(name, culture.Parent.Name);
            if (value == null)
                value = Lookup(name, DefaultLanguage);

            return value ?? base.GetString(name, culture);
        }

        /// <summary>
        /// Returns the localized string for the given name
        /// using the current UI culture
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public override string GetString(string name)
        {
            return GetString(name, CultureInfo.CurrentUICulture);
        }
    }
}
