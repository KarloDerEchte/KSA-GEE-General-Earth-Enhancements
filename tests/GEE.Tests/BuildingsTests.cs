using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

public class BuildingsTests
{
    private static readonly OsmOptions Options = new();
    private const double Lat = 52.5, Lon = 13.4;

    /// <summary>Ring of a w x h metre rectangle around (Lat, Lon), as the vector tile reader produces it.</summary>
    private static List<(double Lat, double Lon)> RectRing(double w, double h)
    {
        double dLat = h / 2 / Geo.MetersPerDegree, dLon = w / 2 / (Geo.MetersPerDegree * Math.Cos(Lat * Math.PI / 180));
        return new() { (Lat - dLat, Lon - dLon), (Lat - dLat, Lon + dLon), (Lat + dLat, Lon + dLon), (Lat + dLat, Lon - dLon) };
    }

    private static List<(double Lat, double Lon)> Circle(int corners, double radius, double jitter = 0)
    {
        var rng = new Random(5);
        var ring = new List<(double, double)>();
        for (int i = 0; i < corners; i++)
        {
            double a = 2 * Math.PI * i / corners, r = radius * (1 + jitter * (rng.NextDouble() * 2 - 1));
            ring.Add((Lat + r * Math.Sin(a) / Geo.MetersPerDegree, Lon + r * Math.Cos(a) / (Geo.MetersPerDegree * Math.Cos(Lat * Math.PI / 180))));
        }
        return ring;
    }

    [Fact]
    public void FromRing_Rectangle_GivesOneBoxOfThatSize_WithGivenHeight()
    {
        var b = Buildings.FromRing(1, RectRing(30, 18), 21, 0, null, Options)!;
        var box = Assert.Single(b.Boxes);
        Assert.Equal(30, Math.Max(box.LengthA, box.LengthB), 1);
        Assert.Equal(18, Math.Min(box.LengthA, box.LengthB), 1);
        Assert.Equal(21, b.Height);
        Assert.Equal(Lat, b.Lat, 6);
    }

    [Fact]
    public void FromRing_RemovesTheClosingPoint_AndRejectsTinyOrDegenerateRings()
    {
        var closed = RectRing(30, 18); closed.Add(closed[0]);
        Assert.NotNull(Buildings.FromRing(1, closed, 10, 0, null, Options));
        Assert.Null(Buildings.FromRing(1, RectRing(1.5, 1.5), 10, 0, null, Options));        // 2 m2
        Assert.Null(Buildings.FromRing(1, RectRing(30, 18).Take(2).ToList(), 10, 0, null, Options));
        Assert.Null(Buildings.FromRing(1, new List<(double, double)>(), 10, 0, null, Options));
    }

    [Theory]
    [InlineData(0.0, 0.0, 2.5, 0.0)]
    [InlineData(1500.0, 0.0, 900.0, 0.0)]
    [InlineData(12.0, 30.0, 12.0, 11.0)]    // MinHeight is limited to height - 1
    public void FromRing_ClampsHeights(double height, double minHeight, double expectedMaxHeight, double expectedMaxMin)
    {
        var b = Buildings.FromRing(1, RectRing(40, 40), height, minHeight, null, Options)!;
        Assert.InRange(b.Height, 2.5, 900);
        if (height > 0) Assert.True(b.Height <= expectedMaxHeight + 1e-9);
        Assert.InRange(b.MinHeight, 0, Math.Max(expectedMaxMin, 0) + 1e-9);
        Assert.True(b.MinHeight <= b.Height);
    }

    [Fact]
    public void FromRing_UnknownHeight_IsEstimated_DeterministicallyAndInTheAreaRange()
    {
        var a = Buildings.FromRing(9, RectRing(20, 20), 5, 0, null, Options)!;    // OMT's 5 m default means "unknown"
        var b = Buildings.FromRing(9, RectRing(20, 20), 5, 0, null, Options)!;
        Assert.Equal(a.Height, b.Height);
        Assert.Equal(a.Variant, b.Variant);
        Assert.InRange(a.Height, 6.0 - 3.5, 15.0 + 3.5);   // 400 m2: 6..15 m (a pitched roof does not take from an estimate)
    }

    [Theory]
    [InlineData(20, 0.0, 3.6)]
    [InlineData(100, 3.0, 6.2)]
    [InlineData(500, 6.0, 15.0)]
    [InlineData(2000, 8.0, 22.0)]
    [InlineData(10000, 7.0, 14.0)]
    public void EstimateHeight_StaysInItsBucket_ForEveryRandomValue(double area, double lo, double hi)
    {
        for (double u = 0; u < 1; u += 0.05)
        {
            double h = Buildings.EstimateHeight(area, u);
            Assert.InRange(h, Math.Max(lo, 2.6), hi + 1e-9);
        }
    }

    [Fact]
    public void RoundOutline_DetectsCirclesOnly()
    {
        double Area(List<(double Lat, double Lon)> r) => Math.Abs(Footprint.SignedArea(r.Select(p => new Footprint.P((p.Lon - Lon) * Geo.MetersPerDegree * Math.Cos(Lat * Math.PI / 180), (p.Lat - Lat) * Geo.MetersPerDegree)).ToList()));
        Footprint.P Local((double Lat, double Lon) p) => new((p.Lon - Lon) * Geo.MetersPerDegree * Math.Cos(Lat * Math.PI / 180), (p.Lat - Lat) * Geo.MetersPerDegree);

        var circle = Circle(24, 10);
        var round = Buildings.RoundOutline(circle.Select(Local).ToList(), Area(circle));
        Assert.NotNull(round);
        Assert.Equal(10, round!.Value.Radius, 1);

        var octagon = Circle(8, 10);
        Assert.Null(Buildings.RoundOutline(octagon.Select(Local).ToList(), Area(octagon)));
        var bumpy = Circle(24, 10, 0.2);
        Assert.Null(Buildings.RoundOutline(bumpy.Select(Local).ToList(), Area(bumpy)));
        var tiny = Circle(24, 1.5);
        Assert.Null(Buildings.RoundOutline(tiny.Select(Local).ToList(), Area(tiny)));
    }

    [Fact]
    public void FromRing_RoundTank_GetsTheTankVariant_AndASingleSquareBox()
    {
        var tank = Buildings.FromRing(3, Circle(32, 9), 0, 0, null, Options)!;
        Assert.Equal(Buildings.TankVariant, tank.Variant);
        var box = Assert.Single(tank.Boxes);
        Assert.Equal(18, box.LengthA, 0);
        Assert.Equal(box.LengthA, box.LengthB, 9);
        Assert.Equal(Buildings.StyleNone, tank.Style);
        Assert.InRange(tank.Height, 4, 26);
    }

    [Fact]
    public void FromRing_SmallHouse_GetsAPitchedRoofOverTheShortSide()
    {
        var b = Buildings.FromRing(11, RectRing(12, 9), 7, 0, "#decdd0", Options)!;   // plaster, a roof colour variant
        Assert.True(b.RoofHeight > 0);
        double minPitch = Math.Tan(28 * Math.PI / 180) * 9 / 2, maxPitch = Math.Tan(42 * Math.PI / 180) * 9 / 2;
        Assert.InRange(b.RoofHeight, minPitch - 0.1, maxPitch + 0.1);
        Assert.InRange(b.RoofVariant, Buildings.FirstRoofVariant, Buildings.FirstRoofVariant + Buildings.RoofVariants - 1);
        Assert.True(b.Height >= 2.6 && b.Height < 7, "the roof takes from the wall height");
    }

    [Fact]
    public void FromRing_BigBuildingAndTowers_GetNoRoof()
    {
        Assert.Equal(0, Buildings.FromRing(1, RectRing(60, 40), 9, 0, "#decdd0", Options)!.RoofHeight);
        Assert.Equal(0, Buildings.FromRing(1, RectRing(12, 9), 50, 0, "#decdd0", Options)!.RoofHeight);
        Assert.Equal(0, Buildings.FromRing(1, RectRing(12, 9), 7, 3, "#decdd0", Options)!.RoofHeight);   // floating part
    }

    [Theory]
    [InlineData("#ffffff", 1)]
    [InlineData("#f00", 3)]
    [InlineData("red", 3)]
    [InlineData("#b2b0aa", 0)]
    [InlineData("grey", 0)]      // (170,170,170) is nearest to concrete (178,176,170)
    [InlineData("silver", 0)]
    [InlineData("black", 4)]
    public void Colour_PicksTheNearestFacadeVariant(string colour, int expected)
    {
        Assert.Equal(expected, Buildings.Variant(1, colour, 10, 100));
    }

    [Fact]
    public void Variant_WithoutColour_IsDeterministic_AndFromTheShapeBasedChoices()
    {
        for (long seed = 0; seed < 500; seed++)
        {
            Assert.Equal(Buildings.Variant(seed, null, 50, 100), Buildings.Variant(seed, null, 50, 100));
            Assert.Contains(Buildings.Variant(seed, null, 50, 100), new[] { 0, 4, 5, 1 });           // towers
            Assert.Contains(Buildings.Variant(seed, null, 8, 3000), new[] { 6, 0, 4 });                // halls
            Assert.Contains(Buildings.Variant(seed, null, 8, 100), new[] { 1, 2, 3, 7, 0 });           // houses
        }
        // all choices are actually used
        Assert.Equal(5, Enumerable.Range(0, 500).Select(i => Buildings.Variant(i, null, 8, 100)).Distinct().Count());
    }

    [Fact]
    public void VariantTable_IsConsistent()
    {
        int[] ids =
        {
            Buildings.RoadVariant, Buildings.FirstLandmarkVariant, Buildings.FirstRoofVariant, Buildings.RunwayVariant, Buildings.TaxiwayVariant,
            Buildings.ApronVariant, Buildings.TankVariant, Buildings.TankCapVariant, Buildings.HorizontalTankVariant, Buildings.TentVariant,
            Buildings.MetalRoofVariant, Buildings.CrewArmVariant, Buildings.RoadPlainVariant, Buildings.HighwayVariant, Buildings.StreetVariant,
            Buildings.RampVariant, Buildings.TrackVariant, Buildings.Highway3Variant, Buildings.Highway4Variant, Buildings.GoreVariant,
            Buildings.JunctionPaintVariant, Buildings.FurnitureVariant,
        };
        Assert.All(ids, id => Assert.InRange(id, 0, Buildings.VariantCount - 1));
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.True(Buildings.VariantCount > ids.Max());
    }
}
