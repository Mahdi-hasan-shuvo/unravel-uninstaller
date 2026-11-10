using Unravel.Core.Apps;
using Unravel.Core.Leftovers;
using Unravel.Core.Programs;
using Unravel.Core.WinGet;
using Windows.Management.Deployment;

namespace Unravel.Tests.Diagnostics;

/// <summary>Not a behaviour test: read-only dump of what each detection source sees, for comparing against Windows' own lists.</summary>
public class DetectionAudit
{
    private static string Dir { get { var d = Path.Combine(Path.GetTempPath(), "unravel-audit"); Directory.CreateDirectory(d); return d; } }

    [DiagnosticFact]
    public async Task DumpDetectionSources()
    {
        var visible = new ProgramInventory().Load(new InventoryOptions());
        var all = new ProgramInventory().Load(new InventoryOptions { IncludeSystemComponents = true, IncludeUpdates = true });
        File.WriteAllLines(Path.Combine(Dir, "programs.tsv"), all.Select(p =>
            $"{(visible.Any(v => v.RegistryKey.SameKeyAs(p.RegistryKey)) ? "shown" : "hidden")}\t{p.Kind}\t{p.Scope}\t{p.DisplayName}\t{p.Version}\t{p.KeyName}\t{p.RegistryKey.FullPath}\t{p.UninstallString}"));

        var apps = WindowsApps.List(new WindowsAppOptions());
        var appsAll = WindowsApps.List(new WindowsAppOptions { IncludeFrameworks = true, IncludeSystemApps = true });
        File.WriteAllLines(Path.Combine(Dir, "apps.tsv"), appsAll.Select(a =>
            $"{(apps.Any(x => x.PackageFullName == a.PackageFullName) ? "shown" : "hidden")}\t{a.Name}\t{a.PackageFullName}\t{a.SignatureKind}\tframework={a.IsFramework}\tnonremovable={a.IsNonRemovable}\tlaunch={a.CanLaunch}"));

        var lines = new List<string>();
        foreach (var pkg in new PackageManager().FindPackagesForUser(""))
        {
            string name = "?";
            try
            {
                name = pkg.Id.FullName;
                if (!appsAll.Any(a => a.PackageFullName == name))
                    lines.Add($"{name}\tres={pkg.IsResourcePackage}\tfw={pkg.IsFramework}\tbundle={pkg.IsBundle}\tstub={pkg.IsStub}\tNotAvailable={pkg.Status.NotAvailable}\tok={pkg.Status.VerifyIsOK()}");
            }
            catch (Exception e) { lines.Add($"{name}\tEXCEPTION {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllLines(Path.Combine(Dir, "apps-dropped.tsv"), lines);

        var client = new WinGetClient();
        var raw = await client.RunAsync("list --accept-source-agreements --disable-interactivity", null, CancellationToken.None);
        File.WriteAllText(Path.Combine(Dir, "winget-raw.txt"), raw.Output);
        var packages = WinGetParser.ParsePackages(raw.Output, WinGetParser.Command.List);
        File.WriteAllLines(Path.Combine(Dir, "winget.tsv"), packages.Select(p => $"{p.Name}\t{p.Id}\t{p.Version}\t{p.Available}\t{p.Source}"));
    }

    [DiagnosticFact]
    public void DumpLeftoverFindings()
    {
        var programs = new ProgramInventory().Load(new InventoryOptions());
        var env = ScanEnvironment.CreateDefault();
        env.OtherPrograms = programs.Select(p => (p.DisplayName, p.ResolvedInstallFolder ?? "")).ToList();
        var rows = new List<string>();
        foreach (var p in programs)
        {
            var others = programs.Where(o => o != p).Select(o => (o.DisplayName, o.ResolvedInstallFolder ?? "")).ToList();
            env.OtherPrograms = others;
            var target = ScanTarget.FromProgram(p);
            foreach (var mode in new[] { ScanMode.Moderate, ScanMode.Advanced })
            {
                var r = new LeftoverScanner(env).Scan(target, mode);
                foreach (var n in r.RegistryLeftovers.Concat(r.Files.Where(f => f.IsCreated)))
                    rows.Add($"{p.DisplayName}\t{p.Publisher}\t{mode}\t{n.Kind}\t{n.Safety.Confidence}\t{n.Safety.IsBlocked}\t{n.Safety.IsSafeToAutoSelect}\t{n.Location}\t{n.Reason}");
            }
        }
        File.WriteAllLines(Path.Combine(Dir, "leftovers.tsv"), rows);
    }
}
