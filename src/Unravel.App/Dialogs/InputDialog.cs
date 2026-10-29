using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Unravel.App.Infrastructure;

namespace Unravel.App.Dialogs;

/// <summary>Asks for one or two lines of text (e.g. a pattern and its description).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _first = new();
    private readonly TextBox _second = new();

    public string Value => _first.Text.Trim();
    public string SecondValue => _second.Text.Trim();

    public InputDialog(string title, string prompt, string hint, string? secondPrompt = null, string initial = "")
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        ThemeManager.ApplyChrome(this);

        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        form.Children.Add(new TextBlock { Text = prompt, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _first.Text = initial;
        form.Children.Add(_first);
        var h = new TextBlock { Text = hint, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        h.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        form.Children.Add(h);
        if (secondPrompt is not null)
        {
            form.Children.Add(new TextBlock { Text = secondPrompt, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
            form.Children.Add(_second);
        }
        var ok = new Button { Content = "OK", Style = (Style)FindResource("PrimaryButton"), MinWidth = 90, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (Value.Length == 0) { _first.Focus(); return; }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), Child = buttons };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        Content = root;
        Loaded += (_, _) => { _first.Focus(); _first.SelectAll(); };
    }
}
