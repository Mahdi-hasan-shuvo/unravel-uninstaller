using Microsoft.Win32;
using Unravel.Core.Apps;
using Unravel.Core.Programs;
using Unravel.Core.Util;
using Unravel.Core.WinGet;
using Core = Unravel.Core;

namespace Unravel.Tests;

/// <summary>Regressions found by comparing Unravel's lists with winget, Get-AppxPackage and the registry on a real PC.</summary>
public class WinGetDetectionTests
{
    private static string Row(string name, string id, string version, string available = "", string source = "") =>
        name.PadRight(30) + id.PadRight(50) + version.PadRight(14) + available.PadRight(12) + source;

    private static string Table(params string[] rows) =>
        Row("Name", "Id", "Version", "Available", "Source") + "\n" + new string('-', 115) + "\n" + string.Join("\n", rows) + "\n";

    [Fact]
    public void PackagesWhoseIdContainsSpacesAreKept()
    {
        // Real rows from "winget list": the ARP key name is the ID, and many key names contain spaces.
        var p = WinGetParser.ParsePackages(Table(
            Row("Google Chrome", @"ARP\Machine\X86\Google Chrome", "148.0"),
            Row("Android Studio", @"ARP\Machine\X64\Android Studio", "2026.1"),
            Row("BlueStacks X", @"ARP\User\X64\BlueStacks X_msi5", "10.4"),
            Row("Git", "Git.Git", "2.51.0", "2.52.0", "winget")), WinGetParser.Command.List);
        Assert.Equal(4, p.Count);
        Assert.Equal(@"ARP\Machine\X86\Google Chrome", p[0].Id);
        Assert.Equal("Google Chrome", p[0].ArpKeyName);
        Assert.Equal("BlueStacks X_msi5", p[2].ArpKeyName);
    }

    [Fact]
    public void LongFooterSentencesAreNotPackages()
    {
        var output = Table(Row("Git", "Git.Git", "2.51.0", "2.52.0", "winget")) +
                     "2 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results and more text after it.\n";
        var p = Assert.Single(WinGetParser.ParsePackages(output, WinGetParser.Command.Upgrade));
        Assert.Equal("Git.Git", p.Id);
    }

    [Fact]
    public void WideCharactersDoNotShiftTheColumns()
    {
        // WinGet pads by display width: a CJK character takes two columns but is one UTF-16 unit.
        var header = "Name".PadRight(20) + "Id".PadRight(30) + "Version".PadRight(10) + "Source";
        var name = "微信开发者工具";                     // 7 characters, 14 columns
        var row = name + new string(' ', 20 - 14) + "Tencent.WeChatDevTools".PadRight(30) + "1.06".PadRight(10) + "winget";
        var p = Assert.Single(WinGetParser.ParsePackages(header + "\n" + new string('-', 70) + "\n" + row + "\n", WinGetParser.Command.List));
        Assert.Equal((name, "Tencent.WeChatDevTools", "1.06", "winget"), (p.Name, p.Id, p.Version, p.Source));
    }

    [Fact]
    public void WideHeadersKeepTheColumnsToo()
    {
        var header = "名称".PadRight(16) + "ID".PadRight(14) + "版本".PadRight(8) + "源";    // headers are wide as well: 2 extra columns each
        var line = "Calculator" + new string(' ', 8) + "Calc.App".PadRight(14) + "1.0".PadRight(10) + "winget";
        var p = Assert.Single(WinGetParser.ParsePackages(header + "\n" + new string('-', 50) + "\n" + line + "\n", WinGetParser.Command.List));
        Assert.Equal(("Calculator", "Calc.App", "1.0"), (p.Name, p.Id, p.Version));
    }

    [Theory]
    [InlineData(0x8A150011u, "hash")]                 // was wrongly described as "more than one package matches"
    [InlineData(0x8A150016u, "More than one")]
    [InlineData(0x8A150014u, "No package")]
    [InlineData(0x8A150045u, "source")]
    [InlineData(0x8A150068u, "pin")]
    [InlineData(0x8A150030u, "uninstall")]
    [InlineData(0x8A150002u, "App Installer")]
    public void ErrorCodesFollowMicrosoftsTable(uint code, string expected) =>
        Assert.Contains(expected, WinGetErrors.Describe(unchecked((int)code), ""), StringComparison.OrdinalIgnoreCase);

    private static WinGetClient Fake(Func<string, WinGetResult> respond, List<string>? calls = null) =>
        new("winget.exe", (args, _, _) => { calls?.Add(args); return Task.FromResult(respond(args)); });

    [Fact]
    public async Task ListKeepsItsRowsWhenOneSourceFails()
    {
        var output = Table(Row("Git", "Git.Git", "2.51.0", "", "winget")) + "Failed when searching source: msstore\n";
        var list = await Fake(_ => new WinGetResult(unchecked((int)0x8A150045), output, "x")).ListInstalledAsync();
        Assert.Equal("Git.Git", Assert.Single(list).Id);
    }

    [Fact]
    public async Task ListStillFailsLoudlyWhenThereIsNothingToShow()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fake(_ => new WinGetResult(unchecked((int)0x8A150045), "Failed\n", "Opening the source failed.")).ListInstalledAsync());
    }

    [Fact]
    public async Task TruncatedIdsAreExpandedBeforeActingOnThem()
    {
        // Older WinGet versions cut IDs at the 120-column width they assume when output is redirected.
        var calls = new List<string>();
        var client = Fake(args => args.StartsWith("list")
            ? new WinGetResult(0, Table(Row("Very Long", "Publisher.AVeryLongPackageIdentifierThatGoesOn", "1.0", "", "winget")), "ok")
            : new WinGetResult(0, "", "ok"), calls);
        var r = await client.UninstallAsync("Publisher.AVeryLongPackage…");
        Assert.True(r.Ok);
        Assert.Contains(calls, c => c.StartsWith("list --id \"Publisher.AVeryLongPackage\""));
        Assert.Contains(calls, c => c.StartsWith("uninstall --id \"Publisher.AVeryLongPackageIdentifierThatGoesOn\" --exact"));
    }

    [Fact]
    public async Task AnUnresolvableTruncatedIdIsNeverSentToWinGet()
    {
        var calls = new List<string>();
        var r = await Fake(_ => new WinGetResult(0, "", "ok"), calls).UninstallAsync("Publisher.Cut…");
        Assert.False(r.Ok);
        Assert.DoesNotContain(calls, c => c.StartsWith("uninstall"));
    }
}

public class ProgramDetectionFixTests
{
    private static InstalledProgram Msi(string uninstall, bool isMsi = true) => new()
    {
        DisplayName = "Demo",
        RegistryKey = new RegPath(RegistryHive.CurrentUser, @"Software\X\{AAAAAAAA-1111-2222-3333-444444444444}"),
        IsMsi = isMsi,
        UninstallString = uninstall,
    };

    [Theory]
    [InlineData("MsiExec.exe /I{AAAAAAAA-1111-2222-3333-444444444444}")]
    [InlineData("msiexec /i {AAAAAAAA-1111-2222-3333-444444444444}")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /I{AAAAAAAA-1111-2222-3333-444444444444}")]
    public void UninstallingAnMsiNeverRunsTheInstallSwitch(string registered) =>
        Assert.Equal("MsiExec.exe /X{AAAAAAAA-1111-2222-3333-444444444444}", ProgramActions.GetCommand(Msi(registered), UninstallCommandKind.Standard));

    [Fact]
    public void OtherUninstallCommandsAreLeftAlone()
    {
        const string x = "MsiExec.exe /X{AAAAAAAA-1111-2222-3333-444444444444}";
        Assert.Equal(x, ProgramActions.GetCommand(Msi(x), UninstallCommandKind.Standard));
        const string exe = "\"C:\\P\\setup.exe\" /i /uninstall";       // not msiexec: /i means whatever that program says
        Assert.Equal(exe, ProgramActions.GetCommand(Msi(exe, isMsi: false), UninstallCommandKind.Standard));
    }

    [Theory]
    [InlineData(1602, UninstallerExit.Cancelled)]
    [InlineData(1603, UninstallerExit.Failed)]
    [InlineData(1605, UninstallerExit.Failed)]
    [InlineData(1618, UninstallerExit.Failed)]
    [InlineData(1641, UninstallerExit.RestartNeeded)]
    [InlineData(3010, UninstallerExit.RestartNeeded)]
    [InlineData(0, UninstallerExit.Success)]
    public void WindowsInstallerExitCodesAreInterpreted(int code, UninstallerExit expected) =>
        Assert.Equal(expected, ProgramActions.InterpretExitCode(code, isWindowsInstaller: true).Kind);

    [Fact]
    public void OtherInstallersExitCodesAreNotCalledFailures()
    {
        // Only Windows Installer documents its codes; NSIS and Inno Setup return values with their own meaning.
        var r = ProgramActions.InterpretExitCode(2, isWindowsInstaller: false);
        Assert.Equal(UninstallerExit.Unknown, r.Kind);
        Assert.Contains("2", r.Message);
    }

    [Fact]
    public void DefaultSourcesReadExplicit32BitPerUserRegistrations()
    {
        Assert.Contains(ProgramInventory.DefaultSources(), s =>
            s.Root.Hive == RegistryHive.CurrentUser && s.Root.SubPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyOtherPeoplesLoadedHivesAreAddedAsSources()
    {
        var sids = ProgramInventory.OtherUserSids(["S-1-5-18", ".DEFAULT", "S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001_Classes", "S-1-5-21-1-2-3-1002"], "S-1-5-21-1-2-3-1001");
        Assert.Equal(["S-1-5-21-1-2-3-1002"], sids);
    }

    [Fact]
    public void ProgramsOfAnotherLoadedUserAreListedAsSuch()
    {
        using var sb = new RegistrySandbox();
        sb.CreateKey(@"Uninstall\Their App", ("DisplayName", "Their App"), ("UninstallString", "x.exe"));
        var p = Assert.Single(new ProgramInventory([new UninstallSource(sb.Path("Uninstall"), InstallScope.OtherUser, true)]).Load(new InventoryOptions()));
        Assert.Equal(InstallScope.OtherUser, p.Scope);
        Assert.Equal("Other user", p.Bitness);
    }
}

public class PerUserMsiDetectionTests
{
    private const string Code = "{7A1D2F60-8B3C-4E55-A1F0-2C9E4B7D3F22}";

    // Per-user Windows Installer products have no key under ...\Uninstall; Windows keeps them here and lists them from here.
    private static RegPath Record(RegistrySandbox sb, string name = "Per User App", int? systemComponent = null)
    {
        var values = new List<(string, object)> { ("DisplayName", name), ("DisplayVersion", "1.0"), ("Publisher", "Contoso"), ("WindowsInstaller", 1), ("UninstallString", $"MsiExec.exe /I{Code}") };
        if (systemComponent is { } s) values.Add(("SystemComponent", s));
        return sb.CreateKey($@"UserData\Products\{Core.Leftovers.MsiGuid.Pack(Code)}\InstallProperties", values.ToArray());
    }

    private static ProgramInventory Inventory(RegistrySandbox sb, bool withUninstallKey = false)
    {
        var uninstall = sb.CreateKey("Uninstall");
        if (withUninstallKey) sb.CreateKey($@"Uninstall\{Code}", ("DisplayName", "Per User App"), ("WindowsInstaller", 1), ("UninstallString", $"MsiExec.exe /X{Code}"));
        return new ProgramInventory([new UninstallSource(uninstall, InstallScope.CurrentUser, true), new UninstallSource(sb.Path(@"UserData\Products"), InstallScope.CurrentUser, true, IsMsiUserData: true)]);
    }

    [Fact]
    public void PerUserMsiProductsAreListedLikeWindowsListsThem()
    {
        using var sb = new RegistrySandbox();
        Record(sb);
        var p = Assert.Single(Inventory(sb).Load(new InventoryOptions()));
        Assert.Equal("Per User App", p.DisplayName);
        Assert.Equal(Code, p.KeyName);                     // the same identity WinGet reports (ARP\...\{code})
        Assert.Equal(Code, p.MsiProductCode);
        Assert.True(p.IsWindowsInstallerRecord);
        Assert.Equal($"MsiExec.exe /X{Code}", ProgramActions.GetCommand(p, UninstallCommandKind.Standard));
        Assert.True(ProgramActions.StillInstalled(p));
    }

    [Fact]
    public void AProductWithAnUninstallKeyIsNotListedTwice()
    {
        using var sb = new RegistrySandbox();
        Record(sb);
        var p = Assert.Single(Inventory(sb, withUninstallKey: true).Load(new InventoryOptions()));
        Assert.False(p.IsWindowsInstallerRecord);
    }

    [Fact]
    public void HiddenPerUserComponentsStayHidden()
    {
        using var sb = new RegistrySandbox();
        Record(sb, "Python Core", systemComponent: 1);
        Assert.Empty(Inventory(sb).Load(new InventoryOptions()));
        Assert.Single(Inventory(sb).Load(new InventoryOptions { IncludeSystemComponents = true }));
    }

    [Fact]
    public void WindowsInstallersOwnRecordIsNeverDeletedOrOfferedAsALeftover()
    {
        using var sb = new RegistrySandbox();
        var key = Record(sb);
        var p = Assert.Single(Inventory(sb).Load(new InventoryOptions()));
        var (ok, error, _) = ProgramActions.RemoveEntry(p, null);
        Assert.False(ok);
        Assert.Contains("Windows Installer", error);
        Assert.True(key.Exists());
        Assert.Null(Core.Leftovers.ScanTarget.FromProgram(p).UninstallKey);
    }

    [Fact]
    public void TheCurrentUsersWindowsInstallerRecordsAreADefaultSource() =>
        Assert.Contains(ProgramInventory.DefaultSources(), s => s.IsMsiUserData && s.Root.Hive == RegistryHive.LocalMachine && s.Root.SubPath.Contains(@"Installer\UserData\S-1-5-21"));
}

public class WindowsAppSafetyFixTests
{
    [Fact]
    public void NonRemovableFactsComeFromWindowsNotFromAProbeThatNeverMatched()
    {
        // App Installer is the package WinGet lives in; Windows marks it NonRemovable.
        var all = WindowsApps.List(new WindowsAppOptions { IncludeFrameworks = true, IncludeSystemApps = true });
        var installer = all.FirstOrDefault(a => a.PackageName == "Microsoft.DesktopAppInstaller");
        if (installer is not null) Assert.True(installer.IsNonRemovable);
    }

    [Fact]
    public async Task RemovingANonRemovablePackageIsRefusedBeforeWindowsIsAsked()
    {
        var app = new WindowsApp { Name = "X", PackageFullName = "X_1_x64__a", PackageFamilyName = "X_a", PackageName = "X", IsNonRemovable = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsApps.UninstallAsync(app));
    }

    [Fact]
    public void StartMenuSystemAppsAreListedButFrameworksAreNot()
    {
        var apps = WindowsApps.List(new WindowsAppOptions());
        Assert.All(apps.Where(a => a.IsSystem), a => Assert.True(a.CanLaunch));
        Assert.DoesNotContain(apps, a => a.IsFramework);
    }

    [Fact]
    public void ProtocolsAreReadFromTheManifest()
    {
        using var t = new TempDir();
        File.WriteAllText(t.Path("AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications><Application Id="App"><Extensions>
                <uap:Extension Category="windows.protocol"><uap:Protocol Name="ms-gamingoverlay"/></uap:Extension>
                <uap:Extension Category="windows.protocol"><uap:Protocol Name="ms-gamebar"/></uap:Extension>
              </Extensions></Application></Applications>
            </Package>
            """);
        Assert.Equal(["ms-gamebar", "ms-gamingoverlay"], WindowsApps.ReadProtocols(t.Root).OrderBy(s => s));
    }
}

public class SystemLinkHealthTests
{
    [Fact]
    public async Task AMissingGameBarHandlerIsReportedWithTheStorePage()
    {
        var issue = Assert.Single(await SystemLinkHealth.CheckAsync(_ => Task.FromResult(false)));
        Assert.Equal("ms-gamingoverlay", issue.Scheme);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9NZKPSTSNW4P", issue.StoreUrl);
        Assert.Equal("ms-settings:gaming-gamebar", issue.SettingsUrl);
    }

    [Fact]
    public async Task AnInstalledHandlerIsNotAnIssue() =>
        Assert.Empty(await SystemLinkHealth.CheckAsync(_ => Task.FromResult(true)));

    [Fact]
    public async Task AFailedCheckMakesNoClaim() =>
        Assert.Empty(await SystemLinkHealth.CheckAsync(_ => throw new InvalidOperationException("query failed")));

    [Fact]
    public async Task TheRealQueryRecognisesALinkWindowsHandles() =>
        Assert.True(await SystemLinkHealth.HasHandlerAsync("ms-settings"));
}
