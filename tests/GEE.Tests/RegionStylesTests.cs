using Xunit;

namespace GEE.Tests;

public class RegionStylesTests
{
    private static readonly OsmOptions Options = new();

    private static List<(double Lat, double Lon)> RectRing(double lat, double lon, double w, double h)
    {
        double dLat = h / 2 / Geo.MetersPerDegree, dLon = w / 2 / (Geo.MetersPerDegree * Math.Cos(lat * Math.PI / 180));
        return new() { (lat - dLat, lon - dLon), (lat - dLat, lon + dLon), (lat + dLat, lon + dLon), (lat + dLat, lon - dLon) };
    }

    [Theory]
    [InlineData("New York", 40.71, -74.0, Region.NorthAmerica)]
    [InlineData("Chicago", 41.88, -87.63, Region.NorthAmerica)]
    [InlineData("Toronto", 43.65, -79.38, Region.NorthAmerica)]
    [InlineData("Seattle", 47.6, -122.33, Region.NorthAmerica)]
    [InlineData("Honolulu", 21.3, -157.86, Region.NorthAmerica)]
    [InlineData("Los Angeles", 34.05, -118.24, Region.SunBelt)]
    [InlineData("Phoenix", 33.45, -112.07, Region.SunBelt)]
    [InlineData("Houston", 29.76, -95.37, Region.SunBelt)]
    [InlineData("Kennedy Space Center", 28.57, -80.65, Region.SunBelt)]
    [InlineData("Miami", 25.77, -80.19, Region.SunBelt)]
    [InlineData("Berlin", 52.52, 13.4, Region.Europe)]
    [InlineData("London", 51.5, -0.12, Region.Europe)]
    [InlineData("Paris", 48.86, 2.35, Region.Europe)]
    [InlineData("Warsaw", 52.23, 21.01, Region.Europe)]
    [InlineData("Oslo", 59.91, 10.75, Region.Europe)]
    [InlineData("Milan", 45.46, 9.19, Region.Europe)]
    [InlineData("Madrid", 40.42, -3.7, Region.Mediterranean)]
    [InlineData("Rome", 41.9, 12.5, Region.Mediterranean)]
    [InlineData("Athens", 37.98, 23.73, Region.Mediterranean)]
    [InlineData("Lisbon", 38.72, -9.14, Region.Mediterranean)]
    [InlineData("Istanbul", 41.01, 28.98, Region.Mediterranean)]
    [InlineData("Cairo", 30.04, 31.24, Region.MiddleEast)]
    [InlineData("Dubai", 25.2, 55.27, Region.MiddleEast)]
    [InlineData("Riyadh", 24.71, 46.68, Region.MiddleEast)]
    [InlineData("Casablanca", 33.57, -7.59, Region.MiddleEast)]
    [InlineData("Tokyo", 35.68, 139.69, Region.EastAsia)]
    [InlineData("Shanghai", 31.23, 121.47, Region.EastAsia)]
    [InlineData("Seoul", 37.57, 126.98, Region.EastAsia)]
    [InlineData("Beijing", 39.9, 116.4, Region.EastAsia)]
    [InlineData("Moscow", 55.75, 37.62, Region.Default)]
    [InlineData("Sydney", -33.87, 151.2, Region.Default)]
    [InlineData("Sao Paulo", -23.55, -46.63, Region.Default)]
    [InlineData("Mumbai", 19.08, 72.88, Region.Default)]
    [InlineData("Mexico City", 19.43, -99.13, Region.Default)]
    [InlineData("Lagos", 6.52, 3.38, Region.Default)]
    [InlineData("Mid Atlantic", 30, -40, Region.Default)]
    public void KnownCities_MapToTheirRegion(string city, double lat, double lon, Region expected)
    {
        Assert.Equal(expected, RegionStyles.RegionOf(lat, lon));
        Assert.NotNull(city);
    }

    [Fact]
    public void Table_IsIndexedByRegion_AndWeightsAreValid()
    {
        Assert.Equal(Enum.GetValues<Region>().Length, RegionStyles.Table.Length);
        for (int i = 0; i < RegionStyles.Table.Length; i++)
        {
            var s = RegionStyles.Table[i];
            Assert.Equal((Region)i, s.Region);
            Assert.Equal(Buildings.RoofVariants, s.RoofWeights.Length);
            Assert.True(s.RoofWeights.Sum() > 0);
            if (s.FacadeWeights != null)
            {
                Assert.Equal(RegionStyles.FacadeCount, s.FacadeWeights.Length);
                Assert.True(s.FacadeWeights.Sum() > 0);
                Assert.All(s.FacadeWeights, w => Assert.True(w >= 0));
            }
            Assert.True(s.HousePitchMin <= s.HousePitchMax && s.BlockPitchMin <= s.BlockPitchMax);
            Assert.InRange(s.HouseShare, 0, 1);
            Assert.InRange(s.BlockShare, 0, 1);
            Assert.InRange(s.HouseHip, 0, 1);
            Assert.InRange(s.BlockHip, 0, 1);
        }
        Assert.Equal(Buildings.Variants.Length + Buildings.ExtraFacades.Length, RegionStyles.FacadeCount);
        Assert.Null(RegionStyles.Generic.FacadeWeights);
        Assert.Equal(new double[] { 15, 45, 40 }, RegionStyles.Generic.RoofWeights);
    }

    [Fact]
    public void At_IsDeterministic_AndFarFromBordersEqualsTheRegion()
    {
        foreach (var (lat, lon) in new[] { (52.5, 13.4), (35.7, 139.7), (30.0, 31.2), (-33.9, 151.2), (40.7, -74.0) })
            for (long seed = 0; seed < 50; seed++)
                Assert.Same(RegionStyles.At(lat, lon, seed), RegionStyles.At(lat, lon, seed));
        for (long seed = 0; seed < 200; seed++)
        {
            Assert.Same(RegionStyles.StyleOf(Region.Europe), RegionStyles.At(50.0, 10.0, seed));
            Assert.Same(RegionStyles.StyleOf(Region.EastAsia), RegionStyles.At(35.7, 139.7, seed));
            Assert.Same(RegionStyles.Generic, RegionStyles.At(-33.9, 151.2, seed));
        }
    }

    [Fact]
    public void Borders_AreRagged_AndMixAboutHalfAtTheEdge()
    {
        // The Europe/Mediterranean border near lat 44.2 at lon 5: just north is Europe, just south Mediterranean.
        int europeNorth = 0, europeSouth = 0, n = 2000;
        for (int i = 0; i < n; i++)
        {
            if (RegionStyles.At(44.3, 5, i).Region == Region.Europe) europeNorth++;
            if (RegionStyles.At(44.1, 5, i).Region == Region.Europe) europeSouth++;
        }
        Assert.InRange(europeNorth / (double)n, 0.40, 0.60);
        Assert.InRange(europeSouth / (double)n, 0.40, 0.60);
        // Deeper inside the feather zone the home region dominates.
        int deep = 0;
        for (int i = 0; i < n; i++)
            if (RegionStyles.At(45.4, 5, i).Region == Region.Europe) deep++;
        Assert.InRange(deep / (double)n, 0.85, 0.99);
        // Beyond the feather width nothing mixes.
        for (int i = 0; i < 200; i++)
            Assert.Equal(Region.Europe, RegionStyles.At(46.0, 5, i).Region);
    }

    [Fact]
    public void Pick_FollowsWeights_AndSkipsZeroes()
    {
        var w = new double[] { 0, 1, 0, 3 };
        var counts = new int[4];
        for (int i = 0; i < 4000; i++)
            counts[RegionStyles.Pick(w, (i + 0.5) / 4000)]++;
        Assert.Equal(0, counts[0]);
        Assert.Equal(0, counts[2]);
        Assert.Equal(1000.0, counts[1], 2);
        Assert.Equal(3000.0, counts[3], 2);
        Assert.Equal(1, RegionStyles.Pick(w, 0.0));
        Assert.Equal(3, RegionStyles.Pick(w, 0.9999999));
    }

    [Fact]
    public void FacadeVariant_MapsToBaseAndExtraMaterials()
    {
        Assert.Equal(3, RegionStyles.FacadeVariant(3));
        Assert.Equal(Buildings.FirstExtraFacade, RegionStyles.FacadeVariant(8));
        Assert.Equal(Buildings.FirstExtraFacade + 7, RegionStyles.FacadeVariant(15));
    }

    private static double Share(Func<int, bool> pred, int n) => Enumerable.Range(0, n).Count(pred) / (double)n;

    [Fact]
    public void Facades_Mediterranean_AreLight_MiddleEastIsSandWhite_EastAsiaIsConcrete()
    {
        int white = Buildings.FirstExtraFacade + 6, cream = Buildings.FirstExtraFacade;
        int n = 4000;
        var med = RegionStyles.StyleOf(Region.Mediterranean);
        double light = Share(i => Buildings.Variant(i, null, 9, 100, med) is int v && (v == white || v == cream || v == 2), n);
        Assert.InRange(light, 0.45, 0.70);
        Assert.Equal(0, Share(i => Buildings.Variant(i, null, 9, 100, med) == 3, n));        // no brick
        var me = RegionStyles.StyleOf(Region.MiddleEast);
        Assert.True(Share(i => Buildings.Variant(i, null, 9, 100, me) is 2 || Buildings.Variant(i, null, 9, 100, me) == white || Buildings.Variant(i, null, 9, 100, me) == cream, n) > 0.65);
        var ea = RegionStyles.StyleOf(Region.EastAsia);
        Assert.True(Share(i => Buildings.Variant(i, null, 9, 100, ea) == 0, n) > 0.25);
        // Europe still has plenty of brick/plaster.
        var eu = RegionStyles.StyleOf(Region.Europe);
        Assert.True(Share(i => Buildings.Variant(i, null, 9, 100, eu) == 3, n) > 0.08);
    }

    [Fact]
    public void Facades_TallAndIndustrialKeepTheirOwnChoices_AndColourTagsWin()
    {
        var med = RegionStyles.StyleOf(Region.Mediterranean);
        for (long seed = 0; seed < 300; seed++)
        {
            Assert.Equal(Buildings.Variant(seed, null, 60, 100), Buildings.Variant(seed, null, 60, 100, med));
            Assert.Equal(Buildings.Variant(seed, null, 8, 3000), Buildings.Variant(seed, null, 8, 3000, med));
            Assert.Equal(Buildings.Variant(seed, "#b2b0aa", 8, 100), Buildings.Variant(seed, "#b2b0aa", 8, 100, med));
        }
    }

    [Fact]
    public void Variant_WithoutRegion_IsTheGenericList()
    {
        // The generic palette contains brick, plaster, sand, ochre-free paint colours; just check it varies and is stable.
        var set = new HashSet<int>();
        for (long seed = 0; seed < 500; seed++)
            set.Add(Buildings.Variant(seed, null, 8, 100, RegionStyles.Generic));
        Assert.True(set.Count >= 8);
        for (long seed = 0; seed < 500; seed++)
            Assert.Equal(Buildings.Variant(seed, null, 8, 100), Buildings.Variant(seed, null, 8, 100, RegionStyles.Generic));
    }

    private sealed record Stats(double Roofed, double Hip, double MinPitchDeg, double MaxPitchDeg, double[] RoofColours);

    private static Stats HouseStats(double lat, double lon, double w = 12, double h = 9, int n = 600)
    {
        int roofed = 0, hip = 0;
        double min = 1000, max = 0;
        var colours = new double[Buildings.RoofVariants];
        for (int i = 0; i < n; i++)
        {
            // A light plaster tag keeps the facade pitched-roof capable in every region.
            var b = Buildings.FromRing(5000 + i, RectRing(lat, lon, w, h), 6, 0, "#decdd0", Options)!;
            if (b.RoofPitch <= 0)
                continue;
            roofed++;
            if (b.RoofHip) hip++;
            double deg = b.RoofPitch * 180 / Math.PI;
            min = Math.Min(min, deg);
            max = Math.Max(max, deg);
            colours[b.RoofVariant - Buildings.FirstRoofVariant]++;
        }
        return new Stats(roofed / (double)n, roofed == 0 ? 0 : hip / (double)roofed, min, max, colours.Select(c => c / Math.Max(roofed, 1)).ToArray());
    }

    [Fact]
    public void Houses_DefaultRegion_KeepTheOriginalNumbers()
    {
        var s = HouseStats(-33.9, 151.2);                        // outside every box
        Assert.Equal(1.0, s.Roofed);
        Assert.InRange(s.MinPitchDeg, 28, 29);
        Assert.InRange(s.MaxPitchDeg, 41, 42.0001);
        Assert.InRange(s.Hip, 0.33, 0.47);
        Assert.InRange(s.RoofColours[0], 0.10, 0.20);
        Assert.InRange(s.RoofColours[1], 0.39, 0.51);
        Assert.InRange(s.RoofColours[2], 0.34, 0.46);
    }

    [Fact]
    public void Houses_Europe_AreSteepAndRed()
    {
        var s = HouseStats(52.5, 13.4);
        Assert.Equal(1.0, s.Roofed);
        Assert.InRange(s.MinPitchDeg, 32, 33);
        Assert.InRange(s.MaxPitchDeg, 47, 48.0001);
        Assert.True(s.RoofColours[0] > 0.4);
    }

    [Fact]
    public void Houses_Mediterranean_AreShallowTerracotta()
    {
        var s = HouseStats(41.9, 12.5);
        Assert.InRange(s.Roofed, 0.78, 0.92);
        Assert.InRange(s.MinPitchDeg, 14, 15);
        Assert.InRange(s.MaxPitchDeg, 21, 22.0001);
        Assert.True(s.RoofColours[0] > 0.75);
    }

    [Fact]
    public void Houses_SunBelt_AreLowAndOftenFlat()
    {
        var s = HouseStats(34.05, -118.24, 20, 12);
        Assert.InRange(s.Roofed, 0.62, 0.78);
        Assert.InRange(s.MaxPitchDeg, 20, 24.0001);
        Assert.True(s.MinPitchDeg >= 14);
        Assert.True(s.Hip > 0.45);
    }

    [Fact]
    public void Houses_NorthAmerica_AreWide_AndShingleColoured()
    {
        // 20 x 20 m = 400 m2 is a block in the generic and European styles but still a house here.
        var s = HouseStats(41.88, -87.63, 20, 20);
        Assert.InRange(s.Roofed, 0.9, 1.0);
        Assert.InRange(s.MinPitchDeg, 20, 21);
        Assert.InRange(s.MaxPitchDeg, 33, 34.0001);
        Assert.True(s.RoofColours[1] > 0.5 && s.RoofColours[0] < 0.1);
        var eu = HouseStats(52.5, 13.4, 20, 20);                // blocks there: shallow or flat
        Assert.True(eu.MaxPitchDeg <= 32.0001);
    }

    [Fact]
    public void Houses_MiddleEast_AreFlat()
    {
        Assert.Equal(0.0, HouseStats(30.04, 31.24).Roofed);
        Assert.Equal(0.0, HouseStats(25.2, 55.27, 30, 16).Roofed);
    }

    [Fact]
    public void Houses_EastAsia_AreMostlyFlat_WithGreyTiles()
    {
        var s = HouseStats(35.68, 139.69);
        Assert.InRange(s.Roofed, 0.32, 0.48);
        Assert.True(s.Hip > 0.55);
        Assert.True(s.RoofColours[1] > 0.55);
    }

    [Fact]
    public void FromRing_IsDeterministicPerLocation()
    {
        var a = Buildings.FromRing(77, RectRing(48.86, 2.35, 12, 9), 6, 0, null, Options)!;
        var b = Buildings.FromRing(77, RectRing(48.86, 2.35, 12, 9), 6, 0, null, Options)!;
        Assert.Equal(a.Variant, b.Variant);
        Assert.Equal(a.RoofPitch, b.RoofPitch);
        Assert.Equal(a.RoofVariant, b.RoofVariant);
        Assert.Equal(a.RoofHip, b.RoofHip);
    }

    [Fact]
    public void Settings_RegionalStyles_DefaultsOn()
    {
        Assert.True(new Settings().RegionalStyles);
    }
}
