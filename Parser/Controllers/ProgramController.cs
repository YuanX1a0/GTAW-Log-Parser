using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Globalization;
using Parser.Localization;

namespace Parser.Controllers
{
    public static class ProgramController
    {
        public const string AssemblyVersion = "5.2.0";
        public static readonly string Version = "v" + AssemblyVersion;
        public const bool IsBetaVersion = false;
        public const string ParameterPrefix = "--";
        public const string ResourceDirectory = "FiveM local NUI chat";

        private const string DevToolsTargetsUrl = "http://127.0.0.1:13172/json";
        private const string RootUiUrl = "nui://game/ui/root.html";
        private const string ClientFrameUrl = "https://cfx-nui-client/web/index.html";
        private static readonly Regex TimestampPrefix = new Regex(@"^\[(?<time>\d{1,2}:\d{2}:\d{2})\]\s+");

        /// <summary>
        /// The Mini has no configurable game directory. This method remains so
        /// the original startup flow can keep its existing entry point.
        /// </summary>
        public static void InitializeServerIp()
        {
        }

        /// <summary>
        /// Reads the chat currently visible in GTAW's FiveM HUD.
        /// Unlike the full Assistant, Mini intentionally does not retain a
        /// session or make automatic backups.
        /// </summary>
        public static string ParseChatLog(bool removeTimestamps)
        {
            try
            {
                List<string> lines = ReadVisibleChatLines();
                if (lines.Count == 0)
                    throw new IOException();

                DateTime capturedAt = DateTime.Now;
                DateTime sessionTimestamp = GetTimestamp(lines[0], capturedAt);
                string log = CreateSessionHeader(sessionTimestamp) + "\n" + string.Join("\n", lines.Select(line => AddTimestamp(line, capturedAt)));
                if (removeTimestamps)
                    log = Regex.Replace(log, @"\[\d{1,2}:\d{1,2}:\d{1,2}\] ", string.Empty);

                return log;
            }
            catch
            {
                MessageBox.Show(
                    "No FiveM GTAW chat is currently available. Open GTAW and wait for its HUD to load.",
                    Strings.Error,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return string.Empty;
            }
        }

        private static string CreateSessionHeader(DateTime timestamp)
        {
            string date = timestamp.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
            return string.Format(CultureInfo.InvariantCulture, "[DATE: {0} | TIME: {1}]", date, timestamp.ToString("HH:mm:ss"));
        }

        private static string AddTimestamp(string line, DateTime capturedAt)
        {
            if (TimestampPrefix.IsMatch(line))
                return line;

            return string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", capturedAt.ToString("HH:mm:ss"), line);
        }

        private static DateTime GetTimestamp(string line, DateTime fallback)
        {
            Match match = TimestampPrefix.Match(line);
            DateTime parsed;
            if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value, "H:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return fallback;

            return fallback.Date.Add(parsed.TimeOfDay);
        }

        private static List<string> ReadVisibleChatLines()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            IDictionary<string, object> target = GetRootTarget(serializer);
            string socketUrl = target["webSocketDebuggerUrl"] as string;
            if (string.IsNullOrWhiteSpace(socketUrl))
                throw new IOException();

            using (ClientWebSocket socket = new ClientWebSocket())
            {
                socket.Options.Proxy = null;
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    socket.ConnectAsync(new Uri(socketUrl), timeout.Token).GetAwaiter().GetResult();

                int requestId = 0;
                IDictionary<string, object> tree = Request(socket, serializer, ref requestId, "Page.getFrameTree", new Dictionary<string, object>());
                IDictionary<string, object> clientFrame = FindClientFrame(DictionaryValue(tree, "frameTree"));
                if (clientFrame == null || !clientFrame.ContainsKey("id"))
                    throw new IOException();

                IDictionary<string, object> world = Request(socket, serializer, ref requestId, "Page.createIsolatedWorld", new Dictionary<string, object>
                {
                    { "frameId", clientFrame["id"] },
                    { "worldName", "gtaw-parser-mini-reader" },
                    { "grantUniveralAccess", true }
                });
                if (!world.ContainsKey("executionContextId"))
                    throw new IOException();

                const string expression = "JSON.stringify(Array.from(document.querySelectorAll('.chat__messages > li'), el => { const text = (el.innerText || '').replace(/\\s+/g, ' ').trim(); if (!text) return ''; const nodes = [el].concat(Array.from(el.querySelectorAll('*'))); let timestamp = ''; for (const node of nodes) { for (const attribute of Array.from(node.attributes || [])) { const match = String(attribute.value).match(/\\b\\d{1,2}:\\d{2}:\\d{2}\\b/); if (match) { timestamp = match[0]; break; } } if (!timestamp) { const match = String(getComputedStyle(node, '::before').content || '').match(/\\b\\d{1,2}:\\d{2}:\\d{2}\\b/); if (match) timestamp = match[0]; } if (timestamp) break; } return (timestamp ? '[' + timestamp + '] ' : '') + text; }).filter(Boolean))";
                IDictionary<string, object> evaluation = Request(socket, serializer, ref requestId, "Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", expression },
                    { "contextId", world["executionContextId"] },
                    { "returnByValue", true }
                });

                IDictionary<string, object> runtimeResult = DictionaryValue(evaluation, "result");
                string value = runtimeResult != null && runtimeResult.ContainsKey("value") ? runtimeResult["value"] as string : "[]";
                object[] values = serializer.DeserializeObject(value ?? "[]") as object[];
                return values == null ? new List<string>() : values.OfType<string>().Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            }
        }

        private static IDictionary<string, object> GetRootTarget(JavaScriptSerializer serializer)
        {
            string json;
            using (HttpClientHandler handler = new HttpClientHandler { UseProxy = false })
            using (HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) })
                json = client.GetStringAsync(DevToolsTargetsUrl).GetAwaiter().GetResult();

            object[] targets = serializer.DeserializeObject(json) as object[];
            if (targets != null)
            {
                foreach (object item in targets)
                {
                    IDictionary<string, object> target = item as IDictionary<string, object>;
                    if (target != null && target.ContainsKey("url") && (target["url"] as string) == RootUiUrl)
                        return target;
                }
            }

            throw new IOException();
        }

        private static IDictionary<string, object> Request(ClientWebSocket socket, JavaScriptSerializer serializer, ref int requestId, string method, IDictionary<string, object> parameters)
        {
            int id = ++requestId;
            string message = serializer.Serialize(new Dictionary<string, object>
            {
                { "id", id },
                { "method", method },
                { "params", parameters }
            });
            byte[] data = Encoding.UTF8.GetBytes(message);

            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                while (true)
                {
                    IDictionary<string, object> response = Receive(socket, serializer, timeout.Token);
                    if (!response.ContainsKey("id") || Convert.ToInt32(response["id"]) != id)
                        continue;
                    if (response.ContainsKey("error"))
                        throw new IOException();
                    return DictionaryValue(response, "result") ?? new Dictionary<string, object>();
                }
            }
        }

        private static IDictionary<string, object> Receive(ClientWebSocket socket, JavaScriptSerializer serializer, CancellationToken token)
        {
            ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[8192]);
            using (MemoryStream stream = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = socket.ReceiveAsync(buffer, token).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException();
                    stream.Write(buffer.Array, buffer.Offset, result.Count);
                } while (!result.EndOfMessage);

                return serializer.DeserializeObject(Encoding.UTF8.GetString(stream.ToArray())) as IDictionary<string, object>;
            }
        }

        private static IDictionary<string, object> DictionaryValue(IDictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key))
                return null;
            return source[key] as IDictionary<string, object>;
        }

        private static IDictionary<string, object> FindClientFrame(IDictionary<string, object> frameTree)
        {
            if (frameTree == null)
                return null;

            IDictionary<string, object> frame = DictionaryValue(frameTree, "frame");
            if (frame != null && frame.ContainsKey("url") && (frame["url"] as string) == ClientFrameUrl)
                return frame;

            object childrenObject;
            if (!frameTree.TryGetValue("childFrames", out childrenObject))
                return null;

            IEnumerable children = childrenObject as IEnumerable;
            if (children == null)
                return null;

            foreach (object child in children)
            {
                IDictionary<string, object> found = FindClientFrame(child as IDictionary<string, object>);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
