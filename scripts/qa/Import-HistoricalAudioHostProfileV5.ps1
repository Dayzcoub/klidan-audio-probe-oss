[CmdletBinding()]
param(
    [string]$RepositoryRoot = 'H:\Projects\Audio-Probe-OSS\worktrees\audio-probe-oss\AS-N4B.2.2B5A-gui-integration',
    [string]$ScannerRunDirectory = 'H:\Projects\Audio-Probe-OSS\qa\B5B.20B\Release-QA-20261113T112125Z-def5e75defb7\evidence\bc6638e5-6e3e-4f31-9a5a-c2084049d37f',
    [string]$EvidenceIndexPath = 'H:\Projects\KLIDAN\artifacts\B5B.21C-BloodyG501-historical-evidence-index.json',
    [string]$MinimumAuditPath = 'H:\Projects\Audio-Probe-OSS\artifacts\B5B.21I-BloodyG501-consented-domain-minimum-audit.json',
    [string]$AudioHostEvidenceManifestPath = 'H:\Projects\Audio-Probe-OSS\artifacts\B5B.21J-AudioHost-physical-evidence-manifest.json',
    [string]$AudioHostRunDirectory = 'H:\Projects\KLIDAN\qa\physical\B5B.21F\2b07ff5e579d451eaa9b539439aca44f\55271755e4d24fd4b411bfd7e346d0c8',
    [string]$ProfileDirectory = (Join-Path $env:LOCALAPPDATA 'KlidanAudioProbe\Profiles'),
    [switch]$ConfirmOperatorProfileWrite
)
$ErrorActionPreference = 'Stop'
$expectedAuditSha = '9fb80629f3ad935812eda6c2879144983d2a091ad77718958e9ae6fdbf87ecef'
$expectedManifestSha = 'caf9523216fc6e4a15dd4824ebc47767ccdf9fe19dfefaedc5a10e4f79f6eea8'
$expectedIndexSha = '9feb3964c44afc7e22836f9e53e6049d04532dce0a5c14d44b8e5187dc4bf60c'
$expectedAudioHostManifestSha = '1bf2d0fba87c5edfbbc43c150da2c471febd2a85704e741007f1ba16c0a0b98e'
$expectedCapture = '{0.0.1.00000000}.{afe1b68b-8f76-4815-aa2e-1e544322465d}'
$expectedRender = '{0.0.0.00000000}.{ba4ca4cc-35d7-437a-b53b-99ffedc7e5b8}'
$manifestPath = Join-Path $ScannerRunDirectory 'run-manifest.json'
$resultPath = Join-Path $ScannerRunDirectory 'v4-qualification-not_qualified-cb8277d3-f7ca-4a02-9691-0b3349cee251.json'
$summaryPath = Join-Path $AudioHostRunDirectory 'measurements\load-qualification-summary.json'
$controlledPath = Join-Path $AudioHostRunDirectory 'controlled-run-result.json'
$qualificationPath = Join-Path $AudioHostRunDirectory 'qualification-request.json'
$runnerProject = Join-Path $RepositoryRoot 'src\tools\KlidanAudioProbe.HistoricalProfileImport\KlidanAudioProbe.HistoricalProfileImport.csproj'
foreach ($path in @($RepositoryRoot, $ScannerRunDirectory, $manifestPath, $resultPath, $EvidenceIndexPath, $MinimumAuditPath, $AudioHostEvidenceManifestPath, $AudioHostRunDirectory, $summaryPath, $controlledPath, $qualificationPath, $runnerProject)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required import input is missing: $path" }
}
$auditHash = (Get-FileHash -LiteralPath $MinimumAuditPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($auditHash -ne $expectedAuditSha) { throw "Minimum audit SHA mismatch: $auditHash" }
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifestHash -ne $expectedManifestSha) { throw "Scanner manifest SHA mismatch: $manifestHash" }
$audioManifestHash = (Get-FileHash -LiteralPath $AudioHostEvidenceManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($audioManifestHash -ne $expectedAudioHostManifestSha) { throw "AudioHost evidence manifest SHA mismatch: $audioManifestHash" }
$indexHash = (Get-FileHash -LiteralPath $EvidenceIndexPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($indexHash -ne $expectedIndexSha) { throw "Historical evidence index SHA mismatch: $indexHash" }
$scannerManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
foreach ($entry in $scannerManifest.files) {
    $file = [IO.Path]::GetFullPath((Join-Path $ScannerRunDirectory $entry.path))
    if (-not $file.StartsWith([IO.Path]::GetFullPath($ScannerRunDirectory).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Scanner manifest path escapes its run directory: $($entry.path)" }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Scanner evidence SHA mismatch: $($entry.path)" }
}
$audit = Get-Content -Raw -LiteralPath $MinimumAuditPath | ConvertFrom-Json
$scanner = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
$audio = Get-Content -Raw -LiteralPath $summaryPath | ConvertFrom-Json
$controlled = Get-Content -Raw -LiteralPath $controlledPath | ConvertFrom-Json
$qualification = Get-Content -Raw -LiteralPath $qualificationPath | ConvertFrom-Json
$audioManifest = Get-Content -Raw -LiteralPath $AudioHostEvidenceManifestPath | ConvertFrom-Json
if ($audioManifest.files.Count -eq 0) { throw 'AudioHost evidence manifest contains no source files.' }
foreach ($entry in $audioManifest.files) {
    $file = Join-Path $AudioHostRunDirectory $entry.path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "AudioHost manifest file is missing: $($entry.path)" }
    if ((Get-Item -LiteralPath $file).Length -ne $entry.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "AudioHost manifest SHA/length mismatch: $($entry.path)" }
}
if ($audit.runId -ne 'bc6638e5-6e3e-4f31-9a5a-c2084049d37f' -or $audit.captureEndpointId -ne $expectedCapture -or $audit.renderEndpointId -ne $expectedRender -or -not $audit.minimumWithinConsentedDomainProven -or $audit.globalMinimumProven) { throw 'Historical minimum audit identity/scope check failed.' }
if ($scanner.runId -ne $audit.runId -or $scanner.buildSha -ne $audit.sourceCommit -or $scanner.candidates.Count -ne 8 -or $scanner.sessionCleanup -ne 'CLOSE_CONFIRMED' -or $scanner.globalMinimumProven) { throw 'Scanner source identity, candidate coverage, or cleanup check failed.' }
$selected = $scanner.candidates[7]
$short = $selected.duplexEvidence.report
if ($selected.candidate.index -ne 7 -or $selected.actualCaptureFrames -ne 480 -or $selected.actualRenderFrames -ne 480 -or $short.requested.sampleRateHz -ne 48000 -or $short.requested.encoding -ne 'PCM16' -or $short.route.mode -ne 'MONO_TO_STEREO' -or $short.capture.actualBufferFrames -ne 480 -or $short.capture.actualBufferPeriod100ns -ne 100000 -or $short.render.actualBufferFrames -ne 480 -or $short.render.actualBufferPeriod100ns -ne 100000) { throw 'Scanner 10 ms SHORT format, route, or geometry check failed.' }
$repeatIds = @($selected.repeatabilityEvidence | ForEach-Object { $_.operationId })
if ($selected.repeatabilityEvidence.Count -ne 5 -or $repeatIds.Count -ne 5 -or @($repeatIds | Select-Object -Unique).Count -ne 5 -or @($selected.repeatabilityEvidence | Where-Object { $_.result -ne 'CLEAN' -or $_.actualCaptureFrames -ne 480 -or $_.actualRenderFrames -ne 480 -or $_.actualCapturePeriod100ns -ne 100000 -or $_.actualRenderPeriod100ns -ne 100000 -or $_.cleanup -ne 'CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED' }).Count -ne 0) { throw 'R5 does not prove five unique CLEAN 10 ms repeats with preserved cleanup statuses.' }
$transport = $audio.transport
if ($audio.qualificationRunId -ne '2b07ff5e-579d-451e-aa9b-539439aca44f' -or $audio.result -ne 'LOAD_CLEAN' -or $audio.sourceEvidence.originalRunId -ne $audit.runId -or $audio.endpoints.captureId -ne $expectedCapture -or $audio.endpoints.renderId -ne $expectedRender -or $transport.sampleRateHz -ne 48000 -or $transport.encoding -ne 'PCM16' -or $transport.voiceRouting -ne 'MONO_TO_STEREO' -or $transport.captureFrames -ne 480 -or $transport.renderFrames -ne 480 -or $transport.period100ns -ne 100000 -or $transport.requestedCandidateIndex -ne 7 -or -not $audio.cleanup.confirmed -or $audio.cleanup.forcedTermination) { throw 'AudioHost summary provenance, endpoint, format, geometry, or cleanup check failed.' }
if ($qualification.qualificationRunId -ne $audio.qualificationRunId -or $qualification.endpoints.captureId -ne $expectedCapture -or $qualification.endpoints.renderId -ne $expectedRender -or $qualification.transport.period100ns -ne 100000) { throw 'AudioHost qualification request does not bind the exact candidate and endpoint pair.' }
if ($audioManifest.qualificationRunId -ne $audio.qualificationRunId -or $audioManifest.runnerRunId -ne $controlled.runId -or $audioManifest.originalScannerRunId -ne $audit.runId -or $audioManifest.captureFrames -ne 480 -or $audioManifest.renderFrames -ne 480 -or $audioManifest.period100ns -ne 100000 -or $audioManifest.result -ne 'LOAD_CLEAN' -or $audioManifest.watchdogOutcome -ne 'RUNNER_SUCCEEDED' -or $audioManifest.watchdogExitCode -ne 0 -or -not $audioManifest.nativeCleanupConfirmed -or $audioManifest.forcedTermination) { throw 'AudioHost physical evidence manifest does not match the qualification.' }
if ($controlled.runId -ne '55271755e4d24fd4b411bfd7e346d0c8' -or $controlled.result -ne 'LOAD_CLEAN' -or $controlled.watchdogOutcome -ne 'RUNNER_SUCCEEDED' -or $controlled.watchdogExitCode -ne 0 -or $controlled.testOnlyFixture -ne $null) { throw 'AudioHost watchdog/result check failed.' }
Write-Host 'Verified historical binding: Bloody G501 · 48 kHz PCM16 · MONO_TO_STEREO · 10 ms · C480/R480 · R5 5/5 CLEAN.'
Write-Host 'Scope: minimum within consented 3–10 ms domain; global minimum remains false.'
Write-Host "Profile destination: $ProfileDirectory"
if (-not $ConfirmOperatorProfileWrite) {
    Write-Host 'Preflight only. No profile was written. Repeat with -ConfirmOperatorProfileWrite after operator authorization.'
    return
}
$confirmation = Read-Host 'Type IMPORT DEVICE PROFILE V5 to create the local profile'
if ($confirmation -cne 'IMPORT DEVICE PROFILE V5') { throw 'Operator confirmation did not match; no profile was written.' }
$args = @(
    'run', '--project', $runnerProject, '-c', 'Release', '--',
    '--scanner-run', $ScannerRunDirectory,
    '--evidence-index', $EvidenceIndexPath,
    '--minimum-audit', $MinimumAuditPath,
    '--minimum-audit-sha256', $expectedAuditSha,
    '--audiohost-manifest', $AudioHostEvidenceManifestPath,
    '--audiohost-manifest-sha256', $expectedAudioHostManifestSha,
    '--audiohost-run', $AudioHostRunDirectory,
    '--output-directory', $ProfileDirectory,
    '--confirm-operator-profile-write', 'I_CONFIRM_DEVICE_PROFILE_V5'
)
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Historical import failed with exit code $LASTEXITCODE." }
