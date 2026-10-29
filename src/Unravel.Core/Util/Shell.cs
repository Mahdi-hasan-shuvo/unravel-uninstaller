using System.Diagnostics;
using Microsoft.Win32;
using Unravel.Core.Native;

namespace Unravel.Core.Util;

/// <summary>Launches external things the way a user would: URLs, Explorer, Regedit, property sheets.</summary>
public static class Shell
{
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("No link is available.");
        if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url.Trim();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static string GoogleSearchUrl(string query) => "https://www.google.com/search?q=" + Uri.EscapeDataString(query.Trim());

    public static void SearchGoogle(string query) => OpenUrl(GoogleSearchUrl(query));

    public static void OpenPath(string path)
    {
        if (!FileOps.PathExists(path)) throw new FileNotFoundException("The item no longer exists.", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens Explorer with the item selected, or opens the folder itself.</summary>
    public static void ShowInExplorer(string path)
    {
        path = path.Trim().Trim('"');
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        else
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is not null && Directory.Exists(parent)) Process.Start("explorer.exe", $"\"{parent}\"");
            else throw new DirectoryNotFoundException($"Folder not found: {path}");
        }
    }

    public static void ShowProperties(string path)
    {
        if (!FileOps.PathExists(path)) throw new FileNotFoundException("The item no longer exists.", path);
        NativeMethods.SHObjectProperties(IntPtr.Zero, NativeMethods.SHOP_FILEPATH, path, null);
    }

    /// <summary>Opens Registry Editor at the given key. Regedit reads its last-opened key on start, so we set it first.</summary>
    public static void OpenRegedit(string fullKeyPath)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
            k.SetValue("LastKey", "Computer\\" + fullKeyPath);
        foreach (var p in Process.GetProcessesByName("regedit"))
        {
            try { p.CloseMainWindow(); p.WaitForExit(1500); } catch { }
        }
        Process.Start(new ProcessStartInfo("regedit.exe", "-m") { UseShellExecute = true });
    }

    public static Process? Run(string fileName, string arguments = "", bool elevate = false, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments) { UseShellExecute = true };
        if (elevate) psi.Verb = "runas";
        if (workingDir is not null && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir;
        return Process.Start(psi);
    }

    public static Process? RunCommandLine(string commandLine, bool elevate = false)
    {
        var (file, args) = CommandLine.Split(commandLine);
        if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("Empty command.");
        return Run(file, args, elevate, Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : null);
    }
}
