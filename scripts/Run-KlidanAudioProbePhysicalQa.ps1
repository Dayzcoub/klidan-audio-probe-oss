#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$gui = Join-Path $package 'KlidanAudioProbe.Gui.App.exe'
$manifestPath = Join-Path $package 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Package manifest is missing: $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.sourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Package manifest does not contain a full source commit SHA.' }
foreach ($item in $manifest.files) {
    $file = Join-Path $package $item.path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Manifest file is missing: $file" }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $item.sha256) { throw "SHA-256 mismatch: $file" }
}
foreach ($file in @($gui, (Join-Path $package 'KlidanProbeWorker.exe'), (Join-Path $package 'KlidanAudioProbe.exe'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package is incomplete: $file" }
}
foreach ($folder in @('logs', 'evidence', 'profiles')) { New-Item -ItemType Directory -Force -Path (Join-Path $package $folder) | Out-Null }
$started = [DateTimeOffset]::UtcNow
$id = [Guid]::NewGuid().ToString('N')
$recordPath = Join-Path $package "logs\launch-$id.json"
$hashes = @{}
foreach ($file in @($gui, (Join-Path $package 'KlidanProbeWorker.exe'), (Join-Path $package 'KlidanAudioProbe.exe'))) {
    $hashes[[IO.Path]::GetFileName($file)] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
}
$record = [ordered]@{ schemaVersion = 1; sourceCommit = $manifest.sourceCommit; packageVersion = $manifest.sourceCommit; launchId = $id; startedUtc = $started.ToString('O'); endedUtc = $null; exitCode = $null; executableSha256 = $hashes }
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8
$startInfo = [Diagnostics.ProcessStartInfo]::new($gui)
$startInfo.WorkingDirectory = $package
$startInfo.ArgumentList.Add('--qa-root')
$startInfo.ArgumentList.Add($package)
$previousBuildSha = $env:KLIDAN_BUILD_SHA
$env:KLIDAN_BUILD_SHA = $manifest.sourceCommit.ToLowerInvariant()
try { $process = [Diagnostics.Process]::Start($startInfo) }
finally { $env:KLIDAN_BUILD_SHA = $previousBuildSha }
$process.WaitForExit()
$record.endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$record.exitCode = $process.ExitCode
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8
exit $process.ExitCode
