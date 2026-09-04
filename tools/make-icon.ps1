<#
    Draws src\ConversationManager\app.ico from scratch, so the icon has a source file rather than
    being a binary nobody can change.

    Two overlapping speech bubbles in the app's own palette: the bright accent one in front, a
    dimmer one behind it. The shapes are described once in unit coordinates and drawn natively at
    every size, rather than drawn large and shrunk - a downscaled 256px bubble turns to mush at
    16px, which is the size that actually appears in the taskbar.

    Sizes up to 64 are stored as raw BGRA (the format every Windows shell has always read); 128
    and 256 as PNG, which is what keeps the file small.

    Run:  pwsh -File tools\make-icon.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $root 'src\ConversationManager\app.ico'

# Theme.xaml's palette, so the icon and the window agree.
$accent = [System.Drawing.Color]::FromArgb(255, 0x5B, 0x9C, 0xFF)   # AccentColor
$behind = [System.Drawing.Color]::FromArgb(255, 0x2E, 0x5C, 0x99)   # accent, darkened
$hollow = [System.Drawing.Color]::FromArgb(255, 0x1B, 0x1D, 0x21)   # BgColor

function New-RoundedRect {
    param([double]$X1, [double]$Y1, [double]$X2, [double]$Y2, [double]$R, [double]$S)

    $x = $X1 * $S; $y = $Y1 * $S
    $w = ($X2 - $X1) * $S; $h = ($Y2 - $Y1) * $S
    $d = [Math]::Min($R * $S * 2, [Math]::Min($w, $h))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Frame {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$Size

    # The reply, behind and to the top right.
    $back = New-RoundedRect 0.38 0.06 0.97 0.50 0.13 $s
    $brush = New-Object System.Drawing.SolidBrush $behind
    $g.FillPath($brush, $back)
    $brush.Dispose(); $back.Dispose()

    # The prompt, in front, with its tail.
    $front = New-RoundedRect 0.03 0.29 0.69 0.79 0.15 $s
    $tail = New-Object System.Drawing.Drawing2D.GraphicsPath
    $corners = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](0.15 * $s), [float](0.74 * $s))
        [System.Drawing.PointF]::new([float](0.15 * $s), [float](0.97 * $s))
        [System.Drawing.PointF]::new([float](0.38 * $s), [float](0.76 * $s))
    )
    $tail.AddPolygon($corners)
    $brush = New-Object System.Drawing.SolidBrush $accent
    $g.FillPath($brush, $front)
    $g.FillPath($brush, $tail)
    $brush.Dispose(); $front.Dispose(); $tail.Dispose()

    # Three dots for what was said. Below 24px they close up into a smudge, so they are left out
    # and the silhouette carries the icon on its own.
    if ($Size -ge 24) {
        $brush = New-Object System.Drawing.SolidBrush $hollow
        $r = 0.055 * $s
        foreach ($cx in 0.19, 0.36, 0.53) {
            $g.FillEllipse($brush, ($cx * $s - $r), (0.54 * $s - $r), ($r * 2), ($r * 2))
        }
        $brush.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# ---- ICO container ---------------------------------------------------------------------

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return , $bytes
}

<#
    An icon directory entry in the classic form: a BITMAPINFOHEADER whose height is doubled to
    account for a mask, then bottom-up BGRA rows, then the 1bpp mask itself. The mask is left
    empty because every pixel's transparency is already in its alpha byte - but the rows still
    have to be there, or the shell reads the image as half its height.
#>
function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width; $h = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $locked = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $stride = $locked.Stride
    $raw = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $raw, 0, $raw.Length)
    $Bitmap.UnlockBits($locked)

    $maskStride = [int](([Math]::Floor(($w + 31) / 32)) * 4)
    $xorSize = $w * $h * 4
    $maskSize = $maskStride * $h

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream

    $writer.Write([uint32]40)          # biSize
    $writer.Write([int32]$w)           # biWidth
    $writer.Write([int32]($h * 2))     # biHeight - image plus mask
    $writer.Write([uint16]1)           # biPlanes
    $writer.Write([uint16]32)          # biBitCount
    $writer.Write([uint32]0)           # biCompression - BI_RGB
    $writer.Write([uint32]($xorSize + $maskSize))
    $writer.Write([int32]0); $writer.Write([int32]0)   # pixels per metre
    $writer.Write([uint32]0); $writer.Write([uint32]0) # palette

    for ($y = $h - 1; $y -ge 0; $y--) {
        $writer.Write($raw, $y * $stride, $w * 4)
    }
    $writer.Write((New-Object byte[] $maskSize), 0, $maskSize)

    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose(); $stream.Dispose()
    return , $bytes
}

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$images = @()

foreach ($size in $sizes) {
    $bmp = New-Frame -Size $size
    $bytes = if ($size -ge 128) { Get-PngBytes -Bitmap $bmp } else { Get-DibBytes -Bitmap $bmp }
    $bmp.Dispose()
    $images += , @{ Size = $size; Bytes = $bytes }
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $stream

$writer.Write([uint16]0)                 # reserved
$writer.Write([uint16]1)                 # type: icon
$writer.Write([uint16]$images.Count)

# 6 byte header, then one 16 byte entry each, then the images back to back.
$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $dim = if ($image.Size -ge 256) { 0 } else { $image.Size }   # 256 is stored as 0
    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)               # colours in palette
    $writer.Write([byte]0)               # reserved
    $writer.Write([uint16]1)             # planes
    $writer.Write([uint16]32)            # bits per pixel
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) {
    $writer.Write($image.Bytes, 0, $image.Bytes.Length)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($outPath, $stream.ToArray())
$writer.Dispose(); $stream.Dispose()

$kb = [Math]::Round((Get-Item $outPath).Length / 1KB, 1)
Write-Host "Wrote $outPath  ($($images.Count) sizes, ${kb}KB)"
