using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace ValheimMCP
{
    /// <summary>
    ///     Loads <c>valheimmcp.yml</c> from the BepInEx config directory (writing a
    ///     commented default if absent) and exposes the settings. Hand-parsed via
    ///     <see cref="MiniYaml" /> — no config-library dependency.
    /// </summary>
    internal static class ModConfig
    {
        public const string FileName = "valheimmcp.yml";

        public static string Host = "127.0.0.1";
        public static int Port = 8731;
        public static int CommandTimeoutMs = 15000;

        public static int RenderDefaultSize = 768;
        public static int RenderMinSize = 256;
        public static int RenderMaxSize = 1280;

        private static List<string> _allow = new();
        private static List<string> _deny = new();

        public static void Load()
        {
            try
            {
                var path = Path.Combine(Paths.ConfigPath, FileName);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, DefaultYaml);
                    Plugin.Log?.LogInfo($"[ValheimMCP] wrote default config: {path}");
                }

                var y = MiniYaml.Parse(File.ReadAllText(path));
                Host = y.Get("server.host", Host);
                Port = y.GetInt("server.port", Port);
                CommandTimeoutMs = y.GetInt("server.commandTimeoutMs", CommandTimeoutMs);
                RenderDefaultSize = y.GetInt("render.defaultSize", RenderDefaultSize);
                RenderMinSize = y.GetInt("render.minSize", RenderMinSize);
                RenderMaxSize = y.GetInt("render.maxSize", RenderMaxSize);
                _allow = y.GetList("commands.allow");
                _deny = y.GetList("commands.deny");

                Plugin.Log?.LogInfo(
                    $"[ValheimMCP] config: {Host}:{Port}, render {RenderMinSize}-{RenderMaxSize} " +
                    $"(default {RenderDefaultSize}), allow={_allow.Count} deny={_deny.Count}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ValheimMCP] failed to load config, using defaults: {ex.Message}");
            }
        }

        /// <summary>Clamp a requested render size into the configured range (0 = use default).</summary>
        public static int ClampRenderSize(int requested)
        {
            if (requested <= 0) requested = RenderDefaultSize;
            var min = Math.Max(64, RenderMinSize);
            var max = Math.Max(min, RenderMaxSize);
            return Math.Min(max, Math.Max(min, requested));
        }

        /// <summary>
        ///     Gate a console command line against the allow/deny lists. Matches the
        ///     command name (first token, case-insensitive); a trailing <c>*</c> is a
        ///     prefix wildcard. Deny wins; a non-empty allow list is exclusive.
        /// </summary>
        public static bool IsCommandAllowed(string commandLine, out string reason)
        {
            reason = null;
            var name = (commandLine ?? "").Trim();
            var sp = name.IndexOfAny(new[] { ' ', '\t' });
            if (sp >= 0) name = name.Substring(0, sp);
            name = name.ToLowerInvariant();

            if (Matches(_deny, name))
            {
                reason = $"command '{name}' is denied by config (commands.deny)";
                return false;
            }

            if (_allow.Count > 0 && !Matches(_allow, name))
            {
                reason = $"command '{name}' is not in the config allowlist (commands.allow)";
                return false;
            }

            return true;
        }

        private static bool Matches(List<string> patterns, string name)
        {
            foreach (var raw in patterns)
            {
                var p = raw.ToLowerInvariant();
                if (p.EndsWith("*"))
                {
                    if (name.StartsWith(p.Substring(0, p.Length - 1))) return true;
                }
                else if (name == p)
                {
                    return true;
                }
            }

            return false;
        }

        private const string DefaultYaml =
            @"# ValheimMCP configuration (YAML). Full-line comments (#) only.

server:
  host: 127.0.0.1          # loopback only — endpoint is unauthenticated, keep it local
  port: 8731
  commandTimeoutMs: 15000  # max wait for a command to run on the main thread

render:
  defaultSize: 768         # render_view size (px, square) when 'size' is omitted
  minSize: 256
  maxSize: 1280

# Access control for run_command (and POST /command). 'deny' always wins. If
# 'allow' is non-empty, ONLY matching commands may run. Match is by command name;
# a trailing '*' is a prefix wildcard, e.g. ""vv_*"" allows all mod commands.
commands:
  allow: []
  deny: []
";
    }
}
