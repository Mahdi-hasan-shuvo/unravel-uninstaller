using Unravel.Core.Native;

namespace Unravel.Core.Util;

public enum DeleteOutcome { Deleted, Recycled, NotFound, ScheduledForReboot, Failed }

public sealed record DeleteResult(string Path, DeleteOutcome Outcome, string? Error = null)
{
    public bool Succeeded => Outcome is DeleteOutcome.Deleted or DeleteOutcome.Recycled or DeleteOutcome.NotFound or DeleteOutcome.ScheduledForReboot;
}

public static class FileOps
{
    public static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Deletes a file or folder, optionally to the Recycle Bin. Items in use can be scheduled for deletion on reboot.</summary>
    public static DeleteResult Delete(string path, bool toRecycleBin, bool scheduleIfLocked = false)
    {
        if (!PathExists(path)) return new(path, DeleteOutcome.NotFound);
        if (toRecycleBin)
        {
            var op = new NativeMethods.SHFILEOPSTRUCT
            {
                wFunc = NativeMethods.FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI
            };
            int rc = NativeMethods.SHFileOperation(ref op);
            if (rc == 0 && op.fAnyOperationsAborted == 0 && !PathExists(path)) return new(path, DeleteOutcome.Recycled);
            // Fall through to the regular path so the caller learns the real reason (in use / access denied).
            if (!PathExists(path)) return new(path, DeleteOutcome.Recycled);
            return Fallback(path, $"Recycle Bin operation failed (code 0x{rc:x}).", scheduleIfLocked);
        }
        try
        {
            if (Directory.Exists(path))
            {
                ClearAttributes(path);
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            return new(path, DeleteOutcome.Deleted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fallback(path, e.Message, scheduleIfLocked);
        }
    }

    private static DeleteResult Fallback(string path, string error, bool scheduleIfLocked)
    {
        if (scheduleIfLocked && ScheduleDeleteOnReboot(path)) return new(path, DeleteOutcome.ScheduledForReboot, error);
        return new(path, DeleteOutcome.Failed, error);
    }

    private static void ClearAttributes(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try
                {
                    var a = File.GetAttributes(f);
                    if ((a & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                        File.SetAttributes(f, a & ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden));
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Registers a file or (empty-after-children) folder for deletion at next boot. Requires administrator rights.</summary>
    public static bool ScheduleDeleteOnReboot(string path)
    {
        if (Directory.Exists(path))
        {
            bool ok = true;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    if (File.Exists(f)) ok &= NativeMethods.MoveFileEx(f, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
                foreach (var d in Directory.EnumerateDirectories(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                             .OrderByDescending(d => d.Length))
                    ok &= NativeMethods.MoveFileEx(d, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch { ok = false; }
            return NativeMethods.MoveFileEx(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT) && ok;
        }
        return NativeMethods.MoveFileEx(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
    }

    /// <summary>Total size of files below a folder; stops early on cancellation and skips inaccessible items and reparse points.</summary>
    public static long GetDirectorySize(string dir, CancellationToken ct = default)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                if (ct.IsCancellationRequested) break;
                try { total += fi.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    public static long GetSize(string path) =>
        File.Exists(path) ? SafeLength(path) : GetDirectorySize(path);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    public static string NormalizeDir(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\'); }
        catch { return path.Trim().TrimEnd('\\'); }
    }

    /// <summary>True when <paramref name="path"/> is the same as or below <paramref name="dir"/>.</summary>
    public static bool IsSameOrUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
        var p = NormalizeDir(path);
        var d = NormalizeDir(dir);
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }
}
