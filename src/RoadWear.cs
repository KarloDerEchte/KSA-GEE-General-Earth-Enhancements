using System.Globalization;

namespace GEE;

/// <summary>
/// Surface variation for roads, done in the game's static object shaders so it costs no extra instances: every road
/// piece shares one texture, so a long road repeats it every piece. The renderer writes a tag per piece into the
/// unused fourth column of its model matrix (RoadWear.Tag in RoadWearTag.cs: road class + a hash of the piece), the
/// patched StaticObject.vert passes it on with the piece-local metres, and the patched StaticObject.frag (on the
/// marked road pixels, see ShaderPatch) varies brightness per piece and in patches, darkens the wheel tracks in the
/// lanes, adds oil drips between them, fresh/old repair patches, and brake marks on the stretches at junctions.
/// This file has no game types, so the patches can be checked offline with glslc.
/// </summary>
internal static partial class RoadWear
{
    public static Settings? Settings { get; set; }

    /// <summary>True once both shaders carry the patch (only then the renderer writes tags: the unpatched vertex shader
    /// would take them as perspective terms).</summary>
    public static bool Active { get; private set; }

    private const string VertMatrix = "    mat4 worldMatrix = instanceData.WorldMatrix;\n";
    private const string VertOut = "layout(location = 3) out flat int outDrawIndex;\n";
    private const string VertDraw = "    outDrawIndex = gl_DrawID;\n";
    private const string FragIn = "layout (location = 3) in flat int inDrawIndex;\n";
    private const string FragMain = "void main()\n{\n";
    private const string FragLift = "    vec3 geeShadowPos = inWorldPosition + geometricNormal * geeShadowLift;\n";

    private static bool _vertexPatched;

    private static bool Wanted => Settings is not { RoadWear: false };

    /// <summary>StaticObject.vert with the tag taken out of the matrix and passed on, or null.</summary>
    public static string? PatchVertex(string source)
    {
        if (!Wanted || !source.Contains(VertMatrix) || !source.Contains(VertOut) || !source.Contains(VertDraw))
            return null;
        source = source.Replace(VertMatrix, VertMatrix + """
                // GEE: road wear tag in the fourth column (GEE RoadWear.cs); the position's w stays 1.
                float geeTag = worldMatrix[0][3];
                worldMatrix[0][3] = 0.0;
                worldMatrix[1][3] = 0.0;
                worldMatrix[2][3] = 0.0;

            """.Replace("\r\n", "\n"))
            .Replace(VertOut, VertOut + """
            layout(location = 4) out flat float outGeeTag;
            layout(location = 5) out vec4 outGeeLocal;

            """.Replace("\r\n", "\n"))
            .Replace(VertDraw, VertDraw + """
                // Metres along the piece, metres from its -B edge, its width, and 1 on the top face.
                float geeLength = length(worldMatrix[1].xyz);
                float geeWidth = length(worldMatrix[2].xyz);
                outGeeTag = geeTag;
                outGeeLocal = vec4(inPos.y * geeLength, (inPos.z + 0.5) * geeWidth, geeWidth, inNormal.x > 0.5 ? 1.0 : 0.0);

            """.Replace("\r\n", "\n"));
        _vertexPatched = true;
        return source;
    }

    /// <summary>StaticObject.frag (already carrying ShaderPatch's lift) with the wear, or null.</summary>
    public static string? PatchFragment(string source)
    {
        if (!_vertexPatched || !Wanted || !source.Contains(FragIn) || !source.Contains(FragMain) || !source.Contains(FragLift))
            return null;
        var inv = CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("0.0###", inv);
        double strength = Settings?.RoadWearStrength ?? 1.0;
        source = source.Replace(FragIn, FragIn + """
            layout (location = 4) in flat float inGeeTag;
            layout (location = 5) in vec4 inGeeLocal;

            """.Replace("\r\n", "\n"))
            .Replace(FragMain, Functions.Replace("STRENGTH", F(strength)) + FragMain)
            .Replace(FragLift, FragLift + """
            #ifndef SAMPLE_TERRAIN
                if (geeShadowLift > 0.0)
                    geeRoadWear(inGeeTag, inGeeLocal, sampledColor, rough, ao);
            #endif

            """.Replace("\r\n", "\n"));
        Active = true;
        return source;
    }

    // Lane layouts per class (RoadWearTag.ClassOf): design width of the profile in make_assets.py ROAD_PROFILES, first lane
    // centre, lane pitch, lane count, wheel track half spacing, traffic. Class 0 = brightness only.
    private const string Functions = """
        // GEE: road surface variation (GEE RoadWear.cs).
        float geeHash(vec2 p)
        {
            p = fract(p * vec2(0.1031, 0.1030));
            p += dot(p, p.yx + 33.33);
            return fract((p.x + p.y) * p.x);
        }

        float geeNoise(vec2 p)
        {
            vec2 i = floor(p);
            vec2 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(geeHash(i), geeHash(i + vec2(1.0, 0.0)), f.x),
                       mix(geeHash(i + vec2(0.0, 1.0)), geeHash(i + vec2(1.0, 1.0)), f.x), f.y);
        }

        void geeRoadWear(float tag, vec4 loc, inout vec3 col, inout float rough, inout float ao)
        {
            if (tag < 1.0 || loc.w < 0.5)
                return;
            float cls = floor(tag);
            float seed = fract(tag);
            // design width, first lane centre, lane pitch, lanes, wheel wheel spacing, traffic, junction stretch
            vec4 lanes = vec4(0.0);
            float designW = max(loc.z, 1.0), wheel = 0.85, traffic = 1.0, junction = 0.0;
            if (cls == 2.0)       { designW = 8.0;  lanes = vec4(2.0, 4.0, 2.0, 0.0); }
            else if (cls == 3.0)  { designW = 8.0;  lanes = vec4(2.0, 4.0, 2.0, 0.0); junction = 1.0; }
            else if (cls == 4.0)  { designW = 11.0; lanes = vec4(4.375, 3.75, 2.0, 0.0); traffic = 1.25; }
            else if (cls == 5.0)  { designW = 6.0;  lanes = vec4(1.5, 3.0, 2.0, 0.0); traffic = 0.7; }
            else if (cls == 6.0)  { designW = 6.0;  lanes = vec4(3.0, 6.0, 1.0, 0.0); }
            else if (cls == 7.0)  { designW = 4.0;  lanes = vec4(2.0, 4.0, 1.0, 0.0); wheel = 1.0; traffic = 1.4; }
            else if (cls == 8.0)  { designW = 14.5; lanes = vec4(4.375, 3.75, 3.0, 0.0); traffic = 1.25; }
            else if (cls == 9.0)  { designW = 16.4; lanes = vec4(2.8, 3.6, 4.0, 0.0); traffic = 1.25; }
            float strength = STRENGTH;
            float fade = 1.0 - smoothstep(90.0, 300.0, length(inWorldPosition));
            float across = loc.y * designW / max(loc.z, 0.5);
            vec2 q = vec2(loc.x, across) + vec2(seed * 211.0, seed * 97.0);

            // Brightness: per piece, wide patches, fine mottling.
            float shade = (seed - 0.5) * 0.12
                        + (geeNoise(q * vec2(0.16, 0.45)) - 0.5) * 0.14
                        + (geeNoise(q * vec2(0.9, 0.9)) - 0.5) * 0.06 * fade;
            // Repair patch of fresher (darker) or bleached asphalt over one lane, on some pieces.
            float roll = geeHash(vec2(seed * 91.7, cls));
            if (roll > 0.82)
            {
                float lane = floor(geeHash(vec2(seed * 17.3, 3.1)) * max(lanes.z, 1.0));
                float centre = lanes.x + lane * lanes.y;
                float pitch = max(lanes.y, designW * 0.5);
                float along0 = (geeHash(vec2(seed * 5.9, 1.7)) - 0.5) * 4.0;
                float length0 = 1.5 + geeHash(vec2(seed * 3.3, 8.8)) * 3.5;
                float inside = smoothstep(0.0, 0.12, loc.x - along0) * (1.0 - smoothstep(length0 - 0.12, length0, loc.x - along0))
                             * smoothstep(0.0, 0.12, pitch * 0.5 - abs(across - centre));
                shade += inside * (roll > 0.93 ? 0.16 : -0.20);
            }
            col *= 1.0 + shade * strength;
            col = max(col, vec3(0.0));

            // Wheel tracks and oil drips in the lanes.
            if (lanes.z > 0.0 && fade > 0.0)
            {
                float lane = clamp(floor((across - lanes.x) / lanes.y + 0.5), 0.0, lanes.z - 1.0);
                float off = across - (lanes.x + lane * lanes.y);
                float inLane = 1.0 - smoothstep(lanes.y * 0.5 - 0.15, lanes.y * 0.5, abs(off));
                float busy = traffic * (0.55 + 0.9 * geeHash(vec2(seed * 53.1, lane + 1.0)));
                float broken = 0.55 + 0.45 * geeNoise(vec2(loc.x * 0.22 + seed * 40.0, lane * 7.3));
                float track = exp(-pow((abs(off) - wheel) / 0.26, 2.0)) * inLane * busy * broken * (1.0 + junction * 0.5);
                col *= 1.0 - 0.17 * clamp(track, 0.0, 1.0) * fade * strength;
                rough *= 1.0 - 0.12 * clamp(track, 0.0, 1.0) * fade * strength;
                // Oil along the lane centre where a drip line has formed.
                float drip = smoothstep(0.62, 0.78, geeNoise(vec2(loc.x * 0.45 + seed * 77.0, lane * 5.1 + 2.0)));
                float oil = exp(-pow(off / 0.14, 2.0)) * drip * inLane * busy;
                col *= 1.0 - 0.34 * clamp(oil, 0.0, 1.0) * fade * strength;
                rough *= 1.0 - 0.30 * clamp(oil, 0.0, 1.0) * fade * strength;
                ao *= 1.0 - 0.1 * clamp(oil, 0.0, 1.0) * fade * strength;
                // Rubber and brake marks: mostly where roads meet, rarely elsewhere.
                float skidRoll = geeHash(vec2(seed * 29.3, 6.6));
                if (skidRoll > (junction > 0.5 ? 0.35 : 0.96))
                {
                    float lane2 = floor(geeHash(vec2(seed * 7.7, 2.2)) * max(lanes.z, 1.0));
                    float c2 = lanes.x + lane2 * lanes.y + (geeHash(vec2(seed * 3.1, 9.9)) - 0.5) * 0.6;
                    float u = (loc.x + 8.0 * (seed - 0.5)) / 6.0 + 0.5;
                    float envelope = smoothstep(0.0, 0.08, u) * (1.0 - smoothstep(0.35, 1.0, u)) * step(0.0, u) * step(u, 1.0);
                    float lateral = exp(-pow((abs(across - c2) - 0.8) / 0.11, 2.0));
                    float mark = envelope * lateral * (0.6 + 0.4 * geeNoise(vec2(loc.x * 3.0, across * 6.0)));
                    col *= 1.0 - 0.42 * mark * fade * strength;
                    rough *= 1.0 - 0.15 * mark * fade * strength;
                }
            }
        }

        """;
}
