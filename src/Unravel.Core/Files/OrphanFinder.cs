using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Unravel.Core.Apps;
using Unravel.Core.Autorun;
using Unravel.Core.Leftovers;
using Unravel.Core.Logging;
using Unravel.Core.Programs;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Core.Files;

public enum OrphanKind { UnownedFolder, BrokenShortcut }

public sealed class OrphanCandidate
{
    public required string Path { get; init; }
    public OrphanKind Kind { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public string? Owner { get; init; }
    public List<string> Evidence { get; init; } = [];
    public required SafetyAssessment Safety { get; init; }
    public bool IsChecked { get; set; }
    public string KindText => Kind == OrphanKind.BrokenShortcut ? "Broken shortcut" : "Folder";
}

/// <summary>Everything that can prove a folder is still in use. Production fills it from the system; tests from fakes.</summary>
public sealed class OrphanEnvironment
{
    public List<string> ProgramRoots { get; init; } = [];
    public List<string> DataRoots { get; init; } = [];
    public List<string> ShortcutRoots { get; init; } = [];
    /// <summary>Installed programs: display name, publisher, install folder.</summary>
    public List<(string Name, string? Publisher, string? Folder)> Programs { get; init; } = [];
    /// <summary>Executable paths referenced by services, scheduled tasks, startup entries, file associations, running processes…</summary>
    public List<(string Path, string Source)> References { get; init; } = [];
    public int MinAgeDays { get; init; } = 30;
    public DateTime Now { get; init; } = DateTime.Now;

    public static OrphanEnvironment CaptureDefault(IEnumerable<InstalledProgram> programs, IEnumerable<WindowsApp>? apps = null)
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var refs = new List<(string, string)>();

        void AddRef(string? path, string source)
        {
            var exe = CommandLine.FindExecutablePath(path) ?? path;
            if (!string.IsNullOrWhiteSpace(exe) && System.IO.Path.IsPathRooted(exe)) refs.Add((Environment.ExpandEnvironmentVariables(exe.Trim('"')), source));
        }

        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var n in svc?.GetSubKeyNames() ?? [])
            {
                using var k = svc!.OpenSubKey(n);
                if (k is null) continue;
                AddRef(RegistryUtil.GetString(k, "ImagePath"), "Windows service " + n);
                using var p = k.OpenSubKey("Parameters");
                if (p is not null) AddRef(RegistryUtil.GetString(p, "ServiceDll"), "Windows service " + n);
            }
        }
        catch { }
        foreach (var t in Tracking.ScheduledTasks.List()) foreach (var a in t.Actions) AddRef(a, "Scheduled task " + t.Path);
        try
        {
            foreach (var e in new AutorunManager(AutorunEnvironment.CreateDefault(includeServices: false)).Load()) AddRef(e.TargetPath, "Startup entry " + e.Name);
        }
        catch { }
        foreach (var (path, source) in FileAssociationHandlers()) AddRef(path, source);
        foreach (var p in ProcessUtil.GetProcesses()) if (p.ImagePath is not null) refs.Add((p.ImagePath, "Running process " + p.Name));
        // Tools reachable from the command line, and folders other software points to through environment variables.
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            try
            {
                foreach (System.Collections.DictionaryEntry v in Environment.GetEnvironmentVariables(target))
                {
                    var name = (string)v.Key;
                    foreach (var part in (v.Value as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var expanded = Environment.ExpandEnvironmentVariables(part).Trim('"');
                        if (expanded.Length > 3 && System.IO.Path.IsPathRooted(expanded)) refs.Add((System.IO.Path.Combine(expanded, "."), $"Environment variable {name}"));
                    }
                }
            }
            catch { }
        }
        // Shortcuts on the Start menu, desktop and taskbar still in use.
        var shortcutRoots = new[]
        {
            F(Environment.SpecialFolder.Programs), F(Environment.SpecialFolder.CommonPrograms), F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            System.IO.Path.Combine(F(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch"),
        };
        foreach (var root in shortcutRoots.Where(Directory.Exists))
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }))
                    if (ShellLink.Read(lnk) is { TargetPath.Length: > 3 } li) refs.Add((li.TargetPath, "Shortcut " + System.IO.Path.GetFileName(lnk)));
            }
            catch { }
        }

        var progList = programs.Select(p => (p.DisplayName, p.Publisher, p.ResolvedInstallFolder)).ToList();
        if (apps is not null) progList.AddRange(apps.Select(a => (a.Name, a.Publisher, (string?)null)));
        return new OrphanEnvironment
        {
            ProgramRoots = new[] { F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86) }
                .Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DataRoots = new[] { F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData), local,
                                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(local)!, "LocalLow") }
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ShortcutRoots = [F(Environment.SpecialFolder.Programs), F(Environment.SpecialFolder.CommonPrograms),
                             F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory)],
            Programs = progList,
            References = refs,
        };
    }

    /// <summary>Programs registered to open file types (HKCR\*\shell\*\command).</summary>
    private static IEnumerable<(string Path, string Source)> FileAssociationHandlers()
    {
        var list = new List<(string, string)>();
        try
        {
            using var cr = Registry.ClassesRoot;
            foreach (var name in cr.GetSubKeyNames())
            {
                if (name.StartsWith('.')) continue;
                using var shell = cr.OpenSubKey(name + @"\shell");
                if (shell is null) continue;
                foreach (var verb in shell.GetSubKeyNames().Take(4))
                {
                    using var cmd = shell.OpenSubKey(verb + @"\command");
                    var c = cmd is null ? null : RegistryUtil.GetString(cmd, "");
                    if (c is not null) list.Add((c, "File association " + name));
                }
            }
        }
        catch { }
        return list;
    }
}

/// <summary>
/// Finds potentially orphaned folders and broken shortcuts. A folder is only reported when no installed program
/// owns it (by folder or name), nothing references a file inside it (services, tasks, startup, file associations,
/// running processes), it is not system-owned, and it has not been modified recently. Names alone never qualify.
/// </summary>
public static class OrphanFinder
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Packages", "Temp", "Microsoft.NET", "Windows Defender", "Windows NT", "Windows Mail",
        "Windows Media Player", "Windows Photo Viewer", "Windows Sidebar", "WindowsApps", "WindowsPowerShell", "Internet Explorer",
        "ModifiableWindowsApps", "Reference Assemblies", "MSBuild", "dotnet", "Package Cache", "regid.1991-06.com.microsoft",
        "ssh", "USOShared", "USOPrivate", "SoftwareDistribution", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps",
        "Desktop", "Documents", "Downloads", "Start Menu", "Templates", "Application Data", "History", "Programs", "PeerDistRepub",
        "Unravel", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Realtek", "Mozilla", "Google", "Adobe", "Apple", "Package Store",
    };

    public static List<OrphanCandidate> Find(OrphanEnvironment env, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<OrphanCandidate>();
        var programVariants = env.Programs.Select(p =>
        {
            var t = new ScanTarget { DisplayName = p.Name, Publisher = p.Publisher, InstallFolder = p.Folder };
            return (p.Name, Names: NameMatcher.NameVariants(t), Publishers: NameMatcher.PublisherVariants(t), p.Folder);
        }).ToList();
        var stems = DistinctiveStems(env.Programs);

        foreach (var root in env.ProgramRoots.Concat(env.DataRoots).Where(Directory.Exists))
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(dir);
                var name = System.IO.Path.GetFileName(dir);
                if (Ignored.Contains(name) || name.StartsWith('.') || name.StartsWith("{", StringComparison.Ordinal)) continue;
                try { if ((File.GetAttributes(dir) & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue; } catch { continue; }
                if (SafetyEngine.IsCriticalPath(dir)) continue;
                if (StartsWithStem(name, stems)) continue; // e.g. "Arduino15" belongs to "Arduino IDE"
                var c = Evaluate(dir, env, programVariants);
                if (c is not null) results.Add(c);
            }
        }

        foreach (var root in env.ShortcutRoots.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }).ToList(); }
            catch { continue; }
            foreach (var lnk in links)
            {
                ct.ThrowIfCancellationRequested();
                var info = ShellLink.Read(lnk);
                if (info is null || string.IsNullOrWhiteSpace(info.TargetPath)) continue;
                var target = info.TargetPath;
                // Only judge plain local paths on drives that are present; network and shell targets are left alone.
                if (!System.IO.Path.IsPathRooted(target) || target.StartsWith(@"\\")) continue;
                var drive = System.IO.Path.GetPathRoot(target);
                if (drive is null || !Directory.Exists(drive)) continue;
                if (FileOps.PathExists(target)) continue;
                results.Add(new OrphanCandidate
                {
                    Path = lnk, Kind = OrphanKind.BrokenShortcut, LastModified = SafeWriteTime(lnk), Size = SafeLength(lnk),
                    Evidence = [$"Points to {target}, which no longer exists"],
                    Safety = new SafetyAssessment(Confidence.High, RiskFlags.None, "Shortcut whose target no longer exists."),
                });
            }
        }
        ActivityLog.Info("Orphans", $"Orphan scan found {results.Count} candidate(s)");
        return results.OrderBy(r => r.Kind).ThenByDescending(r => r.Size).ToList();
    }

    internal static OrphanCandidate? Evaluate(string dir, OrphanEnvironment env, List<(string Name, List<string> Names, List<string> Publishers, string? Folder)> programs)
    {
        var name = System.IO.Path.GetFileName(dir);
        // 1) Owned by an installed program?
        foreach (var p in programs)
        {
            if (p.Folder is { Length: > 3 } f && (FileOps.IsSameOrUnder(f, dir) || FileOps.IsSameOrUnder(dir, f))) return null;
            if (NameMatcher.Matches(name, p.Names) || NameMatcher.Matches(name, p.Publishers)) return null;
        }
        // 2) Referenced by anything that runs?
        var reference = env.References.FirstOrDefault(r => FileOps.IsSameOrUnder(r.Path, dir));
        if (reference.Path is not null) return null;
        // 3) System-owned folders are left alone.
        var owner = GetOwner(dir);
        if (owner is not null && (owner.EndsWith("TrustedInstaller", StringComparison.OrdinalIgnoreCase) || owner.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)) &&
            env.ProgramRoots.Any(r => FileOps.IsSameOrUnder(dir, r)))
            return null;
        // 4) Recently used folders are probably still in use (portable apps, active data).
        var (lastWrite, size, exeCount) = Measure(dir);
        var ageDays = (env.Now - lastWrite).TotalDays;
        if (ageDays < env.MinAgeDays) return null;

        var evidence = new List<string>
        {
            "No installed program or Windows app uses this folder or a matching name",
            "No service, scheduled task, startup entry, file association or running process refers to it",
            $"Not modified for {(int)ageDays} days",
        };
        if (owner is not null) evidence.Add("Owner: " + owner);
        var risks = RiskFlags.None;
        if (exeCount > 0)
        {
            risks |= RiskFlags.ContainsExecutables;
            evidence.Add($"Contains {exeCount} program file(s) – it may be a portable app; check before deleting");
        }
        var explanation = string.Join("; ", evidence) + ".";
        return new OrphanCandidate
        {
            Path = dir, Kind = OrphanKind.UnownedFolder, Size = size, LastModified = lastWrite, Owner = owner, Evidence = evidence,
            Safety = new SafetyAssessment(Confidence.Low, risks, explanation),
        };
    }

    /// <summary>
    /// Size, program count and the newest file time inside a folder. The folder's own timestamp is not used: it
    /// changes whenever anything inside is added or removed, which says nothing about whether the data is used.
    /// </summary>
    /// <summary>
    /// The first distinctive word of every installed program and publisher ("Arduino", "Contoso"). A folder starting
    /// with one is treated as owned: reporting an in-use folder is worse than missing an orphan.
    /// </summary>
    internal static HashSet<string> DistinctiveStems(IEnumerable<(string Name, string? Publisher, string? Folder)> programs)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, publisher, folder) in programs)
        {
            foreach (var text in new[] { name, publisher, folder is null ? null : System.IO.Path.GetFileName(folder.TrimEnd('\\')) })
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var first = System.Text.RegularExpressions.Regex.Split(text, @"[\s\-_.,()]+").FirstOrDefault(w => w.Length > 0);
                if (first is null) continue;
                var stem = NameMatcher.Normalize(first);
                if (stem.Length >= 4 && !NameMatcher.IsGeneric(stem)) stems.Add(stem);
            }
        }
        return stems;
    }

    /// <summary>True when the folder name, or any word in it ("com.seelen.seelen-ui"), starts with a program's stem.</summary>
    internal static bool StartsWithStem(string folderName, HashSet<string> stems)
    {
        var n = NameMatcher.Normalize(folderName);
        if (stems.Any(s => n.StartsWith(s, StringComparison.Ordinal))) return true;
        var words = System.Text.RegularExpressions.Regex.Split(folderName, @"[^A-Za-z0-9]+").Select(NameMatcher.Normalize).Where(w => w.Length >= 4);
        return words.Any(w => stems.Any(s => w.StartsWith(s, StringComparison.Ordinal)));
    }

    private static (DateTime LastWrite, long Size, int Exes) Measure(string dir)
    {
        var last = DateTime.MinValue;
        long size = 0;
        int exes = 0, n = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (++n > 20000) break;
                try
                {
                    size += f.Length;
                    if (f.LastWriteTime > last) last = f.LastWriteTime;
                    if (f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) exes++;
                }
                catch { }
            }
        }
        catch { }
        if (n == 0) last = SafeWriteTime(dir); // an empty folder: its own time is all there is
        return (last, size, exes);
    }

    public static string? GetOwner(string path)
    {
        try
        {
            var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner);
            return acl.GetOwner(typeof(NTAccount))?.Value;
        }
        catch { return null; }
    }

    private static DateTime SafeWriteTime(string p) { try { return File.GetLastWriteTime(p); } catch { return DateTime.MinValue; } }
    private static long SafeLength(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }
}
