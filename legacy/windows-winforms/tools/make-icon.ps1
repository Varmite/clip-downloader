# Draws the app icon (red rounded square with a white download arrow) and saves it as a multi-size .ico.
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0

    # Rounded square background
    $r = 56 * $s; $pad = 8 * $s; $w = $size - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $w, $w
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 72, 72)), ([System.Drawing.Color]::FromArgb(200, 16, 32)), 90.0
    $g.FillPath($brush, $path)

    # Download arrow: shaft + head, then a tray line underneath
    $white = [System.Drawing.Brushes]::White
    $shaftW = [Math]::Max(2, 36 * $s)
    $g.FillRectangle($white, [single](128 * $s - $shaftW / 2), [single](52 * $s), [single]$shaftW, [single](82 * $s))
    $head = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (68 * $s), (122 * $s)),
        (New-Object System.Drawing.PointF (188 * $s), (122 * $s)),
        (New-Object System.Drawing.PointF (128 * $s), (184 * $s)))
    $g.FillPolygon($white, $head)
    $trayH = [Math]::Max(2, 22 * $s)
    $g.FillRectangle($white, [single](64 * $s), [single](198 * $s), [single](128 * $s), [single]$trayH)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    if ($size -eq 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Classic DIB entry: BITMAPINFOHEADER + bottom-up BGRA pixels + empty AND mask
        $bw = New-Object System.IO.BinaryWriter $ms
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]($size * $size * 4 + $maskRow * $size)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $ms.ToArray() }
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$img.Data.Length); $w.Write([int]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) { $w.Write($img.Data) }
$w.Close()
Write-Host "Icon written to $Out"
