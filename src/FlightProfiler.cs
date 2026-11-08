using System.Diagnostics;
using System.Globalization;
using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>
/// Debug tool for test flights (menu GEE > Flight profiler, or AutoShot "profile start|stop"): records the real frame
/// time, altitude, speed and the mod's main-thread cost per section for every frame, then reports fps per altitude
/// band and the "slow phases" (stretches below the fps threshold, with altitude range and the sections that cost the
/// most there). Output goes to profiles/flight_STAMP.txt + .csv in the mod folder and to the game log.
///
/// Reading a report: when "mod ms" is small next to "frame ms" the time is spent in the game or on the GPU (shadow
/// maps, terrain, clouds, the mod's draw calls); when a section of the mod dominates it is a CPU cost of the mod.
/// </summary>
internal static class FlightProfiler
{
    private const double BucketSeconds = 0.5;
    private const int MaxFrames = 400_000;

    /// <summary>Altitude band edges in metres; a band is [edge[i], edge[i+1]).</summary>
    private static readonly double[] Edges = { double.NegativeInfinity, 100, 500, 2_000, 5_000, 10_000, 20_000, 40_000, 80_000, 150_000, 400_000, double.PositiveInfinity };

    private struct Sample { public float Ms, Alt, ModMs; }

    private sealed class Bucket
    {
        public double T0, T1;
        public int Frames;
        public double Sum, Worst, ModSum, AltMin = double.MaxValue, AltMax = double.MinValue, SpeedMax, GcMs;
        public int Instances, Near, Far, Walls, Viewports, Other;
        public readonly Dictionary<string, double> Sections = new();
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<Sample> Frames = new();
    private static readonly List<Bucket> Buckets = new();
    private static Bucket? _bucket;
    private static double _startedAt, _lastFrameAt;
    private static double3 _lastCcf;
    private static bool _haveLast;
    private static TimeSpan _lastGcPause;
    private static string _name = "";
    private static readonly float[] Plot = new float[240];
    private static int _plotAt;

    public static bool Recording { get; private set; }
    public static bool ShowWindow;
    public static double ThresholdFps = 30;
    public static string LastReportPath = "";
    public static string LastSummary = "";
    public static double Altitude { get; private set; } = double.NaN;
    public static double Speed { get; private set; }
    public static double FrameMs { get; private set; }

    public static void Start(string name = "")
    {
        Frames.Clear();
        Buckets.Clear();
        _bucket = null;
        _haveLast = false;
        _name = name;
        _startedAt = Clock.Elapsed.TotalSeconds;
        _lastGcPause = GC.GetTotalPauseDuration();
        Recording = true;
        Log.Notice($"flight profiler: recording started {name}");
    }

    public static void Stop(string dir)
    {
        AbortProbe();
        if (!Recording)
            return;
        Recording = false;
        Flush();
        try
        {
            Report(dir);
        }
        catch (Exception e)
        {
            Log.Error($"flight profiler: report failed: {e}");
        }
    }

    /// <summary>Once per frame from OnFrame. sections = the mod's main-thread ticks of the previous frame.</summary>
    public static void Frame(double dtPlayer, Celestial? body, Camera camera, IReadOnlyDictionary<string, long> sections)
    {
        double now = Clock.Elapsed.TotalSeconds;
        double dt = _lastFrameAt > 0 ? now - _lastFrameAt : 0;
        _lastFrameAt = now;
        if (body != null)
        {
            double3 ccf = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
            Altitude = ccf.Length() - body.MeanRadius;
            Speed = _haveLast && dtPlayer > 1e-6 ? (ccf - _lastCcf).Length() / dtPlayer : 0;
            _lastCcf = ccf;
            _haveLast = true;
        }
        if (dt <= 0 || dt > 5)
            return;
        FrameMs = dt * 1000;
        Plot[_plotAt++ % Plot.Length] = (float)FrameMs;
        if (_probeStep >= 0)
            ProbeFrame(now);
        if (!Recording || double.IsNaN(Altitude))
            return;

        double modMs = 0;
        foreach (var kv in sections)
            if (!kv.Key.StartsWith(' '))
                modMs += kv.Value * 1000.0 / Stopwatch.Frequency;
        if (Frames.Count < MaxFrames)
            Frames.Add(new Sample { Ms = (float)FrameMs, Alt = (float)Altitude, ModMs = (float)modMs });

        double t = now - _startedAt;
        if (_bucket == null || t - _bucket.T0 >= BucketSeconds)
        {
            Flush();
            _bucket = new Bucket { T0 = t };
        }
        var b = _bucket;
        b.T1 = t;
        b.Frames++;
        b.Sum += FrameMs;
        b.Worst = Math.Max(b.Worst, FrameMs);
        b.ModSum += modMs;
        b.AltMin = Math.Min(b.AltMin, Altitude);
        b.AltMax = Math.Max(b.AltMax, Altitude);
        b.SpeedMax = Math.Max(b.SpeedMax, Speed);
        b.Instances = Math.Max(b.Instances, BuildingRenderer.LastDrawn);
        b.Near = Math.Max(b.Near, BuildingRenderer.LastNear);
        b.Far = Math.Max(b.Far, BuildingRenderer.LastFar);
        b.Walls = Math.Max(b.Walls, FacadeRenderer.LastBoxes);
        // Render calls and other-viewport instances since the last frame (then counted again from 0).
        b.Viewports = Math.Max(b.Viewports, BuildingRenderer.Viewports);
        b.Other = Math.Max(b.Other, BuildingRenderer.LastOther);
        BuildingRenderer.Viewports = 0;
        BuildingRenderer.LastOther = 0;
        foreach (var kv in sections)
            b.Sections[kv.Key] = b.Sections.GetValueOrDefault(kv.Key) + kv.Value * 1000.0 / Stopwatch.Frequency;
        var pause = GC.GetTotalPauseDuration();
        b.GcMs += (pause - _lastGcPause).TotalMilliseconds;
        _lastGcPause = pause;
    }

    private static void Flush()
    {
        if (_bucket is { Frames: > 0 })
            Buckets.Add(_bucket);
        _bucket = null;
    }

    // ---- report ----

    private static string F(double v, string format = "0.0") => v.ToString(format, CultureInfo.InvariantCulture);

    private static string BandName(int i)
    {
        string Edge(double m) => m >= 1000 ? $"{m / 1000:0.#} km" : $"{m:0} m";
        if (i == 0) return $"< {Edge(Edges[1])}";
        if (i == Edges.Length - 2) return $">= {Edge(Edges[i])}";
        return $"{Edge(Edges[i])} - {Edge(Edges[i + 1])}";
    }

    private static string TopSections(IEnumerable<Bucket> buckets, int frames, int take)
    {
        var sum = new Dictionary<string, double>();
        foreach (var b in buckets)
            foreach (var kv in b.Sections)
                sum[kv.Key] = sum.GetValueOrDefault(kv.Key) + kv.Value;
        var top = sum.Where(kv => !kv.Key.StartsWith(' ')).OrderByDescending(kv => kv.Value).Take(take)
            .Select(kv => $"{kv.Key} {F(kv.Value / Math.Max(1, frames), "0.00")}");
        string text = string.Join(", ", top);
        return text.Length > 0 ? text : "-";
    }

    private sealed record Phase(double T0, double T1, double AltMin, double AltMax, int Frames, double AvgFps, double WorstMs, double ModMs, double GcMs, int Instances, string Sections, double SpeedMax);

    /// <summary>Stretches of at least MinSeconds where each half-second bucket is below the fps threshold.</summary>
    private static List<Phase> SlowPhases(double thresholdFps, double minSeconds = 1.0)
    {
        var phases = new List<Phase>();
        var run = new List<Bucket>();
        void Close()
        {
            if (run.Count > 0 && run[^1].T1 - run[0].T0 >= minSeconds - 1e-9)
            {
                int frames = run.Sum(b => b.Frames);
                double ms = run.Sum(b => b.Sum);
                phases.Add(new Phase(run[0].T0, run[^1].T1, run.Min(b => b.AltMin), run.Max(b => b.AltMax), frames, 1000 * frames / ms,
                    run.Max(b => b.Worst), run.Sum(b => b.ModSum) / frames, run.Sum(b => b.GcMs), run.Max(b => b.Instances),
                    TopSections(run, frames, 4), run.Max(b => b.SpeedMax)));
            }
            run.Clear();
        }
        foreach (var b in Buckets)
        {
            double fps = 1000 * b.Frames / Math.Max(1e-6, b.Sum);
            if (fps < thresholdFps)
                run.Add(b);
            else
                Close();
        }
        Close();
        return phases;
    }

    private static string AltText(double m) => Math.Abs(m) >= 10_000 ? $"{m / 1000:0.0} km" : $"{m:0} m";

    private static string BuildSummary(out List<Phase> phases)
    {
        var sb = new StringBuilder();
        int total = Frames.Count;
        double seconds = Buckets.Count > 0 ? Buckets[^1].T1 : 0;
        sb.AppendLine($"Flight profile {_name} {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {total} frames in {F(seconds)} s, average {F(total / Math.Max(1e-6, seconds))} fps, threshold {F(ThresholdFps, "0")} fps");
        sb.AppendLine();
        sb.AppendLine("By altitude band (fps = frames / time; low 1% = fps of the slowest 1% of frames):");
        sb.AppendLine($"{"band",-22}{"frames",8}{"seconds",9}{"avg fps",9}{"low 1%",8}{"worst ms",10}{"mod ms",8}");
        for (int i = 0; i < Edges.Length - 1; i++)
        {
            var inBand = Frames.Where(f => f.Alt >= Edges[i] && f.Alt < Edges[i + 1]).ToList();
            if (inBand.Count == 0)
                continue;
            double ms = inBand.Sum(f => f.Ms);
            var sorted = inBand.Select(f => f.Ms).OrderBy(x => x).ToList();
            double p99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
            sb.AppendLine($"{BandName(i),-22}{inBand.Count,8}{F(ms / 1000),9}{F(1000 * inBand.Count / ms),9}{F(1000 / p99, "0"),8}{F(sorted[^1]),10}{F(inBand.Average(f => f.ModMs), "0.00"),8}");
        }
        phases = SlowPhases(ThresholdFps);
        sb.AppendLine();
        if (phases.Count == 0)
            sb.AppendLine($"No stretch of 1 s or more below {F(ThresholdFps, "0")} fps.");
        else
        {
            sb.AppendLine($"Slow phases (>= 1 s below {F(ThresholdFps, "0")} fps), worst first:");
            foreach (var p in phases.OrderBy(p => p.AvgFps))
                sb.AppendLine($"  t {F(p.T0)}-{F(p.T1)} s, alt {AltText(p.AltMin)} - {AltText(p.AltMax)}, speed up to {F(p.SpeedMax, "0")} m/s: {F(p.AvgFps)} fps (worst frame {F(p.WorstMs, "0")} ms), mod {F(p.ModMs, "0.00")} ms/frame, GC {F(p.GcMs, "0")} ms, {p.Instances} instances; top mod sections ms/frame: {p.Sections}");
        }
        sb.AppendLine();
        sb.AppendLine($"Mod sections over the whole flight, ms/frame: {TopSections(Buckets, Math.Max(1, total), 8)}");
        return sb.ToString();
    }

    private static void Report(string dir)
    {
        string folder = Path.Combine(dir, "profiles");
        Directory.CreateDirectory(folder);
        string stem = $"flight_{DateTime.Now:yyyyMMdd_HHmmss}{(_name.Length > 0 ? "_" + _name : "")}";
        string summary = BuildSummary(out _);
        File.WriteAllText(Path.Combine(folder, stem + ".txt"), summary);

        var sections = Buckets.SelectMany(b => b.Sections.Keys).Where(k => !k.StartsWith(' ')).Distinct().OrderBy(k => k).ToList();
        var csv = new StringBuilder();
        csv.Append("t_s,alt_min_m,alt_max_m,speed_max_mps,frames,fps,avg_ms,worst_ms,mod_ms,gc_ms,instances,near,far,walls,viewports,other_viewport_instances");
        foreach (string s in sections)
            csv.Append(',').Append(s.Replace(',', ' ')).Append("_ms");
        csv.AppendLine();
        foreach (var b in Buckets)
        {
            csv.Append(F(b.T0, "0.00")).Append(',').Append(F(b.AltMin, "0")).Append(',').Append(F(b.AltMax, "0")).Append(',').Append(F(b.SpeedMax, "0.0"))
                .Append(',').Append(b.Frames).Append(',').Append(F(1000 * b.Frames / b.Sum)).Append(',').Append(F(b.Sum / b.Frames, "0.00"))
                .Append(',').Append(F(b.Worst, "0.0")).Append(',').Append(F(b.ModSum / b.Frames, "0.00")).Append(',').Append(F(b.GcMs, "0")).Append(',').Append(b.Instances)
                .Append(',').Append(b.Near).Append(',').Append(b.Far).Append(',').Append(b.Walls).Append(',').Append(b.Viewports).Append(',').Append(b.Other);
            foreach (string s in sections)
                csv.Append(',').Append(F(b.Sections.GetValueOrDefault(s) / b.Frames, "0.000"));
            csv.AppendLine();
        }
        File.WriteAllText(Path.Combine(folder, stem + ".csv"), csv.ToString());
        LastReportPath = Path.Combine(folder, stem + ".txt");
        LastSummary = summary;
        foreach (string line in summary.Split('\n'))
            if (line.Trim().Length > 0)
                Log.Notice("flight profile: " + line.TrimEnd('\r'));
        Log.Notice($"flight profiler: report written to {LastReportPath}");
    }

    // ---- GPU probe ----
    // The mod's main-thread cost is measured per section, its GPU cost is not. The probe switches one runtime feature
    // off at a time (camera held still), waits for the frame time to settle and measures it, then switches it back:
    // the difference to the baseline is what that feature costs. Shader patches baked at game start (ground tint and
    // texture, grass boost, lake beds) cannot be switched; "everything" turns off all the mod draws at once, so a
    // baseline barely above it means the cost is in the game itself or in those baked patches.

    public static Settings? Settings;
    private static readonly (string Property, string Label)[] ProbeFeatures =
    {
        ("Enabled", "everything the mod draws"), ("ShowBuildings", "buildings"), ("FarBuildings", "far-field buildings"),
        ("ShowRoads", "roads"), ("Windows", "window shading"), ("BuildingShadows", "building shadows"),
        ("SatelliteImagery", "satellite drape"), ("Water", "lakes (extra ocean passes)"), ("CityGlow", "city glow"),
        ("TankVapor", "tank vapour"), ("NightLights", "night point lights"),
    };
    private const double ProbeSettle = 1.2, ProbeMeasure = 2.0;
    private static int _probeStep = -1;
    private static double _probeStepAt, _probeSum, _probeWorst;
    private static int _probeFrames;
    private static System.Reflection.PropertyInfo? _probeProp;
    private static bool _probeOld;
    private static readonly List<string> ProbeLines = new();
    private static double _probeBaseMs;
    private static string _probeDir = "";
    public static string ProbeStatus = "";

    public static bool Probing => _probeStep >= 0;

    public static void StartProbe()
    {
        if (Settings == null || Probing)
            return;
        ProbeLines.Clear();
        _probeStep = 0;
        BeginProbeStep(Clock.Elapsed.TotalSeconds);
        Log.Notice("gpu probe: started");
    }

    private static void BeginProbeStep(double now)
    {
        _probeStepAt = now;
        _probeSum = _probeWorst = 0;
        _probeFrames = 0;
        _probeProp = null;
        int feature = _probeStep - 1;
        if (feature >= 0 && feature < ProbeFeatures.Length)
        {
            _probeProp = typeof(Settings).GetProperty(ProbeFeatures[feature].Property);
            _probeOld = _probeProp?.GetValue(Settings) is true;
            if (_probeProp == null || !_probeOld)
            {
                ProbeLines.Add($"  without {ProbeFeatures[feature].Label,-30} (already off)");
                _probeProp = null;
                _probeStep++;
                BeginProbeStep(now);
                return;
            }
            _probeProp.SetValue(Settings, false);
        }
        ProbeStatus = $"probe step {_probeStep + 1}/{ProbeFeatures.Length + 2}: {ProbeLabel()}";
    }

    private static string ProbeLabel() => _probeStep == 0 ? "baseline (all on)"
        : _probeStep <= ProbeFeatures.Length ? "without " + ProbeFeatures[_probeStep - 1].Label : "baseline again";

    private static void ProbeFrame(double now)
    {
        double t = now - _probeStepAt;
        if (t >= ProbeSettle)
        {
            _probeSum += FrameMs;
            _probeFrames++;
            _probeWorst = Math.Max(_probeWorst, FrameMs);
        }
        if (t < ProbeSettle + ProbeMeasure)
            return;
        RestoreProbeSetting();
        double ms = _probeSum / Math.Max(1, _probeFrames);
        if (_probeStep == 0)
            _probeBaseMs = ms;
        string saved = _probeStep == 0 ? "" : $"  {(_probeBaseMs - ms >= 0 ? "saves" : "costs")} {Math.Abs(_probeBaseMs - ms):0.0} ms";
        ProbeLines.Add($"  {ProbeLabel(),-38} {F(1000 / Math.Max(ms, 1e-3)),6} fps  {F(ms, "0.0"),6} ms avg  {F(_probeWorst, "0.0"),6} ms worst{saved}");
        _probeStep++;
        if (_probeStep > ProbeFeatures.Length + 1)
        {
            FinishProbe();
            return;
        }
        BeginProbeStep(now);
    }

    private static void RestoreProbeSetting()
    {
        if (_probeProp != null)
            _probeProp.SetValue(Settings, _probeOld);
        _probeProp = null;
    }

    private static void AbortProbe()
    {
        if (!Probing)
            return;
        RestoreProbeSetting();
        _probeStep = -1;
        ProbeStatus = "probe aborted";
    }

    private static void FinishProbe()
    {
        _probeStep = -1;
        var sb = new StringBuilder();
        sb.AppendLine($"GPU probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}, altitude {AltText(Altitude)}: each line is one mod feature switched off for {ProbeMeasure:0} s (camera held still).");
        foreach (string line in ProbeLines)
            sb.AppendLine(line);
        sb.AppendLine("Not switchable at runtime (baked at game start): ground tint/texture/detail, grass boost, lake beds, scatter/tree clutter.");
        string summary = sb.ToString();
        try
        {
            string folder = Path.Combine(_probeDir, "profiles");
            Directory.CreateDirectory(folder);
            LastReportPath = Path.Combine(folder, $"probe_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(LastReportPath, summary);
        }
        catch (Exception e)
        {
            Log.Error($"gpu probe: report failed: {e.Message}");
        }
        LastSummary = summary;
        foreach (string line in summary.Split((char)10))
            if (line.Trim().Length > 0)
                Log.Notice("gpu probe: " + line.TrimEnd((char)13));
        ProbeStatus = "probe done";
    }

    // ---- window ----

    public static void DrawWindow(string dir)
    {
        _probeDir = dir;
        if (!ShowWindow)
            return;
        ImGui.SetNextWindowSize(new float2(520, 0), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("GEE - Flight profiler", ref open))
        {
            ImGui.Text(double.IsNaN(Altitude) ? "Waiting for the camera..."
                : $"Altitude {AltText(Altitude)}   speed {Speed:0} m/s   {FrameMs:0.0} ms ({(FrameMs > 0 ? 1000 / FrameMs : 0):0} fps)");
            ImGui.PlotLines("##frametime", Plot, _plotAt % Plot.Length, default, 0f, 66f, new float2(500, 70), 4);
            if (!Recording)
            {
                if (ImGui.Button("Start recording"))
                    Start();
                ImGui.SameLine();
                ImGui.TextDisabled("Fly from the pad to orbit, then press Stop.");
            }
            else
            {
                if (ImGui.Button("Stop and save report"))
                    Stop(dir);
                ImGui.SameLine();
                double seconds = _bucket?.T1 ?? (Buckets.Count > 0 ? Buckets[^1].T1 : 0);
                ImGui.Text($"Recording {seconds:0} s, {Frames.Count} frames");
            }
            if (!Probing)
            {
                if (ImGui.Button("GPU probe (hold the view still, ~40 s)"))
                    StartProbe();
                ImGui.SameLine();
                ImGui.TextDisabled("switches mod features off one by one");
            }
            else
            {
                if (ImGui.Button("Abort probe"))
                    AbortProbe();
                ImGui.SameLine();
                ImGui.Text(ProbeStatus);
            }
            float threshold = (float)ThresholdFps;
            if (ImGui.SliderFloat("Slow below (fps)", ref threshold, 10f, 120f, "%.0f"))
                ThresholdFps = threshold;
            if (LastReportPath.Length > 0)
            {
                ImGui.Separator();
                ImGui.TextWrapped("Report: " + LastReportPath);
                ImGui.Text(LastSummary.Length > 3000 ? LastSummary[..3000] : LastSummary);
            }
        }
        ImGui.End();
        if (!open)
            ShowWindow = false;
    }
}
