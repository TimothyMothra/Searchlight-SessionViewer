# Data Model

How the app turns the on-disk `~/.copilot` layout into the in-memory domain it renders. Two halves:
the **on-disk sources** (and the reader that consumes each), and the **domain records** the readers
produce. Everything is **read-only** — sessions vary wildly in which files exist.
General enrichment fields degrade to empty/null; the Todos tab uses explicit availability/error
states so an unavailable source cannot look like an empty todo table.

---

## 1. On-disk sources → readers

`CopilotPaths` resolves every path under `~/.copilot`; the app never writes user Copilot data.

**Native sources only.** The app consumes Copilot's own session-state files, not personal
extensions. The former monthly Markdown journal and `status-snapshots/index.db` integrations
have been removed, including their readers, branch/activity enrichment, and Status snapshots
tab. No journaling skill, snapshot hook, SDK installation, or separate index is required.
Searchlight's own settings/notes remain app-owned state, not claimed Copilot metadata.

Source provenance is supported by GitHub's official SDK
[session filesystem tests](https://github.com/github/copilot-sdk/blob/main/nodejs/test/e2e/session_fs.e2e.test.ts)
(events, workspace, checkpoint index, plan),
[SQLite tests](https://github.com/github/copilot-sdk/blob/main/nodejs/test/e2e/session_fs_sqlite.e2e.test.ts)
(native SQL/todos), [workspace schema](https://github.com/github/copilot-sdk/blob/main/java/sdk/src/generated/java/com/github/copilot/generated/rpc/SessionWorkspacesGetWorkspaceResult.java),
and [event schema](https://github.com/github/copilot-sdk/blob/main/nodejs/src/generated/session-events.ts).
These establish native provenance, not a promise of a stable on-disk API. Files and optional
fields vary by client/version; unsupported or missing data must remain explicit.

| Source (under `~/.copilot`) | Reader | What it yields | Cost |
|-----------------------------|--------|----------------|------|
| `session-state/<id>/` folders | `SessionStateScanner` | One base `SessionInfo` per folder: `Id`, `FolderName`, `FolderPath`, `Kind`, `LastWriteTime`, verified activity (`IsInUse` via native lock + live owner), presence flags (`HasPlan`, `HasSessionDb`, `HasCheckpoints`, `HasEvents`) and `IsEnriched`. Splits `optimistic-chat-` prefix → `Chat`, else `Project`. | cheap (bulk) |
| `session-state/<id>/workspace.yaml` | `WorkspaceYamlReader` | `WorkspaceMetadata` (id, name, cwd, Git root/repository/host/branch, client, created/updated, user-named, summary count, remote steering, MC ids) via YamlDotNet. Optional booleans/counts remain nullable. | cheap (bulk) |
| `session-state/<id>/events.jsonl` | `EventsJsonlReader` | `SessionStartInfo` — UTF-8 head preview bounded by 2,000 lines and 8 MiB input, with individual events over 1 MiB skipped: `session.start` baseline + latest in-window `session.model_change` + first in-window `user.message` preview. A separate reverse scan finds the last prompt with the same per-scan limits. Documents are disposed without cloning; the complete log is never materialized. Limit hits are logged. | heavy (lazy, cached) |
| `session-state/<id>/checkpoints/NNN-title.md` + `index.md` | `CheckpointsReader` | `CheckpointInfo` list; prefers the fuller title from `index.md`'s table over the truncated file name. | heavy (lazy) |
| `session-state/<id>/session.db` (table `todos`) | `SessionDbReader.ReadTodos` | `SessionTodosResult`: todo rows, missing-field warnings, and explicit availability/error state. Read-only SQLite. The legacy combined reader still supports `session_state`, but the UI never calls it. | heavy (explicit Todos activation only) |

**Summary/detail split.** The UI uses `LoadCheap` to discover all folders and reuse versioned
summaries. It enriches the newest **30** sessions plus all pins and the selection before first
publication, then loads remaining summaries in **30-row batches**. This preserves catalog-wide
name/folder/branch search without preloading event content. Note-presence is indexed once per
refresh rather than probing the filesystem during search.

**In use means verified live process ownership, not active generation or a visible session.**
Idle, waiting, and background App sessions count while their owners remain alive. The badge
does not count open tabs/windows or prove that the owner is responsive; a lingering or stalled
App server can legitimately satisfy this narrower definition.
`SessionActivityMonitor` requires a
native `inuse.<PID>.lock` whose bounded, plain-text PID matches its filename, plus a host-confirmed
live Copilot process that started no later than the lock's last-write time. Stale locks, reused
PIDs, unrelated processes, malformed files, and unavailable process information do not earn a
badge. The Windows probe recognizes `copilot` and the native updater's
`copilot.exe.old-<PID>-<timestamp>` name. Unsupported launchers (such as generic `node` hosts)
remain unconfirmed rather than guessed from recency. This assumes locally produced native locks;
it is not an authenticated ownership protocol for copied/edited files or remote sessions.
Idle owners may remain valid for days; no recent-activity threshold is used.

Cached summaries recheck lock ownership on refresh independently of folder timestamps.
The watcher polls only previously confirmed locks every **10 seconds**, requesting a debounced
refresh when confirmation is lost (normally within about 12 seconds). An unchanged live owner
does not trigger a catalog reload. File create/delete notifications still discover new locks.
An unavailable inspection hides the badge; manual refresh can confirm it after access recovers.
The Details field is **Live lock owner confirmed**, not **In-use lock present**. Locks are
never deleted, rewritten, or repaired.

Events load asynchronously when Details is active (the default on session selection).
Checkpoint lists load only when their tab is opened.
A versioned, eight-entry LRU cache shared across session/section keys retains recent payloads,
including empty results, without accumulating details for every visited session.
Only the active section's input versions are checked: workspace/events for Details, checkpoint
directory and individual Markdown files for Checkpoints.
Unchanged filtering does not even recheck these files. Catalog-wide branches come from native
workspace summaries, without eagerly parsing event content.
The Details and Checkpoints tabs do not read `session.db`. See
[architecture.md](./architecture.md) for refresh, cancellation and cache assumptions.

**Section tabs.** The session pane has three tabs beneath the shared title and actions:
**Details** (metadata and prompt previews), **Agent tasks** (the tracked-work table),
and **Checkpoints** (checkpoint numbers, titles, file paths, and file last-modified times).
The latter two begin with a short explanation of their contents. Checkpoints has its own
scrolling list, loading indicator, empty message, and visible retryable
load-error message. No checkpoint content is read before activation. Reopening a section rechecks
only its source versions and reuses unchanged cached results. Leaving cancels queued work and
prevents obsolete results from publishing. Selecting another session returns to Details;
refreshing the same session retains and refreshes only the active section (Agent tasks keeps
its separate explicit-refresh contract below). Hidden sections are checked when next opened.
Checkpoint last-modified times display full local date/time, seconds, and UTC offset;
unavailable timestamps show a dash.

**All parsed native metadata.** Details starts with a compact **Overview** in the familiar
order: session ID with its copy button, folder, branch, model, reasoning, version,
workspace Created/Updated times, and first/last prompts. Session ID is no longer a subtitle
or a Session storage row; its copy action retains the existing clipboard command and remains
available while Details loads.
The copy button sits immediately after the left-aligned ID rather than at the value column's
right edge. The pinned session title stays beside the actions when its measured width fits;
otherwise it moves to a full-width row below them. It never wraps, using ellipsis only when
the full row is still too narrow.
Created/Updated appear only in Overview (missing workspace dates stay unknown); Client appears
only in Workspace metadata.
The additional **Workspace metadata** and **Event metadata** groups follow; **Session storage**
is last. Prompts appear only in the overview. `SessionMetadata` is an explicit,
I/O-free display allowlist; tests require coverage of all parsed workspace/start-record fields.
It displays recorded names and identifiers, both workspace/start working directories and Git
context, creation/update/start/folder timestamps, optional flags/counts, client-specific MC IDs,
runtime configuration, file-presence flags, and both prompts. Unknown is a dash, distinct from
known **No** or **0**. File flags wait for enrichment; session kind is labeled as inferred.
Event model/reasoning are labeled as bounded head previews, not authoritative live state.

**Scroll-driven realization.** Overview is built immediately. The other groups keep their
headings and a lightweight estimated body height, but their row controls use `x:Load` and
are created only when the group intersects the effective viewport. No expander click is
required, and there are no additional source reads on scroll: the values already come from
the selected session's native metadata. Realized groups remain available while browsing that
session. Unchanged groups retain their instances across refreshes; changed groups preserve
their realized state. A different session resets realization and scrolls back to Overview.

**Prompt previews.** Details shows **First prompt** followed by **Last prompt**. Both use
nonempty `user.message.data.content` strings, flatten line breaks, and truncate after 2,000
characters with an ellipsis (the UI also caps each preview at 24 lines). Last prompt reads
backward from a captured EOF, independent of the head window; assistant/tool events and
malformed or incomplete JSON are ignored. A single-message session shows the same prompt twice.
Missing prompts display a dash. If the reverse scan hits its byte/line budget or an oversized
event before finding a prompt, it logs the limit and leaves Last prompt unavailable rather than
mislabeling an older prompt as the last one. Existing selection/refresh invalidation rereads
changed logs; this does not add polling or catalog-wide event reads.

### Agent tasks tab contract

The UI calls these **Agent tasks** to distinguish Copilot's tracked work from actions
assigned to the viewer. The underlying SQLite table and internal reader/model names remain `todos`.

- Selecting a different session returns to **Details** without reading its todo database.
  Opening **Agent tasks** reads a fresh snapshot, as does its dedicated **Refresh** button.
  Leaving and reopening Agent tasks also rereads. There is no polling or automatic todo refresh
  from catalog/watcher updates, and same-session metadata changes retain the existing snapshot.
- Display is a flat, virtualized table of ID, title, wrapped description, raw status, created
  timestamp, and updated timestamp, plus total and per-status counts. Narrow panes scroll
  horizontally; column headers remain visible during vertical scrolling. Timestamps are shown
  exactly as stored (including an offset if present), without assuming a timezone for bare values.
  Headers sort all loaded rows in either direction; **Updated descending** is the initial
  order. Text sorts case-insensitively; dates sort chronologically (bare SQLite timestamps
  are assumed UTC for comparison only). Missing/unrecognized dates sort last in both
  directions, with stable source order for ties. The chosen sort survives refresh, tab
  reopening, and session selection within the app run; sorting never triggers a database read.
  Completed rows remain visible. Unknown statuses are preserved; blank/null
  statuses are counted under `(No status)`. Titles absent from rows display `(No title)`.
- Each read discovers available columns in `todos` and selects only recognized fields
  (`id`, `title`, `description`, `status`, `created_at`, `updated_at`). Added/reordered columns are harmless; missing fields
  produce a warning while available fields remain visible. No recognizable display fields means
  unsupported schema, not a list of invented rows. Renamed columns/tables are not guessed.
- Ordinary tables use rowid order. `WITHOUT ROWID` tables use their declared primary-key order,
  since SQLite cannot provide insertion order for them. Shadowed rowid aliases are avoided.
- A successful empty table, missing database, missing table, unsupported schema, and unreadable
  database have distinct outcomes. Busy, corrupt, and access failures are surfaced and retryable
  with Refresh. No migrations, repairs, writes, checkpoints, or source copies are performed.
- Reads include committed WAL data and hold a deferred read transaction across schema discovery
  and row enumeration. Read-only connections are private, non-pooled, and use a one-second busy
  timeout. Cancellation discards obsolete snapshots. No arbitrary row limit is imposed.

---

## 2. Client detection (which Copilot surface created a session)

Derived solely from `client_name` in `workspace.yaml`:

| `client_name` | `ClientLabel` | Projections |
|---------------|---------------|-------------|
| `github/cli` | **CLI** | `IsCliClient = true` |
| `github/autopilot` | **App** | `IsAppClient = true` |
| *(missing)* | **Unknown** | neither |

In real data: ~199 CLI, ~11 App, ~324 older sessions predate the field (Unknown), ~26 have no
`workspace.yaml`. The details pane shows the raw string (`ClientNameRaw`, e.g. `github/cli`); the
row badge shows the short label. Pills are differentiated by **shape + glyph + text**, not hue
(CLI = filled, App = outlined) for accessibility.

---

## 3. Domain records (`Searchlight.Models`)

### `SessionInfo` (the aggregate)

The central `record`, keyed by `Id` (UUID, prefix stripped), merged from every source. Beyond the
stored fields it exposes computed **projections**:

| Projection | Rule |
|------------|------|
| `DisplayName` | Workspace `Name` if set, else the **full** UUID (never truncated). |
| `ShortId` | First 8 chars of the UUID (compact contexts only). |
| `IsUnnamed` | True when no custom name **and** no workspace name, so `DisplayName` falls back to the bare UUID. Drives the optional "hide unnamed sessions" filter. |
| `ClientLabel` / `IsCliClient` / `IsAppClient` / `ClientNameRaw` | From `workspace.yaml` `client_name` (see §2). |
| `Cwd` | Workspace `Cwd`, falling back to the start-event `Cwd`. |
| `Model` / `ReasoningEffort` / `CopilotVersion` / `FirstPromptPreview` | From the `events.jsonl` head (`Start`). |
| `LastPromptPreview` | From the bounded `events.jsonl` tail scan (`Start.LastUserPrompt`). |
| `UpdatedAt` | Workspace `updated_at` if known, else folder `LastWriteTime`. |
| `Branch` | Native `workspace.yaml` branch, falling back to `session.start.data.context.branch` after lazy event loading. |

### Supporting records

| Type | Shape | Source |
|------|-------|--------|
| `SessionKind` (enum) | `Project` \| `Chat` | folder-name prefix |
| `WorkspaceMetadata` | id, cwd, Git root/repository/host/branch, client_name, name, user-named, summary count, created/updated, remote-steerable, MC ids | `workspace.yaml` |
| `SessionStartInfo` | copilot version, context tier, producer, start time, cwd + Git root/repository/branch, nullable already-in-use, effective model + reasoning effort, first and last user prompts | `events.jsonl` head + bounded tail |
| `CheckpointInfo` | number, title, file path, timestamp | `checkpoints/` |
| `SessionTodo` | id, title, description, raw status, created_at and updated_at as stored strings | `session.db` |
| `SessionTodosResult` | rows, `Status` (`Success`/`MissingDatabase`/`MissingTable`/`UnsupportedSchema`/`Unavailable`), missing fields, message | one explicit todo read |
| `SessionGroup` | `ObservableCollection<SessionInfo>` + `Key` header text | built by `MainViewModel` |
| `AppSettings` | `UseSharedTerminalWindow`, `RunElevated`, `AppendYolo`, `HideEmptySessions`, `HideUnnamedSessions`, `EnableMonitoring` (default false; Dev always monitors) | `settings.json` (app-owned, writable) |

> `settings.json` at `%LOCALAPPDATA%\Searchlight\` is the **only** file the app writes — it
> is app configuration, not user Copilot data.

---

## 4. Grouping model (left pane)

`MainViewModel.ApplyFilter()` runs three stages: **hide filters → text search → grouping**.

### 4a. Hide filters (before search)

Two persisted opt-outs prune `_all` before anything else, so a hidden session is excluded from
search results too:

| Filter | Setting (default) | Predicate | Real-data effect (908 folders) |
|--------|-------------------|-----------|-------------------------------|
| Empty sessions | `HideEmptySessions` (**on**) | `IsEnriched && !HasEvents` | −340 rows |
| Unnamed sessions | `HideUnnamedSessions` (**off**) | `IsUnnamed` | −601 rows (superset of the above) |

Both filters are overridden by explicit user intent — a **pinned** or **renamed** session is always
shown — and neither applies to a row still awaiting enrichment (`IsEnriched == false`), because a
cheap placeholder's `HasEvents = false` means *not yet inspected*, not *empty*; hiding it would make
rows flicker out and back in mid-load. `HiddenCount` feeds a footer notice (`N hidden (not searched)`)
so a fruitless search points at the filters instead of looking like missing data.

Why the empty-session filter is safe to default on: a stub folder holds a `workspace.yaml` and empty
sub-folders but **no `events.jsonl`** — it never held a conversation — and on real data **no named
session lacks events**, so the filter can never hide something the user named.

### 4b. Grouping

Sessions are sorted newest-first and bucketed into contiguous `SessionGroup`s via
`GroupKeyFor(updatedAt, now)`:

- **Last 2 / 4 / 8 / 16 / 32 hours** — relative windows (strict `<` at each boundary).
- Older than 32h → an **absolute calendar-day** header (`"dddd, MMMM d, yyyy"` of the last update).

Groups and rows are both newest-first. The XAML uses a grouped `CollectionViewSource`
(`IsSourceGrouped=true`) with a header template bound to `SessionGroup.Key`.

---

## 5. Mock fixture (for demos / screenshots)

`MockSessionDataSource` produces **deterministic synthetic data** so the UI can be exercised and
screenshotted with **zero** proprietary information. Shape (locked by unit tests):

- **15 sessions**, exactly **6 detailed** (ids ending 01/03/05/07/09/12).
- Each detailed session seeds **3 checkpoints**, **4 todos**
  (`done`, `done`, `in_progress`, `pending`) with descriptive IDs, synthetic descriptions, and timestamps.
- Plain (non-detailed) sessions carry no detail collections.
- Every row sets `HasEvents = IsEnriched = true`, so the default **hide empty sessions** filter never
  hides a demo row. Six rows are deliberately **unnamed** (render as a UUID), which also exercises the
  opt-in `HideUnnamedSessions` filter: 15 visible by default → 9 with it enabled.
- Both client kinds (CLI + App) and both session kinds (Project + Chat) are represented.
- Demo launch logs `data source returned 15 sessions` → `published 15 rows in 9 groups`
  (~0.05 s vs ~6 s live), proving no real-data access.

Reach the mock via the **`Demo` build config** (`USE_MOCK`) or the **`--demo`** runtime flag — see
the [Engineering Guide](./engineering.md).
