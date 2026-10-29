using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Unravel.App.Infrastructure;
using Unravel.Core.Junk;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class DriveChoice : INotifyPropertyChanged
{
    private bool _isChecked;
    public required string Root { get; init; }
    public required string Label { get; init; }
    public string Detail { get; init; } = "";
    public bool IsChecked { get => _isChecked; set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public static List<DriveChoice> Fixed(IEnumerable<string>? selected = null)
    {
        var set = selected?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sys = Path.GetPathRoot(Environment.SystemDirectory);
        return DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d =>
        {
            string label;
            try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local disk" : d.VolumeLabel; } catch { label = "Disk"; }
            return new DriveChoice
            {
                Root = d.Name,
                Label = $"{label} ({d.Name.TrimEnd('\\')})",
                Detail = $"{Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)}",
                IsChecked = set is null || set.Count == 0 ? d.Name.Equals(sys, StringComparison.OrdinalIgnoreCase) : set.Contains(d.Name),
            };
        }).ToList();
    }
}

public sealed class JunkRow : INotifyPropertyChanged
{
    private bool _isChecked = true;
    public JunkRow(JunkFile f) => File = f;
    public JunkFile File { get; }
    public string Name => File.Name;
    public string Folder => File.Folder;
    public long Size => File.Size;
    public string Pattern => File.Pattern;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class JunkPage : UserControl, IPage
{
    private readonly ObservableCollection<JunkRow> _rows = new();
    private List<DriveChoice> _drives = [];
    private CancellationTokenSource? _cts;

    public JunkPage()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
    }

    public void OnShown()
    {
        _drives = DriveChoice.Fixed(Ui.Settings.Junk.Drives);
        DriveList.ItemsSource = _drives;
        var on = Ui.Settings.Junk.Patterns.Where(p => p.Enabled).Select(p => p.Pattern).ToList();
        PatternSummary.Text = on.Count == 0 ? "No patterns enabled." : string.Join("   ", on);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var drives = _drives.Where(d => d.IsChecked).Select(d => d.Root).ToList();
        Ui.Settings.Junk.Drives = drives;
        Ui.SaveSettings();
        JunkScanOptions options;
        try { options = JunkScanOptions.FromSettings(Ui.Settings.Junk, drives); }
        catch (Exception ex) { Ui.Error("Cannot scan", ex.Message); return; }
        if (drives.Count == 0 || options.Patterns.Count == 0) { Ui.Error("Cannot scan", "Select at least one drive and enable at least one file pattern."); return; }

        _cts = new CancellationTokenSource();
        ScanOverlay.Visibility = Visibility.Visible;
        ScanButton.IsEnabled = false;
        _rows.Clear();
        var progress = new Progress<JunkScanProgress>(p => { ScanFolder.Text = p.CurrentFolder; ScanCount.Text = $"{p.FilesFound} files · {Format.Bytes(p.BytesFound)}"; });
        List<JunkFile> found;
        try { found = await Task.Run(() => JunkScanner.Scan(options, progress, _cts.Token)); }
        catch (Exception ex) { Ui.Error("Scan failed", ex.Message); found = []; }
        foreach (var f in found.OrderByDescending(f => f.Size)) _rows.Add(new JunkRow(f));
        ScanOverlay.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;
        UpdateSummary(_cts.IsCancellationRequested);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void UpdateSummary(bool cancelled = false)
    {
        var chk = _rows.Where(r => r.IsChecked).ToList();
        ResultSummary.Text = _rows.Count == 0
            ? (cancelled ? "Scan cancelled – no junk found before stopping." : "No junk files found.")
            : $"{(cancelled ? "Scan cancelled. Partial results: " : "")}{_rows.Count} files, {Format.Bytes(_rows.Sum(r => r.Size))} · {chk.Count} checked ({Format.Bytes(chk.Sum(r => r.Size))})";
        DeleteButton.IsEnabled = chk.Count > 0;
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var chk = _rows.Where(r => r.IsChecked).ToList();
        if (chk.Count == 0) return;
        var recycle = Ui.Settings.Junk.DeleteToRecycleBin;
        if (!Ui.Confirm("Delete junk files", $"Delete {chk.Count} file(s) ({Format.Bytes(chk.Sum(r => r.Size))})?" + (recycle ? "\n\nThey will be moved to the Recycle Bin." : "\n\nThey will be deleted permanently."), "Delete", danger: true))
            return;
        DeleteButton.IsEnabled = false;
        var results = await Task.Run(() => JunkScanner.Delete(chk.Select(r => r.File), recycle));
        var ok = results.Where(r => r.Succeeded).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in chk.Where(r => ok.Contains(r.File.Path))) _rows.Remove(r);
        UpdateSummary();
        var failed = results.Where(r => !r.Succeeded).ToList();
        if (failed.Count > 0)
            Ui.Error($"{failed.Count} file(s) could not be deleted", "These files are in use by Windows or another program, or need administrator rights:\n\n" +
                string.Join("\n", failed.Take(10).Select(f => "• " + f.Path)));
        else Ui.Info("Done", $"{ok.Count} junk file(s) deleted.");
    }

    private JunkRow? Selected => Grid.SelectedItem as JunkRow;
    private void Grid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e) => Folder_Click(s, e);
    private void Open_Click(object s, RoutedEventArgs e) { if (Selected is { } r) Ui.Try("Open", () => Shell.OpenPath(r.File.Path)); }
    private void Search_Click(object s, RoutedEventArgs e) { if (Selected is { } r) Ui.Try("Search", () => Shell.SearchGoogle(r.Name)); }
    private void Folder_Click(object s, RoutedEventArgs e) { if (Selected is { } r) Ui.Try("Open folder", () => Shell.ShowInExplorer(r.File.Path)); }
    private void Properties_Click(object s, RoutedEventArgs e) { if (Selected is { } r) Ui.Try("Properties", () => Shell.ShowProperties(r.File.Path)); }
    private void CheckAll_Click(object s, RoutedEventArgs e) { foreach (var r in _rows) r.IsChecked = true; UpdateSummary(); }
    private void UncheckAll_Click(object s, RoutedEventArgs e) { foreach (var r in _rows) r.IsChecked = false; UpdateSummary(); }
    private void CheckSelected_Click(object s, RoutedEventArgs e) { foreach (var r in Grid.SelectedItems.OfType<JunkRow>()) r.IsChecked = true; UpdateSummary(); }
    private void UncheckSelected_Click(object s, RoutedEventArgs e) { foreach (var r in Grid.SelectedItems.OfType<JunkRow>()) r.IsChecked = false; UpdateSummary(); }
    private void Settings_Click(object s, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Navigate("settings");
}
