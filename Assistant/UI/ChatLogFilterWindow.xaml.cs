using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.Controllers;
using Assistant.Localization;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Assistant.UI
{
    /// <summary>
    /// Interaction logic for ChatLogFilterWindow.xaml
    /// </summary>
    public partial class ChatLogFilterWindow
    {
        private readonly MainWindow _mainWindow;

        private readonly bool _isLoading;
        private bool _usingAdvancedFilter;
        private bool _skippedWord;

        // Assign each filter criterion to a regex pattern
        private readonly Dictionary<string, Tuple<string, bool>> _filterCriteria = new Dictionary<string, Tuple<string, bool>>
        {
            // Filter, regex pattern, isEnabled (false = remove from log)
            { "OOC", Tuple.Create(@"^\(\( \(\d*\) [\p{L}]+ {0,1}([\p{L}]+){0,1}:.*?\)\)$", Properties.Settings.Default.OOCCriterionEnabled) },
            { "IC", Tuple.Create(@"^(\(Car\) ){0,1}[\p{L}]+ {0,1}([\p{L}]+){0,1} (says|shouts|whispers)( \[low\]){0,1}:.*$", Properties.Settings.Default.ICCriterionEnabled) },
            { "Emote", Tuple.Create(@"^\* [\p{L}]+ {0,1}([\p{L}]+){0,1} .*$", Properties.Settings.Default.EmoteCriterionEnabled) },
            { "Action", Tuple.Create(@"^\* .* \(\([\p{L}]+ {0,1}([\p{L}]+){0,1}\)\)\*$", Properties.Settings.Default.ActionCriterionEnabled) },
            { "PM", Tuple.Create(@"^\(\( PM (to|from) \(\d*\) [\p{L}]+ {0,1}([\p{L}]+){0,1}:.*?\)\)$", Properties.Settings.Default.PMCriterionEnabled) },
            { "Radio", Tuple.Create(@"^\*\*\[S: .* CH: .*\] [\p{L}]+ {0,1}([\p{L}]+){0,1}.*$", Properties.Settings.Default.RadioCriterionEnabled) },
            { "Ads", Tuple.Create(@"^\[.*Advertisement.*\] .*$", Properties.Settings.Default.AdsCriterionEnabled) }
        };

        private bool OtherEnabled => Other.IsChecked == true;

        private string ChatLog
        {
            get => _chatLog;
            set
            {
                _chatLog = value;
                _chatLogLoaded = !string.IsNullOrEmpty(_chatLog);
                StatusLabel.Content = string.Format(Strings.FilterLogStatus, _chatLogLoaded ? string.Empty : Strings.Negation, _chatLogLoaded ? string.Format(Strings.LoadedAt, DateTime.Now.ToString("HH:mm:ss")) : string.Empty);
                StatusLabel.Foreground = _chatLogLoaded ? Brushes.Green: Brushes.Red;
            }
        }

        private string _chatLog;
        private bool _chatLogLoaded;

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
        /// Initializes the chat log filter window
        /// </summary>
        /// <param name="mainWindow"></param>
        public ChatLogFilterWindow(MainWindow mainWindow)
        {
            _isLoading = true;
            _mainWindow = mainWindow;
            mainWindow.GotKeyboardFocus += GainFocus;
            InitializeComponent();

            Left = _mainWindow.Left + (_mainWindow.Width / 2 - Width / 2);
            Top = _mainWindow.Top + (_mainWindow.Height / 2 - Height / 2);

            ApplyLocalization();
            LoadSettings();
            DispatcherTimer timer = new DispatcherTimer();
            timer.Tick += Timer_Tick;
            timer.Interval = new TimeSpan(0, 0, 0, 1);
            timer.Start();
            _isLoading = false;
        }

        /// <summary>
        /// Applies the localized strings to the window's controls
        /// </summary>
        private void ApplyLocalization()
        {
            Title = Strings.FilterTitle;
            LoadUnparsed.Content = Strings.LoadUnparsed;
            BrowseForParsed.Content = Strings.BrowseForParsed;
            FilterModeToggle.Content = Strings.AdvancedFilter;

            IC.Content = Strings.CriterionLocal;
            OOC.Content = Strings.CriterionOOC;
            Emote.Content = Strings.CriterionEmote;
            Action.Content = Strings.CriterionAction;
            Radio.Content = Strings.CriterionRadio;
            PM.Content = Strings.CriterionPM;
            Ads.Content = Strings.CriterionAds;
            Other.Content = Strings.CriterionOther;

            RemoveTimestamps.Content = Strings.RemoveTimestamps;
            SaveFiltered.Content = Strings.SaveAs;
            CopyFilteredToClipboard.Content = Strings.CopyToClipboard;
            WordsLabel.Content = Strings.WordsToFilter;
            Filter.Content = Strings.Filter;

            if (string.IsNullOrWhiteSpace(Words.Text) || Words.Text == "Firstname Lastname")
                Words.Text = Strings.FirstnameLastname;
        }

        /// <summary>
        /// Saves the chat log filter settings
        /// </summary>
        private void SaveSettings()
        {
            Properties.Settings.Default.FilterNames = Words.Text;

            Properties.Settings.Default.OOCCriterionEnabled = OOC.IsChecked == true;
            Properties.Settings.Default.ICCriterionEnabled = IC.IsChecked == true;
            Properties.Settings.Default.EmoteCriterionEnabled = Emote.IsChecked == true;
            Properties.Settings.Default.ActionCriterionEnabled = Action.IsChecked == true;
            Properties.Settings.Default.PMCriterionEnabled = PM.IsChecked == true;
            Properties.Settings.Default.RadioCriterionEnabled = Radio.IsChecked == true;
            Properties.Settings.Default.AdsCriterionEnabled = Ads.IsChecked == true;
            Properties.Settings.Default.OtherCriterionEnabled = Other.IsChecked == true;
            Properties.Settings.Default.RemoveTimestampsFromFilter = RemoveTimestamps.IsChecked == true;

            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Loads the chat log filter settings
        /// </summary>
        private void LoadSettings()
        {
            LoadUnparsed.Focus();
            Filtered.Text = ChatLog = string.Empty;

            TimeLabel.Content = string.Format(Strings.CurrentTime, DateTime.Now.ToString("HH:mm:ss"));
            Words.Text = Properties.Settings.Default.FilterNames;

            OOC.IsChecked = Properties.Settings.Default.OOCCriterionEnabled;
            IC.IsChecked = Properties.Settings.Default.ICCriterionEnabled;
            Emote.IsChecked = Properties.Settings.Default.EmoteCriterionEnabled;
            Action.IsChecked = Properties.Settings.Default.ActionCriterionEnabled;
            PM.IsChecked = Properties.Settings.Default.PMCriterionEnabled;
            Radio.IsChecked = Properties.Settings.Default.RadioCriterionEnabled;
            Ads.IsChecked = Properties.Settings.Default.AdsCriterionEnabled;
            Other.IsChecked = Properties.Settings.Default.OtherCriterionEnabled;
            RemoveTimestamps.IsChecked = Properties.Settings.Default.RemoveTimestampsFromFilter;
        }

        /// <summary>
        /// Updates the time label to the
        /// current system time every tick
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Timer_Tick(object sender, EventArgs e)
        {
            TimeLabel.Content = string.Format(Strings.CurrentTime, DateTime.Now.ToString("HH:mm:ss"));
        }

        /// <summary>
        /// Parses the most recent chat
        /// log and loads it into memory
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoadUnparsed_Click(object sender, RoutedEventArgs e)
        {
            AppController.InitializeServerIp();
            ChatLog = AppController.ParseChatLog(false, true);
            
            if (_chatLogLoaded)
                TryToFilter(true);
        }

        /// <summary>
        /// Displays a file picker for the user
        /// to select a previously parsed chat log
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BrowseForParsed_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ChatLog = Filtered.Text = string.Empty;

                Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
                {
                    InitialDirectory = string.IsNullOrWhiteSpace(Properties.Settings.Default.BackupPath) ? Path.GetPathRoot(Environment.SystemDirectory) : Properties.Settings.Default.BackupPath,
                    Filter = "Text File | *.txt"
                };

                if (dialog.ShowDialog() == true)
                {
                    using (StreamReader streamReader = new StreamReader(dialog.FileName))
                    {
                        ChatLog = streamReader.ReadToEnd();
                    }
                }

                if (_chatLogLoaded)
                    TryToFilter(true);
            }
            catch
            {
                ChatLog = Filtered.Text = string.Empty;
                MessageBox.Show(Strings.FilterReadError, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Removes timestamps from the filtered chat log
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RemoveTimestamps_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_chatLogLoaded)
                TryToFilter(true);
        }

        /// <summary>
        /// Updates the dictionary with the
        /// current state of each criterion
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Criterion_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            string criterionName;
            try
            {
                criterionName = ((System.Windows.Controls.CheckBox)sender).Name;
            }
            catch
            {
                criterionName = string.Empty;
            }
            if (string.IsNullOrWhiteSpace(criterionName))
                return;

            KeyValuePair<string, Tuple<string, bool>>? entry = _filterCriteria.Where(pair => pair.Key == criterionName)
                            .Select(pair => (KeyValuePair<string, Tuple<string, bool>>?)pair)
                            .FirstOrDefault();

            if (entry != null)
                _filterCriteria[entry.Value.Key] = Tuple.Create(_filterCriteria[entry.Value.Key].Item1, !_filterCriteria[entry.Value.Key].Item2);
            
            if (_chatLogLoaded)
                TryToFilter(true);
        }

        /// <summary>
        /// Tries to filter the currently loaded chat log
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            TryToFilter();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fastFilter"></param>
        private void TryToFilter(bool fastFilter = false)
        {
            if (!_chatLogLoaded)
            {
                if (!fastFilter)
                    MessageBox.Show(Strings.NoChatLogLoaded, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                
                return;
            }

            _skippedWord = false;
            string logToCheck = ChatLog;
            string[] lines = logToCheck.Split('\n');
            string filtered = string.Empty;

            // Simple filtering
            if (!_usingAdvancedFilter)
            {
                // Loop through every line in the
                // loaded chat log
                foreach (string line in lines)
                {
                    // Skip blank lines
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // Assume the criterion is disabled
                    bool isCriterionEnabled = false;
                    bool matchedRegularCriterion = false;
                    
                    // Loop through every criterion and check if the line matches any of them
                    foreach (KeyValuePair<string, Tuple<string, bool>> keyValuePair in _filterCriteria.Where(keyValuePair => !string.IsNullOrWhiteSpace(keyValuePair.Key) && !string.IsNullOrWhiteSpace(keyValuePair.Value.Item1)).Where(keyValuePair => Regex.IsMatch(Regex.Replace(line, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty), keyValuePair.Value.Item1, RegexOptions.IgnoreCase)))
                    {
                        matchedRegularCriterion = true;

                        if (keyValuePair.Value.Item2 != true) continue;
                        isCriterionEnabled = true;
                        break;
                    }

                    // Add the line to the filtered chat log if the criterion is
                    // enabled or if it didn't match any criterion and Other is enabled
                    if (isCriterionEnabled || !matchedRegularCriterion && OtherEnabled)
                        filtered += line + "\n";

                    // Next line
                }
            }
            else // Advanced filtering
            {
                // Get the words we need to look for
                List<string> wordsToCheck = GetWordsToFilterIn();
                if (wordsToCheck.Count == 0)
                {
                    MessageBox.Show(
                        !string.IsNullOrWhiteSpace(Words.Text) ? Strings.FilterHint : Strings.NoWordsToFilter,
                        Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);

                    return;
                }

                // Loop through every line in the
                // loaded chat log
                foreach (string line in lines)
                {
                    // Skip blank lines
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // Skip the line if there are no words of
                    // interest in it
                    if (!wordsToCheck.Where(word => !string.IsNullOrWhiteSpace(word)).Any(word =>
                        Regex.Replace(line, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty).ToLower()
                            .Contains(word.ToLower()))) continue;

                    // Assume the criterion is disabled
                    bool isCriterionEnabled = false;
                    bool matchedRegularCriterion = false;

                    // Loop through every criterion and check if the line matches any of them
                    foreach (KeyValuePair<string, Tuple<string, bool>> keyValuePair in _filterCriteria.Where(keyValuePair => !string.IsNullOrWhiteSpace(keyValuePair.Key) && !string.IsNullOrWhiteSpace(keyValuePair.Value.Item1)).Where(keyValuePair => Regex.IsMatch(Regex.Replace(line, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty), keyValuePair.Value.Item1, RegexOptions.IgnoreCase)))
                    {
                        matchedRegularCriterion = true;

                        if (keyValuePair.Value.Item2 != true) continue;
                        isCriterionEnabled = true;
                        break;
                    }

                    // Add the line to the filtered chat log if the criterion is
                    // enabled or if it didn't match any criterion and Other is enabled
                    if (isCriterionEnabled || !matchedRegularCriterion && OtherEnabled)
                        filtered += line + "\n";

                    // Next line
                }
            }

            // Filter successful
            if (filtered.Length > 0)
            {
                filtered = filtered.TrimEnd('\r', '\n');

                if (RemoveTimestamps.IsChecked == true)
                    filtered = Regex.Replace(filtered, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty);

                Filtered.Text = filtered;
            }
            else // Nothing found
            {
                if (RemoveTimestamps.IsChecked == true)
                    logToCheck = Regex.Replace(logToCheck, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty);

                Filtered.Text = logToCheck;

                if (!fastFilter)
                {
                    if (!Properties.Settings.Default.DisableInformationPopups)
                        MessageBox.Show(Strings.FilterHintNoMatches, Strings.Information, MessageBoxButton.OK, MessageBoxImage.Information);
                    else
                        Filtered.Text = string.Empty;
                }
            }

            if (_usingAdvancedFilter && _skippedWord && !Properties.Settings.Default.DisableInformationPopups)
                MessageBox.Show(Strings.FilterHintSkipped, Strings.Information, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Returns a list of all valid words that are
        /// in the Words text box to use for filtering
        /// </summary>
        /// <returns></returns>
        private List<string> GetWordsToFilterIn()
        {
            _skippedWord = false;
            string words = Words.Text;
            string[] lines = words.Split('\n');

            List<string> finalWords = new List<string>();
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                string newLine = line.Trim();
                
                // NEW: Add the words to the final list
                finalWords.Add(newLine);

                // OLD: single words OR names, nothing larger
                //string[] splitWord = newLine.Split(' ', '_');

                //switch (splitWord.Length)
                //{
                //    case 2 when string.IsNullOrWhiteSpace(splitWord[0]) || string.IsNullOrWhiteSpace(splitWord[1]):
                //        continue;
                //    case 2: // Valid name (Ex: John Doe or John_Doe)
                //        finalWords.Add($"{splitWord[0]} {splitWord[1]}");
                //        finalWords.Add($"{splitWord[0]}_{splitWord[1]}");
                //        break;
                //    case 1 when !string.IsNullOrWhiteSpace(splitWord[0]):
                //        finalWords.Add(splitWord[0]); // Valid word (Ex: Blue)
                //        break;
                //    default:
                //        skippedWord = true;
                //        break;
                //}
            }

            return finalWords;
        }

        /// <summary>
        /// Displays a save file dialog to save the
        /// contents of the Filtered text box to the disk
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SaveFiltered_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Filtered.Text))
                {
                    if (!Properties.Settings.Default.DisableErrorPopups)
                        MessageBox.Show(Strings.NothingFiltered, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                    
                    return;
                }

                Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "filtered_chatlog.txt",
                    Filter = "Text File | *.txt"
                };

                if (dialog.ShowDialog() != true) return;
                using (StreamWriter sw = new StreamWriter(dialog.OpenFile()))
                {
                    sw.Write(Filtered.Text);
                }
            }
            catch
            {
                MessageBox.Show(Strings.SaveError, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Copies the contents of the
        /// Filtered text box to the clipboard
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CopyFilteredToClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Filtered.Text) && !Properties.Settings.Default.DisableErrorPopups)
                MessageBox.Show(Strings.NothingFiltered, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            else
                Clipboard.SetText(Filtered.Text);
        }

        /// <summary>
        /// Toggles between simple filtering and advanced filtering
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FilterModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _usingAdvancedFilter = !_usingAdvancedFilter;

            FilterModeToggle.Content = _usingAdvancedFilter ? Strings.SimpleFilter : Strings.AdvancedFilter;
            Width = _usingAdvancedFilter ? 656 : 494;

            if (_chatLogLoaded && !_usingAdvancedFilter)
                TryToFilter(true);
        }

        /// <summary>
        /// Saves the settings before the filter chat log window closes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ChatLogFilter_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            _mainWindow.GotKeyboardFocus -= GainFocus;
        }

        /// <summary>
        /// Loops back to the start when tabbing over
        /// to the advanced filtering options when
        /// simple filtering is being used
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Words_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_usingAdvancedFilter)
                LoadUnparsed.Focus();
        }

        /// <summary>
        /// Loops back to the start when tabbing over
        /// to the advanced filtering options when
        /// simple filtering is being used
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Filter_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_usingAdvancedFilter)
                LoadUnparsed.Focus();
        }
    }
}
