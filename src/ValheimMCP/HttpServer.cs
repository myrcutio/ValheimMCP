using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
    ///       <item><c>GET  /log</c>      → recent log lines from the ring buffer.
    ///             Query: <c>?since=</c> (cursor), <c>?maxLines=</c>, <c>?contains=</c>,
    ///             <c>?regex=true</c>. Returns {ok, cursor, matching, dropped, lines:[...]}</item>
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
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                /* ignore */
            }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_running) return;
                    continue;
                }

                // Handle each request on a pool thread so the accept loop keeps serving
                // while a long-blocking request (e.g. wait_for_log) is in flight.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Handle(ctx);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogError($"[ValheimMCP] request handler threw: {ex}");
                        try
                        {
                            Write(ctx, 500, Json.Error(ex.Message));
                        }
                        catch
                        {
                            /* response may already be partially sent */
                        }
                    }
                });
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
                    // The transport replies with a single application/json body or, for a
                    // wait_for_log call carrying a progressToken, a text/event-stream of
                    // progress notifications followed by the final result.
                    McpServer.Dispatch(ReadBody(ctx.Request), _commandTimeoutMs, new HttpMcpTransport(ctx));
                    return;
                }

                // Stateless server: no session to delete. SSE is emitted only as a POST
                // response, never via a server-initiated GET stream.
                if (method == "DELETE")
                {
                    WriteEmpty(ctx, 200);
                    return;
                }

                Write(ctx, 405,
                    Json.Error("MCP endpoint supports POST (and DELETE); server-initiated GET stream not implemented"));
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
                if (!ok)
                {
                    Write(ctx, 504, Json.Error("timed out listing commands (game not ticking?)"));
                    return;
                }

                if (err != null)
                {
                    Write(ctx, 500, Json.Error(err.Message));
                    return;
                }

                Write(ctx, 200, body);
                return;
            }

            if (path == "/command" && method == "POST")
            {
                var text = ReadCommandText(ctx.Request);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Write(ctx, 400, Json.Error("missing command text (send as raw body or ?text=)"));
                    return;
                }

                var ok = MainThreadDispatcher.RunBlocking(
                    () => ConsoleBridge.Run(text), _commandTimeoutMs, out var result, out var err);
                if (!ok)
                {
                    Write(ctx, 504,
                        Json.Error($"timed out after {_commandTimeoutMs}ms (game paused or command hung?)"));
                    return;
                }

                if (err != null)
                {
                    Write(ctx, 500, Json.Error(err.Message));
                    return;
                }

                Write(ctx, result.Ok ? 200 : 500, Json.CommandResult(text, result));
                return;
            }

            if (path == "/log" && method == "GET")
            {
                var q = ctx.Request.QueryString;
                var since = long.TryParse(q["since"], out var sv) ? sv : -1L;
                int.TryParse(q["maxLines"], out var mlRaw);
                var maxLines = ModConfig.ClampLogLines(mlRaw);
                var contains = q["contains"];
                var isRegex = q["regex"] == "1" ||
                              string.Equals(q["regex"], "true", StringComparison.OrdinalIgnoreCase);

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
                            Write(ctx, 400, Json.Error("invalid regex: " + ex.Message));
                            return;
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
                Write(ctx, 200, Json.LogResult(cursor, matching, dropped, lines));
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

        /// <summary>
        ///     <see cref="IMcpTransport" /> over one <see cref="HttpListenerContext" />.
        ///     Writes either a single JSON/empty response or, after <see cref="BeginSse" />,
        ///     a <c>text/event-stream</c>. All writes are serialized under a lock and gated
        ///     by a closed flag, so the log/heartbeat threads and the handler thread can't
        ///     trample each other or write past a client disconnect.
        /// </summary>
        private sealed class HttpMcpTransport : IMcpTransport
        {
            private readonly HttpListenerContext _ctx;
            private readonly object _lock = new();
            private bool _sse;
            private bool _closed;

            public HttpMcpTransport(HttpListenerContext ctx) => _ctx = ctx;

            public bool AcceptsSse
            {
                get
                {
                    var accept = _ctx.Request.Headers["Accept"];
                    return accept != null &&
                           accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            public void WriteJson(int status, string body)
            {
                lock (_lock)
                {
                    if (_closed) return;
                    _closed = true;
                    Write(_ctx, status, body);
                }
            }

            public void WriteAccepted()
            {
                lock (_lock)
                {
                    if (_closed) return;
                    _closed = true;
                    WriteEmpty(_ctx, 202);
                }
            }

            public void BeginSse()
            {
                lock (_lock)
                {
                    if (_sse || _closed) return;
                    _sse = true;
                    _ctx.Response.StatusCode = 200;
                    _ctx.Response.ContentType = "text/event-stream";
                    _ctx.Response.Headers["Cache-Control"] = "no-cache";
                    _ctx.Response.SendChunked = true;
                }
            }

            public void SendSse(string jsonMessage)
            {
                lock (_lock)
                {
                    if (!_sse || _closed) return;
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes("data: " + jsonMessage + "\n\n");
                        _ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        _ctx.Response.OutputStream.Flush();
                    }
                    catch (Exception ex)
                    {
                        _closed = true; // client likely disconnected; stop writing
                        Plugin.Log?.LogWarning($"[ValheimMCP] SSE write failed (client gone?): {ex.Message}");
                    }
                }
            }

            public void EndSse()
            {
                lock (_lock)
                {
                    if (!_sse || _closed) return;
                    _closed = true;
                    try
                    {
                        _ctx.Response.OutputStream.Close();
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
        }
    }
}