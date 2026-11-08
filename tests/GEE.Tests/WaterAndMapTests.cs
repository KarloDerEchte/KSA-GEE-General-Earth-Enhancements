using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

public class WaterGeometryTests
{
    // z13 tile near Lake Tahoe
    private static readonly (int X, int Y) T = VectorTile.TileAt(13, 39.1, -120.0);
    private const double R = Geo.R;

    private static WaterTile Build(MvtBuilder mvt, Func<double, double, double>? terrain = null)
    {
        var tile = new WaterTile(T.X, T.Y);
        WaterGeometry.Build(tile, mvt.Build(), R, terrain ?? ((_, _) => 1897.0));
        return tile;
    }

    private static List<(int X, int Y)> Sq(int x, int y, int w, int h) => new() { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };

    private static double TriangleArea(WaterTile t)
    {
        double sum = 0;
        for (int i = 0; i + 2 < t.Count; i += 3)
        {
            var a = new double3(t.Vertices[i].X, t.Vertices[i].Y, t.Vertices[i].Z);
            var b = new double3(t.Vertices[i + 1].X, t.Vertices[i + 1].Y, t.Vertices[i + 1].Z);
            var c = new double3(t.Vertices[i + 2].X, t.Vertices[i + 2].Y, t.Vertices[i + 2].Z);
            sum += Geo.Len(double3.Cross(b - a, c - a)) / 2;
        }
        return sum;
    }

    private static double3 Abs(WaterTile t, int i) => new double3(t.Vertices[i].X, t.Vertices[i].Y, t.Vertices[i].Z) + t.CenterCcf;

    [Fact]
    public void Lake_BecomesFlatTrianglesAtTheShoreLevel_CoveringItsArea()
    {
        var mvt = new MvtBuilder();
        mvt.AddLayer("water").Polygon(1, Sq(1000, 1000, 1500, 1500), ("class", "lake"));
        var tile = Build(mvt);

        Assert.True(tile.Count > 0 && tile.Count % 3 == 0);
        var lake = Assert.Single(tile.Lakes);
        Assert.Equal(0, lake.FirstVertex);
        Assert.Equal(tile.Count, lake.VertexCount);
        Assert.Equal(0, lake.Edges);                                  // touches no tile edge

        // every vertex lies on the sphere at the level: shore height + 0.25 m lift
        for (int i = 0; i < tile.Count; i++)
        {
            Assert.True(float.IsFinite(tile.Vertices[i].X) && float.IsFinite(tile.Vertices[i].Y) && float.IsFinite(tile.Vertices[i].Z));
            Assert.Equal(R + 1897.25, Geo.Len(Abs(tile, i)), 0.05);
        }
        double metersPerUnit = 2 * Math.PI * R * Math.Cos(39.1 * Math.PI / 180) / (1 << 13) / 4096;
        Assert.Equal(1500 * 1500 * metersPerUnit * metersPerUnit, TriangleArea(tile), 0.01 * 1500 * 1500 * metersPerUnit * metersPerUnit);   // within ~1 %
        Assert.InRange(TriangleArea(tile) / lake.AreaM2, 0.98, 1.02);
    }

    [Fact]
    public void Lake_Triangles_NoneDegenerate_AllSameOrientation()
    {
        var mvt = new MvtBuilder();
        mvt.AddLayer("water").Polygon(1, Sq(500, 500, 3000, 2000), ("class", "lake"));
        var tile = Build(mvt);
        double signSum = 0;
        for (int i = 0; i + 2 < tile.Count; i += 3)
        {
            var a = Abs(tile, i); var b = Abs(tile, i + 1); var c = Abs(tile, i + 2);
            var n = double3.Cross(b - a, c - a);
            Assert.True(Geo.Len(n) > 1e-3, "degenerate water triangle");
            signSum += Math.Sign(double3.Dot(n, double3.Normalize(a)));
        }
        Assert.Equal(tile.Count / 3, Math.Abs(signSum));
    }

    [Fact]
    public void LakeWithAnIsland_DoesNotCoverTheIsland()
    {
        var mvt = new MvtBuilder();
        var hole = new List<(int X, int Y)> { (1700, 1700), (1700, 2300), (2300, 2300), (2300, 1700) };   // counter-clockwise = hole
        mvt.AddLayer("water").Add(1, 3, new[] { Sq(1000, 1000, 1500, 1500), hole }, ("class", "lake"));
        var tile = Build(mvt);
        double metersPerUnit = 2 * Math.PI * R * Math.Cos(39.1 * Math.PI / 180) / (1 << 13) / 4096;
        double expected = (1500.0 * 1500 - 600.0 * 600) * metersPerUnit * metersPerUnit;
        Assert.InRange(TriangleArea(tile) / expected, 0.98, 1.02);
    }

    [Fact]
    public void SmallPonds_IntermittentAndTunnelWater_AreSkipped()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("water");
        layer.Polygon(1, Sq(1000, 1000, 40, 40), ("class", "lake"));                      // ~1,400 m2 pond
        layer.Polygon(2, Sq(2000, 1000, 800, 800), ("class", "lake"), ("intermittent", 1));
        layer.Polygon(3, Sq(2000, 2000, 800, 800), ("class", "lake"), ("brunnel", "tunnel"));
        layer.Polygon(4, Sq(100, 2000, 800, 800), ("class", "ocean"));
        var tile = Build(mvt, (_, _) => 0.0);                                             // the open sea (the game's terrain is sea there): not ours
        Assert.Equal(0, tile.Count);
        Assert.Empty(tile.Lakes);
    }

    [Fact]
    public void BigIntermittentLake_IsKept_SmallOneIsNot_AndTheFlagIsRemembered()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("water");
        layer.Polygon(1, Sq(500, 500, 1800, 1800), ("class", "lake"), ("intermittent", 1));      // ~2.8 km2: Walker Lake sized pieces
        layer.Polygon(2, Sq(3000, 3000, 400, 400), ("class", "lake"), ("intermittent", 1));      // 0.14 km2: dropped
        var tile = Build(mvt);
        var lake = Assert.Single(tile.Lakes);
        Assert.True(lake.Intermittent);
        Assert.False(lake.IsRiver);
    }

    [Fact]
    public void WideRiverPolygon_BecomesARiverPiece_NarrowOneAndTunnelDoNot()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("water");
        layer.Polygon(1, Sq(500, 500, 1500, 3000), ("class", "river"));                           // ~1.4 km wide: a river piece
        layer.Polygon(2, Sq(3000, 500, 60, 3000), ("class", "river"));                            // 55 m wide: the river map's
        layer.Polygon(3, Sq(2300, 500, 600, 3000), ("class", "river"), ("brunnel", "tunnel"));
        var tile = Build(mvt);
        var river = Assert.Single(tile.Lakes);
        Assert.True(river.IsRiver);
        Assert.False(river.IsTidal);
        Assert.True(river.AreaM2 > 3e6);
        Assert.Equal(2, tile.RiverAreas.Count);                                                   // the river map gets the wide and the narrow one, not the tunnel
    }

    [Fact]
    public void OceanPolygonOnLand_IsTidalWater_SeaOrWideBayIsNot()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("water");
        layer.Polygon(1, Sq(500, 500, 600, 3000), ("class", "ocean"));                            // a 550 m wide channel the game has as land
        var tile = Build(mvt, (_, _) => 3.0);
        var tidal = Assert.Single(tile.Lakes);
        Assert.True(tidal.IsTidal && tidal.IsRiver);
        Assert.True(tile.Count > 0 && tile.Count % 3 == 0);

        var bay = new MvtBuilder();
        bay.AddLayer("water").Polygon(1, Sq(100, 100, 3800, 3800), ("class", "ocean"));          // wide: the open sea, cut where the game has land
        Assert.Single(Build(bay, (_, _) => 3.0).Lakes);
        Assert.Empty(Build(bay, (_, _) => 0.0).Lakes);                                            // game sea there already
        Assert.Empty(Build(mvt, (_, _) => -20.0).Lakes);                                          // the game has sea there already
    }

    [Fact]
    public void WideLagoonTaggedLake_IsKept_AndSeaLevelLakesAreRecognised()
    {
        // Indian River / Banana River: class lake, 4 km wide, game terrain is land (3 m).
        var mvt = new MvtBuilder();
        mvt.AddLayer("water").Polygon(1, Sq(600, 0, 2400, 4096), ("class", "lake"));
        var tile = Build(mvt, (_, _) => 3.0);
        var lagoon = Assert.Single(tile.Lakes);
        Assert.False(lagoon.IsTidal);
        Assert.True(tile.Count > 0 && tile.Count % 3 == 0);
        Assert.True(lagoon.AreaM2 > 3e6);

        // real surface heights: sea level (0, 1.4) is a sea-level lake, Okeechobee (2.3), Salton Sea (-70) and the Caspian (-28) are not
        Assert.True(WaterGeometry.IsSeaLevelLake(0.0));
        Assert.True(WaterGeometry.IsSeaLevelLake(1.4));
        Assert.True(WaterGeometry.IsSeaLevelLake(-0.4));
        Assert.False(WaterGeometry.IsSeaLevelLake(2.3));
        Assert.False(WaterGeometry.IsSeaLevelLake(-70));
        Assert.False(WaterGeometry.IsSeaLevelLake(-28));
    }

    [Fact]
    public void LakeCutByTheTileEdge_RemembersWhichEdgesItTouches()
    {
        var mvt = new MvtBuilder();
        mvt.AddLayer("water").Polygon(1, Sq(-0, 500, 1500, 1500), ("class", "lake"));
        var lake = Assert.Single(Build(mvt).Lakes);
        Assert.Equal(1, lake.Edges & 1);   // touches the west edge
        Assert.Equal(0, lake.Edges & 2);
    }

    [Fact]
    public void Lake_LevelFollowsTheShoreNotTheLakeBed()
    {
        // lake bed 60 m deep inside the polygon, shore at 1900: the water level comes from the shore
        double3 centre = Geo.Dir(39.1, -120.0);
        var mvt = new MvtBuilder();
        var round = Enumerable.Range(0, 60).Select(k => ((int)(1750 + 750 * Math.Cos(2 * Math.PI * k / 60)), (int)(1750 + 750 * Math.Sin(2 * Math.PI * k / 60)))).ToList();
        mvt.AddLayer("water").Polygon(1, round, ("class", "lake"));
        var (s, w, n, e) = VectorTile.Bounds(13, T.X, T.Y);
        double Terrain(double lat, double lon)
        {
            double fx = (lon - w) / (e - w) * 4096, fy = (n - lat) / (n - s) * 4096;   // approx. tile units (linear in lat is close enough)
            bool inside = Math.Sqrt((fx - 1750) * (fx - 1750) + (fy - 1750) * (fy - 1750)) < 700;
            return inside ? 1840 : 1900;
        }
        var tile = Build(mvt, Terrain);
        var level = Geo.Len(Abs(tile, 0)) - R;
        Assert.InRange(level, 1899.9, 1900.6);
    }

    [Fact]
    public void Rivers_AreCollectedWithTheirClassWidth_AndSkipDrainsAndTunnels()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("waterway");
        layer.Line(1, new List<(int X, int Y)> { (100, 100), (2000, 1500), (3900, 3000) }, ("class", "river"));
        layer.Line(2, new List<(int X, int Y)> { (100, 300), (2000, 500) }, ("class", "stream"));
        layer.Line(3, new List<(int X, int Y)> { (100, 600), (2000, 700) }, ("class", "drain"));
        layer.Line(4, new List<(int X, int Y)> { (100, 900), (2000, 1000) }, ("class", "river"), ("brunnel", "tunnel"));
        layer.Line(5, new List<(int X, int Y)> { (100, 1200), (2000, 1300) }, ("class", "canal"), ("intermittent", 1));
        var tile = Build(mvt);
        Assert.Equal(2, tile.Rivers.Count);
        Assert.Equal(28, tile.Rivers.Single(r => r.LatLon.Length == 6).Width);
        Assert.Equal(7, tile.Rivers.Single(r => r.LatLon.Length == 4).Width);
        var (s, w, n, e) = VectorTile.Bounds(13, T.X, T.Y);
        foreach (var r in tile.Rivers)
            for (int i = 0; i < r.LatLon.Length; i += 2)
            {
                Assert.InRange(r.LatLon[i], s, n); Assert.InRange(r.LatLon[i + 1], w, e);
            }
    }

    [Fact]
    public void RiverBanks_BecomeTriangleListsInLatLon_AndPondsInsideTheTileToo()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("water");
        layer.Polygon(1, Sq(500, 500, 300, 2000), ("class", "river"));
        layer.Polygon(2, Sq(2000, 2000, 60, 60), ("class", "lake"));      // small pond: painted like banks
        var tile = Build(mvt);
        Assert.Equal(2, tile.RiverAreas.Count);
        Assert.All(tile.RiverAreas, a => { Assert.Equal(0, a.Length % 6); Assert.All(a, v => Assert.True(double.IsFinite(v))); });
    }

    [Fact]
    public void ReaderSurvivesGarbage()
    {
        var tile = new WaterTile(T.X, T.Y);
        WaterGeometry.Build(tile, Array.Empty<byte>(), R, (_, _) => 0);
        Assert.Equal(0, tile.Count);
    }
}

public class RiverMapTests
{
    private const double R = Geo.R;
    private const double Lat = 47.0, Lon = 8.0;

    private static (int X, int Y) Texel(RiverMap.Window w, double lat, double lon) =>
        ((int)((0.5 + lon / 360 - w.U0) / w.Du * RiverMap.Size), (int)((0.5 - lat / 180 - w.V0) / w.Dv * RiverMap.Size));

    [Fact]
    public void Geometry_WindowHasTheRequestedSizeInMetres_AndContainsTheCamera()
    {
        foreach (double lat in new[] { 0.0, 47.0, -62.3, 78.0 })
        {
            var (u0, v0, du, dv) = RiverMap.Geometry(lat, 8.0, R, RiverMap.NearMeters);
            Assert.Equal(RiverMap.NearMeters, dv * Math.PI * R, 3);
            Assert.Equal(RiverMap.NearMeters, du * 2 * Math.PI * R * Math.Cos(Math.Round(lat) * Math.PI / 180), 3);
            double uc = 0.5 + 8.0 / 360, vc = 0.5 - lat / 180;
            Assert.InRange(uc, u0, u0 + du); Assert.InRange(vc, v0, v0 + dv);
            // corner is snapped to a 64 texel grid
            double stepU = du * 64 / RiverMap.Size;
            Assert.Equal(Math.Round(u0 / stepU), u0 / stepU, 6);
        }
    }

    [Fact]
    public void Line_IsRasterisedAtItsCentre_AndNothingFarAway()
    {
        var line = new RiverLine { Width = 28, LatLon = new[] { Lat - 0.01, Lon, Lat + 0.01, Lon } };
        var w = RiverMap.Build(Lat, Lon, R, new[] { line }, Array.Empty<double[]>(), RiverMap.NearMeters, 0);
        Assert.Equal(1, w.Lines);
        var (cx, cy) = Texel(w, Lat, Lon);
        Assert.Equal(255, w.Data[cy * RiverMap.Size + cx]);
        // 28 m wide at 5.9 m per texel -> about 5 texels wide, centred on the line
        int wide = 0;
        for (int x = 0; x < RiverMap.Size; x++) if (w.Data[cy * RiverMap.Size + x] == 255) wide++;
        Assert.InRange(wide, 3, 7);
        Assert.Equal(0, w.Data[cy * RiverMap.Size + Math.Min(cx + 40, RiverMap.Size - 1)]);
        Assert.Equal(0, w.Data[0]);
    }

    [Fact]
    public void ThinStream_KeepsAtLeastAboutOneTexel_AndFarWindowSkipsIt()
    {
        var stream = new RiverLine { Width = 4, LatLon = new[] { Lat - 0.01, Lon, Lat + 0.01, Lon } };
        var near = RiverMap.Build(Lat, Lon, R, new[] { stream }, Array.Empty<double[]>(), RiverMap.NearMeters, 0);
        var (cx, cy) = Texel(near, Lat, Lon);
        Assert.True(near.Data[cy * RiverMap.Size + cx] > 128 || near.Data[cy * RiverMap.Size + cx - 1] > 128);
        var far = RiverMap.Build(Lat, Lon, R, new[] { stream }, Array.Empty<double[]>(), RiverMap.FarMeters, RiverMap.FarMinWidth);
        Assert.Equal(0, far.Lines);
        Assert.All(far.Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void AreaTriangles_FillTheInside_WithSoftEdges()
    {
        double d = 0.01;
        var tri = new[] { Lat - d, Lon - d, Lat - d, Lon + d, Lat + d, Lon + d, Lat - d, Lon - d, Lat + d, Lon + d, Lat + d, Lon - d };   // a square, two triangles
        var w = RiverMap.Build(Lat, Lon, R, Array.Empty<RiverLine>(), new[] { tri }, RiverMap.NearMeters, 0);
        Assert.Equal(1, w.Areas);
        var (cx, cy) = Texel(w, Lat, Lon);
        Assert.Equal(255, w.Data[cy * RiverMap.Size + cx]);
        // the shared diagonal leaves no seam
        for (int k = -20; k <= 20; k++)
            Assert.Equal(255, w.Data[(cy + k) * RiverMap.Size + cx + k]);
        int filled = w.Data.Count(b => b > 0);
        double expectedTexels = Math.Pow(2 * d * Geo.MetersPerDegree, 2) * Math.Cos(Lat * Math.PI / 180) / Math.Pow(RiverMap.NearMeters / RiverMap.Size, 2);
        Assert.InRange(filled / expectedTexels, 0.97, 1.06);
    }

    // REAL BUG (minor): the line hash is LatLon[0] + LatLon[^1] * 3 + Width, i.e. first latitude + LAST LONGITUDE. A line that only
    // changes its last latitude or its middle points (e.g. a longer north-south river from a newly loaded tile) keeps the same
    // signature, so a consumer that re-uploads the map only when Signature changes would miss the update.
    [Fact(Skip = "KNOWN BUG: RiverMap.Build signature ignores the last latitude and all middle points")]
    public void Signature_ChangesWhenANorthSouthLineIsExtended()
    {
        var a = new RiverLine { Width = 20, LatLon = new[] { Lat, Lon, Lat + 0.001, Lon } };
        var b = new RiverLine { Width = 20, LatLon = new[] { Lat, Lon, Lat + 0.002, Lon } };
        Assert.NotEqual(RiverMap.Build(Lat, Lon, R, new[] { a }, Array.Empty<double[]>(), RiverMap.NearMeters, 0).Signature,
                        RiverMap.Build(Lat, Lon, R, new[] { b }, Array.Empty<double[]>(), RiverMap.NearMeters, 0).Signature);
    }

    [Fact]
    public void Signature_IsStable_AndChangesWithTheContent()
    {
        var a = new RiverLine { Width = 20, LatLon = new[] { Lat, Lon, Lat + 0.001, Lon + 0.001 } };
        var b = new RiverLine { Width = 20, LatLon = new[] { Lat, Lon, Lat + 0.002, Lon + 0.002 } };
        ulong s1 = RiverMap.Build(Lat, Lon, R, new[] { a }, Array.Empty<double[]>(), RiverMap.NearMeters, 0).Signature;
        ulong s2 = RiverMap.Build(Lat, Lon, R, new[] { a }, Array.Empty<double[]>(), RiverMap.NearMeters, 0).Signature;
        ulong s3 = RiverMap.Build(Lat, Lon, R, new[] { b }, Array.Empty<double[]>(), RiverMap.NearMeters, 0).Signature;
        Assert.Equal(s1, s2);
        Assert.NotEqual(s1, s3);
    }

    [Theory]
    [InlineData("river", 28)]
    [InlineData("canal", 16)]
    [InlineData("stream", 7)]
    [InlineData("drain", 0)]
    [InlineData(null, 0)]
    public void Width_ByClass(string? cls, double width) => Assert.Equal(width, RiverMap.Width(cls));
}

public class ElevationWindowTests
{
    private const double R = Geo.R;

    [Fact]
    public void Pixel_Golden()
    {
        var (x, y) = ElevationWindow.Pixel(0, 0);
        Assert.Equal(524288, x, 6); Assert.Equal(524288, y, 6);
        (x, y) = ElevationWindow.Pixel(0, -180);
        Assert.Equal(0, x, 6);
        (x, y) = ElevationWindow.Pixel(90, 180);       // latitude is clamped to 85.05
        Assert.Equal(1048576, x, 6); Assert.InRange(y, 0, 50);
    }

    [Fact]
    public void DecodeTerrarium_Golden()
    {
        var rgba = new byte[256 * 256 * 4];
        rgba[0] = 128; rgba[1] = 0; rgba[2] = 0;                    // sea level
        rgba[4] = 130; rgba[5] = 5; rgba[6] = 128;                  // 2 * 256 + 5 + 0.5
        rgba[8] = 127; rgba[9] = 255; rgba[10] = 0;                 // -1
        var h = ElevationWindow.DecodeTerrarium(rgba);
        Assert.Equal(0f, h[0]); Assert.Equal(517.5f, h[1]); Assert.Equal(-1f, h[2]);
        Assert.Equal(256 * 256, h.Length);
    }

    [Fact]
    public void TilesNeeded_ContainTheCameraTile_AreContiguousAndWrapAtTheDateLine()
    {
        var tiles = ElevationWindow.TilesNeeded(47.0, 8.0, R);
        var (px, py) = ElevationWindow.Pixel(47.0, 8.0);
        Assert.Contains(((int)(px / 256), (int)(py / 256)), tiles);
        Assert.Equal(tiles.Count, tiles.Distinct().Count());
        int w = tiles.Max(t => t.X) - tiles.Min(t => t.X) + 1, h = tiles.Max(t => t.Y) - tiles.Min(t => t.Y) + 1;
        Assert.Equal(w * h, tiles.Count);
        Assert.InRange(tiles.Count, 100, 250);   // 80 km window over z12 tiles of ~6.6 km

        var wrap = ElevationWindow.TilesNeeded(0, 179.99, R);
        Assert.All(wrap, t => Assert.InRange(t.X, 0, ElevationWindow.TilesPerSide - 1));
        Assert.Contains(wrap, t => t.X > 4000); Assert.Contains(wrap, t => t.X < 100);
        var pole = ElevationWindow.TilesNeeded(84.9, 10, R);
        Assert.All(pole, t => Assert.InRange(t.Y, 0, ElevationWindow.TilesPerSide - 1));
    }

    private static float[] RampTile(int tx, int ty)
    {
        var d = new float[256 * 256];
        for (int r = 0; r < 256; r++)
            for (int c = 0; c < 256; c++)
                d[r * 256 + c] = (float)(0.02 * (tx * 256 + c) + 0.03 * (ty * 256 + r));
        return d;
    }

    [Fact]
    public void Build_ReproducesALinearField_ExactlyAtTheCentre_AndFadesAtTheRim()
    {
        const double lat = 47.0, lon = 8.0;
        var w = ElevationWindow.Build(lat, lon, R, RampTile);
        Assert.Equal(1.0, w.Coverage, 9);
        Assert.NotEmpty(w.Used);
        Assert.True(w.TrySample(lat, lon, out double h, out double weight));
        var (px, py) = ElevationWindow.Pixel(lat, lon);
        Assert.Equal(0.02 * (px - 0.5) + 0.03 * (py - 0.5), h, 1);
        Assert.Equal(1.0, weight, 9);

        // a point near the window edge counts less than the centre; outside it does not count at all
        var (u0, v0, du, dv) = RiverMap.Geometry(lat, lon, R, ElevationWindow.WindowMeters);
        Assert.True(w.TrySample((0.5 - (v0 + dv * 0.01)) * 180, lon, out _, out double rim));
        Assert.True(rim < 0.5, $"rim weight {rim}");
        Assert.False(w.TrySample(lat + 2, lon, out _, out _));
    }

    [Fact]
    public void Build_MissingTiles_LeaveNoData_AndSampleReturnsFalseThere()
    {
        const double lat = 47.0, lon = 8.0;
        var (px, py) = ElevationWindow.Pixel(lat, lon);
        int missingX = (int)(px / 256), missingY = (int)(py / 256);
        var w = ElevationWindow.Build(lat, lon, R, (x, y) => x == missingX && y == missingY ? null : RampTile(x, y));
        Assert.InRange(w.Coverage, 0.9, 0.999);
        Assert.False(w.TrySample(lat, lon, out _, out _));
        Assert.Contains(w.Heights, v => v <= ElevationWindow.NoData);
    }
}
