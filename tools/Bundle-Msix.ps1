[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$X64Package,
    [Parameter(Mandatory)][string]$Arm64Package,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$CertificateThumbprint,
    [uri]$TimestampUrl,
    [switch]$Unsigned
)
. (Join-Path $PSScriptRoot 'Msix-Common.ps1')
$X64Package = [IO.Path]::GetFullPath($X64Package)
$Arm64Package = [IO.Path]::GetFullPath($Arm64Package)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($OutputPath) -ine '.msixbundle') { throw 'OutputPath must end in .msixbundle.' }
if ($OutputPath -ieq $X64Package -or $OutputPath -ieq $Arm64Package) { throw 'The output must not overwrite an input package.' }
if ($Unsigned -and $CertificateThumbprint) { throw 'Choose signing or Unsigned, not both.' }

$x64 = Get-MsixManifest $X64Package
$arm64 = Get-MsixManifest $Arm64Package
if ($x64.Name -cne $arm64.Name -or $x64.Publisher -cne $arm64.Publisher -or
    $x64.Version -ne $arm64.Version -or $x64.ApplicationId -cne $arm64.ApplicationId) {
    throw 'Bundle inputs must have the same package name, publisher, version, and application ID.'
}
if ($x64.HasStartupRegistration -or $arm64.HasStartupRegistration) {
    throw 'Bundle inputs must be startup-free. Rebuild both architectures with Build-Msix.ps1 -ForBundle.'
}
foreach ($inputPackage in @(
    @{ Path = $X64Package; Architecture = 'x64' },
    @{ Path = $Arm64Package; Architecture = 'arm64' }
)) {
    $null = & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') -Path $inputPackage.Path `
        -ExpectedName $x64.Name -ExpectedPublisher $x64.Publisher -ExpectedVersion $x64.Version `
        -ExpectedArchitecture $inputPackage.Architecture -ExpectedStartupRegistration $false
}
if (-not $Unsigned) { Assert-MsixSigningCertificate $CertificateThumbprint $x64.Publisher }

$tools = Get-MsixTools
$stage = Join-Path ([IO.Path]::GetTempPath()) "Searchlight-msixbundle-$([guid]::NewGuid())"
[IO.Directory]::CreateDirectory($stage) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
try {
    # ASSUMPTION: the bundle contains exactly these two validated application packages,
    # not symbols, test installers, certificates, or neighboring build artifacts.
    [IO.File]::Copy($X64Package, (Join-Path $stage 'Searchlight_x64.msix'))
    [IO.File]::Copy($Arm64Package, (Join-Path $stage 'Searchlight_arm64.msix'))
    Invoke-MsixTool $tools.MakeAppx @('bundle', '/d', $stage, '/p', $OutputPath,
        '/bv', $x64.Version.ToString(), '/o')
}
finally { [IO.Directory]::Delete($stage, $true) }

$bundle = [IO.Compression.ZipFile]::OpenRead($OutputPath)
try {
    $entry = $bundle.GetEntry('AppxMetadata/AppxBundleManifest.xml')
    if (-not $entry) { throw 'Generated bundle manifest is missing.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $identity = $manifest.Bundle.Identity
    if ($identity.Name -cne $x64.Name -or $identity.Publisher -cne $x64.Publisher -or
        [version]$identity.Version -ne $x64.Version) {
        throw 'Generated bundle identity differs from the input packages.'
    }
    $packages = @($manifest.Bundle.Packages.Package)
    if ($packages.Count -ne 2 -or
        @($packages | Where-Object { $_.Type -eq 'application' -and $_.Architecture -eq 'x64' }).Count -ne 1 -or
        @($packages | Where-Object { $_.Type -eq 'application' -and $_.Architecture -eq 'arm64' }).Count -ne 1) {
        throw 'Generated bundle must contain exactly one x64 and one ARM64 application package.'
    }
    foreach ($package in $packages) {
        $entry = $bundle.GetEntry($package.FileName)
        if (-not $entry) { throw "Bundled payload is missing: $($package.FileName)" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        $original = if ($package.Architecture -eq 'x64') { $X64Package } else { $Arm64Package }
        if ($actual -cne (Get-FileHash $original -Algorithm SHA256).Hash) {
            throw "Bundled $($package.Architecture) payload differs from its input MSIX."
        }
    }
    if ($Unsigned -and $null -ne $bundle.GetEntry('AppxSignature.p7x')) {
        throw 'The requested unsigned bundle unexpectedly contains a signature.'
    }
}
finally { $bundle.Dispose() }

if (-not $Unsigned) {
    # SignTool can rewrite embedded package signatures. Compare input bytes before
    # signing, then validate the cryptographic signature of the completed bundle.
    Invoke-MsixSigning $tools.SignTool $OutputPath $CertificateThumbprint $TimestampUrl
    Invoke-MsixTool $tools.SignTool @('verify', '/pa', '/q', $OutputPath)
}

[pscustomobject]@{
    Path = $OutputPath
    Name = $x64.Name
    Publisher = $x64.Publisher
    Version = $x64.Version.ToString()
    Architectures = @('x64', 'arm64')
    Signed = -not $Unsigned
    HasStartupRegistration = $false
}
