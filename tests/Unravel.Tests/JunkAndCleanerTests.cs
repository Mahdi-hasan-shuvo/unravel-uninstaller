using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Unravel.Core.Cleaners;
using Unravel.Core.Junk;
using Unravel.Core.Settings;
using Unravel.Core.Util;

namespace Unravel.Tests;

public class JunkScannerTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly DateTime _old = DateTime.Now.AddDays(-10);
    public void Dispose() => _t.Dispose();

    private JunkScanOptions Options(params string[] excludes) => new()
    {
        Roots = [_t.Root],
        Patterns = ["*.tmp", "~*.*", "thumbs.db"],
        ExcludePatterns = excludes,
    };

    [Fact]
    public void FindsMatchingOldFilesAndSkipsRecentOnes()
    {
        _t.File(@"a\x.tmp", "12345", _old);
        _t.File(@"a\~lock.docx", "1", _old);
        _t.File(@"a\b\Thumbs.db", "1", _old);
        _t.File(@"a\keep.txt", "1", _old);
        _t.File(@"a\fresh.tmp", "1");                       // modified now → ignored by the 24-hour rule
        var found = JunkScanner.Scan(Options(), null, default);
        Assert.Equal(["Thumbs.db", "x.tmp", "~lock.docx"], found.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(5, found.Single(f => f.Name == "x.tmp").Size);
    }

    [Fact]
    public void RecentFilesIncludedWhenOptionOff()
    {
        _t.File("fresh.tmp", "1");
        var found = JunkScanner.Scan(new JunkScanOptions { Roots = [_t.Root], Patterns = ["*.tmp"], IgnoreRecentlyAccessed = false }, null, default);
        Assert.Single(found);
    }

    [Fact]
    public void ExcludePatternsSkipFolders()
    {
        _t.File(@"keep\x.tmp", "1", _old);
        _t.File(@"scan\y.tmp", "1", _old);
        var found = JunkScanner.Scan(Options(_t.Path("keep") + "\\*"), null, default);
        Assert.Equal(["y.tmp"], found.Select(f => f.Name));
        Assert.True(JunkScanner.IsExcluded(@"C:\Windows", [@"?:\Windows\*"]));
        Assert.True(JunkScanner.IsExcluded(@"D:\Windows\Temp\a.tmp", [@"?:\Windows\*"]));
        Assert.False(JunkScanner.IsExcluded(@"C:\WindowsApps2\a.tmp", [@"?:\Windows\*"]));
    }

    [Fact]
    public void DoesNotFollowJunctions()
    {
        var outside = new TempDir();
        try
        {
            File.WriteAllText(Path.Combine(outside.Root, "outside.tmp"), "x");
            File.SetLastWriteTime(Path.Combine(outside.Root, "outside.tmp"), _old);
            File.SetLastAccessTime(Path.Combine(outside.Root, "outside.tmp"), _old);
            File.SetCreationTime(Path.Combine(outside.Root, "outside.tmp"), _old);
            var link = _t.Path("link");
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
            Assert.True(Directory.Exists(link));
            var found = JunkScanner.Scan(Options(), null, default);
            Assert.DoesNotContain(found, f => f.Name == "outside.tmp");
            Directory.Delete(link); // remove the junction only
        }
        finally { outside.Dispose(); }
    }

    [Fact]
    public void CancellationReturnsPartialResults()
    {
        for (int i = 0; i < 50; i++) _t.File($@"d{i}\f.tmp", "1", _old);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var found = JunkScanner.Scan(Options(), null, cts.Token);
        Assert.True(found.Count < 50);
    }

    [Fact]
    public void RequiresDrivesAndPatterns()
    {
        Assert.Throws<ArgumentException>(() => JunkScanner.Scan(new JunkScanOptions { Roots = [], Patterns = ["*.tmp"] }, null, default));
        Assert.Throws<ArgumentException>(() => JunkScanner.Scan(new JunkScanOptions { Roots = [_t.Root], Patterns = [] }, null, default));
    }

    [Fact]
    public void DeletesAndReportsLockedFiles()
    {
        var a = _t.File("a.tmp", "1", _old);
        var b = _t.File("b.tmp", "1", _old);
        var found = JunkScanner.Scan(Options(), null, default);
        using var hold = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None);
        var results = JunkScanner.Delete(found, toRecycleBin: false);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Single(results, r => !r.Succeeded);
    }

    [Fact]
    public void SettingsDefaultsBuildOptions()
    {
        var o = JunkScanOptions.FromSettings(new JunkSettings(), ["C:\\"]);
        Assert.Contains("*.tmp", o.Patterns);
        Assert.DoesNotContain("*.log", o.Patterns); // disabled by default
        Assert.Contains(@"?:\Windows\*", o.ExcludePatterns);
    }
}

public class CleanupEngineTests : IDisposable
{
    private readonly RegistrySandbox _sb = new();
    private readonly TempDir _t = new();
    public void Dispose() { _sb.Dispose(); _t.Dispose(); SqliteConnection.ClearAllPools(); }

    private static CleanerItem Item(params CleanupTarget[] targets) => new() { Id = "t", Group = "g", Title = "t", Targets = () => targets };

    [Fact]
    public void RegistryModesPreviewThenExecute()
    {
        _sb.CreateKey("Mru", ("a", "1"), ("b", "2"), ("MRUList", "ab"));
        _sb.CreateKey(@"Tree\Sub1", ("x", "1"));
        _sb.CreateKey("Tree", ("top", "1"));
        _sb.CreateKey("Single", ("LastKey", "x"), ("Keep", "y"));
        var item = Item(
            new RegistryTarget(_sb.Path("Mru"), RegistryCleanMode.Values),
            new RegistryTarget(_sb.Path("Tree"), RegistryCleanMode.ValuesAndSubkeys),
            new RegistryTarget(_sb.Path("Single"), RegistryCleanMode.SingleValue, "LastKey"),
            new RegistryTarget(_sb.Path("Missing"), RegistryCleanMode.Values));
        var preview = CleanupEngine.Preview(item);
        Assert.Equal(3 + 2 + 1, preview.Items);
        using (var k = _sb.Path("Mru").Open()) Assert.Equal(3, k!.ValueCount); // preview changes nothing

        var done = CleanupEngine.Execute(item);
        Assert.Empty(done.Errors);
        using (var k = _sb.Path("Mru").Open()) Assert.Equal(0, k!.ValueCount);
        using (var k = _sb.Path("Tree").Open()) { Assert.Equal(0, k!.ValueCount); Assert.Equal(0, k.SubKeyCount); }
        using (var k = _sb.Path("Single").Open()) Assert.Equal(["Keep"], k!.GetValueNames());
        Assert.Equal(0, CleanupEngine.Preview(item).Items);
    }

    [Fact]
    public void FilesTargetHonoursPatternAgeAndLocks()
    {
        var old = DateTime.Now.AddDays(-3);
        _t.File(@"tmp\a.log", "123", old);
        _t.File(@"tmp\sub\b.log", "45", old);
        _t.File(@"tmp\new.log", "1");
        var locked = _t.File(@"tmp\locked.log", "1", old);
        _t.File(@"tmp\keep.txt", "1", old);
        var item = Item(new FilesTarget(_t.Path("tmp"), "*.log", Recursive: true, RemoveSubfolders: true, MinAge: TimeSpan.FromHours(24)));
        Assert.Equal(3, CleanupEngine.Preview(item).Items);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = CleanupEngine.Execute(item);
        Assert.Equal(2, r.Items);
        Assert.Equal(5, r.Bytes);
        Assert.Single(r.Errors);
        Assert.False(Directory.Exists(_t.Path("tmp", "sub")));
        Assert.True(File.Exists(_t.Path("tmp", "new.log")));
        Assert.True(File.Exists(_t.Path("tmp", "keep.txt")));
    }

    private static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(string db, string table)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void ChromiumProfileIsCleanedPerCategory()
    {
        var local = _t.Dir("Local");
        var profile = _t.Dir("Local", @"Google\Chrome\User Data\Default");
        _t.Dir("Local", @"Google\Chrome\User Data\Profile 2");
        _t.Dir("Local", @"Google\Chrome\User Data\System Profile"); // not a user profile
        var history = Path.Combine(profile, "History");
        Exec(history, "CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT); CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER);" +
                      "CREATE TABLE downloads(id INTEGER PRIMARY KEY, target_path TEXT); CREATE TABLE downloads_url_chains(id INTEGER, url TEXT);" +
                      "CREATE TABLE meta(key TEXT, value TEXT);" +
                      "INSERT INTO urls(url) VALUES('https://a.example'),('https://b.example'); INSERT INTO visits(url) VALUES(1),(2),(2);" +
                      "INSERT INTO downloads(target_path) VALUES('C:\\x.zip'); INSERT INTO downloads_url_chains VALUES(1,'https://a.example/x.zip');" +
                      "INSERT INTO meta VALUES('version','1');");
        Directory.CreateDirectory(Path.Combine(profile, "Network"));
        var cookies = Path.Combine(profile, "Network", "Cookies");
        Exec(cookies, "CREATE TABLE cookies(host_key TEXT); INSERT INTO cookies VALUES('.a.example'),('.b.example');");
        Exec(Path.Combine(profile, "Web Data"), "CREATE TABLE autofill(name TEXT, value TEXT); CREATE TABLE credit_cards(id TEXT);" +
                                                "INSERT INTO autofill VALUES('email','me@example.com'); INSERT INTO credit_cards VALUES('card');");
        _t.File(@"Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_000001", new string('c', 100));
        _t.File(@"Local\Google\Chrome\User Data\Default\Sessions\Session_1", "s");

        var browsers = BrowserCleaner.Detect(new BrowserCleaner.Locations { LocalAppData = local, RoamingAppData = _t.Dir("Roaming"), IncludeWinInet = false });
        var chrome = Assert.Single(browsers);
        Assert.Equal("Google Chrome", chrome.Name);
        Assert.Equal(2, chrome.ProfileDirs.Count);
        var items = BrowserCleaner.BuildItems(browsers).ToDictionary(i => i.Id.Split(':')[1]);

        Assert.Equal(5, CleanupEngine.Preview(items["history"]).Items);   // 2 urls + 3 visits
        var h = CleanupEngine.Execute(items["history"]);
        Assert.Empty(h.Errors);
        Assert.Equal(0, Count(history, "urls"));
        Assert.Equal(0, Count(history, "visits"));
        Assert.Equal(1, Count(history, "downloads"));                       // downloads are a separate choice
        Assert.Equal(1, Count(history, "meta"));                            // schema metadata kept

        CleanupEngine.Execute(items["downloads"]);
        Assert.Equal(0, Count(history, "downloads"));
        Assert.Equal(0, Count(history, "downloads_url_chains"));

        Assert.Equal(2, CleanupEngine.Execute(items["cookies"]).Items);
        Assert.Equal(0, Count(cookies, "cookies"));

        CleanupEngine.Execute(items["forms"]);
        Assert.Equal(0, Count(Path.Combine(profile, "Web Data"), "autofill"));
        Assert.Equal(1, Count(Path.Combine(profile, "Web Data"), "credit_cards")); // payment cards are kept

        Assert.Equal(1, CleanupEngine.Execute(items["cache"]).Items);
        Assert.False(File.Exists(Path.Combine(profile, @"Cache\Cache_Data\f_000001")));
        Assert.Equal(1, CleanupEngine.Execute(items["sessions"]).Items);
    }

    [Fact]
    public void LockedBrowserDatabaseReportsInUse()
    {
        var local = _t.Dir("Local");
        var profile = _t.Dir("Local", @"Microsoft\Edge\User Data\Default");
        var history = Path.Combine(profile, "History");
        Exec(history, "CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT); INSERT INTO urls(url) VALUES('x');");
        using var holder = new SqliteConnection($"Data Source={history};Pooling=False");
        holder.Open();
        using (var cmd = holder.CreateCommand()) { cmd.CommandText = "BEGIN EXCLUSIVE"; cmd.ExecuteNonQuery(); }
        var items = BrowserCleaner.BuildItems(BrowserCleaner.Detect(new BrowserCleaner.Locations { LocalAppData = local, RoamingAppData = _t.Dir("R"), IncludeWinInet = false }));
        var r = CleanupEngine.Execute(items.Single(i => i.Id == "Microsoft Edge:history"));
        Assert.Contains(r.Errors, e => e.Contains("in use"));
        Assert.Equal(["msedge"], items[0].BlockingProcesses);
    }

    [Fact]
    public void FirefoxHistoryKeepsBookmarkedPlaces()
    {
        var roaming = _t.Dir("Roaming");
        var profile = _t.Dir("Roaming", @"Mozilla\Firefox\Profiles\abcd.default-release");
        var places = Path.Combine(profile, "places.sqlite");
        Exec(places, "CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, foreign_count INTEGER DEFAULT 0);" +
                     "CREATE TABLE moz_historyvisits(id INTEGER PRIMARY KEY, place_id INTEGER);" +
                     "CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, fk INTEGER);" +
                     "CREATE TABLE moz_anno_attributes(id INTEGER PRIMARY KEY, name TEXT); CREATE TABLE moz_annos(id INTEGER PRIMARY KEY, anno_attribute_id INTEGER);" +
                     "INSERT INTO moz_places(url, foreign_count) VALUES('https://bookmarked.example',1),('https://visited.example',0);" +
                     "INSERT INTO moz_historyvisits(place_id) VALUES(1),(2); INSERT INTO moz_bookmarks(fk) VALUES(1);" +
                     "INSERT INTO moz_anno_attributes(name) VALUES('downloads/destinationFileURI'),('other/thing');" +
                     "INSERT INTO moz_annos(anno_attribute_id) VALUES(1),(2);");
        Exec(Path.Combine(profile, "formhistory.sqlite"), "CREATE TABLE moz_formhistory(fieldname TEXT); INSERT INTO moz_formhistory VALUES('q');");
        var items = BrowserCleaner.BuildItems(BrowserCleaner.Detect(new BrowserCleaner.Locations { LocalAppData = _t.Dir("Local"), RoamingAppData = roaming, IncludeWinInet = false }))
            .ToDictionary(i => i.Id.Split(':')[1]);
        CleanupEngine.Execute(items["history"]);
        Assert.Equal(1, Count(places, "moz_places"));
        Assert.Equal(0, Count(places, "moz_historyvisits"));
        CleanupEngine.Execute(items["downloads"]);
        Assert.Equal(1, Count(places, "moz_annos"));
        CleanupEngine.Execute(items["forms"]);
        Assert.Equal(0, Count(Path.Combine(profile, "formhistory.sqlite"), "moz_formhistory"));
    }

    [Fact]
    public void OfficeCleanerClearsAllVersionsAndUserLists()
    {
        _sb.CreateKey(@"Office\16.0\Word\File MRU", ("Max Display", 25), ("Item 1", "[F00000000][T01][O00000000]*C:\\a.docx"), ("Item Metadata 1", "x"));
        _sb.CreateKey(@"Office\16.0\Word\User MRU\ADAL_123\File MRU", ("Item 1", "y"));
        _sb.CreateKey(@"Office\11.0\Excel\Recent Files", ("File1", "C:\\b.xls"));
        _sb.CreateKey(@"Office\12.0\Access\Settings", ("MRU1", "C:\\db.mdb"), ("MRUFlags1", 1), ("Keep", "x"));
        _sb.CreateKey(@"Office\Common\Something", ("x", "1"));
        var items = OfficeCleaner.BuildItems(_sb.Path("Office"), _sb.Path("FrontPage")).ToDictionary(i => i.Id);
        Assert.Equal(3, CleanupEngine.Preview(items["office:Word"]).Items);
        CleanupEngine.Execute(items["office:Word"]);
        using (var k = _sb.Path(@"Office\16.0\Word\File MRU").Open()) Assert.Equal(["Max Display"], k!.GetValueNames());
        using (var k = _sb.Path(@"Office\16.0\Word\User MRU\ADAL_123\File MRU").Open()) Assert.Empty(k!.GetValueNames());
        CleanupEngine.Execute(items["office:Excel"]);
        using (var k = _sb.Path(@"Office\11.0\Excel\Recent Files").Open()) Assert.Empty(k!.GetValueNames());
        CleanupEngine.Execute(items["office:Access"]);
        using (var k = _sb.Path(@"Office\12.0\Access\Settings").Open()) Assert.Equal(["Keep"], k!.GetValueNames());
        Assert.Equal(0, CleanupEngine.Preview(items["office:FrontPage"]).Items);
    }

    [Fact]
    public void WindowsCleanerTargetsRedirectedLocations()
    {
        var cv = _sb.CreateKey("CV");
        _sb.CreateKey(@"CV\Explorer\RecentDocs\.txt", ("0", new byte[] { 1 }));
        _sb.CreateKey(@"CV\Explorer\RecentDocs", ("MRUListEx", new byte[] { 0 }));
        _sb.CreateKey(@"CV\Explorer\RunMRU", ("a", "cmd\\1"), ("MRUList", "a"));
        _sb.CreateKey(@"CV\Explorer\WordWheelQuery", ("0", new byte[] { 1 }));
        _sb.CreateKey(@"CV\Applets\Regedit", ("LastKey", "Computer\\HKCU"), ("View", new byte[] { 1 }));
        _sb.CreateKey(@"CV\Applets\Paint\Recent File List", ("File1", "C:\\p.png"));
        _sb.CreateKey(@"CV\Explorer\ComDlg32\OpenSavePidlMRU\txt", ("0", new byte[] { 1 }));
        _sb.CreateKey(@"CV\Explorer\ComDlg32\LastVisitedPidlMRU", ("0", new byte[] { 1 }));
        _sb.CreateKey(@"CV\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count", ("HRZR_PGYFRFFVBA", new byte[] { 1 }));
        var recent = _t.Dir("Recent");
        _t.File(@"Recent\doc.lnk", "l");
        var temp = _t.Dir("Temp");
        _t.File(@"Temp\old\a.tmp", "abc", DateTime.Now.AddDays(-2));
        _t.File(@"Temp\new.tmp", "abc");
        var local = _t.Dir("Local");
        _t.File(@"Local\CrashDumps\app.exe.123.dmp", "d");
        _t.File(@"Local\Microsoft\Windows\Explorer\thumbcache_256.db", "t");
        var drive = _t.Dir("Drive");
        _t.File(@"Drive\FOUND.000\FILE0000.CHK", "c");

        var items = WindowsCleaner.BuildItems(new WindowsCleaner.Locations
        {
            CurrentVersion = cv, RecentFolder = recent, UserTemp = temp, WindowsTemp = _t.Path("WinTemp"),
            LocalAppData = local, WindowsDir = _t.Path("Win"), FixedDrives = [drive], IncludeSystemActions = false,
        }).ToDictionary(i => i.Id);
        Assert.DoesNotContain("win:clipboard", items.Keys);

        foreach (var item in items.Values)
        {
            var r = CleanupEngine.Execute(item);
            Assert.True(r.Errors.Count == 0, item.Id + ": " + string.Join("; ", r.Errors));
        }
        using (var k = _sb.Path(@"CV\Explorer\RecentDocs").Open()) { Assert.Equal(0, k!.ValueCount); Assert.Equal(0, k.SubKeyCount); }
        using (var k = _sb.Path(@"CV\Explorer\RunMRU").Open()) Assert.Equal(0, k!.ValueCount);
        using (var k = _sb.Path(@"CV\Applets\Regedit").Open()) Assert.Equal(["View"], k!.GetValueNames());
        using (var k = _sb.Path(@"CV\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count").Open()) Assert.Equal(0, k!.ValueCount);
        Assert.False(File.Exists(_t.Path("Recent", "doc.lnk")));
        Assert.False(File.Exists(_t.Path("Temp", "old", "a.tmp")));
        Assert.True(File.Exists(_t.Path("Temp", "new.tmp")));          // recent temp files are kept
        Assert.False(File.Exists(_t.Path("Local", "CrashDumps", "app.exe.123.dmp")));
        Assert.False(File.Exists(_t.Path("Local", @"Microsoft\Windows\Explorer\thumbcache_256.db")));
        Assert.False(File.Exists(_t.Path("Drive", "FOUND.000", "FILE0000.CHK")));
    }

    [Fact]
    public void RealSystemPreviewsDoNotThrow()
    {
        foreach (var item in WindowsCleaner.BuildItems().Concat(OfficeCleaner.BuildItems()).Concat(BrowserCleaner.BuildItems(BrowserCleaner.Detect())))
        {
            if (item.Targets().OfType<ActionTarget>().Any(a => a.Label.Contains("Internet"))) continue;
            var r = CleanupEngine.Preview(item);
            Assert.True(r.Items >= 0);
        }
    }
}
