namespace GEE;

/// <summary>One road line within a tile.</summary>
public sealed class RawRoad
{
    public List<(double Lat, double Lon)> Points = new();
    public double Width;
    /// <summary>
    /// Surface height above the terrain. Bigger roads lie higher so they win at crossings, and roads of the
    /// same class differ by a few millimetres so no two of them are ever coplanar.
    /// </summary>
    public double Lift;
    /// <summary>Bridges run straight between their end heights instead of following the valley below.</summary>
    public bool Bridge;
    /// <summary>Ends cut at the tile edge continue in the neighbouring tile, so they get no overlap there.</summary>
    public bool StartAtTileEdge;
    public bool EndAtTileEdge;
    /// <summary>Has street lamps at night: everything but service roads and tracks.</summary>
    public bool Lit;
    public int Rank;
    /// <summary>Runways and taxiways have their own surface and edge lights.</summary>
    public Airfield Airfield;
}

public enum Airfield { None, Runway, Taxiway }

/// <summary>Which OpenMapTiles transportation features become roads, and how wide they are.</summary>
public static class Roads
{
    public const double BaseLift = 0.3;
    /// <summary>Height step between road classes; larger than all tie-break offsets within one class together.</summary>
    public const double RankStep = 0.04;
    public const double TieStep = 0.008;
    public const int TieLevels = 5;

    public static (double Width, int Rank, bool Bridge, bool Lit)? Classify(Dictionary<string, object?> tags)
    {
        string? cls = tags.GetValueOrDefault("class") as string;
        string? subclass = tags.GetValueOrDefault("subclass") as string;
        string? brunnel = tags.GetValueOrDefault("brunnel") as string;
        string? service = tags.GetValueOrDefault("service") as string;
        if (brunnel == "tunnel")
            return null;
        (double width, int rank) = cls switch
        {
            // Pedestrian zones are street-wide; footways and cycleways are left out.
            "path" when subclass == "pedestrian" => (8.0, 0),
            "motorway" => (12.0, 6),
            "trunk" => (10.0, 5),
            "primary" => (9.0, 4),
            "secondary" => (8.0, 3),
            "tertiary" => (7.0, 2),
            "minor" or "busway" or "raceway" => (6.0, 1),
            "service" when service is "driveway" or "parking_aisle" or "alley" => (3.0, 0),
            "service" => (4.0, 0),
            "track" => (3.0, 0),
            _ => (0.0, 0),
        };
        if (width <= 0)
            return null;
        // Dual carriageways are mapped as two one-way lines a few metres apart; full width would merge them.
        bool oneway = tags.GetValueOrDefault("oneway") is double o && o != 0;
        bool ramp = tags.GetValueOrDefault("ramp") is double r && r != 0;
        if (oneway || ramp)
            width = Math.Max(width * 0.6, 3.0);
        return (width, rank, brunnel == "bridge", cls is not ("service" or "track"));
    }

    /// <summary>Lift for a road of the given class; tie picks one of TieLevels sub-levels.</summary>
    public static double Lift(int rank, int tie) => BaseLift + rank * RankStep + (tie % TieLevels) * TieStep;
}
