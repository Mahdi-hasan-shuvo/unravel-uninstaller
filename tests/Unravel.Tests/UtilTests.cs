using Microsoft.Win32;
using Unravel.Core.Leftovers;
using Unravel.Core.Settings;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class WildcardTests
{
    [Theory]
    [InlineData("file.tmp", "*.tmp", true)]
    [InlineData("FILE.TMP", "*.tmp", true)]
    [InlineData("file.tmpx", "*.tmp", false)]
    [InlineData("~$doc.docx", "~*.*", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes", @"HKEY_LOCAL_MACHINE\SOFT????\Classes", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWAREX\Classes", @"HKEY_LOCAL_MACHINE\SOFT????\Classes", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\Anything goes\Classes", @"HKEY_LOCAL_MACHINE\*\Classes", true)]
    [InlineData("a+b(c).txt", "a+b(c).*", true)]
    public void Matches(string text, string pattern, bool expected) => Assert.Equal(expected, Wildcard.IsMatch(text, pattern));
}

public class RegPathTests
{
    [Fact]
    public void ParsesAbbreviationsAndComputerPrefix()
    {
        var p = RegPath.Parse(@"Computer\HKLM\SOFTWARE\Foo\");
        Assert.Equal(RegistryHive.LocalMachine, p.Hive);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Foo", p.FullPath);
        Assert.False(RegPath.TryParse(@"HKXX\Foo", out _));
    }

    [Fact]
    public void ThirtyTwoBitViewRendersWow6432Node()
    {
        var p = new RegPath(RegistryHive.LocalMachine, @"SOFTWARE\Foo", RegistryView.Registry32);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Foo", p.FullPath);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", new RegPath(RegistryHive.LocalMachine, "SOFTWARE", RegistryView.Registry32).FullPath);
        Assert.True(p.SameKeyAs(RegPath.Parse(@"HKLM\SOFTWARE\WOW6432Node\Foo")));
    }

    [Fact]
    public void ParentChildDepthAndIsUnder()
    {
        var p = RegPath.Parse(@"HKCU\Software\A\B");
        Assert.Equal(@"HKEY_CURRENT_USER\Software\A", p.Parent!.FullPath);
        Assert.Equal(3, p.Depth);
        Assert.Equal("B", p.Name);
        Assert.True(p.IsUnder(RegPath.Parse(@"HKCU\Software")));
        Assert.False(p.IsUnder(RegPath.Parse(@"HKCU\Software\A\B")));
        Assert.Null(RegPath.Parse("HKCU").Parent);
    }

    [Fact]
    public void DeleteRefusesHiveAndReportsMissingAsSuccess()
    {
        Assert.False(RegistryUtil.DeleteKeyTree(RegPath.Parse("HKCU"), out var err));
        Assert.NotNull(err);
        Assert.True(RegistryUtil.DeleteKeyTree(RegPath.Parse(@"HKCU\Software\UnravelTests\does-not-exist-" + Guid.NewGuid()), out _));
    }

    [Fact]
    public void LastWriteTimeIsRecent()
    {
        using var sb = new RegistrySandbox();
        var k = sb.CreateKey("Stamp", ("v", "1"));
        var t = k.LastWriteTime();
        Assert.NotNull(t);
        Assert.True((DateTime.Now - t!.Value).Duration() < TimeSpan.FromMinutes(5));
    }
}

public class CommandLineTests
{
    [Fact]
    public void SplitsQuotedPath()
    {
        var (f, a) = CommandLine.Split("\"C:\\Program Files\\X\\unins000.exe\" /SILENT");
        Assert.Equal(@"C:\Program Files\X\unins000.exe", f);
        Assert.Equal("/SILENT", a);
    }

    [Fact]
    public void SplitsUnquotedPathWithSpacesUsingFileExistence()
    {
        using var t = new TempDir();
        var exe = t.File(@"My App\un install.exe");
        var (f, a) = CommandLine.Split($"{exe} /S /D=x");
        Assert.Equal(exe, f);
        Assert.Equal("/S /D=x", a);
    }

    [Fact]
    public void SplitsMsiexecWithoutPath()
    {
        var (f, a) = CommandLine.Split("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}");
        Assert.Equal("MsiExec.exe", f);
        Assert.Equal("/X{11111111-2222-3333-4444-555555555555}", a);
    }

    [Theory]
    [InlineData("MsiExec.exe /I{aaaaaaaa-2222-3333-4444-555555555555}", "{AAAAAAAA-2222-3333-4444-555555555555}")]
    [InlineData("\"C:\\x\\setup.exe\" /uninstall {aaaaaaaa-2222-3333-4444-555555555555}", null)]
    [InlineData(null, null)]
    public void ExtractsMsiProductCode(string? cmd, string? expected) => Assert.Equal(expected, CommandLine.ExtractMsiProductCode(cmd));

    [Theory]
    [InlineData("\"C:\\a b\\app.exe\",0", "C:\\a b\\app.exe", 0)]
    [InlineData("C:\\a\\app.exe,-101", "C:\\a\\app.exe", -101)]
    [InlineData("C:\\a\\app.ico", "C:\\a\\app.ico", 0)]
    public void ParsesIconPath(string value, string path, int index)
    {
        var (p, i) = CommandLine.ParseIconPath(value);
        Assert.Equal(path, p);
        Assert.Equal(index, i);
    }

    [Fact]
    public void FindsExecutableInsideCommand()
    {
        Assert.Equal(@"C:\Tools\a.exe", CommandLine.FindExecutablePath("rundll32.exe C:\\Tools\\a.exe,Entry"));
        Assert.Equal(@"C:\x y\app.exe", CommandLine.FindExecutablePath("\"C:\\x y\\app.exe\" --tray"));
    }
}

public class MsiGuidTests
{
    [Fact]
    public void PacksKnownExample()
    {
        Assert.Equal("87654321BA09FEDC2143658709BADCFE", MsiGuid.Pack("{12345678-90AB-CDEF-1234-567890ABCDEF}"));
    }

    [Fact]
    public void RoundTrips()
    {
        var g = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        Assert.Equal(g, MsiGuid.Unpack(MsiGuid.Pack(g)));
    }
}

public class FileOpsTests
{
    [Fact]
    public void DeletesFoldersWithReadOnlyContentPermanently()
    {
        using var t = new TempDir();
        var f = t.File(@"a\b\c.txt");
        File.SetAttributes(f, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var r = FileOps.Delete(t.Path("a"), toRecycleBin: false);
        Assert.Equal(DeleteOutcome.Deleted, r.Outcome);
        Assert.False(Directory.Exists(t.Path("a")));
    }

    [Fact]
    public void MissingPathIsNotFound() => Assert.Equal(DeleteOutcome.NotFound, FileOps.Delete(@"C:\definitely\not\here\" + Guid.NewGuid(), false).Outcome);

    [Fact]
    public void LockedFileFailsWithoutScheduling()
    {
        using var t = new TempDir();
        var f = t.File("locked.txt");
        using var hold = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = FileOps.Delete(f, toRecycleBin: false, scheduleIfLocked: false);
        Assert.Equal(DeleteOutcome.Failed, r.Outcome);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void IsSameOrUnderIsPathAware()
    {
        Assert.True(FileOps.IsSameOrUnder(@"C:\A\B\c.exe", @"C:\A\B"));
        Assert.True(FileOps.IsSameOrUnder(@"C:\A\B", @"c:\a\b\"));
        Assert.False(FileOps.IsSameOrUnder(@"C:\A\BC\d.exe", @"C:\A\B"));
    }

    [Fact]
    public void DirectorySizeSumsFiles()
    {
        using var t = new TempDir();
        t.File(@"x\1.bin", new string('a', 1000));
        t.File(@"x\y\2.bin", new string('b', 500));
        Assert.Equal(1500, FileOps.GetDirectorySize(t.Path("x")));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void FormatsBytes(long b, string s) => Assert.Equal(s, Format.Bytes(b));
}

public class ShellLinkTests
{
    [Fact]
    public void CreatesAndReadsShortcut()
    {
        using var t = new TempDir();
        var target = t.File(@"bin\tool.exe");
        var lnk = t.Path("Tool.lnk");
        ShellLink.Create(lnk, target, "--flag");
        var info = ShellLink.Read(lnk);
        Assert.NotNull(info);
        Assert.Equal(target, info!.TargetPath, ignoreCase: true);
        Assert.Equal("--flag", info.Arguments);
    }

    [Fact]
    public void ReadsUrlFile()
    {
        using var t = new TempDir();
        var f = t.File("site.url", "[InternetShortcut]\r\nURL=https://example.org/\r\n");
        Assert.Equal("https://example.org/", ShellLink.ReadUrlFile(f));
    }
}

public class SettingsTests
{
    [Fact]
    public void RoundTripsSettings()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.Root);
        store.Load();
        store.Current.Theme = ThemeMode.Dark;
        store.Current.Uninstaller.DefaultScanMode = ScanMode.Advanced;
        store.Current.Uninstaller.RegistryExcludes.Add(new ExcludeRule { Pattern = @"HKEY_CURRENT_USER\Software\Keep*", IncludeSubkeys = true });
        store.Current.Junk.Patterns.RemoveAt(0);
        store.Save();

        var again = new SettingsStore(t.Root).Load();
        Assert.Equal(ThemeMode.Dark, again.Theme);
        Assert.Equal(ScanMode.Advanced, again.Uninstaller.DefaultScanMode);
        Assert.Contains(again.Uninstaller.RegistryExcludes, r => r.Pattern.EndsWith("Keep*"));
        Assert.Equal(new JunkSettings().Patterns.Count - 1, again.Junk.Patterns.Count);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsAndKeepsCopy()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Root, "settings.json"), "{ this is not json");
        var store = new SettingsStore(t.Root);
        var s = store.Load();
        Assert.NotNull(store.LoadError);
        Assert.True(s.Uninstaller.CreateRestorePoint);
        Assert.True(File.Exists(Path.Combine(t.Root, "settings.json.corrupt")));
    }
}
