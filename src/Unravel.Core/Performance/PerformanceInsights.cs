using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unravel.Core.Autorun;
using Unravel.Core.Logging;
using Unravel.Core.Monitoring;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Core.Performance;

public enum StartupImpact { NotMeasured, None, Low, Medium, High }

public sealed record StartupImpactItem(AutorunEntry Entry, StartupImpact Impact, TimeSpan CpuTime, long DiskBytes, long MemoryBytes, int ProcessCount, string Basis);

public sealed record BootInfo(DateTime LastBoot, TimeSpan Uptime, TimeSpan? BootDuration, TimeSpan? MainPathDuration, string? Note);

public enum ProcessGroup { Apps, Background, Windows }

public sealed record ProcessGroupSummary(ProcessGroup Group, int Count, double CpuPercent, long MemoryBytes, double IoBytesPerSec, IReadOnlyList<ProcessSample> Top);

/// <summary>Startup impact, boot timing and background process breakdown.</summary>
public static class PerformanceInsights
{
    /// <summary>
    /// Windows Task Manager's published impact thresholds: High = more than 1 s CPU or 3 MB disk I/O at startup,
    /// Medium = 300 ms–1 s CPU or 300 KB–3 MB I/O, Low = less than that.
    /// </summary>
    public static StartupImpact Classify(TimeSpan cpu, long diskBytes)
    {
        if (cpu.TotalMilliseconds > 1000 || diskBytes > 3L * 1024 * 1024) return StartupImpact.High;
        if (cpu.TotalMilliseconds >= 300 || diskBytes >= 300L * 1024) return StartupImpact.Medium;
        if (cpu.TotalMilliseconds > 0 || diskBytes > 0) return StartupImpact.Low;
        return StartupImpact.None;
    }

    /// <summary>
    /// Measures each enabled startup entry from the processes it started at sign-in. Counters cover the process's
    /// whole life, so for long-running programs the figure is an upper bound of their startup cost.
    /// </summary>
    public static List<StartupImpactItem> AnalyzeStartup(IEnumerable<AutorunEntry> entries, SystemSample sample, TimeSpan? startupWindow = null)
    {
        var window = startupWindow ?? TimeSpan.FromMinutes(5);
        var boot = SystemMonitor.BootTime;
        var signIn = SignInTime(sample) ?? boot;
        var list = new List<StartupImpactItem>();
        foreach (var e in entries)
        {
            if (!e.Enabled || e.TargetPath is null)
            {
                list.Add(new(e, StartupImpact.NotMeasured, TimeSpan.Zero, 0, 0, 0, e.Enabled ? "No program file" : "Disabled"));
                continue;
            }
            // Without administrator rights Windows hides the path of SYSTEM service processes; fall back to the file name.
            var fileName = Path.GetFileName(e.TargetPath);
            var procs = sample.Processes.Where(p => p.ImagePath is not null
                ? p.ImagePath.Equals(e.TargetPath, StringComparison.OrdinalIgnoreCase)
                : p.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (procs.Count == 0)
            {
                list.Add(new(e, StartupImpact.NotMeasured, TimeSpan.Zero, 0, 0, 0, "Not running now – it may have exited after starting"));
                continue;
            }
            // Services start with Windows; everything else starts when the user signs in.
            var start = e.Source == AutorunSource.Service ? boot : signIn;
            var atStartup = procs.Where(p => (p.StartTime - start).Duration() <= window).ToList();
            var basis = atStartup.Count > 0
                ? (e.Source == AutorunSource.Service ? "Measured since Windows started" : "Measured since sign-in")
                : "Restarted after sign-in; measured since it started";
            var used = atStartup.Count > 0 ? atStartup : procs;
            var cpu = TimeSpan.FromTicks(used.Sum(p => p.CpuTime.Ticks));
            var disk = used.Sum(p => p.TotalReadBytes + p.TotalWriteBytes);
            list.Add(new(e, Classify(cpu, disk), cpu, disk, used.Sum(p => p.PrivateBytes), used.Count, basis));
        }
        return list.OrderByDescending(i => i.Impact).ThenByDescending(i => i.CpuTime).ToList();
    }

    /// <summary>
    /// When the current user signed in: the Shell Infrastructure Host (sihost.exe) starts with the session and is rarely
    /// restarted, unlike Explorer.
    /// </summary>
    public static DateTime? SignInTime(SystemSample s)
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var host = s.Processes.Where(p => p.SessionId == session && p.Name.Equals("sihost.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.StartTime).ToList();
        if (host.Count > 0) return host.Min();
        var explorer = s.Processes.Where(p => p.SessionId == session && p.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.StartTime).ToList();
        return explorer.Count > 0 ? explorer.Min() : null;
    }

    /// <summary>Last boot from uptime; boot duration from the Diagnostics-Performance log (needs administrator rights).</summary>
    public static BootInfo GetBootInfo()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var lastBoot = DateTime.Now - uptime;
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var ev = reader.ReadEvent();
            if (ev is null) return new(lastBoot, uptime, null, null, "Windows has not recorded a boot measurement yet.");
            var xml = ev.ToXml();
            TimeSpan? Read(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(xml, $"<Data Name='{name}'>(\\d+)</Data>");
                return m.Success ? TimeSpan.FromMilliseconds(long.Parse(m.Groups[1].Value)) : null;
            }
            return new(lastBoot, uptime, Read("BootTime"), Read("MainPathBootTime"), $"Measured by Windows on {ev.TimeCreated:g}.");
        }
        catch (UnauthorizedAccessException) { return new(lastBoot, uptime, null, null, "Boot duration needs administrator rights to read Windows' performance log."); }
        catch (EventLogException e) { return new(lastBoot, uptime, null, null, "Boot duration is not available: " + e.Message); }
    }

    /// <summary>Apps have a visible window; Windows processes run in session 0 or from the Windows folder; the rest is background.</summary>
    public static List<(ProcessSample Process, ProcessGroup Group)> ClassifyProcesses(SystemSample s)
    {
        var withWindows = NativeSystem.ProcessesWithVisibleWindows();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return s.Processes.Where(p => p.Pid != 0).Select(p => (p,
            withWindows.Contains(p.Pid) ? ProcessGroup.Apps :
            p.SessionId == 0 || p.Pid <= 4 || (p.ImagePath is { } path && FileOps.IsSameOrUnder(path, win)) ? ProcessGroup.Windows :
            ProcessGroup.Background)).ToList();
    }

    /// <summary>Splits processes into apps (with a window), background programs and Windows processes.</summary>
    public static List<ProcessGroupSummary> GroupProcesses(SystemSample s) => Summarize(ClassifyProcesses(s));

    public static List<ProcessGroupSummary> Summarize(List<(ProcessSample Process, ProcessGroup Group)> classified)
    {
        return classified.GroupBy(x => x.Group, x => x.Process)
            .Select(g => new ProcessGroupSummary(g.Key, g.Count(), g.Sum(p => p.CpuPercent), g.Sum(p => p.PrivateBytes), g.Sum(p => p.IoBytesPerSec),
                g.OrderByDescending(p => p.PrivateBytes).Take(8).ToList()))
            .OrderBy(g => g.Group).ToList();
    }
}

// ---------------------------------------------------------------------- disk usage

public sealed class FolderUsage
{
    public required string Path { get; init; }
    public long Size { get; set; }
    public int Files { get; set; }
    public List<FolderUsage> Children { get; } = [];
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
}

public sealed record LargeFile(string Path, long Size, DateTime LastWrite);

public sealed class DiskUsageResult
{
    public required FolderUsage Root { get; init; }
    public List<LargeFile> LargestFiles { get; init; } = [];
    public int Inaccessible { get; set; }
    public bool Cancelled { get; set; }
}

/// <summary>Measures folder sizes under a root (children sorted by size) and finds the largest files.</summary>
public static class DiskUsageScanner
{
    public static DiskUsageResult Scan(string root, int keepDepth = 2, int topFiles = 50, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rootNode = new FolderUsage { Path = root };
        var result = new DiskUsageResult { Root = rootNode };
        var largest = new PriorityQueue<LargeFile, long>();
        var last = DateTime.MinValue;

        long Walk(string dir, FolderUsage? node, int depth)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; return 0; }
            long total = 0;
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
            catch { result.Inaccessible++; return 0; }
            if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { progress?.Report(dir); last = DateTime.UtcNow; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never count junction targets twice
                if (e is DirectoryInfo d)
                {
                    FolderUsage? child = depth < keepDepth ? new FolderUsage { Path = d.FullName } : null;
                    var size = Walk(d.FullName, child, depth + 1);
                    if (child is not null) { child.Size = size; node!.Children.Add(child); }
                    total += size;
                }
                else if (e is FileInfo f)
                {
                    long len;
                    try { len = f.Length; } catch { continue; }
                    total += len;
                    if (node is not null) node.Files++;
                    largest.Enqueue(new LargeFile(f.FullName, len, f.LastWriteTime), len);
                    if (largest.Count > topFiles) largest.Dequeue();
                }
            }
            if (node is not null) node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            return total;
        }

        rootNode.Size = Walk(root, rootNode, 0);
        var files = new List<LargeFile>();
        while (largest.Count > 0) files.Add(largest.Dequeue());
        files.Reverse();
        result.LargestFiles.AddRange(files);
        return result;
    }
}

// ---------------------------------------------------------------------- before / after baselines

public sealed class PerformanceBaseline
{
    public string Label { get; set; } = "";
    public DateTime Captured { get; set; }
    public double? BootSeconds { get; set; }
    public int EnabledStartupItems { get; set; }
    public int HighImpactStartupItems { get; set; }
    public double AverageCpuPercent { get; set; }
    public double MemoryUsedPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long CommitBytes { get; set; }
    public int ProcessCount { get; set; }
    public int BackgroundProcessCount { get; set; }
    public int ThreadCount { get; set; }
    public int RunningServices { get; set; }
    public long SystemDriveFreeBytes { get; set; }
    public double UptimeMinutes { get; set; }
}

public sealed record MetricDelta(string Metric, string Before, string After, string Change, bool? Better);

/// <summary>Captures measurable system state so changes (e.g. disabling startup items) can be compared after a restart.</summary>
public sealed class BaselineStore
{
    public string FilePath { get; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public BaselineStore(string? file = null)
    {
        FilePath = file ?? Path.Combine(Settings.SettingsStore.DefaultFolder, "baselines.json");
    }

    public List<PerformanceBaseline> List()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<PerformanceBaseline>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    public void Add(PerformanceBaseline b)
    {
        var all = List();
        all.Add(b);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
        ActivityLog.Info("Performance", $"Baseline \"{b.Label}\" saved");
    }

    public void Remove(PerformanceBaseline b)
    {
        var all = List().Where(x => !(x.Captured == b.Captured && x.Label == b.Label)).ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
    }

    /// <summary>Averages CPU over <paramref name="seconds"/> one-second samples, then records everything else once.</summary>
    public static async Task<PerformanceBaseline> CaptureAsync(string label, int seconds, IProgress<string>? progress, CancellationToken ct)
    {
        using var monitor = new SystemMonitor();
        var cpu = new List<double>();
        SystemSample? last = null;
        for (int i = 0; i < seconds; i++)
        {
            await Task.Delay(1000, ct);
            last = monitor.Sample();
            cpu.Add(last.CpuPercent);
            progress?.Report($"Measuring… {i + 1}/{seconds} s (CPU {last.CpuPercent:0}%)");
        }
        last ??= monitor.Sample();
        progress?.Report("Reading startup items and services…");
        var startup = await Task.Run(() => new AutorunManager(AutorunEnvironment.CreateDefault()).Load(), ct);
        var impact = PerformanceInsights.AnalyzeStartup(startup, last);
        var boot = PerformanceInsights.GetBootInfo();
        var groups = PerformanceInsights.GroupProcesses(last);
        var sysDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        return new PerformanceBaseline
        {
            Label = label, Captured = DateTime.Now,
            BootSeconds = boot.BootDuration?.TotalSeconds,
            EnabledStartupItems = startup.Count(e => e.Enabled),
            HighImpactStartupItems = impact.Count(i => i.Impact == StartupImpact.High),
            AverageCpuPercent = cpu.Count == 0 ? last.CpuPercent : cpu.Average(),
            MemoryUsedPercent = last.MemoryUsedPercent,
            MemoryUsedBytes = (long)(last.MemoryTotal - last.MemoryAvailable),
            CommitBytes = (long)last.CommitTotal,
            ProcessCount = last.ProcessCount,
            BackgroundProcessCount = groups.FirstOrDefault(g => g.Group == ProcessGroup.Background)?.Count ?? 0,
            ThreadCount = last.ThreadCount,
            RunningServices = ServicesManager.List().Count(s => s.IsRunning),
            SystemDriveFreeBytes = sysDrive.AvailableFreeSpace,
            UptimeMinutes = boot.Uptime.TotalMinutes,
        };
    }

    public static List<MetricDelta> Compare(PerformanceBaseline a, PerformanceBaseline b)
    {
        var list = new List<MetricDelta>();
        void Num(string name, double? x, double? y, Func<double, string> fmt, bool lowerIsBetter = true)
        {
            if (x is null || y is null) { list.Add(new(name, x is null ? "—" : fmt(x.Value), y is null ? "—" : fmt(y.Value), "not available", null)); return; }
            var d = y.Value - x.Value;
            var pct = Math.Abs(x.Value) > 1e-9 ? $" ({d / x.Value:+0%;-0%;0%})" : "";
            bool? better = Math.Abs(d) < 1e-9 ? null : (d < 0) == lowerIsBetter;
            list.Add(new(name, fmt(x.Value), fmt(y.Value), (d >= 0 ? "+" : "−") + fmt(Math.Abs(d)) + pct, better));
        }
        Num("Boot duration", a.BootSeconds, b.BootSeconds, v => $"{v:0.0} s");
        Num("Enabled startup items", a.EnabledStartupItems, b.EnabledStartupItems, v => $"{v:0}");
        Num("High-impact startup items", a.HighImpactStartupItems, b.HighImpactStartupItems, v => $"{v:0}");
        Num("Average CPU", a.AverageCpuPercent, b.AverageCpuPercent, v => $"{v:0.0}%");
        Num("Memory in use", a.MemoryUsedBytes, b.MemoryUsedBytes, v => Format.Bytes((long)v));
        Num("Committed memory", a.CommitBytes, b.CommitBytes, v => Format.Bytes((long)v));
        Num("Processes", a.ProcessCount, b.ProcessCount, v => $"{v:0}");
        Num("Background processes", a.BackgroundProcessCount, b.BackgroundProcessCount, v => $"{v:0}");
        Num("Threads", a.ThreadCount, b.ThreadCount, v => $"{v:0}");
        Num("Running services", a.RunningServices, b.RunningServices, v => $"{v:0}");
        Num("Free space on system drive", a.SystemDriveFreeBytes, b.SystemDriveFreeBytes, v => Format.Bytes((long)v), lowerIsBetter: false);
        return list;
    }
}
