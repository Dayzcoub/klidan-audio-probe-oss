param(
    [string]$InstallPath,
    [string]$WorkspaceRegistryPath = 'H:\Projects\workspace-projects.json',
    [switch]$RegisterBuild,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$expectedBranch = 'feat/audio-probe-as-n3c-gui-integration'
$manifestName = 'audio-probe-qa-build.json'

function Get-RelativePath([string]$BasePath, [string]$Path) {
    $baseUri = [Uri](([IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'))
    $pathUri = [Uri]([IO.Path]::GetFullPath($Path))
    [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Get-WorkspaceContext {
    if (-not (Test-Path -LiteralPath $WorkspaceRegistryPath -PathType Leaf)) { throw "Workspace registry missing: $WorkspaceRegistryPath" }
    $registry = Get-Content -LiteralPath $WorkspaceRegistryPath -Raw | ConvertFrom-Json
    $project = @($registry.projects | Where-Object projectId -eq 'AUDIO-PROBE-OSS')
    $repository = @($registry.repositories | Where-Object repoId -eq 'audio-probe-oss')
    if ($project.Count -ne 1 -or $repository.Count -ne 1) { throw 'A unique AUDIO-PROBE-OSS project and audio-probe-oss repository registration is required.' }
    if (-not (Test-Path -LiteralPath $repository[0].currentPath -PathType Container)) { throw 'Registered Audio Probe repository path does not exist.' }
    [pscustomobject]@{ Root = [IO.Path]::GetFullPath($project[0].root); Repository = [IO.Path]::GetFullPath($repository[0].currentPath) }
}

function Get-CurrentSource {
    $context = Get-WorkspaceContext
    $worktrees = @(& git -C $context.Repository worktree list --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not list registered Git worktrees.' }
    $entries = [Collections.Generic.List[object]]::new()
    $entry = @{}
    foreach ($line in $worktrees) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($entry.worktree) { $entries.Add([pscustomobject]$entry) }
            $entry = @{}
        } elseif ($line -match '^([^ ]+) (.*)$') { $entry[$matches[1]] = $matches[2] }
    }
    if ($entry.worktree) { $entries.Add([pscustomobject]$entry) }
    $branchWorktrees = @($entries | Where-Object { $_.branch -eq "refs/heads/$expectedBranch" })
    if ($branchWorktrees.Count -ne 1) { throw "Expected one registered worktree on $expectedBranch; found $($branchWorktrees.Count)." }
    [pscustomobject]@{ Context = $context; Path = [IO.Path]::GetFullPath($branchWorktrees[0].worktree); Commit = $branchWorktrees[0].HEAD; Branch = $expectedBranch }
}

function Get-BinaryIdentity([string]$GuiPath, [string]$WorkerPath, [string]$CliPath, [string]$SourceCommit) {
    foreach ($path in @($GuiPath, $WorkerPath, $CliPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required executable is missing: $path" }
        if ((Get-Item -LiteralPath $path).Length -lt 4096) { throw "Executable is implausibly small: $path" }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($GuiPath)
    [pscustomobject]@{
        gui = [pscustomobject]@{ path = [IO.Path]::GetFullPath($GuiPath); sha256 = (Get-FileHash -LiteralPath $GuiPath -Algorithm SHA256).Hash; fileVersion = $version.FileVersion; productVersion = $version.ProductVersion }
        nativeWorker = [pscustomobject]@{ path = [IO.Path]::GetFullPath($WorkerPath); sha256 = (Get-FileHash -LiteralPath $WorkerPath -Algorithm SHA256).Hash }
        nativeCli = [pscustomobject]@{ path = [IO.Path]::GetFullPath($CliPath); sha256 = (Get-FileHash -LiteralPath $CliPath -Algorithm SHA256).Hash }
        sourceCommit = $SourceCommit
    }
}

function Register-CurrentBuild {
    $source = Get-CurrentSource
    $gui = Join-Path $source.Context.Root 'builds\as-n3c-ui-integration\artifacts\bin\KlidanAudioProbe.Gui.App\release\KlidanAudioProbe.Gui.App.exe'
    $directory = Split-Path -Parent $gui
    $worker = Join-Path $directory 'KlidanProbeWorker.exe'
    $cli = Join-Path $directory 'KlidanAudioProbe.exe'
    $identity = Get-BinaryIdentity $gui $worker $cli $source.Commit
    $manifest = [pscustomobject]@{ schemaVersion = 1; sourceBranch = $source.Branch; sourceCommit = $source.Commit; registeredUtc = [DateTime]::UtcNow.ToString('O'); binaries = $identity }
    $manifestPath = Join-Path $directory $manifestName
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Output $manifestPath
}

function Select-VerifiedBuild {
    $source = Get-CurrentSource
    $buildRoot = Join-Path $source.Context.Root 'builds'
    $candidates = @()
    foreach ($file in Get-ChildItem -LiteralPath $buildRoot -Filter $manifestName -File -Recurse -Depth 10 -ErrorAction SilentlyContinue) {
        try { $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json } catch { continue }
        if ($manifest.sourceBranch -ne $source.Branch -or $manifest.sourceCommit -ne $source.Commit) { continue }
        $bins = $manifest.binaries
        if ($manifest.schemaVersion -ne 1 -or -not $bins.gui.path -or -not $bins.nativeWorker.path -or -not $bins.nativeCli.path) { throw "Matching AS-N3C build manifest is incomplete: $($file.FullName)" }
        foreach ($binary in @($bins.gui, $bins.nativeWorker, $bins.nativeCli)) {
            if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($binary.path)) -ne [IO.Path]::GetDirectoryName($file.FullName)) { throw "Build manifest binary must be beside the manifest: $($file.FullName)" }
            if (-not (Test-Path -LiteralPath $binary.path -PathType Leaf) -or (Get-FileHash -LiteralPath $binary.path -Algorithm SHA256).Hash -ne $binary.sha256) { throw "Build binary missing or SHA-256 mismatch: $($binary.path)" }
        }
        $candidates += [pscustomobject]@{ ManifestPath = $file.FullName; SourceCommit = $source.Commit; Binaries = $bins }
    }
    if ($candidates.Count -eq 0) { throw "No verified AS-N3C build for source commit $($source.Commit). Build Release outputs, then run this script with -RegisterBuild." }
    if ($candidates.Count -gt 1) { throw "Ambiguous AS-N3C builds for $($source.Commit): $($candidates.ManifestPath -join '; ')" }
    $candidate = $candidates[0]
    [pscustomobject]@{ GuiPath = $candidate.Binaries.gui.path; WorkerPath = $candidate.Binaries.nativeWorker.path; CliPath = $candidate.Binaries.nativeCli.path; SourceCommit = $candidate.SourceCommit; ManifestPath = $candidate.ManifestPath; Identity = $candidate.Binaries }
}

if ($RegisterBuild -and ($ValidateOnly -or -not [string]::IsNullOrWhiteSpace($InstallPath))) { throw '-RegisterBuild cannot be combined with -ValidateOnly or -InstallPath.' }
if ($RegisterBuild) { return Register-CurrentBuild }

if (-not [string]::IsNullOrWhiteSpace($InstallPath)) {
    $guiPath = [IO.Path]::GetFullPath($InstallPath)
    if (-not (Test-Path -LiteralPath $guiPath -PathType Leaf)) { throw "Explicit -InstallPath does not exist: $guiPath" }
    $directory = Split-Path -Parent $guiPath
    $selected = [pscustomobject]@{ GuiPath = $guiPath; WorkerPath = (Join-Path $directory 'KlidanProbeWorker.exe'); CliPath = (Join-Path $directory 'KlidanAudioProbe.exe'); SourceCommit = $null; ManifestPath = $null }
    $selected | Add-Member -NotePropertyName Identity -NotePropertyValue (Get-BinaryIdentity $selected.GuiPath $selected.WorkerPath $selected.CliPath $null)
} else {
    $selected = Select-VerifiedBuild
}

if ($ValidateOnly) { return ($selected | ConvertTo-Json -Depth 6 -Compress) }

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$projectRoot = (Get-WorkspaceContext).Root
$runRoot = Join-Path $projectRoot "logs\physical-qa\$runId"
$reportRoot = Join-Path $runRoot 'reports'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$metadataPath = Join-Path $runRoot 'run.json'
$evidenceRoot = Join-Path $env:LOCALAPPDATA 'KlidanAudioProbe\Scans\V2'
$before = @{}
if (Test-Path -LiteralPath $evidenceRoot -PathType Container) {
    Get-ChildItem -LiteralPath $evidenceRoot -File -Recurse | ForEach-Object { $before[(Get-RelativePath $evidenceRoot $_.FullName)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$process = Start-Process -FilePath $selected.GuiPath -PassThru
@{ startedUtc = [DateTime]::UtcNow.ToString('O'); processId = $process.Id; buildManifest = $selected.ManifestPath; sourceCommit = $selected.SourceCommit; binaries = $selected.Identity; evidenceSource = $evidenceRoot; reportDestination = $reportRoot } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding utf8
Write-Host "Audio Probe launched (PID $($process.Id)). Complete the approved physical QA, then close the app to collect new or changed V2 evidence."
Wait-Process -Id $process.Id

$collected = @()
if (Test-Path -LiteralPath $evidenceRoot -PathType Container) {
    Get-ChildItem -LiteralPath $evidenceRoot -File -Recurse | ForEach-Object {
        $relative = Get-RelativePath $evidenceRoot $_.FullName
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        if (-not $before.ContainsKey($relative) -or $before[$relative] -ne $hash) {
            $destination = Join-Path $reportRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
            $collected += [pscustomobject]@{ relativePath = $relative; sha256 = $hash }
        }
    }
}
$record = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$record | Add-Member -NotePropertyName completedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O'))
$record | Add-Member -NotePropertyName collectedReports -NotePropertyValue $collected
$record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $metadataPath -Encoding utf8
Write-Host "QA session records: $runRoot"
