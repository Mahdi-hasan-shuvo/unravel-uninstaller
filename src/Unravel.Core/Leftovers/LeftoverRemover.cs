using System.Net;
using System.Text;
using Unravel.Core.SystemOps;
using Unravel.Core.Util;

namespace Unravel.Core.Leftovers;

public sealed record RemovalItemResult(string Location, bool Ok, string? Error, DeleteOutcome? FileOutcome = null);

public sealed class RemovalReport
{
    public List<RemovalItemResult> Items { get; } = [];
    public string? BackupFolder { get; set; }
    public string? BackupError { get; set; }
    public int Succeeded => Items.Count(i => i.Ok);
    public int Failed => Items.Count(i => !i.Ok);
    public int ScheduledForReboot => Items.Count(i => i.FileOutcome == DeleteOutcome.ScheduledForReboot);
}

/// <summary>Deletes checked leftovers. Only nodes that are checked, marked as created and not excluded are removed.</summary>
public static class LeftoverRemover
{
    /// <summary>The minimal set of registry nodes to delete: a checked key covers everything beneath it.</summary>
    public static List<LeftoverNode> SelectRegistry(LeftoverNode root)
    {
        var chosen = root.Descendants().Where(n => n.WillDelete && n.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue).ToList();
        return chosen.Where(n => !chosen.Any(o => o.Kind == LeftoverKind.RegistryKey && !ReferenceEquals(o, n) && n.IsDescendantOf(o))).ToList();
    }

    public static RemovalReport DeleteRegistry(LeftoverNode root, RegistryBackup? backup, string title)
    {
        var report = new RemovalReport();
        var items = SelectRegistry(root);
        if (items.Count == 0) return report;
        if (backup is not null)
        {
            var b = backup.Export(items.Select(i => i.Key!), title);
            report.BackupFolder = b.Folder;
            if (!b.Ok)
            {
                report.BackupError = b.Error;
                // Never delete what we could not back up.
                foreach (var i in items) report.Items.Add(new(i.Location, false, "Skipped: registry backup failed. " + b.Error));
                return report;
            }
        }
        // Values first, then keys deepest-first, so parents never disappear under their children.
        foreach (var v in items.Where(i => i.Kind == LeftoverKind.RegistryValue))
        {
            var ok = RegistryUtil.DeleteValue(v.Key!, v.ValueName ?? "", out var err);
            report.Items.Add(new(v.Location, ok, err));
        }
        foreach (var k in items.Where(i => i.Kind == LeftoverKind.RegistryKey).OrderByDescending(i => i.Key!.FullPath.Length))
        {
            var ok = RegistryUtil.DeleteKeyTree(k.Key!, out var err);
            report.Items.Add(new(k.Location, ok, err));
        }
        Log("registry item(s)", title, report);
        return report;
    }

    private static void Log(string what, string title, RemovalReport report)
    {
        var detail = string.Join("\n", report.Items.Select(i => (i.Ok ? "removed: " : "FAILED: ") + i.Location + (i.Error is null ? "" : " – " + i.Error)));
        if (report.BackupFolder is not null) detail = "Backup: " + report.BackupFolder + "\n" + detail;
        Logging.ActivityLog.Write(report.Failed > 0 ? Logging.LogLevel.Warning : Logging.LogLevel.Action, "Uninstaller",
            $"{title}: removed {report.Succeeded} {what}" + (report.Failed > 0 ? $", {report.Failed} failed" : ""), detail);
    }

    public static List<LeftoverNode> SelectFiles(IEnumerable<LeftoverNode> files)
    {
        var chosen = files.Where(f => f.WillDelete).ToList();
        return chosen.Where(n => !chosen.Any(o => o.Kind == LeftoverKind.Folder && !ReferenceEquals(o, n) && FileOps.IsSameOrUnder(n.FsPath!, o.FsPath!) && !n.FsPath!.Equals(o.FsPath, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static RemovalReport DeleteFiles(IEnumerable<LeftoverNode> files, bool toRecycleBin, bool scheduleLocked = true, string title = "Leftovers")
    {
        var report = new RemovalReport();
        foreach (var f in SelectFiles(files).OrderByDescending(f => f.FsPath!.Length))
        {
            var r = FileOps.Delete(f.FsPath!, toRecycleBin, scheduleLocked);
            report.Items.Add(new(f.Location, r.Succeeded, r.Error, r.Outcome));
        }
        if (report.Items.Count > 0) Log(toRecycleBin ? "file/folder item(s) to the Recycle Bin" : "file/folder item(s) permanently", title, report);
        return report;
    }

    /// <summary>Exports a leftover tree/list to a self-contained HTML document.</summary>
    public static string ExportHtml(string programName, LeftoverNode? registry, IEnumerable<LeftoverNode>? files)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Leftovers</title><style>")
          .Append("body{font-family:Segoe UI,system-ui,sans-serif;margin:24px;color:#1b1b1f}li{margin:2px 0;font-size:13px}")
          .Append(".c{font-weight:600}.x{color:#c42b1c}.r{color:#6b6b76;font-size:12px}table{border-collapse:collapse}td,th{padding:4px 10px;border-bottom:1px solid #e3e3ea;font-size:13px;text-align:left}")
          .Append("</style></head><body>");
        sb.Append($"<h1>Leftovers of {WebUtility.HtmlEncode(programName)}</h1><p>Exported {DateTime.Now:yyyy-MM-dd HH:mm}. Bold items were identified as created by the program; red items are excluded.</p>");
        if (registry is not null)
        {
            sb.Append("<h2>Registry</h2>");
            AppendTree(sb, registry);
        }
        if (files is not null)
        {
            var list = files.ToList();
            sb.Append($"<h2>Files and folders ({list.Count})</h2><table><tr><th>Path</th><th>Type</th><th>Size</th><th>Reason</th></tr>");
            foreach (var f in list)
                sb.Append($"<tr><td class=\"{(f.IsExcluded ? "x" : f.IsCreated ? "c" : "")}\">{WebUtility.HtmlEncode(f.FsPath)}</td><td>{f.Kind}</td><td>{Format.Bytes(f.Size)}</td><td class=\"r\">{WebUtility.HtmlEncode(f.Reason)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendTree(StringBuilder sb, LeftoverNode node)
    {
        sb.Append("<ul>");
        foreach (var c in node.Children)
        {
            var cls = c.IsExcluded ? "x" : c.IsCreated ? "c" : "";
            sb.Append($"<li><span class=\"{cls}\">{WebUtility.HtmlEncode(c.DisplayName)}</span>");
            if (c.Kind == LeftoverKind.RegistryValue && !string.IsNullOrEmpty(c.ValueData)) sb.Append($" = {WebUtility.HtmlEncode(c.ValueData)}");
            if (c.IsCreated && c.Reason is not null) sb.Append($" <span class=\"r\">({WebUtility.HtmlEncode(c.Reason)})</span>");
            if (c.Children.Count > 0) AppendTree(sb, c);
            sb.Append("</li>");
        }
        sb.Append("</ul>");
    }
}
