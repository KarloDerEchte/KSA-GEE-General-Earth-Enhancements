using System.Globalization;

namespace GEE;

/// <summary>
/// More ground detail in the terrain shader (Planet.frag), added next to GroundTint's satellite colour:
/// 1. the game's ground texture of the dominant material is sampled once more with hex-tile blending (random offset
///    per hexagon, variance-preserving blend, a scale that does not repeat with the game's own) and the difference to
///    the texture's mean modulates the colour, which breaks up the repetition of the game's tiling close to the ground;
/// 2. two groups of value noise (about 14-450 m) vary brightness and hue across the ground, fading out where a
///    wavelength gets smaller than a few pixels and far away;
/// 3. steep ground turns to dirt and rock according to the surface normal;
/// 4. creases (concave places in the shading normal) are darkened.
/// All of it is fixed work per pixel: no loops, at most four texture fetches (and only within ~450 m of the camera).
/// The settings are baked into the shader text, so changes need a game restart.
/// </summary>
internal static class GroundDetail
{
    public static Settings? Settings { get; set; }

    private const string IncludeAnchor = "#include \"../Common/RingShadows.glsl\"\n";
    private const string SurfaceAnchor = "    vec3 surfaceColor = blendedDiffuse.xyz;\n";

    /// <summary>The shader text with our code in front of the lighting, or null if switched off or the shader has changed.</summary>
    public static string? Patch(string source)
    {
        if (Settings is not { GroundDetail: true } s || source.Split(IncludeAnchor).Length != 2 || source.Split(SurfaceAnchor).Length != 2)
            return null;
        string F(double v) => Math.Clamp(v, 0, 2).ToString("0.0###", CultureInfo.InvariantCulture);
        string consts = $"const float GEE_D_STOCH = {F(s.GroundDetailStrength)}, GEE_D_MACRO = {F(s.GroundMacroStrength)}, "
            + $"GEE_D_SLOPE = {F(s.GroundSlopeStrength)}, GEE_D_CAVITY = {F(s.GroundCavityStrength)};\n";
        return source
            .Replace(IncludeAnchor, IncludeAnchor + Functions.Replace("GEE_D_CONSTS", consts.TrimEnd('\n')))
            .Replace(SurfaceAnchor, SurfaceCode + SurfaceAnchor);
    }

    private const string Functions = """

        // ---- GEE: ground detail (mods/GEE GroundDetail.cs) ----
        GEE_D_CONSTS
        float geeDetailHash(vec3 p)
        {
            p = fract(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return fract((p.x + p.y) * p.z);
        }

        vec2 geeDetailHash2(vec2 p)
        {
            vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
            q += dot(q, q.yzx + 33.33);
            return fract((q.xx + q.yz) * q.zy);
        }

        float geeDetailNoise(vec3 p)
        {
            vec3 i = floor(p);
            vec3 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = mix(mix(geeDetailHash(i), geeDetailHash(i + vec3(1, 0, 0)), f.x),
                          mix(geeDetailHash(i + vec3(0, 1, 0)), geeDetailHash(i + vec3(1, 1, 0)), f.x), f.y);
            float b = mix(mix(geeDetailHash(i + vec3(0, 0, 1)), geeDetailHash(i + vec3(1, 0, 1)), f.x),
                          mix(geeDetailHash(i + vec3(0, 1, 1)), geeDetailHash(i + vec3(1, 1, 1)), f.x), f.y);
            return mix(a, b, f.z);
        }

        // Share of an octave of wavelength lambda (m) that is still wider than a few pixels of size foot (m).
        float geeDetailAlias(float lambda, float foot)
        {
            return clamp((lambda / max(foot, 1e-3) - 4.0) / 8.0, 0.0, 1.0);
        }

        // Hex-tile grid: three vertices (cell ids) with barycentric weights.
        void geeDetailHex(vec2 uv, out vec2 v0, out vec2 v1, out vec2 v2, out vec3 w)
        {
            vec2 st = vec2(uv.x, -0.57735027 * uv.x + 1.15470054 * uv.y) * 3.4641016;
            vec2 id = floor(st);
            vec3 g = vec3(fract(st), 0.0);
            g.z = 1.0 - g.x - g.y;
            if (g.z > 0.0)
            {
                w = vec3(g.z, g.y, g.x);
                v0 = id; v1 = id + vec2(0, 1); v2 = id + vec2(1, 0);
            }
            else
            {
                w = vec3(-g.z, 1.0 - g.y, 1.0 - g.x);
                v0 = id + vec2(1, 1); v1 = id + vec2(1, 0); v2 = id + vec2(0, 1);
            }
        }
        // ---- end GEE ground detail ----

        """;

    private const string SurfaceCode = """
            // GEE: ground detail (mods/GEE GroundDetail.cs): non-tiling texture detail, macro variation,
            // slope materials, creases.
            {
                const vec3 geeDL = vec3(0.2126, 0.7152, 0.0722);
                float geeDAvg = max(uboPlanet.meanDiffuseLuminosity, 1e-3);
                float geeDFoot = max(length(biplanarInfo.ddxWorld), length(biplanarInfo.ddyWorld));
                // Derivatives need uniform control flow: taken before any branch.
                vec3 geeDPx = dFdx(inWorldPosition.xyz), geeDPy = dFdy(inWorldPosition.xyz);
                vec3 geeDNx = dFdx(worldNormal), geeDNy = dFdy(worldNormal);
                float geeDLum = dot(blendedDiffuse, geeDL);

                // 1. the game's ground texture again, without its tiling
                float geeDNear = (1.0 - smoothstep(120.0, 450.0, distanceToTerrain)) * detailStrength;
                if (GEE_D_STOCH > 0.0 && geeDNear > 0.01)
                {
                    BiomeMaterial geeMat = biomeMaterialBuffer.biomeMaterials[topMaterialIds[0]];
                    ivec3 geeAx = biplanarInfo.majorAxis;
                    float geeTiling = geeMat.textureTiling * tilingFactor.y * 1.7;
                    vec2 geeUv = vec2(texturePos[geeAx.y], texturePos[geeAx.z]) * geeTiling;
                    vec2 geeDdx = vec2(biplanarInfo.ddxWorld[geeAx.y], biplanarInfo.ddxWorld[geeAx.z]) * geeTiling;
                    vec2 geeDdy = vec2(biplanarInfo.ddyWorld[geeAx.y], biplanarInfo.ddyWorld[geeAx.z]) * geeTiling;
                    vec2 geeV0, geeV1, geeV2;
                    vec3 geeW;
                    geeDetailHex(geeUv, geeV0, geeV1, geeV2, geeW);
                    vec3 geeMean = textureLod(sampler2D(globalTextures[nonuniformEXT(geeMat.diffuseId)], linearRepeatSampler), geeUv, 12.0).rgb;
                    vec3 geeT0 = textureGrad(sampler2D(globalTextures[nonuniformEXT(geeMat.diffuseId)], linearRepeatSampler), geeUv + geeDetailHash2(geeV0), geeDdx, geeDdy).rgb;
                    vec3 geeT1 = textureGrad(sampler2D(globalTextures[nonuniformEXT(geeMat.diffuseId)], linearRepeatSampler), geeUv + geeDetailHash2(geeV1), geeDdx, geeDdy).rgb;
                    vec3 geeT2 = textureGrad(sampler2D(globalTextures[nonuniformEXT(geeMat.diffuseId)], linearRepeatSampler), geeUv + geeDetailHash2(geeV2), geeDdx, geeDdy).rgb;
                    // Variance-preserving blend: a plain mix of three random tiles would look washed out.
                    vec3 geeDev = (geeW.x * (geeT0 - geeMean) + geeW.y * (geeT1 - geeMean) + geeW.z * (geeT2 - geeMean))
                        * inversesqrt(dot(geeW, geeW));
                    float geeDelta = dot(geeDev, geeDL) * 2.0 * geeMat.diffuseScale;
                    blendedDiffuse *= clamp(1.0 + GEE_D_STOCH * geeDNear * geeDelta, 0.5, 1.8);
                }

                // 2. macro variation of brightness and hue
                float geeDFar = 1.0 - smoothstep(8000.0, 40000.0, distanceToTerrain);
                if (GEE_D_MACRO > 0.0 && geeDFar > 0.0)
                {
                    vec3 geeQ = texturePos;
                    float geeN4 = (geeDetailNoise(geeQ / 450.0) - 0.5) * geeDetailAlias(450.0, geeDFoot);
                    float geeN1 = (geeDetailNoise(geeQ / 160.0 + 11.0) - 0.5) * geeDetailAlias(160.0, geeDFoot);
                    float geeN2 = (geeDetailNoise(geeQ / 48.0 + 23.0) - 0.5) * geeDetailAlias(48.0, geeDFoot);
                    float geeN3 = (geeDetailNoise(geeQ / 14.0 + 37.0) - 0.5) * geeDetailAlias(14.0, geeDFoot);
                    float geeBright = 0.9 * geeN4 + 0.8 * geeN1 + 0.5 * geeN2 + 0.35 * geeN3;
                    float geeHue = (geeDetailNoise(geeQ / 90.0 + 59.0) - 0.5) * geeDetailAlias(90.0, geeDFoot);
                    blendedDiffuse *= exp(GEE_D_MACRO * geeDFar * geeBright)
                        * vec3(1.0 + 0.25 * GEE_D_MACRO * geeHue * geeDFar, 1.0, 1.0 - 0.25 * GEE_D_MACRO * geeHue * geeDFar);
                }

                // 3. dirt and rock on steep ground (not on bright ground: snow, sand)
                if (GEE_D_SLOPE > 0.0 && geeDFar > 0.0)
                {
                    float geeC = dot(worldNormal, sphereWorldNormal)
                        + 0.08 * (geeDetailNoise(texturePos / 30.0 + 71.0) - 0.5) * geeDetailAlias(30.0, geeDFoot);
                    float geeDirt = 1.0 - smoothstep(0.80, 0.94, geeC);
                    float geeRock = 1.0 - smoothstep(0.62, 0.82, geeC);
                    float geeGuard = (1.0 - smoothstep(0.5, 0.75, geeDLum * geeDAvg)) * geeDFar * GEE_D_SLOPE;
                    // Rock gets lighter, greyer and uneven; dirt warmer. The game's own brightness stays the reference.
                    float geeRockNoise = geeDetailNoise(texturePos / 3.0 + 5.0) * geeDetailAlias(3.0, geeDFoot) + 0.5 * (1.0 - geeDetailAlias(3.0, geeDFoot));
                    vec3 geeDirtCol = vec3(geeDLum) * vec3(1.45, 1.1, 0.8) * 0.95;
                    vec3 geeRockCol = vec3(geeDLum) * vec3(1.2, 1.1, 1.0) * 1.5 * (0.7 + 0.6 * geeRockNoise);
                    blendedDiffuse = mix(blendedDiffuse, geeDirtCol, geeDirt * geeGuard);
                    blendedDiffuse = mix(blendedDiffuse, geeRockCol, geeRock * geeGuard);
                }

                // 4. darker creases: the shading normal diverges less than a flat surface where the ground is concave
                if (GEE_D_CAVITY > 0.0)
                {
                    float geeCurv = dot(geeDNx, geeDPx) / max(dot(geeDPx, geeDPx), 1e-6)
                        + dot(geeDNy, geeDPy) / max(dot(geeDPy, geeDPy), 1e-6);
                    float geeCav = smoothstep(0.05, 1.0, -geeCurv) * (1.0 - smoothstep(60.0, 200.0, distanceToTerrain));
                    blendedDiffuse *= 1.0 - 0.5 * GEE_D_CAVITY * geeCav;
                }
            }

        """;
}
