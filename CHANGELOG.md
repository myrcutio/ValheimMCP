# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `get_log` MCP tool (and `GET /log` route): fetch recent lines from an in-memory
  log ring buffer on demand — tail the log, including on a dedicated server whose
  `LogOutput.log` isn't directly accessible. Supports incremental polling via a
  `cursor`/`since` and filtering via `contains`/`regex`. Complements the blocking
  `wait_for_log`.
- `LogWatch` now retains a bounded ring buffer of recent formatted log lines
  (configurable via `log.bufferCapacity` / `log.defaultLines` / `log.maxLines`).

## [0.1.0] - 2026-05-29

### Added
- Standalone BepInEx plugin exposing a localhost HTTP endpoint for driving
  Valheim's in-game console.
- Plain HTTP routes for scripting: `GET /health`, `GET /commands`,
  `POST /command`.
- Native in-process MCP transport (`POST /mcp`, Streamable-HTTP / JSON-RPC 2.0)
  with tools: `run_command`, `list_commands`, `health`, `render_view`.
- Off-screen camera rendering (`render_view`) returning a PNG inline, without
  touching the player's view.
- Dependency-free config at `BepInEx/config/valheimmcp.yml`, including an
  `allow`/`deny` command access-control list.

### Known limitations
- Output from commands that print asynchronously (coroutines, e.g. screenshot
  capture) is not captured — only output printed synchronously during the call.

[Unreleased]: https://github.com/myrcutio/ValheimMCP/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/myrcutio/ValheimMCP/releases/tag/v0.1.0