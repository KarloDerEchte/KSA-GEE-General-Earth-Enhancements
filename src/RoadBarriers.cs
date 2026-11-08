using System.Runtime.CompilerServices;
using Brutal.Numerics;

namespace GEE;

/// <summary>
/// Road furniture that keeps traffic apart, made of thin instanced boxes (BuildingInstance.IsDetail, Layers.Street):
/// W-beam guard rails (beam and posts) along both edges of motorway/trunk carriageways, a concrete median barrier
/// where the two one-way carriageways of a dual carriageway run within MedianMaxGap metres of each other, and parapets
/// on the deck edges of bridges. Rails stop where another road meets or runs beside the carriageway (ramps, crossings,
/// junctions), so entries stay open. The pieces have no colliders (RoadCollision only builds hulls for road slabs), so
/// a vehicle drives through them like through the reflector posts; the road itself stays as it was. Uses no game types.
/// </summary>
public static class RoadBarriers
{
    /// <summary>Set from Settings.RoadBarriers before the roads of a tile are placed.</summary>
    public static volatile bool Enabled = true;

    /// <summary>Median barrier between carriageways whose edges are at most this far apart.</summary>
    private const double MedianMaxGap = 14;
    private const double NeighbourReach = 80;
    private const double SampleMeters = 5;
    private const double RailOffset = 0.6, BeamHeight = 0.32, BeamLift = 0.45, PostSize = 0.1, PostSpacing = 4, PostHeight = 1.4;
    private const double MedianHeight = 0.85, MedianWidth = 0.6;
    private const double ParapetHeight = 0.9, ParapetWidth = 0.35;
    private const int RailVariant = 6;

    private readonly record struct Sample(double3 Pos, double3 Dir, double Half, bool Highway, bool Owner);

    private sealed class Neighbours
    {
        public Sample[] Samples = Array.Empty<Sample>();
    }

    private static readonly ConditionalWeakTable<RawRoad, Neighbours> Table = new();

    /// <summary>
    /// Finds, for each motorway/trunk carriageway, the road lines within NeighbourReach metres of it. Run once per tile
    /// after the roads have their final points (after RoadJunctions/RoadLayers).
    /// </summary>
    public static void Prepare(List<RawRoad> roads, double radius)
    {
        var eligible = new List<int>();
        for (int i = 0; i < roads.Count; i++)
            if (IsCarriageway(roads[i]))
                eligible.Add(i);
        if (eligible.Count == 0)
            return;
        var lines = new List<(double3 Pos, double3 Dir)>?[roads.Count];
        List<(double3 Pos, double3 Dir)> Line(int i) => lines[i] ??= Densify(roads[i], radius);

        foreach (int i in eligible)
        {
            var own = Line(i);
            if (own.Count == 0)
                continue;
            var found = new List<Sample>();
            double reach2 = NeighbourReach * NeighbourReach;
            for (int j = 0; j < roads.Count; j++)
            {
                var other = roads[j];
                if (j == i || other.Airfield != Airfield.None || other.Points.Count < 2)
                    continue;
                foreach (var (pos, dir) in Line(j))
                {
                    bool near = false;
                    foreach (var (p, _) in own)
                    {
                        double3 d = pos - p;
                        if (double3.Dot(d, d) < reach2)
                        {
                            near = true;
                            break;
                        }
                    }
                    if (near)
                        found.Add(new Sample(pos, dir, other.Width / 2, IsCarriageway(other), i < j));
                }
            }
            Table.Remove(roads[i]);
            Table.Add(roads[i], new Neighbours { Samples = found.ToArray() });
        }
    }

    private static bool IsCarriageway(RawRoad road) =>
        road.Surface.IsHighway() && !road.Bridge && road.Airfield == Airfield.None && road.Points.Count >= 2;

    private static List<(double3, double3)> Densify(RawRoad road, double radius)
    {
        var result = new List<(double3, double3)>();
        for (int i = 0; i + 1 < road.Points.Count; i++)
        {
            double3 a = Placement.DirCcf(road.Points[i].Lat, road.Points[i].Lon) * radius;
            double3 b = Placement.DirCcf(road.Points[i + 1].Lat, road.Points[i + 1].Lon) * radius;
            double3 delta = b - a;
            double length = Math.Sqrt(double3.Dot(delta, delta));
            if (length < 1e-6)
                continue;
            double3 dir = delta / length;
            int steps = Math.Max((int)Math.Ceiling(length / SampleMeters), 1);
            for (int s = 0; s <= steps; s++)
                result.Add((a + delta * ((double)s / steps), dir));
        }
        return result;
    }

    /// <summary>
    /// Barriers for one road piece. up/along/side are the piece's axes (side = left of the direction of travel), center
    /// its middle shifted by the end overlaps, mid its unshifted middle (both on the road's base height, the surface is
    /// lift above), length the plain length and span the length with the overlaps, cross the slab's cross slope.
    /// Overpass parapets are always drawn (they were before this class); everything else follows Enabled.
    /// </summary>
    public static void AddPiece(RawRoad road, double3 up, double3 along, double3 side, double3 center, double3 mid, double length,
        double span, double lift, double cross, Action<BuildingInstance> add)
    {
        if (road.Bridge)
        {
            if (road.Overpass || Enabled)
                Parapets(road, up, along, side, center, mid, span, lift, add);
            return;
        }
        if (!Enabled || !Table.TryGetValue(road, out var neighbours))
            return;
        double half = road.Width / 2;
        double window = length / 2 + 7;
        bool blockedLeft = false, blockedRight = false;
        double gap = double.MaxValue, gapLat = 0;
        bool owner = false;
        foreach (var s in neighbours.Samples)
        {
            double3 d = s.Pos - mid;
            d -= up * double3.Dot(d, up);
            double la = double3.Dot(d, along), lat = double3.Dot(d, side);
            if (Math.Abs(la) > window || Math.Abs(lat) > NeighbourReach)
                continue;
            if (s.Highway)
            {
                double c = double3.Dot(s.Dir, along);
                if (c < -0.8)
                {
                    double g = Math.Abs(lat) - half - s.Half;
                    if (Math.Abs(la) < length / 2 + 3 && g >= 0.2 && g < gap)
                    {
                        gap = g;
                        gapLat = lat;
                        owner = s.Owner;
                    }
                    continue;
                }
                if (c > 0.8)
                    continue;
            }
            double reach = half + s.Half + 2.5;
            if (lat > -half && lat < reach)
                blockedLeft = true;
            if (-lat > -half && -lat < reach)
                blockedRight = true;
        }
        bool median = gap <= MedianMaxGap;
        double medianSign = gapLat >= 0 ? 1 : -1;

        if (median && owner)
        {
            double off = medianSign * (half + gap / 2);
            double width = Math.Min(MedianWidth, gap * 0.8);
            double surface = lift + Math.Clamp(cross * off / road.Width, -1.5, 1.5);
            add(Box(up, along, side, center + side * off + up * (surface - 1.0), span, width, 1.0 + MedianHeight, mid, ApronVariant));
        }
        for (int k = 0; k < 2; k++)
        {
            double sign = k == 0 ? 1 : -1;
            if ((sign > 0 ? blockedLeft : blockedRight) || (median && sign == medianSign))
                continue;
            double off = sign * (half + RailOffset);
            double surface = lift + Math.Clamp(cross * off / road.Width, -1.5, 1.5);
            add(Box(up, along, side, center + side * off + up * (surface + BeamLift), span, 0.1, BeamHeight, mid, RailVariant));
            int posts = Math.Max((int)(length / PostSpacing), 1);
            for (int p = 0; p < posts; p++)
            {
                double t = ((p + 0.5) / posts - 0.5) * length;
                add(Box(up, along, side, mid + along * t + side * off + up * (surface - 0.6), PostSize, PostSize, PostHeight, mid, RailVariant));
            }
        }
    }

    private static int ApronVariant => Buildings.ApronVariant;

    private static void Parapets(RawRoad road, double3 up, double3 along, double3 side, double3 center, double3 mid, double span,
        double lift, Action<BuildingInstance> add)
    {
        double half = road.Width / 2;
        foreach (double sign in new[] { -1.0, 1.0 })
            add(Box(up, along, side, center + up * (lift - 0.1) + side * (sign * (half - ParapetWidth / 2)), span, ParapetWidth,
                ParapetHeight + 0.1, mid, ApronVariant));
    }

    private static BuildingInstance Box(double3 up, double3 along, double3 side, double3 origin, double lengthA, double widthB,
        double height, double3 mid, int variant) => new()
    {
        Up = up * height,
        AxisA = along * lengthA,
        AxisB = side * widthB,
        Base = origin,
        Center = mid,
        Variant = variant,
        IsDetail = true,
        Layer = Layers.Street,
        Style = Buildings.StyleNone,
    };
}
