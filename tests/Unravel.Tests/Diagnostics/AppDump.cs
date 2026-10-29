using Windows.Management.Deployment;

namespace Unravel.Tests.Diagnostics;

/// <summary>Not a test of behaviour: writes package naming facts used to compare with the original tool's Windows Apps list.</summary>
public class AppDump
{
    [Fact(Skip = "Diagnostic dump for manual comparison; run explicitly when investigating Windows Apps naming.")]
    public void DumpPackages()
    {
        var lines = new List<string>();
        foreach (var p in new PackageManager().FindPackagesForUser(""))
        {
            try
            {
                if (p.IsResourcePackage || p.IsFramework) continue;
                var entries = p.GetAppListEntries();
                var entryNames = string.Join(" / ", entries.Select(e => e.DisplayInfo.DisplayName));
                lines.Add($"{p.SignatureKind}\tstub={p.IsStub}\tentries={entries.Count}\t{p.Id.Name}\tpkg='{p.DisplayName}'\tapps='{entryNames}'");
            }
            catch (Exception e) { lines.Add("ERR " + p.Id.Name + " " + e.Message); }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "unravel-appdump.txt"), lines.OrderBy(l => l));
    }
}
