[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$ExpectedName,
    [Parameter(Mandatory)][string]$ExpectedPublisher,
    [Parameter(Mandatory)][version]$ExpectedVersion,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$ExpectedArchitecture,
    [Nullable[bool]]$ExpectedStartupRegistration,
    [switch]$RequireSignature
)
. (Join-Path $PSScriptRoot 'Msix-Common.ps1')
$manifest = Get-MsixManifest $Path
if ($manifest.Name -cne $ExpectedName -or $manifest.Publisher -cne $ExpectedPublisher -or
    $manifest.Version -ne $ExpectedVersion -or $manifest.Architecture -ne $ExpectedArchitecture) {
    throw "Unexpected MSIX identity: $($manifest | ConvertTo-Json -Compress)"
}
if ($null -ne $ExpectedStartupRegistration -and
    $manifest.HasStartupRegistration -ne $ExpectedStartupRegistration) {
    throw "Unexpected startup registration: expected $ExpectedStartupRegistration, found $($manifest.HasStartupRegistration)."
}
if ($manifest.Name -ceq 'TimothyMothra.Searchlight.Dev' -and $manifest.HasStartupRegistration) {
    throw 'Dev packages must be startup-free.'
}
# ASSUMPTION: this is the self-contained WinUI/.NET distribution, not just the managed DLL.
foreach ($required in @('Searchlight.exe', 'Searchlight.dll', 'resources.pri',
    'Microsoft.UI.Xaml.dll', 'e_sqlite3.dll', 'coreclr.dll', 'hostfxr.dll')) {
    if ($required -notin $manifest.Entries) { throw "MSIX payload is missing $required." }
}
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    $stream = $zip.GetEntry('Searchlight.dll').Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($memory)
        $memory.Position = 0
        $startupEnabled = Get-MsixStartupTaskEnabled -Stream $memory
        if ($startupEnabled -ne $manifest.HasStartupRegistration) {
            throw 'SearchlightStartupTaskEnabled assembly metadata disagrees with the manifest startup registration.'
        }
    }
    finally { $memory.Dispose(); $stream.Dispose() }
    foreach ($native in @('Searchlight.exe', 'coreclr.dll', 'e_sqlite3.dll')) {
        $stream = $zip.GetEntry($native).Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            $memory.Position = 0
            $reader = [IO.BinaryReader]::new($memory)
            if ($reader.ReadUInt16() -ne 0x5a4d) { throw "$native is not a PE image." }
            $memory.Position = 0x3c
            $offset = $reader.ReadInt32()
            if ($offset -lt 64 -or $offset -gt $memory.Length - 6) { throw "$native has an invalid PE header." }
            $memory.Position = $offset
            if ($reader.ReadUInt32() -ne 0x4550) { throw "$native has an invalid PE signature." }
            $machine = $reader.ReadUInt16()
            $expectedMachine = if ($ExpectedArchitecture -eq 'x64') { 0x8664 } else { 0xaa64 }
            if ($machine -ne $expectedMachine) { throw "$native does not match package architecture $ExpectedArchitecture." }
        }
        finally { $memory.Dispose(); $stream.Dispose() }
    }
}
finally { $zip.Dispose() }
if ($manifest.Entries | Where-Object { $_ -match '(?i)\.(pfx|p12|key)$|(^|[\\/])session-state[\\/]' }) {
    throw 'The package includes private signing material or session data.'
}
if ($RequireSignature) {
    if ('AppxSignature.p7x' -notin $manifest.Entries) { throw 'The MSIX is unsigned.' }
    $tools = Get-MsixTools
    Invoke-MsixTool $tools.SignTool @('verify', '/pa', '/q', ([IO.Path]::GetFullPath($Path)))
}
$manifest
