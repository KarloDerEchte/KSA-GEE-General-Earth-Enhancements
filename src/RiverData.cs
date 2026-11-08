namespace GEE;

/// <summary>A river, stream or canal of a vector tile: its width (m) and its line as latitude, longitude pairs (degrees).</summary>
public sealed class RiverLine
{
    public double Width;
    public double[] LatLon = Array.Empty<double>();
}

/// <summary>
/// The rivers around the camera as a map: coverage 0..255 of water per texel, in the planet's (u, v) map like the lake map. Two windows:
/// a near one (NearMeters, about 6 m per texel: a stream of a few metres still gets a texel or two) and a far one (FarMeters, about 39 m per
/// texel: the big rivers out to the horizon). The terrain's colour shader reads them and paints the water. Rivers come as lines (the
/// centre line, a width by class) and as areas (the banks of the big ones, as triangles). No game types here, so it can be tested offline.
/// </summary>
internal static class RiverMap
{
    public const int Size = 2048;
    public const double NearMeters = 12000, FarMeters = 80000;
    /// <summary>The far window only draws lines at least this wide (m): streams would be exaggerated to a texel.</summary>
    public const double FarMinWidth = 15;

    public sealed class Window
    {
        public float U0, V0, Du, Dv;
        public byte[] Data = Array.Empty<byte>();
        public ulong Signature;
        public int Lines, Areas;
    }

    /// <summary>Width of a water line by the vector tile's waterway class (m); 0 for classes that are drawn no more.</summary>
    public static double Width(string? waterwayClass) => waterwayClass switch
    {
        "river" => 28,
        "canal" => 16,
        "stream" => 7,
        _ => 0,                                                     // drains and ditches
    };

    /// <summary>Window corner and size for a camera position (corner snapped to a 64 texel grid, scale from the whole degree of latitude).</summary>
    public static (double U0, double V0, double Du, double Dv) Geometry(double latDeg, double lonDeg, double planetRadius, double windowMeters)
    {
        double snapLat = Math.Round(latDeg) * Math.PI / 180;
        double du = windowMeters / (2 * Math.PI * planetRadius * Math.Cos(snapLat)), dv = windowMeters / (Math.PI * planetRadius);
        double uc = 0.5 + lonDeg / 360, vc = 0.5 - latDeg / 180;
        double stepU = du * 64 / Size, stepV = dv * 64 / Size;
        return (Math.Floor((uc - du / 2) / stepU) * stepU, Math.Floor((vc - dv / 2) / stepV) * stepV, du, dv);
    }

    /// <summary>
    /// Rasterises the areas (filled, edges softened by a texel) and the lines (soft edges; narrower ones keep a line of about one texel).
    /// areas: triangles as latitude, longitude pairs, six numbers per triangle. Lines narrower than minLineWidth are left out.
    /// </summary>
    public static Window Build(double latDeg, double lonDeg, double planetRadius, IEnumerable<RiverLine> lines, IEnumerable<double[]> areas,
        double windowMeters, double minLineWidth)
    {
        var (u0, v0, du, dv) = Geometry(latDeg, lonDeg, planetRadius, windowMeters);
        var window = new Window { U0 = (float)u0, V0 = (float)v0, Du = (float)du, Dv = (float)dv, Data = new byte[Size * Size] };
        double texelMeters = windowMeters / Size;
        ulong h = 14695981039346656037UL;

        // Areas first: binary, then softened.
        byte[]? fill = null;
        foreach (var triangles in areas)
        {
            for (int k = 0; k + 5 < triangles.Length; k += 6)
            {
                fill ??= new byte[Size * Size];
                Triangle(fill,
                    (0.5 + triangles[k + 1] / 360 - u0) / du * Size, (0.5 - triangles[k] / 180 - v0) / dv * Size,
                    (0.5 + triangles[k + 3] / 360 - u0) / du * Size, (0.5 - triangles[k + 2] / 180 - v0) / dv * Size,
                    (0.5 + triangles[k + 5] / 360 - u0) / du * Size, (0.5 - triangles[k + 4] / 180 - v0) / dv * Size);
            }
            window.Areas++;
            if (triangles.Length >= 2)
                h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(triangles[0] * 7 + triangles[^1] * 3 + triangles.Length)) * 1099511628211UL;
        }
        if (fill != null)
        {
            Soften(fill, window.Data);
        }

        foreach (var line in lines)
        {
            if (line.LatLon.Length < 4 || line.Width < minLineWidth)
                continue;
            // Half the width in texels, but at least 0.8: a stream of seven metres is not lost between two texels.
            double radius = Math.Max(line.Width / 2 / texelMeters, 0.8);
            double px0 = 0, py0 = 0;
            for (int i = 0; i + 1 < line.LatLon.Length; i += 2)
            {
                double px = (0.5 + line.LatLon[i + 1] / 360 - u0) / du * Size, py = (0.5 - line.LatLon[i] / 180 - v0) / dv * Size;
                if (i > 0)
                    Segment(window.Data, px0, py0, px, py, radius);
                px0 = px;
                py0 = py;
            }
            window.Lines++;
            h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(line.LatLon[0] + line.LatLon[^1] * 3 + line.Width)) * 1099511628211UL;
        }
        h = (h ^ BitConverter.SingleToUInt32Bits(window.U0)) * 1099511628211UL;
        h = (h ^ BitConverter.SingleToUInt32Bits(window.V0)) * 1099511628211UL;
        window.Signature = h;
        return window;
    }

    /// <summary>Fills a triangle (texel centres inside it, with a little tolerance so shared edges leave no gaps).</summary>
    private static void Triangle(byte[] data, double ax, double ay, double bx, double by, double cx, double cy)
    {
        int x0 = Math.Max((int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))), 0), x1 = Math.Min((int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))), Size - 1);
        int y0 = Math.Max((int)Math.Floor(Math.Min(ay, Math.Min(by, cy))), 0), y1 = Math.Min((int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))), Size - 1);
        double det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (Math.Abs(det) < 1e-9 || x1 < x0 || y1 < y0)
            return;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / det;
                double l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / det;
                if (l1 >= -0.02 && l2 >= -0.02 && 1 - l1 - l2 >= -0.02)
                    data[y * Size + x] = 255;
            }
        }
    }

    /// <summary>A 3 x 3 average of the filled map into the map (so the edges of an area are one texel soft).</summary>
    private static void Soften(byte[] fill, byte[] output)
    {
        var row = new int[Size];
        for (int y = 0; y < Size; y++)
        {
            int up = Math.Max(y - 1, 0) * Size, here = y * Size, down = Math.Min(y + 1, Size - 1) * Size;
            for (int x = 0; x < Size; x++)
                row[x] = fill[up + x] + fill[here + x] + fill[down + x];
            for (int x = 0; x < Size; x++)
            {
                int sum = row[Math.Max(x - 1, 0)] + row[x] + row[Math.Min(x + 1, Size - 1)];
                byte value = (byte)(sum / 9);
                if (value > output[here + x])
                    output[here + x] = value;
            }
        }
    }

    private static void Segment(byte[] data, double x0, double y0, double x1, double y1, double radius)
    {
        double reach = radius + 1;
        int minX = Math.Max((int)Math.Floor(Math.Min(x0, x1) - reach), 0), maxX = Math.Min((int)Math.Ceiling(Math.Max(x0, x1) + reach), Size - 1);
        int minY = Math.Max((int)Math.Floor(Math.Min(y0, y1) - reach), 0), maxY = Math.Min((int)Math.Ceiling(Math.Max(y0, y1) + reach), Size - 1);
        if (maxX < minX || maxY < minY)
            return;
        double dx = x1 - x0, dy = y1 - y0, length2 = dx * dx + dy * dy;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5 - x0, py = y + 0.5 - y0;
                double t = length2 > 1e-12 ? Math.Clamp((px * dx + py * dy) / length2, 0, 1) : 0;
                double ex = px - t * dx, ey = py - t * dy;
                double coverage = Math.Clamp(radius + 0.5 - Math.Sqrt(ex * ex + ey * ey), 0, 1);
                if (coverage <= 0)
                    continue;
                byte value = (byte)Math.Round(coverage * 255);
                int i = y * Size + x;
                if (value > data[i])
                    data[i] = value;
            }
        }
    }
}
