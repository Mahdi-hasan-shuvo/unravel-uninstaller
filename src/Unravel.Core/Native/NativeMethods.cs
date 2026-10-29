using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Unravel.Core.Native;

internal static partial class NativeMethods
{
    // ---- Shell file operations (Recycle Bin) ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
    public const uint SHOP_FILEPATH = 0x2;

    // ---- Indirect (MUI) strings such as "@%SystemRoot%\system32\mstsc.exe,-4000" ----
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    public static string? LoadIndirectString(string source)
    {
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 ? sb.ToString() : null;
    }

    // ---- Registry key timestamp ----
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegQueryInfoKey(SafeRegistryHandle hKey, StringBuilder? lpClass, IntPtr lpcClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcMaxSubKeyLen, IntPtr lpcMaxClassLen, IntPtr lpcValues, IntPtr lpcMaxValueNameLen,
        IntPtr lpcMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    // ---- Delete on reboot ----
    public const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    // ---- Clipboard ----
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern int CountClipboardFormats();

    // ---- Processes ----
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle hProcess, int flags, StringBuilder exeName, ref int size);

    public const uint TH32CS_SNAPPROCESS = 0x2;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    // ---- Windows / hit testing (Hunter mode) ----
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

    // ---- Disk space ----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);

    public static string? GetProcessImagePath(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
    }

    /// <summary>Returns (pid, parentPid, exeName) for every process in a single snapshot.</summary>
    public static List<(int Pid, int ParentPid, string Exe)> SnapshotProcesses()
    {
        var list = new List<(int, int, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe))
                do list.Add(((int)pe.th32ProcessID, (int)pe.th32ParentProcessID, pe.szExeFile));
                while (Process32Next(snap, ref pe));
        }
        finally { CloseHandle(snap); }
        return list;
    }
}
