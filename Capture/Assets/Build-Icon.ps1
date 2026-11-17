# Capture 아이콘 만들기: 파랑-보라 둥근 판 + 흰 뷰파인더 모서리 + 가운데 셔터 점 → Capture.ico (16~256), Capture.png (256)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
$convert = [System.Windows.Media.BrushConverter]::new()

function New-Pen([string]$color, [double]$width) {
    $pen = [System.Windows.Media.Pen]::new($convert.ConvertFromString($color), $width)
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    return $pen
}

foreach ($size in $sizes) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))

    # 판: 왼쪽 위 밝은 파랑 → 오른쪽 아래 보라
    $fill = [System.Windows.Media.LinearGradientBrush]::new()
    $fill.StartPoint = [System.Windows.Point]::new(0, 0); $fill.EndPoint = [System.Windows.Point]::new(1, 1)
    $fill.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromRgb(0x4F, 0x8F, 0xFF), 0))
    $fill.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromRgb(0x63, 0x45, 0xF0), 1))
    $dc.DrawRoundedRectangle($fill, $null, [System.Windows.Rect]::new(2, 2, 60, 60), 14, 14)

    # 위쪽 은은한 빛
    $shine = [System.Windows.Media.LinearGradientBrush]::new()
    $shine.StartPoint = [System.Windows.Point]::new(0, 0); $shine.EndPoint = [System.Windows.Point]::new(0, 1)
    $shine.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0))
    $shine.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55))
    $dc.DrawRoundedRectangle($shine, $null, [System.Windows.Rect]::new(2, 2, 60, 60), 14, 14)

    # 뷰파인더 모서리
    $stroke = if ($size -le 24) { 5.5 } else { 4.6 }
    $pen = New-Pen '#FFFFFFFF' $stroke
    $corners = 'M 14,25 L 14,14 25,14 M 39,14 L 50,14 50,25 M 50,39 L 50,50 39,50 M 25,50 L 14,50 14,39'
    $dc.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse($corners))

    # 셔터 점: 흰 원 + 안쪽 보라 점
    $dc.DrawEllipse([System.Windows.Media.Brushes]::White, $null, [System.Windows.Point]::new(32, 32), 7.5, 7.5)
    if ($size -ge 32) {
        $dc.DrawEllipse($convert.ConvertFromString('#FF5A5CF6'), $null, [System.Windows.Point]::new(32, 32), 3.4, 3.4)
    }

    $dc.Pop()
    $dc.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames += , $stream.ToArray()
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'Capture.png'), $stream.ToArray()) }
    $stream.Dispose()
}

$output = [System.IO.File]::Create((Join-Path $PSScriptRoot 'Capture.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Host "Capture.ico, Capture.png"
