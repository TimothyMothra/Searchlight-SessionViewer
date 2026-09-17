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

    $info = Get-MsixManifest $Arm64Package
    foreach ($declaration in @(
        '<desktop:Extension Category="windows.startupTask" />',
        '<desktop:StartupTask TaskId="SearchlightStartup" Enabled="false" />'
    )) {
        # Startup is prohibited even when disabled or only the extension is present.
        $startupPackage = Join-Path $root "startup-$([guid]::NewGuid()).msix"
        $zip = [IO.Compression.ZipFile]::Open($startupPackage, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry = $zip.CreateEntry('AppxManifest.xml')
            $writer = [IO.StreamWriter]::new($entry.Open())
            try {
                $name = [Security.SecurityElement]::Escape($info.Name)
                $publisher = [Security.SecurityElement]::Escape($info.Publisher)
                $appId = [Security.SecurityElement]::Escape($info.ApplicationId)
                $writer.Write("<Package xmlns:desktop=`"http://schemas.microsoft.com/appx/manifest/desktop/windows10`"><Identity Name=`"$name`" Publisher=`"$publisher`" Version=`"$($info.Version)`" ProcessorArchitecture=`"arm64`"/><Applications><Application Id=`"$appId`"><Extensions>$declaration</Extensions></Application></Applications></Package>")
            }
            finally { $writer.Dispose() }
        }
        finally { $zip.Dispose() }
        Assert-Rejected {
            & $script -X64Package $X64Package -Arm64Package $startupPackage -OutputPath $output -Unsigned
        } 'Packaged startup registration is temporarily unsupported'
    }
    Write-Host 'PASS: dual-architecture bundle, payload hashes, unsigned output, identity guards, and startup rejection.'
}
finally { [IO.Directory]::Delete($root, $true) }
