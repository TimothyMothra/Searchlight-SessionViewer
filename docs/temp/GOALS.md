---
date: 2026-09-14T14:07:46-07:00
repo: timothymothra-ubiquitous-garbanzo
branch: timothymothra-settings-and-information-panes
worktree: C:\REPOS\copilot-worktrees\main\timothymothra-ubiquitous-garbanzo
source: copilot-cli set-goals skill
status: implemented-local-qualification-pending
---

# Goals — MSIX Distribution and Side-by-Side Development

## Goal

Distribute Searchlight as an MSIX and support a locally built, sideloaded
Searchlight Dev alongside the production app. Both channels must share
Searchlight-owned settings, pins, display names, and notes, while continuing
to read Copilot history without modifying it.

## Success Criteria / Verification

- [x] A documented command builds a signed, installable Dev MSIX from this repository,
  including the required .NET, WinUI, native SQLite, and image/resource payload.
- [ ] Production and Dev have distinct package families and visible names; both
  install and run simultaneously, with separate tray icons and instance activation.
- [ ] Installing, updating, or removing Dev leaves Production's registration and
  executable untouched. A second launch activates only its own channel.
- [ ] Both channels read and write the same Searchlight-owned data. Concurrent saves
  neither corrupt files nor silently discard another instance's unrelated changes.
  Conflicting edits to the same note are detected and surfaced.
- [ ] Shared data survives removal of either channel; upgrades preserve compatibility.
  Existing unpackaged data is migrated without destructive moves or overwriting it.
- [ ] Search, details, tasks, notes, resume, clipboard, titlebar panes, tray behavior,
  and optional administrator mode work from an installed package on supported systems.
- [ ] A clean-machine install has a complete dependency closure; x64 and ARM64 artifacts
  are built and their native payloads verified, with runtime coverage on each architecture.
- [ ] Higher-version updates work within each channel. Manifest versions are valid
  numeric quads, and the Information pane retains its zero-padded date-based build name.
- [ ] Signing failures, missing prerequisites, incompatible shared-data schemas, and
  installation failures produce actionable errors rather than apparent success.

## Scope

**In scope:**
- Source-based single-project MSIX packaging for the existing WinUI host.
- Two stable channels: Production and Dev; Dev means live-data local development,
  not the existing synthetic Demo build configuration.
- Shared user data, channel-specific process identity, and clear Dev branding.
- Local development signing, explicit certificate-trust setup, package validation,
  sideload installation, update, removal, and launch instructions.
- A production packaging path parameterized by identity and signing credentials.
- Existing unpackaged build support during the transition.

**Out of scope:**
- Destination repository discovery, submission, account setup, certification policy,
  production signing-provider selection, or uploading any artifacts.
- Building a hosted update service; an App Installer feed can be considered later.
- Rewriting the app as UWP, adding an app container, or changing the Core/host boundary.
- Installing multiple independent Dev worktree variants simultaneously.
- Creating production credentials or changing device trust without explicit approval.
  Development certificate creation/trust and local Dev installation were separately approved.

## Phases

### Phase 1 — Packaging and Compatibility Spike

**Objective:** Prove that the current host can run as a self-contained packaged desktop app.

**Verification:** Build an x64 prototype through supported WinUI single-project MSIX
tooling; validate the manifest/payload, then install on a consented test machine.
Exercise read-only Copilot access, tray activation, assets, clipboard, terminal
resume, elevation/de-elevation, and shared-storage candidates.

**Notes:** Add a package manifest distinct from the existing Win32 `app.manifest`.
Retain `WindowsAppSDKSelfContained` and .NET self-contained publishing initially.
Prefer a full-trust desktop application, not an app-container conversion.
Determine the actual packaging toolchain and OS floor rather than assuming
the existing `dotnet build` command is sufficient for MSIX generation.
If optional elevation needs `allowElevation`, record the capability and validate
its behavior; do not remove administrator functionality silently.

### Phase 2 — Channel Identity and Shared State

**Objective:** Allow Production and Dev to coexist while deliberately sharing user data.

**Verification:** Both run concurrently; launching one never activates the other.
Settings, pins, display names, and notes are visible across channels after refresh;
parallel saves, conflicting note edits, migration, and uninstall are tested.

**Notes:** Use distinct stable package Names (illustratively `Searchlight` and
`Searchlight.Dev`, final publisher/name values pending). Do not try to achieve
coexistence by changing only package version, configuration, or display name.
Scope the current mutex and activation event by channel/package family, not by
package full name, which changes on update. Separate logs and tray/title branding.
Any shared-data synchronization lock must remain common to both channels.

Use the approved shared storage root `%USERPROFILE%\.searchlight`, outside virtualized
AppData, with copy-only migration from the legacy location. Do not assume identical
AppData path strings inside different MSIX packages mean identical physical files.

Harden whole-file settings saves and debounced note writes for multiple processes,
including external-change refresh, atomic writes, conflict detection, and
cross-version schema compatibility. Shared elevation preferences must not cause
uncontrolled cross-instance restart loops.

### Phase 3 — Build Artifacts, Versioning, and Signing

**Objective:** Produce repeatable Dev and production-channel packages with explicit identities.

**Verification:** Generate and inspect x64 and ARM64 MSIX artifacts; verify signatures,
publisher matching, contents, and architecture. Produce successive packages and prove
that update ordering is correct across different local worktrees.

**Notes:** A packaging entry point should select channel, architecture, configuration,
build name/package version, and signing certificate explicitly. Generate transformed
manifests in intermediate output, not by repeatedly editing the tracked manifest.
Default local packaging to Dev and reject accidental production-targeted installs.
Allocate one package release version before building multiple architectures.

Keep the display name `YYYY.MM.DD.##`. The current counter under each worktree's
`obj\build-version` is not sufficient as the authoritative published package sequence:
different worktrees can produce equal or lower versions. Use a channel-wide local
sequence for the one Dev installation, and a release-controlled sequence for Production.
For ordinary sideloading, a numeric version such as `2026.9.14.4` can represent the
display name `2026.09.14.04`. Preserve the option to map to a fourth-component-zero
version if a future distribution contract requires it.

Use a stable development publisher and development certificate; import only the
public certificate for trust, with explicit user consent. Keep private keys,
passwords, and production credentials outside the repository. Production signing
remains a parameter, not a hard-coded self-signed certificate.

### Phase 4 — Safe Local Install and Review Loop

**Objective:** Make build, Dev sideload/update, and launch the normal local review workflow.

**Verification:** Install a Dev MSIX, make a visible change, then update and launch
that channel only while Production remains installed. Confirm package family,
installed version, and payload before reporting completion.

**Notes:** Use Windows package deployment (`Add-AppxPackage` or App Installer), not
copying binaries into `%LOCALAPPDATA%\Searchlight\app`. Existing `tools\install.ps1`
remains explicitly unpackaged until a replacement workflow is validated.
Stop only the target channel, prefer graceful exit, and ask before force-stopping.
Never uninstall Production or enable forced downgrades to make a Dev install succeed.
Use package activation rather than hard-coded `WindowsApps` executable paths.

### Phase 5 — Distribution Qualification and Handoff

**Objective:** Qualify the package lifecycle and document a generic distribution handoff.

**Verification:** Complete install, launch, update, concurrent-channel, shared-data,
uninstall, clean-machine, and architecture scenarios. Retain package identity/version,
signature evidence, dependencies, and any failed or untested cases in the handoff.

**Notes:** Replace installer-created Start Menu integration with manifest registration.
Use a manifest startup task for packaged login behavior and respect user disabling it.
Recommend Dev startup disabled by default so both channels do not auto-launch.
Keep production startup behavior an explicit decision. Qualify these behaviors
without selecting or submitting to a destination repository.

## Risks & Open Questions

- Shared storage uses the approved `%USERPROFILE%\.searchlight` path. Unmodified legacy
  unpackaged releases still write the old path; they do not participate in ongoing sharing.
- `unvirtualizedResources` is restricted and is not recommended by Microsoft for
  general-purpose scenarios. Directory-specific exclusions require Windows 11;
  the older opt-out starts at Windows 10 version 1903. Searchlight currently declares
  a Windows 10 version 1809 minimum. Do not raise that floor without agreement.
- `allowElevation` is restricted. Runtime compatibility must be tested; a future
  distribution service may impose additional restrictions. The application must
  remain usable by standard users.
- Simultaneous Production/Dev writes and divergent data schemas make sharing more
  work than package-private storage. Backups and compatibility rules are required.
- The current installer can stop all processes named Searchlight. A channel-aware
  MSIX workflow must never reuse that broad process selection.
- Package identity/publisher values and production certificate ownership remain unset.
- One stable Dev identity supports one installed Dev revision at a time. Lower-version
  worktree builds need fresh package versions, not replacing Production or silently
  removing/reinstalling Dev and its state.
- An MSIX file does not configure automatic updates by itself. Repository-managed
  updates and optional `.appinstaller` hosting remain separate distribution choices.

## Relevant Files / Components

- `src\Searchlight\Searchlight.csproj` — packaging selection, runtime closure, assembly metadata.
- `src\Searchlight\app.manifest` — Win32 process manifest; not an MSIX package manifest.
- `src\Searchlight\App.xaml.cs` — fixed mutex/event names, settings bootstrap, log path, tray, activation.
- `src\Searchlight\MainWindow.xaml.cs` and `Views\MainView.xaml` — visible channel branding and build information.
- `src\Searchlight\Services\ElevationHelper.cs` — executable-path `runas` and Explorer relaunch.
- `src\Searchlight\Services\ResumeLauncher.cs` — terminal launch, PATH, CLI arguments, integrity level.
- `src\Searchlight.Core\Services\SettingsService.cs` — shared settings/pins/names, whole-file persistence.
- `src\Searchlight.Core\Services\NotesService.cs` and `ViewModels\MainViewModel.cs` — shared notes and debounced saves.
- `src\Searchlight.Core\Services\CopilotPaths.cs` — read-only source paths; preserve this invariant.
- `src\Searchlight\Assets` — package visual assets and existing executable/tray imagery.
- `tools\install.ps1` — current unpackaged installer; not an MSIX deployment mechanism.
- `tools\Get-NextBuildVersion.ps1` — worktree-local daily counter requiring a packaging-specific contract.

### Microsoft References

- [Single-project WinUI MSIX](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix)
- [Package identity and package families](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview)
- [Self-contained Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [Signing overview](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview)
- [Development certificates](https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing)
- [Packaged desktop runtime behavior](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
- [Flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)
- [Restricted capabilities](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- [Startup task declaration](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop-startuptask)
- [App Installer updates](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings)
- [Store-specific package/version requirements, only if later applicable](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)

## Assumptions

- Production and Dev are separate installed apps, not alternative versions of one
  package family. The exact package identity values shown above are placeholders.
- Both channels share Searchlight-owned data by explicit user choice, and both read
  the same existing Copilot history. Neither writes into `~/.copilot`.
- The initial artifact is an x64 MSIX; ARM64 qualification follows within this scope.
  A multi-architecture bundle is optional, not required for the first sideload.
- Source-based single-project MSIX is the preferred path because Searchlight has one
  application executable. A separate packaging project is a fallback if the spike
  exposes an actual toolchain or payload limitation.
- Scope approval does not authorize certificate trust changes, production publication,
  destructive migration, or removal of existing installations.

## Implementation and Qualification State

- Single-project MSIX tooling produces signed x64 and ARM64 Dev artifacts. The Dev
  certificate was created and trusted with explicit approval; private keys remain
  non-exportable in the Windows certificate store.
- Dev installs and in-place upgrades have succeeded alongside the unchanged legacy
  unpackaged installation. The installed executable, assembly, and resource package
  are compared to the MSIX payload. Production identity guards are exercised.
- Shared persistence is implemented; 200 Core tests pass, including multi-process
  settings coordination, migration, conflict recovery, and shutdown refusal when
  drafts cannot be secured. The host checks the flush result before tearing down DI.
- The packaged elevated app loads live sessions. Packaged image loading uses
  application URIs, and the tray uses ICO rather than PNG decoding.
- Final qualification still needs actual Production/Dev package co-installation,
  interactive clipboard/resume/de-elevation and shared-edit scenarios, uninstall,
  clean-machine dependency tests, and execution on ARM64 hardware. The installed
  app is elevated, so medium-integrity UI automation cannot drive it.
- The production build path was exercised with unsigned placeholder identity inputs;
  it is not a signed, published, or installed production release.
