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
    /// <summary>Older WinGet versions cut long IDs at the 120 columns they assume when output is redirected.</summary>
    public bool IsIdTruncated => Id.EndsWith(WinGetParser.Ellipsis);
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
    private readonly Func<string, IProgress<string>?, CancellationToken, Task<WinGetResult>>? _runner;

    public WinGetClient(string? executable = null, Func<string, IProgress<string>?, CancellationToken, Task<WinGetResult>>? runner = null)
    {
        Executable = executable ?? Locate() ?? "winget.exe";
        _runner = runner;
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
        return Rows(r, WinGetParser.Command.List);
    }

    public async Task<List<WinGetPackage>> ListUpgradesAsync(CancellationToken ct = default)
    {
        var r = await RunAsync($"upgrade {Agreements}", null, ct);
        return Rows(r, WinGetParser.Command.Upgrade).Where(p => p.HasUpdate).ToList();
    }

    public async Task<List<WinGetPackage>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter a name or ID to search for.");
        var r = await RunAsync($"search {Quote(query)} {Agreements}", null, ct);
        return Rows(r, WinGetParser.Command.Search);
    }

    public Task<WinGetResult> ShowAsync(string id, CancellationToken ct = default) =>
        WithFullId(id, "search", full => RunAsync($"show --id {Quote(full)} --exact {Agreements}", null, ct), ct);

    public Task<WinGetResult> InstallAsync(string id, string? version = null, WinGetScope scope = WinGetScope.Any, bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Install", id, WithFullId(id, "search", full => RunAsync($"install --id {Quote(full)} --exact {Version(version)}{Scope(scope)}{(silent ? "--silent " : "")}--accept-package-agreements {Agreements}", progress, ct), ct));

    public Task<WinGetResult> UpgradeAsync(string id, bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Update", id, WithFullId(id, "list", full => RunAsync($"upgrade --id {Quote(full)} --exact {(silent ? "--silent " : "")}--accept-package-agreements {Agreements}", progress, ct), ct));

    public Task<WinGetResult> UpgradeAllAsync(bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Update all", "*", RunAsync($"upgrade --all {(silent ? "--silent " : "")}--accept-package-agreements {Agreements}", progress, ct));

    public Task<WinGetResult> UninstallAsync(string id, bool silent = true, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Logged("Uninstall", id, WithFullId(id, "list", full => RunAsync($"uninstall --id {Quote(full)} --exact {(silent ? "--silent " : "")}{Agreements}", progress, ct), ct));

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

    /// <summary>The table WinGet printed. A failure only counts when it left nothing to show (one source can fail while the rest answer).</summary>
    private static List<WinGetPackage> Rows(WinGetResult r, WinGetParser.Command command)
    {
        var rows = WinGetParser.ParsePackages(r.Output, command);
        if (r.Ok || r.ExitCode == WinGetErrors.NoApplicationsFound) return rows;
        if (rows.Count == 0) throw new InvalidOperationException(r.Message);
        ActivityLog.Warning("WinGet", $"Listed {rows.Count} package(s), but WinGet also reported a problem: {r.Message}");
        return rows;
    }

    /// <summary>Runs an action with the package's complete ID; a shortened ID is looked up first and is never passed on.</summary>
    private async Task<WinGetResult> WithFullId(string id, string lookupVerb, Func<string, Task<WinGetResult>> action, CancellationToken ct)
    {
        if (!id.EndsWith(WinGetParser.Ellipsis)) return await action(id);
        var prefix = id.TrimEnd(WinGetParser.Ellipsis);
        var r = await RunAsync($"{lookupVerb} --id {Quote(prefix)} {Agreements}", null, ct);
        var command = lookupVerb == "search" ? WinGetParser.Command.Search : WinGetParser.Command.List;
        var matches = WinGetParser.ParsePackages(r.Output, command)
            .Where(p => !p.IsIdTruncated && p.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (matches.Count == 1) return await action(matches[0]);
        return new WinGetResult(-3, r.Output, $"WinGet shortened this package's ID to \"{id}\" and the full ID could not be determined. Update App Installer from the Microsoft Store and try again.");
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";
    private static string Version(string? v) => string.IsNullOrWhiteSpace(v) ? "" : $"--version {Quote(v)} ";
    private static string Scope(WinGetScope s) => s switch { WinGetScope.User => "--scope user ", WinGetScope.Machine => "--scope machine ", _ => "" };

    /// <summary>Runs winget with the given arguments, streaming cleaned output lines to <paramref name="progress"/>.</summary>
    public async Task<WinGetResult> RunAsync(string arguments, IProgress<string>? progress, CancellationToken ct)
    {
        if (_runner is not null) return await _runner(arguments, progress, ct);
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

    // Taken from Microsoft's "winget return codes" table. Anything else shows WinGet's own message and the code.
    private static readonly Dictionary<uint, string> Messages = new()
    {
        [0x8A150002] = "This version of WinGet doesn't understand the command Unravel sent. Update App Installer from the Microsoft Store.",
        [0x8A150010] = "No installer for this package fits this PC.",
        [0x8A150011] = "The downloaded installer's hash doesn't match the package manifest, so WinGet refused to run it.",
        [0x8A150014] = "No package matches.",
        [0x8A150015] = "WinGet has no package sources configured.",
        [0x8A150016] = "More than one package matches. Use an exact package ID.",
        [0x8A150017] = "No package manifest matches.",
        [0x8A15002B] = "No applicable update was found.",
        [0x8A15002C] = "Some updates failed. See the output for which ones.",
        [0x8A150030] = "The program's uninstall command failed.",
        [0x8A150045] = "WinGet could not open a package source. Check the internet connection and try again.",
        [0x8A150061] = "This package is already installed.",
        [0x8A150068] = "A pin stops this package from being updated.",
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

    public const char Ellipsis = '\u2026';

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
                if (DisplayWidth(row) < starts[Math.Min(2, starts.Count - 1)]) break; // footer text ("31 upgrades available.")
                string Cell(int c)
                {
                    if (c < 0 || c >= starts.Count) return "";
                    int from = IndexAtColumn(row, starts[c]);
                    int to = c + 1 < starts.Count ? IndexAtColumn(row, starts[c + 1]) : row.Length;
                    return row[from..to].Trim();
                }
                var id = Cell(idCol);
                // ARP entries use the registry key name as their ID, which may contain spaces; a space in anything else is a sentence.
                if (id.Length == 0 || (id.Contains(' ') && !IsLocalId(id))) continue;
                var name = Cell(nameCol);
                result.Add(new WinGetPackage(name, id, Cell(verCol), availCol >= 0 ? NullIfEmpty(Cell(availCol)) : null, srcCol >= 0 ? NullIfEmpty(Cell(srcCol)) : null));
            }
        }
        return result;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static bool IsLocalId(string id) => id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Display column of each header word (columns are separated by at least one space).</summary>
    internal static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        int column = 0;
        bool previousIsSpace = true;
        foreach (var rune in header.EnumerateRunes())
        {
            bool space = rune.Value == ' ';
            if (!space && previousIsSpace) starts.Add(column);
            previousIsSpace = space;
            column += RuneWidth(rune.Value);
        }
        return starts;
    }

    // WinGet pads its tables by display width, so East Asian characters count as two columns.
    internal static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes()) width += RuneWidth(rune.Value);
        return width;
    }

    /// <summary>The string index of the first character at or after a display column.</summary>
    private static int IndexAtColumn(string text, int column)
    {
        int width = 0, index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (width >= column) return index;
            width += RuneWidth(rune.Value);
            index += rune.Utf16SequenceLength;
        }
        return text.Length;
    }

    private static int RuneWidth(int c) =>
        c is >= 0x0300 and <= 0x036F or >= 0x200B and <= 0x200F or >= 0xFE00 and <= 0xFE0F ? 0 :
        c is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
          or >= 0xFE30 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
}
