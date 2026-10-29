using System.Diagnostics;
using Microsoft.Win32;
using Unravel.Core.Util;

namespace Unravel.Core.Cleaners;

public enum BrowserEngine { Chromium, Firefox, WinInet }

public sealed record BrowserInfo(string Name, BrowserEngine Engine, string ProcessName, IReadOnlyList<string> ProfileDirs, IReadOnlyList<string> CacheProfileDirs);

/// <summary>Builds clean-up items for installed browsers: history, downloads, address bar, cookies, cache, form data and sessions.</summary>
public static class BrowserCleaner
{
    public sealed class Locations
    {
        public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public string RoamingAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public bool IncludeWinInet { get; init; } = true;
    }

    private static readonly (string Name, string Process, string RelativeUserData, bool Roaming, bool SingleProfile)[] ChromiumBrowsers =
    [
        ("Google Chrome", "chrome", @"Google\Chrome\User Data", false, false),
        ("Microsoft Edge", "msedge", @"Microsoft\Edge\User Data", false, false),
        ("Brave", "brave", @"BraveSoftware\Brave-Browser\User Data", false, false),
        ("Vivaldi", "vivaldi", @"Vivaldi\User Data", false, false),
        ("Chromium", "chrome", @"Chromium\User Data", false, false),
        ("Opera", "opera", @"Opera Software\Opera Stable", true, true),
        ("Opera GX", "opera", @"Opera Software\Opera GX Stable", true, true),
    ];

    public static List<BrowserInfo> Detect(Locations? loc = null)
    {
        loc ??= new Locations();
        var list = new List<BrowserInfo>();
        foreach (var b in ChromiumBrowsers)
        {
            var root = Path.Combine(b.Roaming ? loc.RoamingAppData : loc.LocalAppData, b.RelativeUserData);
            if (!Directory.Exists(root)) continue;
            List<string> profiles;
            if (b.SingleProfile) profiles = [root];
            else profiles = Directory.GetDirectories(root)
                .Where(d => Path.GetFileName(d) is "Default" or "Guest Profile" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (profiles.Count == 0) continue;
            // Opera keeps its cache under Local while data lives under Roaming.
            var cacheProfiles = b.Roaming
                ? [Path.Combine(loc.LocalAppData, b.RelativeUserData)]
                : profiles;
            list.Add(new BrowserInfo(b.Name, BrowserEngine.Chromium, b.Process, profiles, cacheProfiles));
        }

        var ffRoot = Path.Combine(loc.RoamingAppData, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ffRoot))
        {
            var profiles = Directory.GetDirectories(ffRoot).ToList();
            var caches = profiles.Select(p => Path.Combine(loc.LocalAppData, @"Mozilla\Firefox\Profiles", Path.GetFileName(p))).ToList();
            if (profiles.Count > 0) list.Add(new BrowserInfo("Mozilla Firefox", BrowserEngine.Firefox, "firefox", profiles, caches));
        }

        if (loc.IncludeWinInet) list.Add(new BrowserInfo("Internet Explorer / WinINet", BrowserEngine.WinInet, "iexplore", [], []));
        return list;
    }

    public static List<CleanerItem> BuildItems(IEnumerable<BrowserInfo> browsers) =>
        browsers.SelectMany(b => b.Engine switch
        {
            BrowserEngine.Chromium => Chromium(b),
            BrowserEngine.Firefox => Firefox(b),
            _ => WinInet(b),
        }).ToList();

    private static CleanerItem Item(BrowserInfo b, string id, string title, Func<IEnumerable<CleanupTarget>> targets, string? note = null) => new()
    {
        Id = $"{b.Name}:{id}", Group = b.Name, Title = title, Note = note, Targets = targets,
        BlockingProcesses = b.Engine == BrowserEngine.WinInet ? [] : [b.ProcessName],
    };

    private static IEnumerable<CleanerItem> Chromium(BrowserInfo b)
    {
        IEnumerable<string> P() => b.ProfileDirs;
        string Cookies(string p) => File.Exists(Path.Combine(p, "Network", "Cookies")) ? Path.Combine(p, "Network", "Cookies") : Path.Combine(p, "Cookies");

        yield return Item(b, "history", "Visited pages history", () => P().Select(p => (CleanupTarget)new SqliteTarget(Path.Combine(p, "History"),
        [
            new("visits"), new("visit_source"), new("segment_usage"), new("segments"), new("keyword_search_terms"),
            new("clusters_and_visits"), new("clusters"), new("context_annotations"), new("content_annotations"), new("visited_links"),
            new("urls"),
        ])).Concat(P().Select(p => (CleanupTarget)new FilesTarget(p, "History-journal"))));
        yield return Item(b, "downloads", "Download history", () => P().Select(p => (CleanupTarget)new SqliteTarget(Path.Combine(p, "History"),
            [new("downloads_url_chains"), new("downloads_slices"), new("downloads")])));
        yield return Item(b, "addressbar", "Address bar history and suggestions", () => P().SelectMany(p => new CleanupTarget[]
        {
            new SqliteTarget(Path.Combine(p, "Shortcuts"), [new("omni_box_shortcuts")]),
            new SqliteTarget(Path.Combine(p, "Top Sites"), [new("top_sites"), new("thumbnails")]),
            new SqliteTarget(Path.Combine(p, "Network Action Predictor"), [new("network_action_predictor"), new("resource_prefetch_predictor_url")]),
        }));
        yield return Item(b, "cookies", "Cookies", () => P().Select(p => (CleanupTarget)new SqliteTarget(Cookies(p), [new("cookies")])),
            "You will be signed out of websites.");
        yield return Item(b, "cache", "Temporary internet files (cache)", () => b.CacheProfileDirs.SelectMany(p => new CleanupTarget[]
        {
            new FilesTarget(Path.Combine(p, "Cache"), "*", true, true),
            new FilesTarget(Path.Combine(p, "Code Cache"), "*", true, true),
            new FilesTarget(Path.Combine(p, "GPUCache"), "*", true, true),
            new FilesTarget(Path.Combine(p, "Service Worker", "CacheStorage"), "*", true, true),
        }));
        yield return Item(b, "forms", "Saved form entries", () => P().Select(p => (CleanupTarget)new SqliteTarget(Path.Combine(p, "Web Data"), [new("autofill")])),
            "Saved passwords, addresses and payment cards are kept.");
        yield return Item(b, "sessions", "Session history (open tabs)", () => P().SelectMany(p => new CleanupTarget[]
        {
            new FilesTarget(Path.Combine(p, "Sessions"), "*"),
            new FilesTarget(p, "Current Session"), new FilesTarget(p, "Last Session"),
            new FilesTarget(p, "Current Tabs"), new FilesTarget(p, "Last Tabs"),
        }), "Tabs from the last session will not be restored.");
    }

    private static IEnumerable<CleanerItem> Firefox(BrowserInfo b)
    {
        IEnumerable<string> P() => b.ProfileDirs;
        yield return Item(b, "history", "Visited pages history", () => P().Select(p => (CleanupTarget)new SqliteTarget(Path.Combine(p, "places.sqlite"),
        [
            new("moz_historyvisits"), new("moz_inputhistory"),
            // Places referenced by bookmarks (or anything else, via foreign_count) must survive.
            new("moz_places", "id NOT IN (SELECT fk FROM moz_bookmarks WHERE fk IS NOT NULL) AND foreign_count = 0"),
        ])));
        yield return Item(b, "downloads", "Download history", () => P().Select(p => (CleanupTarget)new SqliteTarget(Path.Combine(p, "places.sqlite"),
            [new("moz_annos", "anno_attribute_id IN (SELECT id FROM moz_anno_attributes WHERE name LIKE 'downloads/%')")])));
        yield return Item(b, "cookies", "Cookies", () => P().Select(p => (CleanupTarget)new SqliteTarget(Path.Combine(p, "cookies.sqlite"), [new("moz_cookies")])),
            "You will be signed out of websites.");
        yield return Item(b, "cache", "Temporary internet files (cache)", () => b.CacheProfileDirs.SelectMany(p => new CleanupTarget[]
        {
            new FilesTarget(Path.Combine(p, "cache2"), "*", true, true),
            new FilesTarget(Path.Combine(p, "startupCache"), "*", true, true),
            new FilesTarget(Path.Combine(p, "thumbnails"), "*", true, true),
        }));
        yield return Item(b, "forms", "Saved form entries", () => P().Select(p => (CleanupTarget)new SqliteTarget(Path.Combine(p, "formhistory.sqlite"), [new("moz_formhistory")])));
        yield return Item(b, "sessions", "Session history (open tabs)", () => P().SelectMany(p => new CleanupTarget[]
        {
            new FilesTarget(p, "sessionstore.jsonlz4"),
            new FilesTarget(Path.Combine(p, "sessionstore-backups"), "*"),
        }), "Tabs from the last session will not be restored.");
    }

    private static IEnumerable<CleanerItem> WinInet(BrowserInfo b)
    {
        // Internet Explorer is retired, but the WinINet stores it used are still shared by many desktop apps.
        CleanerItem Tracks(string id, string title, int flags, Func<CleanCount> preview, string? note = null) =>
            Item(b, id, title, () => [new ActionTarget(title, preview, () => ClearMyTracks(flags, preview))], note);

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Tracks("history", "Visited pages history", 1, () => FolderCount(Path.Combine(local, @"Microsoft\Windows\History")));
        yield return Item(b, "typed", "Address bar history (typed URLs)", () =>
        [
            new RegistryTarget(new RegPath(RegistryHive.CurrentUser, @"Software\Microsoft\Internet Explorer\TypedURLs"), RegistryCleanMode.Values),
            new RegistryTarget(new RegPath(RegistryHive.CurrentUser, @"Software\Microsoft\Internet Explorer\TypedURLsTime"), RegistryCleanMode.Values),
        ]);
        yield return Tracks("cookies", "Cookies", 2, () => FolderCount(Path.Combine(local, @"Microsoft\Windows\INetCookies")));
        yield return Tracks("cache", "Temporary internet files (cache)", 8, () => FolderCount(Path.Combine(local, @"Microsoft\Windows\INetCache")));
        yield return Tracks("forms", "Saved form entries", 16, () => new CleanCount());
    }

    private static CleanCount FolderCount(string folder)
    {
        var c = new CleanCount();
        if (!Directory.Exists(folder)) return c;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("container.dat", StringComparison.OrdinalIgnoreCase)) continue;
            try { c.Items++; c.Bytes += f.Length; } catch { }
        }
        return c;
    }

    private static CleanCount ClearMyTracks(int flags, Func<CleanCount> measure)
    {
        var before = measure();
        using var p = Process.Start(new ProcessStartInfo("rundll32.exe", $"InetCpl.cpl,ClearMyTracksByProcess {flags}") { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(120000);
        var after = measure();
        return new CleanCount { Items = Math.Max(0, before.Items - after.Items), Bytes = Math.Max(0, before.Bytes - after.Bytes) };
    }
}
