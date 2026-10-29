# Installs a harmless per-user demo program whose uninstaller leaves typical leftovers behind.
$ErrorActionPreference = 'Stop'
$name = 'Contoso Demo App'
$publisher = 'Contoso Test Labs'
$dir = Join-Path $env:LOCALAPPDATA "Programs\Contoso Demo"
New-Item -ItemType Directory -Force $dir | Out-Null
# Build a small windowed program with the .NET Framework compiler that ships with Windows.
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:"$(Join-Path $dir 'contosodemo.exe')" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'DemoApp.cs') | Out-Null
if (-not (Test-Path (Join-Path $dir 'contosodemo.exe'))) { throw "Could not build the demo program." }
Set-Content (Join-Path $dir 'readme.txt') 'Demo program for Unravel end-to-end tests.' -Encoding ascii

# The uninstaller removes only the install folder and its own registration (like many real ones).
$uninst = Join-Path $dir 'uninstall.cmd'
@"
@echo off
echo Uninstalling $name ...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoDemo" /f >nul 2>&1
cd /d "%TEMP%"
(goto) 2>nul & rd /s /q "$dir"
"@ | Set-Content $uninst -Encoding ascii

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoDemo'
New-Item -Force $key | Out-Null
Set-ItemProperty $key DisplayName "$name 1.0"
Set-ItemProperty $key DisplayVersion '1.0.0'
Set-ItemProperty $key Publisher $publisher
Set-ItemProperty $key InstallLocation $dir
Set-ItemProperty $key DisplayIcon (Join-Path $dir 'contosodemo.exe')
Set-ItemProperty $key UninstallString "`"$uninst`""
Set-ItemProperty $key EstimatedSize 120 -Type DWord

# Leftovers the uninstaller does not clean up:
New-Item -Force "HKCU:\Software\$publisher\$name" | Out-Null
Set-ItemProperty "HKCU:\Software\$publisher\$name" LastFile 'C:\demo.txt'
New-Item -Force "HKCU:\Software\$name" | Out-Null
Set-ItemProperty "HKCU:\Software\$name" WindowPos '10,10'
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' ContosoDemoTray -EA 0
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' ContosoDemoTray "`"$(Join-Path $dir 'contosodemo.exe')`" --tray"
New-Item -ItemType Directory -Force (Join-Path $env:APPDATA $name) | Out-Null
Set-Content (Join-Path $env:APPDATA "$name\config.ini") '[ui]' -Encoding ascii
New-Item -ItemType Directory -Force (Join-Path $env:LOCALAPPDATA "$publisher\$name") | Out-Null
Set-Content (Join-Path $env:LOCALAPPDATA "$publisher\$name\cache.bin") 'cache' -Encoding ascii
$sm = Join-Path ([Environment]::GetFolderPath('Programs')) $name
New-Item -ItemType Directory -Force $sm | Out-Null
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path $sm "$name.lnk")); $lnk.TargetPath = (Join-Path $dir 'contosodemo.exe'); $lnk.Save()
"installed"
