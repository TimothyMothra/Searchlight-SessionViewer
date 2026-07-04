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
    [switch]$SkipPublish
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

# --- Uninstall ---------------------------------------------------------------
if ($Action -eq 'Uninstall') {
    Write-Step 'Uninstalling Searchlight...'
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
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $InstallDir -Recurse -Force
    Remove-Item $publishDir -Recurse -Force
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
