using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Apps;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class AppItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    public AppItem(WindowsApp a) => App = a;
    public WindowsApp App { get; }
    public string Name => App.Name;
    public long Size => App.SizeBytes ?? 0;
    public DateTime? InstallDate => App.InstallDate;
    public string Version => App.Version;
    public string Publisher => App.Publisher ?? "";
    public string Description => App.Description ?? "";
    public ImageSource? Icon { get => _icon; set { _icon = value; OnChanged(); } }
    public void SizeChanged() => OnChanged(nameof(Size));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class WindowsAppsPage : UserControl, IPage
{
    private readonly ObservableCollection<AppItem> _items = new();
    private readonly ICollectionView _view;
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public WindowsAppsPage()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.Name), ListSortDirection.Ascending));
        _view.Filter = o => o is AppItem a && (SearchBox.Text.Length == 0 ||
            a.Name.Contains(SearchBox.Text, StringComparison.CurrentCultureIgnoreCase) || a.Publisher.Contains(SearchBox.Text, StringComparison.CurrentCultureIgnoreCase));
        Grid.ItemsSource = _view;
        Grid.RowHeight = Ui.Settings.CompactRows ? 30 : 38;
        UpdateButtons();
    }

    public void OnShown()
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        LoadingState.Visibility = Visibility.Visible;
        var opts = new WindowsAppOptions { IncludeFrameworks = ShowSystem.IsChecked == true, IncludeSystemApps = ShowSystem.IsChecked == true };
        List<WindowsApp> apps;
        try { apps = await Task.Run(() => WindowsApps.List(opts), ct); }
        catch (Exception e) { LoadingState.Visibility = Visibility.Collapsed; Ui.Error("Could not list Windows apps", e.Message); return; }
        if (ct.IsCancellationRequested) return;
        _items.Clear();
        foreach (var a in apps) _items.Add(new AppItem(a));
        LoadingState.Visibility = Visibility.Collapsed;
        UpdateCount();
        var list = _items.ToList();
        _ = Task.Run(() =>
        {
            foreach (var i in list)
            {
                if (ct.IsCancellationRequested) return;
                var img = IconCache.FromFile(i.App.LogoPath);
                Dispatcher.BeginInvoke(() => i.Icon = img);
            }
        }, ct);
        _ = WindowsApps.ComputeSizesAsync(list.Select(i => i.App), a => Dispatcher.BeginInvoke(() => list.FirstOrDefault(i => i.App == a)?.SizeChanged()), ct);
    }

    private void UpdateCount() => CountText.Text = $"{_view.Cast<object>().Count()} of {_items.Count} apps · packaged apps installed for your account";

    private AppItem? Selected => Grid.SelectedItem as AppItem;

    private void UpdateButtons()
    {
        UninstallButton.IsEnabled = Selected is not null;
        RunButton.IsEnabled = Selected?.App.CanLaunch == true;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { _view?.Refresh(); if (_view is not null) UpdateCount(); }
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (e.OriginalSource is FrameworkElement { DataContext: AppItem }) Uninstall_Click(sender, e); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();
    private void ShowSystem_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.App is not { } app) return;
        var warn = app.IsSystem || app.IsFramework
            ? $"\"{app.Name}\" is a system app or framework that other apps may need. Windows may refuse to remove it.\n\nContinue?"
            : $"Uninstall \"{app.Name}\"?";
        if (!Ui.Confirm("Uninstall app", warn, "Uninstall", app.IsSystem || app.IsFramework)) return;
        var others = _items.Where(i => i.App != app).Select(i => (i.Name, "")).ToList();
        var wizard = new UninstallWizard(UninstallJob.ForWindowsApp(app, others)) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        if (wizard.ListMayHaveChanged) _ = LoadAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e) { if (Selected?.App is { } a) Ui.Try("Unable to run the app", () => WindowsApps.Launch(a)); }
    private void SearchName_Click(object s, RoutedEventArgs e) { if (Selected is { } a) Ui.Try("Search", () => Shell.SearchGoogle(a.Name)); }
    private void SearchPublisher_Click(object s, RoutedEventArgs e) { if (Selected is { Publisher.Length: > 0 } a) Ui.Try("Search", () => Shell.SearchGoogle(a.Publisher)); }
    private void Location_Click(object s, RoutedEventArgs e) { if (Selected?.App.InstallLocation is { } l) Ui.Try("Open install location", () => Shell.ShowInExplorer(l)); }
    private void Store_Click(object s, RoutedEventArgs e) { if (Selected?.App is { } a) Ui.Try("Open Microsoft Store", () => Shell.OpenUrl(a.StoreUrl)); }
    private void Registry_Click(object s, RoutedEventArgs e) { if (Selected?.App is { } a) Ui.Try("Open Registry Editor", () => Shell.OpenRegedit(a.RegistryKeyPath)); }
    private void CopyPackage_Click(object s, RoutedEventArgs e) { if (Selected?.App is { } a) Ui.Try("Copy", () => Clipboard.SetText(a.PackageFullName)); }
}
