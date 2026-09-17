[CmdletBinding()]
param(
    [ValidateSet('Dev', 'Production')][string]$Channel = 'Dev',
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [ValidateSet('Debug', 'Release', 'Demo')][string]$Configuration = 'Release',
    [string]$BuildName,
    [version]$PackageVersion,
    [string]$PackageName,
    [string]$Publisher,
    [string]$CertificateThumbprint,
    [uri]$TimestampUrl,
    [switch]$Unsigned,
    [switch]$Restore
)
. (Join-Path $PSScriptRoot 'Msix-Common.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repo 'src\Searchlight'
$tools = Get-MsixTools

if ($Channel -eq 'Production') {
    if (-not $PackageName -or -not $Publisher) {
        throw 'Production requires explicit PackageName and Publisher.'
    }
    if ($Configuration -eq 'Demo') { throw 'Synthetic Demo builds cannot target Production.' }
    if ($PackageName -eq 'TimothyMothra.Searchlight.Dev') { throw 'Production cannot use the Dev identity.' }
}
else {
    if ($PackageName -and $PackageName -cne 'TimothyMothra.Searchlight.Dev') {
        throw 'Dev uses the stable TimothyMothra.Searchlight.Dev identity.'
    }
    $PackageName = 'TimothyMothra.Searchlight.Dev'
    if ($Publisher -and $Publisher -cne 'CN=Searchlight Development') {
        throw 'Dev uses the stable CN=Searchlight Development publisher.'
    }
    $Publisher = 'CN=Searchlight Development'
}

if ($Unsigned -and $CertificateThumbprint) { throw 'Choose signing or Unsigned, not both.' }
if (-not $Unsigned) {
    Assert-MsixSigningCertificate $CertificateThumbprint $Publisher
}

if (-not $BuildName) {
    $BuildName = & (Join-Path $PSScriptRoot 'Get-NextBuildVersion.ps1')
}
else {
    $BuildName = & (Join-Path $PSScriptRoot 'Get-NextBuildVersion.ps1') -BuildName $BuildName
}
$parsedDate = [datetime]::MinValue
if ($BuildName -notmatch '^\d{4}\.\d{2}\.\d{2}\.(0[1-9]|[1-9][0-9])$' -or
    -not [datetime]::TryParseExact($BuildName.Substring(0,10), 'yyyy.MM.dd',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsedDate)) {
    throw 'BuildName must be a valid Gregorian YYYY.MM.DD.01 through YYYY.MM.DD.99.'
}
if (-not $PackageVersion) { $PackageVersion = [version]$BuildName }
if ($PackageVersion.Major -lt 1 -or $PackageVersion.Revision -lt 0 -or
    @($PackageVersion.Major, $PackageVersion.Minor, $PackageVersion.Build, $PackageVersion.Revision |
        Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw 'PackageVersion must be a four-part MSIX version with components at most 65535 and a nonzero major.'
}

$stage = Join-Path $projectRoot "obj\MsixInputs\$Channel\$Architecture"
$assets = Join-Path $stage 'Assets'
$output = Join-Path $repo "artifacts\msix\$Channel\$PackageVersion\$Architecture"
[IO.Directory]::CreateDirectory($assets) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
$displayName = if ($Channel -eq 'Dev') { 'Searchlight Dev' } else { 'Searchlight' }

[xml]$manifest = Get-Content (Join-Path $projectRoot 'Package.appxmanifest.template') -Raw
$manifest.Package.Identity.SetAttribute('Name', $PackageName)
$manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
$manifest.Package.Identity.SetAttribute('Version', $PackageVersion.ToString())
$manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $Architecture)
$manifest.Package.Properties.DisplayName = $displayName
$visuals = $manifest.SelectSingleNode("//*[local-name()='VisualElements']")
$visuals.SetAttribute('DisplayName', $displayName)
# ASSUMPTION: every package may become a bundle input. Fail closed if startup
# registration returns before the temporary distribution restriction is retired.
if ($manifest.SelectNodes("//*[local-name()='StartupTask' or @Category='windows.startupTask']").Count -gt 0) {
    throw 'Packaged startup registration is temporarily unsupported.'
}
$manifestPath = Join-Path $stage 'Package.appxmanifest'
$manifest.Save($manifestPath)

Add-Type -AssemblyName System.Drawing
$source = [Drawing.Image]::FromFile((Join-Path $projectRoot 'Assets\app_256.png'))
try {
    foreach ($size in @(44, 50, 150)) {
        $image = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($image)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, 0, 0, $size, $size)
            if ($Channel -eq 'Dev') {
                $badgeSize = [int]($size / 3)
                $graphics.FillRectangle([Drawing.Brushes]::DarkOrange, $size - $badgeSize, $size - $badgeSize, $badgeSize, $badgeSize)
            }
            $image.Save((Join-Path $assets "PackageLogo$size.png"), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $graphics.Dispose(); $image.Dispose() }
    }
}
finally { $source.Dispose() }

$arguments = @((Join-Path $projectRoot 'Searchlight.csproj'), '-nologo', '-m:1', '-t:Build', '-v:minimal',
    "-p:Configuration=$Configuration", "-p:Platform=$Architecture", "-p:RuntimeIdentifier=win-$Architecture",
    '-p:SearchlightPackaging=Msix', "-p:SearchlightChannel=$Channel", "-p:SearchlightBuildVersion=$BuildName",
    "-p:SearchlightPackageManifest=$manifestPath", "-p:SearchlightPackageAssets=$assets",
    '-p:GenerateAppxPackageOnBuild=true', '-p:AppxPackageSigningEnabled=false',
    '-p:UapAppxPackageBuildMode=SideloadOnly', '-p:AppxBundle=Never',
    "-p:AppxPackageDir=$output\", '-p:AppxSymbolPackageEnabled=false')
if ($Restore) { $arguments += '-restore' }
Invoke-MsixTool $tools.MSBuild $arguments

$packages = @(Get-ChildItem $output -Recurse -Filter *.msix |
    Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' })
if ($packages.Count -ne 1) { throw "Expected one MSIX in '$output', found $($packages.Count)." }
$package = $packages[0].FullName
if (-not $Unsigned) {
    Invoke-MsixSigning $tools.SignTool $package $CertificateThumbprint $TimestampUrl
}
$result = & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') -Path $package -ExpectedName $PackageName `
    -ExpectedPublisher $Publisher -ExpectedVersion $PackageVersion -ExpectedArchitecture $Architecture `
    -RequireSignature:(-not $Unsigned)
[pscustomobject]@{
    Path = $package
    Channel = $Channel
    BuildName = $BuildName
    Name = $result.Name
    Publisher = $result.Publisher
    Version = $result.Version.ToString()
    Architecture = $result.Architecture
    Signed = -not $Unsigned
}
