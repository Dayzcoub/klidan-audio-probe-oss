#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\build\physical-qa-publish'),
    [string]$HeadlessPublishDirectory = (Join-Path $PSScriptRoot '..\build\v4-headless-publish'),
    [string]$FakeWorkerPath = (Join-Path $PSScriptRoot '..\build\worker-tests\Release\probe_worker_format_fixture.exe')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = (Resolve-Path $PublishDirectory).Path
$headlessPublish = (Resolve-Path $HeadlessPublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite QA package: $output" }
foreach ($required in @('KlidanAudioProbe.Gui.App.exe', 'KlidanProbeWorker.exe', 'KlidanAudioProbe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) { throw "Release publish output is missing $required" }
}
foreach ($required in @('KlidanAudioProbe.V4.Headless.exe', 'KlidanAudioProbe.V4.Headless.dll', 'KlidanAudioProbe.V4.Headless.deps.json', 'KlidanAudioProbe.V4.Headless.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $headlessPublish $required) -PathType Leaf)) { throw "Headless Release publish output is missing $required" }
}
if (-not (Test-Path -LiteralPath $FakeWorkerPath -PathType Leaf)) { throw "Test-only Worker fixture is missing: $FakeWorkerPath" }
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read source revision.' }
$sourceStatus = @(& git -C $root status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $sourceStatus.Count -ne 0) { throw 'Refusing to package a build from a dirty or unverified source tree.' }
$identityPath = Join-Path ([IO.Path]::GetTempPath()) ("klidan-build-identity-{0}.json" -f [Guid]::NewGuid().ToString('N'))
try {
    $identityProcess = Start-Process -FilePath (Join-Path $publish 'KlidanAudioProbe.Gui.App.exe') -ArgumentList @('--write-build-identity', ('"{0}"' -f $identityPath)) -PassThru -Wait -WindowStyle Hidden
    if ($identityProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $identityPath -PathType Leaf)) { throw "Published GUI identity command failed (exit $($identityProcess.ExitCode))." }
    $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    $guiSha = (Get-FileHash -LiteralPath (Join-Path $publish 'KlidanAudioProbe.Gui.App.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($identity.sourceCommit -ne $head -or $identity.sourceTreeClean -ne $true -or $identity.executableSha256 -ne $guiSha) { throw 'Published GUI source identity does not match current clean HEAD and executable SHA; refusing stale binaries.' }
} finally { Remove-Item -LiteralPath $identityPath -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $output | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $output -Recurse
New-Item -ItemType Directory -Path (Join-Path $output 'headless') | Out-Null
Get-ChildItem -LiteralPath $headlessPublish -Force | Copy-Item -Destination (Join-Path $output 'headless') -Recurse
New-Item -ItemType Directory -Path (Join-Path $output 'logs'), (Join-Path $output 'evidence'), (Join-Path $output 'profiles') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-KlidanAudioProbePhysicalQa.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-V4HeadlessSearch.ps1') -Destination $output
Copy-Item -LiteralPath $FakeWorkerPath -Destination (Join-Path $output 'KlidanProbeWorker.FakeTest.exe')
@"
# KLIDAN Audio Probe physical QA package

Source commit: $head
Configuration: Release, self-contained win-x64
Native host dependency check: KlidanAudioProbe.exe and KlidanProbeWorker.exe use Windows system DLLs only (ole32, SETUPAPI where needed, AVRT, bcrypt, KERNEL32); no app-local VC++ runtime was required.

## Headless V4 workflow

- `Run-V4HeadlessSearch.ps1 -Mode DryRun -Scenario BLOODY` uses only the separately marked Fake Worker; the self-contained runner and dependencies are isolated in `headless` and it never creates a DEVICE profile.
- `-Mode ShortSearch -AuthorizeShortSearch -CaptureId <discovered-id> -RenderId <discovered-id>` is the explicit operator consent for the bounded physical short-search scope. It uses the explicitly selected endpoint identities and performs current Windows discovery and passive format/period queries before Worker session registration.
- Add `-AuthorizeR5` only after separate operator approval. This is a distinct authorization flag and fingerprints R=5 into the authorized budget. Without it the plan has R=0 and no repeatability operation can run. The launcher and runner require no interactive terminal input.
- `-Mode RepeatSelected -AuthorizeR5 -SourceEvidence <verified headless-summary.json>` verifies all source candidate and matrix SHA-256 values, real provenance, the selected CLEAN C480/R480 candidate, route, endpoints, and closed-session cleanup before fresh device discovery. It then builds a new one-candidate plan and requires a fresh R=5 confirmation. It never reuses the prior token or consent and performs only two Initialize operations plus up to five sequential 5-second Duplex operations (max seven operations / 25 active Duplex seconds), stopping on FAILED or UNKNOWN. Each repeat and the combined result are written as new evidence. No profile is created; KLIDAN AudioHost load qualification remains required.
- `MINIMUM_SHORT_CLEAN_FOUND` means the first CLEAN in the scanned candidate ordering; with candidate 7 it is the minimum SHORT_CLEAN only in the discovered/tested 3–10 ms range. `GlobalMinimumProven=false` means no claim is made below or outside that range. A CLEAN on the final candidate stops with `CANDIDATE_DOMAIN_EXHAUSTED`; `SMALLER_SHORT_CLEAN_FOUND` applies only when a larger candidate was actually skipped.
- Top-level/headless candidate files contain the verified package BuildSha. V4 matrix `BuildSha=UNKNOWN` in prior immutable evidence reflects that the workflow had not yet received the package SHA when it wrote its nested matrix; newer workflow runs record `KLIDAN_BUILD_SHA` there too.
- The search is sequential, limited to eight bounds-derived candidates and stops on UNKNOWN. FAILED advances only after confirmed cleanup. Capture and Render evidence, evaluator results, the candidate matrix and read-back verified per-candidate JSON are saved under `evidence`.
- No stable profile is created. AudioHost load qualification remains required. Do not start physical mode without separate operator authorization.

Run Release, not a Debug build. Package manifest verification is mandatory. The GUI launcher remains available for the separately scoped point diagnostic; it does not perform headless search.
"@ | Set-Content -LiteralPath (Join-Path $output 'README-Physical-QA.md') -Encoding UTF8
$files = Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
}
[pscustomobject]@{ schemaVersion = 1; sourceCommit = $identity.sourceCommit; configuration = 'Release'; runtime = 'win-x64-self-contained'; files = @($files) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
Write-Output "QA package created: $output"
