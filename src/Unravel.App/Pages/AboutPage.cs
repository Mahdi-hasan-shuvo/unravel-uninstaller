using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Unravel.Core.Util;

namespace Unravel.App.Pages;

public sealed class AboutPage : UserControl, IPage
{
    public AboutPage()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 16), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/unravel.png")), Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0) });
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Unravel", Style = (Style)FindResource("PageTitle") });
        title.Children.Add(new TextBlock { Text = $"Version {version} · {(ProcessUtil.IsElevated ? "running as administrator" : "running with standard rights")}", Style = (Style)FindResource("Muted") });
        head.Children.Add(title);
        stack.Children.Add(head);

        void Card(string heading, string body)
        {
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Text = heading, Style = (Style)FindResource("SectionTitle") });
            s.Children.Add(new TextBlock { Text = body, Style = (Style)FindResource("Muted"), LineHeight = 20 });
            stack.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = s });
        }

        Card("Uninstalling a program",
            "Select it in Programs and press Uninstall. Unravel notes where the program lives, optionally creates a restore point, then runs the program's own uninstaller. " +
            "Afterwards choose Safe, Moderate or Advanced and scan for leftovers. Only bold items – those identified as created by the program – are deleted, and every registry deletion is backed up first.");
        Card("Hunter mode",
            "Hunter mode hides the main window and shows a round target. Drag the target onto any window, desktop shortcut or taskbar icon and release: " +
            "you can uninstall the program, stop it from starting with Windows, end it, end and delete it, open its folder or look it up. Right-click the target for its options.");
        Card("Keyboard shortcuts",
            "Ctrl+F – search the program list\nF5 – refresh\nDel – uninstall the selected program\nEnter – show details\nCtrl+C in any message – copy its text");
        Card("Independent software",
            "Unravel is an independent implementation. It was designed from public documentation and observed behaviour of uninstaller tools in general; " +
            "it contains no code, text or artwork from any other product.");
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void OnShown() { }
}
