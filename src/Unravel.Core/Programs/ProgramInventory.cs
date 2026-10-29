using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Unravel.Core.Util;

namespace Unravel.Core.Programs;

public sealed record UninstallSource(RegPath Root, InstallScope Scope, bool Is64Bit);

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
        return list;
    }

    public List<InstalledProgram> Load(InventoryOptions options)
    {
        var result = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources)
        {
            using var root = source.Root.Open();
            if (root is null) continue;
            foreach (var name in SafeSubKeyNames(root))
            {
                using var key = TryOpen(root, name);
                if (key is null) continue;
                var program = Read(key, source.Root.Child(name), source, options);
                if (program is null) continue;
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

    private static IEnumerable<string> SafeSubKeyNames(RegistryKey k)
    {
        try { return k.GetSubKeyNames(); } catch { return []; }
    }

    private static RegistryKey? TryOpen(RegistryKey root, string name)
    {
        try { return root.OpenSubKey(name); } catch { return null; }
    }

    internal static InstalledProgram? Read(RegistryKey key, RegPath path, UninstallSource source, InventoryOptions options)
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
