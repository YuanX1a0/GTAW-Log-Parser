using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Assistant.Localization;

namespace Assistant.Controllers
{
    public static class AppController
    {
        public const string AssemblyVersion = "6.2.0";
        public static readonly string Version = "v" + AssemblyVersion;
        public const bool IsBetaVersion = false;
        public static bool CanFollowSystemColor = false;

        public const string ParameterPrefix = "--";
        public const string ProductHeader = "GTAW-FiveM-Log-Parser";
        public const string ResourceDirectory = "FiveM local NUI chat";

        public static readonly string ExecutablePath = Process.GetCurrentProcess().MainModule?.FileName;
        public static readonly string StartupPath = Path.GetDirectoryName(ExecutablePath);
        public static string PreviousLog = string.Empty;

        /// <summary>
        /// Keeps the original controller entry point but starts FiveM NUI capture instead.
        /// </summary>
        public static void InitializeServerIp()
        {
            FiveMChatCaptureController.Initialize();
            SendTranslationController.Start();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        /// <summary>
        /// Applies Windows 11 rounded corners to the given window
        /// through the DWM corner preference API. No-op on older
        /// Windows versions that do not support the attribute.
        /// </summary>
        public static void ApplyRoundedCorners(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                int preference = DwmwcpRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
            catch
            {
                // Rounded corners are cosmetic; ignore failures on unsupported systems.
            }
        }

        public static bool IsFiveMRunning()
        {
            try
            {
                return Process.GetProcesses().Any(process => process.ProcessName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the GTAW chat previously captured locally from the FiveM HUD.
        /// </summary>
        public static string ParseChatLog(bool removeTimestamps, bool showError = false)
        {
            FiveMChatCaptureController.Initialize();
            string log = FiveMChatCaptureController.ReadCapturedChat(false);
            PreviousLog = log;

            if (removeTimestamps)
                log = System.Text.RegularExpressions.Regex.Replace(log, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty);

            if (string.IsNullOrWhiteSpace(log) && showError)
            {
                MessageBox.Show(
                    "No FiveM GTAW chat has been captured yet. Open GTAW and wait for its HUD to load.",
                    Strings.Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return log;
        }
    }
}
