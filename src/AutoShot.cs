using Brutal.Numerics;
using KSA;

namespace OsmBuildings;

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
/// Lines starting with # are comments.
/// </summary>
internal static class AutoShot
{
    private enum Step { Next, Wait, Sun, ShotWait }

    private static string[]? _lines;
    private static int _next;
    private static Step _step = Step.Next;
    private static double _timer;
    private static double _target;
    private static string _shotName = "";
    private static HashSet<string> _before = new();
    private static bool _checked;

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
            _lines = File.ReadAllLines(path);
            File.Move(path, Path.Combine(modDir, "autoshot.done"), true);
            Log.Info($"autoshot: {_lines.Length} lines");
        }
        try
        {
            Run(dt, modDir, bodyId, sunId);
        }
        catch (Exception e)
        {
            Log.Error($"autoshot failed: {e}");
            _next = _lines.Length;
            _step = Step.Next;
        }
    }

    private static void Run(double dt, string modDir, string bodyId, string sunId)
    {
        switch (_step)
        {
            case Step.Wait:
                if ((_timer -= dt) <= 0)
                    _step = Step.Next;
                return;
            case Step.Sun:
                if (SunElevation(bodyId, sunId) is not { } elevation)
                    return;
                double delta = Math.Abs(elevation - _target);
                // Gives up after a minute: a target above the day's highest sun is never reached.
                if (delta < 0.4 || (_timer += dt) > 60)
                {
                    Program.TerminalInterface.Execute("simspeed 1");
                    Log.Info($"autoshot: sun at {elevation:0.0} degrees");
                    _step = Step.Next;
                    return;
                }
                Program.TerminalInterface.Execute($"simspeed {Math.Clamp(delta * 2500, 20, 40000):0}");
                return;
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
                case "crewarm":
                    // crewarm dock | undock | finish
                    if (argument == "dock")
                        CrewArm.Dock();
                    else if (argument == "undock")
                        CrewArm.Undock();
                    else
                        CrewArm.Finish();
                    break;
                case "view":
                    View(argument, bodyId);
                    break;
                case "log":
                    Log.Info($"autoshot: {argument}");
                    break;
                default:
                    Log.Info($"autoshot: > {line}");
                    Program.TerminalInterface.Execute(line);
                    break;
            }
        }
    }

    /// <summary>
    /// view LAT LON ALT TLAT TLON TALT: puts the (free) camera at a place ALT metres above the ground and turns it
    /// to the target point, with the local vertical up.
    /// </summary>
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
        // The renderer's view-up is the LookAt up vector turned by a quarter around the view direction, so pass
        // the vector that ends up as the local vertical (mode 1 or 2 picks the sense; 0 passes it unchanged).
        double3 forward = double3.Normalize(target - camera);
        double3 vertical = double3.Normalize(camera - body.GetPositionEcl());
        int mode = v.Length > 6 ? (int)v[6] : 1;
        double3 up = mode == 1 ? double3.Cross(forward, vertical) : mode == 2 ? double3.Cross(vertical, forward) : vertical;
        Program.GetMainCamera().LookAt(camera, target, up);
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
