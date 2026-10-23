[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\src\PPTAssistant.App\Assets\PPTA.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\PPTAssistant.App\Assets\PPTA.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$destinationPath = [IO.Path]::GetFullPath($Destination)
$sourceBitmap = [Windows.Media.Imaging.BitmapImage]::new()
$sourceBitmap.BeginInit()
$sourceBitmap.CacheOption = [Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$sourceBitmap.UriSource = [Uri]::new($sourcePath)
$sourceBitmap.EndInit()
$sourceBitmap.Freeze()

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    $scale = [Math]::Min($size / $sourceBitmap.PixelWidth, $size / $sourceBitmap.PixelHeight)
    $width = $sourceBitmap.PixelWidth * $scale
    $height = $sourceBitmap.PixelHeight * $scale
    $visual = [Windows.Media.DrawingVisual]::new()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [Windows.Media.BitmapScalingMode]::HighQuality)
    $drawing = $visual.RenderOpen()
    try {
        $drawing.DrawImage($sourceBitmap, [Windows.Rect]::new(($size - $width) / 2, ($size - $height) / 2, $width, $height))
    }
    finally {
        $drawing.Close()
    }

    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        [pscustomobject]@{ Size = $size; Data = $memory.ToArray() }
    }
    finally {
        $memory.Dispose()
    }
}

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
$stream = [IO.File]::Create($destinationPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Data)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Output "Created $destinationPath ($($sizes -join ', ') px)"
