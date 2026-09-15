$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Get-NextBuildVersion.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "Searchlight-build-version-$([guid]::NewGuid())"
$date = [datetime]::new(2026, 9, 14)
$originalCulture = [Globalization.CultureInfo]::CurrentCulture
$originalLocalAppData = $env:LOCALAPPDATA
$jobs = @()

function Assert-Equal($expected, $actual) {
    if ($expected -cne $actual) {
        throw "Expected '$expected', got '$actual'."
    }
}

function Assert-Fails([scriptblock]$action, [string]$message) {
    try {
        & $action
    }
    catch {
        if ($_.Exception.Message -notlike "*$message*") { throw }
        return
    }
    throw "Expected failure containing '$message'."
}

try {
    # ASSUMPTION: isolated directories model independent test environments; no production
    # build counters or user settings are touched by these tests.
    $sequential = Join-Path $root 'sequential'
    Assert-Equal '2026.09.14.01' (& $script -StateDirectory $sequential -BuildDate $date)
    Assert-Equal '2026.09.14.02' (& $script -StateDirectory $sequential -BuildDate $date)
    Assert-Equal '2026.09.15.01' (& $script -StateDirectory $sequential -BuildDate $date.AddDays(1))

    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
    Assert-Equal '2026.09.14.03' (& $script -StateDirectory $sequential -BuildDate $date)
    [Globalization.CultureInfo]::CurrentCulture = $originalCulture

    $shared = Join-Path $root 'shared'
    Assert-Equal '2026.09.14.20' (& $script -StateDirectory $shared -BuildName '2026.09.14.20')
    Assert-Equal '2026.09.14.20' (& $script -StateDirectory $shared -BuildName '2026.09.14.20')
    Assert-Equal '2026.09.14.10' (& $script -StateDirectory $shared -BuildName '2026.09.14.10')
    Assert-Equal '20' ([IO.File]::ReadAllText((Join-Path $shared '2026.09.14.txt')))
    Assert-Equal '2026.09.14.21' (& $script -StateDirectory $shared -BuildDate $date)
    Assert-Fails { & $script -StateDirectory $shared -BuildName '2026.02.30.01' } 'valid Gregorian'

    # Exercise the actual default path from two simulated worktrees, without
    # reading or writing this machine's real build counters.
    $repoA = Join-Path $root 'worktree-a'
    $repoB = Join-Path $root 'worktree-b'
    foreach ($repo in @($repoA, $repoB)) {
        [IO.Directory]::CreateDirectory((Join-Path $repo 'tools')) | Out-Null
        Copy-Item $script (Join-Path $repo 'tools\Get-NextBuildVersion.ps1')
    }
    $legacyA = Join-Path $repoA 'src\Searchlight\obj\build-version'
    [IO.Directory]::CreateDirectory($legacyA) | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyA '2026.09.14.txt'), '8')
    $env:LOCALAPPDATA = Join-Path $root 'LocalAppData'
    $legacyDev = Join-Path $env:LOCALAPPDATA 'Searchlight.Build\Dev'
    [IO.Directory]::CreateDirectory($legacyDev) | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyDev '2026.09.14.txt'), '12')
    Assert-Equal '2026.09.14.13' (& (Join-Path $repoA 'tools\Get-NextBuildVersion.ps1') -BuildDate $date)
    Assert-Equal '2026.09.14.14' (& (Join-Path $repoB 'tools\Get-NextBuildVersion.ps1') -BuildDate $date)
    Assert-Equal '2026.09.14.14' (& (Join-Path $repoA 'tools\Get-NextBuildVersion.ps1') -BuildName '2026.09.14.14')
    Assert-Equal '14' ([IO.File]::ReadAllText((Join-Path $env:LOCALAPPDATA 'Searchlight.Build\Shared\2026.09.14.txt')))
    Assert-Equal '2026.09.15.01' (& (Join-Path $repoB 'tools\Get-NextBuildVersion.ps1') -BuildDate $date.AddDays(1))
    Assert-Equal '8' ([IO.File]::ReadAllText((Join-Path $legacyA '2026.09.14.txt')))
    Assert-Equal '12' ([IO.File]::ReadAllText((Join-Path $legacyDev '2026.09.14.txt')))
    $env:LOCALAPPDATA = $originalLocalAppData

    $counter = Join-Path $sequential '2026.09.14.txt'
    [IO.File]::WriteAllText($counter, '98')
    Assert-Equal '2026.09.14.99' (& $script -StateDirectory $sequential -BuildDate $date)
    Assert-Fails { & $script -StateDirectory $sequential -BuildDate $date } 'Daily build limit'
    Assert-Equal '99' ([IO.File]::ReadAllText($counter))

    [IO.File]::WriteAllText($counter, 'corrupt')
    Assert-Fails { & $script -StateDirectory $sequential -BuildDate $date } 'Invalid build counter'
    Assert-Equal 'corrupt' ([IO.File]::ReadAllText($counter))

    $parallel = Join-Path $root 'parallel'
    $jobs = @(1..8 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($scriptPath, $directory, $buildDate)
            & $scriptPath -StateDirectory $directory -BuildDate $buildDate
        } -ArgumentList $script, $parallel, $date
    })
    $versions = @($jobs | Receive-Job -Wait -ErrorAction Stop | Sort-Object)
    Assert-Equal 8 $versions.Count
    for ($i = 0; $i -lt 8; $i++) {
        Assert-Equal ('2026.09.14.{0:D2}' -f ($i + 1)) $versions[$i]
    }

    Write-Host 'PASS: shared worktree sequence, legacy high-water migration, release reuse, rollover, format, limits, corrupt state, and concurrent allocation.'
}
finally {
    [Globalization.CultureInfo]::CurrentCulture = $originalCulture
    $env:LOCALAPPDATA = $originalLocalAppData
    if ($jobs.Count -gt 0) {
        $jobs | Stop-Job
        $jobs | Remove-Job
    }
    if ([IO.Directory]::Exists($root)) {
        [IO.Directory]::Delete($root, $true)
    }
}
