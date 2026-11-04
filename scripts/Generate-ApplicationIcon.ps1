[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$Destination = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $repoRoot 'assets\application-icon.png' }
if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $repoRoot 'assets\application-icon.ico' }
$Source = (Resolve-Path -LiteralPath $Source).Path

Add-Type -AssemblyName PresentationCore, WindowsBase
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$sourceBitmap = New-Object System.Windows.Media.Imaging.BitmapImage
$sourceBitmap.BeginInit()
$sourceBitmap.UriSource = [Uri]::new($Source)
$sourceBitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$sourceBitmap.EndInit()
$sourceBitmap.Freeze()

$frames = foreach ($size in $sizes) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($sourceBitmap, [System.Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
}

$directory = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$output = New-Object System.IO.FileStream($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
$writer = New-Object System.IO.BinaryWriter($output)
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
    $writer.Write([uint32]$frame.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
$writer.Dispose()
$output.Dispose()
Write-Host "Created $Destination ($($frames.Count) PNG icon frames: $($sizes -join ', '))"
