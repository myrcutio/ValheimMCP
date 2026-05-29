# ValheimMCP

A standalone BepInEx plugin that exposes a **localhost HTTP endpoint** for driving
Valheim's in-game console remotely and returning the console output directly in the
response. Built to give an agent (Claude Code) reproducible, scriptable access to game
state — e.g. running console commands like `pos` and reading what they print, without
typing into the in-game console or round-tripping through dump files.

## ⚠️ Security

This mod opens an **unauthenticated** HTTP endpoint that runs **arbitrary Valheim
console commands** (including anything `devcommands` unlocks). There is no token, no
password, no per-request check beyond the optional allow/deny list below.

- It binds **IPv4 loopback (`127.0.0.1`) only** by default, so it is not reachable
  from other machines. Treat any change to `server.host` as exposing full console
  control to whoever can reach that address — don't bind it to `0.0.0.0` or a LAN IP.
- Any process on your own machine can still drive the game while the listener is up.
  If that matters to you, lock it down with the `commands.allow` / `commands.deny`
  list (see [Config](#config)) so only the commands you intend can run.
- This is a development/automation tool. Don't run it on a shared or public host.

## Compatibility

Built and tested against **Valheim** (BepInEx pack
`denikson-BepInExPack_Valheim-5.4.2333`, BepInEx 5.4.x). It only depends on
Valheim's own `Console`/`Terminal`, so it should be resilient across game patches,
but it is not tied to any specific game build.

## Design

- Ships into `BepInEx/plugins/ValheimMCP/` (loaded once, **not** hot-reloaded), so the
  listener stays up across F6 reloads of the mods you're iterating on and never fights
  for its port.
- A background `HttpListener` thread accepts requests and marshals each onto Unity's
  main thread (`MainThreadDispatcher`) before touching any Valheim API.
- Console output is captured with a Harmony postfix on `Terminal.AddString` while a
  command runs — no private-field reflection.
- Zero dependency on any other mod: it drives Valheim's `Console`/`Terminal`, which
  already has every registered command.

## Routes

| Method | Path        | Body / Query              | Returns |
|--------|-------------|---------------------------|---------|
| POST   | `/mcp`      | JSON-RPC 2.0 (MCP)        | MCP response (`application/json`) |
| GET    | `/health`   | —                         | `{ok, inGame}` |
| GET    | `/commands` | —                         | `{ok, commands:[{name,description}]}` |
| POST   | `/command`  | raw command line, or `?text=` | `{ok, ran, output:[...], error?}` |
| GET    | `/sse`      | —                         | `501 Not Implemented` (no SSE transport) |

The plain `/health`, `/commands`, `/command` routes are for `curl`/scripting.
`/mcp` speaks the protocol Claude Code consumes.

## MCP (native, no bridge)

The plugin implements the MCP Streamable-HTTP transport (JSON-RPC 2.0) directly,
with no external dependencies — a hand-rolled JSON parser/writer and stateless
`application/json` responses (no SSE). Tools:

- `run_command(text)` — run a console command, return captured output.
- `list_commands()` — all registered console commands.
- `health()` — is a world loaded.
- `render_view(x, z, [y, yaw, pitch, dist, size])` — render the location with an
  **independent off-screen camera** (never touches the player's view) and return
  the PNG inline. The off-screen camera is named `valheimmcp_render_cam`, so a mod
  that can draw debug overlays into a named camera can target it to render those
  overlays into *only* this view, leaving the player's screen untouched.

Register it with Claude Code (game can be launched after; the connector reconnects):

```sh
claude mcp add --transport http valheim http://127.0.0.1:8731/mcp
```

### Examples

Use `127.0.0.1`, not `localhost` — the server binds IPv4 loopback only, and
`localhost` may resolve to IPv6 `::1` first (which isn't bound). This IPv4-only
bind is intentional: it keeps the endpoint strictly local.

```sh
curl -s 127.0.0.1:8731/health
curl -s 127.0.0.1:8731/commands
curl -s -X POST 127.0.0.1:8731/command --data 'pos'
```

## Config

`BepInEx/config/valheimmcp.yml` (written with defaults on first run; parsed by the
dependency-free `MiniYaml` reader):

```yaml
server:
  host: 127.0.0.1          # loopback only — endpoint is unauthenticated
  port: 8731
  commandTimeoutMs: 15000

render:
  defaultSize: 768         # render_view size when 'size' is omitted
  minSize: 256
  maxSize: 1280

# Access control for run_command (and POST /command). 'deny' always wins; if
# 'allow' is non-empty, ONLY matching commands run. Match by command name;
# trailing '*' is a prefix wildcard (e.g. "spawn*").
commands:
  allow: []
  deny: []
```

## Install

**From Thunderstore (recommended):** install with [r2modman](https://thunderstore.io/c/valheim/p/ebkr/r2modman/)
or the Thunderstore Mod Manager. It pulls in the BepInEx pack automatically.

**Manual:** install [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/),
then drop `ValheimMCP.dll` into `BepInEx/plugins/ValheimMCP/`. Launch the game once
to generate `BepInEx/config/valheimmcp.yml`, then edit it if needed.

## Build

```sh
dotnet build src/ValheimMCP/ValheimMCP.csproj -c Release
```

Output lands directly in `BepInEx/plugins/ValheimMCP/`. The build paths in the
`.csproj` assume a local Steam install and an r2modman profile named
`valheim-modding`; override `ValheimDir` / `R2ModmanProfile` if yours differ.

## Releasing

CI can't compile this — it needs Valheim's (non-redistributable) managed
assemblies — so releases are built locally. The version lives in **one place**,
`<Version>` in `ValheimMCP.csproj`: it flows into the assembly version, the
generated `PluginInfo` constants (used by `Plugin.cs` / `McpServer.cs`), and the
packaged `manifest.json` (synced by `scripts/package.sh`). Bump it there, add a
`CHANGELOG.md` entry, then:

```sh
./scripts/package.sh                       # -> dist/ValheimMCP-<version>.zip
gh release create v<version> dist/ValheimMCP-<version>.zip --generate-notes
```

Upload the same zip to [Thunderstore](https://thunderstore.io/c/valheim/). The
GitHub Actions workflow (`.github/workflows/release.yml`) can publish the release
for you on a `v*` tag if you `git add -f` the built zip onto the tagged commit.

## Status / roadmap

- **Done:** plain HTTP routes + native in-process MCP (`/mcp`), capturing synchronous
  console output. Usable via `curl` or as Claude Code MCP tools.
- **Known limitation:** output from commands that print asynchronously (coroutines, e.g.
  screenshot capture) is not captured — only what's printed synchronously during the call.
- **Possible next:** a `GET /file` route (and/or inlining `written: <path>` contents) so
  commands that dump to disk return their payload too; typed introspection tools for
  live game state.

## License

[MIT](LICENSE) © 2026 myrcutio
