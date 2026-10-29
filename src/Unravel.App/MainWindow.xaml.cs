using System.Windows;
using System.Windows.Controls;
using Unravel.App.Infrastructure;
using Unravel.App.Pages;
using Unravel.Core.Util;

namespace Unravel.App;

public interface IPage
{
    /// <summary>Called every time the page becomes visible.</summary>
    void OnShown();
}

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private HunterWindow? _hunter;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyChrome(this);
        AdminBanner.Visibility = ProcessUtil.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        if (ProcessUtil.IsElevated) Title = "Unravel (Administrator)";
        Loaded += (_, _) => NavPrograms.IsChecked = true;
        SizeChanged += (_, e) => { if (e.WidthChanged) ApplyNavLayout(e.NewSize.Width < CompactBelow); };
    }

    // ------------------------------------------------------------ responsive navigation

    /// <summary>Below this window width the navigation collapses to an icon rail, giving the pages room.</summary>
    private const double CompactBelow = 1180;
    private bool? _compactNav;
    private readonly HashSet<FrameworkElement> _navTips = [];

    private void ApplyNavLayout(bool compact)
    {
        if (_compactNav == compact) return;
        _compactNav = compact;
        NavColumn.Width = new GridLength(compact ? 68 : 232);
        NavPanel.Margin = compact ? new Thickness(8, 16, 8, 12) : new Thickness(12, 16, 12, 12);
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var child in NavList.Children.OfType<UIElement>().Concat(NavFooter.Children.OfType<UIElement>()))
        {
            switch (child)
            {
                case TextBlock section:   // group headings become simple gaps
                    section.Visibility = compact ? Visibility.Hidden : Visibility.Visible;
                    section.Height = compact ? 0 : double.NaN;
                    break;
                case ContentControl { Content: StackPanel item } nav when item.Children.Count >= 2:
                    var label = (TextBlock)item.Children[^1];
                    label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                    ((FrameworkElement)item.Children[0]).Margin = compact ? new Thickness(4, 0, 0, 0) : new Thickness(0, 0, 12, 0);
                    // The label moves into the tooltip so every icon still says what it is.
                    if (compact && nav.ToolTip is null) { nav.ToolTip = label.Text; _navTips.Add(nav); }
                    else if (!compact && _navTips.Remove(nav)) nav.ClearValue(ToolTipProperty);
                    break;
            }
        }
    }

    private FrameworkElement CreatePage(string tag) => tag switch
    {
        "programs" => new ProgramsPage(),
        "apps" => new WindowsAppsPage(),
        "startup" => new StartupPage(),
        "junk" => new JunkPage(),
        "browsers" => new CleanerPage(CleanerPage.Kind.Browsers),
        "office" => new CleanerPage(CleanerPage.Kind.Office),
        "windows" => new CleanerPage(CleanerPage.Kind.Windows),
        "shred" => new SecureDeletePage(),
        "wipe" => new WipePage(),
        "tools" => new ToolsPage(),
        "packages" => new PackagesPage(),
        "monitor" => new MonitorPage(),
        "performance" => new PerformancePage(),
        "tracker" => new InstallTrackerPage(),
        "duplicates" => new DuplicatesPage(),
        "recovery" => new RecoveryPage(),
        "settings" => new SettingsPage(),
        "about" => new AboutPage(),
        _ => new TextBlock { Text = tag }
    };

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        ShowPage(tag);
    }

    /// <summary>Switches page and keeps the navigation highlight in sync.</summary>
    public void Navigate(string tag)
    {
        var radio = FindNav(this, tag);
        if (radio is not null && radio.IsChecked != true) radio.IsChecked = true;
        else ShowPage(tag);
    }

    private static RadioButton? FindNav(DependencyObject root, string tag)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton { GroupName: "Nav" } rb && Equals(rb.Tag, tag)) return rb;
            if (FindNav(child, tag) is { } found) return found;
        }
        return null;
    }

    private void ShowPage(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = CreatePage(tag);
            _pages[tag] = page;
        }
        PageHost.Content = page;
        if (page is IPage p) p.OnShown();
    }

    /// <summary>Settings changes that affect other pages (e.g. showing system components) invalidate cached pages.</summary>
    public void InvalidatePages(params string[] tags)
    {
        foreach (var t in tags) _pages.Remove(t);
    }

    public ProgramsPage? ProgramsPage => _pages.TryGetValue("programs", out var p) ? p as ProgramsPage : null;

    private void Elevate_Click(object sender, RoutedEventArgs e) => Ui.RestartAsAdministrator();

    private void Hunter_Click(object sender, RoutedEventArgs e) => EnterHunterMode(showMainWhenDone: true);

    public void EnterHunterMode(bool showMainWhenDone)
    {
        if (_hunter is not null) { _hunter.Activate(); return; }
        _hunter = new HunterWindow(this);
        _hunter.Closed += (_, _) => _hunter = null;
        Hide();
        _hunter.Show();
    }

    public void ShowFromHunter()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
