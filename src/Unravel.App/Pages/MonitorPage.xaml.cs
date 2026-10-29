using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Unravel.App.Infrastructure;
using Unravel.Core.Monitoring;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class ProcessRow : INotifyPropertyChanged
{
    public ProcessRow(ProcessSample s) { Pid = s.Pid; Name = s.Name; Path = s.ImagePath ?? ""; Start = s.StartTime; Update(s); }
    public int Pid { get; }
    public string Name { get; }
    public string Path { get; }
    public DateTime Start { get; }
    public double Cpu { get; private set; }
    public long Memory { get; private set; }
    public double Io { get; private set; }
    public int Threads { get; private set; }
    public int Handles { get; private set; }
    public string CpuText => Cpu < 0.05 ? "0%" : $"{Cpu:0.0}%";
    public string MemoryText => Format.Bytes(Memory);
    public string IoText => Io < 1 ? "0" : Format.Bytes((long)Io) + "/s";

    public void Update(ProcessSample s)
    {
        if (Cpu != s.CpuPercent) { Cpu = s.CpuPercent; Changed(nameof(Cpu)); Changed(nameof(CpuText)); }
        if (Memory != s.PrivateBytes) { Memory = s.PrivateBytes; Changed(nameof(Memory)); Changed(nameof(MemoryText)); }
        if (Io != s.IoBytesPerSec) { Io = s.IoBytesPerSec; Changed(nameof(Io)); Changed(nameof(IoText)); }
        if (Threads != s.Threads) { Threads = s.Threads; Changed(nameof(Threads)); }
        if (Handles != s.Handles) { Handles = s.Handles; Changed(nameof(Handles)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class MonitorPage : UserControl, IPage
{
    private readonly ObservableCollection<ProcessRow> _rows = new();
    private readonly Dictionary<(int, DateTime), ProcessRow> _index = new();
    private readonly ListCollectionView _view;
    private readonly ObservableCollection<ServiceInfo> _services = new();
    private readonly ICollectionView _serviceView;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ActivityAlertEngine _alerts = new();
    private SystemMonitor? _monitor;
    private bool _sampling;
    private string _lastAlertSignature = "";

    public MonitorPage()
    {
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        _view.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcessRow.Cpu), nameof(ProcessRow.Memory), nameof(ProcessRow.Io), nameof(ProcessRow.Threads), nameof(ProcessRow.Handles) })
            _view.LiveSortingProperties.Add(p);
        _view.Filter = o => o is ProcessRow r && Matches(r.Name, r.Path);
        ProcessGrid.ItemsSource = _view;
        _serviceView = CollectionViewSource.GetDefaultView(_services);
        _serviceView.Filter = o => o is ServiceInfo s && (RunningOnly.IsChecked != true || s.IsRunning) && Matches(s.DisplayName, s.Name + " " + s.ImagePath);
        ServiceGrid.ItemsSource = _serviceView;
        CpuChart.Capacity = MemChart.Capacity = DiskChart.Capacity = 60;
        _timer.Tick += async (_, _) => await TickAsync();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void OnShown()
    {
        _monitor ??= new SystemMonitor();
        UpdateTimer();
        if (_services.Count == 0) _ = LoadServicesAsync();
    }

    private bool Matches(string a, string? b)
    {
        var q = FilterBox?.Text?.Trim();
        return string.IsNullOrEmpty(q) || a.Contains(q, StringComparison.OrdinalIgnoreCase) || (b?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Sampling runs only while the page is visible and not paused, so the monitor costs nothing otherwise.</summary>
    private void UpdateTimer()
    {
        if (IsVisible && PauseButton.IsChecked != true && _monitor is not null) _timer.Start(); else _timer.Stop();
    }

    private async Task TickAsync()
    {
        if (_sampling || _monitor is null) return;
        _sampling = true;
        try
        {
            var mon = _monitor;
            var s = await Task.Run(() => mon.Sample());
            var newAlerts = _alerts.Evaluate(s);
            Apply(s);
            if (newAlerts.Count > 0 || AlertSignature() != _lastAlertSignature) RenderAlerts();
        }
        catch (Exception e) { UpdatedText.Text = "Sampling failed: " + e.Message; }
        finally { _sampling = false; }
    }

    private void Apply(SystemSample s)
    {
        CpuValue.Text = $"{s.CpuPercent:0}%";
        CpuDetail.Text = $"{s.ProcessCount} processes · {s.ThreadCount:N0} threads";
        CpuChart.Push(s.CpuPercent);
        MemValue.Text = $"{s.MemoryUsedPercent:0}%";
        MemDetail.Text = $"{Format.Bytes((long)(s.MemoryTotal - s.MemoryAvailable))} of {Format.Bytes((long)s.MemoryTotal)} · committed {Format.Bytes((long)s.CommitTotal)}";
        MemChart.Push(s.MemoryUsedPercent);
        if (s.DiskActivePercent is { } active)
        {
            DiskValue.Text = $"{active:0}%";
            DiskDetail.Text = $"read {Format.Bytes((long)(s.DiskReadBytesPerSec ?? 0))}/s · write {Format.Bytes((long)(s.DiskWriteBytesPerSec ?? 0))}/s";
            DiskChart.Push((s.DiskReadBytesPerSec ?? 0) + (s.DiskWriteBytesPerSec ?? 0));
        }
        else
        {
            var io = s.Processes.Sum(p => p.IoBytesPerSec);
            DiskValue.Text = Format.Bytes((long)io) + "/s";
            DiskDetail.Text = "all process I/O (disk counters unavailable)";
            DiskChart.Push(io);
        }

        var alive = new HashSet<(int, DateTime)>();
        foreach (var p in s.Processes)
        {
            if (p.Pid == 0) continue;
            var key = (p.Pid, p.StartTime);
            alive.Add(key);
            if (_index.TryGetValue(key, out var row)) row.Update(p);
            else { row = new ProcessRow(p); _index[key] = row; _rows.Add(row); }
        }
        foreach (var dead in _index.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _rows.Remove(_index[dead]);
            _index.Remove(dead);
        }
        UpdatedText.Text = $"Updated {s.Time:T}";
    }

    private string AlertSignature() => string.Join("|", _alerts.Active.Select(a => a.Key));

    private void RenderAlerts()
    {
        _lastAlertSignature = AlertSignature();
        var active = _alerts.Active.ToList();
        AlertsCard.Visibility = active.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AlertsTitle.Text = active.Count == 1 ? "1 alert" : $"{active.Count} alerts";
        var panel = new StackPanel();
        foreach (var a in active.Take(6))
        {
            var brush = a.Severity == AlertSeverity.Critical ? "DangerBrush" : "WarningBrush";
            var soft = a.Severity == AlertSeverity.Critical ? "DangerSoftBrush" : "WarningSoftBrush";
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (a.Pid is { } pid && a.ProcessName is { } name)
            {
                buttons.Children.Add(SmallButton("End process", () => EndProcess(pid, name, a.ImagePath, tree: false)));
                if (a.ImagePath is { } path)
                {
                    buttons.Children.Add(SmallButton("Open location", () => Shell.ShowInExplorer(path)));
                    buttons.Children.Add(SmallButton("Search", () => Shell.SearchGoogle(System.IO.Path.GetFileName(path))));
                }
            }
            buttons.Children.Add(SmallButton("Ignore", () => { _alerts.Ignore(a); RenderAlerts(); }));
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var glyph = new TextBlock { Text = a.Severity == AlertSeverity.Critical ? "" : "", Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = a.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var d = new TextBlock { Text = a.Details + " " + a.Advice, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            d.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            text.Children.Add(d);
            row.Children.Add(text);
            var border = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 2, 0, 2), Child = row };
            border.SetResourceReference(Border.BackgroundProperty, soft);
            panel.Children.Add(border);
        }
        AlertList.Items.Clear();
        AlertList.Items.Add(panel);
    }

    private Button SmallButton(string text, Action action)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 2, 8, 2), MinHeight = 26 };
        b.Click += (_, _) => Ui.Try(text, action);
        return b;
    }

    private void EndProcess(int pid, string name, string? path, bool tree)
    {
        if (ProcessGuard.IsCritical(pid, name, path)) { Ui.Error("Protected process", $"{name} is a critical Windows process. Ending it would crash or destabilise Windows, so Unravel won't do it."); return; }
        var warning = ProcessGuard.WarningFor(name);
        if (!Ui.Confirm(tree ? "End process tree" : "End process", $"End {name} (PID {pid}){(tree ? " and every process it started" : "")}?\n\nUnsaved work in it will be lost." + (warning is null ? "" : "\n\n" + warning), "End process", danger: true))
            return;
        ProcessGuard.End(pid, name, path, tree);
    }

    private ProcessRow? Selected => ProcessGrid.SelectedItem as ProcessRow;
    private void EndProcess_Click(object s, RoutedEventArgs e) { if (Selected is { } r) Ui.Try("Could not end the process", () => EndProcess(r.Pid, r.Name, r.Path, false)); }
    private void EndTree_Click(object s, RoutedEventArgs e) { if (Selected is { } r) Ui.Try("Could not end the process", () => EndProcess(r.Pid, r.Name, r.Path, true)); }
    private void OpenLocation_Click(object s, RoutedEventArgs e) { if (Selected is { Path.Length: > 0 } r) Ui.Try("Open location", () => Shell.ShowInExplorer(r.Path)); }
    private void Search_Click(object s, RoutedEventArgs e) { if (Selected is { } r) Ui.Try("Search", () => Shell.SearchGoogle(r.Name)); }
    private void Properties_Click(object s, RoutedEventArgs e) { if (Selected is { Path.Length: > 0 } r) Ui.Try("Properties", () => Shell.ShowProperties(r.Path)); }

    private void Signature_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Path.Length: > 0 } r) return;
        var sig = FileSignature.Check(r.Path);
        Ui.Info("Digital signature", sig.IsSigned
            ? $"{r.Name} is signed by {sig.Signer ?? "an unknown publisher"}.\nSignature valid: {(sig.IsTrusted ? "yes" : "no")}"
            : $"{r.Name} is not digitally signed.\n\n{r.Path}");
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.Content = PauseButton.IsChecked == true ? "Resume" : "Pause";
        UpdateTimer();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _view?.Refresh();
        _serviceView?.Refresh();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid is null) return;
        ProcessGrid.Visibility = TabProcesses.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ServicesPanel.Visibility = TabServices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabServices.IsChecked == true) _ = LoadServicesAsync();
    }

    private async Task LoadServicesAsync()
    {
        var list = await Task.Run(ServicesManager.List);
        var selected = (ServiceGrid.SelectedItem as ServiceInfo)?.Name;
        _services.Clear();
        foreach (var s in list) _services.Add(s);
        ServiceGrid.SelectedItem = _services.FirstOrDefault(s => s.Name == selected);
    }

    private void RefreshServices_Click(object s, RoutedEventArgs e) => _ = LoadServicesAsync();

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceGrid.SelectedItem is not ServiceInfo s) return;
        if (!AdminOrOffer()) return;
        await RunServiceAction(() => ServicesManager.Start(s.Name), "Could not start the service");
    }

    private async void StopService_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceGrid.SelectedItem is not ServiceInfo s) return;
        if (s.IsCritical) { Ui.Error("Core service", $"{s.DisplayName} is a core Windows service and is protected."); return; }
        if (!AdminOrOffer()) return;
        if (!Ui.Confirm("Stop service", $"Stop \"{s.DisplayName}\"? Programs that depend on it may stop working until it is started again.", "Stop", danger: true)) return;
        await RunServiceAction(() => ServicesManager.Stop(s.Name), "Could not stop the service");
    }

    private static bool AdminOrOffer()
    {
        if (ProcessUtil.IsElevated) return true;
        if (Ui.Ask("Administrator rights needed", "Starting and stopping services requires administrator rights.", "Restart as administrator", "Cancel") == 0)
            Ui.RestartAsAdministrator();
        return false;
    }

    private async Task RunServiceAction(Action action, string error)
    {
        try { await Task.Run(action); }
        catch (Exception ex) { Ui.Error(error, ex.Message); }
        await LoadServicesAsync();
    }
}
