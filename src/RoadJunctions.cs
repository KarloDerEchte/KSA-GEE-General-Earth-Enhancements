namespace GEE;

/// <summary>
/// Finds where the roads of a tile meet, so junctions can be smoothed: OpenStreetMap roads share the junction's
/// node, and the tile's integer coordinates give shared points the very same position. Every road point there is
/// kept where it is (Placement does not round it off), and one road lays a round pad of plain asphalt over the
/// junction, which covers the square road ends, the lines running through, and rounds the corners. Ends cut at the
/// tile edge are no junctions; each junction lies in exactly one tile, so it gets one pad. Uses no game types.
/// </summary>
public static class RoadJunctions
{
    /// <summary>Two roads continuing into each other get a pad only if they turn by more than this, or change width.</summary>
    private const double ContinuationDegrees = 15;
    /// <summary>Pad height above the highest road meeting there; less than half of Roads.RankStep.</summary>
    private const double PadLift = 0.012;

    private readonly record struct Arm(int Road, int Point, bool End);

    public static void Find(List<RawRoad> roads)
    {
        var arms = new Dictionary<(double, double), List<Arm>>();
        for (int r = 0; r < roads.Count; r++)
        {
            var road = roads[r];
            if (road.Airfield != Airfield.None)
                continue;
            int last = road.Points.Count - 1;
            for (int k = 0; k <= last; k++)
            {
                bool end = k == 0 || k == last;
                if ((k == 0 && road.StartAtTileEdge) || (k == last && road.EndAtTileEdge))
                    continue;
                if (!arms.TryGetValue(road.Points[k], out var list))
                    arms[road.Points[k]] = list = new List<Arm>(2);
                list.Add(new Arm(r, k, end));
            }
        }

        foreach (var (point, list) in arms)
        {
            if (list.Count < 2)
                continue;
            // A road through the point brings two arms, one ending there brings one.
            int count = list.Sum(a => a.End ? 1 : 2);
            if (count < 2 || (count == 2 && !Turns(roads, list, point.Item1)))
                continue;

            foreach (var arm in list)
                (roads[arm.Road].Fixed ??= new bool[roads[arm.Road].Points.Count])[arm.Point] = true;
            // Pads lie on the terrain; on a bridge they would hang below the deck. Plain asphalt would also show as a
            // grey blot on a highway's scanned surface and lines where ramps join.
            if (list.Any(a => roads[a.Road].Bridge || roads[a.Road].Surface == RoadSurface.Highway))
                continue;

            // Big enough to cover the lines of the two widest roads where they cross (half-diagonal of their
            // overlap), a little less so it does not bulge far past the wider road's edges.
            var halves = list.Select(a => roads[a.Road].Width / 2).OrderByDescending(h => h).ToArray();
            double h1 = halves[0], h2 = halves.Length > 1 ? halves[1] : h1;
            double radius = count == 2 ? h1 : Math.Max(h1, 0.9 * Math.Sqrt(h1 * h1 + h2 * h2));
            double lift = list.Max(a => roads[a.Road].Lift) + PadLift;
            var owner = list.OrderByDescending(a => roads[a.Road].Rank).ThenBy(a => a.Road).First();
            (roads[owner.Road].Pads ??= new List<JunctionPad>()).Add(new JunctionPad(owner.Point, radius, lift));
        }
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
