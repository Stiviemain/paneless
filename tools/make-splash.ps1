#Requires -Version 5.1
<# Generates Paneless-splash.png at the repo root for the Velopack Windows installer (--splashImage). #>
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$W = 520
$H = 280
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Subtle dark background gradient
$bgRect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $bgRect,
    [System.Drawing.Color]::FromArgb(255, 24, 26, 33),
    [System.Drawing.Color]::FromArgb(255, 16, 18, 24),
    90.0
)
$g.FillRectangle($bgBrush, $bgRect)
$bgBrush.Dispose()

# Soft blue ambient glow behind logo
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(110, -30, 300, 260)
$gb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$gb.CenterColor = [System.Drawing.Color]::FromArgb(46, 79, 140, 255)
$gb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 79, 140, 255))
$g.FillPath($gb, $glow)
$gb.Dispose(); $glow.Dispose()

# Centered logo badge
$tileSize = 76.0
$tileX = ($W - $tileSize) / 2.0
$tileY = 48.0
$tilePath = New-RoundedPath $tileX $tileY $tileSize $tileSize 18
$tileFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 34, 46))
$tilePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 56, 62, 80), 1.5)
$g.FillPath($tileFill, $tilePath)
$g.DrawPath($tilePen, $tilePath)
$tileFill.Dispose(); $tilePen.Dispose(); $tilePath.Dispose()

$logo = [System.Drawing.Image]::FromFile((Join-Path $root 'Paneless.png'))
$g.DrawImage($logo, ($tileX + 11), ($tileY + 11), 54, 54)
$logo.Dispose()

# Centered text
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Near

$fTitle = New-Object System.Drawing.Font("Segoe UI", [float]34, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$bWhite = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 240, 243, 248))
$g.DrawString("Paneless", $fTitle, $bWhite, (New-Object System.Drawing.RectangleF(0, 140, $W, 48)), $sf)

$fSub = New-Object System.Drawing.Font("Segoe UI Semibold", [float]15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$bDim = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 154, 160, 174))
$g.DrawString("Installing Paneless...", $fSub, $bDim, (New-Object System.Drawing.RectangleF(0, 192, $W, 28)), $sf)

# Subtle 1px outer frame (top/left/right; bottom edge left clean for Velopack's progress bar)
$framePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 48, 52, 66), 1.0)
$g.DrawRectangle($framePen, 0, 0, $W - 1, $H - 1)
$framePen.Dispose()

$out = Join-Path $root 'Paneless-splash.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)

$sf.Dispose(); $fTitle.Dispose(); $fSub.Dispose(); $bWhite.Dispose(); $bDim.Dispose()
$g.Dispose(); $bmp.Dispose()
Write-Host "Wrote $out ($W x $H)"
