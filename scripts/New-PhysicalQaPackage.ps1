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
2. Запустите `Run-KlidanAudioProbePhysicalQa.ps1` из PowerShell 7 в этой папке. Launcher проверит все SHA-256, запишет UTC/PID/build identity и откроет только Release GUI с отдельной папкой evidence. Аудиооперация автоматически не запускается.
3. В GUI выберите Bloody Capture/Render, маршрут MONO_TO_STEREO, 48 kHz PCM16 и подтверждённые активные каналы. В `V4 CUSTOM — Точечная диагностика` выберите 10 ms и PRO_AUDIO, подготовьте план и проверьте N=1, C480/R480, R=0, 3 операции и 5 секунд.
4. Создайте отдельное согласие и запустите ровно один 5-секундный Exclusive Duplex. Не запускайте полный поиск, повторы или следующий период. Этот результат предварительный и не создаёт профиль.
5. При FAILED сохраните причину. При UNKNOWN, таймауте, отмене или неизвестной очистке ничего больше не запускайте. Закройте приложение после сохранения JSON.
6. Передайте Codex содержимое `logs` и `evidence` для проверки полного JSON и cleanup. Квалификация R=5 и испытание под нагрузкой KLIDAN AudioHost — отдельные последующие этапы.

## Operator steps (EN)
Connect Bloody manually and run the PowerShell 7 launcher. It verifies the complete package SHA-256 manifest and records launch metadata, then opens Release GUI with isolated evidence storage; it starts no audio operation. Select the exact Capture/Render pair, MONO_TO_STEREO, 48 kHz PCM16, 10 ms and PRO_AUDIO. Verify the one-candidate C480/R480, R=0 and 3-operation/5-second budget, then explicitly consent and run exactly one diagnostic Duplex. Do not run the full search or repeats. Preserve FAILED evidence; stop on UNKNOWN, timeout, cancellation or uncertain cleanup. Diagnostics never create a profile. Send `logs` and `evidence` to Codex for JSON and cleanup review. R=5 and KLIDAN AudioHost load qualification are later stages.

Physical tests are pending operator execution. Do not launch the GUI elevated and do not disconnect the device while a scan is active.
"@ | Set-Content -LiteralPath (Join-Path $output 'README-Physical-QA.md') -Encoding UTF8
$files = Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
}
[pscustomobject]@{ schemaVersion = 1; sourceCommit = $head; configuration = 'Release'; runtime = 'win-x64-self-contained'; files = @($files) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
Write-Output "QA package created: $output"
