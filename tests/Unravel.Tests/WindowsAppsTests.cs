using Unravel.Core.Apps;
using Unravel.Core.Leftovers;

namespace Unravel.Tests;

public class WindowsAppsTests
{
    [Fact]
    public void ListsUserAppsWithoutFrameworksOrSystemApps()
    {
        var apps = WindowsApps.List(new WindowsAppOptions());
        Assert.NotEmpty(apps);
        Assert.DoesNotContain(apps, a => a.IsFramework);
        Assert.DoesNotContain(apps, a => a.IsSystem);
        Assert.All(apps, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.False(a.Name.StartsWith("ms-resource:"));
            Assert.StartsWith("ms-windows-store://pdp/?PFN=", a.StoreUrl);
        });
        // Apps are named as in the Start menu ("Calculator"), not by package ("Windows Calculator") – as the original does.
        Assert.Contains(apps, a => a.PackageName == "Microsoft.WindowsCalculator" && a.Name == "Calculator");
    }

    [Fact]
    public void AppsWithoutStartMenuEntryUseTheirManifestName()
    {
        // Packages hidden from Start keep their declared application name (resolved from ms-resource strings).
        var all = WindowsApps.List(new WindowsAppOptions());
        var hidden = all.Where(a => !a.CanLaunch).ToList();
        Assert.NotEmpty(hidden);
        Assert.All(all, a => Assert.DoesNotContain("ms-resource", a.Name));
        var ddlm = all.FirstOrDefault(a => a.PackageName.StartsWith("Microsoft.WinAppRuntime.DDLM"));
        if (ddlm is not null) Assert.Equal("Microsoft Windows App Runtime DynamicDependency LifetimeManager", ddlm.Name);
    }

    [Fact]
    public void StubPackagesAreListed()
    {
        var apps = WindowsApps.List(new WindowsAppOptions());
        var stubs = new Windows.Management.Deployment.PackageManager().FindPackagesForUser("")
            .Where(p => p.IsStub && !p.IsFramework && !p.IsResourcePackage).Select(p => p.Id.FullName).ToList();
        Assert.All(stubs, s => Assert.Contains(apps, a => a.PackageFullName == s));
    }

    [Fact]
    public void OptionsIncludeFrameworksAndSystemApps()
    {
        var basic = WindowsApps.List(new WindowsAppOptions());
        var all = WindowsApps.List(new WindowsAppOptions { IncludeFrameworks = true, IncludeSystemApps = true });
        Assert.True(all.Count > basic.Count);
        Assert.Contains(all, a => a.IsFramework);
    }

    [Fact]
    public void ScanTargetUsesPackageIdentityAndNeverTheWindowsAppsFolder()
    {
        var app = WindowsApps.List(new WindowsAppOptions()).First();
        var t = app.ToScanTarget();
        Assert.Equal(app.PackageFamilyName, t.PackageFamilyName);
        Assert.Null(t.InstallFolder);
        var r = new LeftoverScanner(ScanEnvironment.CreateDefault()).Scan(t, ScanMode.Safe);
        Assert.DoesNotContain(r.RawFindings, f => f.FsPath?.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) == true);
        // The app is still installed, so its registry repository entry must be found.
        Assert.Contains(r.RawFindings, f => f.Location.EndsWith(app.PackageFullName, StringComparison.OrdinalIgnoreCase));
    }
}
