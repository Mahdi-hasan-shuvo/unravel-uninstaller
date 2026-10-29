using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Apps;
using Unravel.Core.Files;
using Unravel.Core.Programs;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

/// <summary>A duplicate file row; the group label is used for grouping in the grid.</summary>
public sealed class DupRow : INotifyPropertyChanged
{
    public DupRow(DuplicateFile f, DuplicateGroup g, string label) { File = f; Group = g; GroupLabel = label; }
    public DuplicateFile File { get; }
    public DuplicateGroup Group { get; }
    public string GroupLabel { get; }
    public string Path => File.Path;
    public long Size => File.Size;
    public DateTime LastWrite => File.LastWrite;
    public bool IsChecked { get => File.IsChecked; set { File.IsChecked = value; Changed(nameof(IsChecked)); } }
    public void Refresh() => Changed(nameof(IsChecked));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class DuplicatesPage : UserControl, IPage
{
    private readonly ObservableCollection<string> _folders = new();
    private readonly ObservableCollection<DupRow> _dupRows = new();
    private readonly ObservableCollection<OrphanCandidate> _orphans = new();
    private List<DuplicateGroup> _groups = [];
    private CancellationTokenSource? _cts;

    public DuplicatesPage()
    {
        InitializeComponent();
        foreach (var sf in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic })
        {
            var p = Environment.GetFolderPath(sf);
            if (Directory.Exists(p)) _folders.Add(p);
        }
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) _folders.Add(downloads);
        Folders.ItemsSource = _folders;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_dupRows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DupRow.GroupLabel)));
        DupGrid.ItemsSource = view;
        OrphanGrid.ItemsSource = _orphans;
    }

    public void OnShown() { }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (DupPanel is null) return;
        DupPanel.Visibility = TabDup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OrphanPanel.Visibility = TabOrphan.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ------------------------------------------------------------ duplicates

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Add a folder to scan for duplicates", Multiselect = true };
        if (d.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var f in d.FolderNames)
        {
            if (SafetyEngine.IsCriticalPath(f)) { Ui.Error("Not allowed", $"{f} is a protected system location."); continue; }
            if (!_folders.Contains(f, StringComparer.OrdinalIgnoreCase)) _folders.Add(f);
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Folders.SelectedItem is string f) _folders.Remove(f);
    }

    private async void DupScan_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) { Ui.Error("No folders", "Add at least one folder to scan."); return; }
        var options = new DuplicateScanOptions
        {
            Roots = _folders.ToList(),
            MinSize = long.Parse((string)((ComboBoxItem)MinSize.SelectedItem).Tag),
            IncludePatterns = Patterns.Text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } p ? p : ["*"],
        };
        _cts = new CancellationTokenSource();
        DupProgress.Visibility = Visibility.Visible;
        DupScan.IsEnabled = false;
        var progress = new Progress<DuplicateProgress>(pr =>
        {
            DupPhase.Text = pr.Phase;
            DupBar.IsIndeterminate = pr.BytesToHash == 0;
            DupBar.Value = pr.BytesToHash == 0 ? 0 : 100.0 * pr.BytesHashed / pr.BytesToHash;
            DupDetail.Text = pr.BytesToHash == 0 ? $"{pr.FilesSeen:N0} files · {pr.Current}" : $"{pr.Candidates:N0} candidates · {Format.Bytes(pr.BytesHashed)} of {Format.Bytes(pr.BytesToHash)} read";
        });
        DuplicateScanResult r;
        try { r = await DuplicateFinder.FindAsync(options, progress, _cts.Token); }
        catch (Exception ex) { Ui.Error("Scan failed", ex.Message); r = new DuplicateScanResult(); }
        finally { _cts = null; DupProgress.Visibility = Visibility.Collapsed; DupScan.IsEnabled = true; }

        _groups = r.Groups;
        _dupRows.Clear();
        int n = 0;
        foreach (var g in _groups)
        {
            var label = $"Group {++n} · {g.Files.Count} copies of {Format.Bytes(g.Size)} · {Format.Bytes(g.WastedBytes)} reclaimable · SHA-256 {g.Sha256[..12]}…";
            foreach (var f in g.Files) _dupRows.Add(new DupRow(f, g, label));
        }
        DupSummary.Text = r.Cancelled ? "Scan cancelled." :
            _groups.Count == 0 ? $"No duplicates among {r.FilesScanned:N0} files." :
            $"{_groups.Count} groups of identical files · {Format.Bytes(r.WastedBytes)} reclaimable · {r.FilesScanned:N0} files scanned" +
            (r.Unreadable > 0 ? $" · {r.Unreadable} unreadable (in use)" : "") + ". Pick which copy to keep, or check files yourself.";
        UpdateDupButtons();
    }

    private void ApplyRule_Click(object sender, RoutedEventArgs e)
    {
        if (_groups.Count == 0) return;
        string? folder = null;
        var rule = KeepRule.SelectedIndex switch { 1 => Core.Files.KeepRule.Oldest, 2 => Core.Files.KeepRule.ShortestPath, 3 => Core.Files.KeepRule.FirstInFolder, _ => Core.Files.KeepRule.Newest };
        if (rule == Core.Files.KeepRule.FirstInFolder)
        {
            var d = new OpenFolderDialog { Title = "Keep the copy inside this folder" };
            if (d.ShowDialog(Window.GetWindow(this)) != true) return;
            folder = d.FolderName;
        }
        DuplicateFinder.ApplyKeepRule(_groups, rule, folder);
        foreach (var row in _dupRows) row.Refresh();
        UpdateDupButtons();
    }

    private void DupCheck_Click(object sender, RoutedEventArgs e) => UpdateDupButtons();

    private void UpdateDupButtons()
    {
        var chk = _dupRows.Where(r => r.IsChecked).ToList();
        DupDelete.IsEnabled = chk.Count > 0;
        DupDelete.Content = chk.Count > 0 ? $"Remove {chk.Count} checked ({Format.Bytes(chk.Sum(c => c.Size))})…" : "Remove checked…";
    }

    private async void DupDelete_Click(object sender, RoutedEventArgs e)
    {
        var losing = DuplicateFinder.GroupsLosingAllCopies(_groups.Where(g => g.Files.Any(f => f.IsChecked)));
        if (losing.Count > 0)
        {
            Ui.Error("Every copy is checked", $"In {losing.Count} group(s) every copy is checked, so the file would be lost completely. Leave at least one copy unchecked in each group.");
            return;
        }
        var chk = _dupRows.Where(r => r.IsChecked).ToList();
        var ctx = new SafetyEngine.Context { CheckSignatures = false };
        var items = chk.Select(r => new PreviewItem(r.Path, "Duplicate", SafetyEngine.AssessPath(r.Path, FindingCategory.DuplicateFile, true, ctx), r.Size)).ToList();
        if (!DeletionPreviewDialog.Confirm(Window.GetWindow(this), "Remove duplicate files",
                "These copies are byte-for-byte identical to a copy you are keeping (verified with SHA-256).", items, "Move to quarantine",
                "Quarantined files can be restored from Activity & recovery.")) return;
        var paths = items.Where(i => !i.Safety.IsBlocked).Select(i => i.Location).ToList();
        var (set, results) = await Task.Run(() => new Quarantine().Move(paths, "Duplicate files"));
        var done = results.Where(r => r.Ok).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in chk.Where(r => done.Contains(r.Path))) _dupRows.Remove(row);
        foreach (var g in _groups) g.Files.RemoveAll(f => done.Contains(f.Path));
        UpdateDupButtons();
        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count > 0) Ui.Error($"{failed.Count} file(s) were not moved", string.Join("\n", failed.Take(10).Select(f => $"• {f.Path}: {f.Error}")));
        else Ui.Info("Done", $"{done.Count} duplicate(s) moved to quarantine, freeing {Format.Bytes(set.TotalSize)} once purged.");
    }

    private DupRow? SelectedDup => DupGrid.SelectedItem as DupRow;
    private void DupOpen_Click(object s, RoutedEventArgs e) { if (SelectedDup is { } r) Ui.Try("Open", () => Shell.OpenPath(r.Path)); }
    private void DupFolder_Click(object s, RoutedEventArgs e) { if (SelectedDup is { } r) Ui.Try("Open folder", () => Shell.ShowInExplorer(r.Path)); }

    // ------------------------------------------------------------ orphans

    private async void OrphanScan_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        OrphanProgress.Visibility = Visibility.Visible;
        OrphanScan.IsEnabled = false;
        var ct = _cts.Token;
        var progress = new Progress<string>(p => OrphanDetail.Text = p);
        List<OrphanCandidate> found;
        try
        {
            found = await Task.Run(() =>
            {
                var programs = new ProgramInventory().Load(new InventoryOptions { IncludeSystemComponents = true, IncludeUpdates = true });
                var apps = WindowsApps.List(new WindowsAppOptions { IncludeSystemApps = true, IncludeFrameworks = true });
                return OrphanFinder.Find(OrphanEnvironment.CaptureDefault(programs, apps), progress, ct);
            }, ct);
        }
        catch (OperationCanceledException) { found = []; }
        catch (Exception ex) { Ui.Error("Scan failed", ex.Message); found = []; }
        finally { _cts = null; OrphanProgress.Visibility = Visibility.Collapsed; OrphanScan.IsEnabled = true; }
        _orphans.Clear();
        foreach (var o in found) _orphans.Add(o);
        OrphanSummary.Text = found.Count == 0 ? "No orphans found." :
            $"{found.Count(o => o.Kind == OrphanKind.UnownedFolder)} unowned folder(s) ({Format.Bytes(found.Sum(o => o.Size))}) and {found.Count(o => o.Kind == OrphanKind.BrokenShortcut)} broken shortcut(s). Select a row to see why it was listed.";
        OrphanDelete.IsEnabled = false;
    }

    private void OrphanCheck_Click(object sender, RoutedEventArgs e) => OrphanDelete.IsEnabled = _orphans.Any(o => o.IsChecked);

    private async void OrphanDelete_Click(object sender, RoutedEventArgs e)
    {
        var chk = _orphans.Where(o => o.IsChecked).ToList();
        if (chk.Count == 0) return;
        var items = chk.Select(o => new PreviewItem(o.Path, o.KindText, o.Safety, o.Size)).ToList();
        if (!DeletionPreviewDialog.Confirm(Window.GetWindow(this), "Remove orphaned items",
                "Nothing on this PC refers to these items. Check that none of them is a portable program or data you still want.", items,
                "Move to quarantine", "Quarantined items can be restored from Activity & recovery.")) return;
        var (_, results) = await Task.Run(() => new Quarantine().Move(chk.Where(o => !o.Safety.IsBlocked).Select(o => o.Path), "Orphaned items"));
        foreach (var r in results.Where(r => r.Ok))
        {
            var o = _orphans.FirstOrDefault(x => x.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase));
            if (o is not null) _orphans.Remove(o);
        }
        OrphanDelete.IsEnabled = _orphans.Any(o => o.IsChecked);
        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count > 0) Ui.Error($"{failed.Count} item(s) were not moved", string.Join("\n", failed.Take(10).Select(f => $"• {f.Path}: {f.Error}")) +
            (ProcessUtil.IsElevated ? "" : "\n\nFolders in Program Files and ProgramData usually need administrator rights."));
    }

    private OrphanCandidate? SelectedOrphan => OrphanGrid.SelectedItem as OrphanCandidate;
    private void OrphanOpen_Click(object s, RoutedEventArgs e) { if (SelectedOrphan is { } o) Ui.Try("Open", () => Shell.OpenPath(o.Path)); }
    private void OrphanFolder_Click(object s, RoutedEventArgs e) { if (SelectedOrphan is { } o) Ui.Try("Open folder", () => Shell.ShowInExplorer(o.Path)); }
    private void OrphanSearch_Click(object s, RoutedEventArgs e) { if (SelectedOrphan is { } o) Ui.Try("Search", () => Shell.SearchGoogle(System.IO.Path.GetFileName(o.Path) + " folder")); }
}
