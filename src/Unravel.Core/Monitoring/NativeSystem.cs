using System.Runtime.InteropServices;

namespace Unravel.Core.Monitoring;

/// <summary>Raw per-process counters from one NtQuerySystemInformation(SystemProcessInformation) call.</summary>
public readonly record struct RawProcess(
    int Pid, int ParentPid, string Name, int SessionId, int Threads, int Handles,
    long CreateTimeFileTime, long CpuTime100ns, long WorkingSet, long PrivateBytes,
    long ReadBytes, long WriteBytes, long OtherBytes);

internal static class NativeSystem
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    // Natural (sequential) layout matches the native SYSTEM_PROCESS_INFORMATION on both 32- and 64-bit.
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private static int _bufferSize = 512 * 1024;

    public static List<RawProcess> QueryProcesses()
    {
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(_bufferSize);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, _bufferSize, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH) { _bufferSize = Math.Max(needed, _bufferSize * 2) + 64 * 1024; continue; }
                if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation failed (0x{status:X8}).");
                var list = new List<RawProcess>(400);
                long offset = 0;
                while (true)
                {
                    var p = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(buffer + (nint)offset);
                    int pid = (int)p.UniqueProcessId;
                    string name = pid == 0 ? "System Idle Process" :
                        p.ImageName.Buffer == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(p.ImageName.Buffer, p.ImageName.Length / 2);
                    list.Add(new RawProcess(pid, (int)p.InheritedFromUniqueProcessId, name, (int)p.SessionId, (int)p.NumberOfThreads, (int)p.HandleCount,
                        p.CreateTime, p.UserTime + p.KernelTime, (long)p.WorkingSetSize, (long)p.PrivatePageCount,
                        p.ReadTransferCount, p.WriteTransferCount, p.OtherTransferCount));
                    if (p.NextEntryOffset == 0) break;
                    offset += p.NextEntryOffset;
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    // ---- CPU and memory ----
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static MEMORYSTATUSEX Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return m;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessCritical(Microsoft.Win32.SafeHandles.SafeProcessHandle h, out bool critical);

    // ---- visible windows (to tell apps from background processes) ----
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);

    public static HashSet<int> ProcessesWithVisibleWindows()
    {
        var set = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLength(h) > 0)
            {
                GetWindowThreadProcessId(h, out int pid);
                set.Add(pid);
            }
            return true;
        }, IntPtr.Zero);
        return set;
    }
}

/// <summary>Minimal PDH (performance counter) query using English counter names, so it works on any UI language.</summary>
internal sealed class PdhCounters : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double doubleValue; }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private readonly IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();
    public bool Available { get; }

    public PdhCounters(params string[] paths)
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        foreach (var p in paths)
            if (PdhAddEnglishCounter(_query, p, IntPtr.Zero, out var c) == 0) _counters[p] = c;
        Available = _counters.Count > 0;
        if (Available) PdhCollectQueryData(_query); // rate counters need two samples
    }

    public Dictionary<string, double> Collect()
    {
        var values = new Dictionary<string, double>();
        if (!Available || PdhCollectQueryData(_query) != 0) return values;
        foreach (var (path, c) in _counters)
            if (PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE, IntPtr.Zero, out var v) == 0 && v.CStatus <= 1) values[path] = v.doubleValue;
        return values;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
    }
}
