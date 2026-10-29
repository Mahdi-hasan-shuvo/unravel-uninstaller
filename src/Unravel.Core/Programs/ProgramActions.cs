using System.Diagnostics;
using System.Net;
using System.Text;
using Unravel.Core.SystemOps;
using Unravel.Core.Util;

namespace Unravel.Core.Programs;

public enum UninstallCommandKind { Standard, ForcedMsi, Modify, Repair }

public static class ProgramActions
{
    /// <summary>Builds the command line for an action, or throws when the program does not support it.</summary>
    public static string GetCommand(InstalledProgram p, UninstallCommandKind kind) => kind switch
    {
        UninstallCommandKind.Standard => !string.IsNullOrWhiteSpace(p.UninstallString) ? p.UninstallString!
            : p.MsiProductCode is { } code ? $"MsiExec.exe /X{code}"
            : throw new InvalidOperationException("This program does not provide an uninstall command."),
        UninstallCommandKind.ForcedMsi => p.MsiProductCode is { } c2 ? $"MsiExec.exe /X{c2}"
            : throw new InvalidOperationException("Forced MSI uninstall is only available for Windows Installer programs."),
        UninstallCommandKind.Modify => !string.IsNullOrWhiteSpace(p.ModifyPath) ? p.ModifyPath!
            : p.IsMsi && p.MsiProductCode is { } c3 ? $"MsiExec.exe /I{c3}"
            : throw new InvalidOperationException("This program does not support Change."),
        UninstallCommandKind.Repair => p.SupportsRepair ? $"MsiExec.exe /fa{p.MsiProductCode}"
            : throw new InvalidOperationException("This program does not support Repair."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Starts the command. Programs registered for all users usually need elevation for their uninstaller.</summary>
    public static Process StartCommand(InstalledProgram p, UninstallCommandKind kind)
    {
        var cmd = GetCommand(p, kind);
        var (file, args) = CommandLine.Split(cmd);
        if (string.IsNullOrWhiteSpace(file)) throw new InvalidOperationException("The uninstall command is empty.");
        if (Path.IsPathRooted(file) && !File.Exists(file))
            throw new FileNotFoundException($"The program's uninstaller was not found:\n{file}\n\nThe uninstall command may be invalid. You can still scan for leftovers.", file);
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
        if (Path.IsPathRooted(file)) psi.WorkingDirectory = Path.GetDirectoryName(file)!;
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("The uninstaller did not start.");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The elevation prompt was cancelled.");
        }
    }

    /// <summary>Removes the program's entry from the installed programs list without uninstalling anything. A .reg backup is written first.</summary>
    public static (bool Ok, string? Error, string? BackupFile) RemoveEntry(InstalledProgram p, RegistryBackup? backup)
    {
        string? file = null;
        if (backup is not null)
        {
            var b = backup.Export([p.RegistryKey], $"Entry {p.DisplayName}");
            if (!b.Ok) return (false, "Backup failed, entry was not removed: " + b.Error, null);
            file = b.Folder;
        }
        var ok = RegistryUtil.DeleteKeyTree(p.RegistryKey, out var err);
        return (ok, err, file);
    }

    public static bool StillInstalled(InstalledProgram p) => p.RegistryKey.Exists();
}

public enum ProgramColumn { Name, Version, Publisher, InstallDate, Size, Type, Installer, Website, Comment, InstallLocation, UninstallString, RegistryKey }

/// <summary>Exports a list of programs to HTML or plain text with a selectable set of columns.</summary>
public static class ProgramExporter
{
    public static string Value(InstalledProgram p, ProgramColumn c) => c switch
    {
        ProgramColumn.Name => p.DisplayName,
        ProgramColumn.Version => p.Version ?? "",
        ProgramColumn.Publisher => p.Publisher ?? "",
        ProgramColumn.InstallDate => p.InstallDate?.ToString("yyyy-MM-dd") ?? "",
        ProgramColumn.Size => p.SizeBytes is { } s ? Format.Bytes(s) : "",
        ProgramColumn.Type => p.TypeText,
        ProgramColumn.Installer => p.Installer,
        ProgramColumn.Website => p.AboutLink ?? p.HelpLink ?? "",
        ProgramColumn.Comment => p.Comments ?? "",
        ProgramColumn.InstallLocation => p.InstallLocation ?? "",
        ProgramColumn.UninstallString => p.UninstallString ?? "",
        ProgramColumn.RegistryKey => p.RegistryKey.FullPath,
        _ => ""
    };

    public static string Header(ProgramColumn c) => c switch
    {
        ProgramColumn.InstallDate => "Install date",
        ProgramColumn.InstallLocation => "Install location",
        ProgramColumn.UninstallString => "Uninstall command",
        ProgramColumn.RegistryKey => "Registry key",
        _ => c.ToString()
    };

    public static string ToText(IEnumerable<InstalledProgram> programs, IReadOnlyList<ProgramColumn> columns)
    {
        var rows = programs.Select(p => columns.Select(c => Value(p, c)).ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Min(80, Math.Max(Header(c).Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length)))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"Installed programs - exported {DateTime.Now:yyyy-MM-dd HH:mm} - {rows.Count} item(s)");
        sb.AppendLine();
        sb.AppendLine(string.Join("  ", columns.Select((c, i) => Header(c).PadRight(widths[i]))).TrimEnd());
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var r in rows)
            sb.AppendLine(string.Join("  ", r.Select((v, i) => (v.Length > widths[i] ? v[..(widths[i] - 1)] + "…" : v).PadRight(widths[i]))).TrimEnd());
        return sb.ToString();
    }

    public static string ToHtml(IEnumerable<InstalledProgram> programs, IReadOnlyList<ProgramColumn> columns)
    {
        var list = programs.ToList();
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Installed programs</title><style>")
          .Append("body{font-family:Segoe UI,system-ui,sans-serif;margin:24px;color:#1b1b1f}table{border-collapse:collapse;width:100%}")
          .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #e3e3ea;font-size:13px;vertical-align:top}th{background:#f4f4f8}")
          .Append("</style></head><body>");
        sb.Append($"<h1>Installed programs</h1><p>Exported {DateTime.Now:yyyy-MM-dd HH:mm} &middot; {list.Count} item(s)</p><table><thead><tr>");
        foreach (var c in columns) sb.Append("<th>").Append(WebUtility.HtmlEncode(Header(c))).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var p in list)
        {
            sb.Append("<tr>");
            foreach (var c in columns) sb.Append("<td>").Append(WebUtility.HtmlEncode(Value(p, c))).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }
}
