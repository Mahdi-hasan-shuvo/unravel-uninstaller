using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Unravel.Core.Util;

namespace Unravel.App.Infrastructure;

/// <summary>Extracts and caches program icons (from DisplayIcon paths, executables or icon files).</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;

    /// <summary>Icon for a "path,index" string; returns null when nothing usable is found.</summary>
    public static ImageSource? FromIconLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        return Cache.GetOrAdd(location, loc =>
        {
            var (path, index) = CommandLine.ParseIconPath(loc);
            return Extract(path, index);
        });
    }

    public static ImageSource? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Cache.GetOrAdd("file:" + path, _ => Extract(path, 0) ?? ShellIcon(path));
    }

    private static ImageSource? Extract(string path, int index)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 64;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            var large = new IntPtr[1];
            if (ExtractIconEx(path, index, large, null, 1) == 0 || large[0] == IntPtr.Zero)
                return index != 0 ? Extract(path, 0) : ShellIcon(path);
            try { return ToImage(large[0]); }
            finally { DestroyIcon(large[0]); }
        }
        catch { return null; }
    }

    private static ImageSource? ShellIcon(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_LARGEICON | (File.Exists(path) || Directory.Exists(path) ? 0 : SHGFI_USEFILEATTRIBUTES);
            SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (info.hIcon == IntPtr.Zero) return null;
            try { return ToImage(info.hIcon); }
            finally { DestroyIcon(info.hIcon); }
        }
        catch { return null; }
    }

    private static ImageSource ToImage(IntPtr hIcon)
    {
        var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
