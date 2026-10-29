# Measures UI-thread stalls: a background thread pings the main window every 50 ms (WM_NULL with a
# timeout) while the script navigates every page and starts the read-only scans. Reports the longest stall per step.
param([string]$Exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe"),
      [switch]$Scans)
. (Join-Path $PSScriptRoot "ui.ps1")
if (-not ('UiPing' -as [type])) {
Add-Type @"
using System; using System.Threading; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Diagnostics;
public class UiPing {
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    IntPtr _h; Thread _t; volatile bool _run; public double Max; public int Over100, Over250, Samples;
    public UiPing(IntPtr h) { _h = h; }
    public void Start() { Max = 0; Over100 = 0; Over250 = 0; Samples = 0; _run = true; _t = new Thread(Loop) { IsBackground = true }; _t.Start(); }
    public void Stop() { _run = false; _t.Join(); }
    void Loop() {
        while (_run) {
            var sw = Stopwatch.StartNew(); IntPtr r;
            SendMessageTimeout(_h, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 10000, out r);
            double ms = sw.Elapsed.TotalMilliseconds; Samples++;
            if (ms > Max) Max = ms; if (ms > 100) Over100++; if (ms > 250) Over250++;
            Thread.Sleep(50);
        }
    }
}
"@
}
function Wait-For([scriptblock]$probe, [int]$seconds = 30) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }; $null }
$results = New-Object System.Collections.Generic.List[object]
$p = Start-Process $Exe -PassThru
$sw0 = [Diagnostics.Stopwatch]::StartNew()
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" } 60
$ping = New-Object UiPing ([IntPtr]$w.Current.NativeWindowHandle)
function Measure-Step([string]$name, [scriptblock]$action, [int]$settleMs = 2500) {
    $ping.Start(); $sw = [Diagnostics.Stopwatch]::StartNew()
    & $action | Out-Null
    Start-Sleep -Milliseconds $settleMs
    $ping.Stop()
    $results.Add([pscustomobject]@{ Step = $name; MaxStallMs = [int]$ping.Max; Over100 = $ping.Over100; Over250 = $ping.Over250; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) })
}
Measure-Step "startup (programs load)" { Start-Sleep 1 } 4000
"window after $([int]$sw0.Elapsed.TotalMilliseconds) ms"
$pages = 'Windows apps','Startup manager','Package manager','Live monitor','Performance','Install tracker','Junk files','Duplicates & orphans','Browser history','Office history','Windows history','Secure delete','Wipe free space','Windows tools','Activity & recovery','Settings','About & help','Programs'
foreach ($pg in $pages) { Measure-Step "open $pg" { Select-Nav $w $pg } 2500 }
Select-Nav $w 'Live monitor' | Out-Null; Start-Sleep 3
Measure-Step "monitor steady state (15 s)" { Start-Sleep 15 } 0
Select-Nav $w 'Programs' | Out-Null; Start-Sleep 1
if ($Scans) {
    Select-Nav $w 'Programs' | Out-Null; Start-Sleep 1
    Measure-Step "programs refresh" { Invoke-El (Find-ByName $w 'Refresh') } 5000
    Measure-Step "programs search typing" { $b = Find-ById $w 'SearchBox'; $vp = $b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); foreach ($t in 'm','mi','mic','micr','micro','') { $vp.SetValue($t); Start-Sleep -Milliseconds 80 } } 1500
    Select-Nav $w 'Windows history' | Out-Null; Start-Sleep 1
    Measure-Step "windows history analyze" { Invoke-El (Find-ByName $w 'Analyze') } 15000
    Select-Nav $w 'Browser history' | Out-Null; Start-Sleep 1
    Measure-Step "browser history analyze" { Invoke-El (Find-ByName $w 'Analyze') } 12000
    Select-Nav $w 'Junk files' | Out-Null; Start-Sleep 1
    Measure-Step "junk scan" { Invoke-El (Find-ByName $w 'Scan') } 20000
    Select-Nav $w 'Startup manager' | Out-Null; Start-Sleep 1
    Measure-Step "startup refresh" { Invoke-El (Find-ByName $w 'Refresh') } 5000
    Select-Nav $w 'Performance' | Out-Null; Start-Sleep 1
    Measure-Step "performance analyze" { Invoke-El (Find-ByName $w 'Analyze') } 10000
    Select-Nav $w 'Duplicates & orphans' | Out-Null; Start-Sleep 1
    Measure-Step "orphan scan" { Invoke-El (Find-ById $w 'TabOrphan'); Start-Sleep -Milliseconds 300; Invoke-El (Find-ById $w 'OrphanScan') } 25000
}
Stop-Process -Id $p.Id -Force
$results | Format-Table -AutoSize | Out-String -Width 200
