using System.Text.RegularExpressions;

namespace Unravel.Core.Util;

/// <summary>Case-insensitive wildcard matching where '?' matches one character and '*' matches any run.</summary>
public static class Wildcard
{
    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Regex ToRegex(string pattern)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(pattern, out var rx)) return rx;
            var body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            rx = new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            Cache[pattern] = rx;
            return rx;
        }
    }

    public static bool IsMatch(string text, string pattern) => ToRegex(pattern).IsMatch(text);

    public static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;
}
