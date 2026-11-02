namespace GEE;

public sealed class TileData
{
    public List<RawBuilding> Buildings = new();
    public List<RawRoad> Roads = new();
}

/// <summary>
/// Reads the "building" and "transportation" layers of a Mapbox Vector Tile (OpenMapTiles schema, as served by OpenFreeMap).
/// Kept free of game types so it can be tested offline.
/// </summary>
public static class VectorTile
{
    public static (double South, double West, double North, double East) Bounds(int z, int x, int y)
    {
        var (north, west) = ToLatLon(z, x, y);
        var (south, east) = ToLatLon(z, x + 1, y + 1);
        return (south, west, north, east);
    }

    public static (int X, int Y) TileAt(int z, double lat, double lon)
    {
        int n = 1 << z;
        double latRad = Math.Clamp(lat, -85.05, 85.05) * Math.PI / 180;
        int x = (int)Math.Floor((lon + 180) / 360 * n);
        int y = (int)Math.Floor((1 - Math.Asinh(Math.Tan(latRad)) / Math.PI) / 2 * n);
        return (Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }

    internal static (double Lat, double Lon) ToLatLon(int z, double x, double y)
    {
        double n = 1 << z;
        double lon = x / n * 360 - 180;
        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180 / Math.PI;
        return (lat, lon);
    }

    /// <summary>
    /// Decodes the buildings and roads of one tile. Geometry is clipped to the tile edge, so a building or road
    /// crossing tiles is assembled from the pieces of each tile without doubling up.
    /// </summary>
    public static TileData Parse(byte[] data, int z, int x, int y, OsmOptions options)
    {
        var result = new TileData();
        var tile = new Reader(data);
        while (tile.Next(out int field, out int wire))
        {
            if (field == 3 && wire == 2)
                ReadLayer(tile.Bytes(), z, x, y, options, result);
            else
                tile.Skip(wire);
        }
        Landmarks.AddTo(result, z, x, y);
        Pipelines.AddTo(result, z, x, y);
        Structures.AddTo(result, z, x, y);
        RoadMerges.Apply(result.Roads, options.PlanetRadius);
        RoadBridges.Apply(result.Roads, options.PlanetRadius, Placement.BridgeClearance);
        RoadJunctions.Find(result.Roads);
        return result;
    }

    /// <summary>Feature of any layer: geometry type (1 point, 2 line, 3 polygon), tags, encoded geometry and extent.</summary>
    public delegate void FeatureHandler(string layer, int type, Dictionary<string, object?> tags, List<uint> geometry, int extent);

    /// <summary>Calls handle for every feature of the layers wanted.</summary>
    public static void ForEachFeature(byte[] data, Func<string, bool> wanted, FeatureHandler handle)
    {
        var tile = new Reader(data);
        while (tile.Next(out int field, out int wire))
        {
            if (field != 3 || wire != 2)
            {
                tile.Skip(wire);
                continue;
            }
            ReadOnlySpan<byte> layerBytes = tile.Bytes();
            string name = "";
            int extent = 4096;
            var keys = new List<string>();
            var values = new List<object?>();
            var features = new List<(int Start, int Length)>();
            var layer = new Reader(layerBytes);
            while (layer.Next(out int lf, out int lw))
            {
                switch (lf)
                {
                    case 1 when lw == 2: name = layer.String(); break;
                    case 2 when lw == 2: features.Add(layer.Range()); break;
                    case 3 when lw == 2: keys.Add(layer.String()); break;
                    case 4 when lw == 2: values.Add(ReadValue(layer.Bytes())); break;
                    case 5 when lw == 0: extent = (int)layer.Varint(); break;
                    default: layer.Skip(lw); break;
                }
            }
            if (!wanted(name))
                continue;
            foreach (var (start, length) in features)
            {
                var feature = new Reader(layerBytes.Slice(start, length));
                int type = 0;
                List<uint>? tagIndices = null, geometry = null;
                while (feature.Next(out int ff, out int fw))
                {
                    switch (ff)
                    {
                        case 2 when fw == 2: tagIndices = feature.Packed(); break;
                        case 3 when fw == 0: type = (int)feature.Varint(); break;
                        case 4 when fw == 2: geometry = feature.Packed(); break;
                        default: feature.Skip(fw); break;
                    }
                }
                if (geometry == null)
                    continue;
                var tags = new Dictionary<string, object?>();
                for (int i = 0; tagIndices != null && i + 1 < tagIndices.Count; i += 2)
                {
                    if (tagIndices[i] < keys.Count && tagIndices[i + 1] < values.Count)
                        tags[keys[(int)tagIndices[i]]] = values[(int)tagIndices[i + 1]];
                }
                handle(name, type, tags, geometry, extent);
            }
        }
    }

    /// <summary>Points of a point feature, in tile coordinates.</summary>
    internal static List<Footprint.P> Points(List<uint> geometry)
    {
        var points = new List<Footprint.P>();
        foreach (var line in Lines(geometry))
            points.AddRange(line);
        return points;
    }

    private static void ReadLayer(ReadOnlySpan<byte> layerBytes, int z, int tx, int ty, OsmOptions options, TileData result)
    {
        string name = "";
        int extent = 4096;
        var keys = new List<string>();
        var values = new List<object?>();
        var features = new List<(int Start, int Length)>();
        var layer = new Reader(layerBytes);
        while (layer.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2: name = layer.String(); break;
                case 2 when wire == 2: features.Add(layer.Range()); break;
                case 3 when wire == 2: keys.Add(layer.String()); break;
                case 4 when wire == 2: values.Add(ReadValue(layer.Bytes())); break;
                case 5 when wire == 0: extent = (int)layer.Varint(); break;
                default: layer.Skip(wire); break;
            }
            if (field == 1 && name is not ("building" or "transportation" or "aeroway"))
                return;
        }

        (double Lat, double Lon) ToGeo(Footprint.P p) => ToLatLon(z, tx + p.X / extent, ty + p.Y / extent);

        foreach (var (start, length) in features)
        {
            var feature = new Reader(layerBytes.Slice(start, length));
            long id = 0;
            int type = 0;
            List<uint>? tagIndices = null, geometry = null;
            while (feature.Next(out int field, out int wire))
            {
                switch (field)
                {
                    case 1 when wire == 0: id = (long)feature.Varint(); break;
                    case 2 when wire == 2: tagIndices = feature.Packed(); break;
                    case 3 when wire == 0: type = (int)feature.Varint(); break;
                    case 4 when wire == 2: geometry = feature.Packed(); break;
                    default: feature.Skip(wire); break;
                }
            }
            if (geometry == null)
                continue;
            var tags = new Dictionary<string, object?>();
            for (int i = 0; tagIndices != null && i + 1 < tagIndices.Count; i += 2)
            {
                if (tagIndices[i] < keys.Count && tagIndices[i + 1] < values.Count)
                    tags[keys[(int)tagIndices[i]]] = values[(int)tagIndices[i + 1]];
            }

            if (name == "building" && type == 3)
            {
                // hide_3d marks outlines whose building:part pieces are in the layer as well.
                if (tags.GetValueOrDefault("hide_3d") is true)
                    continue;
                double height = tags.GetValueOrDefault("render_height") is double h ? h : 0;
                double minHeight = tags.GetValueOrDefault("render_min_height") is double mh ? mh : 0;
                foreach (var ring in ExteriorRings(geometry))
                {
                    var clipped = ClipToTile(ring, extent);
                    if (clipped.Count < 3)
                        continue;
                    string? colour = tags.GetValueOrDefault("colour") as string;
                    var building = Buildings.FromRing(id, clipped.Select(ToGeo).ToList(), height, minHeight, colour, options);
                    if (building == null)
                        continue;
                    if (!Landmarks.Adjust(building, colour, options))
                        continue;
                    result.Buildings.Add(building);
                }
            }
            else if (name == "transportation" && type == 2 && Roads.Classify(tags) is { } road)
            {
                AddLines(road.Width, road.Rank, road.Bridge, road.Lit, Airfield.None, road.Surface);
            }
            else if (name == "aeroway" && type == 2 && tags.GetValueOrDefault("class") is "runway" or "taxiway")
            {
                bool runway = tags.GetValueOrDefault("class") is "runway";
                // The tiles carry no widths: 45 m is the common runway width, taxiways are about 20 m.
                AddLines(runway ? 45 : 20, runway ? 8 : 7, false, false, runway ? Airfield.Runway : Airfield.Taxiway);
            }
            else if (name == "aeroway" && type == 3 && tags.GetValueOrDefault("class") is "apron")
            {
                foreach (var ring in ExteriorRings(geometry))
                {
                    var clipped = ClipToTile(ring, extent);
                    if (clipped.Count < 3 || Buildings.FromRing(id, clipped.Select(ToGeo).ToList(), 0, 0, null, options) is not { } apron)
                        continue;
                    apron.Slab = true;
                    apron.Variant = Buildings.ApronVariant;
                    apron.RoofHeight = 0;
                    apron.Style = Buildings.StyleNone;
                    result.Buildings.Add(apron);
                }
            }

            void AddLines(double width, int rank, bool bridge, bool lit, Airfield airfield, RoadSurface surface = RoadSurface.Road)
            {
                foreach (var line in Lines(geometry))
                {
                    foreach (var (piece, startCut, endCut) in ClipLineToTile(line, extent))
                    {
                        // Tie-break from the line's end points: same-class roads meeting at a junction get
                        // different heights instead of flickering where they overlap.
                        Footprint.P first = line[0], last = line[^1];
                        uint seed = (uint)(int)first.X * 73856093u ^ (uint)(int)first.Y * 19349663u
                            ^ (uint)(int)last.X * 83492791u ^ (uint)(int)last.Y * 2654435761u;
                        seed = (seed ^ (seed >> 15)) * 0x5bd1e995u;
                        int tie = (int)((seed >> 16) % Roads.TieLevels);
                        result.Roads.Add(new RawRoad
                        {
                            Points = piece.Select(ToGeo).ToList(),
                            Width = width,
                            Lift = Roads.Lift(rank, tie),
                            Bridge = bridge,
                            Lit = lit,
                            Rank = rank,
                            Airfield = airfield,
                            Surface = surface,
                            StartAtTileEdge = startCut,
                            EndAtTileEdge = endCut,
                        });
                    }
                }
            }
        }
    }

    internal static List<List<Footprint.P>> Lines(List<uint> geometry)
    {
        var lines = new List<List<Footprint.P>>();
        List<Footprint.P>? line = null;
        int cx = 0, cy = 0, i = 0;
        while (i < geometry.Count)
        {
            uint command = geometry[i++];
            int id = (int)(command & 7), count = (int)(command >> 3);
            if (id == 7)
                continue;
            for (int k = 0; k < count && i + 1 < geometry.Count; k++)
            {
                cx += ZigZag(geometry[i++]);
                cy += ZigZag(geometry[i++]);
                if (id == 1)
                {
                    line = new List<Footprint.P>();
                    lines.Add(line);
                }
                line?.Add(new Footprint.P(cx, cy));
            }
        }
        return lines;
    }

    /// <summary>
    /// Cuts a polyline to the tile square (Liang-Barsky per segment), splitting it where it leaves and re-enters.
    /// The flags tell which ends of a piece were cut at the tile edge.
    /// </summary>
    private static List<(List<Footprint.P> Points, bool StartCut, bool EndCut)> ClipLineToTile(List<Footprint.P> line, int extent)
    {
        var pieces = new List<(List<Footprint.P> Points, bool StartCut, bool EndCut)>();
        List<Footprint.P>? current = null;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            Footprint.P a = line[i], b = line[i + 1];
            double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
            bool inside = Clip(-dx, a.X, ref t0, ref t1) && Clip(dx, extent - a.X, ref t0, ref t1)
                && Clip(-dy, a.Y, ref t0, ref t1) && Clip(dy, extent - a.Y, ref t0, ref t1);
            if (!inside)
            {
                current = null;
                continue;
            }
            var p0 = new Footprint.P(a.X + dx * t0, a.Y + dy * t0);
            var p1 = new Footprint.P(a.X + dx * t1, a.Y + dy * t1);
            if (current == null || t0 > 0)
            {
                current = new List<Footprint.P> { p0 };
                pieces.Add((current, t0 > 0, false));
            }
            current.Add(p1);
            if (t1 < 1)
            {
                pieces[^1] = (current, pieces[^1].StartCut, true);
                current = null;
            }
        }
        return pieces;
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0)
            return q >= 0;
        double t = q / p;
        if (p < 0)
        {
            if (t > t1) return false;
            if (t > t0) t0 = t;
        }
        else
        {
            if (t < t0) return false;
            if (t < t1) t1 = t;
        }
        return true;
    }

    /// <summary>Exterior rings have positive area in tile coordinates (y down); holes are dropped.</summary>
    internal static List<List<Footprint.P>> ExteriorRings(List<uint> geometry)
    {
        var rings = new List<List<Footprint.P>>();
        List<Footprint.P>? ring = null;
        int cx = 0, cy = 0, i = 0;
        while (i < geometry.Count)
        {
            uint command = geometry[i++];
            int id = (int)(command & 7), count = (int)(command >> 3);
            if (id == 7)
            {
                if (ring is { Count: >= 3 } && Footprint.SignedArea(ring) > 0)
                    rings.Add(ring);
                ring = null;
                continue;
            }
            for (int k = 0; k < count && i + 1 < geometry.Count; k++)
            {
                cx += ZigZag(geometry[i++]);
                cy += ZigZag(geometry[i++]);
                if (id == 1)
                    ring = new List<Footprint.P>();
                ring?.Add(new Footprint.P(cx, cy));
            }
        }
        return rings;
    }

    private static List<Footprint.P> ClipToTile(List<Footprint.P> ring, int extent)
    {
        IReadOnlyList<Footprint.P> poly = ring;
        poly = Footprint.ClipHalfPlane(poly, new(0, 0), new(1, 0));
        poly = Footprint.ClipHalfPlane(poly, new(0, 0), new(0, 1));
        poly = Footprint.ClipHalfPlane(poly, new(extent, extent), new(-1, 0));
        return Footprint.ClipHalfPlane(poly, new(extent, extent), new(0, -1));
    }

    private static int ZigZag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    private static long ZigZag64(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    private static object? ReadValue(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes);
        object? value = null;
        while (r.Next(out int field, out int wire))
        {
            value = (field, wire) switch
            {
                (1, 2) => r.String(),
                (2, 5) => (double)BitConverter.Int32BitsToSingle((int)r.Fixed32()),
                (3, 1) => BitConverter.Int64BitsToDouble((long)r.Fixed64()),
                (4, 0) => (double)(long)r.Varint(),
                (5, 0) => (double)r.Varint(),
                (6, 0) => (double)ZigZag64(r.Varint()),
                (7, 0) => r.Varint() != 0,
                _ => SkipValue(ref r, wire, value),
            };
        }
        return value;
    }

    private static object? SkipValue(ref Reader r, int wire, object? keep)
    {
        r.Skip(wire);
        return keep;
    }

    /// <summary>Minimal protobuf reader.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public bool Next(out int field, out int wire)
        {
            if (_pos >= _data.Length)
            {
                field = wire = 0;
                return false;
            }
            ulong key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong result = 0;
            for (int shift = 0; _pos < _data.Length; shift += 7)
            {
                byte b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    break;
            }
            return result;
        }

        public uint Fixed32()
        {
            uint v = BitConverter.ToUInt32(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public ulong Fixed64()
        {
            ulong v = BitConverter.ToUInt64(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
        }

        public (int Start, int Length) Range()
        {
            int length = (int)Varint();
            var range = (_pos, length);
            _pos += length;
            return range;
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var (start, length) = Range();
            return _data.Slice(start, length);
        }

        public string String() => System.Text.Encoding.UTF8.GetString(Bytes());

        public List<uint> Packed()
        {
            var inner = new Reader(Bytes());
            var list = new List<uint>();
            while (inner._pos < inner._data.Length)
                list.Add((uint)inner.Varint());
            return list;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: _pos += 8; break;
                case 2: Range(); break;
                case 5: _pos += 4; break;
                default: _pos = _data.Length; break;
            }
        }
    }
}
