using Brutal.Numerics;

namespace GEE.Tests;

/// <summary>Shared helpers: terrain functions, geometry checks.</summary>
internal static class Geo
{
    public const double R = 6371000;
    public const double MetersPerDegree = Math.PI / 180 * R;

    public static double Len(double3 v) => Math.Sqrt(double3.Dot(v, v));
    public static bool Finite(double3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    public static bool Finite(BuildingInstance b) =>
        Finite(b.Up) && Finite(b.AxisA) && Finite(b.AxisB) && Finite(b.Base) && Finite(b.Center) && double.IsFinite(b.GroundOffset);

    public static Func<double3, double> Flat(double h = 0) => _ => h;

    /// <summary>Gentle hills, deterministic: smooth in the direction on a scale of ~100 m.</summary>
    public static Func<double3, double> Hills(double amplitude = 6) => d =>
        amplitude * Math.Sin(d.X * R / 90) * Math.Cos(d.Y * R / 130) + amplitude * Math.Sin(d.Z * R / 70);

    /// <summary>Distance in metres between two CCF points.</summary>
    public static double Dist(double3 a, double3 b) => Len(a - b);

    public static double3 Dir(double lat, double lon) => Placement.DirCcf(lat, lon);

    /// <summary>The eight corners of the instance's oriented box (unit box: x up in [0,1], y,z in [-0.5,0.5]).</summary>
    public static IEnumerable<double3> Corners(BuildingInstance b)
    {
        for (int i = 0; i < 8; i++)
            yield return b.Base + b.Up * (i & 1) + b.AxisA * (((i >> 1) & 1) - 0.5) + b.AxisB * (((i >> 2) & 1) - 0.5);
    }
}
