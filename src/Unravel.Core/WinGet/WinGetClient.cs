using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Unravel.Core.Logging;

namespace Unravel.Core.WinGet;

public sealed record WinGetPackage(string Name, string Id, string Version, string? Available, string? Source)
{
    public bool HasUpdate => !string.IsNullOrWhiteSpace(Available);
    /// <summary>Entries WinGet only sees through Programs and Features or MSIX, not from a source; they can't be updated via WinGet.</summary>
    public bool IsLocalOnly => Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) || Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase);
    /// <summary>For ARP entries, the uninstall registry key name (ARP\Machine\X64\{key}).</summary>
    public string? ArpKeyName => Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) ? Id.Split('\\').Last() : null;
}

public sealed record WinGetResult(int ExitCode, string Output, string Message)
{
    public bool Ok => ExitCode == 0;
}

public enum WinGetScope { Any, User, Machine }

/// <summary>
/// Drives winget.exe (Windows Package Manager). All commands run non-interactively, stream their output, can be
/// cancelled (the process tree is ended), and return a readable message for WinGet's error codes.
/// </summary>
public sealed class WinGetClient
{
    public string Executable { get; }

    public WinGetClient(string? executable = null)
    {
        Executable = executable ?? Locate() ?? "winget.exe";
    }

    public static string? Locate()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "winget.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public bool IsAvailable => Locate() is not null || File.Exists(Executable);

    private const string Agreements = "--accept-source-agreements --disable-interactivity";

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var r = await RunAsync("--version", null, ct);
        return r.Ok ? r.Output.Trim() : null;
    }

    public async Task<List<WinGetPackage>> ListInstalledAsync(CancellationToken ct = default)
    {
        var r = await RunAsync($"list {Agreements}", null, ct);
        EnsureOk(r, allowNoResults: true);
        return WinGetParser.ParsePackages(r.Output, WinGetParser.Command.List);
    }

    public async Task<List<WinGetPackage>> ListUpgradesAsync(CancellationToken ct = default)
    {
        var r = await RunAsync($"upgrade {Agreements}", null, ct);
        EnsureOk(r, allowNoResults: true);
        return WinGetParser.ParsePackages(r.Output, WinGetParser.Command.Upgrade).Where(p => p.HasUpdate).ToList();
    }

    public async Task<List<WinGetPackage>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter a name or ID to search for.");
        var r = await RunAsync($"search {Quote(query)} {Agreements}", null, ct);
        EnsureOk(r, allowNoResults: true);
        return WinGetParser.ParsePackages(r.Output, WinGetParser.Command.Search);
    }

    public Task<WinGetResult> ShowAsync(string id, CancellationToken ct = default) =>
        RunAsync($"show --id {Quote(id)} --exact {Agreements}", null, ct);

    public Task<WinGetResult> InstallAsync(string id, string? version = null, WinGetScope scope = WinGetScope.Any, bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Install", id, RunAsync($"install --id {Quote(id)} --exact {Version(version)}{Scope(scope)}{(silent ? "--silent " : "")}--accept-package-agreements {Agreements}", progress, ct));

    public Task<WinGetResult> UpgradeAsync(string id, bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Update", id, RunAsync($"upgrade --id {Quote(id)} --exact {(silent ? "--silent " : "")}--accept-package-agreements {Agreements}", progress, ct));

    public Task<WinGetResult> UpgradeAllAsync(bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Update all", "*", RunAsync($"upgrade --all {(silent ? "--silent " : "")}--accept-package-agreements {Agreements}", progress, ct));

    public Task<WinGetResult> UninstallAsync(string id, bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Uninstall", id, RunAsync($"uninstall --id {Quote(id)} --exact {(silent ? "--silent " : "")}{Agreements}", progress, ct));

    public async Task<WinGetResult> ExportAsync(string file, CancellationToken ct = default)
    {
        var r = await Logged("Export", file, RunAsync($"export -o {Quote(file)} --include-versions {Agreements}", null, ct));
        // WinGet reports packages it cannot export (e.g. not from a source) but still writes the file.
        return File.Exists(file) && r.ExitCode != 0 ? r with { ExitCode = 0, Message = "Exported. Some installed programs are not available from a WinGet source and were left out." } : r;
    }

    public Task<WinGetResult> ImportAsync(string file, bool ignoreVersions, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("The package list file was not found.", file);
        return Logged("Import", file, RunAsync($"import -i {Quote(file)} {(ignoreVersions ? "--ignore-versions " : "")}--ignore-unavailable --accept-package-agreements {Agreements}", progress, ct));
    }

    /// <summary>Uninstalls and installs the same package again (the same version when it is known).</summary>
    public async Task<WinGetResult> ReinstallAsync(string id, string? version, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"Uninstalling {id}…");
        var u = await UninstallAsync(id, true, progress, ct);
        if (!u.Ok) return u with { Message = "Reinstall stopped – uninstall failed: " + u.Message };
        progress?.Report($"Installing {id} again…");
        var i = await InstallAsync(id, version, WinGetScope.Any, true, progress, ct);
        if (!i.Ok && version is not null)
        {
            progress?.Report($"Version {version} is not available any more; installing the latest version…");
            i = await InstallAsync(id, null, WinGetScope.Any, true, progress, ct);
        }
        return i;
    }

    private static async Task<WinGetResult> Logged(string action, string target, Task<WinGetResult> task)
    {
        var r = await task;
        if (r.Ok) ActivityLog.Action("WinGet", $"{action} {target}: {r.Message}");
        else ActivityLog.Error("WinGet", $"{action} {target} failed: {r.Message}", WinGetParser.Tail(r.Output, 20));
        return r;
    }

    private static void EnsureOk(WinGetResult r, bool allowNoResults)
    {
        if (r.Ok) return;
        if (allowNoResults && r.ExitCode == WinGetErrors.NoApplicationsFound) return;
        throw new InvalidOperationException(r.Message);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";
    private static string Version(string? v) => string.IsNullOrWhiteSpace(v) ? "" : $"--version {Quote(v)} ";
    private static string Scope(WinGetScope s) => s switch { WinGetScope.User => "--scope user ", WinGetScope.Machine => "--scope machine ", _ => "" };

    /// <summary>Runs winget with the given arguments, streaming cleaned output lines to <paramref name="progress"/>.</summary>
    public async Task<WinGetResult> RunAsync(string arguments, IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("WinGet did not start."); }
        catch (System.ComponentModel.Win32Exception)
        {
            return new WinGetResult(-1, "", "WinGet (App Installer) is not installed. Install \"App Installer\" from the Microsoft Store.");
        }
        using (proc)
        {
            var output = new StringBuilder();
            var lineBuffer = new StringBuilder();
            void OnChunk(string? data)
            {
                if (data is null) return;
                lock (output)
                {
                    output.AppendLine(data);
                    foreach (var line in WinGetParser.CleanLines(data)) progress?.Report(line);
                }
            }
            proc.OutputDataReceived += (_, e) => OnChunk(e.Data);
            proc.ErrorDataReceived += (_, e) => OnChunk(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                ActivityLog.Warning("WinGet", $"Cancelled: winget {arguments}");
                return new WinGetResult(-2, output.ToString(), "Cancelled.");
            }
            proc.WaitForExit(); // flush asynchronous output
            var text = output.ToString();
            return new WinGetResult(proc.ExitCode, text, WinGetErrors.Describe(proc.ExitCode, text));
        }
    }
}

/// <summary>WinGet's documented HRESULT exit codes (APPINSTALLER_CLI_ERROR_*) mapped to plain explanations.</summary>
public static class WinGetErrors
{
    public const int NoApplicationsFound = unchecked((int)0x8A150014);

    // Only codes whose meaning is certain are mapped; anything else shows WinGet's own message and the code.
    private static readonly Dictionary<uint, string> Messages = new()
    {
        [0x8A150011] = "More than one package matches. Use an exact package ID.",
        [0x8A150014] = "No package matches.",
        [0x8A15002B] = "No applicable update was found.",
        [0x8A150101] = "The program is in use. Close it and try again.",
        [0x8A150102] = "Another installation is in progress. Wait for it to finish.",
        [0x8A150103] = "A file the installer needs is in use.",
        [0x8A150104] = "A required component of the package is missing.",
        [0x8A150105] = "There is not enough disk space.",
        [0x8A150106] = "There is not enough memory.",
        [0x8A150107] = "The installer needs an internet connection.",
        [0x8A150108] = "The installer hit an error. Contact the publisher or try again later.",
        [0x8A150109] = "Restart your computer to finish the installation.",
        [0x8A15010A] = "The installation failed; a restart is required first.",
        [0x8A15010B] = "Your computer is restarting to finish the installation.",
        [0x8A15010C] = "The installation was cancelled.",
        [0x8A15010D] = "Another version of the program is already installed.",
        [0x8A15010E] = "A newer version of the program is already installed.",
        [0x8A15010F] = "Group policy blocks this installation.",
        [0x8A150110] = "A dependency of the package failed to install.",
        [0x8A150111] = "The program is being used by another program.",
        [0x8A150112] = "An invalid parameter was passed to the installer.",
        [0x8A150113] = "This package is not supported on this system.",
        [0x8A150114] = "The installer does not support upgrading this program.",
        [0x8A150115] = "The installer reported a custom error.",
    };

    public static string Describe(int exitCode, string output)
    {
        if (exitCode == 0) return "Completed successfully.";
        var code = unchecked((uint)exitCode);
        if (Messages.TryGetValue(code, out var m)) return m;
        if (code == 0x80070005) return "Access denied. This needs administrator rights.";
        // WinGet's last readable line is usually its own explanation of the failure.
        var last = WinGetParser.CleanLines(output).LastOrDefault(l => !l.StartsWith("Progress:", StringComparison.Ordinal));
        return last is null ? $"WinGet failed with code 0x{code:X8}." : $"{last} (code 0x{code:X8})";
    }
}

/// <summary>Parses WinGet's text tables. Columns are located from the header line, so localised headers still work.</summary>
public static class WinGetParser
{
    public enum Command { List, Upgrade, Search }

    private static readonly Regex Spinner = new(@"^[\s\-\\|/]+$");
    private static readonly Regex ProgressBar = new(@"[█▒▓░]");

    /// <summary>Splits on CR/LF and drops spinner frames and progress bars, keeping readable lines.</summary>
    public static IEnumerable<string> CleanLines(string data)
    {
        foreach (var raw in data.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || Spinner.IsMatch(line)) continue;
            if (ProgressBar.IsMatch(line))
            {
                var pct = Regex.Match(line, @"(\d+(\.\d+)?\s*(KB|MB|GB)\s*/\s*\d+(\.\d+)?\s*(KB|MB|GB)|\d+%)");
                if (pct.Success) yield return "Progress: " + pct.Value;
                continue;
            }
            yield return line.Trim();
        }
    }

    public static string Tail(string output, int lines) =>
        string.Join("\n", CleanLines(output).TakeLast(lines));

    public static List<WinGetPackage> ParsePackages(string output, Command command)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').SelectMany(l => l.Split('\r').TakeLast(1)).ToList();
        var result = new List<WinGetPackage>();
        for (int i = 1; i < lines.Count; i++)
        {
            // A table starts with a header line followed by a line of dashes.
            if (!Regex.IsMatch(lines[i], @"^-{10,}\s*$")) continue;
            var header = lines[i - 1];
            var starts = ColumnStarts(header);
            if (starts.Count < 3) continue;
            // Name, Id and Version are always the first three columns. The rest are found by their English header,
            // or by position when the header is localised: 5 columns = (Available|Match), Source; 4 columns = Source.
            var names = header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToLowerInvariant()).ToList();
            bool english = names.Count == starts.Count && names.Contains("source");
            int nameCol = 0, idCol = 1, verCol = 2;
            int availCol, srcCol;
            if (english)
            {
                availCol = names.IndexOf("available");
                srcCol = names.IndexOf("source");
            }
            else
            {
                srcCol = starts.Count >= 4 ? starts.Count - 1 : -1;
                availCol = command is Command.List or Command.Upgrade && starts.Count >= 5 ? 3 : -1;
            }
            for (int r = i + 1; r < lines.Count; r++)
            {
                var row = lines[r];
                if (row.Trim().Length == 0) break;
                if (r + 1 < lines.Count && Regex.IsMatch(lines[r + 1], @"^-{10,}\s*$")) break; // next table's header
                if (row.Length < starts[Math.Min(2, starts.Count - 1)]) break; // footer text ("31 upgrades available.")
                string Cell(int c)
                {
                    if (c < 0 || c >= starts.Count || starts[c] >= row.Length) return "";
                    var end = c + 1 < starts.Count ? Math.Min(starts[c + 1], row.Length) : row.Length;
                    return row[starts[c]..end].Trim();
                }
                var id = Cell(idCol);
                if (id.Length == 0 || id.Contains(' ')) continue;
                var name = Cell(nameCol);
                result.Add(new WinGetPackage(name, id, Cell(verCol), availCol >= 0 ? NullIfEmpty(Cell(availCol)) : null, srcCol >= 0 ? NullIfEmpty(Cell(srcCol)) : null));
            }
        }
        return result;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Start index of each header word (columns are separated by at least one space).</summary>
    internal static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        return starts;
    }
}
