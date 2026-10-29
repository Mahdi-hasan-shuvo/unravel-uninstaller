using Microsoft.Win32;
using Unravel.Core.Leftovers;
using Unravel.Core.Logging;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class SafetyEngineTests
{
    private static readonly SafetyEngine.Context NoProcesses = new() { RunningImages = [], CheckSignatures = true };

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\RpcSs")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip")]   // a kernel driver
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Vendor")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE")]
    public void CriticalRegistryLocationsAreBlocked(string path)
    {
        Assert.True(SafetyEngine.IsCriticalRegistry(RegPath.Parse(path)));
        var a = SafetyEngine.AssessRegistry(RegPath.Parse(path), null, FindingCategory.ProgramKey, true);
        Assert.True(a.IsBlocked);
        Assert.False(a.IsSafeToAutoSelect);
    }

    [Fact]
    public void OrdinaryProgramKeysAreHighConfidence()
    {
        var a = SafetyEngine.AssessRegistry(RegPath.Parse(@"HKCU\Software\Contoso\Widget"), null, FindingCategory.ProgramKey, true);
        Assert.Equal(Confidence.High, a.Confidence);
        Assert.True(a.IsSafeToAutoSelect);
        Assert.False(SafetyEngine.IsCriticalRegistry(RegPath.Parse(@"HKLM\SYSTEM\CurrentControlSet\Services\SomeVendorUpdater")));
    }

    [Theory]
    [InlineData(FindingCategory.UninstallRegistration, Confidence.High)]
    [InlineData(FindingCategory.InstallFolder, Confidence.High)]
    [InlineData(FindingCategory.Service, Confidence.Medium)]
    [InlineData(FindingCategory.DataFolder, Confidence.Medium)]
    [InlineData(FindingCategory.DeepNameMatch, Confidence.Low)]
    [InlineData(FindingCategory.OrphanCandidate, Confidence.Low)]
    public void BaseConfidencePerCategory(FindingCategory c, Confidence expected) => Assert.Equal(expected, SafetyEngine.BaseFor(c));

    [Fact]
    public void ItemsNotCreatedByTheProgramOrSharedAreLowAndNeverAutoSelected()
    {
        var notCreated = SafetyEngine.AssessRegistry(RegPath.Parse(@"HKCU\Software\X"), null, FindingCategory.ProgramKey, created: false);
        Assert.Equal(Confidence.Low, notCreated.Confidence);
        Assert.False(notCreated.IsSafeToAutoSelect);
        var shared = SafetyEngine.AssessRegistry(RegPath.Parse(@"HKCU\Software\X"), null, FindingCategory.ProgramKey, true,
            "Program settings key (name also matches another installed program – verify manually)");
        Assert.True(shared.Risks.HasFlag(RiskFlags.SharedWithOtherProgram));
        Assert.False(shared.IsSafeToAutoSelect);
    }

    [Fact]
    public void WindowsFoldersAndRootFilesAreCritical()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(SafetyEngine.IsCriticalPath(Path.Combine(win, "System32", "drivers", "etc")));
        Assert.True(SafetyEngine.IsCriticalPath(@"C:\"));
        Assert.True(SafetyEngine.IsCriticalPath(Path.Combine(Path.GetPathRoot(win)!, "pagefile.sys")));
        Assert.True(SafetyEngine.IsCriticalPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")));
        // Program Files itself contains WindowsApps, so it is critical as a whole.
        Assert.True(SafetyEngine.IsCriticalPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(SafetyEngine.IsCriticalPath(Path.Combine(Path.GetTempPath(), "some-app-folder")));
    }

    [Fact]
    public void FolderWithRunningProgramIsMarkedInUseAndLowered()
    {
        using var t = new TempDir();
        var exe = t.File(@"App\tool.exe", "MZ");
        var ctx = new SafetyEngine.Context { RunningImages = [exe], CheckSignatures = false };
        var a = SafetyEngine.AssessPath(t.Path("App"), FindingCategory.InstallFolder, true, ctx);
        Assert.True(a.Risks.HasFlag(RiskFlags.InUse));
        Assert.Equal(Confidence.Medium, a.Confidence);
        Assert.False(a.IsSafeToAutoSelect);
        Assert.Contains("tool.exe", a.Explanation);
    }

    [Fact]
    public void DataFolderContainingMicrosoftSignedProgramIsLow()
    {
        using var t = new TempDir();
        // A real Microsoft-signed executable copied into a "data" folder.
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), t.Path("ping.exe"));
        var a = SafetyEngine.AssessPath(t.Root, FindingCategory.DataFolder, true, NoProcesses);
        Assert.True(a.Risks.HasFlag(RiskFlags.MicrosoftSigned));
        Assert.Equal(Confidence.Low, a.Confidence);
        // When the program being removed is itself Microsoft's, its own signed files are expected.
        var own = SafetyEngine.AssessPath(t.Root, FindingCategory.DataFolder, true, new SafetyEngine.Context { TargetIsMicrosoft = true });
        Assert.False(own.Risks.HasFlag(RiskFlags.MicrosoftSigned));
    }

    [Fact]
    public void SignatureChecksRecognizeMicrosoftAndUnsignedFiles()
    {
        var notepad = FileSignature.Check(Path.Combine(Environment.SystemDirectory, "PING.EXE"));
        Assert.True(notepad.IsSigned);
        Assert.True(notepad.IsMicrosoft);
        using var t = new TempDir();
        var fake = t.File("fake.exe", "MZ not really a program");
        Assert.False(FileSignature.Check(fake).IsSigned);
    }

    [Fact]
    public void BlockedNodesCannotBeCheckedOrDeleted()
    {
        var n = new LeftoverNode(LeftoverKind.RegistryKey, "Control") { Key = RegPath.Parse(@"HKLM\SYSTEM\CurrentControlSet\Control") };
        n.IsCreated = true;
        n.Safety = SafetyEngine.AssessRegistry(n.Key!, null, FindingCategory.ProgramKey, true);
        n.IsChecked = true;
        Assert.False(n.IsChecked);
        Assert.False(n.WillDelete);
        var root = new LeftoverNode(LeftoverKind.Container, "Computer");
        root.Add(n);
        root.IsChecked = true; // checking the parent must not check a blocked child
        Assert.False(n.IsChecked);
        Assert.Empty(LeftoverRemover.SelectRegistry(root));
    }
}

public class ScannerSafetyIntegrationTests
{
    [Fact]
    public void EveryScannerReasonMapsToACategory()
    {
        foreach (var reason in LeftoverScanner.KnownReasons)
            Assert.NotEqual(FindingCategory.Other, LeftoverScanner.CategoryFor(reason));
        Assert.Equal(FindingCategory.InstallFolder, LeftoverScanner.CategoryFor("Install folder – also used by X"));
    }
}

public class QuarantineTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void MovesAndRestoresFilesAndFolders()
    {
        var q = new Quarantine(_t.Path("q"));
        var f = _t.File(@"data\a.txt", "alpha");
        _t.File(@"data\sub\b.txt", "beta");
        var lone = _t.File("lone.bin", "1234");
        var (set, results) = q.Move([_t.Path("data"), lone], "test set");
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.False(Directory.Exists(_t.Path("data")));
        Assert.False(File.Exists(lone));
        Assert.Equal(13, set.TotalSize); // alpha + beta + 1234

        var listed = Assert.Single(q.List());
        Assert.Equal("test set", listed.Title);
        Assert.Equal(2, listed.Entries.Count);

        var restored = q.Restore(listed);
        Assert.All(restored, r => Assert.True(r.Ok, r.Error));
        Assert.Equal("alpha", File.ReadAllText(f));
        Assert.Equal("beta", File.ReadAllText(_t.Path("data", "sub", "b.txt")));
        Assert.True(File.Exists(lone));
        Assert.Empty(q.List());
    }

    [Fact]
    public void RestoreDoesNotOverwriteANewFileAtTheOriginalPath()
    {
        var q = new Quarantine(_t.Path("q"));
        var f = _t.File("doc.txt", "old");
        var (set, _) = q.Move([f], "x");
        File.WriteAllText(f, "new");
        var r = Assert.Single(q.Restore(set));
        Assert.True(r.Ok);
        Assert.Equal("new", File.ReadAllText(f));
        Assert.Equal("old", File.ReadAllText(_t.Path("doc (restored).txt")));
    }

    [Fact]
    public void PurgeDeletesPermanentlyAndMissingItemsAreReported()
    {
        var q = new Quarantine(_t.Path("q"));
        var (set, results) = q.Move([_t.File("a.txt"), _t.Path("missing.txt")], "x");
        Assert.Single(results, r => !r.Ok);
        q.Purge(set);
        Assert.Empty(q.List());
    }
}

public class ActivityLogTests
{

    [Fact]
    public void WritesJsonLinesAndKeepsRecentEntries()
    {
        LogEntry? seen = null;
        void Handler(LogEntry e) => seen = e;
        ActivityLog.EntryAdded += Handler;
        try
        {
            ActivityLog.Action("Test", "Deleted 3 items", "detail line");
            ActivityLog.Error("Test", "Something failed");
        }
        finally { ActivityLog.EntryAdded -= Handler; }
        Assert.Equal("Something failed", seen!.Message);
        var day = ActivityLog.ReadDay(DateTime.Now).Where(e => e.Area == "Test").ToList(); // other tests log in parallel
        Assert.Equal(2, day.Count);
        Assert.Equal(LogLevel.Action, day[0].Level);
        Assert.Equal("detail line", day[0].Detail);
        Assert.Contains(DateTime.Today, ActivityLog.AvailableDays());
        Assert.True(ActivityLog.Recent.Count >= 2);
    }
}
