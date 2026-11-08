using Xunit;

namespace GEE.Tests;

public class EarcutTests
{
    private static double TriArea(double[] d, List<int> t, int k) =>
        Math.Abs((d[2 * t[k + 1]] - d[2 * t[k]]) * (d[2 * t[k + 2] + 1] - d[2 * t[k] + 1])
               - (d[2 * t[k + 2]] - d[2 * t[k]]) * (d[2 * t[k + 1] + 1] - d[2 * t[k] + 1])) / 2;

    private static double Total(double[] d, List<int> t)
    {
        double a = 0;
        for (int k = 0; k < t.Count; k += 3) a += TriArea(d, t, k);
        return a;
    }

    private static void CheckMesh(double[] d, List<int> t)
    {
        Assert.Equal(0, t.Count % 3);
        Assert.All(t, i => Assert.InRange(i, 0, d.Length / 2 - 1));
        for (int k = 0; k < t.Count; k += 3)
        {
            Assert.NotEqual(t[k], t[k + 1]); Assert.NotEqual(t[k + 1], t[k + 2]); Assert.NotEqual(t[k], t[k + 2]);
            Assert.True(TriArea(d, t, k) > 1e-9, $"degenerate triangle #{k / 3}");
        }
    }

    [Fact]
    public void Square_IsTwoTriangles()
    {
        var d = new double[] { 0, 0, 10, 0, 10, 10, 0, 10 };
        var t = Earcut.Triangulate(d, Array.Empty<int>());
        Assert.Equal(6, t.Count);
        CheckMesh(d, t);
        Assert.Equal(100, Total(d, t), 9);
    }

    [Fact]
    public void ClockwiseInput_GivesTheSameArea()
    {
        var d = new double[] { 0, 0, 0, 10, 10, 10, 10, 0 };
        var t = Earcut.Triangulate(d, Array.Empty<int>());
        CheckMesh(d, t);
        Assert.Equal(100, Total(d, t), 9);
    }

    [Fact]
    public void SquareWithHole_AreaIsOuterMinusHole()
    {
        var d = new double[] { 0, 0, 20, 0, 20, 20, 0, 20, 6, 6, 6, 14, 14, 14, 14, 6 };
        var t = Earcut.Triangulate(d, new[] { 4 });
        CheckMesh(d, t);
        Assert.Equal(400 - 64, Total(d, t), 6);
    }

    [Fact]
    public void ConcaveStar_AreaMatchesShoelace()
    {
        const int n = 17;
        var d = new double[n * 4];
        for (int i = 0; i < 2 * n; i++)
        {
            double r = i % 2 == 0 ? 50 : 18, a = i * Math.PI / n;
            d[2 * i] = r * Math.Cos(a); d[2 * i + 1] = r * Math.Sin(a);
        }
        var poly = Enumerable.Range(0, 2 * n).Select(i => new Footprint.P(d[2 * i], d[2 * i + 1])).ToList();
        var t = Earcut.Triangulate(d, Array.Empty<int>());
        CheckMesh(d, t);
        Assert.Equal(Math.Abs(Footprint.SignedArea(poly)), Total(d, t), 6);
        Assert.Equal(3 * (2 * n - 2), t.Count);   // simple polygon: n - 2 triangles
    }

    [Fact]
    public void RandomWigglyPolygons_AlwaysTriangulateToTheirArea()
    {
        var rng = new Random(1234);
        for (int round = 0; round < 40; round++)
        {
            int n = 5 + rng.Next(40);
            var d = new double[2 * n];
            for (int i = 0; i < n; i++)
            {
                double r = 30 + 20 * rng.NextDouble(), a = 2 * Math.PI * i / n;   // star-shaped, so always simple
                d[2 * i] = r * Math.Cos(a); d[2 * i + 1] = r * Math.Sin(a);
            }
            var poly = Enumerable.Range(0, n).Select(i => new Footprint.P(d[2 * i], d[2 * i + 1])).ToList();
            var t = Earcut.Triangulate(d, Array.Empty<int>());
            CheckMesh(d, t);
            Assert.Equal(Math.Abs(Footprint.SignedArea(poly)), Total(d, t), 5);
        }
    }

    [Fact]
    public void TooFewPoints_GiveNoTriangles()
    {
        Assert.Empty(Earcut.Triangulate(new double[] { 0, 0, 1, 1 }, Array.Empty<int>()));
        Assert.Empty(Earcut.Triangulate(Array.Empty<double>(), Array.Empty<int>()));
    }
}
