using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Unravel.App.Dialogs;
using Unravel.App.Infrastructure;
using Unravel.Core.Leftovers;
using Unravel.Core.Settings;
using Unravel.Core.SystemOps;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class SettingsPage : UserControl, IPage
{
    private readonly StackPanel _body = new() { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
    private AppSettings S => Ui.Settings;

    public SettingsPage()
    {
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        var header = new TextBlock { Text = "Settings", Style = (Style)FindResource("PageTitle"), Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) });
        Content = root;
    }

    public void OnShown() => Build();

    private void Build()
    {
        _body.Children.Clear();

        // ---------------- General
        var general = Section("General", "");
        general.Children.Add(Combo("Theme", ["Use Windows setting", "Light", "Dark"], (int)S.Theme, i =>
        {
            S.Theme = (ThemeMode)i;
            ThemeManager.Apply(S.Theme);
        }));
        general.Children.Add(Switch("Don't show information messages", "For example \"Scan finished\" notices.", S.SkipInformationMessages, v => S.SkipInformationMessages = v));
        general.Children.Add(Switch("Don't ask for confirmation", "Skips \"Are you sure…?\" questions. Irreversible actions such as secure delete always ask.", S.SkipWarningMessages, v => S.SkipWarningMessages = v));

        // ---------------- Uninstaller
        var u = S.Uninstaller;
        var un = Section("Uninstaller", "");
        un.Children.Add(Switch("Show system components", "Programs registered as parts of other products or of Windows.", u.ShowSystemComponents, v => { u.ShowSystemComponents = v; ReloadPrograms(); }));
        un.Children.Add(Switch("Show Windows updates", "Updates and patches registered as installed programs.", u.ShowUpdates, v => { u.ShowUpdates = v; ReloadPrograms(); }));
        un.Children.Add(Switch("Use the program's own install date", "Otherwise the date is taken from when its registry entry was last written.", u.UseInstallDateValue, v => { u.UseInstallDateValue = v; ReloadPrograms(); }));
        un.Children.Add(Switch("Fast loading", "Skip icons and size calculation for programs that don't report a size.", u.FastLoading, v => { u.FastLoading = v; ReloadPrograms(); }));
        un.Children.Add(Switch("Only run the built-in uninstaller", "Don't scan for leftovers afterwards.", u.OnlyRunBuiltInUninstaller, v => u.OnlyRunBuiltInUninstaller = v));
        un.Children.Add(Switch("Create a System Restore point before uninstalling", "Needs administrator rights and System Restore turned on. Windows creates at most one point per 24 hours by default.", u.CreateRestorePoint, v => u.CreateRestorePoint = v));
        un.Children.Add(Switch("Back up registry items before deleting them", "Backups can be restored below.", u.BackupRegistryBeforeDelete, v => u.BackupRegistryBeforeDelete = v));
        un.Children.Add(Switch("Close the program's running processes before uninstalling", "You are asked first.", u.StopRunningExecutables, v => u.StopRunningExecutables = v));
        un.Children.Add(Switch("Check all found leftovers by default", "Only bold (created by the program) items are ever deleted.", u.CheckAllLeftoversByDefault, v => u.CheckAllLeftoversByDefault = v));
        un.Children.Add(Switch("Move deleted leftover files to the Recycle Bin", null, u.DeleteFilesToRecycleBin, v => u.DeleteFilesToRecycleBin = v));
        un.Children.Add(Switch("Open the uninstall wizard maximized", null, u.OpenWizardMaximized, v => u.OpenWizardMaximized = v));
        un.Children.Add(Combo("Default scan mode", ["Safe", "Moderate", "Advanced"], (int)u.DefaultScanMode - 1, i => u.DefaultScanMode = (ScanMode)(i + 1)));

        // ---------------- Exclusions
        var ex = Section("Uninstaller exclusions", "");
        ex.Children.Add(Hint("Excluded registry keys and folders are shown in red and never deleted. Registry patterns accept * and ? wildcards, e.g. HKEY_LOCAL_MACHINE\\SOFT*\\Classes."));
        ex.Children.Add(RuleEditor("Registry keys", u.RegistryExcludes, includeSubkeys: true,
            add: () =>
            {
                var d = new InputDialog("Exclude registry key", "Registry key", "Full path, for example HKEY_CURRENT_USER\\Software\\MyCompany. Wildcards * and ? are allowed.") { Owner = Window.GetWindow(this) };
                if (d.ShowDialog() != true) return null;
                if (!RegPath.TryParse(d.Value.Replace("*", "x").Replace("?", "x"), out _)) { Ui.Error("Invalid key", "The path must start with a registry hive such as HKEY_LOCAL_MACHINE or HKEY_CURRENT_USER."); return null; }
                return new ExcludeRule { Pattern = d.Value.TrimEnd('\\'), IncludeSubkeys = true };
            },
            reset: () => { u.RegistryExcludes = UninstallerSettings.DefaultRegistryExcludes(); Build(); }));
        ex.Children.Add(RuleEditor("Folders", u.FolderExcludes, includeSubkeys: false,
            add: () =>
            {
                var d = new OpenFolderDialog { Title = "Choose a folder to protect" };
                return d.ShowDialog(Window.GetWindow(this)) == true ? new ExcludeRule { Pattern = d.FolderName } : null;
            },
            reset: () => { u.FolderExcludes = []; Build(); }));

        // ---------------- Junk
        var j = S.Junk;
        var junk = Section("Junk files", "");
        junk.Children.Add(Switch("Move junk files to the Recycle Bin", "Safer, but space is only freed when the Recycle Bin is emptied.", j.DeleteToRecycleBin, v => j.DeleteToRecycleBin = v));
        junk.Children.Add(Switch("Ignore files used in the last 24 hours", "Temporary files that are still in use are left alone.", j.IgnoreRecentlyAccessed, v => j.IgnoreRecentlyAccessed = v));
        junk.Children.Add(PatternEditor("File patterns to look for", j.Patterns,
            add: () =>
            {
                var d = new InputDialog("Add junk pattern", "Pattern", "Use * and ? as wildcards, e.g. *.tmp or ~*.*", "Description") { Owner = Window.GetWindow(this) };
                return d.ShowDialog() == true ? new PatternRule { Pattern = d.Value, Description = d.SecondValue } : null;
            },
            reset: () => { j.Patterns = JunkSettings.DefaultPatterns(); Build(); }));
        junk.Children.Add(PatternEditor("Folders to skip", j.Excludes,
            add: () =>
            {
                var d = new InputDialog("Add exclusion", "Folder pattern", "Use * and ? as wildcards, e.g. ?:\\Games\\* or %USERPROFILE%\\Projects\\*", "Description") { Owner = Window.GetWindow(this) };
                return d.ShowDialog() == true ? new PatternRule { Pattern = d.Value, Description = d.SecondValue } : null;
            },
            reset: () => { j.Excludes = JunkSettings.DefaultExcludes(); Build(); }));

        // ---------------- View
        var view = Section("View", "");
        view.Children.Add(Switch("Compact rows", "Shows more items in lists.", S.CompactRows, v => { S.CompactRows = v; InvalidateAll(); }));
        view.Children.Add(Switch("Show the details pane in Programs", null, S.ShowDetailsPane, v => { S.ShowDetailsPane = v; InvalidateAll(); }));
        var resetCols = new Button { Content = "Reset program list columns", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        resetCols.Click += (_, _) => { S.HiddenProgramColumns = new AppSettings().HiddenProgramColumns; S.ProgramView = ProgramViewMode.Details; Ui.SaveSettings(); InvalidateAll(); Ui.Info("Columns reset", "The program list uses the default columns again."); };
        view.Children.Add(resetCols);

        // ---------------- Hunter
        var h = S.Hunter;
        var hunter = Section("Hunter mode", "");
        hunter.Children.Add(Switch("Keep the target on top of other windows", null, h.AlwaysOnTop, v => h.AlwaysOnTop = v));
        hunter.Children.Add(Switch("Start Hunter mode when I sign in to Windows", "Adds a startup entry for the current user.", h.StartWithWindows, v => { h.StartWithWindows = v; HunterWindow.SetStartWithWindows(v); }));
        hunter.Children.Add(Combo("Target size", ["Small", "Medium", "Large"], h.Size, i => h.Size = i));
        hunter.Children.Add(Combo("Transparency", ["None", "25%", "50%", "75%"], Math.Clamp(h.TransparencyPercent / 25, 0, 3), i => h.TransparencyPercent = i * 25));

        // ---------------- Backups
        var b = Section("Registry backups", "");
        b.Children.Add(Hint("A backup is saved every time Unravel deletes registry items. Restoring imports the .reg files back into the registry."));
        b.Children.Add(BackupList());

        Ui.SaveSettings();
    }

    // ------------------------------------------------------------ building blocks

    private StackPanel Section(string title, string glyph)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var g = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 10, 0) };
        g.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        head.Children.Add(g);
        head.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0) });
        stack.Children.Add(head);
        _body.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 14), Child = stack });
        return stack;
    }

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };

    private CheckBox Switch(string label, string? hint, bool value, Action<bool> set)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label });
        if (hint is not null)
        {
            var t = new TextBlock { Text = hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            text.Children.Add(t);
        }
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), Content = text, IsChecked = value };
        cb.Click += (_, _) => { set(cb.IsChecked == true); Ui.SaveSettings(); };
        return cb;
    }

    private DockPanel Combo(string label, string[] options, int selected, Action<int> set)
    {
        var combo = new ComboBox { Width = 220 };
        foreach (var o in options) combo.Items.Add(new ComboBoxItem { Content = o });
        combo.SelectedIndex = Math.Clamp(selected, 0, options.Length - 1);
        combo.SelectionChanged += (_, _) => { set(combo.SelectedIndex); Ui.SaveSettings(); };
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(combo, Dock.Right);
        row.Children.Add(combo);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private FrameworkElement RuleEditor(string title, List<ExcludeRule> rules, bool includeSubkeys, Func<ExcludeRule?> add, Action reset)
    {
        var items = new ObservableCollection<ExcludeRule>(rules);
        var grid = EditableGrid(items);
        grid.Columns.Add(CheckColumn("On", nameof(ExcludeRule.Enabled), 50));
        grid.Columns.Add(new DataGridTextColumn { Header = title == "Folders" ? "Folder" : "Key or pattern", Binding = new Binding(nameof(ExcludeRule.Pattern)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        if (includeSubkeys) grid.Columns.Add(CheckColumn("Include subkeys", nameof(ExcludeRule.IncludeSubkeys), 120));
        return ListBlock(title, grid,
            onAdd: () => { if (add() is { } r) { rules.Add(r); items.Add(r); Ui.SaveSettings(); } },
            onRemove: () => { foreach (var r in grid.SelectedItems.Cast<ExcludeRule>().ToList()) { rules.Remove(r); items.Remove(r); } Ui.SaveSettings(); },
            onReset: () => { if (Ui.Confirm("Reset to defaults", $"Replace your {title.ToLowerInvariant()} list with the defaults?", "Reset")) { reset(); Ui.SaveSettings(); } });
    }

    private FrameworkElement PatternEditor(string title, List<PatternRule> rules, Func<PatternRule?> add, Action reset)
    {
        var items = new ObservableCollection<PatternRule>(rules);
        var grid = EditableGrid(items);
        grid.Columns.Add(CheckColumn("On", nameof(PatternRule.Enabled), 50));
        grid.Columns.Add(new DataGridTextColumn { Header = "Pattern", Binding = new Binding(nameof(PatternRule.Pattern)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new Binding(nameof(PatternRule.Description)), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        return ListBlock(title, grid,
            onAdd: () => { if (add() is { } r) { rules.Add(r); items.Add(r); Ui.SaveSettings(); } },
            onRemove: () => { foreach (var r in grid.SelectedItems.Cast<PatternRule>().ToList()) { rules.Remove(r); items.Remove(r); } Ui.SaveSettings(); },
            onReset: () => { if (Ui.Confirm("Reset to defaults", $"Replace \"{title}\" with the defaults?", "Reset")) { reset(); Ui.SaveSettings(); } });
    }

    private static DataGrid EditableGrid(IEnumerable items)
    {
        var grid = new DataGrid { ItemsSource = items, IsReadOnly = false, Height = 190, SelectionMode = DataGridSelectionMode.Extended, RowHeight = 32, Background = System.Windows.Media.Brushes.Transparent };
        grid.CellEditEnding += (_, _) => grid.Dispatcher.BeginInvoke(Ui.SaveSettings, System.Windows.Threading.DispatcherPriority.Background);
        return grid;
    }

    private static DataGridTemplateColumn CheckColumn(string header, string path, double width)
    {
        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        factory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        factory.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Ui.SaveSettings()));
        return new DataGridTemplateColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = factory } };
    }

    private FrameworkElement ListBlock(string title, DataGrid grid, Action onAdd, Action onRemove, Action onReset)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, action) in new[] { ("Add…", onAdd), ("Remove", onRemove), ("Reset to defaults", onReset) })
        {
            var b = new Button { Content = text, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 3, 10, 3), MinHeight = 28 };
            b.Click += (_, _) => action();
            buttons.Children.Add(b);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(head);
        panel.Children.Add(new Border { BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = grid });
        return panel;
    }

    private FrameworkElement BackupList()
    {
        var store = new RegistryBackup();
        var sets = store.List();
        var panel = new StackPanel();
        if (sets.Count == 0) { panel.Children.Add(Hint("No backups yet.")); return panel; }
        foreach (var set in sets.Take(30))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var restore = new Button { Content = "Restore", Style = (Style)FindResource("GhostButton"), MinHeight = 28, Padding = new Thickness(10, 3, 10, 3) };
            restore.Click += (_, _) =>
            {
                if (!Ui.Confirm("Restore backup", $"Import {set.FileCount} .reg file(s) from \"{set.Title}\" back into the registry?", "Restore")) return;
                var needsAdmin = File.ReadAllText(Directory.GetFiles(set.Folder, "*.reg").FirstOrDefault() ?? "").Contains("HKEY_LOCAL_MACHINE");
                var r = store.Restore(set, elevate: needsAdmin && !ProcessUtil.IsElevated);
                if (r.Ok) Ui.Info("Backup restored", "The registry items were restored."); else Ui.Error("Restore failed", r.Error ?? "");
            };
            var open = new Button { Content = "Open folder", Style = (Style)FindResource("GhostButton"), MinHeight = 28, Padding = new Thickness(10, 3, 10, 3) };
            open.Click += (_, _) => Ui.Try("Open folder", () => Shell.ShowInExplorer(set.Folder));
            var del = new Button { Content = "Delete", Style = (Style)FindResource("GhostButton"), MinHeight = 28, Padding = new Thickness(10, 3, 10, 3) };
            del.Click += (_, _) => { if (Ui.Confirm("Delete backup", $"Delete the backup \"{set.Title}\"?", "Delete", true) && Ui.Try("Delete backup", () => store.Delete(set))) Build(); };
            buttons.Children.Add(restore);
            buttons.Children.Add(open);
            buttons.Children.Add(del);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = set.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            var sub = new TextBlock { Text = $"{set.Created:g} · {set.FileCount} file(s)", FontSize = 11.5 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            text.Children.Add(sub);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private void ReloadPrograms()
    {
        Ui.SaveSettings();
        if (Window.GetWindow(this) is MainWindow mw) mw.InvalidatePages("programs");
    }

    private void InvalidateAll()
    {
        Ui.SaveSettings();
        if (Window.GetWindow(this) is MainWindow mw) mw.InvalidatePages("programs", "apps", "startup", "junk");
    }
}
