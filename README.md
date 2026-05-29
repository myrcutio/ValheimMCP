# ValheimMCP

A standalone BepInEx plugin that exposes a **localhost HTTP endpoint** for driving
Valheim's in-game console remotely and returning the console output directly in the
response. Built to give an agent (Claude Code) reproducible, scriptable access to game
state — e.g. triggering `vv_*` diagnostic commands and reading what they print, without
typing into the in-game console or round-tripping through dump files.

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

The plain `/health`, `/commands`, `/command` routes are for `curl`/scripting.
`/mcp` speaks the protocol Claude Code consumes.

## MCP (native, no bridge)

The plugin implements the MCP Streamable-HTTP transport (JSON-RPC 2.0) directly,
with no external dependencies — a hand-rolled JSON parser/writer and stateless
`application/json` responses (no SSE). It exposes three tools: `run_command`,
`list_commands`, `health`.

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
curl -s -X POST 127.0.0.1:8731/command --data 'vv_probe 100 -50'
```

## Config

`BepInEx/config/com.valheimmcp.debugserver.cfg`:

- `Server.Host` (default `127.0.0.1`) — keep on localhost; the endpoint is unauthenticated.
- `Server.Port` (default `8731`)
- `Server.CommandTimeoutMs` (default `15000`)

## Build

```sh
dotnet build src/ValheimMCP/ValheimMCP.csproj -c Release
```

Output lands directly in `BepInEx/plugins/ValheimMCP/`.

## Status / roadmap

- **Done:** plain HTTP routes + native in-process MCP (`/mcp`), capturing synchronous
  console output. Usable via `curl` or as Claude Code MCP tools.
- **Known limitation:** output from commands that print asynchronously (coroutines, e.g.
  screenshot capture) is not captured — only what's printed synchronously during the call.
- **Possible next:** a `GET /file` route (and/or inlining `written: <path>` contents) so
  commands that dump to `vv_dumps/` return their payload too; typed introspection tools
  for live state (villagers, region graph) as the navigation refactor needs them.
