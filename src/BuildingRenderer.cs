using System.Collections;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>
/// Feeds building instances to the game's static object renderer. Runs right after the game collects
/// launch pad instances (Celestial.UpdateRenderData), so ours are uploaded in the same frame.
/// </summary>
internal static class BuildingRenderer
{
    public static TileManager? Manager;
    public static Settings? Settings;
    public static FarField? Far;
    private static readonly HashSet<int> NearReady = new();
    public static int LastDrawn;

    private static readonly StaticObjectModel?[] Models = new StaticObjectModel?[Buildings.VariantCount];
    private static int _resolvedFrom = -1;

    public static void Render(Celestial celestial, IViewport viewport)
    {
        if (Manager is not { } manager || Settings is not { Enabled: true } settings)
            return;
        if (!string.Equals(celestial.Id, settings.Body, StringComparison.OrdinalIgnoreCase))
            return;
        if (viewport == Program.MainViewport)
            ShadowCasters.Clear();
        Tile[] tiles = manager.Snapshot;
        // The far field can be all there is (high up, or before the detailed tiles are in).
        if ((tiles.Length == 0 && (Far == null || Far.Snapshot.Length == 0)) || !ResolveModels())
            return;

        Camera camera = viewport.GetCamera();
        doubleQuat ccf2Cce = celestial.GetCcf2Cce();
        // The game's Double3Ex.Transform, not the numerics library's double3.Transform, which rotates the other way.
        double3 camCcf = Double3Ex.Transform(camera.PositionEcl - celestial.GetPositionEcl(), celestial.GetCce2Ccf());
        double3 rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce);
        double3 ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce);
        double3 rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);

        double maxDist = settings.RenderDistanceKm * 1000;
        double maxDist2 = maxDist * maxDist;
        // Roads are thin and numerous, so they get their own, shorter distance; -1 hides them.
        double roadDist = Math.Min(settings.RoadDrawDistanceKm * 1000, maxDist);
        double roadDist2 = settings.ShowRoads ? roadDist * roadDist : -1;
        // Relief pieces of buildings only show nearby; -1 hides them.
        double detailDist2 = settings.BuildingDetails ? Math.Pow(Math.Min(settings.DetailDistanceKm * 1000, maxDist), 2) : -1;
        int budget = settings.MaxInstances;
        int drawn = 0;

        float night = settings.NightLights ? NightLights.NightFactor(celestial, camCcf, settings) : 0;
        // Only the nearest lights are used, so candidates come from a small radius.
        double lightRadius2 = night > 0 ? 1200.0 * 1200.0 : -1;
        NightLights.Begin();

        // Window pass: walls near the camera, main view only; lit windows by the tile's night factor.
        bool main = viewport == Program.MainViewport;
        double windowDist = Math.Min(settings.WindowDistanceKm * 1000, maxDist);
        double windowDist2 = main && settings.Windows ? windowDist * windowDist : -1;
        double3? sun = NightLights.SunDirCcf(celestial, settings);
        if (main)
        {
            FacadeRenderer.Begin();
            VaporRenderer.Begin(camCcf, rx, ry, rz, sun);
        }

        // Nearest tiles first, so the instance cap drops the far ones.
        var order = new (double Dist2, Tile Tile)[tiles.Length];
        for (int t = 0; t < tiles.Length; t++)
        {
            double3 d = tiles[t].CenterCcf - camCcf;
            order[t] = (double3.Dot(d, d), tiles[t]);
        }
        Array.Sort(order, (x, y) => x.Dist2.CompareTo(y.Dist2));

        // Boxes smaller than about half a pixel are left out: in a city that is most of what lies kilometres away,
        // and drawing them costs time on both sides without showing anything. Where even that is more than the
        // instance cap, the limit rises (LodScale) so the city thins out with distance instead of ending at an edge.
        double pixelAngle = 2 * Math.Tan(camera.GetFieldOfView() / 2) / Math.Max(camera.FramebufferSize.Y, 1);
        double minSize = MinPixels * (main ? _lodScale : 1) * pixelAngle;
        double minSize2 = minSize * minSize;
        EnsureSelection(budget);
        // Layers switched off in the settings (roads are switched off through roadDist2).
        bool showBuildings = settings.ShowBuildings, showComplex = settings.LaunchComplexModels;
        bool Hidden(in BuildingInstance b) => b.Layer == Layers.LaunchComplex ? !showComplex : b.Layer == Layers.Building && !showBuildings;

        var selectScope = Perf.Measure("  select");
        // Tiles in reach and in view; tiles outside the view are skipped, except near ones (their lights and
        // shadows reach into the view), and a margin keeps shadows of buildings just outside it.
        Candidates.Clear();
        foreach (var (tileDist2, tile) in order)
        {
            double reach = maxDist + tile.RadiusM;
            if (tileDist2 > reach * reach)
                break;
            if (tileDist2 > NearKeepM * NearKeepM
                && camera.IsSphereOutsideFov(Rotate(tile.CenterCcf - camCcf, rx, ry, rz), tile.RadiusM + ShadowMarginM))
                continue;
            int count = Math.Min(tile.Count, tile.Instances.Length);
            Sizes(tile, count);
            Candidates.Add((tile, Math.Max(0, Math.Sqrt(tileDist2) - tile.RadiusM), count));
        }

        var sideScope = Perf.Measure("   side");
        // Lights and vapour near the camera, from every instance of the tiles that close (not thinned by size).
        bool vapour = main && settings.TankVapor;
        foreach (var (tile, tileNear, count) in Candidates)
        {
            BuildingInstance[] instances = tile.Instances;
            if (vapour && tileNear < VaporReachM)
                foreach (int i in tile.Vents)
                {
                    if (Hidden(in instances[i]))
                        continue;
                    double3 d = instances[i].Center - camCcf;
                    if (double3.Dot(d, d) < VaporReachM * VaporReachM)
                        VaporRenderer.AddTank(in instances[i]);
                }
            if (tileNear * tileNear > lightRadius2)
                continue;
            for (int i = 0; i < count; i++)
            {
                ref BuildingInstance b = ref instances[i];
                double3 d = b.Center - camCcf;
                double dist2 = double3.Dot(d, d);
                if (dist2 > (b.IsRoad ? roadDist2 : b.IsDetail ? detailDist2 : maxDist2) || Hidden(in b))
                    continue;
                // Point lights go to building walls and streets, not to roofs or airfield surfaces.
                if (dist2 < lightRadius2 && (b.IsRoad ? Buildings.IsStreetSurface(b.Variant) : b.Style != Buildings.StyleNone))
                    NightLights.Consider(dist2, tile, i);
            }
        }

        sideScope.Dispose();
        var parScope = Perf.Measure("   parallel");
        // What each tile draws, on all cores: boxes below the pixel limit even at the tile's nearest point are
        // skipped on their size alone (a small array), so most of a distant tile is never loaded.
        double sunWindow = settings.CityGlow ? settings.GlowBrightness : 0;
        Parallel.For(0, Candidates.Count, t =>
        {
            var (tile, tileNear, count) = Candidates[t];
            BuildingInstance[] instances = tile.Instances;
            float[] sizes = tile.Sizes;
            if (tile.SelIndex.Length < count)
            {
                tile.SelIndex = new int[Math.Max(count, tile.SelIndex.Length * 2)];
                tile.SelWalls = new float[tile.SelIndex.Length];
                tile.SelVariant = new int[tile.SelIndex.Length];
            }
            int[] sel = tile.SelIndex;
            float[] selWalls = tile.SelWalls;
            int[] selVariant = tile.SelVariant;
            float windowLight = sun is { } sunDir
                ? NightLights.NightAt(double3.Dot(double3.Normalize(tile.CenterCcf), sunDir)) * (float)sunWindow
                : 0;
            float skipBelow = (float)(minSize2 * tileNear * tileNear);
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                if (sizes[i] < skipBelow)
                    continue;
                ref BuildingInstance b = ref instances[i];
                double3 d = b.Center - camCcf;
                double dist2 = double3.Dot(d, d);
                if (dist2 > (b.IsRoad ? roadDist2 : b.IsDetail ? detailDist2 : maxDist2) || sizes[i] < minSize2 * dist2
                    || Hidden(in b))
                    continue;
                bool walls = dist2 < windowDist2 && !b.IsRoad && b.Style != Buildings.StyleNone;
                sel[n] = i;
                selWalls[n] = walls ? windowLight : -1;
                selVariant[n] = b.Variant;
                n++;
            }
            tile.SelCount = n;
        });

        parScope.Dispose();
        // Nearest tiles first, so the instance cap drops the far ones.
        foreach (var (tile, _, _) in Candidates)
        {
            int take = Math.Min(tile.SelCount, budget - drawn);
            Array.Copy(tile.SelIndex, 0, _selIndex, drawn, take);
            Array.Copy(tile.SelWalls, 0, _selWalls, drawn, take);
            Array.Copy(tile.SelVariant, 0, _selVariant, drawn, take);
            Array.Fill(_selArray, tile.Instances, drawn, take);
            drawn += take;
            if (drawn >= budget)
                goto done;
        }
        // Far field beyond the detailed tiles: one box per building, left out where a detailed tile is ready and
        // where it would be smaller than about a pixel.
        if (Far is { } far && settings.FarBuildings && showBuildings && far.Snapshot is { Length: > 0 } farTiles)
        {
            NearReady.Clear();
            foreach (var t in tiles)
                if (t.State == TileState.Ready)
                    NearReady.Add(t.Key.X * 16384 + t.Key.Y);
            double farDist = settings.FarRadiusKm * 1000;
            double farDist2 = farDist * farDist;
            foreach (FarTile ft in farTiles)
            {
                double3 d = ft.CenterCcf - camCcf;
                double reach = farDist + ft.RadiusM;
                if (double3.Dot(d, d) > reach * reach
                    || camera.IsSphereOutsideFov(Rotate(d, rx, ry, rz), ft.RadiusM + ShadowMarginM))
                    continue;
                int count = Math.Min(ft.Count, ft.Instances.Length);
                for (int i = 0; i < count; i++)
                {
                    ref BuildingInstance b = ref ft.Instances[i];
                    double3 c = b.Center - camCcf;
                    double dist2 = double3.Dot(c, c);
                    if (dist2 > farDist2)
                        continue;
                    if (Size2(in b) < minSize2 * dist2 || NearReady.Contains(ft.NearKeys[i]))
                        continue;
                    Select(ft.Instances, i, b.Variant, drawn, -1);
                    if (++drawn >= budget)
                        goto done;
                }
            }
        }
    done:
        selectScope.Dispose();
        using (Perf.Measure("  emit"))
            Emit(drawn, viewport, camCcf, rx, ry, rz, main && settings.BuildingShadows);
        if (showComplex)
            CrewArm.Render(Models[Buildings.CrewArmVariant]!, viewport, camCcf, rx, ry, rz, maxDist);
        NightLights.Emit(viewport, camCcf, rx, ry, rz, night, settings);
        if (main)
        {
            LastDrawn = drawn;
            // Up quickly while the cap cuts off, down slowly once well below it; the gap keeps it from flickering.
            if (drawn >= budget)
                _lodScale = Math.Min(_lodScale * 1.08, 16);
            else if (drawn < budget * 0.8)
                _lodScale = Math.Max(_lodScale / 1.02, 1);
        }
    }

    private static double _lodScale = 1;
    public static double LodScale => _lodScale;

    private const double MinPixels = 0.5;
    private const double VaporReachM = 2500;
    private static readonly List<(Tile Tile, double NearM, int Count)> Candidates = new();
    private const double NearKeepM = 1500;
    private const double ShadowMarginM = 300;

    /// <summary>The tile's squared box sizes, extended as instances are added (they are only ever appended).</summary>
    private static float[] Sizes(Tile tile, int count)
    {
        float[] sizes = tile.Sizes;
        if (tile.SizesCount == count)
            return sizes;
        if (sizes.Length < count)
            Array.Resize(ref sizes, Math.Max(count, sizes.Length * 2));
        for (int i = tile.SizesCount; i < count; i++)
        {
            sizes[i] = (float)Size2(in tile.Instances[i]);
            if (tile.Instances[i].Vents)
                tile.Vents.Add(i);
        }
        tile.Sizes = sizes;
        tile.SizesCount = count;
        return sizes;
    }

    // ---- selection, then matrices in parallel, then one bulk copy per model ----

    private static BuildingInstance[][] _selArray = Array.Empty<BuildingInstance[]>();
    private static int[] _selIndex = Array.Empty<int>();
    private static int[] _selVariant = Array.Empty<int>();
    private static int[] _selSlot = Array.Empty<int>();
    private static float[] _selWalls = Array.Empty<float>();
    private static int[] _selWallSlot = Array.Empty<int>();
    private static byte[] _shadowKind = Array.Empty<byte>();
    private static float[] _shadowDist = Array.Empty<float>();
    private static StaticObjectModel.PerInstanceData[] _out = Array.Empty<StaticObjectModel.PerInstanceData>();
    private static readonly int[] VariantStart = new int[Buildings.VariantCount + 1];
    private static readonly int[] VariantCursor = new int[Buildings.VariantCount];
    private const int Chunk = 2048;

    private static void EnsureSelection(int budget)
    {
        if (_selIndex.Length >= budget)
            return;
        _selArray = new BuildingInstance[budget][];
        _selIndex = new int[budget];
        _selVariant = new int[budget];
        _selSlot = new int[budget];
        _selWalls = new float[budget];
        _selWallSlot = new int[budget];
        _shadowKind = new byte[budget];
        _shadowDist = new float[budget];
        _out = new StaticObjectModel.PerInstanceData[budget];
    }

    /// <summary>walls: brightness of lit windows when the walls get a window pass, else negative.</summary>
    private static void Select(BuildingInstance[] array, int index, int variant, int at, float walls)
    {
        _selArray[at] = array;
        _selIndex[at] = index;
        _selVariant[at] = variant;
        _selWalls[at] = walls;
    }

    /// <summary>
    /// Model matrices of the selected instances, worked out on all cores into one array grouped by model, then
    /// copied into each model's instance list at once (one AddInstance per box was most of the cost in a city).
    /// </summary>
    private static void Emit(int n, IViewport viewport, double3 camCcf, double3 rx, double3 ry, double3 rz, bool shadows)
    {
        if (n == 0)
            return;
        Array.Clear(VariantStart);
        for (int j = 0; j < n; j++)
            VariantStart[_selVariant[j] + 1]++;
        for (int v = 0; v < Buildings.VariantCount; v++)
        {
            VariantStart[v + 1] += VariantStart[v];
            VariantCursor[v] = VariantStart[v];
        }
        int walls = 0;
        for (int j = 0; j < n; j++)
        {
            _selSlot[j] = VariantCursor[_selVariant[j]]++;
            _selWallSlot[j] = _selWalls[j] >= 0 ? walls++ : -1;
        }
        int wallFits = Math.Min(walls, FacadeRenderer.Free);
        int wallStart = FacadeRenderer.Reserve(wallFits, out FacadeInstance[] wallArray);
        if (wallStart < 0)
            wallFits = 0;

        void Work(int from, int to)
        {
            for (int j = from; j < to; j++)
            {
                ref BuildingInstance b = ref _selArray[j][_selIndex[j]];
                double pull = b.IsRoad ? Placement.FlatPull(in b, camCcf) : 1;
                _out[_selSlot[j]] = new StaticObjectModel.PerInstanceData { ModelMatrix = Placement.ModelToEgo(in b, camCcf, rx, ry, rz, pull) };
                int w = _selWallSlot[j];
                if (w >= 0 && w < wallFits)
                    wallArray[wallStart + w] = FacadeRenderer.Make(in b, camCcf, rx, ry, rz, _selWalls[j]);
                byte kind = shadows && !b.IsRoad ? ShadowCasters.KindOf(b.Variant) : ShadowCasters.None;
                if (kind != ShadowCasters.None)
                {
                    double3 d = b.Center - camCcf;
                    double dist = Math.Sqrt(double3.Dot(d, d));
                    _shadowDist[j] = (float)dist;
                    if (dist > ShadowCasters.RangeM)
                        kind = ShadowCasters.None;
                }
                _shadowKind[j] = kind;
            }
        }
        if (n <= Chunk)
            Work(0, n);
        else
            Parallel.For(0, (n + Chunk - 1) / Chunk, c => Work(c * Chunk, Math.Min(n, (c + 1) * Chunk)));
        Array.Clear(_selArray, 0, n);                                   // no references kept to dropped tiles
        if (shadows)
            using (Perf.Measure("  shadow casters"))
                ShadowCasters.Collect(n, _shadowKind, _shadowDist, _out, _selSlot);

        for (int v = 0; v < Buildings.VariantCount; v++)
        {
            int start = VariantStart[v], count = VariantStart[v + 1] - start;
            if (count == 0)
                continue;
            StaticObjectModel model = Models[v]!;
            if (InstanceList(model, viewport) is { } list)
                list.AddRange(new ArraySegment<StaticObjectModel.PerInstanceData>(_out, start, count));
            else
                for (int k = start; k < start + count; k++)
                    model.AddInstance(_out[k], viewport);
        }
    }

    // StaticObjectModel keeps a private list per viewport that AddInstance appends to; the bulk copy goes there.
    private static readonly FieldInfo? ViewportInstancesField =
        typeof(StaticObjectModel).GetField("_viewportInstances", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly Dictionary<(StaticObjectModel, IViewport), List<StaticObjectModel.PerInstanceData>?> Lists = new();

    private static List<StaticObjectModel.PerInstanceData>? InstanceList(StaticObjectModel model, IViewport viewport)
    {
        if (Lists.TryGetValue((model, viewport), out var cached))
            return cached;
        List<StaticObjectModel.PerInstanceData>? list = null;
        try
        {
            if (ViewportInstancesField?.GetValue(model) is IDictionary perViewport)
            {
                if (!perViewport.Contains(viewport))
                {
                    model.AddInstance(default, viewport);                // makes the entry; the dummy is removed below
                    if (perViewport[viewport] is { } made && made.GetType().GetField("InstanceList") is { } f
                        && f.GetValue(made) is List<StaticObjectModel.PerInstanceData> l && l.Count > 0)
                        l.RemoveAt(l.Count - 1);
                }
                object? entry = perViewport[viewport];
                list = entry?.GetType().GetField("InstanceList")?.GetValue(entry) as List<StaticObjectModel.PerInstanceData>;
            }
        }
        catch (Exception e)
        {
            Log.Error($"instance list lookup failed, using AddInstance: {e.Message}");
        }
        if (Lists.Count > 256)
            Lists.Clear();
        Lists[(model, viewport)] = list;
        return list;
    }

    /// <summary>Squared length of the box's longest edge.</summary>
    private static double Size2(in BuildingInstance b) =>
        Math.Max(double3.Dot(b.Up, b.Up), Math.Max(double3.Dot(b.AxisA, b.AxisA), double3.Dot(b.AxisB, b.AxisB)));

    private static double3 Rotate(double3 v, double3 rx, double3 ry, double3 rz) => rx * v.X + ry * v.Y + rz * v.Z;

    public static int ModelsFound => Models.Count(m => m != null);

    /// <summary>The game recreates its model list when the renderer restarts, so look ours up again when it changes.</summary>
    private static bool ResolveModels()
    {
        var all = StaticObjectModel.Instances;
        if (_resolvedFrom == all.Count && Models.All(m => m != null))
            return true;
        for (int v = 0; v < Models.Length; v++)
        {
            string id = $"GEE_Box{v}_Model";
            Models[v] = all.FirstOrDefault(m => m.Template.Id == id);
        }
        _resolvedFrom = all.Count;
        Lists.Clear();
        return Models.All(m => m != null);
    }
}
