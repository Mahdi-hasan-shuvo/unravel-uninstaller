using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Unravel.Core.Util;

/// <summary>Minimal IShellLink wrapper to read and create .lnk shortcuts.</summary>
public static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public sealed record LinkInfo(string TargetPath, string Arguments, string WorkingDirectory);

    /// <summary>Reads a shortcut's target without resolving (no UI, no network search). Returns null when unreadable.</summary>
    public static LinkInfo? Read(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var target = Environment.ExpandEnvironmentVariables(sb.ToString());
            var args = new StringBuilder(2048);
            link.GetArguments(args, args.Capacity);
            var wd = new StringBuilder(1024);
            link.GetWorkingDirectory(wd, wd.Capacity);
            Marshal.FinalReleaseComObject(link);
            return new LinkInfo(target, args.ToString(), wd.ToString());
        }
        catch { return null; }
    }

    public static void Create(string lnkPath, string target, string arguments = "", string? workingDir = null, string? description = null)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(target);
        if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
        link.SetWorkingDirectory(workingDir ?? Path.GetDirectoryName(target) ?? "");
        if (description is not null) link.SetDescription(description);
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        ((IPersistFile)link).Save(lnkPath, true);
        Marshal.FinalReleaseComObject(link);
    }

    /// <summary>Reads the target of an Internet shortcut (.url).</summary>
    public static string? ReadUrlFile(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line.Substring(4).Trim();
        }
        catch { }
        return null;
    }
}
