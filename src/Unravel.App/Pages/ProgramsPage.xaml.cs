using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Leftovers;
using Unravel.Core.Programs;
using Unravel.Core.SystemOps;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class ProgramItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    public ProgramItem(InstalledProgram p) => Program = p;
    public InstalledProgram Program { get; }
    public string Name => Program.DisplayName;
    public string Version => Program.Version ?? "";
    public string Publisher => Program.Publisher ?? "";
    public DateTime? InstallDate => Program.InstallDate;
    public long Size => Program.SizeBytes ?? 0;
    public string Type => Program.TypeText;
    public string Installer => Program.Installer;
    public string Website => Program.AboutLink ?? Program.HelpLink ?? "";
    public string Comment => Program.Comments ?? "";
    public string Location => Program.InstallLocation ?? "";
    public string UninstallString => Program.UninstallString ?? "";
    public string RegistryKey => Program.RegistryKey.FullPath;
    public ImageSource? Icon { get => _icon; set { _icon = value; OnChanged(); } }
    public string Tooltip =>
        $"{Name}\nVersion: {Version}\nPublisher: {Publisher}\nInstalled: {InstallDate:d}\nSize: {(Size > 0 ? Format.Bytes(Size) : "unknown")}" +
        (string.IsNullOrEmpty(Website) ? "" : $"\nWebsite: {Website}");
    public void SizeChanged() => OnChanged(nameof(Size));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class ProgramsPage : UserControl, IPage
{
    private readonly ObservableCollection<ProgramItem> _items = new();
    private readonly ICollectionView _view;
    private CancellationTokenSource? _loadCts;
    private bool _loaded;

    private static readonly Dictionary<string, string> ColumnSettingNames = new()
    {
        ["Version"] = "Version", ["Publisher"] = "Publisher", ["Installed"] = "InstallDate", ["Size"] = "Size", ["Type"] = "Type",
        ["Installer"] = "Installer", ["Website"] = "Website", ["Comment"] = "Comment", ["Install location"] = "InstallLocation",
        ["Uninstall command"] = "UninstallString", ["Registry key"] = "RegistryKey",
    };

    public ProgramsPage()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = Filter;
        _view.SortDescriptions.Add(new SortDescription(nameof(ProgramItem.Name), ListSortDirection.Ascending));
        ProgramGrid.ItemsSource = _view;
        Tiles.ItemsSource = _view;
        ListView.ItemsSource = _view;
        ApplyColumnSettings();
        switch (Ui.Settings.ProgramView)
        {
            case Core.Settings.ProgramViewMode.Tiles: ViewTiles.IsChecked = true; break;
            case Core.Settings.ProgramViewMode.List: ViewList.IsChecked = true; break;
            default: ViewDetails.IsChecked = true; break;
        }
        DetailsColumn.Width = Ui.Settings.ShowDetailsPane ? new GridLength(320) : new GridLength(0);
        DetailsPane.Visibility = Ui.Settings.ShowDetailsPane ? Visibility.Visible : Visibility.Collapsed;
        ProgramGrid.RowHeight = Ui.Settings.CompactRows ? 30 : 38;
        UpdateCommands();

        InputBindings.Add(new KeyBinding(new RelayCommand(() => { SearchBox.Focus(); SearchBox.SelectAll(); }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _ = LoadAsync()), Key.F5, ModifierKeys.None));
        ProgramGrid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { Uninstall_Click(this, new RoutedEventArgs()); e.Handled = true; }
            if (e.Key == Key.Enter) { e.Handled = true; ShowSelectedDetails(); }
        };
    }

    public void OnShown()
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
    }

    public IEnumerable<InstalledProgram> AllPrograms => _items.Select(i => i.Program);

    public async Task LoadAsync(string? selectKey = null)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        LoadingState.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        var s = Ui.Settings.Uninstaller;
        var options = new InventoryOptions { IncludeSystemComponents = s.ShowSystemComponents, IncludeUpdates = s.ShowUpdates, UseInstallDateValue = s.UseInstallDateValue };
        List<InstalledProgram> programs;
        try { programs = await Task.Run(() => new ProgramInventory().Load(options), ct); }
        catch (Exception e) { Ui.Error("Could not read installed programs", e.Message); LoadingState.Visibility = Visibility.Collapsed; return; }
        if (ct.IsCancellationRequested) return;

        var previous = selectKey ?? (Selected?.RegistryKey);
        _items.Clear();
        foreach (var p in programs) _items.Add(new ProgramItem(p));
        LoadingState.Visibility = Visibility.Collapsed;
        UpdateCount();
        if (previous is not null) Select(_items.FirstOrDefault(i => i.RegistryKey == previous));

        // Icons and sizes load in the background; fast loading mode skips them, as in the original.
        if (!s.FastLoading)
        {
            var list = _items.ToList();
            _ = Task.Run(() =>
            {
                foreach (var item in list)
                {
                    if (ct.IsCancellationRequested) return;
                    var img = IconCache.FromIconLocation(item.Program.DisplayIcon)
                              ?? IconCache.FromFile(CommandLine.FindExecutablePath(item.Program.UninstallString));
                    Dispatcher.BeginInvoke(() => item.Icon = img ?? DefaultIcon);
                }
            }, ct);
            _ = ProgramInventory.ComputeMissingSizesAsync(list.Select(i => i.Program), p =>
                Dispatcher.BeginInvoke(() => list.FirstOrDefault(i => i.Program == p)?.SizeChanged()), ct);
        }
        else foreach (var i in _items) i.Icon = DefaultIcon;
    }

    private static ImageSource? _defaultIcon;
    private static ImageSource? DefaultIcon => _defaultIcon ??= IconCache.FromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"));

    private bool Filter(object o)
    {
        if (o is not ProgramItem i) return false;
        var q = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        bool Has(string? s) => s?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
        return SearchBy.SelectedIndex switch
        {
            1 => Has(i.Publisher),
            2 => Has(i.Name) || Has(i.Publisher) || Has(i.Version) || Has(i.Comment) || Has(i.Location) || Has(i.Website),
            _ => Has(i.Name),
        };
    }

    private void Search_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        int visible = _view.Cast<object>().Count();
        var q = SearchBox.Text.Trim();
        CountText.Text = q.Length == 0 ? $"{_items.Count} installed programs" : $"Found {visible} of {_items.Count} programs";
        EmptyState.Visibility = visible == 0 && LoadingState.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _items.Count == 0 ? "No installed programs were found." : "No programs match your search.";
    }

    private ProgramItem? Selected =>
        (ViewTiles.IsChecked == true ? Tiles.SelectedItem : ViewList.IsChecked == true ? ListView.SelectedItem : ProgramGrid.SelectedItem) as ProgramItem;

    private void Select(ProgramItem? item)
    {
        if (item is null) return;
        ProgramGrid.SelectedItem = item;
        Tiles.SelectedItem = item;
        ListView.SelectedItem = item;
        ProgramGrid.ScrollIntoView(item);
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateCommands();
        ShowSelectedDetails();
    }

    private void UpdateCommands()
    {
        var p = Selected?.Program;
        UninstallButton.IsEnabled = p is not null && p.CanUninstall;
        ModifyButton.IsEnabled = p?.SupportsModify == true;
        RepairButton.IsEnabled = p?.SupportsRepair == true;
    }

    private void ShowSelectedDetails()
    {
        var item = Selected;
        NoSelection.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) return;
        var p = item.Program;
        DetailIcon.Source = item.Icon;
        DetailName.Text = p.DisplayName;
        DetailPublisher.Text = p.Publisher ?? "Unknown publisher";
        DetailFacts.Children.Clear();
        DetailFacts.RowDefinitions.Clear();
        void Fact(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            DetailFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 3, 8, 3) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(l, DetailFacts.RowDefinitions.Count - 1);
            Grid.SetRow(v, DetailFacts.RowDefinitions.Count - 1);
            Grid.SetColumn(v, 1);
            DetailFacts.Children.Add(l);
            DetailFacts.Children.Add(v);
        }
        Fact("Version", p.Version);
        Fact("Installed", p.InstallDate?.ToString("D"));
        Fact("Size", p.SizeBytes is { } sz ? Format.Bytes(sz) : null);
        Fact("Type", p.TypeText);
        Fact("Installer", p.Installer);
        Fact("Location", p.InstallLocation);
        Fact("Comment", p.Comments);

        DetailLinks.Children.Clear();
        void Link(string text, string glyph, Action action)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 5, 6, 5) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 13, Margin = new Thickness(0, 0, 10, 0) };
            g.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = text });
            b.Content = sp;
            b.Click += (_, _) => Ui.Try(text, action);
            DetailLinks.Children.Add(b);
        }
        if (p.AboutLink is { } about) Link("Website", "", () => Shell.OpenUrl(about));
        if (p.HelpLink is { } help) Link("Help and support", "", () => Shell.OpenUrl(help));
        if (p.UpdateLink is { } upd) Link("Check for updates", "", () => Shell.OpenUrl(upd));
        if (p.ResolvedInstallFolder is { } folder && Directory.Exists(folder)) Link("Open install folder", "", () => Shell.ShowInExplorer(folder));
        Link("Open registry key", "", () => Shell.OpenRegedit(p.RegistryKey.FullPath));
        Link("Search the web", "", () => Shell.SearchGoogle(p.DisplayName));
        DetailUninstall.IsEnabled = p.CanUninstall;
    }

    // ------------------------------------------------------------ commands

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ProgramItem }) Uninstall_Click(sender, e);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e) => StartUninstall(UninstallCommandKind.Standard);
    private void ForcedMsi_Click(object sender, RoutedEventArgs e) => StartUninstall(UninstallCommandKind.ForcedMsi);

    private void StartUninstall(UninstallCommandKind kind)
    {
        if (Selected?.Program is not { } p) return;
        if (!p.CanUninstall) { Ui.Error("Cannot uninstall", "This program does not provide an uninstall command."); return; }
        var question = p.Kind == ProgramKind.SystemComponent
            ? $"\"{p.DisplayName}\" is a system component. Uninstalling it may affect other programs or Windows itself.\n\nOnly continue if you are sure."
            : $"Uninstall \"{p.DisplayName}\"?";
        if (!Ui.Confirm(kind == UninstallCommandKind.ForcedMsi ? "Forced MSI uninstall" : "Uninstall program", question, "Uninstall", p.Kind == ProgramKind.SystemComponent))
            return;
        var others = _items.Where(i => i.Program != p).Select(i => (i.Program.DisplayName, i.Program.ResolvedInstallFolder ?? "")).ToList();
        var wizard = new UninstallWizard(UninstallJob.ForProgram(p, kind, others)) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        if (wizard.ListMayHaveChanged) _ = LoadAsync();
    }

    private void Modify_Click(object sender, RoutedEventArgs e) => RunSimple(UninstallCommandKind.Modify, "Change");
    private void Repair_Click(object sender, RoutedEventArgs e) => RunSimple(UninstallCommandKind.Repair, "Repair");

    private void RunSimple(UninstallCommandKind kind, string verb)
    {
        if (Selected?.Program is not { } p) return;
        Ui.Try($"{verb} failed", () => ProgramActions.StartCommand(p, kind));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Items inside a resource ContextMenu are not generated as fields; look them up by name.
        var all = ((ContextMenu)sender).Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>().Prepend(m)).ToList();
        MenuItem M(string name) => all.First(m => m.Name == name);
        var (MiUninstall, MiForced, MiModify, MiRepair, MiAbout) = (M("MiUninstall"), M("MiForced"), M("MiModify"), M("MiRepair"), M("MiAbout"));
        var (MiHelp, MiUpdate, MiLocation, MiSearchPublisher) = (M("MiHelp"), M("MiUpdate"), M("MiLocation"), M("MiSearchPublisher"));
        var p = Selected?.Program;
        foreach (var mi in new[] { MiUninstall, MiForced, MiModify, MiRepair, MiAbout, MiHelp, MiUpdate, MiLocation, MiSearchPublisher }) mi.IsEnabled = p is not null;
        if (p is null) return;
        MiUninstall.IsEnabled = p.CanUninstall;
        MiForced.Visibility = p.SupportsForcedMsiUninstall ? Visibility.Visible : Visibility.Collapsed;
        MiModify.IsEnabled = p.SupportsModify;
        MiRepair.IsEnabled = p.SupportsRepair;
        MiAbout.IsEnabled = p.AboutLink is not null;
        MiHelp.IsEnabled = p.HelpLink is not null;
        MiUpdate.IsEnabled = p.UpdateLink is not null;
        MiLocation.IsEnabled = p.ResolvedInstallFolder is not null;
        MiSearchPublisher.IsEnabled = p.Publisher is not null;
    }

    private void SearchName_Click(object s, RoutedEventArgs e) { if (Selected is { } i) Ui.Try("Search", () => Shell.SearchGoogle(i.Name)); }
    private void SearchPublisher_Click(object s, RoutedEventArgs e) { if (Selected is { } i) Ui.Try("Search", () => Shell.SearchGoogle(i.Publisher)); }
    private void AboutLink_Click(object s, RoutedEventArgs e) { if (Selected?.Program.AboutLink is { } u) Ui.Try("Open link", () => Shell.OpenUrl(u)); }
    private void HelpLink_Click(object s, RoutedEventArgs e) { if (Selected?.Program.HelpLink is { } u) Ui.Try("Open link", () => Shell.OpenUrl(u)); }
    private void UpdateLink_Click(object s, RoutedEventArgs e) { if (Selected?.Program.UpdateLink is { } u) Ui.Try("Open link", () => Shell.OpenUrl(u)); }
    private void Location_Click(object s, RoutedEventArgs e) { if (Selected?.Program.ResolvedInstallFolder is { } f) Ui.Try("Open install location", () => Shell.ShowInExplorer(f)); }
    private void Registry_Click(object s, RoutedEventArgs e) { if (Selected is { } i) Ui.Try("Open Registry Editor", () => Shell.OpenRegedit(i.RegistryKey)); }
    private void CopyName_Click(object s, RoutedEventArgs e) { if (Selected is { } i) Ui.Try("Copy", () => Clipboard.SetText(i.Name)); }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Program is not { } p) return;
        var msg = $"Remove \"{p.DisplayName}\" from the list of installed programs?\n\nThis does not uninstall the program – it only deletes its registration. Use it for entries whose program is already gone. A registry backup is saved first.";
        if (p.Kind == ProgramKind.SystemComponent) msg = "This is a system component and may be required by Windows.\n\n" + msg;
        if (!Ui.Confirm("Remove entry", msg, "Remove entry", danger: true)) return;
        var (ok, err, backup) = ProgramActions.RemoveEntry(p, new RegistryBackup());
        if (!ok) { Ui.Error("Entry was not removed", err ?? "Unknown error"); return; }
        Ui.Info("Entry removed", $"\"{p.DisplayName}\" was removed from the list.\nBackup: {backup}");
        _ = LoadAsync();
    }

    private List<ProgramColumn> VisibleExportColumns()
    {
        var cols = new List<ProgramColumn> { ProgramColumn.Name };
        foreach (var c in ProgramGrid.Columns.Skip(1).Where(c => c.Visibility == Visibility.Visible))
            if (ColumnSettingNames.TryGetValue((string)c.Header, out var n) && Enum.TryParse<ProgramColumn>(n, out var pc)) cols.Add(pc);
        return cols;
    }

    private void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ExportButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var html = new MenuItem { Header = "Export as HTML…" };
        html.Click += ExportHtml_Click;
        var txt = new MenuItem { Header = "Export as text…" };
        txt.Click += ExportText_Click;
        menu.Items.Add(html);
        menu.Items.Add(txt);
        menu.IsOpen = true;
    }

    private void ExportHtml_Click(object sender, RoutedEventArgs e) =>
        Ui.ExportAndOpen("Export programs", "Installed programs.html", "HTML file|*.html",
            _ => ProgramExporter.ToHtml(_view.Cast<ProgramItem>().Select(i => i.Program), VisibleExportColumns()));

    private void ExportText_Click(object sender, RoutedEventArgs e) =>
        Ui.ExportAndOpen("Export programs", "Installed programs.txt", "Text file|*.txt",
            _ => ProgramExporter.ToText(_view.Cast<ProgramItem>().Select(i => i.Program), VisibleExportColumns()));

    // ------------------------------------------------------------ view

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (ProgramGrid is null) return;
        var sel = Selected ?? ProgramGrid.SelectedItem as ProgramItem ?? Tiles.SelectedItem as ProgramItem ?? ListView.SelectedItem as ProgramItem;
        ProgramGrid.Visibility = ViewDetails.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Tiles.Visibility = ViewTiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ListView.Visibility = ViewList.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ColumnsButton.IsEnabled = ViewDetails.IsChecked == true;
        Ui.Settings.ProgramView = ViewTiles.IsChecked == true ? Core.Settings.ProgramViewMode.Tiles
            : ViewList.IsChecked == true ? Core.Settings.ProgramViewMode.List : Core.Settings.ProgramViewMode.Details;
        Ui.SaveSettings();
        Select(sel);
    }

    private void ApplyColumnSettings()
    {
        foreach (var c in ProgramGrid.Columns.Skip(1))
            if (ColumnSettingNames.TryGetValue((string)c.Header, out var n))
                c.Visibility = Ui.Settings.HiddenProgramColumns.Contains(n) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ColumnsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = true };
        foreach (var c in ProgramGrid.Columns.Skip(1))
        {
            var header = (string)c.Header;
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = c.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            mi.Click += (_, _) =>
            {
                c.Visibility = mi.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                var n = ColumnSettingNames[header];
                Ui.Settings.HiddenProgramColumns.Remove(n);
                if (!mi.IsChecked) Ui.Settings.HiddenProgramColumns.Add(n);
                Ui.SaveSettings();
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Default DataGrid sorting is fine; this hook keeps the selection visible after sorting.
        Dispatcher.BeginInvoke(() => { if (ProgramGrid.SelectedItem is not null) ProgramGrid.ScrollIntoView(ProgramGrid.SelectedItem); });
    }
}

public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
