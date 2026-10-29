using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Unravel.Core.Settings;

namespace Unravel.App.Infrastructure;

public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    public static bool SystemPrefersDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int i && i == 0;
    }

    public static void Apply(ThemeMode mode)
    {
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        var dicts = Application.Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        foreach (Window w in Application.Current.Windows) ApplyChrome(w);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Matches the native title bar to the app theme.</summary>
    public static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            w.SourceInitialized -= OnSourceInitialized;
            w.SourceInitialized += OnSourceInitialized;
            return;
        }
        int dark = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)); } catch { }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) ApplyChrome(w);
    }
}
