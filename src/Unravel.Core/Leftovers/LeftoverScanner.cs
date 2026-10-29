using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Win32;
using Unravel.Core.Safety;
using Unravel.Core.Settings;
using Unravel.Core.Util;

namespace Unravel.Core.Leftovers;

/// <summary>
/// Finds registry entries, files and folders a program left behind. Search depth grows with the mode:
/// Safe looks only where the program's own identity points; Moderate adds common shared locations;
/// Advanced adds deep name searches and COM registration lookups.
/// </summary>
public sealed class LeftoverScanner
{
    private readonly ScanEnvironment _env;
    private readonly IReadOnlyList<ExcludeRule> _registryExcludes;
    private readonly IReadOnlyList<ExcludeRule> _folderExcludes;
    private readonly bool _checkByDefault;

    public LeftoverScanner(ScanEnvironment env, IReadOnlyList<ExcludeRule>? registryExcludes = null, IReadOnlyList<ExcludeRule>? folderExcludes = null, bool checkByDefault = false)
    {
        _env = env;
        _registryExcludes = registryExcludes ?? [];
        _folderExcludes = folderExcludes ?? [];
        _checkByDefault = checkByDefault;
    }

    private sealed class Ctx
    {
        public required ScanTarget Target;
        public required ScanMode Mode;
        public required List<string> Names;
        public required List<string> Publishers;
        public required HashSet<string> OtherNames;
        public required CancellationToken Ct;
        public IProgress<string>? Progress;
        public readonly List<Finding> Findings = [];
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Warnings = [];
        public string? Folder => Target.InstallFolder;
        public HashSet<string> ExeNames => _exeNames ??= Target.Executables.Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        private HashSet<string>? _exeNames;
    }

    public ScanResult Scan(ScanTarget target, ScanMode mode, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new Ctx
        {
            Target = target, Mode = mode, Ct = ct, Progress = progress,
            Names = NameMatcher.NameVariants(target),
            Publishers = NameMatcher.PublisherVariants(target),
            OtherNames = _env.OtherPrograms
                .Where(o => !o.Name.Equals(target.DisplayName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(o => new[] { NameMatcher.Normalize(o.Name), NameMatcher.Normalize(NameMatcher.StripVersion(o.Name)) })
                .Where(n => n.Length >= 3).ToHashSet(),
        };

        if (mode != ScanMode.BuiltInOnly)
        {
            Step(ctx, "Using the recorded install log", ScanTracked);
            Step(ctx, "Scanning registry: program keys", ScanUninstallKey, ScanSoftwareRoots, ScanMsi);
            if (mode >= ScanMode.Moderate)
                Step(ctx, "Scanning registry: startup, app paths, services", ScanRunKeys, ScanAppPaths, ScanServices, ScanApplicationsClasses,
                    ScanSharedDlls, ScanFirewall, ScanMuiCache);
            if (mode >= ScanMode.Advanced)
                Step(ctx, "Scanning registry: deep search and COM registrations", ScanSoftwareDeep, ScanCom, ScanMsiComponents);
            Step(ctx, "Scanning registry: Windows app data", ScanAppModel);
            Step(ctx, "Scanning files and folders", ScanInstallFolder, ScanShortcuts, ScanStartMenuFolders, ScanPackageData);
            if (mode >= ScanMode.Moderate) Step(ctx, "Scanning user and program data folders", ScanDataFolders);
            if (mode >= ScanMode.Advanced) Step(ctx, "Deep folder search", ScanDataFoldersDeep);
        }

        var result = new ScanResult
        {
            RegistryRoot = BuildRegistryTree(ctx.Findings.Where(f => f.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue)),
            Files = BuildFileList(ctx.Findings.Where(f => f.Kind is LeftoverKind.Folder or LeftoverKind.File),
                SafetyEngine.Context.Capture(target.Publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true), ct),
            RawFindings = ctx.Findings,
            Warnings = ctx.Warnings,
        };
        result.Duration = sw.Elapsed;
        return result;
    }

    private static void Step(Ctx ctx, string label, params Action<Ctx>[] actions)
    {
        ctx.Progress?.Report(label);
        foreach (var a in actions)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            try { a(ctx); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { ctx.Warnings.Add($"{a.Method.Name}: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ registry

    private void AddKey(Ctx ctx, RegPath key, string reason, bool created = true)
    {
        if (IsProtectedKey(key)) return;
        if (!ctx.Seen.Add("K:" + key.FullPath)) return;
        if (created && ctx.OtherNames.Contains(NameMatcher.Normalize(key.Name)) && !reason.StartsWith("Uninstall"))
        {
            created = false;
            reason += " (name also matches another installed program – verify manually)";
        }
        ctx.Findings.Add(new Finding(LeftoverKind.RegistryKey, reason, created, Key: key));
    }

    private void AddValue(Ctx ctx, RegPath key, string valueName, string? data, string reason)
    {
        if (!ctx.Seen.Add("V:" + key.FullPath + "\\" + valueName)) return;
        ctx.Findings.Add(new Finding(LeftoverKind.RegistryValue, reason, true, Key: key, ValueName: valueName, ValueData: data));
    }

    private IEnumerable<RegPath> AllEnvironmentRoots()
    {
        IEnumerable<RegPath> roots = _env.SoftwareRoots.Concat(_env.ClassesRoots).Concat(_env.RunKeys).Concat(_env.AppPathsRoots)
            .Concat(_env.SharedDllKeys).Concat(_env.MuiCacheKeys).Concat(_env.MsiProductKeys).Concat(_env.MsiFeatureKeys)
            .Concat(_env.MsiUpgradeCodeKeys).Concat(_env.MsiComponentKeys).Concat(_env.ShellExtensionApprovedKeys).Concat(_env.AppModelRoots)
            .Concat(_env.StartupApprovedKeys);
        if (_env.ServicesRoot is not null) roots = roots.Append(_env.ServicesRoot);
        if (_env.FirewallRulesKey is not null) roots = roots.Append(_env.FirewallRulesKey);
        return roots;
    }

    /// <summary>Hives, the search roots themselves and their ancestors are never offered for deletion.</summary>
    public bool IsProtectedKey(RegPath key)
    {
        if (key.Depth <= 1) return true;
        foreach (var root in AllEnvironmentRoots())
            if (root.SameKeyAs(key) || root.IsUnder(key)) return true;
        var path = key.FullPath;
        string[] critical =
        [
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft", @"HKEY_CURRENT_USER\Software\Microsoft",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes", @"HKEY_CURRENT_USER\Software\Classes", @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies",
            @"HKEY_CURRENT_USER\Software\Policies", @"HKEY_LOCAL_MACHINE\SYSTEM", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Clients", @"HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications",
        ];
        return critical.Any(c => c.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsExcludedKey(string fullPath)
    {
        foreach (var rule in _registryExcludes)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            var pattern = rule.Pattern.Trim().TrimEnd('\\');
            if (Wildcard.IsMatch(fullPath, pattern)) return true;
            if (rule.IncludeSubkeys)
            {
                var p = fullPath;
                int i;
                while ((i = p.LastIndexOf('\\')) > 0)
                {
                    p = p[..i];
                    if (Wildcard.IsMatch(p, pattern)) return true;
                }
            }
        }
        return false;
    }

    public bool IsExcludedFolder(string path)
    {
        foreach (var rule in _folderExcludes)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            var pattern = rule.Pattern.Trim().TrimEnd('\\');
            if (Wildcard.HasWildcards(pattern) ? Wildcard.IsMatch(path, pattern) || Wildcard.IsMatch(path, pattern + "\\*") : FileOps.IsSameOrUnder(path, pattern))
                return true;
        }
        return false;
    }

    private static string[] SubKeys(RegPath p)
    {
        using var k = p.Open();
        if (k is null) return [];
        try { return k.GetSubKeyNames(); } catch { return []; }
    }

    private bool IsInTarget(Ctx ctx, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim().Trim('"');
        if (ctx.Folder is { } f && !ScanTarget.IsBroadFolder(f) && FileOps.IsSameOrUnder(path, f)) return true;
        return ctx.Target.Executables.Any(e => e.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Everything the install tracker saw the program add, if it still exists.</summary>
    private void ScanTracked(Ctx ctx)
    {
        var log = ctx.Target.TrackedLogName is { } n ? $" ({n})" : "";
        foreach (var c in ctx.Target.TrackedChanges.Where(c => c.Type == Tracking.ChangeType.Added))
        {
            // Windows may change its own settings while an install is recorded; those items are never pre-selected.
            var reason = (Tracking.InstallTracker.IsWindowsOwnArea(c) ? "Recorded in Windows' own settings during installation" : "Recorded during installation") + log;
            switch (c.Area)
            {
                case Tracking.ChangeArea.RegistryKey when RegPath.TryParse(c.Location, out var k) && k!.Exists():
                    AddKey(ctx, k, reason);
                    break;
                case Tracking.ChangeArea.RegistryValue when c.KeyPath is not null && RegPath.TryParse(c.KeyPath, out var vk):
                    using (var key = vk!.Open())
                        if (key?.GetValueNames().Contains(c.ValueName ?? "", StringComparer.OrdinalIgnoreCase) == true)
                            AddValue(ctx, vk, c.ValueName ?? "", RegistryUtil.GetString(key, c.ValueName ?? ""), reason);
                    break;
                case Tracking.ChangeArea.Folder when Directory.Exists(c.Location):
                    AddPath(ctx, c.Location, true, reason);
                    break;
                case Tracking.ChangeArea.File when File.Exists(c.Location):
                    AddPath(ctx, c.Location, false, reason);
                    break;
            }
        }
    }

    private void ScanUninstallKey(Ctx ctx)
    {
        if (ctx.Target.UninstallKey is { } k && k.Exists()) AddKey(ctx, k, "Uninstall registration");
    }

    private void ScanSoftwareRoots(Ctx ctx)
    {
        foreach (var root in _env.SoftwareRoots)
        {
            var subs = SubKeys(root);
            foreach (var sub in subs)
            {
                if (NameMatcher.Matches(sub, ctx.Names)) AddKey(ctx, root.Child(sub), "Program settings key");
            }
            foreach (var sub in subs.Where(s => NameMatcher.Matches(s, ctx.Publishers)))
            {
                var pk = root.Child(sub);
                var children = SubKeys(pk);
                var matched = children.Where(c => ctx.Mode >= ScanMode.Advanced ? NameMatcher.LooseMatches(c, ctx.Names) : NameMatcher.Matches(c, ctx.Names)).ToList();
                foreach (var m in matched) AddKey(ctx, pk.Child(m), "Program key under publisher");
                if (matched.Count > 0 && matched.Count == children.Length && ValueCount(pk) == 0)
                    AddKey(ctx, pk, "Publisher key that only contains this program's data");
            }
        }
    }

    private static int ValueCount(RegPath p)
    {
        using var k = p.Open();
        try { return k?.GetValueNames().Count(n => n.Length > 0) ?? 0; } catch { return 0; }
    }

    private void ScanMsi(Ctx ctx)
    {
        if (ctx.Target.MsiProductCode is not { } code) return;
        string packed;
        try { packed = MsiGuid.Pack(code); } catch { return; }
        foreach (var root in _env.MsiProductKeys.Concat(_env.MsiFeatureKeys))
        {
            var k = root.Child(packed);
            if (k.Exists()) AddKey(ctx, k, "Windows Installer registration");
        }
        foreach (var root in _env.MsiUpgradeCodeKeys)
        {
            foreach (var sub in SubKeys(root))
            {
                var uk = root.Child(sub);
                using var k = uk.Open();
                if (k is null) continue;
                var names = k.GetValueNames();
                if (!names.Contains(packed, StringComparer.OrdinalIgnoreCase)) continue;
                if (names.Count(n => n.Length > 0) == 1) AddKey(ctx, uk, "Windows Installer upgrade code");
                else AddValue(ctx, uk, packed, null, "Windows Installer upgrade code reference");
            }
        }
    }

    private void ScanMsiComponents(Ctx ctx)
    {
        if (ctx.Target.MsiProductCode is not { } code) return;
        var packed = MsiGuid.Pack(code);
        foreach (var root in _env.MsiComponentKeys)
        {
            using var rk = root.Open();
            if (rk is null) continue;
            foreach (var sub in rk.GetSubKeyNames())
            {
                ctx.Ct.ThrowIfCancellationRequested();
                using var k = rk.OpenSubKey(sub);
                if (k is null) continue;
                var names = k.GetValueNames();
                if (!names.Contains(packed, StringComparer.OrdinalIgnoreCase)) continue;
                var data = RegistryUtil.GetString(k, packed);
                if (names.Length == 1) AddKey(ctx, root.Child(sub), "Windows Installer component");
                else AddValue(ctx, root.Child(sub), packed, data, "Windows Installer component reference");
            }
        }
    }

    private void ScanRunKeys(Ctx ctx)
    {
        foreach (var rk in _env.RunKeys)
        {
            using var k = rk.Open();
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                var data = RegistryUtil.GetString(k, name);
                if (!IsInTarget(ctx, CommandLine.FindExecutablePath(data))) continue;
                AddValue(ctx, rk, name, data, "Starts with Windows");
                AddStartupApproval(ctx, name);
            }
        }
    }

    /// <summary>The enable/disable flag Windows keeps for a startup item outlives the item itself.</summary>
    private void AddStartupApproval(Ctx ctx, string valueName)
    {
        foreach (var ak in _env.StartupApprovedKeys)
        {
            using var k = ak.Open();
            if (k?.GetValue(valueName) is not null) AddValue(ctx, ak, valueName, null, "Startup enable/disable flag");
        }
    }

    private void ScanAppPaths(Ctx ctx)
    {
        foreach (var root in _env.AppPathsRoots)
        {
            foreach (var sub in SubKeys(root))
            {
                var kp = root.Child(sub);
                using var k = kp.Open();
                if (k is null) continue;
                var path = CommandLine.ParseIconPath(RegistryUtil.GetString(k, "")).Path;
                if (IsInTarget(ctx, path) || (ctx.ExeNames.Contains(sub) && path.Length > 0 && !File.Exists(path)))
                    AddKey(ctx, kp, "Application path registration");
            }
        }
    }

    private void ScanServices(Ctx ctx)
    {
        if (_env.ServicesRoot is null || ctx.Folder is null) return;
        foreach (var sub in SubKeys(_env.ServicesRoot))
        {
            var kp = _env.ServicesRoot.Child(sub);
            using var k = kp.Open();
            if (k is null) continue;
            var image = RegistryUtil.GetString(k, "ImagePath");
            if (IsInTarget(ctx, CommandLine.FindExecutablePath(image))) AddKey(ctx, kp, "Windows service");
        }
    }

    private void ScanApplicationsClasses(Ctx ctx)
    {
        foreach (var root in _env.ClassesRoots)
            foreach (var exe in ctx.ExeNames)
            {
                var kp = root.Child("Applications").Child(exe);
                if (kp.Exists()) AddKey(ctx, kp, "File association application entry");
            }
    }

    private void ScanSharedDlls(Ctx ctx)
    {
        foreach (var kp in _env.SharedDllKeys)
        {
            using var k = kp.Open();
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
                if (IsInTarget(ctx, name)) AddValue(ctx, kp, name, RegistryUtil.GetString(k, name), "Shared DLL reference count");
        }
    }

    private void ScanFirewall(Ctx ctx)
    {
        if (_env.FirewallRulesKey is null || ctx.Folder is null) return;
        using var k = _env.FirewallRulesKey.Open();
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            var data = RegistryUtil.GetString(k, name);
            if (data is null) continue;
            var i = data.IndexOf("|App=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var app = data[(i + 5)..];
            var end = app.IndexOf('|');
            if (end >= 0) app = app[..end];
            if (IsInTarget(ctx, Environment.ExpandEnvironmentVariables(app))) AddValue(ctx, _env.FirewallRulesKey, name, data, "Windows Firewall rule");
        }
    }

    private void ScanMuiCache(Ctx ctx)
    {
        foreach (var kp in _env.MuiCacheKeys)
        {
            using var k = kp.Open();
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                var dot = name.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (dot < 0) continue;
                if (IsInTarget(ctx, name[..(dot + 4)])) AddValue(ctx, kp, name, RegistryUtil.GetString(k, name), "Program name cache");
            }
        }
    }

    private void ScanSoftwareDeep(Ctx ctx)
    {
        foreach (var root in _env.SoftwareRoots)
        {
            foreach (var l1 in SubKeys(root))
            {
                ctx.Ct.ThrowIfCancellationRequested();
                var p1 = root.Child(l1);
                if (NameMatcher.LooseMatches(l1, ctx.Names)) { AddKey(ctx, p1, "Key name matches the program (deep search)"); continue; }
                if (l1.Equals("Classes", StringComparison.OrdinalIgnoreCase) || l1.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var l2 in SubKeys(p1))
                    if (NameMatcher.LooseMatches(l2, ctx.Names) && !NameMatcher.IsGeneric(l2))
                        AddKey(ctx, p1.Child(l2), "Key name matches the program (deep search)");
            }
        }
    }

    private void ScanCom(Ctx ctx)
    {
        if (ctx.Folder is null && ctx.Target.Executables.Count == 0) return;
        var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _env.ClassesRoots)
        {
            var clsidRoot = root.Child("CLSID");
            using var ck = clsidRoot.Open();
            if (ck is null) continue;
            foreach (var id in ck.GetSubKeyNames())
            {
                ctx.Ct.ThrowIfCancellationRequested();
                foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var sk = ck.OpenSubKey(id + "\\" + server);
                    if (sk is null) continue;
                    var path = CommandLine.FindExecutablePath(RegistryUtil.GetString(sk, "")) ?? CommandLine.ParseIconPath(RegistryUtil.GetString(sk, "")).Path;
                    if (!IsInTarget(ctx, path)) continue;
                    clsids.Add(id);
                    AddKey(ctx, clsidRoot.Child(id), "COM class registered by the program");
                    break;
                }
            }
        }
        if (clsids.Count == 0) return;
        foreach (var root in _env.ClassesRoots)
        {
            using var rk = root.Open();
            if (rk is null) continue;
            foreach (var name in rk.GetSubKeyNames())
            {
                if (name.StartsWith('.') || name.Equals("CLSID", StringComparison.OrdinalIgnoreCase) || name.Equals("Interface", StringComparison.OrdinalIgnoreCase)) continue;
                using var pk = rk.OpenSubKey(name + "\\CLSID");
                if (pk is null) continue;
                var id = RegistryUtil.GetString(pk, "");
                if (id is not null && clsids.Contains(id)) AddKey(ctx, root.Child(name), "ProgID for the program's COM class");
            }
        }
        foreach (var kp in _env.ShellExtensionApprovedKeys)
        {
            using var k = kp.Open();
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
                if (clsids.Contains(name)) AddValue(ctx, kp, name, RegistryUtil.GetString(k, name), "Approved shell extension");
        }
    }

    private void ScanAppModel(Ctx ctx)
    {
        var ids = new[] { ctx.Target.PackageFullName, ctx.Target.PackageFamilyName }.Where(s => !string.IsNullOrEmpty(s)).ToList();
        if (ids.Count == 0) return;
        foreach (var root in _env.AppModelRoots)
            foreach (var sub in SubKeys(root))
                if (ids.Any(i => i!.Equals(sub, StringComparison.OrdinalIgnoreCase)))
                    AddKey(ctx, root.Child(sub), "Windows app registration data");
    }

    // ------------------------------------------------------------------ files

    public bool IsProtectedFolder(string path)
    {
        var p = FileOps.NormalizeDir(path);
        if (p.Length <= 3) return true;
        foreach (var prot in _env.ProtectedFolders.Concat(_env.ProgramFilesRoots).Concat(_env.UserDataRoots).Concat(_env.StartMenuRoots).Concat(_env.DesktopRoots).Concat(_env.TempRoots))
        {
            if (string.IsNullOrEmpty(prot)) continue;
            if (FileOps.IsSameOrUnder(prot, p)) return true; // equal to, or an ancestor of, a protected folder
        }
        if (_env.PackagesDataRoot is not null && FileOps.IsSameOrUnder(_env.PackagesDataRoot, p)) return true;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrEmpty(win) && FileOps.IsSameOrUnder(p, win);
    }

    private void AddPath(Ctx ctx, string path, bool isFolder, string reason, bool created = true)
    {
        path = FileOps.NormalizeDir(path);
        if (IsProtectedFolder(path)) return;
        if (!ctx.Seen.Add("F:" + path)) return;
        if (isFolder && created)
        {
            var sharers = _env.OtherPrograms
                .Where(o => !string.IsNullOrEmpty(o.Folder) && !o.Name.Equals(ctx.Target.DisplayName, StringComparison.OrdinalIgnoreCase) && FileOps.IsSameOrUnder(o.Folder, path))
                .Select(o => o.Name).Distinct().Take(3).ToList();
            if (sharers.Count > 0)
            {
                created = false;
                reason += $" – also used by {string.Join(", ", sharers)}";
            }
            else if (ctx.OtherNames.Contains(NameMatcher.Normalize(Path.GetFileName(path))) && !reason.StartsWith("Install"))
            {
                created = false;
                reason += " (name also matches another installed program – verify manually)";
            }
        }
        ctx.Findings.Add(new Finding(isFolder ? LeftoverKind.Folder : LeftoverKind.File, reason, created, FsPath: path));
    }

    private void ScanInstallFolder(Ctx ctx)
    {
        if (ctx.Folder is { } f && Directory.Exists(f) && !ScanTarget.IsBroadFolder(f)) AddPath(ctx, f, true, "Install folder");
    }

    private void ScanShortcuts(Ctx ctx)
    {
        if (ctx.Folder is null && ctx.Target.Executables.Count == 0) return;
        IEnumerable<string> Links(string root, bool recurse)
        {
            if (!Directory.Exists(root)) return [];
            try { return Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true }); }
            catch { return []; }
        }
        foreach (var lnk in _env.StartMenuRoots.SelectMany(r => Links(r, true)).Concat(_env.DesktopRoots.SelectMany(r => Links(r, false))))
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var info = ShellLink.Read(lnk);
            if (info is null || !IsInTarget(ctx, info.TargetPath)) continue;
            AddPath(ctx, lnk, false, "Shortcut to the program");
            AddStartupApproval(ctx, Path.GetFileName(lnk));
        }
    }

    private void ScanStartMenuFolders(Ctx ctx)
    {
        foreach (var root in _env.StartMenuRoots.Where(Directory.Exists))
        {
            foreach (var dir in SafeDirs(root))
            {
                var name = Path.GetFileName(dir);
                if (NameMatcher.Matches(name, ctx.Names)) AddPath(ctx, dir, true, "Start menu folder");
                else if (NameMatcher.Matches(name, ctx.Publishers))
                {
                    var subs = SafeDirs(dir);
                    var matched = subs.Where(s => NameMatcher.Matches(Path.GetFileName(s), ctx.Names)).ToList();
                    foreach (var m in matched) AddPath(ctx, m, true, "Start menu folder");
                    var files = SafeFiles(dir);
                    var linksIntoTarget = files.All(fl => fl.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && IsInTarget(ctx, ShellLink.Read(fl)?.TargetPath)
                                                          || Path.GetFileName(fl).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
                    if (matched.Count == subs.Length && linksIntoTarget && (matched.Count > 0 || files.Length > 0))
                        AddPath(ctx, dir, true, "Start menu publisher folder");
                }
            }
        }
    }

    private void ScanPackageData(Ctx ctx)
    {
        if (_env.PackagesDataRoot is null || string.IsNullOrEmpty(ctx.Target.PackageFamilyName)) return;
        var d = Path.Combine(_env.PackagesDataRoot, ctx.Target.PackageFamilyName);
        if (Directory.Exists(d)) ctx.Findings.Add(new Finding(LeftoverKind.Folder, "Windows app data folder", true, FsPath: d));
    }

    private void ScanDataFolders(Ctx ctx)
    {
        foreach (var root in _env.UserDataRoots.Concat(_env.ProgramFilesRoots).Concat(_env.TempRoots).Where(Directory.Exists))
        {
            ctx.Ct.ThrowIfCancellationRequested();
            foreach (var dir in SafeDirs(root))
            {
                var name = Path.GetFileName(dir);
                if (NameMatcher.Matches(name, ctx.Names)) { AddPath(ctx, dir, true, "Program data folder"); continue; }
                if (!NameMatcher.Matches(name, ctx.Publishers)) continue;
                var subs = SafeDirs(dir);
                var matched = subs.Where(s => NameMatcher.Matches(Path.GetFileName(s), ctx.Names)).ToList();
                foreach (var m in matched) AddPath(ctx, m, true, "Program data folder under publisher");
                if (matched.Count > 0 && matched.Count == subs.Length && SafeFiles(dir).Length == 0)
                    AddPath(ctx, dir, true, "Publisher folder that only contains this program's data");
            }
        }
    }

    private void ScanDataFoldersDeep(Ctx ctx)
    {
        foreach (var root in _env.UserDataRoots.Where(Directory.Exists))
        {
            foreach (var l1 in SafeDirs(root))
            {
                ctx.Ct.ThrowIfCancellationRequested();
                var n1 = Path.GetFileName(l1);
                if (NameMatcher.LooseMatches(n1, ctx.Names)) { AddPath(ctx, l1, true, "Folder name matches the program (deep search)"); continue; }
                foreach (var l2 in SafeDirs(l1))
                    if (NameMatcher.LooseMatches(Path.GetFileName(l2), ctx.Names) && !NameMatcher.IsGeneric(Path.GetFileName(l2)))
                        AddPath(ctx, l2, true, "Folder name matches the program (deep search)");
            }
        }
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return []; }
    }

    private static string[] SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); } catch { return []; }
    }

    // ------------------------------------------------------------------ result shaping

    private LeftoverNode BuildRegistryTree(IEnumerable<Finding> findings)
    {
        var root = new LeftoverNode(LeftoverKind.Container, "Computer");
        var index = new Dictionary<string, LeftoverNode>(StringComparer.OrdinalIgnoreCase);

        LeftoverNode EnsureKey(RegPath key)
        {
            if (index.TryGetValue(key.FullPath, out var existing)) return existing;
            var parentKey = key.Parent;
            LeftoverNode parent;
            if (parentKey is null)
            {
                var hiveNode = new LeftoverNode(LeftoverKind.RegistryKey, key.HiveName) { Key = key };
                index[key.FullPath] = hiveNode;
                root.Add(hiveNode);
                return hiveNode;
            }
            // Represent the 32-bit view explicitly so the tree reads like regedit.
            parent = EnsureKey(ParentForDisplay(key));
            var node = new LeftoverNode(LeftoverKind.RegistryKey, DisplayLeaf(key)) { Key = key };
            node.IsExcluded = IsExcludedKey(key.FullPath);
            index[key.FullPath] = node;
            parent.Add(node);
            return node;
        }

        foreach (var f in findings.OrderBy(f => f.Location, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Kind == LeftoverKind.RegistryKey)
            {
                var n = EnsureKey(f.Key!);
                n.IsCreated = f.Created;
                n.Reason = f.Reason;
                n.Category = CategoryFor(f.Reason);
                n.IsExcluded = IsExcludedKey(f.Key!.FullPath);
                if (!f.Created) n.Note = f.Reason;
                n.Safety = SafetyEngine.AssessRegistry(f.Key!, null, n.Category, f.Created, f.Reason);
                n.IsChecked = _checkByDefault && f.Created && !n.IsExcluded && n.Safety.IsSafeToAutoSelect;
            }
            else
            {
                var keyNode = EnsureKey(f.Key!);
                var v = new LeftoverNode(LeftoverKind.RegistryValue, string.IsNullOrEmpty(f.ValueName) ? "(Default)" : f.ValueName!)
                {
                    Key = f.Key, ValueName = f.ValueName, ValueData = f.ValueData,
                };
                v.IsCreated = true;
                v.Reason = f.Reason;
                v.Category = CategoryFor(f.Reason);
                v.IsExcluded = IsExcludedKey(f.Key!.FullPath);
                v.Safety = SafetyEngine.AssessRegistry(f.Key!, f.ValueName, v.Category, true, f.Reason);
                v.IsChecked = _checkByDefault && !v.IsExcluded && v.Safety.IsSafeToAutoSelect;
                keyNode.Add(v);
            }
        }
        return root;
    }

    private static readonly (string Prefix, FindingCategory Category)[] ReasonCategories =
    [
        ("Uninstall registration", FindingCategory.UninstallRegistration),
        ("Program settings key", FindingCategory.ProgramKey),
        ("Program key under publisher", FindingCategory.ProgramKey),
        ("Publisher key that only contains", FindingCategory.PublisherKey),
        ("Windows Installer registration", FindingCategory.WindowsInstallerRegistration),
        ("Windows Installer upgrade code", FindingCategory.WindowsInstallerRegistration),
        ("Windows Installer component", FindingCategory.WindowsInstallerComponent),
        ("Starts with Windows", FindingCategory.StartupEntry),
        ("Startup enable/disable flag", FindingCategory.StartupFlag),
        ("Application path registration", FindingCategory.AppPath),
        ("Windows service", FindingCategory.Service),
        ("File association application entry", FindingCategory.FileAssociation),
        ("Shared DLL reference count", FindingCategory.SharedDll),
        ("Windows Firewall rule", FindingCategory.FirewallRule),
        ("Program name cache", FindingCategory.NameCache),
        ("Key name matches the program (deep search)", FindingCategory.DeepNameMatch),
        ("COM class registered by the program", FindingCategory.ComRegistration),
        ("ProgID for the program's COM class", FindingCategory.ComRegistration),
        ("Approved shell extension", FindingCategory.ShellExtension),
        ("Windows app registration data", FindingCategory.AppPackageData),
        ("Windows app data folder", FindingCategory.AppPackageData),
        ("Install folder", FindingCategory.InstallFolder),
        ("Shortcut to the program", FindingCategory.Shortcut),
        ("Start menu folder", FindingCategory.StartMenuFolder),
        ("Start menu publisher folder", FindingCategory.StartMenuFolder),
        ("Program data folder under publisher", FindingCategory.DataFolder),
        ("Program data folder", FindingCategory.DataFolder),
        ("Publisher folder that only contains", FindingCategory.PublisherFolder),
        ("Folder name matches the program (deep search)", FindingCategory.DeepFolderMatch),
        ("Recorded during installation", FindingCategory.TrackedInstallChange),
        ("Recorded in Windows' own settings during installation", FindingCategory.TrackedInWindowsArea),
    ];

    /// <summary>Maps the scanner's reason text (which may carry an appended note) to its category.</summary>
    internal static FindingCategory CategoryFor(string? reason)
    {
        if (reason is null) return FindingCategory.Other;
        foreach (var (prefix, category) in ReasonCategories)
            if (reason.StartsWith(prefix, StringComparison.Ordinal)) return category;
        return FindingCategory.Other;
    }

    internal static IEnumerable<string> KnownReasons => ReasonCategories.Select(r => r.Prefix);

    private static RegPath ParentForDisplay(RegPath key)
    {
        // A 32-bit HKLM\SOFTWARE\X key displays as HKLM\SOFTWARE\WOW6432Node\X; build parents from the display path.
        var full = key.FullPath;
        var i = full.LastIndexOf('\\');
        var parent = RegPath.Parse(full[..i]);
        return parent;
    }

    private static string DisplayLeaf(RegPath key)
    {
        var full = key.FullPath;
        return full[(full.LastIndexOf('\\') + 1)..];
    }

    private ObservableCollection<LeftoverNode> BuildFileList(IEnumerable<Finding> findings, SafetyEngine.Context safety, CancellationToken ct)
    {
        var list = findings.OrderBy(f => f.FsPath, StringComparer.OrdinalIgnoreCase).ToList();
        // Drop entries already contained in another created folder: deleting the folder removes them anyway.
        var createdFolders = list.Where(f => f.Kind == LeftoverKind.Folder && f.Created).Select(f => f.FsPath!).ToList();
        var result = new ObservableCollection<LeftoverNode>();
        foreach (var f in list)
        {
            if (createdFolders.Any(cf => !cf.Equals(f.FsPath, StringComparison.OrdinalIgnoreCase) && FileOps.IsSameOrUnder(f.FsPath!, cf))) continue;
            var node = new LeftoverNode(f.Kind, f.FsPath!)
            {
                FsPath = f.FsPath,
                Size = ct.IsCancellationRequested ? 0 : FileOps.GetSize(f.FsPath!),
            };
            node.IsCreated = f.Created;
            node.Reason = f.Reason;
            node.Category = CategoryFor(f.Reason);
            node.IsExcluded = IsExcludedFolder(f.FsPath!);
            if (!f.Created) node.Note = f.Reason;
            node.Safety = SafetyEngine.AssessPath(f.FsPath!, node.Category, f.Created, safety, f.Reason);
            node.IsChecked = _checkByDefault && f.Created && !node.IsExcluded && node.Safety.IsSafeToAutoSelect;
            result.Add(node);
        }
        return result;
    }
}
