using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValheimMCP
{
    /// <summary>
    ///     Transport sink for one MCP HTTP request: either a single response
    ///     (<see cref="WriteJson" /> / <see cref="WriteAccepted" />) or, when the
    ///     client accepts it, an SSE stream (<see cref="BeginSse" /> →
    ///     <see cref="SendSse" />* → <see cref="EndSse" />) carrying progress
    ///     notifications followed by the final result. Implementations must be safe
    ///     to call from multiple threads — the log and heartbeat threads emit
    ///     progress while the handler thread is blocked in the wait.
    /// </summary>
    internal interface IMcpTransport
    {
        bool AcceptsSse { get; }
        void WriteJson(int status, string body);
        void WriteAccepted();
        void BeginSse();
        void SendSse(string jsonMessage);
        void EndSse();
    }

    /// <summary>
    ///     Hand-rolled MCP (Model Context Protocol) over the Streamable HTTP
    ///     transport, JSON-RPC 2.0. Stateless — no session id. A request normally
    ///     gets a single <c>application/json</c> response and notifications get 202;
    ///     a long-running <c>wait_for_log</c> call carrying a progressToken instead
    ///     gets a <c>text/event-stream</c> response that streams
    ///     <c>notifications/progress</c> as it waits, then the final result. Zero
    ///     dependencies beyond the BCL + <see cref="MiniJson" /> / <see cref="Json" />.
    ///
    ///     Implements: initialize, ping, tools/list, tools/call. Tools dispatch to
    ///     <see cref="ConsoleBridge" /> on the Unity main thread via
    ///     <see cref="MainThreadDispatcher" />; <c>wait_for_log</c> blocks on
    ///     <see cref="LogWatch" />.
    /// </summary>
    internal static class McpServer
    {
        public const string ServerName = "valheim-mcp";
        public const string ServerVersion = PluginInfo.Version; // generated from ValheimMCP.csproj
        private const string DefaultProtocol = "2024-11-05";

        /// <summary>Parse a request body and drive <paramref name="tx" /> with the reply(ies).</summary>
        public static void Dispatch(string body, int commandTimeoutMs, IMcpTransport tx)
        {
            object parsed;
            try
            {
                parsed = MiniJson.Parse(body);
            }
            catch (Exception ex)
            {
                tx.WriteJson(200, ErrorResponse(null, -32700, "Parse error: " + ex.Message));
                return;
            }

            // JSON-RPC batch (array of messages). Batches never stream.
            if (parsed is List<object> batch)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                var any = false;
                foreach (var item in batch)
                {
                    var r = HandleOne(item as Dictionary<string, object>, commandTimeoutMs);
                    if (r == null) continue;
                    if (any) sb.Append(',');
                    sb.Append(r);
                    any = true;
                }

                sb.Append(']');
                if (any) tx.WriteJson(200, sb.ToString());
                else tx.WriteAccepted();
                return;
            }

            var req = parsed as Dictionary<string, object>;

            // A wait_for_log call carrying a progressToken streams progress over SSE
            // (when the client accepts it); everything else is a single response.
            if (tx.AcceptsSse &&
                IsWaitForLogCall(req, out var id, out var args, out var progressToken) && progressToken != null)
            {
                StreamWaitForLog(id, args, progressToken, tx);
                return;
            }

            var single = HandleOne(req, commandTimeoutMs);
            if (single == null) tx.WriteAccepted();
            else tx.WriteJson(200, single);
        }

        /// <summary>Handle one JSON-RPC message; null = notification (no response).</summary>
        private static string HandleOne(Dictionary<string, object> req, int commandTimeoutMs)
        {
            if (req == null) return ErrorResponse(null, -32600, "Invalid Request");

            var method = req.TryGetValue("method", out var m) ? m as string : null;
            var hasId = req.TryGetValue("id", out var id);
            if (method == null) return hasId ? ErrorResponse(id, -32600, "Missing method") : null;

            switch (method)
            {
                case "initialize": return Result(id, InitializeResult(req));
                case "ping": return Result(id, "{}");
                case "tools/list": return Result(id, ToolsListResult());
                case "tools/call": return ToolsCall(id, req, commandTimeoutMs);
                default:
                    // Unknown notification (no id) → ignore; unknown request → error.
                    return hasId ? ErrorResponse(id, -32601, "Method not found: " + method) : null;
            }
        }

        private static string InitializeResult(Dictionary<string, object> req)
        {
            var proto = DefaultProtocol;
            if (req.TryGetValue("params", out var p) && p is Dictionary<string, object> pd &&
                pd.TryGetValue("protocolVersion", out var pv) && pv is string s && s.Length > 0)
                proto = s; // echo the client's requested version

            var sb = new StringBuilder();
            sb.Append("{\"protocolVersion\":").Append(Json.Str(proto))
                .Append(",\"capabilities\":{\"tools\":{}}")
                .Append(",\"serverInfo\":{\"name\":").Append(Json.Str(ServerName))
                .Append(",\"version\":").Append(Json.Str(ServerVersion)).Append("}}");
            return sb.ToString();
        }

        private static string ToolsListResult()
        {
            var sb = new StringBuilder();
            sb.Append("{\"tools\":[");
            AppendTool(sb, "run_command",
                "Run a Valheim console command (e.g. 'pos' to print the player's position, or any " +
                "registered command — call list_commands to discover them) " +
                "and return the lines it printed to the in-game console.",
                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"," +
                "\"description\":\"The full console command line to execute.\"}},\"required\":[\"text\"]}");
            sb.Append(',');
            AppendTool(sb, "list_commands",
                "List all registered Valheim console commands with their descriptions.",
                "{\"type\":\"object\",\"properties\":{}}");
            sb.Append(',');
            AppendTool(sb, "health",
                "Check whether Valheim is running with a world loaded (so commands can execute).",
                "{\"type\":\"object\",\"properties\":{}}");
            sb.Append(',');
            AppendTool(sb, "render_view",
                "Render a PNG of the game world at a point, using an independent off-screen camera " +
                "(does NOT move the player's view). Returns the image inline.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"x\":{\"type\":\"number\",\"description\":\"World X of the look-at point.\"}," +
                "\"z\":{\"type\":\"number\",\"description\":\"World Z of the look-at point.\"}," +
                "\"y\":{\"type\":\"number\",\"description\":\"World Y of the look-at point. Defaults to terrain ground height; pass the feature/villager Y for interiors or elevated floors.\"}," +
                "\"yaw\":{\"type\":\"number\",\"description\":\"Camera compass azimuth in degrees (default 45).\"}," +
                "\"pitch\":{\"type\":\"number\",\"description\":\"Camera elevation above horizon in degrees: 0=level, 90=top-down (default 35).\"}," +
                "\"dist\":{\"type\":\"number\",\"description\":\"Camera distance from the look-at point in meters (default 12).\"}," +
                "\"size\":{\"type\":\"number\",\"description\":\"Square output size in pixels. Defaults to and is clamped by the mod config (render.defaultSize / minSize / maxSize).\"}" +
                "},\"required\":[\"x\",\"z\"]}");
            sb.Append(',');
            AppendTool(sb, "wait_for_log",
                "Block until a line matching 'pattern' appears in the BepInEx/Valheim log (from the MCP " +
                "server, the game, or any other mod), then return that line. Use this to wait for an " +
                "asynchronous event — e.g. a mod hot-reload completing — instead of sleeping or polling: it " +
                "returns the moment the line appears, or reports a timeout. Each log line is matched in the " +
                "form [Level:Source] message. When the request carries a progressToken, observed log lines " +
                "stream back as progress notifications while waiting.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"pattern\":{\"type\":\"string\",\"description\":\"Text to wait for, matched against each formatted log line (case-insensitive substring by default).\"}," +
                "\"regex\":{\"type\":\"boolean\",\"description\":\"Treat 'pattern' as a .NET regular expression instead of a substring (default false).\"}," +
                "\"timeoutMs\":{\"type\":\"number\",\"description\":\"Maximum time to wait in milliseconds. Clamped to the server's configured maximum (default 120000).\"}" +
                "},\"required\":[\"pattern\"]}");
            sb.Append(',');
            AppendTool(sb, "get_log",
                "Fetch recent lines from the in-memory BepInEx/Valheim log buffer (the MCP server, the " +
                "game, and every other mod) — i.e. tail the log on demand, including on a dedicated " +
                "server whose log file isn't directly accessible. Omit 'since' to get the most recent " +
                "lines; pass the 'cursor' value from a previous call as 'since' to get only what's new " +
                "since then (incremental polling). Optionally filter with 'contains' (case-insensitive " +
                "substring) or 'regex'. The first output line is a header: 'cursor=<n> returned=<k> " +
                "matching=<t> dropped=<d>' — pass that cursor back as 'since' next time; dropped>0 means " +
                "older lines were evicted from the buffer between calls (a gap). Unlike wait_for_log " +
                "(which blocks for a pattern), this returns immediately with whatever is buffered.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"since\":{\"type\":\"number\",\"description\":\"Return only lines after this cursor (from a prior get_log call). Omit for the most recent lines.\"}," +
                "\"maxLines\":{\"type\":\"number\",\"description\":\"Max lines to return (default 200, clamped to the server's configured maximum).\"}," +
                "\"contains\":{\"type\":\"string\",\"description\":\"Only return lines containing this text (case-insensitive substring).\"}," +
                "\"regex\":{\"type\":\"boolean\",\"description\":\"Treat 'contains' as a .NET regular expression instead of a substring (default false).\"}" +
                "}}");
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendTool(StringBuilder sb, string name, string description, string schemaJson)
        {
            sb.Append("{\"name\":").Append(Json.Str(name))
                .Append(",\"description\":").Append(Json.Str(description))
                .Append(",\"inputSchema\":").Append(schemaJson).Append('}');
        }

        private static string ToolsCall(object id, Dictionary<string, object> req, int commandTimeoutMs)
        {
            if (!(req.TryGetValue("params", out var p) && p is Dictionary<string, object> pd))
                return ErrorResponse(id, -32602, "Invalid params");

            var name = pd.TryGetValue("name", out var n) ? n as string : null;
            var args = pd.TryGetValue("arguments", out var a) ? a as Dictionary<string, object> : null;
            if (string.IsNullOrEmpty(name)) return ErrorResponse(id, -32602, "Missing tool name");

            switch (name)
            {
                case "health":
                {
                    MainThreadDispatcher.RunBlocking(() => ConsoleBridge.IsReady, 2000, out var ready, out _);
                    return Result(id, ToolText(Json.Health(ready), false));
                }
                case "list_commands":
                {
                    var ok = MainThreadDispatcher.RunBlocking(
                        () => Json.Commands(ConsoleBridge.ListCommands()), 5000, out var listBody, out var err);
                    if (!ok) return Result(id, ToolText("timed out listing commands (game not ticking?)", true));
                    if (err != null) return Result(id, ToolText(err.Message, true));
                    return Result(id, ToolText(listBody, false));
                }
                case "run_command":
                {
                    var text = args != null && args.TryGetValue("text", out var t) ? t as string : null;
                    if (string.IsNullOrWhiteSpace(text)) return Result(id, ToolText("missing 'text' argument", true));

                    var ok = MainThreadDispatcher.RunBlocking(
                        () => ConsoleBridge.Run(text), commandTimeoutMs, out var result, out var err);
                    if (!ok)
                        return Result(id,
                            ToolText($"timed out after {commandTimeoutMs}ms (game paused or command hung?)", true));
                    if (err != null) return Result(id, ToolText(err.Message, true));

                    var joined = result.Output.Count > 0 ? string.Join("\n", result.Output) : "(no console output)";
                    if (!result.Ok)
                        joined = (result.Error ?? "command failed") +
                                 (result.Output.Count > 0 ? "\n" + string.Join("\n", result.Output) : "");
                    return Result(id, ToolText(joined, !result.Ok));
                }
                case "render_view":
                {
                    if (args == null ||
                        !(args.TryGetValue("x", out var xo) && xo is double xd) ||
                        !(args.TryGetValue("z", out var zo) && zo is double zd))
                        return Result(id, ToolText("render_view requires numeric 'x' and 'z'", true));

                    var x = (float)xd;
                    var z = (float)zd;
                    float? y = args.TryGetValue("y", out var yo) && yo is double yv ? (float)yv : null;
                    var yaw = (float)Num(args, "yaw", 45);
                    var pitch = (float)Num(args, "pitch", 35);
                    var dist = (float)Num(args, "dist", 12);
                    var size = (int)Num(args, "size", ModConfig.RenderDefaultSize);

                    var ok = MainThreadDispatcher.RunBlocking(
                        () => CameraRenderer.Render(x, z, y, yaw, pitch, dist, size), 20000, out var rr, out var err);
                    if (!ok) return Result(id, ToolText("render timed out (game not ticking?)", true));
                    if (err != null) return Result(id, ToolText("render threw: " + err.Message, true));
                    if (rr?.Png == null)
                        return Result(id, ToolText("render failed: " + (rr?.Error ?? "unknown"), true));
                    return Result(id, ToolImage(Convert.ToBase64String(rr.Png), "image/png"));
                }
                case "wait_for_log":
                    return Result(id, DoWaitForLog(args, null));
                case "get_log":
                    return Result(id, DoGetLog(args));
                default:
                    return ErrorResponse(id, -32602, "Unknown tool: " + name);
            }
        }

        private static double Num(Dictionary<string, object> args, string key, double dflt)
        {
            return args != null && args.TryGetValue(key, out var v) && v is double d ? d : dflt;
        }

        // ---- wait_for_log ----

        // True when req is a tools/call for wait_for_log that carries an id (needed to
        // reply on the stream). Also extracts the arguments and params._meta.progressToken.
        private static bool IsWaitForLogCall(
            Dictionary<string, object> req, out object id, out Dictionary<string, object> args,
            out object progressToken)
        {
            id = null;
            args = null;
            progressToken = null;
            if (req == null) return false;
            if (!(req.TryGetValue("method", out var m) && m as string == "tools/call")) return false;
            if (!req.TryGetValue("id", out id)) return false;
            if (!(req.TryGetValue("params", out var p) && p is Dictionary<string, object> pd)) return false;
            if ((pd.TryGetValue("name", out var n) ? n as string : null) != "wait_for_log") return false;

            args = pd.TryGetValue("arguments", out var a) ? a as Dictionary<string, object> : null;
            if (pd.TryGetValue("_meta", out var meta) && meta is Dictionary<string, object> md &&
                md.TryGetValue("progressToken", out var tok))
                progressToken = tok;
            return true;
        }

        // Blocks until a log line matches, returning a tool-result payload. onLine, if
        // given, is invoked for every observed line while waiting (used for SSE progress).
        private static string DoWaitForLog(Dictionary<string, object> args, Action<string> onLine)
        {
            var pattern = args != null && args.TryGetValue("pattern", out var po) ? po as string : null;
            if (string.IsNullOrEmpty(pattern)) return ToolText("missing 'pattern' argument", true);

            var isRegex = args.TryGetValue("regex", out var ro) && ro is bool rb && rb;
            var timeoutMs = ModConfig.ClampWaitTimeout((int)Num(args, "timeoutMs", ModConfig.WaitDefaultTimeoutMs));

            Func<string, bool> predicate;
            if (isRegex)
            {
                Regex rx;
                try
                {
                    rx = new Regex(pattern);
                }
                catch (Exception ex)
                {
                    return ToolText("invalid regex: " + ex.Message, true);
                }

                predicate = line => rx.IsMatch(line);
            }
            else
            {
                predicate = line => line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var matched = LogWatch.Wait(predicate, timeoutMs, onLine, out var timedOut);
            if (timedOut) return ToolText($"timed out after {timeoutMs}ms waiting for log match: {pattern}", true);
            return ToolText("matched: " + matched, false);
        }

        // ---- get_log ----

        // Returns a tool-result payload: a header line with the cursor/counts, then the
        // matching log lines. Reads the LogWatch ring buffer directly (no main thread).
        private static string DoGetLog(Dictionary<string, object> args)
        {
            var since = (long)Num(args, "since", -1);
            var maxLines = ModConfig.ClampLogLines((int)Num(args, "maxLines", ModConfig.LogDefaultLines));
            var contains = args != null && args.TryGetValue("contains", out var co) ? co as string : null;
            var isRegex = args != null && args.TryGetValue("regex", out var ro) && ro is bool rb && rb;

            Func<string, bool> filter = null;
            if (!string.IsNullOrEmpty(contains))
            {
                if (isRegex)
                {
                    Regex rx;
                    try
                    {
                        rx = new Regex(contains);
                    }
                    catch (Exception ex)
                    {
                        return ToolText("invalid regex: " + ex.Message, true);
                    }

                    filter = line => rx.IsMatch(line);
                }
                else
                {
                    var needle = contains;
                    filter = line => line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            var lines = LogWatch.GetLog(since, maxLines, filter, out var cursor, out var matching, out var dropped);
            var sb = new StringBuilder();
            sb.Append("cursor=").Append(cursor.ToString(CultureInfo.InvariantCulture))
                .Append(" returned=").Append(lines.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" matching=").Append(matching.ToString(CultureInfo.InvariantCulture))
                .Append(" dropped=").Append(dropped.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            sb.Append(lines.Count > 0 ? string.Join("\n", lines) : "(no matching log lines)");
            return ToolText(sb.ToString(), false);
        }

        // SSE variant: stream progress (each observed line, plus periodic heartbeats so
        // the client's request timeout keeps resetting) while blocked, then the final
        // tool result, then close the stream.
        private static void StreamWaitForLog(
            object id, Dictionary<string, object> args, object progressToken, IMcpTransport tx)
        {
            tx.BeginSse();
            var counter = 0;

            void Emit(string message)
            {
                var n = Interlocked.Increment(ref counter);
                tx.SendSse(ProgressNotification(progressToken, n, message));
            }

            Timer heartbeat = null;
            try
            {
                heartbeat = new Timer(
                    _ =>
                    {
                        try
                        {
                            Emit("waiting…");
                        }
                        catch
                        {
                            /* transport closed */
                        }
                    }, null, ModConfig.WaitHeartbeatMs, ModConfig.WaitHeartbeatMs);

                var resultText = DoWaitForLog(args, line =>
                {
                    try
                    {
                        Emit(line);
                    }
                    catch
                    {
                        /* transport closed */
                    }
                });

                heartbeat.Dispose();
                heartbeat = null;
                tx.SendSse(Result(id, resultText));
            }
            catch (Exception ex)
            {
                tx.SendSse(Result(id, ToolText("wait_for_log threw: " + ex.Message, true)));
            }
            finally
            {
                heartbeat?.Dispose();
                tx.EndSse();
            }
        }

        // A notifications/progress message (a JSON-RPC notification — no id).
        private static string ProgressNotification(object progressToken, int progress, string message)
        {
            var sb = new StringBuilder();
            sb.Append("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{");
            sb.Append("\"progressToken\":").Append(FormatId(progressToken));
            sb.Append(",\"progress\":").Append(progress.ToString(CultureInfo.InvariantCulture));
            if (message != null) sb.Append(",\"message\":").Append(Json.Str(message));
            sb.Append("}}");
            return sb.ToString();
        }

        // ---- JSON-RPC envelope helpers ----

        private static string ToolText(string text, bool isError)
        {
            return "{\"content\":[{\"type\":\"text\",\"text\":" + Json.Str(text) + "}],\"isError\":" +
                   (isError ? "true" : "false") + "}";
        }

        private static string ToolImage(string base64, string mimeType)
        {
            return "{\"content\":[{\"type\":\"image\",\"data\":" + Json.Str(base64) +
                   ",\"mimeType\":" + Json.Str(mimeType) + "}],\"isError\":false}";
        }

        private static string Result(object id, string resultJson)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + FormatId(id) + ",\"result\":" + resultJson + "}";
        }

        private static string ErrorResponse(object id, int code, string message)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + FormatId(id) +
                   ",\"error\":{\"code\":" + code.ToString(CultureInfo.InvariantCulture) +
                   ",\"message\":" + Json.Str(message) + "}}";
        }

        // Echo the request id with its original JSON type (int vs string).
        private static string FormatId(object id)
        {
            if (id == null) return "null";
            if (id is string s) return Json.Str(s);
            if (id is bool b) return b ? "true" : "false";
            if (id is double d)
            {
                if (!double.IsInfinity(d) && !double.IsNaN(d) && Math.Abs(d - Math.Floor(d)) < double.Epsilon &&
                    Math.Abs(d) < 9.2e18)
                    return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }

            return Json.Str(id.ToString());
        }
    }
}