[CmdletBinding()]
param([Parameter(Mandatory)][string]$UnsignedDevPackage)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Msix-Common.ps1')

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection containing '$Message'."
}

# ASSUMPTION: a local unsigned build is an inspection fixture, never an install candidate.
$info = Get-MsixManifest $UnsignedDevPackage
$parameters = @{
    Path = $UnsignedDevPackage
    ExpectedName = 'TimothyMothra.Searchlight.Dev'
    ExpectedPublisher = 'CN=Searchlight Development'
    ExpectedVersion = $info.Version
    ExpectedArchitecture = $info.Architecture
}
$null = & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') @parameters
Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') @parameters -RequireSignature } 'unsigned'
$parameters.ExpectedName = 'Production.Must.Not.Match'
Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') @parameters } 'Unexpected MSIX identity'
Assert-Rejected { & (Join-Path $PSScriptRoot 'Build-Msix.ps1') -Channel Production -Unsigned } 'Production requires explicit'
Assert-Rejected { & (Join-Path $PSScriptRoot 'Build-Msix.ps1') -Channel Dev -PackageName Production -Unsigned } 'stable TimothyMothra.Searchlight.Dev'
Assert-Rejected { & (Join-Path $PSScriptRoot 'Build-Msix.ps1') -Unsigned -BuildName 2026.02.30.01 } 'valid Gregorian'
Write-Host 'PASS: payload/architecture, unsigned rejection, identity mismatch, production guards, and invalid build date.'
