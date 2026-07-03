# Searchlight

**Searchlight: Historical Session Viewer** — a modern **.NET 10 / WinUI 3** Windows app that
shows a read-only GUI of your recent AI coding-agent sessions and lets you resume any of them
with one click.

Today it reads **GitHub Copilot** sessions from `~/.copilot/`. The data layer is agent-neutral
by design — support for other agents (e.g. Claude Code) is a planned extension.

> **This fork** adds both halves of that extension: a **Claude Code** data source reading
> `~/.claude/projects/`, and a cross-platform **Avalonia** host so the GUI runs on macOS and
> Linux (resuming via `claude --resume` in the platform terminal). See
> [macOS / Claude Code](#macos--claude-code-avalonia-host) below.

> **Status:** feature-complete and running. A WinUI host + a platform-neutral Core library + an
> xUnit test project (36 tests green).

## Screenshot

![Searchlight showing synthetic Demo sessions grouped by recency](docs/images/screenshot.png)

_Captured using the `Demo` build config (synthetic data) so no proprietary session content is shown._

## What it does

- **Frequency-sorted session list** with recency group headers (Last 2h / 4h / 8h / 16h / 32h,
  then grouped by day).
- **Details pane** — model, reasoning effort, first-prompt preview, checkpoints, status snapshots,
  client type (CLI vs App), and more.
- **One-click Resume** — hands off to `copilot --resume=<id>` in Windows Terminal.
- **System tray** — lives in the tray like ScriptTray; hides on close, exits from the tray menu.
- **Read-only by design** — never writes to `~/.copilot`.

## Quick start

```powershell
# Build the whole solution
dotnet build Searchlight.slnx -c Debug

# Run the unit tests (platform-neutral, no WinUI needed)
dotnet test src/Searchlight.Core.Tests/Searchlight.Core.Tests.csproj

# Run against your real sessions
dotnet run --project src/Searchlight -c Debug

# Run against synthetic data (safe for screenshots)
dotnet run --project src/Searchlight -c Demo
```

Requires the **.NET 10 SDK** (pinned via `global.json`) on Windows.

## Run modes

| Mode | How | Data source |
|------|-----|-------------|
| Tray (default) | `dotnet run --project src/Searchlight` | Live `~/.copilot` |
| No tray | append `--no-tray` | Live `~/.copilot` |
| Demo / mock | `Demo` build config, or `--demo` flag | Synthetic (15 sessions) |

## macOS / Claude Code (Avalonia host)

`src/Searchlight.Avalonia` is a cross-platform front-end over the same Core library, backed by
the **Claude Code** session store instead of Copilot's:

- Reads `~/.claude/projects/` — the per-project `sessions-index.json` for the cheap bulk list
  (summary, first prompt, message count, branch, timestamps), merged with any un-indexed
  `<uuid>.jsonl` transcripts on disk. Transcript head-parsing (model, CLI version) is deferred
  until a row is selected, mirroring the Copilot source.
- **One-click Resume** — `cd <workspace> && claude --resume <id>` in Terminal.app on macOS
  (`x-terminal-emulator` on Linux, `cmd` on Windows).
- **Read-only by design** — never writes to `~/.claude`.

```bash
# Run the unit tests (any OS)
dotnet test src/Searchlight.Core.Tests/Searchlight.Core.Tests.csproj

# Run against your real Claude Code sessions
dotnet run --project src/Searchlight.Avalonia

# Run against synthetic data
dotnet run --project src/Searchlight.Avalonia -- --demo
```

Building `Searchlight.slnx` as a whole still requires Windows (the WinUI host); on macOS/Linux
build the individual projects above.

## Documentation

Full knowledge base in [`docs/`](./docs/README.md):

- [architecture.md](./docs/architecture.md) — layered architecture, DI composition root, data flow
- [engineering.md](./docs/engineering.md) — build configs, compile flags, run modes, commands
- [data-model.md](./docs/data-model.md) — `~/.copilot` sources and the in-memory domain model

## License

[MIT](./LICENSE)
