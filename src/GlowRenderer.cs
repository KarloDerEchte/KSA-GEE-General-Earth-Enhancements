using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;

namespace OsmBuildings;

/// <summary>
/// Glowing building, street and city lights at night, visible from the ground up to orbit. Each frame picks the
/// lights on the night side, above the horizon and in view, thins them out with distance (the ones kept grow so
/// the total brightness stays the same) and hands them to GlowTechnique.
/// </summary>
internal static class GlowRenderer
{
    public static TileManager? Manager;
    public static RegionalLights? Regional;
    public static RegionalLights? Global;
    public static Settings? Settings;

    public static int LastSprites;
    public static int LastSets;
    public static string? Error;

    private static GlowTechnique? _technique;
    private static GlowInstance[] _instances = new GlowInstance[16384];
    private static readonly List<(double Near, double Dist, LightSet Set, float Weight)> Sets = new();
    /// <summary>Lights kept per pixel of spacing; grows while the sprite budget is exceeded.</summary>
    private static double _thinPixels = 1.5;

    /// <summary>Runs inside the main opaque pass, after the planet and static objects are recorded.</summary>
    public static void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        if (viewport != Program.MainViewport)
            return;
        if (Settings is not { Enabled: true, CityGlow: true } settings || Manager == null)
        {
            LastSprites = 0;
            return;
        }
        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(settings.Body, out Astronomical? astronomical)
            || astronomical is not Celestial body)
            return;
        int count = Collect(viewport.GetCamera(), body, settings);
        LastSprites = count;
        if (count == 0)
            return;
        _technique ??= new GlowTechnique();
        _technique.Draw(commandBuffer, viewport, frameIndex, _instances.AsSpan(0, count));
    }

    /// <summary>After the game rebuilds its render targets (resize, MSAA change).</summary>
    public static void RebuildFrameResources() => _technique?.RebuildFrameResources();

    private static int Collect(Camera camera, Celestial body, Settings settings)
    {
        if (NightLights.SunDirCcf(body, settings) is not { } sun)
            return 0;
        double3 camCcf = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        double3 rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce);
        double3 ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce);
        double3 rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);
        float3 rxf = F(rx), ryf = F(ry), rzf = F(rz);

        double planetRadius = body.MeanRadius;
        double camR = Length(camCcf);
        double3 camDir = camCcf / camR;
        // Angle from below the camera to its horizon, with slack for terrain and lights above the mean radius.
        double horizonAngle = Math.Acos(Math.Clamp((planetRadius - 2000) / camR, -1, 1)) + 0.02;
        double pixelAngle = 2 * Math.Tan(camera.GetFieldOfView() / 2) / Math.Max(camera.FramebufferSize.Y, 1);
        float brightness = (float)settings.GlowBrightness;
        // Rooftop glows take over where the window pass ends.
        double windowEnd = settings.Windows ? Math.Min(settings.WindowDistanceKm, settings.RenderDistanceKm) * 1000 : 0;
        double glowFrom = windowEnd > 0 ? windowEnd * 0.7 : 250, glowTo = windowEnd > 0 ? windowEnd : 1200;
        int budget = Math.Clamp(settings.MaxGlowSprites, 1000, GlowTechnique.MaxSprites);

        Sets.Clear();
        // Regional lights give way to the building lights where tiles are loaded.
        double3 loadedSum = default;
        double loadedRadius = 0;
        // Hidden buildings or roads take their glow with them (and the regional lights then stay on there).
        foreach (var tile in Manager!.Snapshot)
        {
            if (settings.ShowBuildings)
                Consider(tile.BuildingLights, 1);
            if (settings.ShowRoads)
                Consider(tile.LampLights, 1);
            if (settings.ShowBuildings && tile.BuildingLights is { Count: > 0 })
                loadedSum += tile.CenterCcf;
        }
        double3 loadedDir = default;
        double loadedCos = 2;
        if (double3.Dot(loadedSum, loadedSum) > 0)
        {
            loadedDir = double3.Normalize(loadedSum);
            foreach (var tile in Manager.Snapshot)
            {
                if (settings.ShowBuildings && tile.BuildingLights is { Count: > 0 })
                    loadedRadius = Math.Max(loadedRadius, Math.Acos(Math.Clamp(double3.Dot(double3.Normalize(tile.CenterCcf), loadedDir), -1, 1)) * planetRadius + tile.RadiusM - 1000);
            }
            loadedCos = Math.Cos(loadedRadius / planetRadius);
        }
        if (settings.RegionalLights && Regional != null)
        {
            foreach (var set in Regional.Snapshot)
                Consider(set, DetailWeight(set));
        }
        if (settings.GlobalLights && Global != null)
        {
            // Left out where the finer regional tile is ready.
            HashSet<int>? covered = settings.RegionalLights ? Regional?.ReadyKeys : null;
            foreach (var set in Global.Snapshot)
            {
                // Where the finer tile is ready, the coarse lights give way to it in step with its fade-in.
                if (covered == null || !covered.Contains(set.CoverKey))
                    Consider(set, 1);
                else
                    Consider(set, 1 - DetailWeight(set));
            }
        }
        Sets.Sort((a, b) => a.Near.CompareTo(b.Near));
        LastSets = Sets.Count;

        // How much of the finer (zoom 8) lights shows: none from high up, where they would only pop in as their
        // tiles stream in, all of them in the low view; and fading out toward the edge of the loaded area.
        float DetailWeight(LightSet set)
        {
            double altitude = camR - planetRadius;
            float high = 1 - Smooth(600e3, 2500e3, altitude);
            double ground = Math.Acos(Math.Clamp(double3.Dot(double3.Normalize(set.Center), camDir), -1, 1)) * planetRadius;
            double reach = settings.RegionalRadiusKm * 1000;
            return high * (1 - Smooth(reach * 0.6, reach * 0.9, ground));
        }

        void Consider(LightSet? set, float weight)
        {
            if (set == null || set.Count == 0 || weight <= 0.003f)
                return;
            double3 centerDir = double3.Normalize(set.Center);
            double setAngle = set.Radius / planetRadius;
            if (Math.Acos(Math.Clamp(double3.Dot(centerDir, camDir), -1, 1)) - setAngle > horizonAngle)
                return;
            if (double3.Dot(centerDir, sun) - Math.Sin(Math.Min(setAngle, 1.5)) > 0.03)
                return;
            double3 rel = set.Center - camCcf;
            double dist = Length(rel);
            if (dist > set.Radius && camera.IsSphereOutsideFov(Rotate(rel, rx, ry, rz), set.Radius))
                return;
            Sets.Add((Math.Max(dist - set.Radius, 1), dist, set, weight));
        }

        int count = 0;
        foreach (var (near, _, set, weight) in Sets)
        {
            // Fraction of the set's lights that can matter at its nearest point; the rest are skipped unread.
            double spacingPx = set.Spacing / (near * pixelAngle * _thinPixels);
            float keepMax = (float)Math.Min(spacingPx * spacingPx, 1);
            if (keepMax < 1e-5f)
                continue;
            double3 relCenter = set.Center - camCcf;
            float3 egoCenter = F(Rotate(relCenter, rx, ry, rz));
            LightPoint[] points = set.Points;
            int n = Math.Min(set.Count, points.Length);
            bool sorted = set.Sorted;
            for (int i = 0; i < n; i++)
            {
                ref LightPoint p = ref points[i];
                if (p.Level >= keepMax)
                {
                    if (sorted)
                        break;
                    continue;
                }
                double3 position = set.Center + new double3(p.Offset.X, p.Offset.Y, p.Offset.Z);
                double3 up = position / Length(position);
                double3 rel = position - camCcf;
                // Hidden by the planet: the camera is below the light's horizon plane.
                if (double3.Dot(up, rel) > 100)
                    continue;
                float night = NightLights.NightAt(double3.Dot(up, sun));
                if (night <= 0)
                    continue;
                double dist = Length(rel);
                double keepPx = set.Spacing / (Math.Max(dist, 1) * pixelAngle * _thinPixels);
                float keep = (float)Math.Min(keepPx * keepPx, 1);
                if (p.Level >= keep)
                    continue;
                float fade = set.Kind switch
                {
                    // Nearby buildings have lit windows (or point lights); rooftop glows stand in for them from afar.
                    LightKind.Building => Smooth(glowFrom, glowTo, dist),
                    LightKind.Regional => double3.Dot(up, loadedDir) > loadedCos ? 0 : Smooth(4000, 15000, dist),
                    _ => 1,
                };
                if (fade <= 0)
                    continue;
                // The shader fades sprites larger than this on screen; skip those already gone.
                float maxPixels = MaxPixels(set.Kind);
                float radius = p.Radius / MathF.Sqrt(keep);
                float pixelMeters = (float)(dist * pixelAngle);
                float intensity = (p.Color >> 24) / 16f * brightness * night * fade * weight;
                if (set.Kind == LightKind.Regional)
                {
                    // A city glow stands for many lights: closer up it shrinks to a fine point that carries its
                    // light (up to RegionalBoost times brighter), and only fades when it would have to grow beyond.
                    float pixels = radius / pixelMeters;
                    if (pixels > RegionalPixels)
                    {
                        float shrink = pixels / RegionalPixels;
                        intensity *= MathF.Min(shrink * shrink, RegionalBoost);
                        radius = RegionalPixels * pixelMeters;
                    }
                }
                else if (maxPixels > 0 && radius / pixelMeters > maxPixels * 2.5)
                    continue;

                float3 ego = egoCenter + rxf * p.Offset.X + ryf * p.Offset.Y + rzf * p.Offset.Z;
                if (count == _instances.Length)
                    Array.Resize(ref _instances, Math.Min(_instances.Length * 2, GlowTechnique.MaxSprites));
                _instances[count++] = GlowInstance.Make(ego, radius,
                    (p.Color & 0xFF) / 255f * intensity, (p.Color >> 8 & 0xFF) / 255f * intensity, (p.Color >> 16 & 0xFF) / 255f * intensity,
                    maxPixels);
                if (count >= budget)
                    goto done;
            }
        }
    done:
        // Over budget the far sets were cut off, so thin harder next frame; ease back once well below it.
        if (count >= budget)
            _thinPixels = Math.Min(_thinPixels * 1.2, 32);
        else if (count < budget * 0.6 && _thinPixels > 1.5)
            _thinPixels = Math.Max(_thinPixels / 1.05, 1.5);
        return count;
    }

    /// <summary>
    /// Largest on-screen glow radius before the sprite fades: rooftop and city glows stand in for whole buildings
    /// and districts, so they only show as small points; lamp heads may grow a little.
    /// </summary>
    private static float MaxPixels(LightKind kind) => kind switch
    {
        LightKind.Building => 2f,
        LightKind.Regional => 0f,
        _ => 10f,
    };

    /// <summary>On-screen radius regional glows shrink to, and how much brighter they may get for it.</summary>
    private const float RegionalPixels = 1.5f, RegionalBoost = 6f;

    private static float Smooth(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    private static float3 F(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));

    private static double3 Rotate(double3 v, double3 rx, double3 ry, double3 rz) => rx * v.X + ry * v.Y + rz * v.Z;
}
