using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Unravel.Core.Logging;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Core.Files;

public sealed record FileIdentity(uint VolumeSerial, ulong FileIndex);

public sealed class DuplicateFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime LastWrite { get; init; }
    public FileIdentity? Identity { get; init; }
    public bool IsChecked { get; set; }
}

public sealed class DuplicateGroup
{
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public required List<DuplicateFile> Files { get; init; }

    /// <summary>Distinct physical files (hard links to the same data count once).</summary>
    public int PhysicalCopies => Files.Select(f => (object?)f.Identity ?? f.Path).Distinct().Count();

    /// <summary>Space freed by keeping exactly one physical copy.</summary>
    public long WastedBytes => Size * Math.Max(0, PhysicalCopies - 1);
}

public sealed class DuplicateScanOptions
{
    public required IReadOnlyList<string> Roots { get; init; }
    public long MinSize { get; init; } = 1;
    public long? MaxSize { get; init; }
    public IReadOnlyList<string> IncludePatterns { get; init; } = ["*"];
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
    public bool SkipHiddenAndSystem { get; init; } = true;
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

public sealed record DuplicateProgress(string Phase, int FilesSeen, int Candidates, long BytesHashed, long BytesToHash, string Current);

public sealed class DuplicateScanResult
{
    public List<DuplicateGroup> Groups { get; init; } = [];
    public int FilesScanned { get; set; }
    public int Unreadable { get; set; }
    public bool Cancelled { get; set; }
    public long WastedBytes => Groups.Sum(g => g.WastedBytes);
}

public enum KeepRule { Newest, Oldest, ShortestPath, LongestPath, FirstInFolder }

/// <summary>
/// Finds identical files by content: size → partial SHA-256 (first and last 64 KB) → full SHA-256.
/// File names are never used to decide that files are duplicates.
/// </summary>
public static class DuplicateFinder
{
    private const int PartialBlock = 64 * 1024;

    public static async Task<DuplicateScanResult> FindAsync(DuplicateScanOptions o, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        if (o.Roots.Count == 0) throw new ArgumentException("Choose at least one folder to scan.");
        var result = new DuplicateScanResult();
        var files = new List<(string Path, long Size, DateTime Write)>();

        // 1) enumerate
        var stack = new Stack<string>(o.Roots.Where(Directory.Exists).Select(r => r.TrimEnd('\\') + "\\"));
        var lastReport = DateTime.MinValue;
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var dir = stack.Pop();
            if (SafetyEngine.IsCriticalPath(dir) && !o.Roots.Any(r => FileOps.IsSameOrUnder(r, dir))) continue;
            if (Junk.JunkScanner.IsExcluded(dir, o.ExcludePatterns)) continue;
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
            catch { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (o.SkipHiddenAndSystem && (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (e is DirectoryInfo d) { stack.Push(d.FullName); continue; }
                var f = (FileInfo)e;
                if (f.Length < o.MinSize || (o.MaxSize is { } max && f.Length > max)) continue;
                if (!o.IncludePatterns.Any(p => Wildcard.IsMatch(f.Name, p))) continue;
                files.Add((f.FullName, f.Length, f.LastWriteTime));
            }
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
            {
                progress?.Report(new("Listing files", files.Count, 0, 0, 0, dir));
                lastReport = DateTime.UtcNow;
            }
        }
        result.FilesScanned = files.Count;
        if (ct.IsCancellationRequested) { result.Cancelled = true; return result; }

        // 2) same size only
        var sizeGroups = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList();
        var candidates = sizeGroups.SelectMany(g => g).ToList();
        long toHash = candidates.Sum(c => Math.Min(c.Size, 2L * PartialBlock));
        long hashed = 0;
        int unreadable = 0;
        progress?.Report(new("Comparing file starts and ends", files.Count, candidates.Count, 0, toHash, ""));

        // 3) partial hash
        var partial = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = o.MaxParallelism, CancellationToken = ct }, (c, token) =>
            {
                var h = TryHash(c.Path, partialOnly: true, token);
                if (h is null) Interlocked.Increment(ref unreadable); else partial[c.Path] = h;
                var done = Interlocked.Add(ref hashed, Math.Min(c.Size, 2L * PartialBlock));
                progress?.Report(new("Comparing file starts and ends", files.Count, candidates.Count, done, toHash, c.Path));
                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException) { result.Cancelled = true; return result; }

        // 4) full hash for files whose size and partial hash both match (small files: the partial hash is the full hash)
        var fullCandidates = candidates
            .Where(c => partial.ContainsKey(c.Path))
            .GroupBy(c => (c.Size, partial[c.Path]))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g).ToList();
        toHash = fullCandidates.Where(c => c.Size > 2L * PartialBlock).Sum(c => c.Size);
        hashed = 0;
        var full = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Parallel.ForEachAsync(fullCandidates, new ParallelOptions { MaxDegreeOfParallelism = o.MaxParallelism, CancellationToken = ct }, (c, token) =>
            {
                if (c.Size <= 2L * PartialBlock) { full[c.Path] = partial[c.Path]; return ValueTask.CompletedTask; }
                var h = TryHash(c.Path, partialOnly: false, token);
                if (h is null) Interlocked.Increment(ref unreadable); else full[c.Path] = h;
                var done = Interlocked.Add(ref hashed, c.Size);
                progress?.Report(new("Verifying with full SHA-256", files.Count, fullCandidates.Count, done, toHash, c.Path));
                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException) { result.Cancelled = true; return result; }

        result.Unreadable = unreadable;
        foreach (var g in fullCandidates.Where(c => full.ContainsKey(c.Path)).GroupBy(c => (c.Size, full[c.Path])).Where(g => g.Count() > 1))
        {
            var group = new DuplicateGroup
            {
                Sha256 = g.Key.Item2,
                Size = g.Key.Size,
                Files = g.Select(f => new DuplicateFile { Path = f.Path, Size = f.Size, LastWrite = f.Write, Identity = GetIdentity(f.Path) })
                         .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            if (group.PhysicalCopies > 1) result.Groups.Add(group); // only hard links to one file: nothing to gain
        }
        result.Groups.Sort((a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
        ActivityLog.Info("Duplicates", $"Scanned {files.Count} files in {o.Roots.Count} folder(s): {result.Groups.Count} duplicate group(s), {Format.Bytes(result.WastedBytes)} reclaimable",
            string.Join("; ", o.Roots));
        return result;
    }

    /// <summary>SHA-256 of the whole file, or of its first and last 64 KB (plus the length) for the quick pass.</summary>
    internal static string? TryHash(string path, bool partialOnly, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            if (partialOnly && fs.Length > 2L * PartialBlock)
            {
                ReadInto(fs, sha, buffer, PartialBlock, ct);
                fs.Seek(-PartialBlock, SeekOrigin.End);
                ReadInto(fs, sha, buffer, PartialBlock, ct);
                sha.AppendData(BitConverter.GetBytes(fs.Length));
            }
            else ReadInto(fs, sha, buffer, long.MaxValue, ct);
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static void ReadInto(FileStream fs, IncrementalHash sha, byte[] buffer, long max, CancellationToken ct)
    {
        long remaining = max;
        int n;
        while (remaining > 0 && (n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
            remaining -= n;
        }
    }

    // ---- hard-link detection ----
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

    public static FileIdentity? GetIdentity(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(h, out var i)) return null;
            return new FileIdentity(i.VolumeSerialNumber, ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow);
        }
        catch { return null; }
    }

    // ---- selection ----

    /// <summary>Checks every copy except the one the rule keeps. Hard links of the kept file are kept too.</summary>
    public static void ApplyKeepRule(IEnumerable<DuplicateGroup> groups, KeepRule rule, string? preferredFolder = null)
    {
        foreach (var g in groups)
        {
            DuplicateFile keep = rule switch
            {
                KeepRule.Newest => g.Files.MaxBy(f => f.LastWrite)!,
                KeepRule.Oldest => g.Files.MinBy(f => f.LastWrite)!,
                KeepRule.ShortestPath => g.Files.MinBy(f => f.Path.Length)!,
                KeepRule.LongestPath => g.Files.MaxBy(f => f.Path.Length)!,
                KeepRule.FirstInFolder => g.Files.FirstOrDefault(f => preferredFolder is not null && FileOps.IsSameOrUnder(f.Path, preferredFolder)) ?? g.Files[0],
                _ => g.Files[0],
            };
            foreach (var f in g.Files)
                f.IsChecked = !ReferenceEquals(f, keep) && !(f.Identity is not null && f.Identity == keep.Identity);
        }
    }

    /// <summary>Returns the groups in which every physical copy is checked – deleting them would lose the data.</summary>
    public static List<DuplicateGroup> GroupsLosingAllCopies(IEnumerable<DuplicateGroup> groups) =>
        groups.Where(g =>
        {
            var kept = g.Files.Where(f => !f.IsChecked).ToList();
            return kept.Count == 0;
        }).ToList();
}
