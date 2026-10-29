using System.Diagnostics;
using Unravel.Core.Apps;
using Unravel.Core.Leftovers;
using Unravel.Core.Programs;
using Unravel.Core.Util;

namespace Unravel.App.Dialogs;

public enum UninstallerOutcome { Finished, StartFailed, Cancelled, SkippedWaiting }

public sealed record UninstallerResult(UninstallerOutcome Outcome, string? Message = null);

/// <summary>What the wizard uninstalls: a desktop program (registry entry) or a packaged Windows app.</summary>
public sealed class UninstallJob
{
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? IconLocation { get; init; }
    public string? IconFile { get; init; }
    public required ScanTarget Target { get; init; }
    public required List<(string Name, string Folder)> OtherPrograms { get; init; }
    public required Func<bool> StillInstalled { get; init; }
    /// <summary>Starts the uninstaller and completes when it (and any child processes) exit.</summary>
    public required Func<IProgress<string>, CancellationToken, Task<UninstallerResult>> RunUninstaller { get; init; }
    public bool IsWindowsApp { get; init; }

    public static UninstallJob ForProgram(InstalledProgram p, UninstallCommandKind kind, List<(string, string)> others) => new()
    {
        DisplayName = p.DisplayName,
        Publisher = p.Publisher,
        IconLocation = p.DisplayIcon,
        // A recorded install log (Install tracker) makes the leftover scan exact for this program.
        Target = ScanTarget.FromProgram(p, SafeFindLog(p)),
        OtherPrograms = others,
        StillInstalled = () => ProgramActions.StillInstalled(p),
        RunUninstaller = async (progress, ct) =>
        {
            Process proc;
            try { proc = ProgramActions.StartCommand(p, kind); }
            catch (OperationCanceledException e) { return new(UninstallerOutcome.Cancelled, e.Message); }
            catch (Exception e) { return new(UninstallerOutcome.StartFailed, e.Message); }
            progress.Report($"Uninstaller started ({Path.GetFileName(proc.StartInfo.FileName)}).");
            int pid;
            try { pid = proc.Id; } catch { return new(UninstallerOutcome.Finished); }
            await ProcessUtil.WaitForProcessTreeAsync(pid, ct);
            return ct.IsCancellationRequested ? new(UninstallerOutcome.SkippedWaiting) : new(UninstallerOutcome.Finished);
        },
    };

    private static Core.Tracking.InstallLog? SafeFindLog(InstalledProgram p)
    {
        try { return new Core.Tracking.InstallTracker().FindFor(p); } catch { return null; }
    }

    /// <summary>
    /// Uninstalls through WinGet, then continues with the normal leftover scan. When the package maps to a program in
    /// the installed list, that program's footprint is recorded first so the scan knows where to look.
    /// </summary>
    public static UninstallJob ForWinGet(Core.WinGet.WinGetPackage package, InstalledProgram? program, List<(string, string)> others)
    {
        var client = new Core.WinGet.WinGetClient();
        return new UninstallJob
        {
            DisplayName = program?.DisplayName ?? package.Name,
            Publisher = program?.Publisher,
            IconLocation = program?.DisplayIcon,
            Target = program is not null ? ScanTarget.FromProgram(program, SafeFindLog(program)) : new ScanTarget { DisplayName = package.Name },
            OtherPrograms = others,
            StillInstalled = () => program is not null ? ProgramActions.StillInstalled(program)
                : client.ListInstalledAsync().GetAwaiter().GetResult().Any(x => x.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)),
            RunUninstaller = async (progress, ct) =>
            {
                progress.Report($"winget uninstall {package.Id}");
                var r = await client.UninstallAsync(package.Id, silent: false, progress, ct);
                if (r.ExitCode == -2) return new(UninstallerOutcome.SkippedWaiting);
                return r.Ok ? new(UninstallerOutcome.Finished) : new(UninstallerOutcome.StartFailed, "WinGet: " + r.Message);
            },
        };
    }

    public static UninstallJob ForWindowsApp(WindowsApp app, List<(string, string)> others) => new()
    {
        DisplayName = app.Name,
        Publisher = app.Publisher,
        IconFile = app.LogoPath,
        Target = app.ToScanTarget(),
        OtherPrograms = others,
        IsWindowsApp = true,
        StillInstalled = () => WindowsApps.IsInstalled(app),
        RunUninstaller = async (progress, ct) =>
        {
            try
            {
                progress.Report("Removing the app package…");
                await WindowsApps.UninstallAsync(app, new Progress<double>(pct => progress.Report($"Removing the app package… {pct:0}%")), ct);
                return new(UninstallerOutcome.Finished);
            }
            catch (OperationCanceledException) { return new(UninstallerOutcome.Cancelled, "The removal was cancelled."); }
            catch (Exception e) { return new(UninstallerOutcome.StartFailed, e.Message); }
        },
    };
}
