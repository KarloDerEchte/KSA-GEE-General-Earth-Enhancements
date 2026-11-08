namespace GEE;

/// <summary>
/// Which parts of a sea-level lagoon's polygons really become water in the lake map: only pieces whose real elevation is at the sea's
/// height, islands (holes of the polygons) stay land however small, and real high ground inside the outline stays dry. Free of game
/// types except the elevation window, so it can be run offline.
/// </summary>
internal static class LakeMaskFilter
{
    /// <summary>A polygon piece of a sea-level group that touches no tile edge must be at least this big (m2): smaller ones are ponds and canals of the land.</summary>
    public const double MinSeaPieceM2 = 0.4e6;
    /// <summary>Real elevation above this (m) inside a sea-level polygon is a causeway, dyke or island: dry.</summary>
    public const double HighGroundMeters = 3.5;
    /// <summary>Width of the lake map window (m), as LakeOcean.WindowMeters.</summary>
    private const double MaskWindowMeters = 100000;
    /// <summary>Islands up to this many texels across are burned into the mask as land (bigger ones already have pixels of their own).</summary>
    private const double MaxBurnedIslandTexels = 24;

    /// <summary>True if the real elevation inside the piece is at the sea's height (lower fifth of the samples: islands, causeways and offset tiles raise the middle, water is the low end; at least three samples). Unknown: rejectUnknown decides.</summary>
    public static bool AtSeaLevel(LakeInfo info, ElevationWindow real, bool rejectUnknown)
    {
        var heights = new List<double>();
        var geo = info.InsideGeo;
        for (int k = 0; k + 1 < geo.Length; k += 2)
        {
            if (real.TrySample(geo[k], geo[k + 1], out double h, out double w) && w > 0.5)
                heights.Add(h);
        }
        if (heights.Count < 3)
            return !rejectUnknown;
        heights.Sort();
        return heights[heights.Count / 5] <= WaterGeometry.SeaLakeMaxMeters;
    }

    public static void Refine(byte[] mask, int n, byte label, List<LakeInfo> pieces, ElevationWindow? real, bool seaLevel, double u0, double v0, double du, double dv)
    {
        foreach (var info in pieces)
        {
            if (info.HoleGeo == null)
                continue;
            foreach (var ring in info.HoleGeo)
                BurnRing(mask, n, label, ring, u0, v0, du, dv);
        }
        if (seaLevel)
        {
            Open(mask, n, label);
            DropSmallComponents(mask, n, label, (int)(MinSeaPieceM2 / Math.Pow(MaskWindowMeters / n, 2)));
        }
        if (!seaLevel || real == null)
            return;
        // Real high ground inside the outline (causeway, dyke, spoil bank) stays dry, but only where it is a thin feature: a high
        // blob wider than BigHighTexels is not a feature of the shore but a data fault (a zoom 12 tile of another source or datum
        // reads a few metres high over the lagoon, with the tile's straight edges), and the outline is the better source there.
        var high = new bool[n * n];
        for (int y = 0; y < n; y++)
        {
            var row = mask.AsSpan(y * n, n);
            double lat = (0.5 - (v0 + (y + 0.5) / n * dv)) * 180;
            for (int x = 0; x < n; x++)
            {
                if (row[x] != label)
                    continue;
                double lon = (u0 + (x + 0.5) / n * du - 0.5) * 360;
                if (real.TrySample(lat, lon, out double h, out double w) && w > 0.5 && h > HighGroundMeters)
                    high[y * n + x] = true;
            }
        }
        var big = BoxOpen(high, n, BigHighTexels);
        for (int i = 0; i < high.Length; i++)
            if (high[i] && !big[i])
                mask[i] = 0;
    }

    /// <summary>Texel radius of the box (2r+1 across) that a high-ground blob must contain to count as a data fault, not a causeway.</summary>
    private const int BigHighTexels = 3;

    /// <summary>Morphological opening of a boolean image with a (2r+1) square: what survives is every blob that contains such a square (and its surroundings).</summary>
    public static bool[] BoxOpen(bool[] a, int n, int r)
    {
        var eroded = BoxPass(a, n, r, all: true);
        return BoxPass(eroded, n, r, all: false);
    }

    private static bool[] BoxPass(bool[] a, int n, int r, bool all)
    {
        // separable: along x then along y, with running counts (true = count of set texels in the window)
        var tmp = new int[n * n];
        var result = new bool[n * n];
        for (int y = 0; y < n; y++)
        {
            int count = 0;
            for (int x = -r; x < n + r; x++)
            {
                int add = x + r, sub = x - r - 1;
                if (add < n && a[y * n + add]) count++;
                if (sub >= 0 && a[y * n + sub]) count--;
                if (x >= 0 && x < n) tmp[y * n + x] = count;
            }
        }
        int window = 2 * r + 1;
        var column = new int[n];
        for (int x = 0; x < n; x++)
        {
            int count = 0;
            for (int y = -r; y < n + r; y++)
            {
                int add = y + r, sub = y - r - 1;
                if (add < n) count += tmp[add * n + x];
                if (sub >= 0) count -= tmp[sub * n + x];
                if (y >= 0 && y < n) result[y * n + x] = all ? count == window * window : count > 0;
            }
        }
        return result;
    }

    /// <summary>
    /// Connected water (4-neighbour) of this group that is smaller than minPixels and does not reach the window's rim goes dry: a pond
    /// or canal of the land that the tile-edge union joined to the lagoon (its polygon piece touches a tile edge, so the per-piece
    /// size test let it through). It floods as a few centimetres of white shallow water with straight tile-edge cuts. Fragments of
    /// the lagoon cut at tile edges are part of its big component in the raster, so they stay.
    /// </summary>
    public static void DropSmallComponents(byte[] mask, int n, byte label, int minPixels)
    {
        var seen = new bool[n * n];
        var stack = new Stack<int>();
        var members = new List<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] != label || seen[start])
                continue;
            members.Clear();
            bool rim = false;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                members.Add(i);
                int x = i % n, y = i / n;
                if (x == 0 || y == 0 || x == n - 1 || y == n - 1)
                    rim = true;
                if (x > 0) Visit(i - 1);
                if (x < n - 1) Visit(i + 1);
                if (y > 0) Visit(i - n);
                if (y < n - 1) Visit(i + n);
            }
            if (!rim && members.Count < minPixels)
                foreach (int i in members)
                    mask[i] = 0;
        }

        void Visit(int j)
        {
            if (!seen[j] && mask[j] == label)
            {
                seen[j] = true;
                stack.Push(j);
            }
        }
    }

    /// <summary>
    /// Morphological opening with a 3x3 square: water of this group narrower than three texels (finger canals between the lots of a
    /// subdivision, ditches along roads) goes dry. It is too narrow for the map to show without flooding the houses beside it.
    /// </summary>
    public static void Open(byte[] mask, int n, byte label)
    {
        int x0 = n, y0 = n, x1 = -1, y1 = -1;
        for (int y = 0; y < n; y++)
        {
            var row = mask.AsSpan(y * n, n);
            int first = row.IndexOf(label);
            if (first < 0)
                continue;
            x0 = Math.Min(x0, first);
            x1 = Math.Max(x1, row.LastIndexOf(label));
            y0 = Math.Min(y0, y);
            y1 = y;
        }
        if (x1 < 0)
            return;
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var eroded = new bool[w * h];
        bool Is(int x, int y) => x < 0 || y < 0 || x >= n || y >= n || mask[y * n + x] == label;     // beyond the window: water continues
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int mx = x0 + x, my = y0 + y;
                if (mask[my * n + mx] != label)
                    continue;
                bool all = true;
                for (int dy = -1; dy <= 1 && all; dy++)
                    for (int dx = -1; dx <= 1 && all; dx++)
                        all = Is(mx + dx, my + dy);
                eroded[y * w + x] = all;
            }
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int mx = x0 + x, my = y0 + y;
                if (mask[my * n + mx] != label)
                    continue;
                bool keep = false;
                for (int dy = -1; dy <= 1 && !keep; dy++)
                {
                    for (int dx = -1; dx <= 1 && !keep; dx++)
                    {
                        int ex = x + dx, ey = y + dy;
                        if (ex >= 0 && ey >= 0 && ex < w && ey < h)
                            keep = eroded[ey * w + ex];
                    }
                }
                if (!keep)
                    mask[my * n + mx] = 0;
            }
        }
    }

    /// <summary>Sets the pixels along a small island's outline (and the island itself) to land, where they belong to this group.</summary>
    private static void BurnRing(byte[] mask, int n, byte label, double[] ring, double u0, double v0, double du, double dv)
    {
        int count = ring.Length / 2;
        if (count < 3)
            return;
        var px = new double[count];
        var py = new double[count];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            px[i] = (0.5 + ring[2 * i + 1] / 360 - u0) / du * n;
            py[i] = (0.5 - ring[2 * i] / 180 - v0) / dv * n;
            minX = Math.Min(minX, px[i]); maxX = Math.Max(maxX, px[i]);
            minY = Math.Min(minY, py[i]); maxY = Math.Max(maxY, py[i]);
        }
        if (maxX < 0 || maxY < 0 || minX >= n || minY >= n || Math.Max(maxX - minX, maxY - minY) > MaxBurnedIslandTexels)
            return;
        void Clear(double x, double y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            if (ix >= 0 && iy >= 0 && ix < n && iy < n && mask[iy * n + ix] == label)
                mask[iy * n + ix] = 0;
        }
        for (int i = 0, prev = count - 1; i < count; prev = i++)
        {
            double dx = px[i] - px[prev], dy = py[i] - py[prev];
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) * 2));
            for (int s = 0; s <= steps; s++)
                Clear(px[prev] + dx * s / steps, py[prev] + dy * s / steps);
        }
        // The inside, by even-odd rows through the pixel centres (a coarse polygon may have been triangulated into a few pixels).
        for (int y = Math.Max((int)Math.Floor(minY), 0); y <= Math.Min((int)Math.Floor(maxY), n - 1); y++)
        {
            double cy = y + 0.5;
            for (int x = Math.Max((int)Math.Floor(minX), 0); x <= Math.Min((int)Math.Floor(maxX), n - 1); x++)
            {
                double cx = x + 0.5;
                bool inside = false;
                for (int i = 0, j = count - 1; i < count; j = i++)
                {
                    if (py[i] > cy != py[j] > cy && cx < (px[j] - px[i]) * (cy - py[i]) / (py[j] - py[i]) + px[i])
                        inside = !inside;
                }
                if (inside && mask[y * n + x] == label)
                    mask[y * n + x] = 0;
            }
        }
    }
}
