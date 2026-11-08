using Xunit;

namespace GEE.Tests;

public class VectorTileTests
{
    // A z14 tile in Berlin: far from every hard-coded launch complex, so only the tile content shows up.
    private const double Lat = 52.52, Lon = 13.405;
    private static readonly (int X, int Y) T = VectorTile.TileAt(14, Lat, Lon);
    private static readonly OsmOptions Options = new();

    private static double MetersPerUnit()
    {
        var (s, w, n, e) = VectorTile.Bounds(14, T.X, T.Y);
        return (e - w) * Geo.MetersPerDegree * Math.Cos((s + n) / 2 * Math.PI / 180) / 4096;
    }

    private static List<(int X, int Y)> Square(int x, int y, int w, int h) =>
        new() { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };

    [Fact]
    public void Bounds_Z0_IsTheWholeWebMercatorWorld()
    {
        var (s, w, n, e) = VectorTile.Bounds(0, 0, 0);
        Assert.Equal(-180, w, 9); Assert.Equal(180, e, 9);
        Assert.Equal(85.0511287798, n, 6); Assert.Equal(-85.0511287798, s, 6);
    }

    [Fact]
    public void Bounds_AreNeighbourSeamless_AndTilesPartitionTheParent()
    {
        var a = VectorTile.Bounds(14, T.X, T.Y);
        var right = VectorTile.Bounds(14, T.X + 1, T.Y);
        var below = VectorTile.Bounds(14, T.X, T.Y + 1);
        Assert.Equal(a.East, right.West, 12);
        Assert.Equal(a.South, below.North, 12);
        var parent = VectorTile.Bounds(13, T.X >> 1, T.Y >> 1);
        Assert.True(a.West >= parent.West - 1e-12 && a.East <= parent.East + 1e-12 && a.South >= parent.South - 1e-12 && a.North <= parent.North + 1e-12);
    }

    [Fact]
    public void TileAt_RoundTripsWithBounds()
    {
        var rng = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            double lat = rng.NextDouble() * 160 - 80, lon = rng.NextDouble() * 358 - 179;
            int z = rng.Next(0, 17);
            var (x, y) = VectorTile.TileAt(z, lat, lon);
            var (s, w, n, e) = VectorTile.Bounds(z, x, y);
            Assert.True(lat >= s - 1e-9 && lat <= n + 1e-9 && lon >= w - 1e-9 && lon <= e + 1e-9, $"z{z} ({lat},{lon}) not in tile {x}/{y}");
        }
    }

    [Fact]
    public void TileAt_Golden_KennedySpaceCenter()
    {
        var (x, y) = VectorTile.TileAt(14, 28.6082, -80.6044);
        Assert.Equal(4523, x);
        Assert.Equal(6832, y);
    }

    [Fact]
    public void TileAt_ClampsAtThePoles()
    {
        var (x, y) = VectorTile.TileAt(10, 90, 180);
        Assert.Equal(1023, x); Assert.Equal(0, y);
        (x, y) = VectorTile.TileAt(10, -90, -180);
        Assert.Equal(0, x); Assert.Equal(1023, y);
    }

    [Fact]
    public void Parse_Building_BecomesOneOrientedBoxOfTheRightSize()
    {
        double k = 1 / MetersPerUnit();   // tile units per metre
        int w = (int)Math.Round(20 * k), h = (int)Math.Round(12 * k);
        var mvt = new MvtBuilder();
        mvt.AddLayer("building").Polygon(42, Square(2000, 2000, w, h), ("render_height", 18.0), ("colour", "#decdd0"));
        var data = VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, Options);

        var b = Assert.Single(data.Buildings);
        Assert.Equal(42, b.Id);
        Assert.Equal(18, b.Height, 6);
        var box = Assert.Single(b.Boxes);
        Assert.Equal(20, Math.Max(box.LengthA, box.LengthB), 0);
        Assert.Equal(12, Math.Min(box.LengthA, box.LengthB), 0);
        Assert.Equal(1, b.Variant);   // plaster, nearest to the given colour
        var (s, west, n, e) = VectorTile.Bounds(14, T.X, T.Y);
        Assert.InRange(b.Lat, s, n); Assert.InRange(b.Lon, west, e);
    }

    [Fact]
    public void Parse_BuildingWithHide3D_IsDropped_AndHolesDoNotCreateBuildings()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("building");
        layer.Polygon(1, Square(100, 100, 300, 300), ("hide_3d", true));
        // exterior (clockwise in tile coordinates) followed by a hole (counter-clockwise)
        layer.Add(2, 3, new[] { Square(1000, 1000, 600, 600), new List<(int X, int Y)> { (1100, 1100), (1100, 1500), (1500, 1500), (1500, 1100) } },
            ("render_height", 10.0));
        var data = VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, Options);
        Assert.Single(data.Buildings);
        Assert.Equal(2, data.Buildings[0].Id);
    }

    [Fact]
    public void Parse_BuildingCrossingTileEdge_IsClippedToTheTile()
    {
        var mvt = new MvtBuilder();
        mvt.AddLayer("building").Polygon(3, new List<(int X, int Y)> { (-400, 1000), (600, 1000), (600, 1400), (-400, 1400) }, ("render_height", 9.0));
        var b = Assert.Single(VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, Options).Buildings);
        var (_, west, _, _) = VectorTile.Bounds(14, T.X, T.Y);
        double halfWidthDeg = b.Boxes[0].LengthA / 2 / (Geo.MetersPerDegree * Math.Cos(Lat * Math.PI / 180));
        Assert.True(b.Lon - halfWidthDeg >= west - 1e-7, "the box must not stick out of the tile on the west side");
    }

    [Fact]
    public void Parse_Roads_ClassifyClipAndFlagTileEdges()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("transportation");
        layer.Line(1, new List<(int X, int Y)> { (-500, 2000), (2000, 2000), (2000, 3000) }, ("class", "primary"));
        layer.Line(2, new List<(int X, int Y)> { (300, 100), (300, 900) }, ("class", "primary"), ("oneway", 1));
        layer.Line(3, new List<(int X, int Y)> { (300, 100), (300, 900) }, ("class", "motorway"), ("brunnel", "tunnel"));
        layer.Line(4, new List<(int X, int Y)> { (300, 100), (300, 900) }, ("class", "path"), ("subclass", "footway"));
        var roads = VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, Options).Roads;
        Assert.Equal(2, roads.Count);

        var main = roads.Single(r => r.Width == 9.0);
        Assert.True(main.StartAtTileEdge);
        Assert.False(main.EndAtTileEdge);
        Assert.Equal(3, main.Points.Count);   // clipped start point + corner + end
        Assert.Equal(VectorTile.Bounds(14, T.X, T.Y).West, main.Points[0].Lon, 9);

        var oneway = roads.Single(r => r.Width != 9.0);
        Assert.Equal(5.4, oneway.Width, 9);
        Assert.False(oneway.StartAtTileEdge || oneway.EndAtTileEdge);
        double tie = (oneway.Lift - Roads.BaseLift - 4 * Roads.RankStep) / Roads.TieStep;
        Assert.InRange(tie, -1e-6, Roads.TieLevels - 1 + 1e-6);
        Assert.Equal(Math.Round(tie), tie, 6);
    }

    [Fact]
    public void Parse_CrossingRoads_GetASharedJunctionPoint_AndClearStretch()
    {
        var mvt = new MvtBuilder();
        var layer = mvt.AddLayer("transportation");
        layer.Line(1, new List<(int X, int Y)> { (500, 2048), (3500, 2048) }, ("class", "secondary"));
        layer.Line(2, new List<(int X, int Y)> { (2048, 500), (2048, 3500) }, ("class", "secondary"));
        var roads = VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, Options).Roads;
        Assert.Equal(2, roads.Count);
        // a point shared by both (inserted at the crossing, because the tile simplification drops it)
        var shared = roads[0].Points.Intersect(roads[1].Points).ToList();
        Assert.Single(shared);
        int i0 = roads[0].Points.IndexOf(shared[0]), i1 = roads[1].Points.IndexOf(shared[0]);
        Assert.NotNull(roads[0].Clear); Assert.NotNull(roads[1].Clear);
        Assert.True(roads[0].Clear![i0] > roads[1].Width / 2);
        Assert.True(roads[1].Clear![i1] > roads[0].Width / 2);
        Assert.True(roads[0].Fixed![i0] && roads[1].Fixed![i1]);
    }

    [Fact]
    public void Parse_Aeroway_RunwayTaxiwayAndApron()
    {
        var mvt = new MvtBuilder();
        mvt.AddLayer("aeroway")
            .Line(1, new List<(int X, int Y)> { (200, 500), (3800, 500) }, ("class", "runway"))
            .Line(2, new List<(int X, int Y)> { (200, 900), (3800, 900) }, ("class", "taxiway"))
            .Polygon(3, Square(1000, 1500, 800, 500), ("class", "apron"));
        var data = VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, Options);
        Assert.Equal(45, data.Roads.Single(r => r.Airfield == Airfield.Runway).Width);
        Assert.Equal(20, data.Roads.Single(r => r.Airfield == Airfield.Taxiway).Width);
        var apron = Assert.Single(data.Buildings);
        Assert.True(apron.Slab);
        Assert.Equal(Buildings.ApronVariant, apron.Variant);
    }

    [Fact]
    public void Parse_EmptyAndUnknownLayers_GiveNothing()
    {
        Assert.Empty(VectorTile.Parse(Array.Empty<byte>(), 14, T.X, T.Y, Options).Buildings);
        var mvt = new MvtBuilder();
        mvt.AddLayer("water").Polygon(1, Square(0, 0, 4000, 4000));
        var data = VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, Options);
        Assert.Empty(data.Buildings); Assert.Empty(data.Roads);
    }

    [Fact]
    public void Parse_LaunchComplexTile_ContainsLandmarksAndPipelines()
    {
        var (x, y) = VectorTile.TileAt(14, 28.6082656, -80.6044304);
        var data = VectorTile.Parse(Array.Empty<byte>(), 14, x, y, Options);
        Assert.Contains(data.Buildings, b => b.Id == -2);   // fixed service structure
        Assert.Contains(data.Buildings, b => b.LaunchComplex && b.Id >= 1_000_000_000 && b.Id < 1_100_000_000);   // pipeline pieces
    }

    [Fact]
    public void ForEachFeature_ReadsTagsAndGeometry()
    {
        var mvt = new MvtBuilder();
        mvt.AddLayer("poi").Add(9, 1, new[] { new List<(int X, int Y)> { (123, 456) } }, ("name", "x"), ("rank", 3));
        int seen = 0;
        VectorTile.ForEachFeature(mvt.Build(), l => l == "poi", (layer, type, tags, geometry, extent) =>
        {
            seen++;
            Assert.Equal(1, type);
            Assert.Equal("x", tags["name"]);
            Assert.Equal(3.0, tags["rank"]);
            Assert.Equal(4096, extent);
            var pts = VectorTile.Points(geometry);
            Assert.Equal(new Footprint.P(123, 456), Assert.Single(pts));
        });
        Assert.Equal(1, seen);
    }
}
