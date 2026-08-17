using System;
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
            InitializeLanguageSwitcher();
            LoadSettings();
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

            StyleController.DarkMode = ToggleDarkMode.IsChecked == true;
            StyleController.Style = Themes.SelectedItem.ToString();

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
            foreach (LocalizationController.Language language in (LocalizationController.Language[])Enum.GetValues(typeof(LocalizationController.Language)))
                LanguageList.Items.Add(language.ToString());

            string currentLanguage = LocalizationController.GetLanguageFromCode(LocalizationController.GetLanguage());
            int index = LanguageList.Items.IndexOf(currentLanguage);
            LanguageList.SelectedIndex = index >= 0 ? index : 0;

            _handleLanguageChange = true;
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
            if (!_handleLanguageChange || LanguageList.SelectedItem == null)
                return;

            LocalizationController.Language selectedLanguage;
            if (!Enum.TryParse(LanguageList.SelectedItem.ToString(), out selectedLanguage))
                return;

            // Ignore the event if the language did not actually change
            if (LocalizationController.GetLanguage() == LocalizationController.GetCodeFromLanguage(selectedLanguage))
                return;

            CultureInfo cultureInfo = new CultureInfo(LocalizationController.GetCodeFromLanguage(selectedLanguage));
            if (MessageBox.Show(Strings.ResourceManager.GetString("SwitchServer", cultureInfo),
                Strings.ResourceManager.GetString("Restart", cultureInfo), MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                // Revert the ComboBox to the current language
                InitializeLanguageSwitcher();
                return;
            }

            LocalizationController.SetLanguage(selectedLanguage);
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
