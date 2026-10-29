using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Safety;
using Unravel.Core.SystemOps;
using Unravel.Core.Tracking;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public partial class InstallTrackerPage : UserControl, IPage
{
    private readonly InstallTracker _tracker = new();
    private readonly ObservableCollection<InstallLog> _logs = new();
    private readonly ObservableCollection<TrackedChange> _changes = new();
    private readonly ICollectionView _changeView;
    private InstallLog? _current;
    private InstallLog? _pending;
    private CancellationTokenSource? _cts;
    private string _area = "";

    public InstallTrackerPage()
    {
        InitializeComponent();
        Logs.ItemsSource = _logs;
        _changeView = CollectionViewSource.GetDefaultView(_changes);
        _changeView.Filter = o => o is TrackedChange c && (_area.Length == 0 || c.Area.ToString().StartsWith(_area, StringComparison.Ordinal));
        ChangeGrid.ItemsSource = _changeView;
        ShowLog(null);
    }

    public void OnShown() => ReloadLogs();

    private void ReloadLogs(string? select = null)
    {
        var id = select ?? _current?.Id;
        _logs.Clear();
        foreach (var l in _tracker.List()) _logs.Add(l);
        _pending ??= _logs.FirstOrDefault(l => l.State == SessionState.WaitingForInstall);
        if (_pending is not null) ShowWaiting(_pending);
        Logs.SelectedItem = _logs.FirstOrDefault(l => l.Id == id);
    }

    private void ShowWaiting(InstallLog log)
    {
        _pending = log;
        BeginButton.IsEnabled = false;
        ProgramName.Text = log.ProgramName;
        WaitingPanel.Visibility = Visibility.Visible;
        WaitingText.Text = $"Snapshot taken {log.Started:t}. Now install \"{log.ProgramName}\" (or run its installer with the button below). Avoid installing anything else meanwhile.";
    }

    private void SetBusy(bool busy, string text = "")
    {
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = text;
        BeginButton.IsEnabled = !busy && _pending is null;
        CompleteButton.IsEnabled = RunInstallerButton.IsEnabled = !busy;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private Progress<SnapshotProgress> SnapshotProgress() =>
        new(p => ProgressText.Text = $"{p.Phase}: {p.Items:N0} items · {p.Current}");

    private async void Begin_Click(object sender, RoutedEventArgs e)
    {
        var name = ProgramName.Text.Trim();
        if (name.Length == 0) { Ui.Error("Name required", "Enter the name of the program you are about to install."); return; }
        _cts = new CancellationTokenSource();
        SetBusy(true, "Taking the before-snapshot…");
        try
        {
            var progress = SnapshotProgress();
            var ct = _cts.Token;
            var log = await Task.Run(() => _tracker.Begin(name, SnapshotOptions.CreateDefault(), progress, ct));
            ReloadLogs();
            ShowWaiting(log);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.Error("Snapshot failed", ex.Message); }
        finally { _cts = null; SetBusy(false); }
    }

    private async void RunInstaller_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Choose the installer", Filter = "Installers|*.exe;*.msi;*.msix;*.appx;*.bat;*.cmd|All files|*.*" };
        if (d.ShowDialog(Window.GetWindow(this)) != true) return;
        _cts = new CancellationTokenSource();
        SetBusy(true, "Waiting for the installer to finish (press Cancel to stop waiting)…");
        try
        {
            var proc = Shell.Run(d.FileName);
            if (proc is not null) await ProcessUtil.WaitForProcessTreeAsync(proc.Id, _cts.Token);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { Ui.Error("Could not run the installer", ex.Message); }
        finally { _cts = null; SetBusy(false); }
        if (Ui.Ask("Installer finished", "The installer has closed. Compare the system now?", "Compare now", "Later") == 0)
            Complete_Click(sender, e);
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null) return;
        _cts = new CancellationTokenSource();
        SetBusy(true, "Taking the after-snapshot and comparing…");
        try
        {
            var log = _pending;
            var progress = SnapshotProgress();
            var ct = _cts.Token;
            await Task.Run(() => _tracker.Complete(log, SnapshotOptions.CreateDefault(), progress, ct));
            _pending = null;
            WaitingPanel.Visibility = Visibility.Collapsed;
            ProgramName.Text = "";
            ReloadLogs(log.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.Error("Comparison failed", ex.Message); }
        finally { _cts = null; SetBusy(false); }
    }

    private void Logs_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowLog(Logs.SelectedItem as InstallLog);

    private void ShowLog(InstallLog? log)
    {
        _current = log;
        _changes.Clear();
        Chips.Children.Clear();
        var completed = log is not null && log.State != SessionState.WaitingForInstall;
        ExportButton.IsEnabled = completed;
        CleanButton.IsEnabled = completed && log!.Added.Any();
        UndoButton.IsEnabled = log?.Cleanup is { Undone: false };
        if (log is null) { ResultTitle.Text = "Select or record an install"; return; }
        ResultTitle.Text = log.State == SessionState.WaitingForInstall ? $"{log.ProgramName} – waiting for the install to finish" : log.ProgramName;
        foreach (var c in log.Changes.OrderBy(c => c.Type).ThenBy(c => c.Area).ThenBy(c => c.Location)) _changes.Add(c);
        void Chip(string text, string bg, string fg)
        {
            var t = new TextBlock { Text = text, FontSize = 12 };
            t.SetResourceReference(TextBlock.ForegroundProperty, fg);
            var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4), Child = t };
            b.SetResourceReference(Border.BackgroundProperty, bg);
            Chips.Children.Add(b);
        }
        Chip($"{log.Changes.Count(c => c.Type == ChangeType.Added)} added", "SuccessSoftBrush", "SuccessBrush");
        Chip($"{log.Changes.Count(c => c.Type == ChangeType.Modified)} modified", "WarningSoftBrush", "WarningBrush");
        Chip($"{log.Changes.Count(c => c.Type == ChangeType.Removed)} removed", "DangerSoftBrush", "DangerBrush");
        if (log.Cleanup is { } cl) Chip(cl.Undone ? "Cleanup undone" : $"Cleaned up {cl.When:g}: {cl.Removed} removed", "SurfaceAltBrush", "TextBrush");
    }

    private void Area_Checked(object sender, RoutedEventArgs e)
    {
        _area = (sender as RadioButton)?.Tag as string ?? "";
        _changeView?.Refresh();
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } log) return;
        var plan = await Task.Run(() => InstallTracker.PlanCleanup(log));
        if (plan.Count == 0) { Ui.Info("Nothing to remove", "Nothing the install added still exists."); return; }
        var items = plan.ToDictionary(i => new PreviewItem(i.Change.Location, i.Change.Area.ToString(), i.Safety), i => i);
        // Anything else that changed while the install was recorded is in the log too, so the user picks what to remove.
        var chosen = DeletionPreviewDialog.Select(Window.GetWindow(this), $"Remove what {log.ProgramName} added",
                "These items appeared while the install was recorded. Untick anything another program may have created at the same time. Modified items that existed before are never touched.",
                items.Keys, "Remove checked", "Files go to quarantine, registry items and tasks are backed up – Undo cleanup puts everything back.");
        if (chosen is null) return;
        if (chosen.Count == 0) { Ui.Info("Nothing selected", "No items were checked."); return; }
        foreach (var i in plan) i.IsChecked = false;
        foreach (var c in chosen) items[c].IsChecked = true;
        var record = await Task.Run(() => _tracker.Clean(log, plan, new Quarantine(), new RegistryBackup()));
        ReloadLogs(log.Id);
        if (record.Failures.Count > 0)
            Ui.Error($"{record.Failures.Count} item(s) could not be removed", string.Join("\n", record.Failures.Take(12).Select(f => "• " + f)) +
                (ProcessUtil.IsElevated ? "" : "\n\nItems in Program Files, HKEY_LOCAL_MACHINE and services need administrator rights."));
        else Ui.Info("Removed", $"{record.Removed} item(s) added by {log.ProgramName} were removed. Use Undo cleanup to restore them.");
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { Cleanup: not null } log) return;
        if (!Ui.Confirm("Undo cleanup", $"Restore everything the cleanup of {log.ProgramName} removed?", "Restore")) return;
        try
        {
            var problems = await Task.Run(() => _tracker.Undo(log, new Quarantine(), new RegistryBackup()));
            if (problems.Count > 0) Ui.Error("Restored with problems", string.Join("\n", problems.Take(12)));
            else Ui.Info("Restored", "Everything the cleanup removed was put back.");
        }
        catch (Exception ex) { Ui.Error("Undo failed", ex.Message); }
        ReloadLogs(log.Id);
    }

    private void DeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (Logs.SelectedItem is not InstallLog log) return;
        if (!Ui.Confirm("Delete install log", $"Delete the record of \"{log.ProgramName}\"? The uninstaller will no longer be able to use it.", "Delete", danger: true)) return;
        if (Ui.Try("Delete log", () => _tracker.Delete(log)))
        {
            if (_pending?.Id == log.Id) { _pending = null; WaitingPanel.Visibility = Visibility.Collapsed; BeginButton.IsEnabled = true; }
            ReloadLogs();
            ShowLog(null);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } log) return;
        Ui.ExportAndOpen("Export install log", $"Install log - {log.ProgramName}.html", "HTML file|*.html", _ =>
        {
            var sb = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>Install log</title><style>body{font-family:Segoe UI,sans-serif;margin:24px}td,th{padding:4px 10px;border-bottom:1px solid #ddd;font-size:13px;text-align:left}</style></head><body>");
            sb.Append($"<h1>{WebUtility.HtmlEncode(log.ProgramName)}</h1><p>Recorded {log.Started:g} – {log.Completed:g}</p><table><tr><th>Change</th><th>What</th><th>Location</th><th>Details</th></tr>");
            foreach (var c in log.Changes)
                sb.Append($"<tr><td>{c.Type}</td><td>{c.Area}</td><td>{WebUtility.HtmlEncode(c.Location)}</td><td>{WebUtility.HtmlEncode(c.Detail)}</td></tr>");
            return sb.Append("</table></body></html>").ToString();
        });
    }

    private TrackedChange? SelectedChange => ChangeGrid.SelectedItem as TrackedChange;

    private void OpenChange_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedChange is not { } c) return;
        Ui.Try("Open location", () =>
        {
            if (c.Area is ChangeArea.File or ChangeArea.Folder) Shell.ShowInExplorer(c.Location);
            else if (c.Area is ChangeArea.RegistryKey) Shell.OpenRegedit(c.Location);
            else if (c.Area is ChangeArea.RegistryValue && c.KeyPath is not null) Shell.OpenRegedit(c.KeyPath);
            else if (c.Area == ChangeArea.ScheduledTask) Shell.Run("taskschd.msc");
            else if (c.Area == ChangeArea.Service) Shell.Run("services.msc");
        });
    }

    private void CopyChange_Click(object sender, RoutedEventArgs e) { if (SelectedChange is { } c) Ui.Try("Copy", () => Clipboard.SetText(c.Location)); }
}
