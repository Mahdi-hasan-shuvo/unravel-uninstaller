using System.Security.Cryptography;

namespace Unravel.Core.Secure;

public sealed record SecureDeleteProgress(string CurrentPath, int ItemsDone, int ItemsTotal, long BytesDone, long BytesTotal);

public sealed record SecureDeleteFailure(string Path, string Error);

public sealed class SecureDeleteReport
{
    public int FilesErased { get; set; }
    public int FoldersRemoved { get; set; }
    public long BytesOverwritten { get; set; }
    public bool Cancelled { get; set; }
    public List<SecureDeleteFailure> Failures { get; } = [];
}

/// <summary>
/// Overwrites file contents several times with random data, then renames and deletes them so neither
/// the content nor the original name stays in the file system.
/// Limits: on SSDs and other flash media, wear levelling can keep old copies of blocks the OS cannot reach.
/// </summary>
public sealed class SecureDelete
{
    public int Passes { get; init; } = 3;
    private const int BufferSize = 1 << 20;

    /// <summary>Test hook invoked after overwriting a file and before it is deleted.</summary>
    internal Action<string>? AfterOverwrite { get; init; }

    public SecureDeleteReport Erase(IEnumerable<string> paths, IProgress<SecureDeleteProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new SecureDeleteReport();
        var roots = paths.Select(p => p.Trim().TrimEnd('\\')).Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var files = new List<string>();
        var folders = new List<string>();
        foreach (var r in roots)
        {
            if (File.Exists(r)) files.Add(r);
            else if (Directory.Exists(r))
            {
                if ((File.GetAttributes(r) & FileAttributes.ReparsePoint) != 0)
                {
                    // A junction/symlink: remove the link itself, never the target's content.
                    try { Directory.Delete(r); report.FoldersRemoved++; } catch (Exception e) { report.Failures.Add(new(r, e.Message)); }
                    continue;
                }
                files.AddRange(Directory.EnumerateFiles(r, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }));
                folders.AddRange(Directory.EnumerateDirectories(r, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }));
                folders.Add(r);
            }
            else report.Failures.Add(new(r, "Not found."));
        }

        long total = files.Sum(f => { try { return new FileInfo(f).Length * Passes; } catch { return 0L; } });
        long done = 0;
        int i = 0;
        var buffer = new byte[BufferSize];
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) { report.Cancelled = true; break; }
            progress?.Report(new(file, i, files.Count, done, total));
            try
            {
                done += EraseFile(file, buffer, ct, b => progress?.Report(new(file, i, files.Count, done + b, total)));
                report.FilesErased++;
                report.BytesOverwritten = done;
            }
            catch (OperationCanceledException) { report.Cancelled = true; break; }
            catch (Exception e) { report.Failures.Add(new(file, e.Message)); }
            i++;
        }
        if (!report.Cancelled)
        {
            foreach (var dir in folders.OrderByDescending(d => d.Length))
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(dir); continue; }
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) continue; // something failed inside; leave it
                    var renamed = RenameRandomly(dir, isDirectory: true);
                    Directory.Delete(renamed);
                    report.FoldersRemoved++;
                }
                catch (Exception e) { report.Failures.Add(new(dir, e.Message)); }
            }
        }
        progress?.Report(new("", i, files.Count, done, total));
        Logging.ActivityLog.Action("Secure delete", $"Erased {report.FilesErased} file(s) with {Passes} pass(es)" + (report.Cancelled ? " (cancelled)" : "") +
            (report.Failures.Count > 0 ? $", {report.Failures.Count} failure(s)" : ""), string.Join("\n", roots));
        return report;
    }

    private long EraseFile(string path, byte[] buffer, CancellationToken ct, Action<long> onBytes)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            File.SetAttributes(path, FileAttributes.Normal);
        long written = 0;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            long length = fs.Length;
            for (int pass = 0; pass < Passes; pass++)
            {
                fs.Position = 0;
                long remaining = length;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, remaining);
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
                    fs.Write(buffer, 0, n);
                    remaining -= n;
                    written += n;
                    onBytes(written);
                }
                fs.Flush(flushToDisk: true);
            }
        }
        AfterOverwrite?.Invoke(path);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.SetLength(0);
            fs.Flush(flushToDisk: true);
        }
        var renamed = RenameRandomly(path, isDirectory: false);
        // Reset timestamps so they do not reveal when the file was used.
        var epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try { File.SetCreationTimeUtc(renamed, epoch); File.SetLastWriteTimeUtc(renamed, epoch); File.SetLastAccessTimeUtc(renamed, epoch); } catch { }
        File.Delete(renamed);
        return written;
    }

    private static string RenameRandomly(string path, bool isDirectory)
    {
        var dir = Path.GetDirectoryName(path)!;
        var current = path;
        for (int round = 0; round < 3; round++)
        {
            var name = Path.Combine(dir, RandomName(Path.GetFileName(path).Length));
            if (File.Exists(name) || Directory.Exists(name)) continue;
            try
            {
                if (isDirectory) Directory.Move(current, name); else File.Move(current, name);
                current = name;
            }
            catch { break; }
        }
        return current;
    }

    private static string RandomName(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        length = Math.Clamp(length, 8, 40);
        return new string(Enumerable.Range(0, length).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }
}
