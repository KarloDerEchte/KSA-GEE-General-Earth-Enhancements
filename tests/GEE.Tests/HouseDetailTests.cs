using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

public class HouseDetailTests
{
    private static HouseInput House(double la, double lb, bool hip = false, uint hash = 1, double pitchDeg = 36, bool road = false, double roadA = 0, double roadB = 0)
    {
        double rise = Math.Min(Math.Tan(pitchDeg * Math.PI / 180) * Math.Min(la, lb) / 2, 5);
        return new HouseInput
        {
            La = la, Lb = lb, WallHeight = 5, Rise = rise, Hip = hip, RoofVariant = Buildings.FirstRoofVariant,
            Hash = hash, SingleBox = true, Largest = true, HasRoad = road, RoadA = roadA, RoadB = roadB,
        };
    }

    [Fact]
    public void FrontFace_FollowsTheRoad_ElseTheLongSide()
    {
        Assert.Equal((0, 1), HouseDetail.FrontFace(12, 8, true, 3, 20, 1));
        Assert.Equal((0, -1), HouseDetail.FrontFace(12, 8, true, -2, -9, 1));
        Assert.Equal((1, 0), HouseDetail.FrontFace(12, 8, true, 25, 1, 1));
        Assert.Equal((-1, 0), HouseDetail.FrontFace(12, 8, true, -25, 3, 1));
        for (uint h = 0; h < 20; h++)
        {
            var (nx, ny) = HouseDetail.FrontFace(12, 8, false, 0, 0, h);
            Assert.Equal(0, nx);
            Assert.NotEqual(0, ny);
            var (mx, my) = HouseDetail.FrontFace(8, 12, false, 0, 0, h);
            Assert.NotEqual(0, mx);
            Assert.Equal(0, my);
        }
    }

    [Fact]
    public void Door_SitsOnTheRoadFace()
    {
        foreach (var (ra, rb) in new[] { (0.0, 30.0), (0.0, -30.0), (40.0, 2.0), (-40.0, 2.0) })
            for (uint h = 0; h < 30; h++)
            {
                var plan = HouseDetail.Plan(House(12, 8, hash: h, road: true, roadA: ra, roadB: rb));
                var door = plan.Single(p => p.Part == HousePart.DoorLeaf);
                Assert.True(door.Ca * ra + door.Cb * rb > 0);
                double outside = Math.Abs(ra) > Math.Abs(rb) * 3 ? Math.Abs(door.Ca) - 6 : Math.Abs(door.Cb) - 4;
                Assert.InRange(outside, 0, 0.1);
                Assert.True(door.SizeV < door.SizeU);
            }
    }

    [Fact]
    public void GarageAndDoor_FitOnTheFaceWithoutOverlap()
    {
        int garages = 0;
        for (uint h = 0; h < 200; h++)
        {
            var plan = HouseDetail.Plan(House(14, 9, hash: h, road: true, roadB: 20));
            var door = plan.Single(p => p.Part == HousePart.DoorLeaf);
            foreach (var g in plan.Where(p => p.Part == HousePart.Garage))
            {
                garages++;
                Assert.True(Math.Abs(g.Ca - door.Ca) >= g.SizeU / 2 + door.SizeU / 2 + 0.5, "garage and door overlap");
                Assert.True(Math.Abs(g.Ca) + g.SizeU / 2 <= 7 - 0.4, "garage over the corner");
            }
        }
        Assert.InRange(garages, 40, 160);
        for (uint h = 0; h < 100; h++)
            Assert.DoesNotContain(HouseDetail.Plan(House(6.5, 6.5, hash: h)), p => p.Part == HousePart.Garage);
    }

    [Theory]
    [InlineData(false, 11, 8)]
    [InlineData(true, 11, 8)]
    [InlineData(false, 8, 13)]
    [InlineData(true, 16, 9)]
    public void Dormers_StayOnTheSlope(bool hip, double la, double lb)
    {
        int total = 0;
        for (uint hash = 0; hash < 300; hash++)
        {
            var input = House(la, lb, hip, hash, road: true, roadA: 0, roadB: 10);
            var plan = HouseDetail.Plan(input);
            HouseDetail.RoofAxes(la, lb, out double ra, out double rb, out double ca, out double cb, out double ridge, out double across);
            double roofLength = ridge + HouseDetail.OverhangRidge, half = (across + HouseDetail.OverhangAcross) / 2;
            double inset = hip ? Buildings.HipInsets[(Placement.HipVariant(input.RoofVariant, across / ridge) - Buildings.FirstHipVariant) / Buildings.RoofVariants] : 0;
            foreach (var d in plan.Where(p => p.Part == HousePart.Dormer))
            {
                total++;
                var roof = plan.Where(p => p.Part == HousePart.DormerRoof)
                    .OrderBy(p => Math.Abs(p.Ca - d.Ca) + Math.Abs(p.Cb - d.Cb)).First();
                double t = Math.Abs(d.Ca * ca + d.Cb * cb), along = d.Ca * ra + d.Cb * rb;
                double tFront = t + d.SizeV / 2, tBack = t - d.SizeV / 2;
                Assert.True(tFront < half - 0.5, "front wall must stay clear of the eave");
                double slopeAtBack = HouseDetail.RoofHeightAt(input.Rise, half, tBack);
                Assert.InRange(d.Z + d.Height - slopeAtBack, -0.05, 0.05);
                double roofT = Math.Abs(roof.Ca * ca + roof.Cb * cb);
                double roofBack = roofT - roof.SizeU / 2;
                double ridgeTop = roof.Z + roof.Height;
                Assert.True(roofBack >= 0.5, $"dormer roof reaches the ridge ({roofBack})");
                Assert.InRange(HouseDetail.RoofHeightAt(input.Rise, half, roofBack) - ridgeTop, -0.2, 0.25);
                double reach = HouseDetail.RoofHalfLengthAt(roofLength, half, hip, inset, roofBack);
                Assert.True(Math.Abs(along) + roof.SizeV / 2 <= reach, "dormer pokes past the hip line or gable end");
                Assert.InRange(d.SizeU, 1.0, 1.7);
            }
        }
        Assert.True(total > 20, $"dormers should appear ({total})");
    }

    [Fact]
    public void SmallOrShallowRoofs_GetNoDormers()
    {
        for (uint h = 0; h < 200; h++)
        {
            Assert.DoesNotContain(HouseDetail.Plan(House(8, 6, hash: h)), p => p.Part == HousePart.Dormer);
            Assert.DoesNotContain(HouseDetail.Plan(House(14, 9, hash: h, pitchDeg: 15)), p => p.Part == HousePart.Dormer);
        }
    }

    [Fact]
    public void GableWindow_InsideTheGableTriangle_AndHipRoofsHaveNone()
    {
        int seen = 0;
        for (uint h = 0; h < 100; h++)
        {
            var input = House(11, 8, false, h);
            double half = (8 + HouseDetail.OverhangAcross) / 2;
            foreach (var w in HouseDetail.Plan(input).Where(p => p.Part == HousePart.GableWindow))
            {
                seen++;
                double top = w.Z + w.Height;
                double halfWidthThere = half * (1 - (top + HouseDetail.RoofSink) / input.Rise);
                Assert.True(halfWidthThere >= w.SizeU / 2 + 0.1, "window corner leaves the gable");
                Assert.True(w.Z > 0);
            }
            Assert.DoesNotContain(HouseDetail.Plan(House(11, 8, true, h)), p => p.Part == HousePart.GableWindow || p.Part == HousePart.Barge);
        }
        Assert.True(seen > 50);
    }

    [Fact]
    public void RoofTrim_GableGetsBargeboards_HipGetsFourFascias_MultiBoxNone()
    {
        var gable = HouseDetail.Plan(House(11, 8, false, 4));
        Assert.Equal(2, gable.Count(p => p.Part == HousePart.Barge));
        Assert.Equal(2, gable.Count(p => p.Part == HousePart.Fascia));
        Assert.Equal(4, HouseDetail.Plan(House(11, 8, true, 4)).Count(p => p.Part == HousePart.Fascia));
        var multi = House(11, 8);
        multi.SingleBox = false;
        Assert.DoesNotContain(HouseDetail.Plan(multi), p => p.Part == HousePart.Fascia || p.Part == HousePart.Dormer);
        var secondary = House(11, 8);
        secondary.Largest = false;
        Assert.DoesNotContain(HouseDetail.Plan(secondary), p => p.Part == HousePart.DoorLeaf);
    }

    [Fact]
    public void Pieces_AreFinite_PositiveAndUseKnownVariants()
    {
        for (uint h = 0; h < 300; h++)
            foreach (bool hip in new[] { false, true })
                foreach (var p in HouseDetail.Plan(House(8 + h % 9, 6 + h % 7, hip, h, road: h % 2 == 0, roadA: h % 5 - 2, roadB: h % 3 - 1)))
                {
                    Assert.True(double.IsFinite(p.Ca + p.Cb + p.Z));
                    Assert.True(p.SizeU > 0 && p.SizeV > 0 && p.Height > 0, $"{p.Part} has an empty size");
                    Assert.InRange(p.Variant, 0, Buildings.FurnitureVariant - 1);
                    Assert.InRange(Math.Abs(p.Ux * p.Ux + p.Uy * p.Uy - 1), 0, 1e-9);
                }
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var a = HouseDetail.Plan(House(13, 9, true, 77, road: true, roadB: 8));
        var b = HouseDetail.Plan(House(13, 9, true, 77, road: true, roadB: 8));
        Assert.Equal(a, b);
    }

    [Fact]
    public void RoadProximity_FindsTheNearestSegmentPoint_AndSkipsMotorways()
    {
        double lat0 = 40, lon0 = -100, mLat = Geo.MetersPerDegree, mLon = Geo.MetersPerDegree * Math.Cos(lat0 * Math.PI / 180);
        RawRoad Line(double x0, double y0, double x1, double y1, RoadSurface s = RoadSurface.Street) => new()
        {
            Points = { (lat0 + y0 / mLat, lon0 + x0 / mLon), (lat0 + y1 / mLat, lon0 + x1 / mLon) },
            Surface = s,
        };
        var prox = new RoadProximity(new[] { Line(-100, 20, 100, 20), Line(-100, -5, 100, -5, RoadSurface.Highway) }, lat0, lon0);
        var hit = prox.Nearest(10, 0, 70)!.Value;
        Assert.Equal(10, hit.X, 0.01);
        Assert.Equal(20, hit.Y, 0.01);
        Assert.Equal(20, hit.Distance, 0.01);
        Assert.Null(prox.Nearest(10, -40, 30));
        Assert.Equal(60, prox.Nearest(10, -40, 70)!.Value.Distance, 0.01);
        var (x, y) = prox.ToLocal(lat0, lon0, 3, 4);
        Assert.Equal(3, x, 1e-6);
        Assert.Equal(4, y, 1e-6);
    }

    [Fact]
    public void BuildingDetail_WithContext_TurnsTheDoorToTheRoad_AndAddsRoofTrim()
    {
        var b = new RawBuilding
        {
            Id = 9, Lat = 40, Lon = -100, Height = 5, Variant = 1, Style = Buildings.StyleResidential, RoofPitch = 0.6,
            RoofVariant = Buildings.FirstRoofVariant, Boxes = { new Box2(0, 0, 0, 12, 8) },
        };
        b.RoofHeight = Buildings.RoofRise(8, b);
        double mLat = Geo.MetersPerDegree;
        var road = new RawRoad { Points = { (40 - 15 / mLat, -100.01), (40 - 15 / mLat, -99.99) }, Surface = RoadSurface.Street };
        var prox = new RoadProximity(new[] { road }, 40, -100);
        var wall = Placement.MakeInstance(b, b.Boxes[0], Geo.R, 1, Geo.Flat());
        var tile = new Tile();
        BuildingDetail.Add(tile, wall, b, new HouseContext(b.Boxes[0], prox));
        var pieces = tile.Instances.ToList();
        Assert.All(pieces, p => Assert.True(Geo.Finite(p) && p.IsDetail));
        Assert.InRange(pieces.Count, 8, 40);
        Placement.Axes(double3.Normalize(wall.Center), out _, out double3 north);
        var doors = pieces.Where(p => p.Variant >= Buildings.FirstDoorVariant && p.Variant < Buildings.FirstGarageVariant).ToList();
        Assert.Single(doors);
        Assert.True(double3.Dot(doors[0].Base - wall.Center, north) < -3.5, "door on the south face");
    }
}
