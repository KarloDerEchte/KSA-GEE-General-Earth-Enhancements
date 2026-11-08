using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using RenderCore;
using RenderCore.Systems;

namespace GEE;

/// <summary>
/// Land cover drives the ground material detail. A worker thread cuts a 256 x 256 window of ESA WorldCover
/// (10 m pixels, 2.56 km, centred on the camera and rebuilt when the camera leaves its middle) into four class weights
/// per pixel (cropland, bare ground, wetland, snow); the main thread uploads it as a small float texture in the
/// satellite upload batch (like GroundTint's table; the handle is reserved when the game compiles Planet.frag).
/// The terrain shader reads it with four fixed texel fetches (a bilinear blend by hand, no loops) and adds
/// cropland furrow rows, gravel grain and wind ripples on bare ground, dark wet pools on wetland, a smooth crust on
/// snow. Row and noise coordinates are tied to the fixed planet frame (phases computed on the CPU in double
/// precision per window), so the patterns do not jump when the window is rebuilt. Text of the shader functions is
/// prefixed geeClass; the block is added by GroundTint.PatchPlanet after the other ground code.
/// </summary>
internal static class GroundClasses
{
    public const int Size = 256, Rows = Size + 1;
    private const double PixelMeters = 10, RecenterMeters = 500, MaxAltitudeMeters = 6000;
    private const float Pitch = 1.5f, Magic = 24680f;
    /// <summary>The nine fixed row directions of the shader (CCF axes and diagonals); same literals as the GLSL array.</summary>
    private static readonly double[][] Axes =
    {
        new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
        new double[] { 0.70710678f, 0.70710678f, 0 }, new double[] { 0.70710678f, -0.70710678f, 0 },
        new double[] { 0, 0.70710678f, 0.70710678f }, new double[] { 0, 0.70710678f, -0.70710678f },
        new double[] { 0.70710678f, 0, 0.70710678f }, new double[] { -0.70710678f, 0, 0.70710678f },
    };

    public static int Handle = -1;
    private static bool _failed, _bound, _uploadedOnce;
    private static string _status = "off (switched on: restart the game)";
    private static SimpleVkTexture? _texture;
    private static float[]? _current;
    private static volatile float[]? _ready;
    private static volatile Request? _request;
    private static Thread? _worker;
    private static WorldCover? _cover;
    private static string _workerStatus = "waiting";

    private sealed record Request(double Lat, double Lon, double Radius);

    public static bool Active => Handle >= 0 && !_failed && GroundTint.Active && SatelliteImagery.Settings is { GroundClasses: true };
    public static bool HasTexture => _texture != null;
    public static string Status => _failed || Handle < 0 ? _status : _workerStatus;

    public static void Fail(string message)
    {
        if (Handle < 0)
            return;
        _failed = true;
        _status = $"off: {message}";
    }

    /// <summary>When the game compiles Planet.frag: a handle of the bindless array (the blank texture until ours is bound).</summary>
    public static bool Reserve()
    {
        if (Handle >= 0)
            return true;
        try
        {
            if (Program.Instance?.BindlessTextures is not { } library)
                return false;
            var blank = typeof(BindlessTextureLibrary).GetField("_emptyTexture", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(library) as SimpleVkTexture;
            if (blank == null)
                return false;
            Handle = library.AddTexture(blank.VkImageView);
            Log.Info($"ground classes: bindless handle {Handle}");
            return true;
        }
        catch (Exception e)
        {
            _status = $"off: {e.Message}";
            Log.Error($"ground classes: {e}");
            return false;
        }
    }

    // ---- main thread (inside SatelliteImagery's upload) ----

    /// <summary>Hands the camera to the worker; true if the texture contents must be uploaded again.</summary>
    public static bool Build(double camLat, double camLon, double planetRadius, double altitude, Settings settings)
    {
        float strength = _failed ? 0 : (float)Math.Clamp(settings.GroundClassStrength, 0, 1);
        bool onEarth = string.Equals(settings.Body, "Earth", StringComparison.OrdinalIgnoreCase);
        if (onEarth && planetRadius > 0 && altitude < MaxAltitudeMeters)
        {
            _request = new Request(camLat, camLon, planetRadius);
            if (_worker == null)
            {
                _cover ??= new WorldCover(SatelliteImagery.ModDir, NetLoader.CreateGetRange(SatelliteImagery.ModDir));
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "GEE ground classes", Priority = ThreadPriority.BelowNormal };
                _worker.Start();
            }
        }
        else
        {
            _request = null;
            _workerStatus = onEarth ? "camera too high" : "land cover only for Earth";
        }
        bool changed = false;
        var ready = _ready;
        if (ready != null && !ReferenceEquals(ready, _current))
        {
            _current = ready;
            changed = true;
        }
        if (_current != null && _current[1] != strength)
        {
            _current[1] = strength;
            changed = true;
        }
        return changed;
    }

    public static void CreateTexture(Renderer renderer, CommandBuffer cb)
    {
        _texture = new SimpleVkTexture("GEE ground classes", renderer.Allocator, Size, Rows, 1, VkFormat.R32G32B32A32SFloat, 1);
        cb.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                DstAccessMask = VkAccessFlags.ShaderReadBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = _texture.VkImage,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            }));
    }

    public static void RecordUpload(CommandBuffer cb, StagingPool staging)
    {
        if (_texture == null || _current == null)
            return;
        BufferEx buffer = staging.AddStagingBuffer<byte>(MemoryMarshal.AsBytes(_current.AsSpan()));
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        cb.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.HostBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default,
            new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.ShaderReadBit,
                DstAccessMask = VkAccessFlags.TransferWriteBit,
                OldLayout = VkImageLayout.ShaderReadOnlyOptimal,
                NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = _texture.VkImage,
                SubresourceRange = range,
            }));
        cb.CopyBufferToImage(buffer.VkBuffer, _texture.VkImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = Size, Height = Rows, Depth = 1 },
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
        _uploadedOnce = true;
    }

    /// <summary>Points the handle at the texture once its first upload is submitted (the header marker enables the shader).</summary>
    public static void Bind()
    {
        if (_bound || !_uploadedOnce || _texture == null || Program.Instance?.BindlessTextures is not { } library)
            return;
        library.SetTexture(Handle, _texture.VkImageView);
        _bound = true;
        Log.Info("ground classes: map bound to the terrain shader");
    }

    // ---- worker ----

    private static V3 Unit(double lat, double lon) => new(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));

    private readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public V3 Normalized() => this * (1 / Length);
    }

    private static V3 _center;
    private static bool _haveWindow;
    private static DateTime _retryAt = DateTime.MinValue;

    private static void WorkerLoop()
    {
        while (true)
        {
            try
            {
                if (_request is not { } request || !Rebuild(request))
                    Thread.Sleep(250);
            }
            catch (Exception e)
            {
                _workerStatus = $"failed: {e.Message}";
                Log.Error($"ground classes: {e}");
                Thread.Sleep(5000);
            }
        }
    }

    /// <summary>Builds a new window if the camera left the middle of the current one. False if nothing was done.</summary>
    private static bool Rebuild(Request request)
    {
        V3 camera = Unit(request.Lat, request.Lon) * request.Radius;
        if (_haveWindow)
        {
            V3 d = new(camera.X - _center.X, camera.Y - _center.Y, camera.Z - _center.Z);
            if (d.Length < RecenterMeters || DateTime.UtcNow < _retryAt)
                return false;
        }
        // The centre as the shader will see it (floats): the rows' phases and the noise offset are computed from this
        // exact value, which is what keeps the patterns in place from one window to the next.
        V3 ctr = new((float)camera.X, (float)camera.Y, (float)camera.Z);
        V3 up = ctr.Normalized();
        V3 east = V3.Cross(new V3(0, 0, 1), up);
        if (east.Length < 1e-6)
            east = new V3(0, 1, 0);
        east = east.Normalized();
        V3 north = V3.Cross(up, east).Normalized();
        var cover = _cover!;

        var data = new float[Rows * Size * 4];
        bool complete = true;
        _workerStatus = "cutting the land cover window";
        for (int j = 0; j < Size; j++)
        {
            for (int i = 0; i < Size; i++)
            {
                V3 p = ctr + east * ((i + 0.5 - Size / 2) * PixelMeters) + north * ((j + 0.5 - Size / 2) * PixelMeters);
                double lat = Math.Asin(Math.Clamp(p.Z / p.Length, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(p.Y, p.X) * 180 / Math.PI;
                byte? cls = cover.ClassAt(lat, lon);
                if (cls == null && complete)
                {
                    if (cover.Load(lat, lon))
                        cls = cover.ClassAt(lat, lon);
                    else
                        complete = false;
                }
                int at = ((1 + j) * Size + i) * 4;
                switch (cls)
                {
                    case WorldCover.Cropland: data[at] = 1; break;
                    case WorldCover.Bare: data[at + 1] = 1; break;
                    case WorldCover.Wetland: data[at + 2] = 1; break;
                    case WorldCover.Mangroves: data[at + 2] = 0.7f; break;
                    case WorldCover.Snow: data[at + 3] = 1; break;
                }
            }
        }
        // Header (row 0).
        void Set(int x, double a, double b, double c, double d)
        {
            data[x * 4] = (float)a;
            data[x * 4 + 1] = (float)b;
            data[x * 4 + 2] = (float)c;
            data[x * 4 + 3] = (float)d;
        }
        Set(0, Magic, 0, request.Radius, PixelMeters);
        Set(1, ctr.X, ctr.Y, ctr.Z, 0);
        Set(2, east.X, east.Y, east.Z, 0);
        Set(3, north.X, north.Y, north.Z, 0);
        Set(4, Mod(ctr.X, 256), Mod(ctr.Y, 256), Mod(ctr.Z, 256), 0);
        for (int k = 0; k < Axes.Length; k++)
        {
            double phase = (ctr.X * Axes[k][0] + ctr.Y * Axes[k][1] + ctr.Z * Axes[k][2]) / Pitch;
            data[(5 + k / 4) * 4 + k % 4] = (float)(phase - Math.Floor(phase));
        }
        _ready = data;
        _center = ctr;
        _haveWindow = true;
        _retryAt = complete ? DateTime.MinValue : DateTime.UtcNow.AddSeconds(8);
        _workerStatus = complete ? $"window ready, {cover.BlocksLoaded} land cover blocks" : "land cover incomplete, retrying";
        return true;
    }

    private static double Mod(double v, double m) => v - Math.Floor(v / m) * m;

    // ---- Planet.frag patch ----

    private const string IncludeAnchor = "#include \"../Common/RingShadows.glsl\"\n";
    private const string SurfaceAnchor = "    vec3 surfaceColor = blendedDiffuse.xyz;\n";

    /// <summary>The shader text with the class code in front of the lighting, or null if off or the shader has changed.</summary>
    public static string? Patch(string source)
    {
        if (SatelliteImagery.Settings is not { GroundClasses: true } || source.Split(IncludeAnchor).Length != 2 || source.Split(SurfaceAnchor).Length != 2)
            return null;
        if (!Reserve())
            return null;
        string functions = Functions.Replace("GEE_CLASS_HANDLE", Handle.ToString(CultureInfo.InvariantCulture))
            .Replace("GEE_CLASS_PITCH", Pitch.ToString("0.0", CultureInfo.InvariantCulture));
        return source
            .Replace(IncludeAnchor, IncludeAnchor + functions)
            .Replace(SurfaceAnchor, SurfaceCode.Replace("GEE_CLASS_PITCH", Pitch.ToString("0.0", CultureInfo.InvariantCulture)) + SurfaceAnchor);
    }

    private const string Functions = """

        // ---- GEE: land cover ground classes (mods/GEE GroundClasses.cs) ----
        const int GEE_CLASS = GEE_CLASS_HANDLE;
        const vec3 geeClassAxes[9] = vec3[](vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 0.0, 1.0),
            vec3(0.70710678, 0.70710678, 0.0), vec3(0.70710678, -0.70710678, 0.0),
            vec3(0.0, 0.70710678, 0.70710678), vec3(0.0, 0.70710678, -0.70710678),
            vec3(0.70710678, 0.0, 0.70710678), vec3(-0.70710678, 0.0, 0.70710678));
        const int geeClassFirst[6] = int[](0, 1, 2, 3, 5, 7);
        const int geeClassSecond[6] = int[](1, 2, 0, 4, 6, 8);

        vec4 geeClassTexel(int x, int y)
        {
            return texelFetch(sampler2D(globalTextures[GEE_CLASS], pointSampler), ivec2(x, y), 0);
        }

        // Hash of a lattice point, wrapped with the mask m (a power of two minus one).
        uint geeClassHashU(ivec3 c, int m)
        {
            uvec3 u = uvec3(c & ivec3(m));
            uint h = u.x * 73856093u ^ u.y * 19349663u ^ u.z * 83492791u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return h;
        }

        float geeClassHash(ivec3 c, int m)
        {
            return float(geeClassHashU(c, m) & 0xFFFFu) / 65535.0;
        }

        // 3D value noise with features about s metres wide (s a power of two, at most 256); repeats every 256 m,
        // and q carries the window's offset, so it is the same noise in every window.
        float geeClassNoise(vec3 q, float s)
        {
            vec3 c = q / s;
            vec3 fl = floor(c);
            vec3 f = c - fl;
            f = f * f * (3.0 - 2.0 * f);
            ivec3 i = ivec3(fl);
            int m = int(256.0 / s) - 1;
            float a = mix(mix(geeClassHash(i, m), geeClassHash(i + ivec3(1, 0, 0), m), f.x),
                mix(geeClassHash(i + ivec3(0, 1, 0), m), geeClassHash(i + ivec3(1, 1, 0), m), f.x), f.y);
            float b = mix(mix(geeClassHash(i + ivec3(0, 0, 1), m), geeClassHash(i + ivec3(1, 0, 1), m), f.x),
                mix(geeClassHash(i + ivec3(0, 1, 1), m), geeClassHash(i + ivec3(1, 1, 1), m), f.x), f.y);
            return mix(a, b, f.z);
        }

        // Row phase (rows GEE_CLASS_PITCH m apart, stretched on slanted ground) of the direction picked for the cell of q
        // (cell a power of two, 64 or more): one of nine fixed directions, whose rows are the intersection of planes
        // fixed in the planet frame with the ground, never along the surface normal n. dir: direction across the rows
        // (planet frame), rnd: a random number for the cell.
        float geeClassRows(vec3 loc, vec3 q, vec3 n, float cell, int seed, out vec3 dir, out float rnd)
        {
            int m = int(256.0 / cell) - 1;
            uint h = geeClassHashU(ivec3(floor(q / cell)) + ivec3(seed), m);
            int pair = int(h % 6u);
            int k = geeClassFirst[pair];
            if (abs(dot(geeClassAxes[k], n)) > 0.8)
                k = geeClassSecond[pair];
            vec3 a = geeClassAxes[k];
            vec3 at = a - n * dot(a, n);
            dir = at / max(length(at), 1e-3);
            rnd = float((h >> 8) & 0xFFFFu) / 65535.0;
            return dot(loc, a) / GEE_CLASS_PITCH + geeClassTexel(5 + k / 4, 0)[k & 3];
        }
        // ---- end GEE ----

        """;

    private const string SurfaceCode = """
            // GEE: land cover classes (cropland rows, bare ground grain and ripples, wet pools, snow crust)
            {
                vec4 geeClassHead = geeClassTexel(0, 0);
                float geeClassFp = max(length(dFdx(inWorldPosition.xyz)), length(dFdy(inWorldPosition.xyz)));
                if (geeClassHead.x == 24680.0 && geeClassHead.y > 0.0 && abs(uboPlanet.radius - geeClassHead.z) < 1000.0)
                {
                    mat3 geeClassM = mat3(uboPlanet.cce2CcfMatrix);
                    vec3 geeClassCtr = geeClassTexel(1, 0).xyz;
                    vec3 geeClassEast = geeClassTexel(2, 0).xyz;
                    vec3 geeClassNorth = geeClassTexel(3, 0).xyz;
                    vec3 geeClassOff = geeClassTexel(4, 0).xyz;
                    // Position in the planet frame relative to the window centre: the camera-relative part is exact.
                    vec3 geeClassCam = -(geeClassM * inCelestialPosition.xyz) - geeClassCtr;
                    vec3 geeClassL = geeClassM * inWorldPosition.xyz + geeClassCam;
                    vec2 geeClassPc = vec2(dot(geeClassL, geeClassEast), dot(geeClassL, geeClassNorth)) / geeClassHead.w + 128.0;
                    float geeClassK = geeClassHead.y
                        * (1.0 - smoothstep(0.8, 0.98, max(abs(geeClassPc.x - 128.0), abs(geeClassPc.y - 128.0)) / 128.0));
                    // Bilinear blend of the four nearest pixels by hand.
                    vec2 geeClassF = geeClassPc - 0.5;
                    vec2 geeClassFl = floor(geeClassF);
                    vec2 geeClassT = geeClassF - geeClassFl;
                    ivec2 geeClassA = clamp(ivec2(geeClassFl), ivec2(0), ivec2(255));
                    ivec2 geeClassB = clamp(ivec2(geeClassFl) + 1, ivec2(0), ivec2(255));
                    vec4 geeClassW = mix(
                        mix(geeClassTexel(geeClassA.x, 1 + geeClassA.y), geeClassTexel(geeClassB.x, 1 + geeClassA.y), geeClassT.x),
                        mix(geeClassTexel(geeClassA.x, 1 + geeClassB.y), geeClassTexel(geeClassB.x, 1 + geeClassB.y), geeClassT.x),
                        geeClassT.y) * geeClassK;
                    if (max(max(geeClassW.x, geeClassW.y), max(geeClassW.z, geeClassW.w)) > 0.01)
                    {
                        vec3 geeClassQ = geeClassL + geeClassOff;
                        vec3 geeClassUp = normalize(geeClassCtr);
                        float geeClassDist = length(inWorldPosition.xyz);
                        float geeClassTone = 1.0;
                        vec3 geeClassTilt = vec3(0.0);
                        vec3 geeClassDir;
                        float geeClassRnd;
                        const float geeTwoPi = 6.28318531;

                        if (geeClassW.x > 0.01)
                        {
                            // Cropland: rows (direction and depth per 64 m field patch), field-to-field tone.
                            float s = geeClassRows(geeClassL, geeClassQ, geeClassUp, 64.0, 0, geeClassDir, geeClassRnd);
                            float vis = (1.0 - smoothstep(0.2, 0.5, geeClassFp / GEE_CLASS_PITCH)) * (1.0 - smoothstep(80.0, 200.0, geeClassDist));
                            float depth = 0.45 + 0.55 * geeClassRnd;
                            float tone = (geeClassNoise(geeClassQ, 32.0) - 0.5) * 0.14 + (geeClassRnd - 0.5) * 0.06
                                + 0.09 * depth * vis * cos(geeTwoPi * s);
                            geeClassTone += geeClassW.x * tone;
                            geeClassTilt += geeClassW.x * 0.28 * depth * vis * -sin(geeTwoPi * s) * (geeClassDir * geeClassM);
                        }
                        if (geeClassW.y > 0.01)
                        {
                            // Bare ground: gravel grain at three scales, and wind ripples in some patches.
                            float fine = 1.0 - smoothstep(0.1, 0.3, geeClassFp / 0.25);
                            float mid = 1.0 - smoothstep(0.1, 0.3, geeClassFp / 1.0);
                            float grain = (geeClassNoise(geeClassQ, 0.25) - 0.5) * 0.34 * fine
                                + (geeClassNoise(geeClassQ, 1.0) - 0.5) * 0.24 * mid
                                + (geeClassNoise(geeClassQ, 4.0) - 0.5) * 0.18;
                            float dune = smoothstep(0.45, 0.62, geeClassNoise(geeClassQ, 64.0));
                            float s = 3.0 * geeClassRows(geeClassL, geeClassQ, geeClassUp, 128.0, 7, geeClassDir, geeClassRnd)
                                + 0.35 * (geeClassNoise(geeClassQ, 2.0) - 0.5);
                            float vis = (1.0 - smoothstep(0.2, 0.5, geeClassFp / (0.5 * GEE_CLASS_PITCH))) * (1.0 - smoothstep(50.0, 140.0, geeClassDist)) * dune;
                            geeClassTone += geeClassW.y * (grain + 0.07 * vis * cos(geeTwoPi * s));
                            geeClassTilt += geeClassW.y * 0.35 * vis * -sin(geeTwoPi * s) * (geeClassDir * geeClassM);
                            roughness = mix(roughness, 0.97, geeClassW.y * 0.6);
                        }
                        if (geeClassW.z > 0.01)
                        {
                            // Wetland: darker ground with dark, smooth, glossy pools.
                            float pool = smoothstep(0.5, 0.62, geeClassNoise(geeClassQ, 8.0) * 0.6 + geeClassNoise(geeClassQ, 2.0) * 0.4);
                            geeClassTone *= 1.0 - geeClassW.z * (0.22 + 0.3 * pool);
                            roughness = mix(roughness, mix(0.55, 0.12, pool), geeClassW.z * (0.4 + 0.6 * pool));
                            worldNormal = normalize(mix(worldNormal, sphereWorldNormal, geeClassW.z * pool * 0.8));
                        }
                        if (geeClassW.w > 0.01)
                        {
                            // Snow: a smooth crust, soft drifts and a little sparkle.
                            float fine = 1.0 - smoothstep(0.1, 0.3, geeClassFp / 0.25);
                            geeClassTone += geeClassW.w * ((geeClassNoise(geeClassQ, 8.0) - 0.5) * 0.06
                                + (geeClassNoise(geeClassQ, 0.25) - 0.5) * 0.08 * fine);
                            roughness = mix(roughness, 0.7, geeClassW.w * 0.6);
                            worldNormal = normalize(mix(worldNormal, sphereWorldNormal, geeClassW.w * 0.2));
                        }
                        blendedDiffuse *= max(geeClassTone, 0.2);
                        worldNormal = normalize(worldNormal + geeClassTilt);
                    }
                }
            }

        """;
}
