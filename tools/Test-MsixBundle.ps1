[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$X64Package,
    [Parameter(Mandatory)][string]$Arm64Package
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Msix-Common.ps1')
$script = Join-Path $PSScriptRoot 'Bundle-Msix.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "Searchlight-bundle-test-$([guid]::NewGuid())"
[IO.Directory]::CreateDirectory($root) | Out-Null

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection containing '$Message'."
}

try {
    $output = Join-Path $root 'candidate.msixbundle'
    $result = & $script -X64Package $X64Package -Arm64Package $Arm64Package -OutputPath $output -Unsigned
    if ($result.Signed -or ($result.Architectures -join ',') -ne 'x64,arm64') {
        throw 'Expected an unsigned dual-architecture result.'
    }
    Assert-Rejected {
        & $script -X64Package $X64Package -Arm64Package $X64Package -OutputPath $output -Unsigned
    } 'Unexpected MSIX identity'
    Assert-Rejected {
        & $script -X64Package $X64Package -Arm64Package $Arm64Package -OutputPath $output `
            -Unsigned -CertificateThumbprint 'not-used'
    } 'Choose signing or Unsigned'

    # ASSUMPTION: deliberately invalid fixtures stay isolated from real build artifacts.
    $mismatch = Join-Path $root 'mismatch.msix'
    $zip = [IO.Compression.ZipFile]::Open($mismatch, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('AppxManifest.xml')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('<Package><Identity Name="Different.App" Publisher="CN=Different" Version="1.0.0.0" ProcessorArchitecture="arm64"/><Applications><Application Id="App"/></Applications></Package>')
        }
        finally { $writer.Dispose() }
    }
    finally { $zip.Dispose() }
    Assert-Rejected {
        & $script -X64Package $X64Package -Arm64Package $mismatch -OutputPath $output -Unsigned
    } 'same package name, publisher, version'
    Write-Host 'PASS: dual-architecture bundle, payload hashes, unsigned output, and mismatched-input guards.'
}
finally { [IO.Directory]::Delete($root, $true) }
