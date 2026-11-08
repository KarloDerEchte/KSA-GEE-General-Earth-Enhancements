using Xunit;

namespace GEE.Tests;

public class RoadClassifyTests
{
    private static (double Width, int Rank, bool Bridge, bool Lit, RoadSurface Surface)? C(params (string, object)[] tags) =>
        Roads.Classify(tags.ToDictionary(t => t.Item1, t => (object?)t.Item2));

    [Fact]
    public void Golden_WidthsRanksSurfaces()
    {
        Assert.Equal((9.0, 4, false, true, RoadSurface.Road), C(("class", "primary"))!.Value);
        Assert.Equal((6.0, 1, false, true, RoadSurface.Street), C(("class", "minor"))!.Value);
        Assert.Equal((4.0, 0, false, false, RoadSurface.Street), C(("class", "service"))!.Value);
        Assert.Equal((3.0, 0, false, false, RoadSurface.Track), C(("class", "track"))!.Value);
    }

    [Fact]
    public void OnewayWidth_IsSixtyPercent_ButNeverBelowThreeMetres()
    {
        Assert.Equal(5.4, C(("class", "primary"), ("oneway", 1.0))!.Value.Width, 9);
        Assert.Equal(4.2, C(("class", "tertiary"), ("oneway", 1.0))!.Value.Width, 9);
        Assert.Equal(3.6, C(("class", "minor"), ("oneway", 1.0))!.Value.Width, 9);
        Assert.Equal(3.0, C(("class", "service"), ("service", "driveway"), ("oneway", 1.0))!.Value.Width, 9);   // 3 * 0.6 = 1.8 -> 3
    }

    [Fact]
    public void OnewayMotorway_IsAThreeLaneCarriageway_AndTwoWayOneIsFourLanes()
    {
        var one = C(("class", "motorway"), ("oneway", 1.0))!.Value;
        Assert.Equal(RoadSurface.Highway3, one.Surface);
        Assert.Equal(Roads.Highway3Width, one.Width);
        var trunk = C(("class", "trunk"), ("oneway", 1.0))!.Value;
        Assert.Equal(RoadSurface.Highway, trunk.Surface);
        Assert.Equal(9.0, trunk.Width);
        var two = C(("class", "motorway"))!.Value;
        Assert.Equal(RoadSurface.Highway4, two.Surface);
        Assert.Equal(Roads.Highway4Width, two.Width);
        Assert.True(Roads.Highway4Width > Roads.Highway3Width);
    }

    [Fact]
    public void Ramps_LieOneRankLowerThanTheirCarriageway()
    {
        var ramp = C(("class", "motorway"), ("ramp", 1.0))!.Value;
        Assert.Equal(RoadSurface.Ramp, ramp.Surface);
        Assert.Equal(5, ramp.Rank);
        Assert.Equal(7.2, ramp.Width, 9);
        Assert.True(ramp.Rank < C(("class", "motorway"), ("oneway", 1.0))!.Value.Rank);
    }

    [Fact]
    public void Tunnels_FootpathsAndUnknownClasses_AreNotDrawn()
    {
        Assert.Null(C(("class", "motorway"), ("brunnel", "tunnel")));
        Assert.Null(C(("class", "path"), ("subclass", "footway")));
        Assert.Null(C(("class", "rail")));
        Assert.Null(C());
        Assert.NotNull(C(("class", "path"), ("subclass", "pedestrian")));
    }

    [Fact]
    public void Bridges_AreRecognisedByBrunnelOrBridgeTag()
    {
        Assert.True(C(("class", "primary"), ("brunnel", "bridge"))!.Value.Bridge);
        Assert.True(C(("class", "primary"), ("bridge", "viaduct"))!.Value.Bridge);
        Assert.False(C(("class", "primary"), ("bridge", "no"))!.Value.Bridge);
        Assert.False(C(("class", "primary"))!.Value.Bridge);
    }

    [Fact]
    public void UnpavedSurfaceTag_MakesATrack()
    {
        Assert.Equal(RoadSurface.Track, C(("class", "tertiary"), ("surface", "unpaved"))!.Value.Surface);
    }

    [Fact]
    public void Lift_RanksAreSeparatedMoreThanAllTieBreaks()
    {
        double maxTie = Roads.Lift(2, Roads.TieLevels - 1);
        Assert.True(Roads.Lift(3, 0) > maxTie, "a higher class must always lie above every tie level of the lower one");
        for (int rank = 0; rank < 8; rank++)
            for (int tie = 0; tie < Roads.TieLevels; tie++)
                Assert.True(Roads.Lift(rank, tie) >= Roads.BaseLift);
        Assert.Equal(Roads.Lift(1, 2), Roads.Lift(1, 2 + Roads.TieLevels));   // tie wraps
    }

    [Fact]
    public void EverySurface_HasAVariantInRange_AndStreetSurfacesAreMarked()
    {
        foreach (RoadSurface s in Enum.GetValues<RoadSurface>())
        {
            int v = Buildings.SurfaceVariant(s);
            Assert.InRange(v, Buildings.RoadVariant, Buildings.VariantCount - 1);
            Assert.True(Buildings.IsStreetSurface(v), $"{s}");
            Assert.InRange(Buildings.PlainVariant(s), Buildings.RoadVariant, Buildings.VariantCount - 1);
        }
    }
}
