using Microsoft.Win32;
using Unravel.Core.Native;
using Unravel.Core.Util;

namespace Unravel.Core.Cleaners;

/// <summary>Clears usage history Windows keeps (recent documents, Run box, dialogs, Explorer search…) and removes temporary files.</summary>
public static class WindowsCleaner
{
    public sealed class Locations
    {
        /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion (redirected in tests).</summary>
        public RegPath CurrentVersion { get; init; } = new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion");
        public string RecentFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        public string UserTemp { get; init; } = Path.GetTempPath();
        public string WindowsTemp { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public string WindowsDir { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        public IReadOnlyList<string> FixedDrives { get; init; } =
            DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).ToList();
        public bool IncludeSystemActions { get; init; } = true;
    }

    public const string RegistryGroup = "Traces in the Windows Registry";
    public const string DiskGroup = "Traces on the local disk";

    public static List<CleanerItem> BuildItems(Locations? loc = null)
    {
        loc ??= new Locations();
        var cv = loc.CurrentVersion;
        var explorer = cv.Child("Explorer");
        var applets = cv.Child("Applets");
        var items = new List<CleanerItem>
        {
            Reg("recentdocs", "Recent documents history", () =>
            [
                new RegistryTarget(explorer.Child("RecentDocs"), RegistryCleanMode.ValuesAndSubkeys),
                new FilesTarget(loc.RecentFolder, "*.lnk"),
            ], "Also removes the shortcuts in your Recent Items folder."),
            Reg("runmru", "Run dialog history", () => [new RegistryTarget(explorer.Child("RunMRU"), RegistryCleanMode.Values)],
                "Takes full effect after Explorer restarts."),
            Reg("findfiles", "File search history", () =>
            [
                new RegistryTarget(explorer.Child("WordWheelQuery"), RegistryCleanMode.ValuesAndSubkeys),
                new RegistryTarget(explorer.Child("Doc Find Spec MRU"), RegistryCleanMode.Values),
            ]),
            Reg("findcomputers", "Printers, computers and people search history", () =>
            [
                new RegistryTarget(explorer.Child("FindComputerMRU"), RegistryCleanMode.Values),
                new RegistryTarget(explorer.Child("PrnPortsMRU"), RegistryCleanMode.Values),
                new RegistryTarget(explorer.Child("Map Network Drive MRU"), RegistryCleanMode.Values),
            ]),
            Reg("clipboard", "Clipboard contents", () => [new ActionTarget("Clipboard", PreviewClipboard, EmptyClipboard)]),
            Reg("paint", "Paint recent files", () => [new RegistryTarget(applets.Child(@"Paint\Recent File List"), RegistryCleanMode.Values)]),
            Reg("wordpad", "WordPad recent files", () => [new RegistryTarget(applets.Child(@"Wordpad\Recent File List"), RegistryCleanMode.Values)]),
            Reg("regedit", "Registry Editor last opened key", () => [new RegistryTarget(applets.Child("Regedit"), RegistryCleanMode.SingleValue, "LastKey")]),
            Reg("opensave", "Open/Save dialog recent files", () =>
            [
                new RegistryTarget(explorer.Child(@"ComDlg32\OpenSavePidlMRU"), RegistryCleanMode.ValuesAndSubkeys),
                new RegistryTarget(explorer.Child(@"ComDlg32\OpenSaveMRU"), RegistryCleanMode.ValuesAndSubkeys),
            ]),
            Reg("lastvisited", "Open/Save dialog last visited folders", () =>
            [
                new RegistryTarget(explorer.Child(@"ComDlg32\LastVisitedPidlMRU"), RegistryCleanMode.Values),
                new RegistryTarget(explorer.Child(@"ComDlg32\LastVisitedPidlMRULegacy"), RegistryCleanMode.Values),
                new RegistryTarget(explorer.Child(@"ComDlg32\LastVisitedMRU"), RegistryCleanMode.Values),
                new RegistryTarget(explorer.Child(@"ComDlg32\CIDSizeMRU"), RegistryCleanMode.Values),
            ]),
            Reg("userassist", "Start menu usage logs", () =>
            [
                .. SubkeysOf(explorer.Child("UserAssist")).Select(k => (CleanupTarget)new RegistryTarget(k.Child("Count"), RegistryCleanMode.Values)),
            ], "Resets the program usage counts Windows keeps for the Start menu."),

            Disk("recyclebin", "Empty the Recycle Bin", () => [new ActionTarget("Recycle Bin", PreviewRecycleBin, EmptyRecycleBin)]),
            Disk("temp", "Windows temporary files", () =>
            [
                new FilesTarget(loc.UserTemp, "*", true, true, TimeSpan.FromHours(24)),
                new FilesTarget(loc.WindowsTemp, "*", true, true, TimeSpan.FromHours(24)),
            ], "Files changed in the last 24 hours and files in use are kept. The Windows Temp folder needs administrator rights."),
            Disk("dumps", "Crash memory dump files", () =>
            [
                new FilesTarget(Path.Combine(loc.LocalAppData, "CrashDumps"), "*.dmp"),
                new FilesTarget(Path.Combine(loc.WindowsDir, "Minidump"), "*.dmp"),
                new FilesTarget(loc.WindowsDir, "MEMORY.DMP"),
            ]),
            Disk("chkdsk", "Check Disk recovered file fragments", () =>
                loc.FixedDrives.SelectMany(d => SafeDirs(d, "FOUND.???")).Select(f => (CleanupTarget)new FilesTarget(f, "*.CHK"))),
            Disk("thumbcache", "Thumbnail cache", () => [new FilesTarget(Path.Combine(loc.LocalAppData, @"Microsoft\Windows\Explorer"), "thumbcache_*.db")],
                "Explorer keeps the cache open; files in use are skipped and rebuilt automatically."),
        };
        if (!loc.IncludeSystemActions) items.RemoveAll(i => i.Id is "win:clipboard" or "win:recyclebin");
        return items;
    }

    private static CleanerItem Reg(string id, string title, Func<IEnumerable<CleanupTarget>> t, string? note = null) =>
        new() { Id = "win:" + id, Group = RegistryGroup, Title = title, Targets = t, Note = note };

    private static CleanerItem Disk(string id, string title, Func<IEnumerable<CleanupTarget>> t, string? note = null) =>
        new() { Id = "win:" + id, Group = DiskGroup, Title = title, Targets = t, Note = note };

    private static IEnumerable<RegPath> SubkeysOf(RegPath p)
    {
        using var k = p.Open();
        return k is null ? [] : k.GetSubKeyNames().Select(p.Child).ToList();
    }

    private static IEnumerable<string> SafeDirs(string root, string pattern)
    {
        try { return Directory.GetDirectories(root, pattern); } catch { return []; }
    }

    private static CleanCount PreviewClipboard() => new() { Items = NativeMethods.CountClipboardFormats() > 0 ? 1 : 0 };

    private static CleanCount EmptyClipboard()
    {
        var c = new CleanCount();
        bool had = NativeMethods.CountClipboardFormats() > 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try { NativeMethods.EmptyClipboard(); } finally { NativeMethods.CloseClipboard(); }
                c.Items = had ? 1 : 0;
                return c;
            }
            Thread.Sleep(50);
        }
        c.Errors.Add("The clipboard is in use by another program.");
        return c;
    }

    private static CleanCount PreviewRecycleBin()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        NativeMethods.SHQueryRecycleBin(null, ref info);
        return new CleanCount { Items = (int)info.i64NumItems, Bytes = info.i64Size };
    }

    private static CleanCount EmptyRecycleBin()
    {
        var before = PreviewRecycleBin();
        if (before.Items == 0) return new CleanCount();
        int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
        var c = new CleanCount { Items = before.Items, Bytes = before.Bytes };
        if (hr != 0 && PreviewRecycleBin().Items > 0) c.Errors.Add($"Emptying the Recycle Bin failed (0x{hr:x8}).");
        return c;
    }
}

