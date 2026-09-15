[CmdletBinding(DefaultParameterSetName = 'Allocate')]
param(
    [string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'Searchlight.Build\Shared'),

    [Parameter(ParameterSetName = 'Allocate')]
    [datetime]$BuildDate = [datetime]::Now,

    [Parameter(Mandatory, ParameterSetName = 'Reuse')]
    [string]$BuildName,

    [string[]]$LegacyStateDirectory = @()
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$reuse = $PSCmdlet.ParameterSetName -eq 'Reuse'
if ($reuse) {
    $parsedDate = [datetime]::MinValue
    if ($BuildName -notmatch '^\d{4}\.\d{2}\.\d{2}\.(0[1-9]|[1-9][0-9])$' -or
        -not [datetime]::TryParseExact($BuildName.Substring(0,10), 'yyyy.MM.dd', $culture,
            [Globalization.DateTimeStyles]::None, [ref]$parsedDate)) {
        throw 'BuildName must be a valid Gregorian YYYY.MM.DD.01 through YYYY.MM.DD.99.'
    }
    $date = $parsedDate.ToString('yyyy.MM.dd', $culture)
    $requestedNumber = [int]$BuildName.Substring(11)
}
else {
    $date = $BuildDate.ToString('yyyy.MM.dd', $culture)
}
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
[IO.Directory]::CreateDirectory($StateDirectory) | Out-Null

# ASSUMPTION: every channel/worktree on this user account shares one daily sequence.
# Explicit names reuse one release across channels/architectures without allocating
# another number, but still advance the high-water mark if supplied by release tooling.
if (-not $PSBoundParameters.ContainsKey('StateDirectory')) {
    $LegacyStateDirectory += @(
        (Join-Path $env:LOCALAPPDATA 'Searchlight.Build\Dev'),
        (Join-Path $env:LOCALAPPDATA 'Searchlight.Build\Production'),
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Searchlight\obj\build-version')
    )
}

function Read-Counter([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return 0 }
    $stored = [IO.File]::ReadAllText($Path).Trim()
    $value = 0
    if ($stored -notmatch '^[0-9]{1,2}$' -or
        -not [int]::TryParse($stored, [ref]$value) -or $value -lt 1) {
        throw "Invalid build counter in '$Path': '$stored'."
    }
    return $value
}

$lockPath = Join-Path $StateDirectory 'counter.lock'
$counterPath = Join-Path $StateDirectory "$date.txt"
$timer = [Diagnostics.Stopwatch]::StartNew()
$lock = $null
$temporaryPath = $null

try {
    while ($null -eq $lock) {
        try {
            $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
        }
        catch [IO.IOException] {
            $code = $_.Exception.HResult -band 0xffff
            if ($code -notin @(32, 33) -or $timer.Elapsed.TotalSeconds -ge 10) {
                throw
            }
            Start-Sleep -Milliseconds 50
        }
    }

    $number = Read-Counter $counterPath
    foreach ($legacy in $LegacyStateDirectory) {
        $number = [Math]::Max($number, (Read-Counter (Join-Path $legacy "$date.txt")))
    }
    if (-not $reuse -and $number -ge 99) {
        throw "Daily build limit reached for $date. YYYY.MM.DD.## permits builds 01 through 99."
    }

    $result = if ($reuse) { $requestedNumber } else { $number + 1 }
    $number = [Math]::Max($number, $result)
    $temporaryPath = Join-Path $StateDirectory "$([guid]::NewGuid()).tmp"
    [IO.File]::WriteAllText($temporaryPath, $number.ToString($culture))
    [IO.File]::Move($temporaryPath, $counterPath, $true)
    "$date.$($result.ToString('D2', $culture))"
}
finally {
    if ($null -ne $temporaryPath -and [IO.File]::Exists($temporaryPath)) {
        [IO.File]::Delete($temporaryPath)
    }
    if ($null -ne $lock) {
        $lock.Dispose()
    }
}
