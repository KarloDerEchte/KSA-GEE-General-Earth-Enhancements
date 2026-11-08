namespace GEE;

/// <summary>The wall box a house detail is for and the tile's roads, so the door can face the nearest one.</summary>
internal sealed record HouseContext(Box2 Box, RoadProximity? Roads);

/// <summary>What a HousePiece is, so tests (and the instance builder) can tell them apart.</summary>
internal enum HousePart { Fascia, Barge, Dormer, DormerRoof, GableWindow, DoorLeaf, DoorStep, Hood, Garage }

/// <summary>
/// One box-shaped piece of house detail, in the frame of the wall box: (Ca, Cb) is the piece's centre in metres along
/// the box axes a and b, Z the height of its base above the ground (or above the wall top when OnRoof). The piece's
/// own unit-box y axis points along (Ux, Uy) (a unit vector in the a,b plane), its z axis is that turned 90 degrees
/// counter-clockwise (the handedness a to b has), SizeU and SizeV are the lengths along them, Height along up.
/// </summary>
internal readonly record struct HousePiece(HousePart Part, int Variant, double Ca, double Cb, double Z, bool OnRoof,
    double Ux, double Uy, double SizeU, double SizeV, double Height);

/// <summary>Everything the house plan needs to know about one wall box and its roof.</summary>
internal struct HouseInput
{
    public double La, Lb;
    /// <summary>Wall height above the ground at the box centre.</summary>
    public double WallHeight;
    /// <summary>Rise of this box's pitched roof (Buildings.RoofRise), 0 for a flat roof.</summary>
    public double Rise;
    public bool Hip;
    /// <summary>Gable roof variant of the building (the hip's colour comes from it); dormer roofs and the porch hood use it.</summary>
    public int RoofVariant;
    public uint Hash;
    /// <summary>The wall is the building's only box: roof trim and dormers need an undisturbed roof.</summary>
    public bool SingleBox;
    /// <summary>The wall is the biggest box of its building: doors and garages go here only.</summary>
    public bool Largest;
    /// <summary>Direction from the box centre to the nearest road, in metres along a and b; both zero when no road is near.</summary>
    public double RoadA, RoadB;
    public bool HasRoad;
}

/// <summary>
/// Plans the small relief of houses: eave fascia and gable bargeboards, dormers, a window in the gable end, the front
/// door with steps and a hood, and sometimes a garage door. Pure geometry (no game types), so it is tested offline;
/// BuildingDetail turns the pieces into instances.
/// </summary>
internal static class HouseDetail
{
    // Roof overhang beyond the wall, as Placement.MakeRoof lays it: 0.6 m along the ridge, 0.8 m across.
    public const double OverhangRidge = 0.6, OverhangAcross = 0.8;
    // The roof mesh sits this far into the wall top.
    public const double RoofSink = 0.02;
    public const double PlinthHeight = 0.8;
    public const double DoorWidth = 1.0, DoorHeight = 2.15, GarageHeight = 2.1;
    /// <summary>Dormers need a roof at least this steep (rise per metre run, tan of 23 degrees) and this long/wide.</summary>
    public const double MinDormerSlope = 0.42, MinDormerRidge = 7.5, MinDormerSpan = 6.0;
    /// <summary>Vertical thickness of a bargeboard as a share of the roof rise (tools/house_assets.py BARGE_THICKNESS).</summary>
    public const double BargeThickness = 0.07;

    public static List<HousePiece> Plan(in HouseInput h)
    {
        var pieces = new List<HousePiece>(16);
        if (h.La < 4 || h.Lb < 4 || h.WallHeight < 2.4)
            return pieces;
        if (h.Rise > 0 && h.SingleBox)
            RoofTrim(h, pieces);
        if (h.Largest)
            Entrance(h, pieces);
        return pieces;
    }

    // ---- roof frame ----

    /// <summary>Ridge and across directions of the roof over this box in a,b coordinates (Placement.MakeRoof: alongA ? (A, B) : (B, -A)).</summary>
    public static void RoofAxes(double la, double lb, out double ridgeA, out double ridgeB, out double acrossA, out double acrossB,
        out double ridgeLength, out double acrossLength)
    {
        bool alongA = la >= lb;
        (ridgeA, ridgeB) = alongA ? (1.0, 0.0) : (0.0, 1.0);
        (acrossA, acrossB) = alongA ? (0.0, 1.0) : (-1.0, 0.0);
        ridgeLength = Math.Max(la, lb);
        acrossLength = Math.Min(la, lb);
    }

    /// <summary>Roof height above the wall top at distance t from the ridge line (t up to half the roof width), for a roof of this rise.</summary>
    public static double RoofHeightAt(double rise, double halfWidth, double t) => -RoofSink + rise * (1 - t / halfWidth);

    /// <summary>
    /// How far along the ridge (from the middle) the roof surface reaches at distance t from the ridge line: a gable
    /// roof reaches the gable end everywhere, a hip roof narrows toward the ridge (its slopes end on the hip lines
    /// from the eave corners to the ridge ends).
    /// </summary>
    public static double RoofHalfLengthAt(double roofLength, double halfWidth, bool hip, double hipInset, double t)
    {
        if (!hip)
            return roofLength / 2;
        double ridgeHalf = roofLength * (0.5 - hipInset);
        return ridgeHalf + (roofLength / 2 - ridgeHalf) * Math.Clamp(t / halfWidth, 0, 1);
    }

    private static double HipInset(int roofVariant, double acrossLength, double ridgeLength)
    {
        int v = Placement.HipVariant(roofVariant, acrossLength / ridgeLength);
        if (v < Buildings.FirstHipVariant)
            return Buildings.HipInsets[0];
        return Buildings.HipInsets[(v - Buildings.FirstHipVariant) / Buildings.RoofVariants];
    }

    private static double Rand(uint hash, int k) => (LightSet.Hash(hash + (uint)k * 7919u) >> 8) / 16777216.0;

    private static void RoofTrim(in HouseInput h, List<HousePiece> pieces)
    {
        RoofAxes(h.La, h.Lb, out double ra, out double rb, out double ca, out double cb, out double ridgeLength, out double acrossLength);
        double roofLength = ridgeLength + OverhangRidge, roofWidth = acrossLength + OverhangAcross, half = roofWidth / 2;
        double rise = h.Rise;
        double inset = h.Hip ? HipInset(h.RoofVariant, acrossLength, ridgeLength) : 0;

        // (rr, cc) in the roof frame (along the ridge, across it) to the box's a,b plane.
        (double A, double B) At(double rr, double cc) => (ra * rr + ca * cc, rb * rr + cb * cc);

        // Fascia boards along both eaves, hanging a hand's breadth below the roof edge; a hip roof has them all round.
        for (int s = -1; s <= 1; s += 2)
        {
            var (fa, fb) = At(0, s * (half - 0.03));
            pieces.Add(new HousePiece(HousePart.Fascia, Buildings.TrimVariant, fa, fb, -0.2, true, ra, rb, roofLength, 0.06, 0.22));
            if (h.Hip)
            {
                var (ea, eb) = At(s * (roofLength / 2 - 0.03), 0);
                pieces.Add(new HousePiece(HousePart.Fascia, Buildings.TrimVariant, ea, eb, -0.2, true, ca, cb, roofWidth - 0.12, 0.06, 0.22));
            }
            else
            {
                // Bargeboards follow the rake of the gable end.
                var (ba, bb) = At(s * (roofLength / 2 + 0.01), 0);
                pieces.Add(new HousePiece(HousePart.Barge, Buildings.BargeVariant, ba, bb, -RoofSink, true, ra, rb, 0.07, roofWidth, rise));
            }
        }

        if (!h.Hip && rise >= 2.2 && Rand(h.Hash, 90) < 0.7)
            GableWindows(h, pieces, ra, rb, ca, cb, roofLength, half);
        if (rise / half >= MinDormerSlope && ridgeLength >= MinDormerRidge && acrossLength >= MinDormerSpan && Rand(h.Hash, 100) < 0.6)
            Dormers(h, pieces, ra, rb, ca, cb, roofLength, half, inset);
    }

    /// <summary>A small window high in the gable end, kept inside the triangle.</summary>
    private static void GableWindows(in HouseInput h, List<HousePiece> pieces, double ra, double rb, double ca, double cb, double roofLength, double half)
    {
        const double w = 0.75, hh = 0.95;
        // Highest the window top may be: the gable triangle is wider than the window plus a margin there.
        double maxTop = -RoofSink + h.Rise * (1 - (w / 2 + 0.12) / half);
        double z0 = Math.Min(0.3 + 0.12 * h.Rise, maxTop - hh);
        if (z0 < 0.2)
            return;
        int variant = Buildings.FirstDormerVariant + (int)(LightSet.Hash(h.Hash + 5) % Buildings.DormerVariants);
        for (int s = -1; s <= 1; s += 2)
        {
            // Outward direction o = s * ridge; the piece's z axis (its front) must be o, so its y axis is o turned clockwise.
            double ox = s * ra, oy = s * rb;
            double ux = oy, uy = -ox;
            pieces.Add(new HousePiece(HousePart.GableWindow, variant, s * (roofLength / 2) * ra, s * (roofLength / 2) * rb, z0, true, ux, uy, w, 0.12, hh));
        }
    }

    private static void Dormers(in HouseInput h, List<HousePiece> pieces, double ra, double rb, double ca, double cb,
        double roofLength, double half, double inset)
    {
        double rise = h.Rise, k = rise / half;
        // Which sides: the road side nearly always, the other one sometimes.
        var sides = new List<int>();
        int roadSide = h.HasRoad ? Math.Sign(h.RoadA * ca + h.RoadB * cb) : (Rand(h.Hash, 101) < 0.5 ? -1 : 1);
        if (roadSide == 0)
            roadSide = 1;
        if (Rand(h.Hash, 102) < 0.85)
            sides.Add(roadSide);
        if (Rand(h.Hash, 103) < 0.35)
            sides.Add(-roadSide);
        int variant = Buildings.FirstDormerVariant + (int)(LightSet.Hash(h.Hash + 6) % Buildings.DormerVariants);
        int sideIndex = 0;
        foreach (int s in sides)
        {
            sideIndex++;
            double wd = 1.1 + 0.5 * Rand(h.Hash, 110 + sideIndex);
            double hd = 1.25 + 0.2 * Rand(h.Hash, 120 + sideIndex);
            double rd = 0.31 * (wd + 0.3);
            double tf = half - 0.9;                                   // front wall of the dormer, from the ridge line
            // Back end of the dormer roof, where its ridge meets the main slope; shrink the dormer until it stays off the ridge.
            double tr = tf - (hd + rd) / k;
            if (tr < 0.6) { hd = 1.1; rd = 0.31 * (wd + 0.3); tr = tf - (hd + rd) / k; }
            if (tr < 0.6)
                continue;
            double tb = tf - hd / k;                                  // back of the body: the slope reaches the body top there
            double extent = RoofHalfLengthAt(roofLength, half, h.Hip, inset, tr) - (wd + 0.3) / 2 - 0.45;
            double spacing = wd + 0.3 + 1.1;
            int n = (int)Math.Min(3, Math.Floor(2 * extent / spacing) + 1);
            if (extent < 0 || n < 1)
                continue;
            if (n > 1)
                n = 1 + (int)(Rand(h.Hash, 130 + sideIndex) * n);     // 1..n
            double hf = RoofHeightAt(rise, half, tf);
            double z0 = hf - 0.12, z1 = hf + hd;
            double ox = s * ca, oy = s * cb;                           // outward direction of this side
            double ux = oy, uy = -ox;                                  // body y axis: outward turned clockwise, so its z (front) is outward
            for (int i = 0; i < n; i++)
            {
                double centre = n == 1 ? (Rand(h.Hash, 140 + sideIndex) - 0.5) * extent * 0.7 : -extent + 2 * extent * i / (n - 1);
                // Body: from the back (tb) to the front wall (tf).
                double bodyMid = (tf + tb) / 2;
                var (ba, bb) = (ra * centre + ox * bodyMid, rb * centre + oy * bodyMid);
                pieces.Add(new HousePiece(HousePart.Dormer, variant, ba, bb, z0, true, ux, uy, wd, tf - tb, z1 - z0));
                // Roof: a gable with its ridge along the outward direction, a little over the front wall.
                double front = tf + 0.15, back = tr - 0.05;
                var (qa, qb) = (ra * centre + ox * (front + back) / 2, rb * centre + oy * (front + back) / 2);
                pieces.Add(new HousePiece(HousePart.DormerRoof, h.RoofVariant, qa, qb, z1 - RoofSink, true, ox, oy, front - back, wd + 0.3, rd));
            }
        }
    }

    // ---- entrance ----

    /// <summary>
    /// The face of the box that looks at the nearest road (outward normal (nx, ny) in a,b): the axis along which the
    /// road lies furthest out in units of half the box, with the sign of that offset. Without a road the long side,
    /// on a side picked by the hash.
    /// </summary>
    public static (int Nx, int Ny) FrontFace(double la, double lb, bool hasRoad, double roadA, double roadB, uint hash)
    {
        bool alongA = la >= lb;
        if (!hasRoad)
        {
            int sign = Rand(hash, 1) < 0.5 ? -1 : 1;
            return alongA ? (0, sign) : (sign, 0);
        }
        double na = Math.Abs(roadA) / (la / 2), nb = Math.Abs(roadB) / (lb / 2);
        bool useA = na > nb || (na == nb && !alongA);   // a tie goes to the long side
        return useA ? (roadA >= 0 ? 1 : -1, 0) : (0, roadB >= 0 ? 1 : -1);
    }

    private static void Entrance(in HouseInput h, List<HousePiece> pieces)
    {
        double area = h.La * h.Lb;
        if (area < 40)
            return;
        var (nx, ny) = FrontFace(h.La, h.Lb, h.HasRoad, h.RoadA, h.RoadB, h.Hash);
        double faceLength = nx != 0 ? h.Lb : h.La;
        double faceDistance = nx != 0 ? h.La / 2 : h.Lb / 2;
        double tx = ny, ty = -nx;                                       // along the face (width direction of the pieces)
        // Piece centre for a point `along` the face and `out` metres beyond the wall plane.
        (double A, double B) P(double along, double outward) => (nx * (faceDistance + outward) + tx * along, ny * (faceDistance + outward) + ty * along);

        bool pitched = h.Rise > 0;
        // A garage door beside the front door on houses with a long enough front.
        double garageWidth = 0;
        double garageSide = Rand(h.Hash, 3) < 0.5 ? -1 : 1;
        if (pitched && area >= 70 && area <= 320 && h.WallHeight >= 2.6 && Rand(h.Hash, 4) < 0.45)
        {
            garageWidth = faceLength >= 12.5 && Rand(h.Hash, 5) < 0.4 ? 4.8 : 2.6;
            if (faceLength < garageWidth + 4.5)
                garageWidth = 0;
        }
        double doorAlong;
        if (garageWidth > 0)
        {
            double centre = garageSide * (faceLength / 2 - garageWidth / 2 - 0.7);
            var (ga, gb) = P(centre, 0.17);
            double gh = Math.Min(GarageHeight, h.WallHeight - 0.4);
            int gv = Buildings.FirstGarageVariant + (int)(LightSet.Hash(h.Hash + 8) % Buildings.GarageVariants);
            pieces.Add(new HousePiece(HousePart.Garage, gv, ga, gb, 0, false, tx, ty, garageWidth + 0.3, 0.12, gh));
            doorAlong = -garageSide * (faceLength / 2 - 1.5);
        }
        else
        {
            doorAlong = (Rand(h.Hash, 2) * 2 - 1) * Math.Max(faceLength / 2 - 1.6, 0) * 0.6;
        }

        // Door at floor level: above the plinth with two steps in front when the wall is tall enough, else on the ground.
        bool raised = h.WallHeight >= PlinthHeight + DoorHeight + 0.45;
        double floor = raised ? PlinthHeight : 0;
        double doorHeight = Math.Min(DoorHeight, h.WallHeight - floor - 0.3);
        if (doorHeight < 1.8)
            return;
        int doorVariant = Buildings.FirstDoorVariant + (int)(LightSet.Hash(h.Hash + 7) % Buildings.DoorVariants);
        var (da, db) = P(doorAlong, 0.02);
        pieces.Add(new HousePiece(HousePart.DoorLeaf, doorVariant, da, db, floor, false, tx, ty, DoorWidth, 0.14, doorHeight));
        if (raised)
        {
            var (la1, lb1) = P(doorAlong, 0.275);
            pieces.Add(new HousePiece(HousePart.DoorStep, 0, la1, lb1, 0, false, tx, ty, 1.5, 0.55, PlinthHeight));
            var (la2, lb2) = P(doorAlong, 0.8);
            pieces.Add(new HousePiece(HousePart.DoorStep, 0, la2, lb2, 0, false, tx, ty, 1.9, 0.5, PlinthHeight / 2));
        }
        // Hood over the door: a little gabled roof in the roof colour on pitched houses, else a flat slab.
        double hoodZ = floor + doorHeight + 0.15;
        var (ha, hb) = P(doorAlong, 0.5);
        if (pitched)
            pieces.Add(new HousePiece(HousePart.Hood, h.RoofVariant, ha, hb, hoodZ, false, nx, ny, 1.1, 2.0, 0.5));
        else
            pieces.Add(new HousePiece(HousePart.Hood, Buildings.TrimVariant, ha, hb, hoodZ, false, tx, ty, 2.0, 1.1, 0.12));
    }
}

/// <summary>
/// Nearest road to a point, for turning houses toward it. Segments of the tile's roads that houses face (not
/// motorways, ramps, bridges or airfield surfaces) go into a grid of 64 m cells, in metres east/north of a reference.
/// </summary>
internal sealed class RoadProximity
{
    private const double Cell = 64;
    private readonly double _lat0, _lon0, _kLat, _kLon;
    private readonly Dictionary<long, List<(double X0, double Y0, double X1, double Y1)>> _grid = new();

    public RoadProximity(IEnumerable<RawRoad> roads, double lat0, double lon0, double planetRadius = 6371000)
    {
        _lat0 = lat0;
        _lon0 = lon0;
        _kLat = Math.PI / 180 * planetRadius;
        _kLon = _kLat * Math.Cos(lat0 * Math.PI / 180);
        foreach (var road in roads)
        {
            if (road.Airfield != Airfield.None || road.Overpass || road.Bridge || road.Surface.IsHighway() || road.Surface == RoadSurface.Ramp)
                continue;
            for (int i = 1; i < road.Points.Count; i++)
            {
                double x0 = (road.Points[i - 1].Lon - lon0) * _kLon, y0 = (road.Points[i - 1].Lat - lat0) * _kLat;
                double x1 = (road.Points[i].Lon - lon0) * _kLon, y1 = (road.Points[i].Lat - lat0) * _kLat;
                int cx0 = CellOf(Math.Min(x0, x1)), cx1 = CellOf(Math.Max(x0, x1)), cy0 = CellOf(Math.Min(y0, y1)), cy1 = CellOf(Math.Max(y0, y1));
                if ((long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1) > 4096)
                    continue;
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int cy = cy0; cy <= cy1; cy++)
                    {
                        long key = Key(cx, cy);
                        if (!_grid.TryGetValue(key, out var list))
                            _grid[key] = list = new();
                        list.Add((x0, y0, x1, y1));
                    }
            }
        }
    }

    private static int CellOf(double v) => (int)Math.Floor(v / Cell);
    private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    /// <summary>Metres east/north of the reference for a position given as east/north metres from a building origin.</summary>
    public (double X, double Y) ToLocal(double lat, double lon, double east, double north) =>
        ((lon - _lon0) * _kLon + east, (lat - _lat0) * _kLat + north);

    /// <summary>Closest point of any road within maxDistance of (x, y), in the same metres, or null.</summary>
    public (double X, double Y, double Distance)? Nearest(double x, double y, double maxDistance)
    {
        int reach = (int)Math.Ceiling(maxDistance / Cell);
        int cx = CellOf(x), cy = CellOf(y);
        double best = maxDistance * maxDistance;
        (double X, double Y)? point = null;
        for (int ix = cx - reach; ix <= cx + reach; ix++)
            for (int iy = cy - reach; iy <= cy + reach; iy++)
            {
                if (!_grid.TryGetValue(Key(ix, iy), out var list))
                    continue;
                foreach (var (x0, y0, x1, y1) in list)
                {
                    double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
                    double t = len2 > 1e-9 ? Math.Clamp(((x - x0) * dx + (y - y0) * dy) / len2, 0, 1) : 0;
                    double px = x0 + dx * t, py = y0 + dy * t;
                    double d2 = (px - x) * (px - x) + (py - y) * (py - y);
                    if (d2 < best)
                    {
                        best = d2;
                        point = (px, py);
                    }
                }
            }
        return point is { } p ? (p.X, p.Y, Math.Sqrt(best)) : null;
    }
}
