[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('Dev', 'Production')][string]$Channel
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$source = [Drawing.Image]::FromFile([IO.Path]::GetFullPath($SourcePath))
try {
    if ($source.Width -lt 1024 -or $source.Height -ne $source.Width) {
        throw 'Package logos require a square master of at least 1024 pixels; run tools\make_icon.py --master-only.'
    }
    # ASSUMPTION: unqualified assets remain 100% fallbacks for manifest-path readers.
    # Resource-aware consumers select scale/targetsize variants instead of enlarging them.
    $variants = foreach ($baseSize in @(44, 50, 150)) {
        foreach ($scale in @(100, 125, 150, 200, 400)) {
            $suffix = if ($scale -eq 100) { '' } else { ".scale-$scale" }
            @{
                Name = "PackageLogo$baseSize$suffix.png"
                Size = [int][Math]::Ceiling($baseSize * $scale / 100.0)
            }
        }
    }
    $variants += @{ Name = 'PackageLogo44.targetsize-256.png'; Size = 256 }
    foreach ($variant in $variants) {
        $size = $variant.Size
        $image = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($image)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $size, $size)
            if ($Channel -eq 'Dev') {
                $badgeSize = [int]($size / 3)
                $graphics.FillRectangle([Drawing.Brushes]::DarkOrange, $size - $badgeSize, $size - $badgeSize, $badgeSize, $badgeSize)
            }
            $image.Save((Join-Path $OutputDirectory $variant.Name), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $graphics.Dispose(); $image.Dispose() }
    }
}
finally { $source.Dispose() }
