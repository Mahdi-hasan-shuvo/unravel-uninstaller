# Package manager (install + uninstall-with-scan of jqlang.jq via the UI) and Performance baselines.
$S = Join-Path $PSScriptRoot "out"; New-Item -ItemType Directory -Force $S | Out-Null
. (Join-Path $PSScriptRoot "ui.ps1")
$exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe")
function Wait-For([scriptblock]$probe, [int]$seconds = 20) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; $null }
function Click-Dialog($procId, $title, $button) {
    $dlg = Wait-For { Get-AppWindow $procId $title } 30
    if (-not $dlg) { "  dialog '$title' NOT shown"; return }
    Invoke-El (Find-ByName $dlg $button) | Out-Null; "  dialog '$title' -> $button"; Start-Sleep -Milliseconds 700
}
function Rows($el) { $el.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))) }

Get-Process Unravel -EA 0 | Stop-Process -Force
$p = Start-Process $exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" }
Start-Sleep 4
Select-Nav $w "Package manager" | Out-Null
"installed list loaded: $([bool](Wait-For { if ((Rows $w).Count -gt 5) { $true } } 90))"

Invoke-El (Find-ById $w 'TabSearch') | Out-Null; Start-Sleep 1
(Find-ById $w 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('jqlang.jq')
Invoke-El (Find-ByName $w 'Search') | Out-Null
$row = Wait-For { (Rows $w) | Select -First 1 } 60
Invoke-El $row | Out-Null
"search result selected: $([bool]$row)"
Invoke-El (Find-ByName $w 'Install') | Out-Null
Click-Dialog $p.Id 'Install' 'Install'
$status = Wait-For { $t = (Find-ById $w 'ConsoleTitle').Current.Name; if ($t -match 'done|failed|cancelled') { $t } } 300
"install: $status"
Save-Window $w "$S\winget_install.png"
"winget sees jq: " + [bool](winget list --id jqlang.jq --exact --disable-interactivity 2>$null | Select-String 'jqlang.jq')

Invoke-El (Find-ById $w 'TabInstalled') | Out-Null; Start-Sleep 1
Invoke-El (Find-ByName $w 'Refresh') | Out-Null
Wait-For { if ((Rows $w).Count -gt 5) { $true } } 90 | Out-Null
(Find-ById $w 'FilterBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('jqlang')
Start-Sleep 1
$jq = (Rows $w) | Select -First 1
Invoke-El $jq | Out-Null
Invoke-El (Find-ByName $w "Uninstall$([char]0x2026)") | Out-Null
Click-Dialog $p.Id 'Uninstall' 'Uninstall'
$wiz = Wait-For { Get-AppWindow $p.Id 'Uninstall *' } 30
"wizard: $($wiz.Current.Name)"
$next = Find-ById $wiz 'NextButton'
"winget uninstall finished: $([bool](Wait-For { if ($next.Current.IsEnabled) { $true } } 180))"
Save-Window $wiz "$S\winget_wizard.png"
Invoke-El (Find-ById $wiz 'ModeModerate') | Out-Null; Invoke-El $next | Out-Null
Start-Sleep 6
$doneText = (Find-ById $wiz 'DoneText').Current.Name
$regTitle = (Find-ById $wiz 'RegistryTitle').Current.Name
"after scan: registry='$regTitle' done='$doneText'"
Save-Window $wiz "$S\winget_scan.png"
Invoke-El (Find-ById $wiz 'CloseButton') | Out-Null
Click-Dialog $p.Id 'Leftovers remain' 'Close'
"winget still sees jq: " + [bool](winget list --id jqlang.jq --exact --disable-interactivity 2>$null | Select-String 'jqlang.jq')

# ---- Performance: two baselines and a comparison
Select-Nav $w "Performance" | Out-Null; Start-Sleep 3
Invoke-El (Find-ById $w 'TabBaselines') | Out-Null; Start-Sleep 1
foreach ($label in 'E2E before', 'E2E after') {
    (Find-ById $w 'BaselineLabel').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($label)
    Invoke-El (Find-ById $w 'CaptureButton') | Out-Null
    $status = Wait-For { $t = (Find-ById $w 'CaptureStatus').Current.Name; if ($t -like "Saved*$label*") { $t } } 90
    "baseline: $status"
}
$items = (Find-ById $w 'BaselineList').FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($i in $items | Select -First 2) { $sp = $null; if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.AddToSelection() } }
Invoke-El (Find-ByName $w 'Compare selected two') | Out-Null; Start-Sleep 1
"comparison rows: $((Rows (Find-ById $w 'CompareGrid')).Count)"
Save-Window $w "$S\baselines.png"
Stop-Process -Id $p.Id -Force
