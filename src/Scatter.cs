using HarmonyLib;
using KSA;
using Brutal.Numerics;

namespace GEE;

internal enum ScatterKind { Rock, Pebbles, Bush, Flowers, Log }

/// <summary>
/// Small things on the ground: rocks, a pebble patch, bushes, wildflower patches and fallen logs (Scatter.xml, written by
/// tools/scatter_assets.py, procedural). They are ordinary clutter objects GEE_Scatter_&lt;Kind&gt;_&lt;n&gt; appended to Earth's
/// Shrub ecotype like TreePack does it, so the game places, fades, shadows and collides them: each Shrub instance picks one
/// of the ecotype's objects at random. What the game cannot do is tell where each kind belongs, so TreeCover asks
/// <see cref="Keeps"/> for every instance that turned out to be one of ours (the pick is replicated in
/// Clutter.ObjectIndex): by land cover, slope and noise, the rest is removed through the game's exclusion masks.
/// Needs RealisticTrees (the same worker); without it the objects are not added.
/// </summary>
internal static class Scatter
{
    private const string Prefix = "GEE_Scatter_";
    private const string EcotypeName = "Shrub";
    private static readonly string[] KindNames = Enum.GetNames<ScatterKind>();

    public static Settings? Settings;

    /// <summary>Kind of every object of an ecotype (null = the game's own), by ecotype index; set once the grids are known.</summary>
    private static ScatterKind?[]?[] _kinds = Array.Empty<ScatterKind?[]?>();

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.PopulatePlanetData)),
            prefix: new HarmonyMethod(typeof(Scatter), nameof(BeforePopulatePlanetData)));
    }

    private static void BeforePopulatePlanetData()
    {
        if (Settings is not { ScatterClutter: true, RealisticTrees: true })
            return;
        try
        {
            foreach (Celestial body in Universe.CurrentSystem.All.OfType<Celestial>())
            {
                if (body.Id != "Earth" || body.BodyTemplate.GroundClutterReference is not { } clutter)
                    continue;
                foreach (ClutterEcotypeReference ecotype in clutter.Ecotypes.Where(e => e.Key == EcotypeName))
                {
                    int added = TreePack.AppendObjects(ecotype, Ids());
                    if (added > 0)
                        Log.Info($"scatter: {added} objects added to ecotype {ecotype.Key} ({ecotype.ClutterObjects.Count} objects)");
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"scatter: {e}");
        }
    }

    /// <summary>GEE_Scatter_Rock_0, _1, ... then the next kind, as far as the mod's assets define them.</summary>
    private static IEnumerable<string> Ids()
    {
        foreach (string kind in KindNames)
        {
            for (int n = 0; ModLibrary.TryGet($"{Prefix}{kind}_{n}", out ClutterObjectTemplate? o) && o != null; n++)
                yield return $"{Prefix}{kind}_{n}";
        }
    }

    /// <summary>Main thread: records which objects of an ecotype are ours (call for every ecotype after the objects were added).</summary>
    public static void Register(int ecotypeIndex, ClutterEcotypeReference ecotype)
    {
        if (_kinds.Length <= ecotypeIndex)
            Array.Resize(ref _kinds, ecotypeIndex + 1);
        var kinds = new ScatterKind?[ecotype.ClutterObjects.Count];
        bool any = false;
        for (int i = 0; i < kinds.Length; i++)
        {
            string id = ecotype.ClutterObjects[i].Id;
            if (!id.StartsWith(Prefix, StringComparison.Ordinal))
                continue;
            int end = id.IndexOf('_', Prefix.Length);
            if (end > 0 && Enum.TryParse(id.AsSpan(Prefix.Length, end - Prefix.Length), out ScatterKind kind))
            {
                kinds[i] = kind;
                any = true;
            }
        }
        _kinds[ecotypeIndex] = any ? kinds : null;
    }

    /// <summary>Kind per object index of an ecotype, or null if it has none of ours.</summary>
    public static ScatterKind?[]? KindsFor(int ecotypeIndex) => ecotypeIndex < _kinds.Length ? _kinds[ecotypeIndex] : null;

    /// <summary>
    /// Whether an instance of the given kind stays. roll is its fixed random number in [0, 1); classAt looks up other places
    /// (null = not loaded); ground returns terrain height. Worker thread.
    /// </summary>
    public static bool Keeps(ScatterKind kind, byte landCover, double roll, double3 dir, double latDeg, double lonDeg,
        Func<double, double, byte?> classAt, Func<double3, double>? ground)
    {
        double density = Settings?.ScatterDensity ?? 1;
        if (density <= 0 || Base(kind, landCover) <= 0)
            return false;

        // Position in metres on a local plane: noise patches and neighbour lookups.
        double mx = lonDeg * 111320 * Math.Cos(latDeg * Math.PI / 180), my = latDeg * 110574;
        double patch = ValueNoise(mx / PatchMeters(kind), my / PatchMeters(kind), (int)kind)
                       * 0.65 + ValueNoise(mx / (PatchMeters(kind) * 0.37), my / (PatchMeters(kind) * 0.37), (int)kind + 17) * 0.35;
        double keep = Base(kind, landCover) * density;
        switch (kind)
        {
            case ScatterKind.Flowers:
                keep *= Smooth(0.5, 0.68, patch);                            // clearly bounded meadows of flowers
                break;
            case ScatterKind.Bush:
                // Edges of woods: grass, fields and towns get bushes where a tree is within ~30 m.
                if (landCover != WorldCover.Shrubland && landCover != WorldCover.Trees && NearTrees(latDeg, lonDeg, classAt))
                    keep = Math.Max(keep, 0.35 * density);
                keep *= 0.35 + 1.3 * Smooth(0.3, 0.75, patch);
                break;
            case ScatterKind.Log:
                keep *= 0.4 + 1.2 * Smooth(0.25, 0.8, patch);
                break;
            default:                                                         // rocks and pebbles: clusters
                keep *= 0.15 + 1.7 * Smooth(0.4, 0.8, patch);
                break;
        }
        if (keep * Math.Max(MaxSlopeBoost(kind), 1) <= roll)
            return false;
        if (ground != null && keep > 0)
        {
            double slope = Slope(dir, ground);
            keep *= SlopeFactor(kind, slope);
        }
        return roll < keep;
    }

    /// <summary>Share kept on a land cover class, before slope and noise (1 = every instance picking that kind).</summary>
    private static double Base(ScatterKind kind, byte landCover) => kind switch
    {
        ScatterKind.Rock => landCover switch
        {
            WorldCover.Bare => 0.9,
            WorldCover.Snow => 0.25,
            WorldCover.Moss => 0.5,
            WorldCover.Grassland => 0.28,
            WorldCover.Shrubland => 0.35,
            WorldCover.Trees => 0.22,
            WorldCover.Wetland or WorldCover.Mangroves => 0.04,
            WorldCover.BuiltUp => 0.05,
            WorldCover.Cropland => 0.03,
            _ => 0,
        },
        ScatterKind.Pebbles => landCover switch
        {
            WorldCover.Bare => 0.9,
            WorldCover.Moss => 0.35,
            WorldCover.Grassland => 0.22,
            WorldCover.Shrubland => 0.25,
            WorldCover.Trees => 0.15,
            WorldCover.Wetland => 0.3,
            WorldCover.BuiltUp => 0.06,
            WorldCover.Cropland => 0.05,
            _ => 0,
        },
        ScatterKind.Bush => landCover switch
        {
            WorldCover.Shrubland => 1,
            WorldCover.Trees => 0.75,
            WorldCover.Mangroves => 0.2,
            WorldCover.Wetland => 0.3,
            WorldCover.Moss => 0.15,
            WorldCover.BuiltUp => 0.1,
            WorldCover.Grassland => 0.05,
            WorldCover.Cropland => 0.01,
            WorldCover.Bare => 0.02,
            _ => 0,
        },
        ScatterKind.Flowers => landCover switch
        {
            WorldCover.Grassland => 1,
            WorldCover.Moss => 0.6,
            WorldCover.Shrubland => 0.4,
            WorldCover.Wetland => 0.3,
            WorldCover.Trees => 0.1,
            WorldCover.BuiltUp => 0.1,
            WorldCover.Cropland => 0.04,
            WorldCover.Bare => 0.02,
            _ => 0,
        },
        _ => landCover switch                                                // Log
        {
            WorldCover.Trees => 0.4,
            WorldCover.Shrubland => 0.1,
            WorldCover.Wetland or WorldCover.Mangroves => 0.1,
            WorldCover.Grassland => 0.01,
            _ => 0,
        },
    };

    /// <summary>Largest value SlopeFactor takes (for the cheap early out).</summary>
    private static double MaxSlopeBoost(ScatterKind kind) => kind == ScatterKind.Rock ? 2.2 : 1;

    /// <summary>Rocks like steep ground, the rest likes it flat.</summary>
    private static double SlopeFactor(ScatterKind kind, double slope) => kind switch
    {
        ScatterKind.Rock => 0.35 + 1.85 * Smooth(0.04, 0.5, slope),
        ScatterKind.Pebbles => 1.2 - 0.8 * Smooth(0.1, 0.6, slope),
        ScatterKind.Flowers => 1 - Smooth(0.25, 0.7, slope),
        ScatterKind.Bush => 1 - 0.7 * Smooth(0.5, 1.0, slope),
        _ => 1 - Smooth(0.35, 0.8, slope),                                   // Log
    };

    private static double PatchMeters(ScatterKind kind) => kind switch
    {
        ScatterKind.Flowers => 45,
        ScatterKind.Bush => 70,
        ScatterKind.Pebbles => 60,
        ScatterKind.Log => 120,
        _ => 140,
    };

    /// <summary>Rise over run of the terrain around a point (8 m baseline, east and north).</summary>
    private static double Slope(double3 dir, Func<double3, double> ground)
    {
        Placement.Axes(dir, out double3 east, out double3 north);
        const double step = 4.0, radius = 6371000;
        double h(double3 offset) => ground(double3.Normalize(dir + offset / radius));
        double dx = (h(east * step) - h(east * -step)) / (2 * step);
        double dy = (h(north * step) - h(north * -step)) / (2 * step);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool NearTrees(double latDeg, double lonDeg, Func<double, double, byte?> classAt)
    {
        double dLat = 30 / 110574.0, dLon = 30 / (111320 * Math.Max(Math.Cos(latDeg * Math.PI / 180), 0.05));
        return classAt(latDeg + dLat, lonDeg) == WorldCover.Trees || classAt(latDeg - dLat, lonDeg) == WorldCover.Trees
               || classAt(latDeg, lonDeg + dLon) == WorldCover.Trees || classAt(latDeg, lonDeg - dLon) == WorldCover.Trees;
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Smooth lattice noise in 0..1 (cell size 1), the same everywhere and every run.</summary>
    private static double ValueNoise(double x, double y, int seed)
    {
        long x0 = (long)Math.Floor(x), y0 = (long)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        double a = Lattice(x0, y0, seed), b = Lattice(x0 + 1, y0, seed), c = Lattice(x0, y0 + 1, seed), d = Lattice(x0 + 1, y0 + 1, seed);
        return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
    }

    private static double Lattice(long x, long y, int seed)
    {
        ulong h = unchecked((ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)y * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(seed + 1) * 0x165667B19E3779F9UL);
        h ^= h >> 31;
        h = unchecked(h * 0x94D049BB133111EBUL);
        h ^= h >> 29;
        return (h >> 11) / (double)(1UL << 53);
    }
}
