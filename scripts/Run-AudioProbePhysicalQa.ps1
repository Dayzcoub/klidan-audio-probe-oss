param(
    [string]$InstallPath,
    [string]$ProjectDataRoot = 'H:\Projects\Audio-Probe-OSS'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    $registered = foreach ($key in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )) {
        Get-ItemProperty $key -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -match 'Audio Probe' -and $_.InstallLocation } |
            ForEach-Object { Join-Path $_.InstallLocation 'KlidanAudioProbe.Gui.App.exe' }
    }
    $built = Get-ChildItem (Join-Path $ProjectDataRoot 'builds') -Filter 'KlidanAudioProbe.Gui.App.exe' -File -Recurse -Depth 8 -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | ForEach-Object FullName
    $InstallPath = @($registered + $built | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)[0]
}

if (-not (Test-Path -LiteralPath $InstallPath -PathType Leaf)) {
    throw 'Audio Probe was not found in registered install locations or project builds. Pass -InstallPath once to use a nonstandard installation.'
}

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runStartedUtc = [DateTime]::UtcNow
$runRoot = Join-Path $ProjectDataRoot "logs\physical-qa\$runId"
$reportRoot = Join-Path $runRoot 'reports'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$metadataPath = Join-Path $runRoot 'run.json'
$process = Start-Process -FilePath $InstallPath -PassThru
@{
    startedUtc = $runStartedUtc.ToString('O')
    executable = (Resolve-Path -LiteralPath $InstallPath).Path
    processId = $process.Id
    evidenceSource = (Join-Path $env:LOCALAPPDATA 'KlidanAudioProbe\Scans\V2')
    reportDestination = $reportRoot
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $metadataPath -Encoding utf8

Write-Host "Audio Probe launched (PID $($process.Id)). Complete the approved physical QA in the app, then close it to collect reports."
Wait-Process -Id $process.Id

$evidenceRoot = Join-Path $env:LOCALAPPDATA 'KlidanAudioProbe\Scans\V2'
if (Test-Path -LiteralPath $evidenceRoot -PathType Container) {
    Get-ChildItem -LiteralPath $evidenceRoot -File -Recurse | Where-Object LastWriteTimeUtc -GE $runStartedUtc | ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath($evidenceRoot, $_.FullName)
        $reportPath = Join-Path $reportRoot $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $reportPath
    }
}
$run = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$run | Add-Member -NotePropertyName completedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O'))
$run | Add-Member -NotePropertyName collectedReports -NotePropertyValue (@(Get-ChildItem -LiteralPath $reportRoot -File | ForEach-Object Name))
$run | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $metadataPath -Encoding utf8
Write-Host "QA session records: $runRoot"
