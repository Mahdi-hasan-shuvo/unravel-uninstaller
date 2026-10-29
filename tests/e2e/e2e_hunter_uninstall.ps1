# Hunter mode end-to-end: drag the target onto the running demo program, stop it auto-starting, then uninstall it.
$S = Join-Path $PSScriptRoot "out"; New-Item -ItemType Directory -Force $S | Out-Null
. (Join-Path $PSScriptRoot "ui.ps1")
$exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe")
function Wait-For([scriptblock]$probe, [int]$seconds = 20) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }; $null }
function Click-Dialog($procId, $title, $button) {
    $dlg = Wait-For { Get-AppWindow $procId $title } 15
    if (-not $dlg) { "  dialog '$title' NOT shown"; return }
    Invoke-El (Find-ByName $dlg $button) | Out-Null; "  dialog '$title' -> $button"; Start-Sleep -Milliseconds 600
}
function Menu-Item($procId, $name) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
    Wait-For { $AE::RootElement.FindFirst($TS::Descendants, $cond) } 5
}
function Drag-To($hunter, $targetHwnd) {
    $hr = $hunter.Current.BoundingRectangle
    $r = New-Object UW+RECT; [UW]::GetWindowRect($targetHwnd, [ref]$r) | Out-Null
    $sx = [int]($hr.X + $hr.Width / 2); $sy = [int]($hr.Y + $hr.Height / 2)
    $tx = [int](($r.L + $r.R) / 2); $ty = [int](($r.T + $r.B) / 2)
    [UW]::SetCursorPos($sx, $sy) | Out-Null; Start-Sleep -Milliseconds 300
    [UW]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero)
    for ($i = 1; $i -le 25; $i++) { [UW]::SetCursorPos([int]($sx + ($tx - $sx) * $i / 25), [int]($sy + ($ty - $sy) * $i / 25)) | Out-Null; Start-Sleep -Milliseconds 40 }
    Start-Sleep -Milliseconds 500
    [UW]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 900
}
$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
function Flag { $v = (Get-ItemProperty $approved -Name ContosoDemoTray -EA 0).ContosoDemoTray; if ($null -eq $v) { 'none' } else { '0x{0:x2}' -f $v[0] } }

Get-Process Unravel, contosodemo -EA 0 | Stop-Process -Force
& (Join-Path $PSScriptRoot 'demo_install.ps1') | Out-Null
$settings = Join-Path $env:LOCALAPPDATA 'Unravel\settings.json'; $backup = "$settings.e2e-backup"
New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
if (Test-Path $settings) { Copy-Item $settings $backup -Force }
'{ "Uninstaller": { "DeleteFilesToRecycleBin": false } }' | Set-Content $settings -Encoding ascii

$demo = Start-Process (Join-Path $env:LOCALAPPDATA 'Programs\Contoso Demo\contosodemo.exe') -PassThru
$demoWin = Wait-For { $demo.Refresh(); if ($demo.MainWindowHandle -ne 0) { $demo.MainWindowHandle } }
"demo window: $([bool]$demoWin)  startup flag before: $(Flag)"
$p = Start-Process $exe -ArgumentList '--hunter' -PassThru
$h = Wait-For { Get-AppWindow $p.Id '*Hunter mode*' }
"hunter started directly: $([bool]$h)"

# 1) Stop starting with Windows
[UW]::SetForegroundWindow([IntPtr]$demoWin) | Out-Null; Start-Sleep -Milliseconds 400
Drag-To $h ([IntPtr]$demoWin)
$header = Menu-Item $p.Id 'Contoso Demo App  (contosodemo.exe)'
"menu header: $($header.Current.Name)"
Invoke-El (Menu-Item $p.Id 'Stop starting with Windows') | Out-Null
Click-Dialog $p.Id 'Done' 'OK'
"startup flag after: $(Flag)"

# 2) Uninstall from Hunter mode (the program is still running)
[UW]::SetForegroundWindow([IntPtr]$demoWin) | Out-Null; Start-Sleep -Milliseconds 400
Drag-To $h ([IntPtr]$demoWin)
Invoke-El (Menu-Item $p.Id "Uninstall$([char]0x2026)") | Out-Null
Click-Dialog $p.Id 'Uninstall program' 'Uninstall'
$wiz = Wait-For { Get-AppWindow $p.Id 'Uninstall Contoso*' } 15
"wizard: $([bool]$wiz)"
Click-Dialog $p.Id 'Close running programs?' 'Close them'
$next = Find-ById $wiz 'NextButton'
"scan enabled: $([bool](Wait-For { if ($next.Current.IsEnabled) { $true } } 60))"
Invoke-El (Find-ById $wiz 'ModeModerate') | Out-Null; Invoke-El $next | Out-Null
$reg = Wait-For { $t = Find-ById $wiz 'RegistryTitle'; if ($t -and $t.Current.Name -like 'Leftover*') { $t } } 60
"registry step: $($reg.Current.Name)"
Save-Window $wiz "$S\hunter_wizard_registry.png"
Invoke-El (Find-ById $wiz 'RegSelectAllButton') | Out-Null; Start-Sleep -Milliseconds 400
Invoke-El (Find-ById $wiz 'RegDeleteButton') | Out-Null
Click-Dialog $p.Id 'Delete registry items' 'Delete'
$files = Wait-For { $t = Find-ById $wiz 'FilesTitle'; if ($t -and $t.Current.Name -like 'Leftover*') { $t } } 20
"files step: $($files.Current.Name)"
Invoke-El (Find-ById $wiz 'FileSelectAllButton') | Out-Null; Start-Sleep -Milliseconds 400
Invoke-El (Find-ById $wiz 'FileDeleteButton') | Out-Null
Click-Dialog $p.Id 'Delete files and folders' 'Delete'
Invoke-El (Find-ById $wiz 'NextButton') | Out-Null; Start-Sleep 1
"done: $((Find-ById $wiz 'DoneText').Current.Name)"
Invoke-El (Find-ById $wiz 'CloseButton') | Out-Null
"demo process still running: $(-not $demo.HasExited)   startup flag at end: $(Flag)"
Stop-Process -Id $p.Id -Force -EA 0
if (Test-Path $backup) { Move-Item $backup $settings -Force } else { Remove-Item $settings -EA 0 }
& (Join-Path $PSScriptRoot 'demo_check.ps1')
