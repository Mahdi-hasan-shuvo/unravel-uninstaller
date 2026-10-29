# Startup manager: toggle an entry's switch through the UI and verify Windows' StartupApproved flag.
$S = Join-Path $PSScriptRoot "out"; New-Item -ItemType Directory -Force $S | Out-Null
. (Join-Path $PSScriptRoot "ui.ps1")
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
Set-ItemProperty $run UnravelUiTest "`"$env:SystemRoot\System32\PING.EXE`" -n 1 127.0.0.1"
Remove-ItemProperty $approved UnravelUiTest -EA 0
function Flag { $v = (Get-ItemProperty $approved -Name UnravelUiTest -EA 0).UnravelUiTest; if ($null -eq $v) { 'none (enabled)' } else { '0x{0:x2} ({1})' -f $v[0], $(if ($v[0] -band 1) { 'disabled' } else { 'enabled' }) } }
"flag before: $(Flag)"
Get-Process Unravel -EA 0 | Stop-Process -Force
$p = Start-Process (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe") -PassThru; Start-Sleep 6
$w = Get-AppWindow $p.Id "Unravel*"
Select-Nav $w "Startup manager" | Out-Null; Start-Sleep 5
$vp = (Find-ById $w 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue('UnravelUiTest'); Start-Sleep 1
$cb = Find-ByName $w 'Start UnravelUiTest with Windows'
"switch found: $([bool]$cb)  state: $($cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)"
Save-Window $w "$S\e2e_startup_before.png"
$cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1
"flag after switching off: $(Flag)"
$cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1
"flag after switching on:  $(Flag)"
Stop-Process -Id $p.Id -Force
Remove-ItemProperty $run UnravelUiTest -EA 0; Remove-ItemProperty $approved UnravelUiTest -EA 0
"cleaned up"
