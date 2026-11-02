using Brutal.Numerics;
using KSA;
using KSA.Rendering.Lighting;

namespace GEE;

/// <summary>
/// After sunset, puts warm point lights in front of building facades and above roads (street lamps).
/// The game's static object shader has no emissive term, so lit windows are not possible; instead the
/// nearest buildings and roads are lit through the game's clustered light system.
/// </summary>
internal static class NightLights
{
    private static readonly List<(double Dist2, Tile Tile, int Index)> Candidates = new();
    private static readonly float3 FacadeColor = new(1.0f, 0.72f, 0.42f);
    private static readonly float3 SodiumColor = new(1.0f, 0.62f, 0.28f);
    private static readonly float3 LedColor = new(0.95f, 0.9f, 0.8f);

    public static int LastLights;
    public static float LastNightFactor;

    /// <summary>0 by day, 1 when the sun is more than ~5° below the horizon at the camera.</summary>
    public static float NightFactor(Celestial body, double3 cameraCcf, Settings settings)
    {
        return SunDirCcf(body, settings) is { } sun ? NightAt(double3.Dot(double3.Normalize(cameraCcf), sun)) : 0;
    }

    /// <summary>Unit vector from the body's centre to the sun, in the body's fixed frame.</summary>
    public static double3? SunDirCcf(Celestial body, Settings settings)
    {
        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(settings.SunId, out Astronomical? sun) || sun == null)
            return null;
        return double3.Normalize(Double3Ex.Transform(sun.GetPositionEcl() - body.GetPositionEcl(), body.GetCce2Ccf()));
    }

    /// <summary>Night factor for the sine of the sun's elevation.</summary>
    public static float NightAt(double sunSine)
    {
        double t = Math.Clamp((0.03 - sunSine) / 0.12, 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    public static void Begin() => Candidates.Clear();

    /// <summary>Called by the renderer for every drawn instance within light range.</summary>
    public static void Consider(double dist2, Tile tile, int index) => Candidates.Add((dist2, tile, index));

    public static void Emit(IViewport viewport, double3 cameraCcf, double3 rx, double3 ry, double3 rz, float night, Settings settings)
    {
        int emitted = 0;
        if (night > 0 && Candidates.Count > 0)
        {
            Candidates.Sort((a, b) => a.Dist2.CompareTo(b.Dist2));
            float range = (float)settings.LightRangeMeters;
            float intensity = (float)settings.LightIntensity * night;
            foreach (var (_, tile, index) in Candidates)
            {
                if (emitted >= settings.MaxLights)
                    break;
                ref BuildingInstance b = ref tile.Instances[index];
                double3 up = double3.Normalize(b.Center);
                double3 position;
                float3 color;
                if (b.IsRoad)
                {
                    // Lamp head beside the carriageway, 8 m up.
                    double3 side = b.AxisB * (0.5 + 1.0 / Math.Max(Length(b.AxisB), 1));
                    position = b.Center + up * 8 + side;
                    color = (index & 3) == 0 ? LedColor : SodiumColor;
                }
                else
                {
                    // Roughly 60% of buildings lit, on the facade facing the camera, a third of the way up.
                    if (Hash(index, tile.Key.X, tile.Key.Y) % 10 >= 6)
                        continue;
                    double3 toCamera = cameraCcf - b.Center;
                    double3 outward = double3.Dot(toCamera, b.AxisB) >= 0 ? b.AxisB : -b.AxisB;
                    double half = Length(b.AxisB) / 2;
                    double height = Math.Max(double3.Dot(b.Base + b.Up - b.Center, up), 3);
                    position = b.Center + up * Math.Min(height * 0.35, 15) + double3.Normalize(outward) * (half + 3);
                    color = FacadeColor;
                }
                double3 ego = Placement.ToEgo(position - cameraCcf, rx, ry, rz);
                Program.LightSystem.CreateLightInstance(Light.CreatePointLight(ego, range, color, intensity), viewport);
                emitted++;
            }
        }
        if (viewport == Program.MainViewport)
        {
            LastLights = emitted;
            LastNightFactor = night;
        }
    }

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));

    private static uint Hash(int index, int x, int y)
    {
        uint h = (uint)index * 2654435761u ^ (uint)x * 40503u ^ (uint)y * 9973u;
        h ^= h >> 15;
        return h * 2246822519u >> 7;
    }
}
