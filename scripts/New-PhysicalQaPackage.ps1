#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\build\physical-qa-publish')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = (Resolve-Path $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite QA package: $output" }
foreach ($required in @('KlidanAudioProbe.Gui.App.exe', 'KlidanProbeWorker.exe', 'KlidanAudioProbe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) { throw "Release publish output is missing $required" }
}
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read source revision.' }
New-Item -ItemType Directory -Path $output | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $output -Recurse
New-Item -ItemType Directory -Path (Join-Path $output 'logs'), (Join-Path $output 'evidence'), (Join-Path $output 'profiles') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-KlidanAudioProbePhysicalQa.ps1') -Destination $output
@"
# KLIDAN Audio Probe physical QA package

Source commit: $head
Configuration: Release, self-contained win-x64
Native host dependency check: KlidanAudioProbe.exe and KlidanProbeWorker.exe use Windows system DLLs only (ole32, SETUPAPI where needed, AVRT, bcrypt, KERNEL32); no app-local VC++ runtime was required.

## Operator steps (RU)
1. Подключите аудиоустройство вручную и дождитесь его готовности в Windows.
2. Запустите `Run-KlidanAudioProbePhysicalQa.ps1` из этой папки. Приложение не запускает обнаружение или аудиооперации автоматически.
3. Выберите нужные Capture/Render endpoints и маршрут, проверьте план, затем явно подтвердите согласие и нажмите запуск сканирования.
4. Для безопасной остановки нажмите «Отмена» в приложении и дождитесь финального статуса/сохранения evidence. Затем закройте приложение.
5. Отправьте оператору содержимое `logs` и `evidence`; профили находятся в `profiles`.

## Operator steps (EN)
Connect the device manually, run the PowerShell launcher, select the exact Capture/Render endpoints and route, review and explicitly consent before starting. The app does not auto-discover or start audio work. To stop, use Cancel and wait for terminal cleanup/evidence status before closing. Share `logs` and `evidence` with the QA owner.

Physical tests are pending operator execution. Do not launch the GUI elevated and do not disconnect the device while a scan is active.
"@ | Set-Content -LiteralPath (Join-Path $output 'README-Physical-QA.md') -Encoding UTF8
$files = Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
}
[pscustomobject]@{ schemaVersion = 1; sourceCommit = $head; configuration = 'Release'; runtime = 'win-x64-self-contained'; files = @($files) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
Write-Output "QA package created: $output"
