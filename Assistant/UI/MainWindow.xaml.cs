using System;
using System.IO;
using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.Windows.Input;
using System.Globalization;
using Assistant.Controllers;
using Assistant.Localization;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.Win32;
using System.Linq;

namespace Assistant.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private static bool isRestarting;
        private System.Windows.Threading.DispatcherTimer _autoParseTimer;
        private string _lastAutoParsedLog = string.Empty;

        /// <summary>
        /// Initializes the main window
        /// </summary>
        /// <param name="startMinimized"></param>
        public MainWindow(bool startMinimized)
        {
            StartupController.InitializeShortcut();

            InitializeComponent();
            SourceInitialized += (s, e) => AppController.ApplyRoundedCorners(this);
            ApplyLocalization();
            InitializeTrayIcon();
            TranslationController.LogEvent("app", "程序启动");
            AboutVersion.Text = "版本：" + AppController.Version + (AppController.IsBetaVersion ? "（测试版）" : string.Empty);

            if (startMinimized)
                _trayIcon.Visible = true;

            LoadSettings();
            InitializeTranslationControls();
            LoadTranslationSettings();
            InitializeRealtimeLog();

            BackupController.Initialize();
        }

        // ================================================================
        //  Navigation
        // ================================================================

        /// <summary>
        /// Switches the content area to the given page
        /// </summary>
        private void ShowPage(string page)
        {
            bool overview = page == "overview";
            bool chat = page == "chatlog";
            bool realtime = page == "realtimelog";
            bool about = page == "about";

            OverviewPage.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            ChatLogPage.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            TranslationPage.Visibility = (overview || chat || realtime || about) ? Visibility.Collapsed : Visibility.Visible;
            RealtimeLogPage.Visibility = realtime ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = about ? Visibility.Visible : Visibility.Collapsed;

            SetNavButton(NavOverview, NavOverviewText, overview);
            SetNavButton(NavChatLog, NavChatLogText, chat);
            SetNavButton(NavTranslation, NavTranslationText, !overview && !chat && !realtime && !about);
            SetNavButton(NavRealtimeLog, NavRealtimeLogText, realtime);
            SetNavButton(NavAbout, NavAboutText, about);

            if (overview)
                RefreshOverviewStats();
            else if (realtime)
                RefreshRealtimeLog();
        }

        /// <summary>
        /// Highlights the selected navigation entry
        /// </summary>
        private void SetNavButton(Button button, TextBlock text, bool selected)
        {
            button.Background = selected
                ? (Brush)FindResource("FluentSelectionBackground")
                : Brushes.Transparent;
            button.Foreground = selected
                ? (Brush)FindResource("FluentAccentBrush")
                : (Brush)FindResource("FluentTextForeground");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void NavOverview_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("overview");
        }

        /// <summary>
        /// Refreshes the statistics shown on the overview page
        /// </summary>
        public void RefreshOverviewStats()
        {
            TotalTranslationsValue.Text = TranslationStats.TotalTranslations.ToString("N0");
            TotalCharactersValue.Text = TranslationStats.TotalCharacters.ToString("N0");
            TotalApiCallsValue.Text = TranslationStats.TotalApiCalls.ToString("N0");
            TotalTokensValue.Text = TranslationStats.TotalTokens.ToString("N0");

            List<WordRank> items = new List<WordRank>();
            int rank = 1;
            foreach (KeyValuePair<string, long> pair in TranslationStats.TopWords(10))
                items.Add(new WordRank { Rank = rank++, Word = pair.Key, Count = pair.Value });
            TopWordsList.ItemsSource = items;

            RefreshCacheStats();
        }

        /// <summary>
        /// Refreshes the translation cache information on the overview page
        /// </summary>
        private void RefreshCacheStats()
        {
            int count = TranslationController.CacheCount;
            long bytes = TranslationController.CacheSizeBytes;
            string sizeText = bytes >= 1024 * 1024
                ? string.Format("{0:F1} MB", bytes / 1024.0 / 1024.0)
                : bytes >= 1024
                    ? string.Format("{0:F1} KB", bytes / 1024.0)
                    : bytes + " B";
            CacheInfo.Text = string.Format(Strings.OverviewCacheInfo, count.ToString("N0"), sizeText);

            CacheList.ItemsSource = TranslationController.GetRecentCacheEntries(200);
        }

        /// <summary>
        /// Reloads the translation cache list when the refresh button is pressed
        /// </summary>
        private void RefreshCacheButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCacheStats();
        }

        private bool _topWordsExpanded;
        private bool _cacheExpanded;

        // While true, translation control change events are ignored so the
        // initial population of the settings page does not write partial values.
        private bool _loadingTranslationSettings;

        /// <summary>
        /// Toggles the "most translated words" list on the overview page.
        /// </summary>
        private void TopWordsToggle_Click(object sender, RoutedEventArgs e)
        {
            _topWordsExpanded = !_topWordsExpanded;
            TopWordsArrow.Text = _topWordsExpanded ? "▾" : "▸";
            TopWordsList.Visibility = _topWordsExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Toggles the translation cache list on the overview page.
        /// </summary>
        private void CacheToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _cacheExpanded = !_cacheExpanded;
            CacheArrow.Text = _cacheExpanded ? "▾" : "▸";
            CacheList.Visibility = _cacheExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (_cacheExpanded)
                RefreshCacheStats();
        }

        private void NavChatLog_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("chatlog");
        }

        private void NavTranslation_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("translation");
        }

        private void NavRealtimeLog_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("realtimelog");
        }

        private void NavFilter_Click(object sender, RoutedEventArgs e)
        {
            OpenChatLogFilter();
        }

        private void NavSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenProgramSettings();
        }

        private void NavAbout_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("about");
        }

        // ================================================================
        //  Realtime log page
        // ================================================================

        private System.Windows.Threading.DispatcherTimer _realtimeLogTimer;

        /// <summary>
        /// Starts a 1-second timer that refreshes the realtime log page
        /// while it is visible
        /// </summary>
        private void InitializeRealtimeLog()
        {
            _realtimeLogTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _realtimeLogTimer.Tick += (s, e) =>
            {
                if (RealtimeLogPage.Visibility == Visibility.Visible)
                    RefreshRealtimeLog();
            };
            _realtimeLogTimer.Start();
        }

        /// <summary>
        /// Reloads the translation error log into the realtime log box
        /// and scrolls to the bottom if new content arrived
        /// </summary>
        private void RefreshRealtimeLog()
        {
            try
            {
                string content = File.Exists(TranslationController.AppLogPath)
                    ? File.ReadAllText(TranslationController.AppLogPath, System.Text.Encoding.UTF8)
                    : string.Empty;
                if (RealtimeLogBox.Text != content)
                {
                    RealtimeLogBox.Text = content;
                    RealtimeLogBox.CaretIndex = RealtimeLogBox.Text.Length;
                    RealtimeLogBox.ScrollToEnd();
                }
            }
            catch
            {
                // The log file may be locked by another writer; retry on the next tick
            }
        }

        private void RefreshLogButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRealtimeLog();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText(TranslationController.AppLogPath, string.Empty);
                RefreshRealtimeLog();
            }
            catch
            {
                // Ignore failures to clear the log file
            }
        }

        // ================================================================
        //  Localization
        // ================================================================

        /// <summary>
        /// Applies the localized strings to the window's controls
        /// </summary>
        private void ApplyLocalization()
        {
            Title = string.Empty;

            NavOverviewText.Text = Strings.NavOverview;
            NavChatLogText.Text = Strings.NavChatLog;
            NavTranslationText.Text = Strings.NavTranslation;
            NavFilterText.Text = Strings.MenuFilterChatLog;
            NavSettingsText.Text = Strings.SettingsTitle;
            NavAboutText.Text = Strings.MenuAbout;

            OverviewTitle.Text = Strings.NavOverview;
            TotalTranslationsLabel.Text = Strings.StatTotalTranslations;
            CacheTitle.Text = Strings.OverviewCacheTitle;
            RefreshCacheButton.Content = Strings.Refresh;
            RefreshCacheStats();
            TotalCharactersLabel.Text = Strings.StatTotalCharacters;
            TotalApiCallsLabel.Text = Strings.StatApiCalls;
            TotalTokensLabel.Text = Strings.StatTokens;
            TopWordsTitle.Text = Strings.StatTopWords;

            ChatLogTitle.Text = Strings.NavChatLog;
            TranslationTitle.Text = Strings.SectionTranslation;
            SectionGeneral.Text = Strings.SectionGeneral;
            SectionProvider.Text = Strings.SectionProvider;
            SectionSend.Text = Strings.SectionSend;
            SectionGameTranslation.Text = Strings.SectionGameTranslation;

            Parse.Content = Strings.Parse;
            SaveParsed.Content = Strings.SaveAs;
            CopyParsedToClipboard.Content = Strings.CopyToClipboard;

            TranslationEnabled.Content = Strings.TranslationEnabled;
            TargetLanguageLabel.Content = Strings.TargetLanguage;
            SourceLanguageLabel.Content = Strings.SourceLanguage;
            TranslationBulkHotkeyLabel.Content = Strings.TranslationBulkHotkey;
            AutoTranslateCheckBox.Content = Strings.AutoTranslate;
            AutoTranslateHotkeyLabel.Content = Strings.AutoTranslateHotkey;
            ShowGameToastsCheckBox.Content = Strings.ShowGameToasts;
            SettingsPageTranslationCheckBox.Content = Strings.SettingsPageTranslation;
            TranslationProviderLabel.Content = Strings.TranslationProvider;
            DeepSeekApiKeyLabel.Content = Strings.DeepSeekApiKey;
            DeepSeekModelLabel.Content = Strings.DeepSeekModel;
            DeepLApiKeyLabel.Content = Strings.DeepLApiKey;
            DoubaoApiKeyLabel.Content = Strings.DoubaoApiKey;
            DoubaoModelLabel.Content = Strings.DoubaoModel;
            ZoomApiKeyLabel.Content = Strings.ZoomApiKey;
            TranslationDisplayModeLabel.Content = Strings.TranslationDisplayMode;
            TranslationPromptLabel.Content = Strings.TranslationPrompt;
            TranslationStyleLabel.Content = Strings.TranslationStyle;
            SendTranslationEnabled.Content = Strings.SendTranslationEnabled;
            SendSourceLanguageLabel.Content = Strings.SourceLanguage;
            SendTargetLanguageLabel.Content = Strings.TargetLanguage;
            SendTranslationKeyLabel.Content = Strings.SendTranslationHotkey;
            SendProviderLabel.Content = Strings.TranslationProvider;
            SendApiKeyLabel.Content = Strings.DeepSeekApiKey;
            SendModelLabel.Content = Strings.DeepSeekModel;
            SendDeepLApiKeyLabel.Content = Strings.SendDeepLApiKey;
            SendDoubaoApiKeyLabel.Content = Strings.SendDoubaoApiKey;
            SendDoubaoModelLabel.Content = Strings.SendDoubaoModel;
            SendZoomApiKeyLabel.Content = Strings.SendZoomApiKey;
            SendPromptLabel.Content = Strings.TranslationPrompt;
        }

        /// <summary>
        /// Restarts the application
        /// </summary>
        public void RestartApplication()
        {
            isRestarting = true;

            ProcessStartInfo startInfo = Process.GetCurrentProcess().StartInfo;
            startInfo.FileName = AppController.ExecutablePath;
            startInfo.Arguments = $"{AppController.ParameterPrefix}restart";
            Process.Start(startInfo);

            System.Windows.Application.Current.Shutdown();
        }

        // ================================================================
        //  Chat log parsing
        // ================================================================

        /// <summary>
        /// Saves the translation settings
        /// </summary>
        private void SaveSettings()
        {
            Properties.Settings.Default.Save();
            AppController.InitializeServerIp();
        }

        /// <summary>
        /// Loads the main settings
        /// </summary>
        private void LoadSettings()
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            // ReSharper disable once UnreachableCode
#pragma warning disable 162
            Version.Text = string.Format(Strings.VersionInfo, AppController.Version, AppController.IsBetaVersion ? Strings.BetaShort : string.Empty);
#pragma warning restore 162
            StatusLabel.Text = string.Format(Strings.BackupStatus, Properties.Settings.Default.BackupChatLogAutomatically ? Strings.Enabled : Strings.Disabled);
            Counter.Text = string.Format(Strings.CharacterCounter, 0, 0);

            if (Properties.Settings.Default.AutoParse)
                StartAutoParse();
            else
                StopAutoParse();

            ShowPage("chatlog");
        }

        /// <summary>
        /// Parses the current chat log and sets
        /// the text of the main text box to it
        /// </summary>
        private void Parse_Click(object sender, RoutedEventArgs e)
        {
            AppController.InitializeServerIp();
            Parsed.Text = AppController.ParseChatLog(false, true);
            _lastAutoParsedLog = Parsed.Text;
        }

        /// <summary>
        /// Starts the real-time parsing timer
        /// </summary>
        public void StartAutoParse()
        {
            if (_autoParseTimer == null)
            {
                _autoParseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = new TimeSpan(0, 0, 2)
                };
                _autoParseTimer.Tick += AutoParseTimer_Tick;
            }

            _lastAutoParsedLog = string.Empty;
            _autoParseTimer.Start();
        }

        /// <summary>
        /// Stops the real-time parsing timer
        /// </summary>
        public void StopAutoParse()
        {
            if (_autoParseTimer == null)
                return;

            _autoParseTimer.Stop();
        }

        /// <summary>
        /// Periodically reads the captured chat log and updates
        /// the main text box only when the content has changed
        /// </summary>
        private void AutoParseTimer_Tick(object sender, EventArgs e)
        {
            string parsed = AppController.ParseChatLog(false);
            if (parsed == _lastAutoParsedLog)
                return;

            _lastAutoParsedLog = parsed;
            Parsed.Text = parsed;
        }

        /// <summary>
        /// Updates the character and line counter
        /// </summary>
        private void Parsed_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Counter == null)
                return;

            if (string.IsNullOrWhiteSpace(Parsed.Text))
            {
                Counter.Text = string.Format(Strings.CharacterCounter, 0, 0);
                return;
            }

            Counter.Text = string.Format(Strings.CharacterCounter, Parsed.Text.Length, Parsed.Text.Split('\n').Length);
        }

        /// <summary>
        /// Displays a save file dialog to save the
        /// contents of the main text box to the disk
        /// </summary>
        private void SaveParsed_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Parsed.Text))
                {
                    if (!Properties.Settings.Default.DisableErrorPopups)
                        MessageBox.Show(Strings.NothingParsed, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);

                    return;
                }

                Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "chatlog.txt",
                    Filter = "Text File | *.txt"
                };

                if (dialog.ShowDialog() != true) return;
                using (StreamWriter sw = new StreamWriter(dialog.OpenFile()))
                {
                    sw.Write(Parsed.Text);
                }
            }
            catch
            {
                MessageBox.Show(Strings.SaveError, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Copies the contents of the
        /// main text box to the clipboard
        /// </summary>
        private void CopyParsedToClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Parsed.Text) && !Properties.Settings.Default.DisableErrorPopups)
                MessageBox.Show(Strings.NothingParsed, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            else
                Clipboard.SetText(Parsed.Text);
        }

        // ================================================================
        //  Translation controls (moved out of the settings window)
        // ================================================================

        /// <summary>
        /// Initializes the target language ComboBox
        /// for the in-game translation feature
        /// </summary>
        private void InitializeTargetLanguageSwitcher()
        {
            TargetLanguageList.Items.Clear();
            foreach (KeyValuePair<string, string> pair in TranslationController.TargetLanguages)
                TargetLanguageList.Items.Add(pair.Value);

            SourceLanguageList.Items.Clear();
            SourceLanguageList.Items.Add(Strings.SourceLanguageAuto);
            foreach (KeyValuePair<string, string> pair in TranslationController.TargetLanguages)
                SourceLanguageList.Items.Add(pair.Value);
        }

        /// <summary>
        /// Initializes the translation provider, DeepSeek model,
        /// Doubao model and translation display mode ComboBoxes
        /// </summary>
        private void InitializeTranslationControls()
        {
            InitializeTargetLanguageSwitcher();

            TranslationProviderList.Items.Clear();
            TranslationProviderList.Items.Add(Strings.TranslationProviderGoogle);
            TranslationProviderList.Items.Add(Strings.TranslationProviderDeepSeek);
            TranslationProviderList.Items.Add(Strings.TranslationProviderDeepL);
            TranslationProviderList.Items.Add(Strings.TranslationProviderDoubao);
            TranslationProviderList.Items.Add(Strings.TranslationProviderZoom);

            TranslationDisplayModeList.Items.Clear();
            TranslationDisplayModeList.Items.Add(Strings.TranslationDisplayAppend);
            TranslationDisplayModeList.Items.Add(Strings.TranslationDisplayReplace);

            SendSourceLanguageList.Items.Clear();
            SendSourceLanguageList.Items.Add(Strings.SourceLanguageAuto);
            foreach (KeyValuePair<string, string> pair in TranslationController.TargetLanguages)
                SendSourceLanguageList.Items.Add(pair.Value);

            SendTargetLanguageList.Items.Clear();
            foreach (KeyValuePair<string, string> pair in TranslationController.TargetLanguages)
                SendTargetLanguageList.Items.Add(pair.Value);

            SendProviderList.Items.Clear();
            SendProviderList.Items.Add(Strings.TranslationProviderGoogle);
            SendProviderList.Items.Add(Strings.TranslationProviderDeepSeek);
            SendProviderList.Items.Add(Strings.TranslationProviderDeepL);
            SendProviderList.Items.Add(Strings.TranslationProviderDoubao);
            SendProviderList.Items.Add(Strings.TranslationProviderZoom);

            SendModelList.Items.Clear();
            foreach (string model in TranslationController.DeepSeekModels)
                SendModelList.Items.Add(model);

            TranslationStyleList.Items.Clear();
            TranslationStyleList.Items.Add(Strings.TranslationStyleCasual);
            TranslationStyleList.Items.Add(Strings.TranslationStyleFormal);
            TranslationStyleList.Items.Add(Strings.TranslationStyleLiterary);

            DeepSeekModelList.Items.Clear();
            foreach (string model in TranslationController.DeepSeekModels)
                DeepSeekModelList.Items.Add(model);

            DoubaoModelList.Items.Clear();
            foreach (string model in TranslationController.DoubaoModels)
                DoubaoModelList.Items.Add(model);

            SendDoubaoModelList.Items.Clear();
            foreach (string model in TranslationController.DoubaoModels)
                SendDoubaoModelList.Items.Add(model);

            SubscribeImmediateSettings();
        }

        /// <summary>
        /// Applies translation settings the moment a control changes, so the
        /// next chat-line / send translation uses the new provider, language,
        /// prompt etc. without restarting the app. Values are also persisted
        /// on exit as before.
        /// </summary>
        private void SubscribeImmediateSettings()
        {
            RoutedEventHandler saveRouted = delegate { if (!_loadingTranslationSettings) SaveTranslationSettings(); };
            SelectionChangedEventHandler saveSelection = delegate { if (!_loadingTranslationSettings) SaveTranslationSettings(); };
            TextChangedEventHandler saveText = delegate { if (!_loadingTranslationSettings) SaveTranslationSettings(); };

            TranslationEnabled.Checked += saveRouted;
            TranslationEnabled.Unchecked += saveRouted;
            TargetLanguageList.SelectionChanged += saveSelection;
            SourceLanguageList.SelectionChanged += saveSelection;
            DeepSeekApiKeyBox.PasswordChanged += saveRouted;
            DeepSeekModelList.SelectionChanged += saveSelection;
            DeepLApiKeyBox.PasswordChanged += saveRouted;
            DoubaoApiKeyBox.PasswordChanged += saveRouted;
            DoubaoModelList.SelectionChanged += saveSelection;
            ZoomApiKeyBox.PasswordChanged += saveRouted;
            TranslationDisplayModeList.SelectionChanged += saveSelection;
            TranslationPromptBox.TextChanged += saveText;
            TranslationStyleList.SelectionChanged += saveSelection;
            TranslationBulkHotkeyBox.TextChanged += saveText;
            AutoTranslateCheckBox.Checked += saveRouted;
            AutoTranslateCheckBox.Unchecked += saveRouted;
            AutoTranslateHotkeyBox.TextChanged += saveText;
            ShowGameToastsCheckBox.Checked += saveRouted;
            ShowGameToastsCheckBox.Unchecked += saveRouted;
            SettingsPageTranslationCheckBox.Checked += saveRouted;
            SettingsPageTranslationCheckBox.Unchecked += saveRouted;

            SendTranslationEnabled.Checked += saveRouted;
            SendTranslationEnabled.Unchecked += saveRouted;
            SendSourceLanguageList.SelectionChanged += saveSelection;
            SendTargetLanguageList.SelectionChanged += saveSelection;
            SendTranslationKeyBox.TextChanged += saveText;
            SendApiKeyBox.PasswordChanged += saveRouted;
            SendModelList.SelectionChanged += saveSelection;
            SendDeepLApiKeyBox.PasswordChanged += saveRouted;
            SendDoubaoApiKeyBox.PasswordChanged += saveRouted;
            SendDoubaoModelList.SelectionChanged += saveSelection;
            SendZoomApiKeyBox.PasswordChanged += saveRouted;
            SendPromptBox.TextChanged += saveText;
        }

        /// <summary>
        /// Saves the translation settings
        /// </summary>
        private void SaveTranslationSettings()
        {
            Properties.Settings.Default.TranslationEnabled = TranslationEnabled.IsChecked == true;
            if (TargetLanguageList.SelectedIndex >= 0 && TargetLanguageList.SelectedIndex < TranslationController.TargetLanguages.Length)
                Properties.Settings.Default.TargetLanguage = TranslationController.TargetLanguages[TargetLanguageList.SelectedIndex].Key;
            if (SourceLanguageList.SelectedIndex == 0)
                Properties.Settings.Default.SourceLanguage = "auto";
            else if (SourceLanguageList.SelectedIndex > 0 && SourceLanguageList.SelectedIndex <= TranslationController.TargetLanguages.Length)
                Properties.Settings.Default.SourceLanguage = TranslationController.TargetLanguages[SourceLanguageList.SelectedIndex - 1].Key;
            if (SendSourceLanguageList.SelectedIndex == 0)
                Properties.Settings.Default.SendSourceLanguage = "auto";
            else if (SendSourceLanguageList.SelectedIndex > 0 && SendSourceLanguageList.SelectedIndex <= TranslationController.TargetLanguages.Length)
                Properties.Settings.Default.SendSourceLanguage = TranslationController.TargetLanguages[SendSourceLanguageList.SelectedIndex - 1].Key;
            if (SendTargetLanguageList.SelectedIndex >= 0 && SendTargetLanguageList.SelectedIndex < TranslationController.TargetLanguages.Length)
                Properties.Settings.Default.SendTargetLanguage = TranslationController.TargetLanguages[SendTargetLanguageList.SelectedIndex].Key;
            Properties.Settings.Default.TranslationProvider = ProviderName(TranslationProviderList.SelectedIndex);
            Properties.Settings.Default.DeepSeekApiKey = DeepSeekApiKeyBox.Password;
            Properties.Settings.Default.DeepLApiKey = DeepLApiKeyBox.Password;
            Properties.Settings.Default.DoubaoApiKey = DoubaoApiKeyBox.Password;
            Properties.Settings.Default.ZoomApiKey = ZoomApiKeyBox.Password;
            if (DeepSeekModelList.SelectedIndex >= 0)
                Properties.Settings.Default.DeepSeekModel = DeepSeekModelList.SelectedItem.ToString();
            Properties.Settings.Default.DoubaoModel = string.IsNullOrWhiteSpace(DoubaoModelList.Text)
                ? "doubao-seed-2.0-lite"
                : DoubaoModelList.Text.Trim();
            Properties.Settings.Default.TranslationDisplayMode = TranslationDisplayModeList.SelectedIndex == 1 ? "replace" : "append";
            Properties.Settings.Default.TranslationPrompt = TranslationPromptBox.Text;
            string bulkHotkey = (TranslationBulkHotkeyBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.TranslationBulkHotkey = string.IsNullOrEmpty(bulkHotkey) ? "Ctrl+F9" : bulkHotkey;
            Properties.Settings.Default.AutoTranslate = AutoTranslateCheckBox.IsChecked == true;
            string autoHotkey = (AutoTranslateHotkeyBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.ShowGameToasts = ShowGameToastsCheckBox.IsChecked == true;
            Properties.Settings.Default.SettingsPageTranslation = SettingsPageTranslationCheckBox.IsChecked == true;
            Properties.Settings.Default.AutoTranslateHotkey = string.IsNullOrEmpty(autoHotkey) ? "Ctrl+Shift+F9" : autoHotkey;
            Properties.Settings.Default.SendTranslationEnabled = SendTranslationEnabled.IsChecked == true;
            string hotkey = (SendTranslationKeyBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.SendTranslationHotkey = string.IsNullOrEmpty(hotkey) ? "F9" : hotkey;
            Properties.Settings.Default.SendTranslationProvider = ProviderName(SendProviderList.SelectedIndex);
            Properties.Settings.Default.SendDeepSeekApiKey = SendApiKeyBox.Password;
            Properties.Settings.Default.SendDeepLApiKey = SendDeepLApiKeyBox.Password;
            Properties.Settings.Default.SendDoubaoApiKey = SendDoubaoApiKeyBox.Password;
            Properties.Settings.Default.SendZoomApiKey = SendZoomApiKeyBox.Password;
            if (SendModelList.SelectedIndex >= 0)
                Properties.Settings.Default.SendDeepSeekModel = SendModelList.SelectedItem.ToString();
            Properties.Settings.Default.SendDoubaoModel = string.IsNullOrWhiteSpace(SendDoubaoModelList.Text)
                ? "doubao-seed-2.0-lite"
                : SendDoubaoModelList.Text.Trim();
            Properties.Settings.Default.SendTranslationPrompt = SendPromptBox.Text;
            Properties.Settings.Default.TranslationStyle = TranslationStyleList.SelectedIndex == 1 ? "formal" : TranslationStyleList.SelectedIndex == 2 ? "literary" : "casual";

            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Loads the translation settings
        /// </summary>
        private void LoadTranslationSettings()
        {
            _loadingTranslationSettings = true;
            TranslationEnabled.IsChecked = Properties.Settings.Default.TranslationEnabled;
            SelectTargetLanguage(Properties.Settings.Default.TargetLanguage);
            SelectSendLanguage(SourceLanguageList, Properties.Settings.Default.SourceLanguage, true);
            SelectSendLanguage(SendSourceLanguageList, Properties.Settings.Default.SendSourceLanguage, true);
            SelectSendLanguage(SendTargetLanguageList, Properties.Settings.Default.SendTargetLanguage, false);
            TranslationProviderList.SelectedIndex = ProviderIndex(Properties.Settings.Default.TranslationProvider);
            DeepSeekApiKeyBox.Password = Properties.Settings.Default.DeepSeekApiKey;
            DeepLApiKeyBox.Password = Properties.Settings.Default.DeepLApiKey;
            DoubaoApiKeyBox.Password = Properties.Settings.Default.DoubaoApiKey;
            ZoomApiKeyBox.Password = Properties.Settings.Default.ZoomApiKey;
            SelectDeepSeekModel(Properties.Settings.Default.DeepSeekModel);
            SelectDoubaoModel(Properties.Settings.Default.DoubaoModel);
            TranslationDisplayModeList.SelectedIndex = Properties.Settings.Default.TranslationDisplayMode == "replace" ? 1 : 0;
            TranslationPromptBox.Text = Properties.Settings.Default.TranslationPrompt;
            SendTranslationEnabled.IsChecked = Properties.Settings.Default.SendTranslationEnabled;
            SendTranslationKeyBox.Text = Properties.Settings.Default.SendTranslationHotkey;
            SendProviderList.SelectedIndex = ProviderIndex(Properties.Settings.Default.SendTranslationProvider);
            SendApiKeyBox.Password = Properties.Settings.Default.SendDeepSeekApiKey;
            SendDeepLApiKeyBox.Password = Properties.Settings.Default.SendDeepLApiKey;
            SendDoubaoApiKeyBox.Password = Properties.Settings.Default.SendDoubaoApiKey;
            SendZoomApiKeyBox.Password = Properties.Settings.Default.SendZoomApiKey;
            SelectSendModel(Properties.Settings.Default.SendDeepSeekModel);
            SelectSendDoubaoModel(Properties.Settings.Default.SendDoubaoModel);
            SendPromptBox.Text = Properties.Settings.Default.SendTranslationPrompt;
            TranslationStyleList.SelectedIndex = "formal".Equals(Properties.Settings.Default.TranslationStyle) ? 1 : "literary".Equals(Properties.Settings.Default.TranslationStyle) ? 2 : 0;
            TranslationBulkHotkeyBox.Text = Properties.Settings.Default.TranslationBulkHotkey;
            AutoTranslateCheckBox.IsChecked = Properties.Settings.Default.AutoTranslate;
            AutoTranslateHotkeyBox.Text = Properties.Settings.Default.AutoTranslateHotkey;
            ShowGameToastsCheckBox.IsChecked = Properties.Settings.Default.ShowGameToasts;
            SettingsPageTranslationCheckBox.IsChecked = Properties.Settings.Default.SettingsPageTranslation;
            UpdateTranslationProviderState();
            UpdateSendTranslationState();
            UpdateSendProviderState();
            _loadingTranslationSettings = false;
        }

        /// <summary>
        /// Selects the Doubao model matching the given name or endpoint ID.
        /// The box is editable so any Ark inference endpoint ID also works.
        /// </summary>
        private void SelectDoubaoModel(string model)
        {
            int index = -1;
            for (int i = 0; i < TranslationController.DoubaoModels.Length; ++i)
            {
                if (string.Equals(TranslationController.DoubaoModels[i], model, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (index >= 0)
                DoubaoModelList.SelectedIndex = index;
            else
                DoubaoModelList.Text = string.IsNullOrWhiteSpace(model) ? "doubao-seed-2.0-lite" : model;
        }

        /// <summary>
        /// Selects the send translation Doubao model matching the given name
        /// or endpoint ID (the box is editable).
        /// </summary>
        private void SelectSendDoubaoModel(string model)
        {
            int index = -1;
            for (int i = 0; i < TranslationController.DoubaoModels.Length; ++i)
            {
                if (string.Equals(TranslationController.DoubaoModels[i], model, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (index >= 0)
                SendDoubaoModelList.SelectedIndex = index;
            else
                SendDoubaoModelList.Text = string.IsNullOrWhiteSpace(model) ? "doubao-seed-2.0-lite" : model;
        }

        /// <summary>
        /// Selects the DeepSeek model matching the given name
        /// </summary>
        private void SelectDeepSeekModel(string model)
        {
            int index = -1;
            for (int i = 0; i < TranslationController.DeepSeekModels.Length; ++i)
            {
                if (TranslationController.DeepSeekModels[i] == model)
                {
                    index = i;
                    break;
                }
            }
            // Fall back to the first model if the saved one is no longer available
            DeepSeekModelList.SelectedIndex = index >= 0 ? index : 0;
        }

        /// <summary>
        /// Selects the send translation DeepSeek model matching the given name
        /// </summary>
        private void SelectSendModel(string model)
        {
            int index = -1;
            for (int i = 0; i < SendModelList.Items.Count; ++i)
            {
                if (SendModelList.Items[i].ToString() == model)
                {
                    index = i;
                    break;
                }
            }
            SendModelList.SelectedIndex = index >= 0 ? index : 0;
        }

        /// <summary>
        /// Maps a translation provider name to its combo box index
        /// (0 = Google, 1 = DeepSeek, 2 = DeepL, 3 = Doubao, 4 = Zoom)
        /// </summary>
        private static int ProviderIndex(string provider)
        {
            if (string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(provider, "DeepL", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(provider, "Doubao", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (string.Equals(provider, "Zoom", StringComparison.OrdinalIgnoreCase))
                return 4;
            return 0;
        }

        /// <summary>
        /// Maps a translation provider combo box index to its setting value
        /// </summary>
        private static string ProviderName(int index)
        {
            if (index == 1)
                return "DeepSeek";
            if (index == 2)
                return "DeepL";
            if (index == 3)
                return "Doubao";
            if (index == 4)
                return "Zoom";
            return "Google";
        }

        /// <summary>
        /// Returns Visible for true and Collapsed for false.
        /// </summary>
        private static Visibility ToVisibility(bool visible)
        {
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Enables or disables the provider controls depending
        /// on the selected translation provider
        /// </summary>
        private void UpdateTranslationProviderState()
        {
            int index = TranslationProviderList.SelectedIndex;
            bool deepSeek = index == 1;
            bool deepL = index == 2;
            bool doubao = index == 3;
            bool zoom = index == 4;

            DeepSeekApiKeyRow.Visibility = ToVisibility(deepSeek);
            DeepSeekModelRow.Visibility = ToVisibility(deepSeek);
            TranslationPromptRow.Visibility = ToVisibility(deepSeek || doubao);
            DeepLApiKeyRow.Visibility = ToVisibility(deepL);
            DoubaoApiKeyRow.Visibility = ToVisibility(doubao);
            DoubaoModelRow.Visibility = ToVisibility(doubao);
            ZoomApiKeyRow.Visibility = ToVisibility(zoom);
            TranslationStyleRow.Visibility = ToVisibility(deepSeek || doubao);
        }

        /// <summary>
        /// Toggles the provider controls when the
        /// translation provider changes
        /// </summary>
        private void TranslationProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTranslationProviderState();
            if (!_loadingTranslationSettings)
                SaveTranslationSettings();
        }

        /// <summary>
        /// Enables or disables the send translation provider controls
        /// depending on the selected provider
        /// </summary>
        private void UpdateSendProviderState()
        {
            int index = SendProviderList.SelectedIndex;
            bool deepSeek = index == 1;
            bool deepL = index == 2;
            bool doubao = index == 3;
            bool zoom = index == 4;

            SendApiKeyRow.Visibility = ToVisibility(deepSeek);
            SendModelRow.Visibility = ToVisibility(deepSeek);
            SendPromptRow.Visibility = ToVisibility(deepSeek || doubao);
            SendDeepLApiKeyRow.Visibility = ToVisibility(deepL);
            SendDoubaoApiKeyRow.Visibility = ToVisibility(doubao);
            SendDoubaoModelRow.Visibility = ToVisibility(doubao);
            SendZoomApiKeyRow.Visibility = ToVisibility(zoom);
        }

        /// <summary>
        /// Toggles the send provider controls when
        /// the send provider changes
        /// </summary>
        private void SendProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSendProviderState();
            if (!_loadingTranslationSettings)
                SaveTranslationSettings();
        }

        /// <summary>
        /// Selects the target language matching the given code
        /// </summary>
        private void SelectTargetLanguage(string code)
        {
            for (int i = 0; i < TranslationController.TargetLanguages.Length; ++i)
            {
                if (TranslationController.TargetLanguages[i].Key == code)
                {
                    TargetLanguageList.SelectedIndex = i;
                    return;
                }
            }
            TargetLanguageList.SelectedIndex = 0;
        }

        /// <summary>
        /// Selects a language in one of the send translation ComboBoxes.
        /// When withAuto is true the first entry is "auto-detect".
        /// </summary>
        private static void SelectSendLanguage(ComboBox list, string code, bool withAuto)
        {
            if (withAuto && code == "auto")
            {
                list.SelectedIndex = 0;
                return;
            }

            for (int i = 0; i < TranslationController.TargetLanguages.Length; ++i)
            {
                if (TranslationController.TargetLanguages[i].Key == code)
                {
                    list.SelectedIndex = withAuto ? i + 1 : i;
                    return;
                }
            }
            list.SelectedIndex = 0;
        }

        /// <summary>
        /// Enables or disables the send translation controls depending on
        /// whether the feature is enabled
        /// </summary>
        private void UpdateSendTranslationState()
        {
            bool enabled = SendTranslationEnabled.IsChecked == true;
            SendSourceLanguageLabel.IsEnabled = enabled;
            SendSourceLanguageList.IsEnabled = enabled;
            SendTargetLanguageLabel.IsEnabled = enabled;
            SendTargetLanguageList.IsEnabled = enabled;
            SendTranslationKeyLabel.IsEnabled = enabled;
            SendTranslationKeyBox.IsEnabled = enabled;
            SendProviderLabel.IsEnabled = enabled;
            SendProviderList.IsEnabled = enabled;

            if (enabled)
            {
                UpdateSendProviderState();
            }
            else
            {
                SendApiKeyRow.Visibility = Visibility.Collapsed;
                SendModelRow.Visibility = Visibility.Collapsed;
                SendPromptRow.Visibility = Visibility.Collapsed;
                SendDeepLApiKeyRow.Visibility = Visibility.Collapsed;
                SendDoubaoApiKeyRow.Visibility = Visibility.Collapsed;
                SendDoubaoModelRow.Visibility = Visibility.Collapsed;
                SendZoomApiKeyRow.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Toggles the send translation controls when the feature changes
        /// </summary>
        private void SendTranslationEnabled_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSendTranslationState();
            if (!_loadingTranslationSettings)
                SaveTranslationSettings();
        }

        // ================================================================
        //  Backup / filter / settings / about
        // ================================================================

        /// <summary>
        /// Opens the chat log filter window
        /// </summary>
        private static ChatLogFilterWindow chatLogFilter;
        private void OpenChatLogFilter()
        {
            SaveSettings();

            if (chatLogFilter == null)
            {
                chatLogFilter = new ChatLogFilterWindow(this);
                chatLogFilter.Closed += (s, args) =>
                {
                    chatLogFilter = null;
                };
            }

            chatLogFilter.ShowDialog();
        }

        /// <summary>
        /// Opens the program settings window
        /// </summary>
        private static ProgramSettingsWindow programSettings;
        private void OpenProgramSettings()
        {
            if (Properties.Settings.Default.BackupChatLogAutomatically)
            {
                if (!Properties.Settings.Default.DisableWarningPopups && MessageBox.Show(Strings.BackupWillBeOff, Strings.Warning, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                    return;

                StatusLabel.Text = string.Format(Strings.BackupStatus, Strings.Disabled);
            }

            BackupController.AbortAll();
            SaveSettings();

            if (programSettings == null)
            {
                programSettings = new ProgramSettingsWindow(this);
                programSettings.IsVisibleChanged += (s, args) =>
                {
                    if ((bool)args.NewValue) return;
                    BackupController.Initialize();
                    StatusLabel.Text = string.Format(Strings.BackupStatus,
                        Properties.Settings.Default.BackupChatLogAutomatically ? Strings.Enabled : Strings.Disabled);
                };
                programSettings.Closed += (s, args) =>
                {
                    programSettings = null;
                };
            }

            programSettings.ShowDialog();
        }

        /// <summary>
        /// Opens the project homepage in the default browser.
        /// </summary>
        private void ProjectLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(e.Uri.ToString());
            }
            catch
            {
                // Browser could not be started; ignore.
            }
        }

        // ================================================================
        //  Tray icon
        // ================================================================

        /// <summary>
        /// Initializes the tray icon
        /// </summary>
        private void InitializeTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Visible = false,
                Icon = Properties.Resources.AppIcon,
                Text = Strings.TrayText
            };

            _trayIcon.MouseDoubleClick += TrayIcon_MouseDoubleClick;

            _trayIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
            _trayIcon.ContextMenuStrip.Items.Add(Strings.Open, null, ResumeTrayStripMenuItem_Click);
            _trayIcon.ContextMenuStrip.Items.Add(Strings.Exit, null, ExitTrayToolStripMenuItem_Click);
        }

        /// <summary>
        /// Shows a Windows toast notification (balloon tip) for hotkey events.
        /// Safe to call from any thread; delegates to the UI thread when needed.
        /// </summary>
        public void ShowWindowsToast(string text)
        {
            if (string.IsNullOrEmpty(text) || _trayIcon == null)
                return;

            Action show = () =>
            {
                try
                {
                    if (!_trayIcon.Visible)
                        _trayIcon.Visible = true;
                    _trayIcon.ShowBalloonTip(3000, Strings.MainTitle, text, System.Windows.Forms.ToolTipIcon.Info);
                }
                catch
                {
                    // Balloon tips can fail silently on some systems
                }
            };

            if (Dispatcher.CheckAccess())
                show();
            else
                Dispatcher.BeginInvoke(show);
        }

        /// <summary>
        /// Resumes and shows the main window by double clicking the tray icon
        /// </summary>
        private void TrayIcon_MouseDoubleClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            ResumeTrayStripMenuItem_Click(sender, EventArgs.Empty);
        }

        /// <summary>
        /// Resumes and shows the main window from the tray menu
        /// </summary>
        private void ResumeTrayStripMenuItem_Click(object sender, EventArgs e)
        {
            if (isRestarting)
                return;

            Show();
            _trayIcon.Visible = false;
        }

        /// <summary>
        /// Quits the application from the tray
        /// </summary>
        private void ExitTrayToolStripMenuItem_Click(object sender, EventArgs e)
        {
            BackupController.Quitting = true;

            _trayIcon.Visible = false;
            isRestarting = true;
            System.Windows.Application.Current.Shutdown();
        }

        // ================================================================
        //  Closing
        // ================================================================

        /// <summary>
        /// Asks the user if they are sure they want to exit
        /// if automatic backup is enabled.
        /// Saves the settings before the main window closes
        /// </summary>
        private void Main_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!isRestarting)
            {
                if (Properties.Settings.Default.BackupChatLogAutomatically && _trayIcon.Visible == false)
                {
                    MessageBoxResult result = MessageBoxResult.Yes;
                    if (!Properties.Settings.Default.AlwaysCloseToTray)
                        result = MessageBox.Show(Strings.MinimizeInsteadOfClose, Strings.Warning, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (result == MessageBoxResult.Yes)
                    {
                        e.Cancel = true;

                        Hide();
                        _trayIcon.Visible = true;

                        return;
                    }

                    if (result == MessageBoxResult.Cancel)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }

            BackupController.Quitting = true;
            SaveTranslationSettings();
            SaveSettings();

            System.Windows.Application.Current.Shutdown();
        }

        /// <summary>
        /// Display item for the overview "most translated words" list.
        /// </summary>
        private class WordRank
        {
            public int Rank { get; set; }
            public string Word { get; set; }
            public long Count { get; set; }
        }
    }
}
