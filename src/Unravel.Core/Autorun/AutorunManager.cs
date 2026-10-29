using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using Unravel.Core.Util;

namespace Unravel.Core.Autorun;

public enum AutorunSource { RegistryRun, RegistryRunOnce, StartupFolder, Service }
public enum AutorunScope { AllUsers, CurrentUser }
public enum AutorunStatus { Running, NotRunning, Invalid, Unknown }

public sealed class AutorunEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required AutorunSource Source { get; init; }
    public required AutorunScope Scope { get; init; }
    public bool Is32Bit { get; init; }
    public string? TargetPath { get; init; }
    public string Arguments { get; init; } = "";
    public RegPath? RunKey { get; init; }
    public RegPath? ApprovedKey { get; init; }
    public string? LinkFile { get; init; }
    public string? ServiceName { get; init; }
    public bool IsParkedRunOnce { get; init; }
    public bool Enabled { get; set; }
    public AutorunStatus Status { get; set; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }

    public string SourceText => Source switch
    {
        AutorunSource.RegistryRun => "Registry: Run",
        AutorunSource.RegistryRunOnce => "Registry: RunOnce",
        AutorunSource.StartupFolder => "Startup folder",
        AutorunSource.Service => "Service",
        _ => ""
    };

    public string ScopeText => Scope == AutorunScope.AllUsers ? "All users" : "Current user";

    public string Location => Source switch
    {
        AutorunSource.StartupFolder => LinkFile ?? "",
        AutorunSource.Service => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + ServiceName,
        _ => RunKey?.FullPath ?? ""
    };

    public string StatusText => Status switch
    {
        AutorunStatus.Running => "Running",
        AutorunStatus.NotRunning => "Not running",
        AutorunStatus.Invalid => "Invalid",
        _ => "Unknown"
    };
}

public sealed record RunKeySpec(RegPath Key, AutorunScope Scope, bool Is32Bit, bool RunOnce, RegPath? ApprovedKey);
public sealed record StartupFolderSpec(string Folder, AutorunScope Scope, RegPath? ApprovedKey);

public sealed class AutorunEnvironment
{
    public List<RunKeySpec> RunKeys { get; init; } = [];
    public List<StartupFolderSpec> StartupFolders { get; init; } = [];
    /// <summary>RunOnce values cannot be disabled in place, so disabled ones are parked here and restored on enable.</summary>
    public required RegPath ParkingKey { get; init; }
    public bool IncludeServices { get; init; } = true;

    public static AutorunEnvironment CreateDefault(bool includeServices = true)
    {
        const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string runOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        const string approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        var list = new List<RunKeySpec>
        {
            new(new RegPath(RegistryHive.CurrentUser, run), AutorunScope.CurrentUser, false, false, new RegPath(RegistryHive.CurrentUser, approved + "Run")),
            new(new RegPath(RegistryHive.CurrentUser, runOnce), AutorunScope.CurrentUser, false, true, null),
            new(new RegPath(RegistryHive.LocalMachine, run), AutorunScope.AllUsers, false, false, new RegPath(RegistryHive.LocalMachine, approved + "Run")),
            new(new RegPath(RegistryHive.LocalMachine, runOnce), AutorunScope.AllUsers, false, true, null),
        };
        if (Environment.Is64BitOperatingSystem)
        {
            list.Add(new(new RegPath(RegistryHive.LocalMachine, run, RegistryView.Registry32), AutorunScope.AllUsers, true, false, new RegPath(RegistryHive.LocalMachine, approved + "Run32")));
            list.Add(new(new RegPath(RegistryHive.LocalMachine, runOnce, RegistryView.Registry32), AutorunScope.AllUsers, true, true, null));
        }
        return new AutorunEnvironment
        {
            RunKeys = list,
            StartupFolders =
            [
                new(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AutorunScope.CurrentUser, new RegPath(RegistryHive.CurrentUser, approved + "StartupFolder")),
                new(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), AutorunScope.AllUsers, new RegPath(RegistryHive.LocalMachine, approved + "StartupFolder")),
            ],
            ParkingKey = new RegPath(RegistryHive.CurrentUser, @"Software\Unravel\DisabledAutorun"),
            IncludeServices = includeServices,
        };
    }
}

/// <summary>
/// Lists and manages programs that start with Windows. Enable/disable uses Windows' own StartupApproved flags,
/// so the state matches what Task Manager and Settings › Apps › Startup show.
/// </summary>
public sealed class AutorunManager
{
    private readonly AutorunEnvironment _env;
    private readonly Dictionary<string, int> _serviceStartTypes;

    public AutorunManager(AutorunEnvironment env, Dictionary<string, int>? disabledServiceStartTypes = null)
    {
        _env = env;
        _serviceStartTypes = disabledServiceStartTypes ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public bool ShowMicrosoftServices { get; set; }

    public List<AutorunEntry> Load()
    {
        var procs = ProcessUtil.GetProcesses();
        var running = procs.Where(p => p.ImagePath is not null).Select(p => p.ImagePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<AutorunEntry>();

        foreach (var spec in _env.RunKeys)
        {
            using var k = spec.Key.Open();
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                if (name.Length == 0) continue;
                var cmd = RegistryUtil.GetString(k, name) ?? "";
                list.Add(Make(name, cmd, spec.RunOnce ? AutorunSource.RegistryRunOnce : AutorunSource.RegistryRun, spec.Scope, spec.Is32Bit,
                    runKey: spec.Key, approved: spec.ApprovedKey, enabled: IsApproved(spec.ApprovedKey, name), running: running));
            }
        }

        foreach (var spec in _env.StartupFolders)
        {
            if (string.IsNullOrEmpty(spec.Folder) || !Directory.Exists(spec.Folder)) continue;
            foreach (var file in Directory.GetFiles(spec.Folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string cmd = file;
                if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellLink.Read(file) is { } li && li.TargetPath.Length > 0)
                    cmd = string.IsNullOrEmpty(li.Arguments) ? $"\"{li.TargetPath}\"" : $"\"{li.TargetPath}\" {li.Arguments}";
                list.Add(Make(Path.GetFileNameWithoutExtension(file), cmd, AutorunSource.StartupFolder, spec.Scope, false,
                    linkFile: file, approved: spec.ApprovedKey, enabled: IsApproved(spec.ApprovedKey, fileName), running: running));
            }
        }

        // Parked (disabled) RunOnce entries.
        using (var pk = _env.ParkingKey.Open())
        {
            if (pk is not null)
                foreach (var id in pk.GetSubKeyNames())
                {
                    using var e = pk.OpenSubKey(id);
                    if (e is null) continue;
                    var origin = RegistryUtil.GetString(e, "Key");
                    var name = RegistryUtil.GetString(e, "Name");
                    var cmd = RegistryUtil.GetString(e, "Command") ?? "";
                    if (origin is null || name is null || !RegPath.TryParse(origin, out var originKey)) continue;
                    var spec = _env.RunKeys.FirstOrDefault(s => s.Key.SameKeyAs(originKey!));
                    list.Add(Make(name, cmd, AutorunSource.RegistryRunOnce, spec?.Scope ?? AutorunScope.CurrentUser, spec?.Is32Bit ?? false,
                        runKey: spec?.Key ?? originKey, approved: null, enabled: false, running: running, parked: true));
                }
        }

        if (_env.IncludeServices) list.AddRange(LoadServices(running));
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private AutorunEntry Make(string name, string cmd, AutorunSource source, AutorunScope scope, bool is32, HashSet<string> running,
        RegPath? runKey = null, RegPath? approved = null, string? linkFile = null, bool enabled = true, bool parked = false, string? service = null)
    {
        var target = CommandLine.FindExecutablePath(cmd);
        var (_, args) = CommandLine.Split(cmd);
        string? publisher = null, description = null;
        if (target is not null && File.Exists(target))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(target);
                publisher = string.IsNullOrWhiteSpace(vi.CompanyName) ? null : vi.CompanyName.Trim();
                description = string.IsNullOrWhiteSpace(vi.FileDescription) ? null : vi.FileDescription.Trim();
            }
            catch { }
        }
        AutorunStatus status;
        if (target is null || !File.Exists(target)) status = linkFile is not null && cmd == linkFile ? AutorunStatus.NotRunning : AutorunStatus.Invalid;
        else status = running.Contains(FileOps.NormalizeDir(target)) ? AutorunStatus.Running : AutorunStatus.NotRunning;
        return new AutorunEntry
        {
            Name = name, Command = cmd, Source = source, Scope = scope, Is32Bit = is32, TargetPath = target, Arguments = args,
            RunKey = runKey, ApprovedKey = approved, LinkFile = linkFile, Enabled = enabled, Status = status,
            Publisher = publisher, Description = description, IsParkedRunOnce = parked, ServiceName = service,
        };
    }

    private IEnumerable<AutorunEntry> LoadServices(HashSet<string> running)
    {
        ServiceController[] services;
        try { services = ServiceController.GetServices(); } catch { yield break; }
        foreach (var sc in services)
        {
            AutorunEntry? entry = null;
            try
            {
                var start = sc.StartType;
                bool parked = _serviceStartTypes.ContainsKey(sc.ServiceName);
                if (start != ServiceStartMode.Automatic && !(parked && start != ServiceStartMode.Automatic)) continue;
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + sc.ServiceName);
                var image = k is null ? null : RegistryUtil.GetString(k, "ImagePath");
                if (image is null) continue;
                var e = Make(sc.DisplayName, image, AutorunSource.Service, AutorunScope.AllUsers, false, running, enabled: start == ServiceStartMode.Automatic, service: sc.ServiceName);
                if (!ShowMicrosoftServices && (e.Publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                if (!ShowMicrosoftServices && e.TargetPath is not null && e.TargetPath.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) continue;
                e.Status = sc.Status == ServiceControllerStatus.Running ? AutorunStatus.Running : e.Status == AutorunStatus.Invalid ? AutorunStatus.Invalid : AutorunStatus.NotRunning;
                entry = e;
            }
            catch { }
            finally { sc.Dispose(); }
            if (entry is not null) yield return entry;
        }
    }

    // ---------------------------------------------------------------- enable / disable

    public static bool IsApproved(RegPath? approvedKey, string valueName)
    {
        if (approvedKey is null) return true;
        using var k = approvedKey.Open();
        if (k?.GetValue(valueName) is byte[] b && b.Length > 0) return (b[0] & 1) == 0;
        return true;
    }

    public static byte[] ApprovalBytes(bool enabled)
    {
        var b = new byte[12];
        b[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 4);
        return b;
    }

    public void SetEnabled(AutorunEntry e, bool enabled)
    {
        if (e.Enabled == enabled) return;
        switch (e.Source)
        {
            case AutorunSource.RegistryRun:
            case AutorunSource.StartupFolder:
                if (e.ApprovedKey is null) throw new InvalidOperationException("This entry cannot be disabled.");
                WriteApproval(e.ApprovedKey, e.Source == AutorunSource.StartupFolder ? Path.GetFileName(e.LinkFile!) : e.Name, enabled);
                break;
            case AutorunSource.RegistryRunOnce:
                if (enabled) Unpark(e); else Park(e);
                break;
            case AutorunSource.Service:
                SetServiceEnabled(e.ServiceName!, enabled);
                break;
        }
        e.Enabled = enabled;
        Logging.ActivityLog.Action("Startup", $"{(enabled ? "Enabled" : "Disabled")} \"{e.Name}\" ({e.SourceText}, {e.ScopeText})", e.Command);
    }

    private static void WriteApproval(RegPath key, string name, bool enabled)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(key.Hive, key.View);
            using var k = root.CreateSubKey(key.SubPath, writable: true);
            k.SetValue(name, ApprovalBytes(enabled), RegistryValueKind.Binary);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new UnauthorizedAccessException("Changing an all-users startup entry requires administrator rights.", ex);
        }
    }

    private void Park(AutorunEntry e)
    {
        var id = Guid.NewGuid().ToString("N");
        using (var root = RegistryKey.OpenBaseKey(_env.ParkingKey.Hive, _env.ParkingKey.View))
        using (var k = root.CreateSubKey(_env.ParkingKey.SubPath + "\\" + id))
        {
            k.SetValue("Key", e.RunKey!.FullPath);
            k.SetValue("Name", e.Name);
            k.SetValue("Command", e.Command);
        }
        if (!RegistryUtil.DeleteValue(e.RunKey!, e.Name, out var err))
        {
            RegistryUtil.DeleteKeyTree(_env.ParkingKey.Child(id), out _);
            throw new UnauthorizedAccessException(err ?? "Could not disable the entry.");
        }
    }

    private void Unpark(AutorunEntry e)
    {
        using var pk = _env.ParkingKey.Open();
        if (pk is null) return;
        foreach (var id in pk.GetSubKeyNames())
        {
            using var k = pk.OpenSubKey(id);
            if (k is null) continue;
            if (!string.Equals(RegistryUtil.GetString(k, "Name"), e.Name, StringComparison.Ordinal) ||
                !string.Equals(RegistryUtil.GetString(k, "Key"), e.RunKey!.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var root = RegistryKey.OpenBaseKey(e.RunKey.Hive, e.RunKey.View);
                using var rk = root.CreateSubKey(e.RunKey.SubPath, true);
                rk.SetValue(e.Name, e.Command, RegistryValueKind.String);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new UnauthorizedAccessException("Changing an all-users startup entry requires administrator rights.", ex);
            }
            RegistryUtil.DeleteKeyTree(_env.ParkingKey.Child(id), out _);
            return;
        }
    }

    private void SetServiceEnabled(string service, bool enabled)
    {
        if (!ProcessUtil.IsElevated) throw new UnauthorizedAccessException("Changing a service's startup type requires administrator rights.");
        string mode;
        if (enabled)
        {
            mode = _serviceStartTypes.TryGetValue(service, out var prev) && prev == 3 ? "demand" : "auto";
            _serviceStartTypes.Remove(service);
        }
        else
        {
            _serviceStartTypes[service] = 2;
            mode = "demand";
        }
        var psi = new ProcessStartInfo("sc.exe", $"config \"{service}\" start= {mode}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        if (p.ExitCode != 0) throw new InvalidOperationException(output.Trim());
    }

    // ---------------------------------------------------------------- remove / add / run

    public void Remove(AutorunEntry e)
    {
        Logging.ActivityLog.Action("Startup", $"Removed \"{e.Name}\" from startup ({e.SourceText}, {e.ScopeText})", e.Command);
        switch (e.Source)
        {
            case AutorunSource.RegistryRun:
            case AutorunSource.RegistryRunOnce:
                if (e.IsParkedRunOnce)
                {
                    using var pk = _env.ParkingKey.Open();
                    foreach (var id in pk?.GetSubKeyNames() ?? [])
                    {
                        using var k = pk!.OpenSubKey(id);
                        if (k is not null && RegistryUtil.GetString(k, "Name") == e.Name)
                            RegistryUtil.DeleteKeyTree(_env.ParkingKey.Child(id), out _);
                    }
                    return;
                }
                if (!RegistryUtil.DeleteValue(e.RunKey!, e.Name, out var err)) throw new UnauthorizedAccessException(err);
                if (e.ApprovedKey is not null) RegistryUtil.DeleteValue(e.ApprovedKey, e.Name, out _);
                break;
            case AutorunSource.StartupFolder:
                var r = FileOps.Delete(e.LinkFile!, toRecycleBin: true);
                if (!r.Succeeded) throw new IOException(r.Error);
                if (e.ApprovedKey is not null) RegistryUtil.DeleteValue(e.ApprovedKey, Path.GetFileName(e.LinkFile!), out _);
                break;
            case AutorunSource.Service:
                throw new InvalidOperationException("Services cannot be removed from the startup list. Disable the service instead.");
        }
    }

    public AutorunEntry Add(string name, string targetPath, string arguments, AutorunScope scope, AutorunSource source)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a name for the startup entry.");
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Choose the program to start.");
        if (!File.Exists(targetPath)) throw new FileNotFoundException("The program file does not exist.", targetPath);
        var cmd = $"\"{targetPath}\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim());
        try
        {
            switch (source)
            {
                case AutorunSource.RegistryRun:
                case AutorunSource.RegistryRunOnce:
                {
                    var spec = _env.RunKeys.First(s => s.Scope == scope && !s.Is32Bit && s.RunOnce == (source == AutorunSource.RegistryRunOnce));
                    using (var probe = spec.Key.Open())
                        if (probe?.GetValue(name) is not null) throw new InvalidOperationException($"A startup entry named \"{name}\" already exists there.");
                    using var root = RegistryKey.OpenBaseKey(spec.Key.Hive, spec.Key.View);
                    using var k = root.CreateSubKey(spec.Key.SubPath, true);
                    k.SetValue(name, cmd, RegistryValueKind.String);
                    if (spec.ApprovedKey is not null) RegistryUtil.DeleteValue(spec.ApprovedKey, name, out _);
                    break;
                }
                case AutorunSource.StartupFolder:
                {
                    var spec = _env.StartupFolders.First(s => s.Scope == scope);
                    var lnk = Path.Combine(spec.Folder, name + ".lnk");
                    if (File.Exists(lnk)) throw new InvalidOperationException($"A startup shortcut named \"{name}\" already exists.");
                    ShellLink.Create(lnk, targetPath, arguments);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(source), "Only Run, RunOnce and the Startup folder are supported.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new UnauthorizedAccessException("Adding an all-users startup entry requires administrator rights.", ex);
        }
        return Load().First(e => e.Name.Equals(source == AutorunSource.StartupFolder ? name : name, StringComparison.OrdinalIgnoreCase) && e.Source == source && e.Scope == scope);
    }

    public static void Run(AutorunEntry e)
    {
        if (e.Source == AutorunSource.Service)
        {
            using var sc = new ServiceController(e.ServiceName!);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            return;
        }
        if (e.Status == AutorunStatus.Invalid) throw new FileNotFoundException("The program file no longer exists.", e.TargetPath);
        if (e.LinkFile is not null && File.Exists(e.LinkFile)) { Shell.OpenPath(e.LinkFile); return; }
        Shell.RunCommandLine(e.Command);
    }

    public static (int Killed, int Failed) Kill(AutorunEntry e)
    {
        if (e.Source == AutorunSource.Service)
        {
            using var sc = new ServiceController(e.ServiceName!);
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            return (1, 0);
        }
        return e.TargetPath is null ? (0, 0) : ProcessUtil.KillByImagePath(e.TargetPath);
    }

    public static AutorunStatus RefreshStatus(AutorunEntry e)
    {
        if (e.Source == AutorunSource.Service)
        {
            try { using var sc = new ServiceController(e.ServiceName!); return sc.Status == ServiceControllerStatus.Running ? AutorunStatus.Running : AutorunStatus.NotRunning; }
            catch { return AutorunStatus.Unknown; }
        }
        if (e.TargetPath is null || !File.Exists(e.TargetPath)) return AutorunStatus.Invalid;
        return ProcessUtil.IsRunning(e.TargetPath) ? AutorunStatus.Running : AutorunStatus.NotRunning;
    }
}
