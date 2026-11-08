using Brutal.GlfwApi;
using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>
/// Development aid: plays a script from autoshot.txt in the mod folder (once; it is renamed to autoshot.done
/// when it ends), so screenshots can be taken without anyone at the keyboard. Every line is a console command of
/// the game (load, goto, altitude, camera, simspeed, exit ...) or one of:
///   wait SECONDS          pause
///   sunel DEGREES         warp time until the sun stands at that elevation above the camera
///   shot NAME             take a screenshot and copy it to shots/NAME.png in the mod folder
///   crewarm dock|undock|finish  move the LC-39A crew access arm (finish: skip the swing)
///   view LAT LON ALT TLAT TLON TALT   free camera at a spot looking at a target
///   log TEXT              write a line to the game log
///   waitidle [TIMEOUT_S=60]  wait until the mod's background work is idle (satellite tiles + heights, elevation,
///                         near/far/water/regional/global tile queues all empty) and has stayed so for ~1 s;
///                         logs "AutoShot: idle after X s" or "AutoShot: waitidle timeout"
///   waitlog TEXT [TIMEOUT_S=60]  wait until a game log line containing TEXT has been written (the log file
///                         Logs/*.PID.log of this process is tailed; a trailing number is the timeout, so a TEXT
///                         ending in a number needs an explicit timeout); logs "AutoShot: waitlog timeout" on failure
///   assertlog TEXT        record a pass/fail: some log line seen so far contains TEXT
///   assertnolog TEXT      record a pass/fail: no log line seen so far contains TEXT
///   record NAME N [S]     N screenshots NAME_001.. with S seconds between them (a "video" as frames)
///   clouds off|on         hide/show volumetric clouds and cloud shadows (sets GameSettings.Current.Graphics
///                         Clouds/CloudShadows; stays until changed or the game restarts)
///   ascend LAT LON FROM_ALT TO_ALT SECONDS   scripted climb of the free camera (metres, exponential), eastward ground track
///   spawn ROCKET [BODY LOCATION]   spawn a saved rocket (RocketSpawnApi), then `control ID` / `follow ID`
///   vinput ACTION [press|release|tap]   key action for the controlled vehicle (MainEngineStartup, MainEngineThrottleUp, CameraUp = stage, Forward/Backward = pitch)
///   profile start [NAME] | stop   flight profiler (FlightProfiler.cs): record fps per altitude, write profiles/flight_*.txt/.csv
///   quit                  write shots/results.json and shots/DONE (shot count, timestamp), then Environment.Exit(0)
/// If the first line of the script is "#autoquit", quit runs by itself when the script ends (also after an error).
/// shot NAME also writes shots/NAME.jpg (1280 px wide, quality 88) next to the full PNG.
/// shots/results.json: {"asserts":[{"name","ok"}], "shots":[names], "errors":[log lines containing "Exception"]}.
/// Lines starting with # are comments.
/// </summary>
internal static class AutoShot
{
    private enum Step { Next, Wait, Sun, ShotWait, Idle, LogWait, Ascend }

    // ascend: LAT LON FROM_ALT TO_ALT SECONDS
    private static double[] _ascend = new double[5];
    private static double _ascendT;

    private static string[]? _lines;
    private static int _next;
    private static Step _step = Step.Next;
    private static string? _view;
    private static double _timer;
    private static double _target;
    private static string _shotName = "";
    private static HashSet<string> _before = new();
    private static bool _checked;
    private static bool _autoQuit, _quitting;
    private static string _dir = "";
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static double _stepStart, _idleSince = -1, _waitTimeout;
    private static string _waitText = "";
    private static readonly List<string> Shots = new();
    private static readonly List<(string Name, bool Ok)> Asserts = new();
    private static readonly List<string> Errors = new();
    // Log tail: every line of this process's game log file seen so far.
    private static readonly List<string> LogLines = new();
    private static string? _logPath;
    private static long _logPos;
    private static string _logCarry = "";
    private static double _logPolledAt = -1;

    /// <summary>Set by the mod: number of unfinished background jobs of the tile/terrain loaders (0 = idle).</summary>
    public static Func<int>? BusyProbe;

    private static string ScreenshotsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Kitten Space Agency", "exports", "screenshots");

    public static void Tick(double dt, string modDir, string bodyId, string sunId)
    {
        if (_lines == null)
        {
            if (_checked)
                return;
            _checked = true;
            string path = Path.Combine(modDir, "autoshot.txt");
            if (!File.Exists(path))
                return;
            _lines = ExpandRecords(File.ReadAllLines(path));
            _dir = modDir;
            _autoQuit = _lines.Length > 0 && _lines[0].Trim().Equals("#autoquit", StringComparison.OrdinalIgnoreCase);
            File.Move(path, Path.Combine(modDir, "autoshot.done"), true);
            Log.Info($"autoshot: {_lines.Length} lines");
        }
        try
        {
            // A view is held every frame until the next camera command: set once, the free camera drifts away.
            if (_view != null)
                View(_view, bodyId);
            PollLog();
            Run(dt, modDir, bodyId, sunId);
        }
        catch (Exception e)
        {
            Log.Error($"autoshot failed: {e}");
            _next = _lines.Length;
            _step = Step.Next;
            if (_autoQuit)
                Quit();
        }
    }

    /// <summary>
    /// "record NAME COUNT [INTERVAL_S=1]" becomes COUNT x ("shot NAME_001", "wait INTERVAL_S"): a frame sequence for
    /// watching motion (launch, camera moves, time warp). tools/postprocess.py turns NAME_NNN into a filmstrip, a GIF
    /// and, when ffmpeg is installed, an mp4. Put camera/simspeed commands before it; the real interval is the wait plus
    /// the screenshot time (about 1 s).
    /// </summary>
    private static string[] ExpandRecords(string[] lines)
    {
        var result = new List<string>(lines.Length);
        foreach (string raw in lines)
        {
            string[] w = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length >= 3 && w[0] == "record" && int.TryParse(w[2], out int count))
            {
                string interval = w.Length > 3 ? w[3] : "1";
                for (int i = 1; i <= Math.Min(count, 999); i++)
                {
                    result.Add($"shot {w[1]}_{i:000}");
                    result.Add($"wait {interval}");
                }
            }
            else
                result.Add(raw);
        }
        return result.ToArray();
    }

    // ---- log tail ----

    private static void PollLog()
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (now - _logPolledAt < 0.2)
            return;
        _logPolledAt = now;
        try
        {
            if (_logPath == null)
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Kitten Space Agency", "Logs");
                if (!Directory.Exists(folder))
                    return;
                _logPath = Directory.GetFiles(folder, $"*.{Environment.ProcessId}.log").FirstOrDefault();
                if (_logPath == null)
                    return;
            }
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= _logPos)
                return;
            stream.Position = _logPos;
            var buffer = new byte[stream.Length - _logPos];
            int n = stream.Read(buffer, 0, buffer.Length);
            _logPos += n;
            string text = _logCarry + System.Text.Encoding.UTF8.GetString(buffer, 0, n);
            string[] parts = text.Split('\n');
            _logCarry = parts[^1];
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string l = parts[i].TrimEnd('\r');
                LogLines.Add(l);
                if (l.Contains("Exception", StringComparison.Ordinal) && !l.Contains("AutoShot:", StringComparison.OrdinalIgnoreCase)
                    && !l.Contains("autoshot: >", StringComparison.OrdinalIgnoreCase) && Errors.Count < 500)
                    Errors.Add(l.Length > 600 ? l[..600] : l);
            }
        }
        catch (IOException)
        {
        }
    }

    private static bool LogContains(string text) => LogLines.Any(l => l.Contains(text, StringComparison.Ordinal));

    // ---- quit / results ----

    private static string Q(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private static void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        try
        {
            _logPolledAt = -1;
            PollLog();
            string shots = Path.Combine(_dir, "shots");
            Directory.CreateDirectory(shots);
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"asserts\":[").Append(string.Join(",", Asserts.Select(a => $"{{\"name\":{Q(a.Name)},\"ok\":{(a.Ok ? "true" : "false")}}}")));
            sb.Append("],\"shots\":[").Append(string.Join(",", Shots.Select(Q)));
            sb.Append("],\"errors\":[").Append(string.Join(",", Errors.Select(Q))).Append("]}");
            File.WriteAllText(Path.Combine(shots, "results.json"), sb.ToString());
            File.WriteAllText(Path.Combine(shots, "DONE"), $"shots={Shots.Count} time={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            Log.Notice($"AutoShot: quit, {Shots.Count} shots");
        }
        catch (Exception e)
        {
            Log.Error($"AutoShot: quit failed to write results: {e.Message}");
        }
        Environment.Exit(0);
    }

    /// <summary>Writes the PNG as a JPG 1280 px wide (box filter). False when it could not be done.</summary>
    private static unsafe bool WriteJpg(string source, string target)
    {
        using var image = Brutal.StbApi.Stb.Load(source, 3);
        if (image.Data == null || image.Width <= 0)
            return false;
        int w = image.Width, h = image.Height, ow = Math.Min(1280, w), oh = Math.Max(1, (int)((long)h * ow / w));
        var output = new byte[ow * oh * 3];
        byte* src = image.Data;
        for (int y = 0; y < oh; y++)
        {
            int y0 = (int)((long)y * h / oh), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * h / oh));
            for (int x = 0; x < ow; x++)
            {
                int x0 = (int)((long)x * w / ow), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * w / ow));
                int r = 0, g = 0, b = 0, c = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        byte* p = src + ((long)yy * w + xx) * 3;
                        r += p[0]; g += p[1]; b += p[2]; c++;
                    }
                int o = (y * ow + x) * 3;
                output[o] = (byte)(r / c); output[o + 1] = (byte)(g / c); output[o + 2] = (byte)(b / c);
            }
        }
        return Brutal.StbApi.Stb.WriteJpg(target, ow, oh, 3, output, 88);
    }

    private static void Run(double dt, string modDir, string bodyId, string sunId)
    {
        switch (_step)
        {
            case Step.Ascend:
            {
                // Scripted ascent for fps tests: altitude grows exponentially from FROM to TO, the ground track runs east
                // (about 1.2 m downrange per metre of altitude up to 150 km, then orbital speed), the camera looks ahead and down.
                _ascendT += dt;
                double u = Math.Clamp(_ascendT / _ascend[4], 0, 1);
                double alt = _ascend[2] * Math.Pow(_ascend[3] / _ascend[2], u);
                double down = alt < 150_000 ? 1.2 * alt : 180_000 + (alt - 150_000) * 30;
                double lat = _ascend[0], lon = _ascend[1] + down / (6_371_000 * Math.Cos(lat * Math.PI / 180)) * 180 / Math.PI;
                double ahead = (down + Math.Max(3000, alt * 3)) / (6_371_000 * Math.Cos(lat * Math.PI / 180)) * 180 / Math.PI;
                _view = FormattableString.Invariant($"{lat} {lon} {alt} {lat} {_ascend[1] + ahead} 0");
                View(_view, bodyId);
                if (u >= 1)
                    _step = Step.Next;
                return;
            }
            case Step.Wait:
                if ((_timer -= dt) <= 0)
                    _step = Step.Next;
                return;
            case Step.Sun:
                if (SunElevation(bodyId, sunId) is not { } elevation)
                    return;
                double delta = Math.Abs(elevation - _target);
                // Gives up after 3 real minutes: a target above the day's highest sun is never reached.
                if (_timer == 0)
                    _stepStart = Clock.Elapsed.TotalSeconds;
                _timer = 1;
                bool reached = delta < 0.4;
                if (reached || Clock.Elapsed.TotalSeconds - _stepStart > 180)
                {
                    Program.TerminalInterface.Execute("simspeed 1");
                    Log.Notice(FormattableString.Invariant($"AutoShot: sun at {elevation:0.0} degrees (target {_target:0.0}): {(reached ? "reached" : "TIMEOUT")}"));
                    _step = Step.Next;
                    return;
                }
                Program.TerminalInterface.Execute($"simspeed {Math.Clamp(delta * 1500, 20, 20000):0}");
                return;
            case Step.Idle:
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (BusyProbe?.Invoke() is not > 0)
                {
                    if (_idleSince < 0)
                        _idleSince = now;
                }
                else
                    _idleSince = -1;
                if (_idleSince >= 0 && now - _idleSince >= 1)
                {
                    Log.Notice($"AutoShot: idle after {now - _stepStart:0.0} s");
                    _step = Step.Next;
                }
                else if (now - _stepStart > _waitTimeout)
                {
                    Log.Notice("AutoShot: waitidle timeout");
                    _step = Step.Next;
                }
                return;
            }
            case Step.LogWait:
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (LogContains(_waitText))
                {
                    Log.Notice($"AutoShot: waitlog found after {now - _stepStart:0.0} s");
                    _step = Step.Next;
                }
                else if (now - _stepStart > _waitTimeout)
                {
                    Log.Notice($"AutoShot: waitlog timeout ({_waitText})");
                    _step = Step.Next;
                }
                return;
            }
            case Step.ShotWait:
                _timer -= dt;
                string? file = Directory.Exists(ScreenshotsFolder)
                    ? Directory.GetFiles(ScreenshotsFolder).FirstOrDefault(f => !_before.Contains(f))
                    : null;
                if (file != null)
                {
                    Thread.Sleep(300);
                    string shots = Path.Combine(modDir, "shots");
                    Directory.CreateDirectory(shots);
                    File.Copy(file, Path.Combine(shots, _shotName + Path.GetExtension(file)), true);
                    Shots.Add(_shotName);
                    try
                    {
                        if (!WriteJpg(file, Path.Combine(shots, _shotName + ".jpg")))
                            Log.Error($"AutoShot: jpg of {_shotName} failed");
                    }
                    catch (Exception e)
                    {
                        Log.Error($"AutoShot: jpg of {_shotName} failed: {e.Message}");
                    }
                    Log.Info($"autoshot: saved {_shotName}");
                    _step = Step.Next;
                }
                else if (_timer <= 0)
                {
                    Log.Error($"autoshot: screenshot {_shotName} did not appear");
                    _step = Step.Next;
                }
                return;
        }

        while (_next < _lines!.Length && _step == Step.Next)
        {
            string line = _lines[_next++].Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] words = line.Split(' ', 2);
            string argument = words.Length > 1 ? words[1].Trim() : "";
            switch (words[0])
            {
                case "wait":
                    _timer = double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                    _step = Step.Wait;
                    break;
                case "sunel":
                    _target = double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                    _timer = 0;
                    _step = Step.Sun;
                    break;
                case "shot":
                    _shotName = argument;
                    _before = Directory.Exists(ScreenshotsFolder) ? Directory.GetFiles(ScreenshotsFolder).ToHashSet() : new HashSet<string>();
                    _timer = 120;
                    Program.TerminalInterface.Execute("screenshot");
                    _step = Step.ShotWait;
                    break;
                case "waitidle":
                    _waitTimeout = argument.Length > 0 ? double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture) : 60;
                    _stepStart = Clock.Elapsed.TotalSeconds;
                    _idleSince = -1;
                    _step = Step.Idle;
                    break;
                case "waitlog":
                {
                    // A trailing number is the timeout in seconds.
                    string text = argument;
                    _waitTimeout = 60;
                    int sp = argument.LastIndexOf(' ');
                    if (sp > 0 && double.TryParse(argument[(sp + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t))
                    {
                        _waitTimeout = t;
                        text = argument[..sp].Trim();
                    }
                    _waitText = text;
                    _stepStart = Clock.Elapsed.TotalSeconds;
                    _logPolledAt = -1;
                    PollLog();
                    _step = Step.LogWait;
                    break;
                }
                case "assertlog":
                case "assertnolog":
                {
                    _logPolledAt = -1;
                    PollLog();
                    bool has = LogContains(argument);
                    bool ok = words[0] == "assertlog" ? has : !has;
                    Asserts.Add(($"{words[0]} {argument}", ok));
                    Log.Notice($"AutoShot: {words[0]} {argument}: {(ok ? "ok" : "FAILED")}");
                    break;
                }
                case "quit":
                    Quit();
                    break;
                case "crewarm":
                    // crewarm dock | undock | finish
                    if (argument == "dock")
                        CrewArm.Dock();
                    else if (argument == "undock")
                        CrewArm.Undock();
                    else
                        CrewArm.Finish();
                    break;
                case "clouds":
                    SetClouds(!argument.Equals("off", StringComparison.OrdinalIgnoreCase));
                    break;
                case "view":
                    _view = argument;
                    View(argument, bodyId);
                    break;
                case "spawn":
                    Spawn(argument);
                    break;
                case "vinput":
                    VehicleInput(argument);
                    break;
                case "ascend":
                    _ascend = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    _ascendT = 0;
                    _step = Step.Ascend;
                    break;
                case "profile":
                    GeeMod.Instance?.ProfileCommand(argument);
                    break;
                case "log":
                    Log.Info($"autoshot: {argument}");
                    break;
                default:
                    Log.Info($"autoshot: > {line}");
                    if (line.StartsWith("camera", StringComparison.Ordinal) || line.StartsWith("load", StringComparison.Ordinal))
                        _view = null;
                    Program.TerminalInterface.Execute(line);
                    break;
            }
        }
        if (_autoQuit && _step == Step.Next && _next >= _lines.Length)
            Quit();
    }

    /// <summary>
    /// view LAT LON ALT TLAT TLON TALT: puts the (free) camera at a place ALT metres above the ground and turns it
    /// to the target point, with the local vertical up.
    /// </summary>
    /// <summary>spawn ROCKET [BODY LOCATION]: spawns a saved rocket through the RocketSpawnApi mod (http://127.0.0.1:4260).</summary>
    private static void Spawn(string argument)
    {
        string[] w = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string url = "http://127.0.0.1:4260/v1/spawn?rocket=" + Uri.EscapeDataString(w[0]);
        if (w.Length > 2)
            url += "&body=" + Uri.EscapeDataString(w[1]) + "&location=" + Uri.EscapeDataString(w[2]);
        Log.Info($"autoshot: spawn {url}");
        var curl = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("curl.exe", $"-s -m 60 \"{url}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        if (curl != null)
            Task.Run(() =>
            {
                string answer = curl.StandardOutput.ReadToEnd().Replace('\n', ' ') + curl.StandardError.ReadToEnd();
                curl.WaitForExit(65000);
                Log.Notice($"AutoShot: spawn {w[0]} answered: {answer} (curl exit {(curl.HasExited ? curl.ExitCode : -1)})");
            });
    }

    /// <summary>vinput ACTION [press|release|tap]: sends a key action (MainEngineStartup, MainEngineThrottleUp, CameraUp = stage, Forward = pitch ...) to the controlled vehicle.</summary>
    private static void VehicleInput(string argument)
    {
        string[] w = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (Program.ControlledVehicle is not { } vehicle || !Enum.TryParse(w[0], true, out InputAction action))
        {
            Log.Error($"autoshot: vinput {argument}: no controlled vehicle or unknown action");
            return;
        }
        string mode = w.Length > 1 ? w[1].ToLowerInvariant() : "tap";
        void Send(GlfwKeyAction keyAction) => InputEvents.VehicleInputBuffer.Add(new InputEvents.VehicleInputData
        {
            Vehicle = vehicle, Action = action, KeyAction = keyAction, Modifiers = 0
        });
        if (mode != "release")
            Send(GlfwKeyAction.Press);
        if (mode != "press")
            Send(GlfwKeyAction.Release);
        Log.Info($"autoshot: vinput {action} {mode} on {vehicle.Id}");
    }

    private static bool _cloudsSaved;
    private static GameSettings.CloudShadows _savedShadows;

    /// <summary>Volumetric clouds + cloud shadows on/off via the live graphics settings (the console command
    /// renderclouds is only a per-body toggle and is not reliable).</summary>
    private static void SetClouds(bool on)
    {
        var g = GameSettings.Current.Graphics;
        if (!_cloudsSaved) { _savedShadows = g.CloudShadows; _cloudsSaved = true; }
        g.Clouds = on;
        g.CloudShadows = on ? _savedShadows : GameSettings.CloudShadows.Off;
        Log.Info($"autoshot: clouds {(on ? "on" : "off")}");
    }

    private static void View(string argument, string bodyId)
    {
        double[] v = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(bodyId, out Astronomical? astronomical)
            || astronomical is not Celestial body || v.Length < 6)
            return;
        double3 Position(double lat, double lon, double altitude)
        {
            double3 dir = double3.Normalize(body.GetPositionEclFromLatLon(lat, lon) - body.GetPositionEcl());
            return body.GetSurfacePositionEclFromDirCce(dir) + dir * altitude;
        }
        double3 camera = Position(v[0], v[1], v[2]);
        double3 target = Position(v[3], v[4], v[5]);
        // The fly controller rewrites LocalRotation every frame from a cached offset (frame2Ecl * offset), so after
        // aiming the camera the offset has to be re-cached or the old orientation (and its roll) comes back.
        double3 forward = double3.Normalize(target - camera);
        double3 vertical = double3.Normalize(camera - body.GetPositionEcl());
        if (double3.Cross(forward, vertical).Length() < 1e-6)
            vertical = double3.Normalize(double3.Cross(forward, Math.Abs(forward.X) < 0.9 ? double3.UnitX : double3.UnitY));
        var cam = Program.GetMainCamera();
        cam.PositionEcl = camera;
        cam.LocalRotation = Camera.LookAtRotation(forward, vertical);
        Program.GetFlyController().CacheOffset();
    }

    /// <summary>Sun elevation in degrees above the camera's horizon, or null while there is no camera or body.</summary>
    private static double? SunElevation(string bodyId, string sunId)
    {
        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(bodyId, out Astronomical? astronomical)
            || astronomical is not Celestial body)
            return null;
        double3? sun = NightLights.SunDirCcf(body, new Settings { SunId = sunId });
        if (sun is not { } sunDir)
            return null;
        double3 camera = Double3Ex.Transform(Program.GetMainCamera().PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
        double3 up = double3.Normalize(camera);
        return Math.Asin(Math.Clamp(double3.Dot(up, sunDir), -1, 1)) * 180 / Math.PI;
    }
}
