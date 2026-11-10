$ErrorActionPreference = 'Stop'
$launcher = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts\Run-AudioProbePhysicalQa.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('audio-probe-qa-launcher-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repos\audio-probe-oss'; $trees = Join-Path $root 'worktrees'; $registryPath = Join-Path $root 'workspace-projects.json'
function Assert([bool]$condition,[string]$message) { if (-not $condition) { throw "FAIL: $message" } }
function Invoke-Select([string]$branch,[string]$sha,[string]$install='') { if ($install) { & $launcher -ValidateOnly -SourceBranch $branch -ExpectedSha $sha -InstallPath $install -WorkspaceRegistryPath $registryPath } else { & $launcher -ValidateOnly -SourceBranch $branch -ExpectedSha $sha -WorkspaceRegistryPath $registryPath } }
function Add-Build([string]$dir,[string]$branch,[string]$sha) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $bins=@{}; foreach ($spec in @(@('gui','KlidanAudioProbe.Gui.App.exe'),@('nativeWorker','KlidanProbeWorker.exe'),@('nativeCli','KlidanAudioProbe.exe'))) { $p=Join-Path $dir $spec[1]; [IO.File]::WriteAllBytes($p,[byte[]]::new(8192)); $bins[$spec[0]]=@{path=$p;sha256=(Get-FileHash $p -Algorithm SHA256).Hash} }
    @{schemaVersion=2;sourceBranch=$branch;sourceCommit=$sha;binaries=$bins}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $dir 'audio-probe-qa-build.json') -Encoding utf8
    $bins
}
try {
    New-Item -ItemType Directory -Path $repo,$trees -Force|Out-Null
    & git init -b feat/audio-probe-as-n3c-gui-integration $repo|Out-Null; & git -C $repo config user.email qa@example.invalid; & git -C $repo config user.name 'QA Test'
    Set-Content (Join-Path $repo 'source.txt') 'test'; & git -C $repo add source.txt; & git -C $repo commit -m base|Out-Null; $shaN3c=(& git -C $repo rev-parse HEAD).Trim()
    $treeN4b=Join-Path $trees 'n4b'; & git -C $repo worktree add -b feat/audio-probe-as-n4b-bounded-physical-qa $treeN4b|Out-Null; $shaN4b=(& git -C $treeN4b rev-parse HEAD).Trim()
    @{schemaVersion=1;projects=@(@{projectId='AUDIO-PROBE-OSS';root=$root});repositories=@(@{repoId='audio-probe-oss';currentPath=$repo})}|ConvertTo-Json -Depth 5|Set-Content $registryPath -Encoding utf8
    $n3c=Add-Build (Join-Path $root 'builds\n3c') 'feat/audio-probe-as-n3c-gui-integration' $shaN3c
    $n4b=Add-Build (Join-Path $root 'builds\n4b') 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b
    $selectedN3c=Invoke-Select 'feat/audio-probe-as-n3c-gui-integration' $shaN3c
    $selectedN4b=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b
    Assert (($selectedN3c|ConvertFrom-Json).SourceCommit -eq $shaN3c) 'selects registered AS-N3C exact SHA'
    Assert (($selectedN4b|ConvertFrom-Json).SourceCommit -eq $shaN4b) 'selects registered AS-N4B exact SHA'
    $badSha=$false; try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' ('0'*40)}catch{$badSha=$_.Exception.Message -match 'Expected exactly one registered worktree'}; Assert $badSha 'rejects wrong/unregistered source SHA'
    $missingManifest=$false; Remove-Item (Join-Path $root 'builds\n4b\audio-probe-qa-build.json'); try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b}catch{$missingManifest=$_.Exception.Message -match 'No verified build'}; Assert $missingManifest 'rejects missing matching manifest'
    Add-Build (Join-Path $root 'builds\n4b') 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b|Out-Null
    $badInstall=$false; try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b (Join-Path $root 'unverified.exe')}catch{$badInstall=$_.Exception.Message -match 'must exactly match'}; Assert $badInstall 'InstallPath cannot bypass verified build identity'
    $badBytes=[byte[]]::new(8192); $badBytes[0]=1; $badBinary=$false; [IO.File]::WriteAllBytes($n4b.nativeWorker.path,$badBytes); try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b}catch{$badBinary=$_.Exception.Message -match 'SHA-256 mismatch'}; Assert $badBinary 'rejects damaged binary via stored hash'
    Write-Host 'PASS: AS-N3C/AS-N4B source selection, SHA, manifest, binary integrity and InstallPath gate'
} finally { if(Test-Path $root){Remove-Item $root -Recurse -Force} }
