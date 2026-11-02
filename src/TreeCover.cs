using System.Collections.Concurrent;
using System.Diagnostics;
using Brutal.Numerics;

namespace GEE;

/// <summary>
/// Trees where the real world has trees. The game scatters its trees, shrubs and grass by biome and a tiling noise
/// texture; this keeps each of them only where ESA WorldCover says that kind of plant grows (trees in forest and
/// mangroves, a few lone ones in open land and towns, none on water, fields, rock or snow) and switches the others
/// off through the game's exclusion masks. A worker thread classifies every instance of the clutter cells around
/// the camera (the game's placement, replicated in Clutter), nearest cells first, and queues masks that
/// ClutterClearing hands to the game. Instances can only be removed, not added: forests are as dense as the game
/// makes them.
/// </summary>
internal sealed class TreeCover
{
    /// <summary>The kind of plant an ecotype stands for, from its name.</summary>
    public enum Kind { None, Tree, Shrub, Grass }

    /// <summary>Above this the game shows no clutter worth sorting (trees end at 5.5 km).</summary>
    private const double MaxAltitudeMeters = 12000;
    /// <summary>Worker time per pass before it looks at the camera again.</summary>
    private const double PassMs = 150;

    private sealed record Request(double3 Dir, double PlanetRadius, ClutterGrid[] Grids, Kind[] Kinds, double[] Ranges, int Generation);

    private readonly WorldCover _cover;
    private readonly ConcurrentQueue<(ClutterCellKey Key, uint[] Mask)> _masks = new();
    /// <summary>Cells already sorted (worker thread only).</summary>
    private readonly HashSet<ClutterCellKey> _done = new();
    private int _doneGeneration = -1;
    private volatile Request? _request;
    private Thread? _worker;
    private int _cells, _removed;
    private volatile string _status = "waiting";

    public TreeCover(WorldCover cover)
    {
        _cover = cover;
    }

    public int Cells => _cells;
    public int Removed => _removed;
    public string Status => _cover.LastError is { } error ? $"{_status}; {error}" : _status;

    public static Kind KindOf(string ecotypeName)
    {
        if (ecotypeName.Contains("Tree", StringComparison.OrdinalIgnoreCase))
            return Kind.Tree;
        if (ecotypeName.Contains("Shrub", StringComparison.OrdinalIgnoreCase) || ecotypeName.Contains("Bush", StringComparison.OrdinalIgnoreCase))
            return Kind.Shrub;
        if (ecotypeName.Contains("Grass", StringComparison.OrdinalIgnoreCase))
            return Kind.Grass;
        return Kind.None;
    }

    /// <summary>
    /// Share of a kind's instances kept on a land cover class. Lone trees stand in fields, meadows and towns; the
    /// game's grass stays on most open land (crops look like it at clutter scale), but not on water, rock or snow.
    /// WorldCover has no data only over the sea.
    /// </summary>
    private static double Keep(Kind kind, byte landCover) => kind switch
    {
        Kind.Tree => landCover switch
        {
            WorldCover.Trees or WorldCover.Mangroves => 1,
            WorldCover.Shrubland => 0.1,
            WorldCover.BuiltUp => 0.1,
            WorldCover.Wetland => 0.06,
            WorldCover.Grassland => 0.04,
            WorldCover.Cropland => 0.015,
            WorldCover.Moss => 0.02,
            _ => 0,
        },
        Kind.Shrub => landCover switch
        {
            WorldCover.Shrubland or WorldCover.Mangroves => 1,
            WorldCover.Trees => 0.6,
            WorldCover.Wetland => 0.5,
            WorldCover.Grassland or WorldCover.Moss => 0.3,
            WorldCover.BuiltUp => 0.1,
            WorldCover.Bare => 0.05,
            WorldCover.Cropland => 0.03,
            _ => 0,
        },
        Kind.Grass => landCover switch
        {
            WorldCover.Water or WorldCover.Snow or WorldCover.NoData => 0,
            WorldCover.Bare => 0.1,
            WorldCover.BuiltUp => 0.4,
            WorldCover.Mangroves => 0.5,
            WorldCover.Moss => 0.6,
            _ => 1,
        },
        _ => 1,
    };

    /// <summary>Main thread, once per frame: where the camera is and which grids there are.</summary>
    public void Update(double3 cameraCcf, double planetRadius, ClutterGrid[] grids, Kind[] kinds, double[] ranges, int generation)
    {
        double r = Math.Sqrt(double3.Dot(cameraCcf, cameraCcf));
        if (r - planetRadius > MaxAltitudeMeters || r - planetRadius < -1000)
        {
            _request = null;
            _status = "camera too high";
            return;
        }
        _request = new Request(cameraCcf / r, planetRadius, grids, kinds, ranges, generation);
        if (_worker == null)
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "GEE tree cover", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }
    }

    public bool TryDequeue(out ClutterCellKey key, out uint[] mask)
    {
        bool got = _masks.TryDequeue(out var item);
        (key, mask) = item;
        return got;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            try
            {
                if (_request is not { } request || !Pass(request))
                    Thread.Sleep(300);
            }
            catch (Exception e)
            {
                _status = $"failed: {e.Message}";
                Log.Error($"tree cover: {e}");
                Thread.Sleep(5000);
            }
        }
    }

    /// <summary>Sorts the nearest unsorted cells for up to PassMs. False when there was nothing left to do.</summary>
    private bool Pass(Request request)
    {
        if (request.Generation != _doneGeneration)
        {
            // The game's exclusions were reset (a game was loaded): everything again.
            _done.Clear();
            _doneGeneration = request.Generation;
        }
        if (_done.Count > 2_000_000)
            _done.Clear();

        var todo = new List<(ClutterCellKey Key, double Dist)>();
        for (int e = 0; e < request.Grids.Length; e++)
        {
            if (request.Kinds[e] != Kind.None)
                CellsAround(request, e, todo);
        }
        if (todo.Count == 0)
        {
            _status = $"up to date, {_cover.BlocksLoaded} land cover blocks";
            return false;
        }
        todo.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        long deadline = Stopwatch.GetTimestamp() + (long)(PassMs * Stopwatch.Frequency / 1000);
        _status = $"sorting, {todo.Count} cells to go";
        foreach (var (key, _) in todo)
        {
            if (Stopwatch.GetTimestamp() > deadline)
                break;
            if (!Sort(request, key))
            {
                // Land cover could not be fetched: try again later rather than keep the game's trees for good.
                _status = "land cover download failed, retrying";
                Thread.Sleep(2000);
                return true;
            }
            _done.Add(key);
            _cells++;
        }
        return true;
    }

    /// <summary>Unsorted cells of one ecotype grid within its generation range of the camera's ground point.</summary>
    private void CellsAround(Request request, int ecotype, List<(ClutterCellKey, double)> todo)
    {
        int res = request.Grids[ecotype].Resolution;
        double R = request.PlanetRadius, cell = Math.PI / 2 * R / res;
        double range = request.Ranges[ecotype] + cell;
        double step = cell / 2;
        int n = (int)Math.Ceiling(range / step);
        Placement.Axes(request.Dir, out double3 east, out double3 north);
        var seen = new HashSet<ClutterCellKey>();
        for (int j = -n; j <= n; j++)
        {
            for (int i = -n; i <= n; i++)
            {
                double dx = i * step, dy = j * step;
                if (dx * dx + dy * dy > range * range)
                    continue;
                double3 p = double3.Normalize(request.Dir + (east * dx + north * dy) / R);
                int face = Clutter.FaceOf(p);
                var (u, v) = Clutter.ProjectToFace(face, p);
                var key = new ClutterCellKey(ecotype, face, Math.Clamp((int)(u * res), 0, res - 1), Math.Clamp((int)(v * res), 0, res - 1));
                if (seen.Add(key) && !_done.Contains(key))
                    todo.Add((key, Math.Sqrt(dx * dx + dy * dy)));
            }
        }
    }

    /// <summary>Classifies the 256 instances of one cell and queues the ones to remove. False if land cover is missing.</summary>
    private bool Sort(Request request, ClutterCellKey key)
    {
        Kind kind = request.Kinds[key.Ecotype];
        int res = request.Grids[key.Ecotype].Resolution;
        uint[]? mask = null;
        int removed = 0;
        for (int localY = 0; localY < Clutter.Edge; localY++)
        {
            for (int localX = 0; localX < Clutter.Edge; localX++)
            {
                double3 d = Clutter.InstanceDirection(key.Face, key.X, key.Y, localX, localY, res);
                double lat = Math.Asin(Math.Clamp(d.Z, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
                byte? landCover = _cover.ClassAt(lat, lon);
                if (landCover == null)
                {
                    _status = "loading land cover";
                    if (!_cover.Load(lat, lon))
                        return false;
                    landCover = _cover.ClassAt(lat, lon);
                }
                int bit = localY * Clutter.Edge + localX;
                // Unknown (a block dropped from memory meanwhile): leave the game's instance.
                if (landCover is not { } known || Hash01(key, bit) < Keep(kind, known))
                    continue;
                mask ??= new uint[Clutter.Edge * Clutter.Edge / 32];
                mask[bit >> 5] |= 1u << (bit & 31);
                removed++;
            }
        }
        if (mask != null)
        {
            _masks.Enqueue((key, mask));
            Interlocked.Add(ref _removed, removed);
        }
        return true;
    }

    /// <summary>A fixed random number in [0, 1) per instance, so the same lone trees stay every time.</summary>
    private static double Hash01(ClutterCellKey key, int bit)
    {
        uint h = unchecked((uint)key.X * 0x8da6b343u ^ (uint)key.Y * 0xd8163841u ^ (uint)(key.Face * 64 + key.Ecotype) * 0xcb1ab31fu
                           ^ (uint)bit * 0x9e3779b9u);
        h ^= h >> 15;
        h = unchecked(h * 0x2c1b3c6du);
        h ^= h >> 12;
        h = unchecked(h * 0x297a2d39u);
        h ^= h >> 15;
        return h / 4294967296.0;
    }
}
