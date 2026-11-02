using System.Diagnostics;

namespace OsmBuildings;

/// <summary>
/// Main-thread time spent in the mod's parts, summed per section and written to the log every 10 s as milliseconds
/// per frame, so slow parts show up in a test run.
/// </summary>
internal static class Perf
{
    private static readonly Dictionary<string, long> Ticks = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastReport;
    private static int _frames;
    public static string Summary = "";

    public static Scope Measure(string name) => new(name, Stopwatch.GetTimestamp());

    public readonly struct Scope(string name, long start) : IDisposable
    {
        public void Dispose()
        {
            long dt = Stopwatch.GetTimestamp() - start;
            Ticks[name] = Ticks.GetValueOrDefault(name) + dt;
        }
    }

    /// <summary>Called once per frame (OnFrame).</summary>
    public static void Frame()
    {
        _frames++;
        double now = Clock.Elapsed.TotalSeconds;
        if (now - _lastReport < 10 || _frames == 0)
            return;
        double frameMs = (now - _lastReport) * 1000 / _frames;
        var parts = Ticks.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} {kv.Value * 1000.0 / Stopwatch.Frequency / _frames:0.00}");
        Summary = $"frame {frameMs:0.0} ms ({1000 / frameMs:0} fps): " + string.Join(", ", parts) + $" ms; {BuildingRenderer.LastDrawn} instances, lod {BuildingRenderer.LodScale:0.00}";
        Log.Info("perf: " + Summary);
        Ticks.Clear();
        _frames = 0;
        _lastReport = now;
    }
}
