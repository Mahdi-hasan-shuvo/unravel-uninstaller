using System.Windows;
using System.Windows.Controls;
using Unravel.App.Infrastructure;
using Unravel.Core.Tools;

namespace Unravel.App.Pages;

/// <summary>One-click access to useful tools that ship with Windows.</summary>
public sealed class ToolsPage : UserControl, IPage
{
    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["restore"] = "", ["netdiag"] = "", ["netinfo"] = "", ["security"] = "", ["sysprops"] = "",
        ["msinfo"] = "", ["netstat"] = "", ["osk"] = "", ["defrag"] = "", ["services"] = "",
        ["shares"] = "", ["gpedit"] = "", ["features"] = "", ["mrt"] = "", ["backup"] = "",
        ["tasks"] = "", ["chkdsk"] = "", ["sfc"] = "",
    };

    public ToolsPage()
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "Windows tools", Style = (Style)FindResource("PageTitle") });
        header.Children.Add(new TextBlock { Text = "Shortcuts to maintenance and diagnostic tools built into Windows. Tools marked with a shield ask for administrator rights.", Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });

        var wrap = new WrapPanel();
        foreach (var tool in WindowsTools.All)
        {
            bool available = WindowsTools.IsAvailable(tool);
            var glyph = new TextBlock { Text = Glyphs.GetValueOrDefault(tool.Id, ""), Style = (Style)FindResource("Icon"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var title = new TextBlock { Text = tool.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap };
            var desc = new TextBlock { Text = available ? tool.Description : "Not available on this edition of Windows.", Style = (Style)FindResource("Muted"), FontSize = 12 };
            var top = new DockPanel();
            if (tool.NeedsAdmin)
            {
                var shield = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 13, ToolTip = "Asks for administrator rights" };
                shield.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                DockPanel.SetDock(shield, Dock.Right);
                top.Children.Add(shield);
            }
            top.Children.Add(glyph);
            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(title);
            stack.Children.Add(desc);
            var button = new Button
            {
                Content = stack, Width = 250, Height = 150, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, IsEnabled = available,
                ToolTip = $"{tool.FileName} {tool.Arguments}".Trim(),
            };
            button.Template = CardButtonTemplate();
            button.Click += (_, _) => Ui.Try($"Could not open {tool.Name}", () => WindowsTools.Launch(tool));
            wrap.Children.Add(button);
        }
        var root = new DockPanel { Margin = new Thickness(24, 20, 12, 16) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static ControlTemplate CardButtonTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "<Border x:Name='Bd' Background='{DynamicResource SurfaceBrush}' BorderBrush='{DynamicResource BorderBrush}' BorderThickness='1' CornerRadius='10' Padding='{TemplateBinding Padding}'>" +
        "<ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Top'/></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/>" +
        "<Setter TargetName='Bd' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.5'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    public void OnShown() { }
}
