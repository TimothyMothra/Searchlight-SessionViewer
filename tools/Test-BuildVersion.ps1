$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Get-NextBuildVersion.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "Searchlight-build-version-$([guid]::NewGuid())"
$date = [datetime]::new(2026, 9, 14)
$originalCulture = [Globalization.CultureInfo]::CurrentCulture
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
    # ASSUMPTION: isolated directories model separate worktrees; no production
    # build counters or user settings are touched by these tests.
    $sequential = Join-Path $root 'sequential'
    Assert-Equal '2026.09.14.01' (& $script -StateDirectory $sequential -BuildDate $date)
    Assert-Equal '2026.09.14.02' (& $script -StateDirectory $sequential -BuildDate $date)
    Assert-Equal '2026.09.15.01' (& $script -StateDirectory $sequential -BuildDate $date.AddDays(1))

    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
    Assert-Equal '2026.09.14.03' (& $script -StateDirectory $sequential -BuildDate $date)
    [Globalization.CultureInfo]::CurrentCulture = $originalCulture

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

    Write-Host 'PASS: increment, rollover, invariant format, limit, corrupt state, and concurrent allocation.'
}
finally {
    [Globalization.CultureInfo]::CurrentCulture = $originalCulture
    if ($jobs.Count -gt 0) {
        $jobs | Stop-Job
        $jobs | Remove-Job
    }
    if ([IO.Directory]::Exists($root)) {
        [IO.Directory]::Delete($root, $true)
    }
}
