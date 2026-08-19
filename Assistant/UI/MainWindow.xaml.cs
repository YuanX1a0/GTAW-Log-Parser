using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.Windows.Input;
using System.Globalization;
using Assistant.Controllers;
using Assistant.Localization;
using Assistant.UI.Controls;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.Win32;
using System.Linq;
using System.Threading.Tasks;

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

        // Holds the full parsed log so the on-screen text box can be truncated
        // for display while "Save" still writes the complete contents.
        private string _lastParsedFull = string.Empty;
        // Maximum lines shown in the parsed text box; beyond this the newest
        // lines are shown and the rest are kept only in _lastParsedFull.
        private const int MaxDisplayLines = 15000;
        private bool _autoParseBusy;

        // Debounced model-list refresh: when an API key is typed, the model
        // combo boxes are repopulated from the actual account (DeepSeek /
        // Doubao / Custom) instead of the built-in static list. Every
        // provider gets its own request + timer so scheduled refreshes for
        // different providers never overwrite each other.
        private class ModelRefreshRequest
        {
            public string Key;
            public string Endpoint;
            public ComboBox Combo;
        }
        private readonly Dictionary<string, ModelRefreshRequest> _modelRefreshRequests = new Dictionary<string, ModelRefreshRequest>();
        private readonly Dictionary<string, System.Threading.Timer> _modelRefreshTimers = new Dictionary<string, System.Threading.Timer>();
        private readonly Dictionary<string, string> _loadedModelKeys = new Dictionary<string, string>();
        // User-added custom model names for the Custom provider model boxes.
        private readonly HashSet<string> _customChatModels = new HashSet<string>();
        private readonly HashSet<string> _customSendModels = new HashSet<string>();

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
            bool apiusage = page == "apiusage";

            OverviewPage.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            ChatLogPage.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            TranslationPage.Visibility = (overview || chat || realtime || about || apiusage) ? Visibility.Collapsed : Visibility.Visible;
            RealtimeLogPage.Visibility = realtime ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
            ApiUsagePage.Visibility = apiusage ? Visibility.Visible : Visibility.Collapsed;

            SetNavButton(NavOverview, NavOverviewText, overview);
            SetNavButton(NavChatLog, NavChatLogText, chat);
            SetNavButton(NavTranslation, NavTranslationText, !overview && !chat && !realtime && !about && !apiusage);
            SetNavButton(NavRealtimeLog, NavRealtimeLogText, realtime);
            SetNavButton(NavAbout, NavAboutText, about);
            SetNavButton(NavApiUsage, NavApiUsageText, apiusage);

            if (overview)
                RefreshOverviewStats();
            else if (realtime)
                RefreshRealtimeLog();
            else if (apiusage)
                RefreshApiUsage();
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
            CacheInfo.Text = string.Format(Strings.OverviewCacheInfo, count.ToString("N0"), sizeText)
                + "  ·  " + string.Format(Strings.OverviewCacheHits, TranslationController.CacheHits.ToString("N0"))
                + "  ·  " + string.Format(Strings.OverviewCacheFuzzyHits, TranslationController.FuzzyCacheHits.ToString("N0"));

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

        private void NavApiUsage_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("apiusage");
        }

        // ================================================================
        //  API usage page
        // ================================================================

        private int apiUsageDays = 7;

        private void ApiUsageRangeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            apiUsageDays = ApiUsageRangeList.SelectedIndex == 1 ? 30 : 7;
            if (ApiUsagePage.Visibility == Visibility.Visible)
                RefreshApiUsage();
        }

        /// <summary>
        /// Rebuilds the per-model API usage cards on the API usage page.
        /// </summary>
        private void RefreshApiUsage()
        {
            if (ApiUsageCardsPanel == null)
                return;
            ApiUsageCardsPanel.Children.Clear();

            List<ApiUsageTracker.ModelSeries> series = ApiUsageTracker.GetSeries(apiUsageDays);
            if (series.Count == 0)
            {
                ApiUsageCardsPanel.Children.Add(new TextBlock
                {
                    Text = Strings.ApiUsageNoData,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 60, 0, 0)
                });
                return;
            }

            foreach (ApiUsageTracker.ModelSeries model in series)
                ApiUsageCardsPanel.Children.Add(BuildApiUsageCard(model));
        }

        /// <summary>
        /// Builds the white card for one model: header (name + cumulative
        /// totals) followed by the request area chart and the token bar chart.
        /// </summary>
        private Border BuildApiUsageCard(ApiUsageTracker.ModelSeries model)
        {
            Border card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 14),
                Margin = new Thickness(0, 0, 0, 14)
            };

            StackPanel stack = new StackPanel();

            // ---- Header: requests (left), model name (center), tokens (right)
            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock requestsText = new TextBlock
            {
                Text = string.Format(Strings.ApiUsageTotalRequests, model.TotalRequests.ToString("N0")),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock nameText = new TextBlock
            {
                Text = model.Model,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 320
            };

            TextBlock tokensText = new TextBlock
            {
                Text = string.Format(Strings.ApiUsageTotalTokens, FormatCompact(model.TotalTokens)),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Grid.SetColumn(requestsText, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(tokensText, 2);
            header.Children.Add(requestsText);
            header.Children.Add(nameText);
            header.Children.Add(tokensText);
            stack.Children.Add(header);

            // ---- Charts side by side
            Grid charts = new Grid();
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UsageChart lineChart = new UsageChart
            {
                Mode = UsageChartMode.LineArea,
                Data = model.Days,
                Height = 210,
                Margin = new Thickness(0, 0, 8, 0)
            };

            UsageChart barChart = new UsageChart
            {
                Mode = UsageChartMode.StackedBar,
                Data = model.Days,
                Height = 210,
                Margin = new Thickness(8, 0, 0, 0)
            };

            Grid.SetColumn(lineChart, 0);
            Grid.SetColumn(barChart, 1);
            charts.Children.Add(lineChart);
            charts.Children.Add(barChart);
            stack.Children.Add(charts);

            // ---- Legend for the stacked bar chart (input / output colors)
            StackPanel legend = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };
            legend.Children.Add(MakeApiUsageLegendItem(Color.FromRgb(0x5B, 0x9B, 0xD5), Strings.ApiUsageLegendInput));
            legend.Children.Add(MakeApiUsageLegendItem(Color.FromRgb(0xFF, 0xC0, 0x00), Strings.ApiUsageLegendOutput));
            stack.Children.Add(legend);

            card.Child = stack;
            return card;
        }

        private static StackPanel MakeApiUsageLegendItem(Color color, string text)
        {
            StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            panel.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            panel.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        /// <summary>
        /// Formats a token count compactly: 1.2M, 850K, 1,234.
        /// </summary>
        private static string FormatCompact(long value)
        {
            if (value >= 1000000)
                return (value / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000)
                return (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return value.ToString("N0");
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
            NavApiUsageText.Text = Strings.NavApiUsage;
            ApiUsageTitle.Text = Strings.ApiUsageTitle;
            ApiUsageRangeLabel.Text = Strings.ApiUsageRangeLabel;
            int apiSel = ApiUsageRangeList.SelectedIndex >= 0 ? ApiUsageRangeList.SelectedIndex : 0;
            ApiUsageRangeList.Items.Clear();
            ApiUsageRangeList.Items.Add(Strings.ApiUsageRange7);
            ApiUsageRangeList.Items.Add(Strings.ApiUsageRange30);
            ApiUsageRangeList.SelectedIndex = apiSel;
            NavRealtimeLogText.Text = Strings.NavRealtimeLog;
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
            DeepSeekSpeedLabel.Content = Strings.DeepSeekSpeed;
            DeepLApiKeyLabel.Content = Strings.DeepLApiKey;
            DoubaoApiKeyLabel.Content = Strings.DoubaoApiKey;
            DoubaoModelLabel.Content = Strings.DoubaoModel;
            CustomEndpointLabel.Content = Strings.CustomEndpoint;
            CustomProviderLabel.Content = Strings.CustomProvider;
            CustomApiKeyLabel.Content = Strings.CustomApiKey;
            CustomModelLabel.Content = Strings.CustomModel;
            ZoomApiKeyLabel.Content = Strings.ZoomApiKey;
            TranslationDisplayModeLabel.Content = Strings.TranslationDisplayMode;
            EnableCacheLabel.Content = Strings.EnableTranslationCache;
            FuzzyCacheLabel.Content = Strings.EnableFuzzyCacheMatch;
            TranslationPromptLabel.Content = Strings.TranslationPrompt;
            TranslationStyleLabel.Content = Strings.TranslationStyle;
            SendTranslationEnabled.Content = Strings.SendTranslationEnabled;
            EnableManualTranslateCheckBox.Content = Strings.EnableManualTranslate;
            SendSourceLanguageLabel.Content = Strings.SourceLanguage;
            SendTargetLanguageLabel.Content = Strings.TargetLanguage;
            SendTranslationKeyLabel.Content = Strings.SendTranslationHotkey;
            SendProviderLabel.Content = Strings.TranslationProvider;
            SendApiKeyLabel.Content = Strings.DeepSeekApiKey;
            SendModelLabel.Content = Strings.DeepSeekModel;
            SendSpeedLabel.Content = Strings.DeepSeekSpeed;
            SendDeepLApiKeyLabel.Content = Strings.SendDeepLApiKey;
            SendDoubaoApiKeyLabel.Content = Strings.SendDoubaoApiKey;
            SendDoubaoModelLabel.Content = Strings.SendDoubaoModel;
            SendCustomEndpointLabel.Content = Strings.SendCustomEndpoint;
            SendCustomProviderLabel.Content = Strings.SendCustomProvider;
            SendCustomApiKeyLabel.Content = Strings.SendCustomApiKey;
            SendCustomModelLabel.Content = Strings.SendCustomModel;
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
        /// Parses the current chat log on a background thread so a very large
        /// log never freezes the UI, then shows the text (truncated for
        /// display; the full contents stay available for saving).
        /// </summary>
        private async void Parse_Click(object sender, RoutedEventArgs e)
        {
            Parse.IsEnabled = false;
            try
            {
                AppController.InitializeServerIp();
                string full = await Task.Run(() => AppController.ParseChatLog(false, true));
                _lastParsedFull = full;
                _lastAutoParsedLog = full;
                Parsed.Text = TruncateForDisplay(full);
            }
            finally
            {
                Parse.IsEnabled = true;
            }
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
        /// Periodically reads the captured chat log on a background thread and
        /// updates the main text box only when the content has changed.
        /// Follows new messages (scrolls to bottom) only while the
        /// user is at the bottom; keeps the position when scrolling up.
        /// </summary>
        private async void AutoParseTimer_Tick(object sender, EventArgs e)
        {
            if (_autoParseBusy)
                return;
            _autoParseBusy = true;
            try
            {
                string parsed = await Task.Run(() => AppController.ParseChatLog(false));
                if (parsed == _lastAutoParsedLog)
                    return;

                ScrollViewer viewer = FindVisualChild<ScrollViewer>(Parsed);
                bool followBottom = viewer == null
                    || viewer.ScrollableHeight <= 0
                    || viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ScrollableHeight - 2;

                _lastAutoParsedLog = parsed;
                Parsed.Text = TruncateForDisplay(parsed);

                if (followBottom)
                {
                    if (viewer != null)
                        viewer.ScrollToEnd();
                    else
                        Parsed.ScrollToEnd();
                }
            }
            finally
            {
                _autoParseBusy = false;
            }
        }

        /// <summary>
        /// Returns the text for on-screen display: if it is larger than
        /// MaxDisplayLines lines, only the newest lines are returned with a
        /// note, so the text box never freezes on huge logs. The full text is
        /// kept separately (see _lastParsedFull) for saving.
        /// </summary>
        private static string TruncateForDisplay(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            int newlines = 0;
            int cutIndex = text.Length;
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (text[i] == '\n')
                {
                    newlines++;
                    if (newlines >= MaxDisplayLines)
                    {
                        cutIndex = i + 1;
                        break;
                    }
                }
            }
            if (cutIndex >= text.Length)
                return text;

            return string.Format(Strings.LogTooLarge, MaxDisplayLines) + "\n" + text.Substring(cutIndex);
        }

        /// <summary>
        /// Finds the first child of the given type in the visual tree
        /// </summary>
        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                T match = child as T;
                if (match != null)
                    return match;
                match = FindVisualChild<T>(child);
                if (match != null)
                    return match;
            }
            return null;
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
                // Prefer the full log when it was truncated for display only.
                string content = !string.IsNullOrEmpty(_lastParsedFull) ? _lastParsedFull : Parsed.Text;
                if (string.IsNullOrWhiteSpace(content))
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
                    sw.Write(content);
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
            TranslationProviderList.Items.Add(Strings.TranslationProviderCustom);
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
            SendProviderList.Items.Add(Strings.TranslationProviderCustom);
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

            PopulateSpeedList(DeepSeekSpeedList);
            PopulateSpeedList(SendSpeedList);

            DoubaoModelList.Items.Clear();
            foreach (string model in TranslationController.DoubaoModels)
                DoubaoModelList.Items.Add(model);

            SendDoubaoModelList.Items.Clear();
            foreach (string model in TranslationController.DoubaoModels)
                SendDoubaoModelList.Items.Add(model);

            CustomProviderList.Items.Clear();
            foreach (TranslationController.AiProviderPreset preset in TranslationController.AiProviderPresets)
                CustomProviderList.Items.Add(preset.Name);
            CustomModelList.Items.Clear();
            foreach (string model in TranslationController.CustomDefaultModels)
                CustomModelList.Items.Add(model);
            _customChatModels.Clear();
            foreach (string name in SplitModelList(Properties.Settings.Default.CustomModels))
            {
                _customChatModels.Add(name);
                CustomModelList.Items.Add(name);
            }

            SendCustomProviderList.Items.Clear();
            foreach (TranslationController.AiProviderPreset preset in TranslationController.AiProviderPresets)
                SendCustomProviderList.Items.Add(preset.Name);
            SendCustomModelList.Items.Clear();
            foreach (string model in TranslationController.CustomDefaultModels)
                SendCustomModelList.Items.Add(model);
            _customSendModels.Clear();
            foreach (string name in SplitModelList(Properties.Settings.Default.SendCustomModels))
            {
                _customSendModels.Add(name);
                SendCustomModelList.Items.Add(name);
            }

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
            DeepSeekApiKeyBox.PasswordChanged += ProviderKey_PasswordChanged;
            DeepSeekModelList.SelectionChanged += saveSelection;
            DeepSeekModelList.SelectionChanged += DeepSeekModelList_SyncedSpeed;
            DeepSeekModelList.SelectionChanged += ModelSelectionChanged;
            DeepSeekSpeedList.SelectionChanged += DeepSeekSpeedList_SelectionChanged;
            DeepLApiKeyBox.PasswordChanged += saveRouted;
            DoubaoApiKeyBox.PasswordChanged += ProviderKey_PasswordChanged;
            DoubaoModelList.SelectionChanged += saveSelection;
            DoubaoModelList.SelectionChanged += ModelSelectionChanged;
            HookEditableModelCombo(DoubaoModelList);
            DoubaoModelList.LostKeyboardFocus += EditableModelList_LostFocus;
            CustomEndpointBox.TextChanged += CustomEndpoint_TextChanged;
            CustomProviderList.SelectionChanged += CustomProviderList_SelectionChanged;
            CustomApiKeyBox.PasswordChanged += ProviderKey_PasswordChanged;
            CustomModelList.SelectionChanged += saveSelection;
            CustomModelList.SelectionChanged += ModelSelectionChanged;
            HookEditableModelCombo(CustomModelList);
            CustomModelList.LostKeyboardFocus += EditableModelList_LostFocus;
            ZoomApiKeyBox.PasswordChanged += saveRouted;
            TranslationDisplayModeList.SelectionChanged += saveSelection;
            EnableCacheCheckBox.Checked += saveRouted;
            EnableCacheCheckBox.Unchecked += saveRouted;
            FuzzyCacheCheckBox.Checked += saveRouted;
            FuzzyCacheCheckBox.Unchecked += saveRouted;
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
            EnableManualTranslateCheckBox.Checked += saveRouted;
            EnableManualTranslateCheckBox.Unchecked += saveRouted;
            SendSourceLanguageList.SelectionChanged += saveSelection;
            SendTargetLanguageList.SelectionChanged += saveSelection;
            SendTranslationKeyBox.TextChanged += saveText;
            SendApiKeyBox.PasswordChanged += ProviderKey_PasswordChanged;
            SendModelList.SelectionChanged += saveSelection;
            SendModelList.SelectionChanged += SendModelList_SyncedSpeed;
            SendModelList.SelectionChanged += ModelSelectionChanged;
            SendSpeedList.SelectionChanged += SendSpeedList_SelectionChanged;
            SendDeepLApiKeyBox.PasswordChanged += saveRouted;
            SendDoubaoApiKeyBox.PasswordChanged += ProviderKey_PasswordChanged;
            SendDoubaoModelList.SelectionChanged += saveSelection;
            SendDoubaoModelList.SelectionChanged += ModelSelectionChanged;
            HookEditableModelCombo(SendDoubaoModelList);
            SendDoubaoModelList.LostKeyboardFocus += EditableModelList_LostFocus;
            SendCustomEndpointBox.TextChanged += CustomEndpoint_TextChanged;
            SendCustomProviderList.SelectionChanged += SendCustomProviderList_SelectionChanged;
            SendCustomApiKeyBox.PasswordChanged += ProviderKey_PasswordChanged;
            SendCustomModelList.SelectionChanged += saveSelection;
            SendCustomModelList.SelectionChanged += ModelSelectionChanged;
            HookEditableModelCombo(SendCustomModelList);
            SendCustomModelList.LostKeyboardFocus += EditableModelList_LostFocus;
            SendZoomApiKeyBox.PasswordChanged += saveRouted;
            SendPromptBox.TextChanged += saveText;
        }

        /// <summary>
        /// Saves the settings when the Custom provider endpoint URL changes
        /// and schedules a model-list refresh for the new endpoint.
        /// </summary>
        private void CustomEndpoint_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingTranslationSettings)
                return;
            SaveTranslationSettings();
            string provider = ReferenceEquals(sender, SendCustomEndpointBox) ? "SendCustom" : "Custom";
            InvalidateModelCache(provider);
            ScheduleModelRefresh(provider);
        }

        /// <summary>
        /// Saves the settings when a provider API key changes and schedules a
        /// debounced model-list refresh, so the model combo boxes are populated
        /// from the actual account instead of the built-in static list.
        /// </summary>
        private void ProviderKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loadingTranslationSettings)
                return;
            SaveTranslationSettings();
            string provider = null;
            if (ReferenceEquals(sender, DeepSeekApiKeyBox)) provider = "DeepSeek";
            else if (ReferenceEquals(sender, DoubaoApiKeyBox)) provider = "Doubao";
            else if (ReferenceEquals(sender, CustomApiKeyBox)) provider = "Custom";
            else if (ReferenceEquals(sender, SendApiKeyBox)) provider = "SendDeepSeek";
            else if (ReferenceEquals(sender, SendDoubaoApiKeyBox)) provider = "SendDoubao";
            else if (ReferenceEquals(sender, SendCustomApiKeyBox)) provider = "SendCustom";
            if (provider != null)
            {
                InvalidateModelCache(provider);
                ScheduleModelRefresh(provider);
            }
        }

        /// <summary>
        /// (Re)schedules a single-shot model-list refresh for one provider
        /// after the user stops typing, so each API key is queried at most
        /// once per 1.2 s pause. Each provider has its own timer.
        /// </summary>
        private void ScheduleModelRefresh(string provider)
        {
            string key;
            string endpoint;
            ComboBox combo;
            switch (provider)
            {
                case "DeepSeek": combo = DeepSeekModelList; key = DeepSeekApiKeyBox.Password; endpoint = null; break;
                case "Doubao": combo = DoubaoModelList; key = DoubaoApiKeyBox.Password; endpoint = null; break;
                case "Custom": combo = CustomModelList; key = CustomApiKeyBox.Password; endpoint = CustomEndpointBox.Text; break;
                case "SendDeepSeek": combo = SendModelList; key = SendApiKeyBox.Password; endpoint = null; break;
                case "SendDoubao": combo = SendDoubaoModelList; key = SendDoubaoApiKeyBox.Password; endpoint = null; break;
                case "SendCustom": combo = SendCustomModelList; key = SendCustomApiKeyBox.Password; endpoint = SendCustomEndpointBox.Text; break;
                default: return;
            }
            _modelRefreshRequests[provider] = new ModelRefreshRequest
            {
                Key = SanitizeKey(key),
                Endpoint = (endpoint ?? string.Empty).Trim(),
                Combo = combo
            };

            System.Threading.Timer timer;
            if (_modelRefreshTimers.TryGetValue(provider, out timer) && timer != null)
                timer.Dispose();
            _modelRefreshTimers[provider] = new System.Threading.Timer(RefreshModelsTimerCallback, provider, 1200, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// Drops the loaded-model cache entries for a provider so a user
        /// change to the API key or endpoint always triggers a fresh model
        /// list fetch instead of being skipped as "already loaded".
        /// </summary>
        private void InvalidateModelCache(string provider)
        {
            string prefix = provider + "|";
            List<string> matches = null;
            foreach (string key in _loadedModelKeys.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (matches == null)
                        matches = new List<string>();
                    matches.Add(key);
                }
            }
            if (matches != null)
            {
                foreach (string key in matches)
                    _loadedModelKeys.Remove(key);
            }
        }

        /// <summary>
        /// Fetches the model list for the scheduled provider/key/endpoint and
        /// replaces the static items when the account exposes its own model
        /// list. Runs on a background thread; the UI is updated through the
        /// dispatcher.
        /// </summary>
        private void RefreshModelsTimerCallback(object state)
        {
            string provider = state as string;
            ModelRefreshRequest request;
            if (string.IsNullOrWhiteSpace(provider) || !_modelRefreshRequests.TryGetValue(provider, out request) || request.Combo == null)
                return;
            // Local servers (Ollama / LM Studio) work without an API key.
            bool localProvider = (provider == "Custom" || provider == "SendCustom")
                && !string.IsNullOrWhiteSpace(request.Endpoint);
            if (string.IsNullOrWhiteSpace(request.Key) && !localProvider)
                return;
            string dedupKey = provider + "|" + request.Endpoint;
            string loadedKey;
            if (_loadedModelKeys.TryGetValue(dedupKey, out loadedKey) && string.Equals(loadedKey, request.Key, StringComparison.Ordinal))
                return; // already refreshed for this key/endpoint

            try
            {
                List<string> models = TranslationController.FetchModelList(provider, request.Key,
                    string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint);
                if (models == null || models.Count == 0)
                {
                    TranslationController.LogEvent("翻译", "模型列表获取失败（" + provider + "，" + request.Endpoint + "，" + MaskKey(request.Key) + "）：接口未返回可用模型");
                    return; // keep the built-in static list
                }
                _loadedModelKeys[dedupKey] = request.Key;
                ComboBox combo = request.Combo;
                List<string> snapshot = new List<string>(models);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        // Merge the player's manually-added models into the
                        // fetched account list so they are not lost.
                        HashSet<string> customStore = ReferenceEquals(combo, CustomModelList) ? _customChatModels : _customSendModels;
                        List<string> merged = new List<string>(snapshot);
                        foreach (string name in customStore)
                        {
                            bool exists = false;
                            foreach (string item in merged)
                            {
                                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                                {
                                    exists = true;
                                    break;
                                }
                            }
                            if (!exists)
                                merged.Add(name);
                        }
                        string current = combo.Text;
                        combo.Items.Clear();
                        foreach (string model in merged)
                            combo.Items.Add(model);
                        if (!string.IsNullOrEmpty(current) && merged.Contains(current))
                            combo.Text = current;
                        else
                            combo.SelectedIndex = 0;
                        TranslationController.LogEvent("翻译", "模型列表已更新（" + provider + "，" + request.Endpoint + "）：" + merged.Count + " 个模型");
                    }
                    catch (Exception ex)
                    {
                        TranslationController.LogEvent("翻译", "模型列表 UI 更新失败（" + provider + "）：" + ex.Message);
                    }
                }));
            }
            catch (Exception ex)
            {
                string hint = string.Empty;
                if (provider == "SendCustom"
                    && !string.IsNullOrWhiteSpace(Properties.Settings.Default.CustomApiKey)
                    && !string.Equals(request.Key, Properties.Settings.Default.CustomApiKey, StringComparison.Ordinal))
                {
                    hint = "（发送区 Key 与聊天区 Custom Key 不一致，401 多半是发送区 Key 填错或已失效）";
                }
                TranslationController.LogEvent("翻译", "模型列表获取失败（" + provider + "，" + request.Endpoint + "，" + MaskKey(request.Key) + "）：" + ex.Message + hint);
            }
        }

        /// <summary>
        /// Masks an API key for the log so it can be diagnosed without
        /// exposing the secret itself.
        /// </summary>
        private static string MaskKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "Key(空)";
            bool hasWhite = false;
            foreach (char c in key)
            {
                if (char.IsWhiteSpace(c))
                {
                    hasWhite = true;
                    break;
                }
            }
            string suffix = hasWhite ? "，含空白字符" : string.Empty;
            return key.Length <= 6
                ? "Key(" + key.Length + "位" + suffix + ")"
                : "Key(" + key.Substring(0, 4) + "…" + key.Length + "位" + suffix + ")";
        }

        /// <summary>
        /// Removes every whitespace character from an API key. Pasting often
        /// drags in newlines/tabs/spaces which would make the key invalid.
        /// </summary>
        private static string SanitizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }
            return sb.ToString();
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
            Properties.Settings.Default.DeepSeekApiKey = SanitizeKey(DeepSeekApiKeyBox.Password);
            Properties.Settings.Default.DeepLApiKey = SanitizeKey(DeepLApiKeyBox.Password);
            Properties.Settings.Default.DoubaoApiKey = SanitizeKey(DoubaoApiKeyBox.Password);
            Properties.Settings.Default.ZoomApiKey = SanitizeKey(ZoomApiKeyBox.Password);
            if (DeepSeekModelList.SelectedIndex >= 0)
                Properties.Settings.Default.DeepSeekModel = DeepSeekModelList.SelectedItem.ToString();
            if (DeepSeekSpeedList.SelectedItem is ComboBoxItem deepSeekSpeedItem)
                Properties.Settings.Default.DeepSeekReasoningSpeed = (string)deepSeekSpeedItem.Tag;
            Properties.Settings.Default.DoubaoModel = string.IsNullOrWhiteSpace(DoubaoModelList.Text)
                ? "doubao-seed-2.0-lite"
                : DoubaoModelList.Text.Trim();
            Properties.Settings.Default.CustomEndpoint = (CustomEndpointBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.CustomProviderName = CustomProviderList.SelectedItem as string ?? string.Empty;
            Properties.Settings.Default.CustomApiKey = SanitizeKey(CustomApiKeyBox.Password);
            Properties.Settings.Default.CustomModel = (CustomModelList.Text ?? string.Empty).Trim();
            Properties.Settings.Default.CustomModels = string.Join(",", _customChatModels);
            Properties.Settings.Default.TranslationDisplayMode = TranslationDisplayModeList.SelectedIndex == 1 ? "replace" : "append";
            Properties.Settings.Default.EnableTranslationCache = EnableCacheCheckBox.IsChecked == true;
            Properties.Settings.Default.EnableFuzzyCacheMatch = FuzzyCacheCheckBox.IsChecked == true;
            Properties.Settings.Default.TranslationPrompt = TranslationPromptBox.Text;
            string bulkHotkey = (TranslationBulkHotkeyBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.TranslationBulkHotkey = string.IsNullOrEmpty(bulkHotkey) ? "Ctrl+F9" : bulkHotkey;
            Properties.Settings.Default.AutoTranslate = AutoTranslateCheckBox.IsChecked == true;
            string autoHotkey = (AutoTranslateHotkeyBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.ShowGameToasts = ShowGameToastsCheckBox.IsChecked == true;
            Properties.Settings.Default.SettingsPageTranslation = SettingsPageTranslationCheckBox.IsChecked == true;
            Properties.Settings.Default.AutoTranslateHotkey = string.IsNullOrEmpty(autoHotkey) ? "Ctrl+Shift+F9" : autoHotkey;
            Properties.Settings.Default.SendTranslationEnabled = SendTranslationEnabled.IsChecked == true;
            Properties.Settings.Default.EnableManualTranslate = EnableManualTranslateCheckBox.IsChecked == true;
            string hotkey = (SendTranslationKeyBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.SendTranslationHotkey = string.IsNullOrEmpty(hotkey) ? "F9" : hotkey;
            Properties.Settings.Default.SendTranslationProvider = ProviderName(SendProviderList.SelectedIndex);
            Properties.Settings.Default.SendDeepSeekApiKey = SanitizeKey(SendApiKeyBox.Password);
            Properties.Settings.Default.SendDeepLApiKey = SanitizeKey(SendDeepLApiKeyBox.Password);
            Properties.Settings.Default.SendDoubaoApiKey = SanitizeKey(SendDoubaoApiKeyBox.Password);
            Properties.Settings.Default.SendZoomApiKey = SanitizeKey(SendZoomApiKeyBox.Password);
            if (SendModelList.SelectedIndex >= 0)
                Properties.Settings.Default.SendDeepSeekModel = SendModelList.SelectedItem.ToString();
            if (SendSpeedList.SelectedItem is ComboBoxItem sendSpeedItem)
                Properties.Settings.Default.SendDeepSeekReasoningSpeed = (string)sendSpeedItem.Tag;
            Properties.Settings.Default.SendDoubaoModel = string.IsNullOrWhiteSpace(SendDoubaoModelList.Text)
                ? "doubao-seed-2.0-lite"
                : SendDoubaoModelList.Text.Trim();
            Properties.Settings.Default.SendCustomEndpoint = (SendCustomEndpointBox.Text ?? string.Empty).Trim();
            Properties.Settings.Default.SendCustomProviderName = SendCustomProviderList.SelectedItem as string ?? string.Empty;
            Properties.Settings.Default.SendCustomApiKey = SanitizeKey(SendCustomApiKeyBox.Password);
            Properties.Settings.Default.SendCustomModel = (SendCustomModelList.Text ?? string.Empty).Trim();
            Properties.Settings.Default.SendCustomModels = string.Join(",", _customSendModels);
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
            SelectDeepSeekSpeed(Properties.Settings.Default.DeepSeekReasoningSpeed);
            SelectDoubaoModel(Properties.Settings.Default.DoubaoModel);
            SelectCustomProvider(CustomProviderList, CustomEndpointBox, Properties.Settings.Default.CustomProviderName, Properties.Settings.Default.CustomEndpoint);
            CustomApiKeyBox.Password = Properties.Settings.Default.CustomApiKey;
            SelectCustomModel(CustomModelList, Properties.Settings.Default.CustomModel);
            TranslationDisplayModeList.SelectedIndex = Properties.Settings.Default.TranslationDisplayMode == "replace" ? 1 : 0;
            EnableCacheCheckBox.IsChecked = Properties.Settings.Default.EnableTranslationCache;
            FuzzyCacheCheckBox.IsChecked = Properties.Settings.Default.EnableFuzzyCacheMatch;
            TranslationPromptBox.Text = Properties.Settings.Default.TranslationPrompt;
            SendTranslationEnabled.IsChecked = Properties.Settings.Default.SendTranslationEnabled;
            EnableManualTranslateCheckBox.IsChecked = Properties.Settings.Default.EnableManualTranslate;
            SendTranslationKeyBox.Text = Properties.Settings.Default.SendTranslationHotkey;
            SendProviderList.SelectedIndex = ProviderIndex(Properties.Settings.Default.SendTranslationProvider);
            SendApiKeyBox.Password = Properties.Settings.Default.SendDeepSeekApiKey;
            SendDeepLApiKeyBox.Password = Properties.Settings.Default.SendDeepLApiKey;
            SendDoubaoApiKeyBox.Password = Properties.Settings.Default.SendDoubaoApiKey;
            SendZoomApiKeyBox.Password = Properties.Settings.Default.SendZoomApiKey;
            SelectSendModel(Properties.Settings.Default.SendDeepSeekModel);
            SelectSendSpeed(Properties.Settings.Default.SendDeepSeekReasoningSpeed);
            SelectSendDoubaoModel(Properties.Settings.Default.SendDoubaoModel);
            SelectCustomProvider(SendCustomProviderList, SendCustomEndpointBox, Properties.Settings.Default.SendCustomProviderName, Properties.Settings.Default.SendCustomEndpoint);
            SendCustomApiKeyBox.Password = Properties.Settings.Default.SendCustomApiKey;
            SelectCustomModel(SendCustomModelList, Properties.Settings.Default.SendCustomModel);
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

            // Refresh the model lists once at startup so saved API keys are
            // applied against the account's real model list right away.
            ScheduleModelRefresh("DeepSeek");
            ScheduleModelRefresh("Doubao");
            ScheduleModelRefresh("Custom");
            ScheduleModelRefresh("SendDeepSeek");
            ScheduleModelRefresh("SendDoubao");
            ScheduleModelRefresh("SendCustom");
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
        /// Selects the saved Custom provider preset and fills the endpoint URL
        /// (unless the player saved a custom endpoint that differs from the
        /// preset's).
        /// </summary>
        private static void SelectCustomProvider(ComboBox combo, TextBox endpointBox, string savedName, string savedEndpoint)
        {
            TranslationController.AiProviderPreset preset = FindPreset(savedName);
            if (preset == null)
                preset = FindPreset("OpenAI Compatible");
            if (preset != null)
            {
                int idx = combo.Items.IndexOf(preset.Name);
                if (idx >= 0)
                    combo.SelectedIndex = idx;
                if (string.IsNullOrWhiteSpace(savedEndpoint) || string.Equals(savedEndpoint, preset.Endpoint, StringComparison.OrdinalIgnoreCase))
                    endpointBox.Text = preset.Endpoint;
                else
                    endpointBox.Text = savedEndpoint;
            }
            else
                endpointBox.Text = savedEndpoint;
        }

        /// <summary>
        /// Fills the Custom provider endpoint box when a preset is picked.
        /// </summary>
        private void CustomProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingTranslationSettings)
                return;
            FillEndpointFromProvider(CustomProviderList, CustomEndpointBox);
            SaveTranslationSettings();
            InvalidateModelCache("Custom");
            ScheduleModelRefresh("Custom");
        }

        private void SendCustomProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingTranslationSettings)
                return;
            FillEndpointFromProvider(SendCustomProviderList, SendCustomEndpointBox);
            SaveTranslationSettings();
            InvalidateModelCache("SendCustom");
            ScheduleModelRefresh("SendCustom");
        }

        private static void FillEndpointFromProvider(ComboBox combo, TextBox endpointBox)
        {
            TranslationController.AiProviderPreset preset = FindPreset(combo.SelectedItem as string);
            if (preset != null && !string.IsNullOrWhiteSpace(preset.Endpoint))
                endpointBox.Text = preset.Endpoint;
        }

        private static TranslationController.AiProviderPreset FindPreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            foreach (TranslationController.AiProviderPreset preset in TranslationController.AiProviderPresets)
            {
                if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
                    return preset;
            }
            return null;
        }

        /// <summary>
        /// Selects the Custom provider model matching the given name in the
        /// editable combo box (any typed model name works).
        /// </summary>
        private static void SelectCustomModel(ComboBox combo, string model)
        {
            for (int i = 0; i < combo.Items.Count; ++i)
            {
                if (string.Equals(combo.Items[i].ToString(), model, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            combo.Text = string.IsNullOrWhiteSpace(model) ? "qwen-plus" : model;
        }

        /// <summary>
        /// Splits the persisted comma-separated custom model list.
        /// </summary>
        private static IEnumerable<string> SplitModelList(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;
            foreach (string part in value.Split(','))
            {
                string name = part.Trim();
                if (name.Length > 0)
                    yield return name;
            }
        }

        /// <summary>
        /// Adds the typed model name to the chat Custom model box and remembers
        /// it so it is restored on the next start.
        /// </summary>
        private void CustomModelAddButton_Click(object sender, RoutedEventArgs e)
        {
            AddCustomModel(CustomModelList, _customChatModels);
            SaveTranslationSettings();
        }

        /// <summary>
        /// Adds the typed model name to the send-translation Custom model box
        /// and remembers it so it is restored on the next start.
        /// </summary>
        private void SendCustomModelAddButton_Click(object sender, RoutedEventArgs e)
        {
            AddCustomModel(SendCustomModelList, _customSendModels);
            SaveTranslationSettings();
        }

        private static void AddCustomModel(ComboBox combo, HashSet<string> store)
        {
            string name = (combo.Text ?? string.Empty).Trim();
            if (name.Length == 0)
                return;
            foreach (object item in combo.Items)
            {
                if (string.Equals(item.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.Items.Add(name);
            store.Add(name);
            combo.SelectedIndex = combo.Items.Count - 1;
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
        /// Guards the reasoning-speed / model two-way sync against
        /// re-entrancy while one control updates the other.
        /// </summary>
        private bool _syncingDeepSeekSpeed;

        /// <summary>
        /// Fills a reasoning-speed combo box with the localised options.
        /// </summary>
        private static void PopulateSpeedList(ComboBox list)
        {
            list.Items.Clear();
            list.Items.Add(new ComboBoxItem { Tag = "fast", Content = Strings.DeepSeekSpeedFast });
            list.Items.Add(new ComboBoxItem { Tag = "standard", Content = Strings.DeepSeekSpeedStandard });
            list.Items.Add(new ComboBoxItem { Tag = "high", Content = Strings.DeepSeekSpeedHigh });
        }

        /// <summary>
        /// Selects the reasoning-speed entry matching the saved value.
        /// </summary>
        private void SelectDeepSeekSpeed(string speed)
        {
            SelectSpeed(DeepSeekSpeedList, speed);
        }

        private void SelectSendSpeed(string speed)
        {
            SelectSpeed(SendSpeedList, speed);
        }

        private static void SelectSpeed(ComboBox list, string speed)
        {
            int index = SpeedIndex(list, speed);
            list.SelectedIndex = index >= 0 ? index : 0;
        }

        private static string SpeedValue(ComboBox list)
        {
            ComboBoxItem item = list.SelectedItem as ComboBoxItem;
            return item != null ? (string)item.Tag : "fast";
        }

        private static int SpeedIndex(ComboBox list, string speed)
        {
            for (int i = 0; i < list.Items.Count; ++i)
            {
                ComboBoxItem item = list.Items[i] as ComboBoxItem;
                if (item != null && string.Equals((string)item.Tag, speed, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// The reasoning speed drives the DeepSeek model: fast/standard map to
        /// deepseek-v4-flash, high maps to deepseek-v4-pro.
        /// </summary>
        private void DeepSeekSpeedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSpeedDrivenModel(DeepSeekSpeedList, DeepSeekModelList, "deepseek-v4-flash", "deepseek-v4-pro");
            if (!_loadingTranslationSettings)
                SaveTranslationSettings();
        }

        private void SendSpeedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSpeedDrivenModel(SendSpeedList, SendModelList, "deepseek-v4-flash", "deepseek-v4-pro");
            if (!_loadingTranslationSettings)
                SaveTranslationSettings();
        }

        private void UpdateSpeedDrivenModel(ComboBox speedList, ComboBox modelList, string flashModel, string proModel)
        {
            if (_syncingDeepSeekSpeed || speedList.SelectedItem == null)
                return;
            _syncingDeepSeekSpeed = true;
            try
            {
                string speed = SpeedValue(speedList);
                string target = "high".Equals(speed, StringComparison.OrdinalIgnoreCase) ? proModel : flashModel;
                int index = -1;
                for (int i = 0; i < modelList.Items.Count; ++i)
                {
                    if (modelList.Items[i].ToString() == target)
                    {
                        index = i;
                        break;
                    }
                }
                if (index >= 0)
                    modelList.SelectedIndex = index;
            }
            finally
            {
                _syncingDeepSeekSpeed = false;
            }
        }

        /// <summary>
        /// Logs a confirmation whenever the user switches the translation
        /// model in any provider dropdown, so it is clear the change took
        /// effect immediately.
        /// </summary>
        private void ModelSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingTranslationSettings)
                return;

            ComboBox combo = sender as ComboBox;
            if (combo == null)
                return;

            string provider;
            if (combo == DeepSeekModelList) provider = "DeepSeek";
            else if (combo == DoubaoModelList) provider = "豆包";
            else if (combo == CustomModelList) provider = "自定义";
            else if (combo == SendModelList) provider = "发送(DeepSeek)";
            else if (combo == SendDoubaoModelList) provider = "发送(豆包)";
            else if (combo == SendCustomModelList) provider = "发送(自定义)";
            else provider = "未知";

            TranslationController.LogEvent("翻译", "已切换" + provider + "翻译模型：" + (combo.SelectedItem ?? string.Empty));
        }

        /// <summary>
        /// Editable model combos only raise SelectionChanged when an actual
        /// item is picked. When the user types a model name, only the internal
        /// editable TextBox raises TextChanged (ComboBox itself has no such
        /// event), so hook the template part to save immediately and keep the
        /// running translation in sync without a restart.
        /// </summary>
        private void HookEditableModelCombo(ComboBox combo)
        {
            combo.Loaded += delegate
            {
                TextBox box = combo.Template != null ? combo.Template.FindName("PART_EditableTextBox", combo) as TextBox : null;
                if (box == null)
                    return;
                box.TextChanged -= ModelTextChanged;
                box.TextChanged += ModelTextChanged;
            };
        }

        private void ModelTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingTranslationSettings)
                return;
            SaveTranslationSettings();
        }

        /// <summary>
        /// Gives feedback when the user finishes typing a model name that was
        /// not picked from the dropdown. Selection changes are already logged
        /// by ModelSelectionChanged, so only genuinely typed values are logged.
        /// </summary>
        private void EditableModelList_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loadingTranslationSettings)
                return;

            ComboBox combo = sender as ComboBox;
            if (combo == null)
                return;

            string text = (combo.Text ?? string.Empty).Trim();
            string selected = combo.SelectedItem == null ? string.Empty : combo.SelectedItem.ToString();
            if (string.Equals(text, selected, StringComparison.Ordinal) || string.IsNullOrEmpty(text))
                return; // picked from the dropdown (already logged) or empty

            string provider;
            if (combo == DoubaoModelList) provider = "豆包";
            else if (combo == CustomModelList) provider = "自定义";
            else if (combo == SendDoubaoModelList) provider = "发送(豆包)";
            else if (combo == SendCustomModelList) provider = "发送(自定义)";
            else provider = "未知";

            TranslationController.LogEvent("翻译", "已切换" + provider + "翻译模型（输入）：" + text);
        }

        /// <summary>
        /// Keeps the reasoning-speed selection consistent when the model is
        /// changed manually: pro always means high quality; flash never high.
        /// </summary>
        private void DeepSeekModelList_SyncedSpeed(object sender, SelectionChangedEventArgs e)
        {
            SyncSpeedFromModel(DeepSeekModelList, DeepSeekSpeedList);
        }

        private void SendModelList_SyncedSpeed(object sender, SelectionChangedEventArgs e)
        {
            SyncSpeedFromModel(SendModelList, SendSpeedList);
        }

        private void SyncSpeedFromModel(ComboBox modelList, ComboBox speedList)
        {
            if (_loadingTranslationSettings || _syncingDeepSeekSpeed || modelList.SelectedItem == null)
                return;
            string model = modelList.SelectedItem.ToString();
            _syncingDeepSeekSpeed = true;
            try
            {
                if (string.Equals(model, "deepseek-v4-pro", StringComparison.OrdinalIgnoreCase))
                    speedList.SelectedIndex = SpeedIndex(speedList, "high");
                else if (string.Equals(model, "deepseek-v4-flash", StringComparison.OrdinalIgnoreCase)
                    && "high".Equals(SpeedValue(speedList), StringComparison.OrdinalIgnoreCase))
                    speedList.SelectedIndex = SpeedIndex(speedList, "standard");
            }
            finally
            {
                _syncingDeepSeekSpeed = false;
            }
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
            if (string.Equals(provider, "Custom", StringComparison.OrdinalIgnoreCase))
                return 4;
            if (string.Equals(provider, "Zoom", StringComparison.OrdinalIgnoreCase))
                return 5;
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
                return "Custom";
            if (index == 5)
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
            bool custom = index == 4;
            bool zoom = index == 5;

            DeepSeekApiKeyRow.Visibility = ToVisibility(deepSeek);
            DeepSeekModelRow.Visibility = ToVisibility(deepSeek);
            DeepSeekSpeedRow.Visibility = ToVisibility(deepSeek);
            TranslationPromptRow.Visibility = ToVisibility(deepSeek || doubao || custom);
            DeepLApiKeyRow.Visibility = ToVisibility(deepL);
            DoubaoApiKeyRow.Visibility = ToVisibility(doubao);
            DoubaoModelRow.Visibility = ToVisibility(doubao);
            CustomEndpointRow.Visibility = ToVisibility(custom);
            CustomProviderRow.Visibility = ToVisibility(custom);
            CustomApiKeyRow.Visibility = ToVisibility(custom);
            CustomModelRow.Visibility = ToVisibility(custom);
            ZoomApiKeyRow.Visibility = ToVisibility(zoom);
            TranslationStyleRow.Visibility = ToVisibility(deepSeek || doubao || custom);
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
            bool custom = index == 4;
            bool zoom = index == 5;

            SendApiKeyRow.Visibility = ToVisibility(deepSeek);
            SendModelRow.Visibility = ToVisibility(deepSeek);
            SendSpeedRow.Visibility = ToVisibility(deepSeek);
            SendPromptRow.Visibility = ToVisibility(deepSeek || doubao || custom);
            SendDeepLApiKeyRow.Visibility = ToVisibility(deepL);
            SendDoubaoApiKeyRow.Visibility = ToVisibility(doubao);
            SendDoubaoModelRow.Visibility = ToVisibility(doubao);
            SendCustomEndpointRow.Visibility = ToVisibility(custom);
            SendCustomProviderRow.Visibility = ToVisibility(custom);
            SendCustomApiKeyRow.Visibility = ToVisibility(custom);
            SendCustomModelRow.Visibility = ToVisibility(custom);
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
            EnableManualTranslateCheckBox.IsEnabled = enabled;

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
                SendCustomEndpointRow.Visibility = Visibility.Collapsed;
                SendCustomProviderRow.Visibility = Visibility.Collapsed;
                SendCustomApiKeyRow.Visibility = Visibility.Collapsed;
                SendCustomModelRow.Visibility = Visibility.Collapsed;
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
        /// Shows a native Windows balloon tip (the system bubble above the
        /// tray icon) for hotkey events. Safe to call from any thread;
        /// delegates to the UI thread when needed. If the bubble is
        /// suppressed by Windows notification settings, the failure shows up
        /// in the log and the in-game toast can still provide feedback.
        /// </summary>
        public void ShowWindowsToast(string text)
        {
            if (string.IsNullOrEmpty(text) || _trayIcon == null)
                return;

            Action show = () =>
            {
                try
                {
                    _trayIcon.ShowBalloonTip(3000, Strings.MainTitle, text, System.Windows.Forms.ToolTipIcon.Info);
                    TranslationController.LogEvent("翻译", "Windows 气泡已显示：" + text);
                }
                catch (Exception ex)
                {
                    TranslationController.LogEvent("翻译", "Windows 气泡显示失败：" + ex.Message);
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
