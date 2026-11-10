$ErrorActionPreference = 'Stop'
$launcher = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts\Run-AudioProbePhysicalQa.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('audio-probe-qa-launcher-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $testRoot 'repos\audio-probe-oss'
$buildA = Join-Path $testRoot 'builds\candidate-a'
$buildB = Join-Path $testRoot 'builds\candidate-b'
$registryPath = Join-Path $testRoot 'workspace-projects.json'

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "FAIL: $Message" } }
function Invoke-Validate([string]$Path = '') {
    if ($Path) { $json = & $launcher -ValidateOnly -InstallPath $Path -WorkspaceRegistryPath $registryPath }
    else { $json = & $launcher -ValidateOnly -WorkspaceRegistryPath $registryPath }
    $json | ConvertFrom-Json
}
function Write-FakeBuild([string]$Directory, [string]$Commit) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $paths = @{
        gui = Join-Path $Directory 'KlidanAudioProbe.Gui.App.exe'
        nativeWorker = Join-Path $Directory 'KlidanProbeWorker.exe'
        nativeCli = Join-Path $Directory 'KlidanAudioProbe.exe'
    }
    foreach ($item in $paths.GetEnumerator()) {
        $bytes = [byte[]]::new(8192)
        for ($index = 0; $index -lt $bytes.Length; $index++) { $bytes[$index] = [byte]$item.Key.Length }
        [IO.File]::WriteAllBytes($item.Value, $bytes)
    }
    $bins = @{}
    foreach ($key in $paths.Keys) { $bins[$key] = @{ path = $paths[$key]; sha256 = (Get-FileHash -LiteralPath $paths[$key] -Algorithm SHA256).Hash } }
    $manifest = @{ schemaVersion = 1; sourceBranch = 'feat/audio-probe-as-n3c-gui-integration'; sourceCommit = $Commit; binaries = $bins }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'audio-probe-qa-build.json') -Encoding utf8
    $paths
}

try {
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    & git init -b feat/audio-probe-as-n3c-gui-integration $repo | Out-Null
    & git -C $repo config user.email qa-test@example.invalid
    & git -C $repo config user.name 'Launcher Test'
    Set-Content -LiteralPath (Join-Path $repo 'fixture.txt') -Value 'test source'
    & git -C $repo add fixture.txt
    & git -C $repo commit -m 'test fixture' | Out-Null
    $commit = (& git -C $repo rev-parse HEAD).Trim()
    @{ schemaVersion = 1; projects = @(@{ projectId = 'AUDIO-PROBE-OSS'; root = $testRoot }); repositories = @(@{ repoId = 'audio-probe-oss'; currentPath = $repo }) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $registryPath -Encoding utf8

    $pathsA = Write-FakeBuild $buildA $commit
    $selected = Invoke-Validate
    Assert ($selected.sourceCommit -eq $commit -and $selected.GuiPath -eq $pathsA.gui) 'automatically selects the build manifest tied to the registered branch and current source commit'
    $explicit = Invoke-Validate $pathsA.gui
    Assert ($explicit.GuiPath -eq $pathsA.gui -and -not $explicit.SourceCommit) 'explicit InstallPath is used directly and does not substitute installed/build paths'

    $pathsB = Write-FakeBuild $buildB $commit
    $ambiguous = $false
    try { $null = Invoke-Validate } catch { $ambiguous = $_.Exception.Message -match 'Ambiguous AS-N3C builds' }
    Assert $ambiguous 'multiple matching build manifests fail with an ambiguity error'

    Remove-Item -LiteralPath $pathsB.nativeWorker
    $missingWorker = $false
    try { $null = Invoke-Validate $pathsB.gui } catch { $missingWorker = $_.Exception.Message -match 'NativeWorker|KlidanProbeWorker|executable is missing' }
    Assert $missingWorker 'missing Native Worker blocks explicit executable launch validation'
    Write-Host 'PASS: launcher build selection, explicit path, ambiguity and missing Worker checks'
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
