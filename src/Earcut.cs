namespace GEE;

/// <summary>
/// Triangulates a polygon with holes by ear clipping, after Mapbox's earcut (ISC license) without its z-order hash:
/// the outlines here (lakes inside one vector tile) are small enough for the quadratic search.
/// </summary>
internal static class Earcut
{
    private sealed class Node(int i, double x, double y)
    {
        public readonly int I = i;
        public readonly double X = x, Y = y;
        public Node Prev = null!, Next = null!;
        public bool Steiner;
    }

    /// <summary>
    /// data holds x, y pairs: the outer ring first, then the holes; holeStarts are the vertex indices where each hole
    /// begins. Returns vertex indices, three per triangle.
    /// </summary>
    public static List<int> Triangulate(double[] data, int[] holeStarts)
    {
        var triangles = new List<int>();
        int outerLength = holeStarts.Length > 0 ? holeStarts[0] : data.Length / 2;
        Node? outer = Link(data, 0, outerLength, true);
        if (outer == null || outer.Next == outer.Prev)
            return triangles;
        if (holeStarts.Length > 0)
            outer = EliminateHoles(data, holeStarts, outer);
        Clip(outer, triangles, 0);
        return triangles;
    }

    private static Node? Link(double[] d, int start, int end, bool clockwise)
    {
        Node? last = null;
        if (clockwise == (Sum(d, start, end) > 0))
        {
            for (int i = start; i < end; i++)
                last = Insert(i, d[2 * i], d[2 * i + 1], last);
        }
        else
        {
            for (int i = end - 1; i >= start; i--)
                last = Insert(i, d[2 * i], d[2 * i + 1], last);
        }
        if (last != null && Same(last, last.Next))
        {
            Remove(last);
            last = last.Next;
        }
        return last;
    }

    private static double Sum(double[] d, int start, int end)
    {
        double sum = 0;
        for (int i = start, j = end - 1; i < end; j = i, i++)
            sum += (d[2 * j] - d[2 * i]) * (d[2 * i + 1] + d[2 * j + 1]);
        return sum;
    }

    private static Node? Filter(Node? start, Node? end = null)
    {
        if (start == null)
            return null;
        end ??= start;
        Node p = start;
        bool again;
        do
        {
            again = false;
            if (!p.Steiner && (Same(p, p.Next) || Area(p.Prev, p, p.Next) == 0))
            {
                Remove(p);
                p = end = p.Prev;
                if (p == p.Next)
                    break;
                again = true;
            }
            else
            {
                p = p.Next;
            }
        } while (again || p != end);
        return end;
    }

    private static void Clip(Node? ear, List<int> triangles, int pass)
    {
        if (ear == null)
            return;
        Node stop = ear;
        while (ear!.Prev != ear.Next)
        {
            Node prev = ear.Prev, next = ear.Next;
            if (IsEar(ear))
            {
                triangles.Add(prev.I);
                triangles.Add(ear.I);
                triangles.Add(next.I);
                Remove(ear);
                ear = next.Next;
                stop = next.Next;
                continue;
            }
            ear = next;
            if (ear == stop)
            {
                if (pass == 0)
                    Clip(Filter(ear), triangles, 1);
                else if (pass == 1)
                    Clip(CureLocalIntersections(Filter(ear)!, triangles), triangles, 2);
                else if (pass == 2)
                    SplitAndClip(ear, triangles);
                break;
            }
        }
    }

    private static bool IsEar(Node ear)
    {
        Node a = ear.Prev, b = ear, c = ear.Next;
        if (Area(a, b, c) >= 0)
            return false;
        for (Node p = c.Next; p != a; p = p.Next)
        {
            if (InTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
                return false;
        }
        return true;
    }

    private static Node CureLocalIntersections(Node start, List<int> triangles)
    {
        Node p = start;
        do
        {
            Node a = p.Prev, b = p.Next.Next;
            if (!Same(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
            {
                triangles.Add(a.I);
                triangles.Add(p.I);
                triangles.Add(b.I);
                Remove(p);
                Remove(p.Next);
                p = start = b;
            }
            p = p.Next;
        } while (p != start);
        return Filter(p)!;
    }

    private static void SplitAndClip(Node start, List<int> triangles)
    {
        Node a = start;
        do
        {
            Node b = a.Next.Next;
            while (b != a.Prev)
            {
                if (a.I != b.I && ValidDiagonal(a, b))
                {
                    Node c = Split(a, b);
                    a = Filter(a, a.Next)!;
                    c = Filter(c, c.Next)!;
                    Clip(a, triangles, 0);
                    Clip(c, triangles, 0);
                    return;
                }
                b = b.Next;
            }
            a = a.Next;
        } while (a != start);
    }

    private static Node EliminateHoles(double[] d, int[] holeStarts, Node outer)
    {
        var queue = new List<Node>();
        for (int i = 0; i < holeStarts.Length; i++)
        {
            int start = holeStarts[i], end = i < holeStarts.Length - 1 ? holeStarts[i + 1] : d.Length / 2;
            Node? list = Link(d, start, end, false);
            if (list == null)
                continue;
            if (list == list.Next)
                list.Steiner = true;
            queue.Add(Leftmost(list));
        }
        queue.Sort((a, b) => a.X.CompareTo(b.X));
        foreach (Node hole in queue)
            outer = EliminateHole(hole, outer);
        return outer;
    }

    private static Node EliminateHole(Node hole, Node outer)
    {
        Node? bridge = FindBridge(hole, outer);
        if (bridge == null)
            return outer;
        Node reverse = Split(bridge, hole);
        Filter(reverse, reverse.Next);
        return Filter(bridge, bridge.Next)!;
    }

    private static Node? FindBridge(Node hole, Node outer)
    {
        Node p = outer;
        double hx = hole.X, hy = hole.Y, qx = double.NegativeInfinity;
        Node? m = null;
        do
        {
            if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
            {
                double x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
                if (x <= hx && x > qx)
                {
                    qx = x;
                    m = p.X < p.Next.X ? p : p.Next;
                    if (x == hx)
                        return m;
                }
            }
            p = p.Next;
        } while (p != outer);
        if (m == null)
            return null;

        Node stop = m;
        double mx = m.X, my = m.Y, tanMin = double.PositiveInfinity;
        p = m;
        do
        {
            if (hx >= p.X && p.X >= mx && hx != p.X
                && InTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
            {
                double tan = Math.Abs(hy - p.Y) / (hx - p.X);
                if (LocallyInside(p, hole) && (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContains(m, p))))))
                {
                    m = p;
                    tanMin = tan;
                }
            }
            p = p.Next;
        } while (p != stop);
        return m;
    }

    private static bool SectorContains(Node m, Node p) => Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;

    private static Node Leftmost(Node start)
    {
        Node p = start, leftmost = start;
        do
        {
            if (p.X < leftmost.X || (p.X == leftmost.X && p.Y < leftmost.Y))
                leftmost = p;
            p = p.Next;
        } while (p != start);
        return leftmost;
    }

    private static bool InTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
        => (cx - px) * (ay - py) >= (ax - px) * (cy - py)
        && (ax - px) * (by - py) >= (bx - px) * (ay - py)
        && (bx - px) * (cy - py) >= (cx - px) * (by - py);

    private static bool ValidDiagonal(Node a, Node b)
        => a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b)
        && ((LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b)
                && (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0))
            || (Same(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0));

    private static double Area(Node p, Node q, Node r) => (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);

    private static bool Same(Node a, Node b) => a.X == b.X && a.Y == b.Y;

    private static int Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;

    private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        int o1 = Sign(Area(p1, q1, p2)), o2 = Sign(Area(p1, q1, q2));
        int o3 = Sign(Area(p2, q2, p1)), o4 = Sign(Area(p2, q2, q1));
        if (o1 != o2 && o3 != o4)
            return true;
        if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
        if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
        if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
        if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
        return false;
    }

    private static bool OnSegment(Node p, Node q, Node r)
        => q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);

    private static bool IntersectsPolygon(Node a, Node b)
    {
        Node p = a;
        do
        {
            if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b))
                return true;
            p = p.Next;
        } while (p != a);
        return false;
    }

    private static bool LocallyInside(Node a, Node b)
        => Area(a.Prev, a, a.Next) < 0
            ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0
            : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;

    private static bool MiddleInside(Node a, Node b)
    {
        Node p = a;
        bool inside = false;
        double px = (a.X + b.X) / 2, py = (a.Y + b.Y) / 2;
        do
        {
            if (p.Y > py != p.Next.Y > py && p.Next.Y != p.Y && px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X)
                inside = !inside;
            p = p.Next;
        } while (p != a);
        return inside;
    }

    private static Node Split(Node a, Node b)
    {
        var a2 = new Node(a.I, a.X, a.Y);
        var b2 = new Node(b.I, b.X, b.Y);
        Node an = a.Next, bp = b.Prev;
        a.Next = b;
        b.Prev = a;
        a2.Next = an;
        an.Prev = a2;
        b2.Next = a2;
        a2.Prev = b2;
        bp.Next = b2;
        b2.Prev = bp;
        return b2;
    }

    private static Node Insert(int i, double x, double y, Node? last)
    {
        var p = new Node(i, x, y);
        if (last == null)
        {
            p.Prev = p;
            p.Next = p;
        }
        else
        {
            p.Next = last.Next;
            p.Prev = last;
            last.Next.Prev = p;
            last.Next = p;
        }
        return p;
    }

    private static void Remove(Node p)
    {
        p.Next.Prev = p.Prev;
        p.Prev.Next = p.Next;
    }
}
