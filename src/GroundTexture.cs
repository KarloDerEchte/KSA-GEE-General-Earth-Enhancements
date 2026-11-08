using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using RenderCore;
using RenderCore.Systems;

namespace GEE;

/// <summary>
/// Small photographed ground textures inside the terrain shader (Planet.frag), not geometry over the terrain: a
/// 512 x 512 array of three CC0 ambientCG materials (grass, dirt, rock; Textures/GEE_GroundDetail.ktx2, built by
/// tools/ground_textures.py) with a full mip chain, loaded here (the KTX2 is read by hand: RGBA8, zlib levels) and
/// bound to a bindless handle reserved when the game compiles Planet.frag, like GroundTint's. Per layer: R = albedo
/// luminance relative to the mean (/ 3), G/B = tangent normal xy, A = roughness deviation.
///
/// In the shader (code prefixed geeTex, added last in GroundTint.PatchPlanet's chain, so after GroundDetail's slope
/// materials and GroundClasses): within GroundTextureRangeM of the camera the satellite-coloured ground is multiplied
/// by the texture's luminance ratio (the satellite hue and brightness stay, only the detail comes from the photo; darker
/// texels are a little more saturated), the shading normal is tilted by the texture's normal and the roughness
/// deviates a little. The texture is sampled with stochastic non-repeating tiling (triangle lattice, one random
/// offset per vertex, variance-preserving blend) at about GroundTextureScaleM (default 1 m), plus two plain taps at
/// about 8 m and 2.7 m for variation. The material comes from the slope with GroundDetail's thresholds (rock on steep
/// ground, dirt on moderately steep or non-green ground, grass otherwise) and is picked per lattice cell, so
/// transitions are a mosaic; all layers have the same mean, so only the pattern changes. Six texture fetches in all
/// (3 + 2 + the GroundTint header that says the imagery is bound), no loops.
/// The pattern is anchored to the planet: it uses the game's own double-precision UV offsets of the first terrain
/// material (UVOffsetsBuffer, fract(camera * tiling)), times an integer, so tiles are k times smaller and wrap
/// together; lattice vertex hashes wrap with the same integer period, so nothing jumps when the offset wraps.
/// </summary>
internal static class GroundTexture
{
    public const string FileName = "GroundDetail.ktx2";
    public static int Handle = -1;
    private static bool _failed, _bound;
    private static string _status = "off (switched on: restart the game)";
    private static SimpleVkTexture? _texture;
    private static Ktx? _ktx;

    public static bool Active => Handle >= 0 && !_failed && GroundTint.Active && SatelliteImagery.Settings is { GroundTexture: true };
    public static string Status => _failed || Handle < 0 ? _status : _bound ? "bound to the terrain shader" : "loading";

    private sealed record Ktx(int Width, int Height, int Layers, byte[][] Levels);

    private static string PathOnDisk => System.IO.Path.Combine(SatelliteImagery.ModDir, "Textures", "GEE_" + FileName);

    public static void Fail(string message)
    {
        if (Handle < 0)
            return;
        _failed = true;
        _status = $"off: {message}";
    }

    /// <summary>When the game compiles Planet.frag: a handle of the bindless array (the library's blank texture until ours is bound).</summary>
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
            Log.Info($"ground texture: bindless handle {Handle}");
            return true;
        }
        catch (Exception e)
        {
            _status = $"off: {e.Message}";
            Log.Error($"ground texture: {e}");
            return false;
        }
    }

    // ---- KTX2 ----

    /// <summary>Reads an RGBA8 UNORM KTX2 array (no supercompression or zlib); levels are all layers of a mip, layer after layer.</summary>
    private static Ktx ReadKtx(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        ReadOnlySpan<byte> magic = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.Length < 80 || !b.AsSpan(0, 12).SequenceEqual(magic))
            throw new InvalidDataException("not a KTX2 file");
        uint format = BitConverter.ToUInt32(b, 12), w = BitConverter.ToUInt32(b, 20), h = BitConverter.ToUInt32(b, 24),
            layers = BitConverter.ToUInt32(b, 32), faces = BitConverter.ToUInt32(b, 36), levels = BitConverter.ToUInt32(b, 40),
            scheme = BitConverter.ToUInt32(b, 44);
        if (format != 37 || faces != 1 || layers == 0 || levels == 0 || scheme > 3 || scheme == 1 || scheme == 2)
            throw new InvalidDataException($"unsupported KTX2 (format {format}, faces {faces}, layers {layers}, scheme {scheme})");
        var data = new byte[levels][];
        for (int i = 0; i < levels; i++)
        {
            long offset = (long)BitConverter.ToUInt64(b, 80 + 24 * i), length = (long)BitConverter.ToUInt64(b, 88 + 24 * i),
                raw = (long)BitConverter.ToUInt64(b, 96 + 24 * i);
            long lw = Math.Max(w >> i, 1), lh = Math.Max(h >> i, 1);
            if (raw != lw * lh * 4 * layers || offset + length > b.Length)
                throw new InvalidDataException($"KTX2 level {i} has an unexpected size");
            if (scheme == 3)
            {
                data[i] = new byte[raw];
                using var z = new ZLibStream(new MemoryStream(b, (int)offset, (int)length), CompressionMode.Decompress);
                z.ReadExactly(data[i]);
            }
            else
                data[i] = b.AsSpan((int)offset, (int)length).ToArray();
        }
        return new Ktx((int)w, (int)h, (int)layers, data);
    }

    // ---- GPU (in SatelliteImagery's first command buffer) ----

    /// <summary>Creates the array and records its upload; the staging pool is submitted and waited for by the caller.</summary>
    public static void CreateTexture(Renderer renderer, CommandBuffer cb, StagingPool staging)
    {
        try
        {
            _ktx = ReadKtx(PathOnDisk);
            var k = _ktx;
            _texture = new SimpleVkTexture("GEE ground texture", renderer.Allocator, k.Width, k.Height, 1, VkFormat.R8G8B8A8UNorm, k.Levels.Length, k.Layers);
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = k.Levels.Length, LayerCount = k.Layers };
            cb.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default,
                new ReadOnlySpan<VkImageMemoryBarrier>(new VkImageMemoryBarrier
                {
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = VkImageLayout.Undefined,
                    NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = _texture.VkImage,
                    SubresourceRange = range,
                }));
            for (int i = 0; i < k.Levels.Length; i++)
            {
                BufferEx buffer = staging.AddStagingBuffer<byte>(k.Levels[i].AsSpan());
                cb.CopyBufferToImage(buffer.VkBuffer, _texture.VkImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = i, BaseArrayLayer = 0, LayerCount = k.Layers },
                    ImageExtent = new VkExtent3D { Width = Math.Max(k.Width >> i, 1), Height = Math.Max(k.Height >> i, 1), Depth = 1 },
                }));
            }
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
            _ktx = null;
        }
        catch (Exception e)
        {
            _texture = null;
            Fail(e.Message);
            Log.Error($"ground texture: {e}");
        }
    }

    /// <summary>Points the handle at the array once its upload is submitted (before GroundTint's header marker enables the shader).</summary>
    public static void Bind()
    {
        if (_bound || _texture == null || Handle < 0 || Program.Instance?.BindlessTextures is not { } library)
            return;
        library.SetTexture(Handle, _texture.VkImageView);
        _bound = true;
        Log.Info("ground texture: bound to the terrain shader");
    }

    // ---- Planet.frag patch ----

    private const string IncludeAnchor = "#include \"../Common/RingShadows.glsl\"\n";
    private const string SurfaceAnchor = "    vec3 surfaceColor = blendedDiffuse.xyz;\n";

    /// <summary>The shader text with the texture code in front of the lighting, or null if off, the file is missing or the shader has changed.</summary>
    public static string? Patch(string source)
    {
        if (SatelliteImagery.Settings is not { GroundTexture: true } s || source.Split(IncludeAnchor).Length != 2 || source.Split(SurfaceAnchor).Length != 2)
            return null;
        if (!File.Exists(PathOnDisk))
        {
            _status = $"off: {PathOnDisk} is missing (run src/tools/ground_textures.py)";
            return null;
        }
        if (!Reserve())
            return null;
        string F(double v, double lo, double hi) => Math.Clamp(v, lo, hi).ToString("0.0###", CultureInfo.InvariantCulture);
        string Apply(string text) => text
            .Replace("GEE_TEX_HANDLE", Handle.ToString(CultureInfo.InvariantCulture))
            .Replace("GEE_TEX_ALBEDO", F(s.GroundTextureStrength, 0, 2))
            .Replace("GEE_TEX_NORMAL", F(s.GroundTextureNormalStrength, 0, 2))
            .Replace("GEE_TEX_NEAR_M", F(s.GroundTextureScaleM, 0.25, 4))
            .Replace("GEE_TEX_FAR_M", F(8.0, 4, 16))
            .Replace("GEE_TEX_RANGE", F(s.GroundTextureRangeM, 30, 400));
        return source
            .Replace(IncludeAnchor, IncludeAnchor + Apply(Functions))
            .Replace(SurfaceAnchor, Apply(SurfaceCode) + SurfaceAnchor);
    }

    private const string Functions = """

        // ---- GEE: ground texture (mods/GEE GroundTexture.cs) ----
        const int geeTexHandle = GEE_TEX_HANDLE;

        vec2 geeTexHash2(vec2 p)
        {
            vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
            q += dot(q, q.yzx + 33.33);
            return fract((q.xx + q.yz) * q.zy);
        }

        // Triangle lattice (each unit square cut along a diagonal): three vertices and barycentric weights. The lattice
        // is square, so an integer shift of uv shifts the vertex ids by whole numbers.
        void geeTexLattice(vec2 uv, out vec2 v0, out vec2 v1, out vec2 v2, out vec3 w)
        {
            vec2 id = floor(uv);
            vec2 f = uv - id;
            if (f.x + f.y < 1.0)
            {
                w = vec3(1.0 - f.x - f.y, f.x, f.y);
                v0 = id; v1 = id + vec2(1.0, 0.0); v2 = id + vec2(0.0, 1.0);
            }
            else
            {
                w = vec3(f.x + f.y - 1.0, 1.0 - f.x, 1.0 - f.y);
                v0 = id + vec2(1.0, 1.0); v1 = id + vec2(0.0, 1.0); v2 = id + vec2(1.0, 0.0);
            }
        }

        // One fetch of the detail array: layer, tile coordinate (plus the vertex's random offset).
        vec4 geeTexFetch(vec2 uv, vec2 ddx, vec2 ddy, float layer)
        {
            return textureGrad(sampler2DArray(globalTextureArrays[geeTexHandle], linearRepeatSampler), vec3(uv, layer), ddx, ddy);
        }
        // ---- end GEE ground texture ----

        """;

    private const string SurfaceCode = """
            // GEE: ground texture (mods/GEE GroundTexture.cs): photographed grass / dirt / rock detail without tiling.
            if (distanceToTerrain < GEE_TEX_RANGE)
            {
                vec4 geeTexHead = geeTexel(0, 0);               // GroundTint's header: imagery bound, strength, planet
                float geeTexT0 = biomeMaterialBuffer.biomeMaterials[0].textureTiling;
                float geeTexFade = (1.0 - smoothstep(0.4 * GEE_TEX_RANGE, GEE_TEX_RANGE, distanceToTerrain)) * detailStrength;
                if (geeTexHead.x == 12345.0 && geeTexHead.y > 0.0 && abs(uboPlanet.radius - geeTexHead.z) < 1000.0 && geeTexT0 > 1e-4 && geeTexFade > 0.01)
                {
                    const vec3 geeTexL = vec3(0.2126, 0.7152, 0.0722);
                    float geeTexLum = dot(blendedDiffuse, geeTexL);
                    // Snow, sand and other bright ground keep their own look.
                    float geeTexK = geeTexFade * (1.0 - 0.7 * smoothstep(0.5, 0.8, geeTexLum * max(uboPlanet.meanDiffuseLuminosity, 1e-3)));

                    // The terrain's anchored coordinate: the game's own camera-position offset of material 0 (fract(camera * tiling),
                    // double precision), so tile k times smaller wraps with it. Projected along the surface's major axis.
                    ivec3 geeTexAx = biplanarInfo.majorAxis;
                    vec3 geeTexA = texturePos * geeTexT0 + UVOffsetsBuffer.biomeMaterialOffsets[0][1].xyz;
                    vec3 geeTexDx = biplanarInfo.ddxWorld * geeTexT0, geeTexDy = biplanarInfo.ddyWorld * geeTexT0;
                    float geeTexKn = max(1.0, floor(1.0 / (geeTexT0 * GEE_TEX_NEAR_M) + 0.5));
                    float geeTexKf = max(1.0, floor(1.0 / (geeTexT0 * GEE_TEX_FAR_M) + 0.5));
                    vec2 geeTexUv = vec2(geeTexA[geeTexAx.y], geeTexA[geeTexAx.z]);
                    vec2 geeTexDdx = vec2(geeTexDx[geeTexAx.y], geeTexDx[geeTexAx.z]);
                    vec2 geeTexDdy = vec2(geeTexDy[geeTexAx.y], geeTexDy[geeTexAx.z]);

                    // Near scale: lattice of 2 cells per tile (period in cells = 2 * kn), one random offset per vertex.
                    float geeTexNp = 2.0 * geeTexKn;
                    vec2 geeTexV0, geeTexV1, geeTexV2;
                    vec3 geeTexW;
                    geeTexLattice(geeTexUv * geeTexKn * 2.0, geeTexV0, geeTexV1, geeTexV2, geeTexW);
                    vec2 geeTexUn = geeTexUv * geeTexKn, geeTexDxn = geeTexDdx * geeTexKn, geeTexDyn = geeTexDdy * geeTexKn;
                    vec2 geeTexH0 = geeTexHash2(mod(geeTexV0, geeTexNp)), geeTexH1 = geeTexHash2(mod(geeTexV1, geeTexNp)), geeTexH2 = geeTexHash2(mod(geeTexV2, geeTexNp));

                    // Material per lattice cell (its strongest vertex): rock on steep ground, dirt on moderately steep or
                    // non-green ground (GroundDetail's slope thresholds), grass otherwise.
                    float geeTexSlope = dot(worldNormal, sphereWorldNormal);
                    float geeTexGreen = (blendedDiffuse.g - 0.5 * (blendedDiffuse.r + blendedDiffuse.b)) / max(geeTexLum, 1e-3);
                    float geeTexRock = 1.0 - smoothstep(0.62, 0.82, geeTexSlope);
                    float geeTexDirt = max(1.0 - smoothstep(0.80, 0.94, geeTexSlope), 0.85 * (1.0 - smoothstep(0.02, 0.15, geeTexGreen)));
                    vec2 geeTexTop = geeTexW.x >= geeTexW.y && geeTexW.x >= geeTexW.z ? geeTexV0 : (geeTexW.y >= geeTexW.z ? geeTexV1 : geeTexV2);
                    float geeTexPick = geeTexHash2(mod(geeTexTop, geeTexNp) + 17.0).x;
                    float geeTexLayer = geeTexPick < geeTexRock ? 2.0 : (geeTexPick < geeTexDirt ? 1.0 : 0.0);

                    vec4 geeTexT[3];
                    geeTexT[0] = geeTexFetch(geeTexUn + geeTexH0, geeTexDxn, geeTexDyn, geeTexLayer);
                    geeTexT[1] = geeTexFetch(geeTexUn + geeTexH1, geeTexDxn, geeTexDyn, geeTexLayer);
                    geeTexT[2] = geeTexFetch(geeTexUn + geeTexH2, geeTexDxn, geeTexDyn, geeTexLayer);
                    // Variance-preserving blend of zero-mean data (a plain mix of three random tiles would look washed out).
                    float geeTexNorm = inversesqrt(dot(geeTexW, geeTexW));
                    vec4 geeTexNear = (geeTexW.x * (geeTexT[0] - vec4(1.0 / 3.0, 0.5, 0.5, 0.5)) + geeTexW.y * (geeTexT[1] - vec4(1.0 / 3.0, 0.5, 0.5, 0.5))
                        + geeTexW.z * (geeTexT[2] - vec4(1.0 / 3.0, 0.5, 0.5, 0.5))) * geeTexNorm;
                    // Two plain taps for variation at about 8 m and a third of that (integer multiples of the same period).
                    vec2 geeTexUf = geeTexUv * geeTexKf;
                    vec4 geeTexF1 = geeTexFetch(geeTexUf, geeTexDdx * geeTexKf, geeTexDdy * geeTexKf, geeTexLayer) - vec4(1.0 / 3.0, 0.5, 0.5, 0.5);
                    vec4 geeTexF2 = geeTexFetch(geeTexUf * 3.0 + vec2(0.37, 0.71), geeTexDdx * (3.0 * geeTexKf), geeTexDdy * (3.0 * geeTexKf), geeTexLayer) - vec4(1.0 / 3.0, 0.5, 0.5, 0.5);
                    vec4 geeTexFar = (0.6 * geeTexF1 + 0.4 * geeTexF2) * 1.39;

                    // Albedo: luminance ratio (stored / 3), mostly the near scale; brighter texels are a little less saturated.
                    float geeTexDev = 3.0 * (geeTexNear.x + 0.5 * geeTexFar.x);
                    blendedDiffuse *= clamp(1.0 + GEE_TEX_ALBEDO * geeTexK * geeTexDev, 0.5, 1.8);
                    float geeTexLumNew = dot(blendedDiffuse, geeTexL);
                    blendedDiffuse = mix(vec3(geeTexLumNew), blendedDiffuse, 1.0 - 0.3 * clamp(geeTexDev, -1.0, 1.0) * geeTexK);

                    // Normal: the texture's tangent normal (image up = -v) along the two projection axes, to world space.
                    vec2 geeTexTilt = geeTexNear.yz * 2.0 + 0.7 * geeTexFar.yz * 2.0;
                    vec3 geeTexEu = vec3(0.0), geeTexEv = vec3(0.0);
                    geeTexEu[geeTexAx.y] = 1.0;
                    geeTexEv[geeTexAx.z] = 1.0;
                    vec3 geeTexTiltW = (geeTexEu * geeTexTilt.x - geeTexEv * geeTexTilt.y) * mat3(uboPlanet.cce2CcfMatrix);
                    geeTexTiltW -= worldNormal * dot(geeTexTiltW, worldNormal);
                    worldNormal = normalize(worldNormal + GEE_TEX_NORMAL * geeTexK * geeTexTiltW);
                    roughness = clamp(roughness + 2.0 * geeTexNear.w * geeTexK, 0.3, 1.0);
                }
            }

        """;
}
