using Xunit;

namespace GEE.Tests;

/// <summary>Corrupt or odd input must never hang or yield NaN geometry.</summary>
public class RobustnessTests
{
    private static byte[] SampleTile()
    {
        var mvt = new MvtBuilder();
        mvt.AddLayer("building").Polygon(1, new List<(int X, int Y)> { (1000, 1000), (1400, 1000), (1400, 1300), (1000, 1300) }, ("render_height", 12.0));
        mvt.AddLayer("transportation").Line(2, new List<(int X, int Y)> { (100, 100), (3000, 2000) }, ("class", "primary"));
        return mvt.Build();
    }

    [Fact]
    public void TruncatedTiles_AreHandledPredictably()
    {
        var data = SampleTile();
        var (x, y) = VectorTile.TileAt(14, 52.5, 13.4);
        int ok = 0, threw = 0;
        for (int cut = 0; cut < data.Length; cut += 3)
        {
            try
            {
                var parsed = VectorTile.Parse(data[..cut], 14, x, y, new OsmOptions());
                foreach (var b in parsed.Buildings) Assert.True(double.IsFinite(b.Lat) && double.IsFinite(b.Height));
                ok++;
            }
            catch (ArgumentOutOfRangeException) { threw++; }   // callers catch exceptions per tile (Tiles.cs); a truncated download must not crash the process
        }
        Assert.True(ok > 0);
        Assert.Equal(data.Length / 3 + (data.Length % 3 == 0 ? 0 : 1), ok + threw);
    }

    [Fact]
    public void RandomBytes_DoNotHang()
    {
        var rng = new Random(3);
        var (x, y) = VectorTile.TileAt(14, 52.5, 13.4);
        for (int i = 0; i < 200; i++)
        {
            var bytes = new byte[rng.Next(1, 200)];
            rng.NextBytes(bytes);
            try { VectorTile.Parse(bytes, 14, x, y, new OsmOptions()); }
            catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException or InvalidOperationException or IndexOutOfRangeException or ArgumentException) { }
        }
    }

    [Fact]
    public void RoadsAtTheDateLineAndNearThePoles_StayFinite()
    {
        foreach (var (lat, lon) in new[] { (0.0, 179.9999), (0.0, -179.9999), (80.0, 10.0), (-70.0, 170.0), (89.0, 0.0) })
        {
            var road = new RawRoad
            {
                Points = new() { (lat, lon), (lat + 0.0005, lon + 0.0005), (lat + 0.0005, lon + 0.001) },
                Width = 6, Lift = 0.3, Surface = RoadSurface.Road,
            };
            var list = new List<BuildingInstance>();
            Placement.AddRoadInstances(road, Geo.R, Geo.Flat(10), list.Add);
            Assert.NotEmpty(list);
            Assert.All(list, p => Assert.True(Geo.Finite(p), $"NaN at {lat},{lon}"));
        }
    }

    [Fact]
    public void Buildings_NearThePoles_StayFinite()
    {
        foreach (double lat in new[] { 89.0, -89.0, 89.99 })
        {
            var b = new RawBuilding { Id = 1, Lat = lat, Lon = 45, Height = 10, Variant = 1 };
            var inst = Placement.MakeInstance(b, new Box2(5, 5, 0.2, 10, 10), Geo.R, 1, Geo.Flat());
            Assert.True(Geo.Finite(inst));
        }
    }
}
