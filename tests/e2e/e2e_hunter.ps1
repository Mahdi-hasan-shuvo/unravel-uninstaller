# Hunter mode: drag the target onto Notepad with real mouse input and inspect the resulting menu.
$S = Join-Path $PSScriptRoot "out"; New-Item -ItemType Directory -Force $S | Out-Null
. (Join-Path $PSScriptRoot "ui.ps1")
$exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe")
function Wait-For([scriptblock]$probe, [int]$seconds = 20) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }; $null }

Get-Process Unravel -EA 0 | Stop-Process -Force
$started = Get-Date
$np = Start-Process notepad.exe -PassThru
$note = Wait-For { Get-Process notepad -EA 0 | ? { $_.MainWindowHandle -ne 0 -and $_.StartTime -ge $started.AddSeconds(-2) } | Select -First 1 }
"notepad pid: $($note.Id)"
$p = Start-Process $exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" }
Start-Sleep 3
Invoke-El (Find-ById $w 'HunterButton') | Out-Null
$h = Wait-For { Get-AppWindow $p.Id "*Hunter mode*" }
"hunter window: $($h.Current.Name)  main visible: $(-not $w.Current.IsOffscreen)"
$hr = $h.Current.BoundingRectangle
$nh = [IntPtr]$note.MainWindowHandle
[UW]::ShowWindow($nh, 9) | Out-Null; [UW]::SetForegroundWindow($nh) | Out-Null; Start-Sleep -Milliseconds 600
$r = New-Object UW+RECT; [UW]::GetWindowRect($nh, [ref]$r) | Out-Null
$sx = [int]($hr.X + $hr.Width / 2); $sy = [int]($hr.Y + $hr.Height / 2)
$tx = [int](($r.L + $r.R) / 2); $ty = [int](($r.T + $r.B) / 2)
[UW]::SetCursorPos($sx, $sy); Start-Sleep -Milliseconds 300
[UW]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero)   # left down
for ($i = 1; $i -le 25; $i++) { [UW]::SetCursorPos([int]($sx + ($tx - $sx) * $i / 25), [int]($sy + ($ty - $sy) * $i / 25)); Start-Sleep -Milliseconds 40 }
Start-Sleep -Milliseconds 500
[UW]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)   # left up
Start-Sleep -Milliseconds 900
# capture the area around the cursor (menu + notepad only)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 520, 480; $g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($tx - 20, $ty - 20, 0, 0, (New-Object System.Drawing.Size 520, 480)); $bmp.Save("$S\hunter_menu.png")
# read the menu through UI Automation
$mi = $AE::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))))
"menu items: " + (($mi | % { $_.Current.Name }) -join ' | ')
$cancel = $mi | ? { $_.Current.Name -eq 'Cancel' } | Select -First 1
if ($cancel) { Invoke-El $cancel | Out-Null; "cancelled menu" }
Start-Sleep 1
# right-click options and return to the main window
[UW]::SetCursorPos($sx, $sy); Start-Sleep -Milliseconds 200
[UW]::mouse_event(0x8, 0, 0, 0, [IntPtr]::Zero); [UW]::mouse_event(0x10, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 800
$opt = $AE::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))))
"options: " + (($opt | % { $_.Current.Name }) -join ' | ')
$open = $opt | ? { $_.Current.Name -eq 'Open main window' } | Select -First 1
Invoke-El $open | Out-Null; Start-Sleep 1
"main window back: $(-not $w.Current.IsOffscreen)  hunter closed: $(-not (Get-AppWindow $p.Id '*Hunter mode*'))"
Stop-Process -Id $note.Id -Force -EA 0
