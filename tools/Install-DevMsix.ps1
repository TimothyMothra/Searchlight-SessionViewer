[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][string]$Path,
    [switch]$StopRunning,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$Path = [IO.Path]::GetFullPath($Path)

# ASSUMPTION: Appx deployment uses Windows PowerShell's inbox module; PowerShell 7
# remains the entry point, but must not load incompatible WinRT deployment cmdlets.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-Path', $Path)
    if ($StopRunning) { $arguments += '-StopRunning' }
    if ($NoLaunch) { $arguments += '-NoLaunch' }
    if ($WhatIfPreference) { $arguments += '-WhatIf' }
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments
    if ($LASTEXITCODE -ne 0) { throw "Dev MSIX installation failed with exit code $LASTEXITCODE." }
    return
}

. (Join-Path $PSScriptRoot 'Msix-Common.ps1')
$name = 'TimothyMothra.Searchlight.Dev'
$publisher = 'CN=Searchlight Development'
$manifest = Get-MsixManifest $Path
if ($manifest.Name -cne $name -or $manifest.Publisher -cne $publisher) {
    throw 'This installer accepts only the Searchlight Dev package identity; Production is never replaced.'
}
$null = & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') -Path $Path `
    -ExpectedName $name -ExpectedPublisher $publisher -ExpectedVersion $manifest.Version `
    -ExpectedArchitecture $manifest.Architecture -RequireSignature
$current = Get-AppxPackage -Name $name
if ($current -and $current.Publisher -cne $publisher) { throw 'Installed Dev publisher differs from the expected identity.' }
if ($current -and [version]$current.Version -gt $manifest.Version) {
    throw "Installed Dev version $($current.Version) is newer. Build a higher package version; downgrades are not automatic."
}

if (-not $PSCmdlet.ShouldProcess("$name $($manifest.Version)", 'Install/update Dev only')) { return }
if (-not $current -or [version]$current.Version -lt $manifest.Version) {
    $exitEvent = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting('Searchlight.Dev.SingleInstance.Exit', [ref]$exitEvent)) {
        try { [void]$exitEvent.Set() }
        finally { $exitEvent.Dispose() }
        Start-Sleep -Milliseconds 500
        $deadline = [datetime]::UtcNow.AddSeconds(15)
        do {
            $pending = $null
            $stillExiting = [Threading.EventWaitHandle]::TryOpenExisting('Searchlight.Dev.SingleInstance.Exit', [ref]$pending)
            if ($null -ne $pending) { $pending.Dispose() }
            if (-not $stillExiting) { break }
            Start-Sleep -Milliseconds 200
        } while ([datetime]::UtcNow -lt $deadline)
        if ($stillExiting) {
            throw 'Dev did not finish graceful shutdown; it may be protecting unsaved notes. Resolve its shared-data notice and retry. The installer will not override this refusal.'
        }
    }
    $options = @{ Path = $Path; ErrorAction = 'Stop' }
    if ($StopRunning) { $options.ForceTargetApplicationShutdown = $true }
    Add-AppxPackage @options
}
$installed = Get-AppxPackage -Name $name
if (-not $installed -or $installed.Publisher -cne $publisher -or [version]$installed.Version -ne $manifest.Version) {
    throw 'Installed Dev identity/version does not match the requested package.'
}

$zip = [IO.Compression.ZipFile]::OpenRead($Path)
try {
    foreach ($file in @('Searchlight.exe', 'Searchlight.dll', 'resources.pri')) {
        $stream = $zip.GetEntry($file).Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $expected = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        $actual = (Get-FileHash (Join-Path $installed.InstallLocation $file) -Algorithm SHA256).Hash
        if ($actual -cne $expected) { throw "Installed $file differs. Never reuse a package version for different content." }
    }
}
finally { $zip.Dispose() }

Write-Host "Installed Searchlight Dev $($installed.Version): $($installed.PackageFamilyName)"
if (-not $NoLaunch) {
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($installed.PackageFamilyName)!$($manifest.ApplicationId)"
}
