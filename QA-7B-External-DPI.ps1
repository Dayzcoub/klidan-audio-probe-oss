param(
  [Parameter(Mandatory)][string]$ExePath,
  [Parameter(Mandatory)][ValidateSet(120,144)][int]$ExpectedDpi,
  [Parameter(Mandatory)][string]$SourceSha,
  [string]$ArtifactRoot = (Join-Path $env:LOCALAPPDATA ('KLIDAN-QA7B-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw "GUI executable not found: $ExePath" }
New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing.Common
if (-not ('Qa7bExternalWin32' -as [type])) {
  $native = @"
using System;
using System.Runtime.InteropServices;
public static class Qa7bExternalWin32 {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 public static readonly IntPtr TOPMOST=new IntPtr(-1), NOTOPMOST=new IntPtr(-2);
}
"@
  Add-Type $native
}
$pref = Join-Path $env:LOCALAPPDATA 'KlidanAudioProbe\language.txt'
$prefExisted = Test-Path -LiteralPath $pref
$prefBytes = if ($prefExisted) {[IO.File]::ReadAllBytes($pref)} else {$null}
$results = [Collections.Generic.List[object]]::new()
try {
  $scale = $ExpectedDpi / 96.0
foreach ($locale in @('en-US','ru-RU')) {
    foreach ($devTools in @($false,$true)) {
      [IO.Directory]::CreateDirectory((Split-Path -Parent $pref)) | Out-Null
      [IO.File]::WriteAllText($pref,$locale,[Text.UTF8Encoding]::new($false))
      $args = if ($devTools) {'--developer-tools'} else {''}
      $proc = Start-Process -FilePath $ExePath -ArgumentList $args -PassThru
      try {
        for ($i=0; $i -lt 40; $i++) { $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) {break}; Start-Sleep -Milliseconds 250 }
        if ($proc.MainWindowHandle -eq 0) { throw 'GUI main window did not appear within 10 seconds' }
        $hwnd=$proc.MainWindowHandle
        [void][Qa7bExternalWin32]::SetWindowPos($hwnd,[Qa7bExternalWin32]::TOPMOST,0,0,0,0,0x43)
        [void][Qa7bExternalWin32]::SetForegroundWindow($hwnd)
        foreach ($dip in @(@(1440,900),@(1024,720))) {
          $w=[int][math]::Round($dip[0]*$scale); $h=[int][math]::Round($dip[1]*$scale)
          [void][Qa7bExternalWin32]::SetWindowPos($hwnd,[Qa7bExternalWin32]::TOPMOST,0,0,$w,$h,0x40)
          Start-Sleep -Milliseconds 400
          $r=New-Object Qa7bExternalWin32+RECT; [void][Qa7bExternalWin32]::GetWindowRect($hwnd,[ref]$r)
          $dpi=[Qa7bExternalWin32]::GetDpiForWindow($hwnd)
          $actualW=$r.Right-$r.Left; $actualH=$r.Bottom-$r.Top
          if ($dpi -ne $ExpectedDpi) { throw "Expected DPI $ExpectedDpi, actual GetDpiForWindow=$dpi" }
          if ([math]::Abs(($actualW/$scale)-$dip[0]) -gt 2 -or [math]::Abs(($actualH/$scale)-$dip[1]) -gt 2) { throw "Window geometry mismatch: ${actualW}x${actualH}px at ${dpi}dpi, expected $($dip[0])x$($dip[1]) DIP" }
          $bmp=[Drawing.Bitmap]::new($actualW,$actualH); $g=[Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left,$r.Top,0,0,$bmp.Size)
          $toolTag=if($devTools){'devtools-on'}else{'devtools-off'}
          $tag=('{0}-{1}-{2}x{3}' -f $locale,$toolTag,$dip[0],$dip[1])
          $png=Join-Path $ArtifactRoot ($tag+'.png'); $bmp.Save($png,[Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
          $ae=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
          $all=$ae.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
          $outside=0; $unnamed=0; $buttons=0
          foreach($item in $all) { $b=$item.Current.BoundingRectangle; if($b.Width -gt 0 -and $b.Height -gt 0 -and ($b.Left -lt $r.Left -or $b.Top -lt $r.Top -or $b.Right -gt $r.Right -or $b.Bottom -gt $r.Bottom)){$outside++}; if($item.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button){$buttons++;if([string]::IsNullOrWhiteSpace($item.Current.Name) -and $item.Current.AutomationId -notin @('PageUp','PageDown')){$unnamed++}} }
          $results.Add([pscustomobject]@{SourceSha=$SourceSha;Locale=$locale;DeveloperTools=$devTools;TargetDip="$($dip[0])x$($dip[1])";Dpi=$dpi;WindowPixels="${actualW}x${actualH}";UIAElements=$all.Count;OutsideBounds=$outside;UnnamedButtons=$unnamed;Buttons=$buttons;Screenshot=$png})
        }
      } finally { [void][Qa7bExternalWin32]::SetWindowPos($proc.MainWindowHandle,[Qa7bExternalWin32]::NOTOPMOST,0,0,0,0,0x1); if (!$proc.HasExited) {$proc.CloseMainWindow()|Out-Null;if(!$proc.WaitForExit(5000)){$proc.Kill();$proc.WaitForExit()}} }
    }
  }
} finally {
  if($prefExisted){[IO.File]::WriteAllBytes($pref,$prefBytes)} elseif(Test-Path -LiteralPath $pref){Remove-Item -LiteralPath $pref -Force}
  $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'manifest.json') -Encoding utf8
}
$results | Format-Table Locale,DeveloperTools,TargetDip,Dpi,WindowPixels,OutsideBounds,UnnamedButtons,Screenshot -AutoSize
Write-Host "Artifacts: $ArtifactRoot"
