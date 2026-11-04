[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [Parameter(Mandatory = $true)][string]$ExpectedHead,
    [string]$ExpectedBranch = 'feat/s5-a-sustained-device-profiles',
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path $RepoRoot).Path

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkingDirectory = $RepoRoot) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}
function Require-File([string]$Path) { if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" } }
function Find-DumpBin {
    $command = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $roots = @(
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC'
    )
    $candidate = Get-ChildItem -Path $roots -Filter dumpbin.exe -Recurse -ErrorAction SilentlyContinue |
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
if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -notin @('AMD64', 'x86_64')) { throw 'Packaging requires Windows x64.' }
$dotnetVersion = (& dotnet --version).Trim()
if ([string]::IsNullOrWhiteSpace($dotnetVersion)) { throw '.NET SDK version could not be determined.' }

$buildRoot = Join-Path $RepoRoot 'build'
Invoke-Checked cmake @('-S', $RepoRoot, '-B', $buildRoot, '-A', 'x64', '--fresh')
Invoke-Checked cmake @('--build', $buildRoot, '--config', 'Release', '--parallel')

$worker = Join-Path $buildRoot 'worker\Release\KlidanProbeWorker.exe'
$nativeCli = Join-Path $buildRoot 'Release\KlidanAudioProbe.exe'
Require-File $worker; Require-File $nativeCli
$dumpbin = Find-DumpBin
$nativeDependencyAudit = @(
    (Audit-Native $worker $dumpbin),
    (Audit-Native $nativeCli $dumpbin)
)

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$outRoot = Join-Path $RepoRoot 'out\lab-distribution'
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
if ([string]::IsNullOrWhiteSpace($PackagePath)) { $PackagePath = Join-Path $outRoot "KlidanAudioProbe-L3-A-$($head.Substring(0,12)).zip" }
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$externalManifestPath = "$PackagePath.manifest.json"
if ((Test-Path -LiteralPath $PackagePath -PathType Any) -or (Test-Path -LiteralPath $externalManifestPath -PathType Any)) { throw 'Package or distribution manifest already exists; overwrite is refused.' }

$stage = Join-Path ([IO.Path]::GetTempPath()) "klidan-lab-package-$([Guid]::NewGuid().ToString('N'))"
$publish = Join-Path $stage 'publish'
try {
    New-Item -ItemType Directory -Force -Path $publish | Out-Null
    Invoke-Checked dotnet @('publish', (Join-Path $RepoRoot 'src\gui\KlidanAudioProbe.Gui.App\KlidanAudioProbe.Gui.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false', '-o', $publish)
    $gui = Join-Path $publish 'KlidanAudioProbe.Gui.App.exe'
    Require-File $gui
    New-Item -ItemType Directory -Force -Path (Join-Path $publish 'native') | Out-Null
    Copy-Item -LiteralPath $nativeCli -Destination (Join-Path $publish 'native\KlidanAudioProbe.exe') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Launch-LabDistribution.ps1') -Destination $publish -Force
    Require-File (Join-Path $publish 'KlidanProbeWorker.exe')
    Require-File (Join-Path $publish 'native\KlidanAudioProbe.exe')

    $files = @(Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($publish.Length).TrimStart([char]92, [char]47).Replace([char]92, [char]47)
        [pscustomobject]@{ path = $relative; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    } | Sort-Object path)
    if ($files.Count -eq 0) { throw 'Published output is empty.' }
    $manifest = [ordered]@{
        schemaVersion = 1
        distribution = 'KLIDAN Audio Probe Laboratory Distribution'
        sourceBranch = $branch
        sourceHead = $head
        buildConfiguration = 'Release'
        runtime = 'win-x64'
        selfContained = $true
        buildTimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        dotnetSdk = $dotnetVersion
        applicationVersion = ((Get-Item -LiteralPath $gui).VersionInfo.FileVersion -as [string])
        nativeDependencyAudit = $nativeDependencyAudit
        files = $files
        exclusions = @('source code', 'tests', 'historical profiles', 'operator evidence')
    }
    $manifestPath = Join-Path $publish 'distribution-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $PackagePath -CompressionLevel Optimal
    Require-File $PackagePath
    $zipHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ schemaVersion = 1; package = [IO.Path]::GetFileName($PackagePath); bytes = (Get-Item $PackagePath).Length; sha256 = $zipHash; sourceHead = $head; buildTimestampUtc = $manifest.buildTimestampUtc } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $externalManifestPath -Encoding UTF8
    $verify = Join-Path $stage 'verify'
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $verify -Force
    $readback = Get-Content -Raw -LiteralPath (Join-Path $verify 'distribution-manifest.json') | ConvertFrom-Json
    if ($readback.sourceHead -ne $head -or $readback.files.Count -ne $files.Count) { throw 'Package manifest readback is inconsistent.' }
    foreach ($file in $readback.files) {
        $path = Join-Path $verify ($file.path -replace '/', '\')
        Require-File $path
        $actual = Get-FileHash -LiteralPath $path -Algorithm SHA256
        if ((Get-Item $path).Length -ne $file.bytes -or $actual.Hash.ToLowerInvariant() -ne $file.sha256) { throw "Package hash verification failed: $($file.path)" }
    }
    Write-Host "Created: $PackagePath"
    Write-Host "SHA-256: $zipHash"
    Write-Host "Source:  $branch@$head"
}
finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }
