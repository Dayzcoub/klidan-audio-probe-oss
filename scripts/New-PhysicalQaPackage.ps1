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
1. Подключите Bloody вручную и дождитесь его готовности в Windows.
2. Запустите `Run-KlidanAudioProbePhysicalQa.ps1` из PowerShell 7 в этой папке; launcher проверит SHA-256 файлов и запишет полный build SHA в diagnostic evidence. GUI не запускает обнаружение или аудиооперации автоматически.
3. Выберите Bloody Capture/Render, маршрут MONO_TO_STEREO, 48 kHz PCM16 и активные каналы. В разделе «V4 CUSTOM — Точечная диагностика» подготовьте 5 ms план, убедитесь, что он содержит только C240/R240, отдельно подтвердите согласие и запустите тест.
4. Дождитесь сохранения evidence и подтверждённого завершения сессии. Затем выберите 10 ms, подготовьте новый план, проверьте только C480/R480, подтвердите новое согласие и запустите второй тест.
5. При UNKNOWN, ошибке очистки или таймауте не запускайте следующий тест; используйте «Отмена» и дождитесь терминального состояния. Закройте приложение после обоих результатов.
6. Передайте содержимое `logs` и `evidence`; диагностический тест не создаёт профиль.

## Operator steps (EN)
Connect Bloody manually, run the PowerShell 7 launcher (it verifies the SHA-256 manifest and records the full build SHA), then select its Capture/Render endpoints, MONO_TO_STEREO, 48 kHz PCM16 and channels. In “V4 CUSTOM — Point diagnostic”, prepare 5 ms, verify the single C240/R240 candidate, explicitly consent and run. After evidence save and confirmed session close, prepare 10 ms as a new plan, verify only C480/R480, explicitly consent again and run. Stop after UNKNOWN, timeout or uncertain cleanup. Share `logs` and `evidence`; diagnostics never create a profile. The app does not auto-discover or start audio work.

Physical tests are pending operator execution. Do not launch the GUI elevated and do not disconnect the device while a scan is active.
"@ | Set-Content -LiteralPath (Join-Path $output 'README-Physical-QA.md') -Encoding UTF8
$files = Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
}
[pscustomobject]@{ schemaVersion = 1; sourceCommit = $head; configuration = 'Release'; runtime = 'win-x64-self-contained'; files = @($files) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
Write-Output "QA package created: $output"
