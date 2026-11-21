#Requires -Version 5.1
<#
  Draws the Paneless logo (a "P" whose bowl is a screen, on a dark rounded tile) and writes:
    Paneless.ico   16-256 px, used for the window, taskbar, shortcuts and Installed apps
    Paneless.png   256 px, used for the logo in the app's header
  Run it again after changing the design; the output goes next to Paneless.ps1.
  -Preview <file.png> also renders every size side by side (and 4x zoomed) for checking.
#>
param([string]$Preview)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

function Hex([string]$h) { [System.Drawing.ColorTranslator]::FromHtml($h) }
$Tile = Hex '#17181c'; $Edge = Hex '#30323a'; $Stem = Hex '#8db4ff'; $Bowl = Hex '#4f8cff'

function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float](2 * $r)
    $p.AddArc([float]$x, [float]$y, $d, $d, 180, 90)
    $p.AddArc([float]($x + $w - $d), [float]$y, $d, $d, 270, 90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc([float]$x, [float]($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-Logo([int]$size) {
    # Designed on a 112-unit grid. Small sizes get a slightly larger glyph and no edge line.
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 112.0
    $g.ScaleTransform([float]$k, [float]$k)

    $tileShape = New-RoundRect 0.5 0.5 111 111 26
    $g.FillPath((New-Object System.Drawing.SolidBrush($Tile)), $tileShape)
    if ($size -ge 32) { $g.DrawPath((New-Object System.Drawing.Pen($Edge, [float](1.0 / $k))), $tileShape) }   # ~1 px hairline

    $grow = if ($size -le 24) { 1.18 } elseif ($size -le 48) { 1.08 } else { 1.0 }
    if ($grow -ne 1.0) {
        # Grow the glyph around the tile centre so it stays readable at small sizes.
        $g.TranslateTransform(56, 56); $g.ScaleTransform([float]$grow, [float]$grow); $g.TranslateTransform(-56, -56)
    }
    $g.FillPath((New-Object System.Drawing.SolidBrush($Stem)), (New-RoundRect 30 24 15 66 7))
    $g.FillPath((New-Object System.Drawing.SolidBrush($Bowl)), (New-RoundRect 36 24 50 46 12))
    $g.FillPath((New-Object System.Drawing.SolidBrush($Tile)), (New-RoundRect 47 34 28 26 6))
    $g.Dispose()
    $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()     # the comma stops PowerShell from unrolling the byte array
}

function Get-DibBytes($bmp) {
    # Classic 32-bit icon image: BITMAPINFOHEADER + bottom-up BGRA rows + empty AND mask.
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $bw.Write($row)
    }
    $bmp.UnlockBits($data)
    $maskRow = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Logo $s
    # Small sizes as classic bitmaps (read by everything, including .NET's Icon class); large ones as PNG.
    [byte[]]$bytes = if ($s -ge 128) { Get-PngBytes $bmp } else { Get-DibBytes $bmp }
    [pscustomobject]@{ Size = $s; Bytes = $bytes; Bitmap = $bmp }
}

$icoPath = Join-Path $root 'Paneless.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($im in $images) {
    $dim = if ($im.Size -ge 256) { 0 } else { $im.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$im.Bytes.Length); $bw.Write([int]$offset)
    $offset += $im.Bytes.Length
}
foreach ($im in $images) { $bw.Write($im.Bytes) }
$bw.Dispose()
Write-Host "Wrote $icoPath"

$pngPath = Join-Path $root 'Paneless.png'
($images | Where-Object Size -eq 256).Bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote $pngPath"

if ($Preview) {
    # Each size at 1x on dark and light strips, plus 16/32/48 enlarged 4x (nearest neighbour) to judge pixels.
    $sheet = New-Object System.Drawing.Bitmap(900, 420)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear((Hex '#202020'))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush((Hex '#f3f3f3'))), 0, 140, 900, 140)
    $x = 10
    foreach ($im in $images) {
        $g.DrawImage($im.Bitmap, $x, 10, $im.Size, $im.Size)
        $g.DrawImage($im.Bitmap, $x, 150, $im.Size, $im.Size)
        $x += [Math]::Min($im.Size, 128) + 12
    }
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $x = 10
    foreach ($z in @(@(16, 6), @(20, 5), @(24, 4), @(32, 3))) {
        $im = $images | Where-Object Size -eq $z[0]
        $px = $z[0] * $z[1]
        $g.DrawImage($im.Bitmap, $x, 300, $px, $px)
        $x += $px + 24
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Preview $Preview"
}
