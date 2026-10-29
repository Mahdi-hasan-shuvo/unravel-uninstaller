namespace Unravel.Core.Tracking;

public enum ChangeArea { Folder, File, RegistryKey, RegistryValue, Service, ScheduledTask, StartupEntry }
public enum ChangeType { Added, Removed, Modified }

public sealed record TrackedChange(ChangeArea Area, ChangeType Type, string Location, string? Detail = null, int ContainedItems = 0)
{
    /// <summary>For registry values: the key path and value name (Location is "key\value").</summary>
    public string? KeyPath { get; init; }
    public string? ValueName { get; init; }
}

public sealed class SnapshotComparison
{
    public List<TrackedChange> Changes { get; init; } = [];
    public IEnumerable<TrackedChange> Of(ChangeArea area) => Changes.Where(c => c.Area == area);
    public int Count(ChangeType type) => Changes.Count(c => c.Type == type);
}

/// <summary>Compares two snapshots. Added folders and keys are reported once, with the number of items inside them.</summary>
public static class SnapshotDiff
{
    public static SnapshotComparison Compare(SystemSnapshot before, SystemSnapshot after)
    {
        var changes = new List<TrackedChange>();
        CompareFiles(before, after, changes);
        CompareRegistry(before, after, changes);

        foreach (var (name, svc) in after.Services)
        {
            if (!before.Services.TryGetValue(name, out var old)) changes.Add(new(ChangeArea.Service, ChangeType.Added, name, $"{svc.DisplayName} — {svc.ImagePath} ({svc.StartType})"));
            else if (old != svc) changes.Add(new(ChangeArea.Service, ChangeType.Modified, name, $"{old.StartType} → {svc.StartType}; {svc.ImagePath}"));
        }
        foreach (var name in before.Services.Keys.Where(k => !after.Services.ContainsKey(k)))
            changes.Add(new(ChangeArea.Service, ChangeType.Removed, name, before.Services[name].DisplayName));

        foreach (var (path, task) in after.Tasks)
        {
            if (!before.Tasks.TryGetValue(path, out var old)) changes.Add(new(ChangeArea.ScheduledTask, ChangeType.Added, path, task.Actions));
            else if (old.XmlHash != task.XmlHash) changes.Add(new(ChangeArea.ScheduledTask, ChangeType.Modified, path, task.Actions));
        }
        foreach (var path in before.Tasks.Keys.Where(k => !after.Tasks.ContainsKey(k)))
            changes.Add(new(ChangeArea.ScheduledTask, ChangeType.Removed, path, before.Tasks[path].Actions));

        foreach (var (id, s) in after.Startup)
        {
            if (!before.Startup.TryGetValue(id, out var old)) changes.Add(new(ChangeArea.StartupEntry, ChangeType.Added, id, s.Command));
            else if (old != s) changes.Add(new(ChangeArea.StartupEntry, ChangeType.Modified, id, s.Command + (s.Enabled ? "" : " (disabled)")));
        }
        foreach (var id in before.Startup.Keys.Where(k => !after.Startup.ContainsKey(k)))
            changes.Add(new(ChangeArea.StartupEntry, ChangeType.Removed, id, before.Startup[id].Command));

        return new SnapshotComparison { Changes = changes };
    }

    private static void CompareFiles(SystemSnapshot before, SystemSnapshot after, List<TrackedChange> changes)
    {
        var addedDirs = after.Files.Where(f => f.Value.IsDirectory && !before.Files.ContainsKey(f.Key)).Select(f => f.Key).ToList();
        var topAdded = TopMost(addedDirs);
        var counts = topAdded.ToDictionary(d => d, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, rec) in after.Files)
        {
            if (before.Files.TryGetValue(path, out var old))
            {
                if (!rec.IsDirectory && (old.Size != rec.Size || old.WriteTicks != rec.WriteTicks))
                    changes.Add(new(ChangeArea.File, ChangeType.Modified, path, $"{old.Size:N0} → {rec.Size:N0} bytes"));
                continue;
            }
            var container = FindContainer(path, counts);
            if (container is not null) { if (!path.Equals(container, StringComparison.OrdinalIgnoreCase)) counts[container]++; continue; }
            if (!rec.IsDirectory) changes.Add(new(ChangeArea.File, ChangeType.Added, path, $"{rec.Size:N0} bytes"));
        }
        foreach (var (dir, n) in counts) changes.Add(new(ChangeArea.Folder, ChangeType.Added, dir, $"{n} item(s) inside", n));

        var removedDirs = TopMost(before.Files.Where(f => f.Value.IsDirectory && !after.Files.ContainsKey(f.Key)).Select(f => f.Key).ToList());
        var removedCounts = removedDirs.ToDictionary(d => d, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, rec) in before.Files.Where(f => !after.Files.ContainsKey(f.Key)))
        {
            var container = FindContainer(path, removedCounts);
            if (container is not null) { if (!path.Equals(container, StringComparison.OrdinalIgnoreCase)) removedCounts[container]++; continue; }
            if (!rec.IsDirectory) changes.Add(new(ChangeArea.File, ChangeType.Removed, path));
        }
        foreach (var (dir, n) in removedCounts) changes.Add(new(ChangeArea.Folder, ChangeType.Removed, dir, $"{n} item(s) inside", n));
    }

    private static void CompareRegistry(SystemSnapshot before, SystemSnapshot after, List<TrackedChange> changes)
    {
        var addedKeys = TopMost(after.Registry.Keys.Where(k => !before.Registry.ContainsKey(k)).ToList());
        var counts = addedKeys.ToDictionary(k => k, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in after.Registry)
        {
            if (!before.Registry.TryGetValue(key, out var oldValues))
            {
                var container = FindContainer(key, counts)!;
                counts[container] += values.Count + (key.Equals(container, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                continue;
            }
            foreach (var (name, hash) in values)
            {
                if (!oldValues.TryGetValue(name, out var oldHash))
                    changes.Add(new(ChangeArea.RegistryValue, ChangeType.Added, key + "\\" + Display(name)) { KeyPath = key, ValueName = name });
                else if (oldHash != hash)
                    changes.Add(new(ChangeArea.RegistryValue, ChangeType.Modified, key + "\\" + Display(name)) { KeyPath = key, ValueName = name });
            }
            foreach (var name in oldValues.Keys.Where(n => !values.ContainsKey(n)))
                changes.Add(new(ChangeArea.RegistryValue, ChangeType.Removed, key + "\\" + Display(name)) { KeyPath = key, ValueName = name });
        }
        foreach (var (key, n) in counts) changes.Add(new(ChangeArea.RegistryKey, ChangeType.Added, key, $"{n} value(s)/subkey(s) inside", n));

        var removedKeys = TopMost(before.Registry.Keys.Where(k => !after.Registry.ContainsKey(k)).ToList());
        foreach (var key in removedKeys) changes.Add(new(ChangeArea.RegistryKey, ChangeType.Removed, key));
    }

    private static string Display(string valueName) => valueName.Length == 0 ? "(Default)" : valueName;

    /// <summary>Keeps only paths that are not below another path in the list.</summary>
    internal static List<string> TopMost(List<string> paths)
    {
        var sorted = paths.OrderBy(p => p.Length).ToList();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var p in sorted)
        {
            if (HasAncestorIn(p, set)) continue;
            set.Add(p);
            result.Add(p);
        }
        return result;
    }

    private static bool HasAncestorIn(string path, HashSet<string> set)
    {
        for (int i = path.LastIndexOf('\\'); i > 0; i = path.LastIndexOf('\\', i - 1))
            if (set.Contains(path[..i])) return true;
        return false;
    }

    private static string? FindContainer(string path, Dictionary<string, int> containers)
    {
        if (containers.ContainsKey(path)) return path;
        for (int i = path.LastIndexOf('\\'); i > 0; i = path.LastIndexOf('\\', i - 1))
            if (containers.ContainsKey(path[..i])) return path[..i];
        return null;
    }
}
