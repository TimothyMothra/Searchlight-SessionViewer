# Architecture

This document captures the **current architecture** of Searchlight: how the code is
layered, how dependencies are wired, how data flows from disk to the UI, and the design decisions
behind the shape.

---

## 1. Design goals

1. **Read-only, non-destructive.** The app observes `~/.copilot/` and never mutates it. The only
   side effect it produces is launching `copilot --resume=<id>` in a terminal.
2. **Testable and demo-able without leaking data.** All logic that matters lives in a WinUI-free
   library that can be unit-tested and driven by a synthetic data source, so the UI can be
   exercised and screenshotted with zero proprietary content.
3. **Platform-neutral core, thin Windows host.** WinUI/Win32 concerns are isolated in the exe;
   everything else is portable so a future non-Windows front-end could reuse the Core unchanged.
4. **Responsive on large stores.** A folder scan of hundreds of sessions must stay snappy, so
   heavy per-session parsing is deferred until a row is selected.

---

## 2. Project graph

```
                 ┌──────────────────────────────────────────────┐
                 │  Searchlight  (WinUI 3 host, exe)     │
                 │  net10.0-windows10.0.19041.0                  │
                 │                                               │
                 │  • App.xaml.cs  — composition root + tray     │
                 │  • MainWindow / Views / Converters (XAML)     │
                 │  • Services\  ResumeLauncher, ElevationHelper,│
                 │      DispatcherQueueUiDispatcher, ThemeHelper │
                 │  • Interop\   ForegroundWindowHelper (Win32)  │
                 └───────────────────────┬──────────────────────┘
                                         │ ProjectReference
                                         ▼
                 ┌──────────────────────────────────────────────┐
                 │  Searchlight.Core  (library)          │
                 │  net10.0  (ZERO WinUI / Windows-only refs)    │
                 │                                               │
                 │  • Abstractions\  IUiDispatcher,              │
                 │      IResumeLauncher, ISessionWatcher         │
                 │  • Services\      readers, aggregator,        │
                 │      ISessionDataSource (Live + Mock),        │
                 │      SettingsService, CopilotPaths            │
                 │  • ViewModels\    MainViewModel, DetailsVM    │
                 │  • Models\        SessionInfo, SessionGroup…  │
                 │  • Composition\   AddCopilotCore(useMock)     │
                 │  • Diagnostics\   CoreLog                     │
                 └───────────────────────▲──────────────────────┘
                                         │ ProjectReference (InternalsVisibleTo)
                 ┌───────────────────────┴──────────────────────┐
                 │  Searchlight.Core.Tests  (xUnit)      │
                 │  net10.0  — runs on any OS                    │
                 └──────────────────────────────────────────────┘
```

**Dependency rule:** the arrow only points one way. Core never references the host, WinUI, or any
Windows-only assembly. The host and the tests depend on Core.

---

## 3. Layers inside Core

| Layer | Types | Responsibility |
|-------|-------|----------------|
| **Models** | `SessionInfo`, `SessionGroup`, `SessionKind`, `WorkspaceMetadata`, `SessionStartInfo`, `CheckpointInfo`, `SnapshotInfo`, `SessionTodo`, `JournalEntry`, `AppSettings` | Immutable (mostly `record`) domain data. `SessionInfo` carries computed projections (`DisplayName`, `ShortId`, `ClientLabel`, `UpdatedAt`, …). |
| **Readers** | `SessionStateScanner`, `WorkspaceYamlReader`, `EventsJsonlReader`, `SnapshotIndexReader`, `JournalReader`, `CheckpointsReader`, `SessionDbReader` | One read-only reader per on-disk source. All null-safe: a missing/locked/malformed source degrades to empty, never throws. See [data-model.md](./data-model.md). |
| **Aggregation** | `SessionAggregator` | Merges scanner + workspace + snapshot-index + journal into the `SessionInfo` list. Splits work into a **cheap bulk pass** (`LoadAll`) and **lazy per-session enrichment** (`EnrichWithEvents`). |
| **Data source façade** | `ISessionDataSource` → `LiveSessionDataSource`, `MockSessionDataSource` | Single seam the view-models talk to. Live composes the aggregator + detail readers; Mock returns 15 synthetic sessions in-memory. |
| **Abstractions** | `IUiDispatcher`, `IResumeLauncher`, `ISessionWatcher` | Platform seams the host implements. Keep Core free of WinUI/Win32/`Process`. |
| **View-models** | `MainViewModel`, `DetailsViewModel` | MVVM (CommunityToolkit.Mvvm). Own the grouped session list, selection, filter, and the Resume command. |
| **Composition** | `ServiceCollectionExtensions.AddCopilotCore(useMock)` | Registers all of the above into an `IServiceCollection`. |
| **Diagnostics** | `CoreLog` | A `static Action<string> Sink` seam the host points at its log file (Core can't see the exe's logger). |

---

## 4. Composition root & DI

The exe is the **composition host**. `App.OnLaunched` builds the container in `BuildServices`:

```
ServiceCollection
  ├─ AddSingleton(settingsService)                        // pre-built instance shared with Core
  ├─ AddSingleton<IUiDispatcher>(DispatcherQueueUiDispatcher)   // always host-supplied
  ├─ (live only) AddSingleton<IResumeLauncher, ResumeLauncher>
  ├─ (live only) AddSingleton<ISessionWatcher, SessionWatcher>
  └─ AddCopilotCore(useMock)                              // Core contributes the rest
```

`AddCopilotCore(useMock)`:

- Registers `SettingsService` (via `TryAdd`, so the host's pre-built instance wins), and — in live
  mode — the stateless readers plus `SessionAggregator`.
- **`useMock == false` (live):** `ISessionDataSource → LiveSessionDataSource`. The host supplies
  `IResumeLauncher` + `ISessionWatcher`.
- **`useMock == true` (mock):** `ISessionDataSource → MockSessionDataSource`, plus inert
  `MockResumeLauncher` and `NullSessionWatcher` — so a mock host only needs to add `IUiDispatcher`.
- Registers `DetailsViewModel` and `MainViewModel` (singletons).

Two sibling entry points serve the cross-platform Avalonia host: `AddClaudeCore(useMock)` (the
Claude Code store, `~/.claude/projects`) and `AddCombinedCore(useMock)` (both stores merged through
`CompositeSessionDataSource` + `CompositeSessionWatcher`). All three share the same mock mode and
view-model registrations.

**Why `SettingsService` is pre-built:** the host needs it *before* the container exists, to run the
startup elevation pre-check. `TryAddSingleton` lets Core register the same shared instance.

**Lifetime & disposal:** everything is a singleton. Disposing the `ServiceProvider` on exit disposes
the singletons it owns (including the `ISessionWatcher`), so the host must **not** also dispose them
manually (double-dispose). See `App.ExitApplication`.

---

## 5. Data flow (live mode)

```
 ~/.copilot/session-state/<id>/        SessionStateScanner ─┐
   workspace.yaml                      WorkspaceYamlReader  │  (cheap bulk pass)
 ~/.copilot/status-snapshots/index.db  SnapshotIndexReader  ├─► SessionAggregator.LoadAll()
 ~/.copilot/journal/<YYYY-MM>.md       JournalReader       ─┘        │  IReadOnlyList<SessionInfo>
                                                                     ▼
                                            MainViewModel.LoadAsync  (Task.Run, off UI thread)
                                                                     │
                                                    ApplyFilter → GroupKeyFor → SessionGroups
                                                                     │  (ObservableCollection)
                                                                     ▼
                                                          ListView (left pane, grouped)
                                                                     │ selection
                                                                     ▼
                                            DetailsViewModel.Load(session)   ← lazy, per-session:
                                              • EnrichWithEvents  (events.jsonl head parse)
                                              • ReadCheckpoints   (checkpoints/*.md)
                                              • LoadSnapshots     (status-snapshots index.db)
                                              • ReadTodos         (session.db → todos)
                                                                     │
                                                                     ▼
                                                        Details pane + Resume button
```

**Two-tier loading (performance):**

- **Bulk (`LoadAll`)** — folder scan + `workspace.yaml` + bulk snapshot/journal enrichment. This is
  cheap and runs for *every* session so the list appears fast even with hundreds of folders.
- **Lazy (per selection)** — `events.jsonl` head-parse (bounded to a max number of leading lines),
  checkpoints, snapshots, and `session.db` todos are only read when a row is selected. This keeps a
  500-folder scan responsive.

**Live refresh:** `SessionWatcher` wraps a `FileSystemWatcher` on `~/.copilot/session-state` and
raises a single **debounced** `Changed` event. `MainViewModel` hooks it after the first load and
re-loads through the `IUiDispatcher` so list updates marshal back onto the UI thread.

---

## 6. Threading model

- **UI thread:** captured in `OnLaunched` via `DispatcherQueue.GetForCurrentThread()`, wrapped in
  `DispatcherQueueUiDispatcher : IUiDispatcher`.
- **Heavy loads:** `MainViewModel.LoadAsync` does `await Task.Run(() => _dataSource.LoadAll())` so
  the disk/SQLite/YAML work runs on the thread pool.
- **Marshalling back:** the watcher and any background continuation post UI updates through
  `IUiDispatcher.Post`, keeping `ObservableCollection` mutations on the UI thread.
- **In tests:** the test project supplies a synchronous `InlineUiDispatcher` (`Post(a) => a()`), so
  the load pipeline runs deterministically with no dispatcher/SynchronizationContext.

---

## 7. Windows-host concerns (kept out of Core)

| Concern | Type (in exe) | Notes |
|---------|---------------|-------|
| Tray icon + context menu | `App.InitializeTrayIcon` (`H.NotifyIcon.WinUI`) | Left-click shows window; right-click Open/Refresh/Exit. Uses `Assets\app.ico`. |
| Hide-to-tray vs exit | `App.OnWindowClosing` | Close is vetoed and the window hides; only tray **Exit** (or `--no-tray` close) really quits. |
| Resume launcher | `Services\ResumeLauncher` | `wt.exe -w last new-tab … cmd /k copilot --resume=<id>`; falls back to `cmd.exe`. |
| Elevation | `Services\ElevationHelper` + `App.OnSettingsChanged` | On-demand relaunch elevated/non-elevated to match the user's Terminal integrity level. |
| Foreground/resize/Win32 | `Interop\ForegroundWindowHelper` | Bring-to-front past the OS foreground lock; DPI-aware logical resize. |
| UI-thread marshalling | `Services\DispatcherQueueUiDispatcher` | Adapter over `DispatcherQueue.TryEnqueue`. |
| Theme | `Services\SystemThemeHelper` | System light/dark. |

---

## 8. Key design decisions & rationale

- **Single exe with runtime/compile mode switches (not separate exes).** The Windows-scoped tray and
  the potential cross-platform front-end split were deferred; today one exe supports `--no-tray` and
  `--demo`, and a `Demo` build config hard-selects the mock via `USE_MOCK`. This kept the surface
  small while still proving the seams.
- **Façade over the data sources (`ISessionDataSource`).** The view-models never branch on
  live-vs-mock; the container decides. This is what makes the mock a first-class, test-and-screenshot
  path rather than a hack.
- **Mock lives in Core, not the exe.** So both the app (Demo config) and the tests share the exact
  same 15-session fixture — one source of truth for “what does populated data look like.”
- **`InternalsVisibleTo` for white-box tests.** `MainViewModel.GroupKeyFor` (the recency-bucket
  ladder) is `internal static` and unit-tested directly for its strict `<` boundaries.
- **Read-only SQLite + null-safe readers.** Every reader opens sources read-only and degrades to
  empty on any failure, so a locked `session.db` or half-written file never crashes the UI.
- **Diagnostic `CoreLog.Sink` seam.** Core emits breadcrumbs without referencing the host's logger;
  the host points the sink at its temp-file log in the `App` constructor.

---

## 9. Known constraints

- **Interactive/visual verification is currently blocked** by a locked desktop (LogonUI) in the dev
  environment, so `dotnet test` is the automated correctness gate for headless work.
- The repo is a **non-git scratch folder** — there is no version control history; changes are not
  revertible. Proceed methodically.
