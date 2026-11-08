namespace GEE;

/// <summary>
/// Finds where the roads of a tile meet and gives each road a clear stretch there. The tiles simplify their lines,
/// so a straight road often has no point where another one crosses it or ends on it: crossings of two segments and
/// road ends lying on (or within SnapMeters of) another road get a shared point inserted first, the very same
/// (lat, lon) in both roads. At every junction point each road is then marked Fixed (Placement does not round it off)
/// and gets a Clear radius: Placement lays the pieces within it in plain asphalt without lines, so lines stop at the
/// junction instead of running across it. Ends cut at the tile edge are no junctions. Highways and bridges are left
/// out (roads cross them at other levels; ramps join them through RoadMerges). Uses no game types.
/// </summary>
public static class RoadJunctions
{
    /// <summary>Two roads continuing into each other are a junction only if they turn by more than this, or change width.</summary>
    private const double ContinuationDegrees = 15;
    /// <summary>How far off another road a road end may lie and still end on it (tile coordinates are ~0.6 m steps).</summary>
    private const double SnapMeters = 1.5;
    private const double CellMeters = 64;

    private readonly record struct Arm(int Road, int Point, bool End);
    private readonly record struct Insert(int Road, int Segment, double T, (double Lat, double Lon) Point);

    public static void Find(List<RawRoad> roads)
    {
        var joinable = new List<int>();
        for (int r = 0; r < roads.Count; r++)
            if (Joins(roads[r]))
                joinable.Add(r);
        if (joinable.Count == 0)
            return;
        Connect(roads, joinable);

        var arms = new Dictionary<(double, double), List<Arm>>();
        foreach (int r in joinable)
        {
            var road = roads[r];
            int last = road.Points.Count - 1;
            for (int k = 0; k <= last; k++)
            {
                if ((k == 0 && road.StartAtTileEdge) || (k == last && road.EndAtTileEdge))
                    continue;
                if (!arms.TryGetValue(road.Points[k], out var list))
                    arms[road.Points[k]] = list = new List<Arm>(2);
                list.Add(new Arm(r, k, k == 0 || k == last));
            }
        }

        foreach (var (point, list) in arms)
        {
            if (list.Count < 2 || list.All(a => a.Road == list[0].Road))
                continue;
            // A road through the point brings two arms, one ending there brings one.
            int count = list.Sum(a => a.End ? 1 : 2);
            bool turn = count == 2 && Turns(roads, list, point.Item1);
            if (count < 2 || (count == 2 && !turn))
                continue;
            foreach (var arm in list)
            {
                var road = roads[arm.Road];
                (road.Fixed ??= new bool[road.Points.Count])[arm.Point] = true;
                if (count == 2)
                    continue;                                  // a bend between two roads: rounded corners do
                // Clear as far as the widest other road reaches, a little more for oblique junctions.
                double other = list.Where(a => a.Road != arm.Road).Max(a => roads[a.Road].Width / 2);
                (road.Clear ??= new double[road.Points.Count])[arm.Point] = Math.Max(road.Clear[arm.Point], other * 1.15 + 0.5);
            }
        }
    }

    private static bool Joins(RawRoad road) =>
        road.Airfield == Airfield.None && !road.Bridge && !road.Surface.IsHighway() && road.Points.Count >= 2;

    /// <summary>Inserts shared points where segments cross and where road ends lie on another road.</summary>
    private static void Connect(List<RawRoad> roads, List<int> joinable)
    {
        var origin = roads[joinable[0]].Points[0];
        double k = Math.PI / 180 * 6371000, kLon = k * Math.Cos(origin.Lat * Math.PI / 180);
        (double X, double Y) Local((double Lat, double Lon) q) => ((q.Lon - origin.Lon) * kLon, (q.Lat - origin.Lat) * k);
        (double, double) Geo(double x, double y) => (origin.Lat + y / k, origin.Lon + x / kLon);

        // Segments by grid cell (each segment in every cell its bounding box touches).
        var local = roads.Select(r => r.Points.Select(Local).ToArray()).ToArray();
        var cells = new Dictionary<(int, int), List<(int Road, int Segment)>>();
        foreach (int r in joinable)
            for (int s = 0; s + 1 < local[r].Length; s++)
            {
                var (a, b) = (local[r][s], local[r][s + 1]);
                int x0 = Cell(Math.Min(a.X, b.X) - SnapMeters), x1 = Cell(Math.Max(a.X, b.X) + SnapMeters);
                int y0 = Cell(Math.Min(a.Y, b.Y) - SnapMeters), y1 = Cell(Math.Max(a.Y, b.Y) + SnapMeters);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cy = y0; cy <= y1; cy++)
                    {
                        if (!cells.TryGetValue((cx, cy), out var list))
                            cells[(cx, cy)] = list = new List<(int, int)>();
                        list.Add((r, s));
                    }
            }

        var inserts = new List<Insert>();
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var list in cells.Values)
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var (ra, sa) = list[i];
                    var (rb, sb) = list[j];
                    if (ra == rb || !seen.Add(ra < rb ? (ra, sa, rb, sb) : (rb, sb, ra, sa)))
                        continue;
                    var (a0, a1) = (local[ra][sa], local[ra][sa + 1]);
                    var (b0, b1) = (local[rb][sb], local[rb][sb + 1]);
                    if (Cross(a0, a1, b0, b1, out double ta, out double tb) && ta > 1e-6 && ta < 1 - 1e-6 && tb > 1e-6 && tb < 1 - 1e-6)
                    {
                        var p = Geo(a0.X + (a1.X - a0.X) * ta, a0.Y + (a1.Y - a0.Y) * ta);
                        inserts.Add(new Insert(ra, sa, ta, p));
                        inserts.Add(new Insert(rb, sb, tb, p));
                    }
                }

        // Road ends on another road (T-junctions): the end moves onto that road and a point is inserted there.
        foreach (int r in joinable)
        {
            var road = roads[r];
            foreach (int end in new[] { 0, road.Points.Count - 1 })
            {
                if ((end == 0 && road.StartAtTileEdge) || (end > 0 && road.EndAtTileEdge))
                    continue;
                var p = local[r][end];
                if (!cells.TryGetValue((Cell(p.X), Cell(p.Y)), out var near))
                    continue;
                double best = SnapMeters;
                Insert? hit = null;
                foreach (var (o, s) in near)
                {
                    if (o == r)
                        continue;
                    var (a, b) = (local[o][s], local[o][s + 1]);
                    double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                    if (len2 < 1e-6)
                        continue;
                    double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
                    double qx = a.X + dx * t, qy = a.Y + dy * t;
                    double d = Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
                    if (d >= best)
                        continue;
                    best = d;
                    // Onto an existing point of the other road, or a new one inside the segment.
                    var snapped = t < 1e-6 ? roads[o].Points[s] : t > 1 - 1e-6 ? roads[o].Points[s + 1] : Geo(qx, qy);
                    hit = new Insert(o, s, t, snapped);
                }
                if (hit is not { } h || road.Points[end] == h.Point)
                    continue;
                road.Points[end] = h.Point;
                if (h.T > 1e-6 && h.T < 1 - 1e-6)
                    inserts.Add(h);
            }
        }

        // Insert from the back of each road, so earlier segment indices stay valid.
        foreach (var group in inserts.GroupBy(x => x.Road))
        {
            var points = roads[group.Key].Points;
            foreach (var ins in group.OrderByDescending(x => x.Segment).ThenByDescending(x => x.T))
                if (points[ins.Segment] != ins.Point && points[ins.Segment + 1] != ins.Point)
                    points.Insert(ins.Segment + 1, ins.Point);
        }
    }

    private static int Cell(double v) => (int)Math.Floor(v / CellMeters);

    /// <summary>Intersection of segments a0-a1 and b0-b1 as fractions along each.</summary>
    private static bool Cross((double X, double Y) a0, (double X, double Y) a1, (double X, double Y) b0, (double X, double Y) b1,
        out double ta, out double tb)
    {
        double rx = a1.X - a0.X, ry = a1.Y - a0.Y, sx = b1.X - b0.X, sy = b1.Y - b0.Y;
        double denom = rx * sy - ry * sx;
        ta = tb = 0;
        if (Math.Abs(denom) < 1e-9)
            return false;
        double qx = b0.X - a0.X, qy = b0.Y - a0.Y;
        ta = (qx * sy - qy * sx) / denom;
        tb = (qx * ry - qy * rx) / denom;
        return ta >= 0 && ta <= 1 && tb >= 0 && tb <= 1;
    }

    /// <summary>True if the two arms at a point of two road ends turn noticeably or differ in width.</summary>
    private static bool Turns(List<RawRoad> roads, List<Arm> list, double lat)
    {
        if (list.Count != 2)
            return false;
        RawRoad a = roads[list[0].Road], b = roads[list[1].Road];
        if (Math.Abs(a.Width - b.Width) > 0.5)
            return true;
        var (ax, ay) = Away(a, list[0].Point, lat);
        var (bx, by) = Away(b, list[1].Point, lat);
        // Arms leaving in opposite directions go straight on.
        double cos = -(ax * bx + ay * by);
        return cos < Math.Cos(ContinuationDegrees * Math.PI / 180);
    }

    /// <summary>Unit direction (east, north) from a road's end point into the road.</summary>
    private static (double X, double Y) Away(RawRoad road, int point, double lat)
    {
        var p = road.Points[point];
        var q = road.Points[point == 0 ? 1 : point - 1];
        double x = (q.Lon - p.Lon) * Math.Cos(lat * Math.PI / 180), y = q.Lat - p.Lat;
        double length = Math.Sqrt(x * x + y * y);
        return length > 0 ? (x / length, y / length) : (1, 0);
    }
}
