using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace GEE;

/// <summary>
/// ESA WorldCover 2021 (v200): global land cover at 10 m from Sentinel-1 and -2, CC BY 4.0
/// (© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover
/// consortium). Read straight from the Cloud Optimized GeoTIFFs on AWS Open Data: one file per 3 x 3 degrees,
/// 36000 x 36000 pixels on a plain latitude/longitude grid, stored as 1024 x 1024 Deflate blocks, so a block is
/// one HTTP range request. Headers and blocks are cached on disk as fetched; decoded blocks are kept in memory.
/// Files that do not exist (open ocean) count as water; inside a file the sea is NoData (0). Lookups are thread-safe.
/// </summary>
internal sealed class WorldCover
{
    public const byte NoData = 0, Trees = 10, Shrubland = 20, Grassland = 30, Cropland = 40, BuiltUp = 50, Bare = 60,
        Snow = 70, Water = 80, Wetland = 90, Mangroves = 95, Moss = 100;

    private const string UrlFormat = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{0}_Map.tif";
    private const string UserAgent = "KSA-GEE/0.2 (Kitten Space Agency mod; land cover for tree placement)";
    private const int PixelsPerDegree = 12000, FileDegrees = 3, Block = 1024, BlocksPerRow = (FileDegrees * PixelsPerDegree + Block - 1) / Block;
    private const int HeaderBytes = 65536, MaxBlocksInMemory = 48;

    /// <summary>Block offsets and sizes within one file; null arrays: the file does not exist.</summary>
    private sealed record Header(long[]? Offsets, long[]? Counts);

    private readonly string _dir;
    private readonly Func<string, string, long, long, byte[]> _getRange;
    private readonly ConcurrentDictionary<(int South, int West), Header> _headers = new();
    private readonly ConcurrentDictionary<(int South, int West, int Index), byte[]> _blocks = new();
    private readonly ConcurrentDictionary<(int South, int West, int Index), long> _blockUsed = new();
    private long _clock;

    public int BlocksLoaded => _blocks.Count;
    public string? LastError { get; private set; }

    public WorldCover(string modDir, Func<string, string, long, long, byte[]> getRange)
    {
        _dir = Path.Combine(modDir, "cache", "worldcover");
        _getRange = getRange;
    }

    /// <summary>Land cover class at a place, if its block is in memory; null if not loaded yet.</summary>
    public byte? ClassAt(double latDeg, double lonDeg)
    {
        var (south, west, index, px, py) = Locate(latDeg, lonDeg);
        if (_blocks.TryGetValue((south, west, index), out byte[]? pixels))
            return pixels[py * Block + px];
        return _headers.TryGetValue((south, west), out var header) && header.Offsets == null ? Water : null;
    }

    /// <summary>
    /// Worker thread: makes sure the block holding a place is in memory, from the disk cache or the server. Blocks
    /// (seconds over the network). False if it could not be fetched.
    /// </summary>
    public bool Load(double latDeg, double lonDeg)
    {
        var (south, west, index, _, _) = Locate(latDeg, lonDeg);
        var key = (south, west, index);
        if (_blocks.ContainsKey(key))
        {
            _blockUsed[key] = Interlocked.Increment(ref _clock);
            return true;
        }
        string file = FileName(south, west);
        try
        {
            Header header = _headers.TryGetValue((south, west), out var known) ? known : _headers[(south, west)] = LoadHeader(file);
            if (header.Offsets == null)
                return true;
            string path = Path.Combine(_dir, file, $"{index}.zz");
            byte[] compressed;
            if (File.Exists(path))
                compressed = File.ReadAllBytes(path);
            else
            {
                compressed = Download(file, header.Offsets[index], header.Counts![index]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, compressed);
            }
            var pixels = new byte[Block * Block];
            using (var inflate = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
                inflate.ReadExactly(pixels);
            Trim();
            _blocks[key] = pixels;
            _blockUsed[key] = Interlocked.Increment(ref _clock);
            return true;
        }
        catch (Exception e)
        {
            // A refusal is logged once by the gate; the caller asks again for every cell until downloads resume.
            LastError = e is DownloadPausedException ? e.Message : $"WorldCover {file} block {index}: {e.Message}";
            if (e is not DownloadPausedException)
                Log.Error(LastError);
            return false;
        }
    }

    private readonly DownloadGate _gate = new("land cover server");

    /// <summary>A range of a file from the server; paused for a while after the server refuses access.</summary>
    private byte[] Download(string file, long offset, long length)
    {
        _gate.Check();
        try
        {
            byte[] data = _getRange(string.Format(UrlFormat, file), UserAgent, offset, length);
            _gate.Succeeded();
            return data;
        }
        catch (Exception e) when (DownloadGate.IsRefusal(e))
        {
            throw _gate.Refused(e);
        }
    }

    /// <summary>A file's name from its south-west corner, e.g. N27W081.</summary>
    private static string FileName(int south, int west) =>
        $"{(south >= 0 ? 'N' : 'S')}{Math.Abs(south):00}{(west >= 0 ? 'E' : 'W')}{Math.Abs(west):000}";

    /// <summary>File (south-west corner), block index and pixel within the block.</summary>
    private static (int South, int West, int Index, int Px, int Py) Locate(double latDeg, double lonDeg)
    {
        latDeg = Math.Clamp(latDeg, -89.99999, 89.99999);
        lonDeg = lonDeg >= 180 ? lonDeg - 360 : lonDeg;
        int south = (int)Math.Floor(latDeg / FileDegrees) * FileDegrees, west = (int)Math.Floor(lonDeg / FileDegrees) * FileDegrees;
        // The grid starts at the file's north-west corner.
        int x = Math.Clamp((int)((lonDeg - west) * PixelsPerDegree), 0, FileDegrees * PixelsPerDegree - 1);
        int y = Math.Clamp((int)((south + FileDegrees - latDeg) * PixelsPerDegree), 0, FileDegrees * PixelsPerDegree - 1);
        return (south, west, (y / Block) * BlocksPerRow + x / Block, x % Block, y % Block);
    }

    /// <summary>Block table of a file from its first bytes (cached), or a missing file.</summary>
    private Header LoadHeader(string file)
    {
        string dir = Path.Combine(_dir, file);
        string path = Path.Combine(dir, "header.bin"), missing = Path.Combine(dir, "missing");
        if (File.Exists(missing))
            return new Header(null, null);
        byte[] head;
        if (File.Exists(path))
            head = File.ReadAllBytes(path);
        else
        {
            try
            {
                head = Download(file, 0, HeaderBytes);
            }
            catch (InvalidOperationException e) when (e.Message.StartsWith("HTTP 404"))
            {
                // No file: open ocean. (Only 404 says so: a 403 is a refusal, which must not mark land as sea for good.)
                Directory.CreateDirectory(dir);
                File.WriteAllText(missing, "");
                return new Header(null, null);
            }
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, head);
        }
        return ParseHeader(head);
    }

    /// <summary>Tile offsets and byte counts of the first image (full resolution) of a little-endian classic TIFF.</summary>
    private static Header ParseHeader(byte[] d)
    {
        if (d.Length < 8 || d[0] != (byte)'I' || BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)) != 42)
            throw new InvalidDataException("not a little-endian TIFF");
        int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(ifd));
        long[]? offsets = null, counts = null;
        for (int i = 0; i < entries; i++)
        {
            int e = ifd + 2 + 12 * i;
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(e));
            int type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(e + 2));
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 4));
            int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 8));
            if (tag is not (324 or 325))
                continue;
            if (type != 4 || at + 4L * count > d.Length)
                throw new InvalidDataException($"unexpected tile table (tag {tag}, type {type})");
            var values = new long[count];
            for (int k = 0; k < count; k++)
                values[k] = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 4 * k));
            if (tag == 324)
                offsets = values;
            else
                counts = values;
        }
        if (offsets == null || counts == null || offsets.Length != BlocksPerRow * BlocksPerRow)
            throw new InvalidDataException("no tile table");
        return new Header(offsets, counts);
    }

    /// <summary>Drops the least recently used blocks beyond the memory limit (1 MB each).</summary>
    private void Trim()
    {
        while (_blocks.Count >= MaxBlocksInMemory && !_blockUsed.IsEmpty)
        {
            var oldest = _blockUsed.MinBy(kv => kv.Value).Key;
            _blocks.TryRemove(oldest, out _);
            _blockUsed.TryRemove(oldest, out _);
        }
    }
}
