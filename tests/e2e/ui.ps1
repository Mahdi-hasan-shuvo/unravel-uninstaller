# UI automation helpers for driving and capturing the Unravel window (same integrity level as this shell).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class UW {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  public struct RECT { public int L, T, R, B; }
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-AppWindow([int]$procId, [string]$titleLike = "*") {
    # Owned dialogs appear as children of their owner window in the UIA tree, so search two levels of windows.
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    $winType = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $queue = New-Object System.Collections.Queue
    foreach ($w in $AE::RootElement.FindAll($TS::Children, $cond)) { $queue.Enqueue(@($w, 0)) }
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue(); $w = $item[0]; $depth = $item[1]
        try { if ($w.Current.Name -like $titleLike) { return $w } } catch { continue }
        if ($depth -lt 3) { foreach ($c in $w.FindAll($TS::Children, $winType)) { $queue.Enqueue(@($c, $depth + 1)) } }
    }
    return $null
}

function Save-Window($element, [string]$path) {
    $h = [IntPtr]$element.Current.NativeWindowHandle
    [UW]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 350
    $r = New-Object UW+RECT
    [UW]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $hh = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [UW]::PrintWindow($h, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT: captures only this window
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path); $bmp.Dispose()
}

function Find-ByName($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    return $root.FindFirst($TS::Descendants, $cond)
}

function Find-ById($root, [string]$id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $cond)
}

function Invoke-El($el) {
    if ($null -eq $el) { Write-Output '  (element not found)'; return $false }
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return $true }
    return $false
}

function Select-Nav($win, [string]$label) {
    # Nav radio buttons expose their text content as the name of a child text element; find the RadioButton ancestor.
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)
    foreach ($rb in $win.FindAll($TS::Descendants, $cond)) {
        $t = Find-ByName $rb $label
        if ($t -ne $null -or $rb.Current.Name -eq $label) { Invoke-El $rb | Out-Null; return $true }
    }
    return $false
}
