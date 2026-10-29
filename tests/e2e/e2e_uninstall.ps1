# Drives Unravel's real UI through a full uninstall of the demo program and records each step.
param([string]$Mode = 'ModeModerate', [switch]$ScanAgainAfterVerify)
$S = Join-Path $PSScriptRoot "out"; New-Item -ItemType Directory -Force $S | Out-Null
. (Join-Path $PSScriptRoot "ui.ps1")
$exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe")
$log = New-Object System.Collections.Generic.List[string]
function Step($m) { $log.Add("$(Get-Date -f HH:mm:ss) $m"); Write-Output $m }

function Wait-For([scriptblock]$probe, [int]$seconds = 30) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }
    return $null
}
function Click-Dialog($procId, $title, $button) {
    $dlg = Wait-For { Get-AppWindow $procId $title } 15
    if (-not $dlg) { Step "  dialog '$title' NOT shown"; return $false }
    $b = Find-ByName $dlg $button
    Invoke-El $b | Out-Null; Step "  dialog '$title' -> $button"; Start-Sleep -Milliseconds 600; return $true
}

Get-Process Unravel -EA 0 | Stop-Process -Force
# Use test settings (permanent delete instead of Recycle Bin) and restore the user's settings afterwards.
$settings = Join-Path $env:LOCALAPPDATA 'Unravel\settings.json'
$backup = "$settings.e2e-backup"
New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
if (Test-Path $settings) { Copy-Item $settings $backup -Force }
'{ "Uninstaller": { "DeleteFilesToRecycleBin": false } }' | Set-Content $settings -Encoding ascii
$p = Start-Process $exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" } 20
Start-Sleep 4
$search = Find-ById $w 'SearchBox'
$vp = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue('Contoso Demo')
Start-Sleep 1
$row = Wait-For { $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))) } 10
Invoke-El $row | Out-Null; Step "selected row"
Start-Sleep 1
Save-Window $w "$S\e2e_0_selected.png"
Invoke-El (Find-ById $w 'UninstallButton') | Out-Null; Step "clicked Uninstall"
Click-Dialog $p.Id 'Uninstall program' 'Uninstall' | Out-Null

$wiz = Wait-For { Get-AppWindow $p.Id 'Uninstall Contoso*' } 15
if (-not $wiz) { Step "wizard NOT shown"; $log; return }
Step "wizard opened"
$next = Find-ById $wiz 'NextButton'
$ok = Wait-For { if ($next.Current.IsEnabled) { $true } } 60
Step "uninstaller finished, scan enabled: $ok"
Save-Window $wiz "$S\e2e_1_uninstalled.png"
Invoke-El (Find-ById $wiz $Mode) | Out-Null
Invoke-El $next | Out-Null; Step "scan started ($Mode)"
$reg = Wait-For { $t = Find-ById $wiz 'RegistryTitle'; if ($t -and -not $t.Current.IsOffscreen -and $t.Current.Name -like 'Leftover*') { $t } } 60
Step "registry step: $($reg.Current.Name)"
Save-Window $wiz "$S\e2e_2_registry.png"
Invoke-El (Find-ById $wiz 'RegSelectAllButton') | Out-Null
Start-Sleep -Milliseconds 500
Invoke-El (Find-ById $wiz 'RegDeleteButton') | Out-Null
Click-Dialog $p.Id 'Delete registry items' 'Delete' | Out-Null
Start-Sleep 2
$files = Wait-For { $t = Find-ById $wiz 'FilesTitle'; if ($t -and -not $t.Current.IsOffscreen -and $t.Current.Name -like 'Leftover*') { $t } } 20
if (-not $files) { Invoke-El (Find-ById $wiz 'NextButton') | Out-Null; $files = Wait-For { $t = Find-ById $wiz 'FilesTitle'; if ($t -and -not $t.Current.IsOffscreen) { $t } } 10 }
Step "files step: $($files.Current.Name)"
Save-Window $wiz "$S\e2e_3_files.png"
Invoke-El (Find-ById $wiz 'FileSelectAllButton') | Out-Null
Start-Sleep -Milliseconds 500
Save-Window $wiz "$S\e2e_3b_files_checked.png"
Invoke-El (Find-ById $wiz 'FileDeleteButton') | Out-Null
Click-Dialog $p.Id 'Delete files and folders' 'Delete' | Out-Null
Start-Sleep 2
# any error dialog?
$err = Get-AppWindow $p.Id '*could not be deleted*'
if ($err) { Step "ERROR DIALOG: $($err.Current.Name)"; Save-Window $err "$S\e2e_err.png"; Invoke-El (Find-ByName $err 'OK') | Out-Null }
Invoke-El (Find-ById $wiz 'NextButton') | Out-Null
Start-Sleep 1
Save-Window $wiz "$S\e2e_4_done.png"
$doneText = Find-ById $wiz 'DoneText'
Step "done: $($doneText.Current.Name)"
# Post-uninstall verification runs in the background on the last step.
$verdict = Wait-For { $v = (Find-ById $wiz 'VerdictText').Current.Name; if ($v -and $v -ne ([string][char]0x2026).Insert(0,'Verifying')) { $v } } 60
Step "verification: $verdict | $((Find-ById $wiz 'VerifySummary').Current.Name) | title: $((Find-ById $wiz 'DoneTitle').Current.Name)"
Save-Window $wiz "$S\e2e_5_verified.png"
if ($ScanAgainAfterVerify -and $verdict -like '*still present*') {
    # Verification found items; "Scan again" re-runs the leftover scan so they can be reviewed and removed.
    Invoke-El (Find-ById $wiz 'ScanAgainButton') | Out-Null; Step "scan again"
    $reg = Wait-For { $t = Find-ById $wiz 'RegistryTitle'; if ($t -and -not $t.Current.IsOffscreen -and $t.Current.Name -like 'Leftover*') { $t } } 60
    if ($reg) {
        Step "  registry step: $($reg.Current.Name)"
        Invoke-El (Find-ById $wiz 'RegSelectAllButton') | Out-Null; Start-Sleep -Milliseconds 500
        Invoke-El (Find-ById $wiz 'RegDeleteButton') | Out-Null
        Click-Dialog $p.Id 'Delete registry items' 'Delete' | Out-Null
        Start-Sleep 2
    }
    $files = Wait-For { $t = Find-ById $wiz 'FilesTitle'; if ($t -and -not $t.Current.IsOffscreen -and $t.Current.Name -like 'Leftover*') { $t } } 20
    if ($files) {
        Step "  files step: $($files.Current.Name)"
        Invoke-El (Find-ById $wiz 'FileSelectAllButton') | Out-Null; Start-Sleep -Milliseconds 500
        Invoke-El (Find-ById $wiz 'FileDeleteButton') | Out-Null
        Click-Dialog $p.Id 'Delete files and folders' 'Delete' | Out-Null
        Start-Sleep 2
        Invoke-El (Find-ById $wiz 'NextButton') | Out-Null
    }
    Start-Sleep 1
    $verdict = Wait-For { $v = (Find-ById $wiz 'VerdictText').Current.Name; if ($v -and $v -ne ([string][char]0x2026).Insert(0,'Verifying')) { $v } } 60
    Step "verification after scan again: $verdict | $((Find-ById $wiz 'VerifySummary').Current.Name)"
    Save-Window $wiz "$S\e2e_6_reverified.png"
}
Invoke-El (Find-ById $wiz 'CloseButton') | Out-Null
Start-Sleep 2
Step "wizard closed: $(-not (Get-AppWindow $p.Id 'Uninstall Contoso*'))"
$log | Set-Content "$S\e2e_log.txt"
Stop-Process -Id $p.Id -Force -EA 0
if (Test-Path $backup) { Move-Item $backup $settings -Force } else { Remove-Item $settings -EA 0 }
