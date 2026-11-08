using System.Reflection;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Rendering.Water.Rendering;
using KSA.Rendering.Water.Rendering.ShaderStructs;
using Core;
using RenderCore;
using RenderCore.Systems;

namespace GEE;

/// <summary>
/// Lakes made of the game's own ocean and terrain. The mod paints a map of the lakes around the camera (WaterField's
/// polygons, window of WindowMeters in the planet's u, v map): the red channel is the signed distance to the nearest shore,
/// the green channel the index (1..Passes) of the nearest lake's level (the levels are a table in the params texture), so any
/// number of lakes up to Passes distinct levels fit in one map. Wide river polygons are lakes of their own level per tile piece
/// (rivers slope), tidal estuaries the game has as land get level 0 (terrain cut only, the game's sea fills them).
/// Two things read it:
///  - The terrain: the game's FinalizeModifiers.comp makes every terrain vertex's final height; the patched shader sets it to
///    the lake bed (level minus depth) inside the lakes. The map reaches it through two free slots of the terrain
///    modifier texture array (14: the map, 15: the window and the levels), whose textures are made when the game builds
///    the descriptor sets and are only refilled later.
///  - The ocean: after the game has drawn its ocean, the same draw is repeated per lake level with a copy of the ocean's
///    data raised to the lake's level, and the patched Ocean.frag throws away every pixel outside the lakes and takes the
///    water depth from the map. Waves, foam, sun glint, sky reflection and refraction are the ocean's own.
/// The lake level comes from the heights of the shore points of the whole lake (all its tiles), not from the terrain
/// inside: the game's heightmap has no flat lakes. The copy's unused Gerstner fields carry the window and the map's
/// bindless handle and channel (WaveCount -1 marks a lake pass).
/// </summary>
internal static class LakeOcean
{
    /// <summary>Lake levels one map holds (one ocean pass each; the map labels every pixel with the index 1..Passes of its level).</summary>
    public const int Passes = 12, MaskSize = 4096;
    public const double WindowMeters = 100000, MaxDepthMeters = 80, LakeLift = 0.2;
    /// <summary>The lake bed stays this far above the sea (the game's ocean would fill anything below 0): shallow lakes such as Okeechobee are shallow.</summary>
    public const double SeaFloorMeters = 0.2, MinLakeDepthMeters = 0.3;
    /// <summary>A level at or below this is the sea's (estuaries the game has as land): the terrain is cut, the game's ocean fills it; TidalDepthMeters deep.</summary>
    public const double SeaLevelMeters = 0.05, TidalDepthMeters = 4, SeaCutFromMeters = -1.0, SeaCutFullMeters = 0.3;
    /// <summary>First column of the level table in row 0 of the params texture: the level of index i is at column LevelTable + i.</summary>
    public const int LevelTable = 32;
    /// <summary>Depth of the lake bed a few metres from the shore, and how fast it falls away.</summary>
    private const double SlopeMeters = 0.09;
    /// <summary>A remembered lake level is only replaced by a new estimate that differs by more than this.</summary>
    private const double LevelStickMeters = 8, RealStickMeters = 1.5, RealLift = 0.1;
    private const double MinHalfWidthM = 125, RebuildDistanceM = 5000, MergeLevelsM = 1.5, SeaMarginM = 6, SeaMarginRealM = 1.5;
    /// <summary>
    /// Wide river polygons (class river) are rendered as water too: a piece per tile with its own level (rivers slope),
    /// at least this wide (half width = area / shoreline) and this big; levels of neighbouring pieces within RiverMergeM share a pass.
    /// A tidal river (real elevation at the sea's) gets TidalLevel: just above the sea, so the bed stays dry of it.
    /// </summary>
    private const double MinRiverHalfWidthM = 100, MinRiverAreaM2 = 0.4e6, RiverMergeM = 0.6, TidalLevel = 0.9;
    /// <summary>
    /// The map's channels hold the signed distance to the shoreline, bent so that it keeps its resolution near the shore:
    /// byte 128 is the shoreline, above it the distance inside the lake, below it the distance outside, both as
    /// 127 * (1 - exp(-metres / DistanceScaleMeters)). The same scale on both sides puts the zero crossing of the bilinear
    /// filter at the true shoreline, finer than a texel. Outside, the terrain is eased down to the lake over BankMeters.
    /// </summary>
    public const double DistanceScaleMeters = 200, BankMeters = 450;
    /// <summary>Natural bank: it rises from the shore like 0.07 * x * x / (x + 40) metres (flat at the water, 4 degrees further out).</summary>
    public const double BankSlope = 0.07, BankSoftMeters = 40, BankFreeboardMeters = 2;
    /// <summary>The freeboard of the bank grows from 0 at the water to its full height over this distance; the bed is this deep at the shoreline.</summary>
    public const double FreeboardRampMeters = 30, ShoreBedMeters = 0.3;
    /// <summary>With RealShores the terrain eases from the game's heightmap into the real elevation over this distance from a lake.</summary>
    public const double ShoreBlendMeters = 1200;


    /// <summary>The map's modifier slots (the game's array has 16; Earth uses the first few).</summary>
    private const int AtlasSlot = 14, ParamsSlot = 15;

    public static Settings? Settings;
    public static WaterField? Field;
    public static string Status = "";
    public static int ActivePasses;

    private sealed class Result
    {
        public double[] Levels = Array.Empty<double>();
        /// <summary>Per channel: bounding sphere of the lake pixels (planet-fixed metres, at the lake level) for skipping passes out of view.</summary>
        public double3[] Centers = Array.Empty<double3>();
        public double[] Radii = Array.Empty<double>();
        public byte[] Atlas = Array.Empty<byte>();
        public float U0, V0, Du, Dv;
        /// <summary>Fingerprint of the window, the levels and the map: a build that equals the one in use is dropped.</summary>
        public ulong Signature;
        /// <summary>Water tiles with lakes that went into this map (tile x, y); lets a tile placement know the map covers its surroundings.</summary>
        public volatile HashSet<(int, int)> Included = new();
    }

    private static SimpleVkTexture? _atlas, _params;
    private static int _handle = -1, _paramsHandle = -1, _riverHandle = -1, _riverFarHandle = -1;
    private static SimpleVkTexture? _rivers, _riversFar;
    private static bool _riverBound, _riverFarBound;
    private static float[] _riverRow = new float[5], _riverFarRow = new float[5];
    private static StagingPool? _staging;
    private static bool _inFlight, _failed, _bound;
    private static volatile Result? _ready;
    private static volatile Result? _active;
    private static int _building;
    private static bool _haveWindow;
    private static double _winLat, _winLon;
    private static int _winHash;
    private static double _lastBuildAt = -100;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static float[]? _distance, _distance2, _distance3;
    /// <summary>Level chosen for each lake polygon (tile x, y, first vertex). Only the map builder uses it, one build at a time.</summary>
    private static readonly Dictionary<(int, int, int), (double Level, bool Real)> StickyLevels = new();
    private static int _droppedLogged;
    private static readonly OceanRenderData?[] Clones = new OceanRenderData?[Passes];
    private static readonly HashSet<OceanRenderData> CloneSet = new();
    private static MethodInfo? _perform;
    private static readonly FieldInfo? Culling = AccessTools.Field(typeof(OceanRenderer), "_useFrustumCulling");

    // ---- hooks ----

    public static void Apply(Harmony harmony)
    {
        try
        {
            _perform = AccessTools.Method(typeof(OceanRenderer), "PerformRendering");
            if (_perform != null)
                harmony.Patch(_perform, postfix: new HarmonyMethod(typeof(LakeOcean), nameof(AfterPerformRendering)));
            else
                Log.Error("lakes: OceanRenderer.PerformRendering not found, no lake surface");
            var allocate = AccessTools.Method(typeof(PlanetRenderer), "AllocateCelestialDescriptorSet");
            if (allocate != null)
                harmony.Patch(allocate, prefix: new HarmonyMethod(typeof(LakeOcean), nameof(BeforeAllocateDescriptorSet)));
            else
                Log.Error("lakes: PlanetRenderer.AllocateCelestialDescriptorSet not found, no lake beds");
        }
        catch (Exception e)
        {
            Log.Error($"lakes: hook failed: {e.Message}");
        }
    }

    /// <summary>Puts our two textures into the free slots of the terrain modifier texture array before the game writes the descriptor set.</summary>
    private static void BeforeAllocateDescriptorSet(Celestial celestial)
    {
        if (_failed || Settings is not { Enabled: true } settings || !(settings.Water || settings.RealTerrain || settings.RealShores || settings.Rivers) || celestial.BodyTemplate.Id != settings.Body)
            return;
        try
        {
            if (celestial.TerrainModifiersRenderData is not { } data)
                return;
            EnsureResources();
            data.ModifierTexturesImageViews[AtlasSlot] = _atlas!.VkImageView;
            data.ModifierTexturesImageViews[ParamsSlot] = _params!.VkImageView;
            Log.Notice($"lakes: map textures put into terrain modifier slots {AtlasSlot} and {ParamsSlot}");
        }
        catch (Exception e)
        {
            _failed = true;
            Status = $"off: {e.Message}";
            Log.Error($"lakes: terrain hook failed, lakes off: {e}");
        }
    }

    private static void AfterPerformRendering(OceanRenderer __instance, object[] __args)
    {
        if (_failed || !_bound || Settings is not { Enabled: true, Water: true } || _active is not { } active)
            return;
        try
        {
            if (__args[3] is not OceanRenderData original || CloneSet.Contains(original) || !ReferenceEquals(__args[1], Program.MainViewport))
                return;
            int frame = (int)__args[2];
            var body = (Celestial)__args[6];
            if (active.Levels.Length == 0)
                return;
            var camera = Program.GetMainCamera();
            double3 camCcf = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
            doubleQuat ccf2Cce = body.GetCcf2Cce();
            double3 rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce), ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce), rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);
            ref bool culling = ref CullingRef(__instance);
            for (int k = 0; k < active.Levels.Length; k++)
            {
                // A pass whose lakes are out of view (outside the frustum, or under a pixel or two) draws nothing visible:
                // every pixel outside the lakes is thrown away by the shader anyway.
                if (active.Levels[k] <= SeaLevelMeters)
                    continue;                                              // the sea's own level: the game's ocean draws it
                if (k < active.Centers.Length)
                {
                    double3 rel = active.Centers[k] - camCcf;
                    double dist = rel.Length(), radius = active.Radii[k];
                    if (dist > radius && (radius / dist < MinAngularSize || camera.IsSphereOutsideFov(rx * rel.X + ry * rel.Y + rz * rel.Z, radius)))
                        continue;
                }
                OceanRenderData clone = Clones[k] ??= MakeClone(__instance, body);
                Prepare(__instance, original, clone, body, frame, active, k);
                if (_argBuffer.Length != __args.Length)
                    _argBuffer = new object[__args.Length];
                var args = _argBuffer;
                Array.Copy(__args, args, args.Length);
                args[3] = clone;
                args[7] = TessellationNear(__instance, body, active.Levels[k], k);
                // The game culled the ocean's patches for sea level; the lake's surface is higher, so patches under the camera
                // that were out of view down there are in view here: draw the whole mesh.
                bool wasCulling = culling;
                culling = false;
                try
                {
                    _perform!.Invoke(__instance, args);
                }
                finally
                {
                    culling = wasCulling;
                }
            }
        }
        catch (Exception e)
        {
            _failed = true;
            Status = $"off: {e.Message}";
            Log.Error($"lakes: drawing the ocean again failed, lakes off: {e}");
        }
    }

    /// <summary>Below this radius / distance (about 2 pixels at 1440p with a 60 degree view) a pass's lakes are not drawn.</summary>
    private const double MinAngularSize = 0.0003;
    private static object[] _argBuffer = Array.Empty<object>();
    private static readonly object[] HeightArgs = new object[1];
    private static readonly FieldInfo? PlanetRendererField = AccessTools.Field(typeof(OceanRenderer), "_planetRenderer");
    private static readonly FieldInfo? RendererField = AccessTools.Field(typeof(OceanRenderer), "_renderer");
    private static MethodInfo? _heightView;
    private static AccessTools.FieldRef<OceanRenderer, bool>? _cullingRef;

    private static ref bool CullingRef(OceanRenderer renderer)
    {
        _cullingRef ??= AccessTools.FieldRefAccess<OceanRenderer, bool>("_useFrustumCulling");
        return ref _cullingRef(renderer);
    }

    private static readonly FieldInfo? TessellationRange = AccessTools.Field(typeof(OceanRenderer), "_tessellationRangeMeters");

    /// <summary>The game tessellates the ocean within a range of the camera's height above the sea; for a lake it is the height above the lake.</summary>
    private static readonly bool[] Tessellated = new bool[Passes];

    private static bool TessellationNear(OceanRenderer renderer, Celestial body, double level, int pass)
    {
        double range = TessellationRange?.GetValue(renderer) is float r ? r : 5000;
        double3 cam = Program.GetMainCamera().PositionEcl - body.GetPositionEcl();
        double height = cam.Length() - (body.MeanRadius + level);
        // Hysteresis: switching at one exact height would flip the surface between the two meshes every few frames.
        Tessellated[pass] = Tessellated[pass] ? height < range * 1.15 : height < range * 0.9;
        return Tessellated[pass];
    }

    private static OceanRenderData MakeClone(OceanRenderer renderer, Celestial body)
    {
        object? Field(string name) => AccessTools.Field(typeof(OceanRenderer), name).GetValue(renderer);
        var create = typeof(OceanRenderData).GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("OceanRenderData.Create not found");
        var clone = (OceanRenderData)create.Invoke(null, new[]
        {
            body, body.BodyTemplate.OceanReference, Field("_renderer"), Field("_oceanDataDescriptorSetLayout"),
            Field("_oceanDataBindingCounts"), Field("_frameCount"), Field("_linearRepeatAnisotropicSampler"),
        })!;
        CloneSet.Add(clone);
        return clone;
    }

    private static void Prepare(OceanRenderer renderer, OceanRenderData original, OceanRenderData clone, Celestial body, int frame, Result active, int k)
    {
        double level = active.Levels[k];
        ref StaticOceanData s = ref clone.OceanStaticDataMemory.As<StaticOceanData>();
        s = original.OceanStaticDataMemory.As<StaticOceanData>();
        s.OceanRadius = (float)(body.MeanRadius + level);
        s.OceanLevel = (float)level;
        s.TransparencyDepth = 40;
        s.WaveCount = -1;
        s.WaveAmplitude = active.U0;
        s.Wavelength = active.V0;
        s.WaveSpeed = active.Du;
        s.AmplitudePersistence = active.Dv;
        s.WavelengthPersistence = _handle;
        s.SpeedPersistence = k + 1;                                    // the level index this pass draws (the map's green channel)
        clone.OceanRadius = body.MeanRadius + level;
        clone.OceanLevel = level;
        clone.MaxDisplacement = original.MaxDisplacement;
        ref DynamicOceanData d = ref clone.OceanDynamicDataMemory.Offset(frame * OceanRenderer.DynamicOceanDataStride).As<DynamicOceanData>();
        d = original.OceanDynamicDataMemory.Offset(frame * OceanRenderer.DynamicOceanDataStride).As<DynamicOceanData>();
        object? planetRenderer = PlanetRendererField!.GetValue(renderer);
        _heightView ??= planetRenderer!.GetType().GetMethod("GetHeightView")!;
        HeightArgs[0] = body;
        object? height = _heightView.Invoke(planetRenderer, HeightArgs);
        clone.UpdateHeightMapView((Renderer)RendererField!.GetValue(renderer)!, (VkImageView)height!, frame);
    }

    // ---- what is a lake (for the plants) ----

    /// <summary>
    /// Signed distance of a point from the nearest lake shore of the map (metres, positive inside the lake, greatest over
    /// the lakes of the window). Returns 0 while that is not known yet (no map yet, or the point is outside its window): ask
    /// again later. Returns 1 with -infinity when there are no lakes or lakes are switched off. Any thread.
    /// </summary>
    public static int Query(double latDeg, double lonDeg, out double signedMeters)
    {
        int state = Sample(latDeg, lonDeg, out signedMeters, out int index, out var map);
        if (map == null || index <= 0)
            signedMeters = double.NegativeInfinity;
        return state;
    }

    /// <summary>
    /// True if the point is in water of the lake map at least minDepthFromShore metres from the shore (the terrain is cut there and the
    /// game's ocean fills it); level is the water's height. False on land and while the map is not known. Any thread.
    /// </summary>
    public static bool InWater(double latDeg, double lonDeg, double minDepthFromShore, out double level)
    {
        level = 0;
        if (Sample(latDeg, lonDeg, out double signed, out int index, out var map) != 1 || map == null || index <= 0 || signed <= minDepthFromShore)
            return false;
        level = map.Levels[index - 1];
        return true;
    }

    /// <summary>
    /// Signed shore distance of the nearest lake (see Query) and the index (1..Levels.Length) of that lake's level; the map used is
    /// returned for its levels. Index 0: no lake near (the distance is then meaningless).
    /// </summary>
    private static int Sample(double latDeg, double lonDeg, out double signed, out int index, out Result? mapUsed)
    {
        mapUsed = null;
        signed = 0;
        index = 0;
        if (_failed || Settings is not { Enabled: true, Water: true })
            return 1;
        if (_active is not { } map)
            return 0;
        mapUsed = map;
        double px = (0.5 + lonDeg / 360 - map.U0) / map.Du * MaskSize - 0.5, py = (0.5 - latDeg / 180 - map.V0) / map.Dv * MaskSize - 0.5;
        if (px < 1 || py < 1 || px >= MaskSize - 2 || py >= MaskSize - 2)
            return 0;
        int x = (int)px, y = (int)py;
        double fx = px - x, fy = py - y;
        double Signed(int ix, int iy)
        {
            int u = map.Atlas[(iy * MaskSize + ix) * 4] - 128;
            double meters = -DistanceScaleMeters * Math.Log(Math.Max(1 - Math.Abs(u) / 127.0, 0.001));
            return u > 0 ? meters : -meters;
        }
        double top = Signed(x, y) * (1 - fx) + Signed(x + 1, y) * fx, bottom = Signed(x, y + 1) * (1 - fx) + Signed(x + 1, y + 1) * fx;
        signed = top * (1 - fy) + bottom * fy;
        // The label is read at the nearest texel, like the shaders do (texelFetch of floor(uv * size)).
        int label = map.Atlas[((int)Math.Floor(px + 0.5) + (int)Math.Floor(py + 0.5) * MaskSize) * 4 + 1];
        index = label <= map.Levels.Length ? label : 0;
        return 1;
    }

    /// <summary>The deepest a lake of this level gets (m): bounded so the bed stays above the sea, and by MaxDepthMeters.</summary>
    public static double DepthLimit(double level) => level <= SeaLevelMeters ? TidalDepthMeters : Math.Clamp(level - SeaFloorMeters - ShoreBedMeters, MinLakeDepthMeters, MaxDepthMeters);

    /// <summary>1 in a lake and on its shore, falling to 0 over ShoreBlendMeters; 0 where there is no lake (any thread).</summary>
    public static double NearLake(double latDeg, double lonDeg)
    {
        if (Sample(latDeg, lonDeg, out double signed, out int index, out var map) != 1 || map == null || index <= 0)
            return 0;
        double t = Math.Clamp(-signed / ShoreBlendMeters, 0, 1);
        return signed > 0 ? 1 : 1 - t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The height the terrain has at this point once the lake patch has done its work, given the height the game's heightmap
    /// has (above the mean radius, with modifiers): the same bed and bank as in FinalizeModifiers.comp. Roads, buildings and
    /// lights stand on this, so they follow the banks down. Any thread; unchanged where the map is not known.
    /// </summary>
    public static double AdjustHeight(double latDeg, double lonDeg, double height)
    {
        if (Sample(latDeg, lonDeg, out double s, out int index, out var map) != 1 || map == null || index <= 0)
            return height;
        double result = height, level = map.Levels[index - 1];
        if (s > 0)
        {
            double limit = DepthLimit(level);
            double bed = level - ShoreBedMeters - limit * (1 - Math.Exp(-SlopeMeters * s / limit));
            // A bank the real elevation shows clearly above the water stays (see the shader).
            double keep = 0;
            if (Elevation.TryHeight(latDeg, lonDeg, out double real))
            {
                double t = Math.Clamp((real - (level + 2)) / 6, 0, 1);
                keep = Elevation.EffectiveWeight(latDeg, lonDeg) * t * t * (3 - 2 * t);
            }
            result = bed + (height - bed) * keep;
            if (level <= SeaLevelMeters)
            {
                double t = Math.Clamp((height - SeaCutFromMeters) / (SeaCutFullMeters - SeaCutFromMeters), 0, 1);
                result = height + (Math.Min(result, height) - height) * t * t * (3 - 2 * t);
            }
        }
        else if (-s < BankMeters && height > level)
        {
            double x = -s, ramp = Math.Clamp(x / FreeboardRampMeters, 0, 1), bank = level + BankFreeboardMeters * ramp * ramp * (3 - 2 * ramp) + BankSlope * x * x / (x + BankSoftMeters);
            double t = Math.Clamp((x - 0.45 * BankMeters) / (0.55 * BankMeters), 0, 1), w = t * t * (3 - 2 * t);
            double cut = Math.Min(result, Math.Min(height, bank) * (1 - w) + height * w);
            double beach = Math.Clamp((x - FreeboardRampMeters) / FreeboardRampMeters, 0, 1);
            result = result + (cut - result) * Math.Max(1 - Elevation.EffectiveWeight(latDeg, lonDeg), 1 - beach * beach * (3 - 2 * beach));
        }
        return result;
    }

    /// <summary>
    /// For a tile that is about to be placed on the terrain: waits (a worker thread; at most a few seconds) until the lake map
    /// covers its surroundings, so its roads and buildings are placed on the lowered banks and not on the old heights.
    /// Returns at once where there are no lakes.
    /// </summary>
    public static void WaitForMap(double latDeg, double lonDeg)
    {
        if (_failed || Settings is not { Enabled: true, Water: true } || Field is not { } field)
            return;
        var (tx, ty) = VectorTile.TileAt(WaterTile.Zoom, latDeg, lonDeg);
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + 20 * System.Diagnostics.Stopwatch.Frequency;
        while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline && !_failed)
        {
            if (!field.Active)
                return;                                                    // too high for water: no map, no lakes
            bool pending = false, lakes = false, covered = true;
            var included = _active?.Included;
            for (int dx = -1; dx <= 1 && !pending; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var tile = field.TileAt(tx + dx, ty + dy);
                    // This tile's own water tile is not asked for yet (the water scan runs once a second): wait for it.
                    if (tile == null)
                    {
                        pending |= dx == 0 && dy == 0;
                        continue;
                    }
                    if (tile.State is WaterTile.Stage.Queued or WaterTile.Stage.Loading)
                    {
                        pending = true;
                        break;
                    }
                    if (tile.State == WaterTile.Stage.Ready && tile.Lakes.Count > 0)
                    {
                        lakes = true;
                        covered &= included != null && included.Contains((tx + dx, ty + dy));
                    }
                }
            }
            if (!pending && !lakes)
                return;
            if (!pending && covered && Query(latDeg, lonDeg, out _) == 1)
                return;
            Thread.Sleep(100);
        }
    }

    // ---- shaders ----

    private const string UvAnchor = "    vec2 planetUV = CalculateSphericalUVAndGradients(planetTextureNormal, dxPlanet, dyPlanet);\n";
    private const string ShoreAnchor = "    float shoreFilterFade = GetShoreFilterFade(global.lighting.planetPosition.xyz, planetTextureNormal, dxPlanetCubemap, dyPlanetCubemap, landMask, shoreFilterFadeIn);\n";
    private const string ColorAnchor = "    scatteredColor *= gammaToLinear(textureGrad(colorMap, planetUV, dxPlanet, dyPlanet).rgb);\n";
    private const string HeightAnchor = "    worldPosition.xyz += worldNormal * heightAndGrad.x;\n";

    /// <summary>The ocean shader with the lake pass, or null if it is switched off or the game's shader has changed.</summary>
    public static string? PatchOcean(string source)
    {
        string? patched = PatchLakePass(source);
        if (Settings is not { Enabled: true, WaterReflections: false })
            return patched;
        const string fresnelAnchor = "    float fresnel = OceanFresnel(planarSpaceV, oceanNormal, F0, secondOrderMoments);\n";
        string text = patched ?? source;
        if (text.Split(fresnelAnchor).Length != 2)
            return patched;
        // No mirror: no sun glint, no sky reflection (fresnel scales it and takes from the refracted colour); water colour, refraction, foam stay.
        return text.Replace(fresnelAnchor, fresnelAnchor + "    fresnel = 0.0;\n    sunSpecular = vec3(0.0);\n");
    }

    private static string? PatchLakePass(string source)
    {
        if (Settings is not { Water: true })
            return null;
        foreach (string anchor in new[] { UvAnchor, ShoreAnchor, ColorAnchor })
            if (source.Split(anchor).Length != 2)
                return null;
        const string mask = """
                // GEE: lake pass (mods/GEE LakeOcean.cs): only inside the lakes of the map. Its red channel holds the signed distance to
                // the shore, its green channel the index of the level of the nearest lake; this pass draws the lakes of one index.
                float geeLakeDepth = 0.0;
                if (uboStaticOceanData.waveCount < 0)
                {
                    vec2 geeUv = (planetUV - vec2(uboStaticOceanData.waveAmplitude, uboStaticOceanData.waveLength))
                        / vec2(uboStaticOceanData.waveSpeed, uboStaticOceanData.amplitudePersistence);
                    float geeSigned = -1.0;
                    if (geeUv.x >= 0.0 && geeUv.x <= 1.0 && geeUv.y >= 0.0 && geeUv.y <= 1.0)
                    {
                        // (a sampler cannot be a local variable: a macro)
                        #define geeMap TEXTURE_SAMPLER(int(uboStaticOceanData.waveLengthPersistence + 0.5), globalConstants.clampSamplerHandle)
                        GEE_CUBIC(geeMap, geeUv, geeCubicValue)
                        geeSigned = geeCubicValue * 255.0 - 128.0;
                        int geeIndex = int(texelFetch(geeMap, ivec2(min(geeUv * GEE_MASKSIZE, vec2(GEE_MASKSIZE - 1.0))), 0).g * 255.0 + 0.5);
                        if (geeIndex != int(uboStaticOceanData.speedPersistence + 0.5))
                            geeSigned = -1.0;
                        #undef geeMap
                    }
                    if (geeSigned <= 0.0)
                        discard;
                    float geeInside = -GEE_SCALE * log(max(1.0 - geeSigned / 127.0, 0.001));
                    float geeLimit = clamp(uboStaticOceanData.oceanLevel - GEE_SEAFLOOR - GEE_SHOREBED, GEE_MINDEPTH, GEE_MAXDEPTH);
                    geeLakeDepth = geeLimit * (1.0 - exp(-GEE_SLOPE * geeInside / geeLimit));
                }

            """;
        const string shore = """
                if (uboStaticOceanData.waveCount < 0)
                {
                    shoreFilterFade = 0.0;
                    shoreFilterFadeIn = 0.0;
                }

            """;
        const string color = """
                if (uboStaticOceanData.waveCount < 0)
                {
                    // Lakes: the depth from the map (the bed is cut to it) sets how clear and how dark the water is: clear and
                    // light at the bank, deep and dark in the middle.
                    float geeOpacity = clamp(geeLakeDepth / 25.0, 0.25, 1.0);
                    transmittance = 1.0 - geeOpacity;
                    float geeDeep = smoothstep(0.0, 60.0, geeLakeDepth);
                    vec3 geeShallow = vec3(0.07, 0.26, 0.30), geeDark = vec3(0.012, 0.055, 0.105);
                    scatteredColor = unpackRGBA(uboStaticOceanData.color).rgb * geeOpacity * mix(geeShallow, geeDark, geeDeep);
                }
                else
                {
                    scatteredColor *= gammaToLinear(textureGrad(colorMap, planetUV, dxPlanet, dyPlanet).rgb);
                }

            """;
        return source.Replace(UvAnchor, UvAnchor + Constants(mask)).Replace(ShoreAnchor, ShoreAnchor + shore).Replace(ColorAnchor, color);
    }

    /// <summary>
    /// GEE_CUBIC(sampler, uv, name) becomes a block declaring float name: the red channel of the map filtered with a Catmull-Rom
    /// spline (five bilinear fetches) instead of bilinearly. Bilinear interpolation of the distance field has a kink at every
    /// texel edge, which shows as a saw-tooth in the zero contour (the shoreline); the cubic's contour is smooth.
    /// </summary>
    private static string ExpandCubic(string text)
    {
        while (true)
        {
            int at = text.IndexOf("GEE_CUBIC(", StringComparison.Ordinal);
            if (at < 0)
                return text;
            int depth = 0, end = at + "GEE_CUBIC".Length;
            var parts = new List<string>();
            int begin = end + 1;
            for (int i = end; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) { parts.Add(text.Substring(begin, i - begin).Trim()); end = i + 1; break; }
                else if (c == ',' && depth == 1) { parts.Add(text.Substring(begin, i - begin).Trim()); begin = i + 1; }
            }
            string s = parts[0], uv = parts[1], name = parts[2];
            string block = $$"""
                float {{name}};
                {
                    const vec2 gcSize = vec2(GEE_MASKSIZE);
                    vec2 gcPos = ({{uv}}) * gcSize;
                    vec2 gcC = floor(gcPos - 0.5) + 0.5, gcF = gcPos - gcC;
                    vec2 gcW0 = gcF * (-0.5 + gcF * (1.0 - 0.5 * gcF));
                    vec2 gcW1 = 1.0 + gcF * gcF * (-2.5 + 1.5 * gcF);
                    vec2 gcW2 = gcF * (0.5 + gcF * (2.0 - 1.5 * gcF));
                    vec2 gcW3 = gcF * gcF * (-0.5 + 0.5 * gcF);
                    vec2 gcW12 = gcW1 + gcW2;
                    vec2 gcT0 = (gcC - 1.0) / gcSize, gcT3 = (gcC + 2.0) / gcSize, gcT12 = (gcC + gcW2 / gcW12) / gcSize;
                    float gcSum = gcW12.x * gcW0.y + gcW0.x * gcW12.y + gcW12.x * gcW12.y + gcW3.x * gcW12.y + gcW12.x * gcW3.y;
                    {{name}} = (textureLod({{s}}, vec2(gcT12.x, gcT0.y), 0.0).r * gcW12.x * gcW0.y
                        + textureLod({{s}}, vec2(gcT0.x, gcT12.y), 0.0).r * gcW0.x * gcW12.y
                        + textureLod({{s}}, gcT12, 0.0).r * gcW12.x * gcW12.y
                        + textureLod({{s}}, vec2(gcT3.x, gcT12.y), 0.0).r * gcW3.x * gcW12.y
                        + textureLod({{s}}, vec2(gcT12.x, gcT3.y), 0.0).r * gcW12.x * gcW3.y) / gcSum;
                }
                """;
            text = text.Substring(0, at) + block + text.Substring(end);
        }
    }

    /// <summary>The shader text with the map's numbers filled in.</summary>
    public static string Constants(string text)
    {
        text = ExpandCubic(text);
        string F(double v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
        return text.Replace("GEE_MAXDEPTH", F(MaxDepthMeters)).Replace("GEE_SEALEVEL", F(SeaLevelMeters)).Replace("GEE_TIDALDEPTH", F(TidalDepthMeters)).Replace("GEE_SEAFLOOR", F(SeaFloorMeters)).Replace("GEE_MINDEPTH", F(MinLakeDepthMeters)).Replace("GEE_TABLE", LevelTable.ToString()).Replace("GEE_MASKSIZE", F(MaskSize)).Replace("GEE_SLOPE", F(SlopeMeters))
            .Replace("GEE_SCALE", F(DistanceScaleMeters)).Replace("GEE_BANKFREE", F(BankFreeboardMeters)).Replace("GEE_FREERAMP", F(FreeboardRampMeters)).Replace("GEE_SHOREBED", F(ShoreBedMeters)).Replace("GEE_BANKSLOPE", F(BankSlope)).Replace("GEE_BANKSOFT", F(BankSoftMeters))
            .Replace("GEE_BANK", F(BankMeters)).Replace("GEE_SHOREBLEND", F(ShoreBlendMeters)).Replace("GEE_FULL", Settings is { RealTerrain: true } ? "1.0" : "0.0")
            .Replace("GEE_DEMSIZE", F(ElevationWindow.Size)).Replace("GEE_DEMFADE", F(ElevationWindow.FadeShare))
            .Replace("GEE_PARAMS", ParamsSlot.ToString()).Replace("GEE_ATLAS", AtlasSlot.ToString());
    }

    /// <summary>The terrain mesh shader with the lake beds, or null if it is switched off or the game's shader has changed.</summary>
    private const string BaseAnchor = "    float baseHeight = maxHeight * heightSample + minHeight * (1.0 - heightSample);\n";
    private const string MainAnchor = "void main()\n{\n";

    /// <summary>GLSL for both terrain mesh shaders: the numbers of row 0 and the weight and height of the real elevation at a direction.</summary>
    private const string Prelude = """
        // ---- GEE: window numbers and real elevation (mods/GEE LakeOcean.cs, Elevation.cs) ----
        float geeParam(int i)
        {
            return texelFetch(sampler2D(planetModifierTextures[GEE_PARAMS], planetBiomeSampler), ivec2(i, 0), 0).r;
        }

        float geeDemTexel(int x, int y)
        {
            return texelFetch(sampler2D(planetModifierTextures[GEE_PARAMS], planetBiomeSampler), ivec2(x, y + 1), 0).r;
        }

        // The weight (0..1) of the real elevation at a direction, and the elevation itself; 0 where there is none.
        float geeDemHeight(vec3 dir, out float dem)
        {
            dem = 0.0;
            if (geeParam(12) < 0.5)
                return 0.0;
            vec2 uv = vec2(0.5 + atan(dir.y, dir.x) * 0.15915494, 0.5 - asin(clamp(dir.z, -1.0, 1.0)) * 0.31830989);
            vec2 t = (uv - vec2(geeParam(8), geeParam(9))) / vec2(geeParam(10), geeParam(11));
            if (t.x < 0.0 || t.x > 1.0 || t.y < 0.0 || t.y > 1.0)
                return 0.0;
            vec2 f = t * GEE_DEMSIZE - 0.5;
            ivec2 i = clamp(ivec2(floor(f)), ivec2(0), ivec2(int(GEE_DEMSIZE) - 2));
            vec2 a = clamp(f - vec2(i), 0.0, 1.0);
            float h00 = geeDemTexel(i.x, i.y), h10 = geeDemTexel(i.x + 1, i.y), h01 = geeDemTexel(i.x, i.y + 1), h11 = geeDemTexel(i.x + 1, i.y + 1);
            if (min(min(h00, h10), min(h01, h11)) <= -19999.0)
                return 0.0;
            dem = mix(mix(h00, h10, a.x), mix(h01, h11, a.x), a.y);
            return smoothstep(0.0, GEE_DEMFADE, min(min(t.x, 1.0 - t.x), min(t.y, 1.0 - t.y)));
        }

        // 1 in a lake and at its shore, falling to 0 over GEE_SHOREBLEND metres; 0 where the lake map has no lake.
        float geeLakeNear(vec3 dir)
        {
            if (geeParam(2) <= 0.0 || geeParam(7) < 0.5)
                return 0.0;
            vec2 uv = vec2(0.5 + atan(dir.y, dir.x) * 0.15915494, 0.5 - asin(clamp(dir.z, -1.0, 1.0)) * 0.31830989);
            vec2 w = (uv - vec2(geeParam(0), geeParam(1))) / vec2(geeParam(2), geeParam(3));
            if (w.x < 0.0 || w.x > 1.0 || w.y < 0.0 || w.y > 1.0)
                return 0.0;
            float signedDistance = textureLod(sampler2D(planetModifierTextures[GEE_ATLAS], planetBiomeSampler), w, 0.0).r * 255.0 - 128.0;
            float meters = -GEE_SCALE * log(max(1.0 - abs(signedDistance) / 127.0, 0.001));
            return signedDistance > 0.0 ? 1.0 : 1.0 - smoothstep(0.0, GEE_SHOREBLEND, meters);
        }

        // The weight the real elevation has here: all of it with RealTerrain, only near lakes with RealShores.
        float geeDemWeight(vec3 dir, out float dem)
        {
            float w = geeDemHeight(dir, dem);
            if (w <= 0.0)
                return 0.0;
            return w * mix(geeLakeNear(dir), 1.0, GEE_FULL);
        }

        """;

    /// <summary>The real elevation replacing the heightmap value (baseHeight) at textureNormal; shared by PrepareModifiers and the clutter shaders.</summary>
    private const string DemBlock = """
                // GEE: real elevation (mods/GEE Elevation.cs) in place of the game's base map; the game's modifiers add their detail on top.
                float geeDemH;
                float geeDemW = geeDemWeight(textureNormal, geeDemH);
                if (geeDemW > 0.0)
                {
                    // Low land stays dry: where the game's heightmap says land, the real elevation does not drop below its own height
                    // (at most 3 m). Marsh and coast are 0 to 2 m in the data, with noise of a metre: the sea at 0 m would fill every dip.
                    // (From 0.2 m up, not 1 m: a game height of 0.5 m is dry too, and Merritt Island's marsh flooded in patches wherever the data dipped below 0.)
                    if (baseHeight >= 0.2)
                        geeDemH = max(geeDemH, min(baseHeight, 3.0));
                    else
                        // The game's sea stays the game's: the data is a flat 0 m over open water in most zoom 12 tiles but real
                        // bathymetry (-10 m and deeper) in some, which showed as straight tile edges in the shelf. Only data clearly
                        // above the sea (land) counts; below it the game's heightmap (depressions included) stays.
                        geeDemW *= smoothstep(0.5, 1.5, geeDemH);
                    baseHeight = mix(baseHeight, geeDemH, geeDemW);
                }

        """;

    /// <summary>Two bindless handles for the map and the numbers (pointing at the library's blank texture until they exist). False if the library is not there.</summary>
    public static bool ReserveHandles()
    {
        if (_handle >= 0)
            return true;
        try
        {
            if (Program.Instance?.BindlessTextures is not { } library)
                return false;
            var blank = typeof(BindlessTextureLibrary).GetField("_emptyTexture", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(library) as SimpleVkTexture;
            if (blank == null)
                return false;
            _handle = library.AddTexture(blank.VkImageView);
            _paramsHandle = library.AddTexture(blank.VkImageView);
            _riverHandle = library.AddTexture(blank.VkImageView);
            _riverFarHandle = library.AddTexture(blank.VkImageView);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"lakes: handles: {e.Message}");
            return false;
        }
    }

    private const string PlanetAnchor = "    vec3 surfaceColor = blendedDiffuse.xyz;\n";

    /// <summary>
    /// The terrain's colour shader (Planet.frag) with the wet shore of lakes, or null if it is switched off or the game's shader
    /// has changed: the ground at the water turns to sand with a dark wet edge, under the water to a sandy, then muddy bed.
    /// Reads the lake map through bindless handles (no loops: the driver compiles this shader at every start).
    /// </summary>
    public static string? PatchPlanetShore(string source)
    {
        bool wet = Settings is { Water: true, WetShores: true }, rivers = Settings is { Rivers: true };
        if (!(wet || rivers) || source.Split(PlanetAnchor).Length != 2 || !ReserveHandles())
            return null;
        const string shore = """
                // GEE: the wet shore of lakes (mods/GEE LakeOcean.cs): sand and a dark wet edge at the water, a sandy bed under it.
                if (texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(13, 0), 0).r == 12345.0)
                {
                    float geeLakes = texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(7, 0), 0).r;
                    vec4 geeWin = vec4(texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(0, 0), 0).r,
                        texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(1, 0), 0).r,
                        texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(2, 0), 0).r,
                        texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(3, 0), 0).r);
                    if (geeLakes > 0.5 && geeWin.z > 0.0)
                    {
                        vec3 geeD = normalize(mat3(uboPlanet.cce2CcfMatrix) * sphereVector);
                        vec2 geeUv = vec2(0.5 + atan(geeD.y, geeD.x) * 0.15915494, 0.5 - asin(clamp(geeD.z, -1.0, 1.0)) * 0.31830989);
                        vec2 geeW = (geeUv - geeWin.xy) / geeWin.zw;
                        if (geeW.x >= 0.0 && geeW.x <= 1.0 && geeW.y >= 0.0 && geeW.y <= 1.0)
                        {
                            GEE_CUBIC(sampler2D(globalTextures[GEE_HATLAS], GLOBAL_CLAMP_SAMPLER), geeW, geeCubicValue)
                            float geeBest = geeCubicValue * 255.0 - 128.0;
                            float geeM = -GEE_SCALE * log(max(1.0 - abs(geeBest) / 127.0, 0.001));
                            float geeLum = dot(blendedDiffuse, vec3(0.2126, 0.7152, 0.0722));
                            vec3 geeSand = mix(vec3(0.24, 0.19, 0.11), vec3(geeLum), 0.25) * (0.55 + 0.4 * geeLum);
                            if (geeBest > 0.0)
                            {
                                geeInsideLake = 1.0;
                                // under the water: sand in the shallows, mud further out (a pond a hundred metres wide is already mud: its sand bed shone through the
                                // clear water as a flat white patch)
                                float geeDepth = GEE_MAXDEPTH * (1.0 - exp(-GEE_SLOPE * geeM / GEE_MAXDEPTH));
                                // the sea's own bed (cut under the real sea, below the ocean level): sand with only a little mud, so the
                                // game's turquoise shelf shading is not lost to a dark bed
                                float geeSeabed = 1.0 - smoothstep(-0.3, 0.1, length(sphereVector) - global.lighting.planetRadius - uboPlanet.oceanLevel);
                                float geeSeaDepth = GEE_TIDALDEPTH * (1.0 - exp(-GEE_SLOPE * geeM / GEE_TIDALDEPTH));
                                vec3 geeBedLake = mix(geeSand * 0.8, vec3(0.06, 0.05, 0.035), smoothstep(0.0, 5.0, min(geeDepth, 5.0)));
                                float geeD = max(-(length(sphereVector) - global.lighting.planetRadius - uboPlanet.oceanLevel), 0.0);
                                vec3 geeBedSea = mix(mix(vec3(0.50, 0.45, 0.33) * (0.9 + 0.2 * geeLum), vec3(0.07, 0.26, 0.27), smoothstep(0.2, 2.0, geeD)), vec3(0.03, 0.13, 0.17), smoothstep(2.0, 8.0, geeD));
                                // The sea's bed fades back to the game's own shelf shading with height, as it does outside the map (below): otherwise the
                                // tidal pieces inside the map showed as rectangles of another colour from altitude.
                                float geeInAlt = 1.0 - smoothstep(8000.0, 20000.0, length(vec3(0.0) - inCelestialPosition.xyz) - global.lighting.planetRadius);
                                blendedDiffuse = mix(blendedDiffuse, mix(geeBedLake, geeBedSea, geeSeabed), mix(1.0, geeInAlt, geeSeabed));
                            }
                            else
                            {
                                // on land: a narrow sand band (to about 12 m from the water; it was 30 m and bright: round grey-white halos around
                                // every marsh pond in the sun's glare), wet and dark in the first few metres
                                float geeSandBand = 1.0 - smoothstep(2.0, 12.0, geeM);
                                float geeWet = 1.0 - smoothstep(0.0, 5.0, geeM);
                                blendedDiffuse = mix(blendedDiffuse, geeSand, 0.4 * geeSandBand) * mix(1.0, 0.45, geeWet);
                            }
                        }
                    }
                }
                // The sea bed outside the lake map (its window is only 24 km wide: a straight edge to the game's own beige shelf there):
                // the same bed colour by depth, so the window's edge is invisible.
                if (geeInsideLake < 0.5)
                {
                    float geeOutD = -(length(sphereVector) - global.lighting.planetRadius - uboPlanet.oceanLevel);
                    // GEE works up to 20 km: above it the sea looks like the game's own (fading out from 8 km)
                    float geeOutAlt = 1.0 - smoothstep(8000.0, 20000.0, length(vec3(0.0) - inCelestialPosition.xyz) - global.lighting.planetRadius);
                    if (geeOutD > 0.3 && geeOutAlt > 0.0)
                    {
                        float geeOutLum = dot(blendedDiffuse, vec3(0.2126, 0.7152, 0.0722));
                        vec3 geeOutBed = mix(mix(vec3(0.50, 0.45, 0.33) * (0.9 + 0.2 * geeOutLum), vec3(0.07, 0.26, 0.27), smoothstep(0.2, 2.0, geeOutD)), vec3(0.03, 0.13, 0.17), smoothstep(2.0, 8.0, geeOutD));
                        blendedDiffuse = mix(blendedDiffuse, geeOutBed, smoothstep(0.3, 1.0, geeOutD) * geeOutAlt);
                    }
                }

            """;
        const string river = """
                // GEE: rivers (mods/GEE LakeOcean.cs, RiverMap): the maps' coverage paints the water: dark and smooth like a mirror, the game's own sun
                // and sky do the rest. A near map (fine, small) and a far one (the big rivers out to the horizon, hidden where the near one is).
                if (geeInsideLake < 0.5 && texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(13, 0), 0).r == 12345.0)
                {
                    float geeRiver = 0.0;
                    float geeNearIn = 0.0;
                    vec3 geeRD = normalize(mat3(uboPlanet.cce2CcfMatrix) * sphereVector);
                    vec2 geeRUv = vec2(0.5 + atan(geeRD.y, geeRD.x) * 0.15915494, 0.5 - asin(clamp(geeRD.z, -1.0, 1.0)) * 0.31830989);
                    if (texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(20, 0), 0).r > 0.5)
                    {
                        vec4 geeRiverWin = vec4(texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(16, 0), 0).r,
                            texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(17, 0), 0).r,
                            texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(18, 0), 0).r,
                            texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(19, 0), 0).r);
                        vec2 geeRW = (geeRUv - geeRiverWin.xy) / geeRiverWin.zw;
                        if (geeRW.x >= 0.0 && geeRW.x <= 1.0 && geeRW.y >= 0.0 && geeRW.y <= 1.0)
                        {
                            geeNearIn = smoothstep(0.0, 0.06, min(min(geeRW.x, 1.0 - geeRW.x), min(geeRW.y, 1.0 - geeRW.y)));
                            geeRiver = textureLod(sampler2D(globalTextures[GEE_HRIVER], GLOBAL_CLAMP_SAMPLER), geeRW, 0.0).r * geeNearIn;
                        }
                    }
                    if (texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(25, 0), 0).r > 0.5)
                    {
                        vec4 geeFarWin = vec4(texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(21, 0), 0).r,
                            texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(22, 0), 0).r,
                            texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(23, 0), 0).r,
                            texelFetch(sampler2D(globalTextures[GEE_HPARAMS], pointSampler), ivec2(24, 0), 0).r);
                        vec2 geeFW = (geeRUv - geeFarWin.xy) / geeFarWin.zw;
                        if (geeFW.x >= 0.0 && geeFW.x <= 1.0 && geeFW.y >= 0.0 && geeFW.y <= 1.0)
                        {
                            float geeFar = textureLod(sampler2D(globalTextures[GEE_HFAR], GLOBAL_CLAMP_SAMPLER), geeFW, 0.0).r;
                            geeFar *= smoothstep(0.0, 0.04, min(min(geeFW.x, 1.0 - geeFW.x), min(geeFW.y, 1.0 - geeFW.y)));
                            geeRiver = max(geeRiver, geeFar * (1.0 - geeNearIn));
                        }
                    }
                    if (geeRiver > 0.01)
                    {
                        blendedDiffuse = mix(blendedDiffuse, vec3(0.018, 0.050, 0.055), 0.92 * geeRiver);
                        roughness = mix(roughness, 1.0, geeRiver);
                        metallic = mix(metallic, 0.0, geeRiver);
                        worldNormal = normalize(mix(worldNormal, sphereWorldNormal, geeRiver));
                    }
                }

            """;
        string text = "    float geeInsideLake = 0.0;\n" + (wet ? Constants(shore) : "") + (rivers ? Constants(river) : "");
        text = text.Replace("GEE_HPARAMS", _paramsHandle.ToString()).Replace("GEE_HATLAS", _handle.ToString()).Replace("GEE_HFAR", _riverFarHandle.ToString()).Replace("GEE_HRIVER", _riverHandle.ToString());
        return source.Replace(PlanetAnchor, text + PlanetAnchor);
    }

    /// <summary>The terrain base-height shader (PrepareModifiers) with the real elevation instead of the heightmap, or null.</summary>
    public static string? PatchPrepare(string source)
    {
        if (Settings is not { } real || !(real.RealTerrain || real.RealShores) || source.Split(BaseAnchor).Length != 2 || source.Split(MainAnchor).Length != 2)
            return null;
        return source.Replace(MainAnchor, Constants(Prelude) + MainAnchor).Replace(BaseAnchor, BaseAnchor + Constants(DemBlock));
    }


    /// <summary>
    /// GLSL function geeLakeHeight(dir, h, wet) for the terrain (FinalizeModifiers) and the ground clutter (the Finalize shaders): the lake bed and
    /// bank height at a direction, given the height the game computes there (base map and modifiers). wet is 1 where the point ends up under
    /// the water (clutter is dropped there). Needs Prelude. Keep in step with AdjustHeight.
    /// </summary>
    private const string LakeCut = """
        float geeLakeHeight(vec3 textureNormal, float h, out float wet)
        {
            float geeHeight = h;
                wet = 0.0;
                if (geeParam(2) > 0.0 && geeParam(7) > 0.5)
                {
                    vec2 geeUv = vec2(0.5 + atan(textureNormal.y, textureNormal.x) * 0.15915494, 0.5 - asin(clamp(textureNormal.z, -1.0, 1.0)) * 0.31830989);
                    vec2 geeW = (geeUv - vec2(geeParam(0), geeParam(1))) / vec2(geeParam(2), geeParam(3));
                    if (geeW.x >= 0.0 && geeW.x <= 1.0 && geeW.y >= 0.0 && geeW.y <= 1.0)
                    {
                        float geeDemH;
                        float geeDemW = geeDemWeight(textureNormal, geeDemH);
                        // The nearest lake: its signed shore distance (filtered) and the index of its level (the nearest texel).
                        #define geeAtlas sampler2D(planetModifierTextures[GEE_ATLAS], planetBiomeSampler)
                        int geeIndex = int(texelFetch(geeAtlas, ivec2(min(geeW * GEE_MASKSIZE, vec2(GEE_MASKSIZE - 1.0))), 0).g * 255.0 + 0.5);
                        if (geeIndex > 0)
                        {
                            float geeLevel = geeParam(GEE_TABLE + geeIndex);
                            GEE_CUBIC(geeAtlas, geeW, geeCubicValue)
                            float geeSigned = geeCubicValue * 255.0 - 128.0;
                            float geeMeters = -GEE_SCALE * log(max(1.0 - abs(geeSigned) / 127.0, 0.001));
                            if (geeSigned > 0.0)
                            {
                                // The bed gets no deeper than the lake's limit: shallow lakes (Okeechobee) must not sink below the sea.
                                float geeLimit = geeLevel <= GEE_SEALEVEL ? GEE_TIDALDEPTH : clamp(geeLevel - GEE_SEAFLOOR - GEE_SHOREBED, GEE_MINDEPTH, GEE_MAXDEPTH);
                                float geeBedHeight = geeLevel - GEE_SHOREBED - geeLimit * (1.0 - exp(-GEE_SLOPE * geeMeters / geeLimit));
                                // Where the real elevation is in use, a bank that stands clearly above the water is kept: the lake map's edge is
                                // coarse (texels of tens of metres), and cutting a steep shore down to the bed made stairs along it.
                                float geeKeep = geeDemW * smoothstep(geeLevel + 2.0, geeLevel + 8.0, geeDemH);
                                geeHeight = mix(geeBedHeight, h, geeKeep);
                                // The sea's own level: only land (and the very shallow) is lowered; the game's shelf under the water stays as it is
                                // (the cut would otherwise deepen its turquoise shallows to the cut's depth).
                                if (geeLevel <= GEE_SEALEVEL)
                                    geeHeight = mix(h, min(geeHeight, h), smoothstep(-1.0, 0.3, h));
                                // Under the water (the bed was not kept up): nothing stands there.
                                wet = geeHeight < geeLevel - 0.2 ? 1.0 : 0.0;
                            }
                            else if (geeMeters < GEE_BANK && h > geeLevel)
                            {
                                // A natural bank: two metres above the water at the shore, a few degrees further out; the terrain is cut down to it
                                // where it is higher, and left alone from about half the bank's width outwards.
                                float geeBank = geeLevel + GEE_BANKFREE * smoothstep(0.0, GEE_FREERAMP, geeMeters) + GEE_BANKSLOPE * geeMeters * geeMeters / (geeMeters + GEE_BANKSOFT);
                                float geeCut = min(geeHeight, mix(min(h, geeBank), h, smoothstep(0.45 * GEE_BANK, GEE_BANK, geeMeters)));
                                // (with the real elevation too, for the first FREERAMP..2*FREERAMP metres: a beach ramp instead of a step at the water)
                                geeHeight = mix(geeHeight, geeCut, max(1.0 - geeDemW, 1.0 - smoothstep(GEE_FREERAMP, 2.0 * GEE_FREERAMP, geeMeters)));
                            }
                        }
                        #undef geeAtlas
                    }
                }
            return geeHeight;
        }

        """;

    /// <summary>The terrain mesh shader with the lake beds, or null if it is switched off or the game's shader has changed.</summary>
    public static string? PatchTerrain(string source)
    {
        if (Settings is not { Water: true } || source.Split(HeightAnchor).Length != 2 || source.Split(MainAnchor).Length != 2)
            return null;
        const string bed = """
                // GEE: lake beds (mods/GEE LakeOcean.cs): the height is final here (base map and all modifiers added up); see LakeCut.
                vec3 textureNormal = GetAnchoredDirection(pushConsts.textureMatrix, position).direction;
                float geeWetUnused;
                float geeHeight = geeLakeHeight(textureNormal, heightAndGrad.x, geeWetUnused);
                worldPosition.xyz += worldNormal * geeHeight;

            """;
        string text = Constants(bed);
        return source.Replace(MainAnchor, Constants(Prelude + LakeCut) + MainAnchor).Replace(HeightAnchor, text);
    }

    // ---- ground clutter ----

    private const string ClutterHeightFunction = "vec4 GetHeightGradient(CellData cellData, AnchoredDirection anchoredDir)\n{\n";
    private const string ClutterSnapAnchor = "        vec3 dir2 = CollapseAnchoredDir(cellAnchorHi, cellAnchorLo, delta2);\n";
    private const string ClutterProbeAnchor = "        vec3 sphereDir = CollapseAnchoredDir(cellAnchorHi, cellAnchorLo, delta0);\n";
    private const string ClutterSurviveAnchor = "        survived = survived && (altitudeLut > random);\n";

    /// <summary>
    /// The ground clutter shaders with the terrain GEE makes. The game places clutter on its own copy of the height (the heightmap
    /// plus the modifiers in Generate*.comp, or the terrain mesh's accumulated height before FinalizeModifiers), so without this
    /// trees stand on the old ground: they float over a lowered lake bank or bed and over data-corrected land. Generate.comp and
    /// GenerateCollision.comp get the real elevation in their base height (like PrepareModifiers), FinalizeGenerate*.comp the lake
    /// cut on the final height (like FinalizeModifiers), and clutter that ends up under the water is dropped. Null when nothing
    /// applies (off, another file, or the game's shader has changed).
    /// </summary>
    public static string? PatchClutter(string fileName, string source)
    {
        if (Settings is not { } settings)
            return null;
        bool generate = fileName.Equals("Generate.comp", StringComparison.OrdinalIgnoreCase) || fileName.Equals("GenerateCollision.comp", StringComparison.OrdinalIgnoreCase);
        bool finalize = fileName.Equals("FinalizeGenerate.comp", StringComparison.OrdinalIgnoreCase) || fileName.Equals("FinalizeGenerateCollision.comp", StringComparison.OrdinalIgnoreCase);
        if (generate)
        {
            if (!(settings.RealTerrain || settings.RealShores) || source.Split(ClutterHeightFunction).Length != 2 || source.Split(BaseAnchor).Length != 2)
                return null;
            string use = "    {\n        vec3 textureNormal = sphereDir;\n" + DemBlock + "    }\n";
            return source.Replace(ClutterHeightFunction, Constants(Prelude) + ClutterHeightFunction).Replace(BaseAnchor, BaseAnchor + Constants(use));
        }
        if (finalize)
        {
            bool snap = source.Contains("#if defined (SNAP_TO_MESH)");
            if (!settings.Water || source.Split(MainAnchor).Length != 2 || source.Split(ClutterSurviveAnchor).Length != 2
                || source.Split(ClutterProbeAnchor).Length != 2 || (snap && source.Split(ClutterSnapAnchor).Length != 2))
                return null;
            const string probes = """
                // GEE: the lake bed and bank on the clutter's ground too (mods/GEE LakeOcean.cs).
                float geeWet, geeProbeWet;
                interpolatedHeight0 = geeLakeHeight(sphereDir, interpolatedHeight0, geeWet);
                interpolatedHeight1 = geeLakeHeight(normalize(CollapseAnchoredDir(cellAnchorHi, cellAnchorLo, delta1)), interpolatedHeight1, geeProbeWet);
                interpolatedHeight2 = geeLakeHeight(normalize(CollapseAnchoredDir(cellAnchorHi, cellAnchorLo, delta2)), interpolatedHeight2, geeProbeWet);
                interpolatedHeight3 = geeLakeHeight(normalize(CollapseAnchoredDir(cellAnchorHi, cellAnchorLo, delta3)), interpolatedHeight3, geeProbeWet);
                interpolatedHeight4 = geeLakeHeight(normalize(CollapseAnchoredDir(cellAnchorHi, cellAnchorLo, delta4)), interpolatedHeight4, geeProbeWet);

            """;
            const string snapProbes = """
                // GEE: the lake bed and bank on the clutter's ground too (mods/GEE LakeOcean.cs); the mesh heights are those before the cut.
                float geeWet0, geeWet1, geeWet2;
                interpolatedHeight0 = geeLakeHeight(normalize(dir0), interpolatedHeight0, geeWet0);
                interpolatedHeight1 = geeLakeHeight(normalize(dir1), interpolatedHeight1, geeWet1);
                interpolatedHeight2 = geeLakeHeight(normalize(dir2), interpolatedHeight2, geeWet2);
                float geeWet = (geeWet0 + geeWet1 + geeWet2) > 1.5 ? 1.0 : 0.0;

            """;
            string result = source.Replace(MainAnchor, Constants(Prelude + LakeCut) + MainAnchor)
                .Replace(ClutterSurviveAnchor, ClutterSurviveAnchor + "        survived = survived && geeWet < 0.5;\n");
            if (snap)
            {
                result = result.Replace(ClutterSnapAnchor, ClutterSnapAnchor + Constants(snapProbes));
                // The non-snap branch of the same file (#else): its sphereDir line comes after delta0..4 exist.
                return result.Replace(ClutterProbeAnchor, ClutterProbeAnchor + Constants(probes));
            }
            return result.Replace(ClutterProbeAnchor, ClutterProbeAnchor + Constants(probes));
        }
        return null;
    }

    // ---- map ----

    private static int _lastTileHash;
    private static double _tileHashAt = -100;
    private static ulong _lastFingerprint;
    private static Result? _lastResult;

    /// <summary>Main thread, once per frame: asks for a new map window when the camera has left the old one or the water changed.</summary>
    public static void Update(Celestial body, double3 cameraCcf)
    {
        if (_failed || Settings is not { Enabled: true, Water: true } || Field is not { Active: true } field)
        {
            ActivePasses = 0;
            return;
        }
        double r = Math.Sqrt(double3.Dot(cameraCcf, cameraCcf));
        double lat = Math.Asin(Math.Clamp(cameraCcf.Z / r, -1, 1)), lon = Math.Atan2(cameraCcf.Y, cameraCcf.X);
        double planetR = body.MeanRadius;
        double dLat = (lat - _winLat) * planetR, dLon = (lon - _winLon) * planetR * Math.Cos(lat);
        double now = Clock.Elapsed.TotalSeconds;
        // Which tiles with lakes lie in the window: the map only needs a rebuild when these change.
        // Worked out four times a second, not every frame: it walks every water tile with trigonometry, and the
        // rebuild it feeds is itself limited to once per 1.5 s.
        int hash = _lastTileHash;
        if (now - _tileHashAt >= 0.25 || !_haveWindow)
        {
            hash = 17;
            double cosLat = Math.Cos(lat);
            foreach (var tile in field.Snapshot)
            {
                if (tile.Lakes.Count == 0)
                    continue;
                double ex = Math.Atan2(tile.CenterCcf.Y, tile.CenterCcf.X) - lon, ny = Math.Asin(Math.Clamp(tile.CenterCcf.Z / planetR, -1, 1)) - lat;
                if (Math.Sqrt(Math.Pow(ex * cosLat, 2) + ny * ny) * planetR < WindowMeters * 0.75 + tile.RadiusM)
                    hash += (tile.X * 73856093) ^ (tile.Y * 19349663);
            }
            _lastTileHash = hash;
            _tileHashAt = now;
        }
        // The levels come from the real elevation: no lakes before its first window (unless it does not come at all).
        var real = Elevation.Active;
        if (real == null && (Settings.RealTerrain || Settings.RealShores) && now < 120)
        {
            ActivePasses = 0;
            return;
        }
        if (real != null)
            hash = hash * 31 + real.Version;
        bool moved = !_haveWindow || Math.Sqrt(dLat * dLat + dLon * dLon) > RebuildDistanceM
            || (hash != _winHash && now - _lastBuildAt > 1.5);
        if (moved && Interlocked.CompareExchange(ref _building, 1, 0) == 0)
        {
            var tiles = field.Snapshot;
            _winLat = lat;
            _winLon = lon;
            _winHash = hash;
            _haveWindow = true;
            _lastBuildAt = now;
            Task.Run(() =>
            {
                try
                {
                    var built = Build(tiles, lat, lon, planetR);
                    // The same map as the one in use (a tile loaded far from any lake): nothing to upload, nothing to flicker.
                    if (_active is not { } current || current.Signature != built.Signature)
                        _ready = built;
                    else
                        current.Included = built.Included;
                }
                catch (Exception e)
                {
                    Log.Error($"lakes: map failed: {e}");
                }
                finally
                {
                    Interlocked.Exchange(ref _building, 0);
                }
            });
        }
        ActivePasses = _active?.Levels.Length ?? 0;
    }

    private sealed class Lake(WaterTile tile, LakeInfo info)
    {
        public readonly WaterTile Tile = tile;
        public readonly LakeInfo Info = info;
        public int Parent = -1;
    }

    private static int Find(List<Lake> lakes, int i)
    {
        while (lakes[i].Parent != i)
        {
            lakes[i].Parent = lakes[lakes[i].Parent].Parent;
            i = lakes[i].Parent;
        }
        return i;
    }

    private static Result Build(WaterTile[] tiles, double lat, double lon, double planetR)
    {
        // The window's scale comes from the whole degree of latitude and its corner snaps to a grid of 64 texels, so every
        // build rasterises the same world texels: the shoreline does not shimmer from one build to the next.
        double snapLat = Math.Round(lat * 180 / Math.PI) * Math.PI / 180;
        double du = WindowMeters / (2 * Math.PI * planetR * Math.Cos(snapLat)), dv = WindowMeters / (Math.PI * planetR);
        double uc = 0.5 + lon / (2 * Math.PI), vc = 0.5 - lat / Math.PI;
        double stepU = du * 64 / MaskSize, stepV = dv * 64 / MaskSize;
        double u0 = Math.Floor((uc - du / 2) / stepU) * stepU, v0 = Math.Floor((vc - dv / 2) / stepV) * stepV;
        var camera = new double3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat)) * planetR;

        // The lakes of tiles near the window; lakes meeting at a tile edge are one lake.
        var lakes = new List<Lake>();
        var byTile = new Dictionary<(int, int), List<int>>();
        foreach (var tile in tiles)
        {
            if ((tile.CenterCcf - camera).Length() > WindowMeters * 0.75 + tile.RadiusM)
                continue;
            foreach (var info in tile.Lakes)
            {
                lakes.Add(new Lake(tile, info) { Parent = lakes.Count });
                if (!byTile.TryGetValue((tile.X, tile.Y), out var list))
                    byTile[(tile.X, tile.Y)] = list = new();
                list.Add(lakes.Count - 1);
            }
        }
        for (int i = 0; i < lakes.Count; i++)
        {
            var (tile, info) = (lakes[i].Tile, lakes[i].Info);
            if (info.IsRiver)
                continue;                                                  // a river slopes: every tile's piece has its own level
            if ((info.Edges & 2) != 0 && byTile.TryGetValue((tile.X + 1, tile.Y), out var east))
                foreach (int j in east.Where(j => (lakes[j].Info.Edges & 1) != 0 && !lakes[j].Info.IsRiver))
                    Union(lakes, i, j);
            if ((info.Edges & 8) != 0 && byTile.TryGetValue((tile.X, tile.Y + 1), out var south))
                foreach (int j in south.Where(j => (lakes[j].Info.Edges & 4) != 0 && !lakes[j].Info.IsRiver))
                    Union(lakes, i, j);
        }

        // Level of each lake: low end of the shore heights of all its tiles. Lakes at the sea are the ocean's.
        var shores = new Dictionary<int, List<double>>();
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < lakes.Count; i++)
        {
            int root = Find(lakes, i);
            if (!shores.TryGetValue(root, out var s))
                shores[root] = s = new();
            s.AddRange(lakes[i].Info.Shore);
            if (!members.TryGetValue(root, out var m))
                members[root] = m = new();
            m.Add(i);
        }
        var candidates = new List<(double Level, List<int> Members, double Area, bool River)>();
        var real = Elevation.Active;
        foreach (var (root, shore) in shores)
        {
            // Inland tidal water (an estuary the game has as land): the sea's level 0, no ocean pass, the terrain is only cut under it.
            if (members[root].Count == 1 && lakes[members[root][0]].Info.IsTidal)
            {
                // Only where the real elevation says it is at the sea's height: an OMT ocean polygon over real land is not cut.
                if (real != null && !LakeMaskFilter.AtSeaLevel(lakes[members[root][0]].Info, real, rejectUnknown: false))
                    continue;
                candidates.Add((0.0, members[root], lakes[members[root][0]].Info.AreaM2, true));
                continue;
            }
            // With the real elevation the water surface is in the data (flat): the lower quartile of the real heights inside the
            // lake, over all its tiles (islands are higher). Without it: the low end of the game's shore heights.
            var inside = new List<double>();
            if (real != null)
            {
                foreach (int i in members[root])
                {
                    var geo = lakes[i].Info.InsideGeo;
                    for (int k = 0; k + 1 < geo.Length; k += 2)
                        if (real.TrySample(geo[k], geo[k + 1], out double realHeight, out double realWeight) && realWeight > 0.5)
                            inside.Add(realHeight);
                }
            }
            bool fromReal = inside.Count >= 8;
            bool river = members[root].Count == 1 && lakes[members[root][0]].Info.IsRiver;
            bool tidal = false, seaLake = false;
            double level;
            if (fromReal)
            {
                inside.Sort();
                // Lagoons and bays tagged as lakes lie at sea level: the sea's.
                // (The real elevation is flat on the water: the sea reads about 0. Low lakes such as Okeechobee at 2.3 m or Ladoga
                // at 5 m are lakes; the game's own heightmap is too rough for that, hence the wider margin below.)
                // A wide river at the sea (an estuary: the St. Johns at Jacksonville) is water just above the sea.
                double medianReal = inside[inside.Count / 2];
                tidal = medianReal <= SeaMarginRealM;
                // A lagoon or lake at the sea's level (Indian River and Banana River at Cape Canaveral, class lake here, land in the game's
                // heightmap) gets the sea's level 0 like a tidal estuary: the terrain is cut under it, the game's ocean fills the hole.
                // Depressions (Salton Sea, Dead Sea, Caspian) are no sea-level water and stay dry as before.
                seaLake = tidal && !river && WaterGeometry.IsSeaLevelLake(medianReal);
                if (tidal && !river && !seaLake)
                    continue;
                level = seaLake ? 0.0 : tidal ? TidalLevel : inside[inside.Count / 4] + RealLift;
            }
            else
            {
                // Without the real elevation a river's level (from the game's rough heightmap) is no good: the river map paints it instead.
                if (river || shore.Count < 6)
                    continue;
                // Lagoons and bays tagged as lakes lie at sea level: the terrain inside them is the sea's, not a lake bed.
                if (members[root].Any(i => lakes[i].Info.InsideMedian <= SeaMarginM))
                    continue;
                shore.Sort();
                level = shore[shore.Count / 10] + LakeLift;
            }
            if (level <= (fromReal ? SeaMarginRealM : SeaMarginM) && !tidal)
                continue;
            // More tiles of the lake arrive as the camera turns and move the low end of the shore heights by a metre or
            // two; the water must not follow. A level once chosen for any polygon of the lake stays, unless it is far off.
            // A level taken from the game's heightmap is never kept once the real elevation gives one (they differ by tens of metres).
            if (!tidal)
            {
            var remembered = members[root].Select(i => (tile: lakes[i].Tile, info: lakes[i].Info))
                .Where(m => StickyLevels.TryGetValue((m.tile.X, m.tile.Y, m.info.FirstVertex), out var kept) && kept.Real == fromReal)
                .Select(m => StickyLevels[(m.tile.X, m.tile.Y, m.info.FirstVertex)].Level).OrderBy(l => l).ToList();
            // From the game's heightmap the shore heights of other tiles of the lake differ by tens of metres: the first level stays.
            if (remembered.Count > 0 && (!fromReal || Math.Abs(remembered[remembered.Count / 2] - level) < RealStickMeters))
                level = remembered[remembered.Count / 2];
            foreach (int i in members[root])
                StickyLevels[(lakes[i].Tile.X, lakes[i].Tile.Y, lakes[i].Info.FirstVertex)] = (level, fromReal);
            }
            // A sea-level group: its pieces are checked one by one (the tile-edge union also joins unrelated ponds and canals
            // of the land next to the lagoon, and the group's median says nothing about them).
            if (seaLake && real != null)
            {
                members[root] = members[root].Where(i => lakes[i].Info.Edges != 0 || lakes[i].Info.AreaM2 >= LakeMaskFilter.MinSeaPieceM2)
                    .Where(i => LakeMaskFilter.AtSeaLevel(lakes[i].Info, real, rejectUnknown: lakes[i].Info.Edges == 0)).ToList();
                if (members[root].Count == 0)
                    continue;
            }
            double area = members[root].Sum(i => lakes[i].Info.AreaM2), shoreline = members[root].Sum(i => lakes[i].Info.ShoreM);
            // A strip of width w has area / shoreline = w / 2: river banks, canals and creeks (under ~250 m wide) are no lakes;
            // class river polygons count from 200 m (their pieces are cut at every tile edge, which is not shore).
            if (river)
            {
                if (area < MinRiverAreaM2 || (shoreline > 1 && area / shoreline < MinRiverHalfWidthM))
                    continue;
            }
            else
            {
                // Only medium to large lakes; lakes that are dry part of the year only when they are big.
                bool intermittent = members[root].All(i => lakes[i].Info.Intermittent);
                double minArea = (intermittent ? Math.Max(Settings?.MinIntermittentLakeAreaKm2 ?? 10, Settings?.MinLakeAreaKm2 ?? 3) : Settings?.MinLakeAreaKm2 ?? 3) * 1e6;
                if (area < minArea)
                    continue;
                if (shoreline > 1 && area / shoreline < MinHalfWidthM)
                    continue;
            }
            candidates.Add((level, members[root], area, river || seaLake));
        }
        // Up to Passes levels, near-equal levels sharing one. The biggest lakes come first (not the ones with the most triangles:
        // a big lake with a smooth outline has few, and lost its level to a small jagged one). River pieces merge only with very
        // near levels (a river falls, a lake does not); the first member's level stays the group's.
        var channels = new List<(double Level, List<int> Members, bool River)>();
        foreach (var c in candidates.OrderByDescending(c => c.Area))
        {
            int at = channels.FindIndex(ch => Math.Abs(ch.Level - c.Level) < (ch.River || c.River ? RiverMergeM : MergeLevelsM));
            if (at >= 0)
                channels[at].Members.AddRange(c.Members);
            else if (channels.Count < Passes)
                channels.Add((c.Level, new List<int>(c.Members), c.River));
            else if (c.Area > 20e6 && Interlocked.Increment(ref _droppedLogged) <= 20)
                Log.Notice($"lakes: {c.Area / 1e6:F0} km2 lake at {c.Level:F0} m has no level slot (all {Passes} taken by bigger lakes)");
        }

        // Everything the map is made from: the window, the levels and the polygons of every group (and the real elevation where the sea-level
        // refinement reads it). A build whose input equals the last one's gives the same map: it is not made again (a tile arrived whose lakes
        // are too small or too narrow to count, for instance).
        ulong input = 14695981039346656037UL;
        void MixIn(ulong v) => input = (input ^ v) * 1099511628211UL;
        MixIn(BitConverter.DoubleToUInt64Bits(u0));
        MixIn(BitConverter.DoubleToUInt64Bits(v0));
        MixIn((ulong)channels.Count);
        foreach (var channel in channels)
        {
            MixIn(BitConverter.DoubleToUInt64Bits(channel.Level));
            foreach (var key in channel.Members.Select(i => (lakes[i].Tile.X, lakes[i].Tile.Y, lakes[i].Info.FirstVertex, lakes[i].Info.VertexCount)).OrderBy(k => k))
            {
                MixIn((ulong)key.X); MixIn((ulong)key.Y); MixIn((ulong)key.FirstVertex); MixIn((ulong)key.VertexCount);
            }
            if (channel.Level <= SeaLevelMeters && real != null)
                MixIn((ulong)real.Version);
        }
        if (channels.Count == 0)
            input = 1;
        if (_lastResult is { } previous && _lastFingerprint == input)
        {
            previous.Included = tiles.Where(t => t.Lakes.Count > 0).Select(t => (t.X, t.Y)).ToHashSet();
            return previous;
        }

        var result = new Result { U0 = (float)u0, V0 = (float)v0, Du = (float)du, Dv = (float)dv };
        var levels = new List<double>();
        var centers = new List<double3>();
        var radii = new List<double>();
        result.Atlas = new byte[MaskSize * MaskSize * 4];
        // One byte per pixel: the index (1..) of the level of the lake there, 0 on land. Pieces of a group are drawn together.
        var mask = new byte[MaskSize * MaskSize];
        for (int k = 0; k < channels.Count; k++)
        {
            foreach (int i in channels[k].Members)
            {
                var tile = lakes[i].Tile;
                var info = lakes[i].Info;
                var v = tile.Vertices;
                for (int t = info.FirstVertex; t + 2 < info.FirstVertex + info.VertexCount; t += 3)
                {
                    double3 a = tile.CenterCcf + new double3(v[t].X, v[t].Y, v[t].Z);
                    double3 b = tile.CenterCcf + new double3(v[t + 1].X, v[t + 1].Y, v[t + 1].Z);
                    double3 c = tile.CenterCcf + new double3(v[t + 2].X, v[t + 2].Y, v[t + 2].Z);
                    Fill(mask, (byte)(k + 1), a, b, c, u0, v0, du, dv);
                }
            }
            // Islands (holes of the polygons) stay land however small; at the sea's level, real high ground stays dry too.
            LakeMaskFilter.Refine(mask, MaskSize, (byte)(k + 1), channels[k].Members.Select(i => lakes[i].Info).ToList(), real, channels[k].Level <= SeaLevelMeters, u0, v0, du, dv);
        }
        // Pixel bounds of every group; a group whose tiles reach the window but not its pixels takes no level (and no pass).
        var bounds = new int[channels.Count * 4];
        for (int k = 0; k < channels.Count; k++)
        {
            bounds[4 * k] = MaskSize; bounds[4 * k + 1] = MaskSize; bounds[4 * k + 2] = -1; bounds[4 * k + 3] = -1;
        }
        for (int y = 0; y < MaskSize; y++)
        {
            var row = mask.AsSpan(y * MaskSize, MaskSize);
            for (int x = 0; x < MaskSize; x++)
            {
                int g = row[x];
                if (g == 0)
                    continue;
                int o = 4 * (g - 1);
                bounds[o] = Math.Min(bounds[o], x); bounds[o + 1] = Math.Min(bounds[o + 1], y);
                bounds[o + 2] = Math.Max(bounds[o + 2], x); bounds[o + 3] = Math.Max(bounds[o + 3], y);
            }
        }
        // Indices of the groups that have pixels, in order; the mask is relabelled to them.
        var remap = new byte[channels.Count + 1];
        for (int k = 0; k < channels.Count; k++)
        {
            if (bounds[4 * k + 2] < 0)
                continue;
            levels.Add(channels[k].Level);
            remap[k + 1] = (byte)levels.Count;
            int bx0 = bounds[4 * k], by0 = bounds[4 * k + 1], bx1 = bounds[4 * k + 2], by1 = bounds[4 * k + 3];
            // Bounding sphere of the lake pixels (the window's lat/lon box of them), with a margin for the surface's waves.
            double3 At(double px, double py)
            {
                double lonP = (u0 + px / MaskSize * du - 0.5) * 2 * Math.PI, latP = (0.5 - (v0 + py / MaskSize * dv)) * Math.PI;
                return new double3(Math.Cos(latP) * Math.Cos(lonP), Math.Cos(latP) * Math.Sin(lonP), Math.Sin(latP)) * (planetR + channels[k].Level);
            }
            double fx0 = bx0, fy0 = by0, fx1 = bx1 + 1, fy1 = by1 + 1;
            double3 centre = At((fx0 + fx1) / 2, (fy0 + fy1) / 2);
            double radius = 0;
            foreach (var corner in new[] { At(fx0, fy0), At(fx1, fy0), At(fx0, fy1), At(fx1, fy1) })
                radius = Math.Max(radius, (corner - centre).Length());
            centers.Add(centre);
            radii.Add(radius + 300);
        }
        for (int i = 0; i < mask.Length; i++)
            mask[i] = remap[mask[i]];
        if (levels.Count > 0)
            WriteDistance(mask, result.Atlas);
        result.Levels = levels.ToArray();
        result.Centers = centers.ToArray();
        result.Radii = radii.ToArray();
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) => h = (h ^ v) * 1099511628211UL;
        // No lake in the window: every window is the same empty map (the shader sees a zero window), wherever it lies.
        if (result.Levels.Length > 0)
        {
            Mix(BitConverter.SingleToUInt32Bits(result.U0));
            Mix(BitConverter.SingleToUInt32Bits(result.V0));
        }
        foreach (double level in result.Levels)
            Mix((ulong)BitConverter.DoubleToInt64Bits(level));
        for (int i = 0; i < result.Atlas.Length; i += 13)
            Mix(result.Atlas[i]);
        result.Signature = h;
        result.Included = tiles.Where(t => t.Lakes.Count > 0).Select(t => (t.X, t.Y)).ToHashSet();
        _lastFingerprint = input;
        _lastResult = result;
        return result;
    }

    /// <summary>Pixel bounds (inclusive) of the non-zero pixels of the mask; false if there are none.</summary>
    private static bool MaskBounds(byte[] mask, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = MaskSize; y0 = MaskSize; x1 = -1; y1 = -1;
        for (int y = 0; y < MaskSize; y++)
        {
            var row = mask.AsSpan(y * MaskSize, MaskSize);
            int first = row.IndexOfAnyExcept((byte)0);
            if (first < 0)
                continue;
            int last = row.LastIndexOfAnyExcept((byte)0);
            x0 = Math.Min(x0, first);
            x1 = Math.Max(x1, last);
            y0 = Math.Min(y0, y);
            y1 = y;
        }
        return x1 >= 0;
    }

    private static void Union(List<Lake> lakes, int a, int b)
    {
        int ra = Find(lakes, a), rb = Find(lakes, b);
        if (ra != rb)
            lakes[rb].Parent = ra;
    }

    /// <summary>3-4 chamfer distance transform: in dist, 0 where the distance is measured from and a huge value elsewhere.</summary>
    private static void Chamfer(int[] dist, int n)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x, d = dist[i];
                if (d == 0)
                    continue;
                if (x > 0) d = Math.Min(d, dist[i - 1] + 3);
                if (y > 0)
                {
                    d = Math.Min(d, dist[i - n] + 3);
                    if (x > 0) d = Math.Min(d, dist[i - n - 1] + 4);
                    if (x < n - 1) d = Math.Min(d, dist[i - n + 1] + 4);
                }
                dist[i] = d;
            }
        }
        for (int y = n - 1; y >= 0; y--)
        {
            for (int x = n - 1; x >= 0; x--)
            {
                int i = y * n + x, d = dist[i];
                if (d == 0)
                    continue;
                if (x < n - 1) d = Math.Min(d, dist[i + 1] + 3);
                if (y < n - 1)
                {
                    d = Math.Min(d, dist[i + n] + 3);
                    if (x < n - 1) d = Math.Min(d, dist[i + n + 1] + 4);
                    if (x > 0) d = Math.Min(d, dist[i + n - 1] + 4);
                }
                dist[i] = d;
            }
        }
    }

    /// <summary>Like Chamfer, and every pixel takes the label of the pixel its distance comes from (the nearest lake's level index).</summary>
    private static void ChamferLabel(Span<int> dist, byte[] label, int n)
    {
        static void Try(Span<int> dist, byte[] label, int i, int from, int add)
        {
            int d = dist[from] + add;
            if (d < dist[i])
            {
                dist[i] = d;
                label[i] = label[from];
            }
        }
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (dist[i] == 0)
                    continue;
                if (x > 0) Try(dist, label, i, i - 1, 3);
                if (y > 0)
                {
                    Try(dist, label, i, i - n, 3);
                    if (x > 0) Try(dist, label, i, i - n - 1, 4);
                    if (x < n - 1) Try(dist, label, i, i - n + 1, 4);
                }
            }
        }
        for (int y = n - 1; y >= 0; y--)
        {
            for (int x = n - 1; x >= 0; x--)
            {
                int i = y * n + x;
                if (dist[i] == 0)
                    continue;
                if (x < n - 1) Try(dist, label, i, i + 1, 3);
                if (y < n - 1)
                {
                    Try(dist, label, i, i + n, 3);
                    if (x < n - 1) Try(dist, label, i, i + n + 1, 4);
                    if (x > 0) Try(dist, label, i, i + n - 1, 4);
                }
            }
        }
    }

    /// <summary>
    /// Signed distance to the shoreline in the red channel of the map, the index of the nearest lake's level in the green one: from the lake pixels outwards and from the land pixels
    /// inwards. A pixel's distance is measured to the centre of the nearest pixel of the other kind, minus half a texel:
    /// the shoreline lies between them. See DistanceScaleMeters for the encoding.
    /// </summary>
    public static void WriteDistance(byte[] mask, byte[] atlas)
    {
        const int n = MaskSize, inf = 1 << 28;
        double texel = WindowMeters / n;
        var signed = _distance ??= new float[n * n];
        var original = _distance2 ??= new float[n * n];
        var scratch = _distance3 ??= new float[n * n];
        var labelDist = System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(scratch.AsSpan());     // free until the blur
        for (int i = 0; i < labelDist.Length; i++)
            labelDist[i] = mask[i] != 0 ? 0 : inf;
        // The label of the nearest lake for every pixel (the 3-4 chamfer is good enough for a nearest-neighbour lookup).
        var nearest = new byte[n * n];
        Array.Copy(mask, nearest, mask.Length);
        ChamferLabel(labelDist, nearest, n);
        // The distances themselves: exact Euclidean (squared, Felzenszwalb) for both sides. The chamfer's error (up to 8 % and
        // direction dependent) made the shoreline wobble with its direction.
        for (int i = 0; i < signed.Length; i++)
        {
            bool lake = mask[i] != 0;
            signed[i] = lake ? EdtInfinity : 0;       // squared distance to the nearest land pixel
            original[i] = lake ? 0 : EdtInfinity;     // ... to the nearest lake pixel
        }
        SquaredDistance(signed, n);
        SquaredDistance(original, n);
        // Signed metres (positive inside the lake). A pixel's distance is measured to the centre of the nearest pixel of the other
        // kind, minus half a texel: the shoreline lies between them.
        for (int i = 0; i < signed.Length; i++)
        {
            bool lake = mask[i] != 0;
            double meters = Math.Max((Math.Sqrt(Math.Min(lake ? signed[i] : original[i], EdtInfinity)) - 0.5) * texel, 0);
            signed[i] = (float)(lake ? meters : -meters);
        }
        // The unsmoothed distance is kept (in the other array) to limit what the smoothing may do.
        Array.Copy(signed, original, signed.Length);
        // The polygons of the vector tiles are simplified, and the mask is binary: its shoreline is a staircase of 24 m steps.
        // A Gaussian of the distance field (not of the mask) rounds the steps off and moves the zero crossing only a little.
        Blur(signed, scratch, n);
        // Thin features (a causeway, a dam or a spit a few texels wide, a narrow canal) must survive the smoothing: there a pixel
        // keeps at least half its distance from the shore, on its own side. Wide shores are not limited, or the staircase of the
        // mask would stay (the limit alone lets the shore move by a texel only, which is the size of the steps).
        ThinFeatures(signed, original, scratch, mask, n, true);
        ThinFeatures(signed, original, scratch, mask, n, false);
        for (int i = 0; i < signed.Length; i++)
        {
            double meters = Math.Abs(signed[i]);
            int steps = (int)Math.Round(127 * (1 - Math.Exp(-meters / DistanceScaleMeters)));
            // exactly on the shoreline counts as land, so a lake's edge is a real zero crossing
            atlas[i * 4] = (byte)Math.Clamp(signed[i] > 0 ? 128 + Math.Max(steps, 1) : 128 - steps, 1, 255);
            atlas[i * 4 + 1] = mask[i] != 0 ? mask[i] : nearest[i];
        }
    }

    /// <summary>
    /// Limits the smoothing on one side (lake or land) where that side is thin: ridge = the largest unsmoothed distance within 3 texels
    /// on the same side (half the feature's width); up to 2 texels the limit is fully on, from 3.2 texels it is off.
    /// </summary>
    private static void ThinFeatures(float[] signed, float[] original, float[] ridge, byte[] mask, int n, bool lakeSide)
    {
        const int R = 3;
        for (int i = 0; i < ridge.Length; i++)
            ridge[i] = (mask[i] != 0) == lakeSide ? Math.Abs(original[i]) : 0;
        System.Threading.Tasks.Parallel.For(0, n, () => new float[n], (y, _, line) =>
        {
            int row = y * n;
            Array.Copy(ridge, row, line, 0, n);
            for (int x = 0; x < n; x++)
            {
                float m = 0;
                for (int k = Math.Max(x - R, 0); k <= Math.Min(x + R, n - 1); k++)
                    m = Math.Max(m, line[k]);
                ridge[row + x] = m;
            }
            return line;
        }, _ => { });
        double texel = WindowMeters / n;
        System.Threading.Tasks.Parallel.For(0, n, () => new float[n], (x, _, line) =>
        {
            for (int y = 0; y < n; y++)
                line[y] = ridge[y * n + x];
            for (int y = 0; y < n; y++)
            {
                int i = y * n + x;
                if ((mask[i] != 0) != lakeSide)
                    continue;
                float m = 0;
                for (int k = Math.Max(y - R, 0); k <= Math.Min(y + R, n - 1); k++)
                    m = Math.Max(m, line[k]);
                double t = Math.Clamp((m / texel - 2.0) / 1.2, 0, 1);
                double thin = 1 - t * t * (3 - 2 * t);
                float limited = lakeSide ? Math.Max(signed[i], 0.5f * original[i]) : Math.Min(signed[i], 0.5f * original[i]);
                signed[i] += (float)(thin * (limited - signed[i]));
            }
            return line;
        }, _ => { });
    }

    private const float EdtInfinity = 1e20f;
    /// <summary>Standard deviation (in texels) of the Gaussian that smooths the signed distance field.</summary>
    public const double BlurSigmaTexels = 3.0;

    /// <summary>Exact squared Euclidean distance transform in place: 0 at the sources, EdtInfinity elsewhere on entry (texels squared).</summary>
    private static void SquaredDistance(float[] array, int n)
    {
        System.Threading.Tasks.Parallel.For(0, n, () => new Scratch(n), (y, _, sc) =>
        {
            Parabolas(array, y * n, 1, n, sc);
            return sc;
        }, _ => { });
        System.Threading.Tasks.Parallel.For(0, n, () => new Scratch(n), (x, _, sc) =>
        {
            Parabolas(array, x, n, n, sc);
            return sc;
        }, _ => { });
    }

    private sealed class Scratch(int n)
    {
        public readonly float[] F = new float[n], Z = new float[n + 1];
        public readonly int[] V = new int[n];
    }

    /// <summary>Lower envelope of parabolas along one row or column (Felzenszwalb and Huttenlocher).</summary>
    private static void Parabolas(float[] a, int start, int stride, int n, Scratch sc)
    {
        var f = sc.F; var z = sc.Z; var v = sc.V;
        for (int q = 0; q < n; q++)
            f[q] = a[start + q * stride];
        int k = 0;
        v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = ((f[q] + (float)q * q) - (f[p] + (float)p * p)) / (2f * q - 2f * p);
                if (s <= z[k] && k > 0) k--; else break;
            }
            if (s <= z[k]) { v[k] = q; continue; }   // k == 0 and the new parabola is lower everywhere
            k++;
            v[k] = q; z[k] = s; z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float d = q - v[k];
            a[start + q * stride] = Math.Min(d * d + f[v[k]], EdtInfinity);
        }
    }

    /// <summary>Separable Gaussian blur of the field (sigma BlurSigmaTexels), edges clamped.</summary>
    private static void Blur(float[] data, float[] tmp, int n)
    {
        int radius = (int)Math.Ceiling(3 * BlurSigmaTexels);
        var kernel = new float[2 * radius + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
            sum += kernel[i + radius] = (float)Math.Exp(-0.5 * i * i / (BlurSigmaTexels * BlurSigmaTexels));
        for (int i = 0; i < kernel.Length; i++)
            kernel[i] /= (float)sum;
        var src = data;
        System.Threading.Tasks.Parallel.For(0, n, y =>
        {
            int row = y * n;
            for (int x = 0; x < n; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                    acc += kernel[k + radius] * src[row + Math.Clamp(x + k, 0, n - 1)];
                tmp[row + x] = acc;
            }
        });
        const int Stripe = 32;
        System.Threading.Tasks.Parallel.For(0, (n + Stripe - 1) / Stripe, b =>
        {
            int x0 = b * Stripe, x1 = Math.Min(x0 + Stripe, n);
            var acc = new float[Stripe];
            for (int y = 0; y < n; y++)
            {
                Array.Clear(acc);
                for (int k = -radius; k <= radius; k++)
                {
                    int row = Math.Clamp(y + k, 0, n - 1) * n;
                    float w = kernel[k + radius];
                    for (int x = x0; x < x1; x++)
                        acc[x - x0] += w * tmp[row + x];
                }
                for (int x = x0; x < x1; x++)
                    src[y * n + x] = acc[x - x0];
            }
        });
    }

    private static (double X, double Y) Pixel(double3 p, double u0, double v0, double du, double dv)
    {
        double r = p.Length();
        double lat = Math.Asin(Math.Clamp(p.Z / r, -1, 1)), lon = Math.Atan2(p.Y, p.X);
        return ((0.5 + lon / (2 * Math.PI) - u0) / du * MaskSize, (0.5 - lat / Math.PI - v0) / dv * MaskSize);
    }

    private static void Fill(byte[] mask, byte label, double3 a, double3 b, double3 c, double u0, double v0, double du, double dv)
    {
        var (ax, ay) = Pixel(a, u0, v0, du, dv);
        var (bx, by) = Pixel(b, u0, v0, du, dv);
        var (cx, cy) = Pixel(c, u0, v0, du, dv);
        int x0 = Math.Max((int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))), 0), x1 = Math.Min((int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))), MaskSize - 1);
        int y0 = Math.Max((int)Math.Floor(Math.Min(ay, Math.Min(by, cy))), 0), y1 = Math.Min((int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))), MaskSize - 1);
        double det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (Math.Abs(det) < 1e-9 || x1 < x0 || y1 < y0)
            return;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / det;
                double l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / det;
                double l3 = 1 - l1 - l2;
                // A little outside the triangle, so shared edges leave no gaps.
                if (l1 >= -0.02 && l2 >= -0.02 && l3 >= -0.02)
                    mask[y * MaskSize + x] = label;
            }
        }
    }

    // ---- upload (main thread, before the frame is recorded) ----

    private static float[] _lakeRow = new float[8], _demRow = new float[5], _levelTable = new float[Passes + 1];

    public static void Upload()
    {
        if (_failed || Settings is not { Enabled: true } settings || !(settings.Water || settings.RealTerrain || settings.RealShores || settings.Rivers))
            return;
        try
        {
            EnsureResources();
            if (_inFlight)
            {
                if (!_staging!.WaitFor(0))
                    return;
                _inFlight = false;
            }
            Result? lake = settings.Water ? _ready : null;
            ElevationWindow? elevation = settings.RealTerrain || settings.RealShores ? Elevation.TakeReady() : null;
            RiverMap.Window? river = settings.Rivers ? RiverBuilder.TakeReady() : null;
            RiverMap.Window? riverFar = settings.Rivers ? RiverBuilder.TakeReadyFar() : null;
            if (lake == null && elevation == null && river == null && riverFar == null)
                return;
            if (lake != null)
                _ready = null;
            var staging = _staging!;
            CommandBuffer cb = staging.NextCommandBuffer();
            cb.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            if (lake != null)
            {
                RecordImage(cb, staging, _atlas!, lake.Atlas, MaskSize, MaskSize, 0);
                // No lakes: the terrain shader sees a zero window and leaves the terrain alone.
                _lakeRow = new float[8];
                _levelTable = new float[Passes + 1];
                if (lake.Levels.Length > 0)
                {
                    _lakeRow = new float[8] { lake.U0, lake.V0, lake.Du, lake.Dv, 0, 0, 0, lake.Levels.Length };
                    for (int k = 0; k < lake.Levels.Length; k++)
                        _levelTable[k + 1] = (float)lake.Levels[k];
                }
            }
            if (elevation != null)
            {
                RecordImage(cb, staging, _params!, System.Runtime.InteropServices.MemoryMarshal.AsBytes(elevation.Heights.AsSpan()),
                    ElevationWindow.Size, ElevationWindow.Size, 1);
                _demRow = new float[5] { elevation.U0, elevation.V0, elevation.Du, elevation.Dv, 1 };
            }
            if (river != null)
            {
                RecordImage(cb, staging, _rivers!, river.Data, RiverMap.Size, RiverMap.Size, 0);
                _riverRow = new float[5] { river.U0, river.V0, river.Du, river.Dv, 1 };
            }
            if (riverFar != null)
            {
                RecordImage(cb, staging, _riversFar!, riverFar.Data, RiverMap.Size, RiverMap.Size, 0);
                _riverFarRow = new float[5] { riverFar.U0, riverFar.V0, riverFar.Du, riverFar.Dv, 1 };
            }
            // Row 0 is written every time (the numbers of all the windows): 0-3 lake window, 7 lake level count, 8-12 real elevation, 13 the marker, 16-20 near rivers, 21-25 far rivers,
            // LevelTable + i: the level of lake index i.
            var row = new float[LevelTable + Passes + 1];
            Array.Copy(_lakeRow, 0, row, 0, 8);
            Array.Copy(_levelTable, 0, row, LevelTable, _levelTable.Length);
            Array.Copy(_demRow, 0, row, 8, 5);
            row[13] = 12345f;                                  // the numbers are there: the ground shader may read them
            Array.Copy(_riverRow, 0, row, 16, 5);
            Array.Copy(_riverFarRow, 0, row, 21, 5);
            RecordImage(cb, staging, _params!, System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan()), row.Length, 1, 0);
            cb.End();
            staging.Submit();
            _inFlight = true;
            if (lake != null)
            {
                // Queue order: the copies come before every later frame. The handle points at the map from now on.
                if (!_bound && Program.Instance?.BindlessTextures is { } library)
                {
                    library.SetTexture(_handle, _atlas!.VkImageView);
                    library.SetTexture(_paramsHandle, _params!.VkImageView);
                    _bound = true;
                }
                _active = lake;
                Status = lake.Levels.Length == 0 ? "no lakes in the window"
                    : $"{lake.Levels.Length} lake level(s): {string.Join(", ", lake.Levels.Select(l => l.ToString("F0") + " m"))}";
                Log.Notice($"lakes: window ready, {Status}");
            }
            if (river != null)
            {
                if (!_riverBound && Program.Instance?.BindlessTextures is { } riverLibrary)
                {
                    riverLibrary.SetTexture(_riverHandle, _rivers!.VkImageView);
                    riverLibrary.SetTexture(_paramsHandle, _params!.VkImageView);
                    _riverBound = true;
                }
                RiverBuilder.Current = river;
            }
            if (riverFar != null)
            {
                if (!_riverFarBound && Program.Instance?.BindlessTextures is { } farLibrary)
                {
                    farLibrary.SetTexture(_riverFarHandle, _riversFar!.VkImageView);
                    farLibrary.SetTexture(_paramsHandle, _params!.VkImageView);
                    _riverFarBound = true;
                }
                RiverBuilder.CurrentFar = riverFar;
            }
            if (elevation != null)
            {
                Elevation.Active = elevation;
                Log.Notice($"elevation: window on the GPU, {elevation.Used.Count} tiles, {elevation.Coverage:P0} of the window has data");
            }
        }
        catch (Exception e)
        {
            _failed = true;
            Status = $"off: {e.Message}";
            Log.Error($"lakes and elevation: upload failed, off: {e}");
        }
    }

    /// <summary>The map and its window: made once (the terrain's descriptor sets keep their image views), refilled later.</summary>
    private static void EnsureResources()
    {
        if (_staging != null)
            return;
        var renderer = Program.GetRenderer();
        var library = Program.Instance?.BindlessTextures ?? throw new InvalidOperationException("the game's texture library is not ready");
        var blank = typeof(BindlessTextureLibrary).GetField("_emptyTexture", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(library) as SimpleVkTexture ?? throw new InvalidOperationException("the game's texture library has changed");
        _staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
        _atlas = new SimpleVkTexture("GEE lake map", renderer.Allocator, MaskSize, MaskSize, 1, VkFormat.R8G8B8A8UNorm, 1, 1);
        // One float per texel. Row 0: the lake window (0-3: u0, v0, du, dv; 4-6: levels; 7: lake count) and the elevation window (8-11:
        // u0, v0, du, dv; 12: 1 when it is on). Rows 1..: the elevation window, row after row.
        _params = new SimpleVkTexture("GEE lake window and elevation", renderer.Allocator, ElevationWindow.Size, ElevationWindow.Size + 1, 1, VkFormat.R32SFloat, 1, 1);
        if (_handle < 0)
        {
            _handle = library.AddTexture(blank.VkImageView);
            _paramsHandle = library.AddTexture(blank.VkImageView);
            _riverHandle = library.AddTexture(blank.VkImageView);
            _riverFarHandle = library.AddTexture(blank.VkImageView);
        }
        _rivers = new SimpleVkTexture("GEE river map", renderer.Allocator, RiverMap.Size, RiverMap.Size, 1, VkFormat.R8UNorm, 1, 1);
        _riversFar = new SimpleVkTexture("GEE far river map", renderer.Allocator, RiverMap.Size, RiverMap.Size, 1, VkFormat.R8UNorm, 1, 1);
        CommandBuffer cb = _staging.NextCommandBuffer();
        cb.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        foreach (var texture in new[] { _atlas, _params, _rivers, _riversFar })
        {
            cb.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default,
                new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
                {
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = VkImageLayout.Undefined,
                    NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = texture.VkImage,
                    SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
                }));
        }
        // Empty to start with: the window is all zero, so the terrain shader does nothing.
        RecordClear(cb, _staging, _atlas!, MaskSize * MaskSize * 4, MaskSize, MaskSize);
        RecordClear(cb, _staging, _params!, ElevationWindow.Size * (ElevationWindow.Size + 1) * 4, ElevationWindow.Size, ElevationWindow.Size + 1);
        RecordClear(cb, _staging, _rivers!, RiverMap.Size * RiverMap.Size, RiverMap.Size, RiverMap.Size);
        RecordClear(cb, _staging, _riversFar!, RiverMap.Size * RiverMap.Size, RiverMap.Size, RiverMap.Size);
        cb.End();
        _staging.Submit();
        _staging.Wait();
        Log.Notice($"lakes: map textures made, bindless handle {_handle}");
    }

    private static void RecordClear(CommandBuffer cb, StagingPool staging, SimpleVkTexture texture, int bytes, int width, int height)
    {
        BufferEx buffer = staging.AddStagingBuffer<byte>(new byte[bytes].AsSpan());
        cb.CopyBufferToImage(buffer.VkBuffer, texture.VkImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
        }));
        cb.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.TransferWriteBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit,
                OldLayout = VkImageLayout.TransferDstOptimal,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = texture.VkImage,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            }));
    }

    private static void RecordImage(CommandBuffer cb, StagingPool staging, SimpleVkTexture texture, Span<byte> pixels, int width, int height, int offsetY)
    {
        BufferEx buffer = staging.AddStagingBuffer<byte>(pixels);
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        cb.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.HostBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.ShaderReadBit,
                DstAccessMask = VkAccessFlags.TransferWriteBit,
                OldLayout = VkImageLayout.ShaderReadOnlyOptimal,
                NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = texture.VkImage,
                SubresourceRange = range,
            }));
        cb.CopyBufferToImage(buffer.VkBuffer, texture.VkImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageOffset = new VkOffset3D { X = 0, Y = offsetY, Z = 0 },
            ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
        }));
        cb.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.TransferWriteBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit,
                OldLayout = VkImageLayout.TransferDstOptimal,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = texture.VkImage,
                SubresourceRange = range,
            }));
    }
}
