# Copilot instructions — Searchlight (Session Viewer)

This repo's knowledge base lives in [`docs/`](../docs/README.md). Read it before making changes.

| Doc | Read it when you need to understand… |
|-----|--------------------------------------|
| [docs/README.md](../docs/README.md) | Product overview + 30-second orientation (start here) |
| [docs/architecture.md](../docs/architecture.md) | Layered architecture, project graph, DI composition root, data flow, threading model, key design decisions |
| [docs/data-model.md](../docs/data-model.md) | The `~/.copilot` on-disk sources each reader consumes, and the in-memory domain model |
| [docs/engineering.md](../docs/engineering.md) | Projects, build configs & compile flags, run modes, build/run/test commands, dependencies, settings, resume behavior |

Key invariants (see docs for detail):
- **Read-only by design** — the app never writes to `~/.copilot`; its only side effect is launching `copilot --resume=<id>` in a terminal.
- **Platform-neutral Core, thin Windows host** — all logic lives in `Searchlight.Core` (`net10.0`, zero WinUI); `Searchlight` is the WinUI 3 tray exe; `Searchlight.Core.Tests` is xUnit over Core.
- **Two data sources behind one `ISessionDataSource` façade** — the live source over real `~/.copilot`, and a synthetic **mock** source (deterministic sessions) for demos/screenshots/tests, so no proprietary data leaks.
- Commit linear on `main` with the `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` trailer.
