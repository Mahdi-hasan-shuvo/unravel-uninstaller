using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Unravel.App.Infrastructure;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.App.Dialogs;

public sealed record PreviewItem(string Location, string Kind, SafetyAssessment Safety, long Size = 0);

/// <summary>
/// Shows exactly what will be deleted, grouped by confidence, before any destructive action. Shared by the
/// uninstall wizard, the install tracker cleanup and the duplicate/orphan finder.
/// </summary>
public sealed class DeletionPreviewDialog : Window
{
    private bool _confirmed;
    private readonly Dictionary<PreviewItem, CheckBox> _boxes = new();

    private DeletionPreviewDialog(string title, string intro, IReadOnlyList<PreviewItem> items, string confirmText, string? recoveryNote, bool selectable = false)
    {
        Title = title;
        Width = 760;
        Height = 560;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        ThemeManager.ApplyChrome(this);

        int high = items.Count(i => i.Safety.Confidence == Confidence.High);
        int medium = items.Count(i => i.Safety.Confidence == Confidence.Medium);
        int low = items.Count(i => i.Safety.Confidence == Confidence.Low);
        long size = items.Sum(i => i.Size);

        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        var introText = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        introText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        header.Children.Add(introText);
        var chips = new WrapPanel();
        chips.Children.Add(Chip($"{items.Count} item(s)" + (size > 0 ? $" · {Format.Bytes(size)}" : ""), "SurfaceAltBrush", "TextBrush"));
        if (high > 0) chips.Children.Add(Chip($"{high} high confidence", "SuccessSoftBrush", "SuccessBrush"));
        if (medium > 0) chips.Children.Add(Chip($"{medium} medium", "WarningSoftBrush", "WarningBrush"));
        if (low > 0) chips.Children.Add(Chip($"{low} low confidence", "DangerSoftBrush", "DangerBrush"));
        header.Children.Add(chips);
        if (low > 0)
        {
            var warn = new TextBlock
            {
                Text = "Low-confidence items may belong to something else. Review them carefully – uncheck any you are not sure about.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold,
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            header.Children.Add(warn);
        }

        var list = new StackPanel();
        foreach (var group in items.OrderBy(i => i.Safety.Confidence).ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase).GroupBy(i => i.Safety.Confidence))
        {
            var g = new TextBlock { Text = group.Key + " confidence", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
            list.Children.Add(g);
            foreach (var i in group.Take(500))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), ToolTip = i.Safety.Explanation };
                if (selectable)
                {
                    // Safe items start checked; low-confidence or risky ones must be ticked deliberately.
                    var box = new CheckBox { IsChecked = i.Safety.IsSafeToAutoSelect, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
                    System.Windows.Automation.AutomationProperties.SetName(box, "Remove " + i.Location);
                    _boxes[i] = box;
                    DockPanel.SetDock(box, Dock.Left);
                    row.Children.Add(box);
                }
                var meta = new TextBlock { Text = i.Size > 0 ? $"{i.Kind} · {Format.Bytes(i.Size)}" : i.Kind, Margin = new Thickness(12, 0, 0, 0) };
                meta.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                DockPanel.SetDock(meta, Dock.Right);
                row.Children.Add(meta);
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = i.Location, TextTrimming = TextTrimming.CharacterEllipsis });
                var why = new TextBlock { Text = i.Safety.Explanation, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
                why.SetResourceReference(TextBlock.ForegroundProperty, "FaintTextBrush");
                stack.Children.Add(why);
                row.Children.Add(stack);
                list.Children.Add(row);
            }
            if (group.Count() > 500) list.Children.Add(new TextBlock { Text = $"… and {group.Count() - 500} more" });
        }
        var listBorder = new Border
        {
            Margin = new Thickness(24, 0, 24, 12), Padding = new Thickness(12, 4, 12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        listBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");

        var ok = new Button { Content = confirmText, Style = (Style)FindResource("DangerButton"), MinWidth = 110 };
        ok.Click += (_, _) => { _confirmed = true; DialogResult = true; };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var footerDock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        footerDock.Children.Add(buttons);
        if (recoveryNote is not null)
        {
            var rn = new TextBlock { Text = recoveryNote, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0) };
            rn.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            footerDock.Children.Add(rn);
        }
        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), Child = footerDock };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(listBorder);
        Content = root;
    }

    private static Border Chip(string text, string bg, string fg)
    {
        var t = new TextBlock { Text = text, FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4), Child = t };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        return b;
    }

    /// <summary>
    /// Shows the preview with a checkbox per item and returns the items left checked (null when cancelled).
    /// Safe items start checked; low-confidence and risky items start unchecked.
    /// </summary>
    public static List<PreviewItem>? Select(Window? owner, string title, string intro, IEnumerable<PreviewItem> items, string confirmText, string? recoveryNote = null)
    {
        var list = items.Where(i => !i.Safety.IsBlocked).ToList();
        if (list.Count == 0) return null;
        var d = new DeletionPreviewDialog(title, intro, list, confirmText, recoveryNote, selectable: true);
        if (owner is not null && owner.IsVisible) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d._confirmed ? list.Where(i => d._boxes.TryGetValue(i, out var b) && b.IsChecked == true).ToList() : null;
    }

    /// <summary>Shows the preview. Blocked items are removed from the list before showing and never deleted.</summary>
    public static bool Confirm(Window? owner, string title, string intro, IEnumerable<PreviewItem> items, string confirmText = "Delete", string? recoveryNote = null)
    {
        var list = items.Where(i => !i.Safety.IsBlocked).ToList();
        if (list.Count == 0) return false;
        var d = new DeletionPreviewDialog(title, intro, list, confirmText, recoveryNote);
        if (owner is not null && owner.IsVisible) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d._confirmed;
    }
}
