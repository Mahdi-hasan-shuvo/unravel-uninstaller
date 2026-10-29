using System.Diagnostics;
using Unravel.Core.Util;

namespace Unravel.Core.Leftovers;

public enum VerificationArea { Registration, InstallFolder, RunningProcess, RegistryItem, FileItem, Rescan }

public enum VerificationStatus
{
    /// <summary>Confirmed gone.</summary>
    Removed,
    /// <summary>Still there although it should be gone.</summary>
    StillPresent,
    /// <summary>In use; Windows deletes it at the next restart.</summary>
    PendingRestart,
    /// <summary>Still there because the user chose not to delete it.</summary>
    Kept,
}

public enum VerificationVerdict { Clean, CleanAfterRestart, LeftoversKept, ItemsRemain }

public sealed record VerificationCheck(VerificationArea Area, string Location, VerificationStatus Status, string Detail);

/// <summary>An item the wizard reported as deleted, with enough identity to look for it again.</summary>
public sealed record RemovedItem(LeftoverKind Kind, string Location, RegPath? Key = null, string? ValueName = null, string? FsPath = null, DeleteOutcome? Outcome = null)
{
    public static RemovedItem From(LeftoverNode n, DeleteOutcome? outcome = null) =>
        new(n.Kind, n.Location, n.Key, n.ValueName, n.FsPath, outcome);
}

public sealed class VerificationReport
{
    public List<VerificationCheck> Checks { get; } = [];
    public TimeSpan Duration { get; set; }
    public string? RescanError { get; set; }
    public int Count(VerificationStatus s) => Checks.Count(c => c.Status == s);

    public VerificationVerdict Verdict =>
        Count(VerificationStatus.StillPresent) > 0 ? VerificationVerdict.ItemsRemain :
        Count(VerificationStatus.PendingRestart) > 0 ? VerificationVerdict.CleanAfterRestart :
        Count(VerificationStatus.Kept) > 0 ? VerificationVerdict.LeftoversKept :
        VerificationVerdict.Clean;
}

/// <summary>
/// Confirms after an uninstall what is really gone: the program's registration, its install folder and running
/// processes, every item the wizard deleted, and – by scanning again in the same mode – anything still findable.
/// Nothing is trusted from the deletion reports alone; every check looks at the registry and file system again.
/// </summary>
public sealed class UninstallVerifier
{
    public sealed class Input
    {
        public required ScanTarget Target { get; init; }
        /// <summary>Whether Windows still lists the program as installed (uninstall key, package or WinGet entry).</summary>
        public Func<bool>? StillRegistered { get; init; }
        public IReadOnlyList<RemovedItem> Removed { get; init; } = [];
        /// <summary>Locations found earlier that the user decided to keep.</summary>
        public IReadOnlyCollection<string> Kept { get; init; } = [];
        /// <summary>Scanner and mode for the confirming re-scan; null skips it.</summary>
        public LeftoverScanner? Scanner { get; init; }
        public ScanMode RescanMode { get; init; } = ScanMode.Moderate;
        /// <summary>The mode of the scan the user reviewed, when the re-scan looks deeper than that.</summary>
        public ScanMode? ReviewedMode { get; init; }
        public bool CheckProcesses { get; init; } = true;
    }

    public VerificationReport Verify(Input input, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var report = new VerificationReport();
        var kept = new HashSet<string>(input.Kept, StringComparer.OrdinalIgnoreCase);
        var t = input.Target;

        progress?.Report("Checking whether Windows still lists the program…");
        if (input.StillRegistered is { } still)
        {
            bool registered;
            try { registered = still(); } catch { registered = false; }
            report.Checks.Add(new(VerificationArea.Registration, t.DisplayName,
                registered ? VerificationStatus.StillPresent : VerificationStatus.Removed,
                registered ? "Still listed as installed. The uninstaller may have been cancelled or may need a restart." : "No longer listed as installed."));
        }

        if (t.InstallFolder is { } folder && !ScanTarget.IsBroadFolder(folder))
        {
            progress?.Report("Checking the install folder…");
            // When the wizard deleted the folder itself, it is checked with the other deleted items below.
            bool deletedByWizard = input.Removed.Any(r => r.FsPath is not null && r.FsPath.TrimEnd('\\').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (!deletedByWizard && !Directory.Exists(folder))
                report.Checks.Add(new(VerificationArea.InstallFolder, folder, VerificationStatus.Removed, "The install folder is gone."));
            else if (!deletedByWizard)
            {
                var (files, bytes) = CountFiles(folder, ct);
                var status = kept.Contains(folder) || kept.Any(k => FileOps.IsSameOrUnder(k, folder)) ? VerificationStatus.Kept : VerificationStatus.StillPresent;
                report.Checks.Add(new(VerificationArea.InstallFolder, folder, status,
                    files == 0 ? "The folder still exists but is empty." : $"{files} file(s), {Format.Bytes(bytes)} remain."));
            }

            if (input.CheckProcesses)
            {
                progress?.Report("Checking for programs still running from the install folder…");
                foreach (var p in SafeProcesses(folder))
                    report.Checks.Add(new(VerificationArea.RunningProcess, p.ImagePath ?? p.Name, VerificationStatus.StillPresent, $"{p.Name} (PID {p.Pid}) is still running."));
            }
        }

        progress?.Report("Confirming the deleted items are gone…");
        foreach (var r in input.Removed)
        {
            ct.ThrowIfCancellationRequested();
            report.Checks.Add(CheckRemoved(r));
        }

        if (input.Scanner is { } scanner)
        {
            progress?.Report($"Scanning again ({input.RescanMode})…");
            try
            {
                var again = scanner.Scan(t, input.RescanMode, ct);
                var foundBy = input.ReviewedMode is { } shown && shown != input.RescanMode
                    ? $"Found by the {input.RescanMode} check, deeper than your {shown} scan: " : "Found again: ";
                var removedLocations = new HashSet<string>(input.Removed.Select(r => r.Location), StringComparer.OrdinalIgnoreCase);
                foreach (var n in again.RegistryLeftovers.Concat(again.Files.Where(f => f.IsCreated)))
                {
                    // Items already reported above (deleted but back again) are not counted twice.
                    if (removedLocations.Contains(n.Location)) continue;
                    if (n.IsExcluded) continue;
                    // A key whose parent is also reported is covered by the parent.
                    if (n.Parent is { IsCreated: true } && n.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue) continue;
                    bool isKept = kept.Contains(n.Location) || kept.Any(k => IsWithin(n.Location, k));
                    report.Checks.Add(new(VerificationArea.Rescan, n.Location, isKept ? VerificationStatus.Kept : VerificationStatus.StillPresent,
                        isKept ? "You chose to keep this item." : $"{foundBy}{n.Reason ?? "belongs to the program"} ({n.Safety.ConfidenceText} confidence)."));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { report.RescanError = e.Message; }
        }

        report.Duration = sw.Elapsed;
        var remain = report.Checks.Where(c => c.Status == VerificationStatus.StillPresent).ToList();
        Logging.ActivityLog.Write(remain.Count > 0 ? Logging.LogLevel.Warning : Logging.LogLevel.Info, "Uninstaller",
            $"Verified removal of {t.DisplayName}: {report.Verdict}, {report.Count(VerificationStatus.Removed)} confirmed removed, {remain.Count} still present",
            string.Join("\n", report.Checks.Select(c => $"{c.Status}: {c.Location} – {c.Detail}")));
        return report;
    }

    internal static VerificationCheck CheckRemoved(RemovedItem r)
    {
        switch (r.Kind)
        {
            case LeftoverKind.RegistryKey when r.Key is not null:
                return r.Key.Exists()
                    ? new(VerificationArea.RegistryItem, r.Location, VerificationStatus.StillPresent, "The key exists again (re-created or not deleted).")
                    : new(VerificationArea.RegistryItem, r.Location, VerificationStatus.Removed, "Key deleted.");
            case LeftoverKind.RegistryValue when r.Key is not null:
                return ValueExists(r.Key, r.ValueName ?? "")
                    ? new(VerificationArea.RegistryItem, r.Location, VerificationStatus.StillPresent, "The value exists again (re-created or not deleted).")
                    : new(VerificationArea.RegistryItem, r.Location, VerificationStatus.Removed, "Value deleted.");
            case LeftoverKind.File or LeftoverKind.Folder when r.FsPath is not null:
                bool exists = File.Exists(r.FsPath) || Directory.Exists(r.FsPath);
                if (!exists)
                    return new(VerificationArea.FileItem, r.Location, VerificationStatus.Removed,
                        r.Outcome == DeleteOutcome.Recycled ? "Moved to the Recycle Bin." : "Deleted.");
                return r.Outcome == DeleteOutcome.ScheduledForReboot
                    ? new(VerificationArea.FileItem, r.Location, VerificationStatus.PendingRestart, "In use; Windows deletes it at the next restart.")
                    : new(VerificationArea.FileItem, r.Location, VerificationStatus.StillPresent, "Still on disk (re-created or not deleted).");
            default:
                return new(VerificationArea.FileItem, r.Location, VerificationStatus.StillPresent, "Could not be checked.");
        }
    }

    private static bool ValueExists(RegPath key, string name)
    {
        try
        {
            using var k = key.Open(writable: false);
            return k is not null && k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch { return true; }  // unreadable: do not claim it is gone
    }

    private static (int Files, long Bytes) CountFiles(string folder, CancellationToken ct)
    {
        int n = 0; long bytes = 0;
        try
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                n++; bytes += f.Length;
                if (n >= 100_000) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return (n, bytes);
    }

    private static List<ProcessInfo> SafeProcesses(string folder)
    {
        try { return ProcessUtil.FindUnderFolder(folder); } catch { return []; }
    }

    private static bool IsWithin(string location, string keptLocation) =>
        location.Length > keptLocation.Length && location.StartsWith(keptLocation, StringComparison.OrdinalIgnoreCase) && location[keptLocation.Length] == '\\';
}
