using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;

namespace GEE;

/// <summary>
/// One grass clump: its ground point in camera-relative ego axes (the camera is at the origin) and radius; the
/// local sky/east basis as six snorm16 values and the time; colour, height, thinning rank and seed as halves and
/// the wind phase; tuning (translucency, sway, draw radius, time).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GrassInstance
{
    public float4 PositionSize;
    public uint4 Basis;
    public uint4 Style;
    public float4 Tuning;
}

/// <summary>
/// Own grass layer of crossed blade quads: a clump is three quads 60 degrees apart (18 vertices, no vertex buffer)
/// with blades cut out of them in the fragment shader (alpha test, written to depth), swaying in the wind in the
/// vertex shader and lit by the sun with a simple two-sided term, a see-through glow when looking towards the sun and
/// a sky ambient. Drawn in the main pass after the planet like the other GEE passes.
/// </summary>
internal sealed class GrassTechnique() : InstancedPass<GrassInstance>("GEEGrass", VertexShader, FragmentShader, MaxClumps, 18)
{
    public const int MaxClumps = 24_000;

    private static readonly PtrOwner<VkPipelineColorBlendStateCreateInfo> Opaque = new(new VkPipelineColorBlendStateCreateInfo
    {
        AttachmentCount = 1,
        Attachments = RenderingPresets.BlendAttachment.BlendNone,
    });

    protected override VertexInput MakeVertexInput()
    {
        return new VertexInput(1, 4).AddBindingWithAttributes<GrassInstance>(0, 0, VkVertexInputRate.Instance);
    }

    protected override void OnRebuildFrameResources()
    {
        CreatePipeline(VkPrimitiveTopology.TriangleList, VkCullModeFlags.None, VkFrontFace.CounterClockwise, VkPolygonMode.Fill,
            RenderingPresets.ReverseZDepthStencil.DepthTestWrite, Opaque, out Pipeline);
    }

    /// <summary>Mirrors the start of Common/Global.glsl: the whole Camera struct, then the lighting block up to the sun transmittance.</summary>
    internal const string GlobalBlock = """
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
            vec4 planetColor;
            vec4 occlusionColor;
        };

        layout(set = 0, binding = 0) uniform Global
        {
            Camera camera;
            GlobalLighting lighting;
        } global;
        """;

    internal const string VertexShader = ShaderSource.Version + GlobalBlock + """


        layout(location = 0) in vec4 posSize;
        layout(location = 1) in uvec4 basis;
        layout(location = 2) in uvec4 style;
        layout(location = 3) in vec4 tuning;

        layout(location = 0) out vec3 outWorld;
        layout(location = 1) out vec2 outUv;
        layout(location = 2) out vec3 outQuadNormal;
        layout(location = 3) out vec3 outUp;
        layout(location = 4) flat out vec4 outColor;
        layout(location = 5) flat out float outTranslucency;

        const int corners[6] = int[](0, 1, 2, 2, 1, 3);

        void main()
        {
            int quad = gl_VertexIndex / 6;
            int corner = corners[gl_VertexIndex - quad * 6];
            vec2 cuv = vec2(float(corner & 1), float(corner >> 1));

            vec2 a = unpackSnorm2x16(basis.x);
            vec2 b = unpackSnorm2x16(basis.y);
            vec2 c = unpackSnorm2x16(basis.z);
            vec3 up = normalize(vec3(a.x, a.y, b.x));
            vec3 east = vec3(b.y, c.x, c.y);
            east = normalize(east - up * dot(east, up));
            vec3 north = cross(up, east);

            vec2 rg = unpackHalf2x16(style.x);
            vec2 bh = unpackHalf2x16(style.y);
            vec2 rs = unpackHalf2x16(style.z);
            float rank = rs.x;
            float seed = rs.y;
            float height = bh.y;
            float radius = tuning.z;
            float dist = length(posSize.xyz);

            // Thinning with distance: each clump has a rank, those above the share kept at its distance collapse.
            float keep = mix(1.0, 0.3, smoothstep(10.0, radius, dist));
            if (rank > keep || dist > radius)
            {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
                outWorld = vec3(0.0);
                outUv = vec2(0.0);
                outQuadNormal = vec3(0.0, 0.0, 1.0);
                outUp = up;
                outColor = vec4(0.0);
                outTranslucency = 0.0;
                return;
            }
            float fade = 1.0 - smoothstep(radius * 0.78, radius, dist);
            // The remaining clumps grow a little to make up for the thinned ones.
            float spread = mix(1.0, 1.5, smoothstep(10.0, radius, dist));
            float halfWidth = posSize.w * spread * (0.4 + 0.6 * fade);
            float h = height * fade;

            float yaw = (seed + float(quad) / 3.0) * 3.14159265;
            vec3 right = east * cos(yaw) + north * sin(yaw);
            vec3 qn = cross(up, right);

            vec3 local = right * ((cuv.x - 0.5) * 2.0 * halfWidth) + up * (cuv.y * h);

            // Wind: a slow gust, a faster sway and a flutter at the tips; the phase is fixed to the ground.
            vec3 wind = normalize(east * 0.8 + north * 0.6);
            float phase = uintBitsToFloat(style.w);
            float t = tuning.w;
            float gust = sin(t * 1.3 + phase) * 0.55 + sin(t * 2.7 + phase * 1.7 + seed * 12.0) * 0.25
                + sin(t * 0.37 + phase * 0.31) * 0.8 + 0.5;
            float flutter = sin(t * 7.0 + seed * 31.0 + cuv.x * 3.0) * 0.04;
            float bend = (gust + flutter) * tuning.y * cuv.y * cuv.y * h * 0.45;
            local += wind * bend - up * (0.5 * bend * bend / max(h, 0.05));

            vec3 world = posSize.xyz + local;
            gl_Position = global.camera.viewProjection * vec4(world, 1.0);
            outWorld = world;
            outUv = cuv;
            outQuadNormal = qn;
            outUp = up;
            outColor = vec4(vec3(rg, bh.x), seed + float(quad) * 0.31);
            outTranslucency = tuning.x;
        }
        """;

    internal const string FragmentShader = ShaderSource.Version + GlobalBlock + """


        layout(location = 0) in vec3 inWorld;
        layout(location = 1) in vec2 inUv;
        layout(location = 2) in vec3 inQuadNormal;
        layout(location = 3) in vec3 inUp;
        layout(location = 4) flat in vec4 inColor;
        layout(location = 5) flat in float inTranslucency;

        layout(location = 0) out vec4 outColor;

        const float PI = 3.14159265;

        float hash11(float p)
        {
            p = fract(p * 0.1031);
            p *= p + 33.33;
            p *= p + p;
            return fract(p);
        }

        void main()
        {
            float seed = inColor.w;
            vec2 uv = inUv;

            // Blades cut out of the quad: tapering triangles, taller in the middle, each leaning a little.
            float cover = 0.0;
            float bladeId = 0.0;
            for (int i = 0; i < 14; i++)
            {
                float fi = float(i);
                float h1 = hash11(seed * 91.7 + fi * 7.13);
                float h2 = hash11(seed * 53.1 + fi * 3.71 + 0.5);
                float h3 = hash11(seed * 17.9 + fi * 11.3 + 0.25);
                float c = 0.08 + 0.84 * h1;
                float mid = 1.0 - abs(c - 0.5) * 1.1;
                float hh = (0.45 + 0.55 * h2) * mid;
                float w = 0.022 + 0.03 * h3;
                float lean = (h2 - 0.5) * 0.3;
                float t = uv.y / max(hh, 0.05);
                float x = uv.x - c - lean * uv.y * uv.y;
                float halfW = w * (1.0 - t);
                if (t < 1.0 && abs(x) < halfW)
                {
                    cover = 1.0;
                    bladeId = h1 + h3;
                }
            }
            if (cover < 0.5)
                discard;

            vec3 V = normalize(-inWorld);
            vec3 L = normalize(global.lighting.sunPosition.xyz - inWorld);
            vec3 up = normalize(inUp);
            vec3 n = normalize(inQuadNormal);
            if (!gl_FrontFacing)
                n = -n;
            // Blade normals lean towards the sky so the clump is lit like a soft mass, not like flat cards.
            vec3 N = normalize(mix(n, up, 0.55));

            vec3 albedo = inColor.rgb * mix(0.7, 1.25, fract(bladeId * 7.31));
            albedo *= mix(0.38, 1.1, smoothstep(0.0, 0.75, uv.y));
            albedo = mix(albedo, albedo * vec3(1.3, 1.15, 0.55), 0.3 * uv.y);
            float ao = mix(0.3, 1.0, smoothstep(0.0, 0.55, uv.y));

            float sunUp = dot(up, L);
            float aboveHorizon = smoothstep(-0.02, 0.08, sunUp);
            float day = smoothstep(-0.15, 0.25, sunUp);
            vec3 sun = global.lighting.sunColor.rgb * global.lighting.occlusionColor.rgb * global.camera.pbrExposure;

            // Thin blades take light from both sides: the far side counts for part of the near side.
            float diffuse = mix(clamp(dot(N, L), 0.0, 1.0), clamp(dot(-N, L), 0.0, 1.0), 0.4);
            vec3 direct = albedo / PI * sun * diffuse * aboveHorizon;

            // Looking towards the sun: the light comes through the blades, tinted yellow-green, mostly at the tips.
            float towards = clamp(dot(-V, L), 0.0, 1.0);
            float glow = pow(towards, 4.0) * (0.25 + 0.75 * uv.y);
            vec3 through = albedo * vec3(1.15, 1.2, 0.45) * sun * glow * inTranslucency * 1.4 * aboveHorizon;

            vec3 ambient = albedo / PI * global.lighting.sunColor.rgb * global.camera.pbrExposure
                * vec3(0.12, 0.135, 0.17) * day * ao;

            outColor = vec4(direct + through + ambient, 1.0);
        }
        """;
}

/// <summary>
/// Camera-centred grass out to GrassLayerRadiusM. The ground is cut into 1 m cells of the cube-sphere grid (the
/// game's QSC mapping, stable on the planet, see Clutter): each cell has one jittered clump candidate decided by a
/// hash of its cell, so the same grass grows at the same place whenever the camera comes back. A worker thread picks
/// the candidates around the camera into a patch (positions relative to the patch centre) and rebuilds it when the
/// camera has moved a few metres or the tiles under it changed; the main thread only rotates the patch into
/// camera-relative axes and copies it into the instance buffer.
/// Candidates are dropped on: roads and buildings (the footprint rectangles of the ready tiles' instances), launch
/// pads, sea level and below, slopes steeper than GrassLayerSlopeMaxDeg (a coarse 4 m height grid), and by the ESA
/// WorldCover class of the place (water, snow, mangrove, bare ground: none; built-up land: a few lawn clumps; trees:
/// sparse dark undergrowth). The colour is a fixed palette per class modulated by ground-fixed noise (dry patches,
/// brightness) and per clump variation. Ground heights come from Elevation.Height, i.e. what the patched terrain mesh
/// does, cached per cell on the worker.
/// </summary>
internal static class GrassLayer
{
    public static Settings? Settings;
    public static TileManager? Manager;

    public static int LastClumps;
    public static string? Error;
    public static string Status = "off";

    private const double CellMeters = 1.0;
    /// <summary>The patch reaches this much further than the draw radius; it is rebuilt when the camera is 3/4 of it away.</summary>
    private const double MarginMeters = 14;
    private const int CoarseCells = 4;

    private static GrassTechnique? _technique;
    private static GrassInstance[] _instances = new GrassInstance[4096];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static PadSet _pads = new(Array.Empty<double3>(), Array.Empty<double>());
    private static Celestial? _padsBody;

    private readonly record struct PadSet(double3[] Dirs, double[] Radii);

    private readonly record struct Config(double Radius, double Density, double HeightM, double SlopeMax, bool LandCover, bool Buildings, bool Roads, double ClumpSize);

    private sealed record Request(Celestial Body, double3 Cam, Config Config, PadSet Pads);

    private sealed class Patch
    {
        public Celestial Body = null!;
        public double3 Center;
        public double3 BuiltAt;
        public Config Config;
        public GrassClump[] Clumps = Array.Empty<GrassClump>();
        public int Count;
    }

    private struct GrassClump
    {
        public float Ox, Oy, Oz, Size;
        public uint S0, S1, S2, S3;
    }

    private static volatile Request? _request;
    private static volatile Patch? _ready;
    private static Thread? _worker;
    private static volatile bool _dirty;

    // ---- main thread ----

    /// <summary>Runs inside the main opaque pass, after the planet and static objects are recorded.</summary>
    public static void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        if (viewport != Program.MainViewport || Error != null)
            return;
        if (Settings is not { GrassLayer: true } settings || Manager == null)
        {
            LastClumps = 0;
            Status = "off";
            return;
        }
        try
        {
            if (Universe.CurrentSystem is not { } system || !system.All.TryGet(settings.Body, out Astronomical? astronomical)
                || astronomical is not Celestial body)
                return;
            Camera camera = viewport.GetCamera();
            double3 camCcf = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
            double camR = Length(camCcf);
            // Grass is only seen close to the ground (the draw radius is at most 120 m).
            if (camR - body.MeanRadius > 15000)
            {
                _request = null;
                LastClumps = 0;
                Status = "camera too high";
                return;
            }
            UpdatePads(body);
            var config = new Config(Math.Clamp(settings.GrassLayerRadiusM, 15, 120), Math.Clamp(settings.GrassLayerDensity, 0.05, 1),
                Math.Clamp(settings.GrassLayerHeightM, 0.1, 1.5), Math.Clamp(settings.GrassLayerSlopeMaxDeg, 5, 70),
                settings.GrassLayerLandCover, settings.ShowBuildings, settings.ShowRoads, Math.Clamp(settings.GrassLayerClumpMeters, 0.2, 1.5));
            if (_request is not { } old || !ReferenceEquals(old.Body, body) || old.Config != config
                || double3.Dot(old.Cam - camCcf, old.Cam - camCcf) > 0.25 || !ReferenceEquals(old.Pads.Dirs, _pads.Dirs))
                _request = new Request(body, camCcf, config, _pads);
            EnsureWorker();

            if (_ready is not { } patch || !ReferenceEquals(patch.Body, body) || patch.Count == 0)
            {
                LastClumps = 0;
                return;
            }
            // Behind the camera's reach (worker still busy): nothing wrong, the shader fades the far clumps.
            using (Perf.Measure("grass"))
                LastClumps = Emit(commandBuffer, viewport, frameIndex, camera, body, camCcf, patch, settings);
        }
        catch (Exception e)
        {
            Error = e.Message;
            Status = "off: " + e.Message;
            Log.Error($"grass layer disabled after error: {e}");
        }
    }

    private static int Emit(CommandBuffer commandBuffer, IViewport viewport, int frameIndex, Camera camera, Celestial body, double3 camCcf,
        Patch patch, Settings settings)
    {
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        double3 rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce);
        double3 ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce);
        double3 rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);
        double3 egoCenter = Rotate(patch.Center - camCcf, rx, ry, rz);
        float3 rxf = F(rx), ryf = F(ry), rzf = F(rz);

        double3 upCcf = double3.Normalize(patch.Center);
        Placement.Axes(upCcf, out double3 eastCcf, out _);
        float3 up = F(Rotate(upCcf, rx, ry, rz)), east = F(Rotate(eastCcf, rx, ry, rz));
        uint b0 = Snorm(up.X, up.Y), b1 = Snorm(up.Z, east.X), b2 = Snorm(east.Y, east.Z);
        float time = (float)(Clock.Elapsed.TotalSeconds % 100000);
        var tuning = new float4((float)Math.Clamp(settings.GrassLayerTranslucency, 0, 2), (float)Math.Clamp(settings.GrassLayerSway, 0, 2),
            (float)patch.Config.Radius, time);

        float reach = (float)(patch.Config.Radius * 1.02);
        float reach2 = reach * reach;
        int count = Math.Min(patch.Count, GrassTechnique.MaxClumps);
        if (_instances.Length < count)
            Array.Resize(ref _instances, Math.Max(count, _instances.Length * 2));
        var instances = _instances;
        var clumps = patch.Clumps;
        float3 center = new((float)egoCenter.X, (float)egoCenter.Y, (float)egoCenter.Z);
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            ref GrassClump c = ref clumps[i];
            float x = center.X + rxf.X * c.Ox + ryf.X * c.Oy + rzf.X * c.Oz;
            float y = center.Y + rxf.Y * c.Ox + ryf.Y * c.Oy + rzf.Y * c.Oz;
            float z = center.Z + rxf.Z * c.Ox + ryf.Z * c.Oy + rzf.Z * c.Oz;
            if (x * x + y * y + z * z > reach2)
                continue;
            ref GrassInstance g = ref instances[n++];
            g.PositionSize = new float4(x, y, z, c.Size);
            g.Basis = new uint4(b0, b1, b2, 0);
            g.Style = new uint4(c.S0, c.S1, c.S2, c.S3);
            g.Tuning = tuning;
        }
        if (n == 0)
            return 0;
        _technique ??= new GrassTechnique();
        _technique.Draw(commandBuffer, viewport, frameIndex, instances.AsSpan(0, n));
        return n;
    }

    /// <summary>After the game rebuilds its render targets (resize, MSAA change).</summary>
    public static void RebuildFrameResources() => _technique?.RebuildFrameResources();

    /// <summary>Launch pads of the body (the game clears its own clutter to their footprint plus 50 m).</summary>
    private static void UpdatePads(Celestial body)
    {
        if (ReferenceEquals(body, _padsBody))
            return;
        _padsBody = body;
        var pads = (body.BodyTemplate?.Locations ?? new List<LocationReference>()).OfType<LandmarkReference>().Where(l => l.IsLaunchPad).ToArray();
        _pads = new PadSet(pads.Select(l => l.ForwardCcf).ToArray(),
            pads.Select(l => l.GetStaticObject()?.FootprintRadius is > 0 and var r ? (double)r : 110.0).ToArray());
    }

    private static void EnsureWorker()
    {
        if (_worker != null)
            return;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "GEE grass", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    // ---- worker ----

    private static void WorkerLoop()
    {
        var builder = new Builder();
        double lastTilesChange = 0;
        int lastSignature = 0;
        while (true)
        {
            try
            {
                Request? request = _request;
                Patch? built = _ready;
                if (request == null)
                {
                    Thread.Sleep(100);
                    continue;
                }
                bool rebuild = built == null || !ReferenceEquals(built.Body, request.Body) || built.Config != request.Config
                    || double3.Dot(built.BuiltAt - request.Cam, built.BuiltAt - request.Cam) > (MarginMeters * 0.75) * (MarginMeters * 0.75);
                if (!rebuild)
                {
                    // Tiles that became ready (new roads and buildings) or land cover that arrived: build again, not more than once a second.
                    int signature = TileSignature(request);
                    double now = Clock.Elapsed.TotalSeconds;
                    if ((signature != lastSignature || _dirty) && now - lastTilesChange > 1)
                    {
                        rebuild = true;
                        lastTilesChange = now;
                        lastSignature = signature;
                    }
                }
                if (!rebuild)
                {
                    Thread.Sleep(30);
                    continue;
                }
                _dirty = false;
                lastSignature = TileSignature(request);
                var sw = Stopwatch.StartNew();
                Patch patch = builder.Build(request);
                _ready = patch;
                Status = $"{patch.Count:N0} clumps, built in {sw.Elapsed.TotalMilliseconds:0} ms ({builder.Describe()})";
                Log.Info("grass: " + Status);
            }
            catch (Exception e)
            {
                Status = "worker failed: " + e.Message;
                Log.Error($"grass layer worker: {e}");
                Thread.Sleep(3000);
            }
        }
    }

    /// <summary>
    /// A fingerprint of the ready tiles that can reach the patch (the ones GatherFootprints reads): a tile arriving 8 km away leaves
    /// the grass around the camera as it is and no longer rebuilds it.
    /// </summary>
    private static int TileSignature(Request request)
    {
        int signature = 17;
        if (Manager?.Snapshot is not { } tiles)
            return signature;
        // The camera may be up to Radius * 3 + 30 above the ground point the patch is made around.
        double reachBase = request.Config.Radius * 4 + 30 + MarginMeters + 60;
        foreach (var tile in tiles)
        {
            if (tile.State != TileState.Ready)
                continue;
            double3 td = tile.CenterCcf - request.Cam;
            double reach = tile.RadiusM + reachBase;
            if (double3.Dot(td, td) > reach * reach)
                continue;
            signature = signature * 31 + tile.Count + tile.Key.X * 7;
        }
        return signature;
    }

    private sealed class Builder
    {
        private readonly Dictionary<long, float> _heights = new();
        private readonly Dictionary<long, (double3 Position, float Height)> _coarse = new();
        private WorldCover? _cover;
        private bool _coverFailed;
        private readonly ConcurrentDictionary<(int, int), long> _requested = new();
        private object? _elevation;
        private Celestial? _body;
        private int _candidates, _kept, _unknownClass;

        public string Describe() => $"{_candidates:N0} cells, {_unknownClass:N0} without land cover";

        private struct Foot
        {
            public double3 Base, A, B;
            public double Ha, Hb;
        }

        public Patch Build(Request rq)
        {
            Celestial body = rq.Body;
            Config cfg = rq.Config;
            if (!ReferenceEquals(body, _body) || !ReferenceEquals(_elevation, Elevation.Active) || _heights.Count > 150_000)
            {
                _heights.Clear();
                _coarse.Clear();
                _body = body;
                _elevation = Elevation.Active;
            }
            _candidates = _kept = _unknownClass = 0;
            double R = body.MeanRadius;
            double3 dirC = double3.Normalize(rq.Cam);
            double hCam = Elevation.Height(body, dirC);
            var patch = new Patch { Body = body, Config = cfg, BuiltAt = rq.Cam, Center = dirC * (R + hCam) };
            // High above the ground nothing of it is seen.
            if (Length(rq.Cam) - (R + hCam) > cfg.Radius * 3 + 30)
                return patch;

            double rb = cfg.Radius + MarginMeters;
            int face = Clutter.FaceOf(dirC);
            int res = (int)Math.Round(Math.PI / 2 * R / CellMeters);
            var (u0, v0) = Clutter.ProjectToFace(face, dirC);
            int cx = (int)Math.Floor(u0 * res), cy = (int)Math.Floor(v0 * res);
            double sx = Length(Clutter.QscUvToDirection(face, (cx + 1.0) / res, v0) - Clutter.QscUvToDirection(face, (double)cx / res, v0)) * R;
            double sy = Length(Clutter.QscUvToDirection(face, u0, (cy + 1.0) / res) - Clutter.QscUvToDirection(face, u0, (double)cy / res)) * R;
            sx = Math.Max(sx, 0.2);
            sy = Math.Max(sy, 0.2);
            int halfX = (int)Math.Ceiling(rb * 1.1 / sx) + 1, halfY = (int)Math.Ceiling(rb * 1.1 / sy) + 1;

            var feet = GatherFootprints(rq, dirC, rb, cfg, out Grid? grid);
            var results = new List<GrassClump>(8192);
            for (int iy = cy - halfY; iy <= cy + halfY; iy++)
            {
                for (int ix = cx - halfX; ix <= cx + halfX; ix++)
                {
                    double dxm = (ix - cx) * sx, dym = (iy - cy) * sy;
                    if (dxm * dxm + dym * dym > rb * rb * 1.3)
                        continue;
                    _candidates++;
                    ulong h = CellHash(face, ix, iy);
                    double rank = (h & 0xFFFFFF) / 16777216.0;
                    double rankEff = rank / cfg.Density;
                    if (rankEff > 1)
                        continue;
                    double jx = ((h >> 24) & 0xFFFF) / 65536.0, jy = ((h >> 40) & 0xFFFF) / 65536.0;
                    double3 dir = Clutter.QscUvToDirection(face, (ix + jx) / res, (iy + jy) / res);
                    double3 flat = (dir - dirC) * R;
                    double d2 = double3.Dot(flat, flat);
                    if (d2 > rb * rb)
                        continue;
                    // Same thinning as the vertex shader, at the nearest the camera can come before the next rebuild.
                    double lod = KeepLod(Math.Max(Math.Sqrt(d2) - MarginMeters, 0), cfg.Radius);
                    if (rankEff > lod)
                        continue;

                    double3 flatPoint = dir * R;
                    if (OnPad(rq.Pads, dir, R))
                        continue;
                    ulong h2 = Mix(h);
                    double seed = (h2 & 0xFFFF) / 65536.0, v1 = ((h2 >> 16) & 0xFFFF) / 65536.0, v2 = ((h2 >> 32) & 0xFFFF) / 65536.0;
                    double clumpMargin = 0.35 + 0.4 * cfg.ClumpSize;
                    if (grid != null && Covered(feet, grid, flatPoint, clumpMargin))
                        continue;

                    // Land cover.
                    double density = 1, heightFactor = 1;
                    (double R_, double G_, double B_) colour = (0.062, 0.112, 0.030);
                    if (cfg.LandCover)
                    {
                        byte? cls = ClassAt(dir);
                        if (cls == null)
                            _unknownClass++;
                        (density, heightFactor, colour) = Palette(cls ?? WorldCover.Grassland);
                        if (density <= 0)
                            continue;
                        if (rankEff > density)
                            continue;
                    }

                    double height = CachedHeight(face, ix, iy, dir, body);
                    if (height < 0.4)
                        continue;
                    double slope = SlopeDeg(face, ix, iy, res, R, body);
                    double slopeFade = Math.Clamp((cfg.SlopeMax - slope) / Math.Max(cfg.SlopeMax * 0.25, 1), 0, 1);
                    if (slopeFade <= 0.05)
                        continue;

                    // Ground-fixed variation: large dry patches, medium brightness patches, per clump scatter.
                    double dry = Noise(face, ix / 45.0, iy / 45.0) * 0.65 + Noise(face, ix / 11.0 + 7, iy / 11.0 + 3) * 0.35;
                    double dryMix = Math.Clamp((dry - 0.5) * 2.2, 0, 1) * 0.75;
                    double bright = (0.8 + 0.4 * Noise(face, ix / 6.0 + 20, iy / 6.0 + 11)) * (0.88 + 0.24 * v1);
                    double r = colour.R_ * bright, g = colour.G_ * bright, b = colour.B_ * bright;
                    r = Lerp(r, r * 1.65 + 0.012, dryMix);
                    g = Lerp(g, g * 1.25 + 0.006, dryMix);
                    b = Lerp(b, b * 0.8, dryMix);

                    double size = cfg.ClumpSize * (0.8 + 0.45 * v2) * (0.5 + 0.5 * slopeFade);
                    double blade = cfg.HeightM * heightFactor * (0.65 + 0.7 * v1) * (0.4 + 0.6 * slopeFade);
                    double3 position = dir * (R + height - 0.03);
                    double3 off = position - patch.Center;
                    double phase = ((ix * 0.55 + iy * 0.40) * 0.55) % (Math.PI * 2);
                    results.Add(new GrassClump
                    {
                        Ox = (float)off.X, Oy = (float)off.Y, Oz = (float)off.Z, Size = (float)size,
                        S0 = Halves((float)r, (float)g),
                        S1 = Halves((float)b, (float)blade),
                        S2 = Halves((float)rankEff, (float)seed),
                        S3 = BitConverter.SingleToUInt32Bits((float)phase),
                    });
                    _kept++;
                }
            }
            if (results.Count > GrassTechnique.MaxClumps)
            {
                // Over the buffer: keep the lowest ranks (they are the ones the thinning keeps longest).
                results.Sort((a, b) => HalfRank(a.S2).CompareTo(HalfRank(b.S2)));
                results.RemoveRange(GrassTechnique.MaxClumps, results.Count - GrassTechnique.MaxClumps);
            }
            patch.Clumps = results.ToArray();
            patch.Count = patch.Clumps.Length;
            return patch;
        }

        private static float HalfRank(uint s2) => (float)BitConverter.UInt16BitsToHalf((ushort)(s2 & 0xFFFF));

        // ---- exclusions ----

        /// <summary>Footprints bucketed in 6 m cells of the tangent plane around the camera's ground point.</summary>
        private sealed class Grid
        {
            public const double Cell = 6;
            public List<int>?[] Cells = null!;
            public int N;
            public double Extent;
            public double3 Origin, East, North;
        }

        private List<Foot> GatherFootprints(Request rq, double3 dirC, double rb, Config cfg, out Grid? grid)
        {
            var feet = new List<Foot>();
            grid = null;
            if (Manager?.Snapshot is not { } tiles)
                return feet;
            double R = rq.Body.MeanRadius;
            double3 ground = dirC * R;
            foreach (var tile in tiles)
            {
                if (tile.State != TileState.Ready)
                    continue;
                double3 td = tile.CenterCcf - ground;
                double reach = tile.RadiusM + rb + 60;
                if (double3.Dot(td, td) > reach * reach)
                    continue;
                BuildingInstance[] instances = tile.Instances;
                int count = Math.Min(tile.Count, instances.Length);
                for (int i = 0; i < count; i++)
                {
                    ref readonly BuildingInstance b = ref instances[i];
                    if (b.IsDetail)
                        continue;
                    if ((b.Layer == Layers.Building || b.Layer == Layers.LaunchComplex) && !cfg.Buildings)
                        continue;
                    if (b.Layer == Layers.Street && !cfg.Roads)
                        continue;
                    double la = Length(b.AxisA), lb = Length(b.AxisB);
                    if (la < 1e-3 || lb < 1e-3)
                        continue;
                    double3 d = b.Base - ground;
                    double reachB = rb + la + lb;
                    if (double3.Dot(d, d) > reachB * reachB)
                        continue;
                    // Horizontal rectangle around the box's base point (as Clutter.Collect).
                    double3 up = double3.Normalize(b.Base);
                    double3 a = b.AxisA - up * double3.Dot(b.AxisA, up);
                    double3 s = b.AxisB - up * double3.Dot(b.AxisB, up);
                    double ha = Length(a), hb = Length(s);
                    if (ha < 1e-6 || hb < 1e-6)
                        continue;
                    feet.Add(new Foot { Base = b.Base, A = a / ha, B = s / hb, Ha = la / 2, Hb = lb / 2 });
                }
            }
            if (feet.Count == 0)
                return feet;
            var g = new Grid { Extent = rb + 2, Origin = ground };
            Placement.Axes(dirC, out g.East, out g.North);
            g.N = (int)Math.Ceiling(2 * g.Extent / Grid.Cell) + 1;
            g.Cells = new List<int>?[g.N * g.N];
            for (int i = 0; i < feet.Count; i++)
            {
                Foot f = feet[i];
                double3 rel = f.Base - ground;
                double fe = double3.Dot(rel, g.East), fn = double3.Dot(rel, g.North);
                double ee = f.Ha * Math.Abs(double3.Dot(f.A, g.East)) + f.Hb * Math.Abs(double3.Dot(f.B, g.East)) + 2;
                double en = f.Ha * Math.Abs(double3.Dot(f.A, g.North)) + f.Hb * Math.Abs(double3.Dot(f.B, g.North)) + 2;
                int e0 = Math.Max((int)Math.Floor((fe - ee + g.Extent) / Grid.Cell), 0), e1 = Math.Min((int)Math.Floor((fe + ee + g.Extent) / Grid.Cell), g.N - 1);
                int n0 = Math.Max((int)Math.Floor((fn - en + g.Extent) / Grid.Cell), 0), n1 = Math.Min((int)Math.Floor((fn + en + g.Extent) / Grid.Cell), g.N - 1);
                for (int gn = n0; gn <= n1; gn++)
                {
                    for (int ge = e0; ge <= e1; ge++)
                        (g.Cells[gn * g.N + ge] ??= new List<int>(4)).Add(i);
                }
            }
            grid = g;
            return feet;
        }

        private static bool Covered(List<Foot> feet, Grid grid, double3 point, double margin)
        {
            double3 rel = point - grid.Origin;
            int ge = (int)Math.Floor((double3.Dot(rel, grid.East) + grid.Extent) / Grid.Cell);
            int gn = (int)Math.Floor((double3.Dot(rel, grid.North) + grid.Extent) / Grid.Cell);
            if (ge < 0 || gn < 0 || ge >= grid.N || gn >= grid.N || grid.Cells[gn * grid.N + ge] is not { } list)
                return false;
            foreach (int i in list)
            {
                Foot f = feet[i];
                double3 d = point - f.Base;
                if (Math.Abs(double3.Dot(d, f.A)) <= f.Ha + margin && Math.Abs(double3.Dot(d, f.B)) <= f.Hb + margin)
                    return true;
            }
            return false;
        }

        private static bool OnPad(PadSet pads, double3 dir, double R)
        {
            for (int i = 0; i < pads.Dirs.Length; i++)
            {
                double3 d = (dir - pads.Dirs[i]) * R;
                double limit = pads.Radii[i] + 20;
                if (double3.Dot(d, d) < limit * limit)
                    return true;
            }
            return false;
        }

        // ---- land cover ----

        private byte? ClassAt(double3 dir)
        {
            if (_coverFailed)
                return null;
            double lat = Math.Asin(Math.Clamp(dir.Z, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
            try
            {
                _cover ??= new WorldCover(SatelliteImagery.ModDir, NetLoader.CreateGetRange(SatelliteImagery.ModDir));
                if (_cover.ClassAt(lat, lon) is { } known)
                    return known;
                // Not in memory: fetch its 10 km block in the background, build with the default class meanwhile.
                const double block = 1024.0 / 12000;
                var key = ((int)Math.Floor(lat / block), (int)Math.Floor(lon / block));
                long now = Clock.ElapsedMilliseconds;
                if (!_requested.TryGetValue(key, out long at) || now - at > 30000)
                {
                    _requested[key] = now;
                    var cover = _cover;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            if (cover.Load(lat, lon))
                                _dirty = true;
                        }
                        catch (Exception e)
                        {
                            Log.Error($"grass land cover: {e.Message}");
                        }
                    });
                }
                return null;
            }
            catch (Exception e)
            {
                _coverFailed = true;
                Log.Error($"grass land cover unavailable, using one grass type: {e.Message}");
                return null;
            }
        }

        /// <summary>Share of cells that grow a clump, height factor and linear colour for a WorldCover class.</summary>
        private static (double Density, double Height, (double R, double G, double B) Colour) Palette(byte cls) => cls switch
        {
            WorldCover.Grassland => (1.0, 1.0, (0.062, 0.112, 0.030)),
            WorldCover.Cropland => (0.85, 0.85, (0.105, 0.105, 0.040)),
            WorldCover.Shrubland => (0.85, 1.1, (0.075, 0.085, 0.036)),
            WorldCover.Trees => (0.55, 0.8, (0.034, 0.068, 0.020)),
            WorldCover.Wetland => (0.75, 1.3, (0.050, 0.092, 0.040)),
            WorldCover.Moss => (0.6, 0.5, (0.060, 0.090, 0.040)),
            WorldCover.BuiltUp => (0.3, 0.7, (0.070, 0.108, 0.035)),
            WorldCover.Bare => (0.07, 0.5, (0.140, 0.115, 0.060)),
            _ => (0, 0, (0, 0, 0)),     // water, snow and ice, mangrove, no data (sea)
        };

        // ---- ground heights ----

        private double CachedHeight(int face, int ix, int iy, double3 dir, Celestial body)
        {
            long key = Key(face, ix, iy);
            if (_heights.TryGetValue(key, out float h))
                return h;
            h = (float)Elevation.Height(body, dir);
            _heights[key] = h;
            return h;
        }

        private (double3 Position, float Height) Coarse(int face, int cx, int cy, int res, double R, Celestial body)
        {
            long key = Key(face, cx, cy);
            if (_coarse.TryGetValue(key, out var known))
                return known;
            double3 dir = Clutter.QscUvToDirection(face, (double)cx * CoarseCells / res, (double)cy * CoarseCells / res);
            float h = (float)Elevation.Height(body, dir);
            known = (dir * (R + h), h);
            _coarse[key] = known;
            return known;
        }

        /// <summary>Slope of the ground around a cell in degrees, from the plane through three corners of its 4 m cell.</summary>
        private double SlopeDeg(int face, int ix, int iy, int res, double R, Celestial body)
        {
            int cx = Math.DivRem(ix, CoarseCells, out int rx), cy = Math.DivRem(iy, CoarseCells, out int ry);
            if (rx < 0) cx--;
            if (ry < 0) cy--;
            var p00 = Coarse(face, cx, cy, res, R, body).Position;
            var p10 = Coarse(face, cx + 1, cy, res, R, body).Position;
            var p01 = Coarse(face, cx, cy + 1, res, R, body).Position;
            double3 normal = double3.Cross(p10 - p00, p01 - p00);
            double len = Length(normal);
            if (len < 1e-9)
                return 0;
            double3 up = double3.Normalize(p00);
            double cos = Math.Abs(double3.Dot(normal, up)) / len;
            return Math.Acos(Math.Clamp(cos, 0, 1)) * 180 / Math.PI;
        }
    }

    // ---- shared maths ----

    /// <summary>Share of clumps the shader keeps at a distance (same curve as the vertex shader).</summary>
    private static double KeepLod(double distance, double radius)
    {
        double t = Math.Clamp((distance - 10) / Math.Max(radius - 10, 1), 0, 1);
        return 1 - 0.7 * (t * t * (3 - 2 * t));
    }

    private static long Key(int face, int x, int y) => ((long)face << 58) | ((long)(x & 0x1FFFFFFF) << 29) | (long)(y & 0x1FFFFFFF);

    private static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    private static ulong CellHash(int face, int ix, int iy) =>
        Mix(((ulong)(uint)ix | ((ulong)(uint)iy << 32)) ^ ((ulong)(face + 1) * 0xD6E8FEB86659FD93UL));

    /// <summary>Smooth value noise in 0..1 on the cells of a face.</summary>
    private static double Noise(int face, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        static double Corner(int f, int cx, int cy) => (CellHash(f + 8, cx, cy) >> 40) / 16777216.0;
        double a = Lerp(Corner(face, x0, y0), Corner(face, x0 + 1, y0), fx);
        double b = Lerp(Corner(face, x0, y0 + 1), Corner(face, x0 + 1, y0 + 1), fx);
        return Lerp(a, b, fy);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static uint Halves(float lo, float hi) =>
        BitConverter.HalfToUInt16Bits((Half)lo) | (uint)BitConverter.HalfToUInt16Bits((Half)hi) << 16;

    private static uint Snorm(float lo, float hi)
    {
        static uint One(float v) => (ushort)(short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);
        return One(lo) | One(hi) << 16;
    }

    private static float3 F(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));

    private static double3 Rotate(double3 v, double3 rx, double3 ry, double3 rz) => rx * v.X + ry * v.Y + rz * v.Z;
}
