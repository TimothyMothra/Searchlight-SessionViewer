<#
.SYNOPSIS
    Installs (or uninstalls) Searchlight so it can be launched without a taskbar
    pin: publishes a self-contained build to a stable location and creates
    Start Menu, desktop, and run-at-login (Startup) shortcuts.

.DESCRIPTION
    Searchlight is an UNPACKAGED WinUI 3 app (WindowsPackageType=None), so Windows
    does not register a Start-menu tile automatically. This script:

      1. Publishes a self-contained build (no .NET SDK/runtime needed to run it)
         to  %LOCALAPPDATA%\Searchlight\app  (a stable path that survives repo
         cleans, unlike bin\Debug).
      2. Creates .lnk shortcuts:
           - Start Menu  (so Win key -> "Searchlight" finds it)
           - Desktop     (double-click launch)         [skip with -NoDesktop]
           - Startup     (runs at login, lands in tray)[skip with -NoStartup]

    Run  -Action Uninstall  to remove the shortcuts and the installed app folder.

.PARAMETER Action
    Install (default) or Uninstall.

.PARAMETER Configuration
    Build configuration to publish. Default: Release. (Do NOT use Demo here —
    that config forces the synthetic mock datastore.)

.PARAMETER NoDesktop
    Skip creating the desktop shortcut.

.PARAMETER NoStartup
    Skip creating the run-at-login (Startup) shortcut.

.PARAMETER SkipPublish
    Reuse an existing installed app folder instead of re-publishing (only
    recreates the shortcuts). Useful for quickly recreating shortcuts.

.PARAMETER StopRunning
    Force-stop a running Searchlight instance instead of prompting for it. Note
    this skips the app's graceful shutdown, which flushes pending note edits
    (debounced ~600ms), so a few final keystrokes in the Notes pane could be lost.
    Exiting from the tray icon is always the safer option.

.EXAMPLE
    pwsh -File tools\install.ps1
    Full install: publish + Start Menu + desktop + run-at-login shortcuts.

.EXAMPLE
    pwsh -File tools\install.ps1 -NoStartup
    Install but do NOT launch at login.

.EXAMPLE
    pwsh -File tools\install.ps1 -Action Uninstall
    Remove all shortcuts and the installed app folder.
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action = 'Install',

    [string]$Configuration = 'Release',

    [switch]$NoDesktop,
    [switch]$NoStartup,
    [switch]$SkipPublish,
    [switch]$StopRunning
)

$ErrorActionPreference = 'Stop'

# --- Paths -------------------------------------------------------------------
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $RepoRoot 'src\Searchlight\Searchlight.csproj'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Searchlight\app'
$ExeName    = 'Searchlight.exe'
$ExePath    = Join-Path $InstallDir $ExeName

$StartMenuLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Searchlight.lnk'
$StartupLnk   = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Searchlight.lnk'
$DesktopLnk   = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Searchlight.lnk'

function Write-Step($msg) { Write-Host "[install] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  + $msg"        -ForegroundColor Green }

function New-Shortcut {
    param([string]$LinkPath, [string]$Target, [string]$WorkDir, [string]$Description, [string]$Arguments = '')

    $dir = Split-Path -Parent $LinkPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($LinkPath)
    $lnk.TargetPath       = $Target
    $lnk.Arguments        = $Arguments
    $lnk.WorkingDirectory = $WorkDir
    $lnk.Description       = $Description
    # The exe embeds Assets\app.ico via <ApplicationIcon>, so index 0 is our icon.
    $lnk.IconLocation     = "$Target,0"
    $lnk.Save()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}

function Remove-IfPresent($path) {
    if (Test-Path $path) {
        Remove-Item $path -Force -Recurse
        Write-Ok "removed $path"
    }
}

# Detects a running instance by testing the exe for a write lock, rather than
# matching process names: a dev build running from bin\ shares the name but does
# not lock the installed copy, and would otherwise be a false positive.
function Test-PathLocked {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Write', 'None')
        $stream.Close()
        return $false
    }
    catch {
        return $true
    }
}

function Wait-ForUnlock {
    param([string]$Path, [int]$TimeoutSeconds = 15)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-PathLocked $Path)) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return -not (Test-PathLocked $Path)
}

function Stop-RunningInstances {
    param([string]$Path)

    $procs = @(Get-Process -Name 'Searchlight' -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) {
        # Locked but no visible process: almost always an elevated instance that a
        # non-elevated shell cannot enumerate.
        throw "$Path is locked but no Searchlight process is visible to this shell. It is probably running elevated - exit it from the tray icon, or rerun this script from an elevated terminal."
    }

    foreach ($p in $procs) {
        Write-Step "Stopping Searchlight (PID $($p.Id))..."
        try {
            Stop-Process -Id $p.Id -Force -ErrorAction Stop
            Write-Ok "stopped PID $($p.Id)"
        }
        catch {
            throw "Could not stop PID $($p.Id): $($_.Exception.Message). If it is running elevated, exit it from the tray icon or rerun this script from an elevated terminal."
        }
    }
}

# The move-aside-then-copy install below survives a running instance, so this is
# not a correctness guard. It exists because succeeding silently is the wrong
# outcome: the user keeps using the old in-memory build while the new one sits on
# disk unused, and the locked $InstallDir.old cannot be cleaned up. Resolving it
# up front means the new build is the one actually running afterwards.
function Assert-InstallDirWritable {
    param([string]$Path, [switch]$AutoStop)

    if (-not (Test-PathLocked $Path)) { return }

    Write-Host ''
    Write-Host 'Searchlight is running and is holding the install folder open.' -ForegroundColor Yellow
    Write-Host "  $Path" -ForegroundColor Gray

    if ($AutoStop) {
        Stop-RunningInstances -Path $Path
    }
    else {
        Write-Host ''
        Write-Host 'Preferred: right-click the tray icon and choose Exit. That shuts down' -ForegroundColor Gray
        Write-Host 'cleanly and flushes any pending note edits.' -ForegroundColor Gray
        Write-Host ''
        Write-Host 'Press ENTER once it has exited, or type F then ENTER to force-stop it' -ForegroundColor Gray
        Write-Host 'now (may lose the last few keystrokes in the Notes pane).' -ForegroundColor Gray

        # Read-Host throws under -NonInteractive / redirected input (CI, task
        # scheduler, an agent shell). Fail with instructions rather than a raw
        # PowerShell prompt error.
        try {
            $answer = Read-Host 'ENTER = retry, F = force-stop, C = cancel'
        }
        catch {
            throw 'Searchlight is running and this shell cannot prompt. Exit it from the tray icon, or rerun with -StopRunning to force-stop it.'
        }

        switch -Regex ($answer.Trim()) {
            '^[Ff]' { Stop-RunningInstances -Path $Path }
            '^[Cc]' { throw 'Install cancelled.' }
        }
    }

    if (-not (Wait-ForUnlock -Path $Path)) {
        throw "$Path is still locked. Exit Searchlight (tray icon -> Exit) and run this script again."
    }

    Write-Ok 'install folder is free'
}

# --- Uninstall ---------------------------------------------------------------
if ($Action -eq 'Uninstall') {
    Write-Step 'Uninstalling Searchlight...'
    Assert-InstallDirWritable -Path $ExePath -AutoStop:$StopRunning
    Remove-IfPresent $StartMenuLnk
    Remove-IfPresent $StartupLnk
    Remove-IfPresent $DesktopLnk
    Remove-IfPresent $InstallDir

    # Best effort: remove the parent %LOCALAPPDATA%\Searchlight only if now empty.
    $parent = Split-Path -Parent $InstallDir
    if ((Test-Path $parent) -and -not (Get-ChildItem $parent -Force)) {
        Remove-Item $parent -Force
        Write-Ok "removed $parent"
    }

    Write-Host ''
    Write-Host 'Searchlight uninstalled. (Your %LOCALAPPDATA%\Searchlight settings, if kept, were left intact only when non-empty.)' -ForegroundColor Yellow
    return
}

# --- Install -----------------------------------------------------------------
# Resolve the runtime identifier from the current OS architecture.
$rid = switch ($env:PROCESSOR_ARCHITECTURE) {
    'ARM64' { 'win-arm64' }
    'AMD64' { 'win-x64' }
    default { 'win-x64' }
}
$platform = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }

Write-Step "Repo root : $RepoRoot"
Write-Step "Target RID: $rid ($platform)"
Write-Step "Install to: $InstallDir"

if (-not $SkipPublish) {
    # Ask before the (slow) publish, so the user is not left waiting through a
    # full self-contained build before being prompted to close the app.
    Assert-InstallDirWritable -Path $ExePath -AutoStop:$StopRunning

    Write-Step "Publishing self-contained ($Configuration)..."
    $publishDir = Join-Path $RepoRoot "src\Searchlight\bin\$Configuration\_publish_$rid"

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    dotnet publish $Project `
        -c $Configuration `
        -r $rid `
        -p:Platform=$platform `
        --self-contained true `
        -o $publishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

    $publishedExe = Join-Path $publishDir $ExeName
    if (-not (Test-Path $publishedExe)) { throw "Publish succeeded but $ExeName not found in $publishDir." }

    Write-Step 'Copying published output to the install folder...'

    # Move the old folder aside rather than deleting it in place. A directory
    # rename either succeeds outright or fails without touching anything, whereas
    # `Remove-Item -Recurse` deletes every file it can and only then errors on a
    # locked one — which is exactly how a working install gets left half-deleted.
    # The new build is copied into a clean folder, and the old one is removed last
    # (best-effort: if a still-running instance holds locks, leaving it costs a
    # little disk but never breaks the install).
    $backupDir = "$InstallDir.old"
    if (Test-Path $backupDir) { Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $InstallDir) { Move-Item $InstallDir $backupDir }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $InstallDir -Recurse -Force
    Remove-Item $publishDir -Recurse -Force

    if (Test-Path $backupDir) {
        Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $backupDir) {
            Write-Host "  ! left $backupDir behind (files still in use); safe to delete later" -ForegroundColor Yellow
        }
    }

    Write-Ok "installed to $InstallDir"
}

if (-not (Test-Path $ExePath)) {
    throw "$ExePath not found. Run without -SkipPublish to publish first."
}

# --- Shortcuts ---------------------------------------------------------------
Write-Step 'Creating shortcuts...'

New-Shortcut -LinkPath $StartMenuLnk -Target $ExePath -WorkDir $InstallDir `
    -Description 'Searchlight: Historical Session Viewer'
Write-Ok "Start Menu : $StartMenuLnk"

if (-not $NoDesktop) {
    New-Shortcut -LinkPath $DesktopLnk -Target $ExePath -WorkDir $InstallDir `
        -Description 'Searchlight: Historical Session Viewer'
    Write-Ok "Desktop    : $DesktopLnk"
}

if (-not $NoStartup) {
    New-Shortcut -LinkPath $StartupLnk -Target $ExePath -WorkDir $InstallDir `
        -Description 'Searchlight (run at login)'
    Write-Ok "Startup    : $StartupLnk"
}

Write-Host ''
Write-Host 'Searchlight installed.' -ForegroundColor Green
Write-Host '  Launch now : press the Win key and type "Searchlight"' -ForegroundColor Gray
if (-not $NoStartup) {
    Write-Host '  At login   : it will start automatically and sit in the system tray' -ForegroundColor Gray
}
Write-Host '  Uninstall  : pwsh -File tools\install.ps1 -Action Uninstall' -ForegroundColor Gray
