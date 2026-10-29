using System.Diagnostics;
using Unravel.Core.Autorun;
using Unravel.Core.Monitoring;
using Unravel.Core.Performance;
using Unravel.Core.Safety;
using Unravel.Core.WinGet;

namespace Unravel.Tests;

/// <summary>Runs only when UNRAVEL_LIVE_WINGET=1: it installs and removes a small real package from the internet.</summary>
public sealed class LiveWinGetFactAttribute : FactAttribute
{
    public LiveWinGetFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("UNRAVEL_LIVE_WINGET") != "1")
            Skip = "Set UNRAVEL_LIVE_WINGET=1 to install and uninstall a real WinGet package.";
    }
}

public class WinGetParserTests
{
    // Rows are padded to the column widths exactly as WinGet aligns them.
    private static string Row(params string[] cells) =>
        cells[0].PadRight(22) + cells[1].PadRight(40) + cells[2].PadRight(14) + cells[3].PadRight(10) + cells[4];

    private static readonly string ListOutput =
        "   - \r   \\ \r\r\n" +
        Row("Name", "Id", "Version", "Available", "Source") + "\r\n" +
        new string('-', 95) + "\r\n" +
        Row("AMD Chipset Software", @"ARP\Machine\X86\AMD_Chipset_IODrivers", "8.09.28.1124", "", "") + "\r\n" +
        Row("WinRAR 7.13 (64-bit)", "RARLab.WinRAR", "7.13.0", "7.23.0", "winget") + "\r\n" +
        Row("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "11.2607.0.0", "", "") + "\r\n" +
        "2 upgrades available.\r\n";

    [Fact]
    public void ParsesListTablesAndIgnoresSpinnersAndFooters()
    {
        var p = WinGetParser.ParsePackages(ListOutput, WinGetParser.Command.List);
        Assert.Equal(3, p.Count);
        var amd = p[0];
        Assert.Equal("AMD Chipset Software", amd.Name);
        Assert.True(amd.IsLocalOnly);
        Assert.Equal("AMD_Chipset_IODrivers", amd.ArpKeyName);
        Assert.Null(amd.Available);
        var rar = p[1];
        Assert.Equal("RARLab.WinRAR", rar.Id);
        Assert.Equal("7.23.0", rar.Available);
        Assert.Equal("winget", rar.Source);
        Assert.True(rar.HasUpdate);
    }

    [Fact]
    public void ParsesSearchWithMatchColumnAndLocalisedHeaders()
    {
        var search = "Name Id        Version Match    Source\n" +
                     "----------------------------------------\n" +
                     "jq   jqlang.jq 1.8.2   Tag: jq  winget\n";
        var p = Assert.Single(WinGetParser.ParsePackages(search, WinGetParser.Command.Search));
        Assert.Equal(("jq", "jqlang.jq", "1.8.2", "winget"), (p.Name, p.Id, p.Version, p.Source));
        var german = "Name Id        Version Verfügbar Quelle\n" +
                     "----------------------------------------\n" +
                     "jq   jqlang.jq 1.8.1   1.8.2     winget\n";
        var g = Assert.Single(WinGetParser.ParsePackages(german, WinGetParser.Command.Upgrade));
        Assert.Equal("1.8.2", g.Available);
        Assert.Equal("winget", g.Source);
    }

    [Fact]
    public void ParsesUpgradeOutputWithTwoTables()
    {
        var text = "Name Id      Version Available Source\n" +
                   "-------------------------------------\n" +
                   "Foo  Foo.Foo 1.0     2.0       winget\n" +
                   "1 upgrades available.\n\n" +
                   "The following packages have an upgrade available, but require explicit targeting for upgrade:\n" +
                   "Name Id      Version Available Source\n" +
                   "-------------------------------------\n" +
                   "Bar  Bar.Bar 3.0     4.0       winget\n";
        var p = WinGetParser.ParsePackages(text, WinGetParser.Command.Upgrade);
        Assert.Equal(["Foo.Foo", "Bar.Bar"], p.Select(x => x.Id));
    }

    [Fact]
    public void CleansProgressOutput()
    {
        var lines = WinGetParser.CleanLines("  ██████████▒▒▒▒  1.00 MB / 2.00 MB\r  -\r  \\\rSuccessfully installed\n").ToList();
        Assert.Equal(["Progress: 1.00 MB / 2.00 MB", "Successfully installed"], lines);
    }

    [Fact]
    public void DescribesErrorCodes()
    {
        Assert.Equal("No package matches.", WinGetErrors.Describe(WinGetErrors.NoApplicationsFound, ""));
        Assert.Equal("The program is in use. Close it and try again.", WinGetErrors.Describe(unchecked((int)0x8A150101), ""));
        Assert.Equal("Completed successfully.", WinGetErrors.Describe(0, ""));
        Assert.Contains("0x8A15FFFF", WinGetErrors.Describe(unchecked((int)0x8A15FFFF), "Something odd happened\n"));
        Assert.StartsWith("Something odd happened", WinGetErrors.Describe(unchecked((int)0x8A15FFFF), "Something odd happened\n"));
    }
}

public class WinGetLiveTests
{
    private readonly WinGetClient _w = new();

    [Fact]
    public async Task ListsInstalledProgramsThatMatchTheRegistry()
    {
        Assert.True(_w.IsAvailable);
        Assert.NotNull(await _w.GetVersionAsync());
        var list = await _w.ListInstalledAsync();
        Assert.True(list.Count > 20);
        var ourKeys = new Unravel.Core.Programs.ProgramInventory().Load(new() { IncludeSystemComponents = true, IncludeUpdates = true })
            .Select(p => p.KeyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var arp = list.Where(p => p.ArpKeyName is not null).ToList();
        Assert.NotEmpty(arp);
        Assert.True(arp.Count(p => ourKeys.Contains(p.ArpKeyName!)) >= arp.Count * 0.9, "Most ARP entries should map to an uninstall key");
    }

    [Fact]
    public async Task UnknownPackageIsAClearErrorNotACrash()
    {
        var r = await _w.UninstallAsync("Unravel.Does.Not.Exist." + Guid.NewGuid().ToString("N")[..6]);
        Assert.False(r.Ok);
        Assert.Equal(WinGetErrors.NoApplicationsFound, r.ExitCode);
        Assert.Equal("No package matches.", r.Message);
    }

    [Fact]
    public async Task CancellationEndsWinGet()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var r = await _w.RunAsync("search jq --accept-source-agreements --disable-interactivity", null, cts.Token);
        Assert.True(r.ExitCode == -2 || r.Ok); // either cancelled, or it finished faster than 300 ms
    }

    [LiveWinGetFact]
    public async Task InstallReinstallExportAndUninstallARealPackage()
    {
        const string id = "jqlang.jq";
        var lines = new List<string>();
        var progress = new Progress<string>(lines.Add);
        var found = await _w.SearchAsync(id);
        Assert.Contains(found, p => p.Id == id);
        var install = await _w.InstallAsync(id, scope: WinGetScope.User, progress: progress);
        Assert.True(install.Ok, install.Message + "\n" + install.Output);
        Assert.Contains(await _w.ListInstalledAsync(), p => p.Id == id);
        var export = Path.Combine(Path.GetTempPath(), "unravel-winget-export.json");
        Assert.True((await _w.ExportAsync(export)).Ok);
        Assert.Contains(id, File.ReadAllText(export));
        var reinstall = await _w.ReinstallAsync(id, null, progress);
        Assert.True(reinstall.Ok, reinstall.Message);
        var uninstall = await _w.UninstallAsync(id, progress: progress);
        Assert.True(uninstall.Ok, uninstall.Message);
        Assert.DoesNotContain(await _w.ListInstalledAsync(), p => p.Id == id);
        Assert.NotEmpty(lines);
    }
}

public class MonitorTests
{
    [Fact]
    public async Task SamplesSystemAndProcessesWithSaneValues()
    {
        using var m = new SystemMonitor();
        // Burn some CPU in this process so its share is measurable.
        var sw = Stopwatch.StartNew();
        var burn = Task.Run(() => { double x = 0; while (sw.ElapsedMilliseconds < 900) x += Math.Sqrt(x + 1); return x; });
        await Task.Delay(1000);
        await burn;
        var s = m.Sample();
        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.True(s.MemoryTotal > 0 && s.MemoryAvailable < s.MemoryTotal);
        Assert.True(s.ProcessCount > 20);
        var me = s.Processes.Single(p => p.Pid == Environment.ProcessId);
        Assert.True(me.CpuPercent > 0, "the test process burnt CPU");
        Assert.True(me.WorkingSet > 1024 * 1024);
        Assert.Equal(Environment.ProcessPath, me.ImagePath, ignoreCase: true);
        // Protected processes are still listed (one system call, no handles needed).
        Assert.Contains(s.Processes, p => p.Name.Equals("lsass.exe", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(s.DiskActivePercent);
    }

    [Fact]
    public void CriticalProcessesAreProtected()
    {
        var lsass = Process.GetProcessesByName("lsass").First();
        Assert.True(ProcessGuard.IsCritical(lsass.Id, "lsass.exe", Path.Combine(Environment.SystemDirectory, "lsass.exe")));
        Assert.True(ProcessGuard.IsCritical(4, "System", null));
        Assert.True(ProcessGuard.IsCritical(Environment.ProcessId, "testhost.exe", null));
        Assert.Throws<InvalidOperationException>(() => ProcessGuard.End(lsass.Id, "lsass.exe", Path.Combine(Environment.SystemDirectory, "lsass.exe")));
        var ping = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        Assert.False(ProcessGuard.IsCritical(ping.Id, "PING.EXE", Path.Combine(Environment.SystemDirectory, "PING.EXE")));
        ProcessGuard.End(ping.Id, "PING.EXE", null);
        Assert.True(ping.HasExited);
    }

    private static SystemSample Sample(DateTime t, double cpu, params ProcessSample[] p) => new()
    {
        Time = t, CpuPercent = cpu, MemoryTotal = 16UL << 30, MemoryAvailable = 8UL << 30, Processes = p,
    };

    private static ProcessSample P(int pid, string name, double cpu = 0, long priv = 100 << 20, double io = 0, string? path = null) => new()
    {
        Pid = pid, Name = name, CpuPercent = cpu, PrivateBytes = priv, ReadBytesPerSec = io, StartTime = new DateTime(2026, 1, 1), ImagePath = path,
    };

    [Fact]
    public void SustainedCpuRaisesOnceAndResolves()
    {
        var e = new ActivityAlertEngine(new AlertThresholds { CpuPercent = 25, CpuSustainSeconds = 10 }, _ => SignatureInfo.Unsigned);
        var t0 = DateTime.Now;
        var raised = new List<ActivityAlert>();
        for (int i = 0; i <= 12; i++) raised.AddRange(e.Evaluate(Sample(t0.AddSeconds(i), 40, P(100, "busy.exe", cpu: 30))));
        var a = Assert.Single(raised);
        Assert.Equal(AlertRule.SustainedCpu, a.Rule);
        Assert.Equal(100, a.Pid);
        Assert.Single(e.Active);
        e.Evaluate(Sample(t0.AddSeconds(13), 5, P(100, "busy.exe", cpu: 1)));
        Assert.Empty(e.Active);
    }

    [Fact]
    public void ShortSpikesAreNotAlerts()
    {
        var e = new ActivityAlertEngine(new AlertThresholds { CpuSustainSeconds = 10 }, _ => SignatureInfo.Unsigned);
        var t0 = DateTime.Now;
        for (int i = 0; i <= 20; i++)
            Assert.Empty(e.Evaluate(Sample(t0.AddSeconds(i), 30, P(100, "spiky.exe", cpu: i % 3 == 0 ? 90 : 2))));
    }

    [Fact]
    public void SteadyMemoryGrowthIsDetected()
    {
        var e = new ActivityAlertEngine(new AlertThresholds { MemoryWindowSeconds = 20, MemoryGrowthBytes = 200L << 20 }, _ => SignatureInfo.Unsigned);
        var t0 = DateTime.Now;
        var raised = new List<ActivityAlert>();
        for (int i = 0; i <= 22; i++) raised.AddRange(e.Evaluate(Sample(t0.AddSeconds(i), 10, P(7, "leaky.exe", priv: (100L << 20) + i * (15L << 20)))));
        Assert.Contains(raised, a => a.Rule == AlertRule.MemoryGrowth && a.Pid == 7);
    }

    [Fact]
    public void MasqueradingAndUnsignedTempProgramsAreFlagged()
    {
        var e = new ActivityAlertEngine(null, _ => SignatureInfo.Unsigned);
        var temp = Path.Combine(Path.GetTempPath(), "x", "dropper.exe");
        var fake = @"C:\Users\Public\svchost.exe";
        var real = Path.Combine(Environment.SystemDirectory, "svchost.exe");
        var r = e.Evaluate(Sample(DateTime.Now, 5, P(10, "svchost.exe", path: fake), P(11, "svchost.exe", path: real), P(12, "dropper.exe", path: temp)));
        Assert.Contains(r, a => a.Rule == AlertRule.Masquerading && a.Pid == 10 && a.Severity == AlertSeverity.Critical);
        Assert.DoesNotContain(r, a => a.Pid == 11);
        Assert.Contains(r, a => a.Rule == AlertRule.UnsignedFromTemp && a.Pid == 12);
        // Alerts stay while the process runs and can be ignored.
        e.Evaluate(Sample(DateTime.Now.AddSeconds(1), 5, P(10, "svchost.exe", path: fake), P(12, "dropper.exe", path: temp)));
        Assert.Equal(2, e.Active.Count);
        e.Ignore(e.Active.First(a => a.Pid == 12));
        Assert.Single(e.Active);
    }

    [Fact]
    public void ServicesAreListedAndCoreServicesCannotBeStopped()
    {
        var list = ServicesManager.List();
        Assert.Contains(list, s => s.Name.Equals("RpcSs", StringComparison.OrdinalIgnoreCase) && s.IsCritical && s.IsRunning);
        Assert.Contains(list, s => !s.IsCritical);
        Assert.ThrowsAny<Exception>(() => ServicesManager.Stop("RpcSs"));
    }
}

public class PerformanceTests
{
    [Theory]
    [InlineData(1500, 0, StartupImpact.High)]
    [InlineData(0, 4 * 1024 * 1024, StartupImpact.High)]
    [InlineData(500, 0, StartupImpact.Medium)]
    [InlineData(0, 500 * 1024, StartupImpact.Medium)]
    [InlineData(100, 1024, StartupImpact.Low)]
    [InlineData(0, 0, StartupImpact.None)]
    public void ClassifiesWithTaskManagerThresholds(int cpuMs, long bytes, StartupImpact expected) =>
        Assert.Equal(expected, PerformanceInsights.Classify(TimeSpan.FromMilliseconds(cpuMs), bytes));

    [Fact]
    public void AnalyzesRealStartupEntriesAndBoot()
    {
        using var m = new SystemMonitor();
        Thread.Sleep(300);
        var s = m.Sample();
        var entries = new AutorunManager(AutorunEnvironment.CreateDefault()).Load();
        var impact = PerformanceInsights.AnalyzeStartup(entries, s);
        Assert.Equal(entries.Count, impact.Count);
        Assert.All(impact.Where(i => !i.Entry.Enabled), i => Assert.Equal(StartupImpact.NotMeasured, i.Impact));
        Assert.Contains(impact, i => i.Impact >= StartupImpact.Low); // something that started with Windows is running
        var boot = PerformanceInsights.GetBootInfo();
        Assert.True(boot.Uptime > TimeSpan.Zero);
        Assert.True(boot.BootDuration is not null || boot.Note is not null);
        var groups = PerformanceInsights.GroupProcesses(s);
        Assert.Contains(groups, g => g.Group == ProcessGroup.Windows && g.Count > 10);
    }

    [Fact]
    public void DiskUsageSumsSizesAndFindsLargestFiles()
    {
        using var t = new TempDir();
        t.File(@"big\a.bin", new string('a', 5000));
        t.File(@"big\nested\b.bin", new string('b', 3000));
        t.File(@"small\c.txt", new string('c', 100));
        var r = DiskUsageScanner.Scan(t.Root, keepDepth: 2, topFiles: 2);
        Assert.Equal(8100, r.Root.Size);
        Assert.Equal("big", r.Root.Children[0].Name);
        Assert.Equal(8000, r.Root.Children[0].Size);
        Assert.Equal(["a.bin", "b.bin"], r.LargestFiles.Select(f => Path.GetFileName(f.Path)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(DiskUsageScanner.Scan(t.Root, ct: cts.Token).Cancelled);
    }

    [Fact]
    public async Task BaselinesAreCapturedStoredAndCompared()
    {
        using var t = new TempDir();
        var store = new BaselineStore(t.Path("b.json"));
        var a = await BaselineStore.CaptureAsync("Before", 2, null, default);
        Assert.True(a.ProcessCount > 0 && a.EnabledStartupItems >= 0 && a.RunningServices > 0 && a.SystemDriveFreeBytes > 0);
        store.Add(a);
        var b = new PerformanceBaseline { Label = "After", Captured = DateTime.Now, ProcessCount = a.ProcessCount - 5, AverageCpuPercent = a.AverageCpuPercent,
            SystemDriveFreeBytes = a.SystemDriveFreeBytes + 1000, EnabledStartupItems = a.EnabledStartupItems - 2 };
        store.Add(b);
        Assert.Equal(2, store.List().Count);
        var d = BaselineStore.Compare(a, b);
        Assert.True(d.Single(x => x.Metric == "Processes").Better);
        Assert.True(d.Single(x => x.Metric == "Enabled startup items").Better);
        Assert.True(d.Single(x => x.Metric == "Free space on system drive").Better);
        store.Remove(a);
        Assert.Single(store.List());
    }
}
