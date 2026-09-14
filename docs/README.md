# Searchlight — Knowledge Base

A modern **.NET 10 / WinUI 3** Windows system-tray app that shows a read-only GUI of your
recent GitHub Copilot sessions (read from `~/.copilot/`) and lets you resume any of them in
Windows Terminal with one click. Styled after ScriptTray: the window lives in the tray, hides
on close, and only truly exits from the tray menu.

> **Status:** feature-complete and running. The solution is a WinUI host + a platform-neutral
> Core library + an xUnit test project (36 tests green). See the checkpoints in the session
> state for full development history.

## Documents

| Doc | What it covers |
|-----|----------------|
| [architecture.md](./architecture.md) | Layered architecture, project graph, DI composition root, data flow, threading model, key design decisions |
| [engineering.md](./engineering.md) | Projects, build configurations & compile flags, run modes, build/run/test commands, dependencies, settings, resume behavior, platform notes |
| [msix.md](./msix.md) | Dev/Production MSIX builds, signing, sideload installation, shared data, and side-by-side review |
| [data-model.md](./data-model.md) | The `~/.copilot` on-disk sources each reader consumes, and the in-memory domain model |

## 30-second orientation

- **Three projects** (`src/`):
  - `Searchlight` — WinUI 3 tray **host** (`net10.0-windows`, Windows-only).
  - `Searchlight.Core` — **platform-neutral** library (`net10.0`, zero WinUI): models,
    read-only `~/.copilot` readers, view-models, and the abstractions the host implements.
  - `Searchlight.Core.Tests` — xUnit tests over Core (`net10.0`, any OS).
- **Read-only by design.** The app never writes to `~/.copilot`. It only launches `copilot
  --resume=<id>` to hand off to the CLI.
- **Two data sources behind one façade** (`ISessionDataSource`): the **live** source over your
  real `~/.copilot`, and a synthetic **mock** source (15 deterministic sessions) for demos,
  screenshots, and tests — no proprietary data leaks.
- **Run modes:** default tray; `--no-tray` plain window; `--demo` (or the `Demo` build config)
  boots the mock data source.

## Fastest commands

```powershell
# Build the whole solution
dotnet build C:\REPOS\Searchlight\Searchlight.slnx -c Debug

# Run the unit tests (platform-neutral, no WinUI needed)
dotnet test C:\REPOS\Searchlight\src\Searchlight.Core.Tests\Searchlight.Core.Tests.csproj

# Run the tray app against synthetic data (safe for screenshots)
dotnet run --project C:\REPOS\Searchlight\src\Searchlight -c Demo
```

See [engineering.md](./engineering.md) for the full command matrix and flag reference.
