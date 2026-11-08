using Brutal.Numerics;

namespace GEE;

/// <summary>
/// Corner fillets, crosswalks and stop lines where roads meet. Runs after the roads of a tile are placed and uses the
/// junction points RoadJunctions found (RawRoad.Clear &gt; 0), so the cut and the unmarked pieces within the clear stretch
/// stay as they are:
/// - Curb-radius fillets: between two neighbouring arms the wedge outside both roads is filled with a fan of a few
///   plain-asphalt slabs reaching to a circular curb arc. They lie a hair below the roads' own surface, so wherever
///   they overlap a road the road covers them and only the rounded corner shows.
/// - Crosswalk (zebra stripes) and, on the approach lane (right-hand traffic) of minor or ending roads, a stop line,
///   both just outside the clear stretch and the fillets, as thin white slabs (Buildings.JunctionPaintVariant). Only
///   in built-up areas (BuiltUp), they are details: no collision, no clutter clearing.
/// Everything is flat plane fits to the exact terrain (five samples per plane), so no heavy meshes are involved.
/// </summary>
public static class JunctionMarkings
{
    /// <summary>Buildings (240 m around) from which a junction counts as built up and gets crosswalks and stop lines.</summary>
    private const int BuiltUpBuildings = 5;
    private const double MinArmDegrees = 20, MinFilletDegrees = 35, MaxFilletDegrees = 150;
    private const double CrosswalkLength = 3.0, StripeWidth = 0.5, StripePitch = 1.0;
    private const double StopGap = 0.7, StopWidth = 0.45;
    private const double PaintLift = 0.035, PaintThickness = 0.12, FilletBelow = 0.012;
    private const double MaxReach = 14;

    private sealed class Arm
    {
        public RawRoad Road = null!;
        public bool Ends;
        public double Angle, Dx, Dy, Width, Straight, Clear, Reach;
        public bool Eligible;
        public double Nx => -Dy;
        public double Ny => Dx;
    }

    private sealed class Frame
    {
        public double3 Dir, East, North;
        public double Radius;
        public Func<double3, double> Terrain = null!;
        public double3 Ground(double x, double y) => double3.Normalize(Dir * Radius + East * x + North * y);
        public double Height(double x, double y) => Math.Max(Terrain(Ground(x, y)), 0);
    }

    private readonly record struct Plane(double H0, double Gx, double Gy, double Cx, double Cy)
    {
        public double At(double x, double y) => H0 + Gx * (x - Cx) + Gy * (y - Cy);
    }

    public static void Add(List<RawRoad> roads, BuiltUp? builtUp, double radius, Func<double3, double> terrain,
        Action<BuildingInstance> add, bool fillets, bool markings)
    {
        if (!fillets && !markings)
            return;
        var nodes = new Dictionary<(double Lat, double Lon), List<(RawRoad Road, int Point)>>();
        foreach (var road in roads)
        {
            if (road.Clear == null || road.Airfield != Airfield.None || road.Bridge)
                continue;
            for (int k = 0; k < road.Points.Count; k++)
                if (road.Clear[k] > 0)
                {
                    if (!nodes.TryGetValue(road.Points[k], out var list))
                        nodes[road.Points[k]] = list = new List<(RawRoad, int)>(3);
                    list.Add((road, k));
                }
        }
        foreach (var (at, members) in nodes)
        {
            var arms = BuildArms(at, members, radius);
            if (arms == null)
                continue;
            var frame = new Frame { Dir = Placement.DirCcf(at.Lat, at.Lon), Radius = radius, Terrain = terrain };
            Placement.Axes(frame.Dir, out frame.East, out frame.North);
            bool paint = markings && builtUp != null && builtUp.Around(at.Lat, at.Lon) >= BuiltUpBuildings;
            if (fillets)
                AddFillets(frame, arms, add);
            if (paint)
                AddPaint(frame, arms, add);
        }
    }

    /// <summary>The arms of one junction point sorted by angle, or null where the point is no usable junction.</summary>
    private static List<Arm>? BuildArms((double Lat, double Lon) at, List<(RawRoad Road, int Point)> members, double radius)
    {
        double k = Math.PI / 180 * radius, kLon = k * Math.Cos(at.Lat * Math.PI / 180);
        var arms = new List<Arm>(4);
        foreach (var (road, point) in members)
            foreach (int step in new[] { -1, 1 })
            {
                int first = point + step;
                if (first < 0 || first >= road.Points.Count)
                    continue;
                // A junction only holds what goes on straight for a while: the arm is the first segment and the
                // points that stay near its line.
                (double X, double Y) P(int i) => ((road.Points[i].Lon - at.Lon) * kLon, (road.Points[i].Lat - at.Lat) * k);
                var (fx, fy) = P(first);
                double len = Math.Sqrt(fx * fx + fy * fy);
                if (len < 0.3)
                    return null;
                double dx = fx / len, dy = fy / len, straight = len;
                int i = first + step;
                bool open = true;
                for (; i >= 0 && i < road.Points.Count; i += step)
                {
                    var (px, py) = P(i);
                    if (Math.Abs(dx * py - dy * px) > 0.6)
                    {
                        open = false;
                        break;
                    }
                    straight = Math.Max(straight, dx * px + dy * py);
                }
                bool ends = point == 0 || point == road.Points.Count - 1;
                arms.Add(new Arm
                {
                    Road = road,
                    Ends = ends,
                    Angle = Math.Atan2(dy, dx),
                    Dx = dx,
                    Dy = dy,
                    Width = road.Width,
                    Straight = open ? straight : len,
                    Clear = road.Clear![point],
                    Eligible = road.Surface is RoadSurface.Road or RoadSurface.Street && road.Width >= 3.5 && (road.Rank >= 1 || road.Width >= 5),
                });
            }
        if (arms.Count < 3)
            return null;
        arms.Sort((a, b) => a.Angle.CompareTo(b.Angle));
        for (int i = 0; i < arms.Count; i++)
        {
            double gap = (arms[(i + 1) % arms.Count].Angle - arms[i].Angle + 2 * Math.PI) % (2 * Math.PI);
            if (gap < MinArmDegrees * Math.PI / 180)
                return null;
        }
        return arms;
    }

    private static Plane Fit(Frame f, double cx, double cy, double half)
    {
        double e = f.Height(cx + half, cy), w = f.Height(cx - half, cy), n = f.Height(cx, cy + half), s = f.Height(cx, cy - half);
        double c = f.Height(cx, cy);
        return new Plane((c + e + w + n + s) / 5, (e - w) / (2 * half), (n - s) / (2 * half), cx, cy);
    }

    /// <summary>
    /// A slab centred at (cx, cy) on the plane, length along (ux, uy) and width to its left, whose top lies top metres
    /// above the plane and which is thickness deep. Sheared to the plane like the road pieces, so its sides stay vertical.
    /// </summary>
    private static BuildingInstance Slab(Frame f, Plane plane, double cx, double cy, double ux, double uy, double length, double width,
        double top, double thickness, int variant, bool detail)
    {
        double vx = -uy, vy = ux;
        double h = plane.At(cx, cy);
        double3 up = f.Ground(cx, cy);
        double3 a = f.East * ux + f.North * uy, b = f.East * vx + f.North * vy;
        double gA = plane.Gx * ux + plane.Gy * uy, gB = plane.Gx * vx + plane.Gy * vy;
        return new BuildingInstance
        {
            Up = up * thickness,
            AxisA = a * length + up * (gA * length),
            AxisB = b * width + up * (gB * width),
            Base = up * (f.Radius + h + top - thickness),
            Center = up * (f.Radius + h + top),
            Variant = variant,
            IsRoad = true,
            IsDetail = detail,
            Layer = Layers.Street,
            Style = Buildings.StyleNone,
        };
    }

    private static void AddFillets(Frame f, List<Arm> arms, Action<BuildingInstance> add)
    {
        double lowest = arms.Min(a => a.Road.Lift) - FilletBelow;
        for (int i = 0; i < arms.Count; i++)
        {
            Arm a = arms[i], b = arms[(i + 1) % arms.Count];
            if (!a.Eligible || !b.Eligible)
                continue;
            double phi = (b.Angle - a.Angle + 2 * Math.PI) % (2 * Math.PI);
            if (phi < MinFilletDegrees * Math.PI / 180 || phi > MaxFilletDegrees * Math.PI / 180)
                continue;
            double ha = a.Width / 2, hb = b.Width / 2, sin = Math.Sin(phi);
            // The corner where the left edge of a meets the right edge of b.
            double rx = -hb * b.Nx - ha * a.Nx, ry = -hb * b.Ny - ha * a.Ny;
            double t = (rx * b.Dy - ry * b.Dx) / sin, u = (a.Dy * rx - a.Dx * ry) / sin;
            if (t < 0 || u < 0)
                continue;
            double cx = ha * a.Nx + t * a.Dx, cy = ha * a.Ny + t * a.Dy;
            double tan = Math.Tan(phi / 2);
            double radius = Math.Clamp(Math.Min(a.Width, b.Width) * 0.6, 3, 6);
            double tangent = radius / tan;
            double room = Math.Min(Math.Min(a.Straight - t, b.Straight - u), 10);
            if (tangent > room)
            {
                tangent = room;
                radius = tangent * tan;
            }
            if (radius < 1.5)
                continue;
            a.Reach = Math.Max(a.Reach, t + tangent);
            b.Reach = Math.Max(b.Reach, u + tangent);

            // Fan from the corner to the arc: slab k points along ray angle alpha from the bisector, reaches the arc
            // and is as wide as the sector there.
            double bx = a.Dx + b.Dx, by = a.Dy + b.Dy, bl = Math.Sqrt(bx * bx + by * by);
            bx /= bl;
            by /= bl;
            double centre = radius / Math.Sin(phi / 2);
            int n = Math.Max(3, (int)Math.Ceiling(phi / (25 * Math.PI / 180)));
            double step = phi / n;
            var plane = Fit(f, cx + bx * centre * 0.5, cy + by * centre * 0.5, 3);
            Arm wide = a.Width >= b.Width ? a : b;
            int variant = Buildings.PlainVariant(wide.Road.Surface);
            for (int k = 0; k < n; k++)
            {
                double alpha = -phi / 2 + (k + 0.5) * step;
                double reach = centre * Math.Cos(alpha) - Math.Sqrt(Math.Max(radius * radius - centre * centre * Math.Sin(alpha) * Math.Sin(alpha), 0));
                if (reach < 0.2)
                    continue;
                double ca = Math.Cos(alpha), sa = Math.Sin(alpha);
                double ux = bx * ca - by * sa, uy = bx * sa + by * ca;
                double width = 2 * reach * Math.Tan(step / 2) + 0.2;
                // A few millimetres between overlapping slabs keep them from flickering.
                add(Slab(f, plane, cx + ux * reach / 2, cy + uy * reach / 2, ux, uy, reach, width,
                    lowest - (k & 1) * 0.004, 1.5, variant, false));
            }
        }
    }

    private static void AddPaint(Frame f, List<Arm> arms, Action<BuildingInstance> add)
    {
        int maxRank = arms.Where(a => a.Eligible).Select(a => a.Road.Rank).DefaultIfEmpty(0).Max();
        bool allEqual = arms.Where(a => a.Eligible).All(a => a.Road.Rank == maxRank);
        for (int i = 0; i < arms.Count; i++)
        {
            Arm arm = arms[i];
            if (!arm.Eligible || arm.Width < 4)
                continue;
            // Clear of the other roads' pavement: where the arm's edges leave their strips.
            double clear = arm.Clear;
            for (int j = 0; j < arms.Count; j++)
            {
                if (j == i)
                    continue;
                double cos = arm.Dx * arms[j].Dx + arm.Dy * arms[j].Dy, sin = Math.Abs(arm.Dx * arms[j].Dy - arm.Dy * arms[j].Dx);
                if (sin > 0.2)
                    clear = Math.Max(clear, (arms[j].Width / 2 + arm.Width / 2 * Math.Abs(cos)) / sin);
            }
            double start = Math.Max(Math.Min(clear, MaxReach), arm.Reach) + 0.3;
            bool stop = arm.Width >= 4.5 && (arm.Ends || arm.Road.Rank < maxRank || allEqual);
            double end = start + CrosswalkLength + (stop ? StopGap + StopWidth : 0);
            if (arm.Straight < end + 1)
                continue;

            double half = arm.Width / 2;
            var plane = Fit(f, arm.Dx * (start + 2.5), arm.Dy * (start + 2.5), Math.Max(3, Math.Min(half, 4)));
            double top = arm.Road.Lift + PaintLift;
            int paint = Buildings.JunctionPaintVariant;

            // Zebra stripes along the road, centred across it.
            int count = (int)Math.Floor((arm.Width - 1.4) / StripePitch);
            double along = start + CrosswalkLength / 2;
            for (int s = 0; s < count; s++)
            {
                double lateral = (s - (count - 1) / 2.0) * StripePitch;
                add(Slab(f, plane, arm.Dx * along + arm.Nx * lateral, arm.Dy * along + arm.Ny * lateral, arm.Dx, arm.Dy,
                    CrosswalkLength, StripeWidth, top, PaintThickness, paint, true));
            }

            // Stop line across the approach lane: traffic comes toward the junction, so its right is the arm's left.
            if (stop)
            {
                double width = half - 0.95;
                if (width < 1)
                    continue;
                double at = start + CrosswalkLength + StopGap + StopWidth / 2, lateral = 0.45 + width / 2;
                add(Slab(f, plane, arm.Dx * at + arm.Nx * lateral, arm.Dy * at + arm.Ny * lateral, arm.Dx, arm.Dy,
                    StopWidth, width, top, PaintThickness, paint, true));
            }
        }
    }
}
