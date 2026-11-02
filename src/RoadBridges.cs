namespace GEE;

/// <summary>
/// Turns bridges that cross another road into overpasses. OpenStreetMap maps only the span itself as a bridge; drawn
/// as it is, it lies on the ground and the road below runs through it. A bridge whose line crosses a road of the tile
/// gets its whole deck Placement.BridgeClearance above the ground (Overpass), concrete pillars every PillarSpacing
/// metres where no road runs below (Pillars), and the roads joining its ends climb to the deck over RampMeters
/// (RampStart/RampEnd, carried on into the next road where one is shorter). Runs after RoadMerges, before
/// RoadJunctions. Bridges cut at the tile edge keep the full clearance there, so tiles meet; an approach ramp that
/// crosses a tile edge does not reach into the neighbouring tile. Uses no game types.
/// </summary>
public static class RoadBridges
{
    /// <summary>Length of an approach ramp: the deck height at about 4.5 % grade.</summary>
    public const double RampMeters = 130;
    private const double PillarSpacing = 22;
    /// <summary>No pillar this close to a bridge end (the abutment is there).</summary>
    private const double PillarEndMeters = 6;

    private readonly record struct P(double X, double Y);

    public static void Apply(List<RawRoad> roads, double planetRadius, double clearance)
    {
        var bridges = roads.Where(r => r.Bridge && r.Airfield == Airfield.None && r.Points.Count >= 2).ToList();
        if (bridges.Count == 0)
            return;
        var origin = bridges[0].Points[0];
        double k = Math.PI / 180 * planetRadius, kLon = k * Math.Cos(origin.Lat * Math.PI / 180);
        P Local((double Lat, double Lon) q) => new((q.Lon - origin.Lon) * kLon, (q.Lat - origin.Lat) * k);
        var below = roads.Where(r => !r.Bridge && r.Points.Count >= 2)
            .Select(r => (Road: r, Line: r.Points.Select(Local).ToArray())).ToList();

        foreach (var bridge in bridges)
        {
            var line = bridge.Points.Select(Local).ToArray();
            var ends = new HashSet<(double, double)> { bridge.Points[0], bridge.Points[^1] };
            // Roads below: not the ones the bridge starts or ends on.
            var crossed = below.Where(b => !b.Road.Points.Any(ends.Contains) && Crosses(line, b.Line)).ToList();
            if (crossed.Count == 0)
                continue;
            bridge.Overpass = true;

            // Pillars along the deck, except over the roads below.
            double total = 0;
            for (int i = 1; i < line.Length; i++)
                total += Distance(line[i - 1], line[i]);
            var pillars = new List<double>();
            for (double s = PillarEndMeters; s <= total - PillarEndMeters; s += PillarSpacing)
            {
                P at = PointAt(line, s);
                bool onRoad = crossed.Any(b => DistanceToLine(at, b.Line) < b.Road.Width / 2 + 1.5);
                if (!onRoad)
                    pillars.Add(s);
            }
            bridge.Pillars = pillars;

            // Approach ramps on the roads joining the ends.
            if (!bridge.StartAtTileEdge)
                Ramp(roads, bridge, bridge.Points[0], clearance, RampMeters, 2);
            if (!bridge.EndAtTileEdge)
                Ramp(roads, bridge, bridge.Points[^1], clearance, RampMeters, 2);
        }

        double Distance(P a, P b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        void Ramp(List<RawRoad> all, RawRoad from, (double, double) at, double lift, double length, int depth)
        {
            foreach (var road in all)
            {
                if (road == from || road.Bridge || road.Airfield != Airfield.None || road.Points.Count < 2)
                    continue;
                bool start = road.Points[0] == at && !road.StartAtTileEdge;
                bool end = road.Points[^1] == at && !road.EndAtTileEdge;
                if (!start && !end)
                    continue;
                if (start)
                    road.RampStart = Math.Max(road.RampStart, lift);
                else
                    road.RampEnd = Math.Max(road.RampEnd, lift);
                road.RampLength = length;
                // A short road passes the rest of the climb on to the next one.
                double roadLength = 0;
                var local = road.Points.Select(Local).ToArray();
                for (int i = 1; i < local.Length; i++)
                    roadLength += Distance(local[i - 1], local[i]);
                if (roadLength < length && depth > 0)
                {
                    double rest = lift * (1 - roadLength / length);
                    var far = start ? road.Points[^1] : road.Points[0];
                    bool farCut = start ? road.EndAtTileEdge : road.StartAtTileEdge;
                    if (rest > 0.3 && !farCut)
                        Ramp(all, road, far, rest, length - roadLength, depth - 1);
                }
            }
        }
    }

    private static bool Crosses(P[] a, P[] b)
    {
        for (int i = 0; i + 1 < a.Length; i++)
            for (int j = 0; j + 1 < b.Length; j++)
                if (SegmentsCross(a[i], a[i + 1], b[j], b[j + 1]))
                    return true;
        return false;
    }

    private static bool SegmentsCross(P a0, P a1, P b0, P b1)
    {
        double rx = a1.X - a0.X, ry = a1.Y - a0.Y, sx = b1.X - b0.X, sy = b1.Y - b0.Y;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-9)
            return false;
        double qx = b0.X - a0.X, qy = b0.Y - a0.Y;
        double t = (qx * sy - qy * sx) / denom, u = (qx * ry - qy * rx) / denom;
        return t > 0 && t < 1 && u > 0 && u < 1;
    }

    private static P PointAt(P[] line, double s)
    {
        for (int i = 1; i < line.Length; i++)
        {
            double dx = line[i].X - line[i - 1].X, dy = line[i].Y - line[i - 1].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (s <= len && len > 0)
                return new P(line[i - 1].X + dx * s / len, line[i - 1].Y + dy * s / len);
            s -= len;
        }
        return line[^1];
    }

    private static double DistanceToLine(P p, P[] line)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Length; i++)
        {
            P a = line[i], b = line[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
            double ex = a.X + dx * t - p.X, ey = a.Y + dy * t - p.Y;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }
}
