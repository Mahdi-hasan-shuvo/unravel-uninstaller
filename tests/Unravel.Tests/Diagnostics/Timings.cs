using System.Diagnostics;
using Unravel.Core.Leftovers;
using Unravel.Core.Programs;

namespace Unravel.Tests.Diagnostics;

public sealed class DiagnosticFactAttribute : FactAttribute
{
    public DiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("UNRAVEL_DIAG") != "1")
            Skip = "Set UNRAVEL_DIAG=1 to time the real-system scans.";
    }
}

/// <summary>Not a behaviour test: times the main read-only scans on this PC and writes the slowest cases to a file.</summary>
public class Timings
{
    [DiagnosticFact]
    public void TimeRealSystemScans()
    {
        var lines = new List<string>();
        var sw = Stopwatch.StartNew();
        var programs = new ProgramInventory().Load(new InventoryOptions());
        lines.Add($"inventory: {programs.Count} programs in {sw.ElapsedMilliseconds} ms");

        var targets = new List<(string Name, long Ms)>();
        foreach (var p in programs)
        {
            sw.Restart();
            ScanTarget.FromProgram(p);
            targets.Add((p.DisplayName, sw.ElapsedMilliseconds));
        }
        lines.Add($"ScanTarget.FromProgram total {targets.Sum(t => t.Ms)} ms; slowest: " +
                  string.Join(", ", targets.OrderByDescending(t => t.Ms).Take(5).Select(t => $"{t.Name} {t.Ms} ms")));

        sw.Restart();
        var env = ScanEnvironment.CreateDefault();
        lines.Add($"ScanEnvironment.CreateDefault: {sw.ElapsedMilliseconds} ms");
        env.OtherPrograms = programs.Select(p => (p.DisplayName, p.ResolvedInstallFolder ?? "")).ToList();
        var scans = new List<(string Name, ScanMode Mode, long Ms, int Found)>();
        foreach (var p in programs.OrderByDescending(p => p.EstimatedSizeBytes ?? 0).Take(6))
        {
            var target = ScanTarget.FromProgram(p);
            foreach (var mode in new[] { ScanMode.Safe, ScanMode.Moderate, ScanMode.Advanced })
            {
                sw.Restart();
                var r = new LeftoverScanner(env).Scan(target, mode);
                scans.Add((p.DisplayName, mode, sw.ElapsedMilliseconds, r.RawFindings.Count));
            }
        }
        foreach (var g in scans.GroupBy(s => s.Mode))
            lines.Add($"leftover scan {g.Key}: avg {g.Average(s => s.Ms):0} ms, max {g.Max(s => s.Ms)} ms ({g.MaxBy(s => s.Ms).Name})");
        sw.Restart();
        var snap = Unravel.Core.Tracking.SystemSnapshot.Capture(Unravel.Core.Tracking.SnapshotOptions.CreateDefault(), "timing");
        lines.Add($"install-tracker snapshot: {snap.Files.Count} files/folders, {snap.Registry.Count} keys in {sw.ElapsedMilliseconds} ms");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "unravel-timings.txt"), lines);
    }
}
