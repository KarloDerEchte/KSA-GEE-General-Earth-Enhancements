using Xunit;

namespace GEE.Tests;

public class LakeMaskFilterTests
{
    [Fact]
    public void Open_RemovesWaterNarrowerThanThreeTexels_AndKeepsWideWater()
    {
        const int n = 64;
        var mask = new byte[n * n];
        for (int y = 5; y < 40; y++)
            for (int x = 5; x < 40; x++)
                mask[y * n + x] = 1;                                   // a wide lake
        for (int x = 40; x < 60; x++)
            for (int y = 20; y < 22; y++)
                mask[y * n + x] = 1;                                   // a 2 texel canal leaving it
        LakeMaskFilter.Open(mask, n, 1);
        Assert.Equal(1, mask[20 * n + 20]);
        Assert.Equal(1, mask[6 * n + 6]);                              // corner pixels of the lake survive the opening
        Assert.Equal(0, mask[20 * n + 55]);                            // the canal is dry
    }

    [Fact]
    public void DropSmallComponents_DropsIsolatedPonds_KeepsBigWaterAndWhatTouchesTheRim()
    {
        const int n = 64;
        var mask = new byte[n * n];
        for (int y = 10; y < 40; y++)
            for (int x = 10; x < 40; x++)
                mask[y * n + x] = 1;                                   // big lake
        for (int y = 50; y < 53; y++)
            for (int x = 20; x < 23; x++)
                mask[y * n + x] = 1;                                   // small pond
        for (int y = 5; y < 8; y++)
            mask[y * n] = 1;                                           // small piece at the window's rim
        LakeMaskFilter.DropSmallComponents(mask, n, 1, 100);
        Assert.Equal(1, mask[20 * n + 20]);
        Assert.Equal(0, mask[51 * n + 21]);
        Assert.Equal(1, mask[6 * n]);
    }

    [Fact]
    public void BoxOpen_KeepsBlobsWithASquare_AndDropsThinLines()
    {
        const int n = 32;
        var a = new bool[n * n];
        for (int y = 5; y < 20; y++)
            for (int x = 5; x < 20; x++)
                a[y * n + x] = true;
        for (int x = 20; x < 30; x++)
            a[10 * n + x] = true;                                      // a one texel line (a causeway)
        var open = LakeMaskFilter.BoxOpen(a, n, 3);
        Assert.True(open[10 * n + 10]);
        Assert.False(open[10 * n + 25]);
    }

    [Fact]
    public void Refine_BurnsASmallIslandIntoTheMask_EvenIfNoPixelCentreLiesInside()
    {
        // Pixel (x, y) is longitude x deg, latitude -y/2 deg: u0 = v0 = 0.5, du = dv = 1, n = 360.
        const int n = 360;
        var mask = new byte[n * n];
        Array.Fill(mask, (byte)1);
        // a sliver island 0.3 texel wide and 6 long, between pixel centres
        var ring = new[] { -10.0 * 0.5 - 0.0, 100.1, -10.0 * 0.5, 100.4, -16.0 * 0.5, 100.4, -16.0 * 0.5, 100.1 };
        var info = new LakeInfo { HoleGeo = new List<double[]> { ring } };
        LakeMaskFilter.Refine(mask, n, 1, new List<LakeInfo> { info }, null, false, 0.5, 0.5, 1, 1);
        Assert.Equal(0, mask[12 * n + 100]);
        Assert.Equal(1, mask[12 * n + 110]);
    }

    [Fact]
    public void Refine_OutsideTheGroup_IsLeftAlone()
    {
        const int n = 360;
        var mask = new byte[n * n];
        Array.Fill(mask, (byte)2);                                     // another lake's pixels
        var ring = new[] { -5.0, 50.0, -5.0, 53.0, -8.0, 53.0, -8.0, 50.0 };
        LakeMaskFilter.Refine(mask, n, 1, new List<LakeInfo> { new() { HoleGeo = new List<double[]> { ring } } }, null, false, 0.5, 0.5, 1, 1);
        Assert.All(mask, m => Assert.Equal(2, m));
    }
}

public class BuildingsInWaterTests
{
    private const double Lat = 28.4, Lon = -80.65;
    private static readonly (int X, int Y) T = VectorTile.TileAt(14, Lat, Lon);

    private static double Unit()
    {
        var (s, w, n, e) = VectorTile.Bounds(14, T.X, T.Y);
        return (e - w) * Geo.MetersPerDegree * Math.Cos((s + n) / 2 * Math.PI / 180) / 4096;
    }

    private static List<(int X, int Y)> Sq(int x, int y, int w, int h) => new() { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };

    private static TileData Parse(string waterClass, params (string, object)[] extra)
    {
        int big = (int)(600 / Unit()), small = (int)(10 / Unit());
        var mvt = new MvtBuilder();
        var water = mvt.AddLayer("water");
        // 600 m lake with a 100 m island in the middle
        int ix = 2048 + big / 2 - (int)(50 / Unit()), iy = ix;
        water.Add(1, 3, new[] { Sq(1000, 1000, big, big), new List<(int X, int Y)> { (ix, iy), (ix, iy + (int)(100 / Unit())), (ix + (int)(100 / Unit()), iy + (int)(100 / Unit())), (ix + (int)(100 / Unit()), iy) } },
            new[] { ("class", (object)waterClass) }.Concat(extra).ToArray());
        var b = mvt.AddLayer("building");
        b.Polygon(10, Sq(1000 + big / 4, 1000 + big / 4, (int)(20 / Unit()), (int)(12 / Unit())), ("render_height", 8.0));     // in the lake
        b.Polygon(11, Sq(ix + (int)(40 / Unit()), iy + (int)(40 / Unit()), (int)(15 / Unit()), (int)(10 / Unit())), ("render_height", 8.0));  // on the island
        b.Polygon(12, Sq(1000 + big + 200, 1000, (int)(20 / Unit()), (int)(12 / Unit())), ("render_height", 8.0));     // ashore
        b.Polygon(13, Sq(1000 + big - small, 1000 + big / 2, (int)(20 / Unit()), (int)(12 / Unit())), ("render_height", 8.0));  // on the bank
        return VectorTile.Parse(mvt.Build(), 14, T.X, T.Y, new OsmOptions());
    }

    [Theory]
    [InlineData("lake")]
    [InlineData("river")]
    [InlineData("ocean")]
    public void BuildingInWater_IsDropped_IslandAndShoreStay(string waterClass)
    {
        var ids = Parse(waterClass).Buildings.Select(b => b.Id).ToHashSet();
        Assert.DoesNotContain(10, ids);
        Assert.Contains(11, ids);
        Assert.Contains(12, ids);
        Assert.Contains(13, ids);
    }

    [Fact]
    public void PondsAndTunnelsAndIntermittentWater_DoNotRemoveBuildings()
    {
        Assert.Contains(10, Parse("pond").Buildings.Select(b => b.Id));
        Assert.Contains(10, Parse("lake", ("brunnel", "tunnel")).Buildings.Select(b => b.Id));
        Assert.Contains(10, Parse("lake", ("intermittent", 1)).Buildings.Select(b => b.Id));
    }
}
