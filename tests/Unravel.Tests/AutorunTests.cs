using Microsoft.Win32;
using Unravel.Core.Autorun;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class AutorunTests : IDisposable
{
    private readonly RegistrySandbox _sb = new();
    private readonly TempDir _t = new();
    private readonly AutorunEnvironment _env;
    private readonly string _exe;
    private readonly string _running;

    public AutorunTests()
    {
        _exe = _t.File(@"apps\tray.exe", "MZ");
        _running = Environment.ProcessPath!; // the test host itself is guaranteed to be running
        var userRun = _sb.CreateKey("UserRun", ("Tray", $"\"{_exe}\" /min"), ("Host", $"\"{_running}\""), ("Ghost", @"C:\nope\missing.exe"));
        var userRunOnce = _sb.CreateKey("UserRunOnce", ("Once", $"\"{_exe}\" /once"));
        var machineRun = _sb.CreateKey("MachineRun");
        _env = new AutorunEnvironment
        {
            RunKeys =
            [
                new RunKeySpec(userRun, AutorunScope.CurrentUser, false, false, _sb.Path(@"Approved\Run")),
                new RunKeySpec(userRunOnce, AutorunScope.CurrentUser, false, true, null),
                new RunKeySpec(machineRun, AutorunScope.AllUsers, false, false, _sb.Path(@"Approved\MachineRun")),
            ],
            StartupFolders = [new StartupFolderSpec(_t.Dir("Startup"), AutorunScope.CurrentUser, _sb.Path(@"Approved\StartupFolder"))],
            ParkingKey = _sb.Path("Parked"),
            IncludeServices = false,
        };
    }

    public void Dispose() { _sb.Dispose(); _t.Dispose(); }

    private AutorunEntry Get(AutorunManager m, string name) => m.Load().Single(e => e.Name == name);

    [Fact]
    public void LoadsEntriesWithStatusAndDetails()
    {
        var m = new AutorunManager(_env);
        var all = m.Load();
        Assert.Equal(["Ghost", "Host", "Once", "Tray"], all.Select(e => e.Name).OrderBy(n => n));
        Assert.Equal(AutorunStatus.Invalid, all.Single(e => e.Name == "Ghost").Status);
        Assert.Equal(AutorunStatus.Running, all.Single(e => e.Name == "Host").Status);
        var tray = all.Single(e => e.Name == "Tray");
        Assert.Equal(AutorunStatus.NotRunning, tray.Status);
        Assert.Equal(_exe, tray.TargetPath);
        Assert.Equal("/min", tray.Arguments);
        Assert.True(tray.Enabled);
        Assert.Equal("Registry: Run", tray.SourceText);
        Assert.Equal(AutorunSource.RegistryRunOnce, all.Single(e => e.Name == "Once").Source);
    }

    [Fact]
    public void DisableAndEnableUseStartupApprovedFlags()
    {
        var m = new AutorunManager(_env);
        m.SetEnabled(Get(m, "Tray"), false);
        using (var k = _sb.Path(@"Approved\Run").Open())
        {
            var bytes = (byte[])k!.GetValue("Tray")!;
            Assert.Equal(12, bytes.Length);
            Assert.Equal(0x03, bytes[0]);
            Assert.NotEqual(0L, BitConverter.ToInt64(bytes, 4)); // disable timestamp, like Task Manager writes
        }
        Assert.False(Get(m, "Tray").Enabled);
        // The Run value itself is untouched, so the entry stays in the list.
        using (var run = _sb.Path("UserRun").Open()) Assert.NotNull(run!.GetValue("Tray"));
        m.SetEnabled(Get(m, "Tray"), true);
        Assert.True(Get(m, "Tray").Enabled);
        using (var k = _sb.Path(@"Approved\Run").Open()) Assert.Equal(0x02, ((byte[])k!.GetValue("Tray")!)[0]);
    }

    [Fact]
    public void ReadsExistingTaskManagerDisabledState()
    {
        _sb.SetValue(@"Approved\Run", "Tray", new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueKind.Binary);
        Assert.False(Get(new AutorunManager(_env), "Tray").Enabled);
        _sb.SetValue(@"Approved\Run", "Tray", new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.True(Get(new AutorunManager(_env), "Tray").Enabled);
    }

    [Fact]
    public void RunOnceIsParkedWhenDisabledAndRestoredWhenEnabled()
    {
        var m = new AutorunManager(_env);
        m.SetEnabled(Get(m, "Once"), false);
        using (var k = _sb.Path("UserRunOnce").Open()) Assert.Null(k!.GetValue("Once"));
        var parked = Get(m, "Once");
        Assert.False(parked.Enabled);
        Assert.True(parked.IsParkedRunOnce);
        m.SetEnabled(parked, true);
        using (var k = _sb.Path("UserRunOnce").Open()) Assert.Equal($"\"{_exe}\" /once", k!.GetValue("Once"));
        Assert.True(Get(m, "Once").Enabled);
    }

    [Fact]
    public void AddsRunAndStartupFolderEntriesAndRejectsDuplicates()
    {
        var m = new AutorunManager(_env);
        var e = m.Add("My Tool", _exe, "--quiet", AutorunScope.CurrentUser, AutorunSource.RegistryRun);
        Assert.Equal($"\"{_exe}\" --quiet", e.Command);
        Assert.Throws<InvalidOperationException>(() => m.Add("My Tool", _exe, "", AutorunScope.CurrentUser, AutorunSource.RegistryRun));

        var s = m.Add("Folder Tool", _exe, "-x", AutorunScope.CurrentUser, AutorunSource.StartupFolder);
        Assert.Equal(AutorunSource.StartupFolder, s.Source);
        Assert.True(File.Exists(Path.Combine(_t.Path("Startup"), "Folder Tool.lnk")));
        Assert.Equal(_exe, s.TargetPath, ignoreCase: true);

        var once = m.Add("Next Boot", _exe, "", AutorunScope.CurrentUser, AutorunSource.RegistryRunOnce);
        Assert.Equal(AutorunSource.RegistryRunOnce, once.Source);
    }

    [Fact]
    public void AddValidatesInput()
    {
        var m = new AutorunManager(_env);
        Assert.Throws<ArgumentException>(() => m.Add("", _exe, "", AutorunScope.CurrentUser, AutorunSource.RegistryRun));
        Assert.Throws<FileNotFoundException>(() => m.Add("x", @"C:\nope\x.exe", "", AutorunScope.CurrentUser, AutorunSource.RegistryRun));
    }

    [Fact]
    public void StartupFolderEntriesCanBeDisabledAndRemoved()
    {
        var m = new AutorunManager(_env);
        var s = m.Add("Folder Tool", _exe, "", AutorunScope.CurrentUser, AutorunSource.StartupFolder);
        m.SetEnabled(s, false);
        using (var k = _sb.Path(@"Approved\StartupFolder").Open()) Assert.Equal(0x03, ((byte[])k!.GetValue("Folder Tool.lnk")!)[0]);
        Assert.False(Get(m, "Folder Tool").Enabled);
        // Remove goes to the Recycle Bin for startup files; use a direct check that the shortcut is gone.
        m.Remove(Get(m, "Folder Tool"));
        Assert.False(File.Exists(Path.Combine(_t.Path("Startup"), "Folder Tool.lnk")));
        Assert.DoesNotContain(m.Load(), x => x.Name == "Folder Tool");
    }

    [Fact]
    public void RemoveDeletesRunValueAndApprovalFlag()
    {
        var m = new AutorunManager(_env);
        m.SetEnabled(Get(m, "Tray"), false);
        m.Remove(Get(m, "Tray"));
        using (var run = _sb.Path("UserRun").Open()) Assert.Null(run!.GetValue("Tray"));
        using (var ap = _sb.Path(@"Approved\Run").Open()) Assert.Null(ap!.GetValue("Tray"));
    }

    [Fact]
    public void RunAndKillAProcess()
    {
        // Use a real, harmless executable: a copy of the system's ping running for a while.
        var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        var copy = _t.Path("apps", "pingcopy.exe");
        File.Copy(ping, copy);
        _sb.SetValue("UserRun", "Pinger", $"\"{copy}\" -n 30 127.0.0.1");
        var m = new AutorunManager(_env);
        var e = Get(m, "Pinger");
        Assert.Equal(AutorunStatus.NotRunning, e.Status);
        AutorunManager.Run(e);
        var deadline = DateTime.Now.AddSeconds(10);
        while (AutorunManager.RefreshStatus(e) != AutorunStatus.Running && DateTime.Now < deadline) Thread.Sleep(100);
        Assert.Equal(AutorunStatus.Running, AutorunManager.RefreshStatus(e));
        var (killed, failed) = AutorunManager.Kill(e);
        Assert.True(killed >= 1);
        Assert.Equal(0, failed);
        Assert.Equal(AutorunStatus.NotRunning, AutorunManager.RefreshStatus(e));
    }

    [Fact]
    public void RunningInvalidEntryFails()
    {
        var m = new AutorunManager(_env);
        Assert.Throws<FileNotFoundException>(() => AutorunManager.Run(Get(m, "Ghost")));
    }

    [Fact]
    public void ApprovalBytesFormat()
    {
        Assert.Equal(0x02, AutorunManager.ApprovalBytes(true)[0]);
        Assert.Equal(0x03, AutorunManager.ApprovalBytes(false)[0]);
        Assert.Equal(12, AutorunManager.ApprovalBytes(false).Length);
    }

    [Fact]
    public void DefaultEnvironmentLoadsWithoutErrors()
    {
        var list = new AutorunManager(AutorunEnvironment.CreateDefault()).Load();
        Assert.All(list, e => Assert.False(string.IsNullOrEmpty(e.Name)));
    }
}
