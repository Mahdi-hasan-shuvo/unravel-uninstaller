# Clicks every enabled, non-destructive button and tab on every page of the real app, closes whatever dialog
# opens (pressing Cancel where there is one), and reports error dialogs and logged application errors.
# Destructive or outward actions (uninstall, delete, clean, wipe, end, install, run, elevate) are never pressed.
param([string]$Exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe"))
. (Join-Path $PSScriptRoot "ui.ps1")
function Wait-For([scriptblock]$probe, [int]$seconds = 30) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }; $null }

$safe = '^(Refresh|Analyze|Details|Pause|Resume|Columns|Check again|Scan|Find orphans|Find duplicates|Processes|Services|Installed|Updates|Find & install|Startup impact|Background processes|Disk usage|Before & after|Duplicate files|Orphaned files|All|Files|Registry|Tasks|Startup|Tiles|List|Export.*|Add.*|Choose folder.*|Edit patterns.*|Select all|Select none|Deselect all|Microsoft services|Show frameworks.*|Only packages.*|Name|Publisher|Search)$'
$navNames = 'Programs','Windows apps','Startup manager','Package manager','Live monitor','Performance','Install tracker','Junk files','Duplicates & orphans','Browser history','Office history','Windows history','Secure delete','Wipe free space','Windows tools','Activity & recovery','Settings','About & help','Hunter mode'
if (-not ('UWClose' -as [type])) { Add-Type 'using System; using System.Runtime.InteropServices; public class UWClose { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }' }
$logDir = Join-Path $env:LOCALAPPDATA 'Unravel\Logs'
$started = Get-Date

$p = Start-Process $Exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" } 60
Start-Sleep 4
$main = [IntPtr]$w.Current.NativeWindowHandle
$results = New-Object System.Collections.Generic.List[object]

function Close-Dialogs {
    $closed = @()
    for ($i = 0; $i -lt 4; $i++) {
        $wins = @($AE::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id))) |
            ? { [IntPtr]$_.Current.NativeWindowHandle -ne $main })
        $wins += @($w.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
        if ($wins.Count -eq 0) { break }
        foreach ($d in $wins) {
            $title = $d.Current.Name; $closed += $title
            # WM_CLOSE means Cancel for file dialogs and for Unravel's own dialogs alike.
            try { [UWClose]::PostMessage([IntPtr]$d.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null } catch {}
        }
        Start-Sleep -Milliseconds 700
    }
    return $closed
}

foreach ($pg in $navNames) {
    if ($pg -eq 'Hunter mode') { continue }
    Select-Nav $w $pg | Out-Null; Start-Sleep 2
    $cond = New-Object System.Windows.Automation.OrCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)))
    $names = @($w.FindAll($TS::Descendants, $cond) | % { $_.Current.Name } | ? { $_ -and $_ -match $safe -and $navNames -notcontains $_ } | Select -Unique)
    foreach ($n in $names) {
        $el = $w.FindAll($TS::Descendants, $cond) | ? { $_.Current.Name -eq $n -and $_.Current.IsEnabled -and -not $_.Current.IsOffscreen } | Select -First 1
        if (-not $el) { continue }
        $ok = Invoke-El $el
        Start-Sleep -Milliseconds 1500
        $dialogs = Close-Dialogs
        $err = @($dialogs | ? { $_ -like '*went wrong*' -or $_ -like '*failed*' })
        $results.Add([pscustomobject]@{ Page = $pg; Button = $n; Invoked = [bool]$ok; Dialogs = ($dialogs -join '; '); Error = ($err -join '; ') })
        if ($p.HasExited) { break }
    }
    if ($p.HasExited) { $results.Add([pscustomobject]@{ Page = $pg; Button = '(app exited!)'; Invoked = $false; Dialogs = ''; Error = 'CRASH' }); break }
}
$alive = -not $p.HasExited
Stop-Process -Id $p.Id -Force -EA 0

$results | Format-Table -AutoSize | Out-String -Width 220
"buttons pressed: $($results.Count); error dialogs: $(@($results | ? Error).Count); app alive at end: $alive"
$logErrors = Get-ChildItem $logDir -Filter *.log -EA 0 | Get-Content | % { try { $_ | ConvertFrom-Json } catch {} } |
    ? { $_.Level -eq 'Error' -and $_.Area -eq 'Application' -and [datetime]$_.Time -ge $started }
"application errors logged during the sweep: $(@($logErrors).Count)"
$logErrors | % { "  $($_.Message)" }
