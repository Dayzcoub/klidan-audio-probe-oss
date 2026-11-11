#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\build\physical-qa-publish'),
    [string]$FakeWorkerPath = (Join-Path $PSScriptRoot '..\build\worker-tests\Release\probe_worker_format_fixture.exe')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = (Resolve-Path $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite QA package: $output" }
foreach ($required in @('KlidanAudioProbe.Gui.App.exe', 'KlidanAudioProbe.V4.Headless.exe', 'KlidanProbeWorker.exe', 'KlidanAudioProbe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) { throw "Release publish output is missing $required" }
}
if (-not (Test-Path -LiteralPath $FakeWorkerPath -PathType Leaf)) { throw "Test-only Worker fixture is missing: $FakeWorkerPath" }
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read source revision.' }
New-Item -ItemType Directory -Path $output | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $output -Recurse
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

- `Run-V4HeadlessSearch.ps1 -Mode DryRun -Scenario BLOODY` uses only the separately marked Fake Worker; it exercises the production V4 plan/workflow/evaluator and never creates a DEVICE profile.
- `-Mode ShortSearch -AuthorizeShortSearch` is the explicit operator consent for the bounded physical short-search scope. It selects the two previously confirmed Bloody endpoint identities and performs current Windows discovery and passive 48 kHz PCM16 queries before Worker session registration.
- Add `-AuthorizeR5` only after separate operator approval. This is a distinct authorization flag and fingerprints R=5 into the authorized budget. Without it the plan has R=0 and no repeatability operation can run. The launcher and runner require no interactive terminal input.
- The search is sequential, limited to eight bounds-derived candidates and stops on UNKNOWN. FAILED advances only after confirmed cleanup. Capture and Render evidence, evaluator results, the candidate matrix and read-back verified per-candidate JSON are saved under `evidence`.
- No stable profile is created. AudioHost load qualification remains required. Do not start physical mode without separate operator authorization.

Run Release, not a Debug build. Package manifest verification is mandatory. The GUI launcher remains available for the separately scoped point diagnostic; it does not perform headless search.
"@ | Set-Content -LiteralPath (Join-Path $output 'README-Physical-QA.md') -Encoding UTF8
$files = Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
}
[pscustomobject]@{ schemaVersion = 1; sourceCommit = $head; configuration = 'Release'; runtime = 'win-x64-self-contained'; files = @($files) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
Write-Output "QA package created: $output"
