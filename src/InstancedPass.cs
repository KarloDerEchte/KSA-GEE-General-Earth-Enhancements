using System.Reflection;
using System.Text;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using RenderCore;

namespace GEE;

/// <summary>
/// An instanced draw of our own in the main pass, with shaders compiled from source in this assembly. Instance
/// data is copied each frame into a host-visible buffer per frame in flight, made on first use, so a frame never
/// overwrites one the GPU still reads. Vertices come from gl_VertexIndex; there is no vertex buffer.
/// </summary>
internal abstract class InstancedPass<T> : InstancedRenderTechnique<T> where T : unmanaged
{
    private readonly string _name;
    private readonly int _capacity;
    private readonly int _vertexCount;
    private BufferEx?[] _buffers = Array.Empty<BufferEx?>();
    private MappedMemory[] _memory = Array.Empty<MappedMemory>();

    protected InstancedPass(string name, string vertexShader, string fragmentShader, int capacity, int vertexCount)
        : this(name, vertexShader, fragmentShader, capacity, vertexCount, TextureReference.EmptyWhite, Program.PointClampedSampler)
    {
    }

    /// <summary>With a texture of our own at set 1, binding 0 (combined image sampler).</summary>
    protected InstancedPass(string name, string vertexShader, string fragmentShader, int capacity, int vertexCount, VkImageView texture, VkSampler sampler)
        : base(name, Program.GetRenderer(), Program.OffscreenTarget, Compile(name, vertexShader, fragmentShader),
            texture, sampler, 1, Program.GetRenderer().MaxFramesInFlight)
    {
        _name = name;
        _capacity = capacity;
        _vertexCount = vertexCount;
    }

    public int Capacity => _capacity;

    /// <summary>ShaderReference only needs the compiled module and stage; its setter is private.</summary>
    private static ShaderReference[] Compile(string name, string vertexShader, string fragmentShader)
    {
        Renderer renderer = Program.GetRenderer();
        ShaderReference One(string source, VkShaderStageFlags stage, string file)
        {
            VkShaderModule module = ShaderModuleUtils.FromString(renderer.Device, Encoding.UTF8.GetBytes(source), stage,
                ShaderModuleUtils.CloneDefaultCompileOptions(), Encoding.UTF8.GetBytes(file));
            var reference = new ShaderReference { Stage = stage };
            typeof(ShaderReference).GetProperty(nameof(ShaderReference.Shader), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(reference, (VkShaderModule?)module);
            return reference;
        }
        return new[]
        {
            One(vertexShader, VkShaderStageFlags.VertexBit, name + ".vert"),
            One(fragmentShader, VkShaderStageFlags.FragmentBit, name + ".frag"),
        };
    }

    protected override void CreateInstanceBuffer()
    {
    }

    public override void UpdateInstanceBuffer(IViewport viewport, int inFrameIndex)
    {
    }

    public override void Render(CommandBuffer inCommandBuffer, IViewport inViewport, int inFrameIndex)
    {
    }

    public void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex, ReadOnlySpan<T> instances)
        => Draw(commandBuffer, viewport, frameIndex, instances, 0, 0);

    /// <summary>
    /// The last <paramref name="coarseCount"/> instances are drawn with <paramref name="coarseVertices"/> vertices each
    /// (a coarser mesh), the ones before them with the full vertex count.
    /// </summary>
    public void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex, ReadOnlySpan<T> instances, int coarseCount, int coarseVertices)
    {
        int count = Math.Min(instances.Length, _capacity);
        if (count == 0)
            return;
        if (_buffers.Length == 0)
        {
            int frames = Math.Max(Renderer.MaxFramesInFlight, 1) + 1;
            _buffers = new BufferEx?[frames];
            _memory = new MappedMemory[frames];
        }
        int slot = (int)((uint)frameIndex % (uint)_buffers.Length);
        if (_buffers[slot] is not { } buffer)
        {
            buffer = Renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
            {
                Name = $"{_name}{slot}",
                BufferUsage = VkBufferUsageFlags.VertexBufferBit,
                BufferSize = ByteSize.Of<T>(_capacity),
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            });
            _buffers[slot] = buffer;
            _memory[slot] = buffer.Map();
        }
        instances[..count].CopyTo(_memory[slot].AsSpan<T>());

        commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, Pipeline);
        Program.SetViewport(commandBuffer);
        Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[2];
        sets[0] = GlobalShaderBindings.DescriptorSet(viewport.ShaderSlot);
        sets[1] = TextureSet;
        commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, PipelineLayout, 0, sets, default);
        commandBuffer.BindVertexBuffer(0, buffer.VkBuffer);
        coarseCount = Math.Clamp(coarseCount, 0, count);
        if (count - coarseCount > 0)
            commandBuffer.Draw(_vertexCount, count - coarseCount, 0, 0);
        if (coarseCount > 0)
            commandBuffer.Draw(coarseVertices, coarseCount, 0, count - coarseCount);
    }

    public override void Dispose()
    {
        for (int i = 0; i < _buffers.Length; i++)
        {
            if (_buffers[i] is { } buffer)
            {
                _memory[i].Unmap();
                buffer.Dispose();
            }
        }
        base.Dispose();
    }
}

internal static class ShaderSource
{
    public const string Version = """
        #version 450

        """;

    /// <summary>
    /// Mirrors the start of Content/Core/Shaders/Common/Global.glsl (set 0, binding 0); only the leading members
    /// of the block are declared.
    /// </summary>
    public const string CameraBlock = """
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
        };

        layout(set = 0, binding = 0) uniform Global
        {
            Camera camera;
        } global;
        """;
}
