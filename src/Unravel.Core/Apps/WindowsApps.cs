using System.Diagnostics;
using Unravel.Core.Leftovers;
using Unravel.Core.Util;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Unravel.Core.Apps;

public sealed class WindowsApp
{
    public required string Name { get; init; }
    public required string PackageFullName { get; init; }
    public required string PackageFamilyName { get; init; }
    public required string PackageName { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string Version { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string? InstallLocation { get; init; }
    public string? LogoPath { get; init; }
    public DateTime? InstallDate { get; init; }
    public long? SizeBytes { get; set; }
    public bool IsFramework { get; init; }
    public bool IsSystem { get; init; }
    public bool IsNonRemovable { get; init; }
    public string SignatureKind { get; init; } = "";
    public List<string> AppUserModelIds { get; init; } = [];
    public bool CanLaunch => AppUserModelIds.Count > 0;
    public string StoreUrl => $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(PackageFamilyName)}";
    public string RegistryKeyPath =>
        @"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\" + PackageFullName;

    public ScanTarget ToScanTarget() => new()
    {
        DisplayName = Name,
        Publisher = Publisher,
        PackageFamilyName = PackageFamilyName,
        PackageFullName = PackageFullName,
        // The install folder lives under WindowsApps, which Windows manages itself; never offer it.
        InstallFolder = null,
    };
}

public sealed class WindowsAppOptions
{
    public bool IncludeFrameworks { get; init; }
    public bool IncludeSystemApps { get; init; }
}

/// <summary>Lists and removes packaged (MSIX/AppX) apps for the current user through the Windows PackageManager API.</summary>
public static class WindowsApps
{
    public static List<WindowsApp> List(WindowsAppOptions options)
    {
        var pm = new PackageManager();
        var result = new List<WindowsApp>();
        var nonRemovable = NonRemovablePackages();
        foreach (var pkg in pm.FindPackagesForUser(""))
        {
            try
            {
                if (pkg.IsResourcePackage) continue;
                if (pkg.IsFramework && !options.IncludeFrameworks) continue;
                if (pkg.Status.NotAvailable) continue;
                // Stub packages (e.g. Clock, To Do on some editions) are real, removable apps and are listed.
                var app = FromPackage(pkg, nonRemovable);
                // Parts of Windows the user can start (Settings, Windows Security) are listed; invisible plumbing is not.
                if (app.IsSystem && !app.CanLaunch && !options.IncludeSystemApps) continue;
                result.Add(app);
            }
            catch { /* packages in a broken state can throw on property access – skip them */ }
        }
        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static HashSet<string>? _nonRemovable;

    /// <summary>
    /// Packages Windows refuses to remove. WinRT's Package class doesn't expose this; Get-AppxPackage does.
    /// When PowerShell can't answer, the set is empty and Windows' own refusal at removal time remains the safeguard.
    /// </summary>
    private static HashSet<string> NonRemovablePackages()
    {
        if (_nonRemovable is { } known) return known;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(exe, "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Where-Object NonRemovable | ForEach-Object PackageFullName\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return set;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20_000)) { try { p.Kill(); } catch { } return set; }
            foreach (var line in read.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) set.Add(line.Trim());
            if (p.ExitCode == 0 && set.Count > 0) _nonRemovable = set;
        }
        catch (Exception e) { Logging.ActivityLog.Warning("Windows apps", "Could not read which packages are non-removable: " + e.Message); }
        return set;
    }

    /// <summary>Link schemes (protocols) the package declares in its manifest.</summary>
    public static List<string> ReadProtocols(string? installLocation)
    {
        if (installLocation is null) return [];
        try
        {
            var path = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(path)) return [];
            return System.Xml.Linq.XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "Protocol")
                .Select(e => e.Attribute("Name")?.Value).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return []; }
    }

    private static WindowsApp FromPackage(Package pkg, HashSet<string> nonRemovablePackages)
    {
        string? location = null;
        try { location = pkg.InstalledLocation?.Path; } catch { }
        string? logo = null;
        try { logo = pkg.Logo?.LocalPath; } catch { }
        var aumids = new List<string>();
        string? entryName = null;
        try
        {
            foreach (var entry in pkg.GetAppListEntries())
            {
                aumids.Add(entry.AppUserModelId);
                entryName ??= entry.DisplayInfo?.DisplayName;
            }
        }
        catch { }
        // Users know apps by their Start menu name ("Calculator"), not the package name ("Windows Calculator").
        var name = FirstUsable(entryName, ManifestAppName(location, pkg.Id.FullName, pkg.Id.Name), SafeString(() => pkg.DisplayName)) ?? pkg.Id.Name;
        var v = pkg.Id.Version;
        bool nonRemovable = nonRemovablePackages.Contains(pkg.Id.FullName);
        try { nonRemovable |= pkg.SignatureKind == PackageSignatureKind.System; } catch { }
        return new WindowsApp
        {
            Name = name,
            PackageFullName = pkg.Id.FullName,
            PackageFamilyName = pkg.Id.FamilyName,
            PackageName = pkg.Id.Name,
            Publisher = SafeString(() => pkg.PublisherDisplayName),
            Description = SafeString(() => pkg.Description),
            Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
            Architecture = pkg.Id.Architecture.ToString(),
            InstallLocation = location,
            LogoPath = logo,
            InstallDate = SafeDate(() => pkg.InstalledDate.LocalDateTime),
            IsFramework = pkg.IsFramework,
            IsSystem = pkg.SignatureKind == PackageSignatureKind.System,
            IsNonRemovable = nonRemovable,
            SignatureKind = pkg.SignatureKind.ToString(),
            AppUserModelIds = aumids,
        };
    }

    private static string? FirstUsable(params string?[] names) =>
        names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))?.Trim();

    /// <summary>
    /// Display name of the first application declared in AppxManifest.xml. This also covers apps hidden from the
    /// Start menu (AppListEntry="none"), which GetAppListEntries does not return.
    /// </summary>
    internal static string? ManifestAppName(string? installLocation, string fullName, string packageName)
    {
        if (installLocation is null) return null;
        try
        {
            var path = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(path)) return null;
            var doc = System.Xml.Linq.XDocument.Load(path);
            var visual = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            var raw = visual?.Attribute("DisplayName")?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return raw;
            var res = raw.Substring("ms-resource:".Length);
            var uri = res.StartsWith("//") ? "ms-resource:" + res
                : res.Contains('/') ? $"ms-resource://{packageName}/{res.TrimStart('/')}"
                : $"ms-resource://{packageName}/Resources/{res}";
            return Native.NativeMethods.LoadIndirectString($"@{{{fullName}?{uri}}}");
        }
        catch { return null; }
    }

    private static string? SafeString(Func<string> f)
    {
        try
        {
            var s = f();
            return string.IsNullOrWhiteSpace(s) || s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : s;
        }
        catch { return null; }
    }

    private static DateTime? SafeDate(Func<DateTime> f)
    {
        try { var d = f(); return d.Year > 1601 ? d : null; } catch { return null; }
    }

    public static async Task ComputeSizesAsync(IEnumerable<WindowsApp> apps, Action<WindowsApp>? onUpdated, CancellationToken ct)
    {
        foreach (var a in apps.ToList())
        {
            if (ct.IsCancellationRequested) return;
            if (a.InstallLocation is null) continue;
            var size = await Task.Run(() => FileOps.GetDirectorySize(a.InstallLocation, ct), ct).ConfigureAwait(false);
            a.SizeBytes = size;
            onUpdated?.Invoke(a);
        }
    }

    /// <summary>Removes the package for the current user.</summary>
    public static async Task UninstallAsync(WindowsApp app, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (app.IsNonRemovable) throw new InvalidOperationException($"\"{app.Name}\" is part of Windows and can't be removed.");
        var pm = new PackageManager();
        var op = pm.RemovePackageAsync(app.PackageFullName);
        op.Progress = (_, p) => progress?.Report(p.percentage);
        using (ct.Register(() => op.Cancel()))
        {
            var r = await op.AsTask().ConfigureAwait(false);
            if (r.ExtendedErrorCode is not null && r.ExtendedErrorCode.HResult != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.ErrorText) ? r.ExtendedErrorCode.Message : r.ErrorText);
        }
    }

    public static bool IsInstalled(WindowsApp app)
    {
        try { return new PackageManager().FindPackageForUser("", app.PackageFullName) is not null; }
        catch { return false; }
    }

    public static void Launch(WindowsApp app)
    {
        if (!app.CanLaunch) throw new InvalidOperationException("This app has no entry point that can be started.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.AppUserModelIds[0]}") { UseShellExecute = true });
    }
}
