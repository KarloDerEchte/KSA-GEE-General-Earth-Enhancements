using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;

namespace GEE;

/// <summary>One corner of a water triangle: position relative to the tile centre (CCF), wave coordinates, bank coordinate.</summary>
public struct WaterVertex
{
    public float X, Y, Z, Wx, Wy, Across, Kind;
}

/// <summary>
/// One water triangle in camera-relative (ego) space for the shader: the corners' positions with the bank coordinate
/// in w, the corners' wave coordinates (x and y rows), and the kind (0 lake, 1 river) in the x row's w.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WaterTri
{
    public float4 P0, P1, P2, Wx, Wy;
}

/// <summary>
/// One lake polygon of a tile. The game's heightmap is not flat on lakes (it has the lake bed), so the water level
/// comes from the heights of the shore points (outline points that are not cuts at the tile edge) of every tile of the lake.
/// </summary>
public sealed class LakeInfo
{
    public int FirstVertex, VertexCount;
    /// <summary>Area of the polygon inside the tile (m2).</summary>
    public double AreaM2;
    /// <summary>Median terrain height inside the polygon (m above sea level); NaN if it could not be sampled. Near 0: a lagoon or bay, the sea's.</summary>
    public double InsideMedian = double.NaN;
    /// <summary>Latitude, longitude pairs (degrees) of points inside the lake and on its shore: where the real elevation is looked up.</summary>
    public double[] InsideGeo = Array.Empty<double>(), ShoreGeo = Array.Empty<double>();
    /// <summary>Length of the shoreline inside the tile (m), not counting the cuts along the tile edge.</summary>
    public double ShoreM;
    public double[] Shore = Array.Empty<double>();
    /// <summary>Bit 0: touches the west tile edge, 1: east, 2: north, 3: south.</summary>
    public int Edges;
    /// <summary>A polygon of class river (a wide river or estuary): water of its own level per tile piece. Intermittent: tagged as dry part of the year.</summary>
    public bool IsRiver, Intermittent;
    /// <summary>Sea-level water inland of the game's coast (an estuary): level 0, no ocean pass, the terrain is only cut under it.</summary>
    public bool IsTidal;
    /// <summary>Rings of the polygon's holes (islands in the water) as latitude, longitude pairs: they are burned into the lake map as land.</summary>
    public List<double[]>? HoleGeo;
}

/// <summary>Inland water of one zoom 13 vector tile as triangles: lakes flat at their level, rivers as ribbons on the terrain.</summary>
internal sealed class WaterTile(int x, int y)
{
    public const int Zoom = 13;
    public readonly int X = x, Y = y;
    public enum Stage { Queued, Loading, Ready, Failed }
    public volatile Stage State = Stage.Queued;
    public WaterVertex[] Vertices = Array.Empty<WaterVertex>();
    public int Count;
    /// <summary>Each lake polygon: its triangles in Vertices, shore heights and the tile edges its outline touches.</summary>
    public List<LakeInfo> Lakes = new();
    /// <summary>Rivers, streams and canals of the tile (lines as latitude, longitude pairs), for the river map.</summary>
    public List<RiverLine> Rivers = new();
    /// <summary>Banks of the big rivers (class river polygons) as triangles of latitude, longitude pairs (six numbers each), one array per polygon.</summary>
    public List<double[]> RiverAreas = new();
    public double3 CenterCcf;
    public double RadiusM;
    public DateTime RetryAt;

    public (double S, double W, double N, double E) Bounds => VectorTile.Bounds(Zoom, X, Y);
}

/// <summary>
/// Lakes and rivers: the game has the ocean at sea level and nothing inland. Loads the "water" and "waterway" layers
/// of zoom 13 vector tiles around the camera (the far field fetches the same tiles, so they are cached), turns lake
/// polygons into flat triangles at the level of the terrain inside them (the game's heightmap is flat on lakes) and
/// rivers into ribbons that follow the terrain, and draws them with a water shader (WaterRenderer).
/// </summary>
internal sealed class WaterField : IDisposable
{

    private readonly Settings _settings;
    private readonly TileManager _source;
    private readonly object _lock = new();
    private readonly Dictionary<(int, int), WaterTile> _tiles = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _workers = new();
    private double _lastScan = -10;
    private volatile float _camLat, _camLon;
    private volatile Celestial? _body;
    private volatile bool _finished;
    public volatile WaterTile[] Snapshot = Array.Empty<WaterTile>();
    public volatile int Version;
    public bool Active { get; private set; }

    public WaterField(Settings settings, TileManager source)
    {
        _settings = settings;
        _source = source;
        for (int i = 0; i < 2; i++)
        {
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"GEE water {i}", Priority = ThreadPriority.BelowNormal };
            _workers.Add(worker);
            worker.Start();
        }
    }

    /// <summary>The water tile of this zoom 13 tile, if it was asked for (any thread).</summary>
    public WaterTile? TileAt(int x, int y)
    {
        lock (_lock)
            return _tiles.TryGetValue((x, y), out var tile) ? tile : null;
    }

    public (int Ready, int Pending, int Triangles) Stats()
    {
        lock (_lock)
        {
            int ready = 0, pending = 0, triangles = 0;
            foreach (var t in _tiles.Values)
            {
                if (t.State == WaterTile.Stage.Ready) { ready++; triangles += t.Count / 3; }
                else if (t.State != WaterTile.Stage.Failed) pending++;
            }
            return (ready, pending, triangles);
        }
    }

    /// <summary>Main thread, once per frame.</summary>
    public void Update(Celestial body, double3 cameraCcf, double now, Camera? camera = null)
    {
        if (camera != null)
            ViewFilter.Begin(camera, body, cameraCcf);
        _planetRadius = body.MeanRadius;
        double r = Math.Sqrt(double3.Dot(cameraCcf, cameraCcf));
        double lat = Math.Asin(Math.Clamp(cameraCcf.Z / r, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(cameraCcf.Y, cameraCcf.X) * 180 / Math.PI;
        double altitudeKm = (r - body.MeanRadius) / 1000;
        _camLat = (float)lat;
        _camLon = (float)lon;
        _body = body;
        Active = _settings.Enabled && (_settings.Water || _settings.Rivers) && altitudeKm > -1 && altitudeKm < _settings.WaterMaxAltitudeKm;
        if (Active && now - _lastScan > 1)
        {
            _lastScan = now;
            RequestAround(lat, lon, _settings.WaterRadiusKm);
            Evict(lat, lon, _settings.WaterRadiusKm * 1.6 + 5);
        }
        if (_finished)
        {
            _finished = false;
            RebuildSnapshot();
        }
    }

    private double _planetRadius = 6371000;

    /// <summary>
    /// A ring of WaterRingKm around the camera is always wanted (the lake map covers 80 km around it whatever way it looks, and a lake behind
    /// it is as much a lake as one in front); further out only tiles in the view, the rest wait for the camera to turn.
    /// </summary>
    private bool ViewWanted(WaterTile t)
    {
        var (s, w, n, e) = t.Bounds;
        if (DistanceKm(t, _camLat, _camLon) <= _settings.WaterRingKm)
            return true;
        return ViewFilter.Wanted(Placement.DirCcf((s + n) / 2, (w + e) / 2) * _planetRadius, 3200);
    }

    private static double DistanceKm(WaterTile t, double lat, double lon)
    {
        var (s, w, n, e) = t.Bounds;
        double kmPerDeg = 6371 * Math.PI / 180;
        double dLat = Math.Clamp(lat, s, n) - lat, dLon = Math.Clamp(lon, w, e) - lon;
        return Math.Sqrt(Math.Pow(dLat * kmPerDeg, 2) + Math.Pow(dLon * kmPerDeg * Math.Cos(lat * Math.PI / 180), 2));
    }

    private void RequestAround(double lat, double lon, double radiusKm)
    {
        double kmPerDeg = 6371 * Math.PI / 180;
        double dLat = radiusKm / kmPerDeg, dLon = radiusKm / (kmPerDeg * Math.Max(Math.Cos(lat * Math.PI / 180), 0.05));
        var (x0, y0) = VectorTile.TileAt(WaterTile.Zoom, Math.Min(lat + dLat, 85), lon - dLon);
        var (x1, y1) = VectorTile.TileAt(WaterTile.Zoom, Math.Max(lat - dLat, -85), lon + dLon);
        int added = 0;
        lock (_lock)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (_tiles.TryGetValue((x, y), out var existing))
                    {
                        if (existing.State == WaterTile.Stage.Failed && DateTime.UtcNow >= existing.RetryAt)
                        {
                            existing.State = WaterTile.Stage.Queued;
                            added++;
                        }
                        continue;
                    }
                    var tile = new WaterTile(x, y);
                    if (DistanceKm(tile, lat, lon) > radiusKm || !ViewWanted(tile))
                        continue;
                    _tiles.Add((x, y), tile);
                    added++;
                }
            }
        }
        if (added > 0)
            _work.Release(added);
    }

    private void Evict(double lat, double lon, double keepKm)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var (key, tile) in _tiles.ToList())
            {
                if (tile.State != WaterTile.Stage.Loading && DistanceKm(tile, lat, lon) > keepKm)
                {
                    _tiles.Remove(key);
                    changed |= tile.State == WaterTile.Stage.Ready;
                }
            }
        }
        if (changed)
            RebuildSnapshot();
    }

    private void RebuildSnapshot()
    {
        lock (_lock)
        {
            Snapshot = _tiles.Values.Where(t => t.State == WaterTile.Stage.Ready && (t.Count > 0 || t.Rivers.Count > 0 || t.RiverAreas.Count > 0)).ToArray();
            Version++;
        }
    }

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _work.Wait(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            WaterTile? tile;
            lock (_lock)
            {
                float lat = _camLat, lon = _camLon;
                tile = _tiles.Values.Where(t => t.State == WaterTile.Stage.Queued).OrderBy(t => DistanceKm(t, lat, lon)).FirstOrDefault();
                if (tile != null)
                    tile.State = WaterTile.Stage.Loading;
            }
            if (tile == null)
                continue;
            try
            {
                byte[] data = _source.Fetch(WaterTile.Zoom, tile.X, tile.Y, out _);
                var body = _body ?? throw new InvalidOperationException("no body to place on");
                WaterGeometry.Build(tile, data, body.MeanRadius, (lat, lon) => body.GetTerrainHeightFromDirCcf(Placement.DirCcf(lat, lon)));
                lock (_lock)
                    tile.State = WaterTile.Stage.Ready;
                _finished = true;
            }
            catch (Exception ex)
            {
                if (ex is not DownloadPausedException)
                    Log.Error($"water tile {tile.X}/{tile.Y}: {ex}");
                tile.RetryAt = ex is DownloadPausedException paused ? paused.Until : DateTime.UtcNow + TimeSpan.FromSeconds(60);
                tile.State = WaterTile.Stage.Failed;
            }
        }
    }


    public void Dispose()
    {
        _cts.Cancel();
    }
}

/// <summary>
/// Draws the water triangles in the main pass after the planet and the satellite drape: depth tested, no depth
/// writes, premultiplied alpha (river banks fade out). The triangles of the tiles near the camera are moved into
/// camera space each frame (a triangle per instance, three vertices from gl_VertexIndex).
/// </summary>
internal sealed class WaterTechnique() : InstancedPass<WaterTri>("GEEWater", VertexShader, FragmentShader, MaxTriangles, 3)
{
    public const int MaxTriangles = 100_000;

    private static readonly Brutal.Memory.PtrOwner<VkPipelineColorBlendAttachmentState> Attachment = new(new VkPipelineColorBlendAttachmentState
    {
        BlendEnable = true,
        SrcColorBlendFactor = VkBlendFactor.One,
        DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
        ColorBlendOp = VkBlendOp.Add,
        SrcAlphaBlendFactor = VkBlendFactor.Zero,
        DstAlphaBlendFactor = VkBlendFactor.One,
        AlphaBlendOp = VkBlendOp.Add,
        ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
    });

    private static readonly Brutal.Memory.PtrOwner<VkPipelineColorBlendStateCreateInfo> Blend = new(new VkPipelineColorBlendStateCreateInfo
    {
        AttachmentCount = 1,
        Attachments = Attachment,
    });

    protected override VertexInput MakeVertexInput()
    {
        return new VertexInput(1, 5).AddBindingWithAttributes<WaterTri>(0, 0, VkVertexInputRate.Instance);
    }

    protected override void OnRebuildFrameResources()
    {
        CreatePipeline(VkPrimitiveTopology.TriangleList, VkCullModeFlags.None, VkFrontFace.CounterClockwise, VkPolygonMode.Fill,
            RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite, Blend, out Pipeline);
    }

    /// <summary>Mirrors the leading members of Content/Core/Shaders/Common/Global.glsl: the camera and the first lighting fields.</summary>
    private const string GlobalBlock = """
        struct Camera
        {
            mat4 viewProjection;
            mat4 view;
            mat4 projection;
            mat4 inverseView;
            mat4 inverseProjection;
            vec4 cameraPosition;
            float nearPlane;
            float farPlane;
            float aspectRatio;
            int screenWidth;
            int screenHeight;
            float pbrExposure;
            float time;
            float simSpeed;
            int frame;
            int sdfResolution;
            float deltaTime;
            int pad0;
            mat4 galacticPlane;
            vec4 starObserver;
        };

        struct GlobalLighting
        {
            vec4 sunPosition;
            vec4 sunColor;
            vec4 planetPosition;
        };

        layout(set = 0, binding = 0) uniform Global
        {
            Camera camera;
            GlobalLighting lighting;
        } global;
        """;

    private const string VertexShader = ShaderSource.Version + GlobalBlock + """


        layout(location = 0) in vec4 p0;
        layout(location = 1) in vec4 p1;
        layout(location = 2) in vec4 p2;
        layout(location = 3) in vec4 wx;
        layout(location = 4) in vec4 wy;

        layout(location = 0) out vec3 outPos;
        layout(location = 1) out vec2 outWc;
        layout(location = 2) out float outAcross;
        layout(location = 3) flat out float outKind;

        void main()
        {
            int i = gl_VertexIndex;
            vec4 corner = i == 0 ? p0 : (i == 1 ? p1 : p2);
            vec3 p = corner.xyz;
            outWc = vec2(i == 0 ? wx.x : (i == 1 ? wx.y : wx.z), i == 0 ? wy.x : (i == 1 ? wy.y : wy.z));
            outAcross = corner.w;
            outKind = wx.w;

            // Pulled toward the camera along the view ray, like the satellite drape: the same place on screen, but a
            // little above the terrain between the heightmap samples and above the game's coarse distant terrain.
            vec3 up = normalize(p - global.lighting.planetPosition.xyz);
            float dist = length(p);
            float sinDepression = max(-dot(p / dist, up), 0.05);
            float lift = 2.0 + 4.0 * dist / 1000.0;
            p *= 1.0 - min(lift / sinDepression / dist, 0.5);
            outPos = p;
            gl_Position = global.camera.viewProjection * vec4(p, 1.0);
        }
        """;

    private const string FragmentShader = ShaderSource.Version + GlobalBlock + """


        layout(location = 0) in vec3 inPos;
        layout(location = 1) in vec2 inWc;
        layout(location = 2) in float inAcross;
        layout(location = 3) flat in float inKind;

        layout(location = 0) out vec4 outColor;

        const float TAU = 6.28318530718;
        // Wave vectors in cycles per 1024 wave-coordinate units, so the pattern repeats and tiles line up.
        const int WAVES = 5;
        const vec2 K[WAVES] = vec2[](vec2(23.0, 17.0), vec2(-31.0, 29.0), vec2(53.0, -7.0), vec2(11.0, 71.0), vec2(-83.0, -41.0));
        const float SLOPE[WAVES] = float[](0.030, 0.028, 0.022, 0.018, 0.014);
        const float SPEED[WAVES] = float[](0.9, 1.1, 1.4, 1.7, 2.1);

        vec3 skyColor(vec3 d, vec3 up, vec3 L)
        {
            float e = clamp(dot(d, up), 0.0, 1.0);
            vec3 c = mix(vec3(0.60, 0.70, 0.85), vec3(0.16, 0.34, 0.80), pow(e, 0.45));
            c += vec3(1.0, 0.7, 0.4) * pow(max(dot(d, L), 0.0), 12.0) * 0.25;
            return c;
        }

        void main()
        {
            vec3 P = inPos;
            float dist = length(P);
            vec3 V = -P / dist;
            vec3 up = normalize(P - global.lighting.planetPosition.xyz);
            vec3 L = normalize(global.lighting.sunPosition.xyz - P);
            float sunE = dot(up, L);
            float day = smoothstep(-0.10, 0.20, sunE);
            vec3 sunColor = global.lighting.sunColor.rgb;
            vec3 transmittance = exp(-vec3(0.045, 0.085, 0.19) / (max(sunE, 0.0) + 0.12));
            vec3 sunLight = sunColor * transmittance * smoothstep(-0.03, 0.04, sunE);
            vec3 skyLight = sunColor * (0.0015 + 0.28 * day);

            // Surface normal: the planet's up, tilted by a few travelling waves. Waves smaller than a pixel fade out.
            vec3 dp1 = dFdx(P), dp2 = dFdy(P);
            vec2 duv1 = dFdx(inWc), duv2 = dFdy(inWc);
            vec3 dp2perp = cross(dp2, up), dp1perp = cross(up, dp1);
            vec3 tangent = dp2perp * duv1.x + dp1perp * duv2.x;
            vec3 bitangent = dp2perp * duv1.y + dp1perp * duv2.y;
            float invMax = inversesqrt(max(max(dot(tangent, tangent), dot(bitangent, bitangent)), 1e-20));
            tangent *= invMax;
            bitangent *= invMax;
            float pixel = max(length(duv1), length(duv2));
            float scale = inKind > 0.5 ? 1.2 : 1.0;
            vec2 g = vec2(0.0);
            for (int i = 0; i < WAVES; i++)
            {
                float lambda = 1024.0 / length(K[i]);
                float fade = 1.0 - smoothstep(0.10 * lambda, 0.40 * lambda, pixel);
                float phase = TAU * dot(K[i], inWc) / 1024.0 + SPEED[i] * global.camera.time;
                g += normalize(K[i]) * cos(phase) * SLOPE[i] * fade * scale;
            }
            vec3 N = normalize(up - (tangent * g.x + bitangent * g.y));
            float NdotV = max(dot(N, V), 0.02);
            float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

            vec3 R = reflect(-V, N);
            float below = dot(R, up);
            if (below < 0.0)
                R -= 2.0 * below * up;
            vec3 reflection = skyColor(R, up, L) * skyLight;

            // The game's own ocean: water colour (0.588 x its colour map) x 0.03 x the sunlight, plus the sky reflection.
            vec3 deep = (inKind > 0.5 ? vec3(0.030, 0.085, 0.060) : vec3(0.020, 0.095, 0.170)) * 0.588 * 4.0;
            vec3 body = deep * 0.03 * length(sunLight + skyLight * 0.5) * mix(1.0, 0.6, 1.0 - day);

            // Sun glint: GGX highlight, rougher with distance (the waves it comes from are no longer resolved).
            vec3 H = normalize(L + V);
            float NdotH = max(dot(N, H), 0.0);
            float a = mix(0.05, 0.22, smoothstep(50.0, 4000.0, dist));
            float a2 = a * a;
            float dd = NdotH * NdotH * (a2 - 1.0) + 1.0;
            float D = a2 / (3.14159 * dd * dd);
            float fh = 0.02 + 0.98 * pow(1.0 - max(dot(H, V), 0.0), 5.0);
            vec3 glint = min(D * fh * 0.25 / NdotV, 4.0) * sunLight;

            vec3 color = fresnel * reflection + (1.0 - fresnel) * body + glint;
            float alpha = inKind > 0.5 ? (1.0 - smoothstep(0.6, 1.0, abs(inAcross))) * 0.96 : 1.0;
            outColor = vec4(color * alpha, alpha);
        }
        """;
}

internal static class WaterRenderer
{
    public static Settings? Settings;
    public static WaterField? Field;
    public static int LastTriangles;
    private static WaterTechnique? _technique;
    private static WaterTri[] _buffer = new WaterTri[4096];

    public static void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        LastTriangles = 0;
        if (viewport != Program.MainViewport || Settings is not { Enabled: true, Water: true } settings || Field is not { Active: true } field)
            return;
        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(settings.Body, out Astronomical? astronomical)
            || astronomical is not Celestial body)
            return;
        var tiles = field.Snapshot;
        if (tiles.Length == 0)
            return;

        Camera camera = viewport.GetCamera();
        double3 cam = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        double3 rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce);
        double3 ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce);
        double3 rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);
        float3 fx = new((float)rx.X, (float)rx.Y, (float)rx.Z), fy = new((float)ry.X, (float)ry.Y, (float)ry.Z), fz = new((float)rz.X, (float)rz.Y, (float)rz.Z);
        double radiusM = settings.WaterRadiusKm * 1000;

        var visible = new List<(WaterTile Tile, double3 Rel, double Dist)>();
        foreach (var tile in tiles)
        {
            double3 rel = tile.CenterCcf - cam;
            double dist = Math.Sqrt(double3.Dot(rel, rel));
            if (dist - tile.RadiusM > radiusM)
                continue;
            double3 ego = rx * rel.X + ry * rel.Y + rz * rel.Z;
            if (dist > tile.RadiusM && camera.IsSphereOutsideFov(ego, tile.RadiusM))
                continue;
            visible.Add((tile, ego, dist));
        }
        visible.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        int n = 0;
        foreach (var (tile, ego, _) in visible)
        {
            int triangles = tile.Count / 3;
            if (n + triangles > WaterTechnique.MaxTriangles)
                break;
            if (_buffer.Length < n + triangles)
                Array.Resize(ref _buffer, Math.Max(n + triangles, _buffer.Length * 2));
            var v = tile.Vertices;
            float3 center = new((float)ego.X, (float)ego.Y, (float)ego.Z);
            for (int t = 0; t < triangles; t++)
            {
                ref WaterVertex a = ref v[3 * t];
                ref WaterVertex b = ref v[3 * t + 1];
                ref WaterVertex c = ref v[3 * t + 2];
                _buffer[n++] = new WaterTri
                {
                    P0 = ToEgo(in a, center, fx, fy, fz),
                    P1 = ToEgo(in b, center, fx, fy, fz),
                    P2 = ToEgo(in c, center, fx, fy, fz),
                    Wx = new float4(a.Wx, b.Wx, c.Wx, a.Kind),
                    Wy = new float4(a.Wy, b.Wy, c.Wy, 0),
                };
            }
        }
        LastTriangles = n;
        if (n == 0)
            return;
        _technique ??= new WaterTechnique();
        _technique.Draw(commandBuffer, viewport, frameIndex, _buffer.AsSpan(0, n));
    }

    private static float4 ToEgo(in WaterVertex v, float3 center, float3 fx, float3 fy, float3 fz)
    {
        return new float4(
            center.X + fx.X * v.X + fy.X * v.Y + fz.X * v.Z,
            center.Y + fx.Y * v.X + fy.Y * v.Y + fz.Y * v.Z,
            center.Z + fx.Z * v.X + fy.Z * v.Y + fz.Z * v.Z,
            v.Across);
    }

    public static void RebuildFrameResources() => _technique?.RebuildFrameResources();
}
