#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$gui = Join-Path $package 'KlidanAudioProbe.Gui.App.exe'
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
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
$record = [ordered]@{ schemaVersion = 1; sourceCommit = $manifest.sourceCommit; packageVersion = $manifest.sourceCommit; launchId = $id; startedUtc = $started.ToString('O'); endedUtc = $null; exitCode = $null; executableSha256 = $hashes }
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8
$startInfo = [Diagnostics.ProcessStartInfo]::new($gui)
$startInfo.WorkingDirectory = $package
$startInfo.ArgumentList.Add('--qa-root')
$startInfo.ArgumentList.Add($package)
$process = [Diagnostics.Process]::Start($startInfo)
$process.WaitForExit()
$record.endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$record.exitCode = $process.ExitCode
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8
exit $process.ExitCode
