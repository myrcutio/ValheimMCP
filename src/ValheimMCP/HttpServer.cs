using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace ValheimMCP
{
    /// <summary>
    ///     Localhost HTTP endpoint that runs Valheim console commands on the main
    ///     thread and returns their captured console output as JSON.
    ///
    ///     Routes:
    ///     <list type="bullet">
    ///       <item><c>GET  /health</c>   → {ok, inGame}</item>
    ///       <item><c>GET  /commands</c> → {ok, commands:[{name,description}]}</item>
    ///       <item><c>POST /command</c>  → run a command; body is the command line
    ///             (or <c>?text=</c>). Returns {ok, ran, output:[...], error?}</item>
    ///     </list>
    /// </summary>
    internal sealed class HttpServer
    {
        private readonly HttpListener _listener = new();
        private readonly int _commandTimeoutMs;
        private Thread _thread;
        private volatile bool _running;

        public HttpServer(string prefix, int commandTimeoutMs)
        {
            _listener.Prefixes.Add(prefix);
            _commandTimeoutMs = commandTimeoutMs;
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ValheimMCP-http" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); _listener.Close(); }
            catch { /* ignore */ }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { if (!_running) return; continue; }

                try { Handle(ctx); }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[ValheimMCP] request handler threw: {ex}");
                    Write(ctx, 500, Json.Error(ex.Message));
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            var method = ctx.Request.HttpMethod;

            // MCP Streamable HTTP transport (JSON-RPC 2.0)
            //   example execution: claude mcp add --transport http valheim http://127.0.0.1:8731/mcp
            if (path == "/mcp")
            {
                if (method == "POST")
                {
                    var reply = McpServer.HandleHttp(ReadBody(ctx.Request), _commandTimeoutMs);
                    if (reply.HasBody) Write(ctx, reply.Status, reply.Body);
                    else WriteEmpty(ctx, reply.Status);
                    return;
                }

                // Stateless server: no SSE stream (GET) and no session to delete (DELETE).
                if (method == "DELETE") { WriteEmpty(ctx, 200); return; }
                Write(ctx, 405, Json.Error("MCP endpoint supports POST only (no SSE stream)"));
                return;
            }

            // We don't implement an SSE transport; advertise that explicitly.
            if (path == "/sse")
            {
                Write(ctx, 501, Json.Error("SSE transport not implemented; use POST /mcp (JSON-RPC over HTTP)"));
                return;
            }

            if (path == "" || path == "/health")
            {
                MainThreadDispatcher.RunBlocking(() => ConsoleBridge.IsReady, 2000, out var ready, out _);
                Write(ctx, 200, Json.Health(ready));
                return;
            }

            if (path == "/commands" && method == "GET")
            {
                var ok = MainThreadDispatcher.RunBlocking(
                    () => Json.Commands(ConsoleBridge.ListCommands()), 5000, out var body, out var err);
                if (!ok) { Write(ctx, 504, Json.Error("timed out listing commands (game not ticking?)")); return; }
                if (err != null) { Write(ctx, 500, Json.Error(err.Message)); return; }
                Write(ctx, 200, body);
                return;
            }

            if (path == "/command" && method == "POST")
            {
                var text = ReadCommandText(ctx.Request);
                if (string.IsNullOrWhiteSpace(text)) { Write(ctx, 400, Json.Error("missing command text (send as raw body or ?text=)")); return; }

                var ok = MainThreadDispatcher.RunBlocking(
                    () => ConsoleBridge.Run(text), _commandTimeoutMs, out var result, out var err);
                if (!ok) { Write(ctx, 504, Json.Error($"timed out after {_commandTimeoutMs}ms (game paused or command hung?)")); return; }
                if (err != null) { Write(ctx, 500, Json.Error(err.Message)); return; }
                Write(ctx, result.Ok ? 200 : 500, Json.CommandResult(text, result));
                return;
            }

            Write(ctx, 404, Json.Error($"unknown route: {method} {path}"));
        }

        // Command line comes from ?text= (query) or the raw request body.
        private static string ReadCommandText(HttpListenerRequest req)
        {
            var q = req.QueryString["text"];
            if (!string.IsNullOrEmpty(q)) return q.Trim();
            return ReadBody(req).Trim();
        }

        private static string ReadBody(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static void WriteEmpty(HttpListenerContext ctx, int status)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentLength64 = 0;
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ValheimMCP] failed to write empty response: {ex.Message}");
            }
        }

        private static void Write(HttpListenerContext ctx, int status, string json)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ValheimMCP] failed to write response: {ex.Message}");
            }
        }
    }
}
