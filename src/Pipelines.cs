namespace OsmBuildings;

/// <summary>
/// The overground pipelines of Launch Complex 39A (PipelineData, from OSM): the vector tiles carry no pipelines, so
/// they are laid as short insulated pipe boxes on posts, each piece belonging to the tile its middle falls in.
/// Kept free of game types so it can be tested offline.
/// </summary>
public static class Pipelines
{
    private const double PieceMeters = 10, PipeWidth = 0.9, PipeHeight = 1.3, Clearance = 1.2;
    private const double MetersPerDegree = Math.PI / 180 * 6371000;

    public static void AddTo(TileData data, int z, int x, int y)
    {
        var (south, west, north, east) = VectorTile.Bounds(z, x, y);
        if (south > 28.62 || north < 28.59 || west > -80.59 || east < -80.62)
            return;
        long id = 1_000_000_000;
        foreach (double[] line in PipelineData.Lines)
        {
            for (int i = 0; i + 3 < line.Length; i += 2)
            {
                double lat0 = line[i], lon0 = line[i + 1], lat1 = line[i + 2], lon1 = line[i + 3];
                double cos = Math.Cos((lat0 + lat1) / 2 * Math.PI / 180);
                double dEast = (lon1 - lon0) * MetersPerDegree * cos, dNorth = (lat1 - lat0) * MetersPerDegree;
                double length = Math.Sqrt(dEast * dEast + dNorth * dNorth);
                if (length < 0.5)
                    continue;
                int pieces = Math.Max((int)Math.Ceiling(length / PieceMeters), 1);
                double angle = Math.Atan2(dNorth, dEast);
                for (int p = 0; p < pieces; p++, id += 2)
                {
                    double t = (p + 0.5) / pieces;
                    double lat = lat0 + (lat1 - lat0) * t, lon = lon0 + (lon1 - lon0) * t;
                    if (lat < south || lat >= north || lon < west || lon >= east)
                        continue;
                    // The pipe, floating on its posts (a hair longer than its piece, so pieces meet)...
                    data.Buildings.Add(new RawBuilding
                    {
                        LaunchComplex = true,
                        Id = id,
                        Lat = lat,
                        Lon = lon,
                        Height = Clearance + PipeHeight,
                        MinHeight = Clearance,
                        Variant = 6,
                        Style = Buildings.StyleNone,
                        Boxes = { new Box2(0, 0, angle, length / pieces + 0.1, PipeWidth) },
                    });
                    // ...and its post.
                    data.Buildings.Add(new RawBuilding
                    {
                        LaunchComplex = true,
                        Id = id + 1,
                        Lat = lat,
                        Lon = lon,
                        Height = 1,    // placement lifts every box to at least 2 m above the ground
                        Variant = 4,
                        Style = Buildings.StyleNone,
                        Boxes = { new Box2(0, 0, angle, 0.35, 0.35) },
                    });
                }
            }
        }
    }
}
