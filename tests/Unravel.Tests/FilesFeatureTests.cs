using System.Diagnostics;
using Unravel.Core.Files;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class DuplicateFinderTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static DuplicateScanOptions Opt(params string[] roots) => new() { Roots = roots, SkipHiddenAndSystem = false };

    [Fact]
    public async Task FindsIdenticalContentRegardlessOfName()
    {
        var content = new string('x', 5000);
        _t.File(@"a\report.docx", content);
        _t.File(@"b\copy of something.bin", content);
        _t.File(@"c\report.docx", new string('y', 5000));   // same name and size, different content → not a duplicate
        _t.File(@"d\unique.txt", "unique");
        var r = await DuplicateFinder.FindAsync(Opt(_t.Root), null, default);
        var g = Assert.Single(r.Groups);
        Assert.Equal(2, g.Files.Count);
        Assert.Contains(g.Files, f => f.Path.EndsWith(@"a\report.docx"));
        Assert.Contains(g.Files, f => f.Path.EndsWith(@"copy of something.bin"));
        Assert.Equal(5000, g.WastedBytes);
        Assert.Equal(64, g.Sha256.Length);
    }

    [Fact]
    public async Task LargeFilesDifferingOnlyInTheMiddleAreNotDuplicates()
    {
        // Same size, same first and last 64 KB: the partial hash matches, the full SHA-256 must separate them.
        var a = new byte[400_000];
        var b = (byte[])a.Clone();
        b[200_000] = 1;
        File.WriteAllBytes(_t.Path("one.bin"), a);
        File.WriteAllBytes(_t.Path("two.bin"), b);
        File.WriteAllBytes(_t.Path("three.bin"), a);
        var r = await DuplicateFinder.FindAsync(Opt(_t.Root), null, default);
        var g = Assert.Single(r.Groups);
        Assert.Equal(["one.bin", "three.bin"], g.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(x => x));
    }

    [Fact]
    public async Task HardLinksAreOnePhysicalFile()
    {
        var original = _t.File("orig.dat", new string('z', 3000));
        var link = _t.Path("link.dat");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{original}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        Assert.True(File.Exists(link));
        var r = await DuplicateFinder.FindAsync(Opt(_t.Root), null, default);
        Assert.Empty(r.Groups); // nothing to reclaim
        _t.File("real copy.dat", new string('z', 3000));
        r = await DuplicateFinder.FindAsync(Opt(_t.Root), null, default);
        var g = Assert.Single(r.Groups);
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(2, g.PhysicalCopies);
        Assert.Equal(3000, g.WastedBytes);
    }

    [Fact]
    public async Task RespectsMinimumSizeAndPatterns()
    {
        _t.File("a.txt", "12"); _t.File("b.txt", "12");
        _t.File("a.jpg", "photo-bytes"); _t.File("b.jpg", "photo-bytes");
        var r = await DuplicateFinder.FindAsync(new DuplicateScanOptions { Roots = [_t.Root], MinSize = 5, SkipHiddenAndSystem = false }, null, default);
        Assert.Single(r.Groups);
        r = await DuplicateFinder.FindAsync(new DuplicateScanOptions { Roots = [_t.Root], IncludePatterns = ["*.txt"], SkipHiddenAndSystem = false }, null, default);
        Assert.True(Assert.Single(r.Groups).Files.All(f => f.Path.EndsWith(".txt")));
    }

    [Fact]
    public async Task KeepRulesAlwaysLeaveOneCopy()
    {
        _t.File(@"old\x.bin", "same", DateTime.Now.AddDays(-5));
        _t.File(@"new\deeper\x.bin", "same", DateTime.Now.AddDays(-1));
        _t.File(@"mid\x.bin", "same", DateTime.Now.AddDays(-3));
        var g = (await DuplicateFinder.FindAsync(Opt(_t.Root), null, default)).Groups;
        DuplicateFinder.ApplyKeepRule(g, KeepRule.Newest);
        Assert.Contains(g[0].Files, f => !f.IsChecked && f.Path.Contains(@"\new\"));
        Assert.Equal(2, g[0].Files.Count(f => f.IsChecked));
        DuplicateFinder.ApplyKeepRule(g, KeepRule.Oldest);
        Assert.Contains(g[0].Files, f => !f.IsChecked && f.Path.Contains(@"\old\"));
        DuplicateFinder.ApplyKeepRule(g, KeepRule.FirstInFolder, _t.Path("mid"));
        Assert.Contains(g[0].Files, f => !f.IsChecked && f.Path.Contains(@"\mid\"));
        Assert.Empty(DuplicateFinder.GroupsLosingAllCopies(g));
        foreach (var f in g[0].Files) f.IsChecked = true;
        Assert.Single(DuplicateFinder.GroupsLosingAllCopies(g));
    }

    [Fact]
    public async Task CancellationIsReported()
    {
        for (int i = 0; i < 30; i++) _t.File($"d{i}\\f.bin", "dup");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await DuplicateFinder.FindAsync(Opt(_t.Root), null, cts.Token);
        Assert.True(r.Cancelled);
    }

    [Fact]
    public async Task UnreadableFilesAreCountedNotFatal()
    {
        _t.File("a.bin", "same"); _t.File("b.bin", "same");
        var locked = _t.File("c.bin", "same");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = await DuplicateFinder.FindAsync(Opt(_t.Root), null, default);
        Assert.Equal(1, r.Unreadable);
        Assert.Equal(2, Assert.Single(r.Groups).Files.Count);
    }
}

public class OrphanFinderTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private OrphanEnvironment Env(int minAge = 30) => new()
    {
        ProgramRoots = [_t.Dir("PF")],
        DataRoots = [_t.Dir("AppData")],
        ShortcutRoots = [_t.Dir("Start")],
        Programs = [("Contoso Widget 2.0", "Contoso Ltd", _t.Path("PF", "Contoso", "Widget")), ("Fabrikam Studio", "Fabrikam", null)],
        References = [(_t.Path("PF", "ServiceHost", "svc.exe"), "Windows service X")],
        MinAgeDays = minAge,
    };

    private void Old(string rel) => _t.File(rel, "data", DateTime.Now.AddDays(-200));

    [Fact]
    public void ReportsOnlyFoldersWithNoOwnerAndNoReferences()
    {
        Old(@"PF\Contoso\Widget\w.exe");          // owned by install folder
        Old(@"AppData\Fabrikam Studio\cfg.ini");   // owned by name of an installed program
        Old(@"AppData\Fabrikam\x.dat");            // owned by publisher name
        Old(@"PF\ServiceHost\svc.exe");            // referenced by a service
        Old(@"PF\GoneApp\gone.exe");               // orphan (contains a program)
        Old(@"AppData\OldTool\settings.json");     // orphan
        _t.File(@"AppData\RecentTool\s.json", "fresh"); // recently modified → not reported
        var found = OrphanFinder.Find(Env(), null, default);
        var paths = found.Where(f => f.Kind == OrphanKind.UnownedFolder).Select(f => Path.GetFileName(f.Path)).OrderBy(x => x).ToList();
        Assert.Equal(["GoneApp", "OldTool"], paths);
        var gone = found.Single(f => f.Path.EndsWith("GoneApp"));
        Assert.Equal(Confidence.Low, gone.Safety.Confidence);
        Assert.True(gone.Safety.Risks.HasFlag(RiskFlags.ContainsExecutables));
        Assert.False(gone.Safety.IsSafeToAutoSelect);
        Assert.True(gone.Evidence.Count >= 3);
    }

    [Fact]
    public void FoldersStartingWithAnInstalledProgramsNameAreTreatedAsOwned()
    {
        Old(@"AppData\Contoso2026Cache\c.bin");   // "Contoso Widget" is installed
        Old(@"AppData\FabrikamUpdater\u.exe");   // "Fabrikam Studio" is installed
        Old(@"AppData\UnrelatedThing\x.dat");
        var names = OrphanFinder.Find(Env(), null, default).Where(f => f.Kind == OrphanKind.UnownedFolder).Select(f => Path.GetFileName(f.Path)).ToList();
        Assert.Equal(["UnrelatedThing"], names);
        Assert.True(OrphanFinder.StartsWithStem("Arduino15", OrphanFinder.DistinctiveStems([("Arduino IDE 2.3.10", "Arduino SA", null)])));
        Assert.True(OrphanFinder.StartsWithStem("com.seelen.seelen-ui", OrphanFinder.DistinctiveStems([("Seelen UI", null, null)])));
        Assert.False(OrphanFinder.StartsWithStem("Program", OrphanFinder.DistinctiveStems([("Program Manager", null, null)])));
    }

    [Fact]
    public void FolderNamesAloneNeverMakeAnOrphan()
    {
        // A folder whose name looks orphan-ish but is referenced by a running program must not be reported.
        Old(@"AppData\Leftover-Like-Name\tool.exe");
        var env = Env();
        env.References.Add((_t.Path("AppData", "Leftover-Like-Name", "tool.exe"), "Running process tool.exe"));
        Assert.DoesNotContain(OrphanFinder.Find(env, null, default), f => f.Path.EndsWith("Leftover-Like-Name"));
    }

    [Fact]
    public void FindsBrokenShortcutsButNotWorkingOnes()
    {
        var target = _t.File(@"PF\Contoso\Widget\w.exe", "MZ");
        ShellLink.Create(_t.Path("Start", "Working.lnk"), target);
        var gone = _t.Path("PF", "Deleted", "missing.exe");
        ShellLink.Create(_t.Path("Start", "Broken.lnk"), gone);
        var found = OrphanFinder.Find(Env(), null, default).Where(f => f.Kind == OrphanKind.BrokenShortcut).ToList();
        var b = Assert.Single(found);
        Assert.EndsWith("Broken.lnk", b.Path);
        Assert.Equal(Confidence.High, b.Safety.Confidence);
        Assert.Contains("missing.exe", b.Evidence[0]);
    }

    [Fact]
    public void RealSystemScanCompletesAndNeverReportsCriticalOrProgramFolders()
    {
        var programs = new Unravel.Core.Programs.ProgramInventory().Load(new Unravel.Core.Programs.InventoryOptions { IncludeSystemComponents = true });
        var env = OrphanEnvironment.CaptureDefault(programs, Unravel.Core.Apps.WindowsApps.List(new() { IncludeSystemApps = true }));
        Assert.NotEmpty(env.References);
        var found = OrphanFinder.Find(env, null, default);
        Assert.All(found, f => Assert.False(SafetyEngine.IsCriticalPath(f.Path)));
        foreach (var p in programs.Where(p => p.ResolvedInstallFolder is { Length: > 3 }))
            Assert.DoesNotContain(found, f => f.Kind == OrphanKind.UnownedFolder && FileOps.IsSameOrUnder(p.ResolvedInstallFolder!, f.Path));
    }
}
