using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;

namespace ValheimMCP
{
    /// <summary>
    ///     Taps the BepInEx log stream so background (HTTP listener) threads can
    ///     block until a log line matches a predicate — e.g. waiting for another
    ///     mod's hot-reload to finish without polling. Attaches a single global
    ///     <see cref="ILogListener" /> to <c>BepInEx.Logging.Logger.Listeners</c>
    ///     in <see cref="Plugin" />.Awake; every registered waiter sees each
    ///     formatted line and is signalled on the first match.
    ///
    ///     Log events arrive on whatever thread emitted them (often, but not only,
    ///     the Unity main thread), so all shared state is guarded by <see cref="Gate" />.
    /// </summary>
    internal static class LogWatch
    {
        private sealed class Waiter
        {
            public Func<string, bool> Match;
            public Action<string> OnLine; // optional progress callback for every observed line
            public readonly ManualResetEventSlim Done = new(false);
            public volatile string MatchedLine;
        }

        private static readonly object Gate = new();
        private static readonly List<Waiter> Waiters = new();
        private static Listener _listener;

        // --- Recent-line ring buffer (history for the get_log tool). Separate from the
        // Waiters list, which only signals blocked wait_for_log callers and keeps no
        // history. Guarded by its own lock so a get_log read never contends with a
        // wait_for_log dispatch. ---
        private struct Rec
        {
            public long Seq;
            public string Line;
        }

        private static readonly object BufGate = new();
        private static Rec[] _ring;
        private static int _ringHead; // next write slot
        private static int _ringCount; // valid entries
        private static long _ringSeq; // last assigned sequence number (monotonic cursor)

        /// <summary>Attach the global log listener and allocate the history buffer. Idempotent.</summary>
        public static void Install()
        {
            if (_listener != null) return;
            lock (BufGate)
                _ring ??= new Rec[Math.Max(16, ModConfig.LogBufferCapacity)];
            _listener = new Listener();
            Logger.Listeners.Add(_listener);
        }

        /// <summary>Detach the global log listener and release any blocked waiters.</summary>
        public static void Uninstall()
        {
            if (_listener != null)
            {
                try
                {
                    Logger.Listeners.Remove(_listener);
                }
                catch
                {
                    /* ignore */
                }

                _listener.Dispose();
                _listener = null;
            }

            lock (Gate)
            {
                foreach (var w in Waiters) w.Done.Set();
                Waiters.Clear();
            }
        }

        /// <summary>
        ///     Block the calling thread until a log line satisfies <paramref name="match" />
        ///     or <paramref name="timeoutMs" /> elapses. <paramref name="onLine" />, if
        ///     given, is invoked for every observed line (matching or not) on the log
        ///     thread — keep it cheap and non-throwing. Returns the matched line, or null
        ///     on timeout (in which case <paramref name="timedOut" /> is set true).
        /// </summary>
        public static string Wait(Func<string, bool> match, int timeoutMs, Action<string> onLine, out bool timedOut)
        {
            var waiter = new Waiter { Match = match, OnLine = onLine };
            lock (Gate) Waiters.Add(waiter);
            try
            {
                timedOut = !waiter.Done.Wait(timeoutMs);
                return timedOut ? null : waiter.MatchedLine;
            }
            finally
            {
                lock (Gate) Waiters.Remove(waiter);
                waiter.Done.Dispose();
            }
        }

        // Invoked from the log-emitting thread for each event.
        private static void Dispatch(string line)
        {
            Waiter[] snapshot;
            lock (Gate)
            {
                if (Waiters.Count == 0) return;
                snapshot = Waiters.ToArray();
            }

            foreach (var w in snapshot)
            {
                if (w.Done.IsSet) continue;

                if (w.OnLine != null)
                {
                    try
                    {
                        w.OnLine(line);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"[ValheimMCP] log-watch onLine threw: {ex.Message}");
                    }
                }

                bool matched;
                try
                {
                    matched = w.Match(line);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[ValheimMCP] log-watch match threw: {ex.Message}");
                    continue;
                }

                if (matched)
                {
                    w.MatchedLine = line;
                    w.Done.Set();
                }
            }
        }

        /// <summary>Append a formatted line to the recent-line ring buffer.</summary>
        private static void Buffer(string line)
        {
            lock (BufGate)
            {
                if (_ring == null) return;
                _ringSeq++;
                _ring[_ringHead] = new Rec { Seq = _ringSeq, Line = line };
                _ringHead = (_ringHead + 1) % _ring.Length;
                if (_ringCount < _ring.Length) _ringCount++;
            }
        }

        /// <summary>
        ///     Recent log lines for the <c>get_log</c> tool. Returns up to
        ///     <paramref name="maxLines" /> of the MOST RECENT lines whose sequence is
        ///     greater than <paramref name="since" /> (pass &lt; 0 for the latest lines, or a
        ///     prior <paramref name="cursor" /> to fetch only what is new), optionally filtered
        ///     by <paramref name="filter" />. <paramref name="cursor" /> returns the current
        ///     head sequence — pass it back as <paramref name="since" /> to poll incrementally.
        ///     <paramref name="dropped" /> is how many lines were evicted from the buffer
        ///     between the caller's <paramref name="since" /> and the oldest retained line (a
        ///     gap indicator); <paramref name="totalMatching" /> is how many lines matched
        ///     before the <paramref name="maxLines" /> tail cap.
        /// </summary>
        public static List<string> GetLog(
            long since, int maxLines, Func<string, bool> filter,
            out long cursor, out int totalMatching, out int dropped)
        {
            var lines = new List<string>();
            totalMatching = 0;
            dropped = 0;
            lock (BufGate)
            {
                cursor = _ringSeq;
                if (_ring == null || _ringCount == 0) return lines;

                var oldest = _ringSeq - _ringCount + 1;
                if (since >= 0 && since + 1 < oldest) dropped = (int)(oldest - 1 - since);

                var start = (_ringHead - _ringCount + _ring.Length) % _ring.Length;
                var matched = new List<string>();
                for (var i = 0; i < _ringCount; i++)
                {
                    var rec = _ring[(start + i) % _ring.Length];
                    if (rec.Seq <= since) continue;
                    if (filter != null && !filter(rec.Line)) continue;
                    matched.Add(rec.Line);
                }

                totalMatching = matched.Count;
                var from = maxLines > 0 && matched.Count > maxLines ? matched.Count - maxLines : 0;
                for (var i = from; i < matched.Count; i++) lines.Add(matched[i]);
                return lines;
            }
        }

        private sealed class Listener : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                if (eventArgs == null) return;
                var source = eventArgs.Source?.SourceName ?? "?";
                var line = $"[{eventArgs.Level}:{source}] {eventArgs.Data}";
                Buffer(line); // history for get_log (always)
                Dispatch(line); // signal wait_for_log waiters (if any)
            }

            public void Dispose()
            {
            }
        }
    }
}