using System.Diagnostics;
using System.Text;
using Unravel.Core.Hunter;
using Unravel.Core.Programs;
using Unravel.Core.Secure;
using Unravel.Core.Tools;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class SecureDeleteTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private const string Secret = "TOP-SECRET-MARKER-0123456789";

    [Fact]
    public void OverwritesContentBeforeDeleting()
    {
        var f = _t.File("secret.txt", string.Concat(Enumerable.Repeat(Secret, 5000)));
        var originalLength = new FileInfo(f).Length;
        byte[]? afterOverwrite = null;
        var sd = new SecureDelete { Passes = 3, AfterOverwrite = p => afterOverwrite = File.ReadAllBytes(p) };
        var report = sd.Erase([f]);
        Assert.Equal(1, report.FilesErased);
        Assert.Empty(report.Failures);
        Assert.False(File.Exists(f));
        Assert.Empty(Directory.GetFiles(_t.Root)); // renamed copy is gone too
        Assert.NotNull(afterOverwrite);
        Assert.Equal(originalLength, afterOverwrite!.Length);
        Assert.DoesNotContain(Secret, Encoding.ASCII.GetString(afterOverwrite));
        Assert.True(afterOverwrite.Distinct().Count() > 200); // random, not a constant fill
        Assert.Equal(originalLength * 3, report.BytesOverwritten);
    }

    [Fact]
    public void ErasesFoldersRecursivelyIncludingReadOnlyFiles()
    {
        _t.File(@"dir\a.txt", Secret);
        var ro = _t.File(@"dir\sub\b.txt", Secret);
        File.SetAttributes(ro, FileAttributes.ReadOnly | FileAttributes.Hidden);
        _t.Dir("dir", "empty");
        var report = new SecureDelete().Erase([_t.Path("dir")]);
        Assert.Equal(2, report.FilesErased);
        Assert.Equal(3, report.FoldersRemoved);
        Assert.False(Directory.Exists(_t.Path("dir")));
    }

    [Fact]
    public void JunctionInsideFolderDoesNotEraseItsTarget()
    {
        using var outside = new TempDir();
        var precious = outside.File("precious.txt", "keep me");
        _t.File(@"dir\a.txt", "x");
        var link = _t.Path("dir", "link");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Root}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        var report = new SecureDelete().Erase([_t.Path("dir")]);
        Assert.True(File.Exists(precious));
        Assert.Equal("keep me", File.ReadAllText(precious));
        Assert.False(Directory.Exists(_t.Path("dir")));
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void LockedFileIsReportedAndOthersStillErased()
    {
        var a = _t.File("a.txt", "a");
        var b = _t.File("b.txt", "b");
        using var hold = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        var report = new SecureDelete().Erase([a, b]);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Single(report.Failures);
    }

    [Fact]
    public void MissingPathIsReported()
    {
        var report = new SecureDelete().Erase([_t.Path("nope.txt")]);
        Assert.Single(report.Failures);
    }

    [Fact]
    public void CancellationStopsBeforeRemainingFiles()
    {
        var files = Enumerable.Range(0, 5).Select(i => _t.File($"f{i}.bin", new string('x', 10))).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = new SecureDelete().Erase(files, null, cts.Token);
        Assert.True(report.Cancelled);
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }
}

public class FreeSpaceWiperTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void WritesRequestedPassesAndCleansUp()
    {
        var progress = new List<WipeProgress>();
        var w = new FreeSpaceWiper { Passes = 2, MaxBytesPerPass = 6L * 1024 * 1024, MaxSmallFiles = 50, WorkFolderOverride = _t.Root };
        var r = w.Wipe(Path.GetPathRoot(_t.Root)!, new SyncProgress<WipeProgress>(progress.Add));
        Assert.Null(r.Error);
        Assert.False(r.Cancelled);
        Assert.Equal(2, r.PassesCompleted);
        Assert.Equal(6L * 1024 * 1024, r.BytesPerPass);
        Assert.Equal(50, r.SmallFilesWritten);
        Assert.True(r.TempCleanedUp);
        Assert.Empty(Directory.GetFileSystemEntries(_t.Root));
        Assert.Contains(progress, p => p.Pass == 2 && p.BytesWritten == p.BytesTarget);
    }

    [Fact]
    public void CancellationStillRemovesTemporaryFiles()
    {
        using var cts = new CancellationTokenSource();
        var w = new FreeSpaceWiper { Passes = 3, MaxBytesPerPass = 64L * 1024 * 1024, WorkFolderOverride = _t.Root };
        var r = w.Wipe(Path.GetPathRoot(_t.Root)!, new SyncProgress<WipeProgress>(p => { if (p.BytesWritten > 4 * 1024 * 1024) cts.Cancel(); }), cts.Token);
        Assert.True(r.Cancelled);
        Assert.True(r.TempCleanedUp);
        Assert.Empty(Directory.GetFileSystemEntries(_t.Root));
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T value) => a(value); }
}

public class WindowsToolsTests
{
    [Fact]
    public void CatalogHasEighteenToolsWithUniqueIds()
    {
        Assert.Equal(18, WindowsTools.All.Count);
        Assert.Equal(WindowsTools.All.Count, WindowsTools.All.Select(t => t.Id).Distinct().Count());
        Assert.All(WindowsTools.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Theory]
    [InlineData("restore")] [InlineData("netinfo")] [InlineData("sysprops")] [InlineData("msinfo")] [InlineData("osk")]
    [InlineData("defrag")] [InlineData("services")] [InlineData("shares")] [InlineData("features")] [InlineData("tasks")]
    [InlineData("chkdsk")] [InlineData("sfc")] [InlineData("security")] [InlineData("netstat")] [InlineData("netdiag")]
    public void CoreToolsAreAvailableOnThisWindows(string id) => Assert.True(WindowsTools.IsAvailable(WindowsTools.All.Single(t => t.Id == id)));
}

public class HunterResolverTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static InstalledProgram P(string name, string folder) => new()
    {
        DisplayName = name, InstallLocation = folder, RegistryKey = RegPath.Parse(@"HKCU\Software\" + name),
    };

    [Fact]
    public void MatchesMostSpecificInstallFolder()
    {
        var vendor = _t.Dir("Vendor");
        var app = _t.Dir("Vendor", "App");
        var exe = _t.File(@"Vendor\App\bin\app.exe", "MZ");
        var match = HunterResolver.MatchProgram(exe, [P("Vendor Suite", vendor), P("Vendor App", app), P("Other", _t.Dir("Other"))]);
        Assert.Equal("Vendor App", match!.DisplayName);
        Assert.Null(HunterResolver.MatchProgram(_t.File(@"Elsewhere\x.exe", "MZ"), [P("Vendor App", app)]));
    }

    [Fact]
    public void ResolvesShortcutByLabel()
    {
        var exe = _t.File(@"Prog\tool.exe", "MZ");
        var desktop = _t.Dir("Desktop");
        ShellLink.Create(Path.Combine(desktop, "My Tool.lnk"), exe);
        var target = HunterResolver.FromShortcutLabel("My Tool", [desktop]);
        Assert.NotNull(target);
        Assert.Equal(exe, target!.ExecutablePath, ignoreCase: true);
        Assert.Equal(HunterTargetKind.Shortcut, target.Kind);
        Assert.Null(HunterResolver.FromShortcutLabel("Nope", [desktop]));
    }

    [Fact]
    public void FindsWindowOfAnotherProcess()
    {
        // Start a process with a visible window and resolve it from a point inside that window.
        var p = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        Process? owner = null;
        var started = DateTime.Now.AddSeconds(-5);
        try
        {
            IntPtr hwnd = IntPtr.Zero;
            var deadline = DateTime.Now.AddSeconds(10);

            while (DateTime.Now < deadline && hwnd == IntPtr.Zero)
            {
                Thread.Sleep(200);
                // Notepad on Windows 11 may relaunch as a packaged app; find any notepad window.
                owner = Process.GetProcessesByName("notepad").FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero && x.StartTime >= started);
                hwnd = owner?.MainWindowHandle ?? IntPtr.Zero;
            }
            Assert.NotEqual(IntPtr.Zero, hwnd);
            Win32.SetForegroundWindow(hwnd);
            Thread.Sleep(300);
            Win32.GetWindowRect(hwnd, out var r);
            int x = (r.L + r.R) / 2, y = r.T + 10;
            var t = HunterResolver.FromWindowAt(x, y, Environment.ProcessId);
            Assert.NotNull(t);
            // Another (possibly elevated) window may cover Notepad on a live desktop, and Windows can refuse to bring
            // Notepad forward. Cross-check against the real owner of the topmost window at that point via another API.
            var top = Win32.GetAncestor(Win32.WindowFromPoint(new Win32.POINT { X = x, Y = y }), 2);
            Win32.GetWindowThreadProcessId(top, out int ownerPid);
            var ownerName = Process.GetProcessById(ownerPid).ProcessName;
            Assert.Equal(ownerName, Path.GetFileNameWithoutExtension(t!.ExecutablePath), ignoreCase: true);
            Assert.Equal(ownerPid, t.ProcessId);
            if (top == hwnd) Assert.EndsWith("notepad.exe", t.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { owner?.Kill(); } catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
    }

    private static class Win32
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        public struct RECT { public int L, T, R, B; }
        public struct POINT { public int X, Y; }
    }
}

