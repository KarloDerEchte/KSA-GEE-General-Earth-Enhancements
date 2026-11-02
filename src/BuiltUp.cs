namespace GEE;

/// <summary>
/// Where a tile is built up, from its own buildings: counts per 80 m cell. Street lights in many countries (Germany
/// among them) stand only within towns and villages, rural roads have reflector posts instead; OSM's lit tag is not
/// in the vector tiles, so the building density stands in for it. Kept free of game types so it can be tested offline.
/// </summary>
public sealed class BuiltUp
{
    private const double CellMeters = 80;
    private const double MetersPerDegree = Math.PI / 180 * 6371000;
    private readonly Dictionary<(int, int), int> _counts = new();
    private readonly double _cosLat;

    public BuiltUp(IReadOnlyList<RawBuilding> buildings, double referenceLat)
    {
        _cosLat = Math.Cos(referenceLat * Math.PI / 180);
        foreach (RawBuilding b in buildings)
        {
            // Sheds, kiosks and roofs over pumps do not make a village; aprons and pipes are not buildings at all.
            if (b.Slab || b.MinHeight > 0 || b.Boxes.Sum(x => x.LengthA * x.LengthB) < 25)
                continue;
            var key = Cell(b.Lat, b.Lon);
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
        }
    }

    private (int, int) Cell(double lat, double lon)
        => ((int)Math.Floor(lat * MetersPerDegree / CellMeters), (int)Math.Floor(lon * MetersPerDegree * _cosLat / CellMeters));

    /// <summary>Buildings in the 3 x 3 cells (about 240 m across) around a place.</summary>
    public int Around(double lat, double lon)
    {
        var (r, c) = Cell(lat, lon);
        int n = 0;
        for (int dr = -1; dr <= 1; dr++)
            for (int dc = -1; dc <= 1; dc++)
                n += _counts.GetValueOrDefault((r + dr, c + dc));
        return n;
    }

    /// <summary>
    /// Whether a road of this rank has street lights here: within villages and towns (a few buildings around),
    /// motorways and trunk roads only in dense urban areas.
    /// </summary>
    public bool Lit(double lat, double lon, int rank) => Around(lat, lon) >= (rank >= 5 ? 30 : 4);
}
