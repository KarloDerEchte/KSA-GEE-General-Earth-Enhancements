using System.Globalization;
using HarmonyLib;
using KSA;

namespace GEE;

/// <summary>
/// Improves the game's own grass (ground clutter ecotype "Grass": 1.45 m object separation, 80 m range) in three ways.
/// Density and range are data: a prefix on GroundClutterRenderer.PopulatePlanetData (like TreePack) replaces the
/// ecotype's Placement.ObjectSeparation (one candidate per sub-cell of a 16 x 16 cell, so density scales with
/// 1 / separation squared) and Placement.GenerationRange before the game builds its grids and buffers. The cost of the
/// longer range is kept down in CullInstances.comp: beyond a near distance every instance is kept with a probability
/// falling to GrassFarKeep at the range (a per-instance hash against the distance, so the set is stable and grows
/// smoothly as the camera approaches; the game's own LOD still picks the cheapest mesh by screen size). Finally the
/// colour: every instance carries the terrain colour sampled at generation (FinalizeGenerate.comp, planet colour map
/// plus ground materials); for grass that colour gets the same Sentinel-2 correction the terrain shader applies
/// (GroundTint.cs, same table and imagery textures, same hue/brightness maths), so the grass blends with the ground
/// instead of standing out in the game's own colour. The colour is baked into the instance when its cell is
/// generated, with the imagery that is loaded at that time (coarser ancestors first, the game's colour if none).
/// All of it is baked at game start; the grass is recognised in the shaders by its (unusual) generation range.
/// </summary>
internal static class GrassBoost
{
    public static Settings? Settings;

    private static bool _snapshotTaken;
    private static double _range, _density, _nearM, _farKeep;
    private static bool _active;
    private static string _status = "off";
    private static readonly Dictionary<string, (double Separation, double Range)> Originals = new();

    public static string Status => _status;

    /// <summary>Grass range the shaders recognise it by (the ecotype is set to exactly this).</summary>
    public static float BakedRange => (float)_range;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.PopulatePlanetData)),
            prefix: new HarmonyMethod(typeof(GrassBoost), nameof(BeforePopulatePlanetData)));
    }

    /// <summary>Settings as they were at the first use (the shaders are baked then), clamped to what the game's buffers take.</summary>
    private static bool Snapshot()
    {
        if (_snapshotTaken)
            return _active;
        _snapshotTaken = true;
        if (Settings is not { GrassBoost: true } s)
            return _active = false;
        // Not 350 or more: other ecotypes (rocks) use those ranges and the shaders tell the grass apart by its range.
        _range = Math.Round(Math.Clamp(s.GrassRangeMeters, 90, 320));
        _density = Math.Clamp(s.GrassDensity, 0.5, 4);
        _nearM = Math.Min(60, _range * 0.5);
        _farKeep = Math.Clamp(s.GrassFarKeep, 0.05, 1);
        return _active = true;
    }

    private static void BeforePopulatePlanetData()
    {
        if (!Snapshot())
            return;
        try
        {
            foreach (Celestial body in Universe.CurrentSystem.All.OfType<Celestial>())
            {
                if (body.BodyTemplate.GroundClutterReference is not { } clutter)
                    continue;
                foreach (ClutterEcotypeReference ecotype in clutter.Ecotypes)
                {
                    if (ecotype.Key != "Grass")
                        continue;
                    string key = $"{body.Id}/{ecotype.Key}";
                    // The game's values the first time; running again (renderer rebuilt) must not compound.
                    if (!Originals.TryGetValue(key, out var original))
                        Originals[key] = original = (ecotype.Placement.ObjectSeparation.InMeters(), ecotype.Placement.GenerationRange.InMeters());
                    // Cells are 16 separations wide and the game loads (2K + 1)^2 of them with K = range / cell width: keep K <= 13.
                    double separation = Math.Max(original.Separation / Math.Sqrt(_density), _range / (16.0 * 13.0));
                    ecotype.Placement.ObjectSeparation = new DistanceReference(separation);
                    ecotype.Placement.GenerationRange = new DistanceReference(_range);
                    double kernel = Math.Ceiling(_range / (16 * separation));
                    _status = $"{body.Id}: grass every {separation:0.00} m (game {original.Separation:0.00}), range {_range:0} m (game {original.Range:0}), about {(2 * kernel + 1) * (2 * kernel + 1):0} cells around the camera";
                    Log.Info($"grass boost: {_status}");
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"grass boost: {e}");
        }
    }

    // ---- shader patches (ShaderPatch.ReadShader hands the game's text over) ----

    /// <summary>Called for every ground clutter shader the game reads; patches CullInstances.comp and FinalizeGenerate.comp.</summary>
    public static byte[] PatchShader(string path, byte[] bytes)
    {
        string name = Path.GetFileName(path);
        bool cull = name.Equals("CullInstances.comp", StringComparison.OrdinalIgnoreCase);
        bool finalize = name.Equals("FinalizeGenerate.comp", StringComparison.OrdinalIgnoreCase);
        if (!cull && !finalize)
            return bytes;
        try
        {
            string source = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
            string? patched = cull ? PatchCull(source) : PatchFinalize(source);
            if (patched == null)
            {
                Log.Notice($"grass boost: {name} not patched (off or the game's shader has changed)");
                return bytes;
            }
            Log.Notice($"grass boost: {name} patched");
            return System.Text.Encoding.UTF8.GetBytes(patched);
        }
        catch (Exception e)
        {
            Log.Error($"grass boost shader ({name}): {e.Message}");
            return bytes;
        }
    }

    private static string F(double v) => v.ToString("0.0####", CultureInfo.InvariantCulture);

    private const string CullAnchor = "#define NUM_OBJECTS_PER_CELL uint(NUM_OBJECTS_PER_CELL_EDGE * NUM_OBJECTS_PER_CELL_EDGE)";
    private const string CullTest = "if (lenSqr > generationRangeSqr)";

    /// <summary>CullInstances.comp with the distance thinning of the grass, or null (off, or the game's shader has changed).</summary>
    public static string? PatchCull(string source)
    {
        if (!Snapshot() || _farKeep >= 0.999)
            return null;
        if (source.Split(CullAnchor).Length != 2 || source.Split(CullTest).Length != 2)
            return null;
        string functions = CullFunctions
            .Replace("GEE_G_RANGE", F(_range))
            .Replace("GEE_G_NEAR", F(_nearM))
            .Replace("GEE_G_KEEP", F(_farKeep));
        return source.Replace(CullAnchor, CullAnchor + "\n" + functions)
            .Replace(CullTest, "if (lenSqr > generationRangeSqr || geeGrassThinned(globalID, lenSqr))");
    }

    private const string CullFunctions = """

        // ---- GEE: grass distance thinning (mods/GEE GrassBoost.cs) ----
        // Beyond GEE_G_NEAR metres a growing share of the grass is skipped (hash of the instance slot against the
        // distance: stable, and new blades fade in as the camera comes closer). Only the grass has this range.
        bool geeGrassThinned(uint globalID, float lenSqr)
        {
            if (abs(ecotypeInfo.placementParams.generationRange - GEE_G_RANGE) > 0.05)
                return false;
            float keep = mix(1.0, GEE_G_KEEP, smoothstep(GEE_G_NEAR, GEE_G_RANGE, sqrt(lenSqr)));
            return float(pcg32(uvec2(globalID, 0x51ed270bu)) >> 8u) * (1.0 / 16777216.0) >= keep;
        }
        // ---- end GEE ----

        """;

    private const string FinalizeAnchor = "#include \"Common/ClutterSurfaceColor.glsl\"";
    private const string FinalizeCall = "vec3 color = GetPlanetSurfaceColor(sphereDir, texturePos, -texN, 3.0f);";

    /// <summary>FinalizeGenerate.comp with the satellite colour for the grass, or null (off, or the game's shader has changed).</summary>
    public static string? PatchFinalize(string source)
    {
        if (!Snapshot() || Settings is not { Enabled: true, GrassSatelliteColor: true, GroundColorFromSatellite: true, SatelliteImagery: true })
            return null;
        if (!GroundTint.Reserve())
            return null;
        if (source.Split(FinalizeAnchor).Length != 2 || source.Split(FinalizeCall).Length != 2)
            return null;
        string functions = GroundTint.BakedFunctions() + TintFunctions.Replace("GEE_G_RANGE", F(_range));
        return source.Replace(FinalizeAnchor, FinalizeAnchor + "\n" + functions)
            .Replace(FinalizeCall, "vec3 color = geeGrassColor(sphereDir, GetPlanetSurfaceColor(sphereDir, texturePos, -texN, 3.0f));");
    }

    // Uses geeTexel / geeSatellite of GroundTint.Functions. The terrain shader picks the photo's zoom from the pixel
    // footprint; a grass blade is far smaller than any texel, so the finest zoom the table's window reaches is used.
    private const string TintFunctions = """

        // ---- GEE: grass colour like the satellite-tinted terrain (mods/GEE GrassBoost.cs) ----
        vec3 geeGrassColor(vec3 geeDir, vec3 geeColor)
        {
            if (abs(ecotypeInfo.placementParams.generationRange - GEE_G_RANGE) > 0.05)
                return geeColor;
            vec4 geeHead = geeTexel(0, 0);
            if (!(geeHead.x == 12345.0 && geeHead.y > 0.0 && abs(uboPlanet.radius - geeHead.z) < 1000.0))
                return geeColor;
            geeDir = normalize(geeDir);
            float geeLat = asin(clamp(geeDir.z, -1.0, 1.0));
            vec2 geeUnit = vec2((atan(geeDir.y, geeDir.x) + 3.14159265) / 6.28318531,
                (3.14159265 - log(tan(0.78539816 + 0.5 * clamp(geeLat, -1.48, 1.48)))) / 6.28318531);
            vec4 geeTop = geeTexel(1, 0);
            vec2 geeAway = abs(vec2(mod(geeUnit.x * 16384.0 - geeTop.x - 8.0 + 8192.0, 16384.0) - 8192.0,
                geeUnit.y * 16384.0 - geeTop.y - 8.0));
            float geeReach = 14.0 - max(ceil(log2(max(max(geeAway.x, geeAway.y), 1.0) / 7.0)), 0.0);
            int geeZoom = int(clamp(geeReach, 5.0, 14.0));
            vec4 geeFine = geeSatellite(geeUnit, geeZoom);
            if (geeFine.a <= 0.0)
                return geeColor;
            vec4 geeCoarse = geeSatellite(geeUnit, max(geeZoom - 4, 5));
            const vec3 geeLum = vec3(0.2126, 0.7152, 0.0722);
            float geeFineLum = max(dot(geeFine.rgb, geeLum), 1e-4);
            float geeRef = geeCoarse.a > 0.0 ? max(dot(geeCoarse.rgb, geeLum), 1e-4) : geeFineLum;
            vec3 geeHue = geeFine.rgb / geeFineLum;
            geeHue /= max(1.0, max(geeHue.r, max(geeHue.g, geeHue.b)) / 2.5);
            float geeRatio = geeFineLum / geeRef;
            geeRatio = pow(max(geeRatio, 1e-3), GEE_BRIGHTK);
            geeRatio = geeRatio < 1.0 ? max(geeRatio, 0.3) : 1.0 + 1.5 * (1.0 - exp(-(geeRatio - 1.0) / 1.5));
            return mix(geeColor, geeHue * geeRatio * dot(geeColor, geeLum), geeHead.y);
        }
        // ---- end GEE ----

        """;
}
