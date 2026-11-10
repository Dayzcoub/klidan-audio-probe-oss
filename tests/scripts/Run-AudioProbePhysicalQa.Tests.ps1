$ErrorActionPreference = 'Stop'
$launcher = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts\Run-AudioProbePhysicalQa.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('audio-probe-qa-launcher-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repos\audio-probe-oss'; $trees = Join-Path $root 'worktrees'; $registryPath = Join-Path $root 'workspace-projects.json'
$guiFixtures = @{}
function Assert([bool]$condition,[string]$message) { if (-not $condition) { throw "FAIL: $message" } }
function Invoke-Select([string]$branch,[string]$sha,[string]$install='') { if ($install) { & $launcher -ValidateOnly -SourceBranch $branch -ExpectedSha $sha -InstallPath $install -WorkspaceRegistryPath $registryPath } else { & $launcher -ValidateOnly -SourceBranch $branch -ExpectedSha $sha -WorkspaceRegistryPath $registryPath } }
function Get-GuiFixture([string]$sha) {
    if ($guiFixtures.ContainsKey($sha)) { return $guiFixtures[$sha] }
    $project = Join-Path $root "gui-$sha"; New-Item -ItemType Directory -Path $project -Force | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><EnableWindowsTargeting>true</EnableWindowsTargeting><AssemblyName>KlidanAudioProbe.Gui.App</AssemblyName><InformationalVersion>$(IdentitySha)</InformationalVersion></PropertyGroup></Project>
'@ | Set-Content (Join-Path $project 'GuiFixture.csproj') -Encoding utf8
    Set-Content (Join-Path $project 'Program.cs') 'public static class Program { public static void Main() {} }' -Encoding utf8
    $output = Join-Path $project 'out'
    & dotnet build (Join-Path $project 'GuiFixture.csproj') -c Release -o $output "-p:IdentitySha=$sha" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not build GUI ProductVersion fixture.' }
    $exe = Join-Path $output 'KlidanAudioProbe.Gui.App.exe'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -notmatch [regex]::Escape($sha)) { throw 'GUI ProductVersion fixture does not embed its exact source SHA.' }
    $guiFixtures[$sha] = $exe
    $exe
}
function Add-Build([string]$dir,[string]$branch,[string]$sha) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $gui=Join-Path $dir 'KlidanAudioProbe.Gui.App.exe'; Copy-Item (Get-GuiFixture $sha) $gui
    $bins=@{gui=@{path=$gui;sha256=(Get-FileHash $gui -Algorithm SHA256).Hash}}; foreach ($spec in @(@('nativeWorker','KlidanProbeWorker.exe'),@('nativeCli','KlidanAudioProbe.exe'))) { $p=Join-Path $dir $spec[1]; [IO.File]::WriteAllBytes($p,[byte[]]::new(8192)); $bins[$spec[0]]=@{path=$p;sha256=(Get-FileHash $p -Algorithm SHA256).Hash} }
    @{schemaVersion=2;sourceBranch=$branch;sourceCommit=$sha;binaries=$bins}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $dir 'audio-probe-qa-build.json') -Encoding utf8
    $bins
}
try {
    New-Item -ItemType Directory -Path $repo,$trees -Force|Out-Null
    & git init -b feat/audio-probe-as-n3c-gui-integration $repo|Out-Null; & git -C $repo config user.email qa@example.invalid; & git -C $repo config user.name 'QA Test'
    Set-Content (Join-Path $repo 'source.txt') 'test'; & git -C $repo add source.txt; & git -C $repo commit -m base|Out-Null; $shaN3c=(& git -C $repo rev-parse HEAD).Trim()
    $treeN4b=Join-Path $trees 'n4b'; & git -C $repo worktree add -b feat/audio-probe-as-n4b-bounded-physical-qa $treeN4b|Out-Null; $shaN4b=(& git -C $treeN4b rev-parse HEAD).Trim()
    $treeN4b1=Join-Path $trees 'n4b1'; & git -C $repo worktree add -b fix/as-n4b1-native-duplex-v2-cli $treeN4b1 $shaN4b|Out-Null; & git -C $treeN4b1 config user.email qa@example.invalid; & git -C $treeN4b1 config user.name 'QA Test'; & git -C $treeN4b1 commit --allow-empty -m n4b1|Out-Null; $shaN4b1=(& git -C $treeN4b1 rev-parse HEAD).Trim()
    @{schemaVersion=1;projects=@(@{projectId='AUDIO-PROBE-OSS';root=$root});repositories=@(@{repoId='audio-probe-oss';currentPath=$repo})}|ConvertTo-Json -Depth 5|Set-Content $registryPath -Encoding utf8
    $n3c=Add-Build (Join-Path $root 'builds\n3c') 'feat/audio-probe-as-n3c-gui-integration' $shaN3c
    $n4b=Add-Build (Join-Path $root 'builds\n4b') 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b
    $n4b1=Add-Build (Join-Path $root 'builds\n4b1') 'fix/as-n4b1-native-duplex-v2-cli' $shaN4b1
    $selectedN3c=Invoke-Select 'feat/audio-probe-as-n3c-gui-integration' $shaN3c
    $selectedN4b=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b
    $selectedN4b1=Invoke-Select 'fix/as-n4b1-native-duplex-v2-cli' $shaN4b1
    Assert (($selectedN3c|ConvertFrom-Json).SourceCommit -eq $shaN3c) 'selects registered AS-N3C exact SHA'
    Assert (($selectedN4b|ConvertFrom-Json).SourceCommit -eq $shaN4b) 'selects registered AS-N4B exact SHA'
    Assert (($selectedN4b1|ConvertFrom-Json).SourceCommit -eq $shaN4b1) 'selects registered AS-N4B.1 exact SHA'
    $duplicateDir=Join-Path $root 'builds\n4b1-duplicate'; New-Item -ItemType Directory -Path $duplicateDir -Force|Out-Null; Copy-Item (Join-Path $root 'builds\n4b1\audio-probe-qa-build.json') (Join-Path $duplicateDir 'audio-probe-qa-build.json')
    $ambiguous=$false; try{$null=Invoke-Select 'fix/as-n4b1-native-duplex-v2-cli' $shaN4b1}catch{$ambiguous=$_.Exception.Message -match 'Ambiguous builds'}; Assert $ambiguous 'refuses multiple matching manifests'; Remove-Item $duplicateDir -Recurse -Force
    $badSha=$false; try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' ('0'*40)}catch{$badSha=$_.Exception.Message -match 'Expected exactly one registered worktree'}; Assert $badSha 'rejects wrong/unregistered source SHA'
    $missingManifest=$false; Remove-Item (Join-Path $root 'builds\n4b\audio-probe-qa-build.json'); try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b}catch{$missingManifest=$_.Exception.Message -match 'No verified build'}; Assert $missingManifest 'rejects missing matching manifest'
    $n4b1Manifest=Join-Path $root 'builds\n4b1\audio-probe-qa-build.json'; $stale=Get-Content $n4b1Manifest -Raw|ConvertFrom-Json; $stale.sourceCommit='0'*40; $stale|ConvertTo-Json -Depth 5|Set-Content $n4b1Manifest -Encoding utf8; $staleRejected=$false; try{$null=Invoke-Select 'fix/as-n4b1-native-duplex-v2-cli' $shaN4b1}catch{$staleRejected=$_.Exception.Message -match 'No verified build'}; Assert $staleRejected 'rejects a stale source SHA manifest'
    Remove-Item $n4b1Manifest
    Add-Build (Join-Path $root 'builds\n4b') 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b|Out-Null
    $badInstall=$false; try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b (Join-Path $root 'unverified.exe')}catch{$badInstall=$_.Exception.Message -match 'must exactly match'}; Assert $badInstall 'InstallPath cannot bypass verified build identity'
    $badBytes=[byte[]]::new(8192); $badBytes[0]=1; $badBinary=$false; [IO.File]::WriteAllBytes($n4b.nativeWorker.path,$badBytes); try{$null=Invoke-Select 'feat/audio-probe-as-n4b-bounded-physical-qa' $shaN4b}catch{$badBinary=$_.Exception.Message -match 'SHA-256 mismatch'}; Assert $badBinary 'rejects damaged binary via stored hash'
    $registerDir=Join-Path $root 'builds\register-bad'; New-Item -ItemType Directory -Path $registerDir -Force|Out-Null
    Copy-Item (Get-GuiFixture $shaN4b) (Join-Path $registerDir 'KlidanAudioProbe.Gui.App.exe')
    Copy-Item $n4b.nativeWorker.path (Join-Path $registerDir 'KlidanProbeWorker.exe')
    $registerCli=Join-Path $registerDir 'KlidanAudioProbe.exe'; Copy-Item $n4b.nativeCli.path $registerCli
    $registerError=''; try{$null=& $launcher -RegisterBuild -SourceBranch 'fix/as-n4b1-native-duplex-v2-cli' -ExpectedSha $shaN4b1 -BuildDirectory $registerDir -NativeCliPath $registerCli -WorkspaceRegistryPath $registryPath}catch{$registerError=$_.Exception.Message}; Assert ($registerError -match 'ProductVersion must contain exact source commit') "refuses to register GUI without the exact current source SHA [$registerError]"
    $registerGood=Join-Path $root 'builds\register-good'; New-Item -ItemType Directory -Path $registerGood -Force|Out-Null
    Copy-Item (Get-GuiFixture $shaN4b1) (Join-Path $registerGood 'KlidanAudioProbe.Gui.App.exe')
    Copy-Item $n4b.nativeWorker.path (Join-Path $registerGood 'KlidanProbeWorker.exe')
    $goodCli=Join-Path $registerGood 'KlidanAudioProbe.exe'; Copy-Item $n4b.nativeCli.path $goodCli
    $registered=& $launcher -RegisterBuild -SourceBranch 'fix/as-n4b1-native-duplex-v2-cli' -ExpectedSha $shaN4b1 -BuildDirectory $registerGood -NativeCliPath $goodCli -WorkspaceRegistryPath $registryPath
    Assert (Test-Path -LiteralPath $registered -PathType Leaf) 'registers exact-SHA GUI/Worker/CLI manifest'
    $overwrite=$false; try{$null=& $launcher -RegisterBuild -SourceBranch 'fix/as-n4b1-native-duplex-v2-cli' -ExpectedSha $shaN4b1 -BuildDirectory $registerGood -NativeCliPath $goodCli -WorkspaceRegistryPath $registryPath}catch{$overwrite=$_.Exception.Message -match 'Refusing to overwrite'}; Assert $overwrite 'does not overwrite an existing QA build manifest'
    Write-Host 'PASS: AS-N3C/AS-N4B/AS-N4B.1 source selection, exact GUI ProductVersion, manifest, binary integrity, registration and InstallPath gate'
} finally { if(Test-Path $root){Remove-Item $root -Recurse -Force} }
