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
    /// <summary>Points where other roads meet this one (RoadJunctions): bends there are not rounded off.</summary>
    public bool[]? Fixed;
    /// <summary>
    /// At junction points (RoadJunctions): how far along the road on either side Placement lays plain asphalt without
    /// lines (Buildings.PlainVariant), about as far as the other roads there reach.
    /// </summary>
    public double[]? Clear;
    /// <summary>Which surface and markings the road gets (Buildings.SurfaceVariant).</summary>
    public RoadSurface Surface;
    /// <summary>A bridge over another road (RoadBridges): the whole deck at Placement.BridgeClearance, with pillars.</summary>
    public bool Overpass;
    /// <summary>Where along an overpass (metres from its first point) pillars stand.</summary>
    public List<double>? Pillars;
    /// <summary>Approach to an overpass (RoadBridges): height above the ground at the start or end, falling to none over RampLength.</summary>
    public double RampStart, RampEnd, RampLength;
}

/// <summary>
/// Road surfaces, each a scan-based material with its own markings (make_assets.py ROAD_PROFILES): main roads with
/// edge and centre lines, unmarked streets, one-lane ramps with edge lines, highway carriageways, and unpaved tracks.
/// </summary>
public enum RoadSurface { Road, Street, Ramp, Highway, Track }

public enum Airfield { None, Runway, Taxiway }

/// <summary>Which OpenMapTiles transportation features become roads, and how wide they are.</summary>
public static class Roads
{
    public const double BaseLift = 0.3;
    /// <summary>Height step between road classes; larger than all tie-break offsets within one class together.</summary>
    public const double RankStep = 0.04;
    public const double TieStep = 0.008;
    public const int TieLevels = 5;

    public static (double Width, int Rank, bool Bridge, bool Lit, RoadSurface Surface)? Classify(Dictionary<string, object?> tags)
    {
        string? cls = tags.GetValueOrDefault("class") as string;
        string? subclass = tags.GetValueOrDefault("subclass") as string;
        string? brunnel = tags.GetValueOrDefault("brunnel") as string;
        string? service = tags.GetValueOrDefault("service") as string;
        if (brunnel == "tunnel")
            return null;
        object? bridgeTag = tags.GetValueOrDefault("bridge");
        bool bridge = brunnel == "bridge"
            || bridgeTag is string value && value is not ("" or "no" or "false" or "0")
            || bridgeTag is true;
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
        // One carriageway of a motorway: two 3.75 m lanes, a 2.5 m hard shoulder and a strip on the left; of a trunk
        // road two 3.5 m lanes and narrower edges. Both get the highway surface, laid out for 11 m.
        bool highway = oneway && !ramp && cls is "motorway" or "trunk";
        if (highway)
            width = cls == "motorway" ? 11.0 : 9.0;
        else if (oneway || ramp)
            width = Math.Max(width * 0.6, 3.0);
        // Ramps lie under the carriageway they merge into (RoadMerges slides their ends under its edge).
        if (ramp && cls is "motorway" or "trunk")
            rank--;
        bool unpaved = cls == "track" || tags.GetValueOrDefault("surface") as string == "unpaved";
        var surface = highway ? RoadSurface.Highway
            : ramp && cls is "motorway" or "trunk" ? RoadSurface.Ramp
            : unpaved ? RoadSurface.Track
            : rank <= 1 ? RoadSurface.Street
            : RoadSurface.Road;
        return (width, rank, bridge, cls is not ("service" or "track"), surface);
    }

    /// <summary>Lift for a road of the given class; tie picks one of TieLevels sub-levels.</summary>
    public static double Lift(int rank, int tie) => BaseLift + rank * RankStep + (tie % TieLevels) * TieStep;
}
