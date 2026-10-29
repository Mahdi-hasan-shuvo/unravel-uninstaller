using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Unravel.App.Infrastructure;
using Unravel.Core.Cleaners;

namespace Unravel.App.Pages;

/// <summary>Shared page for the browser, Office and Windows history cleaners: grouped checklist, Analyze and Clean.</summary>
public sealed class CleanerPage : UserControl, IPage
{
    public enum Kind { Browsers, Office, Windows }

    private readonly Kind _kind;
    private readonly StackPanel _groups = new();
    private readonly TextBlock _summary = new();
    private readonly Button _analyze = new() { Content = "Analyze", MinWidth = 100 };
    private readonly Button _clean = new() { Content = "Clean selected", MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
    private List<CleanerItem> _items = [];
    private readonly Dictionary<CleanerItem, TextBlock> _statusLabels = new();
    private bool _busy;

    public CleanerPage(Kind kind)
    {
        _kind = kind;
        _clean.Style = (Style)FindResource("PrimaryButton");
        _analyze.Click += async (_, _) => await RunAsync(execute: false);
        _clean.Click += async (_, _) => await RunAsync(execute: true);
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _summary.VerticalAlignment = VerticalAlignment.Center;

        var (title, subtitle) = kind switch
        {
            Kind.Browsers => ("Browser history", "Erase visited pages, downloads, address bar entries, cookies, cache and form entries. Close a browser before cleaning it."),
            Kind.Office => ("Office history", "Remove the lists of recently opened documents in Word, Excel, PowerPoint, Access and FrontPage, for every installed version."),
            _ => ("Windows history", "Remove usage traces Windows keeps – recent documents, Run box, searches, dialog history – and delete temporary files."),
        };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("PageTitle") });
        header.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });

        var toolbar = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_analyze);
        buttons.Children.Add(_clean);
        DockPanel.SetDock(buttons, Dock.Right);
        toolbar.Children.Add(buttons);
        toolbar.Children.Add(_summary);
        var toolbarCard = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 12), Child = toolbar };

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _groups };
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(toolbarCard, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(toolbarCard);
        root.Children.Add(scroll);
        Content = root;
    }

    public void OnShown()
    {
        if (_busy) return;
        var previouslyChecked = _items.Where(i => i.IsChecked).Select(i => i.Id).ToHashSet();
        _items = _kind switch
        {
            Kind.Browsers => BrowserCleaner.BuildItems(BrowserCleaner.Detect()),
            Kind.Office => OfficeCleaner.BuildItems(),
            _ => WindowsCleaner.BuildItems(),
        };
        foreach (var i in _items)
            i.IsChecked = previouslyChecked.Count > 0 ? previouslyChecked.Contains(i.Id) : DefaultChecked(i);
        Build();
        _summary.Text = "Press Analyze to see how much will be removed.";
    }

    // Data that signs people out, loses open tabs or empties the Recycle Bin is opt-in.
    private static bool DefaultChecked(CleanerItem i) =>
        !i.Id.EndsWith(":cookies") && !i.Id.EndsWith(":sessions") && !i.Id.EndsWith(":forms") && i.Id is not ("win:clipboard" or "win:recyclebin");

    private void Build()
    {
        _groups.Children.Clear();
        _statusLabels.Clear();
        if (_items.Count == 0)
        {
            _groups.Children.Add(new TextBlock { Text = "No supported browsers were found for this account.", Style = (Style)FindResource("Muted") });
            return;
        }
        foreach (var g in _items.GroupBy(i => i.Group))
        {
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12) };
            var stack = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var toggle = new Button { Content = "Select all", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 2, 8, 2), MinHeight = 26 };
            var groupItems = g.ToList();
            toggle.Click += (_, _) =>
            {
                bool all = groupItems.All(i => i.IsChecked);
                foreach (var i in groupItems) i.IsChecked = !all;
            };
            DockPanel.SetDock(toggle, Dock.Right);
            head.Children.Add(toggle);
            var gTitle = new TextBlock { Text = g.Key, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0) };
            head.Children.Add(gTitle);
            stack.Children.Add(head);

            foreach (var item in groupItems)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 140, TextAlignment = TextAlignment.Right };
                status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                _statusLabels[item] = status;
                DockPanel.SetDock(status, Dock.Right);
                row.Children.Add(status);
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = item.Title });
                var sub = item.Description ?? item.Note;
                if (sub is not null)
                {
                    var t = new TextBlock { Text = sub, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    label.Children.Add(t);
                }
                var cb = new CheckBox { Content = label, VerticalContentAlignment = VerticalAlignment.Top };
                cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(CleanerItem.IsChecked)) { Source = item, Mode = System.Windows.Data.BindingMode.TwoWay });
                row.Children.Add(cb);
                stack.Children.Add(row);
            }
            card.Child = stack;
            _groups.Children.Add(card);
        }
    }

    private async Task RunAsync(bool execute)
    {
        var selected = _items.Where(i => i.IsChecked).ToList();
        if (selected.Count == 0) { Ui.Info("Nothing selected", "Check the items you want to analyze or clean."); return; }

        if (execute)
        {
            // Programs that hold the data open must be closed first (like the original: offer to force-stop them).
            var blockers = selected.SelectMany(CleanupEngine.RunningBlockers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (blockers.Count > 0)
            {
                var choice = Ui.Ask("Close running programs?", $"{string.Join(", ", blockers)} is running and keeps its data locked.\n\nClose it now? Unsaved data in it may be lost.",
                    "Close and continue", "Skip locked items", "Cancel");
                if (choice == 2 || choice < 0) return;
                if (choice == 0)
                    foreach (var b in blockers)
                        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(b)))
                            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
            }
            if (!Ui.Confirm("Clean selected items", $"Permanently clear {selected.Count} selected item(s)? This cannot be undone.", "Clean", danger: true)) return;
        }

        _busy = true;
        _analyze.IsEnabled = _clean.IsEnabled = false;
        var total = new CleanCount();
        foreach (var item in selected)
        {
            _statusLabels[item].Text = execute ? "Cleaning…" : "Analyzing…";
            var r = await Task.Run(() => execute ? CleanupEngine.Execute(item) : CleanupEngine.Preview(item));
            total += r;
            var label = _statusLabels[item];
            label.Text = r.Errors.Count > 0 ? (execute ? $"{r} · partly" : "Not accessible") : execute ? (r.Items == 0 ? "Already clean" : $"Removed {r}") : r.Items == 0 ? "Nothing to clean" : r.ToString();
            label.ToolTip = r.Errors.Count > 0 ? string.Join("\n", r.Errors) : null;
            label.SetResourceReference(TextBlock.ForegroundProperty, r.Errors.Count > 0 ? "WarningBrush" : execute ? "SuccessBrush" : "TextBrush");
        }
        foreach (var other in _items.Except(selected)) _statusLabels[other].Text = "";
        _summary.Text = execute ? $"Removed {total}." : $"Found {total} to clean.";
        if (total.Errors.Count > 0) _summary.Text += $" {total.Errors.Count} problem(s) – hover the status for details.";
        _analyze.IsEnabled = _clean.IsEnabled = true;
        _busy = false;
    }
}
