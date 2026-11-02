namespace GEE;

/// <summary>
/// Lays motorway ramps alongside their carriageway where they join or leave it. OpenStreetMap ends a ramp on a node of
/// the carriageway's centre line, so drawn as it is the ramp cuts across the lanes at a sharp angle. Here the ramp's
/// points near that node are pushed out to run parallel beside the carriageway (as the acceleration or deceleration
/// lane, overlapping its shoulder a little), and the ramp goes on along the carriageway past the node, sliding under
/// its edge over TaperMeters: downstream for an entry, upstream for an exit. Ramps lie lower than carriageways
/// (Roads.Classify), so the carriageway covers what slides under it. Runs before RoadJunctions, so the moved ends
/// make no junction pads. Uses no game types.
/// </summary>
public static class RoadMerges
{
    /// <summary>How far from the node along the ramp it is pulled beside the carriageway.</summary>
    private const double AlongsideMeters = 150;
    private const double TaperMeters = 90;
    /// <summary>The ramp covers this much of the carriageway's shoulder.</summary>
    private const double OverlapMeters = 1.0;

    private readonly record struct P(double X, double Y)
    {
        public static P operator +(P a, P b) => new(a.X + b.X, a.Y + b.Y);
        public static P operator -(P a, P b) => new(a.X - b.X, a.Y - b.Y);
        public static P operator *(P a, double k) => new(a.X * k, a.Y * k);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public P Unit => Length > 1e-9 ? this * (1 / Length) : new P(1, 0);
        public P Left => new(-Y, X);
        public static double Dot(P a, P b) => a.X * b.X + a.Y * b.Y;
        public static double Cross(P a, P b) => a.X * b.Y - a.Y * b.X;
    }

    public static void Apply(List<RawRoad> roads, double planetRadius)
    {
        var highwayAt = new Dictionary<(double, double), (RawRoad Road, int Index)>();
        foreach (var road in roads)
            if (road.Surface == RoadSurface.Highway)
                for (int k = 0; k < road.Points.Count; k++)
                    highwayAt.TryAdd(road.Points[k], (road, k));
        if (highwayAt.Count == 0)
            return;

        foreach (var ramp in roads)
        {
            if (ramp.Surface != RoadSurface.Ramp || ramp.Points.Count < 2)
                continue;
            if (!ramp.StartAtTileEdge && highwayAt.TryGetValue(ramp.Points[0], out var atStart))
                Merge(ramp, fromStart: true, atStart.Road, atStart.Index, planetRadius);
            if (!ramp.EndAtTileEdge && highwayAt.TryGetValue(ramp.Points[^1], out var atEnd))
                Merge(ramp, fromStart: false, atEnd.Road, atEnd.Index, planetRadius);
        }
    }

    private static void Merge(RawRoad ramp, bool fromStart, RawRoad highway, int node, double planetRadius)
    {
        var origin = highway.Points[node];
        double k = Math.PI / 180 * planetRadius, kLon = k * Math.Cos(origin.Lat * Math.PI / 180);
        P Local((double Lat, double Lon) q) => new((q.Lon - origin.Lon) * kLon, (q.Lat - origin.Lat) * k);
        (double, double) Geo(P p) => (origin.Lat + p.Y / k, origin.Lon + p.X / kLon);

        var h = highway.Points.Select(Local).ToList();
        var r = ramp.Points.Select(Local).ToList();
        if (!fromStart)
            r.Reverse();                                   // r runs from the node outward

        // Which side of the carriageway the ramp lies on, from a point well away from the node.
        P tangent = (h[Math.Min(node + 1, h.Count - 1)] - h[Math.Max(node - 1, 0)]).Unit;
        P far = r.FirstOrDefault(p => p.Length > 25, r[^1]);
        double side = Math.Sign(P.Cross(tangent, far));
        if (side == 0)
            return;
        double offset = (highway.Width + ramp.Width) / 2 - OverlapMeters;

        // Nearest point of the carriageway's line, with its direction there.
        (P Point, P Dir) Nearest(P p)
        {
            (P, P) best = (h[node], tangent);
            double bestD = double.MaxValue;
            for (int i = 0; i + 1 < h.Count; i++)
            {
                P a = h[i], d = h[i + 1] - a;
                double len2 = P.Dot(d, d);
                if (len2 < 1e-6)
                    continue;
                double t = Math.Clamp(P.Dot(p - a, d) / len2, 0, 1);
                P q = a + d * t;
                double dist = (p - q).Length;
                if (dist < bestD)
                {
                    bestD = dist;
                    best = (q, d.Unit);
                }
            }
            return best;
        }

        // Pull the ramp beside the carriageway near the node.
        double along = 0;
        for (int i = 0; i < r.Count; i++)
        {
            if (i > 0)
                along += (r[i] - r[i - 1]).Length;
            if (along > AlongsideMeters)
                break;
            var (q, dir) = Nearest(r[i]);
            double lateral = P.Cross(dir, r[i] - q) * side;
            if (lateral < offset)
                r[i] = q + dir.Left * (side * offset);
        }

        // Taper: on along the carriageway past the node, away from where the ramp comes from, sliding under its edge.
        double sense = -Math.Sign(P.Dot(far, tangent));
        if (sense == 0)
            return;
        int step = sense > 0 ? 1 : -1;
        bool cutEnd = step > 0 ? highway.EndAtTileEdge : highway.StartAtTileEdge;
        var taper = new List<P>();
        double walked = 0, hidden = (highway.Width - ramp.Width) / 2;
        for (int i = node; i + step >= 0 && i + step < h.Count && walked < TaperMeters; i += step)
        {
            P a = h[i], b = h[i + step];
            double len = (b - a).Length;
            if (len < 1e-6)
                continue;
            P dir = (b - a).Unit * step;                  // the carriageway's direction of travel
            for (double s = 10; s <= len && walked + s <= TaperMeters; s += 10)
            {
                double f = (walked + s) / TaperMeters;
                P on = a + (b - a).Unit * s;
                taper.Add(on + dir.Left * (side * (offset + (hidden - offset) * f)));
            }
            walked += len;
            // A carriageway cut at the tile edge continues in the next tile; the taper stops there.
            if (cutEnd && i + step == (step > 0 ? h.Count - 1 : 0))
                break;
        }
        if (taper.Count == 0)
            return;
        taper.Reverse();
        r.InsertRange(0, taper);

        if (!fromStart)
            r.Reverse();
        ramp.Points = r.Select(Geo).ToList();
    }
}
