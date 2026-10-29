using System.Diagnostics;
using Microsoft.Win32;
using Unravel.Core.Leftovers;
using Unravel.Core.Safety;
using Unravel.Core.SystemOps;
using Unravel.Core.Tracking;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class PathFilterTests
{
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\a.txt", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Templates\a.txt", false)]
    [InlineData(@"C:\Prog\app\debug.log", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Contoso", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\x\Parameters\Interfaces\{1}", true)]
    public void Matches(string path, bool expected)
    {
        var f = new PathFilter([@"*\Temp\*", "*.log", @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\*",
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\*\Parameters\Interfaces\*"]);
        Assert.Equal(expected, f.IsMatch(path));
    }
}

public class TrackerSafetyTests
{
    [Theory]
    [InlineData(ChangeArea.RegistryKey, @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Something\X", true)]
    [InlineData(ChangeArea.RegistryKey, @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App", false)]
    [InlineData(ChangeArea.RegistryKey, @"HKEY_CURRENT_USER\Software\Contoso\App", false)]
    [InlineData(ChangeArea.File, @"C:\Users\x\AppData\Local\Microsoft\Windows\thing.dat", true)]
    [InlineData(ChangeArea.File, @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\App.lnk", false)]
    [InlineData(ChangeArea.Folder, @"C:\Program Files\Contoso", false)]
    public void WindowsOwnAreasAreNeverPreselected(ChangeArea area, string location, bool expected) =>
        Assert.Equal(expected, InstallTracker.IsWindowsOwnArea(new TrackedChange(area, ChangeType.Added, location)));
}

public class InstallTrackerTests : IDisposable
{
    private readonly RegistrySandbox _sb = new();
    private readonly TempDir _t = new();
    private readonly SnapshotOptions _opt;

    public InstallTrackerTests()
    {
        _sb.CreateKey(@"Software\Existing", ("Keep", "1"), ("Change", "old"));
        _sb.CreateKey(@"Microsoft\Windows\CurrentVersion\Uninstall\SomethingElse", ("DisplayName", "Other"));
        _t.File(@"PF\Existing\keep.txt", "keep");
        _t.File(@"PF\Existing\change.txt", "v1", DateTime.Now.AddDays(-1));
        _t.File(@"PF\Existing\debug.log", "noise");
        _opt = new SnapshotOptions
        {
            FileRoots = [_t.Dir("PF")],
            RegistryRoots = [_sb.Root],
            Services = false, Tasks = true, Startup = false,
            FileNoise = ["*.log"],
        };
    }

    public void Dispose()
    {
        try { ScheduledTasks.Delete(@"\UnravelTest-" + _sb.Root.Name); } catch { }
        _sb.Dispose();
        _t.Dispose();
    }

    private void SimulateInstall()
    {
        _t.File(@"PF\NewApp\app.exe", "MZ");
        _t.File(@"PF\NewApp\lib\core.dll", "MZ");
        _t.File(@"PF\Existing\added.txt", "new");
        File.WriteAllText(_t.Path("PF", "Existing", "change.txt"), "version two");
        File.WriteAllText(_t.Path("PF", "Existing", "debug.log"), "more noise");
        _sb.CreateKey(@"Software\NewApp\Settings", ("Theme", "dark"));
        _sb.SetValue(@"Software\Existing", "AddedValue", "x");
        _sb.SetValue(@"Software\Existing", "Change", "new");
        _sb.CreateKey(@"Microsoft\Windows\CurrentVersion\Uninstall\NewApp", ("DisplayName", "New App 1.0"));
    }

    [Fact]
    public void DiffReportsAddedRemovedAndModifiedItemsWithoutNoise()
    {
        var before = SystemSnapshot.Capture(_opt, "before");
        SimulateInstall();
        File.Delete(_t.Path("PF", "Existing", "keep.txt"));
        var after = SystemSnapshot.Capture(_opt, "after");
        var c = SnapshotDiff.Compare(before, after).Changes;

        var addedFolder = Assert.Single(c, x => x.Area == ChangeArea.Folder && x.Type == ChangeType.Added);
        Assert.EndsWith(@"PF\NewApp", addedFolder.Location);
        Assert.Equal(3, addedFolder.ContainedItems); // app.exe, lib, lib\core.dll
        Assert.Contains(c, x => x.Area == ChangeArea.File && x.Type == ChangeType.Added && x.Location.EndsWith("added.txt"));
        Assert.Contains(c, x => x.Area == ChangeArea.File && x.Type == ChangeType.Modified && x.Location.EndsWith("change.txt"));
        Assert.Contains(c, x => x.Area == ChangeArea.File && x.Type == ChangeType.Removed && x.Location.EndsWith("keep.txt"));
        Assert.DoesNotContain(c, x => x.Location.EndsWith(".log"));
        Assert.Contains(c, x => x.Area == ChangeArea.RegistryKey && x.Type == ChangeType.Added && x.Location.EndsWith(@"Software\NewApp"));
        Assert.DoesNotContain(c, x => x.Area == ChangeArea.RegistryKey && x.Location.EndsWith(@"NewApp\Settings")); // folded into its parent
        Assert.Contains(c, x => x.Area == ChangeArea.RegistryValue && x.Type == ChangeType.Added && x.ValueName == "AddedValue");
        Assert.Contains(c, x => x.Area == ChangeArea.RegistryValue && x.Type == ChangeType.Modified && x.ValueName == "Change");
    }

    [Fact]
    public void DetectsScheduledTasksAddedByAnInstall()
    {
        var before = SystemSnapshot.Capture(_opt, "before");
        var name = "UnravelTest-" + _sb.Root.Name;
        var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/create /tn \"{name}\" /tr \"notepad.exe\" /sc once /st 23:59 /f") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        var after = SystemSnapshot.Capture(_opt, "after");
        var t = Assert.Single(SnapshotDiff.Compare(before, after).Changes, x => x.Area == ChangeArea.ScheduledTask && x.Type == ChangeType.Added);
        Assert.Equal("\\" + name, t.Location);
        Assert.Contains("notepad.exe", t.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SnapshotsSurviveSaveAndLoad()
    {
        SimulateInstall();
        var s = SystemSnapshot.Capture(_opt, "label");
        var file = _t.Path("snap", "s.snap");
        s.Save(file);
        var l = SystemSnapshot.Load(file);
        Assert.Equal("label", l.Label);
        Assert.Equal(s.Files.Count, l.Files.Count);
        Assert.Equal(s.Registry.Count, l.Registry.Count);
        Assert.Equal(s.Tasks.Count, l.Tasks.Count);
        Assert.Empty(SnapshotDiff.Compare(s, l).Changes);
        File.WriteAllText(file, "garbage");
        Assert.ThrowsAny<Exception>(() => SystemSnapshot.Load(file));
    }

    [Fact]
    public void RecordsCleansUpAndUndoesAnInstall()
    {
        var tracker = new InstallTracker(_t.Path("logs"));
        var log = tracker.Begin("New App 1.0", _opt, null, default);
        Assert.Equal(SessionState.WaitingForInstall, Assert.Single(tracker.List()).State);
        SimulateInstall();
        var taskName = "UnravelTest-" + _sb.Root.Name;
        Process.Start(new ProcessStartInfo("schtasks.exe", $"/create /tn \"{taskName}\" /tr \"notepad.exe\" /sc once /st 23:59 /f") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        tracker.Complete(log, _opt, null, default);
        Assert.Single(log.UninstallKeys);

        var plan = InstallTracker.PlanCleanup(log);
        Assert.All(plan, i => Assert.Equal(Confidence.High, i.Safety.Confidence));
        Assert.Contains(plan, i => i.Change.Area == ChangeArea.Folder);
        Assert.Contains(plan, i => i.Change.Area == ChangeArea.ScheduledTask);
        Assert.DoesNotContain(plan, i => i.Change.Type != ChangeType.Added); // modifications are never "undone" blindly

        var q = new Quarantine(_t.Path("quarantine"));
        var backup = new RegistryBackup(_t.Path("regbackup"));
        var record = tracker.Clean(log, plan, q, backup);
        Assert.Empty(record.Failures);
        Assert.False(Directory.Exists(_t.Path("PF", "NewApp")));
        Assert.False(File.Exists(_t.Path("PF", "Existing", "added.txt")));
        Assert.False(_sb.Path(@"Software\NewApp").Exists());
        using (var k = _sb.Path(@"Software\Existing").Open()) Assert.Null(k!.GetValue("AddedValue"));
        Assert.DoesNotContain(ScheduledTasks.List(), x => x.Path == "\\" + taskName);
        // Things that existed before the install are untouched.
        Assert.True(File.Exists(_t.Path("PF", "Existing", "keep.txt")));
        using (var k = _sb.Path(@"Software\Existing").Open()) Assert.Equal("1", k!.GetValue("Keep"));

        var reloaded = tracker.List().Single();
        Assert.Equal(SessionState.CleanedUp, reloaded.State);
        var problems = tracker.Undo(reloaded, q, backup);
        Assert.Empty(problems);
        Assert.True(File.Exists(_t.Path("PF", "NewApp", "lib", "core.dll")));
        Assert.True(_sb.Path(@"Software\NewApp\Settings").Exists());
        using (var k = _sb.Path(@"Software\Existing").Open()) Assert.Equal("x", k!.GetValue("AddedValue"));
        Assert.Contains(ScheduledTasks.List(), x => x.Path == "\\" + taskName);
        Assert.Throws<InvalidOperationException>(() => tracker.Undo(tracker.List().Single(), q, backup));
    }

    [Fact]
    public void InstallLogFeedsTheUninstallerScan()
    {
        var tracker = new InstallTracker(_t.Path("logs"));
        var log = tracker.Begin("New App 1.0", _opt, null, default);
        SimulateInstall();
        tracker.Complete(log, _opt, null, default);
        var program = new Unravel.Core.Programs.InstalledProgram
        {
            DisplayName = "New App 1.0",
            RegistryKey = RegPath.Parse(log.UninstallKeys[0]),
        };
        var found = tracker.FindFor(program);
        Assert.NotNull(found);
        var target = new ScanTarget { DisplayName = "New App 1.0", TrackedChanges = found!.Changes, TrackedLogName = "log" };
        var r = new LeftoverScanner(new ScanEnvironment()).Scan(target, ScanMode.Safe);
        Assert.Contains(r.RawFindings, f => f.Location.EndsWith(@"PF\NewApp") && f.Reason.StartsWith("Recorded during installation"));
        Assert.Contains(r.RawFindings, f => f.Location.EndsWith(@"Software\NewApp"));
        var node = r.Files.Single(f => f.FsPath!.EndsWith(@"PF\NewApp"));
        Assert.Equal(FindingCategory.TrackedInstallChange, node.Category);
        Assert.Equal(Confidence.High, node.Safety.Confidence);
    }

    [Fact]
    public void RecordedItemsInWindowsOwnSettingsAreLowConfidenceInTheUninstaller()
    {
        var noise = _sb.CreateKey(@"Software\Microsoft\Windows\CurrentVersion\AppListBackup\List_1", ("x", "1"));
        var own = _sb.CreateKey(@"Software\NewVendorApp", ("x", "1"));
        var target = new ScanTarget
        {
            DisplayName = "New App 1.0",
            TrackedChanges = [new TrackedChange(ChangeArea.RegistryKey, ChangeType.Added, noise.FullPath), new TrackedChange(ChangeArea.RegistryKey, ChangeType.Added, own.FullPath)],
        };
        var r = new LeftoverScanner(new ScanEnvironment(), checkByDefault: true).Scan(target, ScanMode.Safe);
        var nodes = r.RegistryRoot.Descendants().Where(n => n.IsCreated).ToList();
        var noiseNode = nodes.Single(n => n.Key!.FullPath == noise.FullPath);
        var ownNode = nodes.Single(n => n.Key!.FullPath == own.FullPath);
        Assert.Equal(FindingCategory.TrackedInWindowsArea, noiseNode.Category);
        Assert.Equal(Confidence.Low, noiseNode.Safety.Confidence);
        Assert.False(noiseNode.IsChecked);              // never pre-selected
        Assert.Equal(Confidence.High, ownNode.Safety.Confidence);
        Assert.True(ownNode.IsChecked);
    }

    [Fact]
    public void RealSystemSnapshotIsFastEnoughAndConsistent()
    {
        var sw = Stopwatch.StartNew();
        var o = SnapshotOptions.CreateDefault();
        var a = SystemSnapshot.Capture(o, "a");
        var elapsed = sw.Elapsed;
        Assert.True(a.Files.Count > 1000);
        Assert.True(a.Registry.Count > 1000);
        Assert.NotEmpty(a.Services);
        Assert.NotEmpty(a.Tasks);
        Assert.True(elapsed < TimeSpan.FromMinutes(5), $"Snapshot took {elapsed}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "unravel-snapshot-stats.txt"),
            $"files {a.Files.Count}, keys {a.Registry.Count}, values {a.Registry.Sum(k => k.Value.Count)}, services {a.Services.Count}, tasks {a.Tasks.Count}, startup {a.Startup.Count}, time {elapsed}");
    }
}
