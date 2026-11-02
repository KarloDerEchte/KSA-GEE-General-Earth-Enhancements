using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using RenderCore;
using RenderCore.Systems;

namespace GEE;

/// <summary>
/// Colours the game's own terrain with the Sentinel-2 imagery, at every height. The terrain shader (Planet.frag) is
/// patched as the game compiles it, so it reads two textures of ours through the game's bindless texture array, at
/// handles reserved then: the satellite tile array (SatelliteImagery's layers) and a small table that names, for a
/// window of 16 x 16 Web Mercator tiles around the camera on each zoom from 14 down to 5, the layer and the part of
/// it that shows the tile (the tile itself or the matching part of its nearest loaded ancestor). The shader does two
/// fixed lookups and no loops (the driver took minutes to compile a version that walked the zooms): the photo at
/// the zoom the pixel needs, and 16 times coarser. The colour comes from the photo; the brightness stays the game's
/// (with its ground texture's detail) times the photo's contrast within a few hundred metres, so the game's
/// lighting still fits. The table is rebuilt in the same upload batch that replaces layers, so the GPU never reads
/// a table naming a layer that is being rewritten.
/// </summary>
internal static class GroundTint
{
    public const int Window = 16, TopZoom = 14, Zooms = 10;
    /// <summary>Header marker, so the shader ignores the table until our texture is in place.</summary>
    private const float Magic = 12345f;
    private const int Width = Window, Height = 1 + Zooms * Window;

    public static int SatelliteHandle = -1, TableHandle = -1;
    private static string _status = "off (switched on: restart the game)";
    private static bool _failed;

    /// <summary>The terrain shader carries our code and the imagery should be loaded for it.</summary>
    public static bool Active => SatelliteHandle >= 0 && !_failed && SatelliteImagery.Settings is { GroundColorFromSatellite: true };

    public static string Status => _failed || SatelliteHandle < 0 ? _status : $"{_tiles} of {Zooms * Window * Window} table cells with imagery";

    private static readonly float[] Table = new float[Width * Height * 4];
    private static readonly float[] Uploaded = new float[Width * Height * 4];
    private static bool _bound, _uploadedOnce;
    private static int _tiles;
    private static SimpleVkTexture? _texture;
    private static (long Version, int X, int Y, float Strength) _builtFor = (-1, 0, 0, 0);

    public static bool HasTexture => _texture != null;

    public static void Fail(string message)
    {
        if (SatelliteHandle < 0)
            return;
        _failed = true;
        _status = $"off: {message}";
    }

    /// <summary>
    /// When the game compiles Planet.frag: two handles of the bindless array for our textures (pointing at the
    /// library's blank texture until ours exist). False if the library is not there yet.
    /// </summary>
    public static bool Reserve()
    {
        if (SatelliteHandle >= 0)
            return true;
        try
        {
            if (Program.Instance?.BindlessTextures is not { } library)
            {
                _status = "off: the game's texture library was not ready";
                return false;
            }
            var blank = typeof(BindlessTextureLibrary).GetField("_emptyTexture", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(library) as SimpleVkTexture;
            if (blank == null)
            {
                _status = "off: the game's texture library has changed";
                return false;
            }
            SatelliteHandle = library.AddTexture(blank.VkImageView);
            TableHandle = library.AddTexture(blank.VkImageView);
            Log.Info($"ground colour: bindless handles {SatelliteHandle} (imagery) and {TableHandle} (table)");
            return true;
        }
        catch (Exception e)
        {
            _status = $"off: {e.Message}";
            Log.Error($"ground colour: {e}");
            return false;
        }
    }

    /// <summary>The camera's Web Mercator tile on a zoom.</summary>
    public static (int X, int Y) CameraTile(double camLat, double camLon, int z)
    {
        int n = 1 << z;
        double merc = Math.Asinh(Math.Tan(Math.Clamp(camLat, -1.48, 1.48)));
        int x = (int)Math.Floor((camLon + Math.PI) / (2 * Math.PI) * n);
        int y = (int)Math.Floor((Math.PI - merc) / (2 * Math.PI) * n);
        return (((x % n) + n) % n, Math.Clamp(y, 0, n - 1));
    }

    // ---- table (main thread, in SatelliteImagery's upload) ----

    /// <summary>
    /// Fills the table again if the loaded layers (version), the camera's tile or the strength changed; true if its
    /// contents then differ from what the GPU has. layer(z, x, y) gives a loaded tile's layer or -1.
    /// </summary>
    public static bool Build(double camLat, double camLon, double planetRadius, Settings settings, long version, Func<int, int, int, int> layer)
    {
        float strength = _failed ? 0 : (float)Math.Clamp(settings.GroundColorStrength, 0, 1);
        var (cx, cy) = CameraTile(camLat, camLon, TopZoom);
        var key = (version, cx, cy, strength);
        if (key == _builtFor)
            return false;
        _builtFor = key;
        var t = Table.AsSpan();
        t.Clear();
        // Strength 0 (switched down, or failed): the shader leaves the terrain as it is.
        Set(t, 0, 0, Magic, strength, (float)planetRadius, Zooms);
        int tiles = 0;
        for (int i = 0; i < Zooms; i++)
        {
            int z = TopZoom - i, n = 1 << z;
            var (tx, ty) = CameraTile(camLat, camLon, z);
            int x0 = ((tx - Window / 2) % n + n) % n;
            int y0 = Math.Clamp(ty - Window / 2, 0, Math.Max(0, n - Window));
            Set(t, 1 + i, 0, x0, y0, z, 0);
            for (int row = 0; row < Window; row++)
                for (int col = 0; col < Window; col++)
                {
                    int x = (x0 + col) % n, y = y0 + row;
                    float lay = -1, ox = 0, oy = 0, scale = 1;
                    // The tile, or the matching part of its nearest loaded ancestor.
                    for (int az = z, ax = x, ay = y; y < n && az >= 3; az--, ax >>= 1, ay >>= 1)
                    {
                        int l = layer(az, ax, ay);
                        if (l < 0)
                            continue;
                        int levels = z - az;
                        scale = 1f / (1 << levels);
                        ox = (x - (ax << levels)) * scale;
                        oy = (y - (ay << levels)) * scale;
                        lay = l;
                        break;
                    }
                    if (lay >= 0)
                        tiles++;
                    Set(t, col, 1 + i * Window + row, lay, ox, oy, scale);
                }
        }
        _tiles = tiles;
        return !t.SequenceEqual(Uploaded);
    }

    private static void Set(Span<float> t, int x, int y, float a, float b, float c, float d)
    {
        int i = (y * Width + x) * 4;
        t[i] = a;
        t[i + 1] = b;
        t[i + 2] = c;
        t[i + 3] = d;
    }

    // ---- GPU (inside SatelliteImagery's upload batches) ----

    /// <summary>With the imagery's array, in its first command buffer (layout transition to shader read).</summary>
    public static void CreateTexture(Renderer renderer, CommandBuffer cb)
    {
        _texture = new SimpleVkTexture("GEE ground colour table", renderer.Allocator, Width, Height, 1, VkFormat.R32G32B32A32SFloat, 1);
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
        if (_texture == null)
            return;
        BufferEx buffer = staging.AddStagingBuffer<byte>(MemoryMarshal.AsBytes(Table.AsSpan()));
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        // Frames already submitted finish reading the table before it changes (same queue).
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
            ImageExtent = new VkExtent3D { Width = Width, Height = Height, Depth = 1 },
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
        Table.CopyTo(Uploaded, 0);
        _uploadedOnce = true;
    }

    /// <summary>Points our handles at the imagery and the table, once the table's first upload is submitted.</summary>
    public static void Bind(SimpleVkTexture imagery)
    {
        if (_bound || !_uploadedOnce || _texture == null || Program.Instance?.BindlessTextures is not { } library)
            return;
        // The imagery first: the table's marker is what lets the shader sample it.
        library.SetTexture(SatelliteHandle, imagery.VkImageView);
        library.SetTexture(TableHandle, _texture.VkImageView);
        _bound = true;
        Log.Info("ground colour: imagery bound to the terrain shader");
    }

    // ---- Planet.frag patch ----

    private const string IncludeAnchor = "#include \"../Common/RingShadows.glsl\"\n";
    private const string SurfaceAnchor = "    vec3 surfaceColor = blendedDiffuse.xyz;\n";

    /// <summary>The shader text with our code, or null if the game's shader has changed.</summary>
    public static string? PatchPlanet(string source)
    {
        foreach (string anchor in new[] { IncludeAnchor, SurfaceAnchor })
            if (source.Split(anchor).Length != 2)
                return null;
        string functions = Functions.Replace("GEE_SAT_HANDLE", SatelliteHandle.ToString()).Replace("GEE_TABLE_HANDLE", TableHandle.ToString());
        return source
            .Replace(IncludeAnchor, IncludeAnchor + functions)
            .Replace(SurfaceAnchor, SurfaceCode + SurfaceAnchor);
    }

    private const string Functions = """

        // ---- GEE: satellite colour (mods/GEE GroundTint.cs) ----
        const int GEE_SAT = GEE_SAT_HANDLE, GEE_TABLE = GEE_TABLE_HANDLE;

        vec4 geeTexel(int x, int y)
        {
            return texelFetch(sampler2D(globalTextures[GEE_TABLE], pointSampler), ivec2(x, y), 0);
        }

        // Linear colour of the photo on zoom z (14 down to 5) at Web Mercator position unit (0..1, y southward);
        // a = 0 where the table has none.
        vec4 geeSatellite(vec2 unit, int z)
        {
            int i = 14 - z;
            vec4 zoom = geeTexel(1 + i, 0);
            float n = exp2(zoom.z);
            vec2 rel = unit * n - zoom.xy;
            rel.x = mod(rel.x, n);
            ivec2 cell = clamp(ivec2(floor(rel)), ivec2(0), ivec2(15));
            vec4 e = geeTexel(cell.x, 1 + 16 * i + cell.y);
            vec2 uv = e.yz + clamp(rel - vec2(cell), 0.0, 1.0) * e.w;
            vec3 rgb = textureLod(sampler2DArray(globalTextureArrays[GEE_SAT], GLOBAL_CLAMP_SAMPLER), vec3(uv, max(e.x, 0.0)), 0.0).rgb;
            // The imagery is graded cool (vegetation comes out blue-green); warmed and a little desaturated it
            // matches the game's grass.
            vec3 linear = pow(rgb, vec3(2.2)) * vec3(1.25, 1.0, 0.6);
            linear = mix(linear, vec3(dot(linear, vec3(0.2126, 0.7152, 0.0722))), 0.15);
            return vec4(linear, e.x >= 0.0 ? 1.0 : 0.0);
        }
        // ---- end GEE ----

        """;

    private const string SurfaceCode = """
            // GEE: colour from the satellite photo; brightness stays the game's (with its ground texture's detail)
            // times the photo's contrast against a tile 16 times coarser.
            {
                vec4 geeHead = geeTexel(0, 0);
                if (geeHead.x == 12345.0 && geeHead.y > 0.0 && abs(uboPlanet.radius - geeHead.z) < 1000.0)
                {
                    vec3 geeDir = normalize(mat3(uboPlanet.cce2CcfMatrix) * sphereVector);
                    float geeLat = asin(clamp(geeDir.z, -1.0, 1.0));
                    vec2 geeUnit = vec2((atan(geeDir.y, geeDir.x) + 3.14159265) / 6.28318531,
                        (3.14159265 - log(tan(0.78539816 + 0.5 * clamp(geeLat, -1.48, 1.48)))) / 6.28318531);
                    // The zoom whose texels are about a pixel, but no finer than the window around the camera reaches.
                    float geeFootprint = max(length(dFdx(inWorldPosition.xyz)), length(dFdy(inWorldPosition.xyz)));
                    float geeTexelZ0 = 6.28318531 * uboPlanet.radius * max(cos(geeLat), 0.01) / 256.0;
                    float geeWant = floor(log2(geeTexelZ0 / max(geeFootprint, 0.01)));
                    vec4 geeTop = geeTexel(1, 0);
                    vec2 geeAway = abs(vec2(mod(geeUnit.x * 16384.0 - geeTop.x - 8.0 + 8192.0, 16384.0) - 8192.0,
                        geeUnit.y * 16384.0 - geeTop.y - 8.0));
                    float geeReach = 14.0 - max(ceil(log2(max(max(geeAway.x, geeAway.y), 1.0) / 7.0)), 0.0);
                    int geeZoom = int(clamp(min(geeWant, geeReach), 5.0, 14.0));
                    vec4 geeFine = geeSatellite(geeUnit, geeZoom);
                    if (geeFine.a > 0.0)
                    {
                        vec4 geeCoarse = geeSatellite(geeUnit, max(geeZoom - 4, 5));
                        const vec3 geeLum = vec3(0.2126, 0.7152, 0.0722);
                        // Hue (luminance 1) and brightness apart: clamping the colour per channel turned bright
                        // spots (concrete, haze left in the imagery) grey-white with rainbow fringes.
                        float geeFineLum = max(dot(geeFine.rgb, geeLum), 1e-4);
                        float geeRef = geeCoarse.a > 0.0 ? max(dot(geeCoarse.rgb, geeLum), 1e-4) : geeFineLum;
                        vec3 geeHue = geeFine.rgb / geeFineLum;
                        geeHue /= max(1.0, max(geeHue.r, max(geeHue.g, geeHue.b)) / 2.5);
                        // Brighter than the surroundings by at most about 2.5 times, approached softly.
                        float geeRatio = geeFineLum / geeRef;
                        geeRatio = geeRatio < 1.0 ? max(geeRatio, 0.3) : 1.0 + 1.5 * (1.0 - exp(-(geeRatio - 1.0) / 1.5));
                        blendedDiffuse = mix(blendedDiffuse, geeHue * geeRatio * dot(blendedDiffuse, geeLum), geeHead.y);
                    }
                }
            }

        """;
}
