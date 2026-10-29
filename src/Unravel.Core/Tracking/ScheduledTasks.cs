using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Unravel.Core.Tracking;

public sealed record ScheduledTaskInfo(string Path, string Name, bool Enabled, string? Author, IReadOnlyList<string> Actions, string Xml);

/// <summary>Reads, exports, deletes and re-registers scheduled tasks through the Task Scheduler COM API.</summary>
public static class ScheduledTasks
{
    private const int TASK_ENUM_HIDDEN = 1;
    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc;
    }

    /// <summary>Every task the current user can read (tasks of other users or SYSTEM may be hidden without administrator rights).</summary>
    public static List<ScheduledTaskInfo> List()
    {
        var list = new List<ScheduledTaskInfo>();
        try
        {
            var svc = Connect();
            Walk(svc.GetFolder("\\"), list, 0);
        }
        catch { }
        return list;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> list, int depth)
    {
        try
        {
            foreach (dynamic t in folder.GetTasks(TASK_ENUM_HIDDEN))
            {
                try
                {
                    string xml = t.Xml;
                    list.Add(new ScheduledTaskInfo((string)t.Path, (string)t.Name, (bool)t.Enabled, ExtractAuthor(xml), ExtractActions(xml), xml));
                }
                catch { }
                finally { Marshal.FinalReleaseComObject(t); }
            }
        }
        catch { }
        if (depth > 12) return;
        try
        {
            foreach (dynamic sub in folder.GetFolders(0))
            {
                try { Walk(sub, list, depth + 1); }
                finally { Marshal.FinalReleaseComObject(sub); }
            }
        }
        catch { }
    }

    internal static List<string> ExtractActions(string xml)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(xml, @"<Exec>.*?<Command>(.*?)</Command>(?:.*?<Arguments>(.*?)</Arguments>)?.*?</Exec>", RegexOptions.Singleline))
        {
            var cmd = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim().Trim('"');
            cmd = Environment.ExpandEnvironmentVariables(cmd);
            list.Add(m.Groups[2].Success ? $"\"{cmd}\" {System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)}" : cmd);
        }
        return list;
    }

    private static string? ExtractAuthor(string xml)
    {
        var m = Regex.Match(xml, @"<Author>(.*?)</Author>", RegexOptions.Singleline);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    public static void Delete(string path)
    {
        var svc = Connect();
        var (folderPath, name) = Split(path);
        dynamic folder = svc.GetFolder(folderPath);
        try { folder.DeleteTask(name, 0); }
        catch (COMException e) when ((uint)e.HResult == 0x80070005)
        { throw new UnauthorizedAccessException("Deleting this task requires administrator rights.", e); }
    }

    /// <summary>Re-creates a task from its exported XML (used to undo a cleanup).</summary>
    public static void Register(string path, string xml)
    {
        var svc = Connect();
        var (folderPath, name) = Split(path);
        dynamic folder = EnsureFolder(svc, folderPath);
        try { folder.RegisterTask(name, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null); }
        catch (COMException e) when ((uint)e.HResult == 0x80070005)
        { throw new UnauthorizedAccessException("Restoring this task requires administrator rights.", e); }
    }

    private static dynamic EnsureFolder(dynamic svc, string folderPath)
    {
        try { return svc.GetFolder(folderPath); }
        catch
        {
            dynamic root = svc.GetFolder("\\");
            return root.CreateFolder(folderPath.TrimStart('\\'), null);
        }
    }

    private static (string Folder, string Name) Split(string path)
    {
        var i = path.LastIndexOf('\\');
        return (i <= 0 ? "\\" : path[..i], path[(i + 1)..]);
    }
}
