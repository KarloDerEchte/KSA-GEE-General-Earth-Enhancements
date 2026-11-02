using System.Diagnostics;
using Brutal.Numerics;
using KSA;

namespace OsmBuildings;

/// <summary>
/// The crew access arm of the LC-39A fixed service structure as a moving part: it turns about a vertical hinge at
/// the tower wall, from docked (reaching to the rocket) to retracted (swung 90 degrees, along the tower face) and
/// back, easing in and out. It is retracted when the game starts.
/// </summary>
internal static class CrewArm
{
    public const double LengthM = 21.0, HeightM = 3.0, WidthM = 2.8;
    private const double SwingDeg = 90, DegPerSecond = 3;            // 30 s for a full swing

    private static bool _registered;
    private static double3 _hinge, _up, _docked, _swing;
    private static double _angle = double.NaN, _target;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastTime;

    public static bool Available => _registered;
    public static bool Moving => _registered && !double.IsNaN(_angle) && Math.Abs(_angle - _target) > 1e-3;

    public static string Status => !_registered ? "Crew arm: not loaded (fly to LC-39A)"
        : Moving ? (_target == 0 ? "Crew arm: docking..." : "Crew arm: undocking...")
        : _target == 0 ? "Crew arm: docked" : "Crew arm: retracted";

    /// <summary>Called when the tiles place the fixed service structure: the hinge follows from its box.</summary>
    public static void Register(in BuildingInstance tower)
    {
        _up = double3.Normalize(tower.Up);
        double3 a = double3.Normalize(tower.AxisA), b = double3.Normalize(tower.AxisB);
        // As the old fixed arm: 58 % of the model's height, at the tower wall 5.7 m from its centre toward -B (east).
        _hinge = tower.Base + tower.Up * 0.58 + b * -5.7;
        _docked = -b;
        _swing = a;
        if (!_registered)
            _angle = _target = SwingDeg;                              // retracted at game start
        _registered = true;
    }

    public static void Dock() => SetTarget(0);
    public static void Undock() => SetTarget(SwingDeg);

    /// <summary>Jumps to the target without the swing.</summary>
    public static void Finish() => _angle = _target;

    private static void SetTarget(double target)
    {
        _target = target;
        if (double.IsNaN(_angle))
            _angle = target;
    }

    public static void Render(StaticObjectModel model, IViewport viewport, double3 cameraCcf, double3 rx, double3 ry, double3 rz, double maxDistance)
    {
        double now = Clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastTime, 0.25);
        _lastTime = now;
        if (!_registered)
            return;
        double3 toCamera = _hinge - cameraCcf;
        if (double3.Dot(toCamera, toCamera) > maxDistance * maxDistance)
            return;
        if (viewport == Program.MainViewport && Moving)
        {
            double step = DegPerSecond * dt;
            _angle += Math.Clamp(_target - _angle, -step, step);
        }
        // Eased: slow start and stop, as a heavy arm on its drive.
        double t = _angle / SwingDeg;
        double eased = t * t * (3 - 2 * t);
        double rad = eased * SwingDeg * Math.PI / 180;
        double3 dir = _docked * Math.Cos(rad) + _swing * Math.Sin(rad);
        var arm = new BuildingInstance
        {
            Up = _up * HeightM,
            AxisA = dir * LengthM,
            AxisB = double3.Cross(_up, dir) * WidthM,
            Base = _hinge,
            Center = _hinge,
            Variant = Buildings.CrewArmVariant,
            Style = Buildings.StyleNone,
        };
        model.AddInstance(new StaticObjectModel.PerInstanceData { ModelMatrix = Placement.ModelToEgo(in arm, cameraCcf, rx, ry, rz) }, viewport);
    }
}
