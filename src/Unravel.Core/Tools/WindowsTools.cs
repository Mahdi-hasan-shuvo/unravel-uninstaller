using System.Diagnostics;

namespace Unravel.Core.Tools;

public sealed record WindowsTool(string Id, string Name, string Description, string FileName, string Arguments, bool KeepConsoleOpen = false, bool NeedsAdmin = false)
{
    /// <summary>Path that must exist for the tool to be offered (null when the shell resolves it, e.g. ms-settings:).</summary>
    public string? RequiredFile => FileName.Contains(':') && !FileName.Contains('\\') ? null : Resolve(FileName);

    private static string Resolve(string file)
    {
        if (Path.IsPathRooted(file)) return file;
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(sys, file);
    }
}

/// <summary>One-click shortcuts to tools that ship with Windows.</summary>
public static class WindowsTools
{
    public static IReadOnlyList<WindowsTool> All { get; } =
    [
        new("restore", "System Restore", "Return Windows to an earlier restore point without touching your personal files.", "rstrui.exe", ""),
        // msdt.exe troubleshooters are being retired by Microsoft; the Settings troubleshooter page replaces them.
        new("netdiag", "Network Troubleshooter", "Diagnose and repair common network connection problems.", "ms-settings:troubleshoot", ""),
        new("netinfo", "Network Information", "Show the full TCP/IP configuration of every network adapter (ipconfig /all).", "cmd.exe", "/k ipconfig /all", true),
        new("security", "Windows Security", "Check firewall, virus protection and update status.", "windowsdefender:", ""),
        new("sysprops", "System Properties", "Computer name, hardware, performance and remote settings.", "SystemPropertiesAdvanced.exe", ""),
        new("msinfo", "System Information", "Detailed report of hardware, components and software environment.", "msinfo32.exe", ""),
        new("netstat", "TCP/IP Netstat", "List active connections and listening ports with the programs that own them (netstat -b).", "cmd.exe", "/k netstat -abno", true, true),
        new("osk", "On-Screen Keyboard", "Type with the mouse – also useful to defeat hardware or software key loggers.", "osk.exe", ""),
        new("defrag", "Defragment and Optimize Drives", "Analyze and optimize drives (TRIM for SSDs, defragmentation for hard disks).", "dfrgui.exe", ""),
        new("services", "Services", "Start, stop and configure background services.", "services.msc", ""),
        new("shares", "Shared Folders", "Manage shared folders, sessions and open files.", "fsmgmt.msc", ""),
        new("gpedit", "Local Group Policy Editor", "Edit computer and user policies (Windows Pro, Enterprise and Education only).", "gpedit.msc", ""),
        new("features", "Turn Windows Features On or Off", "Add or remove optional Windows components.", "OptionalFeatures.exe", ""),
        new("mrt", "Malicious Software Removal Tool", "Microsoft's scanner for prevalent malware families.", "mrt.exe", "", NeedsAdmin: true),
        new("backup", "Backup and Restore", "Back up files and create a system image (Windows 7 tool).", "sdclt.exe", ""),
        new("tasks", "Task Scheduler", "View and manage scheduled tasks.", "taskschd.msc", ""),
        new("chkdsk", "Check Disk", "Check the system drive's file system for errors (read-only scan).", "cmd.exe", "/k chkdsk %SystemDrive%", true, true),
        new("sfc", "System File Checker", "Scan protected Windows files and repair corrupted ones (sfc /scannow).", "cmd.exe", "/k sfc /scannow", true, true),
    ];

    public static bool IsAvailable(WindowsTool t) => t.RequiredFile is null || File.Exists(t.RequiredFile);

    public static void Launch(WindowsTool t)
    {
        if (!IsAvailable(t)) throw new FileNotFoundException($"{t.Name} is not available on this edition of Windows.", t.RequiredFile);
        var psi = new ProcessStartInfo(t.FileName, Environment.ExpandEnvironmentVariables(t.Arguments)) { UseShellExecute = true };
        if (t.NeedsAdmin) psi.Verb = "runas";
        try { Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { /* user cancelled UAC */ }
    }
}
