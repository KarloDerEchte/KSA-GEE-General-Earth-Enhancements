using System.Collections.Concurrent;
using Brutal.Numerics;
using Brutal.StbApi;
using KSA;

namespace GEE;

/// <summary>
/// Real mountains: the game's heightmap is coarse (a lake bed instead of a flat lake, hills in the wrong places). Around the
/// camera the terrain is made from real elevation data instead (AWS Terrain Tiles, zoom 12, about 38 m): the tiles are
/// downloaded and kept on disk, an 80 km window of them (ElevationWindow) is put into a texture, and the patched terrain
/// mesh shaders use it in place of the heightmap, with the game's own surface detail added on top. Roads, buildings and
/// lights get the same height (Height). The window fades into the game's heightmap at its rim and where data is missing.
/// </summary>
internal static class Elevation
{
    private const string Url = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/12/{0}/{1}.png";
    private const string UserAgent = "KSA-GEE/0.2 (Kitten Space Agency mod; elevation)";
    private const double RebuildDistanceM = 12000, MaxAltitudeKm = 20;
    private const int MaxTiles = 420, WorkerCount = 4;

    public static Settings? Settings;
    public static string ModDir = "";
    public static string Status = "";

    /// <summary>The window the GPU has (set when it is uploaded).</summary>
    public static volatile ElevationWindow? Active;

    private static volatile ElevationWindow? _ready;
    private static readonly ConcurrentDictionary<(int, int), float[]> Tiles = new();
    private static readonly HashSet<(int, int)> Pending = new();
    private static readonly Dictionary<(int, int), DateTime> RetryAt = new();
    private static readonly ConcurrentQueue<(int X, int Y)> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static readonly DownloadGate Gate = new("elevation server");
    private static readonly object Lock = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static Func<string, string, byte[]>? _get;
    private static Thread[]? _workers;
    private static bool _wanted, _have, _failed;
    private static double _winLat, _winLon, _lastBuildAt = -100, _planetRadius = 6371000;
    private static int _loadedAtBuild = -1, _building;
    private static double _checkedAt = -100;

    /// <summary>Unfinished work: tiles being downloaded plus a window build in progress (cheap; for AutoShot waitidle).</summary>
    public static int Busy { get { lock (Lock) return Pending.Count + Volatile.Read(ref _building); } }

    /// <summary>Main thread, once per frame: asks for the tiles of the window around the camera and has the window built.</summary>
    public static void Update(Celestial body, double3 cameraCcf)
    {
        double r = Math.Sqrt(double3.Dot(cameraCcf, cameraCcf));
        // Not on the start screen, where the camera sits in the planet's middle.
        _wanted = !_failed && Settings is { Enabled: true } s && (s.RealTerrain || s.RealShores) && (r - body.MeanRadius) / 1000 < MaxAltitudeKm && r - body.MeanRadius > -1000;
        if (!_wanted)
            return;
        _planetRadius = body.MeanRadius;
        double lat = Math.Asin(Math.Clamp(cameraCcf.Z / r, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(cameraCcf.Y, cameraCcf.X) * 180 / Math.PI;
        double now = Clock.Elapsed.TotalSeconds;
        try
        {
            EnsureStarted();
            double dLat = (lat - _winLat) * Math.PI / 180 * _planetRadius, dLon = (lon - _winLon) * Math.PI / 180 * _planetRadius * Math.Cos(lat * Math.PI / 180);
            bool moved = !_have || Math.Sqrt(dLat * dLat + dLon * dLon) > RebuildDistanceM;
            // Settled: the tile list, the count and the download queue only change as tiles arrive or the camera
            // crosses into another window, so they are checked four times a second instead of every frame
            // (they allocate lists and sort ~100 tiles).
            if (!moved && now - _checkedAt < 0.25)
                return;
            _checkedAt = now;
            // The tiles of the window that is current (or is about to be): nearest first.
            var needed = ElevationWindow.TilesNeeded(moved ? lat : _winLat, moved ? lon : _winLon, _planetRadius);
            // Near a pole the map's tiles are tiny: the window would need thousands of them. No real elevation there.
            if (needed.Count > 400)
            {
                Status = "too close to a pole";
                return;
            }
            int loaded = needed.Count(t => Tiles.ContainsKey(t));
            Ask(needed, lat, lon);
            Status = $"{loaded}/{needed.Count} tiles, window {(Active != null ? "on the GPU" : "not yet")}";
            bool more = loaded > _loadedAtBuild && now - _lastBuildAt > 3;
            if ((moved || more) && loaded > 0 && Interlocked.CompareExchange(ref _building, 1, 0) == 0)
            {
                if (moved)
                {
                    _winLat = lat;
                    _winLon = lon;
                }
                _have = true;
                _lastBuildAt = now;
                _loadedAtBuild = loaded;
                double wl = _winLat, wo = _winLon;
                Task.Run(() =>
                {
                    try
                    {
                        _ready = ElevationWindow.Build(wl, wo, _planetRadius, (x, y) => Tiles.TryGetValue((x, y), out var t) ? t : null);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"elevation: window failed: {e}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _building, 0);
                    }
                });
            }
            if (Tiles.Count > MaxTiles)
            {
                var keep = needed.ToHashSet();
                // The ones farthest from the window go first: the next window overlaps this one, its tiles stay.
                var (centreX, centreY) = ElevationWindow.Pixel(lat, lon);
                foreach (var key in Tiles.Keys.Where(k => !keep.Contains(k))
                    .OrderByDescending(k => Math.Pow(k.Item1 + 0.5 - centreX / 256, 2) + Math.Pow(k.Item2 + 0.5 - centreY / 256, 2))
                    .Take(Tiles.Count - MaxTiles + 40).ToList())
                    Tiles.TryRemove(key, out _);
            }
        }
        catch (Exception e)
        {
            _failed = true;
            Status = $"off: {e.Message}";
            Log.Error($"elevation off: {e}");
        }
    }

    /// <summary>The finished window, once (the uploader makes it Active).</summary>
    public static ElevationWindow? TakeReady()
    {
        var window = _ready;
        _ready = null;
        return window;
    }

    private static void EnsureStarted()
    {
        if (_workers != null)
            return;
        _get ??= NetLoader.CreateGet(ModDir);
        _workers = new Thread[WorkerCount];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"GEE elevation {i}", Priority = ThreadPriority.BelowNormal };
            _workers[i].Start();
        }
    }

    private static void Ask(List<(int X, int Y)> needed, double lat, double lon)
    {
        var (cx, cy) = ElevationWindow.Pixel(lat, lon);
        int added = 0;
        lock (Lock)
        {
            foreach (var key in needed.OrderBy(t => Math.Pow(t.X + 0.5 - cx / 256, 2) + Math.Pow(t.Y + 0.5 - cy / 256, 2)))
            {
                if (Tiles.ContainsKey(key) || Pending.Contains(key))
                    continue;
                if (RetryAt.TryGetValue(key, out var retry) && DateTime.UtcNow < retry)
                    continue;
                RetryAt.Remove(key);
                Pending.Add(key);
                Queue.Enqueue(key);
                added++;
            }
        }
        if (added > 0)
            Signal.Release(added);
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            if (!Queue.TryDequeue(out var key))
            {
                Signal.Wait(2000);
                continue;
            }
            string path = Path.Combine(ModDir, "cache", "dem12", $"{key.X}_{key.Y}.png");
            try
            {
                byte[] png;
                if (File.Exists(path))
                {
                    png = File.ReadAllBytes(path);
                }
                else
                {
                    Gate.Check();
                    try
                    {
                        png = _get!(string.Format(Url, key.X, key.Y), UserAgent);
                    }
                    catch (Exception e) when (DownloadGate.IsRefusal(e))
                    {
                        throw Gate.Refused(e);
                    }
                    Gate.Succeeded();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, png);
                }
                using var image = Stb.LoadFromMemory(png, 4);
                if (image.Width != ElevationWindow.TileSize || image.Height != ElevationWindow.TileSize)
                    throw new InvalidOperationException($"unexpected tile size {image.Width}x{image.Height}");
                Tiles[key] = ElevationWindow.DecodeTerrarium(image.AsSpan());
                lock (Lock)
                    Pending.Remove(key);
            }
            catch (Exception e)
            {
                if (e is not DownloadPausedException)
                    Log.Error($"elevation tile {key.X}/{key.Y}: {e.Message}");
                lock (Lock)
                {
                    Pending.Remove(key);
                    RetryAt[key] = e is DownloadPausedException paused ? paused.Until : DateTime.UtcNow + TimeSpan.FromSeconds(60);
                }
            }
        }
    }

    // ---- what the rest of the mod asks ----

    /// <summary>The weight (0..1) the real elevation has at a point: 0 where there is none (outside the window, no data, off).</summary>
    public static double WeightAt(double latDeg, double lonDeg)
        => Active is { } window && window.TrySample(latDeg, lonDeg, out _, out double weight) ? weight : 0;

    /// <summary>
    /// The weight the terrain really uses the real elevation with (the shader's geeDemWeight): all of the window's with RealTerrain,
    /// with RealShores only near lakes.
    /// </summary>
    public static double EffectiveWeight(double latDeg, double lonDeg)
    {
        double weight = WeightAt(latDeg, lonDeg);
        return weight > 0 && Settings is { RealTerrain: false } ? weight * LakeOcean.NearLake(latDeg, lonDeg) : weight;
    }

    /// <summary>The real elevation of a point (m above sea level), where there is data.</summary>
    public static bool TryHeight(double latDeg, double lonDeg, out double height)
    {
        height = 0;
        return Active is { } window && window.TrySample(latDeg, lonDeg, out height, out _);
    }

    /// <summary>
    /// The terrain height at a direction as the patched terrain mesh makes it: the game's height with its base map replaced by the
    /// real elevation (weighted), then the lake patch (bed and bank). Roads, buildings and lights stand on this.
    /// </summary>
    public static double Height(Celestial body, double3 dirCcf)
    {
        double lat = Math.Asin(Math.Clamp(dirCcf.Z, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(dirCcf.Y, dirCcf.X) * 180 / Math.PI;
        double height = body.GetTerrainHeightFromDirCcf(dirCcf);
        // The mesh shader replaces the game's base map by the real elevation and adds the game's detail on top: the same as
        // adding the difference of the two base maps to the full height.
        if (Active is { } window && window.TrySample(lat, lon, out double real, out double windowWeight) && windowWeight > 0)
        {
            double weight = Settings is { RealTerrain: false } ? windowWeight * LakeOcean.NearLake(lat, lon) : windowWeight;
            if (weight <= 0)
                return LakeOcean.AdjustHeight(lat, lon, height);
            double game = body.GetTerrainHeightFromDirCcf(dirCcf, false);
            // Low land stays dry (see the shader): not below the game's own height (at most 3 m) where the game says land.
            if (game >= 0.2)       // the shader's rule (PatchPrepare: baseHeight >= 0.2); 1 m here left a metre-deep mismatch for game heights of 0.2..1 m
            {
                real = Math.Max(real, Math.Min(game, 3));
            }
            else
            {
                // The game's sea stays (see the shader): the data is a flat 0 m over open water.
                double t = Math.Clamp((Math.Abs(real) - 0.5) / 1.0, 0, 1);
                weight *= t * t * (3 - 2 * t);
            }
            height += weight * (real - game);
        }
        return LakeOcean.AdjustHeight(lat, lon, height);
    }

    /// <summary>
    /// For a tile that is about to be placed: waits (a worker thread; the first time the tiles are downloaded) until the window has
    /// the elevation of this place, so roads and buildings are made on it and not on the game's heightmap.
    /// </summary>
    public static void WaitForWindow(double latDeg, double lonDeg)
    {
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + 45 * System.Diagnostics.Stopwatch.Frequency;
        var (px, py) = ElevationWindow.Pixel(latDeg, lonDeg);
        var key = ((((int)(px / ElevationWindow.TileSize) % ElevationWindow.TilesPerSide) + ElevationWindow.TilesPerSide) % ElevationWindow.TilesPerSide, (int)(py / ElevationWindow.TileSize));
        while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline && _wanted && !_failed)
        {
            if (Active is { } window && window.Used.Contains(key))
                return;
            lock (Lock)
            {
                // Asked for and given up on (failed, retry later): the game's heightmap it is.
                if (!Tiles.ContainsKey(key) && !Pending.Contains(key) && RetryAt.ContainsKey(key))
                    return;
            }
            Thread.Sleep(100);
        }
    }
}
