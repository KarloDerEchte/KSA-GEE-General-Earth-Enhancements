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
            string lift = $$"""
                    // GEE: shadow lookup above the mod's ground surfaces (GEE ShaderPatch.cs).
                    float geeShadowLift = 0.0;
                #ifndef SAMPLE_TERRAIN
                    if (abs(texture(sampler2D(globalTextures[drawData.diffuseTextureIndex], textureSampler), inUv).a - {{(DiffuseMarker / 255.0).ToString("0.0000", inv)}}) < 0.03
                        && abs(texture(sampler2D(globalTextures[drawData.pbrTextureIndex], textureSampler), inUv).a - {{(PbrMarker / 255.0).ToString("0.0000", inv)}}) < 0.03)
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

    /// <summary>The terrain shader with the satellite ground colour (GroundTint), if it is switched on.</summary>
    private static byte[] PatchPlanet(string path, byte[] bytes)
    {
        try
        {
            if (SatelliteImagery.Settings is not { Enabled: true, GroundColorFromSatellite: true } || !GroundTint.Reserve())
                return bytes;
            string source = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
            if (GroundTint.PatchPlanet(source) is not { } patched)
            {
                Log.Error($"shader patch: {path} has changed, the ground keeps the game's colours");
                GroundTint.Fail("the game's terrain shader has changed");
                return bytes;
            }
            Log.Notice($"shader patch: {path} patched (ground colour)");
            return Encoding.UTF8.GetBytes(patched);
        }
        catch (Exception e)
        {
            Log.Error($"shader patch (ground colour): {e.Message}");
            return bytes;
        }
    }
}
