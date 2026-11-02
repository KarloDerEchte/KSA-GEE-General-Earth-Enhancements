using System.Collections.Concurrent;
using System.Diagnostics;
using Brutal.Numerics;
using KSA;

namespace OsmBuildings;

/// <summary>
/// Takes trees, shrubs and grass off roads and out of buildings. Worker threads compute which clutter
/// sub-cells fall on a ready tile's footprints (see Clutter); the main thread hands them to the game's
/// exclusion masks within a time budget. Instances with colliders go through the game's own path for
/// clutter knocked over by vessels (PhysicsBubble.PopulatePendingExclusions), which also removes colliders
/// already loaded around vessels; when no vessel bubble asks for exclusions they are applied directly.
/// </summary>
internal sealed class ClutterClearing
{
    public static ClutterClearing? Active;

    private readonly Settings _settings;
    private readonly ConcurrentQueue<BubbleClutterStatics.ClutterRemoval> _viaPhysics = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastPhysicsSync = double.NegativeInfinity;

    private Celestial? _body;
    private ClutterGrid[]? _grids;
    private bool[] _collideable = Array.Empty<bool>();
    private object? _target;
    private int _excludedCells;
    private bool _selfTestDone;

    public int Excluded { get; private set; }
    public int Pending { get; private set; }
    public string Status { get; private set; } = "waiting for ground clutter";

    public ClutterClearing(Settings settings)
    {
        _settings = settings;
    }

    /// <summary>Main thread, once per frame, after placement.</summary>
    public void Update(Celestial body, Tile[] tiles, double3 cameraCcf, double budgetMs)
    {
        if (!_settings.ClearClutter)
        {
            Status = "off";
            return;
        }
        if (Program.GetPlanetRenderer()?.GroundClutterRenderer is not { } renderer
            || !renderer.PlanetPlacementData.TryGetValue(body.Hash, out var placement))
        {
            Status = GameSettings.GenerateGroundClutter() ? "waiting for ground clutter" : "ground clutter is off in the game settings";
            return;
        }
        EnsureGrids(body, renderer);
        if (_grids == null)
            return;

        // The game rebuilds its clutter data when graphics settings change and clears the exclusions when
        // a game is loaded; either way everything is handed over again.
        int excludedCells = placement.Sum(p => p.ExcludedCells.Count);
        if (!ReferenceEquals(placement, _target) || excludedCells < _excludedCells)
        {
            _target = placement;
            Excluded = 0;
            foreach (var tile in tiles)
                tile.ClutterApplied = 0;
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000);
        bool physicsListening = _clock.Elapsed.TotalSeconds - _lastPhysicsSync < 1;
        int pending = 0;
        // Nearest first: grass only shows within ~80 m of the camera, and a city tile holds a lot of it.
        var ordered = tiles.OrderBy(t => double3.Dot(t.CenterCcf - cameraCcf, t.CenterCcf - cameraCcf));
        foreach (var tile in ordered)
        {
            if (tile.State != TileState.Ready)
                continue;
            if (!tile.ClutterStarted)
                StartCollect(tile, _grids, body.MeanRadius);
            if (tile.ClutterMasks is not { } masks)
            {
                pending++;
                continue;
            }
            while (tile.ClutterApplied < masks.Length && Stopwatch.GetTimestamp() < deadline)
            {
                var (key, mask) = masks[tile.ClutterApplied++];
                var cell = new CubeCellGrid.Cell(key.X, key.Y, key.Face);
                foreach (uint word in mask)
                    Excluded += System.Numerics.BitOperations.PopCount(word);
                // Instances with colliders go through the physics path, which also drops loaded colliders.
                if (physicsListening && _collideable[key.Ecotype])
                {
                    for (int bit = 0; bit < Clutter.Edge * Clutter.Edge; bit++)
                    {
                        if ((mask[bit >> 5] & (1u << (bit & 31))) == 0)
                            continue;
                        var instance = new BubbleClutterStatics.ClutterInstanceKey(body.Hash, key.Ecotype, cell, (uint)bit);
                        _viaPhysics.Enqueue(new BubbleClutterStatics.ClutterRemoval(instance, BubbleClutterStatics.ClutterExclusionType.Destroyed));
                    }
                }
                else
                {
                    // What GroundClutterRenderer.ExcludeInstance does, for a whole cell at once.
                    var data = placement[key.Ecotype].GetExclusionData(cell);
                    for (int i = 0; i < mask.Length; i++)
                        data[i] &= ~mask[i];
                    placement[key.Ecotype].ExcludeCell(cell, data);
                    renderer.QueueExclusionUpload(body.Hash, key.Ecotype, cell);
                }
            }
            if (tile.ClutterApplied < masks.Length)
                pending++;
        }
        _excludedCells = placement.Sum(p => p.ExcludedCells.Count);
        Pending = pending;
        Status = pending > 0 ? $"clearing, {pending} tiles to go" : "up to date";

        if (!_selfTestDone)
            SelfTest(body, renderer);
    }

    /// <summary>Harmony postfix on PhysicsBubble.PopulatePendingExclusions, main thread, once per bubble and frame.</summary>
    public void AddPhysicsExclusions(List<BubbleClutterStatics.ClutterRemoval> destination)
    {
        _lastPhysicsSync = _clock.Elapsed.TotalSeconds;
        for (int i = 0; i < 20000 && _viaPhysics.TryDequeue(out var key); i++)
            destination.Add(key);
    }

    /// <summary>Called when the body or the game's clutter setup changes.</summary>
    private void EnsureGrids(Celestial body, GroundClutterRenderer renderer)
    {
        if (ReferenceEquals(body, _body) && _grids != null)
            return;
        _body = body;
        _grids = null;
        if (body.BodyTemplate.GroundClutterReference?.Ecotypes is not { Count: > 0 } ecotypes)
        {
            Status = $"{body.Id} has no ground clutter";
            return;
        }
        var grids = new ClutterGrid[ecotypes.Count];
        _collideable = new bool[ecotypes.Count];
        renderer.PlanetPhysicalData.TryGetValue(body.Hash, out var physical);
        for (int i = 0; i < ecotypes.Count; i++)
        {
            double separation = ecotypes[i].Placement.ObjectSeparation.InMeters();
            int resolution = Clutter.GridResolution(body.MeanRadius, separation);
            if (physical != null && i < physical.Length && physical[i].VesselGrid.GridResolution is int game and > 0 && game != resolution)
            {
                Log.Error($"clutter grid {ecotypes[i].Name}: computed resolution {resolution}, game uses {game}");
                resolution = game;
            }
            // Trees get a few metres so their trunks stand clear of the kerb; grass only needs to stay off the asphalt.
            double clearance = Math.Clamp(0.2 * separation, 0.3, _settings.TreeClearanceMeters);
            grids[i] = new ClutterGrid(i, resolution, clearance);
            _collideable[i] = ecotypes[i].Collideable;
            Log.Info($"clutter grid {ecotypes[i].Name}: separation {separation:0.##} m, resolution {resolution}, clearance {clearance:0.#} m");
        }
        _grids = grids;
    }

    private static void StartCollect(Tile tile, ClutterGrid[] grids, double radius)
    {
        tile.ClutterStarted = true;
        BuildingInstance[] instances = tile.Instances;
        int count = Math.Min(tile.Count, instances.Length);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var masks = new Dictionary<ClutterCellKey, uint[]>();
                Clutter.Collect(instances.AsSpan(0, count), grids, radius, masks);
                tile.ClutterMasks = masks.Select(kv => (kv.Key, kv.Value)).ToArray();
                if (!tile.FromCache && masks.Count > 0)
                    Log.Info($"tile {tile.Key.X}/{tile.Key.Y}: clutter on {masks.Count} cells, {sw.Elapsed.TotalMilliseconds:0} ms");
            }
            catch (Exception e)
            {
                Log.Error($"clutter clearing for tile {tile.Key.X}/{tile.Key.Y} failed: {e}");
                tile.ClutterMasks = Array.Empty<(ClutterCellKey, uint[])>();
            }
        });
    }

    /// <summary>
    /// Compares the replicated placement with the collision copies the game keeps on the CPU for cells
    /// around vessels. Runs until it finds such a cell; the result goes to the log.
    /// </summary>
    private void SelfTest(Celestial body, GroundClutterRenderer renderer)
    {
        if (_grids == null || !renderer.PlanetPhysicalData.TryGetValue(body.Hash, out var physical))
            return;
        Camera camera = Program.GetMainCamera();
        double3 dir = double3.Normalize(Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf()));
        int face = Clutter.FaceOf(dir);
        var (u, v) = Clutter.ProjectToFace(face, dir);
        for (int e = 0; e < physical.Length && e < _grids.Length; e++)
        {
            int res = _grids[e].Resolution;
            int cx = (int)(u * res), cy = (int)(v * res);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    var cell = new CubeCellGrid.Cell(cx + dx, cy + dy, face);
                    if (!physical[e].TryGetCellData(cell, out var instances) || instances.Length == 0)
                        continue;
                    double3 anchor = physical[e].VesselGrid.GetCellAnchorDirection(cell);
                    double worst = 0;
                    foreach (ref readonly var instance in instances)
                    {
                        double3 actual = double3.Normalize(anchor + new double3(instance.CcfDirDelta.X, instance.CcfDirDelta.Y, instance.CcfDirDelta.Z));
                        double3 predicted = Clutter.InstanceDirection(face, cell.X, cell.Y, (int)instance.SubCellId % Clutter.Edge,
                            (int)instance.SubCellId / Clutter.Edge, res);
                        worst = Math.Max(worst, Math.Sqrt(double3.Dot(actual - predicted, actual - predicted)) * body.MeanRadius);
                    }
                    _selfTestDone = true;
                    string verdict = worst < 0.5 ? "matches" : "DOES NOT MATCH, clutter clearing will miss";
                    Log.Info($"clutter self-test, ecotype {e}, {cell}: {instances.Length} instances, worst position error {worst:0.###} m, {verdict}");
                    if (worst >= 0.5)
                        Log.Error("clutter placement replica is off; the game's Generate.comp has probably changed");
                    return;
                }
            }
        }
    }
}
