using Brutal.Numerics;

namespace GEE;

/// <summary>
/// One building box in planet-fixed (CCF) coordinates: the unit box mesh (x up in [0, 1], y and z in
/// [-0.5, 0.5]) is scaled and oriented by Up/AxisA/AxisB and sits on Base. Center is the ground point
/// used for distance culling.
/// </summary>
public struct BuildingInstance
{
    public double3 Up;
    public double3 AxisA;
    public double3 AxisB;
    public double3 Base;
    public double3 Center;
    public int Variant;
    public bool IsRoad;
    /// <summary>Relief piece of a building (BuildingDetail): drawn only near the camera, never clears clutter.</summary>
    public bool IsDetail;
    /// <summary>A cryogenic tank of a launch complex: vapour boils off it (VaporRenderer).</summary>
    public bool Vents;
    /// <summary>Window pattern (Buildings.Style*), per-building random seed and the ground's height above Base.</summary>
    public int Style;
    public uint Seed;
    public float GroundOffset;
    /// <summary>What the instance belongs to (Layers), so each kind can be hidden on its own.</summary>
    public byte Layer;
}

/// <summary>BuildingInstance.Layer values.</summary>
public static class Layers
{
    /// <summary>OSM buildings with their roofs and details.</summary>
    public const byte Building = 0;
    /// <summary>Roads, airfield surfaces and reflector posts.</summary>
    public const byte Street = 1;
    /// <summary>The modelled launch complexes: landmark towers, VAB, hangar, pipelines, tanks and tent from data.</summary>
    public const byte LaunchComplex = 2;
}

/// <summary>A light along a road or airfield surface, in CCF.</summary>
public readonly record struct RoadLight(double3 Position, uint Color, float Radius, uint Seed);

/// <summary>Planet-fixed geometry for building boxes. Uses no game types, so it can be tested offline.</summary>
public static class Placement
{
    public static double3 DirCcf(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        return new double3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }

    /// <summary>Same east/north convention as LocationReference.GetAxesCcf.</summary>
    public static void Axes(double3 up, out double3 east, out double3 north)
    {
        double3 south = double3.Normalize(double3.Cross(double3.Cross(new double3(0, 0, 1), up), up));
        east = double3.Cross(up, south);
        north = -south;
    }

    /// <summary>
    /// Stands one box of a building on the terrain. terrain returns the height above the mean radius for a
    /// CCF direction; the box reaches down to its lowest corner.
    /// </summary>
    public static BuildingInstance MakeInstance(RawBuilding b, Box2 box, double radius, double foundation, Func<double3, double> terrain)
    {
        double3 refDir = DirCcf(b.Lat, b.Lon);
        Axes(refDir, out double3 east, out double3 north);
        double3 up = double3.Normalize(refDir * radius + east * box.CenterEast + north * box.CenterNorth);
        double3 a = east * Math.Cos(box.Angle) + north * Math.Sin(box.Angle);
        a = double3.Normalize(a - up * double3.Dot(a, up));
        double3 bAxis = double3.Cross(up, a);

        double hCenter = terrain(up);
        double hMin = hCenter, hMax = hCenter;
        for (int i = 0; i < 4; i++)
        {
            double sa = (i & 1) == 0 ? -0.5 : 0.5, sb = (i & 2) == 0 ? -0.5 : 0.5;
            double h = terrain(double3.Normalize(up * radius + a * (sa * box.LengthA) + bAxis * (sb * box.LengthB)));
            hMin = Math.Min(hMin, h);
            hMax = Math.Max(hMax, h);
        }

        // Roofs stay above sea level where the game's coastline differs from the real one.
        double ground = Math.Max(hCenter, 0);
        double topHeight = Math.Max(ground + b.Height, hMax + 2);
        double baseHeight = b.MinHeight > 0 ? Math.Min(ground + b.MinHeight, topHeight - 1) : hMin - foundation;
        return new BuildingInstance
        {
            Up = up * (topHeight - baseHeight),
            AxisA = a * box.LengthA,
            AxisB = bAxis * box.LengthB,
            Base = up * (radius + baseHeight),
            Center = up * (radius + hCenter),
            Variant = b.Variant,
            Style = b.Variant < Buildings.RoadVariant ? b.Style : Buildings.StyleNone,
            Seed = LightSet.Hash(b.Id, (long)(b.Lat * 1e6), (long)(b.Lon * 1e6)),
            GroundOffset = (float)(ground - baseHeight),
        };
    }

    /// <summary>Dome on a round tank: the cap mesh is a hemisphere of the tank's diameter, flattened to a bit under half of it.</summary>
    public static BuildingInstance MakeDome(in BuildingInstance wall)
    {
        double3 up = double3.Normalize(wall.Up);
        return new BuildingInstance
        {
            Up = up * (0.4 * Length(wall.AxisA)),
            AxisA = wall.AxisA,
            AxisB = wall.AxisB,
            Base = wall.Base + wall.Up - up * 0.02,
            Center = wall.Center,
            Variant = Buildings.TankCapVariant,
            Style = Buildings.StyleNone,
        };
    }

    /// <summary>
    /// Gabled roof on a wall box, ridge along its longer side, with a little overhang. Swapping the axes
    /// for the ridge turns them by 90 degrees, so the mesh keeps its handedness.
    /// </summary>
    public static BuildingInstance MakeRoof(in BuildingInstance wall, RawBuilding b)
    {
        double3 up = double3.Normalize(wall.Up);
        double lengthA = Length(wall.AxisA), lengthB = Length(wall.AxisB);
        bool alongA = lengthA >= lengthB;
        double3 ridge = alongA ? wall.AxisA : wall.AxisB;
        double3 across = alongA ? wall.AxisB : -wall.AxisA;
        double ridgeLength = Math.Max(lengthA, lengthB), acrossLength = Math.Min(lengthA, lengthB);
        return new BuildingInstance
        {
            Up = up * b.RoofHeight,
            AxisA = ridge * ((ridgeLength + 0.6) / ridgeLength),
            AxisB = across * ((acrossLength + 0.8) / acrossLength),
            // Slightly sunk into the wall top, which it hides.
            Base = wall.Base + wall.Up - up * 0.02,
            Center = wall.Center,
            Variant = b.RoofVariant,
            Style = Buildings.StyleNone,
        };
    }

    /// <summary>
    /// A flat surface such as an apron: level at its mean terrain height plus lift, reaching down below the
    /// lowest corner. Drawn and cleared like a road.
    /// </summary>
    public static BuildingInstance MakeSlab(RawBuilding b, Box2 box, double radius, double lift, Func<double3, double> terrain)
    {
        double3 refDir = DirCcf(b.Lat, b.Lon);
        Axes(refDir, out double3 east, out double3 north);
        double3 up = double3.Normalize(refDir * radius + east * box.CenterEast + north * box.CenterNorth);
        double3 a = east * Math.Cos(box.Angle) + north * Math.Sin(box.Angle);
        a = double3.Normalize(a - up * double3.Dot(a, up));
        double3 bAxis = double3.Cross(up, a);

        double sum = Math.Max(terrain(up), 0), hMin = sum;
        for (int i = 0; i < 4; i++)
        {
            double sa = (i & 1) == 0 ? -0.5 : 0.5, sb = (i & 2) == 0 ? -0.5 : 0.5;
            double h = Math.Max(terrain(double3.Normalize(up * radius + a * (sa * box.LengthA) + bAxis * (sb * box.LengthB))), 0);
            sum += h;
            hMin = Math.Min(hMin, h);
        }
        double top = sum / 5 + lift, bottom = hMin - RoadDepth;
        return new BuildingInstance
        {
            Up = up * (top - bottom),
            AxisA = a * box.LengthA,
            AxisB = bAxis * box.LengthB,
            Base = up * (radius + bottom),
            Center = up * (radius + top),
            Variant = b.Variant,
            IsRoad = true,
            Layer = Layers.Street,
            Style = Buildings.StyleNone,
        };
    }

    private static double Length(double3 v) => Math.Sqrt(double3.Dot(v, v));

    /// <summary>Longest road piece before refinement.</summary>
    public const double RoadPieceMeters = 16;
    /// <summary>Pieces are halved down to this length while the terrain bulges away from them.</summary>
    private const double RoadMinPieceMeters = 3;
    /// <summary>How far the terrain at a piece's middle may lie off the straight piece.</summary>
    private const double RoadToleranceMeters = 0.08;
    private const double RoadDepth = 1.5;

    /// <summary>
    /// Lays a road on the terrain as thin slabs, each tilted along the slope and across it. terrain should
    /// be the exact height (the game's terrain has detail well below any height grid): pieces start at most
    /// RoadPieceMeters long and are halved where the terrain at their middle bulges away from them. Pieces
    /// reach past their ends only as far as a bend needs to close the gap on its outside, and by half the
    /// width at road ends so junctions are covered; ends at the tile edge meet the neighbouring tile's piece
    /// exactly.
    /// </summary>
    public static void AddRoadInstances(RawRoad road, double radius, Func<double3, double> terrain, Action<BuildingInstance> add,
        Action<RoadLight>? light = null)
    {
        var dirs = new List<double3>();
        var points = RoundedPoints(road, radius);
        for (int i = 0; i < points.Count; i++)
        {
            double3 d = points[i];
            if (i > 0)
            {
                double3 prev = dirs[^1];
                // Repeated points would give bends without a direction.
                if (double3.Dot(d - prev, d - prev) * radius * radius < 0.05 * 0.05)
                    continue;
                int steps = (int)Math.Ceiling(Math.Acos(Math.Clamp(double3.Dot(prev, d), -1, 1)) * radius / RoadPieceMeters);
                for (int s = 1; s < steps; s++)
                    dirs.Add(double3.Normalize(prev + (d - prev) * ((double)s / steps)));
            }
            dirs.Add(d);
        }
        if (dirs.Count < 2)
            return;

        // Surface heights: bridges interpolate between their ends by distance, roads follow the terrain.
        double[] heights;
        if (road.Bridge)
        {
            heights = new double[dirs.Count];
            double total = 0;
            var along = new double[dirs.Count];
            for (int i = 1; i < dirs.Count; i++)
                along[i] = total += Math.Sqrt(double3.Dot(dirs[i] - dirs[i - 1], dirs[i] - dirs[i - 1])) * radius;
            double h0 = Math.Max(terrain(dirs[0]), 0), h1 = Math.Max(terrain(dirs[^1]), 0);
            for (int i = 0; i < dirs.Count; i++)
                heights[i] = total > 0 ? h0 + (h1 - h0) * along[i] / total : h0;
        }
        else
        {
            (dirs, heights) = Refine(dirs, radius, terrain);
        }

        double half = road.Width / 2;
        double Extension(int joint)
        {
            if (joint == 0)
                return road.StartAtTileEdge ? 0 : half;
            if (joint == dirs.Count - 1)
                return road.EndAtTileEdge ? 0 : half;
            double3 a = double3.Normalize(dirs[joint] - dirs[joint - 1]);
            double3 b = double3.Normalize(dirs[joint + 1] - dirs[joint]);
            double cos = Math.Clamp(double3.Dot(a, b), -1, 1);
            // Outer corner of a bend by angle t sits half * tan(t / 2) beyond the joint.
            return Math.Min(half * Math.Sqrt((1 - cos) / (1 + cos + 1e-9)), half) + 0.05;
        }

        // Street lamps 8 m up on alternating sides, every 38 m (50 m on motorways and trunks). Runways have
        // white edge lights every 60 m and green rows across their ends, taxiways blue edge lights every 30 m.
        bool airfield = road.Airfield != Airfield.None;
        double lightSpacing = road.Airfield switch
        {
            Airfield.Runway => 60,
            Airfield.Taxiway => 30,
            _ => road.Rank >= 5 ? 50 : 38,
        };
        double lightAlong = lightSpacing / 2;
        int lightCount = 0;
        bool lights = light != null && (road.Lit || airfield);
        int variant = road.Airfield switch
        {
            Airfield.Runway => Buildings.RunwayVariant,
            Airfield.Taxiway => Buildings.TaxiwayVariant,
            _ => Buildings.SurfaceVariant(road.Surface),
        };

        void Emit(double3 position, (int, int, int) color, double intensity, float glow)
        {
            uint seed = LightSet.Hash((long)(position.X * 10), (long)(position.Y * 10), (long)(position.Z * 10));
            light!(new RoadLight(position, LightSet.Pack(color, intensity), glow, seed));
        }

        for (int i = 0; i + 1 < dirs.Count; i++)
        {
            double3 p0 = dirs[i] * (radius + heights[i]), p1 = dirs[i + 1] * (radius + heights[i + 1]);
            double3 mid = (p0 + p1) * 0.5;
            double3 up = double3.Normalize(mid);
            double3 along = p1 - p0;
            double length = Math.Sqrt(double3.Dot(along, along));
            if (length < 0.05)
                continue;
            along *= 1 / length;
            double3 side = double3.Normalize(double3.Cross(up, along));
            double surface = road.Lift + 0.4;
            if (lights && road.Airfield == Airfield.Runway && i == 0 && !road.StartAtTileEdge)
                EndRow(p0);
            if (lights && road.Airfield == Airfield.Runway && i + 2 == dirs.Count && !road.EndAtTileEdge)
                EndRow(p1);
            if (lights)
            {
                for (; lightAlong < length; lightAlong += lightSpacing, lightCount++)
                {
                    double3 at = p0 + along * lightAlong;
                    if (road.Airfield == Airfield.Runway)
                    {
                        Emit(at + up * surface + side * (half + 1.5), LightSet.RunwayWhite, 6, 1.2f);
                        Emit(at + up * surface - side * (half + 1.5), LightSet.RunwayWhite, 6, 1.2f);
                    }
                    else if (road.Airfield == Airfield.Taxiway)
                    {
                        Emit(at + up * surface + side * (half + 1), LightSet.TaxiwayBlue, 5, 1.0f);
                        Emit(at + up * surface - side * (half + 1), LightSet.TaxiwayBlue, 5, 1.0f);
                    }
                    else
                    {
                        double sideSign = (lightCount & 1) == 0 ? 1 : -1;
                        double3 lamp = at + up * 8 + side * (sideSign * (half + 0.8));
                        uint seed = LightSet.Hash((long)(lamp.X * 10), (long)(lamp.Y * 10), (long)(lamp.Z * 10));
                        light!(new RoadLight(lamp, LightSet.Pack(LightSet.StreetColor(seed), road.Rank >= 4 ? 7 : 5), 1.5f, seed));
                    }
                }
                lightAlong -= length;
            }

            void EndRow(double3 end)
            {
                for (double s = -half; s <= half + 0.01; s += 3)
                    Emit(end + up * surface + side * s, LightSet.ThresholdGreen, 6, 1.0f);
            }

            // Across the slope the slab leans with the terrain under its edges (sheared, so its sides stay vertical).
            double cross = 0;
            if (!road.Bridge)
            {
                double3 groundMid = up * radius;
                cross = Math.Max(terrain(double3.Normalize(groundMid + side * half)), 0)
                      - Math.Max(terrain(double3.Normalize(groundMid - side * half)), 0);
            }

            double before = Extension(i), after = Extension(i + 1);
            double3 center = mid + along * ((after - before) / 2);
            // Neighbouring pieces overlap in bends; a few millimetres between them keep them from flickering.
            double lift = road.Lift + (i & 1) * 0.004;
            add(new BuildingInstance
            {
                Up = up * (RoadDepth + lift),
                AxisA = along * (length + before + after),
                AxisB = side * road.Width + up * cross,
                Base = center - up * RoadDepth,
                Center = mid,
                Variant = variant,
                IsRoad = true,
                Layer = Layers.Street,
                Style = Buildings.StyleNone,
            });
        }

        if (road.Pads != null)
            foreach (var pad in road.Pads)
                add(MakePad(DirCcf(road.Points[pad.Point].Lat, road.Points[pad.Point].Lon), pad, radius, terrain));
    }

    /// <summary>Longest stretch on each side of a corner that a bend is rounded off over.</summary>
    private const double RoundMaxMeters = 7;
    /// <summary>Gentler bends stay as they are.</summary>
    private const double RoundMinDegrees = 6;

    /// <summary>
    /// The road's points as CCF directions, with each bend rounded off (a quadratic curve between points up to
    /// RoundMaxMeters before and after the corner, never more than 45 % of either side). Junction points
    /// (RawRoad.Fixed) and both ends stay where they are, so roads still meet and tile edges still match: the
    /// last corner before a cut ends on the straight line to the cut point, which the neighbouring tile continues.
    /// </summary>
    private static List<double3> RoundedPoints(RawRoad road, double radius)
    {
        var p = road.Points.Select(q => DirCcf(q.Lat, q.Lon) * radius).ToList();
        if (p.Count < 3)
            return p.Select(double3.Normalize).ToList();
        var result = new List<double3>(p.Count * 3) { p[0] };
        double reach = Math.Clamp(road.Width, 4, RoundMaxMeters);
        for (int i = 1; i + 1 < p.Count; i++)
        {
            double3 b = p[i], toA = p[i - 1] - b, toC = p[i + 1] - b;
            double la = Length(toA), lc = Length(toC);
            if (road.Fixed?[i] == true || la < 0.05 || lc < 0.05)
            {
                result.Add(b);
                continue;
            }
            toA *= 1 / la;
            toC *= 1 / lc;
            double turn = Math.PI - Math.Acos(Math.Clamp(double3.Dot(toA, toC), -1, 1));
            if (turn < RoundMinDegrees * Math.PI / 180)
            {
                result.Add(b);
                continue;
            }
            double d = Math.Min(reach, 0.45 * Math.Min(la, lc));
            double3 t1 = b + toA * d, t2 = b + toC * d;
            int n = Math.Max(2, (int)Math.Ceiling(turn / (8 * Math.PI / 180)));
            for (int s = 0; s <= n; s++)
            {
                double t = (double)s / n;
                result.Add(t1 * ((1 - t) * (1 - t)) + b * (2 * (1 - t) * t) + t2 * (t * t));
            }
        }
        result.Add(p[^1]);
        return result.Select(double3.Normalize).ToList();
    }

    /// <summary>
    /// A junction pad (RoadJunctions): the round slab mesh, tilted with the terrain under its rim and sheared so
    /// its sides stay vertical, its top pad.Lift above the terrain at its centre.
    /// </summary>
    public static BuildingInstance MakePad(double3 dir, JunctionPad pad, double radius, Func<double3, double> terrain)
    {
        Axes(dir, out double3 east, out double3 north);
        double3 ground = dir * radius;
        double Height(double3 offset) => Math.Max(terrain(double3.Normalize(ground + offset)), 0);
        double h = Math.Max(terrain(dir), 0);
        double r = pad.Radius;
        double slopeEast = Height(east * r) - Height(-east * r), slopeNorth = Height(north * r) - Height(-north * r);
        double3 center = dir * (radius + h);
        return new BuildingInstance
        {
            Up = dir * (RoadDepth + pad.Lift),
            AxisA = east * (2 * r) + dir * slopeEast,
            AxisB = north * (2 * r) + dir * slopeNorth,
            Base = center - dir * RoadDepth,
            Center = center,
            Variant = Buildings.JunctionVariant,
            IsRoad = true,
            Layer = Layers.Street,
            Style = Buildings.StyleNone,
        };
    }

    /// <summary>
    /// Terrain heights along a road line, with pieces halved (down to RoadMinPieceMeters) wherever the
    /// terrain at a piece's middle lies more than RoadToleranceMeters off the straight line between its ends.
    /// </summary>
    private static (List<double3> Dirs, double[] Heights) Refine(List<double3> dirs, double radius, Func<double3, double> terrain)
    {
        double Height(double3 d) => Math.Max(terrain(d), 0);
        var outDirs = new List<double3>(dirs.Count * 2) { dirs[0] };
        var outHeights = new List<double>(dirs.Count * 2) { Height(dirs[0]) };
        var stack = new Stack<(double3 Dir, double H)>();
        for (int i = 1; i < dirs.Count; i++)
        {
            stack.Push((dirs[i], Height(dirs[i])));
            while (stack.Count > 0)
            {
                double3 d0 = outDirs[^1];
                double h0 = outHeights[^1];
                var (d1, h1) = stack.Peek();
                double length = Math.Sqrt(double3.Dot(d1 - d0, d1 - d0)) * radius;
                if (length > RoadMinPieceMeters * 2)
                {
                    double3 dm = double3.Normalize(d0 + d1);
                    double hm = Height(dm);
                    if (Math.Abs(hm - (h0 + h1) / 2) > RoadToleranceMeters)
                    {
                        stack.Push((dm, hm));
                        continue;
                    }
                }
                stack.Pop();
                outDirs.Add(d1);
                outHeights.Add(h1);
            }
        }
        return (outDirs, outHeights.ToArray());
    }

    /// <summary>
    /// Model-to-ego matrix (row vectors, like the game's static objects): the box axes and base point,
    /// relative to the camera, rotated from CCF into ecliptic axes. rx/ry/rz are the CCF unit axes in CCE.
    /// </summary>
    public static float4x4 ModelToEgo(in BuildingInstance b, double3 cameraCcf, double3 rx, double3 ry, double3 rz)
    {
        double3 u = Rotate(b.Up, rx, ry, rz);
        double3 a = Rotate(b.AxisA, rx, ry, rz);
        double3 c = Rotate(b.AxisB, rx, ry, rz);
        double3 p = Rotate(b.Base - cameraCcf, rx, ry, rz);
        return new float4x4(
            (float)u.X, (float)u.Y, (float)u.Z, 0,
            (float)a.X, (float)a.Y, (float)a.Z, 0,
            (float)c.X, (float)c.Y, (float)c.Z, 0,
            (float)p.X, (float)p.Y, (float)p.Z, 1);
    }

    /// <summary>Rotates a camera-relative CCF vector into ego (ecliptic) axes.</summary>
    public static double3 ToEgo(double3 relativeCcf, double3 rx, double3 ry, double3 rz) => Rotate(relativeCcf, rx, ry, rz);

    private static double3 Rotate(double3 v, double3 rx, double3 ry, double3 rz) => rx * v.X + ry * v.Y + rz * v.Z;
}
