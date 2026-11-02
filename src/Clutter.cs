using System.Numerics;
using Brutal.Numerics;

namespace OsmBuildings;

/// <summary>Cell grid of one ground clutter ecotype (trees, shrubs, grass) and how far to keep it off our footprints.</summary>
public readonly record struct ClutterGrid(int Ecotype, int Resolution, double ClearanceMeters);

/// <summary>One cell of an ecotype grid on a cube face, as the game's CubeCellGrid.Cell.</summary>
public readonly record struct ClutterCellKey(int Ecotype, int Face, int X, int Y);

/// <summary>
/// Finds the ground clutter instances that stand on roads or inside buildings. The game places clutter on
/// the GPU (Shaders/Planet/GroundClutter/Generate.comp): every cell of a cube sphere grid holds 16 x 16
/// sub-cells, each with one candidate instance jittered by a hash of its cell and sub-cell. That placement
/// is replicated here exactly, so the matching sub-cells can be switched off through the game's own
/// exclusion masks (the ones it uses for trees knocked over by vessels). Uses no game types, so it can be
/// tested offline.
/// </summary>
public static class Clutter
{
    /// <summary>Sync with NUM_OBJECTS_PER_CELL_EDGE and CLUTTER_JITTER_STRENGTH in GroundClutterCommon.glsl.</summary>
    public const int Edge = 16;
    private const double JitterStrength = 2.0;

    /// <summary>Same as CubeCellGrid.UpdateGridProperties: cells of about 16 object separations per face quarter.</summary>
    public static int GridResolution(double sphereRadius, double objectSeparation) =>
        (int)Math.Ceiling(Math.PI * 2 * sphereRadius / 4 / (16 * objectSeparation));

    /// <summary>
    /// Adds every clutter instance inside one of the instances' ground rectangles (grown by the grid's
    /// clearance) to masks, one bit per sub-cell (bit index = subCellY * 16 + subCellX, as subCellId).
    /// </summary>
    public static void Collect(ReadOnlySpan<BuildingInstance> instances, IReadOnlyList<ClutterGrid> grids, double radius,
        Dictionary<ClutterCellKey, uint[]> masks)
    {
        foreach (var grid in grids)
        {
            foreach (ref readonly var b in instances)
            {
                if (!b.IsDetail)
                    CollectRect(b, grid, radius, masks);
            }
        }
    }

    private static void CollectRect(in BuildingInstance b, ClutterGrid grid, double radius, Dictionary<ClutterCellKey, uint[]> masks)
    {
        // Ground rectangle: around the box's base point, along its two horizontal axes.
        double3 c = double3.Normalize(b.Base);
        double3 a = b.AxisA - c * double3.Dot(b.AxisA, c);
        double3 s = b.AxisB - c * double3.Dot(b.AxisB, c);
        double lengthA = Length(a), lengthB = Length(s);
        if (lengthA < 1e-6 || lengthB < 1e-6)
            return;
        a *= 1 / lengthA;
        s *= 1 / lengthB;
        double halfA = lengthA / 2 + grid.ClearanceMeters, halfB = lengthB / 2 + grid.ClearanceMeters;

        Span<double3> corners = stackalloc double3[5];
        corners[0] = c;
        for (int k = 0; k < 4; k++)
        {
            double sa = (k & 1) == 0 ? -1 : 1, sb = (k & 2) == 0 ? -1 : 1;
            corners[k + 1] = double3.Normalize(c + (a * (sa * halfA) + s * (sb * halfB)) / radius);
        }

        int faces = 0;
        foreach (var corner in corners)
            faces |= 1 << FaceOf(corner);

        int subRes = grid.Resolution * Edge;
        for (int face = 0; face < 6; face++)
        {
            if ((faces & (1 << face)) == 0)
                continue;
            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var corner in corners)
            {
                var (u, v) = ProjectToFace(face, corner);
                uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u);
                vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
            }
            // Instances are jittered by up to one sub-cell from their sub-cell centre.
            int sx0 = Math.Max((int)Math.Floor(uMin * subRes) - 2, 0), sx1 = Math.Min((int)Math.Floor(uMax * subRes) + 2, subRes - 1);
            int sy0 = Math.Max((int)Math.Floor(vMin * subRes) - 2, 0), sy1 = Math.Min((int)Math.Floor(vMax * subRes) + 2, subRes - 1);
            for (int sy = sy0; sy <= sy1; sy++)
            {
                // Rows run against v within a cell: the shader's local y grows from the top edge.
                int cellY = sy / Edge, localY = Edge - 1 - sy % Edge;
                for (int sx = sx0; sx <= sx1; sx++)
                {
                    int cellX = sx / Edge, localX = sx % Edge;
                    double3 d = InstanceDirection(face, cellX, cellY, localX, localY, grid.Resolution);
                    double3 rel = (d - c) * radius;
                    if (Math.Abs(double3.Dot(rel, a)) > halfA || Math.Abs(double3.Dot(rel, s)) > halfB || double3.Dot(d, c) < 0.99)
                        continue;
                    var key = new ClutterCellKey(grid.Ecotype, face, cellX, cellY);
                    if (!masks.TryGetValue(key, out uint[]? mask))
                        masks[key] = mask = new uint[Edge * Edge / 32];
                    int bit = localY * Edge + localX;
                    mask[bit >> 5] |= 1u << (bit & 31);
                }
            }
        }
    }

    /// <summary>Unit CCF direction of a sub-cell's instance, as Generate.comp and GetCellInstanceDirection compute it.</summary>
    public static double3 InstanceDirection(int face, int cellX, int cellY, int localX, int localY, int resolution)
    {
        uint cellSeed = unchecked((uint)cellX * 0x1f123bb5u ^ (uint)cellY * 0x5bd1e995u);
        uint mixedX = unchecked((uint)localX * 17u + (uint)localY * 131u);
        uint mixedY = unchecked((uint)localX * 269u + (uint)localY * 23u);
        uint hx = mixedX ^ cellSeed, hy = mixedY ^ unchecked(cellSeed * 0x9e3779b9u);
        const float inv = 1.0f / 4294967295.0f;
        float jitterX = Pcg32(hx ^ 0x7f4a7c15u, hy ^ 0x7f4a7c15u) * inv;
        float jitterY = Pcg32(hx ^ 0x2545f491u, hy ^ 0x2545f491u) * inv;

        double localU = (localX + 0.5 + (jitterX - 0.5) * JitterStrength) / Edge;
        double localV = (localY + 0.5 + (jitterY - 0.5) * JitterStrength) / Edge;
        double u = (cellX + localU) / resolution;
        double v = (cellY + 1 - localV) / resolution;
        return QscUvToDirection(face, u, v);
    }

    private static uint Pcg32(uint vx, uint vy)
    {
        int rot = (int)(((vx >> 28) + (vy >> 2)) & 31u);
        uint x = unchecked(vx * 1664525u + 1013904223u);
        x ^= x >> 16;
        x = unchecked(x * 2246822519u);
        return BitOperations.RotateLeft(x, rot);
    }

    /// <summary>Same face choice as CubeCellGrid.DirectionToQscUv.</summary>
    public static int FaceOf(double3 d)
    {
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
        if (ax >= ay && ax >= az)
            return d.X > 0 ? 0 : 1;
        if (ay >= ax && ay >= az)
            return d.Y > 0 ? 2 : 3;
        return d.Z > 0 ? 4 : 5;
    }

    /// <summary>Face UV of a direction on a given face (CubeCellGrid.DirectionToQscUv without picking the face).</summary>
    public static (double U, double V) ProjectToFace(int face, double3 d)
    {
        double x = d.X, y = d.Y, z = d.Z;
        (double u, double v) = face switch
        {
            0 => (z / x, y / x),
            1 => (-z / x, y / x),
            2 => (x / y, -z / y),
            3 => (x / y, z / y),
            4 => (x / z, y / z),
            _ => (-x / z, y / z),
        };
        return ((Math.Atan(u) / (Math.PI / 4) + 1) / 2, (Math.Atan(v) / (Math.PI / 4) + 1) / 2);
    }

    /// <summary>Same as CubeCellGrid.QscUvToDirection.</summary>
    public static double3 QscUvToDirection(int face, double u, double v)
    {
        double tu = Math.Tan((u * 2 - 1) * (Math.PI / 4)), tv = Math.Tan((v * 2 - 1) * (Math.PI / 4));
        double3 d = face switch
        {
            0 => new double3(1, tv, tu),
            1 => new double3(-1, -tv, tu),
            2 => new double3(tu, 1, -tv),
            3 => new double3(-tu, -1, -tv),
            4 => new double3(tu, tv, 1),
            _ => new double3(tu, -tv, -1),
        };
        return double3.Normalize(d);
    }

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));
}
