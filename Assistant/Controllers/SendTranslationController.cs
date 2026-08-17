using System;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;
using Assistant.Localization;
using Assistant.Properties;

namespace Assistant.Controllers
{
    /// <summary>
    /// Translates text typed into the in-game chat input and lets the player
    /// review / edit the translation in a NUI popup before sending it.
    /// Triggered by a configurable hotkey or the button next to the chat input.
    /// </summary>
    public static class SendTranslationController
    {
        private static readonly object SyncRoot = new object();
        private static readonly FiveMChatCaptureController.NuiChatReader Reader = new FiveMChatCaptureController.NuiChatReader();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static Thread workerThread;
        private static bool runWorker;
        private static bool hookInstalled;

        public static void Start()
        {
            if (runWorker)
                return;

            runWorker = true;
            workerThread = new Thread(Worker) { IsBackground = true, Name = "GTAW send translation" };
            workerThread.Start();
        }

        public static void Stop()
        {
            runWorker = false;
            lock (SyncRoot)
            {
                try
                {
                    Reader.UninstallSendHook();
                }
                catch
                {
                    // FiveM may already be closed
                }
                Reader.Close();
            }
        }

        private static void Worker()
        {
            while (runWorker)
            {
                try
                {
                    if (Properties.Settings.Default.SendTranslationEnabled)
                    {
                        lock (SyncRoot)
                        {
                            Reader.InstallSendHook(Properties.Settings.Default.SendTranslationHotkey);
                            hookInstalled = true;
                            ProcessTranslator();
                        }
                    }
                    else if (hookInstalled)
                    {
                        lock (SyncRoot)
                        {
                            Reader.UninstallSendHook();
                            hookInstalled = false;
                        }
                    }
                }
                catch
                {
                    // NUI may be reloading; retry on the next poll
                }

                Thread.Sleep(300);
            }
        }

        private static string lastInputText;
        private static string lastTranslated;
        private static DateTime lastRetranslate = DateTime.UtcNow;

        /// <summary>
        /// Keeps the persistent in-game translator window in sync with the chat
        /// input while it is active and the chat box is open. The hotkey toggles
        /// the active state; the window itself is shown/hidden by the in-game
        /// hook whenever the chat box opens or closes.
        /// </summary>
        private static void ProcessTranslator()
        {
            IDictionary<string, object> state = Reader.TakeSendTranslatorState();
            bool active = state != null && state.ContainsKey("active") && Convert.ToBoolean(state["active"]);
            bool visible = state != null && state.ContainsKey("visible") && Convert.ToBoolean(state["visible"]);
            bool created = state != null && state.ContainsKey("created") && Convert.ToBoolean(state["created"]);

            // Persist the window position after the player drags it in-game.
            SaveWindowPosition(state);

            if (!active || !visible)
            {
                lastInputText = null;
                lastTranslated = null;
                return;
            }

            // Handle the window buttons first.
            IDictionary<string, object> result = Reader.TakeSendResult();
            if (result != null)
            {
                string action = result.ContainsKey("action") ? result["action"] as string : null;
                string message = result.ContainsKey("text") ? result["text"] as string : null;
                if (action == "send" && !string.IsNullOrWhiteSpace(message))
                {
                    Reader.SendChatMessage(message);
                    Reader.ClearSendOverlay();
                    lastInputText = null;
                    lastTranslated = null;
                }
                else if (action == "apply" && !string.IsNullOrWhiteSpace(message))
                {
                    Reader.SetChatInputText(message);
                }
                else if (action == "close")
                {
                    lastInputText = null;
                    lastTranslated = null;
                    return; // The close button already deactivated the window
                }
            }

            string inputText = Reader.GetChatInputText();
            if (string.IsNullOrWhiteSpace(inputText))
            {
                if (created)
                    Reader.ClearSendOverlay();
                lastInputText = null;
                lastTranslated = null;
                return;
            }

            if (!created)
            {
                // First time the window is shown: translate the current input
                // and build the window with the result.
                string translated = TranslateBody(inputText);
                Reader.EnsureSendOverlay(
                    inputText,
                    translated,
                    Strings.SendOverlayTitle,
                    Strings.SendOverlaySend,
                    Strings.SendOverlayApply,
                    Strings.SendOverlayCancel,
                    Settings.Default.TranslatorWindowLeft,
                    Settings.Default.TranslatorWindowTop);
                lastInputText = inputText;
                lastTranslated = translated;
                lastRetranslate = DateTime.UtcNow;
                return;
            }

            if (inputText != lastInputText)
            {
                lastInputText = inputText;
                Reader.UpdateSendOverlayOriginal(inputText);

                // Re-translate when the player edits the in-game chat input
                // (throttled so typing does not flood the translation provider).
                if ((DateTime.UtcNow - lastRetranslate).TotalMilliseconds > 1200)
                {
                    lastRetranslate = DateTime.UtcNow;
                    string newTranslated = TranslateBody(inputText);
                    if (!string.Equals(newTranslated, lastTranslated, StringComparison.Ordinal))
                    {
                        lastTranslated = newTranslated;
                        Reader.UpdateSendOverlayTranslated(newTranslated);
                    }
                }
            }
        }

        /// <summary>
        /// Persists the in-game translator window position (reported after the
        /// player drags it) to local settings so it can be restored next time.
        /// </summary>
        private static void SaveWindowPosition(IDictionary<string, object> state)
        {
            try
            {
                if (state == null || !state.ContainsKey("pos") || state["pos"] == null)
                    return;
                IDictionary<string, object> pos = state["pos"] as IDictionary<string, object>;
                if (pos == null || !pos.ContainsKey("left") || !pos.ContainsKey("top"))
                    return;
                int left = Convert.ToInt32(pos["left"]);
                int top = Convert.ToInt32(pos["top"]);
                if (Settings.Default.TranslatorWindowLeft != left || Settings.Default.TranslatorWindowTop != top)
                {
                    Settings.Default.TranslatorWindowLeft = left;
                    Settings.Default.TranslatorWindowTop = top;
                    Settings.Default.Save();
                }
            }
            catch
            {
                // Ignore malformed or partial positions.
            }
        }

        /// <summary>
        /// Translates the chat message body (name prefix stays untranslated)
        /// using the translator provider settings.
        /// </summary>
        private static string TranslateBody(string text)
        {
            string prefix;
            string body = SplitNamePrefix(text, out prefix);
            string translated;
            try
            {
                translated = TranslationController.Translate(
                    body,
                    Properties.Settings.Default.SendTargetLanguage,
                    Properties.Settings.Default.SendSourceLanguage,
                    Properties.Settings.Default.SendTranslationProvider,
                    Properties.Settings.Default.SendDeepSeekApiKey,
                    Properties.Settings.Default.SendDeepSeekModel,
                    Properties.Settings.Default.SendTranslationPrompt,
                    Properties.Settings.Default.TranslationStyle);
            }
            catch
            {
                translated = string.Empty;
            }

            if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(translated))
                translated = prefix + translated;
            return translated;
        }

        /// <summary>
        /// Splits a chat message into its leading name prefix
        /// ("[channel] Name: " or "Name: ") and the rest of the message.
        /// The prefix is kept untranslated.
        /// </summary>
        private static string SplitNamePrefix(string text, out string prefix)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                @"^((\[[^\]]+\]\s*)?[^：:\n]{1,32}?[：:]\s*)(.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[3].Value))
            {
                prefix = match.Groups[1].Value;
                return match.Groups[3].Value.Trim();
            }

            prefix = string.Empty;
            return text;
        }
    }
}
