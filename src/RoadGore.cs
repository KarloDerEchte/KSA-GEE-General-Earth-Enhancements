namespace GEE;

/// <summary>
/// Gore hatching on the tapered stretch where a ramp slides under a carriageway (RoadMerges sets RawRoad.GoreAtStart /
/// GoreAtEnd). The ramp texture there (Buildings.GoreVariant) has chevrons pointing along the ramp over its whole
/// width; the carriageway covers its inner part, so only the wedge beside the carriageway shows hatching, between the
/// ramp's solid edge line and the carriageway. Uses no game types.
/// </summary>
public static class RoadGore
{
    /// <summary>Whether piece i (between points i and i + 1) lies in a gore stretch of the road.</summary>
    public static bool Covers(RawRoad road, double[] at, int i)
    {
        double mid = (at[i] + at[i + 1]) / 2, total = at[^1];
        return mid < road.GoreAtStart || total - mid < road.GoreAtEnd;
    }
}
