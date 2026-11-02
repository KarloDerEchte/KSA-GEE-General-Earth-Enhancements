using System.Diagnostics;
using Brutal.Numerics;
using KSA;

namespace OsmBuildings;

/// <summary>
/// City lights for the view from altitude and orbit, where building tiles are far too small and many. Zoom 8
/// vector tiles (about 150 km) give towns and cities with their importance, built-up areas and major roads;
/// these are turned into scattered glows: a dense core around each place, an even spread over residential and
/// commercial land, and strings of lights along motorways. Only tiles on the night side are fetched.
/// A second, global instance reads zoom 4 tiles (about 2500 km, major cities and motorways) for the whole
/// visible night side; its lights are grouped by the zoom 8 tile they fall in and left out where that is ready.
/// </summary>
public sealed class RegionalLights : IDisposable
{
    public const int RegionalZoom = 8;
    public const int GlobalZoom = 4;

    public readonly int Zoom;
    private readonly RegionalLights? _finer;
    private readonly int _heightGrid;
    private readonly int _maxLightsPerTile;

    private enum State { Queued, Loading, Parsed, Building, Ready, Failed }

    private struct RawLight
    {
        public double Lat, Lon;
        public float Radius;
        public uint Color;
        public float Elevation;
        public uint Seed;
    }

    private sealed class RegionTile(int zoom, (int X, int Y) key)
    {
        public readonly (int X, int Y) Key = key;
        public State State = State.Queued;
        public List<RawLight>? Raw;
        public float[]? Heights;
        public int HeightCount;
        /// <summary>One set, or for the global tiles one per zoom 8 tile with lights.</summary>
        public Dictionary<int, LightSet>? Lights;
        public int Built;
        public DateTime RetryAt;

        public (double South, double West, double North, double East) Bounds { get; } = VectorTile.Bounds(zoom, key.X, key.Y);
    }

    /// <summary>Key of a tile, as LightSet.CoverKey uses it.</summary>
    public static int Key(int x, int y) => x << 16 | y;

    private readonly Settings _settings;
    private readonly TileManager _source;
    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), RegionTile> _tiles = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _workers = new();
    private volatile float _cameraLat, _cameraLon;
    private double _planetRadius = 6371000;
    private double _lastScan = -10;

    public volatile LightSet[] Snapshot = Array.Empty<LightSet>();
    /// <summary>Keys of the ready tiles (Key), for the coarser instance to leave out.</summary>
    public volatile HashSet<int> ReadyKeys = new();
    public string? LastError { get; private set; }
    public bool Active { get; private set; }

    /// <param name="finer">The zoom 8 instance, for the global one.</param>
    public RegionalLights(Settings settings, TileManager source, int zoom, RegionalLights? finer = null)
    {
        _settings = settings;
        _source = source;
        Zoom = zoom;
        _finer = finer;
        _heightGrid = zoom >= RegionalZoom ? 9 : 17;
        _maxLightsPerTile = zoom >= RegionalZoom ? 50_000 : 80_000;
        for (int i = 0; i < 2; i++)
        {
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"OsmBuildings lights z{zoom} {i}" };
            _workers.Add(worker);
            worker.Start();
        }
    }

    public (int Queued, int Loading, int Building, int Ready, int Failed, int Lights) Stats()
    {
        int q = 0, l = 0, b = 0, r = 0, f = 0, lights = 0;
        lock (_lock)
        {
            foreach (var t in _tiles.Values)
            {
                switch (t.State)
                {
                    case State.Queued: q++; break;
                    case State.Loading: l++; break;
                    case State.Parsed or State.Building: b++; break;
                    case State.Ready: r++; lights += t.Lights?.Values.Sum(set => set.Count) ?? 0; break;
                    case State.Failed: f++; break;
                }
            }
        }
        return (q, l, b, r, f, lights);
    }

    /// <summary>Main thread, once per frame.</summary>
    public void Update(Celestial body, double3 cameraCcf, double now)
    {
        _planetRadius = body.MeanRadius;
        double r = Math.Sqrt(double3.Dot(cameraCcf, cameraCcf));
        double altitudeKm = (r - _planetRadius) / 1000;
        double lat = Math.Asin(Math.Clamp(cameraCcf.Z / r, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(cameraCcf.Y, cameraCcf.X) * 180 / Math.PI;
        _cameraLat = (float)lat;
        _cameraLon = (float)lon;
        double h = Math.Max(altitudeKm, 0) * 1000;
        double horizonKm = Math.Sqrt(2 * _planetRadius * h + h * h) / 1000;
        bool global = _finer != null;
        // The global tiles cover the whole night side at all times (a few dozen cached tiles), so nothing
        // loads in while the camera zooms or turns.
        Active = _settings.Enabled && (_settings.CityGlow || _settings.CitiesFromAltitude) && altitudeKm < 1e6 && (global
            ? _settings.GlobalLights
            : _settings.RegionalLights && altitudeKm > 5);

        if (Active && now - _lastScan > 1)
        {
            _lastScan = now;
            // Beyond half the circumference means every tile.
            double radiusKm = global ? _planetRadius * Math.PI / 1000 + 100 : Math.Min(_settings.RegionalRadiusKm, horizonKm + 300);
            // The day side is needed too when towns are drawn by day (UrbanRenderer).
            RequestAround(lat, lon, radiusKm, _settings.CitiesFromAltitude ? null : NightLights.SunDirCcf(body, _settings), global ? 0.6 : 0.25);
            Evict(lat, lon, radiusKm * 1.25 + 200);
        }
        Build(body, 1.5);
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var key in _tiles.Where(kv => kv.Value.State != State.Loading).Select(kv => kv.Key).ToList())
                _tiles.Remove(key);
        }
        RebuildSnapshot();
    }

    private double KmPerDegree => _planetRadius * Math.PI / 180 / 1000;

    private double DistanceKm(RegionTile tile, double lat, double lon)
    {
        var (s, w, n, e) = tile.Bounds;
        double dLat = Math.Clamp(lat, s, n) - lat;
        double dLon = Math.Clamp(lon, w, e) - lon;
        if (dLon > 180) dLon -= 360;
        if (dLon < -180) dLon += 360;
        double cos = Math.Cos(lat * Math.PI / 180);
        double dx = KmPerDegree * dLat, dy = KmPerDegree * dLon * cos;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void RequestAround(double lat, double lon, double radiusKm, double3? sun, double sunMargin)
    {
        double dLat = radiusKm / KmPerDegree;
        double dLon = Math.Min(radiusKm / (KmPerDegree * Math.Max(Math.Cos(lat * Math.PI / 180), 0.05)), 180);
        var (x0, y0) = VectorTile.TileAt(Zoom, Math.Min(lat + dLat, 85), lon);
        var (_, y1) = VectorTile.TileAt(Zoom, Math.Max(lat - dLat, -85), lon);
        int n = 1 << Zoom;
        int span = (int)Math.Ceiling(dLon / 360 * n) + 1;
        int added = 0;
        lock (_lock)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int dx = -span; dx <= span; dx++)
                {
                    int x = ((x0 + dx) % n + n) % n;
                    if (_tiles.TryGetValue((x, y), out var tile))
                    {
                        if (tile.State == State.Failed && DateTime.UtcNow >= tile.RetryAt)
                        {
                            tile.State = State.Queued;
                            added++;
                        }
                        continue;
                    }
                    var candidate = new RegionTile(Zoom, (x, y));
                    if (DistanceKm(candidate, lat, lon) > radiusKm)
                        continue;
                    // Daylit tiles are skipped; they are picked up once the terminator comes closer (the global ones early,
                    // so they are ready before it arrives even under time warp).
                    var (s, w, nn, e) = candidate.Bounds;
                    double halfAngle = Math.Max(nn - s, (e - w) * Math.Cos((s + nn) / 2 * Math.PI / 180)) * Math.PI / 180 * 0.71;
                    if (sun is { } sunDir && double3.Dot(Placement.DirCcf((s + nn) / 2, (w + e) / 2), sunDir) > sunMargin + Math.Sin(halfAngle))
                        continue;
                    _tiles.Add((x, y), candidate);
                    added++;
                }
            }
        }
        if (added > 0)
            _work.Release(added);
    }

    private void Evict(double lat, double lon, double keepKm)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var (key, tile) in _tiles.ToList())
            {
                if (tile.State != State.Loading && DistanceKm(tile, lat, lon) > keepKm)
                {
                    _tiles.Remove(key);
                    changed |= tile.State == State.Ready;
                }
            }
        }
        if (changed)
            RebuildSnapshot();
    }

    private void RebuildSnapshot()
    {
        lock (_lock)
        {
            Snapshot = _tiles.Values.Where(t => t.State == State.Ready && t.Lights != null).SelectMany(t => t.Lights!.Values).ToArray();
            ReadyKeys = _tiles.Values.Where(t => t.State == State.Ready).Select(t => Key(t.Key.X, t.Key.Y)).ToHashSet();
        }
    }

    // ---- main thread: terrain heights and light positions ----

    private void Build(Celestial body, double budgetMs)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            RegionTile? tile;
            lock (_lock)
            {
                float lat = _cameraLat, lon = _cameraLon;
                tile = _tiles.Values
                    .Where(t => t.State is State.Parsed or State.Building)
                    .OrderBy(t => DistanceKm(t, lat, lon))
                    .FirstOrDefault();
            }
            if (tile?.Raw is not { } raw)
                return;

            var (s, w, n, e) = tile.Bounds;
            int g = _heightGrid;
            tile.Heights ??= new float[g * g];
            while (tile.HeightCount < g * g)
            {
                if (Stopwatch.GetTimestamp() >= deadline)
                    return;
                int i = tile.HeightCount;
                double hLat = n - (n - s) * (i / g) / (g - 1);
                double hLon = w + (e - w) * (i % g) / (g - 1);
                tile.Heights[i] = (float)body.GetTerrainHeightFromDirCcf(Placement.DirCcf(hLat, hLon));
                tile.HeightCount++;
            }

            if (tile.State == State.Parsed)
            {
                tile.Lights = new Dictionary<int, LightSet>();
                tile.State = State.Building;
            }
            var sets = tile.Lights!;
            while (tile.Built < raw.Count)
            {
                if ((tile.Built & 255) == 0 && Stopwatch.GetTimestamp() >= deadline)
                    return;
                var light = raw[tile.Built++];
                double3 dir = Placement.DirCcf(light.Lat, light.Lon);
                double height = Math.Max(HeightAt(tile, light.Lat, light.Lon), 0) + light.Elevation;
                // Global lights go into sets per zoom 8 tile, so they can be left out where that tile is loaded.
                int cover = -1;
                if (_finer != null)
                {
                    var (cx, cy) = VectorTile.TileAt(RegionalZoom, light.Lat, light.Lon);
                    cover = Key(cx, cy);
                }
                if (!sets.TryGetValue(cover, out var set))
                {
                    var (cs, cw, cn, ce) = cover < 0 ? (s, w, n, e) : VectorTile.Bounds(RegionalZoom, cover >> 16, cover & 0xFFFF);
                    set = new LightSet(LightKind.Regional, Placement.DirCcf((cs + cn) / 2, (cw + ce) / 2) * _planetRadius, 300 * Coarseness(Zoom)) { CoverKey = cover };
                    sets.Add(cover, set);
                }
                set.Add(dir * (_planetRadius + height), light.Radius, light.Color, light.Seed);
            }
            foreach (var set in sets.Values)
                set.Finish();
            tile.Raw = null;
            lock (_lock)
                tile.State = State.Ready;
            RebuildSnapshot();
        }
    }

    private double HeightAt(RegionTile tile, double lat, double lon)
    {
        int g = _heightGrid;
        var (s, w, n, e) = tile.Bounds;
        double u = Math.Clamp((lon - w) / (e - w), 0, 1) * (g - 1);
        double v = Math.Clamp((n - lat) / (n - s), 0, 1) * (g - 1);
        int x0 = Math.Min((int)u, g - 2), y0 = Math.Min((int)v, g - 2);
        double fx = u - x0, fy = v - y0;
        var h = tile.Heights!;
        double top = h[y0 * g + x0] * (1 - fx) + h[y0 * g + x0 + 1] * fx;
        double bottom = h[(y0 + 1) * g + x0] * (1 - fx) + h[(y0 + 1) * g + x0 + 1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    // ---- worker threads: download and generate ----

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _work.Wait(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RegionTile? tile;
            lock (_lock)
            {
                float lat = _cameraLat, lon = _cameraLon;
                tile = _tiles.Values
                    .Where(t => t.State == State.Queued)
                    .OrderBy(t => DistanceKm(t, lat, lon))
                    .FirstOrDefault();
                if (tile != null)
                    tile.State = State.Loading;
            }
            if (tile == null)
                continue;

            try
            {
                byte[] data = _source.Fetch(Zoom, tile.Key.X, tile.Key.Y, out bool fromCache);
                var raw = Generate(data, Zoom, tile.Key.X, tile.Key.Y, _maxLightsPerTile);
                lock (_lock)
                {
                    tile.Raw = raw;
                    tile.State = State.Parsed;
                }
                if (!fromCache)
                    Log.Info($"regional tile {Zoom}/{tile.Key.X}/{tile.Key.Y}: {raw.Count} lights");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("HTTP 429") || ex.Message.Contains("HTTP 503"))
                    _source.Throttled();
                LastError = $"regional tile {tile.Key.X}/{tile.Key.Y}: {ex.Message}";
                Log.Error(LastError);
                lock (_lock)
                {
                    tile.State = State.Failed;
                    tile.RetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                }
            }
        }
    }

    /// <summary>Scatters the lights of one tile. Kept free of game types.</summary>
    private static List<RawLight> Generate(byte[] data, int zoom, int tx, int ty, int maxLights)
    {
        var lights = new List<RawLight>();
        var (south, west, north, east) = VectorTile.Bounds(zoom, tx, ty);
        double cosLat = Math.Cos((south + north) / 2 * Math.PI / 180);
        const double metersPerDegree = 6371000 * Math.PI / 180;
        int feature = 0;
        // Coarser tiles get fewer, bigger glows: the same light per area, spread out further.
        double coarse = Coarseness(zoom);

        VectorTile.ForEachFeature(data, layer => layer is "place" or "landuse" or "transportation", (layer, type, tags, geometry, extent) =>
        {
            feature++;
            string? cls = tags.GetValueOrDefault("class") as string;
            double metersPerUnit = (east - west) * metersPerDegree * cosLat / extent;
            bool Inside(Footprint.P p) => p.X >= 0 && p.Y >= 0 && p.X < extent && p.Y < extent;
            (double Lat, double Lon) Geo(double x, double y) => VectorTile.ToLatLon(zoom, tx + x / extent, ty + y / extent);

            void Add(double lat, double lon, double radius, (int, int, int) color, double intensity, float elevation, uint seed)
            {
                if (lights.Count < maxLights)
                    lights.Add(new RawLight { Lat = lat, Lon = lon, Radius = (float)radius, Color = LightSet.Pack(color, intensity), Elevation = elevation, Seed = seed });
            }

            if (layer == "place" && type == 1)
            {
                double rank = tags.GetValueOrDefault("rank") is double r ? r : 16;
                // Built-up radius from the place's importance (lower rank is a bigger place).
                double radiusKm = cls switch
                {
                    "city" => Math.Clamp(30 * Math.Exp(-rank / 8), 2, 20),
                    "town" => Math.Clamp(30 * Math.Exp(-rank / 8), 1, 4),
                    "village" => 0.7,
                    "hamlet" => 0.3,
                    _ => 0,
                };
                if (radiusKm <= 0)
                    return;
                double extentM = radiusKm * 1000;
                double glow = Math.Max(60 * coarse, extentM / 40);
                int count = (int)Math.Clamp(0.5 * (extentM / glow) * (extentM / glow), 4, 800);
                foreach (var p in VectorTile.Points(geometry))
                {
                    if (!Inside(p))
                        continue;
                    var (lat0, lon0) = Geo(p.X, p.Y);
                    for (int k = 0; k < count; k++)
                    {
                        uint h = LightSet.Hash(tx * 4096 + ty, feature, k);
                        // Gaussian spread, sigma half the radius, cut at 1.6 radii.
                        double u1 = ((LightSet.Hash(h + 1) >> 8) + 1) / 16777217.0, u2 = (LightSet.Hash(h + 2) >> 8) / 16777216.0;
                        double dist = Math.Min(extentM * 0.5 * Math.Sqrt(-2 * Math.Log(u1)), extentM * 1.6);
                        double angle = 2 * Math.PI * u2;
                        double lat = lat0 + dist * Math.Sin(angle) / metersPerDegree;
                        double lon = lon0 + dist * Math.Cos(angle) / (metersPerDegree * Math.Max(Math.Cos(lat0 * Math.PI / 180), 0.05));
                        double core = Math.Exp(-(dist / extentM) * (dist / extentM));
                        Add(lat, lon, glow, LightSet.StreetColor(h >> 4), 0.5 + 0.8 * core, 15, h);
                    }
                }
            }
            else if (layer == "landuse" && type == 3 && cls is "residential" or "suburb" or "neighbourhood" or "quarter"
                     or "commercial" or "industrial" or "retail")
            {
                double spacingM = 450 * coarse;
                double step = spacingM / metersPerUnit;
                bool busy = cls is "commercial" or "industrial" or "retail";
                foreach (var ring in VectorTile.ExteriorRings(geometry))
                {
                    double minX = ring.Min(q => q.X), maxX = ring.Max(q => q.X), minY = ring.Min(q => q.Y), maxY = ring.Max(q => q.Y);
                    int k = 0;
                    for (double gy = Math.Floor(minY / step) * step; gy <= maxY; gy += step)
                    {
                        for (double gx = Math.Floor(minX / step) * step; gx <= maxX; gx += step, k++)
                        {
                            uint h = LightSet.Hash((long)(gx * 7919 + gy), tx * 4096 + ty, k);
                            var p = new Footprint.P(gx + ((LightSet.Hash(h + 1) >> 8) / 16777216.0 - 0.5) * step,
                                                    gy + ((LightSet.Hash(h + 2) >> 8) / 16777216.0 - 0.5) * step);
                            if (!Inside(p) || !InRing(ring, p))
                                continue;
                            var (lat, lon) = Geo(p.X, p.Y);
                            var color = busy && (h & 1) == 0 ? LightSet.Neutral : LightSet.StreetColor(h >> 4);
                            Add(lat, lon, spacingM * 0.32, color, busy ? 0.7 : 0.5, 10, h);
                        }
                    }
                }
            }
            else if (layer == "transportation" && type == 2 && cls is "motorway" or "trunk" or "primary"
                     && tags.GetValueOrDefault("brunnel") as string != "tunnel")
            {
                double spacingM = (cls == "motorway" ? 300 : 400) * coarse;
                double step = spacingM / metersPerUnit;
                int k = 0;
                foreach (var line in VectorTile.Lines(geometry))
                {
                    double carried = step / 2;
                    for (int i = 0; i + 1 < line.Count; i++)
                    {
                        Footprint.P a = line[i], b = line[i + 1];
                        double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                        for (; carried < length; carried += step, k++)
                        {
                            var p = new Footprint.P(a.X + (b.X - a.X) * carried / length, a.Y + (b.Y - a.Y) * carried / length);
                            if (!Inside(p))
                                continue;
                            uint h = LightSet.Hash(tx * 4096 + ty, feature, k);
                            var (lat, lon) = Geo(p.X, p.Y);
                            Add(lat, lon, 60 * coarse, LightSet.StreetColor(h >> 4), cls == "motorway" ? 1.0 : 0.75, 10, h);
                        }
                        carried -= length;
                    }
                }
            }
        });
        return lights;
    }

    /// <summary>How much coarser than zoom 8 a zoom's tiles are: 1 at zoom 8, 16 at zoom 4.</summary>
    private static int Coarseness(int zoom) => 1 << Math.Max(RegionalZoom - zoom, 0);

    /// <summary>Even-odd point in polygon test.</summary>
    private static bool InRing(List<Footprint.P> ring, Footprint.P p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            Footprint.P a = ring[i], b = ring[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var worker in _workers)
            worker.Join(TimeSpan.FromSeconds(2));
    }
}
