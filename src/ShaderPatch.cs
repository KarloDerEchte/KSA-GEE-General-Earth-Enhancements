using System.Reflection;
using System.Text;
using HarmonyLib;

namespace GEE;

/// <summary>
/// Roads and other flat surfaces of the mod lie a few decimetres above the exact terrain, but the game draws the
/// terrain into its shadow maps from the untessellated base mesh, which lies above that in many places (by
/// metres far away, where the mesh is coarse). The game's static object shader then finds them under the ground and
/// lights them with sky light only: dark navy roads. The game compiles its shaders from source when it starts, so
/// this patches the text of StaticObject.frag as it is read: pixels whose diffuse and PBR textures carry the
/// marker alphas below look up their shadows a little higher up. Buildings and trees, which stand well above the
/// lift, still shade the roads; the shader's diffuse and PBR alpha channels are unused otherwise.
/// These pixels also take the mesh normal as it is: the normal-mapped one came out tilted by tens of degrees on the
/// mod's instances (the shader did not read the mod's flat normal map), lighting roads by their direction.
/// </summary>
internal static class ShaderPatch
{
    /// <summary>Alpha of the diffuse and PBR textures of ground surfaces (tools/make_assets.py writes the same).</summary>
    public const int DiffuseMarker = 0x2B, PbrMarker = 0xB2;
    /// <summary>Shadow lookup height above a marked surface: this much, plus LiftPerKm per km from the camera.</summary>
    private const double LiftMeters = 1.5;
    private const double LiftPerKm = 2.0;

    private const string ShaderFile = "StaticObject.frag";
    private const string Anchor = "    vec3 V = normalize(-inWorldPosition);";
    private const string SunShadow = "getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal)";
    private const string DiffuseFetch = "    vec3 sampledColor = gammaToLinear(texture(sampler2D(globalTextures[drawData.diffuseTextureIndex], textureSampler), inUv).xyz);\n";
    private const string PbrFetch = "    vec3 sampledPbr = texture(sampler2D(globalTextures[drawData.pbrTextureIndex], textureSampler), inUv).xyz;\n";
    private const string Cascaded = "GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth)";

    public static bool Patched { get; private set; }

    public static void Apply(Harmony harmony)
    {
        try
        {
            var type = AccessTools.TypeByName("RenderCore.ShaderModuleUtils");
            var method = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FromFile" && m.GetParameters().Length == 4);
            if (method == null)
            {
                Log.Error("shader patch: ShaderModuleUtils.FromFile not found, roads keep the game's terrain shadows");
                return;
            }
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ShaderPatch), nameof(Transpiler)));
            Log.Notice("shader patch installed");
        }
        catch (Exception e)
        {
            Log.Error($"shader patch failed: {e.Message}");
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
    {
        var read = AccessTools.Method(typeof(File), nameof(File.ReadAllBytes), new[] { typeof(string) });
        var replacement = AccessTools.Method(typeof(ShaderPatch), nameof(ReadShader));
        foreach (var instruction in code)
        {
            if (instruction.Calls(read))
                instruction.operand = replacement;
            yield return instruction;
        }
    }

    private static byte[] ReadShader(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (path.Replace('\\', '/').EndsWith("Planet/Planet.frag", StringComparison.OrdinalIgnoreCase))
            return PatchPlanet(path, bytes);
        if (path.Replace('\\', '/').Contains("Planet/GroundClutter/", StringComparison.OrdinalIgnoreCase))
            return PatchClutter(path, GrassBoost.PatchShader(path, bytes));
        if (path.Replace('\\', '/').EndsWith("Planet/TerrainMesh/PrepareModifiers.comp", StringComparison.OrdinalIgnoreCase))
            return PatchRealElevation(path, bytes);
        if (path.Replace('\\', '/').EndsWith("Planet/TerrainMesh/FinalizeModifiers.comp", StringComparison.OrdinalIgnoreCase))
            return PatchLakeBeds(path, bytes);
        if (path.Replace('\\', '/').EndsWith("Water/Ocean.frag", StringComparison.OrdinalIgnoreCase))
            return PatchOcean(path, bytes);
        if (path.Replace('\\', '/').EndsWith("PostProcess/composite.frag", StringComparison.OrdinalIgnoreCase))
            return PatchComposite(path, bytes);
        if (path.Replace('\\', '/').EndsWith("Mesh/StaticObject.vert", StringComparison.OrdinalIgnoreCase))
            return RoadWear.PatchVertex(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")) is { } wearVert ? Encoding.UTF8.GetBytes(wearVert) : bytes;
        if (!path.EndsWith(ShaderFile, StringComparison.OrdinalIgnoreCase))
            return bytes;
        try
        {
            string text = Encoding.UTF8.GetString(bytes);
            // The game's line endings, whatever they are, so the anchors match either way.
            string source = text.Replace("\r\n", "\n");
            if (!source.Contains(Anchor + "\n") || !source.Contains(SunShadow) || !source.Contains(Cascaded))
            {
                Log.Error($"shader patch: {ShaderFile} has changed, roads keep the game's terrain shadows");
                return bytes;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // The marker test reads the alphas of the very texels the shader fetches anyway: fetch them once (vec4)
            // and reuse them, so unmarked pixels (vessels, buildings) pay no extra texture reads. Falls back to
            // fetching again if the game's two lines have changed.
            bool shared = source.Contains(DiffuseFetch) && source.Contains(PbrFetch);
            string diffuseAlpha = shared ? "geeDiffuse.a" : "texture(sampler2D(globalTextures[drawData.diffuseTextureIndex], textureSampler), inUv).a";
            string pbrAlpha = shared ? "geePbr.a" : "texture(sampler2D(globalTextures[drawData.pbrTextureIndex], textureSampler), inUv).a";
            if (shared)
                source = source.Replace(DiffuseFetch, "    vec4 geeDiffuse = texture(sampler2D(globalTextures[drawData.diffuseTextureIndex], textureSampler), inUv);\n    vec3 sampledColor = gammaToLinear(geeDiffuse.xyz);\n")
                    .Replace(PbrFetch, "    vec4 geePbr = texture(sampler2D(globalTextures[drawData.pbrTextureIndex], textureSampler), inUv);\n    vec3 sampledPbr = geePbr.xyz;\n");
            string lift = $$"""
                    // GEE: shadow lookup above the mod's ground surfaces (GEE ShaderPatch.cs).
                    float geeShadowLift = 0.0;
                #ifndef SAMPLE_TERRAIN
                    if (abs({{diffuseAlpha}} - {{(DiffuseMarker / 255.0).ToString("0.0000", inv)}}) < 0.03
                        && abs({{pbrAlpha}} - {{(PbrMarker / 255.0).ToString("0.0000", inv)}}) < 0.03)
                    {
                        geeShadowLift = {{LiftMeters.ToString("0.0##", inv)}} + {{(LiftPerKm / 1000).ToString("0.0#####", inv)}} * length(inWorldPosition);
                        N = geometricNormal;
                    }
                #endif
                    vec3 geeShadowPos = inWorldPosition + geometricNormal * geeShadowLift;

                """;
            source = source.Replace(Anchor + "\n", lift + Anchor + "\n")
                .Replace(SunShadow, SunShadow.Replace("inWorldPosition", "geeShadowPos"))
                .Replace(Cascaded, Cascaded.Replace("inWorldPosition", "geeShadowPos"));
            source = RoadWear.PatchFragment(source) ?? source;
            Patched = true;
            Log.Notice($"shader patch: {path} patched");
            return Encoding.UTF8.GetBytes(source);
        }
        catch (Exception e)
        {
            Log.Error($"shader patch: {e.Message}");
            return bytes;
        }
    }

    /// <summary>The ground clutter shaders that place clutter on the terrain with the lake cut and the real elevation (LakeOcean.PatchClutter).</summary>
    private static byte[] PatchClutter(string path, byte[] bytes)
    {
        string name = Path.GetFileName(path);
        try
        {
            if (LakeOcean.PatchClutter(name, Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")) is not { } patched)
                return bytes;
            Log.Notice($"shader patch: {name} patched (clutter follows the terrain)");
            return Encoding.UTF8.GetBytes(patched);
        }
        catch (Exception e)
        {
            Log.Error($"shader patch (clutter, {name}): {e.Message}");
            return bytes;
        }
    }

    /// <summary>The terrain base-height shader with the real elevation (Elevation), if it is switched on.</summary>
    private static byte[] PatchRealElevation(string path, byte[] bytes)
    {
        try
        {
            if (LakeOcean.PatchPrepare(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")) is not { } patched)
            {
                Log.Notice($"shader patch: {path} not patched (real terrain off or the game's shader has changed)");
                return bytes;
            }
            Log.Notice($"shader patch: {path} patched (real elevation)");
            return Encoding.UTF8.GetBytes(patched);
        }
        catch (Exception e)
        {
            Log.Error($"shader patch (real elevation): {e.Message}");
            return bytes;
        }
    }

    /// <summary>The terrain mesh shader with the lake beds (LakeOcean), if water is switched on.</summary>
    private static byte[] PatchLakeBeds(string path, byte[] bytes)
    {
        try
        {
            if (LakeOcean.PatchTerrain(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")) is not { } patched)
            {
                Log.Notice($"shader patch: {path} not patched (water off or the game's shader has changed)");
                return bytes;
            }
            Log.Notice($"shader patch: {path} patched (lake beds)");
            return Encoding.UTF8.GetBytes(patched);
        }
        catch (Exception e)
        {
            Log.Error($"shader patch (lake beds): {e.Message}");
            return bytes;
        }
    }

    /// <summary>The ocean shader with the lake pass (LakeOcean), if water is switched on.</summary>
    private static byte[] PatchOcean(string path, byte[] bytes)
    {
        try
        {
            if (LakeOcean.PatchOcean(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")) is not { } patched)
            {
                Log.Notice($"shader patch: {path} not patched (water off or the game's shader has changed)");
                return bytes;
            }
            Log.Notice($"shader patch: {path} patched (lakes, reflections {(LakeOcean.Settings?.WaterReflections == true ? "on" : "off")})");
            return Encoding.UTF8.GetBytes(patched);
        }
        catch (Exception e)
        {
            Log.Error($"shader patch (lakes): {e.Message}");
            return bytes;
        }
    }

    /// <summary>The composite shader with the colour grade (PostFx), if it is switched on.</summary>
    private static byte[] PatchComposite(string path, byte[] bytes)
    {
        try
        {
            if (PostFx.Patch(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")) is not { } patched)
                return bytes;
            Log.Notice($"shader patch: {path} patched (colour grade)");
            return Encoding.UTF8.GetBytes(patched);
        }
        catch (Exception e)
        {
            Log.Error($"shader patch (colour grade): {e.Message}");
            return bytes;
        }
    }

    /// <summary>The terrain shader with the satellite ground colour (GroundTint), if it is switched on.</summary>
    private static byte[] PatchPlanet(string path, byte[] bytes)
    {
        try
        {
            string source = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
            bool changed = false;
            if (SatelliteImagery.Settings is { Enabled: true, GroundColorFromSatellite: true } && GroundTint.Reserve())
            {
                if (GroundTint.PatchPlanet(source) is { } tinted)
                {
                    source = tinted;
                    changed = true;
                    Log.Notice($"shader patch: {path} patched (ground colour)");
                }
                else
                {
                    Log.Error($"shader patch: {path} has changed, the ground keeps the game's colours");
                    GroundTint.Fail("the game's terrain shader has changed");
                }
            }
            if (LakeOcean.PatchPlanetShore(source) is { } shores)
            {
                source = shores;
                changed = true;
                Log.Notice($"shader patch: {path} patched (lake shores)");
            }
            return changed ? Encoding.UTF8.GetBytes(source) : bytes;
        }
        catch (Exception e)
        {
            Log.Error($"shader patch (terrain colour): {e.Message}");
            return bytes;
        }
    }
}
