namespace GEE;

/// <summary>
/// Odd shapes the vector tiles only give as plain outlines, or not at all: horizontal and upright propellant tanks
/// and the payload tent of the launch complexes (StructureData, from OSM). They are placed with their own models, and the tile outlines under
/// them are dropped (Covers). Kept free of game types so it can be tested offline.
/// </summary>
public static class Structures
{
    private const double MetersPerDegree = Math.PI / 180 * 6371000;

    public static void AddTo(TileData data, int z, int x, int y)
    {
        var (south, west, north, east) = VectorTile.Bounds(z, x, y);
        for (int i = 0; i < StructureData.All.Length; i++)
        {
            var (kind, lat, lon, angle, length, width) = StructureData.All[i];
            if (lat < south || lat >= north || lon < west || lon >= east)
                continue;
            bool tent = kind == "Tent", dome = kind == "Dome", upright = kind == "VTank";
            if (upright)
            {
                // An upright round tank: Length is its diameter, Width its height; the tank code adds cap and details.
                data.Buildings.Add(new RawBuilding
                {
                    LaunchComplex = true,
                    Id = 1_100_000_000 + i,
                    Lat = lat,
                    Lon = lon,
                    Height = width,
                    Variant = Buildings.TankVariant,
                    Style = Buildings.StyleNone,
                    Boxes = { new Box2(0, 0, 0, length, length) },
                });
                continue;
            }
            data.Buildings.Add(new RawBuilding
            {
                LaunchComplex = true,
                Id = 1_100_000_000 + i,
                Lat = lat,
                Lon = lon,
                // The models are a cylinder lying down (height = diameter) and a half cylinder (height = radius);
                // a dome is a round tank with a low shell (the tank code adds the cap).
                // MinHeight lifts the model off the usual 4 m foundation, so the whole round shape stands above the ground.
                MinHeight = dome ? 0 : 0.4,
                Height = tent ? width / 2 + 0.4 : dome ? 4 : width + 0.4,
                Variant = tent ? Buildings.TentVariant : dome ? Buildings.TankVariant : Buildings.HorizontalTankVariant,
                Style = Buildings.StyleNone,
                Boxes = { new Box2(0, 0, angle * Math.PI / 180, length, width) },
            });
        }
    }

    /// <summary>True if a building outline (its centre) lies within one of the structures: it is replaced by the model.</summary>
    public static bool Covers(double lat, double lon)
    {
        foreach (var (kind, sLat, sLon, angle, length, w) in StructureData.All)
        {
            double width = kind == "VTank" ? length : w;
            double north = (lat - sLat) * MetersPerDegree;
            if (Math.Abs(north) > 40)
                continue;
            double east = (lon - sLon) * MetersPerDegree * Math.Cos(sLat * Math.PI / 180);
            double a = angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
            double along = east * cos + north * sin, across = -east * sin + north * cos;
            if (Math.Abs(along) <= length / 2 + 1.5 && Math.Abs(across) <= width / 2 + 1.5)
                return true;
        }
        return false;
    }
}
