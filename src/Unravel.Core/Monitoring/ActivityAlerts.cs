using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Unravel.Core.Logging;
using Unravel.Core.Native;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Core.Monitoring;

public enum AlertSeverity { Info, Warning, Critical }

public enum AlertRule { SustainedCpu, MemoryGrowth, HighMemory, SustainedIo, UnsignedFromTemp, Masquerading, SystemCpu, LowMemory }

public sealed record ActivityAlert(AlertRule Rule, AlertSeverity Severity, string Title, string Details, int? Pid, string? ProcessName, string? ImagePath, DateTime Raised)
{
    public string Key => $"{Rule}:{Pid}";
    public string Advice => Rule switch
    {
        AlertRule.SustainedCpu => "If you don't recognise the program or it shouldn't be busy, end it and check its publisher.",
        AlertRule.MemoryGrowth => "Steadily growing memory often means a leak. Saving your work and restarting the program usually fixes it.",
        AlertRule.HighMemory => "Close the program if you don't need it to free memory for others.",
        AlertRule.SustainedIo => "Heavy disk activity slows the PC. Check whether this program is supposed to be copying, indexing or updating.",
        AlertRule.UnsignedFromTemp => "Unsigned programs running from temporary or download folders are a common malware pattern. Scan it with Windows Security.",
        AlertRule.Masquerading => "A program using a Windows system file name outside the Windows folder is very suspicious. Scan your PC with Windows Security now.",
        AlertRule.SystemCpu => "Look for the busiest process in the list below.",
        AlertRule.LowMemory => "Close programs you aren't using, or check for a process using a lot of memory.",
        _ => "",
    };
}

public sealed class AlertThresholds
{
    public double CpuPercent { get; init; } = 25;           // of total capacity, per process
    public int CpuSustainSeconds { get; init; } = 30;
    public long MemoryGrowthBytes { get; init; } = 300L * 1024 * 1024;
    public int MemoryWindowSeconds { get; init; } = 60;
    public double HighMemoryFraction { get; init; } = 0.25;
    public double IoBytesPerSec { get; init; } = 50.0 * 1024 * 1024;
    public int IoSustainSeconds { get; init; } = 30;
    public double SystemCpuPercent { get; init; } = 90;
    public double LowMemoryAvailableFraction { get; init; } = 0.10;
}

/// <summary>
/// Turns a stream of samples into actionable alerts: sustained CPU or I/O, steady memory growth, unusual memory
/// use, unsigned programs running from temp/download folders, and programs impersonating Windows system files.
/// </summary>
public sealed class ActivityAlertEngine
{
    private readonly AlertThresholds _t;
    private readonly Dictionary<(int, DateTime), List<(DateTime Time, double Cpu, long Private, double Io)>> _history = new();
    private readonly List<(DateTime Time, double Cpu)> _systemCpu = new();
    private readonly Dictionary<string, ActivityAlert> _active = new();
    private readonly HashSet<string> _ignored = new();
    private readonly HashSet<(int, DateTime)> _checkedLocations = new();
    private readonly Func<string, SignatureInfo> _signature;

    public ActivityAlertEngine(AlertThresholds? thresholds = null, Func<string, SignatureInfo>? signatureCheck = null)
    {
        _t = thresholds ?? new AlertThresholds();
        _signature = signatureCheck ?? FileSignature.Check;
    }

    public IReadOnlyCollection<ActivityAlert> Active => _active.Values.Where(a => !_ignored.Contains(a.Key)).OrderByDescending(a => a.Severity).ThenByDescending(a => a.Raised).ToList();

    /// <summary>Hides an alert until the condition clears and comes back.</summary>
    public void Ignore(ActivityAlert a) => _ignored.Add(a.Key);

    /// <summary>Processes one sample; returns alerts raised for the first time in this sample.</summary>
    public List<ActivityAlert> Evaluate(SystemSample s)
    {
        var raised = new List<ActivityAlert>();
        var stillActive = new HashSet<string>();
        void Raise(ActivityAlert a)
        {
            stillActive.Add(a.Key);
            if (_active.ContainsKey(a.Key)) return;
            _active[a.Key] = a;
            raised.Add(a);
            ActivityLog.Warning("Monitor", $"{a.Severity}: {a.Title}", a.Details);
        }

        // System-wide
        _systemCpu.Add((s.Time, s.CpuPercent));
        _systemCpu.RemoveAll(x => (s.Time - x.Time).TotalSeconds > _t.CpuSustainSeconds + 2);
        if (Covers(_systemCpu.Select(x => x.Time), _t.CpuSustainSeconds) && _systemCpu.All(x => x.Cpu >= _t.SystemCpuPercent))
            Raise(new(AlertRule.SystemCpu, AlertSeverity.Warning, $"CPU has been above {_t.SystemCpuPercent:0}% for {_t.CpuSustainSeconds} seconds", $"Average {_systemCpu.Average(x => x.Cpu):0}%.", null, null, null, s.Time));
        if (s.MemoryTotal > 0 && s.MemoryAvailable < s.MemoryTotal * _t.LowMemoryAvailableFraction)
            Raise(new(AlertRule.LowMemory, AlertSeverity.Warning, "Memory is almost full", $"Only {Format.Bytes((long)s.MemoryAvailable)} of {Format.Bytes((long)s.MemoryTotal)} is available.", null, null, null, s.Time));

        var alive = new HashSet<(int, DateTime)>();
        foreach (var p in s.Processes)
        {
            if (p.Pid <= 4) continue;
            var key = (p.Pid, p.StartTime);
            alive.Add(key);
            if (!_history.TryGetValue(key, out var h)) _history[key] = h = new();
            h.Add((s.Time, p.CpuPercent, p.PrivateBytes, p.IoBytesPerSec));
            var keep = Math.Max(_t.MemoryWindowSeconds, Math.Max(_t.CpuSustainSeconds, _t.IoSustainSeconds)) + 5;
            h.RemoveAll(x => (s.Time - x.Time).TotalSeconds > keep);

            var cpuWindow = h.Where(x => (s.Time - x.Time).TotalSeconds <= _t.CpuSustainSeconds).ToList();
            if (Covers(cpuWindow.Select(x => x.Time), _t.CpuSustainSeconds - 1) && cpuWindow.All(x => x.Cpu >= _t.CpuPercent))
            {
                var avg = cpuWindow.Average(x => x.Cpu);
                Raise(new(AlertRule.SustainedCpu, avg >= 80 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    $"{p.Name} is using {avg:0}% CPU", $"Above {_t.CpuPercent:0}% of all processors for {_t.CpuSustainSeconds} seconds.", p.Pid, p.Name, p.ImagePath, s.Time));
            }

            var memWindow = h.Where(x => (s.Time - x.Time).TotalSeconds <= _t.MemoryWindowSeconds).ToList();
            if (Covers(memWindow.Select(x => x.Time), _t.MemoryWindowSeconds - 1) && memWindow.Count >= 5)
            {
                var growth = memWindow[^1].Private - memWindow[0].Private;
                int rising = 0;
                for (int i = 1; i < memWindow.Count; i++) if (memWindow[i].Private >= memWindow[i - 1].Private) rising++;
                if (growth >= _t.MemoryGrowthBytes && rising >= 0.8 * (memWindow.Count - 1))
                    Raise(new(AlertRule.MemoryGrowth, AlertSeverity.Warning, $"{p.Name}'s memory keeps growing",
                        $"Grew by {Format.Bytes(growth)} in {_t.MemoryWindowSeconds} seconds (now {Format.Bytes(p.PrivateBytes)}).", p.Pid, p.Name, p.ImagePath, s.Time));
            }

            if (s.MemoryTotal > 0 && p.PrivateBytes > (long)(s.MemoryTotal * _t.HighMemoryFraction))
                Raise(new(AlertRule.HighMemory, AlertSeverity.Warning, $"{p.Name} is using {Format.Bytes(p.PrivateBytes)} of memory",
                    $"That is more than {_t.HighMemoryFraction:P0} of installed memory.", p.Pid, p.Name, p.ImagePath, s.Time));

            var ioWindow = h.Where(x => (s.Time - x.Time).TotalSeconds <= _t.IoSustainSeconds).ToList();
            if (Covers(ioWindow.Select(x => x.Time), _t.IoSustainSeconds - 1) && ioWindow.All(x => x.Io >= _t.IoBytesPerSec))
                Raise(new(AlertRule.SustainedIo, AlertSeverity.Warning, $"{p.Name} is reading/writing {Format.Bytes((long)ioWindow.Average(x => x.Io))}/s",
                    $"Sustained for {_t.IoSustainSeconds} seconds.", p.Pid, p.Name, p.ImagePath, s.Time));

            // The location check is done once per process instance (signature checks are not free).
            if (p.ImagePath is { } path && _checkedLocations.Add(key))
            {
                if (IsMasquerading(p.Name, path))
                    Raise(new(AlertRule.Masquerading, AlertSeverity.Critical, $"{p.Name} is running from an unusual location",
                        $"A Windows system file name, but it runs from {Path.GetDirectoryName(path)} instead of the Windows folder.", p.Pid, p.Name, path, s.Time));
                else if (IsTempLocation(path) && !_signature(path).IsTrusted)
                    Raise(new(AlertRule.UnsignedFromTemp, AlertSeverity.Warning, $"Unsigned program running from {FolderLabel(path)}",
                        path, p.Pid, p.Name, path, s.Time));
            }
            // Location alerts stay active for as long as the process runs.
            foreach (var rule in new[] { AlertRule.Masquerading, AlertRule.UnsignedFromTemp })
                if (_active.ContainsKey($"{rule}:{p.Pid}")) stillActive.Add($"{rule}:{p.Pid}");
        }
        foreach (var dead in _history.Keys.Where(k => !alive.Contains(k)).ToList()) _history.Remove(dead);
        _checkedLocations.RemoveWhere(k => !alive.Contains(k));

        // Conditions that cleared resolve their alerts (and un-ignore them for next time).
        foreach (var k in _active.Keys.Where(k => !stillActive.Contains(k)).ToList())
        {
            _active.Remove(k);
            _ignored.Remove(k);
        }
        return raised;
    }

    private static bool Covers(IEnumerable<DateTime> times, int seconds)
    {
        var list = times.ToList();
        return list.Count >= 2 && (list[^1] - list[0]).TotalSeconds >= seconds;
    }

    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "csrss.exe", "lsass.exe", "winlogon.exe", "services.exe", "smss.exe", "wininit.exe", "dwm.exe",
        "spoolsv.exe", "taskhostw.exe", "conhost.exe", "rundll32.exe", "lsm.exe", "dllhost.exe", "sihost.exe", "ctfmon.exe",
    };

    public static bool IsMasquerading(string name, string path)
    {
        if (!SystemNames.Contains(name)) return false;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !FileOps.IsSameOrUnder(path, win);
    }

    public static bool IsTempLocation(string path)
    {
        var p = path.ToLowerInvariant();
        var temp = Path.GetTempPath().TrimEnd('\\').ToLowerInvariant();
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads").ToLowerInvariant();
        return p.StartsWith(temp + "\\") || p.StartsWith(downloads + "\\") || p.Contains(@"\$recycle.bin\") || p.Contains(@"\users\public\") || p.Contains(@"\windows\temp\");
    }

    private static string FolderLabel(string path) =>
        path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase) ? "the Downloads folder" : "a temporary folder";
}

/// <summary>Protects processes Windows cannot run without, and ends others safely.</summary>
public static class ProcessGuard
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Registry", "Secure System", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe",
        "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "dwm.exe", "svchost.exe", "MsMpEng.exe",
    };

    /// <summary>True for processes whose termination would crash or destabilise Windows.</summary>
    public static bool IsCritical(int pid, string name, string? path)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return true;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (CriticalNames.Contains(name) && (path is null || FileOps.IsSameOrUnder(path, win))) return true;
        try
        {
            using var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (!h.IsInvalid && NativeSystem.IsProcessCritical(h, out bool critical) && critical) return true;
        }
        catch { }
        return false;
    }

    public static string? WarningFor(string name) =>
        name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            ? "Ending Explorer removes the taskbar and desktop until it is restarted." : null;

    /// <summary>Ends a process unless it is critical. Throws with a clear message when it cannot.</summary>
    public static void End(int pid, string name, string? path, bool tree = false)
    {
        if (IsCritical(pid, name, path)) throw new InvalidOperationException($"{name} is a critical Windows process and cannot be ended.");
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(tree);
            p.WaitForExit(5000);
            ActivityLog.Action("Monitor", $"Ended {name} (PID {pid})", path);
        }
        catch (ArgumentException) { /* already exited */ }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 5)
        {
            throw new UnauthorizedAccessException($"Access denied ending {name}. It may belong to another user or need administrator rights.", e);
        }
    }
}
