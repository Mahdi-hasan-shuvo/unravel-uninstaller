using System.Runtime.CompilerServices;
using Unravel.Core.Logging;

namespace Unravel.Tests;

internal static class TestSetup
{
    /// <summary>Keeps every test's log output away from the user's real activity log.</summary>
    [ModuleInitializer]
    internal static void RedirectActivityLog() =>
        ActivityLog.UseFolder(Path.Combine(Path.GetTempPath(), "unravel-tests", "logs-" + Environment.ProcessId));
}
