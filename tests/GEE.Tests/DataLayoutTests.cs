using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

/// <summary>Pipelines / PipelineData / Structures / Landmarks: the hand-collected launch complex data and how tiles pick it up.</summary>
public class DataLayoutTests
{
    private const double MetersPerDegree = Geo.MetersPerDegree;

    private static IEnumerable<(int X, int Y)> TilesAround(int z, double lat, double lon, int radius)
    {
        var (cx, cy) = VectorTile.TileAt(z, lat, lon);
        for (int x = cx - radius; x <= cx + radius; x++)
            for (int y = cy - radius; y <= cy + radius; y++)
                yield return (x, y);
    }

    private static List<RawBuilding> Collect(Action<TileData, int, int, int> add, int z, double lat, double lon, int radius)
    {
        var all = new List<RawBuilding>();
        foreach (var (x, y) in TilesAround(z, lat, lon, radius))
        {
            var d = new TileData();
            add(d, z, x, y);
            all.AddRange(d.Buildings);
        }
        return all;
    }

    // ---- pipelines ----

    [Fact]
    public void PipelineData_LiesInsideTheBoxPipelinesUsesToSkipTiles()
    {
        // Pipelines.AddTo returns early for tiles outside lat 28.59..28.62 / lon -80.62..-80.59, so data outside would be silently lost.
        foreach (double[] line in PipelineData.Lines)
        {
            Assert.True(line.Length >= 4 && line.Length % 2 == 0);
            for (int i = 0; i < line.Length; i += 2)
            {
                Assert.InRange(line[i], 28.59, 28.62);
                Assert.InRange(line[i + 1], -80.62, -80.59);
            }
        }
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Pipelines_EveryPieceBelongsToExactlyOneTile_AtAnyZoom(int z)
    {
        var pieces = Collect(Pipelines.AddTo, z, 28.605, -80.603, 12);
        Assert.NotEmpty(pieces);
        Assert.Equal(pieces.Count, pieces.Select(p => p.Id).Distinct().Count());   // no piece twice
        Assert.Equal(0, pieces.Count % 2);                                         // pipe + post
        Assert.All(pieces, p => Assert.True(p.LaunchComplex));
        // the same number of pieces whatever the tile size
        Assert.Equal(PieceCountAtZoom(14), pieces.Count);
    }

    private static int PieceCountAtZoom(int z) => Collect(Pipelines.AddTo, z, 28.605, -80.603, 12).Count;

    [Fact]
    public void Pipelines_TotalPipeLengthMatchesTheData()
    {
        var pieces = Collect(Pipelines.AddTo, 14, 28.605, -80.603, 12).Where(p => p.Variant == 6).ToList();
        double laid = pieces.Sum(p => p.Boxes[0].LengthA - 0.1);
        double expected = 0;
        foreach (double[] line in PipelineData.Lines)
            for (int i = 0; i + 3 < line.Length; i += 2)
            {
                double cos = Math.Cos((line[i] + line[i + 2]) / 2 * Math.PI / 180);
                double e = (line[i + 3] - line[i + 1]) * MetersPerDegree * cos, n = (line[i + 2] - line[i]) * MetersPerDegree;
                double len = Math.Sqrt(e * e + n * n);
                if (len >= 0.5) expected += len;
            }
        Assert.Equal(expected, laid, 0);
        Assert.True(expected > 1000, "the LC-39A pipelines are several kilometres long");
    }

    [Fact]
    public void Pipelines_PiecesStayInsideTheirTile_AreShortAndFloat()
    {
        foreach (var (x, y) in TilesAround(14, 28.605, -80.603, 6))
        {
            var d = new TileData();
            Pipelines.AddTo(d, 14, x, y);
            var (s, w, n, e) = VectorTile.Bounds(14, x, y);
            foreach (var p in d.Buildings)
            {
                Assert.InRange(p.Lat, s, n); Assert.InRange(p.Lon, w, e);
                var box = Assert.Single(p.Boxes);
                if (p.Variant == 6)
                {
                    Assert.InRange(box.LengthA, 0.5, 10.1 + 0.1);
                    Assert.Equal(0.9, box.LengthB);
                    Assert.True(p.MinHeight > 0 && p.Height > p.MinHeight, "pipes float on their posts");
                }
                else
                {
                    Assert.Equal(4, p.Variant);
                    Assert.Equal(0.35, box.LengthA);
                }
            }
        }
    }

    [Fact]
    public void Pipelines_OutsideTheComplex_AddNothing()
    {
        var (x, y) = VectorTile.TileAt(14, 52.5, 13.4);
        var d = new TileData();
        Pipelines.AddTo(d, 14, x, y);
        Assert.Empty(d.Buildings);
    }

    // ---- structures / landmarks ----

    [Fact]
    public void StructureData_EveryEntryIsSane_AndPlacedInExactlyOneTile()
    {
        Assert.NotEmpty(StructureData.All);
        for (int i = 0; i < StructureData.All.Length; i++)
        {
            var (kind, lat, lon, angle, length, width) = StructureData.All[i];
            Assert.Contains(kind, new[] { "HTank", "VTank", "Tent", "Dome" });
            Assert.InRange(lat, -90, 90); Assert.InRange(lon, -180, 180);
            Assert.True(length > 0 && width > 0 && double.IsFinite(angle));
        }
        // Entries are spread over four sites (LC-39A, Starbase, Mahia, Vandenberg): each one appears in exactly one tile.
        for (int i = 0; i < StructureData.All.Length; i++)
        {
            var (_, lat, lon, _, _, _) = StructureData.All[i];
            var (cx, cy) = VectorTile.TileAt(14, lat, lon);
            int hits = 0;
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int y = cy - 1; y <= cy + 1; y++)
                {
                    var d = new TileData();
                    Structures.AddTo(d, 14, x, y);
                    hits += d.Buildings.Count(b => b.Id == 1_100_000_000 + i);
                }
            Assert.Equal(1, hits);
        }
    }

    [Fact]
    public void Landmarks_AreConsistent()
    {
        Assert.Equal(Landmarks.All.Length, Landmarks.All.Select(l => l.Name).Distinct().Count());
        foreach (var l in Landmarks.All)
        {
            Assert.True(l.LengthA > 0 && l.LengthB > 0 && l.Height > 0, l.Name);
            Assert.InRange(l.Lat, -90, 90); Assert.InRange(l.Lon, -180, 180);
            Assert.True(l.BaseHeight >= 0 && l.BaseHeight < l.Height);
        }
        foreach (var c in Landmarks.Copies)
            Assert.InRange(c.Model, 0, Landmarks.All.Length - 1);
        Assert.Equal(Landmarks.All.Length + Landmarks.Copies.Length, Landmarks.Placed().Count());
        // variants of the landmark models stay inside the variant table
        Assert.True(Buildings.FirstLandmarkVariant + Landmarks.All.Length <= Buildings.FirstRoofVariant);
        Assert.Equal(Buildings.FirstLandmarkVariant, Buildings.RoadVariant + 1);
        Assert.Equal(Buildings.RoadVariant, Buildings.Variants.Length);
    }

    [Fact]
    public void Landmarks_EachPlacedExactlyOnce_InAnyTileGrid()
    {
        foreach (int z in new[] { 12, 14 })
        {
            int total = 0;
            foreach (var (_, l) in Landmarks.Placed())
            {
                var (cx, cy) = VectorTile.TileAt(z, l.Lat, l.Lon);
                int hits = 0;
                for (int x = cx - 1; x <= cx + 1; x++)
                    for (int y = cy - 1; y <= cy + 1; y++)
                    {
                        var d = new TileData();
                        Landmarks.AddTo(d, z, x, y);
                        hits += d.Buildings.Count(b => b.Lat == l.Lat && b.Lon == l.Lon);
                    }
                Assert.True(hits >= 1, $"{l.Name} missing at z{z}");
                total++;
            }
            Assert.Equal(Landmarks.Placed().Count(), total);
        }
    }

    [Fact]
    public void Landmarks_AddedBuildings_HaveValidVariantsAndIds()
    {
        var all = Collect(Landmarks.AddTo, 14, 28.6082, -80.6044, 2);
        Assert.NotEmpty(all);
        foreach (var b in all)
        {
            Assert.True(b.Id < 0 && -1 - b.Id < Landmarks.All.Length);
            Assert.Equal(Buildings.FirstLandmarkVariant + (int)(-1 - b.Id), b.Variant);
            Assert.True(b.LaunchComplex);
            Assert.Single(b.Boxes);
        }
        Assert.True(Landmarks.IsFixedServiceStructure(-2));
        Assert.False(Landmarks.IsFixedServiceStructure(-1));
        Assert.False(Landmarks.IsFixedServiceStructure(5));
        Assert.False(Landmarks.IsTower(-1));   // the VAB is no tower
        Assert.True(Landmarks.IsTower(-3));
    }

    [Fact]
    public void Landmarks_Adjust_LowersBuildingsUnderTheVab_AndLeavesOthersAlone()
    {
        var vab = Landmarks.All[0];
        var options = new OsmOptions();
        RawBuilding Make(double lat, double lon) => new()
        {
            Id = 5, Lat = lat, Lon = lon, Height = 80, Variant = 1, Boxes = { new Box2(0, 0, 0, 30, 30) },
        };
        var under = Make(vab.Lat, vab.Lon);
        Assert.True(Landmarks.Adjust(under, null, options));
        Assert.Equal(vab.BaseHeight, under.Height);

        var far = Make(52.5, 13.4);
        Assert.True(Landmarks.Adjust(far, null, options));
        Assert.Equal(80, far.Height);

        var fss = Landmarks.All[1];
        Assert.False(Landmarks.Adjust(Make(fss.Lat, fss.Lon), null, options), "an outline under a tower model (BaseHeight 0) is dropped");
    }
}
