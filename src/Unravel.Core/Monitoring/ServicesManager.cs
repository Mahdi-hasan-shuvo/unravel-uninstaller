using System.ServiceProcess;
using Microsoft.Win32;
using Unravel.Core.Logging;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Core.Monitoring;

public sealed record ServiceInfo(string Name, string DisplayName, string Status, string StartType, string? ImagePath, bool CanStop, bool IsCritical)
{
    public bool IsRunning => Status == nameof(ServiceControllerStatus.Running);
}

/// <summary>Lists and starts/stops Windows services, refusing to stop services Windows depends on.</summary>
public static class ServicesManager
{
    public static List<ServiceInfo> List()
    {
        var list = new List<ServiceInfo>();
        ServiceController[] all;
        try { all = ServiceController.GetServices(); } catch { return list; }
        foreach (var sc in all)
        {
            using (sc)
            {
                try
                {
                    string? image = null;
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + sc.ServiceName))
                        if (k is not null) image = RegistryUtil.GetString(k, "ImagePath");
                    string start;
                    try { start = sc.StartType.ToString(); } catch { start = "Unknown"; }
                    var critical = SafetyEngine.IsCriticalRegistry(RegPath.Parse(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + sc.ServiceName));
                    list.Add(new ServiceInfo(sc.ServiceName, sc.DisplayName, sc.Status.ToString(), start, image, sc.CanStop, critical));
                }
                catch { }
            }
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void Start(string name)
    {
        RequireAdmin("start");
        using var sc = new ServiceController(name);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        ActivityLog.Action("Services", $"Started service {name}");
    }

    public static void Stop(string name)
    {
        RequireAdmin("stop");
        if (SafetyEngine.IsCriticalRegistry(RegPath.Parse(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + name)))
            throw new InvalidOperationException($"{name} is a core Windows service and cannot be stopped from here.");
        using var sc = new ServiceController(name);
        if (!sc.CanStop) throw new InvalidOperationException($"{name} does not accept a stop request.");
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        ActivityLog.Action("Services", $"Stopped service {name}");
    }

    private static void RequireAdmin(string verb)
    {
        if (!ProcessUtil.IsElevated) throw new UnauthorizedAccessException($"To {verb} a service, restart Unravel as administrator.");
    }
}
