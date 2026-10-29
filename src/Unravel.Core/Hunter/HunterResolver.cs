using System.Diagnostics;
using System.Text;
using Unravel.Core.Autorun;
using Unravel.Core.Native;
using Unravel.Core.Programs;
using Unravel.Core.Util;

namespace Unravel.Core.Hunter;

public enum HunterTargetKind { Window, Shortcut, File }

public sealed record HunterTarget(HunterTargetKind Kind, string ExecutablePath, string DisplayName, int? ProcessId, string? WindowTitle, string? ShortcutPath)
{
    public string FileName => Path.GetFileName(ExecutablePath);
}

/// <summary>Works out which program sits under a screen point (a window or a desktop/taskbar shortcut) and links it to installed programs and startup entries.</summary>
public static class HunterResolver
{
    /// <summary>Resolves the top-level window under the point to its process image.</summary>
    public static HunterTarget? FromWindowAt(int x, int y, int ownPid)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return null;
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        NativeMethods.GetWindowThreadProcessId(root, out int pid);
        if (pid == 0 || pid == ownPid) return null;
        var path = NativeMethods.GetProcessImagePath(pid);
        if (path is null) return null;
        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(root, title, title.Capacity);
        return new HunterTarget(HunterTargetKind.Window, path, DescribeFile(path), pid, title.ToString(), null);
    }

    /// <summary>True when the window under the point belongs to the desktop or taskbar (so a shortcut name lookup is needed).</summary>
    public static bool IsShellSurface(int x, int y)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        var cls = new StringBuilder(256);
        NativeMethods.GetClassName(root, cls, cls.Capacity);
        return cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    /// <summary>Finds a desktop/start menu shortcut by its visible label (as reported by UI Automation) and resolves its target.</summary>
    public static HunterTarget? FromShortcutLabel(string label, IEnumerable<string>? searchFolders = null)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        searchFolders ??=
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        ];
        var name = label.Trim();
        foreach (var folder in searchFolders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }); }
            catch { continue; }
            foreach (var f in files)
            {
                if (!Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var target = ResolveFileTarget(f);
                if (target is not null) return new HunterTarget(HunterTargetKind.Shortcut, target, DescribeFile(target), null, null, f);
            }
        }
        return null;
    }

    /// <summary>Resolves a shortcut (.lnk) or an executable to the program file it starts.</summary>
    public static string? ResolveFileTarget(string file)
    {
        if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var li = ShellLink.Read(file);
            return li is null || string.IsNullOrEmpty(li.TargetPath) ? null : li.TargetPath;
        }
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(file) ? file : null;
    }

    public static string DescribeFile(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Finds the installed program an executable belongs to: install folder first, then icon/uninstaller location, then name.</summary>
    public static InstalledProgram? MatchProgram(string exePath, IEnumerable<InstalledProgram> programs)
    {
        var list = programs.ToList();
        var exe = FileOps.NormalizeDir(exePath);
        var byFolder = list
            .Select(p => (p, folder: p.ResolvedInstallFolder))
            .Where(t => t.folder is not null && t.folder.Length > 3 && !Leftovers.ScanTarget.IsBroadFolder(t.folder) && FileOps.IsSameOrUnder(exe, t.folder))
            .OrderByDescending(t => t.folder!.Length) // most specific folder wins
            .Select(t => t.p)
            .FirstOrDefault();
        if (byFolder is not null) return byFolder;
        var byIcon = list.FirstOrDefault(p => CommandLine.ParseIconPath(p.DisplayIcon).Path.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (byIcon is not null) return byIcon;
        var desc = DescribeFile(exe);
        return list.FirstOrDefault(p => p.DisplayName.Equals(desc, StringComparison.OrdinalIgnoreCase));
    }

    public static List<AutorunEntry> MatchAutorun(string exePath, IEnumerable<AutorunEntry> entries) =>
        entries.Where(e => e.TargetPath is not null && e.TargetPath.Equals(exePath, StringComparison.OrdinalIgnoreCase)).ToList();
}
