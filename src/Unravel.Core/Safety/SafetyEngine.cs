using Microsoft.Win32;
using Unravel.Core.Util;

namespace Unravel.Core.Safety;

public enum Confidence { High, Medium, Low }

[Flags]
public enum RiskFlags
{
    None = 0,
    SystemCritical = 1,
    SharedWithOtherProgram = 2,
    InUse = 4,
    MicrosoftSigned = 8,
    NotCreatedByProgram = 16,
    ContainsExecutables = 32,
    AffectsWindowsService = 64,
    RecentlyUsed = 128,
}

/// <summary>Why something was found. The category drives the base confidence.</summary>
public enum FindingCategory
{
    Other,
    UninstallRegistration, ProgramKey, PublisherKey, WindowsInstallerRegistration, WindowsInstallerComponent,
    StartupEntry, StartupFlag, AppPath, Service, FileAssociation, SharedDll, FirewallRule, NameCache,
    DeepNameMatch, ComRegistration, ShellExtension, AppPackageData,
    InstallFolder, Shortcut, StartMenuFolder, DataFolder, PublisherFolder, DeepFolderMatch,
    TrackedInstallChange, TrackedInWindowsArea, DuplicateFile, OrphanCandidate,
}

public sealed record SafetyAssessment(Confidence Confidence, RiskFlags Risks, string Explanation)
{
    /// <summary>System-critical items can never be deleted.</summary>
    public bool IsBlocked => Risks.HasFlag(RiskFlags.SystemCritical);

    /// <summary>Only these are ever pre-selected or selected by "Select all".</summary>
    public bool IsSafeToAutoSelect =>
        !IsBlocked && Confidence != Confidence.Low &&
        (Risks & (RiskFlags.SharedWithOtherProgram | RiskFlags.InUse | RiskFlags.MicrosoftSigned | RiskFlags.NotCreatedByProgram)) == 0;

    public string ConfidenceText => IsBlocked ? "Blocked" : Confidence.ToString();

    public static readonly SafetyAssessment Unknown = new(Confidence.Low, RiskFlags.None, "Not assessed.");
}

/// <summary>
/// Rates deletion candidates (High / Medium / Low confidence), flags risks, and blocks system-critical items.
/// One engine is shared by the uninstaller, the install tracker cleanup and the duplicate/orphan finder.
/// </summary>
public static class SafetyEngine
{
    private static readonly Dictionary<FindingCategory, Confidence> BaseConfidence = new()
    {
        [FindingCategory.UninstallRegistration] = Confidence.High,
        [FindingCategory.ProgramKey] = Confidence.High,
        [FindingCategory.WindowsInstallerRegistration] = Confidence.High,
        [FindingCategory.StartupEntry] = Confidence.High,
        [FindingCategory.StartupFlag] = Confidence.High,
        [FindingCategory.AppPath] = Confidence.High,
        [FindingCategory.NameCache] = Confidence.High,
        [FindingCategory.FirewallRule] = Confidence.High,
        [FindingCategory.AppPackageData] = Confidence.High,
        [FindingCategory.InstallFolder] = Confidence.High,
        [FindingCategory.Shortcut] = Confidence.High,
        [FindingCategory.StartMenuFolder] = Confidence.High,
        [FindingCategory.TrackedInstallChange] = Confidence.High,
        [FindingCategory.DuplicateFile] = Confidence.High,
        [FindingCategory.PublisherKey] = Confidence.Medium,
        [FindingCategory.PublisherFolder] = Confidence.Medium,
        [FindingCategory.DataFolder] = Confidence.Medium,
        [FindingCategory.Service] = Confidence.Medium,
        [FindingCategory.FileAssociation] = Confidence.Medium,
        [FindingCategory.SharedDll] = Confidence.Medium,
        [FindingCategory.WindowsInstallerComponent] = Confidence.Medium,
        [FindingCategory.ComRegistration] = Confidence.Medium,
        [FindingCategory.ShellExtension] = Confidence.Medium,
        [FindingCategory.DeepNameMatch] = Confidence.Low,
        [FindingCategory.DeepFolderMatch] = Confidence.Low,
        [FindingCategory.OrphanCandidate] = Confidence.Low,
        [FindingCategory.TrackedInWindowsArea] = Confidence.Low,
        [FindingCategory.Other] = Confidence.Low,
    };

    public static Confidence BaseFor(FindingCategory c) => BaseConfidence.GetValueOrDefault(c, Confidence.Low);

    private static Confidence Lower(Confidence c) => c == Confidence.High ? Confidence.Medium : Confidence.Low;

    // ------------------------------------------------------------------ critical locations

    private static readonly string[] CriticalRegistryPrefixes =
    [
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control",
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum",
        @"HKEY_LOCAL_MACHINE\SYSTEM\Setup",
        @"HKEY_LOCAL_MACHINE\SYSTEM\MountedDevices",
        @"HKEY_LOCAL_MACHINE\SAM",
        @"HKEY_LOCAL_MACHINE\SECURITY",
        @"HKEY_LOCAL_MACHINE\BCD00000000",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies",
        @"HKEY_CURRENT_USER\Software\Policies",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender",
    ];

    /// <summary>Windows services that must never be removed, even if a name matches.</summary>
    private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "Winmgmt", "EventLog", "PlugPlay", "Power", "BFE", "MpsSvc", "WinDefend",
        "wuauserv", "TrustedInstaller", "msiserver", "CryptSvc", "Dhcp", "Dnscache", "nsi", "Tcpip", "AFD", "ProfSvc", "Schedule",
        "SamSs", "LanmanWorkstation", "LanmanServer", "AudioSrv", "Audiosrv", "Themes", "UserManager", "CoreMessagingRegistrar",
        "BrokerInfrastructure", "SystemEventsBroker", "TimeBrokerSvc", "StateRepository", "AppXSvc", "ClipSVC", "WdFilter", "WdNisSvc",
        "Disk", "volmgr", "volsnap", "partmgr", "NTFS", "fvevol", "iorate", "storahci", "stornvme", "ACPI", "pci", "CNG", "KSecDD",
    };

    private static string[]? _criticalFolders;
    private static string[] CriticalFolders => _criticalFolders ??= new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files", "microsoft shared"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Common Files", "microsoft shared"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Recovery"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "System Volume Information"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "$Recycle.Bin"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Boot"),
    }.Where(p => !string.IsNullOrEmpty(p)).ToArray();

    private static readonly HashSet<string> CriticalRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "bootsect.bak", "DumpStack.log.tmp",
    };

    public static bool IsCriticalRegistry(RegPath key)
    {
        var p = key.FullPath;
        if (key.Depth <= 1) return true;
        if (CriticalRegistryPrefixes.Any(c => p.Equals(c, StringComparison.OrdinalIgnoreCase) || p.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)))
            return true;
        const string services = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\";
        if (p.StartsWith(services, StringComparison.OrdinalIgnoreCase))
        {
            var name = p[services.Length..].Split('\\')[0];
            if (CriticalServices.Contains(name)) return true;
            if (IsDriverOrSystemService(name)) return true;
        }
        return false;
    }

    /// <summary>Kernel drivers and boot/system-start services are never offered for removal.</summary>
    private static bool IsDriverOrSystemService(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (k is null) return false;
            var type = RegistryUtil.GetInt(k, "Type") ?? 0;
            var start = RegistryUtil.GetInt(k, "Start") ?? 3;
            return (type & 0x3) != 0 || start <= 1; // kernel/file-system driver, or boot/system start
        }
        catch { return false; }
    }

    public static bool IsCriticalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = FileOps.NormalizeDir(path);
        if (p.Length <= 3) return true; // a drive root
        if (CriticalFolders.Any(c => FileOps.IsSameOrUnder(p, c))) return true;
        var dir = Path.GetDirectoryName(p);
        if (dir is not null && dir.Length <= 3 && CriticalRootFiles.Contains(Path.GetFileName(p))) return true;
        // A folder that contains a critical folder (e.g. C:\Program Files when WindowsApps is inside).
        if (CriticalFolders.Any(c => FileOps.IsSameOrUnder(c, p))) return true;
        return p.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) && FileSignature.Check(p).IsMicrosoft;
    }

    // ------------------------------------------------------------------ assessment

    public sealed class Context
    {
        /// <summary>Image paths of running processes, for in-use detection.</summary>
        public IReadOnlyCollection<string> RunningImages { get; init; } = [];
        /// <summary>True when the program being removed is itself published by Microsoft (its files are expected to be MS-signed).</summary>
        public bool TargetIsMicrosoft { get; init; }
        /// <summary>Check signatures of executables inside folders (slower).</summary>
        public bool CheckSignatures { get; init; } = true;

        public static Context Capture(bool targetIsMicrosoft = false) => new()
        {
            RunningImages = ProcessUtil.GetProcesses().Select(p => p.ImagePath).Where(p => p is not null).Select(p => p!).ToList(),
            TargetIsMicrosoft = targetIsMicrosoft,
        };
    }

    public static SafetyAssessment AssessRegistry(RegPath key, string? valueName, FindingCategory category, bool created, string? note = null)
    {
        if (IsCriticalRegistry(key))
            return new(Confidence.Low, RiskFlags.SystemCritical, "System-critical registry location – protected.");
        var c = BaseFor(category);
        var risks = RiskFlags.None;
        var why = new List<string> { Describe(category) };
        if (!created) { risks |= RiskFlags.NotCreatedByProgram; c = Confidence.Low; why.Add("not identified as created by the program"); }
        if (category == FindingCategory.Service) { risks |= RiskFlags.AffectsWindowsService; why.Add("removing a service registration needs a restart to take full effect"); }
        if (note is not null && note.Contains("another installed program", StringComparison.OrdinalIgnoreCase))
        { risks |= RiskFlags.SharedWithOtherProgram; c = Confidence.Low; }
        return new(c, risks, string.Join("; ", why) + ".");
    }

    public static SafetyAssessment AssessPath(string path, FindingCategory category, bool created, Context ctx, string? note = null)
    {
        if (IsCriticalPath(path))
            return new(Confidence.Low, RiskFlags.SystemCritical, "System-critical location – protected.");
        var c = BaseFor(category);
        var risks = RiskFlags.None;
        var why = new List<string> { Describe(category) };
        if (!created) { risks |= RiskFlags.NotCreatedByProgram; c = Confidence.Low; why.Add("not identified as created by the program"); }
        if (note is not null && (note.Contains("also used by", StringComparison.OrdinalIgnoreCase) || note.Contains("another installed program", StringComparison.OrdinalIgnoreCase)))
        { risks |= RiskFlags.SharedWithOtherProgram; c = Confidence.Low; why.Add("shared with another installed program"); }

        bool isDir = Directory.Exists(path);
        var running = ctx.RunningImages.Where(r => FileOps.IsSameOrUnder(r, path)).ToList();
        if (running.Count > 0)
        {
            risks |= RiskFlags.InUse;
            c = Lower(c);
            why.Add($"in use by {Path.GetFileName(running[0])}{(running.Count > 1 ? $" and {running.Count - 1} more" : "")}");
        }
        if (isDir && category is FindingCategory.DataFolder or FindingCategory.PublisherFolder or FindingCategory.DeepFolderMatch or FindingCategory.OrphanCandidate)
        {
            var exes = SafeEnumerate(path, "*.exe", 50);
            if (exes.Count > 0) { risks |= RiskFlags.ContainsExecutables; why.Add("contains programs"); }
            if (ctx.CheckSignatures && !ctx.TargetIsMicrosoft && exes.Take(10).Any(e => FileSignature.Check(e).IsMicrosoft))
            { risks |= RiskFlags.MicrosoftSigned; c = Confidence.Low; why.Add("contains Microsoft-signed programs"); }
        }
        else if (!isDir && ctx.CheckSignatures && !ctx.TargetIsMicrosoft && IsExecutable(path) && FileSignature.Check(path).IsMicrosoft)
        { risks |= RiskFlags.MicrosoftSigned; c = Confidence.Low; why.Add("Microsoft-signed program"); }
        return new(c, risks, string.Join("; ", why) + ".");
    }

    private static bool IsExecutable(string p) =>
        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);

    private static List<string> SafeEnumerate(string dir, string pattern, int max)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4, AttributesToSkip = FileAttributes.ReparsePoint })
                .Take(max).ToList();
        }
        catch { return []; }
    }

    public static string Describe(FindingCategory c) => c switch
    {
        FindingCategory.UninstallRegistration => "The program's own uninstall registration",
        FindingCategory.ProgramKey => "Settings key named after the program",
        FindingCategory.PublisherKey => "Publisher key holding only this program's data",
        FindingCategory.WindowsInstallerRegistration => "Windows Installer registration of the product",
        FindingCategory.WindowsInstallerComponent => "Windows Installer component reference",
        FindingCategory.StartupEntry => "Startup entry that launches the program",
        FindingCategory.StartupFlag => "Windows' enable/disable flag for the program's startup entry",
        FindingCategory.AppPath => "App Paths registration pointing into the program folder",
        FindingCategory.Service => "Windows service whose program is in the install folder",
        FindingCategory.FileAssociation => "File association entry for the program",
        FindingCategory.SharedDll => "Shared DLL reference count for a file in the program folder",
        FindingCategory.FirewallRule => "Firewall rule for the program",
        FindingCategory.NameCache => "Program name cache entry",
        FindingCategory.DeepNameMatch => "Name resembles the program (deep search)",
        FindingCategory.ComRegistration => "COM registration pointing into the program folder",
        FindingCategory.ShellExtension => "Approved shell extension registered by the program",
        FindingCategory.AppPackageData => "Windows app package data",
        FindingCategory.InstallFolder => "The program's install folder",
        FindingCategory.Shortcut => "Shortcut pointing to the program",
        FindingCategory.StartMenuFolder => "Start menu folder of the program",
        FindingCategory.DataFolder => "Data folder named after the program",
        FindingCategory.PublisherFolder => "Publisher folder holding only this program's data",
        FindingCategory.DeepFolderMatch => "Folder name resembles the program (deep search)",
        FindingCategory.TrackedInstallChange => "Recorded as created when the program was installed",
        FindingCategory.TrackedInWindowsArea => "Appeared in Windows' own settings while the install was recorded – Windows may have made it",
        FindingCategory.DuplicateFile => "Identical copy of another file (same SHA-256)",
        FindingCategory.OrphanCandidate => "No installed program or Windows component references it",
        _ => "Matched by name",
    };
}
