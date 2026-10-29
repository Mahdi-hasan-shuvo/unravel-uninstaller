using Microsoft.Win32;
using Unravel.Core.Programs;
using Unravel.Core.Util;

namespace Unravel.Core.Leftovers;

/// <summary>
/// Everything known about a program before its uninstaller runs. The snapshot is taken first because the
/// uninstaller removes the very data (install folder, uninstall key) that identifies the leftovers.
/// </summary>
public sealed class ScanTarget
{
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? InstallFolder { get; init; }
    public RegPath? UninstallKey { get; init; }
    public string? KeyName { get; init; }
    public string? MsiProductCode { get; init; }
    public List<string> Executables { get; init; } = [];
    public string? PackageFamilyName { get; init; }
    public string? PackageFullName { get; init; }
    /// <summary>Items an install log recorded as added by this program (from the install tracker).</summary>
    public List<Tracking.TrackedChange> TrackedChanges { get; init; } = [];
    public string? TrackedLogName { get; init; }

    public static ScanTarget FromProgram(InstalledProgram p, Tracking.InstallLog? installLog = null)
    {
        var folder = p.ResolvedInstallFolder;
        var exes = new List<string>();
        if (folder is not null && Directory.Exists(folder) && !IsBroadFolder(folder))
        {
            try
            {
                exes.AddRange(Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).Take(400));
            }
            catch { }
        }
        var icon = CommandLine.ParseIconPath(p.DisplayIcon).Path;
        if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(icon) && !exes.Contains(icon, StringComparer.OrdinalIgnoreCase)) exes.Add(icon);
        return new ScanTarget
        {
            DisplayName = p.DisplayName,
            Publisher = p.Publisher,
            InstallFolder = folder,
            UninstallKey = p.RegistryKey,
            KeyName = p.KeyName,
            MsiProductCode = p.MsiProductCode,
            Executables = exes,
            TrackedChanges = installLog?.Changes ?? [],
            TrackedLogName = installLog is null ? null : $"install log of {installLog.Started:d}",
        };
    }

    internal static bool IsBroadFolder(string folder)
    {
        var f = folder.TrimEnd('\\');
        if (f.Length <= 3) return true;
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.System })
        {
            var p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p) && FileOps.IsSameOrUnder(p, f)) return true;
        }
        return false;
    }
}

/// <summary>Where the scanner looks. Production uses the real system locations; tests point it at sandboxes.</summary>
public sealed class ScanEnvironment
{
    public List<RegPath> SoftwareRoots { get; init; } = [];
    public List<RegPath> ClassesRoots { get; init; } = [];
    public List<RegPath> RunKeys { get; init; } = [];
    public List<RegPath> AppPathsRoots { get; init; } = [];
    public List<RegPath> SharedDllKeys { get; init; } = [];
    public RegPath? ServicesRoot { get; init; }
    public RegPath? FirewallRulesKey { get; init; }
    public List<RegPath> MuiCacheKeys { get; init; } = [];
    public List<RegPath> MsiProductKeys { get; init; } = [];
    public List<RegPath> MsiFeatureKeys { get; init; } = [];
    public List<RegPath> MsiUpgradeCodeKeys { get; init; } = [];
    public List<RegPath> MsiComponentKeys { get; init; } = [];
    public List<RegPath> ShellExtensionApprovedKeys { get; init; } = [];
    public List<RegPath> AppModelRoots { get; init; } = [];
    /// <summary>Explorer\StartupApproved\* keys: Task Manager's enable/disable flags, keyed by Run value or shortcut name.</summary>
    public List<RegPath> StartupApprovedKeys { get; init; } = [];

    public List<string> ProgramFilesRoots { get; init; } = [];
    public List<string> UserDataRoots { get; init; } = [];
    public List<string> StartMenuRoots { get; init; } = [];
    public List<string> DesktopRoots { get; init; } = [];
    public List<string> TempRoots { get; init; } = [];
    public string? PackagesDataRoot { get; init; }

    /// <summary>Install folders of every other installed program; used to avoid offering shared folders.</summary>
    public List<(string Name, string Folder)> OtherPrograms { get; set; } = [];

    /// <summary>Folders that must never be offered for deletion even if a name matches.</summary>
    public List<string> ProtectedFolders { get; init; } = [];

    public static ScanEnvironment CreateDefault()
    {
        RegPath HKLM(string p, RegistryView v = RegistryView.Registry64) => new(RegistryHive.LocalMachine, p, v);
        RegPath HKCU(string p) => new(RegistryHive.CurrentUser, p);
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var views = Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry64 };

        var env = new ScanEnvironment
        {
            SoftwareRoots = [HKCU("Software"), .. views.Select(v => HKLM("SOFTWARE", v))],
            ClassesRoots = [.. views.Select(v => HKLM(@"SOFTWARE\Classes", v)), HKCU(@"Software\Classes")],
            RunKeys =
            [
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\Run"), HKCU(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                .. views.SelectMany(v => new[] { HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", v), HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", v) })
            ],
            AppPathsRoots = [HKCU(@"Software\Microsoft\Windows\CurrentVersion\App Paths"), .. views.Select(v => HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", v))],
            SharedDllKeys = [.. views.Select(v => HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", v))],
            ServicesRoot = HKLM(@"SYSTEM\CurrentControlSet\Services"),
            FirewallRulesKey = HKLM(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"),
            MuiCacheKeys = [HKCU(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache")],
            MsiProductKeys =
            [
                HKLM(@"SOFTWARE\Classes\Installer\Products"),
                HKCU(@"Software\Microsoft\Installer\Products"),
                HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products"),
            ],
            MsiFeatureKeys = [HKLM(@"SOFTWARE\Classes\Installer\Features"), HKCU(@"Software\Microsoft\Installer\Features")],
            MsiUpgradeCodeKeys = [HKLM(@"SOFTWARE\Classes\Installer\UpgradeCodes"), HKCU(@"Software\Microsoft\Installer\UpgradeCodes")],
            MsiComponentKeys = [HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components")],
            ShellExtensionApprovedKeys = [.. views.Select(v => HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", v))],
            AppModelRoots =
            [
                HKCU(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"),
                HKCU(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage"),
                HKCU(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData"),
                HKCU(@"Software\Classes\ActivatableClasses\Package"),
            ],
            StartupApprovedKeys =
            [
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"),
                HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
                HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
                HKLM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"),
            ],
            ProgramFilesRoots = new[] { F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
                                        F(Environment.SpecialFolder.CommonProgramFiles), F(Environment.SpecialFolder.CommonProgramFilesX86) }
                                .Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            UserDataRoots = new[] { F(Environment.SpecialFolder.ApplicationData), local, Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"),
                                    F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.MyDocuments) }
                                .Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StartMenuRoots = [F(Environment.SpecialFolder.Programs), F(Environment.SpecialFolder.CommonPrograms)],
            DesktopRoots = [F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory)],
            TempRoots = [Path.GetTempPath().TrimEnd('\\')],
            PackagesDataRoot = Path.Combine(local, "Packages"),
        };
        env.ProtectedFolders.AddRange(DefaultProtectedFolders());
        return env;
    }

    public static IEnumerable<string> DefaultProtectedFolders()
    {
        foreach (Environment.SpecialFolder sf in Enum.GetValues<Environment.SpecialFolder>())
        {
            string p;
            try { p = Environment.GetFolderPath(sf); } catch { continue; }
            if (!string.IsNullOrEmpty(p)) yield return p;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(Path.GetDirectoryName(local)!, "LocalLow");
        yield return Path.Combine(local, "Packages");
        yield return Path.Combine(local, "Programs");
        yield return Path.Combine(local, "Microsoft");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft");
        yield return Path.GetTempPath().TrimEnd('\\');
        foreach (var d in DriveInfo.GetDrives()) yield return d.Name.TrimEnd('\\');
    }
}

/// <summary>Converts between product GUIDs and the "packed" form Windows Installer uses as registry key names.</summary>
public static class MsiGuid
{
    public static string Pack(string guid)
    {
        var g = Guid.Parse(guid).ToString("N").ToUpperInvariant(); // 32 hex chars
        static string Rev(string s) => new(s.Reverse().ToArray());
        var sb = new System.Text.StringBuilder();
        sb.Append(Rev(g[..8])).Append(Rev(g.Substring(8, 4))).Append(Rev(g.Substring(12, 4)));
        for (int i = 16; i < 32; i += 2) sb.Append(g[i + 1]).Append(g[i]);
        return sb.ToString();
    }

    public static string Unpack(string packed)
    {
        if (packed.Length != 32) throw new FormatException("Packed GUID must have 32 characters.");
        static string Rev(string s) => new(s.Reverse().ToArray());
        var sb = new System.Text.StringBuilder();
        sb.Append(Rev(packed[..8])).Append(Rev(packed.Substring(8, 4))).Append(Rev(packed.Substring(12, 4)));
        for (int i = 16; i < 32; i += 2) sb.Append(packed[i + 1]).Append(packed[i]);
        return "{" + Guid.ParseExact(sb.ToString(), "N").ToString("D").ToUpperInvariant() + "}";
    }
}
