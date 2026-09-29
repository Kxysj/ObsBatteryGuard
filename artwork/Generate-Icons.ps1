$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\ObsBatteryGuard.App\Assets'))
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null
$xaml = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppIcon.xaml') -Raw

function Render-Png([string]$markup, [int]$size) {
    $drawing = [Windows.Markup.XamlReader]::Parse($markup)
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    # Keep padding fixed in normalized coordinates, including small tray sizes.
    $context.DrawImage($drawing, [Windows.Rect]::new($size / 32.0, $size / 32.0, $size * 30.0 / 32.0, $size * 30.0 / 32.0))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}

function Save-Icon([string]$name, [string]$markup) {
    $sizes = @(16,20,24,32,40,48,64,128,256)
    $frames = @($sizes | ForEach-Object { ,(Render-Png $markup $_) })
    $stream = [IO.File]::Create((Join-Path $assetRoot ($name + '.ico')))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $stream.Dispose() }
}

Save-Icon 'app' $xaml
[IO.File]::WriteAllBytes((Join-Path $assetRoot 'app-icon.png'), (Render-Png $xaml 256))

# Same silhouette; shape + color distinguish states without relying on color alone.
$symbols = @{
    idle = '<GeometryDrawing Brush="#18A980" Geometry="M 23,33 L 29,39 L 41,24 L 37,21 L 29,31 L 26,28 Z"/>'
    recording = '<GeometryDrawing Brush="#E64659"><GeometryDrawing.Geometry><EllipseGeometry Center="31,31" RadiusX="10" RadiusY="10"/></GeometryDrawing.Geometry></GeometryDrawing>'
    paused = '<GeometryDrawing Brush="#BD7100" Geometry="M 22,21 L 28,21 L 28,40 L 22,40 Z M 34,21 L 40,21 L 40,40 L 34,40 Z"/>'
    warning = '<GeometryDrawing Brush="#BC6600" Geometry="M 28,18 L 35,18 L 34,34 L 29,34 Z M 28,38 L 35,38 L 35,45 L 28,45 Z"/>'
    offline = '<GeometryDrawing Brush="#64748B" Geometry="M 23,20 L 31,28 L 39,20 L 43,24 L 35,32 L 43,40 L 39,44 L 31,36 L 23,44 L 19,40 L 27,32 L 19,24 Z"/>'
    busy = '<GeometryDrawing Brush="#2874C6" Geometry="M 21,21 L 41,21 L 41,25 L 34,31 L 41,38 L 41,42 L 21,42 L 21,38 L 28,31 L 21,25 Z"/>'
}
foreach ($state in $symbols.Keys) {
    $tray = $xaml.Substring(0, $xaml.IndexOf('      <GeometryDrawing Brush="#173969"')) + $symbols[$state] + '</DrawingGroup></DrawingImage.Drawing></DrawingImage>'
    if ($state -in @('paused','warning')) { $tray = $tray.Replace('#507DFF','#EAAF35').Replace('#244DCA','#B86613') }
    if ($state -eq 'offline') { $tray = $tray.Replace('#507DFF','#94A3B8').Replace('#244DCA','#475569') }
    Save-Icon ('tray-' + $state) $tray
    [IO.File]::WriteAllBytes((Join-Path $assetRoot ('tray-' + $state + '.png')), (Render-Png $tray 64))
}
Write-Output 'Generated app icon and 6 tray states, each ICO with 16/20/24/32/40/48/64/128/256 px frames.'
