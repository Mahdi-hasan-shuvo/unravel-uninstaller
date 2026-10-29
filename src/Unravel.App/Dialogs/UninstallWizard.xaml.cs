using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Unravel.App.Infrastructure;
using Unravel.Core.Leftovers;
using Unravel.Core.Settings;
using Unravel.Core.SystemOps;
using Unravel.Core.Util;

namespace Unravel.App.Dialogs;

public sealed record LogLine(string Glyph, string Text, Brush Brush);

public partial class UninstallWizard : Window
{
    private enum Step { Uninstall, Scan, Registry, Files, Done }

    private readonly UninstallJob _job;
    private readonly ObservableCollection<LogLine> _log = new();
    private Step _step = Step.Uninstall;
    private bool _uninstallerRunning;
    private bool _uninstallPhaseDone;
    private CancellationTokenSource? _waitCts;
    private CancellationTokenSource? _scanCts;
    private ScanResult? _result;
    private int _deletedRegistry, _deletedFiles, _failed, _rebootPending;
    private string? _backupFolder;
    private string? _doneOverride;

    public bool ListMayHaveChanged { get; private set; }

    public UninstallWizard(UninstallJob job)
    {
        InitializeComponent();
        ThemeManager.ApplyChrome(this);
        _job = job;
        Title = $"Uninstall {job.DisplayName}";
        ProgramName.Text = job.DisplayName;
        ProgramPublisher.Text = job.Publisher ?? "";
        ProgramIcon.Source = job.IconFile is not null ? IconCache.FromFile(job.IconFile) : IconCache.FromIconLocation(job.IconLocation);
        LogList.ItemsSource = _log;
        if (Ui.Settings.Uninstaller.OpenWizardMaximized) WindowState = WindowState.Maximized;
        switch (Ui.Settings.Uninstaller.DefaultScanMode)
        {
            case ScanMode.Safe: ModeSafe.IsChecked = true; break;
            case ScanMode.Advanced: ModeAdvanced.IsChecked = true; break;
            default: ModeModerate.IsChecked = true; break;
        }
        BuildStepper();
        ShowStep(Step.Uninstall);
        Loaded += async (_, _) => await RunUninstallPhaseAsync();
        Closing += OnClosing;
    }

    // ------------------------------------------------------------ step 1: built-in uninstaller

    private void Log(string text, string kind = "info")
    {
        var (glyph, brush) = kind switch
        {
            "ok" => ("", "SuccessBrush"),
            "warn" => ("", "WarningBrush"),
            "error" => ("", "DangerBrush"),
            "busy" => ("", "AccentBrush"),
            _ => ("", "MutedTextBrush"),
        };
        _log.Add(new LogLine(glyph, text, (Brush)FindResource(brush)));
    }

    private async Task RunUninstallPhaseAsync()
    {
        var s = Ui.Settings.Uninstaller;
        NextButton.IsEnabled = false;
        BackButton.Visibility = Visibility.Collapsed;

        Log($"Analyzed {_job.DisplayName}: " + (_job.Target.InstallFolder is { } f ? $"installed in {f}" : "no install folder recorded") +
            (_job.Target.Executables.Count > 0 ? $", {_job.Target.Executables.Count} program file(s) noted for the leftover scan." : "."), "ok");

        if (s.CreateRestorePoint)
        {
            Log("Creating a System Restore point…", "busy");
            var rp = await Task.Run(() => RestorePoint.Create($"Unravel: before uninstalling {_job.DisplayName}"));
            _log.RemoveAt(_log.Count - 1);
            Log(rp.Status switch
            {
                RestorePointStatus.Created => "System Restore point created.",
                RestorePointStatus.NotAdministrator => "Restore point skipped: administrator rights are required.",
                RestorePointStatus.Disabled => "Restore point skipped: System Restore is turned off in Windows.",
                _ => "Restore point could not be created: " + rp.Message,
            }, rp.Status == RestorePointStatus.Created ? "ok" : "warn");
        }
        else Log("Restore point creation is turned off in Settings.");

        if (s.StopRunningExecutables && !_job.IsWindowsApp && _job.Target.InstallFolder is { } folder && !ScanTarget.IsBroadFolder(folder))
        {
            var running = ProcessUtil.FindUnderFolder(folder);
            if (running.Count > 0)
            {
                var names = string.Join(", ", running.Select(r => r.Name).Distinct().Take(6));
                if (Ui.Confirm("Close running programs?", $"These programs from the install folder are running: {names}.\n\nThey will be closed so their files can be removed. Unsaved work in them will be lost.", "Close them"))
                {
                    var (killed, failed) = ProcessUtil.Kill(running);
                    Log($"Closed {killed} running process(es)" + (failed > 0 ? $"; {failed} could not be closed." : "."), failed > 0 ? "warn" : "ok");
                }
                else Log("Running processes were left open; some files may not be removable.", "warn");
            }
        }

        Log(_job.IsWindowsApp ? "Removing the app with Windows…" : "Starting the program's built-in uninstaller…", "busy");
        _uninstallerRunning = true;
        WaitCard.Visibility = _job.IsWindowsApp ? Visibility.Collapsed : Visibility.Visible;
        _waitCts = new CancellationTokenSource();
        var progress = new Progress<string>(m => FooterText.Text = m);
        var result = await _job.RunUninstaller(progress, _waitCts.Token);
        _uninstallerRunning = false;
        WaitCard.Visibility = Visibility.Collapsed;
        _log.RemoveAt(_log.Count - 1);
        ListMayHaveChanged = true;

        switch (result.Outcome)
        {
            case UninstallerOutcome.StartFailed:
                Log("The uninstaller could not be run: " + result.Message, "error");
                Log("You can still scan for leftovers – useful when the program is already partly removed.");
                break;
            case UninstallerOutcome.Cancelled:
                Log("The uninstaller was not started: " + result.Message, "warn");
                break;
            case UninstallerOutcome.SkippedWaiting:
                Log("Continuing without waiting for the uninstaller.", "warn");
                break;
            default:
                Log(_job.IsWindowsApp ? "Windows removed the app." : "The built-in uninstaller has finished.", "ok");
                break;
        }
        bool still = await Task.Run(_job.StillInstalled);
        if (result.Outcome == UninstallerOutcome.Finished || result.Outcome == UninstallerOutcome.SkippedWaiting)
            Log(still ? "The program is still registered as installed. Its uninstaller may have been cancelled or may need a restart to finish."
                      : "The program is no longer registered as installed.", still ? "warn" : "ok");
        FooterText.Text = "";
        _uninstallPhaseDone = true;

        if (s.OnlyRunBuiltInUninstaller)
        {
            _doneOverride = "Only the built-in uninstaller was run. Leftover scanning is turned off in Settings – press Back to scan anyway.";
            ShowStep(Step.Done);
            return;
        }
        ModeCard.Visibility = Visibility.Visible;
        ShowStep(Step.Uninstall);
    }

    private void SkipWait_Click(object sender, RoutedEventArgs e) => _waitCts?.Cancel();

    // ------------------------------------------------------------ step 2: scan

    private ScanMode SelectedMode => ModeSafe.IsChecked == true ? ScanMode.Safe : ModeAdvanced.IsChecked == true ? ScanMode.Advanced : ScanMode.Moderate;

    private async Task ScanAsync()
    {
        ShowStep(Step.Scan);
        _scanCts = new CancellationTokenSource();
        var s = Ui.Settings.Uninstaller;
        var env = ScanEnvironment.CreateDefault();
        env.OtherPrograms = _job.OtherPrograms;
        var scanner = new LeftoverScanner(env, s.RegistryExcludes, s.FolderExcludes, s.CheckAllLeftoversByDefault);
        var progress = new Progress<string>(m => ScanStatus.Text = m);
        var mode = SelectedMode;
        try
        {
            _result = await Task.Run(() => scanner.Scan(_job.Target, mode, _scanCts.Token, progress));
        }
        catch (OperationCanceledException)
        {
            ShowStep(Step.Uninstall);
            return;
        }
        catch (Exception e)
        {
            Ui.Error("The scan failed", e.Message);
            ShowStep(Step.Uninstall);
            return;
        }
        RegistryTree.ItemsSource = _result.RegistryRoot.Children;
        FilesGrid.ItemsSource = _result.Files;
        // Keep the "N of M selected" footer current while the user checks items.
        foreach (var n in _result.RegistryRoot.Descendants().Concat(_result.Files))
            n.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(LeftoverNode.IsChecked)) QueueCountRefresh(); };
        if (_result.RegistryCount > 0) ShowStep(Step.Registry);
        else if (_result.Files.Count > 0) ShowStep(Step.Files);
        else
        {
            _doneOverride = $"No leftovers were found ({mode} scan, {_result.Duration.TotalSeconds:0.0} s). Press Back to try a deeper scan.";
            ShowStep(Step.Done);
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private bool _refreshQueued;
    private void QueueCountRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (_step == Step.Registry) RefreshRegistryTitle();
            else if (_step == Step.Files) RefreshFilesTitle();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ------------------------------------------------------------ step 3: registry

    private void RefreshRegistryTitle()
    {
        if (_result is null) return;
        var leftovers = _result.RegistryLeftovers.ToList();
        RegistryTitle.Text = $"Leftover registry items ({leftovers.Count})";
        FooterText.Text = $"{leftovers.Count(n => n.WillDelete)} of {leftovers.Count} selected for deletion";
    }

    /// <summary>Selects only items the safety engine considers safe; low-confidence and risky items stay unchecked.</summary>
    private void RegSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        foreach (var n in _result.RegistryRoot.Children) n.IsChecked = false;
        foreach (var n in _result.RegistryRoot.Descendants().Where(n => n.IsCreated && !n.IsExcluded && n.Safety.IsSafeToAutoSelect).ToList())
            n.IsChecked = true;
        RefreshRegistryTitle();
    }

    private void RegDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        foreach (var n in _result.RegistryRoot.Children) n.IsChecked = false;
        RefreshRegistryTitle();
    }

    private void RegDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        var selected = LeftoverRemover.SelectRegistry(_result.RegistryRoot);
        if (selected.Count == 0) { Ui.Info("Nothing selected", "Check the bold items you want to delete."); return; }
        if (!DeletionPreviewDialog.Confirm(this, "Delete registry items", $"These registry items of {_job.DisplayName} will be deleted.",
                selected.Select(n => new PreviewItem(n.Location, n.Kind == LeftoverKind.RegistryValue ? "Value" : "Key", n.Safety)),
                "Delete", Ui.Settings.Uninstaller.BackupRegistryBeforeDelete ? "A .reg backup is saved first (Settings › Registry backups)." : "Registry backups are turned off in Settings."))
            return;
        DeleteRegistry(_result.RegistryRoot);
    }

    private void DeleteRegistry(LeftoverNode root)
    {
        var report = LeftoverRemover.DeleteRegistry(root, Ui.Settings.Uninstaller.BackupRegistryBeforeDelete ? new RegistryBackup() : null, _job.DisplayName);
        _backupFolder ??= report.BackupFolder;
        if (report.BackupError is not null)
        {
            Ui.Error("Nothing was deleted", "The registry backup failed, so no items were deleted.\n\n" + report.BackupError);
            return;
        }
        _deletedRegistry += report.Succeeded;
        _failed += report.Failed;
        var deleted = report.Items.Where(i => i.Ok).Select(i => i.Location).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Prune(_result!.RegistryRoot, deleted);
        RefreshRegistryTitle();
        if (report.Failed > 0)
        {
            var hint = ProcessUtil.IsElevated ? "" : "\n\nMost likely these need administrator rights. Restart Unravel as administrator and run the uninstall wizard again for this program to remove them.";
            Ui.Error($"{report.Failed} item(s) could not be deleted", string.Join("\n", report.Items.Where(i => !i.Ok).Take(12).Select(i => $"• {i.Location}: {i.Error}")) + hint);
        }
        if (_result.RegistryCount == 0) Next_Click(this, new RoutedEventArgs());
    }

    /// <summary>Removes deleted nodes (and their now-empty context parents) from the tree.</summary>
    private static void Prune(LeftoverNode node, HashSet<string> deleted)
    {
        foreach (var child in node.Children.ToList())
        {
            if (deleted.Contains(child.Location)) { node.Children.Remove(child); continue; }
            Prune(child, deleted);
            if (!child.IsCreated && child.Children.Count == 0 && child.Kind != LeftoverKind.Container) node.Children.Remove(child);
        }
    }

    private LeftoverNode? SelectedNode => RegistryTree.SelectedItem as LeftoverNode;

    private void ExpandAll_Click(object s, RoutedEventArgs e) { if (_result is not null) foreach (var n in _result.RegistryRoot.Descendants()) n.IsExpanded = true; }
    private void CollapseAll_Click(object s, RoutedEventArgs e) { if (_result is not null) foreach (var n in _result.RegistryRoot.Descendants()) n.IsExpanded = false; }
    private void ExpandSelected_Click(object s, RoutedEventArgs e) { if (SelectedNode is { } n) foreach (var d in n.SelfAndDescendants()) d.IsExpanded = true; }
    private void CollapseSelected_Click(object s, RoutedEventArgs e) { if (SelectedNode is { } n) foreach (var d in n.SelfAndDescendants()) d.IsExpanded = false; }

    private void DeleteSelectedKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n || n.Key is null) return;
        if (n.IsExcluded) { Ui.Error("Excluded item", "This item is on the exclude list and is protected from deletion."); return; }
        var warn = n.IsCreated ? $"Delete \"{n.Location}\" now?" :
            $"\"{n.Location}\" was NOT identified as created by {_job.DisplayName}.\n\nDeleting it may affect other programs. Only continue if you are sure it belongs to this program.";
        if (!Ui.ConfirmAlways("Delete registry item", warn, "Delete")) return;
        var wasCreated = n.IsCreated;
        n.IsCreated = true;
        // Delete just this node: temporarily make it the only checked item.
        var states = _result!.RegistryRoot.Descendants().ToDictionary(x => x, x => x.IsChecked);
        foreach (var x in _result.RegistryRoot.Descendants()) x.IsChecked = false;
        n.IsChecked = true;
        DeleteRegistry(_result.RegistryRoot);
        foreach (var (node, state) in states) if (!ReferenceEquals(node, n)) node.IsChecked = state;
        if (n.Key.Exists()) n.IsCreated = wasCreated;
    }

    private void MarkCreated_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n || n.Kind is LeftoverKind.Container) return;
        if (n.Key is { } k && (n.Key.Depth <= 1 || new LeftoverScanner(ScanEnvironment.CreateDefault()).IsProtectedKey(k)))
        {
            Ui.Error("Protected key", "This key is a system location and cannot be marked for deletion.");
            return;
        }
        if (!Ui.Confirm("Mark as created", $"Mark \"{n.Location}\" as created by {_job.DisplayName}?\n\nOnly do this if you are sure it belongs to this program – it will be deleted together with the other checked items.", "Mark as created", danger: true))
            return;
        n.IsCreated = true;
        n.Note = null;
        RefreshRegistryTitle();
    }

    private void ExcludeKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { Key: not null } n) return;
        var path = n.Key.FullPath;
        var rules = Ui.Settings.Uninstaller.RegistryExcludes;
        if (!rules.Any(r => r.Pattern.Equals(path, StringComparison.OrdinalIgnoreCase)))
            rules.Add(new ExcludeRule { Pattern = path, IncludeSubkeys = true, Enabled = true });
        Ui.SaveSettings();
        foreach (var d in n.SelfAndDescendants()) { d.IsExcluded = true; d.IsChecked = false; }
        RefreshRegistryTitle();
    }

    private void OpenKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode?.Key is { } k) Ui.Try("Open Registry Editor", () => Shell.OpenRegedit(k.FullPath));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        Ui.ExportAndOpen("Export leftovers", $"Leftovers - {_job.DisplayName}.html", "HTML file|*.html",
            _ => LeftoverRemover.ExportHtml(_job.DisplayName, _result.RegistryRoot, _result.Files));
    }

    // ------------------------------------------------------------ step 4: files

    private void RefreshFilesTitle()
    {
        if (_result is null) return;
        var files = _result.Files;
        FilesTitle.Text = $"Leftover files and folders ({files.Count})";
        FilesHint.Text = $"{Format.Bytes(_result.FilesSize)} in total. " +
            (Ui.Settings.Uninstaller.DeleteFilesToRecycleBin ? "Deleted items go to the Recycle Bin." : "Deleted items are removed permanently (Recycle Bin is off in Settings).");
        FooterText.Text = $"{files.Count(f => f.WillDelete)} of {files.Count} selected for deletion";
    }

    private void FileSelectAll_Click(object s, RoutedEventArgs e) { if (_result is null) return; foreach (var f in _result.Files) f.IsChecked = !f.IsExcluded && f.IsCreated && f.Safety.IsSafeToAutoSelect; RefreshFilesTitle(); }
    private void FileDeselectAll_Click(object s, RoutedEventArgs e) { if (_result is null) return; foreach (var f in _result.Files) f.IsChecked = false; RefreshFilesTitle(); }
    private void FileCheckSelected_Click(object s, RoutedEventArgs e) { foreach (var f in FilesGrid.SelectedItems.OfType<LeftoverNode>()) if (!f.IsExcluded) f.IsChecked = true; RefreshFilesTitle(); }
    private void FileUncheckSelected_Click(object s, RoutedEventArgs e) { foreach (var f in FilesGrid.SelectedItems.OfType<LeftoverNode>()) f.IsChecked = false; RefreshFilesTitle(); }

    private void FileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        var selected = LeftoverRemover.SelectFiles(_result.Files);
        var notCreated = _result.Files.Count(f => f.IsChecked && !f.IsCreated);
        if (selected.Count == 0)
        {
            Ui.Info("Nothing selected", notCreated > 0 ? "Only bold items can be deleted. The checked items were not identified as created by this program." : "Check the items you want to delete.");
            return;
        }
        var recycle = Ui.Settings.Uninstaller.DeleteFilesToRecycleBin;
        if (!DeletionPreviewDialog.Confirm(this, "Delete files and folders", $"These files and folders of {_job.DisplayName} will be deleted.",
                selected.Select(f => new PreviewItem(f.Location, f.Kind == LeftoverKind.Folder ? "Folder" : "File", f.Safety, f.Size)),
                "Delete", recycle ? "Items go to the Recycle Bin." : "Items are deleted permanently (Recycle Bin is off in Settings)."))
            return;
        var report = LeftoverRemover.DeleteFiles(_result.Files, recycle, scheduleLocked: ProcessUtil.IsElevated);
        _deletedFiles += report.Items.Count(i => i.Ok && i.FileOutcome != DeleteOutcome.ScheduledForReboot);
        _rebootPending += report.ScheduledForReboot;
        _failed += report.Failed;
        foreach (var done in report.Items.Where(i => i.Ok && i.FileOutcome != DeleteOutcome.ScheduledForReboot))
            foreach (var row in _result.Files.Where(f => f.Location.Equals(done.Location, StringComparison.OrdinalIgnoreCase)).ToList())
                _result.Files.Remove(row);
        RefreshFilesTitle();
        if (report.ScheduledForReboot > 0)
            Ui.Info("Restart required", $"{report.ScheduledForReboot} item(s) are in use and will be deleted the next time Windows starts.");
        if (report.Failed > 0)
            Ui.Error($"{report.Failed} item(s) could not be deleted",
                string.Join("\n", report.Items.Where(i => !i.Ok).Take(12).Select(i => $"• {i.Location}: {i.Error}")) +
                (ProcessUtil.IsElevated ? "" : "\n\nFiles in use or in protected folders need administrator rights."));
    }

    private LeftoverNode? SelectedFile => FilesGrid.SelectedItem as LeftoverNode;
    private void FileOpen_Click(object s, RoutedEventArgs e) { if (SelectedFile?.FsPath is { } p) Ui.Try("Open", () => Shell.OpenPath(p)); }
    private void FileFolder_Click(object s, RoutedEventArgs e) { if (SelectedFile?.FsPath is { } p) Ui.Try("Open folder", () => Shell.ShowInExplorer(p)); }
    private void FileProperties_Click(object s, RoutedEventArgs e) { if (SelectedFile?.FsPath is { } p) Ui.Try("Properties", () => Shell.ShowProperties(p)); }
    private void FilesGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e) => FileFolder_Click(s, e);

    private void FileExclude_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile?.FsPath is not { } p) return;
        var folder = Directory.Exists(p) ? p : Path.GetDirectoryName(p)!;
        var rules = Ui.Settings.Uninstaller.FolderExcludes;
        if (!rules.Any(r => r.Pattern.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            rules.Add(new ExcludeRule { Pattern = folder, Enabled = true });
        Ui.SaveSettings();
        foreach (var f in _result!.Files.Where(f => FileOps.IsSameOrUnder(f.FsPath!, folder))) { f.IsExcluded = true; f.IsChecked = false; }
        RefreshFilesTitle();
    }

    // ------------------------------------------------------------ navigation

    private void BuildStepper()
    {
        Stepper.Children.Clear();
        string[] names = ["Uninstall", "Scan", "Registry", "Files", "Done"];
        for (int i = 0; i < names.Length; i++)
        {
            var current = (int)_step;
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 0) };
            var num = new TextBlock { Text = i < current ? "" : (i + 1).ToString(), FontSize = i < current ? 10 : 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            if (i < current) num.FontFamily = (FontFamily)FindResource("IconFont");
            if (i <= current) { dot.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); num.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush"); }
            else { dot.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush"); num.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); }
            dot.Child = num;
            var label = new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, i <= current ? "TextBrush" : "MutedTextBrush");
            Stepper.Children.Add(dot);
            Stepper.Children.Add(label);
        }
    }

    private void ShowStep(Step step)
    {
        _step = step;
        StepUninstall.Visibility = step == Step.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        StepScan.Visibility = step == Step.Scan ? Visibility.Visible : Visibility.Collapsed;
        StepRegistry.Visibility = step == Step.Registry ? Visibility.Visible : Visibility.Collapsed;
        StepFiles.Visibility = step == Step.Files ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = step == Step.Done ? Visibility.Visible : Visibility.Collapsed;
        BuildStepper();
        FooterText.Text = "";
        BackButton.Visibility = step is Step.Registry or Step.Files or Step.Done && _uninstallPhaseDone ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = step is Step.Scan or Step.Done ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Content = step == Step.Done ? "Finish" : "Close";
        switch (step)
        {
            case Step.Uninstall:
                NextButton.Content = "Scan for leftovers";
                NextButton.IsEnabled = _uninstallPhaseDone;
                break;
            case Step.Registry:
                NextButton.Content = _result!.Files.Count > 0 ? "Next: files" : "Finish";
                NextButton.IsEnabled = true;
                RefreshRegistryTitle();
                break;
            case Step.Files:
                NextButton.Content = "Finish";
                NextButton.IsEnabled = true;
                RefreshFilesTitle();
                break;
            case Step.Done:
                ShowSummary();
                break;
        }
    }

    private void ShowSummary()
    {
        OpenBackupButton.Visibility = _backupFolder is not null && Directory.Exists(_backupFolder) ? Visibility.Visible : Visibility.Collapsed;
        if (_doneOverride is not null)
        {
            DoneTitle.Text = _deletedRegistry + _deletedFiles > 0 ? "Uninstall complete" : "Done";
            DoneText.Text = _doneOverride;
            _doneOverride = null;
            return;
        }
        DoneTitle.Text = _failed > 0 ? "Finished with some items left behind" : "Uninstall complete";
        DoneGlyph.Text = _failed > 0 ? "" : "";
        var parts = new List<string> { $"{_deletedRegistry} registry item(s) and {_deletedFiles} file/folder item(s) removed." };
        var remaining = (_result?.RegistryCount ?? 0) + (_result?.FileCount ?? 0);
        if (remaining > 0) parts.Add($"{remaining} found item(s) were kept.");
        if (_failed > 0) parts.Add($"{_failed} item(s) could not be removed.");
        if (_rebootPending > 0) parts.Add($"{_rebootPending} item(s) will be removed after a restart.");
        DoneText.Text = string.Join(" ", parts);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Uninstall:
                await ScanAsync();
                break;
            case Step.Registry:
                if (sender == NextButton && _result!.RegistryLeftovers.Any(n => n.WillDelete) &&
                    !Ui.Confirm("Skip registry items?", "Some registry items are selected but were not deleted. Continue to files without deleting them?", "Continue"))
                    return;
                if (_result!.Files.Count > 0) ShowStep(Step.Files); else ShowStep(Step.Done);
                break;
            case Step.Files:
                if (sender == NextButton && _result!.Files.Any(f => f.WillDelete) &&
                    !Ui.Confirm("Skip file items?", "Some files and folders are selected but were not deleted. Finish without deleting them?", "Finish"))
                    return;
                ShowStep(Step.Done);
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ModeCard.Visibility = Visibility.Visible;
        ShowStep(Step.Uninstall);
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_backupFolder is not null) Ui.Try("Open folder", () => Shell.ShowInExplorer(_backupFolder));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_uninstallerRunning && !Ui.ConfirmAlways("Close the wizard?", "The program's uninstaller is still running. Close this window anyway? Leftovers will not be scanned.", "Close", danger: false))
        {
            e.Cancel = true;
            return;
        }
        if (_step is Step.Registry or Step.Files && _result is not null && (_result.RegistryCount + _result.Files.Count(f => f.IsCreated)) > 0 &&
            !Ui.Confirm("Leftovers remain", "Leftovers were found but not all were deleted. Close anyway?", "Close"))
        {
            e.Cancel = true;
            return;
        }
        _waitCts?.Cancel();
        _scanCts?.Cancel();
    }
}
