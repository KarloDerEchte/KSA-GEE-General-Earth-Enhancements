using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

/// <summary>Placement.AddRoadInstances: the slab pieces a road is laid out as (miter overlap, widths, bridges, terrain following).</summary>
public class RoadLayoutTests
{
    private const double Lat0 = 47.0, Lon0 = 8.0, R = Geo.R;
    private static readonly double3 Origin = Geo.Dir(Lat0, Lon0);

    private static (double Lat, double Lon) At(double east, double north) =>
        (Lat0 + north / Geo.MetersPerDegree, Lon0 + east / (Geo.MetersPerDegree * Math.Cos(Lat0 * Math.PI / 180)));

    private static RawRoad Road(double width, params (double E, double N)[] pts) => new()
    {
        Points = pts.Select(p => At(p.E, p.N)).ToList(),
        Width = width,
        Lift = Roads.Lift(2, 0),
        Rank = 2,
        Surface = RoadSurface.Road,
    };

    private static List<BuildingInstance> Lay(RawRoad road, Func<double3, double>? terrain = null, Action<RoadLight>? light = null)
    {
        var list = new List<BuildingInstance>();
        Placement.AddRoadInstances(road, R, terrain ?? Geo.Flat(), list.Add, light);
        return list;
    }

    /// <summary>Position of a point along the local north axis (m).</summary>
    private static double AlongNorth(double3 p)
    {
        Placement.Axes(Origin, out _, out var north);
        return double3.Dot(p - Origin * R, north);
    }

    [Fact]
    public void StraightRoad_PiecesAreFiniteAndOfTheRoadWidth()
    {
        var pieces = Lay(Road(8, (0, 0), (0, 200)));
        Assert.True(pieces.Count >= 200 / Placement.RoadPieceMeters);
        foreach (var p in pieces)
        {
            Assert.True(Geo.Finite(p));
            Assert.True(p.IsRoad);
            Assert.Equal(Layers.Street, p.Layer);
            Assert.Equal(Buildings.RoadVariant, p.Variant);
            Assert.Equal(8, Geo.Len(p.AxisB), 5);                 // flat: no cross-slope shear
            Assert.InRange(Geo.Len(p.AxisA), 3, Placement.RoadPieceMeters + 8 + 1);   // + half a width at each end
            var up = double3.Normalize(p.Up);
            Assert.Equal(0, double3.Dot(double3.Normalize(p.AxisA), up), 6);
        }
    }

    [Fact]
    public void StraightRoad_PiecesCoverTheWholeLine_WithoutGaps_AndEndsReachHalfAWidth()
    {
        var pieces = Lay(Road(8, (0, 0), (0, 200)));
        var spans = pieces.Select(p =>
        {
            double c = AlongNorth(p.Base), half = Geo.Len(p.AxisA) / 2;   // Base is the middle of the extended slab; Center is the unextended piece middle
            return (From: c - half, To: c + half);
        }).OrderBy(s => s.From).ToList();
        Assert.Equal(-4, spans[0].From, 1);          // road end reaches half the width past its first point
        Assert.Equal(204, spans[^1].To, 1);
        for (int i = 1; i < spans.Count; i++)
            Assert.True(spans[i].From <= spans[i - 1].To + 1e-6, $"gap of {spans[i].From - spans[i - 1].To} m before piece {i}");
    }

    [Fact]
    public void StraightRoad_InteriorJointsOverlapByOnlyAFewCentimetres()
    {
        var pieces = Lay(Road(8, (0, 0), (0, 200)));
        var spans = pieces.Select(p => (From: AlongNorth(p.Base) - Geo.Len(p.AxisA) / 2, To: AlongNorth(p.Base) + Geo.Len(p.AxisA) / 2))
            .OrderBy(s => s.From).ToList();
        for (int i = 1; i < spans.Count; i++)
            Assert.InRange(spans[i - 1].To - spans[i].From, 0.0, 0.2);   // 0.05 m miter allowance each side
    }

    [Fact]
    public void TileEdgeEnds_AreNotExtended_SoNeighbouringTilesMeetExactly()
    {
        var road = Road(8, (0, 0), (0, 100));
        road.StartAtTileEdge = true; road.EndAtTileEdge = true;
        var pieces = Lay(road);
        double min = pieces.Min(p => AlongNorth(p.Base) - Geo.Len(p.AxisA) / 2);
        double max = pieces.Max(p => AlongNorth(p.Base) + Geo.Len(p.AxisA) / 2);
        Assert.Equal(0, min, 1);
        Assert.Equal(100, max, 1);
    }

    [Fact]
    public void Bend_OuterCornerIsCovered_AndTheRoundedPolylineStaysWithinReachOfTheCorner()
    {
        // 90 degree corner at (0, 100)
        var pieces = Lay(Road(8, (0, 0), (0, 100), (100, 100)));
        Assert.All(pieces, p => Assert.True(Geo.Finite(p)));
        // the corner is rounded off within 7 m on both sides; no piece centre may be further than that from the sharp corner path
        var corner = Origin * R + AxisNorth() * 100;
        double nearest = pieces.Min(p => Geo.Dist(p.Center, corner));
        Assert.InRange(nearest, 0.5, 7.5);
        // total length of the laid out centre line is within 2 % of the sharp polyline (fillets cut the corner a little)
        double pathLength = pieces.Sum(p => Geo.Len(p.AxisA));
        Assert.InRange(pathLength, 195, 215);
    }

    private static double3 AxisNorth() { Placement.Axes(Origin, out _, out var n); return n; }

    [Fact]
    public void Onewayness_WidthFlowsThroughToThePieces()
    {
        var tags = new Dictionary<string, object?> { ["class"] = "primary", ["oneway"] = 1.0 };
        var c = Roads.Classify(tags)!.Value;
        var road = Road(c.Width, (0, 0), (0, 60));
        Assert.All(Lay(road), p => Assert.Equal(5.4, Geo.Len(p.AxisB), 5));
    }

    [Fact]
    public void FollowsTerrain_WithinTolerance_AndRefinesAroundABump()
    {
        Func<double3, double> terrain = d =>
        {
            double dist = Math.Abs(AlongNorth(d * R) - 100);
            return 50 + 4 * Math.Exp(-dist * dist / (2 * 5 * 5));   // 4 m high, ~12 m wide bump
        };
        var pieces = Lay(Road(6, (0, 0), (0, 200)), terrain);
        foreach (var p in pieces)
        {
            double surface = Geo.Len(p.Center) - R;   // Center is the piece's middle on the road surface (without lift)
            double ground = terrain(double3.Normalize(p.Center));
            Assert.InRange(surface - ground, -0.35, 0.35);
        }
        int near = pieces.Count(p => Math.Abs(AlongNorth(p.Center) - 100) < 25);
        int far = pieces.Count(p => Math.Abs(AlongNorth(p.Center) - 20) < 25);
        Assert.True(near > far, $"the bump should get shorter pieces ({near} vs {far})");
        Assert.All(pieces, p => Assert.True(Geo.Len(p.AxisA) >= 2.9));
    }

    [Fact]
    public void CrossSlope_ShearsAxisB_ButKeepsTheSideVertical()
    {
        var refDir = Origin;
        Placement.Axes(refDir, out var east, out _);
        Func<double3, double> terrain = d => 10 + 0.1 * double3.Dot(d * R - refDir * R, east);   // 10 % across
        var pieces = Lay(Road(10, (0, 0), (0, 60)), terrain);
        foreach (var p in pieces)
        {
            var up = double3.Normalize(p.Up);
            double vertical = double3.Dot(p.AxisB, up);
            Assert.Equal(1.0, Math.Abs(vertical), 1);   // 10 m * 10 % = 1 m
            double3 horizontal = p.AxisB - up * vertical;
            Assert.Equal(10, Geo.Len(horizontal), 4);
        }
    }

    [Fact]
    public void Bridge_RisesInTheMiddle_AndEndsOnTheGround()
    {
        var road = Road(8, (0, 0), (0, 120));
        road.Bridge = true;
        var pieces = Lay(road);
        double mid = pieces.Max(p => Geo.Len(p.Center) - R);
        Assert.InRange(mid, Placement.BridgeClearance - 0.5, Placement.BridgeClearance + 0.01);
        var first = pieces.OrderBy(p => AlongNorth(p.Center)).First();
        Assert.True(Geo.Len(first.Center) - R < 2.0);   // 6 m * 4t(1-t) at the first piece middle
    }

    [Fact]
    public void Overpass_StaysAtDeckHeightThroughout()
    {
        var road = Road(8, (0, 0), (0, 60));
        road.Bridge = true; road.Overpass = true;
        var pieces = Lay(road);
        Assert.All(pieces.Where(p => p.Variant == Buildings.RoadVariant), p =>
            Assert.Equal(Placement.BridgeClearance, Geo.Len(p.Center) - R, 3));
    }

    [Fact]
    public void RepeatedAndDegenerateInput_NeverProducesNaN()
    {
        var dup = Road(6, (0, 0), (0, 0), (0, 0.001), (0, 50), (0, 50), (30, 50));
        Assert.All(Lay(dup), p => Assert.True(Geo.Finite(p)));
        Assert.Empty(Lay(Road(6, (0, 0))));
        Assert.Empty(Lay(Road(6, (5, 5), (5, 5))));
        var near = Road(6, (0, 0), (0, 0.02));
        Assert.All(Lay(near), p => Assert.True(Geo.Finite(p)));
    }

    [Fact]
    public void StreetLamps_AlternateSides_8mUp_AtTheirSpacing()
    {
        var road = Road(8, (0, 0), (0, 400));
        road.Lit = true;
        var lamps = new List<RoadLight>();
        Lay(road, light: lamps.Add);
        Assert.InRange(lamps.Count, 400 / 38 - 1, 400 / 38 + 1);
        Assert.All(lamps, l => Assert.True(Geo.Finite(l.Position)));
        var ordered = lamps.OrderBy(l => AlongNorth(l.Position)).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            Assert.InRange(AlongNorth(ordered[i].Position) - AlongNorth(ordered[i - 1].Position), 36, 40);
            // sides alternate: east offset of consecutive lamps has opposite signs
            Placement.Axes(Origin, out var east, out _);
            double e0 = double3.Dot(ordered[i - 1].Position - Origin * R, east), e1 = double3.Dot(ordered[i].Position - Origin * R, east);
            Assert.True(e0 * e1 < 0, "lamps should alternate sides");
            Assert.Equal(4.8, Math.Abs(e1), 0);   // half the width plus 0.8 m
        }
        Assert.All(lamps, l => Assert.InRange(Geo.Len(l.Position) - R, 7.5, 9));
    }

    [Fact]
    public void Runway_GetsEdgeLightsEverySixtyMetres_AndGreenThresholdRows()
    {
        var road = Road(45, (0, 0), (0, 600));
        road.Airfield = Airfield.Runway;
        var lights = new List<RoadLight>();
        var pieces = Lay(road, light: lights.Add);
        Assert.All(pieces, p => Assert.Equal(Buildings.RunwayVariant, p.Variant));
        var green = lights.Where(l => (l.Color & 0xFF) < 100 && ((l.Color >> 8) & 0xFF) > 200).ToList();
        Assert.True(green.Count >= 2 * (45 / 3), "threshold rows at both ends");
        Assert.InRange(lights.Count - green.Count, 2 * 8, 2 * 11);   // two white edge lights per 60 m of runway
    }

    [Fact]
    public void RoadWithClearStretch_UsesPlainAsphaltInsideIt()
    {
        var road = Road(8, (0, 0), (0, 100), (0, 200));
        road.Clear = new double[] { 0, 12, 0 };
        road.Fixed = new[] { false, true, false };
        var pieces = Lay(road);
        var plain = pieces.Where(p => p.Variant == Buildings.PlainVariant(RoadSurface.Road)).ToList();
        Assert.NotEmpty(plain);
        Assert.All(plain, p => Assert.True(Math.Abs(AlongNorth(p.Center) - 100) < 13));
        Assert.Contains(pieces, p => p.Variant == Buildings.RoadVariant);
    }
}
