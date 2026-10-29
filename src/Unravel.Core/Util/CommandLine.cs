using System.Text.RegularExpressions;

namespace Unravel.Core.Util;

/// <summary>Splits Windows command lines such as uninstall strings into an executable and its arguments.</summary>
public static class CommandLine
{
    public static (string FileName, string Arguments) Split(string commandLine, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var s = Environment.ExpandEnvironmentVariables(commandLine ?? "").Trim();
        if (s.Length == 0) return ("", "");

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return (s.Trim('"'), "");
            return (s.Substring(1, end - 1), s.Substring(end + 1).Trim());
        }

        // Unquoted path that may contain spaces: find the shortest prefix that names an existing file.
        var exeMatch = Regex.Match(s, @"^(.+?\.(exe|com|bat|cmd))(\s|$)", RegexOptions.IgnoreCase);
        int searchFrom = 0;
        while (true)
        {
            var space = s.IndexOf(' ', searchFrom);
            var candidate = space < 0 ? s : s.Substring(0, space);
            if (fileExists(candidate)) return (candidate, space < 0 ? "" : s.Substring(space + 1).Trim());
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && fileExists(candidate + ".exe"))
                return (candidate + ".exe", space < 0 ? "" : s.Substring(space + 1).Trim());
            if (space < 0) break;
            searchFrom = space + 1;
        }
        if (exeMatch.Success) return (exeMatch.Groups[1].Value, s.Substring(exeMatch.Groups[1].Length).Trim());
        var firstSpace = s.IndexOf(' ');
        return firstSpace < 0 ? (s, "") : (s.Substring(0, firstSpace), s.Substring(firstSpace + 1).Trim());
    }

    /// <summary>Extracts the file path from a value like <c>"C:\x\app.exe",0</c> or <c>C:\x\app.ico</c>.</summary>
    public static (string Path, int Index) ParseIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ("", 0);
        var s = Environment.ExpandEnvironmentVariables(value.Trim());
        int index = 0;
        var m = Regex.Match(s, @"^(.*?),\s*(-?\d+)$");
        if (m.Success) { s = m.Groups[1].Value; index = int.Parse(m.Groups[2].Value); }
        return (s.Trim().Trim('"'), index);
    }

    /// <summary>Extracts a Windows Installer product code from a command line, e.g. MsiExec.exe /X{GUID}.</summary>
    public static string? ExtractMsiProductCode(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        if (!commandLine.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(commandLine, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    public static bool IsGuid(string? s) =>
        s is not null && Regex.IsMatch(s.Trim(), @"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$");

    /// <summary>Returns the first absolute path to an executable mentioned inside a command line (quoted or not).</summary>
    public static string? FindExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var (file, _) = Split(commandLine);
        if (Path.IsPathRooted(file)) return file;
        var m = Regex.Match(Environment.ExpandEnvironmentVariables(commandLine), @"[A-Za-z]:\\[^""]+?\.(exe|dll|com|bat|cmd|scr)", RegexOptions.IgnoreCase);
        return m.Success ? m.Value : null;
    }
}
