using Brutal.Numerics;

namespace GEE;

/// <summary>
/// Roadside furniture, a few instances of small models (tools/furniture_assets.py, Buildings.FurnitureVariant) drawn
/// as detail pieces near the camera: a lamp post under each lit street lamp (so also on bridges, where the lamps lie
/// on the deck), and at motorway exits a sign post at the verge 110 m before the junction and a cantilever sign
/// gantry 300 m before it. Settings.RoadFurniture. Instance counts stay low: signs only at exits, posts only where a
/// lamp glows, and none of it beyond Settings.FurnitureDistanceKm.
/// </summary>
internal static class RoadFurniture
{
    private const int LampModel = 0, SignModel = 1, GantryModel = 2;
    private const double LampHeight = 8.1, LampReach = 1.4;
    private const double GantryBefore = 300, SignBefore = 110, MinGantrySpacing = 160;
    /// <summary>How near to a carriageway an exit ramp's first point lies (RoadMerges puts it beside the edge).</summary>
    private const double ExitMatchMeters = 24;

    /// <summary>
    /// Squared real extent of a model in metres, for the renderer's size cull: the instance axes are unit length (the
    /// models are modelled at real size), which would count a 8 m lamp post as a 1 m box and drop it past ~2 km.
    /// </summary>
    public static double Extent2(int model) => model switch { LampModel => 8.1 * 8.1, SignModel => 4.0 * 4.0, _ => 13.0 * 13.0 };

    private static BuildingInstance Make(int model, double3 foot, double3 up, double3 facing, double across, uint seed)
    {
        up = double3.Normalize(up);
        double3 a = facing - up * double3.Dot(facing, up);
        double length = Math.Sqrt(double3.Dot(a, a));
        a = length > 1e-9 ? a / length : double3.Cross(up, new double3(0, 0, 1));
        return new BuildingInstance
        {
            Up = up,
            AxisA = a,
            AxisB = double3.Cross(up, a) * across,
            Base = foot,
            Center = foot,
            Variant = Buildings.FurnitureVariant + model,
            Style = Buildings.StyleNone,
            IsDetail = true,
            Layer = Layers.Street,
            Seed = seed,
        };
    }

    /// <summary>The post of a lit street lamp: its head sits where the lamp's light is, the arm reaches toward the road.</summary>
    public static void AddLamp(Tile tile, in RoadLight light)
    {
        if (double3.Dot(light.Toward, light.Toward) < 0.25)
            return;
        double3 up = double3.Normalize(light.Position);
        double3 toward = double3.Normalize(light.Toward - up * double3.Dot(light.Toward, up));
        tile.Add(Make(LampModel, light.Position - up * LampHeight - toward * LampReach, up, toward, 1, light.Seed));
    }

    private readonly record struct P(double X, double Y);

    /// <summary>Signs and gantries before the exits of the tile's one-way motorway carriageways.</summary>
    public static void AddSigns(Tile tile, List<RawRoad> roads, double planetRadius, Func<double3, double> terrain)
    {
        var carriageways = roads.Where(r => r.Surface is RoadSurface.Highway or RoadSurface.Highway3 && !r.Bridge && !r.Overpass
                                            && r.Airfield == Airfield.None && r.Points.Count >= 2).ToList();
        if (carriageways.Count == 0)
            return;
        var ramps = roads.Where(r => r.Surface == RoadSurface.Ramp && !r.Bridge && !r.StartAtTileEdge && r.Points.Count >= 2).ToList();
        if (ramps.Count == 0)
            return;
        var origin = carriageways[0].Points[0];
        double k = Math.PI / 180 * planetRadius, kLon = k * Math.Cos(origin.Lat * Math.PI / 180);
        P Local((double Lat, double Lon) q) => new((q.Lon - origin.Lon) * kLon, (q.Lat - origin.Lat) * k);
        double3 Dir(P p) => Placement.DirCcf(origin.Lat + p.Y / k, origin.Lon + p.X / kLon);

        var lines = carriageways.Select(r => (Road: r, Line: r.Points.Select(Local).ToArray())).ToList();
        // A carriageway is often several roads end to start (they are cut at junctions): upstream continues there.
        var endsAt = new Dictionary<(double, double), int>();
        for (int i = 0; i < lines.Count; i++)
            endsAt[lines[i].Road.Points[^1]] = i;

        var gantries = new List<P>();
        foreach (var ramp in ramps)
        {
            P start = Local(ramp.Points[0]);
            // The carriageway the ramp leaves: nearest line within reach.
            int best = -1, segment = 0;
            double bestDistance = ExitMatchMeters, bestT = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Line;
                for (int j = 0; j + 1 < line.Length; j++)
                {
                    double d = DistanceToSegment(start, line[j], line[j + 1], out double t);
                    if (d < bestDistance) { bestDistance = d; best = i; segment = j; bestT = t; }
                }
            }
            if (best < 0)
                continue;
            var (road, poly) = lines[best];
            double half = road.Width / 2;
            double along = 0;
            for (int j = 0; j < segment; j++)
                along += Distance(poly[j], poly[j + 1]);
            along += bestT * Distance(poly[segment], poly[segment + 1]);

            uint seed = LightSet.Hash((long)(start.X * 10), (long)(start.Y * 10), 31);
            if (TryUpstream(best, along, SignBefore, out P signAt, out P signDir))
                PlaceSign(SignModel, signAt, signDir, half + 2.8, 1, seed, half);
            if (TryUpstream(best, along, GantryBefore, out P gantryAt, out P gantryDir)
                && gantries.All(g => Distance(g, gantryAt) > MinGantrySpacing))
            {
                gantries.Add(gantryAt);
                double span = (2 * half + 1.3) / 12.6;
                PlaceSign(GantryModel, gantryAt, gantryDir, half + 1.8 - 7 * span, span, seed + 1, half);
            }
        }

        // The point `distance` metres before `along` on carriageway `index` (continuing into the road that ends where
        // it starts), with the direction of travel there.
        bool TryUpstream(int index, double along, double distance, out P at, out P direction)
        {
            at = direction = default;
            double remaining = along - distance;
            for (int hops = 0; hops < 4; hops++)
            {
                if (remaining >= 0)
                {
                    var line = lines[index].Line;
                    double walked = 0;
                    for (int j = 0; j + 1 < line.Length; j++)
                    {
                        double length = Distance(line[j], line[j + 1]);
                        if (walked + length >= remaining && length > 0.01)
                        {
                            double t = (remaining - walked) / length;
                            at = new P(line[j].X + (line[j + 1].X - line[j].X) * t, line[j].Y + (line[j + 1].Y - line[j].Y) * t);
                            direction = new P((line[j + 1].X - line[j].X) / length, (line[j + 1].Y - line[j].Y) / length);
                            return true;
                        }
                        walked += length;
                    }
                    return false;
                }
                var first = lines[index].Road.Points[0];
                if (lines[index].Road.StartAtTileEdge || !endsAt.TryGetValue(first, out int previous) || previous == index)
                    return false;
                index = previous;
                double total = 0;
                var predecessor = lines[index].Line;
                for (int j = 0; j + 1 < predecessor.Length; j++)
                    total += Distance(predecessor[j], predecessor[j + 1]);
                remaining += total;
            }
            return false;
        }

        // Origin `lateral` m to the right of the carriageway centre (travel direction `direction`), facing the traffic.
        void PlaceSign(int model, P at, P direction, double lateral, double across, uint seed, double half)
        {
            P right = new(direction.Y, -direction.X);
            P foot = new(at.X + right.X * lateral, at.Y + right.Y * lateral);
            double3 dir = Dir(foot);
            double ground = Math.Max(terrain(dir), 0);
            double3 up = dir;
            double3 travel = Dir(new P(foot.X + direction.X, foot.Y + direction.Y)) * planetRadius - dir * planetRadius;
            tile.Add(Make(model, dir * (planetRadius + ground), up, -travel, across, seed));
        }

        static double Distance(P a, P b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    private static double DistanceToSegment(P p, P a, P b, out double t)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        t = l2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1) : 0;
        double ex = a.X + dx * t - p.X, ey = a.Y + dy * t - p.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
