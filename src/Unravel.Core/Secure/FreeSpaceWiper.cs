using System.Security.Cryptography;
using Unravel.Core.Native;

namespace Unravel.Core.Secure;

public sealed record WipeProgress(string Drive, int Pass, int Passes, long BytesWritten, long BytesTarget, string Phase);

public sealed class WipeReport
{
    public string Drive { get; init; } = "";
    public long BytesPerPass { get; set; }
    public int PassesCompleted { get; set; }
    public int SmallFilesWritten { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public bool TempCleanedUp { get; set; }
}

/// <summary>
/// Overwrites a drive's free space so previously deleted files cannot be recovered: it fills free space with
/// random-data files (several passes), optionally fills unused file-table records with tiny files, then deletes it all.
/// The drive temporarily runs out of free space while this runs.
/// </summary>
public sealed class FreeSpaceWiper
{
    public int Passes { get; init; } = 3;
    /// <summary>Upper bound on bytes written per pass (tests use a small cap). Null = all free space.</summary>
    public long? MaxBytesPerPass { get; init; }
    /// <summary>Space deliberately left free so Windows and other programs keep working.</summary>
    public long ReserveBytes { get; init; } = 64L * 1024 * 1024;
    public bool WipeFileTableSlack { get; init; } = true;
    public int MaxSmallFiles { get; init; } = 20000;
    /// <summary>Folder in which the temporary fill files are created (defaults to the drive root).</summary>
    public string? WorkFolderOverride { get; init; }

    private const long ChunkFileSize = 512L * 1024 * 1024;
    private const int BufferSize = 4 * 1024 * 1024;

    public static ulong FreeBytes(string path) =>
        NativeMethods.GetDiskFreeSpaceEx(path, out var free, out _, out _) ? free : 0;

    public WipeReport Wipe(string drive, IProgress<WipeProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new WipeReport { Drive = drive };
        var baseDir = WorkFolderOverride ?? drive;
        var work = Path.Combine(baseDir, "~UnravelWipe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            File.SetAttributes(work, FileAttributes.Directory | FileAttributes.Hidden);
            long free = (long)FreeBytes(work);
            long target = Math.Max(0, free - ReserveBytes);
            if (MaxBytesPerPass is { } cap) target = Math.Min(target, cap);
            report.BytesPerPass = target;

            var buffer = new byte[BufferSize];
            var files = new List<string>();
            // Pass 1 allocates the fill files; later passes overwrite the same clusters again with fresh random data.
            for (int pass = 1; pass <= Passes; pass++)
            {
                long written = 0;
                int fileIndex = 0;
                while (written < target)
                {
                    ct.ThrowIfCancellationRequested();
                    string file;
                    if (pass == 1) { file = Path.Combine(work, $"fill{fileIndex:0000}.bin"); files.Add(file); }
                    else if (fileIndex < files.Count) file = files[fileIndex];
                    else break;
                    long size = Math.Min(ChunkFileSize, target - written);
                    if (pass > 1) size = Math.Min(size, new FileInfo(file).Length);
                    long w = WriteRandom(file, size, buffer, pass == 1, ct, b =>
                        progress?.Report(new WipeProgress(drive, pass, Passes, written + b, target, "Overwriting free space")));
                    written += w;
                    fileIndex++;
                    if (w < size) break; // disk full
                }
                report.PassesCompleted = pass;
            }

            if (WipeFileTableSlack)
            {
                progress?.Report(new WipeProgress(drive, Passes, Passes, target, target, "Overwriting unused file records"));
                var small = new byte[700];
                for (int n = 0; n < MaxSmallFiles; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomNumberGenerator.Fill(small);
                    try { File.WriteAllBytes(Path.Combine(work, $"r{n:00000}"), small); report.SmallFilesWritten++; }
                    catch (IOException) { break; }
                }
            }
        }
        catch (OperationCanceledException) { report.Cancelled = true; }
        catch (Exception e) { report.Error = e.Message; }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                report.TempCleanedUp = !Directory.Exists(work);
            }
            catch (Exception e) { report.Error ??= "Could not remove temporary files: " + e.Message; }
        }
        return report;
    }

    private static long WriteRandom(string path, long size, byte[] buffer, bool create, CancellationToken ct, Action<long> onProgress)
    {
        long written = 0;
        try
        {
            using var fs = new FileStream(path, create ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            while (written < size)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, size - written);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
                fs.Write(buffer, 0, n);
                written += n;
                onProgress(written);
            }
            fs.Flush(flushToDisk: true);
        }
        catch (IOException) when (written > 0 || create)
        {
            // ERROR_DISK_FULL: everything that fits has been written.
        }
        return written;
    }
}
