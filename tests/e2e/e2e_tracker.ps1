# Install tracker end-to-end: snapshot -> install the demo -> compare -> remove only the demo's items -> undo.
$S = Join-Path $PSScriptRoot "out"; New-Item -ItemType Directory -Force $S | Out-Null
. (Join-Path $PSScriptRoot "ui.ps1")
$exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe")
function Wait-For([scriptblock]$probe, [int]$seconds = 20) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; $null }
function Click-Dialog($procId, $title, $button) {
    $dlg = Wait-For { Get-AppWindow $procId $title } 30
    if (-not $dlg) { "  dialog '$title' NOT shown"; return $null }
    Invoke-El (Find-ByName $dlg $button) | Out-Null; "  dialog '$title' -> $button"; Start-Sleep -Milliseconds 700
}

Get-Process Unravel -EA 0 | Stop-Process -Force
$p = Start-Process $exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" }
Start-Sleep 4
Select-Nav $w "Install tracker" | Out-Null; Start-Sleep 2
(Find-ById $w 'ProgramName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Contoso Demo App')
$t0 = Get-Date
Invoke-El (Find-ById $w 'BeginButton') | Out-Null
$done = Wait-For { $b = Find-ById $w 'CompleteButton'; if ($b -and $b.Current.IsEnabled -and -not $b.Current.IsOffscreen) { $b } } 300
"before-snapshot: $([bool]$done) in $([int]((Get-Date) - $t0).TotalSeconds) s"

& (Join-Path $PSScriptRoot 'demo_install.ps1') | Out-Null
"demo installed"

$t0 = Get-Date
Invoke-El (Find-ById $w 'CompleteButton') | Out-Null
$title = Wait-For { $x = Find-ById $w 'ResultTitle'; if ($x.Current.Name -eq 'Contoso Demo App') { $x } } 300
"after-snapshot + compare: $([bool]$title) in $([int]((Get-Date) - $t0).TotalSeconds) s"
$chips = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) |
    % { $_.Current.Name } | ? { $_ -match '^\d+ (added|modified|removed)$' }
"changes: $($chips -join ', ')"
Save-Window $w "$S\tracker_result.png"

Invoke-El (Find-ById $w 'CleanButton') | Out-Null
$dlg = Wait-For { Get-AppWindow $p.Id 'Remove what Contoso*' } 30
$boxes = $dlg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
$kept = 0; $unticked = @()
foreach ($b in $boxes) {
    $tp = $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $isDemo = $b.Current.Name -match 'Contoso'
    $on = $tp.Current.ToggleState -eq 'On'
    if ($isDemo -and -not $on) { $tp.Toggle() }
    if (-not $isDemo -and $on) { $tp.Toggle(); $unticked += $b.Current.Name }
    if ($isDemo) { $kept++ }
}
"preview: $($boxes.Count) item(s); $kept demo item(s) checked; unticked $($unticked.Count) unrelated item(s)"
$unticked | Select -First 8 | % { "   unrelated: $_" }
Save-Window $dlg "$S\tracker_preview.png"
Invoke-El (Find-ByName $dlg 'Remove checked') | Out-Null
Click-Dialog $p.Id 'Removed' 'OK'
"--- after cleanup:"; & (Join-Path $PSScriptRoot 'demo_check.ps1')

Invoke-El (Find-ById $w 'UndoButton') | Out-Null
Click-Dialog $p.Id 'Undo cleanup' 'Restore'
Click-Dialog $p.Id 'Restored' 'OK'
"--- after undo:"; & (Join-Path $PSScriptRoot 'demo_check.ps1')
Stop-Process -Id $p.Id -Force
