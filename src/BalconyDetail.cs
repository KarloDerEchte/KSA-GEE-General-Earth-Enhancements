using Brutal.Numerics;

namespace GEE;

/// <summary>
/// Real balconies (slab plus railing pieces, IsDetail, so only near the camera) on the bays the facade shader paints
/// balconies on. The decisions repeat FacadeExtras.balconyDarkening bit for bit (same hash, same bay fitting), so
/// the geometry sits exactly where the painted slab and railing are.
/// </summary>
internal static class BalconyDetail
{
    private const int MaxPieces = 48, Concrete = 0, Dark = 4;

    // Same integer hash as the GLSL hash/random in FacadeTechnique.
    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
        return x;
    }

    internal static float Random(uint a, uint b, uint c) => (Hash(a ^ Hash(b ^ Hash(unchecked(c + 0x9e3779b9u)))) >> 8) / 16777216f;

    /// <summary>Does this wall (0..3 as in the vertex shader) of the building carry balconies, at all?</summary>
    internal static bool WallHasBalconies(uint seed, uint wall, double width, double floors) =>
        floors >= 3 && width >= 8 && Random(seed, wall, 12u) < FacadeExtras.WallShare;

    /// <summary>Is bay i of the wall a balcony column?</summary>
    internal static bool BayHasBalcony(uint seed, uint wall, int bay) => Random(seed, (uint)bay + wall * 256u, 11u) < FacadeExtras.BaySharePct;

    /// <summary>The shader's grid for housing walls: floor height 2.9, bays of about 3.3 m fitted to the width.</summary>
    internal static double BayWidth(double width) => width / Math.Max(Math.Floor(width / 3.3), 1);

    public static void Add(Tile tile, in BuildingInstance wall, RawBuilding building)
    {
        if (wall.Style != Buildings.StyleResidential || !Buildings.IsFacade(wall.Variant) || wall.IsRoad || wall.IsDetail
            || building.Slab || building.MinHeight > 0)
            return;
        const double floorH = 2.9, depth = 1.0;
        double la = Length(wall.AxisA), lb = Length(wall.AxisB);
        double sizeY = Length(wall.Up) - wall.GroundOffset;
        double floors = Math.Floor((sizeY - 0.6) / floorH);
        if (floors < 3 || floors > 12)
            return;
        double3 up = double3.Normalize(wall.Up);
        double3 a = wall.AxisA / la, b = wall.AxisB / lb;
        double3 ground = wall.Base + up * wall.GroundOffset;
        uint seed = (uint)(wall.Seed & 0xFFFFFF);
        int pieces = 0;
        double3 center = wall.Center;

        // Box centred on origin (bottom), sn thick along the wall normal, sr along the wall; always with the wall's own
        // axes (a, b) so the winding stays right-handed.
        void Piece(int variant, double3 origin, uint wallIndex, double sn, double sr, double h)
        {
            tile.Add(new BuildingInstance
            {
                Up = up * h, AxisA = a * (wallIndex < 2 ? sn : sr), AxisB = b * (wallIndex < 2 ? sr : sn), Base = origin, Center = center,
                Variant = variant, Style = Buildings.StyleNone, IsDetail = true,
            });
            pieces++;
        }

        for (uint w = 0; w < 4 && pieces < MaxPieces; w++)
        {
            double side = (w & 1) == 0 ? 1 : -1;
            double3 normal = (w < 2 ? a : b) * side;
            double3 run = w < 2 ? b : a;
            double width = w < 2 ? lb : la;
            double faceDistance = (w < 2 ? la : lb) / 2;
            if (!WallHasBalconies(seed, w, width, floors))
                continue;
            double bayW = BayWidth(width);
            int bays = (int)Math.Max(Math.Floor(width / 3.3), 1);
            for (int bay = 0; bay < bays && pieces < MaxPieces; bay++)
            {
                if (!BayHasBalcony(seed, w, bay))
                    continue;
                // Position along the run axis (the shader's x runs against it on the odd walls).
                double s = ((bay + 0.5) * bayW - width / 2) * side;
                double bw = bayW * 0.8;
                for (int fl = 1; fl < floors && pieces <= MaxPieces - 3; fl++)
                {
                    double3 slabCentre = ground + normal * (faceDistance + depth / 2) + run * s + up * (fl * floorH);
                    // Slab 20 cm thick, railing 1 m high on the outer edge and 6 cm thick posts-and-panel on the sides.
                    Piece(Concrete, slabCentre, w, depth, bw, 0.2);
                    double3 top = slabCentre + up * 0.2;
                    Piece(Dark, top + normal * (depth / 2 - 0.03), w, 0.06, bw, 0.9);
                    Piece(Dark, top + run * (bw / 2 - 0.03), w, depth, 0.06, 0.9);
                    Piece(Dark, top - run * (bw / 2 - 0.03), w, depth, 0.06, 0.9);
                }
            }
        }
    }

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));
}
