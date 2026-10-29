using Microsoft.Win32;
using Unravel.Core.Programs;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class ProgramInventoryTests : IDisposable
{
    private readonly RegistrySandbox _sb = new();
    private readonly ProgramInventory _inv;

    public ProgramInventoryTests()
    {
        var root = _sb.CreateKey("Uninstall");
        _sb.CreateKey(@"Uninstall\Contoso_is1",
            ("DisplayName", "Contoso Widget 2.1"), ("DisplayVersion", "2.1.0"), ("Publisher", "Contoso Ltd"),
            ("UninstallString", "\"C:\\Program Files\\Contoso\\unins000.exe\""), ("InstallLocation", @"C:\Program Files\Contoso\"),
            ("InstallDate", "20240315"), ("EstimatedSize", 2048), ("URLInfoAbout", "https://contoso.example"));
        _sb.CreateKey(@"Uninstall\{AAAAAAAA-1111-2222-3333-444444444444}",
            ("DisplayName", "Fabrikam Runtime"), ("WindowsInstaller", 1), ("UninstallString", "MsiExec.exe /I{AAAAAAAA-1111-2222-3333-444444444444}"));
        _sb.CreateKey(@"Uninstall\Hidden", ("DisplayName", "Hidden Component"), ("SystemComponent", 1), ("UninstallString", "x.exe"));
        _sb.CreateKey(@"Uninstall\KB500001", ("DisplayName", "Security Update for Thing (KB500001)"), ("UninstallString", "x.exe"));
        _sb.CreateKey(@"Uninstall\Child", ("DisplayName", "Child Patch"), ("ParentKeyName", "Contoso_is1"), ("UninstallString", "x.exe"));
        _sb.CreateKey(@"Uninstall\NoName", ("UninstallString", "x.exe"));
        _sb.CreateKey(@"Uninstall\NoUninstall", ("DisplayName", "Registered Without Uninstaller"));
        _inv = new ProgramInventory([new UninstallSource(root, InstallScope.AllUsers, true)]);
    }

    public void Dispose() => _sb.Dispose();

    [Fact]
    public void ListsOnlyRegularProgramsByDefault()
    {
        var list = _inv.Load(new InventoryOptions());
        Assert.Equal(["Contoso Widget 2.1", "Fabrikam Runtime"], list.Select(p => p.DisplayName));
    }

    [Fact]
    public void OptionsRevealComponentsAndUpdates()
    {
        var all = _inv.Load(new InventoryOptions { IncludeSystemComponents = true, IncludeUpdates = true });
        Assert.Contains(all, p => p.DisplayName == "Hidden Component" && p.Kind == ProgramKind.SystemComponent);
        Assert.Contains(all, p => p.DisplayName.Contains("KB500001") && p.Kind == ProgramKind.Update);
        Assert.Contains(all, p => p.DisplayName == "Child Patch" && p.Kind == ProgramKind.Update);
        Assert.Contains(all, p => p.DisplayName == "Registered Without Uninstaller" && p.Kind == ProgramKind.SystemComponent);
        Assert.DoesNotContain(all, p => p.KeyName == "NoName");
    }

    [Fact]
    public void ReadsFieldsAndDetectsInstaller()
    {
        var p = _inv.Load(new InventoryOptions { UseInstallDateValue = true }).Single(x => x.KeyName == "Contoso_is1");
        Assert.Equal("2.1.0", p.Version);
        Assert.Equal("Contoso Ltd", p.Publisher);
        Assert.Equal(2048L * 1024, p.SizeBytes);
        Assert.Equal(new DateTime(2024, 3, 15), p.InstallDate);
        Assert.Equal("Inno Setup", p.Installer);
        Assert.Equal("64-bit", p.TypeText);
        Assert.False(p.SupportsRepair);
        Assert.Equal("https://contoso.example", p.AboutLink);
    }

    [Fact]
    public void WithoutInstallDateOptionUsesKeyTimestamp()
    {
        var p = _inv.Load(new InventoryOptions()).Single(x => x.KeyName == "Contoso_is1");
        Assert.NotNull(p.InstallDate);
        Assert.True((DateTime.Now - p.InstallDate!.Value).Duration() < TimeSpan.FromHours(1));
    }

    [Fact]
    public void MsiProgramSupportsForcedUninstallModifyRepair()
    {
        var p = _inv.Load(new InventoryOptions()).Single(x => x.DisplayName == "Fabrikam Runtime");
        Assert.True(p.IsMsi);
        Assert.Equal("{AAAAAAAA-1111-2222-3333-444444444444}", p.MsiProductCode);
        Assert.True(p.SupportsForcedMsiUninstall);
        Assert.True(p.SupportsModify);
        Assert.True(p.SupportsRepair);
        Assert.Equal("MsiExec.exe /X{AAAAAAAA-1111-2222-3333-444444444444}", ProgramActions.GetCommand(p, UninstallCommandKind.ForcedMsi));
        Assert.Equal("MsiExec.exe /fa{AAAAAAAA-1111-2222-3333-444444444444}", ProgramActions.GetCommand(p, UninstallCommandKind.Repair));
        Assert.Equal("MsiExec.exe /I{AAAAAAAA-1111-2222-3333-444444444444}", ProgramActions.GetCommand(p, UninstallCommandKind.Modify));
    }

    [Fact]
    public void UnsupportedCommandsThrowClearErrors()
    {
        var p = _inv.Load(new InventoryOptions()).Single(x => x.KeyName == "Contoso_is1");
        Assert.Throws<InvalidOperationException>(() => ProgramActions.GetCommand(p, UninstallCommandKind.ForcedMsi));
        Assert.Throws<InvalidOperationException>(() => ProgramActions.GetCommand(p, UninstallCommandKind.Repair));
    }

    [Fact]
    public void StartingMissingUninstallerReportsInvalidCommand()
    {
        var p = _inv.Load(new InventoryOptions()).Single(x => x.KeyName == "Contoso_is1");
        var ex = Assert.Throws<FileNotFoundException>(() => ProgramActions.StartCommand(p, UninstallCommandKind.Standard));
        Assert.Contains("uninstall command may be invalid", ex.Message);
    }

    [Fact]
    public void RemoveEntryBacksUpThenDeletes()
    {
        using var t = new TempDir();
        var p = _inv.Load(new InventoryOptions()).Single(x => x.KeyName == "Contoso_is1");
        var backup = new Unravel.Core.SystemOps.RegistryBackup(t.Root);
        var (ok, err, folder) = ProgramActions.RemoveEntry(p, backup);
        Assert.True(ok, err);
        Assert.False(p.RegistryKey.Exists());
        Assert.NotNull(folder);
        Assert.Single(Directory.GetFiles(folder!, "*.reg"));
        // Restoring the backup brings the entry back.
        var set = backup.List().Single();
        Assert.True(backup.Restore(set).Ok);
        Assert.True(p.RegistryKey.Exists());
        Assert.Contains(_inv.Load(new InventoryOptions()), x => x.KeyName == "Contoso_is1");
    }

    [Fact]
    public void ResolvesLocalizedIndirectDisplayName()
    {
        _sb.CreateKey(@"Uninstall\mstsc-test", ("DisplayName_Localized", @"@%SystemRoot%\system32\mstsc.exe,-4000"),
            ("UninstallString", "\"C:\\Windows\\System32\\mstsc.exe\" /uninstall"));
        var p = _inv.Load(new InventoryOptions()).Single(x => x.KeyName == "mstsc-test");
        Assert.Equal("Remote Desktop Connection", p.DisplayName);
    }

    [Theory]
    [InlineData("Security Update for Windows (KB5034441)", null, null, true)]
    [InlineData("Some App", "Hotfix", null, true)]
    [InlineData("Some App", null, "Parent", true)]
    [InlineData("KBox Player", null, null, false)]
    public void DetectsUpdates(string name, string? release, string? parent, bool expected) =>
        Assert.Equal(expected, ProgramInventory.IsUpdate(name, release, parent));

    [Theory]
    [InlineData("20231201", 2023, 12, 1)]
    [InlineData("2023-12-01", 2023, 12, 1)]
    [InlineData("12/1/2023", 2023, 12, 1)]
    public void ParsesInstallDates(string s, int y, int m, int d) => Assert.Equal(new DateTime(y, m, d), ProgramInventory.ParseInstallDate(s));

    [Fact]
    public void ExportsTextAndEscapedHtml()
    {
        var list = _inv.Load(new InventoryOptions());
        var cols = new[] { ProgramColumn.Name, ProgramColumn.Version, ProgramColumn.Publisher };
        var txt = ProgramExporter.ToText(list, cols);
        Assert.Contains("Contoso Widget 2.1", txt);
        Assert.Contains("2 item(s)", txt);
        var fake = new InstalledProgram { DisplayName = "<script>alert(1)</script>", RegistryKey = RegPath.Parse(@"HKCU\Software\X") };
        var html = ProgramExporter.ToHtml([fake], cols);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}

public class ProgramInventoryLiveTests
{
    /// <summary>
    /// Oracle check against Windows itself: every program that Programs and Features lists must also be in our list.
    /// The shell folder ::{7b81be6a...} is the same data source the Control Panel applet shows.
    /// </summary>
    [Fact]
    public void MatchesProgramsAndFeatures()
    {
        var ours = new ProgramInventory().Load(new InventoryOptions())
            .Select(p => p.DisplayName.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("Shell.Application")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var folder = shell.NameSpace("::{26EE0668-A00A-44D7-9371-BEB064C98683}\\8\\::{7B81BE6A-CE2B-4676-A29E-EB907A5126C5}");
        var arp = new List<string>();
        foreach (var item in folder.Items()) arp.Add(((string)item.Name).Trim());
        Assert.NotEmpty(arp);
        var missing = arp.Where(n => !ours.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "Listed by Windows but not by Unravel: " + string.Join(", ", missing));
        var extra = ours.Where(n => !arp.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "unravel-arp-diff.txt"),
            $"Windows: {arp.Count}\nUnravel: {ours.Count}\nOnly in Unravel:\n{string.Join("\n", extra)}\n");
    }
}
