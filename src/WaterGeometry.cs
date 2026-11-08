using Brutal.Numerics;

namespace GEE;

/// <summary>
/// Turns the water layers of a vector tile into triangles. Free of game types (the terrain comes in as a function),
/// so it can be run offline against cached tiles.
/// </summary>
internal static class WaterGeometry
{
    private const double LakeLift = 0.25, RiverLift = 0.35, MaxEdgeMeters = 100_000, MinLakeArea = 40_000;
    /// <summary>Intermittent lakes below this are skipped (m2); pieces cut by the tile edge may belong to a big one, so they need less.</summary>
    private const double MinIntermittentArea = 1.5e6, MinIntermittentEdgeArea = 0.1e6;
    /// <summary>River polygons below this area, or narrower than twice MinRiverHalfWidth (area / shoreline), are left to the river map.</summary>
    private const double MinRiverArea = 0.3e6, MinRiverHalfWidth = 90;
    /// <summary>Sea water (class ocean) where the game has land: at least this big.</summary>
    private const double MinTidalArea = 0.1e6;
    private const int MaxVerticesPerTile = 240_000;
    /// <summary>Water below this height belongs to the game's ocean (bays, estuaries, tidal flats).</summary>
    public const double SeaMargin = 6;

    /// <summary>
    /// A lake whose real (flat) surface is at the sea's height, within these limits, is a lagoon or bay (Indian River, Banana River):
    /// water at level 0 with the terrain cut under it. Depressions below the sea (Salton Sea, Caspian) are not.
    /// </summary>
    public const double SeaLakeMaxMeters = 1.5, SeaLakeMinMeters = -2;
    public static bool IsSeaLevelLake(double realMedianMeters) => realMedianMeters <= SeaLakeMaxMeters && realMedianMeters >= SeaLakeMinMeters;

    private static int _logged;

    private static double3 Dir(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        return new double3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }


    internal sealed class Poly
    {
        public List<Footprint.P> Outer = new();
        public readonly List<List<Footprint.P>> Holes = new();
        /// <summary>A polygon of class river (wide rivers and estuaries) and one marked as dry part of the year.</summary>
        public bool River, Intermittent, Ocean;
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;

        public bool Contains(double x, double y)
        {
            if (x < MinX || x > MaxX || y < MinY || y > MaxY || !InRing(Outer, x, y))
                return false;
            foreach (var hole in Holes)
            {
                if (InRing(hole, x, y))
                    return false;
            }
            return true;
        }
    }

    private static bool InRing(List<Footprint.P> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if (ring[i].Y > y != ring[j].Y > y && x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Polygons of a feature: each outer ring (positive area in tile coordinates) with the holes that follow it.</summary>
    internal static List<Poly> Polygons(List<uint> geometry)
    {
        var polys = new List<Poly>();
        Poly? current = null;
        List<Footprint.P>? ring = null;
        int cx = 0, cy = 0, i = 0;
        while (i < geometry.Count)
        {
            uint command = geometry[i++];
            int id = (int)(command & 7), count = (int)(command >> 3);
            if (id == 7)
            {
                if (ring is { Count: >= 3 })
                {
                    if (Footprint.SignedArea(ring) > 0)
                    {
                        current = new Poly { Outer = ring };
                        foreach (var p in ring)
                        {
                            current.MinX = Math.Min(current.MinX, p.X);
                            current.MaxX = Math.Max(current.MaxX, p.X);
                            current.MinY = Math.Min(current.MinY, p.Y);
                            current.MaxY = Math.Max(current.MaxY, p.Y);
                        }
                        polys.Add(current);
                    }
                    else
                    {
                        current?.Holes.Add(ring);
                    }
                }
                ring = null;
                continue;
            }
            for (int k = 0; k < count && i + 1 < geometry.Count; k++)
            {
                cx += (int)(geometry[i] >> 1) ^ -(int)(geometry[i] & 1);
                cy += (int)(geometry[i + 1] >> 1) ^ -(int)(geometry[i + 1] & 1);
                i += 2;
                if (id == 1)
                    ring = new List<Footprint.P>();
                ring?.Add(new Footprint.P(cx, cy));
            }
        }
        return polys;
    }

    private static double Width(string? waterwayClass) => waterwayClass switch
    {
        "river" => 18,
        "canal" => 12,
        "stream" => 4,
        "drain" => 3,
        "ditch" => 2.5,
        _ => 3,
    };

    /// <summary>Worker thread: decodes the tile and writes its triangles.</summary>
    public static void Build(WaterTile tile, byte[] data, double planetR, Func<double, double, double> terrainHeight)
    {
        var (south, west, north, east) = tile.Bounds;
        tile.CenterCcf = Dir((south + north) / 2, (west + east) / 2) * planetR;
        double kmPerDeg = planetR * Math.PI / 180;
        tile.RadiusM = kmPerDeg * Math.Sqrt(Math.Pow(north - south, 2) + Math.Pow((east - west) * Math.Cos(south * Math.PI / 180), 2)) / 2 + 400;

        tile.Lakes = new();
        tile.Rivers = new();
        tile.RiverAreas = new();
        var lakes = new List<Poly>();
        var lines = new List<(List<Footprint.P> Points, double Width)>();
        int extent = 4096;
        void AddArea(Poly area)
        {
            var flatRing = new List<double>();
            var ringStarts = new List<int>();
            AddRing(flatRing, area.Outer);
            foreach (var hole in area.Holes)
            {
                ringStarts.Add(flatRing.Count / 2);
                AddRing(flatRing, hole);
            }
            try
            {
                var corners = Earcut.Triangulate(flatRing.ToArray(), ringStarts.ToArray());
                var triangles = new double[corners.Count * 2];
                for (int c = 0; c < corners.Count; c++)
                {
                    var (glat, glon) = VectorTile.ToLatLon(WaterTile.Zoom, tile.X + flatRing[2 * corners[c]] / extent, tile.Y + flatRing[2 * corners[c] + 1] / extent);
                    triangles[2 * c] = glat;
                    triangles[2 * c + 1] = glon;
                }
                if (triangles.Length >= 6)
                    tile.RiverAreas.Add(triangles);
            }
            catch (Exception)
            {
                // a degenerate polygon of the data: left out
            }
        }
        VectorTile.ForEachFeature(data, layer => layer is "water" or "waterway", (layer, type, tags, geometry, ext) =>
        {
            extent = ext;
            if (layer == "water" && type == 3)
            {
                // The banks of the big rivers are areas of class river: their triangles feed the river maps. The wide ones are
                // also water of their own (a piece per tile with a level of its own, see LakeOcean).
                if (tags.GetValueOrDefault("class") is "river" && tags.GetValueOrDefault("brunnel") is not "tunnel")
                {
                    foreach (var area in Polygons(geometry))
                    {
                        AddArea(area);
                        area.River = true;
                        lakes.Add(area);
                    }
                    return;
                }
                // Tidal water the game does not show (an estuary like the St. Johns at Jacksonville is class ocean here, but the game's
                // heightmap has land there): checked against the terrain below, narrow pieces only. The open sea is the game's.
                if (tags.GetValueOrDefault("class") is "ocean" && tags.GetValueOrDefault("brunnel") is not "tunnel")
                {
                    foreach (var sea in Polygons(geometry))
                    {
                        sea.Ocean = true;
                        lakes.Add(sea);
                    }
                    return;
                }
                // Lakes that are dry part of the year are only dropped when small: Walker and Pyramid Lake are tagged so too.
                bool intermittent = tags.GetValueOrDefault("intermittent") is double and not 0.0;
                if (tags.GetValueOrDefault("class") is not "lake" || tags.GetValueOrDefault("brunnel") is "tunnel")
                    return;
                foreach (var pond in Polygons(geometry))
                {
                    pond.Intermittent = intermittent;
                    if (intermittent)
                    {
                        double unitI = 2 * Math.PI * planetR * Math.Cos((south + north) / 2 * Math.PI / 180) / (1 << WaterTile.Zoom) / ext;
                        double areaI = Math.Abs(Footprint.SignedArea(pond.Outer)) * unitI * unitI;
                        bool edge = pond.MinX <= 2 || pond.MinY <= 2 || pond.MaxX >= ext - 2 || pond.MaxY >= ext - 2;
                        if (areaI < (edge ? MinIntermittentEdgeArea : MinIntermittentArea))
                            continue;
                    }
                    lakes.Add(pond);
                    // Ponds and small lakes never get the ocean pass (too small, MinLakeAreaKm2): they are painted like the river banks.
                    // Polygons touching the tile edge may be pieces of a big lake and are left to that pass.
                    double unit = 2 * Math.PI * planetR * Math.Cos((south + north) / 2 * Math.PI / 180) / (1 << WaterTile.Zoom) / ext;
                    double box = (pond.MaxX - pond.MinX) * (pond.MaxY - pond.MinY) * unit * unit;
                    if (box < 1.5e6 && pond.MinX > 4 && pond.MinY > 4 && pond.MaxX < ext - 4 && pond.MaxY < ext - 4)
                        AddArea(pond);
                }
            }
            else if (layer == "waterway" && type == 2)
            {
                // Rivers, streams and canals only (drains and ditches are too small), not in tunnels or dry part of the year.
                if (tags.GetValueOrDefault("brunnel") is "tunnel" || tags.GetValueOrDefault("intermittent") is double and not 0.0)
                    return;
                double width = RiverMap.Width(tags.GetValueOrDefault("class") as string);
                if (width <= 0)
                    return;
                foreach (var line in VectorTile.Lines(geometry))
                {
                    if (line.Count < 2)
                        continue;
                    var latLon = new double[line.Count * 2];
                    for (int k = 0; k < line.Count; k++)
                    {
                        var (glat, glon) = Geo(line[k].X, line[k].Y);
                        latLon[2 * k] = glat;
                        latLon[2 * k + 1] = glon;
                    }
                    tile.Rivers.Add(new RiverLine { Width = width, LatLon = latLon });
                }
            }
        });

        double metersPerUnit = 2 * Math.PI * planetR * Math.Cos((south + north) / 2 * Math.PI / 180) / (1 << WaterTile.Zoom) / extent;
        double lonWestRad = west * Math.PI / 180, latNorthRad = north * Math.PI / 180;
        double originX = Mod(lonWestRad * planetR, 1024), originY = Mod(latNorthRad * planetR, 1024);
        var output = new List<WaterVertex>();

        (double Lat, double Lon) Geo(double px, double py) => VectorTile.ToLatLon(WaterTile.Zoom, tile.X + px / extent, tile.Y + py / extent);
        double Height(double px, double py)
        {
            var (lat, lon) = Geo(px, py);
            return terrainHeight(lat, lon);
        }
        WaterVertex Vertex(double px, double py, double level, float across, float kind)
        {
            var (lat, lon) = Geo(px, py);
            double3 local = Dir(lat, lon) * (planetR + level) - tile.CenterCcf;
            double latRad = lat * Math.PI / 180, lonRad = lon * Math.PI / 180;
            return new WaterVertex
            {
                X = (float)local.X, Y = (float)local.Y, Z = (float)local.Z,
                Wx = (float)(originX + (lonRad - lonWestRad) * planetR),
                Wy = (float)(originY + (latNorthRad - latRad) * planetR),
                Across = across,
                Kind = kind,
            };
        }

        List<double[]> HoleRings(Poly poly)
        {
            var rings = new List<double[]>();
            foreach (var hole in poly.Holes)
            {
                if (hole.Count < 3)
                    continue;
                var geo = new double[hole.Count * 2];
                for (int k = 0; k < hole.Count; k++)
                {
                    var (glat, glon) = Geo(hole[k].X, hole[k].Y);
                    geo[2 * k] = glat;
                    geo[2 * k + 1] = glon;
                }
                rings.Add(geo);
            }
            return rings;
        }

        void AddTidal(Poly poly, double area, double shoreMeters)
        {
            // The triangles of the piece (their level is not used: the map paints it and the terrain is cut under it).
            int first = output.Count;
            var flat = new List<double>();
            AddRing(flat, poly.Outer);
            var holeStarts = new List<int>();
            foreach (var hole in poly.Holes)
            {
                holeStarts.Add(flat.Count / 2);
                AddRing(flat, hole);
            }
            List<int> indices;
            try
            {
                indices = Earcut.Triangulate(flat.ToArray(), holeStarts.ToArray());
            }
            catch (Exception)
            {
                return;
            }
            for (int t = 0; t + 2 < indices.Count; t += 3)
                for (int c = 0; c < 3; c++)
                    output.Add(Vertex(flat[2 * indices[t + c]], flat[2 * indices[t + c] + 1], 0, 0, 0));
            int edges = 0;
            foreach (var q in poly.Outer)
            {
                if (q.X <= 2) edges |= 1;
                if (q.X >= extent - 2) edges |= 2;
                if (q.Y <= 2) edges |= 4;
                if (q.Y >= extent - 2) edges |= 8;
            }
            // Points inside (the real elevation is checked there: only water at the sea's height is cut) and the islands.
            var insideGeo = new List<double>();
            const int fine = 12;
            for (int gy = 0; gy < fine; gy++)
            {
                for (int gx = 0; gx < fine; gx++)
                {
                    double px = poly.MinX + (poly.MaxX - poly.MinX) * (gx + 0.5) / fine, py = poly.MinY + (poly.MaxY - poly.MinY) * (gy + 0.5) / fine;
                    if (poly.Contains(px, py))
                    {
                        var (glat, glon) = Geo(px, py);
                        insideGeo.Add(glat);
                        insideGeo.Add(glon);
                    }
                }
            }
            tile.Lakes.Add(new LakeInfo { AreaM2 = area, ShoreM = shoreMeters, FirstVertex = first, VertexCount = output.Count - first, Edges = edges, IsRiver = true, IsTidal = true, InsideGeo = insideGeo.ToArray(), HoleGeo = HoleRings(poly) });
        }

        // Lakes.
        foreach (var poly in lakes)
        {
            if (output.Count > MaxVerticesPerTile)
                break;
            double area = Math.Abs(Footprint.SignedArea(poly.Outer)) * metersPerUnit * metersPerUnit;
            if (area < MinLakeArea || poly.Outer.Count < 3)
                continue;
            double shoreMeters = 0;
            for (int k = 0, prev = poly.Outer.Count - 1; k < poly.Outer.Count; prev = k++)
            {
                var p = poly.Outer[prev];
                var q = poly.Outer[k];
                bool cut = (p.X <= 2 && q.X <= 2) || (p.X >= extent - 2 && q.X >= extent - 2) || (p.Y <= 2 && q.Y <= 2) || (p.Y >= extent - 2 && q.Y >= extent - 2);
                if (!cut)
                    shoreMeters += Dist(p.X, p.Y, q.X, q.Y) * metersPerUnit;
            }
            foreach (var hole in poly.Holes)
            {
                for (int k = 0, prev = hole.Count - 1; k < hole.Count; prev = k++)
                    shoreMeters += Dist(hole[prev].X, hole[prev].Y, hole[k].X, hole[k].Y) * metersPerUnit;
            }
            if (poly.Ocean)
            {
                // Sea water where the game's terrain is land: estuaries inland of the game's coast, and the game's land that sticks out
                // over the real shelf (the terrain is only ever cut down there, never raised: LakeOcean). Wide polygons (the open sea)
                // count too, but only where some sample of the game's terrain is land.
                if (area < MinTidalArea || OceanHasNoLand(poly, Height))
                    continue;
                AddTidal(poly, area, shoreMeters);
                continue;
            }
            // A river polygon is water of its own only where it is wide (shore points of the tile edge are cuts, not shore).
            if (poly.River && (area < MinRiverArea || (shoreMeters > 1 && area / shoreMeters < MinRiverHalfWidth)))
                continue;
            // The level is the height of the shore: the heightmap is flat on lakes and meets the bank at the lake's level, and
            // neighbouring tiles of one lake then agree. Points on the tile edge are cuts, not shore. A lake that fills the
            // tile has no shore here: its level is the median of the terrain inside, a little above it so the surface is
            // not buried.
            var shore = new List<double>();
            int shoreStep = Math.Max(1, poly.Outer.Count / 40);
            for (int k = 0; k < poly.Outer.Count; k += shoreStep)
            {
                var q = poly.Outer[k];
                if (q.X > 2 && q.Y > 2 && q.X < extent - 2 && q.Y < extent - 2)
                    shore.Add(Height(q.X, q.Y));
            }
            var samples = new List<double>();
            const int grid = 8;
            for (int gy = 0; gy < grid; gy++)
            {
                for (int gx = 0; gx < grid; gx++)
                {
                    double px = poly.MinX + (poly.MaxX - poly.MinX) * (gx + 0.5) / grid;
                    double py = poly.MinY + (poly.MaxY - poly.MinY) * (gy + 0.5) / grid;
                    if (poly.Contains(px, py))
                        samples.Add(Height(px, py));
                }
            }
            // Where the real elevation can be looked up (no terrain query needed here): points inside and on the shore.
            var insideGeo = new List<double>();
            const int fine = 12;
            for (int gy = 0; gy < fine; gy++)
            {
                for (int gx = 0; gx < fine; gx++)
                {
                    double px = poly.MinX + (poly.MaxX - poly.MinX) * (gx + 0.5) / fine;
                    double py = poly.MinY + (poly.MaxY - poly.MinY) * (gy + 0.5) / fine;
                    if (poly.Contains(px, py))
                    {
                        var (glat, glon) = Geo(px, py);
                        insideGeo.Add(glat);
                        insideGeo.Add(glon);
                    }
                }
            }
            var shoreGeo = new List<double>();
            for (int k = 0; k < poly.Outer.Count; k += shoreStep)
            {
                var q = poly.Outer[k];
                if (q.X > 2 && q.Y > 2 && q.X < extent - 2 && q.Y < extent - 2)
                {
                    var (glat, glon) = Geo(q.X, q.Y);
                    shoreGeo.Add(glat);
                    shoreGeo.Add(glon);
                }
            }
            samples.Sort();
            shore.Sort();
            double level;
            string source;
            if (shore.Count >= 6)
            {
                level = shore[shore.Count / 4] + LakeLift;
                source = "shore";
            }
            else if (samples.Count >= 3)
            {
                level = samples[samples.Count / 20] + LakeLift;
                source = "inside";
            }
            else
            {
                var edge = shore.Count > 0 ? shore : samples;
                if (edge.Count == 0)
                    continue;
                level = edge[edge.Count / 2] + LakeLift;
                source = "few";
            }
            if (area > 1e6 && Interlocked.Increment(ref _logged) <= 60)
                Log.Notice($"water: tile {tile.X}/{tile.Y} lake {area / 1e6:F1} km2 level {level:F1} m from {source}"
                    + $" (shore {(shore.Count > 0 ? shore[0].ToString("F1") + ".." + shore[^1].ToString("F1") : "-")},"
                    + $" inside {(samples.Count > 0 ? samples[0].ToString("F1") + ".." + samples[^1].ToString("F1") : "-")})");

            int firstVertex = output.Count;
            var flat = new List<double>();
            AddRing(flat, poly.Outer);
            var holeStarts = new List<int>();
            foreach (var hole in poly.Holes)
            {
                holeStarts.Add(flat.Count / 2);
                AddRing(flat, hole);
            }
            List<int> indices;
            try
            {
                indices = Earcut.Triangulate(flat.ToArray(), holeStarts.ToArray());
            }
            catch (Exception e)
            {
                Log.Notice($"water: triangulation failed in {tile.X}/{tile.Y}: {e.Message}");
                continue;
            }
            double maxEdge = MaxEdgeMeters / metersPerUnit;
            var stack = new Stack<(double, double, double, double, double, double)>();
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                stack.Push((flat[2 * a], flat[2 * a + 1], flat[2 * b], flat[2 * b + 1], flat[2 * c], flat[2 * c + 1]));
            }
            while (stack.Count > 0)
            {
                var (ax, ay, bx, by, cx, cy) = stack.Pop();
                double longest = Math.Max(Math.Max(Dist(ax, ay, bx, by), Dist(bx, by, cx, cy)), Dist(cx, cy, ax, ay));
                if (longest > maxEdge && output.Count + stack.Count * 3 < MaxVerticesPerTile)
                {
                    double abx = (ax + bx) / 2, aby = (ay + by) / 2, bcx = (bx + cx) / 2, bcy = (by + cy) / 2, cax = (cx + ax) / 2, cay = (cy + ay) / 2;
                    stack.Push((ax, ay, abx, aby, cax, cay));
                    stack.Push((abx, aby, bx, by, bcx, bcy));
                    stack.Push((cax, cay, bcx, bcy, cx, cy));
                    stack.Push((abx, aby, bcx, bcy, cax, cay));
                    continue;
                }
                output.Add(Vertex(ax, ay, level, 0, 0));
                output.Add(Vertex(bx, by, level, 0, 0));
                output.Add(Vertex(cx, cy, level, 0, 0));
            }
            int edges = 0;
            foreach (var q in poly.Outer)
            {
                if (q.X <= 2) edges |= 1;
                if (q.X >= extent - 2) edges |= 2;
                if (q.Y <= 2) edges |= 4;
                if (q.Y >= extent - 2) edges |= 8;
            }
            tile.Lakes.Add(new LakeInfo { InsideGeo = insideGeo.ToArray(), ShoreGeo = shoreGeo.ToArray(), InsideMedian = samples.Count >= 3 ? samples[samples.Count / 2] : double.NaN, AreaM2 = area, ShoreM = shoreMeters, FirstVertex = firstVertex, VertexCount = output.Count - firstVertex, Shore = shore.ToArray(), Edges = edges, IsRiver = poly.River, Intermittent = poly.Intermittent, HoleGeo = HoleRings(poly) });
        }

        // Rivers.
        foreach (var (points, width) in lines)
        {
            if (output.Count > MaxVerticesPerTile)
                break;
            double halfWidth = width / 2 / metersPerUnit;
            double step = Math.Min(20 / metersPerUnit, 40 / metersPerUnit);
            var stations = Resample(points, step);
            if (stations.Count < 2)
                continue;
            var heights = new double[stations.Count];
            for (int k = 0; k < stations.Count; k++)
                heights[k] = Height(stations[k].X, stations[k].Y);
            for (int pass = 0; pass < 2; pass++)
            {
                var smooth = (double[])heights.Clone();
                for (int k = 1; k + 1 < heights.Length; k++)
                    smooth[k] = (heights[k - 1] + 2 * heights[k] + heights[k + 1]) / 4;
                heights = smooth;
            }
            double lift = RiverLift + width * 0.01;
            var left = new WaterVertex[stations.Count];
            var right = new WaterVertex[stations.Count];
            for (int k = 0; k < stations.Count; k++)
            {
                var a = stations[Math.Max(k - 1, 0)];
                var b = stations[Math.Min(k + 1, stations.Count - 1)];
                double tx = b.X - a.X, ty = b.Y - a.Y, len = Math.Sqrt(tx * tx + ty * ty);
                if (len < 1e-9)
                    len = 1;
                double nx = -ty / len * halfWidth, ny = tx / len * halfWidth;
                var c = stations[k];
                left[k] = Vertex(c.X + nx, c.Y + ny, heights[k] + lift, -1, 1);
                right[k] = Vertex(c.X - nx, c.Y - ny, heights[k] + lift, 1, 1);
            }
            for (int k = 0; k + 1 < stations.Count; k++)
            {
                var mid = new Footprint.P((stations[k].X + stations[k + 1].X) / 2, (stations[k].Y + stations[k + 1].Y) / 2);
                if (heights[k] + lift <= SeaMargin || lakes.Any(l => l.Contains(mid.X, mid.Y)))
                    continue;                                              // the sea, or inside a lake
                output.Add(left[k]);
                output.Add(right[k]);
                output.Add(left[k + 1]);
                output.Add(right[k]);
                output.Add(right[k + 1]);
                output.Add(left[k + 1]);
            }
        }

        tile.Vertices = output.ToArray();
        tile.Count = output.Count;
    }

    /// <summary>Game terrain at or above this height (m) inside an ocean polygon is land sticking out of the real sea.</summary>
    public const double SeaLandMeters = 0.3;

    /// <summary>True if no sample point of the polygon (16 x 16 over its box) has game terrain at or above SeaLandMeters: the sea is there already.</summary>
    private static bool OceanHasNoLand(Poly poly, Func<double, double, double> height)
    {
        const int grid = 16;
        for (int gy = 0; gy < grid; gy++)
        {
            for (int gx = 0; gx < grid; gx++)
            {
                double px = poly.MinX + (poly.MaxX - poly.MinX) * (gx + 0.5) / grid, py = poly.MinY + (poly.MaxY - poly.MinY) * (gy + 0.5) / grid;
                if (poly.Contains(px, py) && height(px, py) >= SeaLandMeters)
                    return false;
            }
        }
        return true;
    }

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    private static double Mod(double v, double m) => v - Math.Floor(v / m) * m;

    private static void AddRing(List<double> flat, List<Footprint.P> ring)
    {
        int n = ring.Count;
        if (n > 1 && ring[0] == ring[n - 1])
            n--;
        for (int i = 0; i < n; i++)
        {
            flat.Add(ring[i].X);
            flat.Add(ring[i].Y);
        }
    }

    /// <summary>The line's points with extra ones so none is farther apart than step.</summary>
    private static List<Footprint.P> Resample(List<Footprint.P> line, double step)
    {
        var result = new List<Footprint.P> { line[0] };
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            double d = Dist(a.X, a.Y, b.X, b.Y);
            int parts = Math.Max(1, (int)Math.Ceiling(d / step));
            for (int k = 1; k <= parts; k++)
                result.Add(new Footprint.P(a.X + (b.X - a.X) * k / parts, a.Y + (b.Y - a.Y) * k / parts));
        }
        return result;
    }
}
