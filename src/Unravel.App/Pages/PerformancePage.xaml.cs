using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Unravel.App.Infrastructure;
using Unravel.Core.Autorun;
using Unravel.Core.Monitoring;
using Unravel.Core.Performance;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class ImpactRow : INotifyPropertyChanged
{
    private readonly Func<ImpactRow, bool, bool> _toggle;
    public ImpactRow(StartupImpactItem i, Func<ImpactRow, bool, bool> toggle) { Item = i; _toggle = toggle; }
    public StartupImpactItem Item { get; }
    public string Name => Item.Entry.Name;
    public string Source => Item.Entry.SourceText;
    public string Basis => Item.Basis;
    public int ImpactOrder => (int)Item.Impact;
    public string ImpactText => Item.Impact == StartupImpact.NotMeasured ? "Not measured" : Item.Impact.ToString();
    public double CpuMs => Item.CpuTime.TotalMilliseconds;
    public long DiskBytes => Item.DiskBytes;
    public long MemoryBytes => Item.MemoryBytes;
    public string CpuText => Item.Impact == StartupImpact.NotMeasured ? "" : CpuMs >= 1000 ? $"{CpuMs / 1000:0.0} s" : $"{CpuMs:0} ms";
    public string DiskText => Item.Impact == StartupImpact.NotMeasured ? "" : Format.Bytes(DiskBytes);
    public string MemoryText => Item.Impact == StartupImpact.NotMeasured ? "" : Format.Bytes(MemoryBytes);
    public Brush ImpactBackground => (Brush)Application.Current.FindResource(Item.Impact switch
    { StartupImpact.High => "DangerSoftBrush", StartupImpact.Medium => "WarningSoftBrush", StartupImpact.Low or StartupImpact.None => "SuccessSoftBrush", _ => "SurfaceAltBrush" });
    public Brush ImpactForeground => (Brush)Application.Current.FindResource(Item.Impact switch
    { StartupImpact.High => "DangerBrush", StartupImpact.Medium => "WarningBrush", StartupImpact.Low or StartupImpact.None => "SuccessBrush", _ => "MutedTextBrush" });
    public bool Enabled
    {
        get => Item.Entry.Enabled;
        set { if (value != Item.Entry.Enabled) _toggle(this, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record BgRow(string Name, ProcessGroup Group, double Cpu, long Memory, DateTime Started, string Path, int Pid);
public sealed record DeltaRow(string Metric, string Before, string After, string Text, Brush Brush);

/// <summary>Folder node for the disk usage tree, with its share of the parent for the size bar.</summary>
public sealed class UsageNode
{
    public UsageNode(FolderUsage f, long parentSize)
    {
        Path = f.Path; Name = f.Name; Size = f.Size;
        Share = parentSize > 0 ? (double)f.Size / parentSize : 1;
        Children = f.Children.Take(60).Select(c => new UsageNode(c, f.Size)).ToList();
    }
    public string Path { get; }
    public string Name { get; }
    public long Size { get; }
    public double Share { get; }
    public string SizeText => Format.Bytes(Size);
    public List<UsageNode> Children { get; }
}

public partial class PerformancePage : UserControl, IPage
{
    private readonly ObservableCollection<ImpactRow> _impact = new();
    private readonly BaselineStore _store = new();
    private AutorunManager? _autorun;
    private CancellationTokenSource? _diskCts;
    private bool _analyzed;

    public PerformancePage()
    {
        InitializeComponent();
        StartupGrid.ItemsSource = _impact;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            DriveBox.Items.Add(new ComboBoxItem { Content = $"{d.Name} – {Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)}", Tag = d.RootDirectory.FullName });
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DriveBox.Items.Insert(0, new ComboBoxItem { Content = "Your user folder (" + profile + ")", Tag = profile });
        DriveBox.SelectedIndex = 0;
    }

    public void OnShown()
    {
        if (!_analyzed) { _analyzed = true; AnalyzeStartup_Click(this, new RoutedEventArgs()); }
        ReloadBaselines();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (StartupPanel is null) return;
        StartupPanel.Visibility = TabStartup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPanel.Visibility = TabProcesses.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiskPanel.Visibility = TabDisk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BaselinePanel.Visibility = TabBaselines.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabProcesses.IsChecked == true) RefreshProcesses_Click(sender, e);
    }

    // ------------------------------------------------------------ startup impact

    private async void AnalyzeStartup_Click(object sender, RoutedEventArgs e)
    {
        BootText.Text = "Measuring…";
        try
        {
            var (items, boot) = await Task.Run(() =>
            {
                _autorun = new AutorunManager(AutorunEnvironment.CreateDefault(), Ui.Settings.DisabledServiceStartTypes);
                using var m = new SystemMonitor();
                return (PerformanceInsights.AnalyzeStartup(_autorun.Load(), m.Sample()), PerformanceInsights.GetBootInfo());
            });
            _impact.Clear();
            foreach (var i in items) _impact.Add(new ImpactRow(i, Toggle));
            BootText.Text = $"Last start {boot.LastBoot:g} (up {(int)boot.Uptime.TotalHours} h {boot.Uptime.Minutes} min)" +
                (boot.BootDuration is { } d ? $" · Windows took {d.TotalSeconds:0.0} s to boot" : "") +
                $" · {items.Count(i => i.Impact == StartupImpact.High)} high-impact startup item(s)";
            if (boot.BootDuration is null && boot.Note is not null) BootNote.Text = boot.Note + " Impact uses Task Manager's thresholds: High = over 1 s CPU or 3 MB disk at sign-in.";
        }
        catch (Exception ex) { BootText.Text = "Analysis failed: " + ex.Message; }
    }

    private bool Toggle(ImpactRow row, bool enabled)
    {
        if (_autorun is null) return false;
        var ok = Ui.Try(enabled ? "Could not enable the item" : "Could not disable the item", () => _autorun.SetEnabled(row.Item.Entry, enabled));
        if (ok) Ui.SaveSettings();
        return ok;
    }

    // ------------------------------------------------------------ background processes

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        // Two samples one second apart give real CPU percentages.
        var classified = await Task.Run(() =>
        {
            using var m = new SystemMonitor();
            Thread.Sleep(1000);
            return PerformanceInsights.ClassifyProcesses(m.Sample());
        });
        var groups = PerformanceInsights.Summarize(classified);
        GroupCards.Children.Clear();
        foreach (var g in groups)
        {
            var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 8, 0) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = g.Group switch { ProcessGroup.Apps => "Apps", ProcessGroup.Background => "Background programs", _ => "Windows processes" }, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = $"{g.Count}", FontSize = 22, FontWeight = FontWeights.SemiBold });
            var d = new TextBlock { Text = $"{g.CpuPercent:0.0}% CPU · {Format.Bytes(g.MemoryBytes)} memory", FontSize = 12 };
            d.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            sp.Children.Add(d);
            card.Child = sp;
            GroupCards.Children.Add(card);
        }
        // Windows' own processes are summarised in the card only; programs you installed are listed for review.
        BgGrid.ItemsSource = classified.Where(x => x.Group != ProcessGroup.Windows && x.Process.ImagePath is not null)
            .Select(x => new BgRow(x.Process.Name, x.Group, x.Process.CpuPercent, x.Process.PrivateBytes, x.Process.StartTime, x.Process.ImagePath!, x.Process.Pid))
            .OrderBy(r => r.Group == ProcessGroup.Apps).ThenByDescending(r => r.Memory).ToList();
    }

    private BgRow? SelectedBg => BgGrid.SelectedItem as BgRow;

    private void EndBg_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBg is not { } r) return;
        if (ProcessGuard.IsCritical(r.Pid, r.Name, r.Path)) { Ui.Error("Protected process", $"{r.Name} is critical to Windows and cannot be ended."); return; }
        if (!Ui.Confirm("End process", $"End {r.Name}? Unsaved work in it will be lost.", "End process", danger: true)) return;
        if (Ui.Try("Could not end the process", () => ProcessGuard.End(r.Pid, r.Name, r.Path))) RefreshProcesses_Click(sender, e);
    }

    private void OpenBg_Click(object s, RoutedEventArgs e) { if (SelectedBg is { } r) Ui.Try("Open location", () => Shell.ShowInExplorer(r.Path)); }
    private void SearchBg_Click(object s, RoutedEventArgs e) { if (SelectedBg is { } r) Ui.Try("Search", () => Shell.SearchGoogle(r.Name)); }

    // ------------------------------------------------------------ disk usage

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Choose a folder to analyse" };
        if (d.ShowDialog(Window.GetWindow(this)) != true) return;
        var item = new ComboBoxItem { Content = d.FolderName, Tag = d.FolderName };
        DriveBox.Items.Add(item);
        DriveBox.SelectedItem = item;
    }

    private async void DiskScan_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not ComboBoxItem { Tag: string root }) return;
        _diskCts = new CancellationTokenSource();
        DiskScanButton.IsEnabled = false;
        DiskCancelButton.IsEnabled = true;
        var progress = new Progress<string>(p => DiskStatus.Text = "Scanning " + p);
        var ct = _diskCts.Token;
        try
        {
            var r = await Task.Run(() => DiskUsageScanner.Scan(root, keepDepth: 3, topFiles: 100, progress, ct));
            FolderTree.ItemsSource = new[] { new UsageNode(r.Root, r.Root.Size) };
            LargeGrid.ItemsSource = r.LargestFiles;
            DiskStatus.Text = $"{root}: {Format.Bytes(r.Root.Size)}" + (r.Cancelled ? " (scan cancelled – partial)" : "") +
                              (r.Inaccessible > 0 ? $" · {r.Inaccessible} folder(s) could not be read" + (ProcessUtil.IsElevated ? "" : " without administrator rights") : "");
        }
        catch (Exception ex) { DiskStatus.Text = "Scan failed: " + ex.Message; }
        finally { _diskCts = null; DiskScanButton.IsEnabled = true; DiskCancelButton.IsEnabled = false; }
    }

    private void DiskCancel_Click(object sender, RoutedEventArgs e) => _diskCts?.Cancel();
    private void OpenTreeFolder_Click(object s, RoutedEventArgs e) { if (FolderTree.SelectedItem is UsageNode n) Ui.Try("Open folder", () => Shell.ShowInExplorer(n.Path)); }
    private void OpenLarge_Click(object s, RoutedEventArgs e) { if (LargeGrid.SelectedItem is LargeFile f) Ui.Try("Open folder", () => Shell.ShowInExplorer(f.Path)); }

    private void RecycleLarge_Click(object sender, RoutedEventArgs e)
    {
        if (LargeGrid.SelectedItem is not LargeFile f) return;
        if (SafetyEngine.IsCriticalPath(f.Path)) { Ui.Error("Protected file", $"{f.Path} is a system file and cannot be deleted here."); return; }
        if (!Ui.Confirm("Move to Recycle Bin", $"Move this file to the Recycle Bin?\n\n{f.Path}\n{Format.Bytes(f.Size)}", "Move to Recycle Bin", danger: true)) return;
        var r = FileOps.Delete(f.Path, toRecycleBin: true);
        if (!r.Succeeded) Ui.Error("Could not delete", r.Error ?? "");
        else
        {
            Core.Logging.ActivityLog.Action("Disk usage", $"Moved {f.Path} ({Format.Bytes(f.Size)}) to the Recycle Bin");
            LargeGrid.ItemsSource = ((IEnumerable<LargeFile>)LargeGrid.ItemsSource).Where(x => x != f).ToList();
        }
    }

    // ------------------------------------------------------------ baselines

    private void ReloadBaselines() => BaselineList.ItemsSource = _store.List().OrderByDescending(b => b.Captured).ToList();

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        var label = BaselineLabel.Text.Trim();
        if (label.Length == 0) label = $"Baseline {DateTime.Now:g}";
        CaptureButton.IsEnabled = false;
        try
        {
            var b = await BaselineStore.CaptureAsync(label, 15, new Progress<string>(s => CaptureStatus.Text = s), default);
            _store.Add(b);
            CaptureStatus.Text = $"Saved \"{label}\": {b.AverageCpuPercent:0.0}% CPU, {Format.Bytes(b.MemoryUsedBytes)} memory in use, {b.EnabledStartupItems} startup items.";
            BaselineLabel.Clear();
            ReloadBaselines();
        }
        catch (Exception ex) { CaptureStatus.Text = "Capture failed: " + ex.Message; }
        finally { CaptureButton.IsEnabled = true; }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        var sel = BaselineList.SelectedItems.OfType<PerformanceBaseline>().OrderBy(b => b.Captured).ToList();
        if (sel.Count != 2) { Ui.Info("Select two baselines", "Select exactly two baselines (the earlier one is treated as \"before\")."); return; }
        CompareGrid.ItemsSource = BaselineStore.Compare(sel[0], sel[1]).Select(d => new DeltaRow(d.Metric, d.Before, d.After, d.Change,
            (Brush)FindResource(d.Better switch { true => "SuccessBrush", false => "DangerBrush", _ => "MutedTextBrush" }))).ToList();
    }

    private void DeleteBaseline_Click(object sender, RoutedEventArgs e)
    {
        var sel = BaselineList.SelectedItems.OfType<PerformanceBaseline>().ToList();
        if (sel.Count == 0 || !Ui.Confirm("Delete baselines", $"Delete {sel.Count} baseline(s)?", "Delete", danger: true)) return;
        foreach (var b in sel) _store.Remove(b);
        ReloadBaselines();
    }
}
