using System.Reflection;

namespace GEE;

/// <summary>
/// Procedural massing for tall buildings (roof 35 m and up): a podium at the full footprint with a slimmer shaft,
/// or stepped setbacks, topped by a mechanical penthouse (a second cap on the really tall ones) and an antenna mast
/// on the tallest. Each part is its own box with its own RawBuilding (Height = top, MinHeight = bottom, Style kept
/// for the shafts), so the facade windows and lights treat it like any other building.
///
/// The parts are regular instances, not BuildingDetail pieces: they change the silhouette, which is what a tower
/// shows from kilometres away, while details are culled beyond DetailDistanceKm. A tower adds at most 7 instances.
/// The geometry (Plan) is pure and deterministic from the building seed; Parts wraps it for Tiles.
/// </summary>
internal static class TowerMassing
{
    public const double MinHeight = 35;
    /// <summary>Upper parts start this far below the roof of the part under them, so no gap shows on sloping ground.</summary>
    public const double Overlap = 2;
    /// <summary>A flush upper tier sits this far inside the wall below, so the two walls never share a plane.</summary>
    public const double FlushGap = 0.15;
    /// <summary>Facade variant of the antenna mast (BuildingDetail's metal).</summary>
    public const int MastVariant = 6;

    public enum Kind { Podium, Shaft, Crown, Mast }

    /// <summary>One box: centre offset (along the box axes A and B) and size, bottom and top above the building's ground.</summary>
    public readonly record struct Tier(Kind Kind, double CA, double CB, double SA, double SB, double Bottom, double Top);

    public readonly record struct Part(RawBuilding Building, Box2 Box, bool IsMain);

    public static bool Eligible(RawBuilding b) =>
        b.Id >= 0 && !b.LaunchComplex && !b.Slab && b.MinHeight <= 0 && b.Height >= MinHeight &&
        b.RoofHeight <= 0 && b.RoofPitch <= 0 && Buildings.IsFacade(b.Variant) &&
        b.Variant != Buildings.TankVariant && b.Variant != Buildings.HorizontalTankVariant && b.Boxes.Count is >= 1 and <= 3;

    /// <summary>The seed MakeInstance gives the building, so the plan does not change between runs.</summary>
    public static uint SeedOf(RawBuilding b) => LightSet.Hash(b.Id, (long)(b.Lat * 1e6), (long)(b.Lon * 1e6));

    /// <summary>Tiers for a box of la x lb metres and a roof at height. Empty: leave the building a plain box.</summary>
    public static List<Tier> Plan(double la, double lb, double height, uint seed)
    {
        var tiers = new List<Tier>();
        double minSide = Math.Min(la, lb);
        if (height < MinHeight || minSide < 10 || la * lb < 150)
            return tiers;
        double Rand(int k) => (LightSet.Hash(seed + (uint)k * 7919u + 1013u) >> 8) / 16777216.0;

        // Crown first: it fixes where the shaft ends so the whole stays at the OSM height.
        double crownH = Math.Min(2.8 + 0.04 * height, 8);
        double shaftTop = height - crownH;

        double ca = 0, cb = 0, sa = la, sb = lb, bottom = 0;
        // Podium: full footprint for 3-5 floors, the shaft inset from it (flush with one side or centred).
        if (minSide >= 22 && height >= 45 && Rand(1) < 0.55)
        {
            double ph = Math.Min(10 + 8 * Rand(2), 0.28 * height);
            double insetA = Math.Min(Math.Max(3, la * (0.12 + 0.13 * Rand(3))), (la - 14) / 2);
            double insetB = Math.Min(Math.Max(3, lb * (0.12 + 0.13 * Rand(4))), (lb - 14) / 2);
            if (insetA >= 2 && insetB >= 2)
            {
                tiers.Add(new Tier(Kind.Podium, 0, 0, la, lb, 0, ph));
                (sa, sb) = (la - 2 * insetA, lb - 2 * insetB);
                (ca, cb) = (Shift(la, sa, Rand(5)), Shift(lb, sb, Rand(6)));
                bottom = ph - Overlap;
            }
        }

        // Setbacks (a slimmer upper shaft is the one-step case): each tier is a bit smaller than the one below.
        int steps = 0;
        if (Rand(7) < 0.6)
            steps = height >= 140 && minSide >= 24 ? 2 : height >= 60 ? 1 : 0;
        double start = bottom;
        for (int s = 0; s < steps; s++)
        {
            double f = (s == 0 ? 0.55 : 0.78) + 0.12 * Rand(10 + s);
            double top = Math.Max(shaftTop * f, start + 12);
            double ns = s == 0 && steps == 1 ? 0.62 + 0.14 * Rand(12) : 0.78 + 0.1 * Rand(13 + s);
            double nsa = Math.Max(sa * ns, 8), nsb = Math.Max(sb * ns, 8);
            if (top >= shaftTop - 8 || nsa > sa - 2 || nsb > sb - 2)
                break;
            tiers.Add(new Tier(Kind.Shaft, ca, cb, sa, sb, bottom, top));
            ca += Shift(sa, nsa, Rand(16 + s));
            cb += Shift(sb, nsb, Rand(18 + s));
            (sa, sb) = (nsa, nsb);
            bottom = top - Overlap;
            start = top;
        }
        tiers.Add(new Tier(Kind.Shaft, ca, cb, sa, sb, bottom, shaftTop));

        // Penthouse: 50-70 % of the top tier, anywhere inside its roof; the tall ones get a second smaller cap.
        double pa = Math.Max(sa * (0.5 + 0.2 * Rand(20)), 4), pb = Math.Max(sb * (0.5 + 0.2 * Rand(21)), 4);
        double pca = ca + Shift(sa, pa, Rand(22), true), pcb = cb + Shift(sb, pb, Rand(23), true);
        tiers.Add(new Tier(Kind.Crown, pca, pcb, pa, pb, shaftTop - 1, height));
        double mastBase = height;
        double mca = pca, mcb = pcb;
        if (height >= 80 && pa >= 8 && pb >= 8)
        {
            double capA = pa * 0.55, capB = pb * 0.55, capH = 2.5 + 0.015 * height;
            mca = pca + Shift(pa, capA, Rand(24), true);
            mcb = pcb + Shift(pb, capB, Rand(25), true);
            tiers.Add(new Tier(Kind.Crown, mca, mcb, capA, capB, height - 1, height + capH));
            mastBase = height + capH;
        }
        // Antenna mast on the tallest.
        if (height >= 90 || (height >= 60 && Rand(26) < 0.3))
        {
            double w = height >= 150 ? 1.4 : 0.9, mh = Math.Clamp(0.1 * height, 6, 45);
            tiers.Add(new Tier(Kind.Mast, mca, mcb, w, w, mastBase - 1, mastBase + mh));
        }
        return tiers;
    }

    /// <summary>
    /// Offset of an inner extent (size inner) inside an outer one, by the random value r: centred or pushed against
    /// one edge (leaving FlushGap); with any = true anywhere inside.
    /// </summary>
    public static double Shift(double outer, double inner, double r, bool any = false)
    {
        double room = (outer - inner) / 2 - FlushGap;
        if (room <= 0)
            return 0;
        if (any)
            return (r * 2 - 1) * room * 0.8;
        return r < 0.45 ? 0 : (r < 0.725 ? -room : room);
    }

    /// <summary>
    /// The pieces a tall building is drawn from, or null for the plain box(es). The largest box gets the massing;
    /// the smaller boxes of an outline (at most two) stay as they were. Needs the main box to be most of the area.
    /// </summary>
    public static List<Part>? Parts(RawBuilding building, IReadOnlyList<Box2> boxes, bool enabled)
    {
        if (!enabled || !Eligible(building) || boxes.Count == 0)
            return null;
        int main = 0;
        double total = 0;
        for (int i = 0; i < boxes.Count; i++)
        {
            double area = boxes[i].LengthA * boxes[i].LengthB;
            total += area;
            if (area > boxes[main].LengthA * boxes[main].LengthB)
                main = i;
        }
        var m = boxes[main];
        if (m.LengthA * m.LengthB < 0.6 * total)
            return null;
        var tiers = Plan(m.LengthA, m.LengthB, building.Height, SeedOf(building));
        if (tiers.Count == 0)
            return null;

        var parts = new List<Part>(tiers.Count + boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
            if (i != main)
                parts.Add(new Part(building, boxes[i], false));
        double cos = Math.Cos(m.Angle), sin = Math.Sin(m.Angle);
        int topShaft = tiers.FindLastIndex(t => t.Kind == Kind.Shaft);
        for (int i = 0; i < tiers.Count; i++)
        {
            var t = tiers[i];
            var part = Clone(building);
            part.Boxes = new List<Box2>();
            part.Height = t.Top;
            part.MinHeight = t.Bottom;
            part.RoofHeight = 0;
            part.RoofPitch = 0;
            if (t.Kind == Kind.Mast)
                part.Variant = MastVariant;
            if (t.Kind is Kind.Crown or Kind.Mast)
                part.Style = Buildings.StyleNone;
            if (i > 0)
                part.Id = building.Id * 8 + i;      // own window seed per part
            // Box axes: A = (cos, sin), B = (-sin, cos) in east/north.
            var box = new Box2(m.CenterEast + cos * t.CA - sin * t.CB, m.CenterNorth + sin * t.CA + cos * t.CB, m.Angle, t.SA, t.SB);
            parts.Add(new Part(part, box, i == topShaft));
        }
        return parts;
    }

    private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Shallow copy, so fields other code adds to RawBuilding come along.</summary>
    private static RawBuilding Clone(RawBuilding b) => (RawBuilding)CloneMethod.Invoke(b, null)!;
}
