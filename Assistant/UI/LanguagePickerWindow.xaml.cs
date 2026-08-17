using System;
using System.Windows;
using System.Diagnostics;
using Assistant.Controllers;
using Assistant.Localization;
using System.Windows.Threading;

namespace Assistant.UI
{
    /// <summary>
    /// Interaction logic for LanguagePickerWindow.xaml
    /// </summary>
    public partial class LanguagePickerWindow
    {
        private bool _isStarting;
        private readonly bool _handleListChange;

        /// <summary>
        /// Initializes the language picker window
        /// </summary>
        public LanguagePickerWindow()
        {
            InitializeComponent();

            DispatcherTimer timer = new DispatcherTimer();
            timer.Tick += Timer_Tick;
            timer.Interval = new TimeSpan(0, 0, 0, 0, 10);
            timer.Start();

            foreach (string code in LocalizationController.AvailableLanguages)
                LanguageList.Items.Add(LocalizationController.GetDisplayName(code));

            StartButton.Content = Strings.Start;
            Title = Strings.MenuChangeServer;

            LanguageList.SelectedIndex = 0;
            _handleListChange = true;

            StartButton.Focus();
        }

        /// <summary>
        /// Saves the chosen locale and restarts
        /// the application
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.LanguageCode = LocalizationController.GetLanguage();
            Properties.Settings.Default.HasPickedLanguage = true;
            Properties.Settings.Default.Save();

            _isStarting = true;
            Close();
        }

        /// <summary>
        /// Sets the chosen locale
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LanguageList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_handleListChange)
                return;

            LocalizationController.SetLanguage(LocalizationController.AvailableLanguages[LanguageList.SelectedIndex], false);
        }

        /// <summary>
        /// Translates the greeting label to the left every tick
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Timer_Tick(object sender, EventArgs e)
        {
            Thickness margin = WelcomeLabel.Margin;

            if (margin.Left < -1201 && margin.Right > -549)
            {
                margin.Left = 0;
                margin.Right = -1750;
            }
            else
            {
                margin.Left -= 1;
                margin.Right += 1;
            }

            WelcomeLabel.Margin = margin;
        }

        /// <summary>
        /// Quits the application if no locale has been chosen or
        /// restarts the application if the start button was pressed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LanguagePicker_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isStarting)
            {
                ProcessStartInfo startInfo = Process.GetCurrentProcess().StartInfo;
                startInfo.FileName = AppController.ExecutablePath;
                startInfo.Arguments = $"{AppController.ParameterPrefix}restart";
                Process.Start(startInfo);
            }

            Application.Current.Shutdown();
        }
    }
}
