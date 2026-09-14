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

## Review workflow

- After application changes, validate and build the current worktree's Release **Dev MSIX**
  with `tools\Build-Msix.ps1`, then install/launch it with `tools\Install-DevMsix.ps1`
  before handing back for review. Keep Production installed and untouched.
  Use `tools\install.ps1` only when the user explicitly wants an unpackaged installation.
  Installation of Dev is authorized by default;
  do not stop at a build-only handoff unless installation is blocked or the user asks to skip it.
- Copilot has standing authorization to control the **Dev installation**: build, install,
  launch, close, and restart it without repeated prompts. Prefer the graceful exit handshake;
  use `Install-DevMsix.ps1 -StopRunning` when necessary. Never extend this authorization to
  stopping, replacing, or uninstalling Production. Preserve shared settings and notes.
- Verify the package identity/version and that the installed executable, assembly, and
  `resources.pri` match the MSIX payload (`Searchlight.pri` for unpackaged installs).
  Report installation blockers plainly rather than implying the running app has been updated.
