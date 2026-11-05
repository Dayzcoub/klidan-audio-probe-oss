param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][ValidateSet(96, 120, 144)][int]$ExpectedDpi,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40,64}$')][string]$SourceSha,
    [Parameter(Mandatory)][string]$BuildManifestPath,
    [string]$ArtifactRoot = (Join-Path $env:LOCALAPPDATA ('KLIDAN-QA7B-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$BuildManifestPath = (Resolve-Path -LiteralPath $BuildManifestPath).Path
$source = $SourceSha.ToLowerInvariant()
$manifest = Get-Content -LiteralPath $BuildManifestPath -Raw | ConvertFrom-Json
$exeHash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.sourceSha.ToLowerInvariant() -ne $source -or
    $manifest.configuration -ne 'Release' -or
    $manifest.executableSha256.ToLowerInvariant() -ne $exeHash) {
    throw 'Build manifest does not match the requested source SHA, Release configuration, and executable hash.'
}

New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null
try { Add-Type -AssemblyName System.Drawing.Common -ErrorAction Stop }
catch { Add-Type -AssemblyName System.Drawing -ErrorAction Stop }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not ('Qa7cExternalWin32' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Qa7cExternalWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint="GetWindowLong")] public static extern int GetWindowLong32(IntPtr h, int index);
    public static readonly IntPtr TOPMOST = new IntPtr(-1), NOTOPMOST = new IntPtr(-2);
}
'@
}

function Find-Id($Root, [string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-Name($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ButtonName($Root, [string]$Name) {
    $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $element.Current.Name -eq $Name) { return $element }
    }
    return $null
}

function Invoke-Element($Element, [string]$Description) {
    if ($null -eq $Element -or -not $Element.Current.IsEnabled) { throw "UIA element unavailable: $Description" }
    try { $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { throw "UIA InvokePattern failed for ${Description}: $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 350
}

function Expand-Element($Element, [string]$Description) {
    if ($null -eq $Element) { throw "UIA element unavailable: $Description" }
    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) { $pattern.Expand() }
    } catch { throw "UIA ExpandCollapsePattern failed for ${Description}: $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 250
}

function Get-ItemCount($Element, [string]$Description) {
    try {
        $container = $Element.GetCurrentPattern([System.Windows.Automation.ItemContainerPattern]::Pattern)
        $item = $container.FindItemByProperty($null, $null, $null)
        $count = 0
        while ($null -ne $item -and $count -lt 10000) {
            $count++
            $item = $container.FindItemByProperty($item, $null, $null)
        }
        if ($count -ge 10000) { throw 'Item count exceeded safety bound.' }
        return $count
    } catch { throw "Could not count UIA items for ${Description}: $($_.Exception.Message)" }
}

function Get-WindowProcess([int]$Id) {
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -ne $p) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { return $p } }
    return $null
}

function Save-Page([string]$Locale, [string]$Mode, [string]$Page, $Process, [int]$DipWidth, [int]$DipHeight) {
    $hwnd = $Process.MainWindowHandle
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $rect = New-Object Qa7cExternalWin32+RECT
    [void][Qa7cExternalWin32]::GetWindowRect($hwnd, [ref]$rect)
    $dpi = [Qa7cExternalWin32]::GetDpiForWindow($hwnd)
    $scale = $dpi / 96.0
    $actualWidth = $rect.Right - $rect.Left
    $actualHeight = $rect.Bottom - $rect.Top
    if ($dpi -ne $ExpectedDpi) { throw "GetDpiForWindow=$dpi, expected $ExpectedDpi" }
    if ([math]::Abs(($actualWidth / $scale) - $DipWidth) -gt 2 -or
        [math]::Abs(($actualHeight / $scale) - $DipHeight) -gt 2) {
        throw "GetWindowRect ${actualWidth}x${actualHeight}px does not match ${DipWidth}x${DipHeight} DIP at ${dpi} DPI."
    }

    $horizontalOverflow = 0
    $horizontallyScrollable = 0
    $verticalScrollContent = 0
    $unnamedButtons = 0
    $buttonCount = 0
    $inspector = Find-Id $window 'InspectorSectionList'
    $inspectorVisible = ($null -ne $inspector -and -not $inspector.Current.IsOffscreen -and
        $inspector.Current.BoundingRectangle.Width -gt 0 -and $inspector.Current.BoundingRectangle.Height -gt 0)
    $evidenceName = if ($Locale -eq 'en-US') { 'Evidence Library records' } else { 'Записи библиотеки свидетельств' }
    $grid = Find-Name $window $evidenceName
    $gridRows = $null
    $gridColumns = $null
    if ($null -ne $grid) {
        try { $gridPattern = $grid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern); $gridRows = $gridPattern.Current.RowCount; $gridColumns = $gridPattern.Current.ColumnCount }
        catch { throw 'Evidence Library grid does not expose UIA GridPattern.' }
    }
    foreach ($element in $all) {
        $bounds = $element.Current.BoundingRectangle
        if (-not $element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) {
            if ($bounds.Left -lt $rect.Left -or $bounds.Right -gt $rect.Right) { $horizontalOverflow++ }
            if ($bounds.Top -lt $rect.Top -or $bounds.Bottom -gt $rect.Bottom) { $verticalScrollContent++ }
        }
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
            $buttonCount++
            if ([string]::IsNullOrWhiteSpace($element.Current.Name) -and
                $element.Current.AutomationId -notin @('PageUp', 'PageDown')) { $unnamedButtons++ }
        }
        try {
            $scroll = $element.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($scroll.Current.HorizontallyScrollable) { $horizontallyScrollable++ }
        } catch { }
    }

    $bitmap = [Drawing.Bitmap]::new($actualWidth, $actualHeight)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        finally { $graphics.Dispose() }
        $file = Join-Path $ArtifactRoot ("{0}-{1}-{2}-{3}x{4}.png" -f $Locale, $Mode, $Page, $DipWidth, $DipHeight)
        $bitmap.Save($file, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }

    return [pscustomobject]@{
        SourceSha = $source; ExecutableSha256 = $exeHash; Locale = $Locale; DeveloperTools = $Mode
        Page = $Page; TargetDip = "${DipWidth}x${DipHeight}"; Dpi = $dpi
        WindowPixels = "${actualWidth}x${actualHeight}"; UIAElements = $all.Count
        HorizontalOverflow = $horizontalOverflow; HorizontalScrollPatterns = $horizontallyScrollable
        VerticalScrollContent = $verticalScrollContent
        Buttons = $buttonCount; UnnamedButtons = $unnamedButtons; InspectorVisible = $inspectorVisible
        EvidenceGridRows = $gridRows; EvidenceGridColumns = $gridColumns
        SavedProfileOptions = $null; HistoricalResultOptions = $null; Coverage = 'PASS'
        Screenshot = $file
    }
}

$preference = Join-Path $env:LOCALAPPDATA 'KlidanAudioProbe\language.txt'
$preferenceDirectory = Split-Path -Parent $preference
$directoryExisted = Test-Path -LiteralPath $preferenceDirectory
$preferenceExisted = Test-Path -LiteralPath $preference
$preferenceBytes = if ($preferenceExisted) { [IO.File]::ReadAllBytes($preference) } else { $null }
$results = [Collections.Generic.List[object]]::new()
$baselineGui = @(Get-Process -Name 'KlidanAudioProbe.Gui.App' -ErrorAction SilentlyContinue | ForEach-Object Id)
$workerPath = Join-Path (Split-Path -Parent $ExePath) 'KlidanProbeWorker.exe'
$baselineWorkers = @()
if (Test-Path -LiteralPath $workerPath) {
    $baselineWorkers = @(Get-CimInstance Win32_Process -Filter "Name='KlidanProbeWorker.exe'" |
        Where-Object ExecutablePath -eq $workerPath | ForEach-Object ProcessId)
}

try {
    if ($baselineGui.Count -gt 0) { throw 'Close existing Audio Probe GUI instances before running this isolated QA matrix.' }
    $scale = $ExpectedDpi / 96.0
    foreach ($locale in @('en-US', 'ru-RU')) {
        foreach ($developerTools in @($false, $true)) {
            [IO.Directory]::CreateDirectory($preferenceDirectory) | Out-Null
            [IO.File]::WriteAllText($preference, $locale, [Text.UTF8Encoding]::new($false))
            if ($developerTools) {
                $process = Start-Process -FilePath $ExePath -ArgumentList '--developer-tools' -PassThru
            } else {
                $process = Start-Process -FilePath $ExePath -PassThru
            }
            $wasTopmost = $false
            try {
                for ($i = 0; $i -lt 40; $i++) {
                    $process.Refresh()
                    if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
                    Start-Sleep -Milliseconds 250
                }
                if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'GUI main window did not appear within 10 seconds.' }
                $hwnd = $process.MainWindowHandle
                $oldExStyle = [Qa7cExternalWin32]::GetWindowLong32($hwnd, -20)
                $wasTopmost = (($oldExStyle -band 0x00000008) -ne 0)
                $navDevName = if ($locale -eq 'en-US') { 'Developer Tools · real + synthetic' } else { 'Инструменты разработчика · реальные + синтетические' }
                $navEvidenceName = if ($locale -eq 'en-US') { 'Evidence library' } else { 'Библиотека evidence' }
                $gridName = if ($locale -eq 'en-US') { 'Evidence Library records' } else { 'Записи библиотеки свидетельств' }
                $resultTitle = if ($locale -eq 'en-US') { 'Test result' } else { 'Результат проверки' }

                foreach ($dip in @(@(1440, 900), @(1024, 720))) {
                    $pixelWidth = [int][math]::Round($dip[0] * $scale)
                    $pixelHeight = [int][math]::Round($dip[1] * $scale)
                    [void][Qa7cExternalWin32]::SetWindowPos($hwnd, [Qa7cExternalWin32]::TOPMOST, 0, 0, $pixelWidth, $pixelHeight, 0x40)
                    [void][Qa7cExternalWin32]::SetForegroundWindow($hwnd)
                    Start-Sleep -Milliseconds 350
                    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                    $devNav = Find-Name $root $navDevName
                    if ($developerTools -and $null -eq $devNav) { throw 'Developer Tools ON gate missing from UI.' }
                    if (-not $developerTools -and $null -ne $devNav) { throw 'Developer Tools OFF gate exposed the developer navigation.' }

                    $mode = if ($developerTools) { 'ON' } else { 'OFF' }
                    $results.Add((Save-Page $locale $mode 'device' $process $dip[0] $dip[1]))
                    if ($developerTools) {
                        Invoke-Element (Find-ButtonName $root $navDevName) 'Developer Tools navigation'
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        if ($null -eq (Find-Id $root 'OperatorTestStepButton') -or
                            $null -eq (Find-Id $root 'OperatorResultStepButton')) { throw 'Developer Tools did not expose Test and Result navigation.' }

                        $results.Add((Save-Page $locale $mode 'developer' $process $dip[0] $dip[1]))
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        Invoke-Element (Find-ButtonName $root $navEvidenceName) 'Evidence Library navigation'
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        $grid = Find-Name $root $gridName
                        if ($null -eq $grid) { throw 'Evidence Library grid is missing from UI Automation.' }
                        $results.Add((Save-Page $locale $mode 'evidence-library' $process $dip[0] $dip[1]))

                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        Invoke-Element (Find-Id $root 'OperatorResultStepButton') 'Result navigation'
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        if ($null -ne (Find-Name $root $resultTitle)) {
                            $results.Add((Save-Page $locale $mode 'results' $process $dip[0] $dip[1]))
                        } else {
                            if ($null -eq (Find-Name $root $gridName)) {
                                throw 'Result navigation neither opened Results nor remained on Evidence Library; state is unexpected.'
                            }
                            Invoke-Element (Find-Id $root 'OperatorDeviceStepButton') 'Return to Device after guarded Result navigation'
                            $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                            if ($null -eq (Find-Id $root 'DeviceScanButton')) { throw 'Device screen did not return after guarded Result navigation.' }
                            $blocked = Save-Page $locale $mode 'results-blocked-no-evidence' $process $dip[0] $dip[1]
                            $blocked.Coverage = 'PARTIAL: no real result evidence; Result command stayed on prior page'
                            $results.Add($blocked)
                        }

                        Invoke-Element (Find-Id $root 'OperatorDeviceStepButton') 'Device navigation'
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        Expand-Element (Find-Id $root 'SavedProfileDiagnosticsExpander') 'Saved Profiles expander'
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        foreach ($id in @('SavedDeviceProfileSelector', 'HistoricalRecoveryScanSelector')) {
                            if ($null -eq (Find-Id $root $id)) { throw "Device page control missing: $id" }
                        }
                        $savedPage = Save-Page $locale $mode 'saved-profiles-history-inspector' $process $dip[0] $dip[1]
                        $savedPage.SavedProfileOptions = Get-ItemCount (Find-Id $root 'SavedDeviceProfileSelector') 'saved profiles'
                        $savedPage.HistoricalResultOptions = Get-ItemCount (Find-Id $root 'HistoricalRecoveryScanSelector') 'historical results'
                        if ($dip[0] -eq 1440 -and $null -eq (Find-Id $root 'InspectorSectionList')) { throw 'Inspector is missing at the normal window size.' }
                        $results.Add($savedPage)
                    } else {
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        Expand-Element (Find-Id $root 'SavedProfileDiagnosticsExpander') 'Saved Profiles expander'
                        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                        foreach ($id in @('SavedDeviceProfileSelector', 'HistoricalRecoveryScanSelector')) {
                            if ($null -eq (Find-Id $root $id)) { throw "Device page control missing: $id" }
                        }
                        $savedPage = Save-Page $locale $mode 'saved-profiles-history-inspector' $process $dip[0] $dip[1]
                        $savedPage.SavedProfileOptions = Get-ItemCount (Find-Id $root 'SavedDeviceProfileSelector') 'saved profiles'
                        $savedPage.HistoricalResultOptions = Get-ItemCount (Find-Id $root 'HistoricalRecoveryScanSelector') 'historical results'
                        if ($dip[0] -eq 1440 -and $null -eq (Find-Id $root 'InspectorSectionList')) { throw 'Inspector is missing at the normal window size.' }
                        $results.Add($savedPage)
                    }
                }
            } finally {
                if ($process -and -not $process.HasExited) {
                    $process.Refresh()
                    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                        [void][Qa7cExternalWin32]::SetWindowPos($process.MainWindowHandle,
                            $(if ($wasTopmost) { [Qa7cExternalWin32]::TOPMOST } else { [Qa7cExternalWin32]::NOTOPMOST }), 0,0,0,0,0x3)
                        [void]$process.CloseMainWindow()
                    }
                    if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
                }
                $newWorkers = @(Get-CimInstance Win32_Process -Filter "Name='KlidanProbeWorker.exe'" |
                    Where-Object { $_.ExecutablePath -eq $workerPath -and $_.ProcessId -notin $baselineWorkers })
                foreach ($worker in $newWorkers) { Stop-Process -Id $worker.ProcessId -Force -ErrorAction Stop }
                if ($newWorkers.Count -gt 0) { Start-Sleep -Milliseconds 300 }
            }
        }
    }
    if ($results.Count -ne 28) { throw "Incomplete visual matrix: captured $($results.Count) of 28 expected page/size states." }
    if (@($results | Where-Object {
        $_.Dpi -ne $ExpectedDpi -or $_.UnnamedButtons -gt 0 -or
        ($_.HorizontalOverflow -gt 0 -and
         ($_.Page -ne 'evidence-library' -or $_.HorizontalScrollPatterns -eq 0))
    }).Count -gt 0) {
        throw 'DPI, UI bounds, or button accessibility check failed.'
    }
} finally {
    if ($preferenceExisted) { [IO.File]::WriteAllBytes($preference, $preferenceBytes) }
    elseif (Test-Path -LiteralPath $preference) { Remove-Item -LiteralPath $preference -Force }
    if (-not $directoryExisted -and (Test-Path -LiteralPath $preferenceDirectory) -and
        @(Get-ChildItem -LiteralPath $preferenceDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $preferenceDirectory -Force
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'manifest.json') -Encoding utf8
}

$results | Format-Table Locale, DeveloperTools, Page, TargetDip, Dpi, WindowPixels, InspectorVisible, EvidenceGridRows, SavedProfileOptions, HistoricalResultOptions, HorizontalOverflow, HorizontalScrollPatterns, UnnamedButtons, Coverage -AutoSize
$blockedResults = @($results | Where-Object { $_.Page -eq 'results-blocked-no-evidence' }).Count
Write-Host "MATRIX_STATES=$($results.Count); RESULTS=$($(if($blockedResults){'PARTIAL'}else{'PASS'})); RESULTS_BLOCKED_NO_EVIDENCE=$blockedResults; SOURCE=$source; EXE_SHA256=$exeHash"
Write-Host "Artifacts: $ArtifactRoot"
