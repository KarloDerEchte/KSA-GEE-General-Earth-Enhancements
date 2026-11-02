using Brutal.Numerics;

namespace OsmBuildings;

/// <summary>
/// Real-world relief on top of a building's plain box, made of more instances of the same unit boxes: a plinth at
/// the foot, an entrance with a canopy, on flat roofs a cornice, a parapet, plant units and a stair housing, on
/// pitched roofs a chimney. Drawn only near the camera (BuildingInstance.IsDetail, Settings.DetailDistanceKm).
/// </summary>
internal static class BuildingDetail
{
    private const int Concrete = 0, Brick = 3, Dark = 4, Metal = 6;

    public static void Add(Tile tile, in BuildingInstance wall, RawBuilding building)
    {
        if (wall.Variant == Buildings.TankVariant && !wall.IsDetail)
        {
            AddTank(tile, wall);
            return;
        }
        if (wall.Variant >= Buildings.RoadVariant || wall.IsRoad || wall.IsDetail || building.Slab || building.MinHeight > 0)
            return;
        double3 up = double3.Normalize(wall.Up);
        double la = Length(wall.AxisA), lb = Length(wall.AxisB);
        double height = Length(wall.Up) - wall.GroundOffset;
        if (la < 4 || lb < 4 || height < 2.4)
            return;
        double3 a = wall.AxisA / la, b = wall.AxisB / lb;
        double3 roof = wall.Base + wall.Up;
        double3 center = wall.Center, wallBase = wall.Base;
        double groundOffset = wall.GroundOffset;
        uint h = LightSet.Hash(wall.Seed, 17);
        double Rand(int k) => (LightSet.Hash(h + (uint)k * 7919u) >> 8) / 16777216.0;
        bool flat = building.RoofHeight <= 0;
        double area = la * lb;

        void Piece(int variant, double3 origin, double sa, double sb, double3 upVector)
        {
            tile.Add(new BuildingInstance
            {
                Up = upVector,
                AxisA = a * sa,
                AxisB = b * sb,
                Base = origin,
                Center = center,
                Variant = variant,
                Style = Buildings.StyleNone,
                IsDetail = true,
            });
        }
        double3 At(double3 origin, double ca, double cb, double rise) => origin + a * ca + b * cb + up * rise;

        // Plinth: a dark concrete step around the foot, about 0.8 m above the ground.
        Piece(Dark, wallBase, la + 0.3, lb + 0.3, up * (groundOffset + 0.8));

        // Entrance on one of the long sides: door and a canopy over it.
        if (area >= 60)
        {
            bool alongA = la >= lb;
            double sideSign = Rand(1) < 0.5 ? -1 : 1;
            double run = (alongA ? la : lb) / 2 - 2.2;
            double along = (Rand(2) * 2 - 1) * Math.Max(run, 0);
            double3 ground = wallBase + up * groundOffset;
            double faceDistance = (alongA ? lb : la) / 2;
            double ca = alongA ? along : sideSign * faceDistance;
            double cb = alongA ? sideSign * faceDistance : along;
            // Door leaf (slightly proud of the wall) and canopy.
            double3 doorOrigin = At(ground, ca, cb, 0);
            if (alongA)
            {
                Piece(Dark, doorOrigin, 1.9, 0.5, up * 2.2);
                Piece(Concrete, At(ground, ca, cb + sideSign * 0.6, 2.35), 3.6, 1.7, up * 0.22);
            }
            else
            {
                Piece(Dark, doorOrigin, 0.5, 1.9, up * 2.2);
                Piece(Concrete, At(ground, ca + sideSign * 0.6, cb, 2.35), 1.7, 3.6, up * 0.22);
            }
        }

        if (!flat)
        {
            // Chimney on the ridge of a pitched roof.
            if (area >= 40 && building.RoofVariant != Buildings.MetalRoofVariant)
            {
                bool ridgeAlongA = la >= lb;
                double along = (Rand(3) < 0.5 ? -1 : 1) * (ridgeAlongA ? la : lb) * 0.25;
                double3 origin = ridgeAlongA ? At(roof, along, 0, 0) : At(roof, 0, along, 0);
                Piece(Brick, origin, 0.8, 0.8, up * (building.RoofHeight + 0.9));
            }
            return;
        }

        // Cornice, a band that overhangs the wall top by 0.3 m, and the parapet above it.
        if (height >= 5)
            Piece(building.Variant, roof - up * 0.45, la + 0.6, lb + 0.6, up * 0.45);
        if (area >= 40)
        {
            const double t = 0.3, ph = 0.75;
            Piece(building.Variant, At(roof, 0, lb / 2 - t / 2, 0), la, t, up * ph);
            Piece(building.Variant, At(roof, 0, -lb / 2 + t / 2, 0), la, t, up * ph);
            Piece(building.Variant, At(roof, la / 2 - t / 2, 0, 0), t, lb - 2 * t, up * ph);
            Piece(building.Variant, At(roof, -la / 2 + t / 2, 0, 0), t, lb - 2 * t, up * ph);
        }

        // Plant units and a stair housing on bigger roofs.
        if (area >= 120)
        {
            int units = (int)Math.Min(1 + area / 500, 5);
            for (int k = 0; k < units; k++)
            {
                double w = 1.8 + 2.6 * Rand(10 + k), d = 1.4 + 1.8 * Rand(20 + k), hh = 0.9 + 1.4 * Rand(30 + k);
                double ca = (Rand(40 + k) - 0.5) * Math.Max(la - w - 3, 0), cb = (Rand(50 + k) - 0.5) * Math.Max(lb - d - 3, 0);
                Piece(Metal, At(roof, ca, cb, 0), w, d, up * hh);
            }
            if (height > 9)
            {
                double ca = (Rand(60) < 0.5 ? -1 : 1) * Math.Max(la / 2 - 4, 0), cb = (Rand(61) < 0.5 ? -1 : 1) * Math.Max(lb / 2 - 4, 0);
                Piece(Concrete, At(roof, ca, cb, 0), 3.4, 3.4, up * 2.8);
            }
        }
    }

    /// <summary>Round tank: foot ring, insulation bands, a ladder and a pipe up the side.</summary>
    private static void AddTank(Tile tile, in BuildingInstance wall)
    {
        double3 up = double3.Normalize(wall.Up);
        double d = Length(wall.AxisA), height = Length(wall.Up) - wall.GroundOffset;
        if (d < 3 || height < 3)
            return;
        double3 a = wall.AxisA / d, b = wall.AxisB / Length(wall.AxisB);
        double3 ground = wall.Base + up * wall.GroundOffset;
        double3 center = wall.Center, wallBase = wall.Base;
        double groundOffset = wall.GroundOffset;

        void Piece(int variant, double3 origin, double sa, double sb, double3 upVector)
        {
            tile.Add(new BuildingInstance
            {
                Up = upVector, AxisA = a * sa, AxisB = b * sb, Base = origin, Center = center,
                Variant = variant, Style = Buildings.StyleNone, IsDetail = true,
            });
        }
        // Concrete foot ring, then bands every few metres, each a little wider than the shell.
        Piece(Buildings.TankVariant, wallBase, d + 1.2, d + 1.2, up * (groundOffset + 0.5));
        for (double h = 3.5; h < height - 1.5; h += 3.5)
            Piece(Buildings.TankVariant, ground + up * h, d + 0.22, d + 0.22, up * 0.3);
        // Ladder cage and a fill pipe on opposite sides.
        Piece(Metal, ground + a * (d / 2 + 0.12), 0.3, 0.9, up * (height + 0.9));
        Piece(Metal, ground - a * (d / 2 + 0.15), 0.4, 0.4, up * (height * 0.8));
    }

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));
}
