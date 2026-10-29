using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Autorun;
using Unravel.Core.Hunter;
using Unravel.Core.Programs;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class StartupItem : INotifyPropertyChanged
{
    private readonly Func<StartupItem, bool, bool> _setEnabled;
    public StartupItem(AutorunEntry e, Func<StartupItem, bool, bool> setEnabled) { Entry = e; _setEnabled = setEnabled; }
    public AutorunEntry Entry { get; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Publisher => Entry.Publisher ?? "";
    public string Description => Entry.Description ?? "";
    public string Source => Entry.SourceText;
    public string Scope => Entry.ScopeText;
    public string StatusText => Entry.StatusText;
    public ImageSource? Icon { get; set; }
    public Brush StatusBackground => (Brush)Application.Current.FindResource(Entry.Status switch
    {
        AutorunStatus.Running => "SuccessSoftBrush", AutorunStatus.Invalid => "DangerSoftBrush", _ => "SurfaceAltBrush" });
    public Brush StatusForeground => (Brush)Application.Current.FindResource(Entry.Status switch
    {
        AutorunStatus.Running => "SuccessBrush", AutorunStatus.Invalid => "DangerBrush", _ => "MutedTextBrush" });

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (value == Entry.Enabled) return;
            if (_setEnabled(this, value)) OnChanged();
            else OnChanged(); // revert the switch
        }
    }

    public void StatusChanged() { OnChanged(nameof(StatusText)); OnChanged(nameof(StatusBackground)); OnChanged(nameof(StatusForeground)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class StartupPage : UserControl, IPage
{
    private readonly ObservableCollection<StartupItem> _items = new();
    private readonly ICollectionView _view;
    private AutorunManager _manager;
    private bool _loaded;

    public StartupPage()
    {
        InitializeComponent();
        _manager = CreateManager();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(StartupItem.Name), ListSortDirection.Ascending));
        _view.Filter = o => o is StartupItem i &&
            (ShowServices.IsChecked == true || i.Entry.Source != AutorunSource.Service) &&
            (SearchBox.Text.Length == 0 || i.Name.Contains(SearchBox.Text, StringComparison.CurrentCultureIgnoreCase) ||
             i.Command.Contains(SearchBox.Text, StringComparison.CurrentCultureIgnoreCase) || i.Publisher.Contains(SearchBox.Text, StringComparison.CurrentCultureIgnoreCase));
        Grid.ItemsSource = _view;
        Grid.RowHeight = Ui.Settings.CompactRows ? 32 : 40;
        UpdateButtons();
    }

    private AutorunManager CreateManager() =>
        new(AutorunEnvironment.CreateDefault(), Ui.Settings.DisabledServiceStartTypes) { ShowMicrosoftServices = ShowMicrosoft?.IsChecked == true };

    public void OnShown()
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
    }

    private async Task LoadAsync()
    {
        LoadingState.Visibility = Visibility.Visible;
        List<AutorunEntry> entries;
        try { entries = await Task.Run(_manager.Load); }
        catch (Exception e) { LoadingState.Visibility = Visibility.Collapsed; Ui.Error("Could not read startup items", e.Message); return; }
        var selected = Selected?.Entry.Location + "|" + Selected?.Name;
        _items.Clear();
        foreach (var e in entries) _items.Add(new StartupItem(e, SetEnabled) { Icon = IconCache.FromFile(e.TargetPath) });
        LoadingState.Visibility = Visibility.Collapsed;
        Grid.SelectedItem = _items.FirstOrDefault(i => i.Entry.Location + "|" + i.Name == selected);
        UpdateCount();
    }

    private void UpdateCount()
    {
        var visible = _view.Cast<StartupItem>().ToList();
        CountText.Text = $"{visible.Count} items · {visible.Count(i => i.Enabled)} enabled · {visible.Count(i => i.Entry.Status == AutorunStatus.Running)} running";
    }

    private bool SetEnabled(StartupItem item, bool enabled)
    {
        var ok = Ui.Try(enabled ? "Could not enable the item" : "Could not disable the item", () => _manager.SetEnabled(item.Entry, enabled));
        if (ok)
        {
            Ui.SaveSettings(); // keeps service start types that were changed
            UpdateCount();
        }
        else if (!ProcessUtil.IsElevated && item.Entry.Scope == AutorunScope.AllUsers)
        {
            if (Ui.Ask("Administrator rights needed", "Items that start for all users can only be changed by an administrator.", "Restart as administrator", "Cancel") == 0)
                Ui.RestartAsAdministrator();
        }
        return ok;
    }

    private StartupItem? Selected => Grid.SelectedItem as StartupItem;

    private void UpdateButtons()
    {
        var e = Selected?.Entry;
        RunButton.IsEnabled = e is not null && e.Status == AutorunStatus.NotRunning;
        KillButton.IsEnabled = e is not null && e.Status == AutorunStatus.Running;
        RemoveButton.IsEnabled = e is not null && e.Source != AutorunSource.Service;
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Search_Changed(object sender, TextChangedEventArgs e) { _view?.Refresh(); if (_view is not null) UpdateCount(); }
    private void Filter_Click(object sender, RoutedEventArgs e) { _view.Refresh(); UpdateCount(); }
    private void ShowMicrosoft_Click(object sender, RoutedEventArgs e) { _manager = CreateManager(); _ = LoadAsync(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var d = new AddStartupDialog { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() != true) return;
        if (Ui.Try("Could not add the startup item", () => _manager.Add(d.EntryName, d.TargetPath, d.Arguments, d.Scope, d.Source)))
            _ = LoadAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } i) return;
        if (Ui.Try("Could not run the program", () => AutorunManager.Run(i.Entry)))
            _ = RefreshStatusLater(i);
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } i) return;
        if (!Ui.Confirm("End process", $"Close \"{i.Name}\"? Any unsaved data in it will be lost.", "End process", danger: true)) return;
        Ui.Try("Could not end the process", () =>
        {
            var (_, failed) = AutorunManager.Kill(i.Entry);
            if (failed > 0) throw new InvalidOperationException("Access denied. The process may belong to another user or need administrator rights.");
        });
        _ = RefreshStatusLater(i);
    }

    private async Task RefreshStatusLater(StartupItem i)
    {
        await Task.Delay(1200);
        i.Entry.Status = await Task.Run(() => AutorunManager.RefreshStatus(i.Entry));
        i.StatusChanged();
        UpdateButtons();
        UpdateCount();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } i) return;
        if (!Ui.Confirm("Remove startup item", $"Remove \"{i.Name}\" from startup?\n\nTip: switching it off keeps it in this list so you can turn it back on later.", "Remove", danger: true)) return;
        if (Ui.Try("Could not remove the item", () => _manager.Remove(i.Entry))) _ = LoadAsync();
    }

    private void RemoveWhere(string what, Func<StartupItem, bool> predicate)
    {
        var targets = _items.Where(i => i.Entry.Source != AutorunSource.Service && predicate(i)).ToList();
        if (targets.Count == 0) { Ui.Info("Nothing to remove", $"There are no {what} items."); return; }
        if (!Ui.Confirm("Remove startup items", $"Remove all {targets.Count} {what} item(s)?\n\n" + string.Join("\n", targets.Take(10).Select(t => "• " + t.Name)), "Remove", danger: true)) return;
        var errors = new List<string>();
        foreach (var t in targets)
        {
            try { _manager.Remove(t.Entry); } catch (Exception ex) { errors.Add($"{t.Name}: {ex.Message}"); }
        }
        if (errors.Count > 0) Ui.Error("Some items were not removed", string.Join("\n", errors));
        _ = LoadAsync();
    }

    private void RemoveInvalid_Click(object s, RoutedEventArgs e) => RemoveWhere("invalid", i => i.Entry.Status == AutorunStatus.Invalid);
    private void RemoveNotRunning_Click(object s, RoutedEventArgs e) => RemoveWhere("not running", i => i.Entry.Status == AutorunStatus.NotRunning);
    private void RemoveDisabled_Click(object s, RoutedEventArgs e) => RemoveWhere("disabled", i => !i.Enabled);

    private void SearchName_Click(object s, RoutedEventArgs e) { if (Selected is { } i) Ui.Try("Search", () => Shell.SearchGoogle(i.Name)); }
    private void SearchFile_Click(object s, RoutedEventArgs e) { if (Selected?.Entry.TargetPath is { } p) Ui.Try("Search", () => Shell.SearchGoogle(Path.GetFileName(p))); }
    private void SearchPublisher_Click(object s, RoutedEventArgs e) { if (Selected is { Publisher.Length: > 0 } i) Ui.Try("Search", () => Shell.SearchGoogle(i.Publisher)); }
    private void SearchDescription_Click(object s, RoutedEventArgs e) { if (Selected is { Description.Length: > 0 } i) Ui.Try("Search", () => Shell.SearchGoogle(i.Description)); }
    private void Folder_Click(object s, RoutedEventArgs e) { if (Selected?.Entry.TargetPath is { } p) Ui.Try("Open folder", () => Shell.ShowInExplorer(p)); }
    private void Copy_Click(object s, RoutedEventArgs e) { if (Selected is { } i) Ui.Try("Copy", () => Clipboard.SetText(i.Command)); }
    private void Properties_Click(object s, RoutedEventArgs e) { if (Selected?.Entry.TargetPath is { } p) Ui.Try("Properties", () => Shell.ShowProperties(p)); }
    private void Services_Click(object s, RoutedEventArgs e) => Ui.Try("Open Services", () => Shell.Run("services.msc"));

    private void Location_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Entry is not { } en) return;
        Ui.Try("Go to location", () =>
        {
            if (en.Source == AutorunSource.StartupFolder) Shell.ShowInExplorer(en.LinkFile!);
            else Shell.OpenRegedit(en.Location);
        });
    }

    private void UninstallKey_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Entry.TargetPath is not { } path) return;
        var programs = (Window.GetWindow(this) as MainWindow)?.ProgramsPage?.AllPrograms.ToList();
        programs ??= new ProgramInventory().Load(new InventoryOptions { IncludeSystemComponents = true });
        var match = HunterResolver.MatchProgram(path, programs);
        if (match is null) { Ui.Info("No installed program found", "This startup item does not belong to a program in the installed programs list."); return; }
        Ui.Try("Open Registry Editor", () => Shell.OpenRegedit(match.RegistryKey.FullPath));
    }
}
