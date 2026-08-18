using System;
using System.Linq;
using Assistant.UI;
using System.Windows;
using System.Threading;
using Assistant.Properties;
using Assistant.Controllers;

namespace Assistant
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        private static bool startMinimized;
        private static bool isRestarted;

        /// <summary>
        /// Initializes the "follow system eligibility"
        /// for the app mode and system accent color
        /// </summary>
        /// <param name="e"></param>
        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize the eligibility
            StyleController.InitializeFollowEligibility();

            // Set the current app mode depending
            // on the "follow system eligibility"
            if (Settings.Default.FollowSystemMode)
            {
                if (AppController.CanFollowSystemMode)
                    StyleController.DarkMode = StyleController.GetAppMode();
                else
                    Settings.Default.FollowSystemMode = false;
            }

            // Set the current app theme depending
            // on the "follow system eligibility"
            if (Settings.Default.FollowSystemColor)
            {
                if (AppController.CanFollowSystemColor)
                {
                    StyleController.ValidStyles.Add("Windows");
                    StyleController.Style = "Windows";
                }
                else
                    Settings.Default.FollowSystemColor = false;
            }
            Settings.Default.Save();

            // Apply the changes
            StyleController.UpdateTheme();
            base.OnStartup(e);
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Get the command line arguments and check
            // if the current session is a restart or
            // a minimized start
            string[] args = Environment.GetCommandLineArgs();
            if (args.Any(arg => arg == $"{AppController.ParameterPrefix}restart"))
                isRestarted = true;

            if (args.Any(arg => arg == $"{AppController.ParameterPrefix}minimized"))
                startMinimized = true;

            // Make sure only one instance is running
            // if the application is not currently restarting
            Mutex mutex = new Mutex(true, "GTAWChatLogAssistant", out bool isUnique);
            if (!isUnique && !isRestarted)
            {
                MessageBox.Show(Localization.Strings.OtherInstanceRunning, Localization.Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown();
                return;
            }

            // Check if settings already exist
            // for a previous assembly version
            if (!Settings.Default.HasPickedLanguage)
                Settings.Default.Upgrade();

            // Initialize the controllers and
            // display the server picker on the
            // first start, or the main window
            // on subsequent starts
            LocalizationController.InitializeLocale();
            AppController.InitializeServerIp();

            if (!Settings.Default.HasPickedLanguage)
            {
                //LanguagePickerWindow languagePicker = new LanguagePickerWindow();
                //languagePicker.Show();

                Settings.Default.LanguageCode = LocalizationController.DefaultLanguage;
                Settings.Default.HasPickedLanguage = true;
                Settings.Default.Save();
            }

            MainWindow mainWindow = new MainWindow(startMinimized);
            if (!startMinimized)
                mainWindow.Show();

            // Don't let the garbage
            // collector touch the Mutex
            GC.KeepAlive(mutex);
        }

        /// <summary>
        /// Stops the running threads when
        /// quitting the application
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            StyleController.StopWatchers();
            BackupController.Quitting = true;
            SendTranslationController.Stop();
            FiveMChatCaptureController.Stop();
            TranslationController.FlushCache();
        }
    }
}
