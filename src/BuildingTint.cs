using System.Globalization;

namespace GEE;

/// <summary>
/// Per-building colour variation for generic facades without extra materials: the renderer writes a tag per instance
/// into the unused fourth column of the model matrix (the road wear mechanism, RoadWear.cs), and the patched
/// StaticObject.frag multiplies the sampled diffuse colour by a small hue/brightness jitter derived from it. The tag
/// is in (0, 1) (roads' tags are 1 and up and are told apart by the road marker in the alpha; negative tags made the game drop the boxes). No game types here: testable offline.
/// </summary>
internal static class BuildingTint
{
    public static Settings? Settings { get; set; }

    /// <summary>The setting; the shader patch is baked at game start.</summary>
    public static bool Wanted => Settings is not { BuildingTint: false };

    /// <summary>
    /// Tag of a building instance: 0.01 + f, f in [0, 0.98) hashed from the building seed (all boxes of one building
    /// share it), or 0 for anything that is not a plain facade box (roads, details, landmarks, launch complex halls).
    /// </summary>
    public static float Tag(in BuildingInstance b)
    {
        if (!RoadWear.Active || !Wanted || b.IsRoad || b.IsDetail || !Buildings.IsFacade(b.Variant)
            || b.Style == Buildings.StyleMetalHall || b.Style == Buildings.StyleShelter)
            return 0;
        return TagFromSeed(b.Seed);
    }

    public static float TagFromSeed(uint seed)
    {
        unchecked
        {
            uint h = seed * 0x9e3779b1u + 0x85ebca6bu;
            h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
            return 0.01f + (h & 0xFFFFu) / 65536f * 0.98f;
        }
    }

    public static string Functions(double strength)
    {
        string s = strength.ToString("0.0###", CultureInfo.InvariantCulture);
        return Template.Replace("STRENGTH", s);
    }

    private const string Template = """
        // GEE: per-building facade tint (GEE BuildingTint.cs): chroma tilt (+-8 %) and brightness (+-9 %) from the tag.
        float geeTintHash(float x)
        {
            return fract(sin(x * 127.1 + 311.7) * 43758.5453);
        }

        void geeBuildingTint(float tag, inout vec3 col)
        {
            float f = tag;
            vec3 c = vec3(geeTintHash(f * 91.3 + 1.7), geeTintHash(f * 57.9 + 5.3), geeTintHash(f * 13.1 + 9.1)) - 0.5;
            c -= (c.x + c.y + c.z) / 3.0;
            float b = geeTintHash(f * 31.7 + 2.9) - 0.5;
            col *= (1.0 + c * 0.32 * STRENGTH) * (1.0 + b * 0.18 * STRENGTH);
        }

        """;
}
