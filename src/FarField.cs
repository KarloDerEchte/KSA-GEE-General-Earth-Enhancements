using System.Diagnostics;
using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>One zoom 13 tile of the far field: simplified buildings as single boxes.</summary>
internal sealed class FarTile(int x, int y)
{
    public const int Zoom = 13, GridSize = 9;
    public readonly int X = x, Y = y;
    public enum Stage { Queued, Loading, Parsed, Placing, Ready, Failed }
    public volatile Stage State = Stage.Queued;
    public float[]? Heights;
    public BuildingInstance[] Instances = Array.Empty<BuildingInstance>();
    /// <summary>Zoom 14 tile of each instance (TileKey packed as X * 16384 + Y), to leave it out where that is loaded.</summary>
    public int[] NearKeys = Array.Empty<int>();
    public int Count;
    public double3 CenterCcf;
    public double RadiusM;
    public DateTime RetryAt;

    public (double S, double W, double N, double E) Bounds => VectorTile.Bounds(Zoom, X, Y);

    public double HeightAt(double lat, double lon)
    {
        const int g = GridSize;
        var (s, w, n, e) = Bounds;
        double u = Math.Clamp((lon - w) / (e - w), 0, 1) * (g - 1);
        double v = Math.Clamp((n - lat) / (n - s), 0, 1) * (g - 1);
        int x0 = Math.Min((int)u, g - 2), y0 = Math.Min((int)v, g - 2);
        double fx = u - x0, fy = v - y0;
        var h = Heights!;
        double top = h[y0 * g + x0] * (1 - fx) + h[y0 * g + x0 + 1] * fx;
        double bottom = h[(y0 + 1) * g + x0] * (1 - fx) + h[(y0 + 1) * g + x0 + 1] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}

/// <summary>
/// Buildings beyond the loaded tiles, for the view from altitude: zoom 13 vector tiles (four times the area of the
/// detailed zoom 14 tiles; small buildings left out, no heights) out to Settings.FarRadiusKm, each outline as one
/// box with a height estimated from its footprint, no roads, roofs, details or windows. Streams on above the
/// altitude where the detailed tiles stop, and is drawn only where no detailed tile is ready.
/// </summary>
internal sealed class FarField : IDisposable
{
    private readonly Settings _settings;
    private readonly TileManager _source;
    private readonly object _lock = new();
    private readonly Dictionary<(int, int), FarTile> _tiles = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _workers = new();
    private double _lastScan = -10;
    private double _planetRadius = 6371000;
    private volatile float _camLat, _camLon;
    /// <summary>The body the workers place tiles on (set by the main thread before it asks for tiles).</summary>
    private volatile Celestial? _body;
    private volatile bool _tilesFinished;
    public volatile FarTile[] Snapshot = Array.Empty<FarTile>();
    public bool Active { get; private set; }

    public FarField(Settings settings, TileManager source)
    {
        _settings = settings;
        _source = source;
        // Hundreds of small tiles: several downloads at once. The workers also place them, below normal priority.
        for (int i = 0; i < 6; i++)
        {
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"GEE far field {i}", Priority = ThreadPriority.BelowNormal };
            _workers.Add(worker);
            worker.Start();
        }
    }

    public (int Ready, int Pending, int Buildings) Stats()
    {
        lock (_lock)
        {
            int ready = 0, pending = 0, buildings = 0;
            foreach (var t in _tiles.Values)
            {
                if (t.State == FarTile.Stage.Ready) { ready++; buildings += t.Count; }
                else if (t.State != FarTile.Stage.Failed) pending++;
            }
            return (ready, pending, buildings);
        }
    }

    /// <summary>Main thread, once per frame.</summary>
    public void Update(Celestial body, double3 cameraCcf, double now)
    {
        _planetRadius = body.MeanRadius;
        double r = Math.Sqrt(double3.Dot(cameraCcf, cameraCcf));
        double lat = Math.Asin(Math.Clamp(cameraCcf.Z / r, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(cameraCcf.Y, cameraCcf.X) * 180 / Math.PI;
        double altitudeKm = (r - _planetRadius) / 1000;
        _camLat = (float)lat;
        _camLon = (float)lon;
        _body = body;
        Active = _settings.Enabled && _settings.FarBuildings && altitudeKm > -1 && altitudeKm < _settings.FarMaxAltitudeKm;
        if (Active && now - _lastScan > 1)
        {
            _lastScan = now;
            RequestAround(lat, lon, _settings.FarRadiusKm);
            Evict(lat, lon, _settings.FarRadiusKm * 1.25 + 3);
        }
        if (_tilesFinished)
        {
            _tilesFinished = false;
            RebuildSnapshot();
        }
    }

    private static double DistanceKm(FarTile t, double lat, double lon)
    {
        var (s, w, n, e) = t.Bounds;
        double kmPerDeg = 6371 * Math.PI / 180;
        double dLat = Math.Clamp(lat, s, n) - lat, dLon = Math.Clamp(lon, w, e) - lon;
        return Math.Sqrt(Math.Pow(dLat * kmPerDeg, 2) + Math.Pow(dLon * kmPerDeg * Math.Cos(lat * Math.PI / 180), 2));
    }

    private void RequestAround(double lat, double lon, double radiusKm)
    {
        double kmPerDeg = 6371 * Math.PI / 180;
        double dLat = radiusKm / kmPerDeg, dLon = radiusKm / (kmPerDeg * Math.Max(Math.Cos(lat * Math.PI / 180), 0.05));
        var (x0, y0) = VectorTile.TileAt(FarTile.Zoom, Math.Min(lat + dLat, 85), lon - dLon);
        var (x1, y1) = VectorTile.TileAt(FarTile.Zoom, Math.Max(lat - dLat, -85), lon + dLon);
        int added = 0;
        lock (_lock)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (_tiles.TryGetValue((x, y), out var existing))
                    {
                        if (existing.State == FarTile.Stage.Failed && DateTime.UtcNow >= existing.RetryAt)
                        {
                            existing.State = FarTile.Stage.Queued;
                            added++;
                        }
                        continue;
                    }
                    var tile = new FarTile(x, y);
                    if (DistanceKm(tile, lat, lon) > radiusKm)
                        continue;
                    _tiles.Add((x, y), tile);
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
                if (tile.State is not (FarTile.Stage.Loading or FarTile.Stage.Placing) && DistanceKm(tile, lat, lon) > keepKm)
                {
                    _tiles.Remove(key);
                    changed |= tile.State == FarTile.Stage.Ready;
                }
            }
        }
        if (changed)
            RebuildSnapshot();
    }

    private void RebuildSnapshot()
    {
        lock (_lock)
            Snapshot = _tiles.Values.Where(t => t.State == FarTile.Stage.Ready).ToArray();
    }

    // ---- worker threads: terrain and instances ----

    /// <summary>Turns a simplified tile into instances, on the worker that loaded it (terrain queries are thread-safe).</summary>
    private void Place(FarTile tile, List<(double Lat, double Lon, Box2 Box, double Height, int Variant, uint Seed)> raw, Celestial body)
    {
        const int g = FarTile.GridSize;
        var (s, w, n, e) = tile.Bounds;
        var heights = new float[g * g];
        for (int i = 0; i < g * g; i++)
        {
            double hLat = n - (n - s) * (i / g) / (g - 1);
            double hLon = w + (e - w) * (i % g) / (g - 1);
            heights[i] = (float)body.GetTerrainHeightFromDirCcf(Placement.DirCcf(hLat, hLon));
        }
        tile.Heights = heights;
        tile.Instances = new BuildingInstance[raw.Count];
        tile.NearKeys = new int[raw.Count];
        tile.CenterCcf = Placement.DirCcf((s + n) / 2, (w + e) / 2) * _planetRadius;
        double kmPerDeg = _planetRadius * Math.PI / 180;
        tile.RadiusM = kmPerDeg * Math.Sqrt(Math.Pow(n - s, 2) + Math.Pow((e - w) * Math.Cos(s * Math.PI / 180), 2)) / 2 + 500;
        foreach (var (lat, lon, box, height, variant, seed) in raw)
        {
            double3 refDir = Placement.DirCcf(lat, lon);
            Placement.Axes(refDir, out double3 east, out double3 north);
            double3 up = double3.Normalize(refDir * _planetRadius + east * box.CenterEast + north * box.CenterNorth);
            double3 a = east * Math.Cos(box.Angle) + north * Math.Sin(box.Angle);
            a = double3.Normalize(a - up * double3.Dot(a, up));
            double3 b = double3.Cross(up, a);
            double ground = Math.Max(tile.HeightAt(lat, lon), 0);
            const double foot = 3;
            tile.Instances[tile.Count] = new BuildingInstance
            {
                Up = up * (height + foot),
                AxisA = a * box.LengthA,
                AxisB = b * box.LengthB,
                Base = up * (_planetRadius + ground - foot),
                Center = up * (_planetRadius + ground),
                Variant = variant,
                Style = Buildings.StyleNone,
                Seed = seed,
            };
            var key = TileKey.Of(lat, lon);
            tile.NearKeys[tile.Count] = key.X * 16384 + key.Y;
            tile.Count++;
        }
    }

    // ---- worker threads: download and simplify ----

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
            FarTile? tile;
            lock (_lock)
            {
                float lat = _camLat, lon = _camLon;
                tile = _tiles.Values.Where(t => t.State == FarTile.Stage.Queued).OrderBy(t => DistanceKm(t, lat, lon)).FirstOrDefault();
                if (tile != null)
                    tile.State = FarTile.Stage.Loading;
            }
            if (tile == null)
                continue;
            try
            {
                byte[] data = _source.Fetch(FarTile.Zoom, tile.X, tile.Y, out _);
                tile.State = FarTile.Stage.Placing;
                Place(tile, Simplify(data, tile.X, tile.Y), _body ?? throw new InvalidOperationException("no body to place on"));
                lock (_lock)
                    tile.State = FarTile.Stage.Ready;
                _tilesFinished = true;
            }
            catch (Exception ex)
            {
                Log.Error($"far tile {tile.X}/{tile.Y}: {ex.Message}");
                tile.RetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                tile.State = FarTile.Stage.Failed;
            }
        }
    }

    /// <summary>Every building outline of a zoom 13 tile as one oriented box with an estimated height.</summary>
    private static List<(double, double, Box2, double, int, uint)> Simplify(byte[] data, int tx, int ty)
    {
        var result = new List<(double, double, Box2, double, int, uint)>();
        const double metersPerDegree = Math.PI / 180 * 6371000;
        VectorTile.ForEachFeature(data, layer => layer == "building", (layer, type, tags, geometry, extent) =>
        {
            if (type != 3)
                return;
            string? colour = tags.GetValueOrDefault("colour") as string;
            foreach (var ring in VectorTile.ExteriorRings(geometry))
            {
                if (ring.Count < 3)
                    continue;
                var ll = ring.Select(p => VectorTile.ToLatLon(FarTile.Zoom, tx + p.X / extent, ty + p.Y / extent)).ToList();
                double lat0 = ll.Average(p => p.Lat), lon0 = ll.Average(p => p.Lon);
                double kLon = metersPerDegree * Math.Cos(lat0 * Math.PI / 180);
                var outline = ll.Select(p => new Footprint.P((p.Lon - lon0) * kLon, (p.Lat - lat0) * metersPerDegree)).ToList();
                double area = Math.Abs(Footprint.SignedArea(outline));
                if (area < 30)
                    continue;
                var boxes = Footprint.Decompose(outline, 0, 0);
                if (boxes.Count == 0)
                    continue;
                uint seed = LightSet.Hash((long)(lat0 * 1e6), (long)(lon0 * 1e6));
                double u = (LightSet.Hash(seed) >> 8) / 16777216.0;
                // The zoom 13 layer merges neighbouring buildings and drops heights: estimate from the footprint,
                // capped for merged blocks.
                double height = Math.Min(Buildings.EstimateHeight(Math.Min(area, 6000), u), 24);
                int variant = Buildings.Variant(seed, colour, height, area);
                result.Add((lat0, lon0, boxes[0], height, variant, seed));
            }
        });
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var worker in _workers)
            worker.Join(TimeSpan.FromSeconds(2));
    }
}
