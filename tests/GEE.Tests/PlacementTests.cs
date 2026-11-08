using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

public class PlacementTests
{
    private const double R = Geo.R;

    private static RawBuilding Building(double lat = 40, double lon = -100, double height = 12, double minHeight = 0, int variant = 1) =>
        new() { Id = 77, Lat = lat, Lon = lon, Height = height, MinHeight = minHeight, Variant = variant, Style = Buildings.StyleOffice };

    // ---- direction / frame ----

    [Theory]
    [InlineData(0, 0, 1, 0, 0)]
    [InlineData(0, 90, 0, 1, 0)]
    [InlineData(90, 0, 0, 0, 1)]
    [InlineData(-90, 33, 0, 0, -1)]
    public void DirCcf_Golden(double lat, double lon, double x, double y, double z)
    {
        var d = Placement.DirCcf(lat, lon);
        Assert.Equal(x, d.X, 9); Assert.Equal(y, d.Y, 9); Assert.Equal(z, d.Z, 9);
    }

    [Fact]
    public void Axes_AreOrthonormalAndRightHanded_EverywhereButTheExactPoles()
    {
        for (double lat = -89; lat <= 89; lat += 11.5)
            for (double lon = -180; lon <= 180; lon += 37)
            {
                var up = Placement.DirCcf(lat, lon);
                Placement.Axes(up, out var east, out var north);
                Assert.Equal(1, Geo.Len(east), 9); Assert.Equal(1, Geo.Len(north), 9);
                Assert.Equal(0, double3.Dot(east, north), 9);
                Assert.Equal(0, double3.Dot(east, up), 9);
                Assert.Equal(0, double3.Dot(north, up), 9);
                Assert.Equal(1, double3.Dot(double3.Cross(east, north), up), 9);   // east x north = up
                Assert.True(north.Z > 0, "north points toward the pole");
            }
    }

    [Fact]
    public void Axes_Golden_AtTheEquatorAndPrimeMeridian()
    {
        Placement.Axes(new double3(1, 0, 0), out var east, out var north);
        Assert.Equal(0, east.X, 12); Assert.Equal(1, east.Y, 12); Assert.Equal(0, east.Z, 12);
        Assert.Equal(1, north.Z, 12);
    }

    // ---- building boxes ----

    [Fact]
    public void MakeInstance_OnFlatGround_StandsOnTheSurface()
    {
        var b = Building(height: 12);
        var box = new Box2(3, -4, 0.7, 20, 10);
        var inst = Placement.MakeInstance(b, box, R, 1.0, Geo.Flat(0));
        Assert.True(Geo.Finite(inst));
        Assert.Equal(20, Geo.Len(inst.AxisA), 6);
        Assert.Equal(10, Geo.Len(inst.AxisB), 6);
        Assert.Equal(13, Geo.Len(inst.Up), 6);                    // 12 m wall + 1 m foundation below ground
        Assert.Equal(R - 1, Geo.Len(inst.Base), 4);
        Assert.Equal(1.0, inst.GroundOffset, 6);
        Assert.Equal(R, Geo.Len(inst.Center), 4);
        // axes orthogonal to each other and to up
        var up = double3.Normalize(inst.Up);
        Assert.Equal(0, double3.Dot(double3.Normalize(inst.AxisA), double3.Normalize(inst.AxisB)), 9);
        Assert.Equal(0, double3.Dot(double3.Normalize(inst.AxisA), up), 9);
        Assert.Equal(1, double3.Dot(double3.Cross(inst.AxisA, inst.AxisB), up) / (20 * 10), 9);   // right-handed: A x B = up
    }

    [Fact]
    public void MakeInstance_BoxCenterIsAtItsEastNorthOffset()
    {
        var b = Building();
        var inst = Placement.MakeInstance(b, new Box2(50, 30, 0, 8, 8), R, 0, Geo.Flat(0));
        var refDir = Placement.DirCcf(b.Lat, b.Lon);
        Placement.Axes(refDir, out var east, out var north);
        var expected = refDir * R + east * 50 + north * 30;
        Assert.True(Geo.Dist(inst.Center, expected) < 0.5, $"off by {Geo.Dist(inst.Center, expected)} m");   // sphere curvature over 58 m: 0.26 m
    }

    [Fact]
    public void MakeInstance_AngleTurnsAxisAFromEastTowardNorth()
    {
        var b = Building();
        var e = Placement.MakeInstance(b, new Box2(0, 0, 0, 10, 4), R, 0, Geo.Flat());
        var n = Placement.MakeInstance(b, new Box2(0, 0, Math.PI / 2, 10, 4), R, 0, Geo.Flat());
        Placement.Axes(Placement.DirCcf(b.Lat, b.Lon), out var east, out var north);
        Assert.Equal(1, double3.Dot(double3.Normalize(e.AxisA), east), 6);
        Assert.Equal(1, double3.Dot(double3.Normalize(n.AxisA), north), 6);
    }

    [Fact]
    public void MakeInstance_OnSlope_ReachesBelowTheLowestCorner_AndRoofClearsTheHighest()
    {
        // 25 % slope rising to the east: the lowest corner is at the west side
        var b = Building(height: 8);
        Placement.Axes(Placement.DirCcf(b.Lat, b.Lon), out var east, out _);
        var refDir = Placement.DirCcf(b.Lat, b.Lon);
        Func<double3, double> slope = d => 0.25 * double3.Dot(d - refDir, east) * R + 100;
        var box = new Box2(0, 0, 0.3, 24, 16);
        var inst = Placement.MakeInstance(b, box, R, 0.5, slope);
        Assert.True(Geo.Finite(inst));
        foreach (var corner in Geo.Corners(inst))
            Assert.True(Geo.Len(corner) > 0);
        // every bottom corner of the box lies at or below the terrain beneath it
        for (int i = 0; i < 4; i++)
        {
            double sa = (i & 1) == 0 ? -0.5 : 0.5, sb = (i & 2) == 0 ? -0.5 : 0.5;
            var p = inst.Base + inst.AxisA * sa + inst.AxisB * sb;
            double terrain = slope(double3.Normalize(p));
            Assert.True(Geo.Len(p) - R <= terrain + 1e-6, $"corner {i} sticks out of the ground");
        }
        // the roof is at least ground + building height at the centre
        double roof = Geo.Len(inst.Base + inst.Up) - R;
        Assert.True(roof >= slope(double3.Normalize(inst.Center)) + 8 - 1e-6);
    }

    [Fact]
    public void MakeInstance_BelowSeaLevel_KeepsTheRoofAboveWater_AndStaysFinite()
    {
        var inst = Placement.MakeInstance(Building(height: 9), new Box2(0, 0, 0, 10, 10), R, 1, Geo.Flat(-40));
        Assert.True(Geo.Finite(inst));
        Assert.True(Geo.Len(inst.Base + inst.Up) - R >= 9 - 1e-6, "roof must stay a full building height above sea level");
    }

    [Fact]
    public void MakeInstance_FloatingPart_UsesMinHeight()
    {
        var inst = Placement.MakeInstance(Building(height: 20, minHeight: 12), new Box2(0, 0, 0, 10, 10), R, 1, Geo.Flat(0));
        Assert.Equal(R + 12, Geo.Len(inst.Base), 4);
        Assert.Equal(8, Geo.Len(inst.Up), 4);
    }

    [Fact]
    public void MakeInstance_SeedIsStablePerBuilding()
    {
        var a = Placement.MakeInstance(Building(), new Box2(0, 0, 0, 10, 10), R, 0, Geo.Flat());
        var b = Placement.MakeInstance(Building(), new Box2(5, 5, 1, 12, 12), R, 0, Geo.Flat());
        Assert.Equal(a.Seed, b.Seed);   // same building, so the same colours and windows for all its boxes
        var other = Building(); other.Id = 78;
        Assert.NotEqual(a.Seed, Placement.MakeInstance(other, new Box2(0, 0, 0, 10, 10), R, 0, Geo.Flat()).Seed);
    }

    // ---- roof / dome / slab ----

    [Theory]
    [InlineData(20, 10)]
    [InlineData(10, 20)]
    [InlineData(12, 12)]
    public void MakeRoof_RidgeFollowsTheLongSide_KeepsHandedness_AndSitsOnTheWall(double la, double lb)
    {
        var b = Building(variant: 1); b.RoofHeight = 3.5; b.RoofVariant = Buildings.FirstRoofVariant;
        var wall = Placement.MakeInstance(b, new Box2(0, 0, 0.4, la, lb), R, 0, Geo.Flat());
        var roof = Placement.MakeRoof(wall, b);
        Assert.True(Geo.Finite(roof));
        Assert.Equal(3.5, Geo.Len(roof.Up), 6);
        Assert.Equal(Math.Max(la, lb) + 0.6, Geo.Len(roof.AxisA), 6);
        Assert.Equal(Math.Min(la, lb) + 0.8, Geo.Len(roof.AxisB), 6);
        double wallHand = Math.Sign(double3.Dot(double3.Cross(wall.AxisA, wall.AxisB), wall.Up));
        double roofHand = Math.Sign(double3.Dot(double3.Cross(roof.AxisA, roof.AxisB), roof.Up));
        Assert.Equal(wallHand, roofHand);
        // base is just under the top of the wall
        Assert.Equal(Geo.Len(wall.Base + wall.Up) - 0.02, Geo.Len(roof.Base), 4);
    }

    [Theory]
    [InlineData(20, 5, 0)]     // long: first hip shape
    [InlineData(20, 13, 1)]
    [InlineData(12, 12, 2)]    // square: pyramid
    public void HipVariant_PicksTheRidgeInsetThatFitsTheFootprint(double ridge, double across, int shape)
    {
        int gable = Buildings.FirstRoofVariant + 1;
        int hip = Placement.HipVariant(gable, across / ridge);
        Assert.Equal(Buildings.FirstHipVariant + shape * Buildings.RoofVariants + 1, hip);
    }

    [Fact]
    public void MakeRoof_OfAHipBuilding_UsesAHipVariant_AndOtherRoofsStayAsTheyAre()
    {
        var b = Building(variant: 1); b.RoofHeight = 3.5; b.RoofVariant = Buildings.FirstRoofVariant + 2; b.RoofHip = true;
        var wall = Placement.MakeInstance(b, new Box2(0, 0, 0, 20, 10), R, 0, Geo.Flat());
        Assert.Equal(Buildings.FirstHipVariant + 1 * Buildings.RoofVariants + 2 - Buildings.RoofVariants, Placement.MakeRoof(wall, b).Variant);
        Assert.Equal(2.0, Geo.Len(Placement.MakeRoof(wall, b, 2.0).Up), 6);
        b.RoofVariant = Buildings.MetalRoofVariant;
        Assert.Equal(Buildings.MetalRoofVariant, Placement.MakeRoof(wall, b).Variant);
    }

    [Fact]
    public void MakeDome_IsFortyPercentOfTheDiameterHigh_OnTopOfTheTank()
    {
        var tank = Placement.MakeInstance(Building(height: 15), new Box2(0, 0, 0, 16, 16), R, 0, Geo.Flat());
        var dome = Placement.MakeDome(tank);
        Assert.Equal(0.4 * 16, Geo.Len(dome.Up), 6);
        Assert.Equal(Buildings.TankCapVariant, dome.Variant);
        Assert.Equal(Geo.Len(tank.Base + tank.Up) - 0.02, Geo.Len(dome.Base), 4);
    }

    [Fact]
    public void MakeSlab_IsLevelAtItsMeanTerrainHeightPlusLift_ReachingBelowTheLowestCorner()
    {
        var b = Building(); b.Variant = Buildings.ApronVariant; b.Slab = true;
        var refDir = Placement.DirCcf(b.Lat, b.Lon);
        Placement.Axes(refDir, out var east, out _);
        Func<double3, double> terrain = d => 30 + 0.05 * double3.Dot(d - refDir, east) * R;
        var slab = Placement.MakeSlab(b, new Box2(0, 0, 0, 40, 40), R, 0.2, terrain);
        Assert.True(Geo.Finite(slab));
        Assert.True(slab.IsRoad);
        Assert.Equal(Layers.Street, slab.Layer);
        double top = Geo.Len(slab.Base + slab.Up) - R;
        Assert.InRange(top, 30 + 0.2 - 0.5, 30 + 0.2 + 0.5);   // mean of a symmetric slope is the centre value
        Assert.True(Geo.Len(slab.Base) - R < 30 - 0.05 * 20 - 1, "slab must reach below the lowest corner");
    }

    // ---- flat pull ----

    [Fact]
    public void FlatPull_IsOneNearTheCamera_AndBoundedAndMonotoneInGrazingAngle()
    {
        var inst = Placement.MakeInstance(Building(), new Box2(0, 0, 0, 10, 10), R, 0, Geo.Flat());
        Assert.Equal(1f, Placement.FlatPull(inst, inst.Center + double3.Normalize(inst.Up) * 0.5));
        var up = double3.Normalize(inst.Up);
        float straightDown = Placement.FlatPull(inst, inst.Center + up * 1000);
        Assert.Equal(1 - Placement.FlatPullPerMeter, straightDown, 5);
        double3 side = double3.Normalize(double3.Cross(up, new double3(0, 0, 1)));
        float grazing = Placement.FlatPull(inst, inst.Center + up * 5 + side * 5000);
        Assert.InRange(grazing, 0.5f, straightDown);
        Assert.Equal(1 - Placement.FlatPullPerMeter / 0.05, grazing, 4);   // clamp at sin = 0.05
    }

    // ---- matrix ----

    [Fact]
    public void ModelToEgo_Golden_IdentityRotation()
    {
        var inst = new BuildingInstance
        {
            Up = new double3(0, 0, 3), AxisA = new double3(5, 0, 0), AxisB = new double3(0, 7, 0), Base = new double3(10, 20, 30),
        };
        var m = Placement.ModelToEgo(inst, new double3(1, 2, 3), new double3(1, 0, 0), new double3(0, 1, 0), new double3(0, 0, 1), 1, 0.25f);
        Assert.Equal(3f, m.M13); Assert.Equal(0.25f, m.M14);
        Assert.Equal(5f, m.M21); Assert.Equal(7f, m.M32);
        Assert.Equal(9f, m.M41); Assert.Equal(18f, m.M42); Assert.Equal(27f, m.M43); Assert.Equal(1f, m.M44);
    }

    [Fact]
    public void ModelToEgo_RotatesAxes_AndScales()
    {
        var inst = new BuildingInstance { Up = new double3(1, 0, 0), AxisA = new double3(0, 1, 0), AxisB = new double3(0, 0, 1), Base = default };
        // CCF x -> ego y, y -> z, z -> x
        var m = Placement.ModelToEgo(inst, default, new double3(0, 1, 0), new double3(0, 0, 1), new double3(1, 0, 0), 2);
        Assert.Equal(2f, m.M12);   // up row: x axis ends up as y
        Assert.Equal(2f, m.M23);
        Assert.Equal(2f, m.M31);
    }
}
