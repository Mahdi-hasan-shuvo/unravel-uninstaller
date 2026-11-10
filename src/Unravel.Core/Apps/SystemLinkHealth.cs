using Unravel.Core.Logging;
using Windows.System;

namespace Unravel.Core.Apps;

/// <summary>A link Windows itself opens, with no installed app to handle it.</summary>
public sealed record SystemLinkIssue(string Scheme, string AppName, string StoreProductId, string SettingsUrl, string Explanation)
{
    public string StoreUrl => $"ms-windows-store://pdp/?ProductId={StoreProductId}";
}

/// <summary>
/// Finds links that Windows and games open but nothing on this PC can handle. Windows answers those with
/// "You'll need a new app to open this link". This check only reads; it never installs or changes anything.
/// </summary>
public static class SystemLinkHealth
{
    private static readonly (string Scheme, string App, string ProductId, string Settings, string Explanation)[] Known =
    [
        ("ms-gamingoverlay", "Xbox Game Bar", "9NZKPSTSNW4P", "ms-settings:gaming-gamebar",
         "Windows opens Xbox Game Bar through the ms-gamingoverlay: link (Win+G, a controller's Xbox button, some games), " +
         "but Game Bar isn't installed, so Windows asks you to find an app for it. Install Game Bar to fix this, or turn off its shortcuts in Windows Settings."),
    ];

    /// <summary>Asks Windows whether anything can open the link (works for desktop and Store apps).</summary>
    public static async Task<bool> HasHandlerAsync(string scheme) =>
        await Launcher.QueryUriSupportAsync(new Uri(scheme + ":"), LaunchQuerySupportType.Uri) == LaunchQuerySupportStatus.Available;

    public static async Task<List<SystemLinkIssue>> CheckAsync(Func<string, Task<bool>>? hasHandler = null)
    {
        hasHandler ??= HasHandlerAsync;
        var issues = new List<SystemLinkIssue>();
        foreach (var k in Known)
        {
            try
            {
                if (!await hasHandler(k.Scheme)) issues.Add(new(k.Scheme, k.App, k.ProductId, k.Settings, k.Explanation));
            }
            catch (Exception e)
            {
                // A failed query says nothing about the link, so no problem is reported.
                ActivityLog.Warning("Windows apps", $"Could not check the {k.Scheme}: link: {e.Message}");
            }
        }
        return issues;
    }
}
