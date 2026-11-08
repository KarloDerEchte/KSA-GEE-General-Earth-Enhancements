using System.Runtime.InteropServices;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;

namespace GEE;

/// <summary>
/// One building box for the window pass: the box axes and base point in ego axes (row vectors, as the
/// static object's model matrix), with the per-building values in the w components.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FacadeInstance
{
    /// <summary>xyz: box up axis (wall height), w: building seed (24 bits).</summary>
    public float4 Up;
    /// <summary>xyz: AxisA, w: height of the ground above the box base.</summary>
    public float4 AxisA;
    /// <summary>xyz: AxisB, w: window style (Buildings.Style*).</summary>
    public float4 AxisB;
    /// <summary>xyz: base point relative to the camera, w: brightness of lit windows (0 by day).</summary>
    public float4 Base;
}

/// <summary>
/// Windows on the building walls, drawn over the game's own building boxes in the same pass: the four walls
/// again, a few centimetres outwards, with a procedural window grid per wall (floors and bays fitted to the
/// wall's size). The blend darkens the facade where the glass is and adds the light of lit windows:
/// colour = window light + facade * (1 - glass share). Far away, where a window is smaller than a pixel, the
/// grid gives way to its average so it does not shimmer.
/// </summary>
internal sealed unsafe class FacadeTechnique() : InstancedPass<FacadeInstance>("GEEFacade", VertexShader, FragmentShader, MaxBoxes, 24)
{
    public const int MaxBoxes = 200_000;

    private static readonly PtrOwner<VkPipelineColorBlendAttachmentState> WindowBlendAttachment = new(new VkPipelineColorBlendAttachmentState
    {
        BlendEnable = true,
        SrcColorBlendFactor = VkBlendFactor.One,
        DstColorBlendFactor = VkBlendFactor.SrcAlpha,
        ColorBlendOp = VkBlendOp.Add,
        SrcAlphaBlendFactor = VkBlendFactor.Zero,
        DstAlphaBlendFactor = VkBlendFactor.One,
        AlphaBlendOp = VkBlendOp.Add,
        ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
    });

    private static readonly PtrOwner<VkPipelineColorBlendStateCreateInfo> WindowBlend = new(new VkPipelineColorBlendStateCreateInfo
    {
        AttachmentCount = 1,
        Attachments = WindowBlendAttachment,
    });

    protected override VertexInput MakeVertexInput()
    {
        return new VertexInput(1, 4).AddBindingWithAttributes<FacadeInstance>(0, 0, VkVertexInputRate.Instance);
    }

    protected override void OnRebuildFrameResources()
    {
        CreatePipeline(VkPrimitiveTopology.TriangleList, VkCullModeFlags.None, VkFrontFace.CounterClockwise, VkPolygonMode.Fill,
            RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite, WindowBlend, out Pipeline);
    }

    private const string Hash = """

        uint hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
            return x;
        }

        float random(uint a, uint b, uint c)
        {
            return float(hash(a ^ hash(b ^ hash(c + 0x9e3779b9u))) >> 8) / 16777216.0;
        }

        """;

    private const string VertexShader = ShaderSource.Version + ShaderSource.CameraBlock + Hash + """

        layout(location = 0) in vec4 up;
        layout(location = 1) in vec4 axisA;
        layout(location = 2) in vec4 axisB;
        layout(location = 3) in vec4 base;

        // Position on the wall in metres: x along it, y above the ground.
        layout(location = 0) out vec2 outWall;
        // seed, wall index, style, brightness
        layout(location = 1) flat out vec4 outParams;
        // wall width, wall top above the ground
        layout(location = 2) flat out vec2 outSize;
        // floor height, bay width (fitted to the wall), floors, share of lit windows
        layout(location = 3) flat out vec4 outGrid;
        // window glass rectangle within its cell: x0, x1, y0, y1
        layout(location = 4) flat out vec4 outGlass;

        const vec2 corners[6] = vec2[](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));

        void main()
        {
            int wall = gl_VertexIndex / 6;
            vec2 q = corners[gl_VertexIndex % 6];
            // Walls 0 and 1 face along +-AxisA and run along AxisB; walls 2 and 3 the other way round.
            float side = (wall & 1) == 0 ? 1.0 : -1.0;
            vec3 normal = (wall < 2 ? axisA.xyz : axisB.xyz) * side;
            vec3 run = wall < 2 ? axisB.xyz : axisA.xyz;
            vec3 p = base.xyz + normal * 0.5 + run * ((q.x - 0.5) * side) + up.xyz * q.y;
            // A little in front of the game's wall, so the depth test passes; more far away for depth precision.
            p += normalize(normal) * (0.03 + length(p) * 2e-5);
            gl_Position = global.camera.viewProjection * vec4(p, 1.0);
            // Walls facing away from the camera are hidden behind the box: clip them away.
            if (dot(normal, base.xyz + normal * 0.5) >= 0.0)
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0);

            float width = length(run);
            float height = length(up.xyz);
            float sizeY = height - axisA.w;
            outWall = vec2(q.x * width, q.y * height - axisA.w);
            outParams = vec4(up.w, float(wall), axisB.w, base.w);
            outSize = vec2(width, sizeY);

            // Window grid of the wall's style, once per vertex instead of once per pixel.
            int style = int(axisB.w + 0.5);
            float floorHeight, bayWidth, lit;
            vec4 glass;
            if (style == 1)      { floorHeight = 3.7; bayWidth = 1.8; glass = vec4(0.06, 0.94, 0.12, 0.88); lit = 0.5; }  // office
            else if (style == 2) { floorHeight = 6.0; bayWidth = 5.0; glass = vec4(0.10, 0.90, 0.55, 0.80); lit = 0.2; }  // hall
            else if (style == 6) { floorHeight = 3.8; bayWidth = 1.5; glass = vec4(0.04, 0.96, 0.36, 0.80); lit = 0.45; } // ribbon windows
            else                 { floorHeight = 2.9; bayWidth = 3.3; glass = vec4(0.28, 0.72, 0.32, 0.78); lit = 0.32; } // housing
            lit = clamp(lit + (random(uint(up.w), 7u, 0u) - 0.5) * 0.4, 0.05, 0.9);
            // Whole bays across the wall, so corners are never cut through a window.
            bayWidth = width / max(floor(width / bayWidth), 1.0);
            outGrid = vec4(floorHeight, bayWidth, floor((sizeY - 0.6) / floorHeight), lit);
            outGlass = glass;
        }
        """;

    private const string FragmentShader = ShaderSource.Version + Hash + """
        layout(location = 0) in vec2 wallPos;
        layout(location = 1) flat in vec4 params;
        layout(location = 2) flat in vec2 size;
        // floor height, bay width (fitted to the wall), floors, share of lit windows
        layout(location = 3) flat in vec4 grid;
        // window glass rectangle within its cell: x0, x1, y0, y1
        layout(location = 4) flat in vec4 glass;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            uint seed = uint(params.x);
            uint wall = uint(params.y);
            int style = int(params.z + 0.5);
            float brightness = params.w;

            // Pre-engineered metal halls (4) and concrete utility shelters (5) of launch complexes: no window grid.
            // (Taken first: style is flat, so the window grid below is never evaluated for them.)
            if (style == 4 || style == 5)
            {
                float px = max(fwidth(wallPos.x), 1e-4);
                float py = max(fwidth(wallPos.y), 1e-4);
                float onWall = step(0.0, wallPos.y);
                float a = 1.0;
                // Corrugated cladding: vertical ribs every 0.3 m, averaged out once they get smaller than a pixel.
                float ribFade = 1.0 - smoothstep(0.08, 0.25, px);
                float rib = 0.5 + 0.5 * cos(6.2831853 * wallPos.x / 0.3);
                if (style == 4)
                    a -= 0.08 * rib * ribFade + 0.04 * (1.0 - ribFade);
                // Roll-up doors on walls of a hall that are not too long (the ends), with horizontal slats.
                float doors = style == 4 && size.x < 45.0 ? clamp(floor(size.x / 14.0), 1.0, 2.0) : 0.0;
                float dw = min(5.5, size.x * 0.3), dh = min(5.5, size.y - 1.5);
                float door = 0.0;
                for (int k = 0; k < 2; k++)
                {
                    if (float(k) >= doors)
                        break;
                    float c = size.x * (float(k) + 0.5) / doors;
                    float inX = smoothstep(c - dw * 0.5 - px, c - dw * 0.5 + px, wallPos.x) - smoothstep(c + dw * 0.5 - px, c + dw * 0.5 + px, wallPos.x);
                    float inY = 1.0 - smoothstep(dh - py, dh + py, wallPos.y);
                    door = max(door, inX * inY * onWall);
                }
                float slats = 0.5 + 0.5 * cos(6.2831853 * wallPos.y / 0.22);
                a = mix(a, 0.66 - 0.06 * slats * (1.0 - smoothstep(0.05, 0.15, py)), door);
                // A steel personnel door near one end.
                float pdx = random(seed, wall, 9u) < 0.5 ? 1.6 : size.x - 2.6;
                float pd = (smoothstep(pdx - px, pdx + px, wallPos.x) - smoothstep(pdx + 1.0 - px, pdx + 1.0 + px, wallPos.x))
                    * (1.0 - smoothstep(2.15 - py, 2.15 + py, wallPos.y)) * onWall;
                pd *= (style == 5 ? (wall == 0u ? 1.0 : 0.0) : ((wall & 1u) == 0u ? 1.0 : 0.0)) * step(4.0, size.x);
                a = mix(a, 0.42, pd);
                // High window band of a hall: 1.2 m panes every 6 m, some lit at night.
                vec3 em = vec3(0.0);
                if (style == 4 && size.y > 6.5)
                {
                    float y0 = size.y - 2.5, y1 = size.y - 1.6;
                    float c6 = fract(wallPos.x / 6.0) * 6.0;
                    float inX = smoothstep(2.4 - px, 2.4 + px, c6) - smoothstep(3.6 - px, 3.6 + px, c6);
                    float inY = smoothstep(y0 - py, y0 + py, wallPos.y) - smoothstep(y1 - py, y1 + py, wallPos.y);
                    float pane = inX * inY;
                    a = mix(a, 0.5, pane);
                    uint wid = uint(floor(wallPos.x / 6.0)) + wall * 4096u;
                    em = vec3(1.0, 0.9, 0.75) * pane * (random(seed, wid, 5u) < 0.35 ? 1.0 : 0.0);
                }
                // Shelters: streaks of weathering on the concrete.
                if (style == 5)
                    a -= 0.05 * (0.5 + 0.5 * sin(wallPos.x * 2.1 + float(seed & 255u))) * (1.0 - wallPos.y / max(size.y, 1.0));
                float footS = 1.0 - smoothstep(0.0, 0.6, wallPos.y);
                float eaveS = 1.0 - smoothstep(0.0, 0.5, size.y - wallPos.y);
                a -= 0.14 * footS + 0.10 * eaveS;
                outColor = vec4(em * brightness, clamp(a, 0.2, 1.0));
                return;
            }


            // Floor height, bay width, floors and share of lit windows come per wall from the vertex shader.
            float bayWidth = grid.y;
            float floors = grid.z;
            float lit = grid.w;
            vec2 cell = vec2(wallPos.x / bayWidth, wallPos.y / grid.x);
            vec2 fw = max(fwidth(cell), vec2(1e-4));
            float inside = wallPos.y > 0.0 && floor(cell.y) < floors && size.x >= 2.0 ? 1.0 : 0.0;

            // Foot of the wall is soiled, the top edge sits in the shadow of the eave.
            float foot = 1.0 - smoothstep(0.0, 0.9, wallPos.y);
            float eave = 1.0 - smoothstep(0.0, 0.7, size.y - wallPos.y);

            // Windows smaller than about a pixel: the grid's average instead (no hashes, no grid maths).
            float far = smoothstep(0.35, 0.9, max(fw.x, fw.y));
            float meanCover = (glass.y - glass.x) * (glass.w - glass.z) * inside;
            float meanAlpha = 1.0 - (1.0 - 0.55) * meanCover - 0.04 - 0.16 * foot - 0.10 * eave;
            if (far >= 1.0)
            {
                outColor = vec4(vec3(1.0, 0.8, 0.55) * (lit * meanCover * brightness), clamp(meanAlpha, 0.2, 1.0));
                return;
            }
            // Outside the window area (below ground, above the last floor): only the soiling and the eave.
            if (inside == 0.0)
            {
                outColor = vec4(0.0, 0.0, 0.0, clamp(mix(1.0 - 0.16 * foot - 0.10 * eave, meanAlpha, far), 0.2, 1.0));
                return;
            }

            vec2 id = floor(cell);
            vec2 f = fract(cell);
            // Frame: the glass rectangle grown by a few centimetres; the glass itself sits inside it.
            vec2 frame = vec2(0.07, 0.06);
            vec4 outer = vec4(glass.x - frame.x * 0.5, glass.y + frame.x * 0.5, glass.z - frame.y * 0.5, glass.w + frame.y * 0.5);
            float cx = smoothstep(glass.x - fw.x, glass.x + fw.x, f.x) - smoothstep(glass.y - fw.x, glass.y + fw.x, f.x);
            float cy = smoothstep(glass.z - fw.y, glass.z + fw.y, f.y) - smoothstep(glass.w - fw.y, glass.w + fw.y, f.y);
            float cover = cx * cy;
            float ox = smoothstep(outer.x - fw.x, outer.x + fw.x, f.x) - smoothstep(outer.y - fw.x, outer.y + fw.x, f.x);
            float oy = smoothstep(outer.z - fw.y, outer.z + fw.y, f.y) - smoothstep(outer.w - fw.y, outer.w + fw.y, f.y);
            float frameCover = max(ox * oy - cover, 0.0);

            uint cellId = uint(id.x) + uint(max(id.y, 0.0)) * 1024u + wall * 1048576u;
            // Light of lit windows: only at night (brightness 0 by day).
            vec3 emission = vec3(0.0);
            if (brightness > 0.0)
            {
                float on = random(seed, cellId, 1u) < lit ? 1.0 : 0.0;
                float tone = random(seed, cellId, 2u);
                vec3 light = tone < 0.6 ? vec3(1.0, 0.72, 0.42) : tone < 0.9 ? vec3(1.0, 0.88, 0.72) : vec3(0.75, 0.85, 1.0);
                light *= 0.6 + 0.8 * random(seed, cellId, 3u);
                emission = light * on * cover;
            }

            // Glass darkness by day: some panes are curtained or reflect the bright sky.
            float pane = random(seed, cellId, 4u);
            float glassAlpha = style == 1 ? mix(0.30, 0.55, pane) : mix(0.35, 0.80, pane * pane);

            // Slab edge between floors (a shadow line) and, on the ground floor, a taller shop front.
            float slab = 1.0 - smoothstep(0.05, 0.05 + fw.y * 2.0, f.y);
            float groundShop = id.y < 0.5 && style != 0 ? 1.0 : 0.0;
            float alpha = 1.0 - (1.0 - glassAlpha) * cover - 0.22 * frameCover - 0.14 * slab;
            alpha -= 0.12 * groundShop * cover;
            alpha -= 0.16 * foot + 0.10 * eave;

            emission = mix(emission, vec3(1.0, 0.8, 0.55) * lit * meanCover, far);
            alpha = mix(alpha, meanAlpha, far);

            outColor = vec4(emission * brightness, clamp(alpha, 0.2, 1.0));
        }
        """;
}

/// <summary>Collects the building walls near the camera each frame and draws their windows.</summary>
internal static class FacadeRenderer
{
    private static FacadeTechnique? _technique;
    private static FacadeInstance[] _instances = new FacadeInstance[4096];
    private static int _count;
    public static int LastBoxes;

    /// <summary>Called by BuildingRenderer before it hands out the frame's boxes.</summary>
    public static void Begin() => _count = 0;

    public static void Add(in BuildingInstance b, double3 cameraCcf, double3 rx, double3 ry, double3 rz, float brightness)
    {
        if (Reserve(1, out FacadeInstance[] array) is var at and >= 0)
            array[at] = Make(in b, cameraCcf, rx, ry, rz, brightness);
    }

    /// <summary>
    /// Room for n walls (at most Free), filled by the caller (from several threads if it likes) with Make; returns
    /// the first slot, or -1 when there is none.
    /// </summary>
    public static int Reserve(int n, out FacadeInstance[] array)
    {
        n = Math.Min(n, FacadeTechnique.MaxBoxes - _count);
        array = _instances;
        if (n <= 0)
            return -1;
        if (_count + n > _instances.Length)
            Array.Resize(ref _instances, Math.Min(Math.Max(_instances.Length * 2, _count + n), FacadeTechnique.MaxBoxes));
        array = _instances;
        int at = _count;
        _count += n;
        return at;
    }

    public static int Free => FacadeTechnique.MaxBoxes - _count;

    public static FacadeInstance Make(in BuildingInstance b, double3 cameraCcf, double3 rx, double3 ry, double3 rz, float brightness)
    {
        static float4 Row(double3 v, float w) => new((float)v.X, (float)v.Y, (float)v.Z, w);
        return new FacadeInstance
        {
            Up = Row(Placement.ToEgo(b.Up, rx, ry, rz), b.Seed & 0xFFFFFF),
            AxisA = Row(Placement.ToEgo(b.AxisA, rx, ry, rz), b.GroundOffset),
            AxisB = Row(Placement.ToEgo(b.AxisB, rx, ry, rz), b.Style),
            Base = Row(Placement.ToEgo(b.Base - cameraCcf, rx, ry, rz), brightness),
        };
    }

    public static void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        if (viewport != Program.MainViewport)
            return;
        // Used once: a frame that collects nothing must not draw last frame's walls.
        int count = _count;
        _count = 0;
        LastBoxes = count;
        if (count == 0)
            return;
        _technique ??= new FacadeTechnique();
        _technique.Draw(commandBuffer, viewport, frameIndex, _instances.AsSpan(0, count));
    }

    public static void RebuildFrameResources() => _technique?.RebuildFrameResources();
}
