using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>
/// Collision for road slabs. The game's own terrain colliders (BubbleTerrainBlocks) follow the bare ground, so a road
/// lying 0.3-0.6 m above it was something vehicles drove through and then up against as a step. Near every vessel
/// each road piece gets a static convex hull in the bubble's physics simulation: the slab's top plate plus sloped
/// shoulders running down to the ground, so wheels roll on and off the road instead of hitting a kerb.
/// </summary>
internal sealed class RoadCollision
{
    private const double ReachM = 90;
    private const double RefreshSeconds = 0.4;
    /// <summary>Same as Placement.RoadDepth: how far a slab reaches below the ground.</summary>
    private const double SlabDepth = 1.5;
    private static readonly BufferPool HullPool = new(16384);
    private static readonly ConditionalWeakTable<PhysicsBubble, RoadCollision> ByBubble = new();

    private struct Piece
    {
        public StaticHandle Handle;
        public TypedIndex Shape;
        public double3 CenterCcf;
    }

    private readonly Dictionary<(Tile, int), Piece> _pieces = new();
    private readonly HashSet<(Tile, int)> _wanted = new();
    private readonly List<(Tile, int)> _gone = new();
    private readonly List<Vehicle> _vehicles = new();
    private ConstraintSim? _sim;
    private long _lastRefresh;
    private Tile[]? _lastTiles;

    /// <summary>Called after the game's SyncGroundClutterStatics (once per bubble and frame, main thread).</summary>
    public static void Sync(PhysicsBubble bubble)
    {
        if (BuildingRenderer.Settings is not { Enabled: true, RoadCollision: true } settings || BuildingRenderer.Manager is not { } manager)
        {
            if (ByBubble.TryGetValue(bubble, out var existing))
                existing.Clear();
            return;
        }
        ByBubble.GetOrCreateValue(bubble).Update(bubble, settings, manager);
    }

    /// <summary>Called after the bubble origin moved: the statics are placed relative to it.</summary>
    public static void Redescribe(PhysicsBubble bubble)
    {
        if (ByBubble.TryGetValue(bubble, out var existing))
            existing.Reposition(bubble);
    }

    /// <summary>The simulation goes back to the pool, which empties it: forget everything without removing.</summary>
    public static void SimReset(ConstraintSim sim)
    {
        foreach (var (_, roads) in ByBubbleSnapshot())
            if (roads._sim == sim)
                roads.Forget();
    }

    private static List<(PhysicsBubble, RoadCollision)> ByBubbleSnapshot()
    {
        var list = new List<(PhysicsBubble, RoadCollision)>();
        foreach (var pair in ByBubble)
            list.Add((pair.Key, pair.Value));
        return list;
    }

    private void Update(PhysicsBubble bubble, Settings settings, TileManager manager)
    {
        ConstraintSim? sim = bubble.ConstraintSim;
        if (sim == null || !bubble.Origin.BubFrame.IsCcf() || bubble.Parent is not Celestial celestial
            || !string.Equals(celestial.Id, settings.Body, StringComparison.OrdinalIgnoreCase))
        {
            Clear();
            return;
        }
        if (_sim != null && _sim != sim)
            Forget();
        _sim = sim;

        Tile[] tiles = manager.Snapshot;
        long now = Environment.TickCount64;
        if (ReferenceEquals(tiles, _lastTiles) && now - _lastRefresh < RefreshSeconds * 1000)
            return;
        _lastTiles = tiles;
        _lastRefresh = now;

        _wanted.Clear();
        double top = celestial.MeanRadius + celestial.MaxTerrainHeightApprox * 1.25;
        _vehicles.Clear();
        bubble.CollectVehicles(_vehicles);
        foreach (Vehicle vehicle in _vehicles)
        {
            double3 positionCce = vehicle.GetPositionCce();
            if (positionCce.LengthSquared() > top * top)
                continue;
            double3 position = Double3Ex.Transform(positionCce, celestial.GetCce2Ccf());
            Collect(tiles, position, ReachM + vehicle.BoundingSphereRadiusBody);
        }

        _gone.Clear();
        foreach (var key in _pieces.Keys)
            if (!_wanted.Contains(key))
                _gone.Add(key);
        foreach (var key in _gone)
        {
            Remove(sim, _pieces[key]);
            _pieces.Remove(key);
        }
        BubbleOrigin origin = bubble.Origin;
        foreach (var key in _wanted)
            if (!_pieces.ContainsKey(key))
                if (TryAdd(sim, key.Item1.Instances[key.Item2], in origin, out Piece piece))
                    _pieces[key] = piece;
                else
                    _pieces[key] = default;
    }

    private void Collect(Tile[] tiles, double3 position, double reach)
    {
        foreach (Tile tile in tiles)
        {
            double3 d = tile.CenterCcf - position;
            double tileReach = reach + tile.RadiusM;
            if (double3.Dot(d, d) > tileReach * tileReach)
                continue;
            BuildingInstance[] instances = tile.Instances;
            int count = Math.Min(tile.Count, instances.Length);
            for (int i = 0; i < count; i++)
            {
                ref BuildingInstance b = ref instances[i];
                if (!b.IsRoad || b.IsDetail || b.Layer != Layers.Street)
                    continue;
                double3 e = b.Center - position;
                double r = reach + Math.Sqrt(double3.Dot(b.AxisA, b.AxisA)) * 0.5;
                if (double3.Dot(e, e) <= r * r)
                    _wanted.Add((tile, i));
            }
        }
    }

    private static bool TryAdd(ConstraintSim sim, in BuildingInstance b, ref readonly BubbleOrigin origin, out Piece piece)
    {
        piece = default;
        double upLength = Math.Sqrt(double3.Dot(b.Up, b.Up));
        if (upLength < 1e-3)
            return false;
        double3 up = b.Up / upLength;
        // Height of the surface above the ground (the slab reaches SlabDepth below it); ramps to an overpass stand
        // on their embankment, where the drop is bigger.
        double lift = Math.Clamp(upLength - SlabDepth, 0.1, 3);
        double3 centre = b.Base + b.Up * 0.5;
        double3 a = b.AxisA * 0.5, side = b.AxisB * 0.5;
        // Horizontal direction across the road, for the shoulders.
        double3 across = side - up * double3.Dot(side, up);
        double acrossLength = Math.Sqrt(double3.Dot(across, across));
        if (acrossLength < 1e-3)
            return false;
        across /= acrossLength;
        double shoulder = Math.Max(1.2, 3 * lift);

        // The hull keeps the top plate (a sheared quad: the slab leans with the terrain across it), a thin body below
        // it, and for each long edge a foot at ground level, outside the road.
        Span<Vector3> points = stackalloc Vector3[12];
        int n = 0;
        foreach (double sa in stackalloc double[] { -1, 1 })
            foreach (double sb in stackalloc double[] { -1, 1 })
            {
                double3 corner = b.Base + b.Up + a * sa + side * sb - centre;
                points[n++] = ToVector(corner);
                points[n++] = ToVector(corner - up * Math.Min(upLength, lift + 0.4));
                points[n++] = ToVector(corner + across * (sb * shoulder) - up * lift);
            }
        // 12 points = 4 corners x (top, body, foot).
        if (!ConvexHullHelper.CreateShape(points[..n], HullPool, out Vector3 hullCentre, out ConvexHull hull))
            return false;
        // CreateShape re-centres the points on their centroid; the pose gets that offset.
        TypedIndex shape = sim.Simulation.Shapes.Add(hull);
        double3 world = centre + new double3(hullCentre.X, hullCentre.Y, hullCentre.Z);
        StaticHandle handle = sim.Simulation.Statics.Add(new StaticDescription
        {
            Pose = new RigidPose((world - origin.PositionBub).ToBepu(), Quaternion.Identity),
            Shape = shape,
        }, ref KSA.StaticsShouldntAwakenBodies.Shared);
        piece = new Piece { Handle = handle, Shape = shape, CenterCcf = world };
        return true;
    }

    private static Vector3 ToVector(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static void Remove(ConstraintSim sim, in Piece piece)
    {
        if (!piece.Shape.Exists)
            return;
        sim.Simulation.Statics.Remove(piece.Handle, ref KSA.StaticsShouldntAwakenBodies.Shared);
        sim.Simulation.Shapes.RemoveAndDispose(piece.Shape, HullPool);
    }

    private void Reposition(PhysicsBubble bubble)
    {
        ConstraintSim? sim = bubble.ConstraintSim;
        if (sim == null || sim != _sim)
            return;
        BubbleOrigin origin = bubble.Origin;
        foreach (Piece piece in _pieces.Values)
        {
            if (!piece.Shape.Exists)
                continue;
            sim.Simulation.Statics.ApplyDescription(piece.Handle, new StaticDescription
            {
                Pose = new RigidPose((piece.CenterCcf - origin.PositionBub).ToBepu(), Quaternion.Identity),
                Shape = piece.Shape,
            }, ref KSA.StaticsShouldntAwakenBodies.Shared);
        }
    }

    private void Clear()
    {
        if (_sim != null)
            foreach (Piece piece in _pieces.Values)
                Remove(_sim, in piece);
        Forget();
    }

    /// <summary>The simulation no longer holds our statics; free only the hull memory.</summary>
    private void Forget()
    {
        foreach (Piece piece in _pieces.Values)
            if (piece.Shape.Exists)
                FreeHull(piece.Shape);
        _pieces.Clear();
        _sim = null;
        _lastTiles = null;
    }

    private static void FreeHull(TypedIndex shape)
    {
        // The shape stays in the shared collection (its simulation is gone); nothing else refers to it.
        try
        {
            ConstraintSimShapes()?.RemoveAndDispose(shape, HullPool);
        }
        catch (Exception e)
        {
            Log.Error($"road collision: freeing a hull failed: {e.Message}");
        }
    }

    private static Shapes? ConstraintSimShapes() => ConstraintSim.ReadOnlyShapes;
}
