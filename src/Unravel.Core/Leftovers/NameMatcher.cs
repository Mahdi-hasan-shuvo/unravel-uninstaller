using System.Text;
using System.Text.RegularExpressions;
using Unravel.Core.Util;

namespace Unravel.Core.Leftovers;

/// <summary>Derives the names a program is likely to use for its registry keys and folders, and compares candidates to them.</summary>
public static class NameMatcher
{
    // Words that are far too generic to identify a single program on their own.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "common", "commonfiles", "program", "programs", "programfiles", "update", "updates", "updater",
        "setup", "install", "installer", "uninstall", "uninstaller", "bin", "app", "apps", "application", "applications",
        "tools", "tool", "system", "shared", "data", "temp", "tmp", "cache", "x64", "x86", "win32", "win64", "software",
        "classes", "clsid", "policies", "default", "user", "users", "local", "roaming", "locallow", "settings", "config",
        "plugins", "plugin", "driver", "drivers", "runtime", "service", "services", "client", "server", "help", "docs",
        "packages", "package", "inc", "llc", "ltd", "corp", "corporation", "company", "group", "the", "and", "for", "free",
        "pro", "professional", "edition", "version", "beta", "google", "mozilla", "apple", "adobe", "intel", "nvidia", "amd",
        "logs", "log", "backup", "documents", "desktop", "downloads", "pictures", "music", "videos", "startmenu", "start",
        "wow6432node", "appdata", "programdata", "public", "all", "new", "files", "file", "home"
    };

    // Vendor names that appear across many unrelated products; they are kept as publisher folders but never matched alone.
    public static bool IsGeneric(string s) => Generic.Contains(Normalize(s)) || Normalize(s).Length < 3;

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>"Foo Bar 2.1.3 (x64) en-US" → "Foo Bar".</summary>
    public static string StripVersion(string name)
    {
        var s = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", " ");                      // (x64), [beta]
        s = Regex.Replace(s, @"\s+v?\d+([\.\-_]\d+)*[a-z]?(\s|$).*$", " ", RegexOptions.IgnoreCase); // version and anything after
        s = Regex.Replace(s, @"\s+(x64|x86|64-bit|32-bit|amd64|arm64)\b.*$", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+-\s*$", "");
        return s.Trim().Trim('-', ',', ' ');
    }

    public static string StripCompanySuffix(string publisher)
    {
        var s = Regex.Replace(publisher, @"[,\.]?\s+(Inc\.?|Incorporated|LLC|L\.L\.C\.|Ltd\.?|Limited|Corp\.?|Corporation|GmbH|AG|S\.A\.?|S\.L\.?|SAS|B\.V\.|Co\.?|Pty|Pvt|PBC|plc|s\.r\.o\.|Oy|AB|KG|SE)\b\.?", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[,\.]?\s+(Inc\.?|Ltd\.?|Corp\.?|Co\.?)$", "", RegexOptions.IgnoreCase);
        return s.Trim().Trim(',', '.', ' ');
    }

    public static List<string> NameVariants(ScanTarget t)
    {
        var raw = new List<string?> { t.DisplayName, StripVersion(t.DisplayName) };
        if (t.KeyName is { } k && !CommandLine.IsGuid(k))
        {
            raw.Add(Regex.Replace(k, "_is1$", "", RegexOptions.IgnoreCase));
            raw.Add(StripVersion(Regex.Replace(k, "_is1$", "", RegexOptions.IgnoreCase)));
        }
        if (t.InstallFolder is { } f)
        {
            var leaf = Path.GetFileName(f.TrimEnd('\\'));
            raw.Add(leaf);
            raw.Add(StripVersion(leaf));
        }
        // Without a publisher, product names like "Foo Bar" are often stored as "FooBar" – Normalize handles that.
        return raw.Where(s => !string.IsNullOrWhiteSpace(s))
                  .Select(s => s!.Trim())
                  .Where(s => !IsGeneric(s) && Normalize(s).Length >= 3)
                  .DistinctBy(Normalize, StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    public static List<string> PublisherVariants(ScanTarget t)
    {
        var raw = new List<string?>();
        if (!string.IsNullOrWhiteSpace(t.Publisher))
        {
            raw.Add(t.Publisher);
            raw.Add(StripCompanySuffix(t.Publisher));
        }
        if (t.InstallFolder is { } f)
        {
            // C:\Program Files\<Vendor>\<Product> – the vendor folder is a strong hint.
            var parent = Path.GetDirectoryName(f.TrimEnd('\\'));
            var grand = parent is null ? null : Path.GetDirectoryName(parent);
            if (parent is not null && grand is not null && IsProgramFilesRoot(grand)) raw.Add(Path.GetFileName(parent));
        }
        return raw.Where(s => !string.IsNullOrWhiteSpace(s))
                  .Select(s => s!.Trim())
                  .Where(s => Normalize(s).Length >= 3 && !s.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
                  .DistinctBy(Normalize, StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    private static bool IsProgramFilesRoot(string dir)
    {
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p) && p.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return Regex.IsMatch(dir.TrimEnd('\\'), @"^[A-Za-z]:\\Program Files( \(x86\))?$", RegexOptions.IgnoreCase);
    }

    /// <summary>Exact match after normalization ("Foo Bar" == "foo-bar" == "FooBar").</summary>
    public static bool Matches(string candidate, IEnumerable<string> variants)
    {
        var c = Normalize(candidate);
        if (c.Length < 3) return false;
        return variants.Any(v => Normalize(v) == c);
    }

    /// <summary>Looser match used only in Advanced mode: the candidate starts with a long variant ("FooBar" → "FooBarHelper").</summary>
    public static bool LooseMatches(string candidate, IEnumerable<string> variants)
    {
        var c = Normalize(candidate);
        if (c.Length < 4) return false;
        return variants.Any(v =>
        {
            var n = Normalize(v);
            return n == c || (n.Length >= 5 && c.StartsWith(n, StringComparison.Ordinal) && c.Length - n.Length <= 12);
        });
    }
}
