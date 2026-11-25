using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Assistant.Controllers
{
    /// <summary>
    /// Watches the project's GitHub release feed and reports newer published
    /// versions. The check runs on a background thread so a slow or blocked
    /// network never delays startup, and every failure is ignored.
    ///
    /// The releases.atom feed is used instead of the REST API on purpose: the
    /// API is rate limited (shared IPs are frequently rejected with 403)
    /// while the public feed needs no token and has no quota.
    /// </summary>
    public static class UpdateController
    {
        private const string ReleasesAtom = "https://github.com/YuanX1a0/GTAW-Log-Parser/releases.atom";
        public const string ReleasesPage = "https://github.com/YuanX1a0/GTAW-Log-Parser/releases";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        private static readonly Regex EntryPattern = new Regex(@"<entry>(?<body>.*?)</entry>", RegexOptions.Singleline);
        private static readonly Regex TitlePattern = new Regex(@"<title[^>]*>(?<value>[^<]*)</title>", RegexOptions.Singleline);
        private static readonly Regex LinkPattern = new Regex(@"href=""(?<value>[^""]*?/releases/tag/[^""]+)""", RegexOptions.IgnoreCase);
        private static readonly Regex PreReleasePattern = new Regex(@"(alpha|beta|preview|pre-?release|rc\d*)", RegexOptions.IgnoreCase);

        private static readonly string StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTAW-Log-Parser");
        private static readonly string StatePath = Path.Combine(StateDirectory, "update-state.json");

        /// <summary>
        /// A published GitHub release newer than the running build.
        /// </summary>
        public sealed class UpdateInfo
        {
            public string Version;
            public string Tag;
            public string Name;
            public string Url;
        }

        /// <summary>
        /// Starts the periodic check. The callback runs on the UI thread and is
        /// invoked every time a newer release is found, so a long-running
        /// session notices a release published later.
        /// </summary>
        public static void StartWatching(Action<UpdateInfo> onUpdate)
        {
            Thread thread = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        UpdateInfo info = FetchLatest();
                        if (info == null)
                        {
                            TranslationController.LogEvent("app", "检查更新失败：无法获取发行版信息");
                        }
                        else if (CompareVersions(info.Version, AppController.AssemblyVersion) > 0)
                        {
                            TranslationController.LogEvent("app", "检查更新：发现新版本 " + info.Version + "（当前 " + AppController.AssemblyVersion + "）");
                            if (onUpdate != null)
                            {
                                System.Windows.Application app = System.Windows.Application.Current;
                                if (app != null)
                                    app.Dispatcher.BeginInvoke(new Action(delegate { onUpdate(info); }));
                            }
                        }
                        else
                        {
                            TranslationController.LogEvent("app", "检查更新：已是最新版本 " + info.Version);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Offline or GitHub unreachable - retry next cycle.
                        TranslationController.LogEvent("app", "检查更新异常：" + ex.Message);
                    }

                    Thread.Sleep(CheckInterval);
                }
            })
            {
                IsBackground = true,
                Name = "Update check"
            };
            thread.Start();
        }

        private static UpdateInfo FetchLatest()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ReleasesAtom);
            request.Method = "GET";
            request.UserAgent = "Mozilla/5.0 (GTAWorldChatAssistant/" + AppController.AssemblyVersion + ")";
            request.Accept = "application/atom+xml,application/xml;q=0.9,*/*;q=0.8";
            request.Timeout = 10000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string feed = reader.ReadToEnd();
                foreach (Match entry in EntryPattern.Matches(feed))
                {
                    string body = entry.Groups["body"].Value;
                    Match titleMatch = TitlePattern.Match(body);
                    Match linkMatch = LinkPattern.Match(body);
                    if (!titleMatch.Success || !linkMatch.Success)
                        continue;

                    string tag = WebUtility.HtmlDecode(titleMatch.Groups["value"].Value).Trim();
                    if (string.IsNullOrWhiteSpace(tag) || PreReleasePattern.IsMatch(tag))
                        continue;

                    string version = NormalizeVersion(tag);
                    Version parsed;
                    if (!Version.TryParse(PadVersion(version), out parsed))
                        continue;

                    return new UpdateInfo
                    {
                        Tag = tag,
                        Version = version,
                        Name = tag,
                        Url = WebUtility.HtmlDecode(linkMatch.Groups["value"].Value)
                    };
                }

                return null;
            }
        }

        /// <summary>
        /// Turns a release tag such as "v6.4.0" or "v6.4.0-beta.1" into the
        /// numeric part used for comparison.
        /// </summary>
        private static string NormalizeVersion(string tag)
        {
            string value = (tag ?? string.Empty).Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);

            int cut = value.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0)
                value = value.Substring(0, cut);

            return value;
        }

        /// <summary>
        /// Compares two version strings numerically. Returns 1 when left is
        /// newer, -1 when older and 0 when they are equal or unparsable.
        /// </summary>
        public static int CompareVersions(string left, string right)
        {
            Version a;
            Version b;
            if (!Version.TryParse(PadVersion(left), out a))
                return 0;
            if (!Version.TryParse(PadVersion(right), out b))
                return 0;
            return a.CompareTo(b);
        }

        private static string PadVersion(string value)
        {
            string text = NormalizeVersion(value);
            string[] parts = text.Split('.');
            if (parts.Length == 1)
                return text + ".0.0";
            if (parts.Length == 2)
                return text + ".0";
            return text;
        }

        /// <summary>
        /// True when this release has not been announced to the player yet.
        /// </summary>
        public static bool ShouldNotify(string version)
        {
            try
            {
                if (!File.Exists(StatePath))
                    return true;

                string json = File.ReadAllText(StatePath, Encoding.UTF8);
                IDictionary<string, object> state = new JavaScriptSerializer()
                    .DeserializeObject(json) as IDictionary<string, object>;
                if (state == null || !state.ContainsKey("notified"))
                    return true;

                return !string.Equals(state["notified"] as string, version, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Remembers the release that was already announced so the popup does
        /// not appear on every launch.
        /// </summary>
        public static void MarkNotified(string version)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);
                Dictionary<string, object> state = new Dictionary<string, object>
                {
                    { "notified", version }
                };
                File.WriteAllText(StatePath, new JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
            }
            catch
            {
                // Losing the notification state only means one extra popup later.
            }
        }

        /// <summary>
        /// Opens the release page in the default browser.
        /// </summary>
        public static void OpenReleasePage(string url)
        {
            try
            {
                string target = string.IsNullOrWhiteSpace(url) ? ReleasesPage : url;
                if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    target = ReleasesPage;

                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch
            {
                // No default browser configured - nothing else to do.
            }
        }
    }
}
