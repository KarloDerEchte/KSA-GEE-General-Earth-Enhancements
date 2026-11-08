namespace GEE;

internal static partial class RoadWear
{
    /// <summary>
    /// The wear tag of a road piece: class + 1 (integer part) and a hash of the piece's position (fraction), stable
    /// from frame to frame. 0 when the piece gets no wear (not a road, or the shader patch is not active).
    /// </summary>
    public static float Tag(in BuildingInstance b)
    {
        if (!Active || !Wanted || !b.IsRoad || b.IsDetail)
            return 0;
        int cls = ClassOf(b.Variant);
        unchecked
        {
            uint h = (uint)(long)Math.Floor(b.Center.X * 0.5) * 73856093u
                   ^ (uint)(long)Math.Floor(b.Center.Y * 0.5) * 19349663u
                   ^ (uint)(long)Math.Floor(b.Center.Z * 0.5) * 83492791u;
            h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
            return cls + 1 + (h & 0xFFFFu) / 65536f * 0.99f;
        }
    }

    /// <summary>Lane layout class used by the shader's geeRoadWear (0 = brightness only), see RoadWear.Functions.</summary>
    private static int ClassOf(int v)
    {
        if (v == Buildings.RoadVariant) return 1;
        if (v == Buildings.RoadPlainVariant) return 2;
        if (v == Buildings.HighwayVariant) return 3;
        if (v == Buildings.StreetVariant) return 4;
        if (v == Buildings.RampVariant || v == Buildings.GoreVariant) return 5;
        if (v == Buildings.TrackVariant) return 6;
        if (v == Buildings.Highway3Variant) return 7;
        if (v == Buildings.Highway4Variant) return 8;
        return 0;
    }
}
