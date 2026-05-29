using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace ValheimMCP
{
    /// <summary>
    ///     Standalone BepInEx plugin that exposes a localhost HTTP endpoint for
    ///     driving Valheim's in-game console remotely and returning the console
    ///     output directly in the response. Intended to live in <c>plugins/</c>
    ///     (loaded once, never hot-reloaded) so the listener stays up across F6
    ///     reloads of other mods.
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        // Guid/Name/Version live in PluginInfo, generated from ValheimMCP.csproj.

        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;
        private HttpServer _server;

        private void Awake()
        {
            Log = Logger;

            // Settings come from BepInEx/config/valheimmcp.yml (written with defaults
            // on first run). See ModConfig / MiniYaml.
            ModConfig.Load();

            // Harmony postfix on Terminal.AddString tees console output into the
            // capture buffer while a command runs (see ConsoleOutputCapture).
            _harmony = new Harmony(PluginInfo.Guid);
            _harmony.PatchAll(typeof(ConsoleOutputCapture));

            var prefix = $"http://{ModConfig.Host}:{ModConfig.Port}/";
            try
            {
                _server = new HttpServer(prefix, ModConfig.CommandTimeoutMs);
                _server.Start();
                Log.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} listening on {prefix}");
            }
            catch (Exception ex)
            {
                Log.LogError($"{PluginInfo.Name} failed to start on {prefix}: {ex}");
            }
        }

        // Drains main-thread work queued by the HTTP listener's background thread.
        private void Update()
        {
            MainThreadDispatcher.Pump();
        }

        private void OnDestroy()
        {
            _server?.Stop();
            _harmony?.UnpatchSelf();
            Log?.LogInfo($"{PluginInfo.Name} stopped.");
        }
    }
}
