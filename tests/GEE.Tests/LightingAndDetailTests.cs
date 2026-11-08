using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

public class LightSetTests
{
    [Fact]
    public void Hash_IsDeterministic_AndSpreadsEvenly()
    {
        Assert.Equal(LightSet.Hash(12345u), LightSet.Hash(12345u));
        Assert.Equal(LightSet.Hash(1, 2, 3), LightSet.Hash(1, 2, 3));
        Assert.NotEqual(LightSet.Hash(1, 2, 3), LightSet.Hash(3, 2, 1));
        var buckets = new int[16];
        const int n = 160_000;
        for (uint i = 0; i < n; i++)
            buckets[LightSet.Hash(i) >> 28]++;
        foreach (int c in buckets)
            Assert.InRange(c, n / 16 * 0.95, n / 16 * 1.05);   // flat to 5 %
    }

    [Fact]
    public void Hash_Golden_KeepsColoursAndLevelsStableBetweenVersions()
    {
        // Seeds drive window/lamp colours and thinning order; changing the hash silently re-rolls every city. Pin two values.
        Assert.Equal(0x688990c0u, LightSet.Hash(1u));
        Assert.Equal(0x172733c2u, LightSet.Hash(42u));
    }

    [Fact]
    public void Pack_RoundTripsColourAndIntensity()
    {
        uint packed = LightSet.Pack((12, 200, 255), 4.0);
        Assert.Equal(12u, packed & 255);
        Assert.Equal(200u, (packed >> 8) & 255);
        Assert.Equal(255u, (packed >> 16) & 255);
        Assert.Equal(64u, packed >> 24);                                   // intensity / 16 resolution
        Assert.Equal(1u, LightSet.Pack((0, 0, 0), 0) >> 24);               // never zero
        Assert.Equal(255u, LightSet.Pack((0, 0, 0), 100) >> 24);           // clamped
    }

    [Fact]
    public void ColourSchemes_CoverTheirHashRange()
    {
        var window = Enumerable.Range(0, 10).Select(i => LightSet.WindowColor((uint)i)).Distinct().ToList();
        var street = Enumerable.Range(0, 10).Select(i => LightSet.StreetColor((uint)i)).Distinct().ToList();
        Assert.Equal(3, window.Count); Assert.Equal(3, street.Count);
    }

    [Fact]
    public void Add_TracksRadius_AndFinishSortsByLevel()
    {
        var set = new LightSet(LightKind.Lamp, new double3(1000, 0, 0), 30f);
        for (uint i = 0; i < 300; i++)
            set.Add(new double3(1000 + i % 20, i / 20, 0), 2f, 0xFFFFFFFF, i);
        Assert.Equal(300, set.Count);
        Assert.False(set.Sorted);
        set.Finish();
        Assert.True(set.Sorted);
        for (int i = 1; i < set.Count; i++)
            Assert.True(set.Points[i - 1].Level <= set.Points[i].Level);
        Assert.All(set.Points.Take(set.Count), p => Assert.InRange(p.Level, 0f, 1f));
        Assert.True(set.Radius >= Math.Sqrt(19 * 19 + 14 * 14) + 2 - 1e-6);
    }
}

public class BuildingDetailTests
{
    private static BuildingInstance Wall(double la, double lb, double height, int variant = 1, double groundOffset = 1)
    {
        var b = new RawBuilding { Id = 3, Lat = 40, Lon = -100, Height = height, Variant = variant, Style = Buildings.StyleOffice };
        return Placement.MakeInstance(b, new Box2(0, 0, 0.3, la, lb), Geo.R, groundOffset, Geo.Flat());
    }

    private static List<BuildingInstance> Detail(BuildingInstance wall, RawBuilding b)
    {
        var tile = new Tile();
        BuildingDetail.Add(tile, wall, b);
        return tile.Instances;
    }

    private static RawBuilding Raw(double height, int variant = 1, double roof = 0) =>
        new() { Id = 3, Lat = 40, Lon = -100, Height = height, Variant = variant, RoofHeight = roof, RoofVariant = Buildings.FirstRoofVariant };

    [Fact]
    public void FlatRoofedOffice_GetsPlinthEntranceParapetAndRoofUnits_AllValid()
    {
        var pieces = Detail(Wall(40, 25, 15), Raw(15));
        Assert.True(pieces.Count >= 10, $"only {pieces.Count} pieces");
        foreach (var p in pieces)
        {
            Assert.True(Geo.Finite(p));
            Assert.True(p.IsDetail);
            Assert.False(p.IsRoad);
            Assert.InRange(p.Variant, 0, Buildings.VariantCount - 1);
            Assert.True(Geo.Len(p.Up) > 0 && Geo.Len(p.AxisA) > 0 && Geo.Len(p.AxisB) > 0);
            Assert.True(Geo.Dist(p.Base, Geo.Dir(40, -100) * Geo.R) < 100, "pieces stay at the building");
            Assert.Equal(Buildings.StyleNone, p.Style);
        }
    }

    [Fact]
    public void Details_AreDeterministic()
    {
        var wall = Wall(40, 25, 15);
        var a = Detail(wall, Raw(15)); var b = Detail(wall, Raw(15));
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) { Assert.Equal(a[i].Base, b[i].Base); Assert.Equal(a[i].Variant, b[i].Variant); }
    }

    [Fact]
    public void PitchedRoof_GetsAChimneyAndNoParapet()
    {
        var flat = Detail(Wall(12, 9, 6), Raw(6));
        var pitched = Detail(Wall(12, 9, 6), Raw(6, roof: 3));
        Assert.True(pitched.Count < flat.Count);
        Assert.Contains(pitched, p => p.Variant == 3);   // brick chimney
    }

    [Fact]
    public void ThingsThatGetNoDetail()
    {
        Assert.Empty(Detail(Wall(3, 3, 6), Raw(6)));                                  // too small
        Assert.Empty(Detail(Wall(20, 20, 2), Raw(2)));                                // too low
        var slab = Raw(6); slab.Slab = true;
        Assert.Empty(Detail(Wall(20, 20, 6), slab));
        var floating = Raw(10); floating.MinHeight = 4;
        Assert.Empty(Detail(Wall(20, 20, 10), floating));
        var road = Wall(20, 20, 6, variant: Buildings.RoadVariant);
        Assert.Empty(Detail(road, Raw(6, Buildings.RoadVariant)));
    }

    [Fact]
    public void Tank_GetsFootRingBandsLadderAndPipe()
    {
        var wall = Wall(14, 14, 16, Buildings.TankVariant, 0.5);
        var pieces = Detail(wall, Raw(16, Buildings.TankVariant));
        Assert.True(pieces.Count >= 4, "foot ring, ladder, pipe and at least one band");
        Assert.All(pieces, p => Assert.True(p.IsDetail && Geo.Finite(p)));
        Assert.Contains(pieces, p => Geo.Len(p.AxisA) > 14 + 1);       // foot ring wider than the shell
        // detail on a detail is not added again
        var detail = wall; detail.IsDetail = true;
        Assert.Empty(Detail(detail, Raw(16, Buildings.TankVariant)));
    }
}
