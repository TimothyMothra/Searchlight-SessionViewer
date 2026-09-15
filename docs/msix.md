# MSIX builds and side-by-side development

Searchlight supports unpackaged builds and single-project WinUI MSIX packaging.
**Searchlight Dev** has a different package family from Production: installing a
Dev build does not update or remove Production. Dev is a distribution channel,
not the synthetic `Demo` configuration.

## Prerequisites

- Windows build machine with the repository's .NET SDK, PowerShell 7, Visual Studio
  MSBuild, WinUI/MSIX packaging tooling, and a Windows SDK containing SignTool.
- x64 or ARM64 target; the scripts currently discover the x64 build/signing tools.
- A code-signing certificate whose subject exactly matches the manifest publisher.
  Package installation also requires that the device trusts the signer.
- Copilot CLI and Windows Terminal are external applications, not included in MSIX.
  Browsing does not require them; resume needs Copilot on PATH and falls back to cmd
  if Windows Terminal is unavailable.

The package includes .NET, Windows App SDK, native SQLite, icons, and compiled XAML
resources. It is not a single-file executable. Package file validation checks the
payload and native architecture; clean-machine execution still needs its own test.

## One-time development certificate

After approving creation and trust, run from an elevated PowerShell:

```powershell
pwsh -NoProfile -File .\tools\Initialize-DevCertificate.ps1 -Trust
```

The script creates or reuses `CN=Searchlight Development` in `CurrentUser\My`.
The key is non-exportable. Only the public certificate is exported under
`%LOCALAPPDATA%\Searchlight.Build\signing` and imported into
`LocalMachine\TrustedPeople`, **not Trusted Root Certification Authorities**.
It never creates a production certificate. Save the returned thumbprint for builds.

Certificate creation and trust are separate from ordinary builds. The script
supports `-WhatIf`; without `-Trust` it does not change deployment trust.
Development certificates expire after one year. To renew, rerun setup and use the
new thumbprint. Untimestamped Dev packages must be rebuilt/re-signed after expiry
before installing them on another machine; already-installed apps can still run.

## Build, install, and review Dev

Run from the repository root:

```powershell
$certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq 'CN=Searchlight Development' -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

$build = .\tools\Build-Msix.ps1 -CertificateThumbprint $certificate.Thumbprint
.\tools\Install-DevMsix.ps1 -Path $build.Path
```

The installer verifies signature, identity, version, and the installed executable,
managed assembly, and `resources.pri`. It launches through package activation.
It only accepts `TimothyMothra.Searchlight.Dev` with publisher
`CN=Searchlight Development`; passing a Production package is rejected.

The installer first requests Dev's normal graceful exit, which flushes pending notes.
For older builds without that handshake, exit Dev from its tray menu before updating.
`-StopRunning` asks Windows to shut down only the target
package's applications. The script never force-stops all processes named Searchlight.
Copilot is authorized to use this fallback for Dev without repeated prompts;
graceful shutdown remains preferred to preserve pending edits. This authorization
does not include stopping or replacing Production.
If a cooperating Dev instance refuses to exit because notes cannot be saved or
recovered, installation stops even with `-StopRunning`; resolve the visible warning
rather than discarding those drafts.
`-NoLaunch` installs without launching; `-WhatIf` performs validation without deployment.
The inbox Appx cmdlets run in Windows PowerShell, invoked automatically from PowerShell 7.

Package output is under:

```text
artifacts\msix\<channel>\<numeric-package-version>\<architecture>\
```

The generated manifest and package logos are under `src\Searchlight\obj\MsixInputs`.
Builds do not rewrite the tracked manifest template. Artifacts, intermediates, and
private certificate files are ignored by git.

If a build reports missing restored assets, repeat with `-Restore`. This invokes
Visual Studio MSBuild's restore before building. `-Unsigned` is available for
payload inspection but is rejected by the installer.

## Architectures and production output

To build another architecture for the **same release**, pass the allocated name:

```powershell
.\tools\Build-Msix.ps1 -Architecture arm64 -BuildName $build.BuildName `
    -CertificateThumbprint $certificate.Thumbprint
```

Production requires its final package name, publisher, and certificate. Its build name
comes from the same sequence as Dev unless an allocated release name is supplied.
It does not use Dev's identity or automatically install:

```powershell
.\tools\Build-Msix.ps1 -Channel Production -Architecture x64 `
    -PackageName '<assigned-production-name>' -Publisher '<certificate-subject>' `
    -BuildName '2026.09.14.05' -CertificateThumbprint '<production-thumbprint>' `
    -TimestampUrl 'https://<your-timestamp-service>'
```

Use a trusted production signing arrangement; do not distribute the Dev
self-signed certificate as a production trust solution. Timestamping is recommended.
An unsigned Production artifact can be generated explicitly if the destination signs
packages itself. Destination submission and publishing policy are not implemented.
Each build invocation produces one architecture-specific MSIX. Use the bundling step
below for a single multi-architecture distribution file; hosted update feeds remain separate.

### One MSIXBUNDLE for x64 and ARM64

Build both packages with identical package name, publisher, and release version, then:

```powershell
.\tools\Bundle-Msix.ps1 -X64Package $x64.Path -Arm64Package $arm64.Path `
    -OutputPath .\artifacts\Searchlight.msixbundle -Unsigned
```

Windows selects the application package for the recipient's architecture. The script
validates both native payloads, bundle identity, architecture entries, and byte-for-byte
inclusion of the input packages before signing (which can rewrite embedded signatures).
Signed output is then checked with SignTool. Only the two MSIX files are included, not adjacent
symbols or installer scripts.

Run `tools\Test-MsixBundle.ps1 -X64Package $x64.Path -Arm64Package $arm64.Path`
to exercise bundle creation and identity/architecture guardrails using temporary output.

For a signed bundle, omit `-Unsigned` and supply `-CertificateThumbprint` and optionally
`-TimestampUrl`, just as for individual packages. The signing certificate's subject must
match the bundle publisher. An unsigned bundle is only a signing/submission candidate;
packaging it as a bundle does not establish trust or publish it anywhere.

## Versions

The visible build name remains `YYYY.MM.DD.##`. By default MSIX uses the equivalent
numeric quad, for example `2026.09.14.05` becomes `2026.9.14.5`.
`-PackageVersion` can explicitly supply a different valid numeric quad for a
distribution contract such as a reserved fourth component.

All channels and build paths allocate from `%LOCALAPPDATA%\Searchlight.Build\Shared`
for this user on this machine. Production and Dev no longer have independent sequences.
Migration seeds the shared counter from the highest existing channel/worktree counter.
Allocate once and reuse that name across channels and architectures when building the
same source release:

```powershell
$version = .\tools\Get-NextBuildVersion.ps1
.\tools\install.ps1 -Configuration Release -BuildName $version
.\tools\Build-Msix.ps1 -BuildName $version -CertificateThumbprint '<development-thumbprint>'
```

An explicit name reserves its high-water mark without another increment; reusing a
previous name never lowers the next automatic version. Separate CI machines need a
persistent shared allocator location or externally allocated release names.

Numbers run from 01 through 99 per local Gregorian date. Failed builds can consume
numbers. Do not delete counters or reuse a version for different content.
The installer rejects downgrades and detects differing payloads at an already
installed version. A build from another machine or a rolled-back clock may need
an explicitly higher version.

## Shared data and process isolation

Production, Dev, and updated unpackaged builds share `%USERPROFILE%\.searchlight`,
outside MSIX-virtualized AppData. Existing `settings.json` and notes are copied from
`%LOCALAPPDATA%\Searchlight` without deleting the originals or replacing already
migrated data. Old, unmodified unpackaged releases still use the legacy folder;
they do not automatically participate in the new shared store.

Settings and notes use shared persistence coordination; failures and note conflicts
appear in a warning in the main window. Do not run an old release against data written
by a newer incompatible schema. Shared user data is intentionally retained when
either package is removed.

Settings saves merge unrelated property, pin, and name changes under a shared lock;
same-field edits are last-writer-wins. Unknown JSON properties are retained and
unsupported/corrupt schemas are rejected rather than overwritten. Refresh reloads
external changes, and opening Settings also reloads the shared values.

If a note changed externally while you edited it, the shared version is preserved
and your draft is saved under `.searchlight\notes\conflicts`. The visible notice gives
the recovery location; reconcile the texts manually. Shutdown is refused when neither
the normal save nor durable conflict recovery can preserve pending notes.

Each channel has its own process mutex, activation event, window/tray label, and log:
`%TEMP%\Searchlight.Dev.log`, `Searchlight.Production.log`, or
`Searchlight.Unpackaged.log`. Dev always writes monitoring logs; Production and unpackaged
builds require the shared `EnableMonitoring` opt-in, which defaults off. A second launch
activates only its own channel.
Dev has an orange badge on its package logos and declares no startup task, so it does not
appear as an auto-start option in the app or Windows Settings. Production exposes
**Start Searchlight when I sign in**, reflecting OS startup state rather than shared JSON.
Packaged Production declares a startup task enabled on first launch; Windows/user startup
settings still control it. Neither package creates the old Startup-folder shortcut.
The unpackaged Production installation manages its existing Startup shortcut, and its
installer preserves an absent/disabled shortcut on upgrades instead of re-enabling it.

The manifest declares `runFullTrust` and `allowElevation` to preserve optional
administrator behavior. Elevation remains subject to UAC and must be tested on the
target system. A destination may restrict those capabilities; this implementation
does not claim store certification or silently bypass policy.

## Validation

```powershell
pwsh -NoProfile -File .\tools\Test-BuildVersion.ps1

$inspection = .\tools\Build-Msix.ps1 -Unsigned
.\tools\Test-MsixWorkflow.ps1 -UnsignedDevPackage $inspection.Path

dotnet test .\src\Searchlight.Core.Tests\Searchlight.Core.Tests.csproj
```

The package checks cover identity, signatures, expected runtime files, native
architecture, accidental private-key/session-data inclusion, and installer identity
guards. Runtime qualification also needs simultaneous channels, updates, shared-data
conflicts, tray/clipboard/resume/elevation, clean-machine dependency checks, and real
ARM64 execution. Building an ARM64 artifact on x64 is not an ARM64 runtime test.

Do not uninstall either channel just to fix an update failure. Close its running
instance and inspect the package version, signature, and Windows deployment error.
Removing a package or changing device trust remains an explicit user action.
