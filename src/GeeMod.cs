using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace GEE;

/// <summary>
/// Places OpenStreetMap buildings on Earth: footprints come from OpenMapTiles vector tiles around the camera,
/// reduced to a few boxes each and drawn through the game's static object renderer (no collisions).
/// </summary>
[StarMapMod]
public class GeeMod
{
    internal static GeeMod? Instance;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _dir = "";
    private string _settingsPath = "";
    private Settings _settings = new();
    private TileManager? _manager;
    private RegionalLights? _regional;
    private RegionalLights? _global;
    private FarField? _far;
    private WaterField? _water;
    private Harmony? _harmony;
    private double _settingsChangedAt = -1;
    private double _padRadiusAtLoad = double.NaN;

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        EnsureStarted();
    }

    [StarMapAfterOnFrame]
    public void OnFrame(double dt, double dtPlayer)
    {
        EnsureStarted();
        Perf.Frame();
        AutoShot.Tick(dtPlayer, _dir, _settings.Body, _settings.SunId);
        using (Perf.Measure("satellite upload"))
            SatelliteImagery.Upload();
        using (Perf.Measure("lake upload"))
            LakeOcean.Upload();
        // Collected again when the buildings are rendered this frame; never drawn from an old camera position.
        ShadowCasters.Clear();
        if (_manager == null)
            return;
        double now = _clock.Elapsed.TotalSeconds;
        if (_settingsChangedAt >= 0 && now - _settingsChangedAt > 1)
        {
            _settings.Save(_settingsPath);
            _settingsChangedAt = -1;
        }

        if (Universe.CurrentSystem is not { } system || !system.All.TryGet(_settings.Body, out Astronomical? astronomical)
            || astronomical is not Celestial body)
            return;
        try
        {
            Camera camera = Program.GetMainCamera();
            using (Perf.Measure("tiles"))
                _manager.Update(body, camera, now);
            double3 cameraCcf = Double3Ex.Transform(camera.PositionEcl - body.GetPositionEcl(), body.GetCce2Ccf());
            using (Perf.Measure("city lights"))
            {
                _regional?.Update(body, cameraCcf, now);
                _global?.Update(body, cameraCcf, now);
            }
            using (Perf.Measure("far field"))
                _far?.Update(body, cameraCcf, now, camera);
            using (Perf.Measure("water field"))
                _water?.Update(body, cameraCcf, now, camera);
            TreeCover.Ground = dir => body.GetTerrainHeightFromDirCcf(dir);
            LakeOcean.Update(body, cameraCcf);
            RiverBuilder.Update(body, cameraCcf);
            using (Perf.Measure("elevation"))
                Elevation.Update(body, cameraCcf);
        }
        catch (Exception e)
        {
            Log.Error($"update failed: {e}");
        }
    }

    [StarMapAfterGui]
    public void OnGui(double dt)
    {
        if (_manager != null && _settings.ShowWindow)
            DrawWindow();
    }

    [StarMapUnload]
    public void OnUnload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _global?.Dispose();
        _far?.Dispose();
        _water?.Dispose();
        LakeOcean.Field = null;
        _regional?.Dispose();
        _manager?.Dispose();
        _settings.Save(_settingsPath);
        BuildingRenderer.Manager = null;
        GlowRenderer.Manager = null;
        GlowRenderer.Regional = null;
        GlowRenderer.Global = null;
        Instance = null;
    }

    /// <summary>Unfinished background jobs of all loaders (0 = idle); read by AutoShot waitidle.</summary>
    private int BusyCount()
    {
        int busy = SatelliteImagery.Busy + Elevation.Busy;
        if (_manager != null) { var s = _manager.Stats(); busy += s.Queued + s.Loading + s.Placing; }
        if (_far != null) busy += _far.Stats().Pending;
        if (_water != null) busy += _water.Stats().Pending;
        foreach (var r in new[] { _regional, _global })
            if (r != null) { var s = r.Stats(); busy += s.Queued + s.Loading + s.Building; }
        return busy;
    }

    private void EnsureStarted()
    {
        if (Instance != null)
            return;
        Instance = this;
        try
        {
            // Streaming tiles allocates a lot; blocking gen2 collections paused the whole game for 50-350 ms per 10 s.
            // Sustained low latency makes gen2 collections run in the background (concurrent GC is on by default).
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        }
        catch (Exception e)
        {
            Log.Error($"GC latency mode: {e.Message}");
        }
        try
        {
            _dir = Path.GetDirectoryName(typeof(GeeMod).Assembly.Location)!;
            _settingsPath = Path.Combine(_dir, "settings.json");
            _settings = Settings.Load(_settingsPath);
            Log.Debug = _settings.DebugLogging;
            _padRadiusAtLoad = _settings.LaunchPadClearRadiusKm;
            _manager = new TileManager(_settings, _dir);
            _regional = new RegionalLights(_settings, _manager, RegionalLights.RegionalZoom);
            _global = new RegionalLights(_settings, _manager, RegionalLights.GlobalZoom, _regional);
            _far = new FarField(_settings, _manager);
            BuildingRenderer.Far = _far;
            _water = new WaterField(_settings, _manager);
            LakeOcean.Settings = _settings;
            LakeOcean.Field = _water;
            BuildingRenderer.Settings = _settings;
            BuildingRenderer.Manager = _manager;
            GlowRenderer.Settings = _settings;
            GlowRenderer.Manager = _manager;
            GlowRenderer.Regional = _regional;
            GlowRenderer.Global = _global;
            SatelliteImagery.Settings = _settings;
            SatelliteImagery.ModDir = _dir;
            TreePack.Settings = _settings;
            Scatter.Settings = _settings;
            GrassBoost.Settings = _settings;
            GrassLayer.Settings = _settings;
            GrassLayer.Manager = _manager;
            PostFx.Settings = _settings;
            GroundDetail.Settings = _settings;
            RoadWear.Settings = _settings;
            Elevation.Settings = _settings;
            RiverBuilder.Settings = _settings;
            Elevation.ModDir = _dir;
            _harmony = new Harmony("GEE");
            Patches.Apply(_harmony);
            AutoShot.BusyProbe = BusyCount;
            Log.Info($"started, data dir {_dir}");
        }
        catch (Exception e)
        {
            Log.Error($"failed to start: {e}");
        }
    }

    private static readonly (string Mode, string Label)[] StreetLightModes =
        { ("BuiltUp", "In towns"), ("Data", "OSM data"), ("All", "All roads"), ("None", "None") };

    private void Changed() => _settingsChangedAt = _clock.Elapsed.TotalSeconds;

    private void SetDebug(bool debug)
    {
        _settings.DebugLogging = debug;
        Log.Debug = debug;
        Changed();
    }

    internal void DrawMenu()
    {
        if (!ImGui.BeginMenu("GEE"))
            return;
        ((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true);
        bool show = _settings.ShowWindow;
        if (ImGui.MenuItem("Window", default(ImString), ref show))
        {
            _settings.ShowWindow = show;
            Changed();
        }
        bool enabled = _settings.Enabled;
        if (ImGui.MenuItem("Show buildings", default(ImString), ref enabled))
        {
            _settings.Enabled = enabled;
            Changed();
        }
        bool debug = _settings.DebugLogging;
        if (ImGui.MenuItem("Debug", default(ImString), ref debug))
            SetDebug(debug);
        ImGui.EndMenu();
    }

    private void DrawWindow()
    {
        var manager = _manager!;
        ImGui.SetNextWindowSize(new float2(400, 0), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("GEE - General Earth Enhancements", ref open))
        {
            bool enabled = _settings.Enabled;
            if (ImGui.Checkbox("Show buildings", ref enabled))
            {
                _settings.Enabled = enabled;
                Changed();
            }
            float loadRadius = (float)_settings.LoadRadiusKm;
            if (ImGui.SliderFloat("Load radius (km)", ref loadRadius, 0.5f, (float)Settings.MaxLoadRadiusKm, "%.1f"))
            {
                _settings.LoadRadiusKm = loadRadius;
                Changed();
            }
            float drawDistance = (float)_settings.RenderDistanceKm;
            if (ImGui.SliderFloat("Draw distance (km)", ref drawDistance, 1f, 120f, "%.0f"))
            {
                _settings.RenderDistanceKm = drawDistance;
                Changed();
            }
            ImGui.Text(CrewArm.Status);
            if (ImGui.Button("Dock crew arm"))
            {
                CrewArm.Dock();
                Changed();
            }
            ImGui.SameLine();
            if (ImGui.Button("Undock crew arm"))
            {
                CrewArm.Undock();
                Changed();
            }
            bool satellite = _settings.SatelliteImagery;
            if (ImGui.Checkbox("Satellite imagery from altitude (Sentinel-2 cloudless, EOX)", ref satellite))
            {
                _settings.SatelliteImagery = satellite;
                Changed();
            }
            bool groundColour = _settings.GroundColorFromSatellite;
            if (ImGui.Checkbox("Ground colour from satellite imagery (next game start)", ref groundColour))
            {
                _settings.GroundColorFromSatellite = groundColour;
                Changed();
            }
            if (_settings.GroundColorFromSatellite)
            {
                float strength = (float)_settings.GroundColorStrength;
                if (ImGui.SliderFloat("Ground colour strength", ref strength, 0f, 1f, "%.2f"))
                {
                    _settings.GroundColorStrength = strength;
                    Changed();
                }
                bool groundClasses = _settings.GroundClasses;
                if (ImGui.Checkbox("Land cover ground detail: field rows, gravel, wet pools (next game start)", ref groundClasses))
                {
                    _settings.GroundClasses = groundClasses;
                    Changed();
                }
                if (_settings.GroundClasses)
                {
                    float classStrength = (float)_settings.GroundClassStrength;
                    if (ImGui.SliderFloat("Land cover detail strength", ref classStrength, 0f, 1f, "%.2f"))
                    {
                        _settings.GroundClassStrength = classStrength;
                        Changed();
                    }
                    ImGui.Text($"Land cover detail: {GroundClasses.Status}");
                }
                bool groundDetail = _settings.GroundDetail;
                if (ImGui.Checkbox("Extra ground detail (next game start)", ref groundDetail))
                {
                    _settings.GroundDetail = groundDetail;
                    Changed();
                }
                if (_settings.GroundDetail)
                {
                    float gd = (float)_settings.GroundDetailStrength, gm = (float)_settings.GroundMacroStrength, gs = (float)_settings.GroundSlopeStrength, gc = (float)_settings.GroundCavityStrength;
                    if (ImGui.SliderFloat("Non-tiling detail (restart)", ref gd, 0f, 1.5f, "%.2f")) { _settings.GroundDetailStrength = gd; Changed(); }
                    if (ImGui.SliderFloat("Macro variation (restart)", ref gm, 0f, 1.5f, "%.2f")) { _settings.GroundMacroStrength = gm; Changed(); }
                    if (ImGui.SliderFloat("Slope dirt and rock (restart)", ref gs, 0f, 1.5f, "%.2f")) { _settings.GroundSlopeStrength = gs; Changed(); }
                    if (ImGui.SliderFloat("Dark creases (restart)", ref gc, 0f, 1.5f, "%.2f")) { _settings.GroundCavityStrength = gc; Changed(); }
                }
                bool groundTexture = _settings.GroundTexture;
                if (ImGui.Checkbox("Ground textures: grass, dirt, rock detail near the camera (next game start)", ref groundTexture))
                {
                    _settings.GroundTexture = groundTexture;
                    Changed();
                }
                if (_settings.GroundTexture)
                {
                    float tx = (float)_settings.GroundTextureStrength, tn = (float)_settings.GroundTextureNormalStrength, ts = (float)_settings.GroundTextureScaleM, tr = (float)_settings.GroundTextureRangeM;
                    if (ImGui.SliderFloat("Texture detail (restart)", ref tx, 0f, 1.5f, "%.2f")) { _settings.GroundTextureStrength = tx; Changed(); }
                    if (ImGui.SliderFloat("Texture bumps (restart)", ref tn, 0f, 1.5f, "%.2f")) { _settings.GroundTextureNormalStrength = tn; Changed(); }
                    if (ImGui.SliderFloat("Texture tile size m (restart)", ref ts, 0.25f, 4f, "%.2f")) { _settings.GroundTextureScaleM = ts; Changed(); }
                    if (ImGui.SliderFloat("Texture range m (restart)", ref tr, 30f, 400f, "%.0f")) { _settings.GroundTextureRangeM = tr; Changed(); }
                    ImGui.Text($"Ground textures: {GroundTexture.Status}");
                }
            }
            bool riversOn = _settings.Rivers;
            if (ImGui.Checkbox("Rivers (next game start)", ref riversOn))
            {
                _settings.Rivers = riversOn;
                Changed();
            }
            bool realShores = _settings.RealShores;
            if (ImGui.Checkbox("Real shores at lakes (next game start)", ref realShores))
            {
                _settings.RealShores = realShores;
                Changed();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Real elevation data (AWS Terrain Tiles) near lakes only: the lake level and the banks are the real ones, the rest of the terrain stays the game's.");
            bool realTerrain = _settings.RealTerrain;
            if (ImGui.Checkbox("Real terrain heights (next game start)", ref realTerrain))
            {
                _settings.RealTerrain = realTerrain;
                Changed();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Real elevation data (AWS Terrain Tiles: SRTM, ASTER, USGS NED and others, about 38 m) in an 80 km window around the camera instead of the game's coarse heightmap.");
            bool water = _settings.Water;
            if (ImGui.Checkbox("Lakes and rivers", ref water))
            {
                _settings.Water = water;
                Changed();
            }
            if (_settings.Water)
            {
                float minLake = (float)_settings.MinLakeAreaKm2;
                if (ImGui.SliderFloat("Smallest lake (km2)", ref minLake, 0.1f, 50f, "%.1f", ImGuiSliderFlags.Logarithmic))
                {
                    _settings.MinLakeAreaKm2 = minLake;
                    Changed();
                }
                float waterRadius = (float)_settings.WaterRadiusKm;
                if (ImGui.SliderFloat("Water distance (km)", ref waterRadius, 3f, 100f, "%.0f"))
                {
                    _settings.WaterRadiusKm = waterRadius;
                    Changed();
                }
            }
            bool grading = _settings.PostGrading;
            if (ImGui.Checkbox("Colour grade (next game start)", ref grading))
            {
                _settings.PostGrading = grading;
                Changed();
            }
            if (_settings.PostGrading)
            {
                float contrast = (float)_settings.GradeContrast, saturation = (float)_settings.GradeSaturation;
                float vibrance = (float)_settings.GradeVibrance, warmth = (float)_settings.GradeWarmth;
                float vignette = (float)_settings.GradeVignette;
                bool changed = ImGui.SliderFloat("Grade contrast", ref contrast, 0f, 1f, "%.2f");
                changed |= ImGui.SliderFloat("Grade saturation", ref saturation, 0.5f, 1.5f, "%.2f");
                changed |= ImGui.SliderFloat("Grade vibrance", ref vibrance, 0f, 1f, "%.2f");
                changed |= ImGui.SliderFloat("Grade warmth", ref warmth, -1f, 1f, "%.2f");
                changed |= ImGui.SliderFloat("Grade vignette", ref vignette, 0f, 0.6f, "%.2f");
                if (changed)
                {
                    _settings.GradeContrast = contrast;
                    _settings.GradeSaturation = saturation;
                    _settings.GradeVibrance = vibrance;
                    _settings.GradeWarmth = warmth;
                    _settings.GradeVignette = vignette;
                    Changed();
                }
            }
            bool shadows = _settings.BuildingShadows;
            if (ImGui.Checkbox("Building shadows", ref shadows))
            {
                _settings.BuildingShadows = shadows;
                Changed();
            }
            bool far = _settings.FarBuildings;
            if (ImGui.Checkbox("Far buildings (simplified, beyond the loaded tiles)", ref far))
            {
                _settings.FarBuildings = far;
                Changed();
            }
            float farRadius = (float)_settings.FarRadiusKm;
            if (ImGui.SliderFloat("Far radius (km)", ref farRadius, 10f, 160f, "%.0f"))
            {
                _settings.FarRadiusKm = farRadius;
                Changed();
            }
            bool buildings = _settings.ShowBuildings;
            if (ImGui.Checkbox("Show buildings (OpenStreetMap)", ref buildings))
            {
                _settings.ShowBuildings = buildings;
                Changed();
            }
            bool complex = _settings.LaunchComplexModels;
            if (ImGui.Checkbox("Launch complex models (LC-39A/B, VAB, crew arm)", ref complex))
            {
                _settings.LaunchComplexModels = complex;
                Changed();
            }
            bool roads = _settings.ShowRoads;
            if (ImGui.Checkbox("Show roads", ref roads))
            {
                _settings.ShowRoads = roads;
                Changed();
            }
            bool roadCollision = _settings.RoadCollision;
            if (ImGui.Checkbox("Road collision (vehicles drive on roads)", ref roadCollision))
            {
                _settings.RoadCollision = roadCollision;
                Changed();
            }
            float roadDistance = (float)_settings.RoadDrawDistanceKm;
            if (ImGui.SliderFloat("Road distance (km)", ref roadDistance, 0.5f, 20f, "%.1f"))
            {
                _settings.RoadDrawDistanceKm = roadDistance;
                Changed();
            }
            float padRadius = (float)_settings.LaunchPadClearRadiusKm;
            if (ImGui.SliderFloat("No plain buildings near pads (km)", ref padRadius, 0f, 20f, "%.1f"))
            {
                _settings.LaunchPadClearRadiusKm = padRadius;
                Changed();
            }
            // Placement skips those buildings, so the tiles are rebuilt once the slider is let go.
            if (ImGui.IsItemDeactivated() && _padRadiusAtLoad != _settings.LaunchPadClearRadiusKm)
            {
                _padRadiusAtLoad = _settings.LaunchPadClearRadiusKm;
                manager.Clear();
            }
            // Both change what tiles are made of, so they are rebuilt.
            bool roofs = _settings.Roofs;
            if (ImGui.Checkbox("Pitched roofs on houses", ref roofs))
            {
                _settings.Roofs = roofs;
                Changed();
                manager.Clear();
            }
            bool junctionFillets = _settings.JunctionFillets;
            if (ImGui.Checkbox("Rounded curbs at junctions", ref junctionFillets))
            {
                _settings.JunctionFillets = junctionFillets;
                Changed();
                manager.Clear();
            }
            bool junctionMarkings = _settings.JunctionMarkings;
            if (ImGui.Checkbox("Crosswalks and stop lines in towns", ref junctionMarkings))
            {
                _settings.JunctionMarkings = junctionMarkings;
                Changed();
                manager.Clear();
            }
            bool airports = _settings.Airports;
            if (ImGui.Checkbox("Airports (runways, taxiways, aprons)", ref airports))
            {
                _settings.Airports = airports;
                Changed();
                manager.Clear();
            }
            bool details = _settings.BuildingDetails;
            if (ImGui.Checkbox("Building details nearby (doors, cornices, roof units)", ref details))
            {
                _settings.BuildingDetails = details;
                Changed();
            }
            bool grassLayer = _settings.GrassLayer;
            if (ImGui.Checkbox("Grass layer (blade clumps around the camera)", ref grassLayer))
            {
                _settings.GrassLayer = grassLayer;
                Changed();
            }
            if (_settings.GrassLayer)
            {
                float gr = (float)_settings.GrassLayerRadiusM, gdens = (float)_settings.GrassLayerDensity, gh = (float)_settings.GrassLayerHeightM;
                float gsway = (float)_settings.GrassLayerSway, gtrans = (float)_settings.GrassLayerTranslucency, gslope = (float)_settings.GrassLayerSlopeMaxDeg;
                if (ImGui.SliderFloat("Grass distance (m)", ref gr, 15f, 100f, "%.0f")) { _settings.GrassLayerRadiusM = gr; Changed(); }
                if (ImGui.SliderFloat("Grass density", ref gdens, 0.1f, 1f, "%.2f")) { _settings.GrassLayerDensity = gdens; Changed(); }
                if (ImGui.SliderFloat("Grass height (m)", ref gh, 0.15f, 1.2f, "%.2f")) { _settings.GrassLayerHeightM = gh; Changed(); }
                if (ImGui.SliderFloat("Grass wind sway", ref gsway, 0f, 2f, "%.2f")) { _settings.GrassLayerSway = gsway; Changed(); }
                if (ImGui.SliderFloat("Grass sun translucency", ref gtrans, 0f, 2f, "%.2f")) { _settings.GrassLayerTranslucency = gtrans; Changed(); }
                if (ImGui.SliderFloat("Grass steepest slope (deg)", ref gslope, 10f, 60f, "%.0f")) { _settings.GrassLayerSlopeMaxDeg = gslope; Changed(); }
                bool gcover = _settings.GrassLayerLandCover;
                if (ImGui.Checkbox("Grass by land cover (ESA WorldCover)", ref gcover)) { _settings.GrassLayerLandCover = gcover; Changed(); }
                ImGui.Text($"Grass layer: {GrassLayer.LastClumps:N0} clumps drawn; {GrassLayer.Status}");
            }
            bool vapour = _settings.TankVapor;
            if (ImGui.Checkbox("Tank farm vapour", ref vapour))
            {
                _settings.TankVapor = vapour;
                Changed();
            }
            bool posts = _settings.ReflectorPosts;
            if (ImGui.Checkbox("Reflector posts on unlit roads", ref posts))
            {
                _settings.ReflectorPosts = posts;
                Changed();
                manager.Clear();                                       // part of the tiles: rebuilt
            }
            bool barriers = _settings.RoadBarriers;
            if (ImGui.Checkbox("Guard rails, median barriers, bridge parapets", ref barriers))
            {
                _settings.RoadBarriers = barriers;
                Changed();
                manager.Clear();                                       // part of the tiles: rebuilt
            }
            bool windows = _settings.Windows;
            if (ImGui.Checkbox("Windows (lit at night)", ref windows))
            {
                _settings.Windows = windows;
                Changed();
            }
            float windowDistance = (float)_settings.WindowDistanceKm;
            if (ImGui.SliderFloat("Window distance (km)", ref windowDistance, 0.5f, 20f, "%.1f"))
            {
                _settings.WindowDistanceKm = windowDistance;
                Changed();
            }
            bool clearClutter = _settings.ClearClutter;
            if (ImGui.Checkbox("Clear trees from roads and buildings", ref clearClutter))
            {
                _settings.ClearClutter = clearClutter;
                Changed();
            }
            bool realisticTrees = _settings.RealisticTrees;
            if (ImGui.Checkbox("Trees only where real forests are (ESA WorldCover)", ref realisticTrees))
            {
                _settings.RealisticTrees = realisticTrees;
                Changed();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Removes the game's trees from water, fields, rock and snow. Turning it off brings them back after loading the game again.\n"
                    + "Land cover: © ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium (CC BY 4.0)");
            bool treePack = _settings.ForestTreePack;
            if (ImGui.Checkbox("Mix in forest tree pack (needs restart)", ref treePack))
            {
                _settings.ForestTreePack = treePack;
                Changed();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Adds the trees of \"Low Poly Forest Tree Pack\" by 99.Miles (CC BY 4.0) to the game's trees.");
            bool scatter = _settings.ScatterClutter;
            if (ImGui.Checkbox("Rocks, bushes, flowers and logs (needs restart)", ref scatter))
            {
                _settings.ScatterClutter = scatter;
                Changed();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Small things scattered on the ground by land cover, slope and noise: rocks (more on steep and bare ground), pebbles, bushes near trees, wildflower patches on meadows, fallen logs in forests. Needs the tree cover option above.");
            if (_settings.ScatterClutter)
            {
                float scatterDensity = (float)_settings.ScatterDensity;
                if (ImGui.SliderFloat("Scatter density", ref scatterDensity, 0f, 2f, "%.2f"))
                {
                    _settings.ScatterDensity = scatterDensity;
                    Changed();
                }
            }
            bool grassBoost = _settings.GrassBoost;
            if (ImGui.Checkbox("Denser, farther grass (next game start)", ref grassBoost))
            {
                _settings.GrassBoost = grassBoost;
                Changed();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The game's grass with more blades, visible farther (thinned out with distance to save time), in the colour of the satellite-tinted ground.");
            if (_settings.GrassBoost)
            {
                float grassRange = (float)_settings.GrassRangeMeters, grassDensity = (float)_settings.GrassDensity, grassKeep = (float)_settings.GrassFarKeep;
                if (ImGui.SliderFloat("Grass range m (restart)", ref grassRange, 90f, 320f, "%.0f")) { _settings.GrassRangeMeters = grassRange; Changed(); }
                if (ImGui.SliderFloat("Grass density x (restart)", ref grassDensity, 0.5f, 4f, "%.2f")) { _settings.GrassDensity = grassDensity; Changed(); }
                if (ImGui.SliderFloat("Grass kept at the far edge (restart)", ref grassKeep, 0.05f, 1f, "%.2f")) { _settings.GrassFarKeep = grassKeep; Changed(); }
                bool grassColour = _settings.GrassSatelliteColor;
                if (ImGui.Checkbox("Grass colour from satellite imagery (restart, needs ground colour)", ref grassColour)) { _settings.GrassSatelliteColor = grassColour; Changed(); }
                ImGui.Text($"Grass: {GrassBoost.Status}");
            }
            bool lights = _settings.NightLights;
            if (ImGui.Checkbox("Night lights", ref lights))
            {
                _settings.NightLights = lights;
                Changed();
            }
            float intensity = (float)_settings.LightIntensity;
            if (ImGui.SliderFloat("Light brightness", ref intensity, 10f, 5000f, "%.0f", ImGuiSliderFlags.Logarithmic))
            {
                _settings.LightIntensity = intensity;
                Changed();
            }
            int maxLights = _settings.MaxLights;
            if (ImGui.SliderInt("Max lights", ref maxLights, 0, 600))
            {
                _settings.MaxLights = maxLights;
                Changed();
            }
            bool glow = _settings.CityGlow;
            if (ImGui.Checkbox("Night glow (visible from orbit)", ref glow))
            {
                _settings.CityGlow = glow;
                Changed();
            }
            float glowBrightness = (float)_settings.GlowBrightness;
            if (ImGui.SliderFloat("Glow brightness", ref glowBrightness, 0.1f, 100f, "%.1f", ImGuiSliderFlags.Logarithmic))
            {
                _settings.GlowBrightness = glowBrightness;
                Changed();
            }
            bool regional = _settings.RegionalLights;
            if (ImGui.Checkbox("City lights from orbit", ref regional))
            {
                _settings.RegionalLights = regional;
                Changed();
            }
            float regionalRadius = (float)_settings.RegionalRadiusKm;
            if (ImGui.SliderFloat("City lights radius (km)", ref regionalRadius, 200f, 3000f, "%.0f"))
            {
                _settings.RegionalRadiusKm = regionalRadius;
                Changed();
            }
            bool globalLights = _settings.GlobalLights;
            if (ImGui.Checkbox("Major cities on the whole night side", ref globalLights))
            {
                _settings.GlobalLights = globalLights;
                Changed();
            }
            ImGui.Text("Street lights:");
            foreach (var (mode, label) in StreetLightModes)
            {
                ImGui.SameLine();
                if (ImGui.RadioButton(label, _settings.StreetLights == mode))
                {
                    _settings.StreetLights = mode;
                    Changed();
                }
            }
            if (_settings.StreetLights == "Data")
                ImGui.TextWrapped("Mapped lamps and lit roads from OpenStreetMap via the Overpass API (StreetLightOverpassUrls in "
                    + "settings.json). The public servers are shared and slow: prefer your own instance. Press Reload tiles after a change.");

            ImGui.Separator();
            bool debug = _settings.DebugLogging;
            if (ImGui.Checkbox("Debug (statistics here, progress in the game's log)", ref debug))
                SetDebug(debug);
            if (debug)
                DrawStatistics(manager);
            if (_settings.SatelliteImagery && SatelliteImagery.LastError is { } satelliteError)
                ImGui.TextColored(new float4(1, 0.5f, 0.3f, 1), $"Satellite: {satelliteError}");
            if (_settings.CityGlow && GlowRenderer.Error is { } glowError)
                ImGui.TextColored(new float4(1, 0.5f, 0.3f, 1), $"Glow off: {glowError}");
            if (BuildingRenderer.ModelsFound < Buildings.VariantCount)
                ImGui.TextColored(new float4(1, 0.5f, 0.3f, 1), $"Building models missing ({BuildingRenderer.ModelsFound}/{Buildings.VariantCount}), check Assets.xml");
            if (manager.LastError is { } error)
                ImGui.TextWrapped($"Last error: {error}");

            if (ImGui.Button("Reload tiles"))
            {
                manager.Clear();
                _regional?.Clear();
                _global?.Clear();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear download cache"))
                manager.ClearDiskCache();

            ImGui.Separator();
            ImGui.TextDisabled("Data (c) OpenStreetMap contributors (ODbL), (c) OpenMapTiles, tiles by OpenFreeMap");
            ImGui.TextDisabled("Imagery: EOxCloudless https://cloudless.eox.at by EOX IT Services GmbH");
            ImGui.TextDisabled("(contains modified Copernicus Sentinel data 2016), CC BY 4.0");
            ImGui.TextDisabled("Launch tower model from NASA 3D Resources \"Gantry\" (NASA / Michael D. Carbajal)");
        }
        ImGui.End();
        if (!open)
        {
            _settings.ShowWindow = false;
            Changed();
        }
    }

    private void DrawStatistics(TileManager manager)
    {
        var stats = manager.Stats();
        if (double.IsNaN(manager.CameraAltitudeKm))
            ImGui.Text($"Waiting for {_settings.Body}...");
        else
            ImGui.Text($"Camera {manager.CameraLat:0.0000}, {manager.CameraLon:0.0000}  alt {manager.CameraAltitudeKm:0.0} km");
        if (!manager.Streaming && _settings.Enabled)
            ImGui.TextDisabled($"Not loading: camera above {_settings.MaxCameraAltitudeKm:0} km");
        ImGui.Text($"Tiles: {stats.Ready} ready, {stats.Placing} placing, {stats.Loading} downloading, {stats.Queued} queued, {stats.Failed} failed");
        ImGui.Text($"Buildings: {stats.Buildings:N0}   roads: {stats.Roads:N0}   drawn pieces: {BuildingRenderer.LastDrawn:N0}");
        if (_far != null && _settings.FarBuildings)
        {
            var fs = _far.Stats();
            ImGui.Text($"Far field: {fs.Ready} tiles, {fs.Buildings:N0} buildings, {fs.Pending} loading");
        }
        if (_settings.Rivers)
            ImGui.Text($"Rivers: {(RiverBuilder.Status.Length > 0 ? RiverBuilder.Status : "waiting for water tiles")}");
        if (_settings.RealTerrain || _settings.RealShores)
            ImGui.Text($"Real elevation: {Elevation.Status}");
        if (_water != null && _settings.Water)
        {
            var ws = _water.Stats();
            ImGui.Text($"Water: {ws.Ready} tiles, {ws.Triangles:N0} triangles, {ws.Pending} loading");
            ImGui.Text($"Lakes: {(LakeOcean.Status.Length > 0 ? LakeOcean.Status : "waiting for water")}");
        }
        if (_settings.SatelliteImagery)
            ImGui.Text($"Satellite:{SatelliteImagery.LastTiles} tiles drawn, {SatelliteImagery.LoadedLayers} loaded");
        if (_settings.GroundColorFromSatellite)
            ImGui.Text($"Ground colour: {GroundTint.Status}");
        if (_settings.BuildingShadows)
            ImGui.Text($"Shadow casters: {ShadowCasters.LastCount:N0}{(ShadowCasters.Error is { } se ? $" (off: {se})" : "")}");
        if (_settings.ClearClutter)
            ImGui.Text($"Clutter removed: {manager.Clutter.Excluded:N0} ({manager.Clutter.Status})");
        if (_settings.RealisticTrees && manager.Clutter.TreeCoverError is { } treeError)
            ImGui.TextColored(new float4(1, 0.5f, 0.3f, 1), $"Tree cover off: {treeError}");
        else if (_settings.RealisticTrees && manager.Clutter.Trees is { } trees)
            ImGui.Text($"Tree cover: {trees.Removed:N0} removed in {trees.Cells:N0} cells ({trees.Status})");
        if (_settings.LaunchPadClearRadiusKm > 0)
            ImGui.TextDisabled($"No plain buildings within {_settings.LaunchPadClearRadiusKm:0.#} km of {manager.LaunchPadCount} launch pads");
        if (_settings.NightLights)
            ImGui.Text(NightLights.LastNightFactor > 0 ? $"Night: {NightLights.LastNightFactor:P0}, {NightLights.LastLights} lights on" : "Day: lights off");
        if (_settings.CityGlow)
        {
            ImGui.Text($"Glow: {GlowRenderer.LastSprites:N0} sprites from {GlowRenderer.LastSets} light sets");
            if (_regional != null && _settings.RegionalLights)
            {
                var r = _regional.Stats();
                ImGui.Text(_regional.Active || r.Ready > 0
                    ? $"City lights: {r.Ready} tiles ({r.Lights:N0} lights), {r.Loading + r.Queued} loading, {r.Failed} failed"
                    : "City lights: load above 5 km");
            }
            if (_global != null && _settings.GlobalLights && (_global.Active || _global.Stats().Ready > 0))
            {
                var g = _global.Stats();
                ImGui.Text($"Major cities: {g.Ready} tiles ({g.Lights:N0} lights), {g.Loading + g.Queued} loading, {g.Failed} failed");
            }
            if (_settings.Windows)
                ImGui.Text($"Windows on {FacadeRenderer.LastBoxes:N0} walls");
        }
        if (Perf.Summary.Length > 0)
            ImGui.TextWrapped("Perf: " + Perf.Summary);
    }
}
