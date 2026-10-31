# Draws the PhotoTwins icon (two overlapping photos, the front one with a sun and a hill) and writes
# src\PhotoTwins\photostwins.ico with 16 to 256 pixel pictures. Only needs Windows itself.
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\PhotoTwins\photostwins.ico'

function New-Card([System.Drawing.Graphics]$g, [float]$angle, [System.Drawing.Color]$fill, [System.Drawing.Color]$border, [bool]$scene) {
    $state = $g.Save()
    $g.TranslateTransform(128, 128)
    $g.RotateTransform($angle)
    $rect = New-Object System.Drawing.RectangleF(-78, -64, 156, 128)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 14.0
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $fill), $path)
    $g.DrawPath((New-Object System.Drawing.Pen($border, 7)), $path)
    if ($scene) {
        $g.SetClip($path)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 190, 225, 250))), -78, -64, 156, 128)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 196, 61))), 22, -46, 34, 34)
        $hill = New-Object System.Drawing.Drawing2D.GraphicsPath
        $hill.AddPolygon(@((New-Object System.Drawing.PointF(-90, 70)), (New-Object System.Drawing.PointF(-40, -6)), (New-Object System.Drawing.PointF(0, 40)), (New-Object System.Drawing.PointF(30, 8)), (New-Object System.Drawing.PointF(90, 70))))
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 38, 150, 120))), $hill)
        $g.ResetClip()
        $g.DrawPath((New-Object System.Drawing.Pen($border, 7)), $path)
    }
    $g.Restore($state)
}

$big = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($big)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
New-Card $g -12 ([System.Drawing.Color]::FromArgb(255, 214, 226, 240)) ([System.Drawing.Color]::FromArgb(255, 90, 120, 170)) $false
New-Card $g 8 ([System.Drawing.Color]::White) ([System.Drawing.Color]::FromArgb(255, 25, 95, 190)) $true
$g.Dispose()

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($bmp)
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gg.DrawImage($big, 0, 0, $s, $s)
    $gg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , @($s, $ms.ToArray())
    $bmp.Dispose()
}

$stream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $size = $p[0]; $data = $p[1]
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $w.Write($p[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $stream.ToArray())
$big.Save((Join-Path (Split-Path $out -Parent) 'photostwins-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"Wrote $out"
