using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;

namespace GEE;

/// <summary>
/// One glow sprite: camera-relative position (ego axes) and radius, colour and largest on-screen radius as four
/// halves. Above maxPixels the sprite fades out (0 never fades).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GlowInstance
{
    public float4 PositionRadius;
    public uint2 Color;

    public static GlowInstance Make(float3 position, float radius, float r, float g, float b, float maxPixels)
    {
        static uint Halves(float lo, float hi) => BitConverter.HalfToUInt16Bits((Half)lo) | (uint)BitConverter.HalfToUInt16Bits((Half)hi) << 16;
        return new GlowInstance
        {
            PositionRadius = new float4(position.X, position.Y, position.Z, radius),
            Color = new uint2(Halves(r, g), Halves(b, maxPixels)),
        };
    }
}

/// <summary>
/// Draws the lights as additive camera-facing sprites in the main opaque pass, after the planet, so terrain and
/// the planet hide them through the depth test while nothing is written to depth. Built like the game's star
/// sprites (InstancedStarTechnique), but with world-sized glows that keep a minimum size on screen: a light
/// smaller than that is spread over the minimum size at the same total brightness, so a city seen from orbit
/// sums up to a lit patch instead of flickering sub-pixel dots.
/// </summary>
internal sealed class GlowTechnique() : InstancedPass<GlowInstance>("GEEGlow", VertexShader, FragmentShader, MaxSprites, 4)
{
    public const int MaxSprites = 400_000;

    protected override VertexInput MakeVertexInput()
    {
        return new VertexInput(1, 2).AddBindingWithAttributes<GlowInstance>(0, 0, VkVertexInputRate.Instance);
    }

    protected override void OnRebuildFrameResources()
    {
        CreatePipeline(VkPrimitiveTopology.TriangleStrip, VkCullModeFlags.None, VkFrontFace.Clockwise, VkPolygonMode.Fill,
            RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite, RenderingPresets.BlendState.BlendAdditive, out Pipeline);
    }

    private const string VertexShader = ShaderSource.Version + ShaderSource.CameraBlock + """

        layout(location = 0) in vec4 positionRadius;
        layout(location = 1) in uvec2 packedColor;

        layout(location = 0) out vec3 outColor;
        layout(location = 1) out vec2 outUv;

        // Smallest sprite radius on screen, in pixels; smaller glows are spread over it at equal total brightness.
        const float MIN_RADIUS_PX = 1.5;

        const vec2 corners[4] = vec2[](vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0), vec2(1.0, 1.0));

        void main()
        {
            vec2 rg = unpackHalf2x16(packedColor.x);
            vec2 bm = unpackHalf2x16(packedColor.y);
            vec3 color = vec3(rg, bm.x);
            float maxRadiusPx = bm.y;

            // The camera sits at the origin. Pulling the sprite towards it keeps the glow in front of its own
            // building and of coarse far-away terrain. Seen at a grazing angle, the lower half of a sprite covers
            // ground a few percent closer than the light, so the pull is a fair share of the distance; the depth
            // test still hides lights behind hills (the planet itself is culled on the CPU).
            vec3 p = positionRadius.xyz;
            float radius = positionRadius.w;
            float dist = length(p);
            float pull = min(max(radius * 2.0, dist * 0.15), dist * 0.5);
            p *= (dist - pull) / max(dist, 1e-3);

            vec4 clip = global.camera.viewProjection * vec4(p, 1.0);
            float w = max(clip.w, 1e-3);
            float radiusPx = radius * abs(global.camera.projection[1][1]) * 0.5 * float(global.camera.screenHeight) / w;
            // Glows that stand in for whole buildings or districts only work as far-away points; closer up the
            // geometry and point lights take over, instead of the glow growing into a large blob.
            if (maxRadiusPx > 0.0)
                color *= 1.0 - smoothstep(maxRadiusPx, maxRadiusPx * 2.5, radiusPx);
            if (radiusPx < MIN_RADIUS_PX)
            {
                float k = radiusPx / MIN_RADIUS_PX;
                color *= k * k;
                radiusPx = MIN_RADIUS_PX;
            }

            vec2 corner = corners[gl_VertexIndex];
            outUv = corner;
            outColor = color;
            clip.xy += corner * radiusPx * 2.0 / vec2(global.camera.screenWidth, global.camera.screenHeight) * clip.w;
            // Lights too faint to matter are dropped outside the clip volume.
            gl_Position = max(color.r, max(color.g, color.b)) < 1e-5 ? vec4(2.0, 2.0, 2.0, 1.0) : clip;
        }
        """;

    private const string FragmentShader = """
        #version 450

        layout(location = 0) in vec3 inColor;
        layout(location = 1) in vec2 inUv;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            float r2 = dot(inUv, inUv);
            if (r2 >= 1.0)
                discard;
            float k = 1.0 - r2;
            outColor = vec4(inColor * (k * k), 0.0);
        }
        """;
}
