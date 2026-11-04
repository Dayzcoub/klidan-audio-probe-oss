[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [Parameter(Mandatory = $true)][string]$ExpectedHead,
    [string]$ExpectedBranch = 'feat/s5-a-sustained-device-profiles',
    [string]$InstallerPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path -Parent $scriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkingDirectory = $RepoRoot) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}
function Require-File([string]$Path) { if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" } }
function Find-DumpBin {
    $command = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidate = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC','C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC' -Filter dumpbin.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\bin\\Hostx64\\x64\\dumpbin\.exe$' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { throw 'dumpbin.exe is required for the native dependency audit.' }
    return $candidate.FullName
}
function Audit-Native([string]$Path, [string]$DumpBin) {
    $output = @(& $DumpBin /nologo /dependents $Path 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "PE dependency audit failed: $Path" }
    $dependencies = @($output | ForEach-Object { if ($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') { $Matches[1].ToUpperInvariant() } } | Sort-Object -Unique)
    $unsupported = @($dependencies | Where-Object { $_ -match '^(VCRUNTIME140(?:_1)?|MSVCP140[^.]*)\.DLL$' })
    if ($unsupported.Count -gt 0) { throw "Unsupported external MSVC runtime dependency in ${Path}: $($unsupported -join ', ')" }
    [pscustomobject]@{ binary = [IO.Path]::GetFileName($Path); dependencies = $dependencies; unsupportedExternalMsvcRuntime = $unsupported; passed = $true }
}

$branch = (& git -C $RepoRoot branch --show-current).Trim()
$head = (& git -C $RepoRoot rev-parse HEAD).Trim()
if ($branch -ne $ExpectedBranch) { throw "Expected branch '$ExpectedBranch', found '$branch'." }
if ($head -ne $ExpectedHead) { throw "Expected HEAD '$ExpectedHead', found '$head'." }
if ((& git -C $RepoRoot status --porcelain)) { throw 'Working tree must be clean before packaging.' }
foreach ($tool in @('git', 'cmake', 'dotnet')) { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Required build tool is missing: $tool" } }
if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -notin @('AMD64', 'x86_64')) { throw 'Installer packaging requires Windows x64.' }

$icon = Join-Path $RepoRoot 'assets\application-icon.ico'
$png = Join-Path $RepoRoot 'assets\application-icon.png'
Require-File $icon; Require-File $png
$props = [xml](Get-Content -Raw -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props'))
$versionPrefix = $props.Project.PropertyGroup.VersionPrefix
$versionSuffix = $props.Project.PropertyGroup.VersionSuffix
$appVersion = "$versionPrefix-$versionSuffix"
$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $iscc = @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe', 'C:\Program Files (x86)\Inno Setup\ISCC.exe', 'C:\Program Files\Inno Setup\ISCC.exe') |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 compiler (ISCC.exe) is unavailable. Install it separately; this script never downloads or installs build dependencies.' }
$isccPath = if ($iscc.PSObject.Properties.Name -contains 'Source' -and $iscc.Source) { $iscc.Source }
elseif ($iscc.PSObject.Properties.Name -contains 'FullName' -and $iscc.FullName) { $iscc.FullName }
else { [string]$iscc }

$buildRoot = Join-Path $RepoRoot 'build'
Invoke-Checked cmake @('-S', $RepoRoot, '-B', $buildRoot, '-A', 'x64', '--fresh')
Invoke-Checked cmake @('--build', $buildRoot, '--config', 'Release', '--parallel')
$worker = Join-Path $buildRoot 'worker\Release\KlidanProbeWorker.exe'
$nativeCli = Join-Path $buildRoot 'Release\KlidanAudioProbe.exe'
Require-File $worker; Require-File $nativeCli
$dumpbin = Find-DumpBin
$nativeDependencyAudit = @((Audit-Native $worker $dumpbin), (Audit-Native $nativeCli $dumpbin))

$outRoot = Join-Path $RepoRoot 'out\installer'
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
if ([string]::IsNullOrWhiteSpace($InstallerPath)) { $InstallerPath = Join-Path $outRoot 'KLIDAN-AudioProbe-Setup.exe' }
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
if (Test-Path -LiteralPath $InstallerPath -PathType Any) { throw "Installer already exists; overwrite is refused: $InstallerPath" }
$stage = Join-Path ([IO.Path]::GetTempPath()) "klidan-lab-installer-$([Guid]::NewGuid().ToString('N'))"
$publish = Join-Path $stage 'publish'
$previousSource = $env:KLIDAN_INSTALLER_SOURCE; $previousOutput = $env:KLIDAN_INSTALLER_OUTPUT; $previousVersion = $env:KLIDAN_APP_VERSION
try {
    New-Item -ItemType Directory -Force -Path $publish | Out-Null
    Invoke-Checked dotnet @('publish', (Join-Path $RepoRoot 'src\gui\KlidanAudioProbe.Gui.App\KlidanAudioProbe.Gui.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false', '-o', $publish)
    Require-File (Join-Path $publish 'KlidanAudioProbe.Gui.App.exe')
    Require-File (Join-Path $publish 'KlidanProbeWorker.exe')
    Require-File (Join-Path $publish 'KlidanAudioProbe.exe')
    $licenseFiles = @()
    foreach ($projectFile in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        $sourceFile = Join-Path $RepoRoot $projectFile
        Require-File $sourceFile
        $destinationFile = Join-Path $publish $projectFile
        Copy-Item -LiteralPath $sourceFile -Destination $destinationFile -Force
        $licenseFiles += [ordered]@{ path = $projectFile; source = 'repository'; sha256 = (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $publish 'KlidanAudioProbe.Gui.App.runtimeconfig.json') | ConvertFrom-Json
    foreach ($framework in @($runtimeConfig.runtimeOptions.includedFrameworks)) {
        $packageName = switch ($framework.name) {
            'Microsoft.NETCore.App' { 'microsoft.netcore.app.runtime.win-x64' }
            'Microsoft.WindowsDesktop.App' { 'microsoft.windowsdesktop.app.runtime.win-x64' }
            default { throw "Unsupported self-contained framework for notice collection: $($framework.name)" }
        }
        $packageDirectory = Join-Path $env:USERPROFILE ".nuget\packages\$packageName\$($framework.version)"
        if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) { throw "Runtime package is missing for notice collection: $packageName $($framework.version)" }
        foreach ($noticeName in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $sourceNotice = Join-Path $packageDirectory $noticeName
            if (-not (Test-Path -LiteralPath $sourceNotice -PathType Leaf)) { continue }
            $destinationName = "runtime-$($framework.name)-$noticeName"
            $destinationNotice = Join-Path $publish $destinationName
            Copy-Item -LiteralPath $sourceNotice -Destination $destinationNotice -Force
            $licenseFiles += [ordered]@{ path = $destinationName; source = "$packageName/$($framework.version)/$noticeName"; sha256 = (Get-FileHash -LiteralPath $destinationNotice -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    }
    Copy-Item -LiteralPath $icon -Destination (Join-Path $publish 'application-icon.ico') -Force
    $env:KLIDAN_INSTALLER_SOURCE = $publish
    $env:KLIDAN_INSTALLER_OUTPUT = (Split-Path -Parent $InstallerPath)
    $env:KLIDAN_APP_VERSION = $appVersion
    Invoke-Checked $isccPath @((Join-Path $RepoRoot 'installer\KLIDAN-AudioProbe.iss'))
    Require-File $InstallerPath
    $manifest = [ordered]@{
        schemaVersion = 1
        installer = [IO.Path]::GetFileName($InstallerPath)
        sourceBranch = $branch
        sourceHead = $head
        applicationVersion = $appVersion
        buildConfiguration = 'Release'
        runtime = 'win-x64'
        selfContained = $true
        buildTimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        nativeDependencyAudit = $nativeDependencyAudit
        installedRuntimeFiles = @('KlidanAudioProbe.Gui.App.exe', 'KlidanProbeWorker.exe', 'KlidanAudioProbe.exe', 'application-icon.ico', 'self-contained .NET runtime files')
        licenseFiles = $licenseFiles
        sha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $InstallerPath).Length
        unsigned = $true
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$InstallerPath.manifest.json" -Encoding UTF8
    Write-Host "Created: $InstallerPath"
    Write-Host "SHA-256: $($manifest.sha256)"
    Write-Host "Source:  $branch@$head"
}
finally {
    $env:KLIDAN_INSTALLER_SOURCE = $previousSource; $env:KLIDAN_INSTALLER_OUTPUT = $previousOutput; $env:KLIDAN_APP_VERSION = $previousVersion
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
