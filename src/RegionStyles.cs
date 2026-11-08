namespace GEE;

/// <summary>Regions with their own architecture (RegionStyles.Table). Default keeps the generic look.</summary>
public enum Region { Default, NorthAmerica, SunBelt, Europe, Mediterranean, MiddleEast, EastAsia }

/// <summary>
/// How houses and low blocks look in one region: roof pitch ranges, how many get a pitched roof and how many of
/// those are hipped, roof colour and facade colour weights. Shares are probabilities in [0, 1].
/// </summary>
public sealed class RegionStyle
{
    public Region Region;
    public string Name = "";
    /// <summary>Largest footprint (m2) still counted as a house; bigger low buildings up to 900 m2 are blocks.</summary>
    public double HouseMaxArea = 350;
    public double HousePitchMin = 28, HousePitchMax = 42;
    public double BlockPitchMin = 12, BlockPitchMax = 22;
    /// <summary>Share of houses / low blocks with a pitched roof; the rest keep the flat roof.</summary>
    public double HouseShare = 1, BlockShare = 0.5;
    /// <summary>Share of pitched roofs that are hipped instead of gabled.</summary>
    public double HouseHip = 0.4, BlockHip = 0.5;
    /// <summary>Weights of the three roof colours: tile red, slate, brown (Buildings.RoofVariants).</summary>
    public double[] RoofWeights = { 15, 45, 40 };
    /// <summary>Facade colour weights by RegionStyles facade index (0-7 Buildings.Variants, 8-15 Buildings.ExtraFacades); null = the generic list.</summary>
    public double[]? FacadeWeights;
}

/// <summary>
/// Regional architecture from latitude/longitude. Game-free. Regions are a few lat/lon boxes (first match wins, so
/// specific boxes come first); within Feather degrees of a border a building takes the neighbouring region with a
/// probability that rises to one half at the border, chosen from its seed, so borders are ragged instead of straight.
/// Disabled (Settings.RegionalStyles = false), every place gets RegionStyles.Generic, which is the pre-region behaviour.
/// </summary>
public static class RegionStyles
{
    /// <summary>Set from Settings.RegionalStyles.</summary>
    public static bool Enabled = true;
    /// <summary>Width of the ragged border zone, degrees.</summary>
    public const double Feather = 1.5;

    // Facade indices: Buildings.Variants 0-7, then ExtraFacades 8-15.
    private const int Concrete = 0, Plaster = 1, Sand = 2, Brick = 3, Dark = 4, Ochre = 7,
        Cream = 8, PaleYellow = 9, Sage = 10, Terracotta = 11, GreyBlue = 12, Salmon = 13, White = 14, DarkBrick = 15;
    public const int FacadeCount = 16;

    /// <summary>Combined facade index to material variant.</summary>
    public static int FacadeVariant(int index) => index < Buildings.Variants.Length ? index : Buildings.FirstExtraFacade + index - Buildings.Variants.Length;

    private static double[] Facades(params (int Index, double Weight)[] w)
    {
        var a = new double[FacadeCount];
        foreach (var (i, x) in w)
            a[i] = x;
        return a;
    }

    public static readonly RegionStyle Generic = new() { Region = Region.Default, Name = "default" };

    public static readonly RegionStyle[] Table =
    {
        Generic,
        // US/Canada north of the sun belt: siding and brick, asphalt shingles, medium pitch, wide houses.
        new()
        {
            Region = Region.NorthAmerica, Name = "North America", HouseMaxArea = 450,
            HousePitchMin = 20, HousePitchMax = 34, BlockPitchMin = 8, BlockPitchMax = 16,
            HouseShare = 0.95, BlockShare = 0.4, HouseHip = 0.25, BlockHip = 0.3,
            RoofWeights = new double[] { 5, 60, 35 },
            FacadeWeights = Facades((Plaster, 2), (Sand, 2), (Brick, 3), (Concrete, 1), (Cream, 3), (PaleYellow, 1), (Sage, 2),
                (GreyBlue, 3), (White, 3), (DarkBrick, 2), (Ochre, 0.5)),
        },
        // Sun belt (southern US): stucco in light colours, low tile roofs, many flat commercial boxes.
        new()
        {
            Region = Region.SunBelt, Name = "US sun belt", HouseMaxArea = 500,
            HousePitchMin = 14, HousePitchMax = 24, BlockPitchMin = 6, BlockPitchMax = 12,
            HouseShare = 0.7, BlockShare = 0.25, HouseHip = 0.55, BlockHip = 0.5,
            RoofWeights = new double[] { 35, 25, 40 },
            FacadeWeights = Facades((Cream, 4), (Sand, 3), (PaleYellow, 1.5), (White, 3), (Terracotta, 1.5), (Salmon, 1), (Plaster, 2),
                (Ochre, 1), (Concrete, 1), (Brick, 0.5)),
        },
        // Western and central Europe: plaster, steep red/brown tile roofs, hips and gables on blocks too.
        new()
        {
            Region = Region.Europe, Name = "Europe", HouseMaxArea = 300,
            HousePitchMin = 32, HousePitchMax = 48, BlockPitchMin = 20, BlockPitchMax = 32,
            HouseShare = 1, BlockShare = 0.8, HouseHip = 0.35, BlockHip = 0.5,
            RoofWeights = new double[] { 50, 20, 30 },
            FacadeWeights = Facades((Plaster, 3), (Cream, 3), (PaleYellow, 2), (Salmon, 1.5), (Brick, 2), (White, 1.5), (Sand, 1),
                (Terracotta, 1), (Ochre, 1), (Sage, 1), (GreyBlue, 1), (Concrete, 1)),
        },
        // Mediterranean: white and sand walls, shallow terracotta roofs.
        new()
        {
            Region = Region.Mediterranean, Name = "Mediterranean", HouseMaxArea = 250,
            HousePitchMin = 14, HousePitchMax = 22, BlockPitchMin = 10, BlockPitchMax = 16,
            HouseShare = 0.85, BlockShare = 0.5, HouseHip = 0.5, BlockHip = 0.5,
            RoofWeights = new double[] { 85, 5, 10 },
            FacadeWeights = Facades((White, 4), (Sand, 3), (Cream, 3), (Ochre, 2), (Terracotta, 2), (PaleYellow, 2), (Salmon, 1), (Plaster, 1.5)),
        },
        // Middle East and North Africa: flat roofs, sand, white and bare concrete.
        new()
        {
            Region = Region.MiddleEast, Name = "Middle East / N Africa", HouseMaxArea = 300,
            HouseShare = 0, BlockShare = 0,
            FacadeWeights = Facades((Sand, 4), (White, 3), (Cream, 3), (Ochre, 1.5), (Concrete, 2), (PaleYellow, 1), (Salmon, 0.3)),
        },
        // East Asia: grey and white concrete blocks, a minority of grey-tiled hip roofs.
        new()
        {
            Region = Region.EastAsia, Name = "East Asia", HouseMaxArea = 300,
            HousePitchMin = 20, HousePitchMax = 30, BlockPitchMin = 12, BlockPitchMax = 18,
            HouseShare = 0.4, BlockShare = 0.1, HouseHip = 0.7, BlockHip = 0.6,
            RoofWeights = new double[] { 5, 70, 25 },
            FacadeWeights = Facades((Concrete, 4), (White, 3), (Cream, 1.5), (Plaster, 2), (GreyBlue, 2), (PaleYellow, 0.5), (Sage, 0.5),
                (Brick, 0.3), (Dark, 0.5)),
        },
    };

    // Boxes in priority order: south, north, west, east (degrees).
    private static readonly (Region Region, double S, double N, double W, double E)[] Boxes =
    {
        (Region.SunBelt, 25, 36.5, -125, -75),
        (Region.NorthAmerica, 25, 60, -168, -52),
        (Region.NorthAmerica, 18.5, 22.5, -161, -154.5),            // Hawaii
        (Region.EastAsia, 20, 46, 100, 146),
        (Region.MiddleEast, 12, 35.5, -17.5, 63),
        (Region.Mediterranean, 35.5, 44.2, -9.8, 26.5),             // Iberia, southern France, Italy, Greece, Balkan coast
        (Region.Mediterranean, 36, 42, 26.5, 44.8),                 // Turkey
        (Region.Europe, 42, 71, -11, 30),
    };

    /// <summary>The region of a point, without the ragged border.</summary>
    public static Region RegionOf(double lat, double lon)
    {
        foreach (var b in Boxes)
            if (lat >= b.S && lat < b.N && lon >= b.W && lon < b.E)
                return b.Region;
        return Region.Default;
    }

    /// <summary>Style by region.</summary>
    public static RegionStyle StyleOf(Region region) => Table[(int)region];

    /// <summary>
    /// The style for a building at (lat, lon): the region of the point, or near a border the region across it with a
    /// probability of up to one half (from seed).
    /// </summary>
    public static RegionStyle At(double lat, double lon, long seed)
    {
        if (!Enabled)
            return Generic;
        var home = RegionOf(lat, lon);
        double u = Mix(seed);
        // Nearest border of the home region's box (a point in no box measures against every box it is close to).
        foreach (var b in Boxes)
        {
            double outLat = Math.Max(Math.Max(b.S - lat, lat - b.N), 0), outLon = Math.Max(Math.Max(b.W - lon, lon - b.E), 0);
            bool inside = outLat == 0 && outLon == 0;
            if (inside && b.Region != home)
                continue;                                           // hidden by a higher priority box
            if (inside)
            {
                double depth = Math.Min(Math.Min(lat - b.S, b.N - lat), Math.Min(lon - b.W, b.E - lon));
                if (depth >= Feather)
                    return Table[(int)home];
                if (u >= 0.5 * (1 - depth / Feather))
                    return Table[(int)home];
                // Across the nearest edge.
                double dLat = 0, dLon = 0;
                if (depth == lat - b.S) dLat = -1; else if (depth == b.N - lat) dLat = 1; else if (depth == lon - b.W) dLon = -1; else dLon = 1;
                return Table[(int)RegionOf(lat + dLat * (depth + 0.01), lon + dLon * (depth + 0.01))];
            }
            double dist = Math.Max(outLat, outLon);
            if (dist < Feather && u < 0.5 * (1 - dist / Feather))
                return Table[(int)b.Region];
        }
        return Table[(int)home];
    }

    /// <summary>Deterministic value in [0, 1) per seed.</summary>
    private static double Mix(long seed)
    {
        ulong x = ((ulong)seed + 0x632BE59BD9B4E019UL) * 0x94D049BB133111EBUL;
        x ^= x >> 29;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 32;
        return (x >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Index into weights by u in [0, 1).</summary>
    public static int Pick(double[] weights, double u)
    {
        double total = 0;
        foreach (double w in weights)
            total += w;
        double t = u * total, acc = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            acc += weights[i];
            if (weights[i] > 0 && t < acc)
                return i;
        }
        for (int i = weights.Length - 1; i >= 0; i--)
            if (weights[i] > 0)
                return i;
        return 0;
    }
}
