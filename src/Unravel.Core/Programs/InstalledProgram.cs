using Unravel.Core.Util;

namespace Unravel.Core.Programs;

public enum ProgramKind { Program, SystemComponent, Update }
public enum InstallScope { AllUsers, CurrentUser }

public sealed class InstalledProgram
{
    public required string DisplayName { get; init; }
    public required RegPath RegistryKey { get; init; }
    public string KeyName => RegistryKey.Name;
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? ModifyPath { get; init; }
    public string? DisplayIcon { get; init; }
    public string? Comments { get; init; }
    public string? HelpLink { get; init; }
    public string? AboutLink { get; init; }
    public string? UpdateLink { get; init; }
    public string? InstallSource { get; init; }
    public bool IsMsi { get; init; }
    public bool NoModify { get; init; }
    public bool NoRepair { get; init; }
    public bool NoRemove { get; init; }
    public string? ParentKeyName { get; init; }
    public ProgramKind Kind { get; init; }
    public InstallScope Scope { get; init; }
    public bool Is64Bit { get; init; }
    public string Installer { get; init; } = "Unknown";
    public DateTime? InstallDate { get; set; }
    public long? EstimatedSizeBytes { get; init; }
    public long? ComputedSizeBytes { get; set; }
    public long? SizeBytes => EstimatedSizeBytes ?? ComputedSizeBytes;

    public string? MsiProductCode =>
        IsMsi && CommandLine.IsGuid(KeyName) ? KeyName.ToUpperInvariant() : CommandLine.ExtractMsiProductCode(UninstallString);

    public string Bitness => Scope == InstallScope.CurrentUser ? "User" : Is64Bit ? "64-bit" : "32-bit";

    public string TypeText => Kind switch
    {
        ProgramKind.SystemComponent => "System component",
        ProgramKind.Update => "Update",
        _ => Bitness
    };

    /// <summary>MSI packages whose uninstall command is a modify command (/I) need a dedicated forced uninstall (/X).</summary>
    public bool SupportsForcedMsiUninstall =>
        MsiProductCode is not null && UninstallString is not null &&
        !System.Text.RegularExpressions.Regex.IsMatch(UninstallString, @"/x\s*\{", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public bool SupportsModify => !NoModify && (!string.IsNullOrEmpty(ModifyPath) || (IsMsi && MsiProductCode is not null));
    public bool SupportsRepair => IsMsi && !NoRepair && MsiProductCode is not null;
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString) || MsiProductCode is not null;

    /// <summary>Best guess of the folder the program occupies, used for sizing, leftovers and Hunter mode matching.</summary>
    public string? ResolvedInstallFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InstallLocation))
            {
                var dir = FileOps.NormalizeDir(InstallLocation);
                if (Directory.Exists(dir)) return dir;
                if (File.Exists(dir)) return Path.GetDirectoryName(dir);
            }
            foreach (var candidate in new[] { CommandLine.ParseIconPath(DisplayIcon).Path, CommandLine.FindExecutablePath(UninstallString) })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate)) continue;
                var dir = Path.GetDirectoryName(candidate);
                if (dir is null || IsGenericFolder(dir)) continue;
                if (Directory.Exists(dir)) return dir;
            }
            return string.IsNullOrWhiteSpace(InstallLocation) ? null : FileOps.NormalizeDir(InstallLocation);
        }
    }

    private static bool IsGenericFolder(string dir)
    {
        var d = dir.TrimEnd('\\');
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return FileOps.IsSameOrUnder(d, win) ||
               d.Contains(@"\Installer", StringComparison.OrdinalIgnoreCase) && d.Contains("Package Cache", StringComparison.OrdinalIgnoreCase) ||
               d.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ||
               d.EndsWith(@"\Temp", StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => DisplayName;
}
