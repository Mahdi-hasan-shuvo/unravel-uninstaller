using Microsoft.Win32;
using Unravel.Core.Leftovers;
using Unravel.Core.Settings;
using Unravel.Core.SystemOps;
using Unravel.Core.Util;

namespace Unravel.Tests;

/// <summary>
/// Builds a fake "installed program" across a sandboxed registry and folder tree, then checks what each scan mode finds,
/// that unrelated data is never offered, and that deletion removes exactly the checked, created items.
/// </summary>
public class LeftoverScannerTests : IDisposable
{
    private const string ProductCode = "{12345678-90AB-CDEF-1234-567890ABCDEF}";
    private readonly RegistrySandbox _sb = new();
    private readonly TempDir _t = new();
    private readonly ScanEnvironment _env;
    private readonly ScanTarget _target;
    private readonly string _installDir;
    private readonly string _exe;

    public LeftoverScannerTests()
    {
        // ---- folders
        var pf = _t.Dir("ProgramFiles");
        var appData = _t.Dir("AppData");
        var programData = _t.Dir("ProgramData");
        var startMenu = _t.Dir("StartMenu");
        var desktop = _t.Dir("Desktop");
        _installDir = _t.Dir("ProgramFiles", "Contoso", "Widget");
        _exe = _t.File(@"ProgramFiles\Contoso\Widget\widget.exe", "MZ");
        _t.File(@"ProgramFiles\Contoso\Widget\data.bin", new string('x', 4096));
        _t.Dir("ProgramFiles", "Contoso", "Gadget");                // another product of the same vendor
        _t.File(@"AppData\Contoso Widget\settings.json", "{}");      // moderate
        _t.File(@"AppData\Contoso\Widget\cache.db", "c");             // moderate (publisher\name)
        _t.File(@"AppData\Unrelated\keep.txt", "keep");
        _t.File(@"ProgramData\Widget\shared.dat", "s");               // moderate (folder-leaf name)
        _t.File(@"ProgramData\WidgetHelperTool\x.dat", "s");          // advanced (loose match)
        _t.Dir("StartMenu", "Contoso Widget");
        ShellLink.Create(Path.Combine(startMenu, "Contoso Widget", "Contoso Widget.lnk"), _exe);
        ShellLink.Create(Path.Combine(desktop, "Widget.lnk"), _exe);
        ShellLink.Create(Path.Combine(desktop, "Other.lnk"), Path.Combine(_t.Root, "other.exe"));

        // ---- registry (each sandbox subtree stands in for a real hive location)
        var hklmSoftware = _sb.CreateKey("HKLM_SOFTWARE");
        var hkcuSoftware = _sb.CreateKey("HKCU_Software");
        _sb.CreateKey(@"HKLM_SOFTWARE\Contoso\Contoso Widget", ("Path", _installDir));
        _sb.CreateKey(@"HKLM_SOFTWARE\Contoso\Gadget", ("Path", "elsewhere"));          // keeps publisher key alive
        _sb.CreateKey(@"HKCU_Software\Contoso Widget\Recent", ("File1", "a.txt"));
        _sb.CreateKey(@"HKCU_Software\ContosoLabs\Contoso Widget", ("x", 1));           // publisher variant? no – unrelated vendor
        _sb.CreateKey(@"HKCU_Software\Fabrikam\Contoso Widget", ("x", 1));             // only under unrelated vendor: not offered
        _sb.CreateKey(@"HKCU_Software\WidgetHelperTool", ("x", 1));                    // advanced loose match
        _sb.CreateKey(@"HKCU_Software\Unrelated", ("x", 1));
        var uninstall = _sb.CreateKey(@"Uninstall\Contoso Widget_is1", ("DisplayName", "Contoso Widget 2.1"));
        var run = _sb.CreateKey("Run", ("WidgetTray", $"\"{_exe}\" --tray"), ("Other", "\"C:\\Other\\o.exe\""));
        var startupApproved = _sb.CreateKey("StartupApprovedRun",
            ("WidgetTray", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), ("Other", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        var appPaths = _sb.CreateKey("AppPaths");
        _sb.CreateKey(@"AppPaths\widget.exe", ("", _exe));
        _sb.CreateKey(@"AppPaths\other.exe", ("", @"C:\Other\other.exe"));
        var packed = MsiGuid.Pack(ProductCode);
        var products = _sb.CreateKey(@"Installer\Products");
        _sb.CreateKey($@"Installer\Products\{packed}", ("ProductName", "Contoso Widget"));
        var upgrade = _sb.CreateKey(@"Installer\UpgradeCodes");
        _sb.CreateKey(@"Installer\UpgradeCodes\AAAA1111", (packed, ""));
        _sb.CreateKey(@"Installer\UpgradeCodes\BBBB2222", (packed, ""), ("OTHERPRODUCT0000000000000000000", ""));
        var services = _sb.CreateKey("Services");
        _sb.CreateKey(@"Services\WidgetSvc", ("ImagePath", $"\"{_exe}\" -service"));
        _sb.CreateKey(@"Services\OtherSvc", ("ImagePath", @"C:\Windows\system32\svchost.exe -k x"));
        var classes = _sb.CreateKey("Classes");
        _sb.CreateKey(@"Classes\CLSID\{0000AAAA-0000-0000-0000-000000000001}\InprocServer32", ("", Path.Combine(_installDir, "widgetext.dll")));
        _sb.CreateKey(@"Classes\CLSID\{0000AAAA-0000-0000-0000-000000000002}\InprocServer32", ("", @"C:\Windows\system32\shell32.dll"));
        _sb.CreateKey(@"Classes\Contoso.WidgetDoc\CLSID", ("", "{0000AAAA-0000-0000-0000-000000000001}"));
        _sb.CreateKey(@"Classes\Applications\widget.exe\shell\open\command", ("", $"\"{_exe}\" \"%1\""));
        var approved = _sb.CreateKey("Approved", ("{0000AAAA-0000-0000-0000-000000000001}", "Widget Shell Ext"), ("{0000AAAA-0000-0000-0000-000000000002}", "Windows"));
        var shared = _sb.CreateKey("SharedDLLs", (Path.Combine(_installDir, "common.dll"), 1), (@"C:\Windows\x.dll", 3));
        var firewall = _sb.CreateKey("Firewall", ("rule1", $"v2.30|Action=Allow|Dir=In|App={_exe}|Name=Widget|"), ("rule2", "v2.30|Action=Allow|App=C:\\Other\\o.exe|"));

        _env = new ScanEnvironment
        {
            SoftwareRoots = [hklmSoftware, hkcuSoftware],
            ClassesRoots = [classes],
            RunKeys = [run],
            StartupApprovedKeys = [startupApproved],
            AppPathsRoots = [appPaths],
            SharedDllKeys = [shared],
            ServicesRoot = services,
            FirewallRulesKey = firewall,
            MsiProductKeys = [products],
            MsiUpgradeCodeKeys = [upgrade],
            ShellExtensionApprovedKeys = [approved],
            ProgramFilesRoots = [pf],
            UserDataRoots = [appData, programData],
            StartMenuRoots = [startMenu],
            DesktopRoots = [desktop],
            OtherPrograms = [("Contoso Gadget", Path.Combine(pf, "Contoso", "Gadget"))],
        };
        _target = new ScanTarget
        {
            DisplayName = "Contoso Widget 2.1",
            Publisher = "Contoso Ltd",
            InstallFolder = _installDir,
            UninstallKey = uninstall,
            KeyName = "Contoso Widget_is1",
            MsiProductCode = ProductCode,
            Executables = [_exe],
        };
    }

    public void Dispose() { _sb.Dispose(); _t.Dispose(); }

    private static HashSet<string> Created(ScanResult r) =>
        r.RawFindings.Where(f => f.Created).Select(f => f.Location).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private string K(string rel) => _sb.Path(rel).FullPath;

    [Fact]
    public void BuiltInOnlyFindsNothing()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.BuiltInOnly);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void SafeModeFindsProgramIdentityOnly()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Safe);
        var c = Created(r);
        Assert.Contains(K(@"Uninstall\Contoso Widget_is1"), c);
        Assert.Contains(K(@"HKLM_SOFTWARE\Contoso\Contoso Widget"), c);
        Assert.Contains(K(@"HKCU_Software\Contoso Widget"), c);
        Assert.Contains(K(@"Installer\Products\" + MsiGuid.Pack(ProductCode)), c);
        Assert.Contains(K(@"Installer\UpgradeCodes\AAAA1111"), c);                       // only value → whole key
        Assert.Contains(K(@"Installer\UpgradeCodes\BBBB2222") + "\\" + MsiGuid.Pack(ProductCode), c); // shared → value only
        Assert.Contains(_installDir, c);
        Assert.Contains(_t.Path("StartMenu", "Contoso Widget"), c);
        Assert.Contains(_t.Path("Desktop", "Widget.lnk"), c);
        // Moderate-only locations are not part of Safe.
        Assert.DoesNotContain(_t.Path("AppData", "Contoso Widget"), c);
        Assert.DoesNotContain(K(@"Services\WidgetSvc"), c);
        // The vendor key also holds another product, so it must not be deleted.
        Assert.DoesNotContain(K(@"HKLM_SOFTWARE\Contoso"), c);
    }

    [Fact]
    public void ModerateAddsSharedLocations()
    {
        var c = Created(new LeftoverScanner(_env).Scan(_target, ScanMode.Moderate));
        Assert.Contains(_t.Path("AppData", "Contoso Widget"), c);
        Assert.Contains(_t.Path("AppData", "Contoso", "Widget"), c);
        Assert.Contains(_t.Path("AppData", "Contoso"), c);                 // vendor folder holds nothing else
        Assert.Contains(_t.Path("ProgramData", "Widget"), c);
        Assert.Contains(K("Run") + "\\WidgetTray", c);
        Assert.Contains(K("StartupApprovedRun") + "\\WidgetTray", c);  // Task Manager's disabled flag outlives the Run value
        Assert.DoesNotContain(K("StartupApprovedRun") + "\\Other", c);
        Assert.Contains(K(@"AppPaths\widget.exe"), c);
        Assert.Contains(K(@"Services\WidgetSvc"), c);
        Assert.Contains(K(@"Classes\Applications\widget.exe"), c);
        Assert.Contains(K("SharedDLLs") + "\\" + Path.Combine(_installDir, "common.dll"), c);
        Assert.Contains(K("Firewall") + "\\rule1", c);
        Assert.DoesNotContain(_t.Path("ProgramData", "WidgetHelperTool"), c); // loose match is Advanced only
        Assert.DoesNotContain(K(@"Classes\CLSID\{0000AAAA-0000-0000-0000-000000000001}"), c);
    }

    [Fact]
    public void AdvancedAddsDeepAndComFindings()
    {
        var c = Created(new LeftoverScanner(_env).Scan(_target, ScanMode.Advanced));
        Assert.Contains(_t.Path("ProgramData", "WidgetHelperTool"), c);
        Assert.Contains(K(@"HKCU_Software\WidgetHelperTool"), c);
        Assert.Contains(K(@"Classes\CLSID\{0000AAAA-0000-0000-0000-000000000001}"), c);
        Assert.Contains(K(@"Classes\Contoso.WidgetDoc"), c);
        Assert.Contains(K("Approved") + "\\{0000AAAA-0000-0000-0000-000000000001}", c);
    }

    [Fact]
    public void NeverOffersUnrelatedOrProtectedItems()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Advanced);
        var all = r.RawFindings.Select(f => f.Location).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(K(@"HKCU_Software\Unrelated"), all);
        // A product-named key under an unrelated vendor is only reported by the deep (Advanced) search.
        var moderate = new LeftoverScanner(_env).Scan(_target, ScanMode.Moderate).RawFindings.Select(f => f.Location).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(K(@"HKCU_Software\Fabrikam\Contoso Widget"), moderate);
        Assert.Contains(K(@"HKCU_Software\Fabrikam\Contoso Widget"), all);
        Assert.DoesNotContain(K(@"HKLM_SOFTWARE\Contoso\Gadget"), all);
        Assert.DoesNotContain(K("Run") + "\\Other", all);
        Assert.DoesNotContain(K(@"AppPaths\other.exe"), all);
        Assert.DoesNotContain(K(@"Services\OtherSvc"), all);
        Assert.DoesNotContain(K(@"Classes\CLSID\{0000AAAA-0000-0000-0000-000000000002}"), all);
        Assert.DoesNotContain(K("Firewall") + "\\rule2", all);
        Assert.DoesNotContain(_t.Path("AppData", "Unrelated"), all);
        Assert.DoesNotContain(_t.Path("Desktop", "Other.lnk"), all);
        Assert.DoesNotContain(_t.Path("ProgramFiles", "Contoso", "Gadget"), all);
        // Search roots themselves are never offered.
        Assert.DoesNotContain(K("HKLM_SOFTWARE"), all);
        Assert.DoesNotContain(_t.Path("ProgramFiles"), all);
        // The vendor folder in Program Files contains another product → not created.
        Assert.DoesNotContain(_t.Path("ProgramFiles", "Contoso"), Created(r));
    }

    [Fact]
    public void FolderSharedWithAnotherProgramIsNotMarkedCreated()
    {
        _env.OtherPrograms = [("Contoso Gadget", _installDir)];
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Safe);
        var node = r.Files.Single(f => f.FsPath == _installDir);
        Assert.False(node.IsCreated);
        Assert.Contains("Contoso Gadget", node.Note);
    }

    [Fact]
    public void NameSharedWithAnotherInstalledProgramIsNotMarkedCreated()
    {
        _env.OtherPrograms = [("Contoso Widget 3.0", @"C:\Elsewhere")];
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Moderate);
        var finding = r.RawFindings.Single(f => f.Location.Equals(K(@"HKCU_Software\Contoso Widget"), StringComparison.OrdinalIgnoreCase));
        Assert.False(finding.Created);
    }

    [Fact]
    public void RegistryTreeShowsBoldLeavesUnderPlainParents()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Safe);
        var hive = Assert.Single(r.RegistryRoot.Children);
        Assert.Equal("HKEY_CURRENT_USER", hive.DisplayName);
        Assert.False(hive.IsCreated);
        var leaf = r.RegistryRoot.Descendants().Single(n => n.Key?.FullPath == K(@"HKCU_Software\Contoso Widget"));
        Assert.True(leaf.IsCreated);
        Assert.False(leaf.Parent!.IsCreated);
        // Checking a parent checks all of its children (but only created ones will be deleted).
        hive.IsChecked = true;
        Assert.True(leaf.IsChecked);
        Assert.All(r.RegistryRoot.Descendants().Where(n => n.IsChecked && !n.IsCreated), n => Assert.False(n.WillDelete));
    }

    [Fact]
    public void ExcludeRulesMarkItemsAndProtectThem()
    {
        var rules = new List<ExcludeRule> { new() { Pattern = K(@"HKCU_Software\Contoso*"), IncludeSubkeys = true } };
        var folderRules = new List<ExcludeRule> { new() { Pattern = _t.Path("AppData", "Contoso*") } };
        var scanner = new LeftoverScanner(_env, rules, folderRules, checkByDefault: true);
        var r = scanner.Scan(_target, ScanMode.Moderate);
        var reg = r.RegistryRoot.Descendants().Single(n => n.Key?.FullPath == K(@"HKCU_Software\Contoso Widget") && n.Kind == LeftoverKind.RegistryKey);
        Assert.True(reg.IsExcluded);
        Assert.False(reg.WillDelete);
        var file = r.Files.Single(f => f.FsPath == _t.Path("AppData", "Contoso Widget"));
        Assert.True(file.IsExcluded);
        Assert.False(file.IsChecked);
        // Wildcard ? and include-subkeys semantics
        Assert.True(scanner.IsExcludedKey(K(@"HKCU_Software\ContosoXYZ\Deep\Er")));
        var exact = new LeftoverScanner(_env, [new ExcludeRule { Pattern = K(@"HKCU_Software\Contoso Widget"), IncludeSubkeys = false }]);
        Assert.True(exact.IsExcludedKey(K(@"HKCU_Software\Contoso Widget")));
        Assert.False(exact.IsExcludedKey(K(@"HKCU_Software\Contoso Widget\Recent")));
        var disabled = new LeftoverScanner(_env, [new ExcludeRule { Pattern = K(@"HKCU_Software\*"), Enabled = false }]);
        Assert.False(disabled.IsExcludedKey(K(@"HKCU_Software\Contoso Widget")));
    }

    [Fact]
    public void DeletesOnlyCheckedCreatedItemsWithBackup()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Advanced);
        foreach (var top in r.RegistryRoot.Children) top.IsChecked = true; // "Select All"
        var backup = new RegistryBackup(_t.Path("backups"));
        var report = LeftoverRemover.DeleteRegistry(r.RegistryRoot, backup, "Contoso Widget");
        Assert.Equal(0, report.Failed);
        Assert.NotNull(report.BackupFolder);
        Assert.NotEmpty(Directory.GetFiles(report.BackupFolder!, "*.reg"));

        Assert.False(_sb.Path(@"HKCU_Software\Contoso Widget").Exists());
        Assert.False(_sb.Path(@"Services\WidgetSvc").Exists());
        using (var run = _sb.Path("Run").Open())
        {
            Assert.Null(run!.GetValue("WidgetTray"));
            Assert.NotNull(run.GetValue("Other"));
        }
        using (var up = _sb.Path(@"Installer\UpgradeCodes\BBBB2222").Open())
            Assert.Equal(["OTHERPRODUCT0000000000000000000"], up!.GetValueNames());
        // Unrelated data and plain (non-bold) parents survive.
        Assert.True(_sb.Path(@"HKCU_Software\Unrelated").Exists());
        Assert.True(_sb.Path(@"HKLM_SOFTWARE\Contoso\Gadget").Exists());
        Assert.True(_sb.Path("HKLM_SOFTWARE").Exists());

        // Files: delete permanently (not to the Recycle Bin, to keep the test machine clean).
        foreach (var f in r.Files) f.IsChecked = true;
        var fileReport = LeftoverRemover.DeleteFiles(r.Files, toRecycleBin: false);
        Assert.Equal(0, fileReport.Failed);
        Assert.False(Directory.Exists(_installDir));
        Assert.False(Directory.Exists(_t.Path("AppData", "Contoso Widget")));
        Assert.True(Directory.Exists(_t.Path("AppData", "Unrelated")));
        Assert.True(Directory.Exists(_t.Path("ProgramFiles", "Contoso", "Gadget")));
        Assert.True(File.Exists(_t.Path("Desktop", "Other.lnk")));

        // The backup restores what was removed.
        Assert.True(backup.Restore(backup.List().Single()).Ok);
        Assert.True(_sb.Path(@"HKCU_Software\Contoso Widget\Recent").Exists());
        Assert.True(_sb.Path(@"Services\WidgetSvc").Exists());
    }

    [Fact]
    public void UncheckedItemsAreKept()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Safe);
        var report = LeftoverRemover.DeleteRegistry(r.RegistryRoot, null, "x");
        Assert.Empty(report.Items);
        Assert.True(_sb.Path(@"HKCU_Software\Contoso Widget").Exists());
    }

    [Fact]
    public void MarkAsCreatedMakesPlainNodeDeletable()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Safe);
        var plain = r.RegistryRoot.Descendants().First(n => n.Key?.FullPath == K(@"HKLM_SOFTWARE\Contoso"));
        Assert.False(plain.IsCreated);
        plain.IsCreated = true; // "Mark selected key as Created"
        plain.IsChecked = true;
        var selected = LeftoverRemover.SelectRegistry(r.RegistryRoot);
        Assert.Contains(plain, selected);
        Assert.DoesNotContain(selected, n => n.IsDescendantOf(plain)); // children covered by the parent
    }

    [Fact]
    public void BackupFailurePreventsDeletion()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Safe);
        foreach (var top in r.RegistryRoot.Children) top.IsChecked = true;
        // A backup root that cannot be created (a file is in the way).
        var blocker = _t.File("blocker", "x");
        var report = LeftoverRemover.DeleteRegistry(r.RegistryRoot, new RegistryBackup(Path.Combine(blocker, "sub")), "x");
        Assert.NotNull(report.BackupError);
        Assert.True(_sb.Path(@"HKCU_Software\Contoso Widget").Exists());
    }

    [Fact]
    public void ExportsHtml()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Moderate);
        var html = LeftoverRemover.ExportHtml("Contoso <Widget>", r.RegistryRoot, r.Files);
        Assert.Contains("Contoso &lt;Widget&gt;", html);
        Assert.Contains("WidgetTray", html);
        Assert.Contains("settings", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancellationStopsTheScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LeftoverScanner(_env).Scan(_target, ScanMode.Advanced, cts.Token));
    }
    // ------------------------------------------------------------ post-uninstall verification

    /// <summary>Deletes the safe leftovers the way the wizard does and returns what was removed and what was kept.</summary>
    private (ScanResult Scan, List<RemovedItem> Removed, List<string> Kept) RemoveRecommended()
    {
        var r = new LeftoverScanner(_env).Scan(_target, ScanMode.Moderate);
        foreach (var n in r.RegistryRoot.Descendants().Where(n => n.IsCreated && n.Safety.IsSafeToAutoSelect)) n.IsChecked = true;
        foreach (var f in r.Files.Where(f => f.IsCreated && f.Safety.IsSafeToAutoSelect)) f.IsChecked = true;
        var regNodes = LeftoverRemover.SelectRegistry(r.RegistryRoot).ToDictionary(n => n.Location, StringComparer.OrdinalIgnoreCase);
        var fileNodes = LeftoverRemover.SelectFiles(r.Files).ToDictionary(n => n.Location, StringComparer.OrdinalIgnoreCase);
        var removed = new List<RemovedItem>();
        foreach (var i in LeftoverRemover.DeleteRegistry(r.RegistryRoot, null, "test").Items.Where(i => i.Ok)) removed.Add(RemovedItem.From(regNodes[i.Location]));
        foreach (var i in LeftoverRemover.DeleteFiles(r.Files, toRecycleBin: false).Items.Where(i => i.Ok)) removed.Add(RemovedItem.From(fileNodes[i.Location], i.FileOutcome));
        bool Covered(string loc) => removed.Any(x => loc.Equals(x.Location, StringComparison.OrdinalIgnoreCase) || loc.StartsWith(x.Location + "\\", StringComparison.OrdinalIgnoreCase));
        var kept = r.RegistryRoot.Descendants().Where(n => n.IsCreated && !Covered(n.Location)).Select(n => n.Location)
            .Concat(r.Files.Where(f => f.IsCreated && !Covered(f.Location)).Select(f => f.Location)).ToList();
        return (r, removed, kept);
    }

    [Fact]
    public void VerificationConfirmsEveryDeletedItemAndReportsKeptOnes()
    {
        var (_, removed, kept) = RemoveRecommended();
        Assert.NotEmpty(removed);
        var report = new UninstallVerifier().Verify(new UninstallVerifier.Input
        {
            Target = _target,
            StillRegistered = () => _target.UninstallKey!.Exists(),
            Removed = removed,
            Kept = kept,
            Scanner = new LeftoverScanner(_env),
            RescanMode = ScanMode.Moderate,
        });

        Assert.Equal(0, report.Count(VerificationStatus.StillPresent));
        Assert.Contains(report.Checks, c => c.Area == VerificationArea.Registration && c.Status == VerificationStatus.Removed);
        // Every deleted item was looked up again and confirmed gone.
        foreach (var r in removed)
            Assert.Contains(report.Checks, c => c.Location == r.Location && c.Status == VerificationStatus.Removed);
        // Whatever the re-scan still finds is exactly what the user kept.
        Assert.All(report.Checks.Where(c => c.Area == VerificationArea.Rescan), c => Assert.Equal(VerificationStatus.Kept, c.Status));
        Assert.Equal(kept.Count > 0 ? VerificationVerdict.LeftoversKept : VerificationVerdict.Clean, report.Verdict);
    }

    [Fact]
    public void VerificationDetectsItemsThatCameBackOrWereNeverRemoved()
    {
        var (_, removed, _) = RemoveRecommended();
        // A running program re-creates its settings key and a folder after the "successful" deletion.
        _sb.CreateKey(@"HKCU_Software\Contoso Widget\Recent", ("File1", "b.txt"));
        Directory.CreateDirectory(_t.Path("AppData", "Contoso Widget"));
        File.WriteAllText(_t.Path("AppData", "Contoso Widget", "settings.json"), "{}");

        var report = new UninstallVerifier().Verify(new UninstallVerifier.Input
        {
            Target = _target,
            StillRegistered = () => true,   // e.g. the uninstaller was cancelled
            Removed = removed,
            Scanner = new LeftoverScanner(_env),
        });

        Assert.Equal(VerificationVerdict.ItemsRemain, report.Verdict);
        Assert.Contains(report.Checks, c => c.Area == VerificationArea.Registration && c.Status == VerificationStatus.StillPresent);
        Assert.Contains(report.Checks, c => c.Location == K(@"HKCU_Software\Contoso Widget") && c.Status == VerificationStatus.StillPresent);
        Assert.Contains(report.Checks, c => c.Location == _t.Path("AppData", "Contoso Widget") && c.Status == VerificationStatus.StillPresent);
    }

    [Fact]
    public void VerificationChecksValuesFilesAndRestartPendingItems()
    {
        var run = _sb.Path("Run");
        var locked = _t.File(@"locked\in-use.dll", "x");
        var gone = _t.Path("gone.txt");
        var removed = new List<RemovedItem>
        {
            new(LeftoverKind.RegistryValue, run.FullPath + @"\WidgetTray", run, "WidgetTray"),     // still there
            new(LeftoverKind.RegistryValue, run.FullPath + @"\Missing", run, "Missing"),           // gone
            new(LeftoverKind.File, locked, FsPath: locked, Outcome: DeleteOutcome.ScheduledForReboot),
            new(LeftoverKind.File, gone, FsPath: gone, Outcome: DeleteOutcome.Recycled),
        };
        var report = new UninstallVerifier().Verify(new UninstallVerifier.Input { Target = new ScanTarget { DisplayName = "x" }, Removed = removed });

        Assert.Equal(VerificationStatus.StillPresent, report.Checks.Single(c => c.Location.EndsWith(@"\WidgetTray")).Status);
        Assert.Equal(VerificationStatus.Removed, report.Checks.Single(c => c.Location.EndsWith(@"\Missing")).Status);
        Assert.Equal(VerificationStatus.PendingRestart, report.Checks.Single(c => c.Location == locked).Status);
        var recycled = report.Checks.Single(c => c.Location == gone);
        Assert.Equal(VerificationStatus.Removed, recycled.Status);
        Assert.Contains("Recycle Bin", recycled.Detail);
    }

    [Fact]
    public void VerificationReportsTheInstallFolderAndRestartOnlyVerdict()
    {
        // The install folder is still full and nothing else was touched.
        var report = new UninstallVerifier().Verify(new UninstallVerifier.Input { Target = _target, CheckProcesses = false });
        var folder = report.Checks.Single(c => c.Area == VerificationArea.InstallFolder);
        Assert.Equal(VerificationStatus.StillPresent, folder.Status);
        Assert.Contains("2 file(s)", folder.Detail);

        var pendingOnly = new UninstallVerifier().Verify(new UninstallVerifier.Input
        {
            Target = new ScanTarget { DisplayName = "x" },
            Removed = [new(LeftoverKind.File, _exe, FsPath: _exe, Outcome: DeleteOutcome.ScheduledForReboot)],
        });
        Assert.Equal(VerificationVerdict.CleanAfterRestart, pendingOnly.Verdict);
    }
}

public class NameMatcherTests
{
    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("Revo Uninstaller 2.6.8", "Revo Uninstaller")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("VLC media player", "VLC media player")]
    [InlineData("Python 3.12.1 (64-bit)", "Python")]
    public void StripsVersions(string input, string expected) => Assert.Equal(expected, NameMatcher.StripVersion(input));

    [Theory]
    [InlineData("Contoso Ltd.", "Contoso")]
    [InlineData("Mozilla Corporation", "Mozilla")]
    [InlineData("Fabrikam, Inc.", "Fabrikam")]
    public void StripsCompanySuffix(string input, string expected) => Assert.Equal(expected, NameMatcher.StripCompanySuffix(input));

    [Fact]
    public void GenericNamesAreNotUsed()
    {
        var t = new ScanTarget { DisplayName = "Update", Publisher = "Microsoft Corporation", InstallFolder = @"C:\Program Files\Common Files" };
        Assert.Empty(NameMatcher.NameVariants(t));
        Assert.Empty(NameMatcher.PublisherVariants(t));
    }

    [Fact]
    public void ScannerRefusesProtectedRealSystemLocations()
    {
        var env = ScanEnvironment.CreateDefault();
        var s = new LeftoverScanner(env);
        Assert.True(s.IsProtectedKey(RegPath.Parse(@"HKLM\SOFTWARE")));
        Assert.True(s.IsProtectedKey(RegPath.Parse(@"HKLM\SOFTWARE\Microsoft")));
        Assert.True(s.IsProtectedKey(RegPath.Parse(@"HKCU\Software\Classes")));
        Assert.True(s.IsProtectedKey(RegPath.Parse(@"HKLM\SYSTEM\CurrentControlSet\Services")));
        Assert.False(s.IsProtectedKey(RegPath.Parse(@"HKCU\Software\SomeVendor\SomeApp")));
        Assert.True(s.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.True(s.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        Assert.True(s.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(s.IsProtectedFolder(@"C:\"));
        Assert.True(s.IsProtectedFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers")));
        Assert.False(s.IsProtectedFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SomeVendorApp")));
    }

    [Fact]
    public void ScanTargetIgnoresBroadInstallFolders()
    {
        var p = new Unravel.Core.Programs.InstalledProgram
        {
            DisplayName = "Bad Registration",
            RegistryKey = RegPath.Parse(@"HKCU\Software\X"),
            InstallLocation = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        var t = ScanTarget.FromProgram(p);
        Assert.Empty(t.Executables);
        var r = new LeftoverScanner(ScanEnvironment.CreateDefault()).Scan(t, ScanMode.Safe);
        Assert.DoesNotContain(r.RawFindings, f => f.Kind == LeftoverKind.Folder &&
            f.FsPath!.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase));
    }
}
