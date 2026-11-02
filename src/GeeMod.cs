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
                _far?.Update(body, cameraCcf, now);
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
        _regional?.Dispose();
        _manager?.Dispose();
        _settings.Save(_settingsPath);
        BuildingRenderer.Manager = null;
        GlowRenderer.Manager = null;
        GlowRenderer.Regional = null;
        GlowRenderer.Global = null;
        Instance = null;
    }

    private void EnsureStarted()
    {
        if (Instance != null)
            return;
        Instance = this;
        try
        {
            _dir = Path.GetDirectoryName(typeof(GeeMod).Assembly.Location)!;
            _settingsPath = Path.Combine(_dir, "settings.json");
            _settings = Settings.Load(_settingsPath);
            _padRadiusAtLoad = _settings.LaunchPadClearRadiusKm;
            _manager = new TileManager(_settings, _dir);
            _regional = new RegionalLights(_settings, _manager, RegionalLights.RegionalZoom);
            _global = new RegionalLights(_settings, _manager, RegionalLights.GlobalZoom, _regional);
            _far = new FarField(_settings, _manager);
            BuildingRenderer.Far = _far;
            BuildingRenderer.Settings = _settings;
            BuildingRenderer.Manager = _manager;
            GlowRenderer.Settings = _settings;
            GlowRenderer.Manager = _manager;
            GlowRenderer.Regional = _regional;
            GlowRenderer.Global = _global;
            SatelliteImagery.Settings = _settings;
            SatelliteImagery.ModDir = _dir;
            _harmony = new Harmony("GEE");
            Patches.Apply(_harmony);
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
            if (ImGui.SliderFloat("Draw distance (km)", ref drawDistance, 1f, 40f, "%.0f"))
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
            if (_settings.SatelliteImagery)
                ImGui.Text($"Satellite: {SatelliteImagery.LastTiles} tiles drawn, {SatelliteImagery.LoadedLayers} loaded{(SatelliteImagery.LastError is { } se ? ", " + se : "")}");
            bool far = _settings.FarBuildings;
            if (ImGui.Checkbox("Far buildings (simplified, beyond the loaded tiles)", ref far))
            {
                _settings.FarBuildings = far;
                Changed();
            }
            float farRadius = (float)_settings.FarRadiusKm;
            if (ImGui.SliderFloat("Far radius (km)", ref farRadius, 10f, 80f, "%.0f"))
            {
                _settings.FarRadiusKm = farRadius;
                Changed();
            }
            if (_far != null)
            {
                var fs = _far.Stats();
                ImGui.Text($"Far field: {fs.Ready} tiles, {fs.Buildings:N0} buildings, {fs.Pending} loading");
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
            float roadDistance = (float)_settings.RoadDrawDistanceKm;
            if (ImGui.SliderFloat("Road distance (km)", ref roadDistance, 0.5f, 20f, "%.1f"))
            {
                _settings.RoadDrawDistanceKm = roadDistance;
                Changed();
            }
            float padRadius = (float)_settings.LaunchPadClearRadiusKm;
            if (ImGui.SliderFloat("No buildings near pads (km)", ref padRadius, 0f, 20f, "%.1f"))
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
            var stats = manager.Stats();
            if (double.IsNaN(manager.CameraAltitudeKm))
                ImGui.Text($"Waiting for {_settings.Body}...");
            else
                ImGui.Text($"Camera {manager.CameraLat:0.0000}, {manager.CameraLon:0.0000}  alt {manager.CameraAltitudeKm:0.0} km");
            if (!manager.Streaming && _settings.Enabled)
                ImGui.TextDisabled($"Not loading: camera above {_settings.MaxCameraAltitudeKm:0} km");
            ImGui.Text($"Tiles: {stats.Ready} ready, {stats.Placing} placing, {stats.Loading} downloading, {stats.Queued} queued, {stats.Failed} failed");
            ImGui.Text($"Buildings: {stats.Buildings:N0}   roads: {stats.Roads:N0}   drawn pieces: {BuildingRenderer.LastDrawn:N0}");
            if (_settings.ClearClutter)
                ImGui.Text($"Clutter removed: {manager.Clutter.Excluded:N0} ({manager.Clutter.Status})");
            if (_settings.LaunchPadClearRadiusKm > 0)
                ImGui.TextDisabled($"No buildings within {_settings.LaunchPadClearRadiusKm:0.#} km of {manager.LaunchPadCount} launch pads");
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
                if (GlowRenderer.Error is { } glowError)
                    ImGui.TextColored(new float4(1, 0.5f, 0.3f, 1), $"Glow off: {glowError}");
            }
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
}
