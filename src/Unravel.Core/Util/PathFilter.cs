namespace Unravel.Core.Util;

/// <summary>
/// Fast matcher for many wildcard path patterns. Patterns of the form "prefix\*" (no other wildcards) become
/// prefix checks, "*.ext" become name checks, and the rest fall back to wildcard regexes.
/// </summary>
public sealed class PathFilter
{
    private readonly List<string> _prefixes = [];
    private readonly List<string> _segments = [];
    private readonly List<string> _namePatterns = [];
    private readonly List<string> _general = [];

    public PathFilter(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var p = Environment.ExpandEnvironmentVariables(raw.Trim());
            if (p.Length == 0) continue;
            if (p.StartsWith("*\\") && p.EndsWith("\\*") && p.Length > 4 && !Wildcard.HasWildcards(p[2..^2])) _segments.Add(p[1..^1]); // "\\seg\\"
            else if (p.EndsWith("\\*") && !Wildcard.HasWildcards(p[..^2])) _prefixes.Add(p[..^1]); // keep the trailing backslash
            else if (!p.Contains('\\')) _namePatterns.Add(p);
            else _general.Add(p);
        }
    }

    /// <summary>True when the path itself, or a folder above it, matches.</summary>
    public bool IsMatch(string path)
    {
        foreach (var pre in _prefixes)
            if (path.StartsWith(pre, StringComparison.OrdinalIgnoreCase) || path.Equals(pre[..^1], StringComparison.OrdinalIgnoreCase)) return true;
        if (_segments.Count > 0)
        {
            var withSlash = path + "\\";
            foreach (var seg in _segments) if (withSlash.Contains(seg, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (_namePatterns.Count > 0)
        {
            var name = Path.GetFileName(path);
            foreach (var n in _namePatterns) if (Wildcard.IsMatch(name, n)) return true;
        }
        foreach (var g in _general)
            if (Wildcard.IsMatch(path, g) || Wildcard.IsMatch(path + "\\", g)) return true;
        return false;
    }
}
