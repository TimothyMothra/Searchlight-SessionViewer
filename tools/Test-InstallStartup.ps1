$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "Searchlight-startup-test-$([guid]::NewGuid())"
$originalLocal = $env:LOCALAPPDATA
$originalRoaming = $env:APPDATA
$shell = $null
$link = $null
try {
    # ASSUMPTION: redirected installer paths plus NoDesktop/SkipPublish keep this
    # test entirely outside the user's real startup folders and installed app.
    $env:LOCALAPPDATA = Join-Path $root 'Local'
    $env:APPDATA = Join-Path $root 'Roaming'
    $app = Join-Path $env:LOCALAPPDATA 'Searchlight\app'
    [IO.Directory]::CreateDirectory($app) | Out-Null
    $exe = Join-Path $app 'Searchlight.exe'
    [IO.File]::WriteAllText($exe, 'test fixture - never executed')
    $startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Searchlight.lnk'

    & (Join-Path $PSScriptRoot 'install.ps1') -SkipPublish -NoDesktop
    if (Test-Path $startup) { throw 'An upgrade re-enabled disabled auto-start.' }

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($startup)) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($startup)
    $link.TargetPath = $exe
    $link.Description = 'Preserve this existing startup registration'
    $link.Save()
    $before = (Get-FileHash $startup).Hash
    & (Join-Path $PSScriptRoot 'install.ps1') -SkipPublish -NoDesktop
    if ((Get-FileHash $startup).Hash -ne $before) { throw 'An upgrade rewrote the existing startup registration.' }
}
finally {
    if ($link -and [Runtime.InteropServices.Marshal]::IsComObject($link)) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
    }
    if ($shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
    $env:LOCALAPPDATA = $originalLocal
    $env:APPDATA = $originalRoaming
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $true) }
}
Write-Host 'PASS: disabled startup stays disabled; existing startup shortcut remains unchanged.'
