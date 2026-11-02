using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;

namespace GEE;

/// <summary>
/// One satellite tile on the ground: a local tangent frame at the tile centre (ego axes), the tile's bounds, a grid
/// of terrain heights over it, and where its image lies in the texture array.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SatInstance
{
    /// <summary>xyz: tile centre at the lowest terrain height of the tile (ego), w: texture layer.</summary>
    public float4 Center;
    /// <summary>Unit east, north and up at the centre (ego axes); Up.w: height span of the grid (m).</summary>
    public float4 East;
    public float4 North;
    public float4 Up;
    /// <summary>Centre latitude and longitude, Mercator ordinate of the north and south edges (radians).</summary>
    public float4 Geo;
    /// <summary>West and east longitude (radians), radius of the lowest terrain (planet radius plus height), lift.</summary>
    public float4 Geo2;
    /// <summary>UV offset and scale within the layer (a parent's part while the tile itself loads).</summary>
    public float4 Uv;
    /// <summary>x opacity, y sunlight, z height (m) the tile is pulled in front of the ground, along the view ray.</summary>
    public float4 Params;
    /// <summary>
    /// 9 x 9 terrain heights (rows north to south on the vertex grid's Mercator rows, columns west to east), 10 bits
    /// each as a fraction of the span above the lowest, three per word.
    /// </summary>
    public uint4 H0, H1, H2, H3, H4, H5, H6;
}

/// <summary>
/// Satellite imagery draped over the terrain: each tile is a 16 x 16 grid whose points sit on the sphere at their
/// exact latitude and longitude (Web Mercator rows), written relative to the tile centre in a form that stays precise
/// in single precision for tiles of a few hundred metres as well as for tiles of a thousand kilometres, and lifted to
/// the sampled terrain heights; textured from the tile's layer of a texture array.
/// Blended over the planet (premultiplied alpha), depth tested, no depth writes.
/// </summary>
internal sealed class SatelliteTechnique(VkImageView array) : InstancedPass<SatInstance>("GEESatellite", VertexShader, FragmentShader,
    MaxTiles, Grid * Grid * 6, array, Program.LinearClampedSampler)
{
    public const int MaxTiles = 1024, Grid = 16;

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
        return new VertexInput(1, 15).AddBindingWithAttributes<SatInstance>(0, 0, VkVertexInputRate.Instance);
    }

    protected override void OnRebuildFrameResources()
    {
        CreatePipeline(VkPrimitiveTopology.TriangleList, VkCullModeFlags.None, VkFrontFace.CounterClockwise, VkPolygonMode.Fill,
            RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite, Blend, out Pipeline);
    }

    private const string VertexShader = ShaderSource.Version + ShaderSource.CameraBlock + """

        layout(location = 0) in vec4 center;
        layout(location = 1) in vec4 east;
        layout(location = 2) in vec4 north;
        layout(location = 3) in vec4 up;
        layout(location = 4) in vec4 geo;
        layout(location = 5) in vec4 geo2;
        layout(location = 6) in vec4 uvRect;
        layout(location = 7) in vec4 params;
        layout(location = 8) in uvec4 h0;
        layout(location = 9) in uvec4 h1;
        layout(location = 10) in uvec4 h2;
        layout(location = 11) in uvec4 h3;
        layout(location = 12) in uvec4 h4;
        layout(location = 13) in uvec4 h5;
        layout(location = 14) in uvec4 h6;

        layout(location = 0) out vec3 outUv;
        layout(location = 1) flat out vec2 outLook;

        const int GRID = 16, HGRID = 9;
        const vec2 corners[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));

        float gridHeight(uvec4 words[7], int col, int row)
        {
            int i = row * HGRID + col, w = i / 3;
            return float((words[w >> 2][w & 3] >> uint(10 * (i % 3))) & 1023u) / 1023.0;
        }

        void main()
        {
            int cell = gl_VertexIndex / 6;
            vec2 q = (vec2(cell % GRID, cell / GRID) + corners[gl_VertexIndex % 6]) / float(GRID);
            // q: x eastward, y southward (as the image). Mercator rows: latitude from the ordinate.
            float lat = atan(sinh(mix(geo.z, geo.w, q.y)));
            float lon = mix(geo2.x, geo2.y, q.x);
            float lat0 = geo.x;
            float dLat = lat - lat0, dLon = lon - geo.y;
            float sLat = sin(0.5 * dLat), sLon = sin(0.5 * dLon);
            uvec4 words[7] = uvec4[](h0, h1, h2, h3, h4, h5, h6);
            vec2 g = q * float(HGRID - 1);
            ivec2 g0 = min(ivec2(g), ivec2(HGRID - 2));
            vec2 f = g - vec2(g0);
            float h = mix(mix(gridHeight(words, g0.x, g0.y), gridHeight(words, g0.x + 1, g0.y), f.x),
                          mix(gridHeight(words, g0.x, g0.y + 1), gridHeight(words, g0.x + 1, g0.y + 1), f.x), f.y) * up.w;
            float r = geo2.z + h;
            // The point's direction in the centre's east/north/up frame, minus up, without cancellation.
            float cl = cos(lat);
            vec3 local = vec3(cl * sin(dLon),
                              sin(dLat) + 2.0 * cl * sin(lat0) * sLon * sLon,
                              -2.0 * sLat * sLat - 2.0 * cl * cos(lat0) * sLon * sLon) * r;
            local.z += h + geo2.w;
            vec3 p = center.xyz + east.xyz * local.x + north.xyz * local.y + up.xyz * local.z;
            // Pulled toward the camera along the view ray: the same place on screen, but params.z metres (vertically)
            // in front of the terrain between the height samples, of the game's coarse distant terrain triangles,
            // and of the trees, which the photo already shows. Grazing rays need a longer pull for the same height.
            float dist = length(p);
            float sinDepression = max(-dot(p / dist, up.xyz), 0.05);
            p *= 1.0 - min(params.z / sinDepression / dist, 0.5);
            gl_Position = global.camera.viewProjection * vec4(p, 1.0);
            outUv = vec3(uvRect.xy + q * uvRect.zw, center.w);
            outLook = params.xy;
        }
        """;

    private const string FragmentShader = """
        #version 450

        layout(location = 0) in vec3 uv;
        layout(location = 1) flat in vec2 look;

        layout(set = 1, binding = 0) uniform sampler2DArray imagery;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            vec3 color = texture(imagery, uv).rgb;
            // The images are display referred (sRGB); the scene is linear.
            color = pow(color, vec3(2.2)) * look.y;
            float alpha = look.x;
            outColor = vec4(color * alpha, alpha);
        }
        """;
}

/// <summary>
/// Sentinel-2 cloudless imagery (EOX IT Services, s2maps.eu, 2016 edition, CC BY 4.0; contains modified Copernicus
/// Sentinel data 2016) over the ground when seen from altitude, so towns, cities, fields and roads look as they do
/// from a real aircraft or from orbit. Web Mercator tiles of 256 pixels are chosen by distance (finer near the
/// camera, coarser far away), downloaded and cached on disk by worker threads, decoded with the game's stb, and
/// kept in the layers of one texture array (least recently used layers are reused). A tile still loading is drawn
/// from the matching part of a loaded ancestor.
/// </summary>
internal static class SatelliteImagery
{
    private const int TileSize = 256, Layers = 512, MaxZoom = 14, MinZoom = 3, UploadsPerFrame = 8, WorkerCount = 8;
    /// <summary>Seconds a newly loaded tile takes to fade in over the coarser image it replaces.</summary>
    private const double FadeInSeconds = 0.6;
    /// <summary>Trees and low buildings the drape covers once fully faded in.</summary>
    private const double CanopyMeters = 45;
    private const string Url = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{0}/{2}/{1}.jpg";
    private const string UserAgent = "KSA-GEE/0.2 (Kitten Space Agency mod; satellite imagery)";

    public static Settings? Settings;
    public static string ModDir = "";
    public static int LastTiles, LoadedLayers;
    public static string? LastError;

    private static SimpleVkTexture? _texture;
    private static SatelliteTechnique? _technique;
    private static StagingPool? _staging;
    private static readonly Dictionary<(int Z, int X, int Y), int> Layer = new();
    private static readonly (int Z, int X, int Y)?[] LayerKey = new (int, int, int)?[Layers];
    private static readonly long[] LayerUsed = new long[Layers];
    private static readonly double[] LayerLoadedAt = new double[Layers];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly ConcurrentDictionary<(int, int, int), byte[]> Decoded = new();
    private static readonly HashSet<(int, int, int)> Pending = new();
    private static readonly Dictionary<(int, int, int), double> Wanted = new();
    /// <summary>Points per side of a tile's height grid, and the words the grid packs into (see SatInstance).</summary>
    private const int HeightPoints = 9, HeightWords = 28;
    private static readonly ConcurrentDictionary<(int, int, int), TileHeights> HeightCache = new();
    /// <summary>Tiles whose heights are queued or being sampled, with the last frame they were wanted in.</summary>
    private static readonly ConcurrentDictionary<(int, int, int), long> HeightPending = new();
    private static readonly ConcurrentQueue<(int, int, int)> HeightQueue = new();
    private static readonly List<((int, int, int) Key, double Dist)> HeightWanted = new();
    private static Celestial? _body;
    private static long _heightTicks, _heightTiles;
    private static readonly object Lock = new();
    private static readonly List<SatInstance> Instances = new();
    private static SatInstance[] _buffer = new SatInstance[256];
    private static long _frame;
    private static bool _inFlight;
    private static Thread[]? _workers;
    private static readonly SemaphoreSlim Signal = new(0);
    private static Func<string, string, byte[]>? _get;

    public static void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        if (viewport != Program.MainViewport)
            return;
        LastTiles = 0;
        if (Settings is not { Enabled: true, SatelliteImagery: true } settings)
            return;
        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(settings.Body, out Astronomical? astronomical)
            || astronomical is not Celestial body)
            return;
        if (_texture == null)
            return;                                                    // made in Upload, outside the render pass
        try
        {
            _frame++;
            int n = Collect(viewport.GetCamera(), body, settings);
            LastTiles = n;
            if (n == 0)
                return;
            _technique ??= new SatelliteTechnique(_texture!.VkImageView);
            _technique.Draw(commandBuffer, viewport, frameIndex, _buffer.AsSpan(0, n));
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Log.Error($"satellite imagery: {e}");
            settings.SatelliteImagery = false;
        }
    }

    /// <summary>Main thread, before the frame is recorded: puts decoded tiles into free layers, in one submission.</summary>
    public static void Upload()
    {
        if (Settings is not { Enabled: true, SatelliteImagery: true })
            return;
        try
        {
            EnsureResources();
            var staging = _staging!;
            // The previous batch must be through before its staging buffers are reused; never wait for it here.
            if (_inFlight)
            {
                if (!staging.WaitFor(0))
                    return;
                _inFlight = false;
            }
            if (Decoded.IsEmpty)
                return;
            CommandBuffer cb = default;
            bool started = false;
            int done = 0;
            foreach (var key in Decoded.Keys.ToList())
            {
                if (done >= UploadsPerFrame)
                    break;
                int layer = FreeLayer();
                if (layer < 0 || !Decoded.TryRemove(key, out byte[]? pixels))
                    break;
                if (LayerKey[layer] is { } old)
                    Layer.Remove(old);
                if (!started)
                {
                    cb = staging.NextCommandBuffer();
                    cb.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
                    started = true;
                }
                RecordLayer(cb, staging, layer, pixels);
                LayerKey[layer] = key;
                Layer[key] = layer;
                LayerUsed[layer] = _frame;
                LayerLoadedAt[layer] = Clock.Elapsed.TotalSeconds;
                lock (Lock)
                    Pending.Remove(key);
                done++;
            }
            if (started)
            {
                cb.End();
                staging.Submit();
                _inFlight = true;
            }
            LoadedLayers = Layer.Count;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Log.Error($"satellite imagery upload: {e}");
            Settings!.SatelliteImagery = false;
        }
    }

    private static int FreeLayer()
    {
        int best = -1;
        long oldest = long.MaxValue;
        for (int i = 0; i < Layers; i++)
        {
            if (LayerKey[i] == null)
                return i;
            // Not drawn in the last frames, so no frame in flight still reads it.
            if (LayerUsed[i] < oldest && _frame - LayerUsed[i] > 4)
            {
                oldest = LayerUsed[i];
                best = i;
            }
        }
        return best;
    }

    private static void EnsureResources()
    {
        if (_texture != null)
            return;
        var renderer = Program.GetRenderer();
        _texture = new SimpleVkTexture("GEE satellite imagery", renderer.Allocator, TileSize, TileSize, 1, VkFormat.R8G8B8A8UNorm, 1, Layers);
        _staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
        // Every layer readable from the start (layers not loaded yet are never sampled, but must be in a valid layout).
        CommandBuffer cb = _staging.NextCommandBuffer();
        cb.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        cb.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                DstAccessMask = VkAccessFlags.ShaderReadBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = _texture.VkImage,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = Layers },
            }));
        cb.End();
        _staging.Submit();
        _staging.Wait();
        _get ??= NetLoader.CreateGet(ModDir);
        _workers = new Thread[WorkerCount];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"GEE satellite {i}" };
            _workers[i].Start();
        }
    }

    private static void RecordLayer(CommandBuffer cb, StagingPool staging, int layer, byte[] pixels)
    {
        BufferEx buffer = staging.AddStagingBuffer<byte>(pixels.AsSpan());
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, BaseArrayLayer = layer, LayerCount = 1 };
        cb.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.HostBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.ShaderReadBit,
                DstAccessMask = VkAccessFlags.TransferWriteBit,
                OldLayout = VkImageLayout.ShaderReadOnlyOptimal,
                NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = _texture!.VkImage,
                SubresourceRange = range,
            }));
        cb.CopyBufferToImage(buffer.VkBuffer, _texture.VkImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, BaseArrayLayer = layer, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = TileSize, Height = TileSize, Depth = 1 },
        }));
        cb.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.TransferWriteBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit,
                OldLayout = VkImageLayout.TransferDstOptimal,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = _texture.VkImage,
                SubresourceRange = range,
            }));
    }

    // ---- tile choice ----

    private static int Collect(Camera camera, Celestial body, Settings settings)
    {
        Instances.Clear();
        double3 cam = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
        double camR = Math.Sqrt(double3.Dot(cam, cam));
        double planetR = body.MeanRadius;
        double altitude = camR - planetR;
        float fade = Smooth(settings.SatelliteFadeFromM, settings.SatelliteFadeFromM * 4, altitude);
        if (fade <= 0)
            return 0;
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        double3 rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce);
        double3 ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce);
        double3 rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);
        double3 camDir = cam / camR;
        double horizon = Math.Acos(Math.Clamp(planetR / camR, -1, 1));
        double pixelAngle = 2 * Math.Tan(camera.GetFieldOfView() / 2) / Math.Max(camera.FramebufferSize.Y, 1);
        double3? sun = NightLights.SunDirCcf(body, settings);
        lock (Lock)
            Wanted.Clear();
        HeightWanted.Clear();
        _body = body;
        if (HeightCache.Count > 50000)
            HeightCache.Clear();

        void Visit(int z, int x, int y)
        {
            var (s, w, n, e) = VectorTile.Bounds(z, x, y);
            double3 c = Placement.DirCcf((s + n) / 2, (w + e) / 2);
            double cosLat = Math.Cos((s + n) / 2 * Math.PI / 180);
            double halfE = (e - w) / 2 * Math.PI / 180 * planetR * cosLat;
            double halfN = (n - s) / 2 * Math.PI / 180 * planetR;
            double radius = Math.Sqrt(halfE * halfE + halfN * halfN);
            double angle = Math.Acos(Math.Clamp(double3.Dot(c, camDir), -1, 1));
            if (angle - radius / planetR > horizon + 0.02)
                return;
            double3 rel = c * planetR - cam;
            double dist = Math.Sqrt(double3.Dot(rel, rel));
            // A margin around the view, so turning the camera finds the tiles already there.
            if (dist > radius * 1.3 && camera.IsSphereOutsideFov(Rotate(rel, rx, ry, rz), radius * 1.3))
                return;
            double near = Math.Max(dist - radius, altitude * 0.9);
            // Split while a texel would cover more than about 1.5 pixels.
            double texel = 2 * halfN / TileSize;
            if (z < MaxZoom && texel / (near * pixelAngle) > 1.5)
            {
                for (int k = 0; k < 4; k++)
                    Visit(z + 1, x * 2 + (k & 1), y * 2 + (k >> 1));
                return;
            }
            Emit(z, x, y, c, halfE, halfN, rel, dist, body, planetR, rx, ry, rz, fade, sun);
        }

        int roots = 1 << MinZoom;
        for (int x = 0; x < roots; x++)
            for (int y = 0; y < roots; y++)
                Visit(MinZoom, x, y);

        lock (Lock)
        {
            int added = 0;
            // Coarse tiles first (they stand in for everything below them), near before far.
            foreach (var key in Wanted.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Value).Select(kv => kv.Key).Take(64))
            {
                if (!Layer.ContainsKey(key) && !Decoded.ContainsKey(key) && Pending.Add(key))
                {
                    WorkQueue.Enqueue(key);
                    added++;
                }
            }
            // Heights the same way; queued tiles still wanted are marked, so workers skip the ones left behind.
            int heights = 0;
            foreach (var (key, _) in HeightWanted.OrderBy(w => w.Key.Item1).ThenBy(w => w.Dist))
            {
                // TryUpdate, so a key a worker has just finished is not put back without being queued.
                if (HeightPending.TryGetValue(key, out long seen))
                    HeightPending.TryUpdate(key, _frame, seen);
                else if (heights < 64 && HeightPending.TryAdd(key, _frame))
                {
                    HeightQueue.Enqueue(key);
                    heights++;
                }
            }
            if (added + heights > 0)
                Signal.Release(added + heights);
        }

        int count = Math.Min(Instances.Count, SatelliteTechnique.MaxTiles);
        if (_buffer.Length < count)
            Array.Resize(ref _buffer, Math.Max(count, _buffer.Length * 2));
        for (int i = 0; i < count; i++)
            _buffer[i] = Instances[i];
        return count;
    }

    private static readonly ConcurrentQueue<(int, int, int)> WorkQueue = new();

    private static void Emit(int z, int x, int y, double3 centerDir, double halfE, double halfN, double3 rel, double dist, Celestial body,
        double planetR, double3 rx, double3 ry, double3 rz, float fade, double3? sun)
    {
        var key = (z, x, y);
        // The tile's own layer, or the nearest loaded ancestor's matching part.
        int layer = -1, az = z, ax = x, ay = y;
        while (az >= MinZoom - 1)
        {
            if (Layer.TryGetValue((az, ax, ay), out layer))
                break;
            layer = -1;
            ax >>= 1;
            ay >>= 1;
            az--;
        }
        if (az != z)
            lock (Lock)
            {
                Wanted[key] = dist;
                // The parent too, so the stand-in gets sharper soon even while this one waits in the queue.
                if (z > MinZoom && !Layer.ContainsKey((z - 1, x >> 1, y >> 1)))
                    Wanted.TryAdd((z - 1, x >> 1, y >> 1), dist);
            }
        bool ownHeights = HeightCache.TryGetValue(key, out TileHeights? own);
        if (!ownHeights)
            HeightWanted.Add((key, dist));
        if (layer < 0)
            return;

        Span<uint> words = stackalloc uint[HeightWords];
        float low, span, residual;
        if (ownHeights)
        {
            own!.Words.CopyTo(words);
            (low, span, residual) = (own.Low, own.Span, own.Residual);
        }
        else if (!AncestorHeights(z, x, y, words, out low, out span, out residual))
            return;                                                    // nothing to stand on yet
        Placement.Axes(centerDir, out double3 east, out double3 north);
        // rel is the centre on the mean sphere minus the camera; the ground point adds the terrain height.
        double3 egoCenter = Rotate(rel + centerDir * low, rx, ry, rz);
        double3 eE = Rotate(east, rx, ry, rz), eN = Rotate(north, rx, ry, rz), eU = Rotate(centerDir, rx, ry, rz);
        float light = sun is { } sd ? (float)Math.Clamp(0.1 + 1.15 * double3.Dot(centerDir, sd), 0.02, 1.0) : 1f;
        var (bs, bw, bn, be) = VectorTile.Bounds(z, x, y);
        double lat0 = (bs + bn) / 2 * Math.PI / 180, lon0 = (bw + be) / 2 * Math.PI / 180;
        // Height the drape must clear: the terrain rising between the height samples (measured when sampled) plus
        // the 10-bit rounding, the game's coarse distant terrain triangles (a small part of the tile's size), and
        // once faded in, the trees and low buildings standing on the ground (the photo shows them already).
        double pull = 3 + residual + span / 1023 + 0.004 * halfN + CanopyMeters * fade;
        var instance = new SatInstance
        {
            Center = new float4((float)egoCenter.X, (float)egoCenter.Y, (float)egoCenter.Z, layer),
            East = new float4((float)eE.X, (float)eE.Y, (float)eE.Z, 0),
            North = new float4((float)eN.X, (float)eN.Y, (float)eN.Z, 0),
            Up = new float4((float)eU.X, (float)eU.Y, (float)eU.Z, span),
            Geo = new float4((float)lat0, (float)lon0, (float)Merc(bn), (float)Merc(bs)),
            Geo2 = new float4((float)(bw * Math.PI / 180), (float)(be * Math.PI / 180), (float)(planetR + low), 3),
            Params = new float4(fade, light, (float)pull, 0),
            H0 = new uint4(words[0], words[1], words[2], words[3]),
            H1 = new uint4(words[4], words[5], words[6], words[7]),
            H2 = new uint4(words[8], words[9], words[10], words[11]),
            H3 = new uint4(words[12], words[13], words[14], words[15]),
            H4 = new uint4(words[16], words[17], words[18], words[19]),
            H5 = new uint4(words[20], words[21], words[22], words[23]),
            H6 = new uint4(words[24], words[25], words[26], words[27]),
        };

        LayerUsed[layer] = _frame;
        // A tile that has just arrived fades in over the coarser part it replaces, instead of popping.
        double age = Clock.Elapsed.TotalSeconds - LayerLoadedAt[layer];
        if (az == z && age < FadeInSeconds)
        {
            int pl = -1, pz = z - 1, px = x >> 1, py = y >> 1;
            for (; pz >= MinZoom - 1; pz--, px >>= 1, py >>= 1)
                if (Layer.TryGetValue((pz, px, py), out pl))
                    break;
            if (pl >= 0 && pz >= MinZoom - 1)
            {
                LayerUsed[pl] = _frame;
                var under = instance;
                under.Center.W = pl;
                under.Uv = Part(z, x, y, pz, px, py);
                Instances.Add(under);
                float t = (float)(age / FadeInSeconds);
                instance.Params.X = fade * t * t * (3 - 2 * t);
            }
        }
        instance.Uv = Part(z, x, y, az, ax, ay);
        Instances.Add(instance);
    }

    /// <summary>Where tile z/x/y lies inside its ancestor az/ax/ay, as UV offset and scale.</summary>
    private static float4 Part(int z, int x, int y, int az, int ax, int ay)
    {
        int levels = z - az;
        float scale = 1f / (1 << levels);
        return new float4((x - (ax << levels)) * scale, (y - (ay << levels)) * scale, scale, scale);
    }

    private static double Merc(double latDeg) => Math.Asinh(Math.Tan(latDeg * Math.PI / 180));

    /// <summary>A tile's terrain: heights (m) on the grid, packed for the shader, and how far the ground rises above it.</summary>
    private sealed class TileHeights
    {
        public readonly float[] Values = new float[HeightPoints * HeightPoints];
        public readonly uint[] Words = new uint[HeightWords];
        public float Low, Span, Residual;
    }

    /// <summary>
    /// Worker thread: terrain heights of a tile on a grid twice as fine as the one the shader gets (rows on the same
    /// Mercator ordinates as the vertex rows); every other point goes to the shader, the ones between tell how far
    /// the ground rises above the interpolated surface.
    /// </summary>
    private static TileHeights SampleHeights(Celestial body, int z, int x, int y)
    {
        const int Fine = 2 * (HeightPoints - 1) + 1;
        var (s, w, n, e) = VectorTile.Bounds(z, x, y);
        double mn = Merc(n), ms = Merc(s);
        var fine = new float[Fine * Fine];
        for (int j = 0; j < Fine; j++)
        {
            double lat = Math.Atan(Math.Sinh(mn + (ms - mn) * j / (Fine - 1))) * 180 / Math.PI;
            for (int i = 0; i < Fine; i++)
            {
                double lon = w + (e - w) * i / (Fine - 1);
                // Water: the drape lies on the sea surface, not on the seabed.
                fine[j * Fine + i] = (float)Math.Max(body.GetTerrainHeightFromDirCcf(Placement.DirCcf(lat, lon)), 0);
            }
        }
        var t = new TileHeights();
        for (int j = 0; j < HeightPoints; j++)
            for (int i = 0; i < HeightPoints; i++)
                t.Values[j * HeightPoints + i] = fine[2 * j * Fine + 2 * i];
        float residual = 0;
        for (int j = 0; j < Fine; j++)
            for (int i = 0; i < Fine; i++)
            {
                // Between grid points the shader's surface is the mean of the neighbouring grid points.
                int j0 = j / 2, j1 = (j + 1) / 2, i0 = i / 2, i1 = (i + 1) / 2;
                float surface = 0.25f * (t.Values[j0 * HeightPoints + i0] + t.Values[j0 * HeightPoints + i1]
                                         + t.Values[j1 * HeightPoints + i0] + t.Values[j1 * HeightPoints + i1]);
                residual = Math.Max(residual, fine[j * Fine + i] - surface);
            }
        // The finer sampling only catches part of what rises between the points.
        t.Residual = 1.5f * residual;
        Pack(t.Values, t.Words, out t.Low, out t.Span);
        return t;
    }

    /// <summary>Heights as 10-bit fractions of the span above the lowest one, three per word (see SatInstance).</summary>
    private static void Pack(ReadOnlySpan<float> values, Span<uint> words, out float low, out float span)
    {
        low = float.MaxValue;
        float high = float.MinValue;
        foreach (float v in values)
        {
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }
        span = high - low;
        float scale = span > 0 ? 1023 / span : 0;
        words.Clear();
        for (int i = 0; i < values.Length; i++)
            words[i / 3] |= (uint)Math.Clamp((int)MathF.Round((values[i] - low) * scale), 0, 1023) << (10 * (i % 3));
    }

    /// <summary>
    /// While a tile's own heights are being sampled: the nearest sampled ancestor's surface over the same area, so
    /// the tile neither sinks to sea level nor jumps. False if no ancestor has heights either.
    /// </summary>
    private static bool AncestorHeights(int z, int x, int y, Span<uint> words, out float low, out float span, out float residual)
    {
        for (int pz = z - 1, px = x >> 1, py = y >> 1; pz >= MinZoom; pz--, px >>= 1, py >>= 1)
        {
            if (!HeightCache.TryGetValue((pz, px, py), out TileHeights? parent))
                continue;
            float4 part = Part(z, x, y, pz, px, py);
            Span<float> values = stackalloc float[HeightPoints * HeightPoints];
            const int Last = HeightPoints - 1;
            for (int j = 0; j < HeightPoints; j++)
                for (int i = 0; i < HeightPoints; i++)
                {
                    // Mercator rows split evenly into the children's, so the part maps linearly onto the grid.
                    float gx = (part.X + part.Z * i / Last) * Last, gy = (part.Y + part.W * j / Last) * Last;
                    int x0 = Math.Min((int)gx, Last - 1), y0 = Math.Min((int)gy, Last - 1);
                    float fx = gx - x0, fy = gy - y0;
                    float[] v = parent.Values;
                    float top = v[y0 * HeightPoints + x0] + (v[y0 * HeightPoints + x0 + 1] - v[y0 * HeightPoints + x0]) * fx;
                    float bottom = v[(y0 + 1) * HeightPoints + x0] + (v[(y0 + 1) * HeightPoints + x0 + 1] - v[(y0 + 1) * HeightPoints + x0]) * fx;
                    values[j * HeightPoints + i] = top + (bottom - top) * fy;
                }
            Pack(values, words, out low, out span);
            residual = parent.Residual;
            return true;
        }
        low = span = residual = 0;
        return false;
    }

    // ---- workers: download, cache, decode ----

    private static void WorkerLoop()
    {
        while (true)
        {
            // Heights first: a tile is not drawn without them, and they need no network.
            if (HeightJob())
                continue;
            if (!WorkQueue.TryDequeue(out var key))
            {
                Signal.Wait(2000);
                continue;
            }
            {
                var (z, x, y) = key;
                string path = Path.Combine(ModDir, "cache", "sat", z.ToString(), $"{x}_{y}.jpg");
                try
                {
                    byte[] jpg;
                    if (File.Exists(path))
                    {
                        jpg = File.ReadAllBytes(path);
                    }
                    else
                    {
                        jpg = _get!(string.Format(Url, z, x, y), UserAgent);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, jpg);
                    }
                    using var image = Stb.LoadFromMemory(jpg, 4);
                    if (image.Width != TileSize || image.Height != TileSize)
                        throw new InvalidOperationException($"unexpected tile size {image.Width}x{image.Height}");
                    Decoded[key] = image.AsSpan().ToArray();
                }
                catch (Exception e)
                {
                    LastError = $"satellite tile {z}/{x}/{y}: {e.Message}";
                    lock (Lock)
                        Pending.Remove(key);
                }
            }
        }
    }

    /// <summary>Samples the heights of one queued tile, unless the view left it behind. False if none is queued.</summary>
    private static bool HeightJob()
    {
        if (!HeightQueue.TryDequeue(out var key))
            return false;
        try
        {
            if (!HeightPending.TryGetValue(key, out long wanted) || Interlocked.Read(ref _frame) - wanted > 60 || _body is not { } body)
                return true;
            long start = Stopwatch.GetTimestamp();
            HeightCache[key] = SampleHeights(body, key.Item1, key.Item2, key.Item3);
            long ticks = Interlocked.Add(ref _heightTicks, Stopwatch.GetTimestamp() - start);
            long tiles = Interlocked.Increment(ref _heightTiles);
            if (tiles % 200 == 0)
                Log.Info($"satellite heights: {tiles} tiles sampled, {ticks * 1000.0 / Stopwatch.Frequency / tiles:F1} ms each");
        }
        catch (Exception e)
        {
            LastError = $"satellite heights {key}: {e.Message}";
        }
        finally
        {
            HeightPending.TryRemove(key, out _);
        }
        return true;
    }

    public static void RebuildFrameResources() => _technique?.RebuildFrameResources();

    private static float Smooth(double from, double to, double v)
    {
        double t = Math.Clamp((v - from) / (to - from), 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    private static double3 Rotate(double3 v, double3 rx, double3 ry, double3 rz) => rx * v.X + ry * v.Y + rz * v.Z;
}
