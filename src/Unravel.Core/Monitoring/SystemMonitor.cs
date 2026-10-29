using System.Collections.Concurrent;
using Unravel.Core.Native;

namespace Unravel.Core.Monitoring;

public sealed class ProcessSample
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public required string Name { get; init; }
    public string? ImagePath { get; init; }
    public int SessionId { get; init; }
    public DateTime StartTime { get; init; }
    /// <summary>Share of total CPU capacity (all cores) in percent.</summary>
    public double CpuPercent { get; init; }
    public TimeSpan CpuTime { get; init; }
    public long WorkingSet { get; init; }
    public long PrivateBytes { get; init; }
    public double ReadBytesPerSec { get; init; }
    public double WriteBytesPerSec { get; init; }
    public long TotalReadBytes { get; init; }
    public long TotalWriteBytes { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public double IoBytesPerSec => ReadBytesPerSec + WriteBytesPerSec;
}

public sealed class SystemSample
{
    public DateTime Time { get; init; }
    public double CpuPercent { get; init; }
    public ulong MemoryTotal { get; init; }
    public ulong MemoryAvailable { get; init; }
    public ulong CommitTotal { get; init; }
    public ulong CommitLimit { get; init; }
    /// <summary>Physical disk throughput from performance counters (null if unavailable).</summary>
    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }
    public double? DiskActivePercent { get; init; }
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public required IReadOnlyList<ProcessSample> Processes { get; init; }
    public double MemoryUsedPercent => MemoryTotal == 0 ? 0 : 100.0 * (MemoryTotal - MemoryAvailable) / MemoryTotal;
}

/// <summary>
/// Samples system-wide and per-process CPU, memory and I/O. Each call to <see cref="Sample"/> reports rates since
/// the previous call; the first call establishes the baseline (rates are zero).
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private long _lastIdle, _lastKernel, _lastUser;
    private DateTime _lastTime;
    private Dictionary<(int Pid, long Create), RawProcess> _last = new();
    private readonly ConcurrentDictionary<(int, long), string?> _paths = new();
    private readonly PdhCounters _pdh;
    private readonly object _gate = new();
    private const string DiskRead = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private const string DiskIdle = @"\PhysicalDisk(_Total)\% Idle Time";

    public SystemMonitor()
    {
        _pdh = new PdhCounters(DiskRead, DiskWrite, DiskIdle);
        NativeSystem.GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
        _lastTime = DateTime.UtcNow;
        foreach (var p in NativeSystem.QueryProcesses()) _last[(p.Pid, p.CreateTimeFileTime)] = p;
    }

    public static DateTime BootTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public SystemSample Sample()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var seconds = Math.Max(0.001, (now - _lastTime).TotalSeconds);
            NativeSystem.GetSystemTimes(out var idle, out var kernel, out var user);
            long busyTotal = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle time
            long idleDelta = idle - _lastIdle;
            double cpu = busyTotal <= 0 ? 0 : Math.Clamp(100.0 * (busyTotal - idleDelta) / busyTotal, 0, 100);
            // Total CPU capacity in 100 ns units over the interval, for per-process percentages.
            double capacity = busyTotal > 0 ? busyTotal : seconds * 1e7 * Environment.ProcessorCount;
            (_lastIdle, _lastKernel, _lastUser, _lastTime) = (idle, kernel, user, now);

            var raw = NativeSystem.QueryProcesses();
            var current = new Dictionary<(int, long), RawProcess>(raw.Count);
            var samples = new List<ProcessSample>(raw.Count);
            foreach (var p in raw)
            {
                var key = (p.Pid, p.CreateTimeFileTime);
                current[key] = p;
                _last.TryGetValue(key, out var prev);
                bool known = prev.CreateTimeFileTime == p.CreateTimeFileTime && prev.Pid == p.Pid && prev.Name is not null;
                double cpuPct = !known || p.Pid == 0 ? 0 : Math.Clamp(100.0 * (p.CpuTime100ns - prev.CpuTime100ns) / capacity, 0, 100);
                samples.Add(new ProcessSample
                {
                    Pid = p.Pid, ParentPid = p.ParentPid, Name = p.Name, SessionId = p.SessionId,
                    ImagePath = p.Pid <= 4 ? null : _paths.GetOrAdd(key, _ => NativeMethods.GetProcessImagePath(p.Pid)),
                    StartTime = p.CreateTimeFileTime > 0 ? DateTime.FromFileTime(p.CreateTimeFileTime) : BootTime,
                    CpuPercent = cpuPct, CpuTime = TimeSpan.FromTicks(p.CpuTime100ns),
                    WorkingSet = p.WorkingSet, PrivateBytes = p.PrivateBytes,
                    ReadBytesPerSec = known ? Math.Max(0, (p.ReadBytes - prev.ReadBytes) / seconds) : 0,
                    WriteBytesPerSec = known ? Math.Max(0, (p.WriteBytes - prev.WriteBytes) / seconds) : 0,
                    TotalReadBytes = p.ReadBytes, TotalWriteBytes = p.WriteBytes,
                    Threads = p.Threads, Handles = p.Handles,
                });
            }
            foreach (var gone in _last.Keys.Where(k => !current.ContainsKey(k))) _paths.TryRemove(gone, out _);
            _last = current;

            var mem = NativeSystem.Memory();
            var disk = _pdh.Collect();
            return new SystemSample
            {
                Time = DateTime.Now, CpuPercent = cpu,
                MemoryTotal = mem.ullTotalPhys, MemoryAvailable = mem.ullAvailPhys,
                CommitTotal = mem.ullTotalPageFile - mem.ullAvailPageFile, CommitLimit = mem.ullTotalPageFile,
                DiskReadBytesPerSec = disk.TryGetValue(DiskRead, out var r) ? r : null,
                DiskWriteBytesPerSec = disk.TryGetValue(DiskWrite, out var w) ? w : null,
                DiskActivePercent = disk.TryGetValue(DiskIdle, out var idlePct) ? Math.Clamp(100 - idlePct, 0, 100) : null,
                ProcessCount = samples.Count, ThreadCount = samples.Sum(s => s.Threads), HandleCount = samples.Sum(s => s.Handles),
                Processes = samples,
            };
        }
    }

    public void Dispose() => _pdh.Dispose();
}
