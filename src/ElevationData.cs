namespace GEE;

/// <summary>
/// Real elevation (AWS Terrain Tiles, "Terrarium" encoding: metres = R * 256 + G + B / 256 - 32768; SRTM, ASTER, NED and others)
/// on a window around the camera, in the planet's (u, v) map like the lake map: u0, v0 is the window's corner, du, dv its size, N x N
/// texels, row 0 in the north. The numbers here do not use game types, so they can be tested offline against real tiles.
/// </summary>
internal sealed class ElevationWindow
{
    public const int Zoom = 12, TileSize = 256, Size = 2048;
    public const double WindowMeters = 80000;
    /// <summary>Marks a texel without data; real heights are all above this.</summary>
    public const float NoData = -20000f;
    /// <summary>Share of the window's width over which the real elevation fades into the game's heightmap at its rim.</summary>
    public const double FadeShare = 0.12;

    public float U0, V0, Du, Dv;
    public float[] Heights = Array.Empty<float>();
    private static int _counter;
    /// <summary>Counts the windows built: a window with more tiles has the same corner but a new number.</summary>
    public int Version;
    /// <summary>Tiles along one side of the zoom 12 map.</summary>
    public const int TilesPerSide = 1 << Zoom;
    /// <summary>Tiles (zoom 12 x, y) the window was made from, for the loader to keep.</summary>
    public HashSet<(int, int)> Used = new();
    /// <summary>Texels with data / all texels.</summary>
    public double Coverage;

    /// <summary>Web Mercator pixel of a point on the zoom 12 map (fractional).</summary>
    public static (double X, double Y) Pixel(double latDeg, double lonDeg)
    {
        double n = (1 << Zoom) * (double)TileSize;
        double latRad = Math.Clamp(latDeg, -85.05, 85.05) * Math.PI / 180;
        return ((lonDeg + 180) / 360 * n, (1 - Math.Asinh(Math.Tan(latRad)) / Math.PI) / 2 * n);
    }

    /// <summary>Window corner and size for a camera position: scale from the whole degree of latitude, corner snapped to a 64 texel grid.</summary>
    public static (double U0, double V0, double Du, double Dv) Geometry(double latDeg, double lonDeg, double planetRadius)
    {
        double snapLat = Math.Round(latDeg) * Math.PI / 180;
        double du = WindowMeters / (2 * Math.PI * planetRadius * Math.Cos(snapLat)), dv = WindowMeters / (Math.PI * planetRadius);
        double uc = 0.5 + lonDeg / 360, vc = 0.5 - latDeg / 180;
        double stepU = du * 64 / Size, stepV = dv * 64 / Size;
        return (Math.Floor((uc - du / 2) / stepU) * stepU, Math.Floor((vc - dv / 2) / stepV) * stepV, du, dv);
    }

    /// <summary>The zoom 12 tiles a window around this position needs.</summary>
    public static List<(int X, int Y)> TilesNeeded(double latDeg, double lonDeg, double planetRadius)
    {
        var (u0, v0, du, dv) = Geometry(latDeg, lonDeg, planetRadius);
        var tiles = new HashSet<(int, int)>();
        // Corners and edge midpoints are enough: the window is small against the curvature.
        for (int j = 0; j <= 8; j++)
        {
            for (int i = 0; i <= 8; i++)
            {
                double lat = (0.5 - (v0 + dv * j / 8)) * 180, lon = (u0 + du * i / 8 - 0.5) * 360;
                var (px, py) = Pixel(lat, lon);
                tiles.Add(((int)(px / TileSize), (int)(py / TileSize)));
            }
        }
        // The rim tiles between the sampled points
        int minX = tiles.Min(t => t.Item1), maxX = tiles.Max(t => t.Item1), minY = tiles.Min(t => t.Item2), maxY = tiles.Max(t => t.Item2);
        // The map wraps around in longitude and ends at the poles: tile numbers that do not exist are left out.
        var all = new HashSet<(int, int)>();
        for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
                if (y >= 0 && y < TilesPerSide)
                    all.Add((((x % TilesPerSide) + TilesPerSide) % TilesPerSide, y));
        return all.ToList();
    }

    /// <summary>
    /// Builds the window: every texel is the bilinear height of the zoom 12 map at its place. tile(x, y) gives a tile's 256 x 256
    /// heights (row-major, north first) or null where it is not there (those texels get NoData).
    /// </summary>
    public static ElevationWindow Build(double latDeg, double lonDeg, double planetRadius, Func<int, int, float[]?> tile)
    {
        var (u0, v0, du, dv) = Geometry(latDeg, lonDeg, planetRadius);
        var window = new ElevationWindow { U0 = (float)u0, V0 = (float)v0, Du = (float)du, Dv = (float)dv, Heights = new float[Size * Size] };
        var cache = new Dictionary<(int, int), float[]?>();
        float[]? Get(int x, int y)
        {
            if (!cache.TryGetValue((x, y), out var data))
                cache[(x, y)] = data = tile(x, y);
            if (data != null)
                window.Used.Add((x, y));
            return data;
        }
        const long Wrap = (long)TilesPerSide * TileSize;
        float Texel(long gx, long gy)
        {
            if (gy < 0 || gy >= Wrap)
                return NoData;
            gx = ((gx % Wrap) + Wrap) % Wrap;
            var data = Get((int)(gx >> 8), (int)(gy >> 8));
            return data == null ? NoData : data[(int)(gy & 255) * TileSize + (int)(gx & 255)];
        }
        long valid = 0;
        for (int j = 0; j < Size; j++)
        {
            double lat = (0.5 - (v0 + (j + 0.5) / Size * dv)) * 180;
            for (int i = 0; i < Size; i++)
            {
                double lon = (u0 + (i + 0.5) / Size * du - 0.5) * 360;
                var (px, py) = Pixel(lat, lon);
                double fx = px - 0.5, fy = py - 0.5;
                long x0 = (long)Math.Floor(fx), y0 = (long)Math.Floor(fy);
                double tx = fx - x0, ty = fy - y0;
                float a = Texel(x0, y0), b = Texel(x0 + 1, y0), c = Texel(x0, y0 + 1), d = Texel(x0 + 1, y0 + 1);
                float h;
                if (a <= NoData || b <= NoData || c <= NoData || d <= NoData)
                {
                    h = NoData;
                }
                else
                {
                    h = (float)((a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty);
                    valid++;
                }
                window.Heights[j * Size + i] = h;
            }
        }
        window.Coverage = valid / (double)(Size * Size);
        window.Version = Interlocked.Increment(ref _counter);
        return window;
    }

    /// <summary>
    /// The window's height at a point and how far it counts (1 inside, falling to 0 over FadeShare of the width at the rim):
    /// exactly what the terrain shader does. False where the point is outside or on missing data.
    /// </summary>
    public bool TrySample(double latDeg, double lonDeg, out double height, out double weight)
    {
        height = weight = 0;
        double tx = (0.5 + lonDeg / 360 - U0) / Du, ty = (0.5 - latDeg / 180 - V0) / Dv;
        if (tx < 0 || tx > 1 || ty < 0 || ty > 1)
            return false;
        double fx = tx * Size - 0.5, fy = ty * Size - 0.5;
        int ix = Math.Clamp((int)Math.Floor(fx), 0, Size - 2), iy = Math.Clamp((int)Math.Floor(fy), 0, Size - 2);
        double ax = Math.Clamp(fx - ix, 0, 1), ay = Math.Clamp(fy - iy, 0, 1);
        float a = Heights[iy * Size + ix], b = Heights[iy * Size + ix + 1], c = Heights[(iy + 1) * Size + ix], d = Heights[(iy + 1) * Size + ix + 1];
        if (a <= NoData || b <= NoData || c <= NoData || d <= NoData)
            return false;
        height = (a * (1 - ax) + b * ax) * (1 - ay) + (c * (1 - ax) + d * ax) * ay;
        double edge = Math.Min(Math.Min(tx, 1 - tx), Math.Min(ty, 1 - ty));
        double t = Math.Clamp(edge / FadeShare, 0, 1);
        weight = t * t * (3 - 2 * t);
        return true;
    }

    /// <summary>Decodes a Terrarium tile from RGB(A) pixels (RGBA, 4 bytes per pixel).</summary>
    public static float[] DecodeTerrarium(ReadOnlySpan<byte> rgba)
    {
        var heights = new float[TileSize * TileSize];
        for (int i = 0; i < heights.Length; i++)
            heights[i] = rgba[i * 4] * 256f + rgba[i * 4 + 1] + rgba[i * 4 + 2] / 256f - 32768f;
        return heights;
    }
}
