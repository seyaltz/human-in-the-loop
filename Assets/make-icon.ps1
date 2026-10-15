# Draws the Dev break logo (the same shapes as logo.svg) at every icon size and writes DevBreak.ico.
# 32 px and below drop the speech bubble so the question mark stays sharp in the tray.
# Run from Windows PowerShell: .\Assets\make-icon.ps1
Add-Type -AssemblyName PresentationCore, WindowsBase

$violet = [Windows.Media.Color]::FromRgb(0x5B, 0x3D, 0xF5)
$marker = [Windows.Media.Color]::FromRgb(0xD4, 0xF5, 0x3C)
$ink = [Windows.Media.Color]::FromRgb(0x16, 0x1A, 0x33)

function Draw-Png([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($size / 100.0), ($size / 100.0)))
    $dc.DrawRoundedRectangle((New-Object Windows.Media.SolidColorBrush $violet), $null, (New-Object Windows.Rect 0, 0, 100, 100), 24, 24)
    if ($size -le 32) {
        $pen = New-Object Windows.Media.Pen (New-Object Windows.Media.SolidColorBrush $marker), 12
        $pen.StartLineCap = $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
        $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse('M36,36 a14,14 0 1 1 20,12.5 c-4,2 -6,4.5 -6,9 v3'))
        $dc.DrawEllipse((New-Object Windows.Media.SolidColorBrush $marker), $null, (New-Object Windows.Point 50, 77), 7, 7)
    } else {
        $bubble = 'M22,20 h56 a12,12 0 0 1 12,12 v34 a12,12 0 0 1 -12,12 h-30 l-14,12 v-12 h-12 a12,12 0 0 1 -12,-12 v-34 a12,12 0 0 1 12,-12 z'
        $dc.DrawGeometry((New-Object Windows.Media.SolidColorBrush $marker), $null, [Windows.Media.Geometry]::Parse($bubble))
        $pen = New-Object Windows.Media.Pen (New-Object Windows.Media.SolidColorBrush $ink), 8
        $pen.StartLineCap = $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
        $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse('M40,40 a10,10 0 1 1 15,8.7 c-3.2,1.8 -5,3.6 -5,7.3 v2'))
        $dc.DrawEllipse((New-Object Windows.Media.SolidColorBrush $ink), $null, (New-Object Windows.Point 50, 70), 4.6, 4.6)
    }
    $dc.Pop()
    $dc.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = $sizes | ForEach-Object { ,(Draw-Png $_) }

# an .ico is a small header, one directory entry per image, then the PNGs
$out = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$pngs[$i].Length); $writer.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $writer.Write($png) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'DevBreak.ico'), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'logo-256.png'), $pngs[-1])
Write-Output "Wrote DevBreak.ico ($($sizes -join ', ') px) and logo-256.png"
