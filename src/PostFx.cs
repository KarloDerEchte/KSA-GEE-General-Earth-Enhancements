using System.Globalization;

namespace GEE;

/// <summary>
/// Colour grading after the game's tonemapper: the game's composite pass (composite.frag) only tonemaps. This patches
/// its text as it is read, so the grade runs in the same pass and costs next to nothing. SDR output only (HDR output
/// is in nits and is left alone). The numbers are baked into the shader, so changes take effect at the next game start.
/// </summary>
internal static class PostFx
{
    public static Settings? Settings { get; set; }

    private const string Anchor = "    outFragColor = vec4(color, 1.0f);";

    /// <summary>The composite shader with the grade, or null if it is switched off or the shader has changed.</summary>
    public static string? Patch(string source)
    {
        if (Settings is not { PostGrading: true } s || !source.Contains(Anchor + "\n"))
            return null;
        var inv = CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("0.0###", inv);
        // Neutral steps are left out of the shader text altogether (their constants would not fold away exactly).
        bool Zero(double v) => Math.Abs(v) < 1e-6;
        var sb = new System.Text.StringBuilder();
        sb.Append("    // GEE: colour grade after the tonemapper (GEE PostFx.cs).\n    if (outputMode == COMPOSITE_OUTPUT_SDR)\n    {\n        color = clamp(color, 0.0, 1.0);\n");
        if (!Zero(s.GradeContrast))
            sb.Append($"        // S-curve about mid grey: deeper shadows, brighter highlights\n        color = mix(color, color * color * (3.0 - 2.0 * color), {F(s.GradeContrast)});\n");
        if (!Zero(s.GradeSaturation - 1.0) || !Zero(s.GradeVibrance))
            sb.Append($"        // saturation, with extra for dull colours (vibrance)\n        float geeLuma = dot(color, vec3(0.2126, 0.7152, 0.0722));\n"
                + (Zero(s.GradeVibrance)
                    ? $"        color = mix(vec3(geeLuma), color, {F(s.GradeSaturation)});\n"
                    : $"        float geeSat = max(color.r, max(color.g, color.b)) - min(color.r, min(color.g, color.b));\n        color = mix(vec3(geeLuma), color, {F(s.GradeSaturation)} + {F(s.GradeVibrance)} * (1.0 - geeSat));\n"));
        if (!Zero(s.GradeWarmth))
            sb.Append($"        color *= vec3({F(1.0 + s.GradeWarmth * 0.06)}, 1.0, {F(1.0 - s.GradeWarmth * 0.06)});\n");
        if (!Zero(s.GradeVignette))
            sb.Append($"        // darker corners\n        vec2 geeOffset = inUV - 0.5;\n        color *= 1.0 - {F(s.GradeVignette * 2.0)} * dot(geeOffset, geeOffset);\n");
        sb.Append("        color = clamp(color, 0.0, 1.0);\n    }\n\n");
        string grade = sb.ToString();
        return source.Replace(Anchor + "\n", grade + Anchor + "\n");
    }
}
