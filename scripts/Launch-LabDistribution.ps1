[CmdletBinding()]
param([string]$DistributionRoot = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($DistributionRoot)) { $DistributionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
$DistributionRoot = (Resolve-Path $DistributionRoot).Path
if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -notin @('AMD64', 'x86_64')) { throw 'This distribution requires Windows x64.' }
$manifestPath = Join-Path $DistributionRoot 'distribution-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'distribution-manifest.json is missing.' }
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.runtime -ne 'win-x64' -or $manifest.selfContained -ne $true) { throw 'Distribution manifest is invalid or not self-contained win-x64.' }
foreach ($file in $manifest.files) {
    if ([IO.Path]::IsPathRooted($file.path) -or $file.path -match '(^|/|\\)\.\.(/|\\|$)') { throw "Unsafe distribution path: $($file.path)" }
    $path = Join-Path $DistributionRoot ($file.path -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Distribution file is missing: $($file.path)" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Item $path).Length -ne $file.bytes -or $hash -ne $file.sha256) { throw "Distribution hash mismatch: $($file.path)" }
}
if ((Get-Process -Name 'KlidanAudioProbe.Gui.App','KlidanProbeWorker','KlidanAudioProbe' -ErrorAction SilentlyContinue)) { throw 'An Audio Probe process is already running.' }
$gui = Join-Path $DistributionRoot 'KlidanAudioProbe.Gui.App.exe'
if (-not (Test-Path -LiteralPath $gui -PathType Leaf)) { throw 'GUI executable is missing.' }
Write-Host "KLIDAN Audio Probe laboratory distribution"
Write-Host "Version:       $($manifest.applicationVersion)"
Write-Host "Build:         $($manifest.buildTimestampUtc) · Release · self-contained win-x64"
Write-Host "Source SHA:    $($manifest.sourceHead)"
Write-Host 'Worker/audio:  not connected or started by this launcher.'
Start-Process -FilePath $gui -WorkingDirectory $DistributionRoot
