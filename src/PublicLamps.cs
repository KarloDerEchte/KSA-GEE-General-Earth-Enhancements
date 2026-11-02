using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace GEE;

/// <summary>What OpenStreetMap knows about the street lighting of one tile.</summary>
public sealed class LampData
{
    /// <summary>Mapped lamp posts (highway=street_lamp).</summary>
    public List<(double Lat, double Lon)> Lamps = new();
    /// <summary>Roads with a lit tag: lit=no (or disused) is dark, anything else is lit.</summary>
    public List<(bool Lit, List<(double Lat, double Lon)> Points)> Ways = new();

    public static LampData Parse(byte[] json)
    {
        var data = new LampData();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            string type = e.GetProperty("type").GetString() ?? "";
            if (type == "node" && e.TryGetProperty("lat", out var lat) && e.TryGetProperty("lon", out var lon))
            {
                data.Lamps.Add((lat.GetDouble(), lon.GetDouble()));
            }
            else if (type == "way" && e.TryGetProperty("geometry", out var geometry) && e.TryGetProperty("tags", out var tags))
            {
                string lit = tags.TryGetProperty("lit", out var l) ? l.GetString() ?? "" : "";
                var points = new List<(double, double)>();
                foreach (var p in geometry.EnumerateArray())
                    points.Add((p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble()));
                if (points.Count >= 2)
                    data.Ways.Add((lit is not ("no" or "disused" or "unknown"), points));
            }
        }
        return data;
    }
}

/// <summary>
/// Fetches OpenStreetMap street lighting from the Overpass API in the background, one zoom 12 block (16 tiles,
/// about 10 km) per query: lamp posts (coordinates only) and the lit tag of the road classes the mod draws, neither
/// of which is in the vector tiles. The public servers answer in 15-40 s, mostly queueing, so the waiting is hidden:
/// one worker per server fetches different blocks at the same time (never the same block twice at once), blocks are
/// prefetched around the camera well beyond the loaded tiles, a small timeout gets earlier server slots (dense blocks
/// that do not fit are split in four), and results are cached on disk for good. Until a block is in, and where a
/// fetch keeps failing, the street light rule falls back to the building density.
/// </summary>
internal sealed class PublicLamps : IDisposable
{
    private readonly string[] Endpoints;
    private const string UserAgent = "KSA-GEE/0.2 (Kitten Space Agency mod; street lights)";
    private const string Roads = "^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|pedestrian|motorway_link|trunk_link|primary_link|secondary_link|tertiary_link)$";
    private const int MaxAttempts = 4;

    private readonly string _dir;
    private readonly Func<Func<string, string, byte[]>> _getFactory;
    private Func<string, string, byte[]>? _get;
    // Keyed by the zoom 12 block (TileKey numbers at zoom 12); a tile's data is its block's.
    private readonly ConcurrentDictionary<TileKey, LampData> _ready = new();
    private readonly Dictionary<TileKey, double> _wanted = new();
    private readonly HashSet<TileKey> _busy = new();
    private readonly Dictionary<TileKey, int> _attempts = new();
    private readonly HashSet<TileKey> _gaveUp = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _workers = new();
    public string? LastError { get; private set; }
    public int Fetched;

    private static TileKey Block(TileKey tile) => new(tile.X >> 2, tile.Y >> 2);

    /// <param name="endpoints">Overpass API interpreter URLs (Settings.StreetLightOverpassUrls), one worker each.</param>
    public PublicLamps(string modDir, Func<Func<string, string, byte[]>> getFactory, string endpoints)
    {
        Endpoints = endpoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _)).Distinct().ToArray();
        _dir = Path.Combine(modDir, "cache", "lamps");
        _getFactory = getFactory;
        for (int i = 0; i < Endpoints.Length; i++)
        {
            int endpoint = i;
            var worker = new Thread(() => WorkerLoop(endpoint)) { IsBackground = true, Name = $"GEE street lights {i}" };
            _workers.Add(worker);
            worker.Start();
        }
    }

    /// <summary>The street lighting around a zoom 14 tile (its whole block).</summary>
    public bool TryGet(TileKey key, out LampData data) => _ready.TryGetValue(Block(key), out data!);

    /// <summary>True once every attempt for the tile's block has failed; meanwhile and then the fallback holds.</summary>
    public bool Failed(TileKey key)
    {
        lock (_lock)
            return _gaveUp.Contains(Block(key));
    }

    /// <summary>Main thread: asks for a tile's block (distance orders the queue).</summary>
    public void Request(TileKey tile, double distanceKm) => RequestBlock(Block(tile), distanceKm);

    /// <summary>Main thread: prefetches every block within the radius around the camera, nearest first.</summary>
    public void RequestArea(double lat, double lon, double radiusKm)
    {
        double kmPerDeg = 6371 * Math.PI / 180;
        double cos = Math.Max(Math.Cos(lat * Math.PI / 180), 0.05);
        double dLat = radiusKm / kmPerDeg, dLon = radiusKm / (kmPerDeg * cos);
        var (x0, y0) = VectorTile.TileAt(12, Math.Min(lat + dLat, 85), lon - dLon);
        var (x1, y1) = VectorTile.TileAt(12, Math.Max(lat - dLat, -85), lon + dLon);
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                var (s, w, n, e) = VectorTile.Bounds(12, x, y);
                double cLat = Math.Clamp(lat, s, n), cLon = Math.Clamp(lon, w, e);
                double dist = Math.Sqrt(Math.Pow((cLat - lat) * kmPerDeg, 2) + Math.Pow((cLon - lon) * kmPerDeg * cos, 2));
                if (dist <= radiusKm)
                    RequestBlock(new TileKey(x, y), dist);
            }
        }
    }

    private void RequestBlock(TileKey key, double distanceKm)
    {
        if (_ready.ContainsKey(key))
            return;
        lock (_lock)
        {
            if (_gaveUp.Contains(key) || _busy.Contains(key))
                return;
            bool added = !_wanted.ContainsKey(key);
            _wanted[key] = distanceKm;
            if (added)
                _signal.Release();
        }
    }

    private void WorkerLoop(int endpoint)
    {
        while (!_cts.IsCancellationRequested)
        {
            TileKey key = new(-1, -1);
            lock (_lock)
            {
                if (_wanted.Count > 0)
                {
                    key = _wanted.MinBy(kv => kv.Value).Key;
                    _wanted.Remove(key);
                    _busy.Add(key);
                }
            }
            if (key.X < 0)
            {
                try
                {
                    _signal.Wait(2000, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            bool ok = Load(key, endpoint);
            lock (_lock)
                _busy.Remove(key);
            // A busy server rests a while; the block goes back to the queue for the others.
            if (!ok)
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
        }
    }

    private bool Load(TileKey key, int endpoint)
    {
        string path = Path.Combine(_dir, $"z12_{key.X}_{key.Y}.json");
        try
        {
            if (!File.Exists(path))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                byte[] json;
                try
                {
                    json = Query(VectorTile.Bounds(12, key.X, key.Y), endpoint);
                }
                catch (TimeoutException)
                {
                    // Too much for one quick query (a dense city): the four quarters, merged.
                    var parts = new List<byte[]>();
                    for (int k = 0; k < 4; k++)
                        parts.Add(Query(VectorTile.Bounds(13, key.X * 2 + (k & 1), key.Y * 2 + (k >> 1)), endpoint));
                    json = Merge(parts);
                }
                Directory.CreateDirectory(_dir);
                File.WriteAllBytes(path, json);
                Interlocked.Increment(ref Fetched);
                var fresh = LampData.Parse(json);
                Log.Info($"street lights block {key.X}/{key.Y}: {fresh.Lamps.Count} lamps, {fresh.Ways.Count} lit-tagged roads, "
                    + $"{clock.Elapsed.TotalSeconds:0} s from {new Uri(Endpoints[endpoint]).Host}");
            }
            _ready[key] = LampData.Parse(File.ReadAllBytes(path));
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"street lights {key.X}/{key.Y} ({new Uri(Endpoints[endpoint]).Host}): {ex.Message}";
            lock (_lock)
            {
                int attempts = _attempts.GetValueOrDefault(key) + 1;
                _attempts[key] = attempts;
                if (attempts >= MaxAttempts)
                    _gaveUp.Add(key);
                else
                    _wanted[key] = 0;
            }
            return false;
        }
    }

    private byte[] Query((double S, double W, double N, double E) b, int endpoint)
    {
        string bbox = string.Create(CultureInfo.InvariantCulture, $"{b.S:0.######},{b.W:0.######},{b.N:0.######},{b.E:0.######}");
        string query = $"[out:json][timeout:25][maxsize:134217728];node[\"highway\"=\"street_lamp\"]({bbox});out skel qt;"
            + $"way[\"highway\"~\"{Roads}\"][\"lit\"]({bbox});out tags geom qt;";
        _get ??= _getFactory();
        byte[] json = _get($"{Endpoints[endpoint]}?data={Uri.EscapeDataString(query)}", UserAgent);
        if (json.Length == 0 || json[0] != (byte)'{')
            throw new InvalidOperationException("no JSON (server busy)");
        // A query that ran out of time or memory still answers 200, with a remark and only part of the data.
        string head = System.Text.Encoding.UTF8.GetString(json, 0, Math.Min(json.Length, 800));
        if (head.Contains("\"remark\"") && (head.Contains("timed out") || head.Contains("out of memory") || head.Contains("error")))
            throw new TimeoutException("query too big for the limits of the server");
        return json;
    }

    /// <summary>Joins Overpass answers (their element lists) into one.</summary>
    private static byte[] Merge(List<byte[]> parts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("elements");
            foreach (byte[] part in parts)
            {
                using var doc = JsonDocument.Parse(part);
                foreach (var e in doc.RootElement.GetProperty("elements").EnumerateArray())
                    e.WriteTo(writer);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var worker in _workers)
            worker.Join(TimeSpan.FromSeconds(2));
    }
}
