using System.Windows;
using System.Windows.Controls;
using Unravel.App.Infrastructure;
using Unravel.Core.Secure;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

/// <summary>Evidence remover: overwrites the free space of selected drives so already-deleted files cannot be recovered.</summary>
public sealed class WipePage : UserControl, IPage
{
    private readonly ItemsControl _drives = new();
    private readonly ComboBox _passes = new() { Width = 220 };
    private readonly CheckBox _mft = new() { Content = "Also overwrite unused file-table records (removes names and tiny files)", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _run = new() { Content = "Wipe free space", MinWidth = 150 };
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ProgressBar _progress = new() { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private List<DriveChoice> _choices = [];
    private CancellationTokenSource? _cts;

    public WipePage()
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "Wipe free space", Style = (Style)FindResource("PageTitle") });
        header.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0),
            Text = "Deleted files stay on the disk until something overwrites them. This fills each selected drive's free space with random data (several passes), then removes it again. Your existing files are not touched.",
        });

        _drives.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<CheckBox IsChecked='{Binding IsChecked, Mode=TwoWay}' Margin='0,5'><StackPanel><TextBlock Text='{Binding Label}'/>" +
            "<TextBlock Text='{Binding Detail}' FontSize='11' Opacity='0.7'/></StackPanel></CheckBox></DataTemplate>");
        _passes.Items.Add(new ComboBoxItem { Content = "1 pass (fast)", Tag = 1 });
        _passes.Items.Add(new ComboBoxItem { Content = "3 passes (recommended)", Tag = 3 });
        _passes.SelectedIndex = 1;
        _run.Style = (Style)FindResource("PrimaryButton");
        _run.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = "Drives", Style = (Style)FindResource("SectionTitle") });
        card.Children.Add(_drives);
        card.Children.Add(new TextBlock { Text = "Overwrite", Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 14, 0, 6) });
        card.Children.Add(_passes);
        card.Children.Add(_mft);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_run);
        buttons.Children.Add(_cancel);
        card.Children.Add(buttons);
        card.Children.Add(_progress);
        card.Children.Add(_status);
        var note = new TextBlock
        {
            Style = (Style)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 14, 0, 0),
            Text = "This takes a long time on large drives, and the drive is temporarily almost full while it runs – avoid saving files meanwhile. On SSDs, prefer Windows' built-in TRIM (Windows tools › Defragment and Optimize Drives); flash wear levelling limits what overwriting can guarantee.",
        };
        card.Children.Add(note);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        root.Children.Add(header);
        root.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = card, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left });
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void OnShown()
    {
        if (_cts is not null) return;
        _choices = DriveChoice.Fixed([]);
        foreach (var c in _choices) c.IsChecked = false;
        _drives.ItemsSource = _choices;
    }

    private async Task RunAsync()
    {
        var drives = _choices.Where(c => c.IsChecked).ToList();
        if (drives.Count == 0) { Ui.Error("No drive selected", "Check the drive(s) whose free space should be wiped."); return; }
        if (!Ui.ConfirmAlways("Wipe free space?", $"Overwrite the free space on {string.Join(", ", drives.Select(d => d.Root.TrimEnd('\\')))}?\n\nThis can take hours on large drives. The drive will temporarily be nearly full.", "Start", danger: false))
            return;
        int passes = (int)((ComboBoxItem)_passes.SelectedItem).Tag;
        _cts = new CancellationTokenSource();
        _run.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        var summary = new List<string>();
        foreach (var d in drives)
        {
            if (_cts.IsCancellationRequested) break;
            var progress = new Progress<WipeProgress>(p =>
            {
                _progress.Value = p.BytesTarget == 0 ? 0 : 100.0 * p.BytesWritten / p.BytesTarget;
                _status.Text = $"{p.Drive.TrimEnd('\\')} – {p.Phase}, pass {p.Pass} of {p.Passes}: {Format.Bytes(p.BytesWritten)} of {Format.Bytes(p.BytesTarget)}";
            });
            var wiper = new FreeSpaceWiper { Passes = passes, WipeFileTableSlack = _mft.IsChecked == true };
            var r = await Task.Run(() => wiper.Wipe(d.Root, progress, _cts.Token));
            summary.Add($"{d.Root.TrimEnd('\\')}: " + (r.Error is not null ? "failed – " + r.Error
                : r.Cancelled ? $"cancelled after {r.PassesCompleted} pass(es)"
                : $"{r.PassesCompleted} pass(es) over {Format.Bytes(r.BytesPerPass)}") + (r.TempCleanedUp ? "" : " (temporary files could not be removed)"));
        }
        _cts = null;
        _run.IsEnabled = true;
        _cancel.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Collapsed;
        _status.Text = string.Join("\n", summary);
        OnShown();
    }
}
