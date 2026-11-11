#requires -Version 7.0
param(
    [ValidateSet('DryRun', 'ShortSearch', 'RepeatSelected')][string]$Mode = 'DryRun',
    [ValidateSet('BLOODY', 'FAILED', 'UNKNOWN', 'FIRST_CLEAN')][string]$Scenario = 'BLOODY',
    [switch]$AuthorizeShortSearch,
    [switch]$AuthorizeR5,
    [string]$SourceEvidence
)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$manifestPath = Join-Path $package 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.sourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'QA manifest source SHA is invalid.' }
foreach ($item in $manifest.files) {
    $file = [IO.Path]::GetFullPath((Join-Path $package $item.path))
    if (!$file.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe package path: $($item.path)" }
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $($item.path)" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256) { throw "Package hash mismatch: $($item.path)" }
}
if ($Mode -eq 'ShortSearch' -and !$AuthorizeShortSearch) { throw 'ShortSearch is locked. Pass -AuthorizeShortSearch only after operator authorization.' }
if ($Mode -eq 'RepeatSelected' -and (!$AuthorizeR5 -or $AuthorizeShortSearch -or !$SourceEvidence)) { throw 'RepeatSelected requires -AuthorizeR5 and -SourceEvidence; ShortSearch authorization is not applicable.' }
if ($AuthorizeR5 -and $Mode -ne 'RepeatSelected') { throw '-AuthorizeR5 is valid only for RepeatSelected.' }
if ($Mode -eq 'RepeatSelected' -and !(Test-Path -LiteralPath $SourceEvidence -PathType Leaf)) { throw 'Source evidence summary does not exist.' }
$runner = Join-Path $package 'KlidanAudioProbe.V4.Headless.exe'
$worker = if ($Mode -eq 'DryRun') { Join-Path $package 'KlidanProbeWorker.FakeTest.exe' } else { Join-Path $package 'KlidanProbeWorker.exe' }
foreach ($required in @($runner, $worker, (Join-Path $package 'KlidanAudioProbe.exe'))) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Release package is incomplete: $required" }
}
$evidenceRoot = Join-Path $package 'evidence'
$logsRoot = Join-Path $package 'logs'
New-Item -ItemType Directory -Force -Path $evidenceRoot, $logsRoot | Out-Null
$runId = [Guid]::NewGuid().ToString('N')
$started = [DateTimeOffset]::UtcNow
$log = Join-Path $logsRoot "headless-$runId.log"
$record = [ordered]@{
    schemaVersion = 1; mode = $Mode; scenario = $(if ($Mode -eq 'DryRun') { $Scenario } else { $null }); sourceEvidence = $SourceEvidence
    runId = $runId; sourceCommit = $manifest.sourceCommit; startedUtc = $started.ToString('O')
    runnerSha256 = (Get-FileHash -LiteralPath $runner -Algorithm SHA256).Hash.ToLowerInvariant()
    workerSha256 = (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash.ToLowerInvariant()
    authorizeShortSearchSwitch = [bool]$AuthorizeShortSearch; authorizeR5Switch = [bool]$AuthorizeR5
    log = [IO.Path]::GetRelativePath($package, $log); exitCode = $null; endedUtc = $null
}
$recordPath = Join-Path $logsRoot "headless-$runId.json"
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8
$exitCode = 3
$priorBuildSha = $env:KLIDAN_BUILD_SHA
$env:KLIDAN_BUILD_SHA = $manifest.sourceCommit.ToLowerInvariant()
try {
    $runnerMode = switch ($Mode) { DryRun { 'DRY_RUN' } ShortSearch { 'SHORT_SEARCH' } RepeatSelected { 'REPEAT_SELECTED' } }
    $runnerArgs = @('--mode', $runnerMode, '--worker-path', $worker, '--evidence-root', $evidenceRoot)
    if ($Mode -eq 'DryRun') { $runnerArgs += @('--scenario', $Scenario) }
    if ($AuthorizeShortSearch) { $runnerArgs += '--authorize-short-search' }
    if ($AuthorizeR5) { $runnerArgs += '--authorize-r5' }
    if ($Mode -eq 'RepeatSelected') { $runnerArgs += @('--source-evidence', (Resolve-Path -LiteralPath $SourceEvidence).Path) }
    & $runner @runnerArgs 2>&1 | Tee-Object -FilePath $log
    $exitCode = $LASTEXITCODE
}
finally { $env:KLIDAN_BUILD_SHA = $priorBuildSha }
$record.exitCode = $exitCode
$record.endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8
Write-Host "RUN_LOG=$log"
Write-Host "RUN_METADATA=$recordPath"
exit $exitCode
