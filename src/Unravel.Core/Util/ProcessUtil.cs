using System.Diagnostics;
using System.Security.Principal;
using Unravel.Core.Native;

namespace Unravel.Core.Util;

public sealed record ProcessInfo(int Pid, int ParentPid, string Name, string? ImagePath);

public static class ProcessUtil
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static List<ProcessInfo> GetProcesses(bool withPaths = true) =>
        NativeMethods.SnapshotProcesses()
            .Select(p => new ProcessInfo(p.Pid, p.ParentPid, p.Exe, withPaths && p.Pid > 4 ? NativeMethods.GetProcessImagePath(p.Pid) : null))
            .ToList();

    public static List<ProcessInfo> FindByImagePath(string imagePath)
    {
        var target = FileOps.NormalizeDir(imagePath);
        var name = Path.GetFileName(target);
        return NativeMethods.SnapshotProcesses()
            .Where(p => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ProcessInfo(p.Pid, p.ParentPid, p.Exe, NativeMethods.GetProcessImagePath(p.Pid)))
            .Where(p => p.ImagePath is not null && p.ImagePath.Equals(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static List<ProcessInfo> FindUnderFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        return GetProcesses().Where(p => p.ImagePath is not null && FileOps.IsSameOrUnder(p.ImagePath, folder)).ToList();
    }

    public static bool IsRunning(string imagePath) => FindByImagePath(imagePath).Count > 0;

    /// <summary>Kills every process with the given image path. Returns (killed, failed) counts.</summary>
    public static (int Killed, int Failed) KillByImagePath(string imagePath) => Kill(FindByImagePath(imagePath));

    public static (int Killed, int Failed) Kill(IEnumerable<ProcessInfo> processes)
    {
        int ok = 0, fail = 0;
        foreach (var p in processes)
        {
            if (p.Pid == Environment.ProcessId) continue;
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                proc.Kill(entireProcessTree: false);
                proc.WaitForExit(3000);
                ok++;
            }
            catch (ArgumentException) { ok++; } // already gone
            catch { fail++; }
        }
        return (ok, fail);
    }

    /// <summary>
    /// Waits for a process and every descendant it spawns. Many uninstallers copy themselves to %TEMP% and
    /// relaunch, so waiting only for the first process would return too early.
    /// </summary>
    public static async Task WaitForProcessTreeAsync(int rootPid, CancellationToken ct, TimeSpan? pollInterval = null)
    {
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var trackingStarted = DateTime.Now.AddSeconds(-5);
        var tracked = new HashSet<int> { rootPid };
        var rejected = new HashSet<int>();
        while (!ct.IsCancellationRequested)
        {
            var snapshot = NativeMethods.SnapshotProcesses();
            var alive = snapshot.Select(p => p.Pid).ToHashSet();
            // Adopt descendants until closure. A child is adopted only if it started after tracking began,
            // which guards against an unrelated process inheriting a recycled parent PID.
            bool added;
            do
            {
                added = false;
                foreach (var p in snapshot)
                {
                    if (tracked.Contains(p.Pid) || rejected.Contains(p.Pid) || !tracked.Contains(p.ParentPid)) continue;
                    if (StartedAfter(p.Pid, trackingStarted)) { tracked.Add(p.Pid); added = true; }
                    else rejected.Add(p.Pid);
                }
            } while (added);
            if (!tracked.Any(alive.Contains)) return;
            try { await Task.Delay(poll, ct); } catch (TaskCanceledException) { return; }
        }
    }

    private static bool StartedAfter(int pid, DateTime time)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime >= time;
        }
        catch (System.ComponentModel.Win32Exception) { return true; } // elevated child: cannot read start time, assume ours
        catch { return false; }
    }
}
