using System.Diagnostics;
using System.Text;
using Unravel.Core.Util;

namespace Unravel.Core.SystemOps;

public sealed record BackupResult(bool Ok, string? Folder, string? Error);

public sealed record BackupSet(string Folder, string Title, DateTime Created, int FileCount);

/// <summary>Exports registry keys to .reg files (via reg.exe) before they are deleted, and restores them on request.</summary>
public sealed class RegistryBackup
{
    public string Root { get; }

    public RegistryBackup(string? root = null)
    {
        Root = root ?? Path.Combine(Settings.SettingsStore.DefaultFolder, "Backups");
    }

    public BackupResult Export(IEnumerable<RegPath> keys, string title)
    {
        var distinct = keys.GroupBy(k => k.FullPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        // Skip keys whose ancestor is also exported.
        distinct = distinct.Where(k => !distinct.Any(o => !ReferenceEquals(o, k) && k.IsUnder(o))).ToList();
        var folder = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(title)}");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "title.txt"), title, Encoding.UTF8);
            int i = 0;
            var errors = new List<string>();
            foreach (var key in distinct)
            {
                if (!key.Exists()) continue;
                var file = Path.Combine(folder, $"{++i:000}.reg");
                var (code, output) = RunReg($"export \"{key.FullPath}\" \"{file}\" /y");
                if (code != 0) errors.Add($"{key.FullPath}: {output.Trim()}");
            }
            if (errors.Count > 0) return new(false, folder, string.Join("\n", errors));
            return new(true, folder, null);
        }
        catch (Exception e) { return new(false, folder, e.Message); }
    }

    /// <summary>Backs up individual values by exporting their parent key.</summary>
    public BackupResult ExportValues(IEnumerable<(RegPath Key, string Value)> values, string title) =>
        Export(values.Select(v => v.Key), title);

    public List<BackupSet> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.GetDirectories(Root)
            .Select(d =>
            {
                var titleFile = Path.Combine(d, "title.txt");
                var title = File.Exists(titleFile) ? File.ReadAllText(titleFile) : Path.GetFileName(d);
                return new BackupSet(d, title, Directory.GetCreationTime(d), Directory.GetFiles(d, "*.reg").Length);
            })
            .OrderByDescending(b => b.Created)
            .ToList();
    }

    public BackupResult Restore(BackupSet set, bool elevate = false)
    {
        var errors = new List<string>();
        foreach (var file in Directory.GetFiles(set.Folder, "*.reg").OrderBy(f => f))
        {
            if (elevate)
            {
                var p = Process.Start(new ProcessStartInfo("reg.exe", $"import \"{file}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                p?.WaitForExit();
                if (p is null || p.ExitCode != 0) errors.Add($"{Path.GetFileName(file)}: import failed");
            }
            else
            {
                var (code, output) = RunReg($"import \"{file}\"");
                if (code != 0) errors.Add($"{Path.GetFileName(file)}: {output.Trim()}");
            }
        }
        return new(errors.Count == 0, set.Folder, errors.Count == 0 ? null : string.Join("\n", errors));
    }

    public void Delete(BackupSet set) => Directory.Delete(set.Folder, recursive: true);

    private static (int Code, string Output) RunReg(string args)
    {
        var psi = new ProcessStartInfo("reg.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60000);
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 60 ? clean[..60] : clean;
    }
}
