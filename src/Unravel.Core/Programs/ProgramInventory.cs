using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Unravel.Core.Util;

namespace Unravel.Core.Programs;

/// <param name="IsMsiUserData">The root holds Windows Installer's per-user records ({packed code}\InstallProperties) instead of Uninstall keys.</param>
public sealed record UninstallSource(RegPath Root, InstallScope Scope, bool Is64Bit, bool IsMsiUserData = false);

public sealed class InventoryOptions
{
    public bool IncludeSystemComponents { get; init; }
    public bool IncludeUpdates { get; init; }
    public bool UseInstallDateValue { get; init; }
}

/// <summary>Reads installed programs from the Windows "Uninstall" registry keys (all users and current user, 64- and 32-bit views).</summary>
public sealed class ProgramInventory
{
    public const string UninstallSubPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<UninstallSource> Sources { get; }

    public ProgramInventory(IReadOnlyList<UninstallSource>? sources = null)
    {
        Sources = sources ?? DefaultSources();
    }

    public static IReadOnlyList<UninstallSource> DefaultSources()
    {
        var list = new List<UninstallSource>
        {
            new(new RegPath(RegistryHive.LocalMachine, UninstallSubPath, RegistryView.Registry64), InstallScope.AllUsers, Environment.Is64BitOperatingSystem),
        };
        if (Environment.Is64BitOperatingSystem)
            list.Add(new(new RegPath(RegistryHive.LocalMachine, UninstallSubPath, RegistryView.Registry32), InstallScope.AllUsers, false));
        list.Add(new(new RegPath(RegistryHive.CurrentUser, UninstallSubPath, RegistryView.Registry64), InstallScope.CurrentUser, Environment.Is64BitOperatingSystem));
        // Per-user Windows Installer products have no Uninstall key at all; Windows lists them from this record.
        var currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (currentSid is not null)
            list.Add(new(new RegPath(RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{currentSid}\Products", RegistryView.Registry64),
                InstallScope.CurrentUser, Environment.Is64BitOperatingSystem, IsMsiUserData: true));
        // HKCU is not redirected for 32-bit programs, but some installers write to its WOW6432Node path explicitly.
        list.Add(new(new RegPath(RegistryHive.CurrentUser, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64), InstallScope.CurrentUser, false));
        // Hives of other signed-in users are loaded and readable when elevated (e.g. UAC with a different admin account).
        foreach (var sid in OtherUserSids(LoadedHives(), currentSid))
            list.Add(new(new RegPath(RegistryHive.Users, sid + "\\" + UninstallSubPath, RegistryView.Registry64), InstallScope.OtherUser, Environment.Is64BitOperatingSystem));
        return list;
    }

    private static string[] LoadedHives()
    {
        try { return Registry.Users.GetSubKeyNames(); } catch { return []; }
    }

    internal static IReadOnlyList<string> OtherUserSids(IEnumerable<string> hives, string? currentSid) =>
        hives.Where(h => h.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !h.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) &&
                         !h.Equals(currentSid, StringComparison.OrdinalIgnoreCase)).ToList();

    public List<InstalledProgram> Load(InventoryOptions options)
    {
        var result = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Uninstall keys come first: a Windows Installer record only adds a product that has no key of its own.
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources.OrderBy(s => s.IsMsiUserData))
        {
            using var root = source.Root.Open();
            if (root is null) continue;
            foreach (var name in SafeSubKeyNames(root))
            {
                string? productCode = null;
                if (source.IsMsiUserData && !TryUnpackProductCode(name, out productCode)) continue;
                if (productCode is not null && knownKeys.Contains(productCode)) continue;
                var relative = source.IsMsiUserData ? name + "\\InstallProperties" : name;
                using var key = TryOpen(root, relative);
                if (key is null) continue;
                var program = Read(key, source.Root.Child(relative), source, options, productCode);
                if (program is null) continue;
                knownKeys.Add(program.KeyName);
                if (!Include(program, options)) continue;
                // The same product can be registered in both views (rare) – keep the first occurrence.
                var identity = $"{program.DisplayName}|{program.Version}|{program.Scope}|{program.KeyName}";
                if (!seen.Add(identity)) continue;
                result.Add(program);
            }
        }
        return result.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool Include(InstalledProgram p, InventoryOptions o) => p.Kind switch
    {
        ProgramKind.SystemComponent => o.IncludeSystemComponents,
        ProgramKind.Update => o.IncludeUpdates,
        _ => true
    };

    private static bool TryUnpackProductCode(string packed, out string? code)
    {
        try { code = Leftovers.MsiGuid.Unpack(packed); return true; }
        catch (FormatException) { code = null; return false; }
    }

    private static IEnumerable<string> SafeSubKeyNames(RegistryKey k)
    {
        try { return k.GetSubKeyNames(); } catch { return []; }
    }

    private static RegistryKey? TryOpen(RegistryKey root, string name)
    {
        try { return root.OpenSubKey(name); } catch { return null; }
    }

    internal static InstalledProgram? Read(RegistryKey key, RegPath path, UninstallSource source, InventoryOptions options, string? productCode = null)
    {
        var name = ResolveDisplayName(key, path.Name);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var uninstall = RegistryUtil.GetString(key, "UninstallString");
        var isMsi = RegistryUtil.GetInt(key, "WindowsInstaller") == 1;
        var parent = RegistryUtil.GetString(key, "ParentKeyName");
        var releaseType = RegistryUtil.GetString(key, "ReleaseType");
        var systemComponent = RegistryUtil.GetInt(key, "SystemComponent") == 1;

        ProgramKind kind;
        if (IsUpdate(name, releaseType, parent)) kind = ProgramKind.Update;
        else if (systemComponent || (string.IsNullOrEmpty(uninstall) && !isMsi)) kind = ProgramKind.SystemComponent;
        else kind = ProgramKind.Program;

        long? size = null;
        var est = RegistryUtil.GetInt(key, "EstimatedSize");
        if (est is > 0) size = (long)est.Value * 1024;

        DateTime? date = null;
        if (options.UseInstallDateValue) date = ParseInstallDate(RegistryUtil.GetString(key, "InstallDate"));
        date ??= RegistryUtil.GetLastWriteTime(key);

        return new InstalledProgram
        {
            DisplayName = name,
            RegistryKey = path,
            KeyNameOverride = productCode,
            IsWindowsInstallerRecord = source.IsMsiUserData,
            Version = RegistryUtil.GetString(key, "DisplayVersion"),
            Publisher = RegistryUtil.GetString(key, "Publisher"),
            InstallLocation = RegistryUtil.GetString(key, "InstallLocation") ?? RegistryUtil.GetString(key, "Inno Setup: App Path"),
            UninstallString = uninstall,
            QuietUninstallString = RegistryUtil.GetString(key, "QuietUninstallString"),
            ModifyPath = RegistryUtil.GetString(key, "ModifyPath"),
            DisplayIcon = RegistryUtil.GetString(key, "DisplayIcon"),
            Comments = RegistryUtil.GetString(key, "Comments"),
            HelpLink = RegistryUtil.GetString(key, "HelpLink"),
            AboutLink = RegistryUtil.GetString(key, "URLInfoAbout"),
            UpdateLink = RegistryUtil.GetString(key, "URLUpdateInfo"),
            InstallSource = RegistryUtil.GetString(key, "InstallSource"),
            IsMsi = isMsi,
            NoModify = RegistryUtil.GetInt(key, "NoModify") == 1,
            NoRepair = RegistryUtil.GetInt(key, "NoRepair") == 1,
            NoRemove = RegistryUtil.GetInt(key, "NoRemove") == 1,
            ParentKeyName = parent,
            Kind = kind,
            Scope = source.Scope,
            Is64Bit = source.Is64Bit,
            Installer = DetectInstaller(key, path.Name, uninstall, isMsi),
            InstallDate = date,
            EstimatedSizeBytes = size,
        };
    }

    /// <summary>
    /// Windows prefers DisplayName_Localized, an indirect resource string (@file,-id), over DisplayName,
    /// so Programs and Features shows e.g. "Remote Desktop Connection" for an entry with no DisplayName at all.
    /// </summary>
    internal static string? ResolveDisplayName(RegistryKey key, string keyName)
    {
        foreach (var valueName in new[] { "DisplayName_Localized", "DisplayName" })
        {
            var raw = RegistryUtil.GetString(key, valueName);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!raw.StartsWith('@')) return raw;
            var resolved = Native.NativeMethods.LoadIndirectString(raw);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved.Trim();
        }
        return RegistryUtil.GetString(key, "DisplayName") is { } fallback && fallback.StartsWith('@') ? keyName : null;
    }

    internal static bool IsUpdate(string name, string? releaseType, string? parentKeyName)
    {
        if (releaseType is not null && Regex.IsMatch(releaseType, @"^(Security Update|Update Rollup|Hotfix|Update|Service Pack)$", RegexOptions.IgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(parentKeyName)) return true;
        return Regex.IsMatch(name, @"\(KB\d{6,7}\)|^(Security )?Update for .*\bKB\d{6,7}\b|^Hotfix for ", RegexOptions.IgnoreCase);
    }

    internal static DateTime? ParseInstallDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "d.M.yyyy", "yyyy/MM/dd"];
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (long.TryParse(s, out var unix) && unix > 946684800 && unix < 4102444800) return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        return null;
    }

    internal static string DetectInstaller(RegistryKey key, string keyName, string? uninstall, bool isMsi)
    {
        if (isMsi) return "Windows Installer (MSI)";
        if (keyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || RegistryUtil.GetString(key, "Inno Setup: Setup Version") is not null)
            return "Inno Setup";
        if (RegistryUtil.GetString(key, "BundleCachePath") is not null || RegistryUtil.GetString(key, "BundleProviderKey") is not null)
            return "WiX Burn bundle";
        var u = uninstall ?? "";
        if (u.Contains("InstallShield", StringComparison.OrdinalIgnoreCase) || u.Contains("IsUninst", StringComparison.OrdinalIgnoreCase))
            return "InstallShield";
        if (Regex.IsMatch(u, @"Update\.exe""?\s+--uninstall", RegexOptions.IgnoreCase)) return "Squirrel";
        if (u.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) return "Windows Installer (MSI)";
        if (RegistryUtil.GetString(key, "NSIS:StartMenuDir") is not null || Regex.IsMatch(u, @"\\(uninst|uninstall)[^\\]*\.exe""?\s*$", RegexOptions.IgnoreCase))
            return "Setup program";
        return string.IsNullOrEmpty(u) ? "None" : "Setup program";
    }

    /// <summary>Computes sizes for programs that did not register one, unless fast loading is on.</summary>
    public static async Task ComputeMissingSizesAsync(IEnumerable<InstalledProgram> programs, Action<InstalledProgram>? onUpdated, CancellationToken ct)
    {
        foreach (var p in programs.Where(p => p.EstimatedSizeBytes is null).ToList())
        {
            if (ct.IsCancellationRequested) return;
            var folder = p.ResolvedInstallFolder;
            if (folder is null || !Directory.Exists(folder) || IsTooBroad(folder)) continue;
            var size = await Task.Run(() => FileOps.GetDirectorySize(folder, ct), ct).ConfigureAwait(false);
            if (size > 0) { p.ComputedSizeBytes = size; onUpdated?.Invoke(p); }
        }
    }

    private static bool IsTooBroad(string folder)
    {
        var f = folder.TrimEnd('\\');
        return f.Length <= 3 ||
               f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase) ||
               f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase) ||
               f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) ||
               f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase) ||
               f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase);
    }
}
