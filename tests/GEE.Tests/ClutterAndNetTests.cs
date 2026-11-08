using System.IO.Compression;
using System.Reflection;
using Brutal.Numerics;
using Xunit;

namespace GEE.Tests;

public class ClutterTests
{
    private const double R = Geo.R;

    [Fact]
    public void GridResolution_Golden()
    {
        Assert.Equal(113723, Clutter.GridResolution(R, 5.5));
        Assert.True(Clutter.GridResolution(R, 1.45) > Clutter.GridResolution(R, 5.5));
    }

    [Fact]
    public void FaceUv_RoundTripsOnEveryFace()
    {
        var rng = new Random(11);
        for (int i = 0; i < 600; i++)
        {
            int face = i % 6;
            double u = rng.NextDouble(), v = rng.NextDouble();
            var d = Clutter.QscUvToDirection(face, u, v);
            Assert.Equal(1, Geo.Len(d), 9);
            Assert.Equal(face, Clutter.FaceOf(d));
            var (u2, v2) = Clutter.ProjectToFace(face, d);
            Assert.Equal(u, u2, 9);
            Assert.Equal(v, v2, 9);
        }
    }

    [Fact]
    public void FaceOf_IsConsistentWithProjection_AndCoversTheSphere()
    {
        var rng = new Random(2);
        for (int i = 0; i < 2000; i++)
        {
            var d = double3.Normalize(new double3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5));
            int face = Clutter.FaceOf(d);
            var (u, v) = Clutter.ProjectToFace(face, d);
            Assert.InRange(u, -1e-9, 1 + 1e-9);
            Assert.InRange(v, -1e-9, 1 + 1e-9);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InstanceDirection_IsAUnitVector_NearItsSubCell_AndDeterministic(int face)
    {
        const int res = 5000;
        for (int cx = 3; cx < res; cx += 1700)
            for (int local = 0; local < 16; local += 5)
            {
                var d = Clutter.InstanceDirection(face, cx, 77, local, 15 - local, res);
                Assert.Equal(1, Geo.Len(d), 9);
                Assert.Equal(d, Clutter.InstanceDirection(face, cx, 77, local, 15 - local, res));
                Assert.Equal(face, Clutter.FaceOf(d));
                var (u, v) = Clutter.ProjectToFace(face, d);
                // jitter is +-1 sub-cell around the sub-cell centre
                Assert.InRange(u * res - cx, (local - 1.0) / 16, (local + 2.0) / 16);
                Assert.InRange(v * res - 77, (local - 1.0) / 16, (local + 2.0) / 16);   // rows run against v: local y = 15 - local
            }
    }

    [Fact]
    public void Collect_MarksExactlyTheSubCellsInsideTheFootprint()
    {
        var dir = Geo.Dir(40, -100);
        Placement.Axes(dir, out _, out _);
        var b = Placement.MakeInstance(new RawBuilding { Id = 1, Lat = 40, Lon = -100, Height = 10, Variant = 1 }, new Box2(0, 0, 0.6, 24, 11), R, 0, Geo.Flat());
        var grid = new ClutterGrid(0, Clutter.GridResolution(R, 5.5), 1.0);
        var masks = new Dictionary<ClutterCellKey, uint[]>();
        Clutter.Collect(new[] { b }, new[] { grid }, R, masks);
        Assert.NotEmpty(masks);

        // brute force over the cells around the building
        double3 c = double3.Normalize(b.Base);
        double3 a = double3.Normalize(b.AxisA - c * double3.Dot(b.AxisA, c)), s = double3.Normalize(b.AxisB - c * double3.Dot(b.AxisB, c));
        double halfA = 12 + 1.0, halfB = 5.5 + 1.0;
        int face = Clutter.FaceOf(c);
        var (u0, v0) = Clutter.ProjectToFace(face, c);
        int cellX0 = (int)(u0 * grid.Resolution), cellY0 = (int)(v0 * grid.Resolution);
        int expected = 0, found = 0;
        for (int cy = cellY0 - 2; cy <= cellY0 + 2; cy++)
            for (int cx = cellX0 - 2; cx <= cellX0 + 2; cx++)
                for (int ly = 0; ly < 16; ly++)
                    for (int lx = 0; lx < 16; lx++)
                    {
                        var d = Clutter.InstanceDirection(face, cx, cy, lx, ly, grid.Resolution);
                        var rel = (d - c) * R;
                        bool inside = Math.Abs(double3.Dot(rel, a)) <= halfA && Math.Abs(double3.Dot(rel, s)) <= halfB && double3.Dot(d, c) >= 0.99;
                        masks.TryGetValue(new ClutterCellKey(0, face, cx, cy), out var mask);
                        int bit = ly * 16 + lx;
                        bool marked = mask != null && (mask[bit >> 5] & (1u << (bit & 31))) != 0;
                        Assert.Equal(inside, marked);
                        if (inside) expected++;
                        if (marked) found++;
                    }
        Assert.True(expected >= 10, $"a 24 x 11 m footprint holds {expected} trees at 5.5 m spacing, expected more than a few");
        Assert.Equal(expected, found);
    }

    [Fact]
    public void Collect_IgnoresDetailPiecesAndEmptyBoxes()
    {
        var b = Placement.MakeInstance(new RawBuilding { Id = 1, Lat = 40, Lon = -100, Height = 10, Variant = 1 }, new Box2(0, 0, 0, 24, 11), R, 0, Geo.Flat());
        var detail = b; detail.IsDetail = true;
        var empty = b; empty.AxisA = default;
        var masks = new Dictionary<ClutterCellKey, uint[]>();
        Clutter.Collect(new[] { detail, empty }, new[] { new ClutterGrid(0, 5000, 1) }, R, masks);
        Assert.Empty(masks);
    }
}

public class BuiltUpTests
{
    private static RawBuilding House(double lat, double lon, double size = 10) => new() { Lat = lat, Lon = lon, Boxes = { new Box2(0, 0, 0, size, size) } };

    [Fact]
    public void Counts_BuildingsInTheNeighbouringCells_AndIgnoresSheds()
    {
        var list = new List<RawBuilding>();
        for (int i = 0; i < 5; i++) list.Add(House(50 + i * 0.0002, 8));
        list.Add(House(50, 8, size: 3));                                           // shed
        var floating = House(50, 8); floating.MinHeight = 3; list.Add(floating);  // canopy
        var apron = House(50, 8); apron.Slab = true; list.Add(apron);
        var built = new BuiltUp(list, 50);
        Assert.Equal(5, built.Around(50.0004, 8));
        Assert.Equal(0, built.Around(50.1, 8));
    }

    [Fact]
    public void Lit_NeedsFourBuildingsNearby_AndThirtyForMotorways()
    {
        var villageHouses = Enumerable.Range(0, 4).Select(i => House(50, 8 + i * 0.0001)).ToList();
        var built = new BuiltUp(villageHouses, 50);
        Assert.True(built.Lit(50, 8.0002, rank: 2));
        Assert.False(built.Lit(50, 8.0002, rank: 6));
        Assert.False(new BuiltUp(villageHouses.Take(3).ToList(), 50).Lit(50, 8.0002, rank: 2));
    }
}

public class DownloadGateTests
{
    [Fact]
    public void Refusal_ClosesTheGateForFiveMinutes_AndCheckThrows()
    {
        var gate = new DownloadGate("test server");
        gate.Check();                                    // open
        var ex = gate.Refused(new InvalidOperationException("HTTP 403 Forbidden"));
        Assert.True(gate.IsClosed);
        Assert.InRange((gate.ReopensAt - DateTime.UtcNow).TotalMinutes, 4.9, 5.1);
        Assert.Throws<DownloadPausedException>(gate.Check);
        Assert.Equal(gate.ReopensAt, ex.Until);
        gate.Refused(new InvalidOperationException("HTTP 403"));   // while closed: no doubling
        Assert.InRange((gate.ReopensAt - DateTime.UtcNow).TotalMinutes, 4.9, 5.1);
    }

    [Fact]
    public void RepeatedRefusals_DoubleThePause_UpToOneHour_AndSuccessResets()
    {
        var gate = new DownloadGate("test server");
        var closedUntil = typeof(DownloadGate).GetField("_closedUntil", BindingFlags.NonPublic | BindingFlags.Instance)!;
        double[] expected = { 5, 10, 20, 40, 60, 60 };
        foreach (double minutes in expected)
        {
            gate.Refused(new InvalidOperationException("HTTP 401"));
            Assert.InRange((gate.ReopensAt - DateTime.UtcNow).TotalMinutes, minutes - 0.1, minutes + 0.1);
            closedUntil.SetValue(gate, DateTime.UtcNow.AddSeconds(-1));        // pause over
            gate.Check();
        }
        gate.Succeeded();
        gate.Refused(new InvalidOperationException("HTTP 401"));
        Assert.InRange((gate.ReopensAt - DateTime.UtcNow).TotalMinutes, 4.9, 5.1);
    }

    [Theory]
    [InlineData("HTTP 403 Forbidden", true)]
    [InlineData("HTTP 401", true)]
    [InlineData("HTTP 404", false)]
    [InlineData("HTTP 502 Bad Gateway", false)]
    [InlineData("timeout", false)]
    public void IsRefusal_OnlyForAuthErrors(string message, bool refusal) =>
        Assert.Equal(refusal, DownloadGate.IsRefusal(new InvalidOperationException(message)));
}

public class WorldCoverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gee-worldcover-" + Guid.NewGuid().ToString("N"));
    private const int BlocksPerRow = 36;
    private const int BlockBytes = 1024 * 1024;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>A little-endian TIFF header with the TileOffsets/TileByteCounts tables, like the ESA COGs.</summary>
    private static byte[] MakeHeader(out long[] offsets, out long[] counts, byte[] blockData)
    {
        int n = BlocksPerRow * BlocksPerRow;
        offsets = new long[n]; counts = new long[n];
        int tableAt = 8 + 2 + 2 * 12 + 4;
        var head = new byte[tableAt + 8 * n];
        head[0] = (byte)'I'; head[1] = (byte)'I'; head[2] = 42; head[4] = 8;
        BitConverter.GetBytes((ushort)2).CopyTo(head, 8);
        void Entry(int i, ushort tag, int at)
        {
            int e = 10 + 12 * i;
            BitConverter.GetBytes(tag).CopyTo(head, e);
            BitConverter.GetBytes((ushort)4).CopyTo(head, e + 2);
            BitConverter.GetBytes((uint)n).CopyTo(head, e + 4);
            BitConverter.GetBytes((uint)at).CopyTo(head, e + 8);
        }
        Entry(0, 324, tableAt); Entry(1, 325, tableAt + 4 * n);
        for (int i = 0; i < n; i++)
        {
            offsets[i] = 100_000 + (long)i * 1000; counts[i] = blockData.Length;
            BitConverter.GetBytes((uint)offsets[i]).CopyTo(head, tableAt + 4 * i);
            BitConverter.GetBytes((uint)counts[i]).CopyTo(head, tableAt + 4 * n + 4 * i);
        }
        return head;
    }

    private static byte[] Compress(Func<int, int, byte> pixel)
    {
        var raw = new byte[BlockBytes];
        for (int y = 0; y < 1024; y++) for (int x = 0; x < 1024; x++) raw[y * 1024 + x] = pixel(x, y);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
        return ms.ToArray();
    }

    private sealed class Server
    {
        public readonly List<string> Urls = new();
        public byte[] Header = Array.Empty<byte>(), Block = Array.Empty<byte>();
        public Exception? Fail;
        public byte[] Get(string url, string agent, long offset, long length)
        {
            Urls.Add(url);
            if (Fail != null) throw Fail;
            Assert.Contains("KSA-GEE", agent);
            return offset == 0 && length == 65536 ? Header : Block;
        }
    }

    private Server Make(out WorldCover wc, Func<int, int, byte>? pixel = null)
    {
        var s = new Server();
        // pixel (651, 317) of the block is Bare (60); the rest Trees (10)
        s.Block = Compress(pixel ?? ((x, y) => x == 651 && y == 317 ? WorldCover.Bare : WorldCover.Trees));
        s.Header = MakeHeader(out _, out _, s.Block);
        wc = new WorldCover(_dir, s.Get);
        return s;
    }

    [Fact]
    public void Load_ThenClassAt_ReadsTheRightPixelOfTheRightFile()
    {
        var server = Make(out var wc);
        Assert.Null(wc.ClassAt(28.6082, -80.6044));                 // nothing loaded yet
        Assert.True(wc.Load(28.6082, -80.6044));
        // 3 x 3 degree file N27W081; pixel (4747, 16701) -> block row 16, column 4 -> pixel (651, 317) in the block
        Assert.Contains("N27W081", server.Urls[0]);
        Assert.Equal(WorldCover.Bare, wc.ClassAt(28.6082, -80.6044));
        Assert.Equal(WorldCover.Trees, wc.ClassAt(28.6082, -80.6044 + 2.0 / 12000));
        Assert.Equal(1, wc.BlocksLoaded);
        Assert.Equal(2, server.Urls.Count);                         // header + block
    }

    [Fact]
    public void SecondInstance_UsesTheDiskCache_WithoutDownloading()
    {
        Make(out var first);
        Assert.True(first.Load(28.6082, -80.6044));
        var second = new WorldCover(_dir, (_, _, _, _) => throw new Exception("must not download"));
        Assert.True(second.Load(28.6082, -80.6044));
        Assert.Equal(WorldCover.Bare, second.ClassAt(28.6082, -80.6044));
    }

    [Fact]
    public void Http404_MeansOpenOcean_AndIsRemembered()
    {
        var server = Make(out var wc);
        server.Fail = new InvalidOperationException("HTTP 404 Not Found");
        Assert.True(wc.Load(0.5, -30.2));
        Assert.Equal(WorldCover.Water, wc.ClassAt(0.5, -30.2));
        Assert.Equal(WorldCover.Water, wc.ClassAt(2.9, -31.0));       // anywhere in that file
        int calls = server.Urls.Count;
        Assert.True(new WorldCover(_dir, (_, _, _, _) => throw new Exception("must not ask again")).Load(0.5, -30.2));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Http403_IsARefusal_NotOcean_AndPausesDownloads()
    {
        var server = Make(out var wc);
        server.Fail = new InvalidOperationException("HTTP 403 Forbidden");
        Assert.False(wc.Load(28.6, -80.6));
        Assert.Null(wc.ClassAt(28.6, -80.6));                           // must not become water
        Assert.NotNull(wc.LastError);
        int calls = server.Urls.Count;
        Assert.False(wc.Load(28.7, -80.7));                              // gate closed: no new request
        Assert.Equal(calls, server.Urls.Count);
        Assert.Contains("paused", wc.LastError);
    }

    [Fact]
    public void CorruptHeader_FailsCleanly()
    {
        var server = Make(out var wc);
        server.Header = new byte[65536];
        Assert.False(wc.Load(28.6, -80.6));
        Assert.Contains("TIFF", wc.LastError);
        Assert.Null(wc.ClassAt(28.6, -80.6));
    }

    [Fact]
    public void MemoryIsBounded_ByEvictingTheOldestBlocks()
    {
        Make(out var wc);
        // 60 distinct blocks in one file (each ~1024 px = 0.085 degrees)
        for (int i = 0; i < 60; i++)
            Assert.True(wc.Load(28.0 + (i % 10) * 0.09, -80.9 + (i / 10) * 0.09));
        Assert.InRange(wc.BlocksLoaded, 40, 48);
    }

    [Fact]
    public void SouthernAndWesternHemispheres_NameTheirFilesLikeEsa()
    {
        var server = Make(out var wc);
        wc.Load(-33.9, 151.2);     // Sydney: S36E150
        wc.Load(51.5, -0.1);       // London: N51W003
        wc.Load(10, 180);          // date line wraps to W180
        Assert.Contains(server.Urls, u => u.Contains("S36E150"));
        Assert.Contains(server.Urls, u => u.Contains("N51W003"));
        Assert.Contains(server.Urls, u => u.Contains("N09W180"));
    }
}
