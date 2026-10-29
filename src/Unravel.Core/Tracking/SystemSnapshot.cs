using System.IO.Compression;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Unravel.Core.Autorun;
using Unravel.Core.Util;

namespace Unravel.Core.Tracking;

public readonly record struct FileRecord(long Size, long WriteTicks, bool IsDirectory);
public sealed record ServiceRecord(string DisplayName, string? ImagePath, string StartType);
public sealed record TaskRecord(bool Enabled, string Actions, ulong XmlHash);
public sealed record StartupRecord(string Command, bool Enabled);

public sealed class SnapshotOptions
{
    public List<string> FileRoots { get; init; } = [];
    public List<RegPath> RegistryRoots { get; init; } = [];
    public bool Services { get; init; } = true;
    public bool Tasks { get; init; } = true;
    public bool Startup { get; init; } = true;
    /// <summary>Wildcard patterns for paths that change constantly (caches, temp, logs) and only add noise.</summary>
    public List<string> FileNoise { get; init; } = [];
    public List<string> RegistryNoise { get; init; } = [];

    public static SnapshotOptions CreateDefault()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        return new SnapshotOptions
        {
            FileRoots = new[]
            {
                F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86), F(Environment.SpecialFolder.CommonApplicationData),
                F(Environment.SpecialFolder.ApplicationData), local, Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"),
                F(Environment.SpecialFolder.CommonPrograms), F(Environment.SpecialFolder.CommonDesktopDirectory), F(Environment.SpecialFolder.DesktopDirectory),
            }.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RegistryRoots =
            [
                new RegPath(RegistryHive.CurrentUser, "Software"),
                new RegPath(RegistryHive.LocalMachine, "SOFTWARE"),
                new RegPath(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services"),
            ],
            FileNoise =
            [
                @"*\Temp\*", @"*\Temp", @"*\INetCache\*", @"*\INetCookies\*", @"*\CrashDumps\*", @"*\Packages\*", @"*\D3DSCache\*",
                @"*\Microsoft\Windows\Explorer\*", @"*\Microsoft\Windows\Recent\*", @"*\Microsoft\Windows\WER\*", @"*\Microsoft\Windows\WebCache\*",
                @"*\Microsoft\Windows\Notifications\*", @"*\Microsoft\Windows\History\*", @"*\Microsoft\Windows\Caches\*",
                @"*\Google\Chrome\User Data\*", @"*\Microsoft\Edge\User Data\*", @"*\BraveSoftware\*\User Data\*", @"*\Mozilla\Firefox\Profiles\*",
                @"*\Cache\*", @"*\Code Cache\*", @"*\GPUCache\*", @"*\ShaderCache\*", @"*\DXCache\*", @"*\GLCache\*", @"*\logs\*", @"*.log", @"*.etl", @"*.tmp",
                @"*\Unravel\*", @"*\Microsoft\Windows Defender\*", @"*\Microsoft\Diagnosis\*", @"*\Microsoft\Windows\WinX\*",
                @"*\Microsoft\Windows\SystemData\*", @"*\USOPrivate\*", @"*\USOShared\*", @"*\Microsoft\Search\*", @"*\Microsoft\Network\*",
            ],
            RegistryNoise =
            [
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\*",
                @"HKEY_CURRENT_USER\Software\Classes\Local Settings\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CloudStore\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\PushNotifications\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Store\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Internet Explorer\*",
                @"HKEY_CURRENT_USER\Software\Unravel*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AppListBackup\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\UFH\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\UFH\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Privacy\*",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Diagnostics\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Prefetcher\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\SystemCertificates\*",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Search\*",
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\bam\*",
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\*",
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Dnscache\*",
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\*\Parameters\Interfaces\*",
            ],
        };
    }
}

public sealed record SnapshotProgress(string Phase, int Items, string Current);

/// <summary>
/// A point-in-time record of files, registry, services, scheduled tasks and startup entries. Values are stored as
/// 64-bit hashes so snapshots stay compact; comparisons report added, removed and modified items.
/// </summary>
public sealed class SystemSnapshot
{
    public string Label { get; set; } = "";
    public DateTime Taken { get; set; }
    public Dictionary<string, FileRecord> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Registry key path → (value name → hash of kind and data).</summary>
    public Dictionary<string, Dictionary<string, ulong>> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ServiceRecord> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TaskRecord> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, StartupRecord> Startup { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];

    public int ItemCount => Files.Count + Registry.Count + Services.Count + Tasks.Count + Startup.Count;

    public static SystemSnapshot Capture(SnapshotOptions o, string label, IProgress<SnapshotProgress>? progress = null, CancellationToken ct = default)
    {
        var s = new SystemSnapshot { Label = label, Taken = DateTime.Now };
        foreach (var root in o.FileRoots) CaptureFiles(s, root, o, progress, ct);
        foreach (var root in o.RegistryRoots) CaptureRegistry(s, root, o, progress, ct);
        ct.ThrowIfCancellationRequested();
        if (o.Services)
        {
            progress?.Report(new("Services", s.ItemCount, ""));
            try
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    using (sc)
                    {
                        string? image = null;
                        try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + sc.ServiceName); image = k is null ? null : RegistryUtil.GetString(k, "ImagePath"); } catch { }
                        string start;
                        try { start = sc.StartType.ToString(); } catch { start = "Unknown"; }
                        s.Services[sc.ServiceName] = new ServiceRecord(sc.DisplayName, image, start);
                    }
                }
            }
            catch (Exception e) { s.Warnings.Add("Services: " + e.Message); }
        }
        if (o.Tasks)
        {
            progress?.Report(new("Scheduled tasks", s.ItemCount, ""));
            foreach (var t in ScheduledTasks.List())
                s.Tasks[t.Path] = new TaskRecord(t.Enabled, string.Join(" | ", t.Actions), Hash(Encoding.UTF8.GetBytes(t.Xml)));
        }
        if (o.Startup)
        {
            progress?.Report(new("Startup entries", s.ItemCount, ""));
            try
            {
                foreach (var e in new AutorunManager(AutorunEnvironment.CreateDefault(includeServices: false)).Load())
                    s.Startup[$"{e.Location}|{e.Name}"] = new StartupRecord(e.Command, e.Enabled);
            }
            catch (Exception e) { s.Warnings.Add("Startup: " + e.Message); }
        }
        return s;
    }

    private static void CaptureFiles(SystemSnapshot s, string root, SnapshotOptions o, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        var noise = new PathFilter(o.FileNoise);
        var stack = new Stack<string>();
        stack.Push(root);
        var last = DateTime.MinValue;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
            catch { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (noise.IsMatch(e.FullName)) continue;
                if (e is DirectoryInfo d)
                {
                    s.Files[d.FullName] = new FileRecord(0, 0, true);
                    stack.Push(d.FullName);
                }
                else if (e is FileInfo f)
                {
                    try { s.Files[f.FullName] = new FileRecord(f.Length, f.LastWriteTimeUtc.Ticks, false); } catch { }
                }
            }
            if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { progress?.Report(new("Files", s.ItemCount, dir)); last = DateTime.UtcNow; }
        }
    }

    private static void CaptureRegistry(SystemSnapshot s, RegPath root, SnapshotOptions o, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        using var rk = root.Open();
        if (rk is null) { s.Warnings.Add("Registry root not readable: " + root.FullPath); return; }
        var last = DateTime.MinValue;
        var noise = new PathFilter(o.RegistryNoise);
        Walk(rk, root.FullPath);

        void Walk(RegistryKey key, string path)
        {
            ct.ThrowIfCancellationRequested();
            if (noise.IsMatch(path)) return;
            var values = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var name in key.GetValueNames())
                {
                    try { values[name] = HashValue(key.GetValueKind(name), key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)); }
                    catch { }
                }
            }
            catch { }
            s.Registry[path] = values;
            if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { progress?.Report(new("Registry", s.ItemCount, path)); last = DateTime.UtcNow; }
            string[] subs;
            try { subs = key.GetSubKeyNames(); } catch { return; }
            foreach (var sub in subs)
            {
                RegistryKey? child = null;
                try { child = key.OpenSubKey(sub); } catch { }
                if (child is null) continue;
                using (child) Walk(child, path + "\\" + sub);
            }
        }
    }

    internal static ulong HashValue(RegistryValueKind kind, object? value)
    {
        byte[] data = value switch
        {
            null => [],
            byte[] b => b,
            string str => Encoding.Unicode.GetBytes(str),
            string[] arr => Encoding.Unicode.GetBytes(string.Join("\0", arr)),
            int i => BitConverter.GetBytes(i),
            long l => BitConverter.GetBytes(l),
            _ => Encoding.Unicode.GetBytes(value.ToString() ?? ""),
        };
        return Hash(data) ^ (ulong)kind * 0x9E3779B97F4A7C15UL;
    }

    /// <summary>FNV-1a, 64-bit. Collisions only hide a change if two different values hash the same (1 in 2^64).</summary>
    internal static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in data) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    // ------------------------------------------------------------------ persistence

    private const int FormatVersion = 1;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write("UNRAVELSNAP"); w.Write(FormatVersion); w.Write(Label); w.Write(Taken.Ticks);
            w.Write(Files.Count);
            foreach (var (k, v) in Files) { w.Write(k); w.Write(v.Size); w.Write(v.WriteTicks); w.Write(v.IsDirectory); }
            w.Write(Registry.Count);
            foreach (var (k, vals) in Registry)
            {
                w.Write(k); w.Write(vals.Count);
                foreach (var (n, h) in vals) { w.Write(n); w.Write(h); }
            }
            w.Write(Services.Count);
            foreach (var (k, v) in Services) { w.Write(k); w.Write(v.DisplayName); w.Write(v.ImagePath ?? ""); w.Write(v.StartType); }
            w.Write(Tasks.Count);
            foreach (var (k, v) in Tasks) { w.Write(k); w.Write(v.Enabled); w.Write(v.Actions); w.Write(v.XmlHash); }
            w.Write(Startup.Count);
            foreach (var (k, v) in Startup) { w.Write(k); w.Write(v.Command); w.Write(v.Enabled); }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static SystemSnapshot Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (r.ReadString() != "UNRAVELSNAP") throw new InvalidDataException("Not an Unravel snapshot file.");
        if (r.ReadInt32() != FormatVersion) throw new InvalidDataException("Unsupported snapshot version.");
        var s = new SystemSnapshot { Label = r.ReadString(), Taken = new DateTime(r.ReadInt64()) };
        for (int i = r.ReadInt32(); i > 0; i--) s.Files[r.ReadString()] = new FileRecord(r.ReadInt64(), r.ReadInt64(), r.ReadBoolean());
        for (int i = r.ReadInt32(); i > 0; i--)
        {
            var key = r.ReadString();
            var vals = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            for (int j = r.ReadInt32(); j > 0; j--) vals[r.ReadString()] = r.ReadUInt64();
            s.Registry[key] = vals;
        }
        for (int i = r.ReadInt32(); i > 0; i--)
        {
            var k = r.ReadString(); var dn = r.ReadString(); var img = r.ReadString(); var st = r.ReadString();
            s.Services[k] = new ServiceRecord(dn, img.Length == 0 ? null : img, st);
        }
        for (int i = r.ReadInt32(); i > 0; i--) s.Tasks[r.ReadString()] = new TaskRecord(r.ReadBoolean(), r.ReadString(), r.ReadUInt64());
        for (int i = r.ReadInt32(); i > 0; i--) s.Startup[r.ReadString()] = new StartupRecord(r.ReadString(), r.ReadBoolean());
        return s;
    }
}
