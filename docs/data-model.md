# Data Model

How the app turns the on-disk `~/.copilot` layout into the in-memory domain it renders. Two halves:
the **on-disk sources** (and the reader that consumes each), and the **domain records** the readers
produce. Everything is **read-only** and **null-safe** — sessions vary wildly in which files exist,
so every enrichment field degrades to empty/null rather than throwing.

---

## 1. On-disk sources → readers

`CopilotPaths` resolves every path under `~/.copilot`; the app never writes user Copilot data.

| Source (under `~/.copilot`) | Reader | What it yields | Cost |
|-----------------------------|--------|----------------|------|
| `session-state/<id>/` folders | `SessionStateScanner` | One base `SessionInfo` per folder: `Id`, `FolderName`, `FolderPath`, `Kind`, `LastWriteTime`, presence flags (`IsInUse` via `inuse.<PID>.lock`, `HasPlan`, `HasSessionDb`, `HasCheckpoints`, `HasEvents`) and `IsEnriched`. Splits `optimistic-chat-` prefix → `Chat`, else `Project`. | cheap (bulk) |
| `session-state/<id>/workspace.yaml` | `WorkspaceYamlReader` | `WorkspaceMetadata` (name, cwd, `client_name`, created/updated, user-named, summary count, MC ids) via YamlDotNet. | cheap (bulk) |
| `session-state/<id>/events.jsonl` | `EventsJsonlReader` | `SessionStartInfo` — head-parse only (≤ `MaxLines`): `session.start` baseline + latest `session.model_change` + first `user.message` preview. Full ~300 KB log never materialized. | heavy (lazy) |
| `status-snapshots/index.db` (table `snapshots`) | `SnapshotIndexReader` | Branch + snapshot count per session (bulk index scan); `SnapshotInfo` rows on demand. Read-only SQLite. | cheap bulk / heavy detail |
| `journal/<YYYY-MM>.md` | `JournalReader` | `JournalEntry` rows from the pipe table `\| time \| session_id \| branch \| cwd \| activity \|`; last row wins per session → `JournalActivity`. | cheap (bulk) |
| `session-state/<id>/checkpoints/NNN-title.md` + `index.md` | `CheckpointsReader` | `CheckpointInfo` list; prefers the fuller title from `index.md`'s table over the truncated file name. | heavy (lazy) |
| `session-state/<id>/session.db` (tables `todos`, `session_state`) | `SessionDbReader` | `SessionTodo` list + session-state key/values. Read-only SQLite. | heavy (lazy) |

**Two-tier loading.** `SessionAggregator` / `LiveSessionDataSource` run a cheap **bulk pass**
(`LoadAll` = folder scan + workspace.yaml + bulk snapshot-index + journal) across *all* sessions, then
defer the **heavy per-session** reads (events head-parse, checkpoints, snapshots, todos) to selection.
This keeps ~500-folder scans responsive.

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
| `UpdatedAt` | Workspace `updated_at` if known, else folder `LastWriteTime`. |
| `SnapshotCount` / `Branch` / `JournalActivity` | Best-effort enrichment. |

### Supporting records

| Type | Shape | Source |
|------|-------|--------|
| `SessionKind` (enum) | `Project` \| `Chat` | folder-name prefix |
| `WorkspaceMetadata` | id, cwd, client_name, name, user-named, summary count, created/updated, remote-steerable, MC ids | `workspace.yaml` |
| `SessionStartInfo` | copilot version, context tier, producer, start time, cwd, already-in-use, effective model + reasoning effort, first user prompt | `events.jsonl` head |
| `CheckpointInfo` | number, title, file path, timestamp | `checkpoints/` |
| `SnapshotInfo` | snapshot id, session id, timestamp (raw + parsed), cwd, branch, file path, `SourceTrigger` (`ask_user`/`handoff`/`task_complete`/`long_turn`/`on_demand`/`checkpoint`) | `status-snapshots/index.db` |
| `SessionTodo` | id, title, status (`pending`/`in_progress`/`done`/`blocked`) | `session.db` |
| `JournalEntry` | time, session id, branch, cwd, activity | `journal/<YYYY-MM>.md` |
| `SessionGroup` | `ObservableCollection<SessionInfo>` + `Key` header text | built by `MainViewModel` |
| `AppSettings` | `UseSharedTerminalWindow`, `RunElevated`, `AppendYolo`, `HideEmptySessions`, `HideUnnamedSessions` | `settings.json` (app-owned, writable) |

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
- Each detailed session seeds **3 checkpoints**, **3 snapshots** (`SnapshotCount = 3`), **4 todos**
  (`done`, `done`, `in_progress`, `pending`).
- Plain (non-detailed) sessions carry no detail collections.
- Every row sets `HasEvents = IsEnriched = true`, so the default **hide empty sessions** filter never
  hides a demo row. Six rows are deliberately **unnamed** (render as a UUID), which also exercises the
  opt-in `HideUnnamedSessions` filter: 15 visible by default → 9 with it enabled.
- Both client kinds (CLI + App) and both session kinds (Project + Chat) are represented.
- Demo launch logs `data source returned 15 sessions` → `published 15 rows in 9 groups`
  (~0.05 s vs ~6 s live), proving no real-data access.

Reach the mock via the **`Demo` build config** (`USE_MOCK`) or the **`--demo`** runtime flag — see
the [Engineering Guide](./engineering.md).
