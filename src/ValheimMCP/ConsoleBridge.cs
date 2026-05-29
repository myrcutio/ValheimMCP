using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ValheimMCP
{
    /// <summary>Result of running a console command, including captured output.</summary>
    internal sealed class CommandResult
    {
        public bool Ok;
        public string Error;
        public List<string> Output = new();
    }

    /// <summary>
    ///     Thin wrapper over Valheim's <c>Terminal</c>/<c>Console</c>. Drives the
    ///     in-game console so any registered console command (including other mods'
    ///     <c>vv_*</c> commands) can be triggered remotely, capturing whatever the
    ///     command prints. No dependency on those mods.
    ///
    ///     Every method here MUST be called on the main thread.
    /// </summary>
    internal static class ConsoleBridge
    {
        // Terminal.commands is a non-public static dictionary; reach it reflectively.
        private static readonly FieldInfo CommandsField =
            typeof(Terminal).GetField("commands",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>True when an in-game console exists to run commands against.</summary>
        public static bool IsReady => Console.instance != null;

        /// <summary>(name, description) for every registered console command.</summary>
        public static List<KeyValuePair<string, string>> ListCommands()
        {
            var result = new List<KeyValuePair<string, string>>();
            if (CommandsField?.GetValue(null) is IDictionary dict)
                foreach (DictionaryEntry entry in dict)
                {
                    var name = entry.Key as string;
                    var description = (entry.Value as Terminal.ConsoleCommand)?.Description ?? "";
                    if (name != null)
                        result.Add(new KeyValuePair<string, string>(name, description));
                }

            return result.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        ///     Run a console command line (e.g. <c>vv_probe 100 -50</c>) and return
        ///     the lines it printed to the console. Captures synchronous output only;
        ///     commands that print from a coroutine will emit later lines we don't see.
        /// </summary>
        public static CommandResult Run(string commandLine)
        {
            if (!ModConfig.IsCommandAllowed(commandLine, out var denyReason))
                return new CommandResult { Ok = false, Error = denyReason };

            var console = Console.instance;
            if (console == null)
                return new CommandResult { Ok = false, Error = "Console.instance is null (no game loaded yet)" };

            ConsoleOutputCapture.Begin();
            try
            {
                // skipAllowedCheck: true so non-cheat-gated mod commands always run.
                console.TryRunCommand(commandLine, false, true);
            }
            catch (Exception ex)
            {
                return new CommandResult { Ok = false, Error = "command threw: " + ex.Message, Output = ConsoleOutputCapture.End() };
            }

            return new CommandResult { Ok = true, Output = ConsoleOutputCapture.End() };
        }
    }
}
