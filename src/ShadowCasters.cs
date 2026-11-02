using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using KSA.Rendering.Lighting;
using RenderCore;

namespace GEE;

/// <summary>One shadow caster: the instance's model matrix (rows: mesh x, y, z axes and the origin, ego frame).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ShadowInstance
{
    public float4 Row0, Row1, Row2, Row3;
}

/// <summary>
/// The game draws static objects (our buildings) into no shadow map, so houses stood shadowless next to trees with
/// long shadows. This draws the buildings into the sun's cascaded shadow maps, the way the game draws vessel parts:
/// a depth-only pipeline of our own on the cascade's descriptor set and push constant, recorded at the end of every
/// cascade (Harmony prefix on CascadedShadowSystem.EndCascade). The casters are simple stand-in shapes in the
/// unit space of our meshes (box, gable roof, tanks, tent), so no mesh data of the game is needed. Static objects
/// already receive these shadows, as do the terrain and the trees, so buildings shade streets, gardens and each
/// other. Cascades reach 1.5 km; casters further out are not collected.
/// </summary>
internal static class ShadowCasters
{
    public const byte None = 0, Box = 1, Gable = 2, Cylinder = 3, Dome = 4, LyingCylinder = 5, Tent = 6;
    private const int Kinds = 7;
    /// <summary>Casters beyond this from the camera are left out (the game's last cascade ends at 1.5 km).</summary>
    public const double RangeM = 2000;
    /// <summary>
    /// Distance bands: a cascade ending at splitFar draws the casters nearer than splitFar plus this reach (long
    /// evening shadows of tall buildings), so the small near cascades do not draw the whole town.
    /// </summary>
    private const float ShadowReachM = 400;
    private const int MaxCasters = 65536;

    public static bool Enabled = true;
    public static int LastCount;
    public static string? Error;

    private static byte[]? _kinds;
    private static readonly int[] MeshFirst = new int[Kinds], MeshCount = new int[Kinds];

    // Collected on the main thread in BuildingRenderer.Render, drawn later in the same frame.
    private static readonly ShadowInstance[] Cpu = new ShadowInstance[MaxCasters];
    private static readonly float[] CpuDist = new float[MaxCasters];
    private static readonly int[] KindStart = new int[Kinds + 1];
    private static int _count;
    private static long _generation, _uploaded = -1;
    private static readonly List<int>[] Buckets = Enumerable.Range(0, Kinds).Select(_ => new List<int>()).ToArray();

    // GPU side.
    private static BufferEx? _mesh;
    private static BufferEx?[] _buffers = Array.Empty<BufferEx?>();
    private static MappedMemory[] _memory = Array.Empty<MappedMemory>();
    private static int _slot;
    private static VkPipelineLayout _layout;
    private static VkPipeline _pipeline;
    private static CascadedShadowSystem? _builtFor;
    private static bool _failed;

    /// <summary>The stand-in shape of a model variant, None for roads, ground cover, towers and the like.</summary>
    public static byte KindOf(int variant)
    {
        _kinds ??= BuildKinds();
        return (uint)variant < (uint)_kinds.Length ? _kinds[variant] : None;
    }

    private static byte[] BuildKinds()
    {
        var kinds = new byte[Buildings.VariantCount];
        for (int v = 0; v < 8; v++)
            kinds[v] = Box;
        for (int i = 0; i < Landmarks.All.Length; i++)
        {
            string name = Landmarks.All[i].Name;
            // Closed halls; the lattice towers would cast solid blocks.
            if (name.Contains("Assembly Building") || name.Contains("Integration Facility") || name.Contains("mobile gantry"))
                kinds[Buildings.FirstLandmarkVariant + i] = Box;
        }
        for (int r = 0; r < Buildings.RoofVariants; r++)
            kinds[Buildings.FirstRoofVariant + r] = Gable;
        kinds[Buildings.MetalRoofVariant] = Gable;
        kinds[Buildings.TankVariant] = Cylinder;
        kinds[Buildings.TankCapVariant] = Dome;
        kinds[Buildings.HorizontalTankVariant] = LyingCylinder;
        kinds[Buildings.TentVariant] = Tent;
        return kinds;
    }

    // ---- collection (main thread) ----

    /// <summary>
    /// Takes the casters of this frame's selection: kind[j] (None to skip) and distance of instance j, its matrix
    /// at matrices[slot[j]].
    /// </summary>
    public static void Collect(int n, byte[] kind, float[] dist, StaticObjectModel.PerInstanceData[] matrices, int[] slot)
    {
        foreach (var bucket in Buckets)
            bucket.Clear();
        if (Enabled)
            for (int j = 0; j < n; j++)
                if (kind[j] != None)
                    Buckets[kind[j]].Add(j);
        int at = 0;
        for (int k = 0; k < Kinds; k++)
        {
            KindStart[k] = at;
            var bucket = Buckets[k];
            // Nearest band first, so a near cascade draws a prefix of each kind.
            for (int band = 0; band < 3; band++)
                foreach (int j in bucket)
                {
                    float d = dist[j];
                    if (Band(d) != band || at >= MaxCasters)
                        continue;
                    float4x4 m = matrices[slot[j]].ModelMatrix;
                    Cpu[at] = new ShadowInstance
                    {
                        Row0 = new float4(m.M11, m.M12, m.M13, m.M14),
                        Row1 = new float4(m.M21, m.M22, m.M23, m.M24),
                        Row2 = new float4(m.M31, m.M32, m.M33, m.M34),
                        Row3 = new float4(m.M41, m.M42, m.M43, m.M44),
                    };
                    CpuDist[at] = d;
                    at++;
                }
        }
        KindStart[Kinds] = at;
        _count = at;
        LastCount = at;
        _generation++;
    }

    /// <summary>Nothing to cast this frame (the renderer did not run or found nothing).</summary>
    public static void Clear()
    {
        if (_count == 0)
            return;
        _count = 0;
        LastCount = 0;
        _generation++;
    }

    private static int Band(float d) => d < 500 ? 0 : d < 900 ? 1 : 2;

    // ---- drawing (inside each cascade's rendering) ----

    public static void DrawCascade(CascadedShadowSystem csm, CommandBuffer cb, int cascade)
    {
        if (_failed || !Enabled || _count == 0)
            return;
        try
        {
            if (!EnsurePipeline(csm))
                return;
            if (_uploaded != _generation)
            {
                Upload();
                _uploaded = _generation;
            }
            BufferEx instances = _buffers[_slot]!.Value;
            float far = cascade < csm.CascadeData.Length ? csm.CascadeData[cascade].SplitFar : float.MaxValue;
            float reach = far + ShadowReachM;

            cb.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            cb.SetDepthBias(csm.CurrentSettings.VkDepthBias, 0f, csm.CurrentSettings.VkSlopeBias);
            ByteSize32 offset = csm.DescriptorDynamicOffset ?? default;
            cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, new ReadOnlySpan<VkDescriptorSet>(csm.DescriptorSet),
                new Span<ByteSize32>(ref offset));
            cb.PushConstants(_layout, VkShaderStageFlags.VertexBit, ByteSize.Zero, cascade);
            cb.BindVertexBuffer(0, _mesh!.Value.VkBuffer);
            cb.BindVertexBuffer(1, instances.VkBuffer);
            for (int k = 1; k < Kinds; k++)
            {
                int start = KindStart[k], end = KindStart[k + 1];
                // The kind's casters are in distance bands; draw those that can reach into this cascade.
                int take = end;
                if (reach < 900)
                {
                    take = start;
                    while (take < end && CpuDist[take] < (reach < 500 ? 500 : 900))
                        take++;
                }
                if (take > start)
                    cb.Draw(MeshCount[k], take - start, MeshFirst[k], start);
            }
        }
        catch (Exception e)
        {
            _failed = true;
            Error = e.Message;
            Log.Error($"building shadows disabled after error: {e}");
        }
    }

    private static void Upload()
    {
        Renderer renderer = Program.GetRenderer();
        if (_buffers.Length == 0)
        {
            int frames = Math.Max(renderer.MaxFramesInFlight, 1) + 1;
            _buffers = new BufferEx?[frames];
            _memory = new MappedMemory[frames];
        }
        _slot = (_slot + 1) % _buffers.Length;
        if (_buffers[_slot] is null)
        {
            var buffer = renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
            {
                Name = $"GEEShadowCasters{_slot}",
                BufferUsage = VkBufferUsageFlags.VertexBufferBit,
                BufferSize = ByteSize.Of<ShadowInstance>(MaxCasters),
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            });
            _buffers[_slot] = buffer;
            _memory[_slot] = buffer.Map();
        }
        Cpu.AsSpan(0, _count).CopyTo(_memory[_slot].AsSpan<ShadowInstance>());
    }

    // ---- pipeline and stand-in meshes ----

    private static unsafe bool EnsurePipeline(CascadedShadowSystem csm)
    {
        if (_builtFor == csm && _pipeline.IsNotNull())
            return true;
        Renderer renderer = Program.GetRenderer();
        DeviceEx device = renderer.Device;
        if (_pipeline.IsNotNull())
            device.DestroyPipeline(_pipeline, null);
        if (_layout.IsNotNull())
            device.DestroyPipelineLayout(_layout, null);

        var push = new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Offset = ByteSize.Zero, Size = ByteSize.Of<int>() };
        Span<VkDescriptorSetLayout> sets = stackalloc VkDescriptorSetLayout[1];
        sets[0] = csm.DescriptorSetLayout;
        _layout = device.CreatePipelineLayout(sets, new ReadOnlySpan<VkPushConstantRange>(push), null);

        VkShaderModule module = ShaderModuleUtils.FromString(device, Encoding.UTF8.GetBytes(VertexShader), VkShaderStageFlags.VertexBit,
            ShaderModuleUtils.CloneDefaultCompileOptions(), Encoding.UTF8.GetBytes("GEEShadowCasters.vert"));
        var stage = new VkPipelineShaderStageCreateInfo
        {
            Name = Presets.Entrypoint.Main,
            Module = module,
            Stage = VkShaderStageFlags.VertexBit,
        };
        // As the game's vessel part casters: no culling, depth clamp, dynamic depth bias.
        var raster = new VkPipelineRasterizationStateCreateInfo
        {
            RasterizerDiscardEnable = false,
            PolygonMode = VkPolygonMode.Fill,
            CullMode = VkCullModeFlags.None,
            FrontFace = VkFrontFace.CounterClockwise,
            DepthBiasEnable = true,
            DepthBiasConstantFactor = 1.25f,
            DepthBiasSlopeFactor = 2f,
            DepthClampEnable = true,
            LineWidth = 1f,
        };
        VkDynamicState* dynamicStates = stackalloc VkDynamicState[1] { VkDynamicState.DepthBias };
        var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 1, DynamicStates = dynamicStates };
        VertexInput vertexInput = new VertexInput(2, 5)
            .AddBinding(0, ByteSize.Of<float3>(), VkVertexInputRate.Vertex)
            .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, new ByteSize(0u))
            .AddBindingWithAttributes<ShadowInstance>(1, 1, VkVertexInputRate.Instance);
        var info = new VkGraphicsPipelineCreateInfo
        {
            Layout = _layout,
            StageCount = 1,
            Stages = &stage,
            DynamicState = &dynamic,
            VertexInputState = vertexInput,
            InputAssemblyState = Presets.InputAssembly.TriangleList,
            RasterizationState = &raster,
            DepthStencilState = Presets.DepthStencil.DepthTestWrite,
            ColorBlendState = Presets.BlendState.BlendNone,
        };
        csm.SetupGraphicsPipeline(ref info);
        _pipeline = device.CreateGraphicsPipeline(PipelineCaches.Shared, info, null);
        device.DestroyShaderModule(module, null);
        _builtFor = csm;
        _mesh ??= MakeMesh(renderer);
        Log.Info("building shadow pipeline built");
        return true;
    }

    private static BufferEx MakeMesh(Renderer renderer)
    {
        var all = new List<float3>();
        for (int k = 1; k < Kinds; k++)
        {
            MeshFirst[k] = all.Count;
            all.AddRange(Shape((byte)k));
            MeshCount[k] = all.Count - MeshFirst[k];
        }
        var buffer = renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "GEEShadowShapes",
            BufferUsage = VkBufferUsageFlags.VertexBufferBit,
            BufferSize = ByteSize.Of<float3>(all.Count),
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        });
        MappedMemory memory = buffer.Map();
        all.ToArray().AsSpan().CopyTo(memory.AsSpan<float3>());
        memory.Unmap();
        return buffer;
    }

    /// <summary>
    /// Triangles of a stand-in in the unit space of our meshes: x up from 0 to 1, y and z across in [-0.5, 0.5]
    /// (tools/make_assets.py unit_box and gable, tools/structure_assets.py tank, tank_dome, horizontal_tank, tent).
    /// </summary>
    private static List<float3> Shape(byte kind)
    {
        const int N = 16;
        var t = new List<float3>();
        void Tri(float3 a, float3 b, float3 c)
        {
            t.Add(a);
            t.Add(b);
            t.Add(c);
        }
        void Quad(float3 a, float3 b, float3 c, float3 d)
        {
            Tri(a, b, c);
            Tri(a, c, d);
        }
        // Vertical solid of revolution about the x axis: rings of (height, radius).
        void Lathe((float X, float R)[] profile)
        {
            for (int p = 0; p + 1 < profile.Length; p++)
                for (int s = 0; s < N; s++)
                {
                    float a0 = s * MathF.Tau / N, a1 = (s + 1) * MathF.Tau / N;
                    var (x0, r0) = profile[p];
                    var (x1, r1) = profile[p + 1];
                    Quad(new float3(x0, r0 * MathF.Cos(a0), r0 * MathF.Sin(a0)), new float3(x0, r0 * MathF.Cos(a1), r0 * MathF.Sin(a1)),
                        new float3(x1, r1 * MathF.Cos(a1), r1 * MathF.Sin(a1)), new float3(x1, r1 * MathF.Cos(a0), r1 * MathF.Sin(a0)));
                }
            var (xt, rt) = profile[^1];
            if (rt > 0)
                for (int s = 0; s < N; s++)
                {
                    float a0 = s * MathF.Tau / N, a1 = (s + 1) * MathF.Tau / N;
                    Tri(new float3(xt, 0, 0), new float3(xt, rt * MathF.Cos(a0), rt * MathF.Sin(a0)), new float3(xt, rt * MathF.Cos(a1), rt * MathF.Sin(a1)));
                }
        }
        // Lying along y: a cross-section (x, z) from angle from to to, swept from y = -0.5 to 0.5, ends closed by a
        // fan from the centre.
        void Sweep(Func<float, (float X, float Z)> section, float from, float to, (float X, float Z) centre)
        {
            for (int s = 0; s < N; s++)
            {
                var (x0, z0) = section(from + (to - from) * s / N);
                var (x1, z1) = section(from + (to - from) * (s + 1) / N);
                Quad(new float3(x0, -0.5f, z0), new float3(x1, -0.5f, z1), new float3(x1, 0.5f, z1), new float3(x0, 0.5f, z0));
                foreach (float y in new[] { -0.5f, 0.5f })
                    Tri(new float3(centre.X, y, centre.Z), new float3(x0, y, z0), new float3(x1, y, z1));
            }
        }

        switch (kind)
        {
            case Box:
            {
                const float h = 0.5f;
                Quad(new float3(1, -h, -h), new float3(1, h, -h), new float3(1, h, h), new float3(1, -h, h));
                Quad(new float3(0, -h, -h), new float3(1, -h, -h), new float3(1, -h, h), new float3(0, -h, h));
                Quad(new float3(0, h, -h), new float3(1, h, -h), new float3(1, h, h), new float3(0, h, h));
                Quad(new float3(0, -h, -h), new float3(1, -h, -h), new float3(1, h, -h), new float3(0, h, -h));
                Quad(new float3(0, -h, h), new float3(1, -h, h), new float3(1, h, h), new float3(0, h, h));
                break;
            }
            case Gable:
                // Ridge along y at x = 1 over z = 0, eaves at x = 0, z = +-0.5.
                Quad(new float3(0, -0.5f, -0.5f), new float3(0, 0.5f, -0.5f), new float3(1, 0.5f, 0), new float3(1, -0.5f, 0));
                Quad(new float3(0, -0.5f, 0.5f), new float3(0, 0.5f, 0.5f), new float3(1, 0.5f, 0), new float3(1, -0.5f, 0));
                Tri(new float3(0, -0.5f, -0.5f), new float3(0, -0.5f, 0.5f), new float3(1, -0.5f, 0));
                Tri(new float3(0, 0.5f, -0.5f), new float3(0, 0.5f, 0.5f), new float3(1, 0.5f, 0));
                break;
            case Cylinder:
                Lathe(new[] { (0f, 0.5f), (1f, 0.5f) });
                break;
            case Dome:
            {
                var rings = new (float, float)[6];
                for (int i = 0; i < rings.Length; i++)
                {
                    float phi = i * MathF.PI / 2 / (rings.Length - 1);
                    rings[i] = (MathF.Sin(phi), 0.5f * MathF.Cos(phi));
                }
                Lathe(rings);
                break;
            }
            case LyingCylinder:
                Sweep(a => (0.5f + 0.5f * MathF.Cos(a), 0.5f * MathF.Sin(a)), 0, MathF.Tau, (0.5f, 0));
                break;
            case Tent:
                Sweep(a => (MathF.Sin(a), 0.5f * MathF.Cos(a)), 0, MathF.PI, (0, 0));
                break;
        }
        return t;
    }

    public static void Dispose()
    {
        DeviceEx device = Program.GetRenderer().Device;
        if (_pipeline.IsNotNull())
            device.DestroyPipeline(_pipeline, null);
        if (_layout.IsNotNull())
            device.DestroyPipelineLayout(_layout, null);
        _pipeline = default;
        _layout = default;
        _builtFor = null;
    }

    // Only the cascades' matrices of the game's CSM block (Lighting/CascadedShadows.glsl, std140).
    private const string VertexShader = """
        #version 450

        layout(location = 0) in vec3 inPos;
        layout(location = 1) in vec4 row0;
        layout(location = 2) in vec4 row1;
        layout(location = 3) in vec4 row2;
        layout(location = 4) in vec4 row3;

        struct CSMCascade
        {
            mat4 viewProj;
            float splitNear;
            float splitFar;
            float invDepthRange;
            float texelWorldSize;
        };

        layout(set = 0, binding = 0) uniform CsmShadows
        {
            CSMCascade cascades[6];
        } csm;

        layout(push_constant) uniform PushConsts
        {
            int cascadeIndex;
        } pushConsts;

        void main()
        {
            vec3 world = row0.xyz * inPos.x + row1.xyz * inPos.y + row2.xyz * inPos.z + row3.xyz;
            gl_Position = csm.cascades[pushConsts.cascadeIndex].viewProj * vec4(world, 1.0);
        }
        """;
}
