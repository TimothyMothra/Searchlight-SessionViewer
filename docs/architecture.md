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
5. **Native Copilot sources only.** No personal journal, status-snapshot index, or custom-hook
   prerequisite. Optional native fields are shown as unknown when absent.

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
| **Models** | `SessionInfo`, `SessionGroup`, `SessionKind`, `WorkspaceMetadata`, `SessionStartInfo`, `CheckpointInfo`, `SessionTodo`, `AppSettings` | Immutable (mostly `record`) domain data. `SessionInfo` carries computed projections (`DisplayName`, `ShortId`, `ClientLabel`, `UpdatedAt`, …). |
| **Readers** | `SessionStateScanner`, `WorkspaceYamlReader`, `EventsJsonlReader`, `CheckpointsReader`, `SessionDbReader` | Read-only readers over native session files. Todo reads return explicit availability/error states; other readers retain their null-safe empty-result contracts. See [data-model.md](./data-model.md). |
| **Aggregation** | `SessionAggregator` | Combines scanner + native workspace metadata into the `SessionInfo` list. Splits work into a **cheap bulk pass** (`LoadAll`) and **lazy per-session enrichment** (`EnrichWithEvents`), without external enrichment stores. |
| **Data source façade** | `ISessionDataSource` → `LiveSessionDataSource`, `MockSessionDataSource` | Single seam the view-models talk to. Live composes the aggregator + detail readers; Mock returns 15 synthetic sessions in-memory. |
| **Abstractions** | `IUiDispatcher`, `IResumeLauncher`, `ISessionWatcher`, `ICopilotProcessProbe` | Platform seams. The host's `CopilotProcessProbe` supplies live process identity/start time; Core's `SessionActivityMonitor` validates native locks. Keep Core free of WinUI/Win32/`Process`. |
| **View-models** | `MainViewModel`, `DetailsViewModel`, `TodosViewModel` | MVVM (CommunityToolkit.Mvvm). Own the grouped session list, selection, filter, Resume command, and independently activated Todos snapshot. |
| **Composition** | `ServiceCollectionExtensions.AddCopilotCore(useMock)` | Registers all of the above into an `IServiceCollection`. |
| **Diagnostics** | `CoreLog`, `MonitoringPolicy` | Consent-gated sink shared by Core/host. Dev is always enabled; Production/unpackaged require the saved opt-in. `EnabledChanged` releases active render observers on opt-out. |

---

## 4. Composition root & DI

The exe is the **composition host**. `App.OnLaunched` builds the container in `BuildServices`:

```
ServiceCollection
  ├─ AddSingleton(settingsService)                        // pre-built instance shared with Core
  ├─ AddSingleton<IUiDispatcher>(DispatcherQueueUiDispatcher)   // always host-supplied
  ├─ (live only) AddSingleton<IResumeLauncher, ResumeLauncher>
  ├─ (live only) AddSingleton<ISessionWatcher, SessionWatcher>
  ├─ (live only) AddSingleton<ICopilotProcessProbe, CopilotProcessProbe>
  └─ AddCopilotCore(useMock)                              // Core contributes the rest
```

`AddCopilotCore(useMock)`:

- Registers the stateless readers, `SessionAggregator`, and `SettingsService` (via `TryAdd`, so the
  host's pre-built instance wins).
- **`useMock == false` (live):** `ISessionDataSource → LiveSessionDataSource`. The host supplies
  `IResumeLauncher` + `ISessionWatcher` + `ICopilotProcessProbe`; Core registers `SessionActivityMonitor`.
- **`useMock == true` (mock):** `ISessionDataSource → MockSessionDataSource`, plus inert
  `MockResumeLauncher` and `NullSessionWatcher` — so a mock host only needs to add `IUiDispatcher`.
- Registers `DetailsViewModel` and `MainViewModel` (singletons).

**Why `SettingsService` is pre-built:** the host needs it *before* the container exists, to run the
startup elevation pre-check. `TryAddSingleton` lets Core register the same shared instance.

**Lifetime & disposal:** everything is a singleton. Disposing the `ServiceProvider` on exit disposes
the singletons it owns (including the `ISessionWatcher`), so the host must **not** also dispose them
manually (double-dispose). See `App.ExitApplication`.

---

## 5. Data flow (live mode)

```
 ~/.copilot/session-state/<id>/        SessionStateScanner ─┐
   workspace.yaml                      WorkspaceYamlReader ─┴─► SessionAggregator.LoadAll()
                                                                     │  IReadOnlyList<SessionInfo>
                                                                     ▼
                                            MainViewModel.LoadAsync  (Task.Run, off UI thread)
                                                                     │
                                                    ApplyFilter → GroupKeyFor → SessionGroups
                                                                     │  (ObservableCollection)
                                                                     ▼
                                                          ListView (left pane, grouped)
                                                                     │ selection
                                                                     ▼
                                            DetailsViewModel.Load(session)   ← default Details tab:
                                              • EnrichWithEvents  (events.jsonl head + tail previews)
                                            Open Checkpoints tab:
                                              • ReadCheckpoints   (checkpoints/*.md)
                                                                     │
                                                                     ▼
                                                        Details / Checkpoints tabs
                                                          + shared Resume button

                                            Explicitly select Todos / click its Refresh
                                                                     │
                                                                     ▼
                                            TodosViewModel (independent serial worker)
                                              • ReadTodos (session.db → schema + rows)
                                                                     │
                                                                     ▼
                                            Todos tab: counts, title, description, status
```

**Summary and detail loading (performance):**

- **Catalog (`LoadCheap`)** discovers all folders using native workspace summaries.
  Unchanged session summaries are reused from an in-memory cache. Folder timestamps, workspace
  timestamps/lengths, and checkpoint-directory timestamps invalidate changed summaries; deleted
  folders are evicted. The cache is not persisted to Copilot's data directory.
  Catalog metadata probes use at most **four concurrent readers**, including overlapping
  scanner calls. Filesystem and ownership checks run outside the global cache lock;
  per-folder coordination protects publication and prevents evicted state from being
  reinserted by an older read. Cold discovery still skips YAML parsing and per-folder file
  enumeration. Results remain newest-first, with ordinal folder-path ordering for timestamp ties.
  Lock paths are cached with summaries, but live ownership is rechecked on cache hits: process
  exit is independent of file versions. The watcher checks confirmed owners every 10 seconds
  and reloads only when confirmation is lost, not on every timer tick.
- **First publication** enriches the newest **30** rows plus every pin and the current selection.
  Remaining missing summaries load in **30-row batches**, with indexed row lookup and a
  bounded producer/consumer queue. The producer can retain two queued batches and continue reading
  while the UI renders; it does not wait for a UI continuation before starting every batch.
  Within a batch, indexed item replacements preserve WinUI's realized
  containers; resetting entire groups for metadata updates causes costly layout churn.
  Names, folders, branches and dates remain searchable across the
  entire catalog once this background pass completes; heavy details are not prefetched.
  At most **four summary readers** overlap filesystem latency within each batch. Input order
  is preserved, and pending pins finish before ordinary recent rows start. YAML file reads
  overlap, but use of the shared deserializer is serialized.
- **Filtering** uses in-memory metadata and a note-presence index loaded once per refresh.
  Unchanged groups/rows are retained, and filtering an unchanged selection does not reload details.
  The compact tag pane beside Search combines In use/CLI/App toggles with AND before text search.
  Choices are transient and do not read files. With a tag enabled, each summary batch
  reconciles membership so newly known matches appear without waiting for the entire catalog.
  Group reconciliation uses session IDs to remove, insert, and move rows individually,
  replacing only changed summaries (or explicitly refreshed mutable row flags). Surviving
  row instances are retained; filtering and progressive regrouping never emit a group Reset.
- **Section details** load asynchronously through `SessionDetailsLoader`, with a serial worker and an
  **eight-entry LRU cache shared across session/section keys**. Only the active tab's reader
  and version probes run. Details checks workspace/events; Checkpoints checks its directory
  and Markdown files (including edits in place).
  Switching tabs or explicitly refreshing validates just that section's cache; hidden sections
  wait until opened. Inputs that change during a read are not cached. Superseded selections
  and tab switches cancel queued work and cannot publish stale results. Resume/copy remain
  available while sections load. These two tabs never read `session.db`.
- **Section tabs** split native metadata into Details, tasks into Agent tasks, and checkpoint
  summaries into Checkpoints, in that order. The latter two start with explanatory text and
  load only when opened. Checkpoint empty messages depend on successfully loaded collections
  and stay hidden before activation, during loading, or after a load failure. File last-modified
  times show full local date/time with UTC offset. Details uses an I/O-free, explicit metadata
  display allowlist grouped by native source; absent booleans/counts are not fabricated as false/zero.
  Overview is immediately realized; advanced metadata bodies use effective-viewport intersection
  plus `x:Load`, with estimated-height placeholders preserving scroll extent. Scrolling reveals
  the controls automatically, not via expanders. Stable observable group instances avoid
  resetting unchanged visual trees on refresh, and no new disk/SQLite reads happen on scroll.
- **Agent tasks** (the `todos` table) live on the second tab beneath the shared session header,
  with an introductory paragraph explaining the read-only work list. Every explicit activation
  and its dedicated **Refresh** action reads a fresh snapshot off-thread; there is no polling,
  watcher/global-refresh read, or todo LRU cache. Switching to another session resets to Details.
  Same-session metadata refreshes preserve the selected tab and snapshot. Cancellation and a
  generation guard discard reads superseded by a session/tab change; reads are serialized so a
  cancelled SQLite call cannot cause overlapping retries. Loading clears the old snapshot, and
  empty/unavailable/error states are distinct. A constrained `ListView` virtualizes the flat
  six-column table (ID, title, description, status, created, updated); the surrounding horizontal-only
  scroller keeps headers aligned without unbounding vertical layout. Counts include completed,
  unfamiliar, and missing statuses. Compact native tabs retain the selected accent underline.
  Notes remain in their separate pane.
- **Event previews** are UTF-8, bounded to 2,000 lines / 8 MiB input / 1 MiB per event per scan.
  Oversized events are skipped through the next line boundary; budget hits are logged. The parser
  uses pooled buffers and disposes each JSON document without cloning it. These limits bound a
  preview, not a complete transcript or a claim about the model after the scanned window.
  A separate reverse scan finds the last complete nonempty user prompt from a captured EOF
  without relying on the head window. It stops at the same limits or an oversized event,
  logging an unavailable preview rather than presenting a potentially stale earlier prompt.
- **Diagnostics** distinguish first publication from full summary completion. The footer's
  `Loaded N sessions in Xs` covers all summary batches, not just first display or detail completion.
  Per-phase timings separate catalog/eager/background reader work, UI mutations, and scheduling
  delays so a headless benchmark is not mistaken for end-to-end WinUI startup performance.
  Accumulated queue-wait time overlaps producer work and must not be added to reader time as
  though they were sequential stages.
  Detail logging now separates cache/read work, metadata projection, publication, and actual
  UI control realization/layout observations/next render tick. Correlation uses request and
  presentation counters, not session text. Render event handlers exist only while a sample is
  pending; completion, unload, viewport exit, opt-out, and disposal release them.
  A render tick is not GPU presentation or isolated CPU layout time. See the monitoring
  contract in [engineering.md](engineering.md) before interpreting these overlapping milestones.

**Live refresh:** `SessionWatcher` wraps a `FileSystemWatcher` on `~/.copilot/session-state` and
raises a single **debounced** `Changed` event for structural session/lock changes. `MainViewModel`
hooks it after first publication. Requests arriving during enrichment coalesce into one follow-up
pass instead of repeatedly abandoning work. A catalog refresh still discovers every folder, but
only changed summaries are reparsed. External note edits and detail content changes are observed
on refresh (detail content is also checked when reselected).

---

## 6. Threading model

- **UI thread:** captured in `OnLaunched` via `DispatcherQueue.GetForCurrentThread()`, wrapped in
  `DispatcherQueueUiDispatcher : IUiDispatcher`.
- **Heavy loads:** catalog, note-index, summary batches and detail reads run on the thread pool.
  View-model continuations capture the UI synchronization context to publish bound collections.
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
| Channel identity | `Services\AppIdentity` | Build metadata selects Dev, Production, or Unpackaged branding, mutex/event namespace, and diagnostics. |

**MSIX and shared state:** the host supports single-project MSIX packaging without adding
WinUI references to Core. Production and Dev use separate package families, while both use
the shared `%USERPROFILE%\.searchlight` data root outside virtualized AppData. Persistence
coordination and migration belong in Core; manifest selection, certificate signing, package
installation, and activation remain host/tooling concerns. See [msix.md](msix.md).

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
- **Read-only SQLite + explicit todo outcomes.** Todo reads use a short-lived read-only connection
  and a deferred read transaction so schema and rows come from one committed snapshot, including
  WAL commits. A bounded busy timeout keeps retries responsive. Schema discovery tolerates added,
  reordered, or missing columns and unfamiliar statuses. Missing database/table, unsupported schema,
  successful empty results, and read errors are separate outcomes, not interchangeable empty lists.
  Other readers retain their existing null-safe contracts.
- **Diagnostic `CoreLog.Sink` seam.** Core emits breadcrumbs without referencing the host's logger;
  the host points the sink at its temp-file log in the `App` constructor.

---

## 9. Known constraints

- **Interactive/visual verification is currently blocked** by a locked desktop (LogonUI) in the dev
  environment, so `dotnet test` is the automated correctness gate for headless work.
- The repo is a **non-git scratch folder** — there is no version control history; changes are not
  revertible. Proceed methodically.
