namespace GEE;

/// <summary>
/// Gives roads of the same class that overlap (ramps running side by side, roads meeting at an interchange) different
/// heights, so they do not flicker. A random height per road (VectorTile) lets two of five overlapping roads collide
/// by chance; here neighbouring roads are coloured like a graph, each taking the lowest tie level none of its
/// neighbours has. Uses no game types.
/// </summary>
public static class RoadLayers
{
    private const double CellMeters = 16;
    private const double SampleMeters = 4;

    public static void Apply(List<RawRoad> roads, double planetRadius)
    {
        var candidates = new List<RawRoad>();
        foreach (var road in roads)
            if (road.Points.Count >= 2 && !road.Overpass && road.Airfield == Airfield.None)
                candidates.Add(road);
        if (candidates.Count < 2)
            return;

        var origin = candidates[0].Points[0];
        double k = Math.PI / 180 * planetRadius, kLon = k * Math.Cos(origin.Lat * Math.PI / 180);

        // Samples of every road's centre line along its length, in a grid keyed by cell.
        var grid = new Dictionary<(int, int), List<(int Road, double X, double Y)>>();
        for (int r = 0; r < candidates.Count; r++)
        {
            var pts = candidates[r].Points;
            double carry = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double ax = (pts[i].Lon - origin.Lon) * kLon, ay = (pts[i].Lat - origin.Lat) * k;
                double bx = (pts[i + 1].Lon - origin.Lon) * kLon, by = (pts[i + 1].Lat - origin.Lat) * k;
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                for (double s = carry; s <= len; s += SampleMeters)
                {
                    double t = len > 0 ? s / len : 0;
                    AddSample(r, ax + (bx - ax) * t, ay + (by - ay) * t);
                    carry = s + SampleMeters - len;
                }
                if (i + 2 == pts.Count)
                    AddSample(r, bx, by);
            }
        }

        void AddSample(int road, double x, double y)
        {
            var key = ((int)Math.Floor(x / CellMeters), (int)Math.Floor(y / CellMeters));
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new();
            list.Add((road, x, y));
        }

        // Roads of the same rank (the class steps are larger than all tie levels together) closer than their widths.
        var neighbours = new HashSet<int>[candidates.Count];
        for (int r = 0; r < candidates.Count; r++)
            neighbours[r] = new HashSet<int>();
        foreach (var ((cx, cy), list) in grid)
            for (int dx = 0; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy < 0)
                        continue;
                    if (!grid.TryGetValue((cx + dx, cy + dy), out var other))
                        continue;
                    foreach (var a in list)
                        foreach (var b in other)
                        {
                            if (a.Road == b.Road || candidates[a.Road].Rank != candidates[b.Road].Rank)
                                continue;
                            double reach = (candidates[a.Road].Width + candidates[b.Road].Width) / 2 + 0.5;
                            if ((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) <= reach * reach)
                            {
                                neighbours[a.Road].Add(b.Road);
                                neighbours[b.Road].Add(a.Road);
                            }
                        }
                }

        // Most crowded roads first.
        var order = Enumerable.Range(0, candidates.Count).OrderByDescending(r => neighbours[r].Count).ToList();
        var level = new int[candidates.Count];
        Array.Fill(level, -1);
        var used = new int[Roads.TieLevels];
        foreach (int r in order)
        {
            if (neighbours[r].Count == 0)
                continue;
            Array.Clear(used);
            foreach (int n in neighbours[r])
                if (level[n] >= 0)
                    used[level[n]]++;
            int best = 0;
            for (int l = 1; l < Roads.TieLevels; l++)
                if (used[l] < used[best])
                    best = l;
            level[r] = best;
            candidates[r].Lift = Roads.Lift(candidates[r].Rank, best);
        }
    }
}
