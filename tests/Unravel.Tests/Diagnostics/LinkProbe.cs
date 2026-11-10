using Windows.System;

namespace Unravel.Tests.Diagnostics;

/// <summary>Not a behaviour test: what Windows answers about link handlers on this PC.</summary>
public class LinkProbe
{
    [DiagnosticFact]
    public async Task DumpLinkHandlers()
    {
        var lines = new List<string>();
        foreach (var scheme in new[] { "ms-gamingoverlay", "ms-gamebar", "ms-settings", "ms-xbox-unravel-test-nonexistent" })
        {
            var status = await Launcher.QueryUriSupportAsync(new Uri(scheme + ":"), LaunchQuerySupportType.Uri);
            var handlers = await Launcher.FindUriSchemeHandlersAsync(scheme);
            lines.Add($"{scheme}: {status}; handlers: {string.Join(", ", handlers.Select(h => h.PackageFamilyName + " / " + h.DisplayInfo.DisplayName))}");
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "unravel-links.txt"), lines);
    }
}
