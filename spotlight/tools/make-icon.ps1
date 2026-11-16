# Draws spotlight.ico (the exe's icon): a dark tile with one lit window shape, at 256/48/32/16 px.
param([string]$Out = (Join-Path $PSScriptRoot '..\spotlight.ico'))
Add-Type -AssemblyName System.Drawing

function New-RoundPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $tile = New-RoundPath 0 0 $size $size ($size * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 38, 48))), $tile)
    $m = $size * 0.24
    $lit = New-RoundPath $m ($size * 0.28) ($size - 2 * $m) ($size * 0.44) ($size * 0.07)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 199, 117))), $lit)
    $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(65, 36, 2))
    $g.FillRectangle($bar, [float]$m, [float]($size * 0.28), [float]($size - 2 * $m), [float]($size * 0.08))
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
