using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Programs;
using Unravel.Core.WinGet;

namespace Unravel.App.Pages;

public partial class PackagesPage : UserControl, IPage
{
    private readonly WinGetClient _winget = new();
    private readonly ObservableCollection<WinGetPackage> _installed = new();
    private readonly ObservableCollection<WinGetPackage> _updates = new();
    private readonly ObservableCollection<WinGetPackage> _search = new();
    private readonly ICollectionView _installedView;
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public PackagesPage()
    {
        InitializeComponent();
        _installedView = CollectionViewSource.GetDefaultView(_installed);
        _installedView.Filter = o => o is WinGetPackage p && (HideLocal.IsChecked != true || !p.IsLocalOnly) &&
            (FilterBox.Text.Length == 0 || p.Name.Contains(FilterBox.Text, StringComparison.CurrentCultureIgnoreCase) || p.Id.Contains(FilterBox.Text, StringComparison.OrdinalIgnoreCase));
        Grid.ItemsSource = _installedView;
    }

    public async void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        if (!_winget.IsAvailable)
        {
            ShowEmpty("WinGet is not installed. Install \"App Installer\" from the Microsoft Store to manage packages here.");
            return;
        }
        var v = await _winget.GetVersionAsync();
        VersionText.Text = $"Install, update, uninstall and move programs with WinGet {v}, the Windows Package Manager.";
        await LoadInstalledAsync();
    }

    private bool IsBusy => _cts is not null;

    private void ShowEmpty(string? text)
    {
        Empty.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = text ?? "";
    }

    private async Task LoadInstalledAsync()
    {
        ShowEmpty("Asking WinGet for installed packages…");
        try
        {
            var list = await _winget.ListInstalledAsync();
            _installed.Clear();
            foreach (var p in list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) _installed.Add(p);
            ShowEmpty(_installed.Count == 0 ? "WinGet reported no installed packages." : null);
        }
        catch (Exception e) { ShowEmpty("WinGet could not list packages: " + e.Message); }
    }

    private async Task LoadUpdatesAsync()
    {
        ShowEmpty("Checking for updates…");
        try
        {
            var list = await _winget.ListUpgradesAsync();
            _updates.Clear();
            foreach (var p in list) _updates.Add(p);
            ShowEmpty(_updates.Count == 0 ? "Everything WinGet manages is up to date." : null);
        }
        catch (Exception e) { ShowEmpty("WinGet could not check for updates: " + e.Message); }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (Grid is null) return;
        InstalledBar.Visibility = TabInstalled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdatesBar.Visibility = TabUpdates.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SearchBar.Visibility = TabSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabInstalled.IsChecked == true) { Grid.ItemsSource = _installedView; ShowEmpty(_installed.Count == 0 ? "No packages loaded." : null); }
        else if (TabUpdates.IsChecked == true) { Grid.ItemsSource = _updates; _ = LoadUpdatesAsync(); }
        else { Grid.ItemsSource = _search; ShowEmpty(_search.Count == 0 ? "Search the WinGet catalogue to install a program." : null); SearchBox.Focus(); }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => _installedView?.Refresh();

    private List<WinGetPackage> Selected => Grid.SelectedItems.OfType<WinGetPackage>().ToList();

    // ------------------------------------------------------------ running operations

    private async Task<WinGetResult?> RunAsync(string title, Func<IProgress<string>, CancellationToken, Task<WinGetResult>> op)
    {
        if (IsBusy) { Ui.Info("Please wait", "Another WinGet operation is still running."); return null; }
        _cts = new CancellationTokenSource();
        ConsoleCard.Visibility = Visibility.Visible;
        ConsoleTitle.Text = title;
        ConsoleText.Clear();
        Busy.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        var progress = new Progress<string>(line =>
        {
            // Progress lines replace each other; everything else is appended.
            if (line.StartsWith("Progress:") && ConsoleText.LineCount > 0 && ConsoleText.GetLineText(ConsoleText.LineCount - 1).StartsWith("Progress:"))
            {
                var start = ConsoleText.GetCharacterIndexFromLineIndex(ConsoleText.LineCount - 1);
                ConsoleText.Text = ConsoleText.Text[..start] + line;
            }
            else ConsoleText.AppendText((ConsoleText.Text.Length > 0 ? Environment.NewLine : "") + line);
            ConsoleText.ScrollToEnd();
        });
        WinGetResult r;
        try { r = await op(progress, _cts.Token); }
        catch (Exception e) { r = new WinGetResult(-1, "", e.Message); }
        finally
        {
            _cts = null;
            Busy.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = false;
        }
        ConsoleTitle.Text = $"{title} — {(r.Ok ? "done" : r.ExitCode == -2 ? "cancelled" : "failed")}";
        ConsoleText.AppendText(Environment.NewLine + "» " + r.Message);
        ConsoleText.ScrollToEnd();
        if (!r.Ok && r.ExitCode != -2)
        {
            var admin = r.Message.Contains("administrator", StringComparison.OrdinalIgnoreCase) && !Core.Util.ProcessUtil.IsElevated;
            if (admin && Ui.Ask(title + " failed", r.Message, "Restart as administrator", "Close") == 0) Ui.RestartAsAdministrator();
            else if (!admin) Ui.Error(title + " failed", r.Message);
        }
        return r;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void HideConsole_Click(object sender, RoutedEventArgs e) { if (!IsBusy) ConsoleCard.Visibility = Visibility.Collapsed; }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (TabUpdates.IsChecked == true) await LoadUpdatesAsync(); else await LoadInstalledAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel.Count == 0) { Ui.Info("Nothing selected", "Select the package(s) to update."); return; }
        var local = sel.Where(p => p.IsLocalOnly).ToList();
        if (local.Count == sel.Count) { Ui.Info("Can't update with WinGet", "These programs aren't from a WinGet source, so WinGet can't update them. Use the program's own updater."); return; }
        foreach (var p in sel.Except(local))
        {
            var r = await RunAsync($"Updating {p.Name}", (pr, ct) => _winget.UpgradeAsync(p.Id, true, pr, ct));
            if (r is null || r.ExitCode == -2) break;
        }
        await (TabUpdates.IsChecked == true ? LoadUpdatesAsync() : LoadInstalledAsync());
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Count == 0) { Ui.Info("Up to date", "There is nothing to update."); return; }
        if (!Ui.Confirm("Update all", $"Update {_updates.Count} package(s)? Installers may close running programs.", "Update all")) return;
        await RunAsync("Updating all packages", (pr, ct) => _winget.UpgradeAllAsync(true, pr, ct));
        await LoadUpdatesAsync();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not [var p]) { Ui.Info("Select one package", "Select a single package to uninstall."); return; }
        if (!Ui.Confirm("Uninstall", $"Uninstall \"{p.Name}\" with WinGet? Unravel then scans for leftovers.", "Uninstall")) return;
        // Map the package to an installed program (by uninstall key or name) so the leftover scan knows where it lived.
        var programs = await Task.Run(() => new ProgramInventory().Load(new InventoryOptions { IncludeSystemComponents = true }));
        var program = programs.FirstOrDefault(x => p.ArpKeyName is not null && x.KeyName.Equals(p.ArpKeyName, StringComparison.OrdinalIgnoreCase))
                      ?? programs.FirstOrDefault(x => x.DisplayName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        var others = programs.Where(x => x != program).Select(x => (x.DisplayName, x.ResolvedInstallFolder ?? "")).ToList();
        var wizard = new UninstallWizard(UninstallJob.ForWinGet(p, program, others)) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        await LoadInstalledAsync();
    }

    private async void Reinstall_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not [var p]) { Ui.Info("Select one package", "Select a single package to reinstall."); return; }
        if (p.IsLocalOnly) { Ui.Info("Can't reinstall with WinGet", "This program isn't from a WinGet source, so WinGet can't download it again."); return; }
        if (!Ui.Confirm("Reinstall", $"Uninstall and install \"{p.Name}\" {p.Version} again? Its settings may be reset.", "Reinstall", danger: true)) return;
        await RunAsync($"Reinstalling {p.Name}", (pr, ct) => _winget.ReinstallAsync(p.Id, p.Version, pr, ct));
        await LoadInstalledAsync();
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not [var p]) return;
        if (p.IsLocalOnly) { Ui.Info(p.Name, $"ID: {p.Id}\nVersion: {p.Version}\n\nWinGet knows this program only from Programs and Features."); return; }
        await RunAsync($"Details of {p.Name}", async (pr, ct) =>
        {
            var r = await _winget.ShowAsync(p.Id, ct);
            foreach (var line in WinGetParser.CleanLines(r.Output)) pr.Report(line);
            return r;
        });
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return;
        ShowEmpty("Searching…");
        try
        {
            var list = await _winget.SearchAsync(q);
            _search.Clear();
            foreach (var p in list) _search.Add(p);
            ShowEmpty(_search.Count == 0 ? $"No packages match \"{q}\"." : null);
        }
        catch (Exception ex) { ShowEmpty("Search failed: " + ex.Message); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not [var p]) { Ui.Info("Select one package", "Select the package to install."); return; }
        var scope = ScopeBox.SelectedIndex switch { 1 => WinGetScope.User, 2 => WinGetScope.Machine, _ => WinGetScope.Any };
        if (!Ui.Confirm("Install", $"Install \"{p.Name}\" {p.Version} ({p.Id})?\n\nBy installing you accept the package's license agreement.", "Install")) return;
        var r = await RunAsync($"Installing {p.Name}", (pr, ct) => _winget.InstallAsync(p.Id, null, scope, true, pr, ct));
        if (r?.Ok == true) _ = LoadInstalledAsync();
    }

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TabSearch.IsChecked == true) Install_Click(sender, e); else Details_Click(sender, e);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var path = Ui.SaveFile("Export package list", $"packages-{Environment.MachineName}.json", "WinGet package list|*.json");
        if (path is null) return;
        var r = await RunAsync("Exporting package list", (pr, ct) => _winget.ExportAsync(path, ct));
        if (r?.Ok == true) Ui.Info("Exported", $"{r.Message}\n\nUse \"Reinstall from list…\" on this or another PC to install the same programs.\n{path}");
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Choose a package list", Filter = "WinGet package list|*.json" };
        if (d.ShowDialog(Window.GetWindow(this)) != true) return;
        var choice = Ui.Ask("Reinstall from list", "Install every package in this list that isn't installed yet?\n\nBy continuing you accept the packages' license agreements.",
            "Install latest versions", "Install the listed versions", "Cancel");
        if (choice is < 0 or 2) return;
        await RunAsync("Installing packages from list", (pr, ct) => _winget.ImportAsync(d.FileName, ignoreVersions: choice == 0, pr, ct));
        await LoadInstalledAsync();
    }
}
