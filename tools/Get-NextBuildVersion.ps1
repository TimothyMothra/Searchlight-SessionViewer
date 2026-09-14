[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StateDirectory,

    [datetime]$BuildDate = [datetime]::Now
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$date = $BuildDate.ToString('yyyy.MM.dd', $culture)
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
[IO.Directory]::CreateDirectory($StateDirectory) | Out-Null

# ASSUMPTION: numbers are local to a worktree and use the build machine's calendar
# date. One exclusive lock shares the counter across configurations and processes.
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

    $number = 0
    if ([IO.File]::Exists($counterPath)) {
        $stored = [IO.File]::ReadAllText($counterPath).Trim()
        if ($stored -notmatch '^[0-9]{1,2}$' -or
            -not [int]::TryParse($stored, [ref]$number) -or $number -lt 1) {
            throw "Invalid build counter in '$counterPath': '$stored'."
        }
    }
    if ($number -ge 99) {
        throw "Daily build limit reached for $date. YYYY.MM.DD.## permits builds 01 through 99."
    }

    $number++
    $temporaryPath = Join-Path $StateDirectory "$([guid]::NewGuid()).tmp"
    [IO.File]::WriteAllText($temporaryPath, $number.ToString($culture))
    [IO.File]::Move($temporaryPath, $counterPath, $true)
    "$date.$($number.ToString('D2', $culture))"
}
finally {
    if ($null -ne $temporaryPath -and [IO.File]::Exists($temporaryPath)) {
        [IO.File]::Delete($temporaryPath)
    }
    if ($null -ne $lock) {
        $lock.Dispose()
    }
}
