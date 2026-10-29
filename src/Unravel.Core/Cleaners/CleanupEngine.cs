using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Unravel.Core.Util;

namespace Unravel.Core.Cleaners;

public enum RegistryCleanMode { Values, ValuesAndSubkeys, SingleValue, Key }

public abstract record CleanupTarget;

/// <summary>Registry data: all values of a key, values plus subkeys, one value, or the key itself.</summary>
public sealed record RegistryTarget(RegPath Key, RegistryCleanMode Mode, string? ValueName = null, Func<string, bool>? ValueFilter = null) : CleanupTarget;

/// <summary>Files under a folder matching a wildcard; optionally recursive and optionally removing emptied subfolders.</summary>
public sealed record FilesTarget(string Folder, string Pattern = "*", bool Recursive = false, bool RemoveSubfolders = false, TimeSpan? MinAge = null) : CleanupTarget;

/// <summary>Rows in SQLite tables (browser databases). Tables that do not exist are skipped.</summary>
public sealed record SqliteTarget(string DatabasePath, IReadOnlyList<SqliteDelete> Deletes) : CleanupTarget;

public sealed record SqliteDelete(string Table, string? Where = null);

/// <summary>Anything else (clipboard, Recycle Bin, WinINet): explicit preview and execute callbacks.</summary>
public sealed record ActionTarget(string Label, Func<CleanCount> Preview, Func<CleanCount> Execute) : CleanupTarget;

public sealed class CleanCount
{
    public int Items { get; set; }
    public long Bytes { get; set; }
    public List<string> Errors { get; } = [];

    public static CleanCount operator +(CleanCount a, CleanCount b)
    {
        var r = new CleanCount { Items = a.Items + b.Items, Bytes = a.Bytes + b.Bytes };
        r.Errors.AddRange(a.Errors);
        r.Errors.AddRange(b.Errors);
        return r;
    }

    public override string ToString() => Bytes > 0 ? $"{Items} item(s), {Format.Bytes(Bytes)}" : $"{Items} item(s)";
}

public sealed class CleanerItem : INotifyPropertyChanged
{
    private bool _isChecked;
    private string? _status;

    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? Note { get; init; }
    public required Func<IEnumerable<CleanupTarget>> Targets { get; init; }
    /// <summary>Process names (without .exe) that lock this data while running.</summary>
    public string[] BlockingProcesses { get; init; } = [];
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnChanged(); } }
    public string? Status { get => _status; set { _status = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class CleanupEngine
{
    public static List<string> RunningBlockers(CleanerItem item) =>
        item.BlockingProcesses.Where(p => System.Diagnostics.Process.GetProcessesByName(p).Length > 0).Select(p => p + ".exe").ToList();

    public static CleanCount Preview(CleanerItem item) => Run(item, execute: false);
    public static CleanCount Execute(CleanerItem item)
    {
        var r = Run(item, execute: true);
        Logging.ActivityLog.Action("Cleaners", $"{item.Group} › {item.Title}: {r}", string.Join("\n", r.Errors));
        return r;
    }

    private static CleanCount Run(CleanerItem item, bool execute)
    {
        var total = new CleanCount();
        IEnumerable<CleanupTarget> targets;
        try { targets = item.Targets().ToList(); }
        catch (Exception e) { total.Errors.Add(e.Message); return total; }
        foreach (var t in targets)
        {
            try
            {
                total += t switch
                {
                    RegistryTarget r => Registry(r, execute),
                    FilesTarget f => Files(f, execute),
                    SqliteTarget s => Sqlite(s, execute),
                    ActionTarget a => execute ? a.Execute() : a.Preview(),
                    _ => new CleanCount()
                };
            }
            catch (Exception e) { total.Errors.Add(e.Message); }
        }
        return total;
    }

    private static CleanCount Registry(RegistryTarget t, bool execute)
    {
        var c = new CleanCount();
        using var k = t.Key.Open(writable: execute && t.Mode != RegistryCleanMode.Key);
        if (k is null)
        {
            if (execute && t.Key.Exists()) c.Errors.Add($"Access denied: {t.Key.FullPath}");
            return c;
        }
        switch (t.Mode)
        {
            case RegistryCleanMode.SingleValue:
                if (k.GetValue(t.ValueName) is null) return c;
                c.Items = 1;
                if (execute) k.DeleteValue(t.ValueName!, false);
                break;
            case RegistryCleanMode.Values:
            case RegistryCleanMode.ValuesAndSubkeys:
                var names = k.GetValueNames().Where(n => t.ValueFilter?.Invoke(n) ?? true).ToList();
                c.Items += names.Count;
                if (execute) foreach (var n in names) k.DeleteValue(n, false);
                if (t.Mode == RegistryCleanMode.ValuesAndSubkeys)
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        c.Items += CountTree(t.Key.Child(sub));
                        if (execute) k.DeleteSubKeyTree(sub, false);
                    }
                break;
            case RegistryCleanMode.Key:
                c.Items = CountTree(t.Key);
                k.Dispose();
                if (execute && !RegistryUtil.DeleteKeyTree(t.Key, out var err)) c.Errors.Add(err!);
                break;
        }
        return c;
    }

    private static int CountTree(RegPath key)
    {
        using var k = key.Open();
        if (k is null) return 0;
        int n = Math.Max(1, k.ValueCount);
        foreach (var s in k.GetSubKeyNames()) n += CountTree(key.Child(s));
        return n;
    }

    private static CleanCount Files(FilesTarget t, bool execute)
    {
        var c = new CleanCount();
        if (string.IsNullOrEmpty(t.Folder) || !Directory.Exists(t.Folder)) return c;
        var opts = new EnumerationOptions { RecurseSubdirectories = t.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        List<FileInfo> files;
        try { files = new DirectoryInfo(t.Folder).EnumerateFiles(t.Pattern, opts).ToList(); }
        catch (Exception e) { c.Errors.Add(e.Message); return c; }
        int locked = 0;
        foreach (var f in files)
        {
            long len;
            try { len = f.Length; } catch { continue; }
            if (t.MinAge is { } age && DateTime.Now - f.LastWriteTime < age) continue;
            if (!execute) { c.Items++; c.Bytes += len; continue; }
            try
            {
                f.Attributes = FileAttributes.Normal;
                f.Delete();
                c.Items++;
                c.Bytes += len;
            }
            catch { locked++; }
        }
        if (execute && t.RemoveSubfolders)
        {
            foreach (var d in Directory.EnumerateDirectories(t.Folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderByDescending(d => d.Length).ToList())
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
        }
        if (locked > 0) c.Errors.Add($"{locked} file(s) in {t.Folder} are in use and were skipped.");
        return c;
    }

    private static CleanCount Sqlite(SqliteTarget t, bool execute)
    {
        var c = new CleanCount();
        if (!File.Exists(t.DatabasePath)) return c;
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = t.DatabasePath,
            Mode = execute ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 3,
        }.ToString();
        try
        {
            using var conn = new SqliteConnection(cs);
            conn.Open();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) existing.Add(r.GetString(0));
            }
            using var tx = execute ? conn.BeginTransaction() : null;
            foreach (var d in t.Deletes.Where(d => existing.Contains(d.Table)))
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var where = d.Where is null ? "" : " WHERE " + d.Where;
                cmd.CommandText = execute ? $"DELETE FROM \"{d.Table}\"{where}" : $"SELECT COUNT(*) FROM \"{d.Table}\"{where}";
                var n = execute ? cmd.ExecuteNonQuery() : Convert.ToInt32(cmd.ExecuteScalar());
                c.Items += Math.Max(0, n);
            }
            if (execute)
            {
                tx!.Commit();
                // Reclaim freed pages so deleted rows are not left in the file.
                using var vac = conn.CreateCommand();
                vac.CommandText = "VACUUM";
                try { vac.ExecuteNonQuery(); } catch { }
            }
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) // BUSY / LOCKED
        {
            c.Errors.Add($"{Path.GetFileName(t.DatabasePath)} is in use. Close the browser and try again.");
        }
        catch (SqliteException e)
        {
            c.Errors.Add($"{Path.GetFileName(t.DatabasePath)}: {e.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
        return c;
    }
}

