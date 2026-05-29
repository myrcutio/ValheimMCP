using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ValheimMCP
{
    /// <summary>
    ///     Marshals work from the HTTP listener's background thread onto Unity's
    ///     main thread, where Valheim/Unity APIs are safe to touch. The queue is
    ///     drained once per frame from <see cref="Plugin" />'s Update.
    /// </summary>
    internal static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        /// <summary>
        ///     Run <paramref name="func" /> on the main thread and block the calling
        ///     (background) thread until it completes or <paramref name="timeoutMs" />
        ///     elapses. Returns false on timeout (e.g. game paused / not ticking).
        /// </summary>
        public static bool RunBlocking<T>(Func<T> func, int timeoutMs, out T result, out Exception error)
        {
            T captured = default;
            Exception err = null;
            using (var done = new ManualResetEventSlim(false))
            {
                Queue.Enqueue(() =>
                {
                    try { captured = func(); }
                    catch (Exception ex) { err = ex; }
                    finally { done.Set(); }
                });

                var completed = done.Wait(timeoutMs);
                result = captured;
                error = err;
                return completed;
            }
        }

        /// <summary>Drain queued actions. MUST be called from the main thread.</summary>
        public static void Pump()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[ValheimMCP] queued main-thread action threw: {ex}");
                }
            }
        }
    }
}
