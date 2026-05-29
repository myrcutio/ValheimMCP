using System.Collections.Generic;
using HarmonyLib;

namespace ValheimMCP
{
    /// <summary>
    ///     Tees lines printed to the in-game console into a capture buffer while a
    ///     command runs, via a Harmony postfix on <c>Terminal.AddString(string)</c>.
    ///     Preferred over reflecting Terminal's private buffer: it rides the same
    ///     public sink every <c>Console.Print</c> / <c>ConsoleEventArgs.Context.AddString</c>
    ///     call funnels through.
    ///
    ///     All access is on the Unity main thread (commands are run via
    ///     <see cref="MainThreadDispatcher" />), so no locking is needed.
    /// </summary>
    [HarmonyPatch]
    internal static class ConsoleOutputCapture
    {
        private static List<string> _sink;

        /// <summary>Start capturing. Returns the buffer that will collect output.</summary>
        public static void Begin()
        {
            _sink = new List<string>();
        }

        /// <summary>Stop capturing and return the collected lines.</summary>
        public static List<string> End()
        {
            var collected = _sink ?? new List<string>();
            _sink = null;
            return collected;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), typeof(string))]
        private static void Terminal_AddString_Postfix(string text)
        {
            _sink?.Add(text);
        }
    }
}
