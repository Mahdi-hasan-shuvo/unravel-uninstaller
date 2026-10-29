# Records the README demo video and screenshots from the real application.
# Installs the harmless demo program, uninstalls it through Unravel's wizard while recording the screen,
# then tours the main pages. Needs ffmpeg on PATH. Output: docs/media.
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\Unravel.App\bin\Debug\net8.0-windows10.0.19041.0\Unravel.exe"),
    [string]$Out = (Join-Path $PSScriptRoot "..\..\docs\media"),
    [int]$X = 160, [int]$Y = 80
)
. (Join-Path $PSScriptRoot "ui.ps1")
Add-Type @"
using System; using System.Runtime.InteropServices;
public class UWMove { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint); }
"@
$Shots = Join-Path $Out "screenshots"
New-Item -ItemType Directory -Force $Shots | Out-Null
$VW = 1280; $VH = 820

function Wait-For([scriptblock]$probe, [int]$seconds = 30) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }
    return $null
}
function Press-Dialog($procId, $title, $button, [int]$pause = 0) {
    $dlg = Wait-For { Get-AppWindow $procId $title } 20
    if (-not $dlg) { "  dialog '$title' not shown"; return $null }
    if ($pause) { Start-Sleep -Milliseconds $pause }
    Invoke-El (Find-ByName $dlg $button) | Out-Null
    Start-Sleep -Milliseconds 700
    return $dlg
}
function Shot($el, $name) { Save-Window $el (Join-Path $Shots "$name.png"); "  saved $name.png" }
function Type-Slowly($box, [string]$text) {
    $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    for ($i = 1; $i -le $text.Length; $i++) { $vp.SetValue($text.Substring(0, $i)); Start-Sleep -Milliseconds 120 }
}

# ---- Demo data: the demo program and a few duplicate files (removed again at the end)
& (Join-Path $PSScriptRoot "demo_install.ps1") | Out-Null
$dupDir = Join-Path $env:USERPROFILE "Downloads\Unravel demo files"
New-Item -ItemType Directory -Force (Join-Path $dupDir "Backup") | Out-Null
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
function New-RandomFile($path, [int]$kb) { $b = New-Object byte[] ($kb * 1024); $rng.GetBytes($b); [IO.File]::WriteAllBytes($path, $b) }
New-RandomFile (Join-Path $dupDir "Holiday video.mp4") 9000
Copy-Item (Join-Path $dupDir "Holiday video.mp4") (Join-Path $dupDir "Backup\Holiday video.mp4")
New-RandomFile (Join-Path $dupDir "Project report.pdf") 2400
Copy-Item (Join-Path $dupDir "Project report.pdf") (Join-Path $dupDir "Project report (1).pdf")
New-RandomFile (Join-Path $dupDir "IMG_0412.jpg") 3100
Copy-Item (Join-Path $dupDir "IMG_0412.jpg") (Join-Path $dupDir "Backup\beach-sunset.jpg")   # same content, different name
New-RandomFile (Join-Path $dupDir "notes.docx") 300
New-RandomFile (Join-Path $dupDir "Backup\notes.docx") 300                                  # same name, different content

# ---- Start the app at a fixed place and size
$p = Start-Process $Exe -PassThru
$w = Wait-For { Get-AppWindow $p.Id "Unravel*" } 40
if (-not $w) { throw "Unravel window not found" }
[UWMove]::MoveWindow([IntPtr]$w.Current.NativeWindowHandle, $X, $Y, $VW, $VH, $true) | Out-Null
[UW]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null
Start-Sleep 4
Shot $w "programs"

# ---- Record the screen region of the window
$video = Join-Path $Out "unravel-demo.mp4"
$ff = New-Object System.Diagnostics.ProcessStartInfo "ffmpeg"
$ff.Arguments = "-y -loglevel error -f gdigrab -framerate 20 -offset_x $X -offset_y $Y -video_size ${VW}x${VH} -draw_mouse 0 -i desktop -c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p `"$video`""
$ff.UseShellExecute = $false; $ff.RedirectStandardInput = $true; $ff.CreateNoWindow = $true
$rec = [System.Diagnostics.Process]::Start($ff)
Start-Sleep 2

# ---- Uninstall the demo program with a leftover scan
Type-Slowly (Find-ById $w 'SearchBox') 'Contoso'
Start-Sleep 1
$row = Wait-For { $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))) } 10
Invoke-El $row | Out-Null
Start-Sleep 2
Shot $w "programs-selected"
Invoke-El (Find-ById $w 'UninstallButton') | Out-Null
Press-Dialog $p.Id 'Uninstall program' 'Uninstall' 1500 | Out-Null
$wiz = Wait-For { Get-AppWindow $p.Id 'Uninstall Contoso*' } 20
$next = Find-ById $wiz 'NextButton'
Wait-For { if ($next.Current.IsEnabled) { $true } } 60 | Out-Null
Start-Sleep 2
Invoke-El (Find-ById $wiz 'ModeModerate') | Out-Null
Start-Sleep 1
Invoke-El $next | Out-Null
Wait-For { $t = Find-ById $wiz 'RegistryTitle'; if ($t -and -not $t.Current.IsOffscreen -and $t.Current.Name -like 'Leftover*') { $t } } 60 | Out-Null
Start-Sleep 2
Invoke-El (Find-ById $wiz 'RegSelectAllButton') | Out-Null
Start-Sleep 2
Shot $wiz "wizard-registry"
Invoke-El (Find-ById $wiz 'RegDeleteButton') | Out-Null
$prev = Wait-For { Get-AppWindow $p.Id 'Delete registry items' } 15
Start-Sleep 2
if ($prev) { Shot $prev "deletion-preview" }
Start-Sleep 1
Press-Dialog $p.Id 'Delete registry items' 'Delete' | Out-Null
Wait-For { $t = Find-ById $wiz 'FilesTitle'; if ($t -and -not $t.Current.IsOffscreen -and $t.Current.Name -like 'Leftover*') { $t } } 20 | Out-Null
Start-Sleep 2
Invoke-El (Find-ById $wiz 'FileSelectAllButton') | Out-Null
Start-Sleep 2
Shot $wiz "wizard-files"
Invoke-El (Find-ById $wiz 'FileDeleteButton') | Out-Null
Press-Dialog $p.Id 'Delete files and folders' 'Delete' 2000 | Out-Null
Start-Sleep 1
Invoke-El (Find-ById $wiz 'NextButton') | Out-Null
Start-Sleep 3
Shot $wiz "wizard-done"
Invoke-El (Find-ById $wiz 'CloseButton') | Out-Null
Start-Sleep 1
(Find-ById $w 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
Start-Sleep 1

# ---- Tour of the main pages
Select-Nav $w "Live monitor" | Out-Null; Start-Sleep 6; Shot $w "live-monitor"
Select-Nav $w "Performance" | Out-Null; Start-Sleep 8; Shot $w "performance"
Select-Nav $w "Duplicates & orphans" | Out-Null; Start-Sleep 1
Invoke-El (Find-ById $w 'DupScan') | Out-Null
Wait-For { $x = (Find-ById $w 'DupSummary').Current.Name; if ($x -notlike 'Choose folders*' -and (Find-ById $w 'DupScan').Current.IsEnabled) { $x } } 300 | Out-Null
Start-Sleep 3; Shot $w "duplicates"
Select-Nav $w "Startup manager" | Out-Null; Start-Sleep 4; Shot $w "startup-manager"
Select-Nav $w "Install tracker" | Out-Null; Start-Sleep 3; Shot $w "install-tracker"
Select-Nav $w "Package manager" | Out-Null; Start-Sleep 8; Shot $w "package-manager"
Select-Nav $w "Windows history" | Out-Null; Start-Sleep 3; Shot $w "windows-history"
Select-Nav $w "Programs" | Out-Null; Start-Sleep 3

$rec.StandardInput.Write("q"); $rec.StandardInput.Flush()
$rec.WaitForExit(20000) | Out-Null
"video: $video ($([int]((Get-Item $video).Length / 1KB)) KB)"

# ---- Clean up the demo data
Stop-Process -Id $p.Id -Force -EA 0
Remove-Item $dupDir -Recurse -Force -EA 0
& (Join-Path $PSScriptRoot "demo_check.ps1")
