# Draws glance.ico (the exe's icon): an amber tile with two bars, at 256/48/32/16 px.
param([string]$Out = (Join-Path $PSScriptRoot '..\glance.ico'))
Add-Type -AssemblyName System.Drawing

function New-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = $size * 0.25
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 199, 117))
    $g.FillPath($fill, $path)
    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(65, 36, 2))
    $pad = [float]($size * 0.22)
    $barH = [float]($size * 0.12)
    $gap = [float]($size * 0.10)
    $w = [float]($size - 2 * $pad)
    $y1 = [float]($size * 0.30)
    $y2 = [float]($y1 + $barH + $gap)
    $y3 = [float]($y2 + $barH + $gap)
    $g.FillRectangle($ink, $pad, $y1, $w, $barH)
    $g.FillRectangle($ink, $pad, $y2, [float]($w * 0.62), $barH)
    $g.FillRectangle($ink, $pad, $y3, [float]($w * 0.38), $barH)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

$sizes = 256, 48, 32, 16
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $images.Add([byte[]](New-Tile $s)) }

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    if ($s -ge 256) { $b = [byte]0 } else { $b = [byte]$s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Dispose(); $fs.Dispose()
Write-Output "wrote $Out"
