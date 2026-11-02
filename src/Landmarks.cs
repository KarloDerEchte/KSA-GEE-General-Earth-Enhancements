namespace GEE;

/// <summary>A well-known building drawn with its own model and facade texture (tools/landmark_assets.py).</summary>
/// <param name="AngleDeg">Direction of the model's AxisA, counter-clockwise from east (as Box2.Angle).</param>
public sealed record Landmark(string Name, double Lat, double Lon, double AngleDeg, double LengthA, double LengthB, double Height,
    double BaseHeight, double SurroundMeters);

/// <summary>
/// Landmarks replace the OSM boxes at their place: OSM outlines centred inside a landmark are cut down to
/// BaseHeight (they still cover annexes the landmark model leaves out), and those within SurroundMeters of
/// it get a light facade unless OSM gives a colour. Kept free of game types so it can be tested offline.
/// </summary>
public static class Landmarks
{
    /// <summary>The order must match the landmark materials in Assets.xml, from Buildings.FirstLandmarkVariant on.</summary>
    public static readonly Landmark[] All =
    {
        // Vehicle Assembly Building, Kennedy Space Center: the high bay of OSM way 269299040, 157 x 137 m and
        // 160 m tall. AxisA runs west to east along the south face (flag and NASA logo); the low bays to the
        // south stay OSM building parts.
        new("Vehicle Assembly Building", 28.5861162, -80.651013, 13.295, 157.16, 136.82, 160, 20, 120),
        // Steel launch towers of LC-39A (OSM ways 569088673 and 1445875980): open lattice models. Their OSM
        // outlines are dropped (BaseHeight 0); sizes are the outlines' minimum-area rectangles.
        new("LC-39A Fixed Service Structure", 28.6082656, -80.6044304, 89.7, 22.02, 13.27, 100, 0, 0),
        new("LC-39A Integration Tower", 28.6082476, -80.6011324, 57.33, 11.33, 11.31, 144.5, 0, 0),
        // The water tower beside the pad (OSM node, 80.5 m; the box holds the 17 m tank) and SpaceX's Horizontal
        // Integration Facility (OSM way 475501780, whose outline is dropped).
        new("LC-39A water tower", 28.6093588, -80.6030832, 0, 20, 20, 80.5, 0, 0),
        // Pad 39B: the lightning tower from the tiles (147 m, 22 x 24 m footprint).
        new("LC-39B lightning tower", 28.6275564, -80.6195819, 0, 22, 24.1, 147, 0, 0),
        // Starbase: launch tower 1 (OSM way 968227813, 143 m), 32 m west of the game's pad "Starbase OLM A"; the catch
        // arms (+B) point at the pad. Tower 2 is a copy.
        new("Starbase launch tower 1", 25.9961259, -97.1547359, -86.4, 13, 13, 143, 0, 0),
        // Ariane 6 pad ELA-4 (Kourou): the four lightning masts (OSM nodes) around the launch table, centred on the
        // game's pad (the table is 30 m from the game's coordinate), about 115 m tall after the CNES/ESA photo; the
        // mobile gantry (OSM way 514549015, 90 m) in its parked position north of the table, bay toward the pad; and
        // the concrete water tower (OSM way 666132848).
        new("ELA-4 lightning mast", 5.2645052, -52.7916748, 19.7, 4.5, 4.5, 115, 0, 0),
        new("ELA-4 mobile gantry", 5.2659869, -52.7924831, 200.7, 44.1, 68.1, 90, 0, 0),
        new("ELA-4 water tower", 5.2661577, -52.7907852, 0, 15, 15, 68, 0, 0),
        // Rocket Lab Launch Complex 1 (Mahia): the slim white lightning masts beside pads A and B (OSM nodes).
        new("LC-1A lightning mast", -39.2614679, 177.8654784, 0, 2, 2, 22, 0, 0),
        new("SpaceX Horizontal Integration Facility", 28.6040886, -80.604168, 89.98, 94.53, 47.81, 26, 0, 0),
    };

    /// <summary>
    /// More of the same landmark models elsewhere: index into All for the model, the place, and a size (0 keeps the
    /// model's): pad 39B's water tower (the tiles draw it as stacked pieces) and its other two lightning towers,
    /// Starbase's second launch tower, the other ELA-4 lightning masts, the other masts at Mahia, and the launch tower
    /// at Vandenberg SLC-4E (OSM way 568768533, 70 m; moved from 11 m to 24 m off the game's pad so it stands clear
    /// of the pad deck).
    /// </summary>
    public static readonly (int Model, double Lat, double Lon, double AngleDeg, double LengthA, double LengthB, double Height)[] Copies =
    {
        (3, 28.6282300, -80.6198250, 0, 0, 0, 0),
        (4, 28.6261461, -80.6219153, 0, 0, 0, 0),
        (4, 28.6278169, -80.6223349, 0, 0, 0, 0),
        (5, 25.9970432, -97.1580678, -86.4, 12, 12, 144.5),
        (6, 5.2652067, -52.7919276, 19.7, 0, 0, 0),
        (6, 5.2649189, -52.7927314, 19.7, 0, 0, 0),
        (6, 5.2642085, -52.7924786, 19.7, 0, 0, 0),
        (9, -39.2615398, 177.8647582, 0, 0, 0, 0),
        (9, -39.2611981, 177.8657223, 0, 0, 0, 0),
        (9, -39.2606945, 177.8652461, 0, 0, 0, 0),
        (6, 34.6322603, -120.6105760, 0, 8, 8, 70),
    };

    /// <summary>The landmark models and their copies, as landmarks.</summary>
    public static IEnumerable<(int Model, Landmark Landmark)> Placed()
    {
        for (int i = 0; i < All.Length; i++)
            yield return (i, All[i]);
        foreach (var (model, lat, lon, angle, a, b, h) in Copies)
        {
            Landmark l = All[model];
            yield return (model, l with
            {
                Lat = lat, Lon = lon, AngleDeg = angle,
                LengthA = a > 0 ? a : l.LengthA, LengthB = b > 0 ? b : l.LengthB, Height = h > 0 ? h : l.Height,
            });
        }
    }

    /// <summary>
    /// Launch complexes: ordinary buildings within the radius are plant, hangars and support buildings, so they
    /// get white and grey facades and flat roofs instead of the houses' red gables. Centres are the game's launch
    /// pads (Content/Core/Astronomicals.xml).
    /// </summary>
    private static readonly (double Lat, double Lon, double RadiusM)[] Compounds =
    {
        (28.60829876577433, -80.60412690984597, 3000),
        (34.6320535004723, -120.6106513816658, 3000),
        (25.996143841726674, -97.15441574026951, 2500),
        (5.264709828776202, -52.792203100187855, 3000),
        (-39.26157579085742, 177.86515315917015, 2000),
    };

    private const string SurroundColour = "white";

    /// <summary>
    /// Lowers or recolours an OSM building near a landmark. A building is lowered when one of its boxes is
    /// centred inside the landmark: outlines around a landmark and its annexes often have their centre in
    /// the annex.
    /// </summary>
    public static bool Adjust(RawBuilding building, string? colour, OsmOptions options)
    {
        if (Structures.Covers(building.Lat, building.Lon))
            return false;
        InCompound(building, colour, options);
        foreach (var (_, l) in Placed())
        {
            double nearest = double.MaxValue;
            foreach (Box2 box in building.Boxes)
            {
                var (a, b) = Local(l, building.Lat, building.Lon, box.CenterEast, box.CenterNorth, options.PlanetRadius);
                double outsideA = Math.Max(Math.Abs(a) - l.LengthA / 2, 0), outsideB = Math.Max(Math.Abs(b) - l.LengthB / 2, 0);
                nearest = Math.Min(nearest, Math.Sqrt(outsideA * outsideA + outsideB * outsideB));
            }
            if (nearest == 0)
            {
                // Nothing of the outline is left where the landmark model replaces the whole structure.
                if (l.BaseHeight <= 0)
                    return false;
                building.Height = Math.Min(building.Height, l.BaseHeight);
                building.MinHeight = 0;
            }
            if (colour == null && nearest < l.SurroundMeters)
                building.Variant = Buildings.Variant(building.Id, SurroundColour, building.Height, 0);
        }
        return true;
    }

    /// <summary>
    /// Buildings of a launch complex, by size, after photos of the support area at LC-39A: small concrete utility
    /// shelters with a steel door, pre-engineered metal halls (corrugated walls, roll-up doors, a high window band and
    /// a low metal gable roof) and offices with ribbon windows.
    /// </summary>
    private static void InCompound(RawBuilding building, string? colour, OsmOptions options)
    {
        if (building.Variant >= Buildings.RoadVariant)
            return;
        double k = Math.PI / 180 * options.PlanetRadius;
        foreach (var (lat, lon, radius) in Compounds)
        {
            double north = (building.Lat - lat) * k;
            double east = (building.Lon - lon) * k * Math.Cos(lat * Math.PI / 180);
            if (north * north + east * east > radius * radius)
                continue;
            if (building.RoofHeight > 0)
            {
                building.Height += building.RoofHeight;
                building.RoofHeight = 0;
            }
            double area = building.Boxes.Sum(b => b.LengthA * b.LengthB);
            uint h = (uint)(building.Id ^ (building.Id >> 32));
            if (area < 120 || building.Height < 4)
            {
                building.Style = Buildings.StyleShelter;
                if (colour == null)
                    building.Variant = 0;
            }
            else if (building.Height >= 7 && area < 1500 && h % 3 == 0)
            {
                building.Style = Buildings.StyleRibbon;
                if (colour == null)
                    building.Variant = h % 2 == 0 ? 1 : 0;
            }
            else
            {
                building.Style = Buildings.StyleMetalHall;
                if (colour == null)
                    building.Variant = 6;
                if (building.Boxes.Count == 1 && building.MinHeight <= 0)
                {
                    Box2 box = building.Boxes[0];
                    building.RoofHeight = 0.09 * Math.Min(box.LengthA, box.LengthB);
                    building.RoofVariant = Buildings.MetalRoofVariant;
                }
            }
            return;
        }
    }

    /// <summary>True within one of the launch complexes (Compounds).</summary>
    public static bool InAnyCompound(double lat, double lon)
    {
        const double k = Math.PI / 180 * 6371000;
        foreach (var (cLat, cLon, radius) in Compounds)
        {
            double north = (lat - cLat) * k, east = (lon - cLon) * k * Math.Cos(cLat * Math.PI / 180);
            if (north * north + east * east <= radius * radius)
                return true;
        }
        return false;
    }

    /// <summary>True for the LC-39A fixed service structure (it carries the swinging crew access arm).</summary>
    public static bool IsFixedServiceStructure(long buildingId)
    {
        int index = (int)(-1 - buildingId);
        return buildingId < 0 && index < All.Length && All[index].Name == "LC-39A Fixed Service Structure";
    }

    /// <summary>True for the tall launch complex structures (they get beacons and platform lights at night).</summary>
    public static bool IsTower(long buildingId)
    {
        int index = (int)(-1 - buildingId);
        return buildingId < 0 && index < All.Length && All[index].Height >= 60 && index != 0;   // not the VAB
    }

    /// <summary>Adds the landmarks whose centre lies in the tile, so each is placed exactly once.</summary>
    public static void AddTo(TileData data, int z, int x, int y)
    {
        var (s, w, n, e) = VectorTile.Bounds(z, x, y);
        foreach (var (model, l) in Placed())
        {
            if (l.Lat < s || l.Lat >= n || l.Lon < w || l.Lon >= e)
                continue;
            data.Buildings.Add(new RawBuilding
            {
                LaunchComplex = true,
                // The model's id, so a copy gets the same lights; distinct per place through Lat/Lon.
                Id = -1 - model,
                Lat = l.Lat,
                Lon = l.Lon,
                Height = l.Height,
                Variant = Buildings.FirstLandmarkVariant + model,
                Style = Buildings.StyleNone,
                Boxes = { new Box2(0, 0, l.AngleDeg * Math.PI / 180, l.LengthA, l.LengthB) },
            });
        }
    }

    /// <summary>Position in meters along the landmark's AxisA and AxisB of a point east/north of (lat, lon).</summary>
    private static (double A, double B) Local(Landmark l, double lat, double lon, double east, double north, double radius)
    {
        double k = Math.PI / 180 * radius;
        east += (lon - l.Lon) * k * Math.Cos(l.Lat * Math.PI / 180);
        north += (lat - l.Lat) * k;
        double angle = l.AngleDeg * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        return (east * cos + north * sin, -east * sin + north * cos);
    }
}
