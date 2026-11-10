# Real Windows Installer test with a harmless per-user MSI (build: wix build MsiDemo.wxs -o MsiDemo.msi; needs a file MsiDemo.txt).
# 1. The registered "MsiExec /I{code}" command does not uninstall (it reconfigures the product).
# 2. Unravel lists the per-user product (it has no Uninstall key) and its wizard runs "MsiExec /X{code}".
# 3. Answering No / Yes in Windows Installer's own confirmation is reported correctly (activity log), and Yes removes the product.
# Windows Installer's dialog only reacts to real input, so it is pressed with the mouse; keep the desktop free while this runs.
param([Parameter(Mandatory)][string]$Msi,
      [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe"))
. (Join-Path $PSScriptRoot "ui.ps1")
function Wait-For([scriptblock]$probe, [int]$seconds = 30) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }; $null }
$code = '{7A1D2F60-8B3C-4E55-A1F0-2C9E4B7D3F22}'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
# Per-user MSI products have no Uninstall key; this is Windows Installer's record of them.
$key = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products\06F2D1A7C3B855E41A0FC2E9B4D7F322\InstallProperties"
$log = Join-Path $env:LOCALAPPDATA "Unravel\Logs\unravel-$(Get-Date -f yyyyMMdd).log"
function Msi-Run([string]$a) { (Start-Process msiexec -ArgumentList $a -Wait -PassThru).ExitCode }
function LastWizardLog { Get-Content $log -Encoding UTF8 -EA 0 | % { $_ | ConvertFrom-Json } | ? { $_.Message -like 'Ran the uninstaller of Unravel MSI Demo*' } | Select -Last 1 | % { "$($_.Time) [$($_.Level)] $($_.Message)" } }

function Uninstall-Through-Wizard([string]$answer) {
    $p = Start-Process $Exe -PassThru
    $w = Wait-For { Get-AppWindow $p.Id "Unravel*" } 60
    Start-Sleep 5
    Invoke-El (Find-ById $w 'ViewDetails') | Out-Null
    (Find-ById $w 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Unravel MSI Demo')
    $listed = Wait-For {
        $row = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)))
        if ($row) { Invoke-El $row | Out-Null }
        Start-Sleep -Milliseconds 400
        if ((Find-ById $w 'UninstallButton').Current.IsEnabled) { $true }
    } 30
    "  listed in Programs: $([bool]$listed)"
    $before = LastWizardLog
    Invoke-El (Find-ById $w 'UninstallButton') | Out-Null
    $c = Wait-For { Get-AppWindow $p.Id 'Uninstall program' } 15; Invoke-El (Find-ByName $c 'Uninstall') | Out-Null
    $wiz = Wait-For { Get-AppWindow $p.Id 'Uninstall Unravel MSI Demo*' } 15
    $client = Wait-For { Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | ? { $_.CommandLine -match '7A1D2F60' } | Select -Last 1 } 30
    "  wizard ran: $($client.CommandLine)"
    $dlg = Wait-For { foreach ($m in Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | ? { $_.CommandLine -match '7A1D2F60' }) {
        $d = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$m.ProcessId))); if ($d -and (Find-ByName $d $answer)) { return $d } } } 60
    Start-Sleep 3
    [UW]::ShowWindow([IntPtr]$wiz.Current.NativeWindowHandle, 6) | Out-Null; [UW]::ShowWindow([IntPtr]$w.Current.NativeWindowHandle, 6) | Out-Null
    Start-Sleep 1
    $r = (Find-ByName $dlg $answer).Current.BoundingRectangle
    [UW]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null; Start-Sleep -Milliseconds 200
    [UW]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 80; [UW]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
    "  pressed $answer in Windows Installer's confirmation"
    $after = Wait-For { $l = LastWizardLog; if ($l -and $l -ne $before) { $l } } 180
    "  wizard reported: $after"
    Stop-Process -Id $p.Id -Force -EA 0
    Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | ? { $_.CommandLine -match '7A1D2F60' } | % { Stop-Process -Id $_.ProcessId -Force }
}

$installArgs = '/i "' + $Msi + '" /qn'
"--- install: exit $(Msi-Run $installArgs)  registered=$(Test-Path $key)"
if (-not (Test-Path $key)) { "install failed; stopping"; return }
"--- the registered command, as Unravel ran it before 1.2: $((Get-ItemProperty $key).UninstallString)"
"    exit $(Msi-Run "/I$code /qn")  still registered=$(Test-Path $key)"
"--- wizard, answer No"
Uninstall-Through-Wizard 'No'
"    still registered=$(Test-Path $key)"
"--- wizard, answer Yes"
Uninstall-Through-Wizard 'Yes'
"    still registered=$(Test-Path $key)  file=$(Test-Path (Join-Path $env:LOCALAPPDATA 'UnravelMsiDemo\MsiDemo.txt'))"
if (Test-Path $key) { "cleanup: exit $(Msi-Run "/x$code /qn")" }
reg delete "HKCU\Software\Contoso Test Labs" /f 2>$null | Out-Null
