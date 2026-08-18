using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private bool _isLoadingBackup;

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
            SourceInitialized += (s, e) => AppController.ApplyRoundedCorners(this);

            Left = _mainWindow.Left + (_mainWindow.Width / 2 - Width / 2);
            Top = _mainWindow.Top + (_mainWindow.Height / 2 - Height / 2) + 55;

            CloseWindow.Focus();
            ApplyLocalization();
            InitializeLanguageSwitcher();
            LoadSettings();
        }

        /// <summary>
        /// Applies the localized strings to the window's controls
        /// </summary>
        private void ApplyLocalization()
        {
            Title = Strings.SettingsTitle;
            TabOther.Header = Strings.SettingsOther;
            TabLanguage.Header = Strings.Language;
            TabBackup.Header = Strings.BackupSettingsTitle;

            DisableInformationPopups.Content = Strings.SettingsDisableInfoPopups;
            DisableWarningPopups.Content = Strings.SettingsDisableWarningPopups;
            DisableErrorPopups.Content = Strings.SettingsDisableErrorPopups;
            AutoParse.Content = Strings.AutoParse;

            ClearTranslationCache.Content = Strings.DeleteTranslationCache;
            UpdateCacheHint();

            BackupPathLabel.Content = Strings.BackupPathLabel;
            Browse.Content = Strings.Browse;
            BackUpChatLogAutomatically.Content = Strings.BackupAutomatically;
            IntervalLabel1.Content = Strings.BackupEveryPrefix;
            RemoveTimestamps.Content = Strings.RemoveTimestampsFromBackup;
            SuppressNotifications.Content = Strings.SuppressNotifications;
            AlwaysCloseToTray.Content = Strings.AlwaysCloseToTray;
            StartWithWindows.Content = Strings.StartWithWindows;
            WarnWithHash.Content = Strings.WarnOnSameHash;

            CloseWindow.Content = Strings.Close;
            Reset.Content = Strings.Reset;
        }

        /// <summary>
        /// Saves the program settings
        /// </summary>
        private void SaveSettings()
        {
            Properties.Settings.Default.DisableInformationPopups = DisableInformationPopups.IsChecked == true;
            Properties.Settings.Default.DisableWarningPopups = DisableWarningPopups.IsChecked == true;
            Properties.Settings.Default.DisableErrorPopups = DisableErrorPopups.IsChecked == true;
            Properties.Settings.Default.AutoParse = AutoParse.IsChecked == true;

            if (AutoParse.IsChecked == true)
                _mainWindow.StartAutoParse();
            else
                _mainWindow.StopAutoParse();

            Properties.Settings.Default.BackupPath = BackupPath.Text;
            Properties.Settings.Default.BackupChatLogAutomatically = BackUpChatLogAutomatically.IsChecked == true;
            Properties.Settings.Default.EnableIntervalBackup = EnableIntervalBackup.IsChecked == true;
            if (Interval.Value != null) Properties.Settings.Default.IntervalTime = (int)Interval.Value;
            Properties.Settings.Default.RemoveTimestampsFromBackup = RemoveTimestamps.IsChecked == true;
            Properties.Settings.Default.AlwaysCloseToTray = AlwaysCloseToTray.IsChecked == true;
            Properties.Settings.Default.StartWithWindows = StartWithWindows.IsChecked == true;
            Properties.Settings.Default.SuppressNotifications = SuppressNotifications.IsChecked == true;
            Properties.Settings.Default.WarnOnSameHash = WarnWithHash.IsChecked == true;

            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Loads the program settings
        /// </summary>
        private void LoadSettings()
        {
            DisableInformationPopups.IsChecked = Properties.Settings.Default.DisableInformationPopups;
            DisableWarningPopups.IsChecked = Properties.Settings.Default.DisableWarningPopups;
            DisableErrorPopups.IsChecked = Properties.Settings.Default.DisableErrorPopups;
            AutoParse.IsChecked = Properties.Settings.Default.AutoParse;

            _isLoadingBackup = true;
            BackupPath.Text = Properties.Settings.Default.BackupPath;
            BackUpChatLogAutomatically.IsChecked = Properties.Settings.Default.BackupChatLogAutomatically;
            EnableIntervalBackup.IsChecked = Properties.Settings.Default.EnableIntervalBackup;
            Interval.Value = Properties.Settings.Default.IntervalTime;
            RemoveTimestamps.IsChecked = Properties.Settings.Default.RemoveTimestampsFromBackup;
            AlwaysCloseToTray.IsChecked = Properties.Settings.Default.AlwaysCloseToTray;
            StartWithWindows.IsChecked = Properties.Settings.Default.StartWithWindows;
            SuppressNotifications.IsChecked = Properties.Settings.Default.SuppressNotifications;
            WarnWithHash.IsChecked = Properties.Settings.Default.WarnOnSameHash;
            _isLoadingBackup = false;
            UpdateBackupControlsState();
            UpdateCacheHint();
        }

        /// <summary>
        /// Shows the current number of cached translations next to the
        /// "delete translation cache" button.
        /// </summary>
        private void UpdateCacheHint()
        {
            ClearTranslationCacheHint.Text = string.Format(Strings.DeleteTranslationCacheHint,
                TranslationController.CacheCount.ToString("N0"));
        }

        /// <summary>
        /// Deletes all cached translations after asking for confirmation.
        /// </summary>
        private void ClearTranslationCache_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(Strings.DeleteTranslationCacheConfirm, Strings.DeleteTranslationCache,
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            TranslationController.ClearCache();
            UpdateCacheHint();
            _mainWindow.RefreshOverviewStats();
            MessageBox.Show(Strings.TranslationCacheCleared, Strings.Information,
                MessageBoxButton.OK, MessageBoxImage.Information);
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
        /// Resets the program settings to their defaults
        /// </summary>
        private static void ResetSettings()
        {
            Properties.Settings.Default.DisableInformationPopups = false;
            Properties.Settings.Default.DisableWarningPopups = false;
            Properties.Settings.Default.DisableErrorPopups = false;
            Properties.Settings.Default.AutoParse = false;

            Properties.Settings.Default.BackupPath = string.Empty;
            Properties.Settings.Default.BackupChatLogAutomatically = false;
            Properties.Settings.Default.EnableIntervalBackup = false;
            Properties.Settings.Default.IntervalTime = 10;
            Properties.Settings.Default.RemoveTimestampsFromBackup = false;
            Properties.Settings.Default.AlwaysCloseToTray = false;
            Properties.Settings.Default.StartWithWindows = false;
            Properties.Settings.Default.SuppressNotifications = false;
            Properties.Settings.Default.WarnOnSameHash = false;

            Properties.Settings.Default.Save();
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
            if (BackUpChatLogAutomatically.IsChecked == true && (string.IsNullOrWhiteSpace(BackupPath.Text) || !Directory.Exists(BackupPath.Text)))
            {
                e.Cancel = true;
                MessageBox.Show(Strings.BadBackupPathSave, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (StartWithWindows.IsChecked == true && !StartupController.IsAddedToStartup() || !StartWithWindows.IsChecked == true && StartupController.IsAddedToStartup())
                StartupController.ToggleStartup(StartWithWindows.IsChecked == true);

            SaveSettings();
            _mainWindow.GotKeyboardFocus -= GainFocus;
        }

        /// <summary>
        /// Enables or disables the backup checkboxes depending on whether
        /// automatic backup is turned on.
        /// </summary>
        private void UpdateBackupControlsState()
        {
            bool enabled = BackUpChatLogAutomatically.IsChecked == true;
            EnableIntervalBackup.IsEnabled = enabled;
            RemoveTimestamps.IsEnabled = enabled;
            AlwaysCloseToTray.IsEnabled = enabled;
            StartWithWindows.IsEnabled = enabled;
            SuppressNotifications.IsEnabled = enabled;
            WarnWithHash.IsEnabled = enabled;
        }

        /// <summary>
        /// Asks the user if they would like to move their
        /// backups to the new backup directory location
        /// </summary>
        private void BackupPath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isLoadingBackup || string.IsNullOrWhiteSpace(Properties.Settings.Default.BackupPath))
                return;

            try
            {
                DirectoryInfo[] directories = new DirectoryInfo(Properties.Settings.Default.BackupPath).GetDirectories();
                List<DirectoryInfo> finalDirectories = directories.Where(directory => Regex.IsMatch(directory.Name, @"20\d{2}")).ToList();

                if (finalDirectories.Count <= 0) return;
                if (MessageBox.Show(Strings.MoveBackups, Strings.Question, MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                List<string> moved = new List<string>();
                List<string> notMoved = new List<string>();

                foreach (DirectoryInfo directory in finalDirectories)
                {
                    if (!Directory.Exists(BackupPath.Text + directory.Name))
                    {
                        Directory.Move(directory.FullName, BackupPath.Text + directory.Name);
                        moved.Add(directory.Name);
                    }
                    else
                        notMoved.Add(directory.Name);
                }

                Properties.Settings.Default.BackupPath = BackupPath.Text;
                Properties.Settings.Default.Save();

                if (notMoved.Count > 0)
                    MessageBox.Show((moved.Count > 0 ? string.Format(Strings.PartialMoveWarning, string.Join(", ", moved)) : Strings.NothingMovedWarning) + string.Format(Strings.AlreadyExistingDirectoriesWarning, string.Join(", ", notMoved)), Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                MessageBox.Show(Strings.BackupMoveError, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Opens the directory picker when the path text box is clicked.
        /// </summary>
        private void BackupPath_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(BackupPath.Text))
                Browse_Click(this, null);
        }

        /// <summary>
        /// Displays a directory picker until a non-root directory is selected.
        /// </summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog directoryBrowserDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = Strings.BackupPathLabel,
                RootFolder = Environment.SpecialFolder.MyComputer,
                SelectedPath = string.IsNullOrWhiteSpace(BackupPath.Text) || !Directory.Exists(BackupPath.Text) ? Path.GetPathRoot(Environment.SystemDirectory) : BackupPath.Text,
                ShowNewFolderButton = true
            };

            bool validLocation = false;
            while (!validLocation)
            {
                if (directoryBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (directoryBrowserDialog.SelectedPath[directoryBrowserDialog.SelectedPath.Length - 1] != '\\')
                    {
                        BackupPath.Text = directoryBrowserDialog.SelectedPath + "\\";
                        validLocation = true;
                    }
                    else
                        MessageBox.Show(Strings.BadBackupPath, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                    validLocation = true;
            }

            Activate();
        }

        /// <summary>
        /// Toggles the backup controls when the automatic backup option changes.
        /// </summary>
        private void BackUpChatLogAutomatically_CheckedChanged(object sender, RoutedEventArgs e)
        {
            UpdateBackupControlsState();

            if (BackUpChatLogAutomatically.IsChecked == true) return;
            AlwaysCloseToTray.IsChecked = false;
            StartWithWindows.IsChecked = false;
            RemoveTimestamps.IsChecked = false;
            EnableIntervalBackup.IsChecked = false;
            SuppressNotifications.IsChecked = false;
            WarnWithHash.IsChecked = false;
        }

        /// <summary>
        /// Toggles the interval IntegerUpDown when interval backup is toggled.
        /// </summary>
        private void EnableIntervalBackup_CheckedChanged(object sender, RoutedEventArgs e)
        {
            Interval.IsEnabled = EnableIntervalBackup.IsChecked == true;
        }

        /// <summary>
        /// Updates the interval backup hint text according to the IntegerUpDown.
        /// </summary>
        private void Interval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (IntervalLabel2 == null)
                return;

            IntervalLabel2.Content = string.Format(Strings.IntervalRecommended, Interval.Value > 1 ? Strings.MinutePlural : Strings.MinuteSingular);
            EnableIntervalBackup.Content = string.Format(Strings.IntervalHint, Interval.Value, Interval.Value > 1 ? Strings.MinutePlural : Strings.MinuteSingular);
        }

        /// <summary>
        /// Displays a warning about the Start With Windows functionality.
        /// </summary>
        private void StartWithWindows_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (StartWithWindows.IsChecked == true && !StartupController.IsAddedToStartup() && !Properties.Settings.Default.DisableWarningPopups)
                MessageBox.Show(Strings.AutoStartWarning, Strings.Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
