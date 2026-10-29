using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Apps;
using Unravel.Core.Autorun;
using Unravel.Core.Hunter;
using Unravel.Core.Native;
using Unravel.Core.Programs;
using Unravel.Core.Util;

namespace Unravel.App;

/// <summary>
/// A small always-on-top target. Drag it onto a window, desktop icon or taskbar button and release to act on that program.
/// </summary>
public sealed class HunterWindow : Window
{
    private readonly MainWindow _main;
    private readonly Popup _tip = new() { Placement = PlacementMode.AbsolutePoint, AllowsTransparency = true, IsHitTestVisible = false };
    private readonly TextBlock _tipTitle = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _tipPath = new() { FontSize = 11.5 };
    private Point _downAt;
    private bool _hunting;
    private HunterTarget? _target;
    private DateTime _lastResolve;

    private static readonly int[] Sizes = [48, 68, 92];

    public HunterWindow(MainWindow main)
    {
        _main = main;
        Title = "Unravel – Hunter mode";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Icon = main.Icon;
        Cursor = Cursors.Cross;
        ApplyOptions();
        Content = BuildTarget();
        ToolTip = "Drag onto a window, desktop icon or taskbar button and release.\nCtrl+drag to move. Right-click for options.";

        var tipBorder = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), BorderThickness = new Thickness(1), MaxWidth = 520 };
        tipBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        tipBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _tipTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _tipPath.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var sp = new StackPanel();
        sp.Children.Add(_tipTitle);
        sp.Children.Add(_tipPath);
        tipBorder.Child = sp;
        _tip.Child = tipBorder;

        var area = SystemParameters.WorkArea;
        Left = Ui.Settings.Hunter.Left ?? area.Right - Width - 24;
        Top = Ui.Settings.Hunter.Top ?? area.Top + 24;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, e) => { ShowOptionsMenu(); e.Handled = true; };
        LocationChanged += (_, _) => { Ui.Settings.Hunter.Left = Left; Ui.Settings.Hunter.Top = Top; };
        Closed += (_, _) => Ui.SaveSettings();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _hunting = false; _tip.IsOpen = false; ReleaseMouseCapture(); } };
    }

    private void ApplyOptions()
    {
        var h = Ui.Settings.Hunter;
        Width = Height = Sizes[Math.Clamp(h.Size, 0, 2)];
        Topmost = h.AlwaysOnTop;
        Opacity = 1 - Math.Clamp(h.TransparencyPercent, 0, 80) / 100.0;
    }

    private UIElement BuildTarget()
    {
        var g = new Grid();
        var accent = (Brush)FindResource("AccentBrush");
        var outer = new Ellipse { Fill = (Brush)FindResource("SurfaceBrush"), Stroke = accent, StrokeThickness = 3 };
        outer.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.3 };
        g.Children.Add(outer);
        g.Children.Add(new Ellipse { Stroke = accent, StrokeThickness = 2, Margin = new Thickness(Width * 0.2) });
        g.Children.Add(new Ellipse { Fill = accent, Margin = new Thickness(Width * 0.38) });
        g.Children.Add(new Rectangle { Fill = accent, Width = 2, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 });
        g.Children.Add(new Rectangle { Fill = accent, Height = 2, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
        return g;
    }

    // ------------------------------------------------------------ hunting

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { DragMove(); return; }
        _downAt = PointToScreen(e.GetPosition(this));
        _hunting = true;
        _target = null;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_hunting) return;
        NativeMethods.GetCursorPos(out var p);
        if ((DateTime.UtcNow - _lastResolve).TotalMilliseconds < 120) return;
        _lastResolve = DateTime.UtcNow;
        if (Math.Abs(p.X - _downAt.X) < 6 && Math.Abs(p.Y - _downAt.Y) < 6) return;
        _target = Resolve(p.X, p.Y);
        if (_target is null) { _tip.IsOpen = false; return; }
        _tipTitle.Text = _target.DisplayName + (_target.WindowTitle is { Length: > 0 } t && t != _target.DisplayName ? $" – {t}" : "");
        _tipPath.Text = _target.ExecutablePath;
        var dpi = VisualTreeHelper.GetDpi(this);
        _tip.HorizontalOffset = p.X / dpi.DpiScaleX + 18;
        _tip.VerticalOffset = p.Y / dpi.DpiScaleY + 18;
        _tip.IsOpen = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hunting) return;
        _hunting = false;
        ReleaseMouseCapture();
        _tip.IsOpen = false;
        NativeMethods.GetCursorPos(out var p);
        if (Math.Abs(p.X - _downAt.X) < 6 && Math.Abs(p.Y - _downAt.Y) < 6) return; // a click, not a hunt
        var target = _target ?? Resolve(p.X, p.Y);
        if (target is null)
        {
            Ui.Info("Nothing found", "No program could be identified at that spot. Release the target over a window, a desktop shortcut or a taskbar button.");
            return;
        }
        ShowTargetMenu(target);
    }

    private HunterTarget? Resolve(int x, int y)
    {
        try
        {
            if (HunterResolver.IsShellSurface(x, y))
            {
                var el = AutomationElement.FromPoint(new Point(x, y));
                var name = el?.Current.Name;
                if (string.IsNullOrWhiteSpace(name)) return null;
                return HunterResolver.FromShortcutLabel(name) ?? FromRunningLabel(name);
            }
            return HunterResolver.FromWindowAt(x, y, Environment.ProcessId);
        }
        catch { return null; }
    }

    /// <summary>Taskbar and tray buttons are labelled with the program's name ("Notepad – 2 running windows").</summary>
    private static HunterTarget? FromRunningLabel(string label)
    {
        var name = label.Split([" - ", " – "], StringSplitOptions.None)[0].Trim();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero && !p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var path = NativeMethods.GetProcessImagePath(p.Id);
                if (path is null) continue;
                var desc = HunterResolver.DescribeFile(path);
                if (desc.Equals(name, StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    p.MainWindowTitle.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                    return new HunterTarget(HunterTargetKind.Window, path, desc, p.Id, p.MainWindowTitle, null);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    // ------------------------------------------------------------ actions

    private void ShowTargetMenu(HunterTarget t)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var title = t.DisplayName.Equals(t.FileName, StringComparison.OrdinalIgnoreCase) ? t.FileName : $"{t.DisplayName}  ({t.FileName})";
        var header = new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        Add(menu, "Uninstall…", "", () => Uninstall(t));
        Add(menu, "Stop starting with Windows", "", () => StopAutostart(t));
        Add(menu, "End process", "", () => Kill(t, delete: false), enabled: ProcessUtil.IsRunning(t.ExecutablePath));
        Add(menu, "End process and delete file…", "", () => Kill(t, delete: true));
        Add(menu, "Open containing folder", "", () => Shell.ShowInExplorer(t.ExecutablePath));
        var search = new MenuItem { Header = "Search on Google for" };
        Add(search, "Program name", null, () => Shell.SearchGoogle(t.DisplayName));
        Add(search, "File name", null, () => Shell.SearchGoogle(t.FileName));
        var company = SafeCompany(t.ExecutablePath);
        if (company is not null) Add(search, "Publisher", null, () => Shell.SearchGoogle(company));
        menu.Items.Add(search);
        Add(menu, "Properties", "", () => Shell.ShowProperties(t.ExecutablePath));
        menu.Items.Add(new Separator());
        Add(menu, "Cancel", null, () => { });
        menu.IsOpen = true;
    }

    private static string? SafeCompany(string path)
    {
        try { var c = FileVersionInfo.GetVersionInfo(path).CompanyName; return string.IsNullOrWhiteSpace(c) ? null : c.Trim(); } catch { return null; }
    }

    private static void Add(ItemsControl parent, string header, string? glyph, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (glyph is not null) mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 13 };
        mi.Click += (_, _) => Ui.Try(header.TrimEnd('…'), action);
        parent.Items.Add(mi);
    }

    private void Uninstall(HunterTarget t)
    {
        if (t.ExecutablePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            var app = WindowsApps.List(new WindowsAppOptions { IncludeSystemApps = true })
                .FirstOrDefault(a => a.InstallLocation is not null && FileOps.IsSameOrUnder(t.ExecutablePath, a.InstallLocation));
            if (app is not null && Ui.Confirm("Uninstall app", $"Uninstall \"{app.Name}\"?", "Uninstall"))
            {
                new UninstallWizard(UninstallJob.ForWindowsApp(app, [])).ShowDialog();
                return;
            }
        }
        var programs = new ProgramInventory().Load(new InventoryOptions { IncludeSystemComponents = true });
        var match = HunterResolver.MatchProgram(t.ExecutablePath, programs);
        if (match is null)
        {
            Ui.Info("Not an installed program", $"{t.FileName} does not belong to any program in the installed programs list. You can end it, delete it or stop it from starting with Windows instead.");
            return;
        }
        if (!Ui.Confirm("Uninstall program", $"{t.FileName} belongs to \"{match.DisplayName}\".\n\nUninstall it?", "Uninstall")) return;
        var others = programs.Where(p => p != match).Select(p => (p.DisplayName, p.ResolvedInstallFolder ?? "")).ToList();
        new UninstallWizard(UninstallJob.ForProgram(match, UninstallCommandKind.Standard, others)).ShowDialog();
    }

    private static void StopAutostart(HunterTarget t)
    {
        var manager = new AutorunManager(AutorunEnvironment.CreateDefault(), Ui.Settings.DisabledServiceStartTypes);
        var entries = HunterResolver.MatchAutorun(t.ExecutablePath, manager.Load()).Where(e => e.Enabled).ToList();
        if (entries.Count == 0) { Ui.Info("Not starting with Windows", $"{t.DisplayName} is not set to start automatically."); return; }
        foreach (var e in entries) manager.SetEnabled(e, false);
        Ui.SaveSettings();
        Ui.Info("Done", $"{t.DisplayName} will no longer start with Windows ({entries.Count} startup item(s) turned off). You can turn it back on in the Startup manager.");
    }

    private static void Kill(HunterTarget t, bool delete)
    {
        var question = delete
            ? $"End {t.FileName} and move it to the Recycle Bin?\n\n{t.ExecutablePath}\n\nThe program may stop working. Only do this for programs you are sure you don't need."
            : $"End {t.FileName}? Unsaved data in it will be lost.";
        if (!(delete ? Ui.ConfirmAlways("End and delete", question, "End and delete") : Ui.Confirm("End process", question, "End process", true))) return;
        var (_, failed) = ProcessUtil.KillByImagePath(t.ExecutablePath);
        if (failed > 0) throw new InvalidOperationException("The process could not be ended. It may need administrator rights.");
        if (!delete) return;
        Thread.Sleep(500);
        var r = FileOps.Delete(t.ExecutablePath, toRecycleBin: true);
        if (!r.Succeeded) throw new IOException(r.Error ?? "The file could not be deleted.");
        Ui.Info("Done", $"{t.FileName} was ended and moved to the Recycle Bin.");
    }

    // ------------------------------------------------------------ options

    private void ShowOptionsMenu()
    {
        var h = Ui.Settings.Hunter;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        Add(menu, "Open main window", "", OpenMain);
        menu.Items.Add(new Separator());
        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = h.StartWithWindows };
        startup.Click += (_, _) => { h.StartWithWindows = startup.IsChecked; SetStartWithWindows(startup.IsChecked); Ui.SaveSettings(); };
        menu.Items.Add(startup);
        var top = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = h.AlwaysOnTop };
        top.Click += (_, _) => { h.AlwaysOnTop = top.IsChecked; ApplyOptions(); Ui.SaveSettings(); };
        menu.Items.Add(top);
        var size = new MenuItem { Header = "Size" };
        string[] names = ["Small", "Medium", "Large"];
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var mi = new MenuItem { Header = names[i], IsCheckable = true, IsChecked = h.Size == i };
            mi.Click += (_, _) => { h.Size = idx; ApplyOptions(); Content = BuildTarget(); Ui.SaveSettings(); };
            size.Items.Add(mi);
        }
        menu.Items.Add(size);
        var transparency = new MenuItem { Header = "Transparency" };
        foreach (var pct in new[] { 0, 25, 50, 75 })
        {
            var mi = new MenuItem { Header = pct == 0 ? "None" : $"{pct}%", IsCheckable = true, IsChecked = h.TransparencyPercent == pct };
            mi.Click += (_, _) => { h.TransparencyPercent = pct; ApplyOptions(); Ui.SaveSettings(); };
            transparency.Items.Add(mi);
        }
        menu.Items.Add(transparency);
        menu.Items.Add(new Separator());
        Add(menu, "Help", "", () => { OpenMain(); _main.Navigate("about"); });
        Add(menu, "Exit Unravel", "", () => Application.Current.Shutdown());
        menu.IsOpen = true;
    }

    private void OpenMain()
    {
        Close();
        _main.ShowFromHunter();
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) k.SetValue("Unravel Hunter", $"\"{Environment.ProcessPath}\" --hunter");
        else k.DeleteValue("Unravel Hunter", false);
    }
}
