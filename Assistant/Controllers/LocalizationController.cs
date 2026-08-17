using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Assistant.Localization;

namespace Assistant.Controllers
{
    public static class LocalizationController
    {
        public const string DefaultLanguage = "en-US";

        private static string currentLanguage = string.Empty;
        private static readonly List<string> Languages = new List<string>();

        /// <summary>
        /// All available language codes, discovered from the
        /// JSON files in the Languages directory
        /// </summary>
        public static IReadOnlyList<string> AvailableLanguages
        {
            get
            {
                if (Languages.Count == 0)
                    DiscoverLanguages();
                return Languages;
            }
        }

        /// <summary>
        /// Discovers all available languages from the
        /// JSON files in the Languages directory.
        /// The default language always comes first,
        /// the rest is sorted alphabetically by code
        /// </summary>
        private static void DiscoverLanguages()
        {
            Languages.Clear();
            Languages.Add(DefaultLanguage);
            foreach (string code in JsonResourceManager.GetAvailableLanguages().OrderBy(code => code))
            {
                if (code != DefaultLanguage)
                    Languages.Add(code);
            }
        }

        /// <summary>
        /// Changes the current thread's UI culture to the one in @currentLanguage 
        /// if it is not empty, otherwise grabs it from the settings. 
        /// Optionally saves @currentLanguage to the settings
        /// </summary>
        /// <param name="save"></param>
        public static void InitializeLocale(bool save = false)
        {
            if (string.IsNullOrWhiteSpace(currentLanguage))
                currentLanguage = Properties.Settings.Default.LanguageCode;

            if (!AvailableLanguages.Contains(currentLanguage))
                currentLanguage = DefaultLanguage;

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(currentLanguage);

            if (!save) return;
            Properties.Settings.Default.LanguageCode = currentLanguage;
            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Returns the @currentLanguage code
        /// </summary>
        /// <returns></returns>
        public static string GetLanguage()
        {
            return currentLanguage;
        }

        /// <summary>
        /// Sets the @currentLanguage to a given language code
        /// Defaults to the default language if the code
        /// has no matching JSON language file
        /// </summary>
        /// <param name="code"></param>
        /// <param name="save"></param>
        public static void SetLanguage(string code, bool save = true)
        {
            if (!AvailableLanguages.Contains(code))
                code = DefaultLanguage;

            currentLanguage = code;
            InitializeLocale(save);
        }

        /// <summary>
        /// Returns the display name of a given language code
        /// (the "_DisplayName" key of its JSON file).
        /// Defaults to the code itself
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static string GetDisplayName(string code)
        {
            return JsonResourceManager.GetDisplayName(code);
        }

        /// <summary>
        /// Returns the index of a given language code
        /// in the @AvailableLanguages list. Defaults to 0
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static int GetLanguageIndex(string code)
        {
            for (int i = 0; i < AvailableLanguages.Count; ++i)
            {
                if (AvailableLanguages[i] == code)
                    return i;
            }
            return 0;
        }
    }
}
