using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Unravel.App.Infrastructure;

namespace Unravel.App.Dialogs;

public enum DialogKind { Info, Question, Warning, Danger, Error }

/// <summary>A themed replacement for MessageBox with arbitrary buttons. Returns the index of the button pressed (-1 = closed).</summary>
public sealed class MessageDialog : Window
{
    private int _result = -1;

    private MessageDialog(string title, string message, DialogKind kind, IReadOnlyList<string> buttons)
    {
        Title = title;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        ThemeManager.ApplyChrome(this);

        var (glyph, brush) = kind switch
        {
            DialogKind.Error => ("", "DangerBrush"),
            DialogKind.Danger => ("", "DangerBrush"),
            DialogKind.Warning => ("", "WarningBrush"),
            DialogKind.Question => ("", "AccentBrush"),
            _ => ("", "AccentBrush"),
        };

        var icon = new TextBlock { Text = glyph, FontSize = 26, Style = (Style)FindResource("Icon"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 16, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var heading = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        // Ctrl+C copies the whole message (handled below), which is useful for error details.
        var bodyText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var text = new StackPanel { MaxWidth = 540 };
        text.Children.Add(heading);
        text.Children.Add(bodyText);

        var top = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            if (i == 0)
            {
                b.Style = (Style)FindResource(kind is DialogKind.Danger or DialogKind.Error && buttons.Count > 1 ? "DangerButton" : "PrimaryButton");
                b.IsDefault = true;
            }
            if (i == buttons.Count - 1 && buttons.Count > 1) b.IsCancel = true;
            b.Click += (_, _) => { _result = index; DialogResult = true; };
            buttonRow.Children.Add(b);
        }
        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), Child = buttonRow };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");

        var root = new DockPanel { MinWidth = 380 };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(top);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Clipboard.SetText(title + "\n\n" + message); e.Handled = true; }
        };
    }

    public static int Show(Window? owner, string title, string message, DialogKind kind, IReadOnlyList<string> buttons)
    {
        var d = new MessageDialog(title, message, kind, buttons);
        if (owner is not null && owner.IsVisible) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d._result;
    }
}
