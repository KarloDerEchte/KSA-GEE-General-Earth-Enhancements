using System.Runtime.InteropServices;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;

namespace OsmBuildings;

/// <summary>One urban patch: ground-aligned quad (centre and radius, east and north axes in ego space) and its look.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct UrbanInstance
{
    public float4 CenterRadius;
    public float4 East;
    public float4 North;
    /// <summary>x opacity, y sunlight, z seed, w metres per uv unit (the radius, for the street grid).</summary>
    public float4 Params;
}

/// <summary>
/// Built-up land seen from altitude by day: soft ground-aligned patches with a block and street pattern, blended
/// over the terrain (premultiplied alpha), so towns and cities show as grey areas from high up and from orbit,
/// where single buildings are far too small and too many. The pattern fades to its average once a block is smaller
/// than a pixel.
/// </summary>
internal sealed class UrbanTechnique() : InstancedPass<UrbanInstance>("OsmBuildingsUrban", VertexShader, FragmentShader, MaxPatches, 4)
{
    public const int MaxPatches = 120_000;

    private static readonly PtrOwner<VkPipelineColorBlendAttachmentState> Attachment = new(new VkPipelineColorBlendAttachmentState
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

    private static readonly PtrOwner<VkPipelineColorBlendStateCreateInfo> Blend = new(new VkPipelineColorBlendStateCreateInfo
    {
        AttachmentCount = 1,
        Attachments = Attachment,
    });

    protected override VertexInput MakeVertexInput()
    {
        return new VertexInput(1, 4).AddBindingWithAttributes<UrbanInstance>(0, 0, VkVertexInputRate.Instance);
    }

    protected override void OnRebuildFrameResources()
    {
        CreatePipeline(VkPrimitiveTopology.TriangleStrip, VkCullModeFlags.None, VkFrontFace.Clockwise, VkPolygonMode.Fill,
            RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite, Blend, out Pipeline);
    }

    private const string VertexShader = ShaderSource.Version + ShaderSource.CameraBlock + """

        layout(location = 0) in vec4 centerRadius;
        layout(location = 1) in vec4 east;
        layout(location = 2) in vec4 north;
        layout(location = 3) in vec4 params;

        layout(location = 0) out vec2 outUv;
        layout(location = 1) flat out vec4 outParams;

        const vec2 corners[4] = vec2[](vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0), vec2(1.0, 1.0));

        void main()
        {
            vec2 c = corners[gl_VertexIndex];
            vec3 p = centerRadius.xyz + (east.xyz * c.x + north.xyz * c.y) * centerRadius.w;
            gl_Position = global.camera.viewProjection * vec4(p, 1.0);
            outUv = c;
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
            p = fract(p * vec2(127.1, 311.7));
            p += dot(p, p + 34.23);
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
            float opacity = params.x, light = params.y, seed = params.z, metres = params.w;
            // Ragged edge, so neighbouring patches merge into one built-up area.
            float edge = noise(uv * 2.3 + seed * 17.0);
            float cover = smoothstep(1.0, 0.45, r + (edge - 0.5) * 0.5);
            // Blocks of about 110 m between darker streets, each block its own shade (roofs, yards, car parks).
            vec2 m = uv * metres / 110.0 + seed * 31.0;
            vec2 cell = floor(m), f = fract(m);
            float px = max(fwidth(m.x), fwidth(m.y));
            float street = 1.0 - smoothstep(0.06, 0.06 + px * 1.5, min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y)));
            float block = hash(cell);
            vec3 detail = mix(vec3(0.46, 0.45, 0.43), vec3(0.66, 0.64, 0.60), block) * (1.0 - 0.35 * street);
            vec3 mean = vec3(0.52, 0.51, 0.48);
            vec3 color = mix(detail, mean, smoothstep(0.25, 0.8, px)) * light;
            float alpha = clamp(cover * opacity, 0.0, 1.0);
            outColor = vec4(color * alpha, alpha);
        }
        """;
}

/// <summary>
/// Places the urban patches from the regional and global city light data (towns by size, residential and commercial
/// land, motorways), which the mod loads for both the night and the day side: by day each light point of a set
/// becomes a ground patch, thinned out with distance like the night glow, each kept patch growing so the built-up
/// area stays covered. Hidden near the ground, where the buildings themselves are drawn.
/// </summary>
internal static class UrbanRenderer
{
    public static RegionalLights? Regional;
    public static RegionalLights? Global;
    public static Settings? Settings;
    public static int LastPatches;

    private static UrbanTechnique? _technique;
    private static UrbanInstance[] _instances = new UrbanInstance[4096];

    public static void Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        if (viewport != Program.MainViewport)
            return;
        LastPatches = 0;
        if (Settings is not { Enabled: true, CitiesFromAltitude: true } settings)
            return;
        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(settings.Body, out Astronomical? astronomical)
            || astronomical is not Celestial body)
            return;
        int count = Collect(viewport.GetCamera(), body, settings);
        LastPatches = count;
        if (count == 0)
            return;
        _technique ??= new UrbanTechnique();
        _technique.Draw(commandBuffer, viewport, frameIndex, _instances.AsSpan(0, count));
    }

    public static void RebuildFrameResources() => _technique?.RebuildFrameResources();

    private static int Collect(Camera camera, Celestial body, Settings settings)
    {
        if (NightLights.SunDirCcf(body, settings) is not { } sun)
            return 0;
        double3 cam = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        double3 rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce);
        double3 ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce);
        double3 rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);
        double planetRadius = body.MeanRadius;
        double camR = Length(cam);
        double altitude = camR - planetRadius;
        // Near the ground the buildings (detailed and far field) are there; the patches come in with altitude.
        float altitudeFade = Smooth(3000, 12000, altitude);
        if (altitudeFade <= 0)
            return 0;
        double3 camDir = cam / camR;
        double horizonAngle = Math.Acos(Math.Clamp((planetRadius - 2000) / camR, -1, 1)) + 0.02;
        double pixelAngle = 2 * Math.Tan(camera.GetFieldOfView() / 2) / Math.Max(camera.FramebufferSize.Y, 1);
        double nearFade0 = settings.FarBuildings ? settings.FarRadiusKm * 600 : 6000, nearFade1 = nearFade0 * 2;

        int count = 0;
        void Consider(LightSet set)
        {
            if (set.Count == 0 || count >= UrbanTechnique.MaxPatches)
                return;
            double3 centerDir = double3.Normalize(set.Center);
            double setAngle = set.Radius / planetRadius;
            if (Math.Acos(Math.Clamp(double3.Dot(centerDir, camDir), -1, 1)) - setAngle > horizonAngle)
                return;
            // Wholly on the night side: nothing to see by day light.
            if (double3.Dot(centerDir, sun) + Math.Sin(Math.Min(setAngle, 1.5)) < -0.12)
                return;
            double3 relCenter = set.Center - cam;
            double setDist = Length(relCenter);
            if (setDist > set.Radius && camera.IsSphereOutsideFov(Rotate(relCenter, rx, ry, rz), set.Radius))
                return;
            double near = Math.Max(setDist - set.Radius, 1);
            // Patches of about 8 pixels: the share of points kept at the set's nearest point.
            double keepNear = Math.Min(Math.Pow(set.Spacing / (near * pixelAngle * 8), 2), 1);
            if (keepNear < 1e-5)
                return;
            LightPoint[] points = set.Points;
            int n = Math.Min(set.Count, points.Length);
            for (int i = 0; i < n && count < UrbanTechnique.MaxPatches; i++)
            {
                ref LightPoint p = ref points[i];
                if (p.Level >= keepNear)
                {
                    if (set.Sorted)
                        break;
                    continue;
                }
                double3 position = set.Center + new double3(p.Offset.X, p.Offset.Y, p.Offset.Z);
                double3 up = position / Length(position);
                double3 rel = position - cam;
                if (double3.Dot(up, rel) > 100)
                    continue;                                          // behind the horizon
                double sunUp = double3.Dot(up, sun);
                if (sunUp < -0.1)
                    continue;
                double dist = Length(rel);
                double keep = Math.Min(Math.Pow(set.Spacing / (Math.Max(dist, 1) * pixelAngle * 8), 2), 1);
                if (p.Level >= keep)
                    continue;
                float fade = altitudeFade * Smooth(nearFade0, nearFade1, dist);
                if (fade <= 0)
                    continue;
                // Each kept patch stands for the points left out around it.
                double radius = set.Spacing * 0.85 / Math.Sqrt(keep);
                Placement.Axes(up, out double3 east, out double3 north);
                float light = (float)Math.Clamp(0.12 + 1.1 * sunUp, 0.06, 1.0);
                double3 ego = Rotate(rel, rx, ry, rz);
                double3 e = Rotate(east, rx, ry, rz), nn = Rotate(north, rx, ry, rz);
                if (count == _instances.Length)
                    Array.Resize(ref _instances, Math.Min(_instances.Length * 2, UrbanTechnique.MaxPatches));
                _instances[count++] = new UrbanInstance
                {
                    CenterRadius = new float4((float)ego.X, (float)ego.Y, (float)ego.Z, (float)radius),
                    East = new float4((float)e.X, (float)e.Y, (float)e.Z, 0),
                    North = new float4((float)nn.X, (float)nn.Y, (float)nn.Z, 0),
                    Params = new float4(0.5f * fade, light, p.Level * 97.3f % 1f, (float)radius),
                };
            }
        }

        if (settings.RegionalLights && Regional != null)
            foreach (var set in Regional.Snapshot)
                Consider(set);
        if (settings.GlobalLights && Global != null)
        {
            HashSet<int>? covered = settings.RegionalLights ? Regional?.ReadyKeys : null;
            foreach (var set in Global.Snapshot)
                if (covered == null || !covered.Contains(set.CoverKey))
                    Consider(set);
        }
        return count;
    }

    private static float Smooth(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));
    private static double3 Rotate(double3 v, double3 rx, double3 ry, double3 rz) => rx * v.X + ry * v.Y + rz * v.Z;
}
