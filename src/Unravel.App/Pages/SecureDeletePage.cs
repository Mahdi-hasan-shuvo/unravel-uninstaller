using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Unravel.App.Infrastructure;
using Unravel.Core.Secure;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

/// <summary>Unrecoverable delete: queue files/folders (buttons or drag and drop) and overwrite them before deletion.</summary>
public sealed class SecureDeletePage : UserControl, IPage
{
    private readonly ObservableCollection<string> _paths = new();
    private readonly ListBox _list = new();
    private readonly ComboBox _passes = new() { Width = 220 };
    private readonly Button _erase = new() { Content = "Delete forever", MinWidth = 140 };
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _drop;
    private CancellationTokenSource? _cts;

    public SecureDeletePage()
    {
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "Secure delete", Style = (Style)FindResource("PageTitle") });
        header.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0),
            Text = "Overwrites files with random data before deleting them, so recovery tools cannot bring them back. Drag files or folders here, or add them with the buttons.",
        });

        var addFiles = new Button { Content = "Add files…", Margin = new Thickness(0, 0, 8, 0) };
        addFiles.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Multiselect = true, Title = "Choose files to erase" };
            if (d.ShowDialog(Window.GetWindow(this)) == true) Add(d.FileNames);
        };
        var addFolder = new Button { Content = "Add folder…", Margin = new Thickness(0, 0, 8, 0) };
        addFolder.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = "Choose a folder to erase (with everything inside)" };
            if (d.ShowDialog(Window.GetWindow(this)) == true) Add([d.FolderName]);
        };
        var remove = new Button { Content = "Remove from list", Style = (Style)FindResource("GhostButton") };
        remove.Click += (_, _) => { foreach (var s in _list.SelectedItems.Cast<string>().ToList()) _paths.Remove(s); Update(); };
        var clear = new Button { Content = "Clear list", Style = (Style)FindResource("GhostButton") };
        clear.Click += (_, _) => { _paths.Clear(); Update(); };

        _passes.Items.Add(new ComboBoxItem { Content = "1 pass (fast)", Tag = 1 });
        _passes.Items.Add(new ComboBoxItem { Content = "3 passes (recommended)", Tag = 3 });
        _passes.Items.Add(new ComboBoxItem { Content = "7 passes (slow)", Tag = 7 });
        _passes.SelectedIndex = 1;
        _erase.Style = (Style)FindResource("DangerButton");
        _erase.Click += async (_, _) => await EraseAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();

        var bar = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_passes);
        right.Children.Add(new Border { Width = 8 });
        right.Children.Add(_erase);
        right.Children.Add(_cancel);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        var left = new WrapPanel { Orientation = Orientation.Horizontal };   // wraps instead of sliding under the right-hand controls
        left.Children.Add(addFiles);
        left.Children.Add(addFolder);
        left.Children.Add(remove);
        left.Children.Add(clear);
        bar.Children.Add(left);
        var barCard = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12), Child = bar };

        _list.ItemsSource = _paths;
        _list.SelectionMode = SelectionMode.Extended;
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal' Margin='6,4'>" +
            "<TextBlock FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' Text='&#xE8A5;' Margin='0,0,10,0' VerticalAlignment='Center'/>" +
            "<TextBlock Text='{Binding}' VerticalAlignment='Center'/></StackPanel></DataTemplate>");

        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        var glyph = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 34 };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "FaintTextBrush");
        empty.Children.Add(glyph);
        var hint = new TextBlock { Text = "Drop files and folders here", Margin = new Thickness(0, 10, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        empty.Children.Add(hint);
        var listGrid = new Grid();
        listGrid.Children.Add(_list);
        listGrid.Children.Add(empty);
        _paths.CollectionChanged += (_, _) => empty.Visibility = _paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _drop = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(8), Child = listGrid };

        var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        foot.Children.Add(_status);
        foot.Children.Add(_progress);
        var note = new TextBlock
        {
            Text = "Note: on SSDs and USB flash drives, the device's wear levelling can keep old copies of data that no program can overwrite. For full protection on those, use encryption (BitLocker).",
            Style = (Style)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
        };
        foot.Children.Add(note);

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(barCard, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(barCard);
        root.Children.Add(foot);
        root.Children.Add(_drop);
        Content = root;
        Update();
    }

    public void OnShown() { }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
    }

    private void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p).TrimEnd('\\');
            if (full.Length <= 3) { Ui.Error("Not allowed", "A whole drive cannot be added. Use \"Wipe free space\" for drives."); continue; }
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (FileOps.IsSameOrUnder(full, win) || Leftovers_IsCritical(full)) { Ui.Error("Not allowed", $"{full} is a system folder and cannot be erased."); continue; }
            if (!_paths.Contains(full, StringComparer.OrdinalIgnoreCase)) _paths.Add(full);
        }
        Update();
    }

    private static bool Leftovers_IsCritical(string path)
    {
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData })
        {
            var p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p) && FileOps.IsSameOrUnder(p, path)) return true; // the path is that folder or one of its parents
        }
        return false;
    }

    private void Update()
    {
        _erase.IsEnabled = _paths.Count > 0 && _cts is null;
        if (_cts is null) _status.Text = _paths.Count == 0 ? "" : $"{_paths.Count} item(s) queued.";
    }

    private async Task EraseAsync()
    {
        var items = _paths.ToList();
        if (items.Count == 0) return;
        if (!Ui.ConfirmAlways("Delete forever?", $"{items.Count} item(s) will be overwritten and deleted. They cannot be recovered – not even from the Recycle Bin.\n\n" +
                string.Join("\n", items.Take(8).Select(i => "• " + i)) + (items.Count > 8 ? "\n…" : ""), "Delete forever"))
            return;
        int passes = (int)((ComboBoxItem)_passes.SelectedItem).Tag;
        _cts = new CancellationTokenSource();
        _erase.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        var progress = new Progress<SecureDeleteProgress>(p =>
        {
            _progress.Value = p.BytesTotal == 0 ? 0 : 100.0 * p.BytesDone / p.BytesTotal;
            _status.Text = $"Erasing {p.ItemsDone + (p.CurrentPath.Length > 0 ? 1 : 0)} of {p.ItemsTotal}: {p.CurrentPath}";
        });
        var report = await Task.Run(() => new SecureDelete { Passes = passes }.Erase(items, progress, _cts.Token));
        _cts = null;
        _cancel.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Collapsed;
        foreach (var i in items.Where(i => !File.Exists(i) && !Directory.Exists(i))) _paths.Remove(i);
        _status.Text = $"{report.FilesErased} file(s) erased, {Format.Bytes(report.BytesOverwritten)} overwritten" + (report.Cancelled ? " – cancelled (files already erased stay erased)." : ".");
        Update();
        if (report.Failures.Count > 0)
            Ui.Error($"{report.Failures.Count} item(s) could not be erased", string.Join("\n", report.Failures.Take(12).Select(f => $"• {f.Path}: {f.Error}")) +
                "\n\nFiles open in another program, or protected by Windows, cannot be erased.");
    }
}
