using Unravel.Core.Settings;
using Unravel.Core.Util;

namespace Unravel.Core.Junk;

public sealed class JunkFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime LastWrite { get; init; }
    public required string Pattern { get; init; }
    public bool IsChecked { get; set; } = true;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed record JunkScanProgress(string CurrentFolder, int FilesFound, long BytesFound);

public sealed class JunkScanOptions
{
    public required IReadOnlyList<string> Roots { get; init; }
    public required IReadOnlyList<string> Patterns { get; init; }
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
    public bool IgnoreRecentlyAccessed { get; init; } = true;
    public TimeSpan RecentWindow { get; init; } = TimeSpan.FromHours(24);
    public DateTime? Now { get; init; }

    public static JunkScanOptions FromSettings(JunkSettings s, IEnumerable<string> drives) => new()
    {
        Roots = drives.ToList(),
        Patterns = s.Patterns.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Pattern)).Select(p => p.Pattern.Trim()).ToList(),
        ExcludePatterns = s.Excludes.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Pattern)).Select(p => p.Pattern.Trim()).ToList(),
        IgnoreRecentlyAccessed = s.IgnoreRecentlyAccessed,
    };
}

/// <summary>Walks selected drives looking for files whose names match junk patterns, honouring folder exclusions.</summary>
public static class JunkScanner
{
    public static List<string> AvailableDrives() =>
        DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => d.Name).ToList();

    public static List<JunkFile> Scan(JunkScanOptions o, IProgress<JunkScanProgress>? progress, CancellationToken ct)
    {
        if (o.Roots.Count == 0 || o.Patterns.Count == 0)
            throw new ArgumentException("Select at least one drive and one file pattern to scan.");
        var found = new List<JunkFile>();
        long bytes = 0;
        var now = o.Now ?? DateTime.Now;
        var lastReport = DateTime.MinValue;
        var stack = new Stack<string>(o.Roots.Select(r => r.TrimEnd('\\') + "\\").Reverse());
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break; // return partial results, as the original does
            var dir = stack.Pop();
            if (IsExcluded(dir, o.ExcludePatterns)) continue;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 120)
            {
                progress?.Report(new JunkScanProgress(dir, found.Count, bytes));
                lastReport = DateTime.UtcNow;
            }
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
            catch { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow junctions/symlinks
                if (e is DirectoryInfo d) { stack.Push(d.FullName); continue; }
                var pattern = o.Patterns.FirstOrDefault(p => Wildcard.IsMatch(e.Name, p));
                if (pattern is null) continue;
                var f = (FileInfo)e;
                if (o.IgnoreRecentlyAccessed)
                {
                    var latest = new[] { f.LastAccessTime, f.LastWriteTime, f.CreationTime }.Max();
                    if (now - latest < o.RecentWindow) continue;
                }
                if (IsExcluded(f.FullName, o.ExcludePatterns)) continue;
                found.Add(new JunkFile { Path = f.FullName, Size = f.Length, LastWrite = f.LastWriteTime, Pattern = pattern });
                bytes += f.Length;
            }
        }
        progress?.Report(new JunkScanProgress("", found.Count, bytes));
        return found;
    }

    public static bool IsExcluded(string path, IReadOnlyList<string> excludePatterns)
    {
        var p = path.TrimEnd('\\');
        foreach (var x in excludePatterns)
        {
            var pat = Environment.ExpandEnvironmentVariables(x).TrimEnd('\\');
            if (Wildcard.IsMatch(p, pat) || Wildcard.IsMatch(p + "\\", pat)) return true;
            // "C:\Foo\*" should also exclude the folder "C:\Foo" itself.
            if (pat.EndsWith("\\*") && Wildcard.IsMatch(p, pat[..^2])) return true;
        }
        return false;
    }

    public static List<DeleteResult> Delete(IEnumerable<JunkFile> files, bool toRecycleBin, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<DeleteResult>();
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(f.Path);
            results.Add(FileOps.Delete(f.Path, toRecycleBin));
        }
        Logging.ActivityLog.Action("Junk files", $"Deleted {results.Count(r => r.Succeeded)} junk file(s)" + (toRecycleBin ? " to the Recycle Bin" : "") +
            (results.Any(r => !r.Succeeded) ? $", {results.Count(r => !r.Succeeded)} could not be deleted" : ""),
            string.Join("\n", results.Where(r => !r.Succeeded).Select(r => r.Path + ": " + r.Error)));
        return results;
    }
}
