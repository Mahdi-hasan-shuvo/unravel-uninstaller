using System.Text.Json;
using Unravel.Core.Logging;
using Unravel.Core.Util;

namespace Unravel.Core.Safety;

public sealed record QuarantineEntry(string OriginalPath, string StoredPath, bool IsDirectory, long Size);

public sealed class QuarantineSet
{
    public string Folder { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime Created { get; init; }
    public List<QuarantineEntry> Entries { get; init; } = [];
    public long TotalSize => Entries.Sum(e => e.Size);
}

public sealed record QuarantineResult(string Path, bool Ok, string? Error);

/// <summary>
/// Reversible deletion: files and folders are moved into a quarantine folder with a manifest, so they can be
/// restored to their original location or purged later. Used wherever a deletion should be undoable.
/// </summary>
public sealed class Quarantine
{
    public string Root { get; }

    public Quarantine(string? root = null)
    {
        Root = root ?? Path.Combine(Settings.SettingsStore.DefaultFolder, "Quarantine");
    }

    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Moves the given paths into a new quarantine set. Items that cannot be moved are reported and left in place.</summary>
    public (QuarantineSet Set, List<QuarantineResult> Results) Move(IEnumerable<string> paths, string title, CancellationToken ct = default)
    {
        var folder = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Sanitize(title)}");
        var store = Path.Combine(folder, "items");
        Directory.CreateDirectory(store);
        var set = new QuarantineSet { Folder = folder, Title = title, Created = DateTime.Now };
        var results = new List<QuarantineResult>();
        int i = 0;
        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested) break;
            var path = FileOps.NormalizeDir(raw);
            try
            {
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path)) { results.Add(new(path, false, "Not found.")); continue; }
                long size = FileOps.GetSize(path);
                var stored = Path.Combine(store, $"{++i:00000}_{Path.GetFileName(path)}");
                MoveAny(path, stored, isDir);
                set.Entries.Add(new QuarantineEntry(path, stored, isDir, size));
                results.Add(new(path, true, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                results.Add(new(path, false, e.Message));
            }
            SaveManifest(set); // keep the manifest current so a crash never loses track of moved items
        }
        SaveManifest(set);
        ActivityLog.Action("Quarantine", $"Moved {set.Entries.Count} item(s) to quarantine: {title}", folder);
        return (set, results);
    }

    public List<QuarantineResult> Restore(QuarantineSet set)
    {
        var results = new List<QuarantineResult>();
        foreach (var e in set.Entries.ToList())
        {
            try
            {
                if (!FileOps.PathExists(e.StoredPath)) { results.Add(new(e.OriginalPath, false, "The quarantined copy is missing.")); continue; }
                var target = e.OriginalPath;
                if (FileOps.PathExists(target)) target = AlternativeName(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                MoveAny(e.StoredPath, target, e.IsDirectory);
                set.Entries.Remove(e);
                results.Add(new(target, true, target == e.OriginalPath ? null : "Restored under a new name because the original path exists again."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new(e.OriginalPath, false, ex.Message));
            }
        }
        SaveManifest(set);
        if (set.Entries.Count == 0) TryDeleteFolder(set.Folder);
        ActivityLog.Action("Quarantine", $"Restored {results.Count(r => r.Ok)} item(s) from \"{set.Title}\"", string.Join("\n", results.Where(r => !r.Ok).Select(r => r.Path + ": " + r.Error)));
        return results;
    }

    /// <summary>Permanently deletes a quarantine set.</summary>
    public void Purge(QuarantineSet set)
    {
        TryDeleteFolder(set.Folder);
        if (Directory.Exists(set.Folder)) throw new IOException("Some quarantined files are in use and could not be deleted.");
        ActivityLog.Action("Quarantine", $"Purged quarantine \"{set.Title}\" ({Format.Bytes(set.TotalSize)})");
    }

    public List<QuarantineSet> List()
    {
        if (!Directory.Exists(Root)) return [];
        var sets = new List<QuarantineSet>();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<QuarantineSet>(File.ReadAllText(manifest));
                if (s is not null) sets.Add(new QuarantineSet { Folder = dir, Title = s.Title, Created = s.Created, Entries = s.Entries });
            }
            catch { }
        }
        return sets.OrderByDescending(s => s.Created).ToList();
    }

    private static void SaveManifest(QuarantineSet set)
    {
        Directory.CreateDirectory(set.Folder);
        File.WriteAllText(Path.Combine(set.Folder, ManifestName), JsonSerializer.Serialize(set, Json));
    }

    /// <summary>Moves within a volume, or copies then deletes across volumes.</summary>
    private static void MoveAny(string from, string to, bool isDirectory)
    {
        bool sameVolume = string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
        if (isDirectory)
        {
            if (sameVolume) { Directory.Move(from, to); return; }
            CopyDirectory(from, to);
            FileOps.Delete(from, toRecycleBin: false);
            if (Directory.Exists(from)) throw new IOException("The folder was copied but some files could not be removed from the original location.");
        }
        else
        {
            if (sameVolume) { File.Move(from, to); return; }
            File.Copy(from, to);
            File.SetAttributes(from, FileAttributes.Normal);
            File.Delete(from);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from))
        {
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    private static string AlternativeName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} (restored{(n > 1 ? " " + n : "")}){ext}");
            if (!FileOps.PathExists(candidate)) return candidate;
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) FileOps.Delete(folder, toRecycleBin: false); } catch { }
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 50 ? clean[..50] : clean;
    }
}
