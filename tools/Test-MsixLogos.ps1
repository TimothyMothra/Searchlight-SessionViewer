[CmdletBinding()]
param([string]$PackagePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ASSUMPTION: half-pixel scale dimensions round upward to the Windows asset size.
# Keep expected dimensions independent of the generator's size calculation.
$expected = @{
    'PackageLogo44.png' = 44
    'PackageLogo44.scale-125.png' = 55
    'PackageLogo44.scale-150.png' = 66
    'PackageLogo44.scale-200.png' = 88
    'PackageLogo44.scale-400.png' = 176
    'PackageLogo44.targetsize-256.png' = 256
    'PackageLogo50.png' = 50
    'PackageLogo50.scale-125.png' = 63
    'PackageLogo50.scale-150.png' = 75
    'PackageLogo50.scale-200.png' = 100
    'PackageLogo50.scale-400.png' = 200
    'PackageLogo150.png' = 150
    'PackageLogo150.scale-125.png' = 188
    'PackageLogo150.scale-150.png' = 225
    'PackageLogo150.scale-200.png' = 300
    'PackageLogo150.scale-400.png' = 600
}

function Assert-Logo([Drawing.Bitmap]$Image, [string]$Name, [int]$Size, [bool]$Dev) {
    if ($Image.Width -ne $Size -or $Image.Height -ne $Size) {
        throw "$Name must be ${Size}x${Size}, found $($Image.Width)x$($Image.Height)."
    }
    if ($Image.GetPixel(0, 0).A -ne 0) { throw "$Name lost its transparent corner." }
    $badge = $Image.GetPixel([int]($Size * 0.8), [int]($Size * 0.8))
    if (($badge.ToArgb() -eq [Drawing.Color]::DarkOrange.ToArgb()) -ne $Dev) {
        throw "$Name has incorrect Dev badge behavior."
    }
}

if ($PackagePath) {
    . (Join-Path $PSScriptRoot 'Msix-Common.ps1')
    $manifest = Get-MsixManifest $PackagePath
    $dev = $manifest.Name -eq 'TimothyMothra.Searchlight.Dev'
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
    try {
        $expected['app_1024.png'] = 1024
        foreach ($name in $expected.Keys) {
            $entry = $zip.GetEntry("Assets/$name")
            if (-not $entry) { throw "Package is missing Assets/$name." }
            $stream = $entry.Open()
            try {
                $image = [Drawing.Bitmap]::new($stream)
                try { Assert-Logo $image $name $expected[$name] ($dev -and $name -ne 'app_1024.png') }
                finally { $image.Dispose() }
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    Write-Host "PASS: packaged logo dimensions, transparency, and channel badges ($($manifest.Architecture))."
}
else {
    $root = Join-Path ([IO.Path]::GetTempPath()) "Searchlight-logo-test-$([guid]::NewGuid())"
    [IO.Directory]::CreateDirectory($root) | Out-Null
    try {
        $source = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Searchlight\Assets\app_1024.png'
        foreach ($channel in @('Production', 'Dev')) {
            $output = Join-Path $root $channel
            & (Join-Path $PSScriptRoot 'New-MsixLogos.ps1') -SourcePath $source -OutputDirectory $output -Channel $channel
            if (@(Get-ChildItem $output -Filter *.png).Count -ne $expected.Count) {
                throw 'Unexpected generated logo count.'
            }
            foreach ($name in $expected.Keys) {
                $image = [Drawing.Bitmap]::new((Join-Path $output $name))
                try { Assert-Logo $image $name $expected[$name] ($channel -eq 'Dev') }
                finally { $image.Dispose() }
            }
        }
        $rejected = $false
        try {
            & (Join-Path $PSScriptRoot 'New-MsixLogos.ps1') `
                -SourcePath (Join-Path $root 'Production\PackageLogo150.png') `
                -OutputDirectory (Join-Path $root 'Invalid') -Channel Production
        }
        catch {
            if ($_.Exception.Message -notlike '*square master of at least 1024 pixels*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'An undersized master was accepted.' }
    }
    finally { [IO.Directory]::Delete($root, $true) }
    Write-Host 'PASS: all logo scales, target size, transparency, Dev badges, and undersized-master rejection.'
}
