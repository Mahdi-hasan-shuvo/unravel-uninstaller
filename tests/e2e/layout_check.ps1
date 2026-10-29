# Captures every page at a given window size so clipped or overlapping layouts can be reviewed.
param([string]$Exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe"),
      [int]$Width = 980, [int]$Height = 600, [string]$Tag = "min")
. (Join-Path $PSScriptRoot "ui.ps1")
if (-not ('UWMove' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public class UWMove { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint); }
"@
}
function Wait-For([scriptblock]$probe, [int]$seconds = 30) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }; $null }
$out = Join-Path $PSScriptRoot "out\layout-$Tag"; New-Item -ItemType Directory -Force $out | Out-Null
$p = Start-Process $Exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" } 60
[UWMove]::MoveWindow([IntPtr]$w.Current.NativeWindowHandle, 100, 60, $Width, $Height, $true) | Out-Null
Start-Sleep 3
$pages = 'Programs','Windows apps','Startup manager','Package manager','Live monitor','Performance','Install tracker','Junk files','Duplicates & orphans','Browser history','Office history','Windows history','Secure delete','Wipe free space','Windows tools','Activity & recovery','Settings','About & help'
$i = 0
foreach ($pg in $pages) {
    Select-Nav $w $pg | Out-Null; Start-Sleep -Milliseconds 2500
    $i++; Save-Window $w (Join-Path $out ("{0:00}-{1}.png" -f $i, ($pg -replace '[^A-Za-z]', '')))
}
Stop-Process -Id $p.Id -Force
"saved $i pages to $out"
