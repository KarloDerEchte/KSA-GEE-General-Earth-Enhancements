using System.Diagnostics;

namespace GEE;

/// <summary>
/// Main-thread time spent in the mod's parts, summed per section and written to the log every 10 s as milliseconds
/// per frame, so slow parts show up in a test run.
/// </summary>
internal static class Perf
{
    private static readonly Dictionary<string, long> Ticks = new();
    private static readonly Dictionary<string, long> FrameTicks = new(), MaxTicks = new();
    private static long _worstFrame;
    private static int _gc0, _gc1, _gc2;
    private static TimeSpan _gcPause;
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
            FrameTicks[name] = FrameTicks.GetValueOrDefault(name) + dt;
        }
    }

    /// <summary>Called once per frame (OnFrame).</summary>
    public static void Frame()
    {
        _frames++;
        long frameSum = 0;
        foreach (var kv in FrameTicks)
        {
            if (kv.Value > MaxTicks.GetValueOrDefault(kv.Key))
                MaxTicks[kv.Key] = kv.Value;
            if (!kv.Key.StartsWith(' '))
                frameSum += kv.Value;
        }
        _worstFrame = Math.Max(_worstFrame, frameSum);
        FrameTicks.Clear();
        double now = Clock.Elapsed.TotalSeconds;
        if (now - _lastReport < 10 || _frames == 0)
            return;
        double frameMs = (now - _lastReport) * 1000 / _frames;
        var parts = Ticks.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} {kv.Value * 1000.0 / Stopwatch.Frequency / _frames:0.00}");
        Summary = $"frame {frameMs:0.0} ms ({1000 / frameMs:0} fps): " + string.Join(", ", parts) + $" ms; {BuildingRenderer.LastDrawn} instances, lod {BuildingRenderer.LodScale:0.00}";
        Log.Info("perf: " + Summary);
        var worst = MaxTicks.OrderByDescending(kv => kv.Value).Take(6)
            .Select(kv => $"{kv.Key.Trim()} {kv.Value * 1000.0 / Stopwatch.Frequency:0.0}");
        Log.Info($"perf worst single frame: mod total {_worstFrame * 1000.0 / Stopwatch.Frequency:0.0} ms; " + string.Join(", ", worst) + " ms");
        Ticks.Clear();
        MaxTicks.Clear();
        _worstFrame = 0;
        var pause = GC.GetTotalPauseDuration();
        Log.Info($"perf gc: gen0 {GC.CollectionCount(0) - _gc0}, gen1 {GC.CollectionCount(1) - _gc1}, gen2 {GC.CollectionCount(2) - _gc2}, paused {(pause - _gcPause).TotalMilliseconds:0} ms in 10 s");
        _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2); _gcPause = pause;
        _frames = 0;
        _lastReport = now;
    }
}
