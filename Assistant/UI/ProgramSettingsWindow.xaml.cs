using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Assistant.Controllers;
using Assistant.Localization;

namespace Assistant.UI
{
    /// <summary>
    /// Interaction logic for ProgramSettingsWindow.xaml
    /// </summary>
    public partial class ProgramSettingsWindow
    {
        private readonly MainWindow _mainWindow;
        private bool _handleLanguageChange;

        /// <summary>
        /// Focuses back on this window if
        /// another window from this application
        /// gains focus (workaround for MahApps)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void GainFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            Focus();
        }

        /// <summary>
        /// Initializes the program settings window
        /// </summary>
        /// <param name="mainWindow"></param>
        public ProgramSettingsWindow(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            _mainWindow.GotKeyboardFocus += GainFocus;
            InitializeComponent();

            Left = _mainWindow.Left + (_mainWindow.Width / 2 - Width / 2);
            Top = _mainWindow.Top + (_mainWindow.Height / 2 - Height / 2) + 55;

            CloseWindow.Focus();
            StyleController.ValidStyles.Remove("Windows");
            ApplyLocalization();
            InitializeLanguageSwitcher();
            InitializeTargetLanguageSwitcher();
            InitializeTranslationControls();
            LoadSettings();
        }

        /// <summary>
        /// Applies the localized strings to the window's controls
        /// </summary>
        private void ApplyLocalization()
        {
            Title = Strings.SettingsTitle;
            TabTitleBar.Header = Strings.SettingsTitleBar;
            TabOther.Header = Strings.SettingsOther;
            TabTheme.Header = Strings.SettingsTheme;
            TabLanguage.Header = Strings.Language;
            TabTranslation.Header = Strings.SectionTranslation;

            DisableForumsButton.Content = Strings.SettingsDisableForumsIcon;
            DisableFacebrowserButton.Content = Strings.SettingsDisableFacebrowserIcon;
            DisableUCPButton.Content = Strings.SettingsDisableUCPIcon;
            DisableReleasesButton.Content = Strings.SettingsDisableReleasesIcon;
            DisableProjectButton.Content = Strings.SettingsDisableProjectIcon;
            DisableInformationPopups.Content = Strings.SettingsDisableInfoPopups;
            DisableWarningPopups.Content = Strings.SettingsDisableWarningPopups;
            DisableErrorPopups.Content = Strings.SettingsDisableErrorPopups;
            TimeoutLabel1.Content = Strings.SettingsAbortCheckPrefix;
            IgnoreBetaVersions.Content = Strings.SettingsIgnoreBeta;
            FollowSystemColor.Content = Strings.SettingsFollowSystemColor;
            FollowSystemMode.Content = Strings.SettingsFollowSystemMode;
            ToggleDarkMode.Content = Strings.SettingsDarkMode;
            AutoParse.Content = Strings.AutoParse;
            TranslationEnabled.Content = Strings.TranslationEnabled;
            TargetLanguageLabel.Content = Strings.TargetLanguage;
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
            DoubaoFreeEndpointLabel.Content = Strings.DoubaoFreeEndpoint;
            TranslationDisplayModeLabel.Content = Strings.TranslationDisplayMode;
            TranslationPromptLabel.Content = Strings.TranslationPrompt;
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
            SendDoubaoFreeEndpointLabel.Content = Strings.SendDoubaoFreeEndpoint;
            SendPromptLabel.Content = Strings.TranslationPrompt;
            TranslationStyleLabel.Content = Strings.TranslationStyle;

            CloseWindow.Content = Strings.Close;
            Reset.Content = Strings.Reset;
        }

        /// <summary>
        /// Saves the program settings
        /// </summary>
        private void SaveSettings()
        {
            Properties.Settings.Default.DisableForumsButton = DisableForumsButton.IsChecked == true;
            Properties.Settings.Default.DisableFacebrowserButton = DisableFacebrowserButton.IsChecked == true;
            Properties.Settings.Default.DisableUCPButton = DisableUCPButton.IsChecked == true;
            Properties.Settings.Default.DisableReleasesButton = DisableReleasesButton.IsChecked == true;
            Properties.Settings.Default.DisableProjectButton = DisableProjectButton.IsChecked == true;
            if (Timeout.Value != null) Properties.Settings.Default.UpdateCheckTimeout = (int) Timeout.Value;

            Properties.Settings.Default.DisableInformationPopups = DisableInformationPopups.IsChecked == true;
            Properties.Settings.Default.DisableWarningPopups = DisableWarningPopups.IsChecked == true;
            Properties.Settings.Default.DisableErrorPopups = DisableErrorPopups.IsChecked == true;
            Properties.Settings.Default.IgnoreBetaVersions = IgnoreBetaVersions.IsChecked == true;
            Properties.Settings.Default.FollowSystemColor = FollowSystemColor.IsChecked == true;
            Properties.Settings.Default.FollowSystemMode = FollowSystemMode.IsChecked == true;
            Properties.Settings.Default.AutoParse = AutoParse.IsChecked == true;
            Properties.Settings.Default.TranslationEnabled = TranslationEnabled.IsChecked == true;
            if (TargetLanguageList.SelectedIndex >= 0 && TargetLanguageList.SelectedIndex < TranslationController.TargetLanguages.Length)
                Properties.Settings.Default.TargetLanguage = TranslationController.TargetLanguages[TargetLanguageList.SelectedIndex].Key;
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
            Properties.Settings.Default.DoubaoFreeEndpoint = (DoubaoFreeEndpointBox.Text ?? string.Empty).Trim();
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
            Properties.Settings.Default.SendDoubaoFreeEndpoint = (SendDoubaoFreeEndpointBox.Text ?? string.Empty).Trim();
            if (SendModelList.SelectedIndex >= 0)
                Properties.Settings.Default.SendDeepSeekModel = SendModelList.SelectedItem.ToString();
            Properties.Settings.Default.SendDoubaoModel = string.IsNullOrWhiteSpace(SendDoubaoModelList.Text)
                ? "doubao-seed-2.0-lite"
                : SendDoubaoModelList.Text.Trim();
            Properties.Settings.Default.SendTranslationPrompt = SendPromptBox.Text;
            Properties.Settings.Default.TranslationStyle = TranslationStyleList.SelectedIndex == 1 ? "formal" : TranslationStyleList.SelectedIndex == 2 ? "literary" : "casual";

            StyleController.DarkMode = ToggleDarkMode.IsChecked == true;
            StyleController.Style = Themes.SelectedItem.ToString();

            if (AutoParse.IsChecked == true)
                _mainWindow.StartAutoParse();
            else
                _mainWindow.StopAutoParse();

            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Loads the program settings
        /// </summary>
        private void LoadSettings()
        {
            DisableForumsButton.IsChecked = Properties.Settings.Default.DisableForumsButton;
            DisableFacebrowserButton.IsChecked = Properties.Settings.Default.DisableFacebrowserButton;
            DisableUCPButton.IsChecked = Properties.Settings.Default.DisableUCPButton;
            DisableReleasesButton.IsChecked = Properties.Settings.Default.DisableReleasesButton;
            DisableProjectButton.IsChecked = Properties.Settings.Default.DisableProjectButton;
            Timeout.Value = Properties.Settings.Default.UpdateCheckTimeout;

            DisableInformationPopups.IsChecked = Properties.Settings.Default.DisableInformationPopups;
            DisableWarningPopups.IsChecked = Properties.Settings.Default.DisableWarningPopups;
            DisableErrorPopups.IsChecked = Properties.Settings.Default.DisableErrorPopups;
            IgnoreBetaVersions.IsChecked = Properties.Settings.Default.IgnoreBetaVersions;

            FollowSystemColor.IsChecked = Properties.Settings.Default.FollowSystemColor;
            FollowSystemMode.IsChecked = Properties.Settings.Default.FollowSystemMode;
            FollowSystemColor.IsEnabled = AppController.CanFollowSystemColor;
            FollowSystemMode.IsEnabled = AppController.CanFollowSystemMode;
            AutoParse.IsChecked = Properties.Settings.Default.AutoParse;
            TranslationEnabled.IsChecked = Properties.Settings.Default.TranslationEnabled;
            SelectTargetLanguage(Properties.Settings.Default.TargetLanguage);
            SelectSendLanguage(SendSourceLanguageList, Properties.Settings.Default.SendSourceLanguage, true);
            SelectSendLanguage(SendTargetLanguageList, Properties.Settings.Default.SendTargetLanguage, false);
            TranslationProviderList.SelectedIndex = ProviderIndex(Properties.Settings.Default.TranslationProvider);
            DeepSeekApiKeyBox.Password = Properties.Settings.Default.DeepSeekApiKey;
            DeepLApiKeyBox.Password = Properties.Settings.Default.DeepLApiKey;
            DoubaoApiKeyBox.Password = Properties.Settings.Default.DoubaoApiKey;
            DoubaoFreeEndpointBox.Text = Properties.Settings.Default.DoubaoFreeEndpoint;
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
            SendDoubaoFreeEndpointBox.Text = Properties.Settings.Default.SendDoubaoFreeEndpoint;
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

            ToggleDarkMode.IsChecked = StyleController.DarkMode;
            ToggleDarkMode.IsEnabled = !Properties.Settings.Default.FollowSystemMode;
            Timeout.Foreground = _mainWindow.UpdateCheckProgress.Foreground = ToggleDarkMode.IsChecked == true ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;

            Themes.IsEnabled = !Properties.Settings.Default.FollowSystemColor;
            UpdateThemeSwitcher();
        }

        /// <summary>
        /// Initializes the Style picker ComboBox
        /// </summary>
        private void UpdateThemeSwitcher()
        {
            Themes.Items.Clear();
            foreach (string style in StyleController.ValidStyles)
            {
                Themes.Items.Add(style);
            }
            Themes.SelectedItem = StyleController.Style;
        }

        /// <summary>
        /// Initializes the Language picker ComboBox
        /// with all available languages and
        /// selects the currently used one
        /// </summary>
        private void InitializeLanguageSwitcher()
        {
            _handleLanguageChange = false;

            LanguageList.Items.Clear();
            foreach (string code in LocalizationController.AvailableLanguages)
                LanguageList.Items.Add(LocalizationController.GetDisplayName(code));

            LanguageList.SelectedIndex = LocalizationController.GetLanguageIndex(LocalizationController.GetLanguage());

            _handleLanguageChange = true;
        }

        /// <summary>
        /// Initializes the target language ComboBox
        /// for the in-game translation feature
        /// </summary>
        private void InitializeTargetLanguageSwitcher()
        {
            TargetLanguageList.Items.Clear();
            foreach (KeyValuePair<string, string> pair in TranslationController.TargetLanguages)
                TargetLanguageList.Items.Add(pair.Value);
        }

        /// <summary>
        /// Initializes the translation provider, DeepSeek model
        /// and translation display mode ComboBoxes
        /// </summary>
        private void InitializeTranslationControls()
        {
            TranslationProviderList.Items.Clear();
            TranslationProviderList.Items.Add(Strings.TranslationProviderGoogle);
            TranslationProviderList.Items.Add(Strings.TranslationProviderDeepSeek);
            TranslationProviderList.Items.Add(Strings.TranslationProviderDeepL);
            TranslationProviderList.Items.Add(Strings.TranslationProviderDoubao);
            TranslationProviderList.Items.Add(Strings.TranslationProviderDoubaoFree);

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
            SendProviderList.Items.Add(Strings.TranslationProviderDoubaoFree);

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
        }

        /// <summary>
        /// Selects the Doubao model matching the given name or endpoint ID.
        /// The box is editable so any Ark inference endpoint ID also works.
        /// </summary>
        /// <param name="model"></param>
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
        /// <param name="model"></param>
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
        /// <param name="model"></param>
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
        /// Maps a translation provider name to its combo box index
        /// (0 = Google, 1 = DeepSeek, 2 = DeepL, 3 = Doubao, 4 = DoubaoFree)
        /// </summary>
        private static int ProviderIndex(string provider)
        {
            if (string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(provider, "DeepL", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(provider, "Doubao", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (string.Equals(provider, "DoubaoFree", StringComparison.OrdinalIgnoreCase))
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
                return "DoubaoFree";
            return "Google";
        }

        /// <summary>
        /// Enables or disables the DeepSeek controls depending
        /// on the selected translation provider
        /// </summary>
        private void UpdateTranslationProviderState()
        {
            int index = TranslationProviderList.SelectedIndex;
            bool deepSeek = index == 1;
            bool deepL = index == 2;
            bool doubao = index == 3;
            bool doubaoFree = index == 4;
            DeepSeekApiKeyLabel.IsEnabled = deepSeek;
            DeepSeekApiKeyBox.IsEnabled = deepSeek;
            DeepSeekModelLabel.IsEnabled = deepSeek;
            DeepSeekModelList.IsEnabled = deepSeek;
            TranslationPromptLabel.IsEnabled = deepSeek || doubao || doubaoFree;
            TranslationPromptBox.IsEnabled = deepSeek || doubao || doubaoFree;
            DeepLApiKeyLabel.IsEnabled = deepL;
            DeepLApiKeyBox.IsEnabled = deepL;
            DoubaoApiKeyLabel.IsEnabled = doubao;
            DoubaoApiKeyBox.IsEnabled = doubao;
            DoubaoModelLabel.IsEnabled = doubao;
            DoubaoModelList.IsEnabled = doubao;
            DoubaoFreeEndpointLabel.IsEnabled = doubaoFree;
            DoubaoFreeEndpointBox.IsEnabled = doubaoFree;
        }

        /// <summary>
        /// Toggles the DeepSeek controls when the
        /// translation provider changes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TranslationProviderList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateTranslationProviderState();
        }

        /// <summary>
        /// Selects the send translation DeepSeek model matching the given name
        /// </summary>
        /// <param name="model"></param>
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
        /// Enables or disables the send translation DeepSeek controls
        /// depending on the selected provider
        /// </summary>
        private void UpdateSendProviderState()
        {
            int index = SendProviderList.SelectedIndex;
            bool deepSeek = index == 1;
            bool deepL = index == 2;
            bool doubao = index == 3;
            bool doubaoFree = index == 4;
            SendApiKeyLabel.IsEnabled = deepSeek;
            SendApiKeyBox.IsEnabled = deepSeek;
            SendModelLabel.IsEnabled = deepSeek;
            SendModelList.IsEnabled = deepSeek;
            SendPromptLabel.IsEnabled = deepSeek || doubao || doubaoFree;
            SendPromptBox.IsEnabled = deepSeek || doubao || doubaoFree;
            SendDeepLApiKeyLabel.IsEnabled = deepL;
            SendDeepLApiKeyBox.IsEnabled = deepL;
            SendDoubaoApiKeyLabel.IsEnabled = doubao;
            SendDoubaoApiKeyBox.IsEnabled = doubao;
            SendDoubaoModelLabel.IsEnabled = doubao;
            SendDoubaoModelList.IsEnabled = doubao;
            SendDoubaoFreeEndpointLabel.IsEnabled = doubaoFree;
            SendDoubaoFreeEndpointBox.IsEnabled = doubaoFree;
        }

        /// <summary>
        /// Toggles the send translation DeepSeek controls when
        /// the send provider changes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SendProviderList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateSendProviderState();
        }

        /// <summary>
        /// Selects the target language matching the given code
        /// </summary>
        /// <param name="code"></param>
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
        private static void SelectSendLanguage(System.Windows.Controls.ComboBox list, string code, bool withAuto)
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
            if (!enabled)
            {
                SendApiKeyLabel.IsEnabled = false;
                SendApiKeyBox.IsEnabled = false;
                SendModelLabel.IsEnabled = false;
                SendModelList.IsEnabled = false;
                SendPromptLabel.IsEnabled = false;
                SendPromptBox.IsEnabled = false;
            }
            else
            {
                UpdateSendProviderState();
            }
        }

        /// <summary>
        /// Toggles the hotkey box when the send translation feature changes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SendTranslationEnabled_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSendTranslationState();
        }

        /// <summary>
        /// Asks the user to restart the application
        /// after changing the language and applies
        /// the change if confirmed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LanguageList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_handleLanguageChange || LanguageList.SelectedIndex < 0)
                return;

            string selectedCode = LocalizationController.AvailableLanguages[LanguageList.SelectedIndex];

            // Ignore the event if the language did not actually change
            if (LocalizationController.GetLanguage() == selectedCode)
                return;

            CultureInfo cultureInfo = new CultureInfo(selectedCode);
            if (MessageBox.Show(Strings.ResourceManager.GetString("SwitchServer", cultureInfo),
                Strings.ResourceManager.GetString("Restart", cultureInfo), MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                // Revert the ComboBox to the current language
                InitializeLanguageSwitcher();
                return;
            }

            LocalizationController.SetLanguage(selectedCode);
            _mainWindow.RestartApplication();
        }

        /// <summary>
        /// Resets the backup settings
        /// </summary>
        private static void ResetSettings()
        {
            Properties.Settings.Default.DisableForumsButton = true;
            Properties.Settings.Default.DisableFacebrowserButton = true;
            Properties.Settings.Default.DisableUCPButton = true;
            Properties.Settings.Default.DisableReleasesButton= false;
            Properties.Settings.Default.DisableProjectButton = true;
            Properties.Settings.Default.UpdateCheckTimeout = 4;

            Properties.Settings.Default.DisableInformationPopups = false;
            Properties.Settings.Default.DisableWarningPopups = false;
            Properties.Settings.Default.DisableErrorPopups = false;
            Properties.Settings.Default.IgnoreBetaVersions = true;
            Properties.Settings.Default.FollowSystemColor = AppController.CanFollowSystemColor;
            Properties.Settings.Default.FollowSystemMode = AppController.CanFollowSystemMode;
            Properties.Settings.Default.AutoParse = false;
            Properties.Settings.Default.TranslationEnabled = false;
            Properties.Settings.Default.TargetLanguage = "zh-CN";
            Properties.Settings.Default.SendSourceLanguage = "zh-CN";
            Properties.Settings.Default.SendTargetLanguage = "en";
            Properties.Settings.Default.TranslationProvider = "Google";
            Properties.Settings.Default.DeepSeekApiKey = string.Empty;
            Properties.Settings.Default.DeepLApiKey = string.Empty;
            Properties.Settings.Default.DeepSeekModel = "deepseek-v4-flash";
            Properties.Settings.Default.DoubaoApiKey = string.Empty;
            Properties.Settings.Default.DoubaoModel = "doubao-seed-2.0-lite";
            Properties.Settings.Default.DoubaoFreeEndpoint = "http://127.0.0.1:8791/v1/chat/completions";
            Properties.Settings.Default.TranslationDisplayMode = "append";
            Properties.Settings.Default.TranslationPrompt = string.Empty;
            Properties.Settings.Default.SendTranslationEnabled = false;
            Properties.Settings.Default.SendTranslationHotkey = "F9";
            Properties.Settings.Default.SendTranslationProvider = "Google";
            Properties.Settings.Default.SendDeepSeekApiKey = string.Empty;
            Properties.Settings.Default.SendDeepLApiKey = string.Empty;
            Properties.Settings.Default.SendDeepSeekModel = "deepseek-v4-flash";
            Properties.Settings.Default.SendDoubaoApiKey = string.Empty;
            Properties.Settings.Default.SendDoubaoModel = "doubao-seed-2.0-lite";
            Properties.Settings.Default.SendDoubaoFreeEndpoint = "http://127.0.0.1:8791/v1/chat/completions";
            Properties.Settings.Default.SendTranslationPrompt = string.Empty;
            Properties.Settings.Default.TranslationStyle = "casual";
            Properties.Settings.Default.TranslationBulkHotkey = "Ctrl+F9";
            Properties.Settings.Default.AutoTranslate = false;
            Properties.Settings.Default.AutoTranslateHotkey = "Ctrl+Shift+F9";
            Properties.Settings.Default.ShowGameToasts = true;
            Properties.Settings.Default.SettingsPageTranslation = false;
            Properties.Settings.Default.TranslatorWindowLeft = -1;
            Properties.Settings.Default.TranslatorWindowTop = -1;

            StyleController.DarkMode = AppController.CanFollowSystemMode && StyleController.GetAppMode();
            StyleController.Style = AppController.CanFollowSystemColor ? "Windows" : "Default";

            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Updates the update timeout hint text
        /// according to the IntegerUpDown
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Timeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (TimeoutLabel2 == null)
                return;

            TimeoutLabel2.Content = string.Format(Strings.UpdateAbortTime, Timeout.Value > 1 ? Strings.SecondPlural : Strings.SecondSingular);
        }

        /// <summary>
        /// Toggles the Forums button on the title bar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisableForumsButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            _mainWindow.OpenForums.Visibility = DisableForumsButton.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Toggles the Facebrowser button on the title bar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisableFacebrowserButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            _mainWindow.OpenFacebrowser.Visibility = DisableFacebrowserButton.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Toggles the UCP button on the title bar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisableUCPButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            _mainWindow.OpenUCP.Visibility = DisableUCPButton.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Toggles the Releases button on the title bar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisableReleasesButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            _mainWindow.OpenGithubReleases.Visibility = DisableReleasesButton.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Toggles the Project button on the title bar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisableProjectButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            _mainWindow.OpenGithubProject.Visibility = DisableProjectButton.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Toggles the "Follow System Accent Color" option
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FollowSystemColor_CheckedChanged(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.FollowSystemColor = FollowSystemColor.IsChecked == true;

            Themes.IsEnabled = FollowSystemColor.IsChecked != true;
            if (FollowSystemColor.IsChecked == true)
                StyleController.ValidStyles.Add("Windows");
            else
                StyleController.ValidStyles.Remove("Windows");

            UpdateThemeSwitcher();
            Themes.SelectedItem = FollowSystemColor.IsChecked == true ? "Windows" : "Default";
        }

        /// <summary>
        /// Toggles the "Follow System App Mode" option
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FollowSystemMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.FollowSystemMode = FollowSystemMode.IsChecked == true;

            ToggleDarkMode.IsEnabled = FollowSystemMode.IsChecked != true;
            ToggleDarkMode.IsChecked = FollowSystemMode.IsChecked == true && StyleController.GetAppMode();
        }

        /// <summary>
        /// Toggles the app mode from light to dark and vice versa
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ToggleDarkMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            StyleController.DarkMode = ToggleDarkMode.IsChecked == true;
            StyleController.UpdateTheme();
            
            Timeout.Foreground = _mainWindow.UpdateCheckProgress.Foreground = ToggleDarkMode.IsChecked == true ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
        }

        /// <summary>
        /// Changes the application theme to the one chosen
        /// in the Theme ComboBox
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Themes_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (Themes.Items.Count < StyleController.ValidStyles.Count)
                return;

            StyleController.Style = Themes.SelectedItem.ToString();
            StyleController.UpdateTheme();
        }

        /// <summary>
        /// Resets and reloads the program settings
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ResetSettings();
            LoadSettings();
        }

        /// <summary>
        /// Closes the program settings window
        /// when the "Close" button is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Saves the settings before the program settings window closes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ProgramSettings_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            _mainWindow.GotKeyboardFocus -= GainFocus;
        }
    }
}
