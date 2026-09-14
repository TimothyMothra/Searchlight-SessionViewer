# Engineering Guide

Everything you need to build, run, test, and reason about the moving parts of Copilot Sessions
Tray: the projects, the build configurations and compile flags, the run modes, the dependency
set (with pin rationale), and the settings/resume/elevation behavior.

---

## 1. Prerequisites

| Requirement | Value | Notes |
|-------------|-------|-------|
| .NET SDK | **10.0.301** (pinned) | `global.json` at repo root, `rollForward: latestFeature`. |
| OS to build/run the **exe** | Windows 10 1809+ (`10.0.17763.0` min) | WinUI 3 / Windows App SDK. |
| OS to build/run **Core + Tests** | any (`net10.0`) | No WinUI; `dotnet test` runs cross-platform. |
| Architecture | `x64` or `arm64` | Exe declares `Platforms=x64;arm64`, `RuntimeIdentifiers=win-x64;win-arm64`. |
| PowerShell | **7+** (`pwsh` on PATH) | Allocates the daily app build number; also used by the installer. |

Direct builds remain **unpackaged and self-contained** (`WindowsPackageType=None`,
`WindowsAppSDKSelfContained=true`, `SelfContained=true`). The MSIX build entry point enables
single-project packaging while retaining that runtime closure. See [MSIX distribution](msix.md)
for the Dev/Production package identities, signing, and side-by-side installation workflow.

---

## 2. Projects

| Project | Path | TFM | Output | Purpose |
|---------|------|-----|--------|---------|
| **Searchlight** | `src/Searchlight/` | `net10.0-windows10.0.19041.0` | `WinExe` | WinUI 3 tray **host**: XAML UI, tray, Win32 interop, resume/elevation, DI composition root. Windows-only. |
| **Searchlight.Core** | `src/Searchlight.Core/` | `net10.0` | library | Platform-neutral: models, read-only readers, aggregator, data-source façade, view-models, abstractions, DI extension. **Zero WinUI.** |
| **Searchlight.Core.Tests** | `src/Searchlight.Core.Tests/` | `net10.0` | xUnit test | 36 tests over Core; runs on any OS. |

Solution file: `Searchlight.slnx` (XML SLN format) references all three.

---

## 3. Build configurations & compile flags

There are **three build configurations**. `Demo` is the only custom one:

| Configuration | `USE_MOCK` defined? | Optimize | Effect |
|---------------|:-------------------:|:--------:|--------|
| **Debug** | no | no | Normal dev build. Live data source. |
| **Release** | no | yes | Optimized build. Live data source. |
| **Demo** | **yes** | no (`DebugType=portable`) | Compiles the app to boot the **mock** data source unconditionally — for screenshots/demos with no real `~/.copilot` access. |

The `Demo` config is defined only on the **exe** csproj:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Demo'">
  <DefineConstants>$(DefineConstants);USE_MOCK</DefineConstants>
  <Optimize>false</Optimize>
  <DebugType>portable</DebugType>
</PropertyGroup>
```

### How the mock is selected — `ResolveUseMock()`

```csharp
private static bool ResolveUseMock()
{
#if USE_MOCK
    return true;                 // Demo build config → always mock
#else
    return HasFlag("--demo");    // normal build → opt in at runtime
#endif
}
```

So **compile-time** (`Demo` config) and **runtime** (`--demo` flag) both reach the mock; the compile
flag wins hard, the runtime flag is the opt-in for a normal build.

### App build names

Every real host build embeds a name in **`YYYY.MM.DD.##`** format, for example
`2026.09.14.01`. The date is the build machine's local Gregorian date, not the launch date.
`tools\Get-NextBuildVersion.ps1` increments the daily suffix before assembly metadata generation;
Debug, Release, Demo, and publish builds share this worktree's ignored
`src\Searchlight\obj\build-version` counters. Restore and IDE design-time builds do not allocate.
The informational version preserves zero padding; assembly/file versions carry the same numeric
components. Information displays the embedded name without a Git SHA suffix.

Numbers start at **01** on each new day. Allocation is locked across concurrent processes and
counter replacement is atomic. Failed builds can consume a number. To preserve the exact two-digit
format, build 100 fails explicitly rather than wrapping or reusing a number. Counters are local,
not a globally coordinated release sequence: a new worktree or deleting its counter directory
starts again at 01. Keep that directory to retain same-day numbering.

MSIX builds instead accept an allocated build name. Automatic Dev allocation is shared across
worktrees under `%LOCALAPPDATA%\Searchlight.Build\Dev`; Production requires an explicit release
name. Package versions and additional-architecture builds follow the [MSIX version contract](msix.md#versions).

Run `pwsh -NoProfile -File tools\Test-BuildVersion.ps1` for isolated checks of incrementing,
date rollover, culture-independent formatting, exhaustion, corrupt state, and concurrent allocation.

> **MSBuild/XAML gotcha:** XML/XAML comments cannot contain a double-hyphen (`--`). Both MSBuild
> (`MSB4025`) and the XAML compiler (`WMC9997`) reject it. When documenting the `--demo`/`--no-tray`
> flags inside a `.csproj` or `.xaml` comment, reword to avoid a literal `--`.

---

## 4. Runtime flags (command line)

The exe reads the **raw process command line** via `Environment.GetCommandLineArgs()` because
unpackaged WinUI does **not** surface args through `LaunchActivatedEventArgs`.

| Flag | Behavior |
|------|----------|
| *(none)* | Tray mode. Window hides to tray on close; only tray **Exit** quits. |
| `--no-tray` | Plain window, **no** tray icon; closing the window exits the process. (For a non-tray/cross-platform-style presentation.) |
| `--demo` | Boot against the synthetic `MockSessionDataSource` at runtime (15 deterministic sessions). Ignored — always-on — in the `Demo` build. |
| `--no-admin` | Ignore the saved elevation preference for this launch and disable its toggle without changing settings. Launch from a standard-user shell; an already elevated invocation exits with a diagnostic instead of claiming to be non-admin. |

Flags compose, e.g. `--no-tray --demo`.

For unattended demo/UI work, use `--no-tray --demo --no-admin` from a non-elevated
terminal. This avoids a UAC prompt even when the normal app is configured to run as administrator.

---

## 5. Build / run / test commands

All paths are absolute for copy-paste.

```powershell
# ── Build ──────────────────────────────────────────────────────────────────
# Whole solution (Debug)
dotnet build C:\REPOS\Searchlight\Searchlight.slnx -c Debug

# Release, explicit RID (self-contained publish-style build)
dotnet build C:\REPOS\Searchlight\src\Searchlight\Searchlight.csproj -c Release -r win-x64

# Demo build (mock data baked in)
dotnet build C:\REPOS\Searchlight\src\Searchlight\Searchlight.csproj -c Demo

# ── Run ────────────────────────────────────────────────────────────────────
# Tray app against real ~/.copilot data
dotnet run --project C:\REPOS\Searchlight\src\Searchlight -c Debug

# Plain window, no tray
dotnet run --project C:\REPOS\Searchlight\src\Searchlight -c Debug -- --no-tray

# Synthetic data (safe for screenshots) — either of:
dotnet run --project C:\REPOS\Searchlight\src\Searchlight -c Demo
dotnet run --project C:\REPOS\Searchlight\src\Searchlight -c Debug -- --demo

# ── Test ───────────────────────────────────────────────────────────────────
dotnet test C:\REPOS\Searchlight\src\Searchlight.Core.Tests\Searchlight.Core.Tests.csproj

# ── Install / deploy ───────────────────────────────────────────────────────
# Publishes self-contained to %LOCALAPPDATA%\Searchlight\app and creates the
# Start Menu / desktop / run-at-login shortcuts. EXIT THE APP FIRST (tray icon
# -> Exit; the window's X only hides it) or the script aborts on its pre-flight.
pwsh -File C:\REPOS\Searchlight\tools\install.ps1
pwsh -File C:\REPOS\Searchlight\tools\install.ps1 -Action Uninstall
```

**Deploying is what makes a change visible.** Local review uses the Dev MSIX workflow
in [msix.md](msix.md). `dotnet build`/`run` only touch `bin\`. For an explicitly unpackaged
installation, the Start Menu, desktop, and login shortcuts point at
`%LOCALAPPDATA%\Searchlight\app`, so `install.ps1` must republish there.
The script refuses to run while Searchlight is running, and swaps the install folder aside
rather than deleting it in place — a `Remove-Item -Recurse` over a locked file deletes
everything it *can* before erroring, which silently leaves a half-deleted, unlaunchable
install behind. Shared settings and notes live under `%USERPROFILE%\.searchlight`,
outside both the unpackaged install folder and MSIX-managed package storage.

**Diagnostics:** the app writes breadcrumbs to `%TEMP%\Searchlight.<channel>.log`
(`Dev`, `Production`, or `Unpackaged`). Core routes its
own breadcrumbs there through the `CoreLog.Sink` seam. A healthy live launch logs e.g.
`published NNN rows in MM groups (total NNN)`; a mock launch logs `data source returned 15 sessions`.

---

## 6. Dependencies & pin rationale

### Exe (`Searchlight`)

| Package | Version | Why this version |
|---------|---------|------------------|
| Microsoft.WindowsAppSDK | 1.8.260529003 | **First** WinAppSDK line whose XamlCompiler handles .NET 10 reference assemblies. 1.6's net472 XamlCompiler aborts (exit 1, no diagnostic) against `net10.0` refs. |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 | Matches the WinAppSDK toolchain. |
| H.NotifyIcon.WinUI | 2.4.1 | Tray icon (WinUI has no native tray). Depends on WinAppSDK ≥ 1.6, accepts 1.8. |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM (`ObservableObject`, `[ObservableProperty]`, `RelayCommand`). |
| Microsoft.Data.Sqlite | 9.0.0 | Read-only reads of `index.db` / `session.db`. |
| **SQLitePCLRaw.lib.e_sqlite3** | **3.50.3** | **Security pin.** NU1903 (GHSA-2m69-gcr7-jv3q / CVE-2025-6965) affects the native SQLite binary ≤ 2.1.11. The fix ships only in the realigned native package 3.50.3; pinning just the native lib clears the advisory while keeping the 2.1.11 managed provider Microsoft.Data.Sqlite needs. *ASSUMPTION: 3.50.3 native is ABI-compatible with SQLitePCLRaw.core 2.1.11.* |
| YamlDotNet | 16.3.0 | Parse `workspace.yaml`. |
| Microsoft.Extensions.DependencyInjection | 9.0.0 | DI container (`BuildServiceProvider`) — the exe is the composition host. |

### Core (`Searchlight.Core`)

Same data/parse/MVVM set **minus** WinUI/tray, and DI **Abstractions only** (Core declares services;
the host builds the provider):

CommunityToolkit.Mvvm 8.4.0 · Microsoft.Data.Sqlite 9.0.0 · SQLitePCLRaw.lib.e_sqlite3 3.50.3 ·
YamlDotNet 16.3.0 · Microsoft.Extensions.DependencyInjection.**Abstractions** 9.0.0.
Plus `InternalsVisibleTo("Searchlight.Core.Tests")`.

### Tests (`Searchlight.Core.Tests`)

Microsoft.NET.Test.Sdk 17.11.1 · xunit 2.9.2 · xunit.runner.visualstudio 2.8.2. References Core only.

### Other suppressions

- **`MVVMTK0045`** (`NoWarn` in both projects): the app uses field-based `[ObservableProperty]`
  (partial-property generation isn't emitting in this SDK combo). MVVMTK0045 only matters for AOT
  WinRT marshalling; this app is self-contained but not AOT-published.
- **`AllowUnsafeBlocks=true`** (exe): required by the `LibraryImport` source generator used in
  `Interop\ForegroundWindowHelper`.

---

## 7. Settings, resume & elevation behavior

### Settings (`AppSettings`)

Persisted as JSON at `%USERPROFILE%\.searchlight\settings.json`, shared by the channels
and auto-saved on any property
change (`SettingsService`). Options are exposed via the titlebar gear's full-window Settings pane:

| Setting | Default | Effect |
|---------|:-------:|--------|
| `UseSharedTerminalWindow` | **on** (opt-out) | Resume opens a **new tab** in your most-recently-used Windows Terminal window (`-w last`); off → each resume opens its **own** new window (`-w new`). |
| `RunElevated` | **off** | Relaunch the app elevated/non-elevated. A process can't change integrity level in place, so toggling **restarts** the app (elevate via `runas`; de-elevate by relaunching through `explorer.exe`). Needed because a non-elevated `wt -w` can't attach a tab to an **Admin** Terminal (UIPI). |
| `AppendYolo` | **off** (opt-in) | Append `--yolo` to the default resume command (auto-approves tool actions). Custom templates control their own flags. Does **not** restart the app. |
| `UseCustomResumeCommand` | **off** | Replace the command inside the terminal with the custom template; terminal/window handling remains unchanged. |
| `CustomResumeCommand` | `copilot --resume={sessionId}` | Single-line cmd.exe command with a required `{sessionId}` token. Saved even while custom mode is off. |
| `HideEmptySessions` | **on** (opt-out) | Hide sessions with no `events.jsonl` — folders Copilot provisions on project open that never held a conversation. Information-lossless: they contain no messages, and no *named* session lacks events. |
| `HideUnnamedSessions` | **off** (opt-in) | Hide every session still showing a bare UUID. Stronger than the above — also hides older sessions that hold real conversations but predate auto-naming. |

Both hide filters apply **before** the search box, so hidden sessions are excluded from search
results too; the footer shows an `N hidden (not searched)` notice so a fruitless search points at
the filters rather than looking like missing data. **Pinned and renamed sessions are never hidden**
by either filter, and neither is a row the two-phase load hasn't enriched yet (`IsEnriched == false`),
since absent flags there mean *unknown*, not *empty*. Flipping either toggle re-filters live — no reload.

Real-data scale (908 folders): `HideEmptySessions` alone drops 340 rows → **568 visible**; enabling
both drops 601 → **307 visible**.

When elevated, the titlebar shows a **white UAC shield** at the far left (matching Windows Terminal's
admin affordance).

### Settings and Information navigation

The titlebar keeps the **Settings** gear followed immediately by a circled-i **Information**
button, separate from its draggable label region. Each opens a full-size pane below the titlebar,
replacing the search bar, session list/details/notes, and session status footer rather than opening
a popup. Both panes scroll vertically as needed; text wraps without horizontal scrolling.
Settings retain their existing immediate auto-save and live-filter behavior.

**Back** or **Escape** returns to the existing session view without recreating its controls,
preserving search, selection, notes, and scroll position (subject to live data and filter updates).
The titlebar buttons can switch directly between panes; keyboard focus moves to Back on entry
and returns to the corresponding titlebar button on exit.

Information shows the embedded date-based build name, a link to the
[GitHub repository](https://github.com/TimothyMothra/Searchlight-SessionViewer), app purpose,
basic session/task/resume guidance, local-data storage behavior, and tray versus no-tray behavior.

### Resume (`ResumeLauncher`)

The **Resume command** Settings group contains the existing `--yolo` toggle, custom-mode
switch, one template text box, a selectable monospace preview panel, validation warning,
and **Restore default command** action. The preview uses the empty GUID
`00000000-0000-0000-0000-000000000000` for the session ID:

```text
copilot --resume=00000000-0000-0000-0000-000000000000 --yolo
```

Custom mode replaces the complete command **inside** the terminal, not the terminal launcher.
Use the exact, case-sensitive `{sessionId}` token, for example:

```text
"C:\My Tools\resume.cmd" --session "{sessionId}"
```

The template owns all flags; the `--yolo` toggle is disabled in custom mode and is not
automatically appended. Switching back preserves its previous value. Restoring the default
turns off custom mode and resets the template without changing the saved `--yolo` preference.
Editing only saves settings and updates the preview; it never executes the command.

Templates must contain the token, have no control characters/newlines, and be at most 3000
characters including full-UUID expansion (leaving room for encoded Windows command-line transport). Invalid templates
show a warning and cannot be launched. At execution the selected ID must be a UUID; every
token occurrence is replaced with that ID. Arbitrary remaining shell syntax is intentional
user-authored code and runs with the app's current permissions, including elevation.

Default mode retains the existing launch behavior:

```
wt.exe -w <last|new> new-tab --title "<name>" cmd /k copilot --resume=<session-id> [--yolo]
```

- The CLI has **no bare `resume` subcommand** — the correct syntax is `copilot --resume=<id>`
  (alias `-r`). An earlier `copilot resume <id>` form produced *"Invalid command format"*.
- If Windows Terminal (`wt.exe`) isn't available, falls back to `cmd.exe /k`.
- Custom commands use an encoded Windows PowerShell transport inside Windows Terminal so
  its semicolon parser and quoting reconstruction cannot rewrite the template. The transport
  starts `cmd.exe /s /k` with the original expanded text; no temporary scripts are written.
  Direct Command Prompt fallback uses the same expanded command without that transport.
- Live and mock resume paths use the same Core command builder as the preview. Launch errors
  and invalid templates are surfaced in the session's action/status message.
- Cross-integrity-level window reuse is blocked by the OS: to reuse an **elevated** main Terminal,
  the app must also be elevated (the `RunElevated` toggle).

---

## 8. Icon assets

- `Assets/app.ico` — multi-res (16/32/48/256) app icon: embedded as the exe/taskbar/Alt-Tab icon
  (`<ApplicationIcon>`), the tray icon (`H.NotifyIcon` `IconSource` loaded by **absolute path**
  because unpackaged `ms-appx:///` is unreliable), and the window titlebar icon (`AppWindow.SetIcon`).
- `tools/make_icon.py` — Pillow generator that produces `app.ico` + `app_{256,48,32,16}.png`
  previews (8× supersample + Lanczos downscale). Deliberately **not** the trademarked Copilot logo.

---

## 9. Testing approach

The Core extraction is what makes the app testable — **no WinUI, no real `~/.copilot` access**:

- `TestDoubles.InlineUiDispatcher` runs the load pipeline synchronously (`Post(a) => a()`).
- `GroupKeyForTests` — boundary tests for the recency-bucket ladder (strict `<` at 2/4/8/16/32h;
  locale-independent absolute-date fallback).
- `SessionInfoProjectionTests` — pure projections (`DisplayName` full-Id fallback, `ShortId`,
  `ClientLabel`/`IsCli`/`IsApp`, `ClientNameRaw`, `Cwd` fallback, `UpdatedAt` precedence).
- `MockSessionDataSourceTests` — locks the fixture shape (15 sessions, 6 detailed, seeded
  checkpoints/snapshots/todos).
- `MainViewModelGroupingTests` — integration: a real `MainViewModel` over the mock (no FS/WinUI)
  asserting counts, grouping, newest-first ordering, and branch filtering.

**Gate:** `dotnet test` → **36 passed, 0 failed**. This is the automated correctness gate while
interactive verification is blocked (see constraints).

---

## 10. Known constraints

- **No git.** The repo is a scratch folder with no version control — changes are **not** revertible.
  Work carefully.
- **Interactive/visual verification is blocked** by a locked desktop (LogonUI) in the dev
  environment. Build + `dotnet test` + log inspection are the only automated gates; UI/tray/resume
  behavior is verified by code inspection and the user's own eyeball checks.
- **Unpackaged specifics:** command-line args come from `Environment.GetCommandLineArgs()`; icons
  load by absolute path; the app is self-contained so no WinAppSDK runtime install is required.
