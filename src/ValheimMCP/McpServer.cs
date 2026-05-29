using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimMCP
{
    /// <summary>What to send back over HTTP for one MCP message (or batch).</summary>
    internal struct McpHttpReply
    {
        public int Status;
        public string Body;
        public bool HasBody;
    }

    /// <summary>
    ///     Hand-rolled MCP (Model Context Protocol) over the Streamable HTTP
    ///     transport, JSON-RPC 2.0. Stateless — no session id, no SSE: requests get
    ///     a single <c>application/json</c> response, notifications get 202. Zero
    ///     dependencies beyond the BCL + <see cref="MiniJson" /> / <see cref="Json" />.
    ///
    ///     Implements: initialize, ping, tools/list, tools/call. Tools dispatch to
    ///     <see cref="ConsoleBridge" /> on the Unity main thread via
    ///     <see cref="MainThreadDispatcher" />.
    /// </summary>
    internal static class McpServer
    {
        public const string ServerName = "valheim-mcp";
        public const string ServerVersion = PluginInfo.Version; // generated from ValheimMCP.csproj
        private const string DefaultProtocol = "2024-11-05";

        /// <summary>Parse a request body and produce the HTTP reply (status + JSON).</summary>
        public static McpHttpReply HandleHttp(string body, int commandTimeoutMs)
        {
            object parsed;
            try { parsed = MiniJson.Parse(body); }
            catch (Exception ex) { return Json200(ErrorResponse(null, -32700, "Parse error: " + ex.Message)); }

            // JSON-RPC batch (array of messages).
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
                return any ? Json200(sb.ToString()) : Accepted();
            }

            var single = HandleOne(parsed as Dictionary<string, object>, commandTimeoutMs);
            return single == null ? Accepted() : Json200(single);
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
                    if (!ok) return Result(id, ToolText($"timed out after {commandTimeoutMs}ms (game paused or command hung?)", true));
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
                    if (rr?.Png == null) return Result(id, ToolText("render failed: " + (rr?.Error ?? "unknown"), true));
                    return Result(id, ToolImage(Convert.ToBase64String(rr.Png), "image/png"));
                }
                default:
                    return ErrorResponse(id, -32602, "Unknown tool: " + name);
            }
        }

        private static double Num(Dictionary<string, object> args, string key, double dflt)
        {
            return args != null && args.TryGetValue(key, out var v) && v is double d ? d : dflt;
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
                if (!double.IsInfinity(d) && !double.IsNaN(d) && d == Math.Floor(d) && Math.Abs(d) < 9.2e18)
                    return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }

            return Json.Str(id.ToString());
        }

        private static McpHttpReply Json200(string body)
        {
            return new McpHttpReply { Status = 200, Body = body, HasBody = true };
        }

        private static McpHttpReply Accepted()
        {
            return new McpHttpReply { Status = 202, Body = null, HasBody = false };
        }
    }
}
