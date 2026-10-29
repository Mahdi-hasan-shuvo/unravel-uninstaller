using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Unravel.App.Infrastructure;
using Unravel.Core.Logging;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

/// <summary>Everything Unravel removed that can be put back, plus the detailed activity log.</summary>
public sealed class RecoveryPage : UserControl, IPage
{
    private readonly StackPanel _quarantine = new();
    private readonly DataGrid _log = new() { RowHeight = 30, Background = Brushes.Transparent };
    private readonly ComboBox _days = new() { Width = 200 };
    private readonly Quarantine _store = new();

    public RecoveryPage()
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "Activity & recovery", Style = (Style)FindResource("PageTitle") });
        header.Children.Add(new TextBlock
        {
            Text = "Restore anything Unravel moved to quarantine, and review a detailed log of every change it made. Registry backups are under Settings › Registry backups.",
            Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0),
        });

        var qCard = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12) };
        var qStack = new StackPanel();
        qStack.Children.Add(new TextBlock { Text = "Quarantine", Style = (Style)FindResource("SectionTitle") });
        qStack.Children.Add(new ScrollViewer { Content = _quarantine, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        qCard.Child = qStack;

        _log.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding(nameof(LogEntry.Time)) { StringFormat = "T" }, Width = 90 });
        _log.Columns.Add(new DataGridTextColumn { Header = "Level", Binding = new Binding(nameof(LogEntry.Level)), Width = 80 });
        _log.Columns.Add(new DataGridTextColumn { Header = "Area", Binding = new Binding(nameof(LogEntry.Area)), Width = 130 });
        _log.Columns.Add(new DataGridTextColumn { Header = "What happened", Binding = new Binding(nameof(LogEntry.Message)), Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
        _log.Columns.Add(new DataGridTextColumn { Header = "Details", Binding = new Binding(nameof(LogEntry.Detail)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _log.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.VisibleWhenSelected;
        _log.RowDetailsTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBox Text='{Binding Detail, Mode=OneWay}' IsReadOnly='True' " +
            "TextWrapping='Wrap' Margin='12,0,12,8' MaxHeight='180' VerticalScrollBarVisibility='Auto' FontFamily='Cascadia Mono, Consolas' FontSize='12'/></DataTemplate>");
        _days.SelectionChanged += (_, _) => LoadLog();
        var openFolder = new Button { Content = "Open log folder", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(8, 0, 0, 0) };
        openFolder.Click += (_, _) => Ui.Try("Open folder", () => { Directory.CreateDirectory(ActivityLog.Folder); Shell.ShowInExplorer(ActivityLog.Folder); });
        var logHead = new DockPanel { Margin = new Thickness(14, 10, 14, 8) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_days);
        right.Children.Add(openFolder);
        DockPanel.SetDock(right, Dock.Right);
        logHead.Children.Add(right);
        logHead.Children.Add(new TextBlock { Text = "Activity log", Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        var logDock = new DockPanel();
        DockPanel.SetDock(logHead, Dock.Top);
        logDock.Children.Add(logHead);
        logDock.Children.Add(_log);
        var logCard = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Child = logDock };

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(qCard, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(qCard);
        root.Children.Add(logCard);
        Content = root;
    }

    public void OnShown()
    {
        LoadQuarantine();
        var days = ActivityLog.AvailableDays();
        if (days.Count == 0) days.Add(DateTime.Today);
        _days.Items.Clear();
        foreach (var d in days) _days.Items.Add(new ComboBoxItem { Content = d.ToString("D"), Tag = d });
        _days.SelectedIndex = 0;
    }

    private void LoadLog()
    {
        if (_days.SelectedItem is not ComboBoxItem { Tag: DateTime day }) return;
        _log.ItemsSource = ActivityLog.ReadDay(day).OrderByDescending(e => e.Time).ToList();
    }

    private void LoadQuarantine()
    {
        _quarantine.Children.Clear();
        var sets = _store.List();
        if (sets.Count == 0)
        {
            _quarantine.Children.Add(new TextBlock { Text = "Nothing is in quarantine.", Style = (Style)FindResource("Muted") });
            return;
        }
        foreach (var s in sets)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var restore = new Button { Content = "Restore", Style = (Style)FindResource("GhostButton"), MinHeight = 28, Padding = new Thickness(10, 3, 10, 3) };
            restore.Click += async (_, _) =>
            {
                if (!Ui.Confirm("Restore", $"Put the {s.Entries.Count} item(s) of \"{s.Title}\" back where they were?", "Restore")) return;
                var r = await Task.Run(() => _store.Restore(s));
                var failed = r.Where(x => !x.Ok).ToList();
                var renamed = r.Where(x => x.Ok && x.Error is not null).ToList();
                if (failed.Count > 0) Ui.Error("Some items were not restored", string.Join("\n", failed.Select(f => $"• {f.Path}: {f.Error}")));
                else Ui.Info("Restored", $"{r.Count} item(s) restored." + (renamed.Count > 0 ? $"\n{renamed.Count} were restored under a new name because the original path is in use again." : ""));
                LoadQuarantine();
            };
            var purge = new Button { Content = "Delete permanently", Style = (Style)FindResource("GhostButton"), MinHeight = 28, Padding = new Thickness(10, 3, 10, 3) };
            purge.Click += (_, _) =>
            {
                if (!Ui.ConfirmAlways("Delete permanently", $"Permanently delete the {s.Entries.Count} quarantined item(s) of \"{s.Title}\" ({Format.Bytes(s.TotalSize)})? This cannot be undone.", "Delete permanently")) return;
                Ui.Try("Delete permanently", () => _store.Purge(s));
                LoadQuarantine();
            };
            var details = new Button { Content = "Contents", Style = (Style)FindResource("GhostButton"), MinHeight = 28, Padding = new Thickness(10, 3, 10, 3) };
            details.Click += (_, _) => Ui.Info(s.Title, string.Join("\n", s.Entries.Take(40).Select(e => $"• {e.OriginalPath} ({Format.Bytes(e.Size)})")) + (s.Entries.Count > 40 ? "\n…" : ""));
            buttons.Children.Add(details);
            buttons.Children.Add(restore);
            buttons.Children.Add(purge);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold });
            var sub = new TextBlock { Text = $"{s.Created:g} · {s.Entries.Count} item(s) · {Format.Bytes(s.TotalSize)}", FontSize = 11.5 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            text.Children.Add(sub);
            row.Children.Add(text);
            _quarantine.Children.Add(row);
        }
    }
}
