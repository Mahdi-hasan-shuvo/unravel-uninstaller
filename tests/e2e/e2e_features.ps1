# End-to-end checks of the newer pages on the real system (all read-only except ending the test's own CPU burner).
$S = Join-Path $PSScriptRoot "out"; New-Item -ItemType Directory -Force $S | Out-Null
. (Join-Path $PSScriptRoot "ui.ps1")
$exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe")
function Wait-For([scriptblock]$probe, [int]$seconds = 20) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; $null }
function Texts($el) { $el.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | % { $_.Current.Name } }

Get-Process Unravel, cpuburner -EA 0 | Stop-Process -Force
$p = Start-Process $exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" }
Start-Sleep 4

# ---- Live monitor: sustained CPU and unsigned-from-temp alerts, then end the process from the alert
$burnDir = Join-Path $env:TEMP "unravel-burner"; New-Item -ItemType Directory -Force $burnDir | Out-Null
$burnExe = Join-Path $burnDir "cpuburner.exe"
& (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /out:"$burnExe" (Join-Path $PSScriptRoot 'CpuBurner.cs') | Out-Null
Select-Nav $w "Live monitor" | Out-Null
$burner = Start-Process $burnExe -ArgumentList 120 -PassThru -WindowStyle Hidden
"burner pid $($burner.Id)"
$alert = Wait-For { (Texts $w) | ? { $_ -like 'cpuburner.exe is using*' } } 60
"cpu alert: $alert"
$temp = (Texts $w) | ? { $_ -like 'Unsigned program running from*' }
"location alert: $temp"
Save-Window $w "$S\monitor_alert.png"
$end = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'End process'))) | Select -First 1
Invoke-El $end | Out-Null
$dlg = Wait-For { Get-AppWindow $p.Id 'End process' } 10
Invoke-El (Find-ByName $dlg 'End process') | Out-Null
Start-Sleep 2
"burner ended by Unravel: $($burner.HasExited)"
$cleared = Wait-For { if (-not ((Texts $w) | ? { $_ -like 'cpuburner.exe*' })) { $true } } 10
"alerts cleared after the process ended: $([bool]$cleared)"
Remove-Item $burnDir -Recurse -Force -EA 0

# ---- Live monitor: the critical-process guard
(Find-ById $w 'FilterBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('lsass')
Start-Sleep 2
$row = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)))
Invoke-El $row | Out-Null
"lsass listed: $([bool]$row)"
(Find-ById $w 'FilterBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')

# ---- Duplicates (read-only scan of the default user folders)
Select-Nav $w "Duplicates & orphans" | Out-Null; Start-Sleep 1
$t0 = Get-Date
Invoke-El (Find-ById $w 'DupScan') | Out-Null
$sum = Wait-For { $x = (Find-ById $w 'DupSummary').Current.Name; if ($x -notlike 'Choose folders*' -and (Find-ById $w 'DupScan').Current.IsEnabled) { $x } } 600
"duplicates ($([int]((Get-Date)-$t0).TotalSeconds) s): $sum"
Save-Window $w "$S\duplicates.png"

# ---- Orphans (read-only)
Invoke-El (Find-ByName $w 'Orphaned files') | Out-Null; Start-Sleep 1
$t0 = Get-Date
Invoke-El (Find-ById $w 'OrphanScan') | Out-Null
$os = Wait-For { $x = (Find-ById $w 'OrphanSummary').Current.Name; if ($x -notlike 'Looks in*' -and (Find-ById $w 'OrphanScan').Current.IsEnabled) { $x } } 600
"orphans ($([int]((Get-Date)-$t0).TotalSeconds) s): $os"
"remove button enabled with nothing checked: $((Find-ById $w 'OrphanDelete').Current.IsEnabled)"
Save-Window $w "$S\orphans.png"

# ---- Activity log shows what happened
Select-Nav $w "Activity & recovery" | Out-Null; Start-Sleep 2
$log = (Texts $w) | ? { $_ -like 'Ended cpuburner.exe*' -or $_ -like 'Scanned * files*' -or $_ -like 'Orphan scan found*' }
"activity log entries: $($log -join ' | ')"
Save-Window $w "$S\recovery.png"
Stop-Process -Id $p.Id -Force
