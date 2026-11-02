using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;

namespace OsmBuildings;

/// <summary>One vapour puff: camera-relative position (ego axes) and radius; opacity, lightness, seed, age.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VaporInstance
{
    public float4 PositionRadius;
    public float4 Params;
}

/// <summary>
/// Soft camera-facing puffs blended over the scene (premultiplied alpha), for the vapour that boils off the
/// cryogenic tanks of a launch complex. Each puff is a round falloff broken up by animated value noise; drawn in
/// the main pass after the planet with the depth test on and no depth writes, like the glow sprites.
/// </summary>
internal sealed class VaporTechnique() : InstancedPass<VaporInstance>("OsmBuildingsVapor", VertexShader, FragmentShader, MaxPuffs, 4)
{
    public const int MaxPuffs = 20_000;

    private static readonly PtrOwner<VkPipelineColorBlendAttachmentState> PremultipliedAttachment = new(new VkPipelineColorBlendAttachmentState
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

    private static readonly PtrOwner<VkPipelineColorBlendStateCreateInfo> Premultiplied = new(new VkPipelineColorBlendStateCreateInfo
    {
        AttachmentCount = 1,
        Attachments = PremultipliedAttachment,
    });

    protected override VertexInput MakeVertexInput()
    {
        return new VertexInput(1, 2).AddBindingWithAttributes<VaporInstance>(0, 0, VkVertexInputRate.Instance);
    }

    protected override void OnRebuildFrameResources()
    {
        CreatePipeline(VkPrimitiveTopology.TriangleStrip, VkCullModeFlags.None, VkFrontFace.Clockwise, VkPolygonMode.Fill,
            RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite, Premultiplied, out Pipeline);
    }

    private const string VertexShader = ShaderSource.Version + ShaderSource.CameraBlock + """

        layout(location = 0) in vec4 positionRadius;
        layout(location = 1) in vec4 params;

        layout(location = 0) out vec2 outUv;
        layout(location = 1) flat out vec4 outParams;

        const vec2 corners[4] = vec2[](vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0), vec2(1.0, 1.0));

        void main()
        {
            vec2 corner = corners[gl_VertexIndex];
            // A quad in view space, so the puff keeps its world size and always faces the camera.
            vec4 view = global.camera.view * vec4(positionRadius.xyz, 1.0);
            view.xy += corner * positionRadius.w;
            gl_Position = global.camera.projection * view;
            outUv = corner;
            outParams = params;
        }
        """;

    private const string FragmentShader = """
        #version 450

        layout(location = 0) in vec2 uv;
        layout(location = 1) flat in vec4 params;

        layout(location = 0) out vec4 outColor;

        float hash(vec2 p)
        {
            p = fract(p * vec2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }

        float noise(vec2 p)
        {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
        }

        void main()
        {
            float r = length(uv);
            if (r >= 1.0)
                discard;
            float opacity = params.x, lightness = params.y, seed = params.z, age = params.w;
            // Billowing edge: noise at two scales, drifting with the puff's age.
            vec2 q = uv * 1.7 + vec2(seed * 13.1, seed * 7.7 - age * 0.8);
            float n = 0.6 * noise(q * 1.6) + 0.4 * noise(q * 3.7 + 11.0);
            float density = smoothstep(1.0, 0.15, r + (n - 0.5) * 0.55);
            float alpha = clamp(density * opacity, 0.0, 1.0);
            // Lit from above, a little darker underneath.
            vec3 color = lightness * mix(vec3(0.80, 0.82, 0.86), vec3(1.0), 0.5 + 0.5 * uv.y);
            outColor = vec4(color * alpha, alpha);
        }
        """;
}

/// <summary>
/// Vapour plumes over the cryogenic tanks of launch complexes (tanks flagged Vents at placement): a stream of puffs
/// from each vent that rise, drift downwind (the sea breeze, from the east) and grow and fade; and slow ground fog
/// rolling off the larger round tanks. Puffs are computed from the time alone, nothing is simulated.
/// </summary>
internal static class VaporRenderer
{
    private static VaporTechnique? _technique;
    private static readonly List<(double3 Vent, double3 Up, double Size, uint Seed, bool Ground)> Emitters = new();
    private static readonly List<(double Dist2, VaporInstance Puff)> Puffs = new();
    private static VaporInstance[] _instances = new VaporInstance[1024];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double3 _camera, _rx, _ry, _rz, _sun;
    private static bool _haveSun;
    public static int LastPuffs;

    /// <summary>Called by BuildingRenderer at the start of a frame's collection.</summary>
    public static void Begin(double3 cameraCcf, double3 rx, double3 ry, double3 rz, double3? sun)
    {
        Emitters.Clear();
        _camera = cameraCcf;
        _rx = rx;
        _ry = ry;
        _rz = rz;
        _haveSun = sun.HasValue;
        _sun = sun ?? default;
    }

    /// <summary>A venting tank near the camera (BuildingRenderer, for instances flagged Vents).</summary>
    public static void AddTank(in BuildingInstance tank)
    {
        double3 up = double3.Normalize(tank.Up);
        double length = Math.Sqrt(double3.Dot(tank.AxisA, tank.AxisA));
        uint seed = tank.Seed != 0 ? tank.Seed : LightSet.Hash((long)(tank.Base.X * 10), (long)(tank.Base.Y * 10));
        // About two thirds of the tanks are venting at any time.
        if (seed % 3 == 0)
            return;
        if (tank.Variant == Buildings.HorizontalTankVariant)
        {
            // Relief valve on top near one end.
            Emitters.Add((tank.Base + tank.Up + tank.AxisA * 0.42, up, Math.Clamp(Math.Sqrt(double3.Dot(tank.AxisB, tank.AxisB)) / 3, 0.9, 2.0), seed, false));
            return;
        }
        // Round tank: vent at the top of the dome (0.4 diameters above the shell), ground fog for the big ones.
        Emitters.Add((tank.Base + tank.Up + up * (0.4 * length), up, Math.Clamp(length / 7, 0.9, 2.6), seed, false));
        if (length >= 8)
            Emitters.Add((tank.Base + up * tank.GroundOffset, up, length / 10, seed * 7919u, true));
    }

    public static void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        if (viewport != Program.MainViewport)
            return;
        LastPuffs = 0;
        if (Emitters.Count == 0)
            return;
        double t = Clock.Elapsed.TotalSeconds;
        Puffs.Clear();
        foreach (var (vent, up, size, seed, ground) in Emitters)
        {
            Placement.Axes(up, out double3 east, out double3 north);
            double3 wind = double3.Normalize(-east * 0.85 + north * 0.25);
            double3 side = double3.Cross(up, wind);
            float light = 1f;
            if (_haveSun)
                light = 0.12f + 0.88f * (1 - NightLights.NightAt(double3.Dot(up, _sun)));
            int count = ground ? 7 : 16;
            double period = ground ? 26 : 9;
            double offset = (seed & 1023) / 1023.0;
            for (int i = 0; i < count; i++)
            {
                double phase = (t / period + (double)i / count + offset) % 1.0;
                double wobble = Math.Sin(seed * 0.37 + i * 2.3 + t * 0.6);
                double3 p;
                double radius;
                float opacity;
                if (ground)
                {
                    // Cold, heavy fog spreading over the ground downwind of the tank.
                    p = vent + up * (1.2 + 0.8 * phase) + wind * (size * (4 + 22 * phase)) + side * (size * 6 * wobble);
                    radius = size * (5 + 7 * phase);
                    opacity = (float)(0.38 * Math.Sin(Math.PI * phase));
                }
                else
                {
                    // A plume: out of the vent, up a little, then bent over by the wind and spreading.
                    p = vent + up * (size * (1.0 + 7 * Math.Pow(phase, 0.6))) + wind * (size * 16 * phase * phase)
                        + side * (size * 1.5 * wobble * phase);
                    radius = size * (0.9 + 5.5 * phase);
                    opacity = (float)(0.7 * Math.Pow(Math.Sin(Math.PI * Math.Min(phase * 1.15, 1.0)), 1.3));
                }
                double3 rel = p - _camera;
                double3 ego = Placement.ToEgo(rel, _rx, _ry, _rz);
                Puffs.Add((double3.Dot(rel, rel), new VaporInstance
                {
                    PositionRadius = new float4((float)ego.X, (float)ego.Y, (float)ego.Z, (float)radius),
                    Params = new float4(opacity, light, (seed % 997) / 997f + i * 0.13f, (float)phase),
                }));
            }
        }
        // Back to front, so the blending stacks correctly.
        Puffs.Sort((a, b) => b.Dist2.CompareTo(a.Dist2));
        int n = Math.Min(Puffs.Count, VaporTechnique.MaxPuffs);
        if (_instances.Length < n)
            Array.Resize(ref _instances, Math.Max(n, _instances.Length * 2));
        for (int i = 0; i < n; i++)
            _instances[i] = Puffs[i].Puff;
        LastPuffs = n;
        _technique ??= new VaporTechnique();
        _technique.Draw(commandBuffer, viewport, frameIndex, _instances.AsSpan(0, n));
    }

    public static void RebuildFrameResources() => _technique?.RebuildFrameResources();
}
