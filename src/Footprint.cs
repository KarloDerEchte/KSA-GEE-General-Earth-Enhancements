namespace OsmBuildings;

/// <summary>Oriented rectangle in a local east/north frame, in meters. Angle turns the A axis from east toward north.</summary>
public readonly record struct Box2(double CenterEast, double CenterNorth, double Angle, double LengthA, double LengthB);

/// <summary>Turns building outlines into a few oriented boxes. Kept free of game types so it can be tested offline.</summary>
public static class Footprint
{
    public readonly record struct P(double X, double Y);

    /// <summary>
    /// Covers the polygon with oriented boxes: one minimum-area rectangle when it fills the outline well,
    /// otherwise the outline is cut in half across its long side and each half is covered again.
    /// </summary>
    public static List<Box2> Decompose(IReadOnlyList<P> outline, int maxDepth, double minFill)
    {
        var boxes = new List<Box2>();
        Cover(outline, maxDepth, minFill, boxes);
        return boxes;
    }

    private static void Cover(IReadOnlyList<P> poly, int depth, double minFill, List<Box2> boxes)
    {
        if (MinAreaRect(poly) is not { } box)
            return;
        double boxArea = box.LengthA * box.LengthB;
        double fill = boxArea > 0 ? Math.Abs(SignedArea(poly)) / boxArea : 1;
        if (depth <= 0 || boxArea < 60 || fill >= minFill)
        {
            boxes.Add(box);
            return;
        }

        var center = new P(box.CenterEast, box.CenterNorth);
        double angle = box.LengthA >= box.LengthB ? box.Angle : box.Angle + Math.PI / 2;
        var axis = new P(Math.Cos(angle), Math.Sin(angle));
        foreach (var half in new[] { ClipHalfPlane(poly, center, axis), ClipHalfPlane(poly, center, new P(-axis.X, -axis.Y)) })
        {
            if (half.Count >= 3 && Math.Abs(SignedArea(half)) > 4)
                Cover(half, depth - 1, minFill, boxes);
        }
    }

    public static double SignedArea(IReadOnlyList<P> poly)
    {
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += poly[j].X * poly[i].Y - poly[i].X * poly[j].Y;
        return a / 2;
    }

    /// <summary>Minimum-area enclosing rectangle via the convex hull: the best rectangle has a side on a hull edge.</summary>
    public static Box2? MinAreaRect(IReadOnlyList<P> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3)
            return null;

        Box2? best = null;
        double bestArea = double.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            P a = hull[i], b = hull[(i + 1) % hull.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
                continue;
            double ux = dx / len, uy = dy / len;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in hull)
            {
                double u = p.X * ux + p.Y * uy;
                double v = -p.X * uy + p.Y * ux;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
                best = new Box2(cu * ux - cv * uy, cu * uy + cv * ux, Math.Atan2(uy, ux), maxU - minU, maxV - minV);
            }
        }
        return best;
    }

    /// <summary>Andrew's monotone chain, counter-clockwise, without collinear points.</summary>
    public static List<P> ConvexHull(IReadOnlyList<P> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3)
            return pts;
        var hull = new P[2 * pts.Count];
        int k = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
            hull[k++] = pts[i];
        }
        for (int i = pts.Count - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
            hull[k++] = pts[i];
        }
        return hull.Take(k - 1).ToList();
    }

    private static double Cross(P o, P a, P b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Sutherland-Hodgman: keeps the part of the polygon where dot(p - origin, normal) >= 0.</summary>
    public static List<P> ClipHalfPlane(IReadOnlyList<P> poly, P origin, P normal)
    {
        var result = new List<P>(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            P cur = poly[i], next = poly[(i + 1) % poly.Count];
            double dc = (cur.X - origin.X) * normal.X + (cur.Y - origin.Y) * normal.Y;
            double dn = (next.X - origin.X) * normal.X + (next.Y - origin.Y) * normal.Y;
            if (dc >= 0)
                result.Add(cur);
            if ((dc >= 0) != (dn >= 0))
            {
                double t = dc / (dc - dn);
                result.Add(new P(cur.X + (next.X - cur.X) * t, cur.Y + (next.Y - cur.Y) * t));
            }
        }
        return result;
    }
}
