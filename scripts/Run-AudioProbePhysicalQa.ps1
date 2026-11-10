param(
    [string]$SourceBranch = 'feat/audio-probe-as-n4b-bounded-physical-qa',
    [string]$ExpectedSha,
    [string]$BuildDirectory,
    [string]$NativeCliPath,
    [string]$InstallPath,
    [string]$WorkspaceRegistryPath = 'H:\Projects\workspace-projects.json',
    [switch]$RegisterBuild,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$manifestName = 'audio-probe-qa-build.json'
$supportedBranches = @('feat/audio-probe-as-n3c-gui-integration', 'feat/audio-probe-as-n4b-bounded-physical-qa', 'fix/as-n4b1-native-duplex-v2-cli', 'fix/as-n4b2-auto-short')

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
    if ($SourceBranch -notin $supportedBranches) { throw "Unsupported source branch: $SourceBranch" }
    if ($ExpectedSha -notmatch '^[0-9a-fA-F]{40}$') { throw '-ExpectedSha must be an exact 40-character commit SHA.' }
    $context = Get-WorkspaceContext
    $worktrees = @(& git -C $context.Repository worktree list --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not list registered Git worktrees.' }
    $entries = [Collections.Generic.List[object]]::new(); $entry = @{}
    foreach ($line in $worktrees) {
        if ([string]::IsNullOrWhiteSpace($line)) { if ($entry.worktree) { $entries.Add([pscustomobject]$entry) }; $entry = @{} }
        elseif ($line -match '^([^ ]+) (.*)$') { $entry[$matches[1]] = $matches[2] }
    }
    if ($entry.worktree) { $entries.Add([pscustomobject]$entry) }
    $matches = @($entries | Where-Object { $_.branch -eq "refs/heads/$SourceBranch" -and $_.HEAD -eq $ExpectedSha })
    if ($matches.Count -ne 1) { throw "Expected exactly one registered worktree at $SourceBranch@$ExpectedSha; found $($matches.Count)." }
    $status = @(& git -C $matches[0].worktree status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Source worktree must be clean before QA build registration or launch.' }
    [pscustomobject]@{ Context = $context; Path = [IO.Path]::GetFullPath($matches[0].worktree); Commit = $matches[0].HEAD; Branch = $SourceBranch }
}

function Get-BinaryIdentity([string]$GuiPath, [string]$WorkerPath, [string]$CliPath, [string]$SourceCommit) {
    foreach ($path in @($GuiPath, $WorkerPath, $CliPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required executable is missing: $path" }
        if ((Get-Item -LiteralPath $path).Length -lt 4096) { throw "Executable is implausibly small: $path" }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($GuiPath)
    $binaryCommit = [regex]::Match([string]$version.ProductVersion, '[0-9a-fA-F]{40}')
    if (-not $SourceCommit -or -not $binaryCommit.Success -or $binaryCommit.Value -ine $SourceCommit) { throw "GUI ProductVersion must contain exact source commit ${SourceCommit}: $GuiPath" }
    [pscustomobject]@{
        gui = [pscustomobject]@{ path = [IO.Path]::GetFullPath($GuiPath); sha256 = (Get-FileHash -LiteralPath $GuiPath -Algorithm SHA256).Hash; fileVersion = $version.FileVersion; productVersion = $version.ProductVersion }
        nativeWorker = [pscustomobject]@{ path = [IO.Path]::GetFullPath($WorkerPath); sha256 = (Get-FileHash -LiteralPath $WorkerPath -Algorithm SHA256).Hash }
        nativeCli = [pscustomobject]@{ path = [IO.Path]::GetFullPath($CliPath); sha256 = (Get-FileHash -LiteralPath $CliPath -Algorithm SHA256).Hash }
        sourceCommit = $SourceCommit
    }
}

function Register-CurrentBuild {
    $source = Get-CurrentSource
    if (-not $BuildDirectory -or -not $NativeCliPath) { throw '-RegisterBuild requires the exact GUI -BuildDirectory and NativeCliPath.' }
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $source.Context.Root 'builds'))
    $directory = [IO.Path]::GetFullPath($BuildDirectory)
    $cli = [IO.Path]::GetFullPath($NativeCliPath)
    if (-not $directory.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $cli.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'GUI and Native CLI build outputs must be under the registered project builds directory.' }
    $gui = Join-Path $directory 'KlidanAudioProbe.Gui.App.exe'; $worker = Join-Path $directory 'KlidanProbeWorker.exe'
    if (-not (Test-Path -LiteralPath $gui -PathType Leaf)) { $gui = Join-Path $directory 'KlidanAudioProbe.exe' }
    $identity = Get-BinaryIdentity $gui $worker $cli $source.Commit
    $manifest = [pscustomobject]@{ schemaVersion = 2; sourceBranch = $source.Branch; sourceCommit = $source.Commit; registeredUtc = [DateTime]::UtcNow.ToString('O'); binaries = $identity }
    $manifestPath = Join-Path $directory $manifestName
    if (Test-Path -LiteralPath $manifestPath) { throw "Refusing to overwrite an existing QA build manifest: $manifestPath" }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Output $manifestPath
}

function Select-VerifiedBuild {
    $source = Get-CurrentSource; $buildRoot = Join-Path $source.Context.Root 'builds'; $candidates = @()
    foreach ($file in Get-ChildItem -LiteralPath $buildRoot -Filter $manifestName -File -Recurse -Depth 10 -ErrorAction SilentlyContinue) {
        try { $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json } catch { continue }
        if ($manifest.sourceBranch -ne $source.Branch -or $manifest.sourceCommit -ne $source.Commit) { continue }
        $bins = $manifest.binaries
        if ($manifest.schemaVersion -ne 2 -or -not $bins.gui.path -or -not $bins.nativeWorker.path -or -not $bins.nativeCli.path) { throw "Matching QA build manifest is incomplete: $($file.FullName)" }
        foreach ($binary in @($bins.gui, $bins.nativeWorker, $bins.nativeCli)) {
            if (-not [IO.Path]::GetFullPath($binary.path).StartsWith([IO.Path]::GetFullPath((Join-Path $source.Context.Root 'builds')) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest binary must be inside registered project builds: $($file.FullName)" }
            if (-not (Test-Path -LiteralPath $binary.path -PathType Leaf) -or (Get-FileHash -LiteralPath $binary.path -Algorithm SHA256).Hash -ne $binary.sha256) { throw "Build binary missing or SHA-256 mismatch: $($binary.path)" }
        }
        $null = Get-BinaryIdentity $bins.gui.path $bins.nativeWorker.path $bins.nativeCli.path $source.Commit
        $candidates += [pscustomobject]@{ ManifestPath = $file.FullName; SourceCommit = $source.Commit; Binaries = $bins }
    }
    if ($candidates.Count -eq 0) { throw "No verified build for $($source.Branch)@$($source.Commit). Build Release outputs, then run -RegisterBuild." }
    if ($candidates.Count -gt 1) { throw "Ambiguous builds for $($source.Branch)@$($source.Commit): $($candidates.ManifestPath -join '; ')" }
    $candidate = $candidates[0]
    if ($InstallPath -and [IO.Path]::GetFullPath($InstallPath) -ne [IO.Path]::GetFullPath($candidate.Binaries.gui.path)) { throw '-InstallPath must exactly match the GUI binary in the verified source manifest.' }
    [pscustomobject]@{ GuiPath = $candidate.Binaries.gui.path; WorkerPath = $candidate.Binaries.nativeWorker.path; CliPath = $candidate.Binaries.nativeCli.path; SourceCommit = $candidate.SourceCommit; ManifestPath = $candidate.ManifestPath; Identity = $candidate.Binaries }
}

if ($RegisterBuild -and ($ValidateOnly -or $InstallPath)) { throw '-RegisterBuild cannot be combined with -ValidateOnly or -InstallPath.' }
if ($RegisterBuild) { return Register-CurrentBuild }
$selected = Select-VerifiedBuild
if ($ValidateOnly) { return ($selected | ConvertTo-Json -Depth 6 -Compress) }

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'; $projectRoot = (Get-WorkspaceContext).Root
$runRoot = Join-Path $projectRoot "logs\physical-qa\$runId"; $reportRoot = Join-Path $runRoot 'reports'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$metadataPath = Join-Path $runRoot 'run.json'; $evidenceRoot = Join-Path $env:LOCALAPPDATA 'KlidanAudioProbe\Scans\V2'; $before = @{}
if (Test-Path -LiteralPath $evidenceRoot -PathType Container) { Get-ChildItem -LiteralPath $evidenceRoot -File -Recurse | ForEach-Object { $before[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }
$process = Start-Process -FilePath $selected.GuiPath -PassThru
@{ startedUtc = [DateTime]::UtcNow.ToString('O'); processId = $process.Id; buildManifest = $selected.ManifestPath; sourceBranch = $SourceBranch; sourceCommit = $selected.SourceCommit; binaries = $selected.Identity; evidenceSource = $evidenceRoot; reportDestination = $reportRoot } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding utf8
Write-Host "Audio Probe launched (PID $($process.Id)). No audio operation is started automatically; select a QA mode and approve it in the application. Close the app to collect new V2 evidence."
Wait-Process -Id $process.Id
$collected = @()
if (Test-Path -LiteralPath $evidenceRoot -PathType Container) { Get-ChildItem -LiteralPath $evidenceRoot -File -Recurse | ForEach-Object { $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; if (-not $before.ContainsKey($_.FullName) -or $before[$_.FullName] -ne $hash) { $destination = Join-Path $reportRoot ([IO.Path]::GetFileName($_.FullName)); if (Test-Path -LiteralPath $destination) { throw "Evidence filename collision; refusing overwrite: $destination" }; Copy-Item -LiteralPath $_.FullName -Destination $destination; $collected += [pscustomobject]@{ fileName = [IO.Path]::GetFileName($_.FullName); sha256 = $hash } } } }
$record = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$record | Add-Member -NotePropertyName completedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O')); $record | Add-Member -NotePropertyName collectedReports -NotePropertyValue $collected
$record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $metadataPath -Encoding utf8
Write-Host "QA session records: $runRoot"
