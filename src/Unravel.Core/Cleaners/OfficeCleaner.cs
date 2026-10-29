using System.Text.RegularExpressions;
using Microsoft.Win32;
using Unravel.Core.Util;

namespace Unravel.Core.Cleaners;

/// <summary>Clears the recent-documents lists of Microsoft Office applications (all installed versions).</summary>
public static class OfficeCleaner
{
    public static RegPath DefaultOfficeRoot => new(RegistryHive.CurrentUser, @"Software\Microsoft\Office");
    public static RegPath DefaultFrontPageRoot => new(RegistryHive.CurrentUser, @"Software\Microsoft\FrontPage");

    private static readonly (string App, string Title, string Versions)[] Apps =
    [
        ("Word", "Microsoft Word", "Word 2000 – Microsoft 365"),
        ("Excel", "Microsoft Excel", "Excel 2000 – Microsoft 365"),
        ("PowerPoint", "Microsoft PowerPoint", "PowerPoint 2000 – Microsoft 365"),
        ("Access", "Microsoft Access", "Access 2000 – Microsoft 365"),
    ];

    public static List<CleanerItem> BuildItems(RegPath? officeRoot = null, RegPath? frontPageRoot = null)
    {
        var root = officeRoot ?? DefaultOfficeRoot;
        var fp = frontPageRoot ?? DefaultFrontPageRoot;
        var items = Apps.Select(a => new CleanerItem
        {
            Id = "office:" + a.App,
            Group = "Microsoft Office",
            Title = $"{a.Title} recent documents",
            Description = a.Versions,
            Targets = () => TargetsFor(root, a.App),
            BlockingProcesses = [a.App switch { "Word" => "winword", "Excel" => "excel", "PowerPoint" => "powerpnt", _ => "msaccess" }],
        }).ToList();
        items.Add(new CleanerItem
        {
            Id = "office:FrontPage",
            Group = "Microsoft Office",
            Title = "Microsoft FrontPage recent files and webs",
            Description = "FrontPage 2000 – 2003",
            Targets = () =>
            [
                new RegistryTarget(fp.Child(@"Explorer\FrontPage Explorer\Recent File List"), RegistryCleanMode.Values),
                new RegistryTarget(fp.Child(@"Explorer\FrontPage Explorer\Recent Web List"), RegistryCleanMode.Values),
                new RegistryTarget(fp.Child(@"Editor\Recent File List"), RegistryCleanMode.Values),
            ],
            BlockingProcesses = ["frontpg"],
        });
        return items;
    }

    public static IEnumerable<CleanupTarget> TargetsFor(RegPath officeRoot, string app)
    {
        using var rk = officeRoot.Open();
        if (rk is null) yield break;
        foreach (var version in rk.GetSubKeyNames().Where(v => Regex.IsMatch(v, @"^\d+\.\d+$")))
        {
            var appKey = officeRoot.Child(version).Child(app);
            foreach (var list in new[] { "File MRU", "Place MRU", "Recent Files", "Recent File List" })
                yield return new RegistryTarget(appKey.Child(list), RegistryCleanMode.Values, ValueFilter: n => n.Length > 0 && n != "Max Display");
            // Office 2013+ keeps per-account lists under User MRU\<id>.
            using (var um = appKey.Child("User MRU").Open())
                if (um is not null)
                    foreach (var id in um.GetSubKeyNames())
                        foreach (var list in new[] { "File MRU", "Place MRU" })
                            yield return new RegistryTarget(appKey.Child("User MRU").Child(id).Child(list), RegistryCleanMode.Values, ValueFilter: n => n.Length > 0 && n != "Max Display");
            if (app == "Access")
                yield return new RegistryTarget(appKey.Child("Settings"), RegistryCleanMode.Values,
                    ValueFilter: n => Regex.IsMatch(n, @"^MRU(Flags)?\d+$", RegexOptions.IgnoreCase));
        }
    }
}
