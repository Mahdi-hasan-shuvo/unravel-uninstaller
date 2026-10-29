# Reports which pieces of the demo program still exist.
$name = 'Contoso Demo App'; $publisher = 'Contoso Test Labs'
$checks = [ordered]@{
  'Uninstall key'          = Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoDemo'
  'Install folder'         = Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\Contoso Demo')
  'HKCU\Software\Publisher\App' = Test-Path "HKCU:\Software\$publisher\$name"
  'HKCU\Software\Publisher'     = Test-Path "HKCU:\Software\$publisher"
  'HKCU\Software\App'      = Test-Path "HKCU:\Software\$name"
  'Run value'              = $null -ne (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ContosoDemoTray -EA 0)
  'AppData folder'         = Test-Path (Join-Path $env:APPDATA $name)
  'LocalAppData publisher folder' = Test-Path (Join-Path $env:LOCALAPPDATA $publisher)
  'Start menu folder'      = Test-Path (Join-Path ([Environment]::GetFolderPath('Programs')) $name)
}
$checks.GetEnumerator() | % { "{0,-32} {1}" -f $_.Key, $(if ($_.Value) { 'PRESENT' } else { 'gone' }) }
