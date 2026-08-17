using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Assistant.Controllers;
using Assistant.Localization;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Assistant.UI
{
    /// <summary>
    /// Interaction logic for BackupSettingsWindow.xaml
    /// </summary>
    public partial class BackupSettingsWindow
    {
        private readonly MainWindow _mainWindow;
        private readonly bool _isLoading;

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
        /// Initializes the backup window
        /// </summary>
        /// <param name="mainWindow"></param>
        public BackupSettingsWindow(MainWindow mainWindow)
        {
            _isLoading = true;
            _mainWindow = mainWindow;
            _mainWindow.GotKeyboardFocus += GainFocus;
            InitializeComponent();

            Left = _mainWindow.Left + (_mainWindow.Width / 2 - Width / 2);
            Top = _mainWindow.Top + (_mainWindow.Height / 2 - Height / 2);

            ApplyLocalization();
            LoadSettings();
            _isLoading = false;
        }

        /// <summary>
        /// Applies the localized strings to the window's controls
        /// </summary>
        private void ApplyLocalization()
        {
            Title = Strings.BackupSettingsTitle;
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
        /// Saves the backup settings
        /// </summary>
        private void SaveSettings()
        {
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
        /// Loads the backup settings
        /// </summary>
        private void LoadSettings()
        {
            Browse.Focus();

            BackupPath.Text = Properties.Settings.Default.BackupPath;

            BackUpChatLogAutomatically.IsChecked = Properties.Settings.Default.BackupChatLogAutomatically;
            EnableIntervalBackup.IsChecked = Properties.Settings.Default.EnableIntervalBackup;
            Interval.Value = Properties.Settings.Default.IntervalTime;
            RemoveTimestamps.IsChecked = Properties.Settings.Default.RemoveTimestampsFromBackup;
            AlwaysCloseToTray.IsChecked = Properties.Settings.Default.AlwaysCloseToTray;
            StartWithWindows.IsChecked = Properties.Settings.Default.StartWithWindows;
            SuppressNotifications.IsChecked = Properties.Settings.Default.SuppressNotifications;
            WarnWithHash.IsChecked = Properties.Settings.Default.WarnOnSameHash;

            Interval.Foreground = StyleController.DarkMode ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
        }

        /// <summary>
        /// Resets the backup settings
        /// </summary>
        public static void ResetSettings()
        {
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
        /// Asks the user if they would like to move their
        /// backups to the new backup directory location
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BackupPath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isLoading || string.IsNullOrWhiteSpace(Properties.Settings.Default.BackupPath))
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
        /// Opens the directory picker
        /// when the text box is clicked on
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BackupPath_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(BackupPath.Text))
                Browse_Click(this, null);
        }

        /// <summary>
        /// Displays a directory picker until
        /// a non-root directory is selected
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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
        /// Disables most of the checkboxes when the
        /// automatic backup functionality is turned off
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BackUpChatLogAutomatically_CheckedChanged(object sender, RoutedEventArgs e)
        {
            EnableIntervalBackup.IsEnabled = BackUpChatLogAutomatically.IsChecked == true;
            RemoveTimestamps.IsEnabled = BackUpChatLogAutomatically.IsChecked == true;
            AlwaysCloseToTray.IsEnabled = BackUpChatLogAutomatically.IsChecked == true;
            StartWithWindows.IsEnabled = BackUpChatLogAutomatically.IsChecked == true;
            SuppressNotifications.IsEnabled = BackUpChatLogAutomatically.IsChecked == true;
            WarnWithHash.IsEnabled = BackUpChatLogAutomatically.IsChecked == true;

            if (BackUpChatLogAutomatically.IsChecked == true) return;
            AlwaysCloseToTray.IsChecked = false;
            StartWithWindows.IsChecked = false;
            RemoveTimestamps.IsChecked = false;
            EnableIntervalBackup.IsChecked = false;
            SuppressNotifications.IsChecked = false;
            WarnWithHash.IsChecked = false;
        }

        /// <summary>
        /// Toggles the interval IntegerUpDown when
        /// the interval backup option is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void EnableIntervalBackup_CheckedChanged(object sender, RoutedEventArgs e)
        {
            Interval.IsEnabled = EnableIntervalBackup.IsChecked == true;
        }

        /// <summary>
        /// Updates the interval backup hint text
        /// according to the IntegerUpDown
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Interval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (IntervalLabel2 == null)
                return;

            IntervalLabel2.Content = string.Format(Strings.IntervalRecommended, Interval.Value > 1 ? Strings.MinutePlural : Strings.MinuteSingular);
            EnableIntervalBackup.Content = string.Format(Strings.IntervalHint, Interval.Value, Interval.Value > 1 ? Strings.MinutePlural : Strings.MinuteSingular);
        }

        /// <summary>
        /// Displays a warning about the Start With Windows functionality
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StartWithWindows_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (StartWithWindows.IsChecked == true && !StartupController.IsAddedToStartup() && !Properties.Settings.Default.DisableWarningPopups)
                MessageBox.Show(Strings.AutoStartWarning, Strings.Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>
        /// Resets and reloads the backup settings
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ResetSettings();
            LoadSettings();
        }

        /// <summary>
        /// Closes the backup window when the
        /// "Close" button is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Saves the settings before the backup window closes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BackupSettings_Closing(object sender, System.ComponentModel.CancelEventArgs e)
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
    }
}
