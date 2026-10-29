using System.Management;
using Unravel.Core.Util;

namespace Unravel.Core.SystemOps;

public enum RestorePointStatus { Created, Failed, NotAdministrator, Disabled, Throttled }

public sealed record RestorePointResult(RestorePointStatus Status, string Message);

/// <summary>Requests a System Restore point through WMI (root\default:SystemRestore).</summary>
public static class RestorePoint
{
    private const int ApplicationUninstall = 1;
    private const int BeginSystemChange = 100;

    public static RestorePointResult Create(string description)
    {
        if (!ProcessUtil.IsElevated)
            return new(RestorePointStatus.NotAdministrator, "Creating a restore point requires administrator rights. Restart Unravel as administrator to enable it.");
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description.Length > 64 ? description[..64] : description;
            inParams["RestorePointType"] = ApplicationUninstall;
            inParams["EventType"] = BeginSystemChange;
            using var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            var rc = Convert.ToUInt32(outParams["ReturnValue"]);
            return rc switch
            {
                0 => new(RestorePointStatus.Created, "Restore point created."),
                1058 => new(RestorePointStatus.Disabled, "System Restore is turned off in Windows settings."),
                _ => new(RestorePointStatus.Failed, $"System Restore returned error {rc}.")
            };
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.NotFound || e.ErrorCode == ManagementStatus.InvalidClass)
        {
            return new(RestorePointStatus.Disabled, "System Restore is not available on this system.");
        }
        catch (Exception e)
        {
            return new(RestorePointStatus.Failed, e.Message);
        }
    }
}
