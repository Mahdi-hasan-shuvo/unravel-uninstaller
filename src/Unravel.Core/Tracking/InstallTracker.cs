using System.Text.Json;
using System.Text.Json.Serialization;
using Unravel.Core.Leftovers;
using Unravel.Core.Logging;
using Unravel.Core.Programs;
using Unravel.Core.Safety;
using Unravel.Core.SystemOps;
using Unravel.Core.Util;

namespace Unravel.Core.Tracking;

public enum SessionState { WaitingForInstall, Completed, CleanedUp }

/// <summary>A recorded installation: the before/after snapshots and the changes between them.</summary>
public sealed class InstallLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string ProgramName { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime? Completed { get; set; }
    public SessionState State { get; set; }
    public List<TrackedChange> Changes { get; set; } = [];
    /// <summary>Uninstall registry keys created by the install – used to link the log to an installed program.</summary>
    public List<string> UninstallKeys { get; set; } = [];
    public CleanupRecord? Cleanup { get; set; }

    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public string BeforePath => Path.Combine(Folder, "before.snap");
    [JsonIgnore] public string AfterPath => Path.Combine(Folder, "after.snap");
    [JsonIgnore] public IEnumerable<TrackedChange> Added => Changes.Where(c => c.Type == ChangeType.Added);
}

/// <summary>Everything needed to undo a cleanup.</summary>
public sealed class CleanupRecord
{
    public DateTime When { get; set; }
    public string? QuarantineFolder { get; set; }
    public string? RegistryBackupFolder { get; set; }
    public string? TaskBackupFolder { get; set; }
    public List<string> RemovedTasks { get; set; } = [];
    public List<string> Failures { get; set; } = [];
    public int Removed { get; set; }
    public bool Undone { get; set; }
}

public sealed record CleanupItem(TrackedChange Change, SafetyAssessment Safety)
{
    public bool IsChecked { get; set; } = Safety.IsSafeToAutoSelect;
}

/// <summary>Records installations and removes exactly what they added, with backups that allow undoing the cleanup.</summary>
public sealed class InstallTracker
{
    public string Root { get; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public InstallTracker(string? root = null)
    {
        Root = root ?? Path.Combine(Settings.SettingsStore.DefaultFolder, "InstallLogs");
    }

    public InstallLog Begin(string programName, SnapshotOptions options, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(programName)) throw new ArgumentException("Enter the name of the program you are about to install.");
        var log = new InstallLog { ProgramName = programName.Trim(), Started = DateTime.Now, State = SessionState.WaitingForInstall };
        log.Folder = Path.Combine(Root, $"{log.Started:yyyyMMdd-HHmmss}-{log.Id}");
        var snap = SystemSnapshot.Capture(options, "Before " + log.ProgramName, progress, ct);
        snap.Save(log.BeforePath);
        Save(log);
        ActivityLog.Action("Install tracker", $"Before-snapshot taken for \"{log.ProgramName}\" ({snap.ItemCount:N0} items)", log.Folder);
        return log;
    }

    public SnapshotComparison Complete(InstallLog log, SnapshotOptions options, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        var before = SystemSnapshot.Load(log.BeforePath);
        var after = SystemSnapshot.Capture(options, "After " + log.ProgramName, progress, ct);
        after.Save(log.AfterPath);
        var cmp = SnapshotDiff.Compare(before, after);
        log.Changes = cmp.Changes;
        log.Completed = DateTime.Now;
        log.State = SessionState.Completed;
        log.UninstallKeys = cmp.Changes
            .Where(c => c.Type == ChangeType.Added && c.Area == ChangeArea.RegistryKey && c.Location.Contains(@"\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Location).ToList();
        Save(log);
        ActivityLog.Action("Install tracker", $"Install of \"{log.ProgramName}\" recorded: {cmp.Count(ChangeType.Added)} added, {cmp.Count(ChangeType.Modified)} modified, {cmp.Count(ChangeType.Removed)} removed", log.Folder);
        return cmp;
    }

    public void Save(InstallLog log)
    {
        Directory.CreateDirectory(log.Folder);
        File.WriteAllText(Path.Combine(log.Folder, "log.json"), JsonSerializer.Serialize(log, Json));
    }

    public List<InstallLog> List()
    {
        if (!Directory.Exists(Root)) return [];
        var list = new List<InstallLog>();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var file = Path.Combine(dir, "log.json");
            if (!File.Exists(file)) continue;
            try
            {
                var log = JsonSerializer.Deserialize<InstallLog>(File.ReadAllText(file), Json);
                if (log is null) continue;
                log.Folder = dir;
                list.Add(log);
            }
            catch { }
        }
        return list.OrderByDescending(l => l.Started).ToList();
    }

    public void Delete(InstallLog log) => FileOps.Delete(log.Folder, toRecycleBin: false);

    /// <summary>Finds the recorded install of an installed program (by its uninstall key, then by name).</summary>
    public InstallLog? FindFor(InstalledProgram program)
    {
        var logs = List().Where(l => l.State != SessionState.WaitingForInstall).ToList();
        var key = program.RegistryKey.FullPath;
        var byKey = logs.FirstOrDefault(l => l.UninstallKeys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)));
        if (byKey is not null) return byKey;
        var names = NameMatcher.NameVariants(new ScanTarget { DisplayName = program.DisplayName });
        return logs.FirstOrDefault(l => NameMatcher.Matches(l.ProgramName, names) || NameMatcher.Matches(NameMatcher.StripVersion(l.ProgramName), names));
    }

    // ------------------------------------------------------------------ cleanup

    /// <summary>The removable items of a log (only added items that still exist), each rated by the safety engine.</summary>
    public static List<CleanupItem> PlanCleanup(InstallLog log)
    {
        var ctx = SafetyEngine.Context.Capture();
        var items = new List<CleanupItem>();
        foreach (var c in log.Added)
        {
            SafetyAssessment? a = c.Area switch
            {
                ChangeArea.Folder or ChangeArea.File when FileOps.PathExists(c.Location) =>
                    SafetyEngine.AssessPath(c.Location, FindingCategory.TrackedInstallChange, true, ctx),
                ChangeArea.RegistryKey when RegPath.TryParse(c.Location, out var k) && k!.Exists() =>
                    SafetyEngine.AssessRegistry(k, null, FindingCategory.TrackedInstallChange, true),
                ChangeArea.RegistryValue when c.KeyPath is not null && RegPath.TryParse(c.KeyPath, out var vk) && ValueExists(vk!, c.ValueName) =>
                    SafetyEngine.AssessRegistry(vk!, c.ValueName, FindingCategory.TrackedInstallChange, true),
                ChangeArea.ScheduledTask => new SafetyAssessment(Confidence.High, RiskFlags.None, "Scheduled task created by the install."),
                ChangeArea.Service => RegPath.TryParse(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + c.Location, out var sk)
                    ? SafetyEngine.AssessRegistry(sk!, null, FindingCategory.Service, true) : null,
                _ => null, // startup entries are covered by their registry value / shortcut
            };
            if (a is null) continue;
            if (!a.IsBlocked && IsWindowsOwnArea(c)) a = a with { Confidence = Confidence.Low, Explanation = "Inside Windows' own settings – Windows may have changed this itself while the install was recorded." };
            items.Add(new CleanupItem(c, a));
        }
        // Registry values inside keys that are removed anyway, and files inside removed folders, are covered by their parent.
        var keys = items.Where(i => i.Change.Area == ChangeArea.RegistryKey).Select(i => i.Change.Location).ToList();
        var folders = items.Where(i => i.Change.Area == ChangeArea.Folder).Select(i => i.Change.Location).ToList();
        items.RemoveAll(i => i.Change.Area == ChangeArea.RegistryValue && keys.Any(k => i.Change.KeyPath!.Equals(k, StringComparison.OrdinalIgnoreCase) || i.Change.KeyPath!.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)));
        items.RemoveAll(i => i.Change.Area == ChangeArea.File && folders.Any(f => FileOps.IsSameOrUnder(i.Change.Location, f)));
        // A service is removed with "sc delete" (which also tells the Service Control Manager); drop its duplicate key entry.
        var services = items.Where(i => i.Change.Area == ChangeArea.Service).Select(i => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + i.Change.Location).ToList();
        items.RemoveAll(i => i.Change.Area == ChangeArea.RegistryKey && services.Any(s => i.Change.Location.Equals(s, StringComparison.OrdinalIgnoreCase)));
        return items;
    }

    private static readonly string[] InstallerAreas =
    [
        @"\CurrentVersion\Uninstall\", @"\CurrentVersion\Run", @"\CurrentVersion\App Paths\", @"\CurrentVersion\SharedDLLs",
        @"\Installer\", @"\CurrentVersion\Explorer\StartupApproved\", @"\Windows\Start Menu\",
    ];

    /// <summary>
    /// Changes inside Microsoft's own registry and data areas are often made by Windows itself during the recording,
    /// so they are never pre-selected – except well-known places installers write to (Uninstall, Run, App Paths…).
    /// </summary>
    internal static bool IsWindowsOwnArea(TrackedChange c)
    {
        var p = c.Area is ChangeArea.RegistryValue ? c.KeyPath ?? c.Location : c.Location;
        if (InstallerAreas.Any(a => p.Contains(a, StringComparison.OrdinalIgnoreCase))) return false;
        if (c.Area is ChangeArea.RegistryKey or ChangeArea.RegistryValue)
            return p.Contains(@"\Software\Microsoft\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\WOW6432Node\Microsoft\", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(@"HKEY_LOCAL_MACHINE\SYSTEM\", StringComparison.OrdinalIgnoreCase) && !p.Contains(@"\Services\", StringComparison.OrdinalIgnoreCase);
        if (c.Area is ChangeArea.File or ChangeArea.Folder) return p.Contains(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool ValueExists(RegPath key, string? name)
    {
        using var k = key.Open();
        return k is not null && k.GetValueNames().Contains(name ?? "", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the checked items: files go to quarantine, registry items are exported to .reg first, scheduled
    /// tasks are exported to XML. Everything is recorded so <see cref="Undo"/> can put it back.
    /// </summary>
    public CleanupRecord Clean(InstallLog log, IEnumerable<CleanupItem> items, Quarantine quarantine, RegistryBackup backup)
    {
        var chosen = items.Where(i => i.IsChecked && !i.Safety.IsBlocked).ToList();
        var record = new CleanupRecord { When = DateTime.Now };

        var regItems = chosen.Where(i => i.Change.Area is ChangeArea.RegistryKey or ChangeArea.RegistryValue or ChangeArea.Service).ToList();
        if (regItems.Count > 0)
        {
            var keys = regItems.Select(i => i.Change.Area switch
            {
                ChangeArea.RegistryKey => RegPath.Parse(i.Change.Location),
                ChangeArea.RegistryValue => RegPath.Parse(i.Change.KeyPath!),
                _ => RegPath.Parse(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + i.Change.Location),
            }).ToList();
            var b = backup.Export(keys, "Install cleanup " + log.ProgramName);
            record.RegistryBackupFolder = b.Folder;
            if (!b.Ok)
            {
                record.Failures.Add("Registry backup failed, so no registry items were removed: " + b.Error);
                regItems.Clear();
            }
        }

        // Tasks: export, then delete.
        var tasks = chosen.Where(i => i.Change.Area == ChangeArea.ScheduledTask).ToList();
        if (tasks.Count > 0)
        {
            record.TaskBackupFolder = Path.Combine(log.Folder, $"tasks-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(record.TaskBackupFolder);
            var all = ScheduledTasks.List().ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var t in tasks)
            {
                if (!all.TryGetValue(t.Change.Location, out var info)) continue;
                var file = Path.Combine(record.TaskBackupFolder, $"{++n:000}.xml");
                File.WriteAllText(file, info.Path + "\n" + info.Xml);
                try { ScheduledTasks.Delete(info.Path); record.RemovedTasks.Add(file); record.Removed++; }
                catch (Exception e) { record.Failures.Add($"{info.Path}: {e.Message}"); }
            }
        }

        // Registry: values first, then keys (deepest first).
        foreach (var i in regItems.Where(i => i.Change.Area == ChangeArea.RegistryValue))
        {
            if (RegistryUtil.DeleteValue(RegPath.Parse(i.Change.KeyPath!), i.Change.ValueName ?? "", out var err)) record.Removed++;
            else record.Failures.Add($"{i.Change.Location}: {err}");
        }
        foreach (var i in regItems.Where(i => i.Change.Area == ChangeArea.Service))
        {
            var err = DeleteService(i.Change.Location);
            if (err is null) record.Removed++; else record.Failures.Add($"Service {i.Change.Location}: {err}");
        }
        foreach (var i in regItems.Where(i => i.Change.Area == ChangeArea.RegistryKey).OrderByDescending(i => i.Change.Location.Length))
        {
            if (RegistryUtil.DeleteKeyTree(RegPath.Parse(i.Change.Location), out var err)) record.Removed++;
            else record.Failures.Add($"{i.Change.Location}: {err}");
        }

        // Files and folders: quarantine (reversible).
        var paths = chosen.Where(i => i.Change.Area is ChangeArea.File or ChangeArea.Folder).Select(i => i.Change.Location).ToList();
        if (paths.Count > 0)
        {
            var (set, results) = quarantine.Move(paths, "Install cleanup " + log.ProgramName);
            record.QuarantineFolder = set.Folder;
            record.Removed += results.Count(r => r.Ok);
            record.Failures.AddRange(results.Where(r => !r.Ok).Select(r => $"{r.Path}: {r.Error}"));
        }

        log.Cleanup = record;
        log.State = SessionState.CleanedUp;
        Save(log);
        ActivityLog.Action("Install tracker", $"Removed {record.Removed} item(s) added by \"{log.ProgramName}\"" + (record.Failures.Count > 0 ? $", {record.Failures.Count} failure(s)" : ""),
            string.Join("\n", record.Failures));
        return record;
    }

    /// <summary>Stops a service and deletes it through the Service Control Manager. Returns an error message or null.</summary>
    private static string? DeleteService(string name)
    {
        if (!ProcessUtil.IsElevated) return "Removing a service requires administrator rights.";
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(name);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch { /* not running or already gone */ }
        var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", $"delete \"{name}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode == 0 ? null : output.Trim();
    }

    /// <summary>Restores everything a cleanup removed: quarantined files, registry backups and scheduled tasks.</summary>
    public List<string> Undo(InstallLog log, Quarantine quarantine, RegistryBackup backup)
    {
        var problems = new List<string>();
        var c = log.Cleanup ?? throw new InvalidOperationException("This install has not been cleaned up.");
        if (c.Undone) throw new InvalidOperationException("This cleanup has already been undone.");
        if (c.RegistryBackupFolder is not null && Directory.Exists(c.RegistryBackupFolder))
        {
            var set = backup.List().FirstOrDefault(s => s.Folder.Equals(c.RegistryBackupFolder, StringComparison.OrdinalIgnoreCase))
                      ?? new BackupSet(c.RegistryBackupFolder, "", DateTime.Now, Directory.GetFiles(c.RegistryBackupFolder, "*.reg").Length);
            var r = backup.Restore(set);
            if (!r.Ok) problems.Add("Registry: " + r.Error);
        }
        foreach (var file in c.RemovedTasks)
        {
            try
            {
                var text = File.ReadAllText(file);
                var nl = text.IndexOf('\n');
                ScheduledTasks.Register(text[..nl], text[(nl + 1)..]);
            }
            catch (Exception e) { problems.Add($"Task {Path.GetFileName(file)}: {e.Message}"); }
        }
        if (c.QuarantineFolder is not null)
        {
            var set = quarantine.List().FirstOrDefault(s => s.Folder.Equals(c.QuarantineFolder, StringComparison.OrdinalIgnoreCase));
            if (set is not null) problems.AddRange(quarantine.Restore(set).Where(r => !r.Ok).Select(r => $"{r.Path}: {r.Error}"));
        }
        c.Undone = true;
        log.State = SessionState.Completed;
        Save(log);
        ActivityLog.Action("Install tracker", $"Undid the cleanup of \"{log.ProgramName}\"" + (problems.Count > 0 ? $" with {problems.Count} problem(s)" : ""), string.Join("\n", problems));
        return problems;
    }
}
