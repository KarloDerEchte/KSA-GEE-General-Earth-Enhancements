using Xunit;
using P = GEE.Footprint.P;

namespace GEE.Tests;

public class FootprintTests
{
    private static List<P> Rect(double cx, double cy, double a, double b, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        var pts = new List<P>();
        foreach (var (u, v) in new[] { (-a / 2, -b / 2), (a / 2, -b / 2), (a / 2, b / 2), (-a / 2, b / 2) })
            pts.Add(new P(cx + u * c - v * s, cy + u * s + v * c));
        return pts;
    }

    [Fact]
    public void SignedArea_IsPositiveForCounterClockwise_AndGolden()
    {
        var ccw = new List<P> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        Assert.Equal(100, Footprint.SignedArea(ccw), 9);
        ccw.Reverse();
        Assert.Equal(-100, Footprint.SignedArea(ccw), 9);
    }

    [Fact]
    public void ConvexHull_DropsInteriorAndCollinearPoints()
    {
        var pts = new List<P> { new(0, 0), new(5, 0), new(10, 0), new(10, 10), new(0, 10), new(5, 5), new(2, 3) };
        var hull = Footprint.ConvexHull(pts);
        Assert.Equal(4, hull.Count);
        Assert.Equal(100, Footprint.SignedArea(hull), 9);   // counter-clockwise
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.3)]
    [InlineData(-2.0)]
    public void MinAreaRect_RecoversARotatedRectangle(double angle)
    {
        var box = Footprint.MinAreaRect(Rect(12, -7, 20, 8, angle))!.Value;
        Assert.Equal(160, box.LengthA * box.LengthB, 6);
        Assert.Equal(20, Math.Max(box.LengthA, box.LengthB), 6);
        Assert.Equal(8, Math.Min(box.LengthA, box.LengthB), 6);
        Assert.Equal(12, box.CenterEast, 6);
        Assert.Equal(-7, box.CenterNorth, 6);
    }

    [Fact]
    public void MinAreaRect_OfDegenerateInput_IsNull()
    {
        Assert.Null(Footprint.MinAreaRect(new List<P> { new(0, 0), new(1, 1) }));
        Assert.Null(Footprint.MinAreaRect(new List<P> { new(0, 0), new(1, 1), new(2, 2) }));
    }

    [Fact]
    public void ClipHalfPlane_SplitsASquareInHalf()
    {
        var sq = new List<P> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var left = Footprint.ClipHalfPlane(sq, new P(5, 5), new P(-1, 0));
        var right = Footprint.ClipHalfPlane(sq, new P(5, 5), new P(1, 0));
        Assert.Equal(50, Math.Abs(Footprint.SignedArea(left)), 9);
        Assert.Equal(50, Math.Abs(Footprint.SignedArea(right)), 9);
    }

    [Fact]
    public void Decompose_Rectangle_IsOneBox()
    {
        var boxes = Footprint.Decompose(Rect(0, 0, 30, 12, 0.4), 3, 0.8);
        Assert.Single(boxes);
    }

    [Fact]
    public void Decompose_LShape_CoversTheOutline_WithoutOverlapOrNaN()
    {
        var l = new List<P> { new(0, 0), new(40, 0), new(40, 15), new(15, 15), new(15, 40), new(0, 40) };
        double area = Math.Abs(Footprint.SignedArea(l));
        var boxes = Footprint.Decompose(l, 3, 0.8);
        Assert.True(boxes.Count >= 2, "an L is not 80% of its bounding rectangle, it must be split");
        double boxArea = 0;
        foreach (var b in boxes)
        {
            Assert.True(double.IsFinite(b.CenterEast) && double.IsFinite(b.CenterNorth) && double.IsFinite(b.Angle));
            Assert.True(b.LengthA > 0 && b.LengthB > 0);
            boxArea += b.LengthA * b.LengthB;
        }
        Assert.InRange(boxArea, area * 0.99, area * 1.35);   // covers it, with limited waste
    }

    [Fact]
    public void Decompose_RespectsMaxDepth()
    {
        var comb = new List<P>();
        comb.Add(new P(0, 0));
        for (int i = 0; i < 6; i++) { comb.Add(new P(i * 10 + 5, 0)); comb.Add(new P(i * 10 + 5, 30)); comb.Add(new P(i * 10 + 10, 30)); comb.Add(new P(i * 10 + 10, 0)); }
        comb.Add(new P(60, -5)); comb.Add(new P(0, -5));
        Assert.True(Footprint.Decompose(comb, 0, 0.9).Count <= 1);
        Assert.True(Footprint.Decompose(comb, 2, 0.9).Count <= 4);
    }
}
