using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>A zoom 14 web mercator tile (about 2.4 km at the equator, 1.6 km at 48°).</summary>
public readonly record struct TileKey(int X, int Y)
{
    public const int Zoom = 14;

    public static TileKey Of(double lat, double lon)
    {
        var (x, y) = VectorTile.TileAt(Zoom, lat, lon);
        return new TileKey(x, y);
    }

    public (double South, double West, double North, double East) Bounds => VectorTile.Bounds(Zoom, X, Y);
}

public enum TileState { Queued, Loading, Parsed, Placing, Ready, Failed }

public sealed class Tile(TileKey key)
{
    public readonly TileKey Key = key;
    public TileState State = TileState.Queued;
    public TileData? Raw;
    /// <summary>Building density of the tile, for street lights (made with its first road).</summary>
    public BuiltUp? BuiltUp;
    /// <summary>Where the road placement would put lamps (every 38-50 m), with the road's rank; StreetLights picks.</summary>
    public List<(RoadLight Light, int Rank)> LampCandidates = new();
    /// <summary>Runway and taxiway lights: always on.</summary>
    public List<RoadLight> AirfieldLamps = new();
    /// <summary>Street lights are settled (data or its absence known); reflector posts are added then.</summary>
    public bool LampsFinal;
    public bool LampsBuilt;
    /// <summary>Progress through Raw: buildings first, then roads.</summary>
    public int PlaceIndex;
    /// <summary>Written on the main thread only; the renderer reads the first Count entries.</summary>
    public BuildingInstance[] Instances = Array.Empty<BuildingInstance>();
    public int Count;
    public int BuildingCount;
    public int RoadCount;

    public void Add(BuildingInstance instance)
    {
        if (Count == Instances.Length)
        {
            var grown = new BuildingInstance[Math.Max(256, Instances.Length * 2)];
            Array.Copy(Instances, grown, Count);
            Instances = grown;
        }
        Instances[Count] = instance;
        Count++;
    }
    /// <summary>Squared size of each instance's box, for the first SizesCount (BuildingRenderer's pixel test).</summary>
    public float[] Sizes = Array.Empty<float>();
    public int SizesCount;
    /// <summary>Indices of the venting tanks among them (vapour).</summary>
    public List<int> Vents = new();
    /// <summary>The instances the renderer picked this frame (indices) and their window brightness, -1 for none.</summary>
    public int[] SelIndex = Array.Empty<int>();
    public float[] SelWalls = Array.Empty<float>();
    public int[] SelVariant = Array.Empty<int>();
    public int SelCount;
    public double3 CenterCcf;
    public double RadiusM;
    /// <summary>Night glow: one rooftop light per building, and street lamps (see GlowRenderer).</summary>
    public LightSet? BuildingLights;
    public LightSet? LampLights;
    public DateTime RetryAt;
    public string? Error;
    public bool FromCache;

    /// <summary>Ground clutter on this tile's footprints, computed once the tile is ready (see ClutterClearing).</summary>
    public bool ClutterStarted;
    public volatile (ClutterCellKey Key, uint[] Mask)[]? ClutterMasks;
    public int ClutterApplied;

    /// <summary>Terrain heights on a GridSize² lat/lon grid over the tile, row 0 at the north edge (~25 m spacing).</summary>
    public const int GridSize = 65;
    public float[]? Heights;
    public int HeightCount;

    /// <summary>Bilinear terrain height for a CCF direction; points just outside the tile use the edge.</summary>
    public double HeightAt(double3 dirCcf)
    {
        const int g = GridSize;
        var (s, w, n, e) = Key.Bounds;
        double lat = Math.Asin(Math.Clamp(dirCcf.Z, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(dirCcf.Y, dirCcf.X) * 180 / Math.PI;
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
/// Streams vector tiles around the camera: worker threads download (or read from the disk cache) and
/// parse them, the main thread samples terrain heights and turns them into render instances within a
/// per-frame time budget. Nearest tiles go first at every stage.
/// </summary>
public sealed class TileManager : IDisposable
{
    private const string UserAgent = "KSA-GEE/0.2 (Kitten Space Agency mod)";

    private readonly Settings _settings;
    private readonly string _modDir;
    private readonly object _lock = new();
    private readonly Dictionary<TileKey, Tile> _tiles = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _workers = new();
    private readonly object _netLock = new();

    private Func<string, string, byte[]>? _get;
    private string? _tileUrlTemplate;
    private double _planetRadius = 6371000;
    private volatile float _cameraLat, _cameraLon;
    private DateTime _pauseUntil;
    private double _lastScan;
    private readonly ClutterClearing _clutter;
    private Celestial? _padsBody;
    private double3[] _launchPads = Array.Empty<double3>();
    /// <summary>Footprint radius of each pad's static object (108 m for the stock pad).</summary>
    private double[] _padRadii = Array.Empty<double>();

    /// <summary>Tiles that have instances, for the renderer. Replaced as a whole.</summary>
    public volatile Tile[] Snapshot = Array.Empty<Tile>();

    public string? LastError { get; private set; }
    public double CameraLat { get; private set; }
    public double CameraLon { get; private set; }
    public double CameraAltitudeKm { get; private set; } = double.NaN;
    public bool Streaming { get; private set; }
    internal ClutterClearing Clutter => _clutter;
    public int LaunchPadCount => _launchPads.Length;

    private readonly PublicLamps _lamps;
    public string? StreetLightError => _lamps.LastError;
    public int StreetLightTilesFetched => _lamps.Fetched;

    public TileManager(Settings settings, string modDir)
    {
        _settings = settings;
        _modDir = modDir;
        _lamps = new PublicLamps(modDir, () => _get ??= NetLoader.CreateGet(modDir), settings.StreetLightOverpassUrls);   // street lights from OSM (opt-in)
        _clutter = new ClutterClearing(settings);
        ClutterClearing.Active = _clutter;
        for (int i = 0; i < Math.Clamp(settings.DownloadThreads, 1, 8); i++)
        {
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"GEE tiles {i}" };
            _workers.Add(worker);
            worker.Start();
        }
    }

    public (int Queued, int Loading, int Placing, int Ready, int Failed, int Buildings, int Roads) Stats()
    {
        int q = 0, l = 0, p = 0, r = 0, f = 0, b = 0, roads = 0;
        lock (_lock)
        {
            foreach (var t in _tiles.Values)
            {
                switch (t.State)
                {
                    case TileState.Queued: q++; break;
                    case TileState.Loading: l++; break;
                    case TileState.Parsed or TileState.Placing: p++; break;
                    case TileState.Ready: r++; break;
                    case TileState.Failed: f++; break;
                }
                if (t.State is TileState.Placing or TileState.Ready)
                {
                    b += t.BuildingCount;
                    roads += t.RoadCount;
                }
            }
        }
        return (q, l, p, r, f, b, roads);
    }

    /// <summary>Main thread, once per frame.</summary>
    public void Update(Celestial body, Camera camera, double now)
    {
        _planetRadius = body.MeanRadius;
        // The game's Double3Ex.Transform, as in Celestial.GetLatitude.
        double3 camCcf = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
        double r = Math.Sqrt(double3.Dot(camCcf, camCcf));
        CameraLat = Math.Asin(Math.Clamp(camCcf.Z / r, -1, 1)) * 180 / Math.PI;
        CameraLon = Math.Atan2(camCcf.Y, camCcf.X) * 180 / Math.PI;
        CameraAltitudeKm = (r - _planetRadius) / 1000;
        _cameraLat = (float)CameraLat;
        _cameraLon = (float)CameraLon;
        // The lower bound skips cameras orbiting the planet's centre, e.g. on the start screen.
        Streaming = _settings.Enabled && CameraAltitudeKm > -1 && CameraAltitudeKm < _settings.MaxCameraAltitudeKm;

        if (now - _lastScan > 0.5)
        {
            _lastScan = now;
            // Only near the ground: tiles stay loaded while the camera is in map view or orbit, so
            // switching views does not rebuild them. Also drops queued tiles the camera has left behind.
            if (Streaming && _settings.StreetLights == "Data")
                _lamps.RequestArea(CameraLat, CameraLon, _settings.StreetLightDataRadiusKm);
            foreach (var t in Snapshot)
                UpdateStreetLights(t);
            if (Streaming)
            {
                RequestAround(CameraLat, CameraLon, _settings.LoadRadiusKm);
                Evict(CameraLat, CameraLon, _settings.LoadRadiusKm * 1.5 + 1);
            }
        }
        Place(body, _settings.PlacementBudgetMs);
        _clutter.Update(body, Snapshot, camCcf, _settings.ClutterBudgetMs);
    }

    /// <summary>Launch pad directions of the body, from its landmarks (as the game's own clutter keep-out zones).</summary>
    private void UpdateLaunchPads(Celestial body)
    {
        if (ReferenceEquals(body, _padsBody))
            return;
        _padsBody = body;
        var pads = (body.BodyTemplate?.Locations ?? new List<LocationReference>())
            .OfType<LandmarkReference>()
            .Where(l => l.IsLaunchPad)
            .ToArray();
        _launchPads = pads.Select(l => l.ForwardCcf).ToArray();
        _padRadii = pads.Select(l => l.GetStaticObject()?.FootprintRadius is > 0 and var r ? r : 110).ToArray();
        Log.Info($"{_launchPads.Length} launch pads on {body.Id}");
    }

    /// <summary>
    /// True if a flat piece (road, apron) overlaps a pad's footprint: the game's pad model lies there, with its
    /// own apron and ring road, and OSM surfaces would cover it.
    /// </summary>
    private bool OnLaunchPad(in BuildingInstance b, double margin = 0)
    {
        double r = Math.Sqrt(double3.Dot(b.Center, b.Center));
        double la = Math.Max(Math.Sqrt(double3.Dot(b.AxisA, b.AxisA)), 1e-3), lb = Math.Max(Math.Sqrt(double3.Dot(b.AxisB, b.AxisB)), 1e-3);
        for (int i = 0; i < _launchPads.Length; i++)
        {
            double limit = _padRadii[i] + margin;
            double3 d = _launchPads[i] * r - b.Center;
            double reach = limit + la + lb;
            if (double3.Dot(d, d) > reach * reach)
                continue;
            // Distance from the pad centre to the piece's rectangle.
            double a = Math.Max(Math.Abs(double3.Dot(d, b.AxisA)) / la - la / 2, 0);
            double c = Math.Max(Math.Abs(double3.Dot(d, b.AxisB)) / lb - lb / 2, 0);
            if (a * a + c * c < limit * limit)
                return true;
        }
        return false;
    }

    /// <summary>Aprons reaching this close to a pad's footprint belong to the pad.</summary>
    private const double PadApronMargin = 250;

    private bool OnLaunchPad(double3 point)
    {
        double r = Math.Sqrt(double3.Dot(point, point));
        for (int i = 0; i < _launchPads.Length; i++)
        {
            double3 d = _launchPads[i] * r - point;
            if (double3.Dot(d, d) < _padRadii[i] * _padRadii[i])
                return true;
        }
        return false;
    }

    private const double PadCoreMeters = 75;

    private bool NearLaunchPad(double lat, double lon) => NearLaunchPad(lat, lon, _settings.LaunchPadClearRadiusKm * 1000);

    private bool NearLaunchPad(double lat, double lon, double radiusM)
    {
        if (radiusM <= 0 || _launchPads.Length == 0)
            return false;
        double cosLimit = Math.Cos(radiusM / _planetRadius);
        double3 dir = Placement.DirCcf(lat, lon);
        foreach (var pad in _launchPads)
        {
            if (double3.Dot(dir, pad) > cosLimit)
                return true;
        }
        return false;
    }

    /// <summary>Drops all tiles from memory; they reload from the disk cache.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var key in _tiles.Where(kv => kv.Value.State != TileState.Loading).Select(kv => kv.Key).ToList())
                _tiles.Remove(key);
        }
        Snapshot = Array.Empty<Tile>();
    }

    public void ClearDiskCache()
    {
        Clear();
        try
        {
            string dir = Path.Combine(_modDir, "cache");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e)
        {
            LastError = $"clearing cache failed: {e.Message}";
        }
    }

    private double KmPerDegree => _planetRadius * Math.PI / 180 / 1000;

    private double DistanceKm(TileKey key, double lat, double lon)
    {
        var (s, w, n, e) = key.Bounds;
        double dLat = Math.Clamp(lat, s, n) - lat;
        double dLon = Math.Clamp(lon, w, e) - lon;
        double cos = Math.Cos(lat * Math.PI / 180);
        return KmPerDegree * Math.Sqrt(dLat * dLat + dLon * dLon * cos * cos);
    }

    private void RequestAround(double lat, double lon, double radiusKm)
    {
        double dLat = radiusKm / KmPerDegree;
        double dLon = radiusKm / (KmPerDegree * Math.Max(Math.Cos(lat * Math.PI / 180), 0.01));
        var northWest = TileKey.Of(lat + dLat, lon - dLon);
        var southEast = TileKey.Of(lat - dLat, lon + dLon);
        int added = 0;
        lock (_lock)
        {
            for (int y = northWest.Y; y <= southEast.Y; y++)
            {
                for (int x = northWest.X; x <= southEast.X; x++)
                {
                    var key = new TileKey(x, y);
                    if (DistanceKm(key, lat, lon) > radiusKm)
                        continue;
                    if (!_tiles.TryGetValue(key, out var tile))
                    {
                        _tiles.Add(key, new Tile(key));
                        added++;
                    }
                    else if (tile.State == TileState.Failed && DateTime.UtcNow >= tile.RetryAt)
                    {
                        tile.State = TileState.Queued;
                        added++;
                    }
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
                if (tile.State != TileState.Loading && DistanceKm(key, lat, lon) > keepKm)
                {
                    _tiles.Remove(key);
                    changed |= tile.State is TileState.Placing or TileState.Ready;
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
            Snapshot = _tiles.Values.Where(t => t.State is TileState.Placing or TileState.Ready).ToArray();
        }
    }

    // ---- main thread: terrain sampling and instance creation ----

    private void Place(Celestial body, double budgetMs)
    {
        UpdateLaunchPads(body);
        long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Tile? tile;
            lock (_lock)
            {
                tile = _tiles.Values
                    .Where(t => t.State is TileState.Parsed or TileState.Placing)
                    .OrderBy(t => DistanceKm(t.Key, CameraLat, CameraLon))
                    .FirstOrDefault();
            }
            if (tile?.Raw is not { } raw)
                return;

            // Terrain queries are the expensive part, so each tile samples a height grid once and interpolates.
            if (!SampleHeights(body, tile, deadline))
                return;
            Func<double3, double> terrain = dir => tile.HeightAt(dir);
            // Roads lie flat on the ground, so they follow the exact terrain: its detail is far finer than the grid.
            Func<double3, double> exactTerrain = dir => body.GetTerrainHeightFromDirCcf(dir);

            if (tile.State == TileState.Parsed)
            {
                // Nearest first, so the area around the camera fills in before the tile edges.
                double lat = CameraLat, lon = CameraLon, cos = Math.Cos(lat * Math.PI / 180);
                double Dist2(double pLat, double pLon) => Math.Pow(pLat - lat, 2) + Math.Pow((pLon - lon) * cos, 2);
                raw.Buildings = raw.Buildings.OrderBy(b => Dist2(b.Lat, b.Lon)).ToList();
                raw.Roads = raw.Roads.OrderBy(r => Dist2(r.Points[0].Lat, r.Points[0].Lon)).ToList();
                tile.Instances = new BuildingInstance[raw.Buildings.Sum(b => b.Boxes.Count) + raw.Roads.Count * 4];
                var (s, w, n, e) = tile.Key.Bounds;
                tile.CenterCcf = Placement.DirCcf((s + n) / 2, (w + e) / 2) * _planetRadius;
                double halfDiagonal = KmPerDegree * 1000 * Math.Sqrt(Math.Pow(n - s, 2) + Math.Pow((e - w) * Math.Cos(s * Math.PI / 180), 2)) / 2;
                tile.RadiusM = halfDiagonal + 1000;
                tile.BuildingLights = new LightSet(LightKind.Building, tile.CenterCcf, 25);
                tile.LampLights = new LightSet(LightKind.Lamp, tile.CenterCcf, 38);
                tile.State = TileState.Placing;
                RebuildSnapshot();
            }

            int total = raw.Buildings.Count + raw.Roads.Count;
            while (tile.PlaceIndex < total && Stopwatch.GetTimestamp() < deadline)
            {
                int i = tile.PlaceIndex++;
                if (i < raw.Buildings.Count)
                {
                    var building = raw.Buildings[i];
                    if (building.Slab)
                    {
                        // Aprons: flat, so they stay near launch pads too. Next to a pad an apron is the pad's own
                        // hardstand, which the game's pad model brings, so the whole apron goes (also its parts
                        // clipped into neighbouring tiles, hence the margin).
                        if (_settings.Airports)
                        {
                            var slabs = building.Boxes
                                .Select(box => Placement.MakeSlab(building, box, _planetRadius, Roads.BaseLift + 0.02, terrain))
                                .ToList();
                            if (!slabs.Any(s => OnLaunchPad(s, PadApronMargin)))
                                slabs.ForEach(tile.Add);
                        }
                        continue;
                    }
                    if (NearLaunchPad(building.Lat, building.Lon))
                        continue;
                    // OSM draws the pad itself (and what stands on it) as buildings; the game's pad model is there
                    // instead. Landmarks (negative ids) stay: the service tower is one.
                    if (building.Id >= 0 && NearLaunchPad(building.Lat, building.Lon, PadCoreMeters))
                        continue;
                    BuildingInstance main = default;
                    double mainArea = 0;
                    int first = tile.Count;
                    foreach (var box in building.Boxes)
                    {
                        var instance = Placement.MakeInstance(building, box, _planetRadius, _settings.FoundationMeters, terrain);
                        if (building.Variant == Buildings.TankVariant || building.Variant == Buildings.HorizontalTankVariant)
                            instance.Vents = Landmarks.InAnyCompound(building.Lat, building.Lon);
                        tile.Add(instance);
                        BuildingDetail.Add(tile, instance, building);
                        if (building.Variant == Buildings.TankVariant)
                            tile.Add(Placement.MakeDome(instance));
                        if (box.LengthA * box.LengthB > mainArea)
                        {
                            mainArea = box.LengthA * box.LengthB;
                            main = instance;
                        }
                    }
                    if (Landmarks.IsTower(building.Id) && mainArea > 0)
                        AddTowerLights(tile.LampLights!, main);
                    if (Landmarks.IsFixedServiceStructure(building.Id) && mainArea > 0)
                        CrewArm.Register(main);
                    if (building.RoofHeight > 0 && mainArea > 0 && _settings.Roofs)
                        tile.Add(Placement.MakeRoof(main, building));
                    if (building.LaunchComplex)
                        for (int k = first; k < tile.Count; k++)
                            tile.Instances[k].Layer = Layers.LaunchComplex;
                    AddBuildingLight(tile.BuildingLights!, building, main, mainArea);
                }
                else
                {
                    var road = raw.Roads[i - raw.Buildings.Count];
                    if (road.Airfield != Airfield.None && !_settings.Airports)
                        continue;
                    var (ts, tw, tn, te) = tile.Key.Bounds;
                    tile.BuiltUp ??= new BuiltUp(raw.Buildings, (ts + tn) / 2);
                    Placement.AddRoadInstances(road, _planetRadius, exactTerrain,
                        piece =>
                        {
                            if (!OnLaunchPad(piece))
                                tile.Add(piece);
                        },
                        light =>
                        {
                            if (OnLaunchPad(light.Position))
                                return;
                            if (road.Airfield != Airfield.None)
                                tile.AirfieldLamps.Add(light);
                            else
                                tile.LampCandidates.Add((light, road.Rank));
                        });
                }
            }

            if (tile.PlaceIndex >= total)
            {
                tile.BuildingLights?.Finish();
                tile.Raw = null;
                tile.State = TileState.Ready;
                UpdateStreetLights(tile);
            }
        }
    }

    /// <summary>
    /// Settles a ready tile's street lights: asks for OpenStreetMap data when the mode wants it and the tile is near,
    /// and rebuilds the lamps once the data (or its failure) is in. Until then the building density decides.
    /// </summary>
    private void UpdateStreetLights(Tile tile)
    {
        if (tile.LampsFinal || tile.State != TileState.Ready)
            return;
        bool wantData = _settings.StreetLights == "Data";
        if (!wantData)
        {
            BuildStreetLights(tile, null, final: true);
            return;
        }
        if (_lamps.TryGet(tile.Key, out var data))
        {
            BuildStreetLights(tile, data, final: true);
            return;
        }
        double distance = DistanceKm(tile.Key, CameraLat, CameraLon);
        if (distance <= _settings.StreetLightDataRadiusKm)
            _lamps.Request(tile.Key, distance);
        // Meanwhile the building density decides; once the fetch has failed, that is final (posts are added).
        bool failed = _lamps.Failed(tile.Key);
        if (!tile.LampsBuilt || failed)
            BuildStreetLights(tile, null, final: failed);
    }

    /// <summary>
    /// The tile's lamps: mapped lamp posts where they are, lamps every 35 m along roads tagged lit without mapped
    /// posts, none along lit=no roads; the placement's candidates only where OpenStreetMap says nothing, by the
    /// building density (or Settings.StreetLights "All"/"None"). Reflector posts stand where a candidate stays dark.
    /// </summary>
    private void BuildStreetLights(Tile tile, LampData? data, bool final)
    {
        var set = new LightSet(LightKind.Lamp, tile.CenterCcf, 38);
        foreach (var a in tile.AirfieldLamps)
            set.Add(a.Position, a.Radius, a.Color, a.Seed);
        var (s, w, n, e) = tile.Key.Bounds;
        double cosLat = Math.Cos((s + n) / 2 * Math.PI / 180);
        const double cell = 12, mPerDeg = Math.PI / 180 * 6371000;
        (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat * mPerDeg / cell), (int)Math.Floor(lon * mPerDeg * cosLat / cell));
        bool Near(HashSet<(int, int)> cells, double lat, double lon, int reach)
        {
            var (r, c) = Cell(lat, lon);
            for (int dr = -reach; dr <= reach; dr++)
                for (int dc = -reach; dc <= reach; dc++)
                    if (cells.Contains((r + dr, c + dc)))
                        return true;
            return false;
        }
        double3 LampAt(double lat, double lon)
        {
            double3 dir = Placement.DirCcf(lat, lon);
            return dir * (_planetRadius + Math.Max(tile.HeightAt(dir), 0) + 8);
        }
        bool Inside(double lat, double lon) => lat >= s && lat < n && lon >= w && lon < e;

        var lampCells = new HashSet<(int, int)>();
        var litCells = new HashSet<(int, int)>();
        var darkCells = new HashSet<(int, int)>();
        if (data != null)
        {
            foreach (var (lat, lon) in data.Lamps)
            {
                lampCells.Add(Cell(lat, lon));
                if (!Inside(lat, lon))
                    continue;
                uint seed = LightSet.Hash((long)(lat * 1e7), (long)(lon * 1e7));
                set.Add(LampAt(lat, lon), 1.5f, LightSet.Pack(LightSet.StreetColor(seed), 5), seed);
            }
            foreach (var (lit, points) in data.Ways)
            {
                double carried = 0;
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    var (lat0, lon0) = points[i];
                    var (lat1, lon1) = points[i + 1];
                    double length = Math.Sqrt(Math.Pow((lat1 - lat0) * mPerDeg, 2) + Math.Pow((lon1 - lon0) * mPerDeg * cosLat, 2));
                    for (double d = 0; d < length; d += 6)
                    {
                        double t = d / Math.Max(length, 1e-6);
                        double lat = lat0 + (lat1 - lat0) * t, lon = lon0 + (lon1 - lon0) * t;
                        (lit ? litCells : darkCells).Add(Cell(lat, lon));
                        // Lit roads without mapped posts get a lamp every 35 m.
                        if (lit && (carried += 6) >= 35 && Inside(lat, lon) && !Near(lampCells, lat, lon, 2))
                        {
                            carried = 0;
                            uint seed = LightSet.Hash((long)(lat * 1e7), (long)(lon * 1e7), 7);
                            set.Add(LampAt(lat, lon), 1.5f, LightSet.Pack(LightSet.StreetColor(seed), 5), seed);
                        }
                    }
                }
            }
        }

        tile.BuiltUp ??= new BuiltUp(Array.Empty<RawBuilding>(), (s + n) / 2);
        foreach (var (light, rank) in tile.LampCandidates)
        {
            double r = Math.Sqrt(double3.Dot(light.Position, light.Position));
            double lat = Math.Asin(Math.Clamp(light.Position.Z / r, -1, 1)) * 180 / Math.PI;
            double lon = Math.Atan2(light.Position.Y, light.Position.X) * 180 / Math.PI;
            bool lit;
            if (data != null && (Near(lampCells, lat, lon, 2) || Near(litCells, lat, lon, 1)))
                continue;                                   // the data already lights this stretch
            else if (data != null && Near(darkCells, lat, lon, 1))
                lit = false;                                // lit=no
            else
                lit = _settings.StreetLights switch
                {
                    "All" => true,
                    "None" => false,
                    _ => tile.BuiltUp.Lit(lat, lon, rank),
                };
            if (lit)
                set.Add(light.Position, light.Radius, light.Color, light.Seed);
            else if (final && _settings.ReflectorPosts)
                tile.Add(ReflectorPost(light.Position));
        }
        set.Finish();
        tile.LampLights = set;
        tile.LampsFinal = final;
        tile.LampsBuilt = true;
    }

    /// <summary>
    /// A reflector post where a lamp would stand outside built-up areas: white, 1 m tall, 12 cm square, at the road
    /// edge (the lamp head is 8 m above it). Drawn only near the camera.
    /// </summary>
    private static BuildingInstance ReflectorPost(double3 lamp)
    {
        double3 up = double3.Normalize(lamp);
        double3 a = double3.Cross(up, new double3(0, 0, 1));
        a = double3.Dot(a, a) > 1e-12 ? double3.Normalize(a) : new double3(1, 0, 0);
        double3 b = double3.Cross(up, a);
        double3 foot = lamp - up * 8.25;
        return new BuildingInstance
        {
            Up = up * 1.25,
            AxisA = a * 0.12,
            AxisB = b * 0.16,
            Base = foot,
            Center = foot,
            Variant = 1,
            Style = Buildings.StyleNone,
            IsDetail = true,
            Layer = Layers.Street,
        };
    }

    /// <summary>Red obstruction lights up the tower and, on the tallest, warm lights on its platforms.</summary>
    private static void AddTowerLights(LightSet lights, in BuildingInstance tower)
    {
        double3 up = double3.Normalize(tower.Up);
        double height = Math.Sqrt(double3.Dot(tower.Up, tower.Up));
        uint seed = LightSet.Hash(tower.Seed, 91);
        uint red = LightSet.Pack((255, 30, 20), 6);
        int beacons = Math.Max((int)(height / 45), 1);
        for (int k = 1; k <= beacons; k++)
        {
            double sign = k % 2 == 0 ? -1 : 1;
            lights.Add(tower.Base + tower.Up * ((double)k / beacons) + up * 1.5 + tower.AxisA * (0.5 * sign), 3.5f, red, seed + (uint)k);
        }
        if (height < 90)
            return;
        uint warm = LightSet.Pack((255, 205, 150), 2.2);
        for (double h = 14; h < height - 12; h += 12)
        {
            double sign = ((int)(h / 12)) % 2 == 0 ? 1 : -1;
            lights.Add(tower.Base + up * h + tower.AxisA * (0.52 * sign) + tower.AxisB * (0.52 * -sign), 4f, warm, seed + (uint)h);
        }
    }

    /// <summary>
    /// Rooftop glow of a lit building, sized to its largest box: from afar the glows of a district add up to the
    /// light its windows give off. Taller buildings shine brighter; a few stay dark.
    /// </summary>
    private static void AddBuildingLight(LightSet lights, RawBuilding building, in BuildingInstance main, double area)
    {
        if (area < 30)
            return;
        uint h = LightSet.Hash(building.Id, (long)(building.Lat * 1e6), (long)(building.Lon * 1e6));
        if (h % 100 < 12)
            return;
        double3 up = double3.Normalize(main.Base);
        double3 roof = main.Base + main.Up + up * 1.0;
        float radius = (float)Math.Clamp(0.45 * Math.Sqrt(area), 3, 30);
        double intensity = 0.6 + Math.Min(Math.Max(building.Height, 4) / 25, 2);
        lights.Add(roof, radius, LightSet.Pack(LightSet.WindowColor(h >> 8), intensity), h);
    }

    /// <summary>Fills the tile's height grid within the deadline; true once it is complete.</summary>
    private static bool SampleHeights(Celestial body, Tile tile, long deadline)
    {
        const int g = Tile.GridSize;
        tile.Heights ??= new float[g * g];
        var (s, w, n, e) = tile.Key.Bounds;
        while (tile.HeightCount < g * g)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                return false;
            int i = tile.HeightCount;
            double lat = n - (n - s) * (i / g) / (g - 1);
            double lon = w + (e - w) * (i % g) / (g - 1);
            tile.Heights[i] = (float)body.GetTerrainHeightFromDirCcf(Placement.DirCcf(lat, lon));
            tile.HeightCount++;
        }
        return true;
    }

    // ---- worker threads: download, cache, parse ----

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

            Tile? tile;
            lock (_lock)
            {
                float lat = _cameraLat, lon = _cameraLon;
                tile = _tiles.Values
                    .Where(t => t.State == TileState.Queued)
                    .OrderBy(t => DistanceKm(t.Key, lat, lon))
                    .FirstOrDefault();
                if (tile != null)
                    tile.State = TileState.Loading;
            }
            if (tile == null)
                continue;

            try
            {
                var sw = Stopwatch.StartNew();
                byte[] data = LoadOrFetch(tile);
                double fetchMs = sw.Elapsed.TotalMilliseconds;
                var raw = VectorTile.Parse(data, TileKey.Zoom, tile.Key.X, tile.Key.Y, new OsmOptions { PlanetRadius = _planetRadius });
                lock (_lock)
                {
                    tile.Raw = raw;
                    tile.BuildingCount = raw.Buildings.Count;
                    tile.RoadCount = raw.Roads.Count;
                    tile.State = TileState.Parsed;
                }
                if (!tile.FromCache && raw.Buildings.Count + raw.Roads.Count > 0)
                    Log.Info($"tile {tile.Key.X}/{tile.Key.Y}: {raw.Buildings.Count} buildings, {raw.Roads.Count} roads, download {fetchMs:0} ms, parse {sw.Elapsed.TotalMilliseconds - fetchMs:0} ms");
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                bool throttled = message.Contains("HTTP 429") || message.Contains("HTTP 503");
                if (throttled)
                    _pauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                LastError = $"tile {tile.Key.X}/{tile.Key.Y}: {message}";
                Log.Error(LastError);
                lock (_lock)
                {
                    tile.Error = message;
                    tile.State = TileState.Failed;
                    tile.RetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(throttled ? 60 : 30);
                }
            }
        }
    }

    private byte[] LoadOrFetch(Tile tile)
    {
        byte[] data = Fetch(TileKey.Zoom, tile.Key.X, tile.Key.Y, out bool fromCache);
        tile.FromCache = fromCache;
        return data;
    }

    /// <summary>The tile server asked to slow down: downloads pause for a while.</summary>
    internal void Throttled() => _pauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(30);

    /// <summary>A vector tile of any zoom from the disk cache, or downloaded and cached. Worker threads.</summary>
    internal byte[] Fetch(int zoom, int x, int y, out bool fromCache)
    {
        string dir = Path.Combine(_modDir, "cache", $"omt{zoom}");
        string path = Path.Combine(dir, $"{x}_{y}.pbf.gz");
        if (File.Exists(path))
        {
            using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var memory = new MemoryStream();
            gz.CopyTo(memory);
            fromCache = true;
            return memory.ToArray();
        }
        fromCache = false;

        var wait = _pauseUntil - DateTime.UtcNow;
        if (wait > TimeSpan.Zero && _cts.Token.WaitHandle.WaitOne(wait))
            throw new OperationCanceledException();

        string url = TileUrlTemplate()
            .Replace("{z}", zoom.ToString())
            .Replace("{x}", x.ToString())
            .Replace("{y}", y.ToString());
        byte[] data = _get!(url, UserAgent);

        Directory.CreateDirectory(dir);
        string tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
        using (var gz = new GZipStream(File.Create(tmp), CompressionLevel.Fastest))
            gz.Write(data);
        File.Move(tmp, path, overwrite: true);
        return data;
    }

    /// <summary>The tile URL holds a dated planet version, so it is read from the TileJSON once per session.</summary>
    private string TileUrlTemplate()
    {
        lock (_netLock)
        {
            _get ??= NetLoader.CreateGet(_modDir);
            if (_tileUrlTemplate == null)
            {
                using var doc = JsonDocument.Parse(_get(_settings.TileJsonUrl, UserAgent));
                _tileUrlTemplate = doc.RootElement.GetProperty("tiles")[0].GetString()
                    ?? throw new InvalidOperationException($"no tile URL in {_settings.TileJsonUrl}");
                Log.Info($"tile source {_tileUrlTemplate}");
            }
            return _tileUrlTemplate;
        }
    }

    public void Dispose()
    {
        if (ClutterClearing.Active == _clutter)
            ClutterClearing.Active = null;
        _cts.Cancel();
        foreach (var worker in _workers)
            worker.Join(TimeSpan.FromSeconds(2));
        _lamps.Dispose();
    }
}
