using System.Text.Json;

namespace GEE;

/// <summary>User settings, stored as settings.json next to the mod.</summary>
public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool ShowWindow { get; set; } = true;
    public string Body { get; set; } = "Earth";
    /// <summary>Progress and timing lines in the game's log, and statistics in the window. Errors are always logged.</summary>
    public bool DebugLogging { get; set; }

    /// <summary>Tiles are requested while the camera is below this altitude (and above the ground).</summary>
    public double MaxCameraAltitudeKm { get; set; } = 20;

    public const double MaxLoadRadiusKm = 10;
    private double _loadRadiusKm = 3;
    public double LoadRadiusKm
    {
        get => _loadRadiusKm;
        set => _loadRadiusKm = Math.Clamp(value, 0.5, MaxLoadRadiusKm);
    }
    public double RenderDistanceKm { get; set; } = 15;
    public bool ShowRoads { get; set; } = true;
    /// <summary>Plain OSM buildings (boxes with facades and roofs). Off leaves roads, lights and the launch complexes.</summary>
    public bool ShowBuildings { get; set; } = true;
    /// <summary>The modelled launch complexes (LC-39A/B towers, VAB, hangar, crew arm, pipelines, tank farm).</summary>
    public bool LaunchComplexModels { get; set; } = true;
    public double RoadDrawDistanceKm { get; set; } = 5;
    public int MaxInstances { get; set; } = 150000;
    /// <summary>Gabled roofs on small houses (tiles must be reloaded).</summary>
    public bool Roofs { get; set; } = true;
    /// <summary>Runways, taxiways and aprons with their lights (tiles must be reloaded).</summary>
    public bool Airports { get; set; } = true;
    /// <summary>Window pattern on facades, lit at night, up to this distance.</summary>
    public bool Windows { get; set; } = true;
    public double WindowDistanceKm { get; set; } = 6;
    /// <summary>Plinths, entrances, cornices, parapets and roof units on buildings, up to this distance.</summary>
    public bool BuildingDetails { get; set; } = true;
    public double DetailDistanceKm { get; set; } = 0.8;

    /// <summary>No buildings within this distance of a launch pad (roads stay); 0 turns it off.</summary>
    public double LaunchPadClearRadiusKm { get; set; } = 5;
    /// <summary>Removes the game's trees, shrubs and grass from roads and buildings.</summary>
    public bool ClearClutter { get; set; } = true;
    /// <summary>Most a tree's trunk keeps away from a road edge or wall; small clutter keeps less.</summary>
    public double TreeClearanceMeters { get; set; } = 3;
    /// <summary>Main-thread time per frame spent handing clutter exclusions to the game.</summary>
    public double ClutterBudgetMs { get; set; } = 1.5;

    /// <summary>
    /// Where street lights are: "BuiltUp" (default: within towns and villages, from the building density; motorways
    /// only in dense urban areas, as in Germany and much of Europe), "Data" (opt-in: OpenStreetMap lamp posts and lit
    /// tags from an Overpass API server, cached; where it knows nothing, as "BuiltUp"), "All" or "None". Airfield
    /// lights are not affected.
    /// </summary>
    public string StreetLights { get; set; } = "BuiltUp";
    /// <summary>
    /// Overpass API servers for StreetLights "Data", comma separated, one worker each. The public servers ask not to
    /// be the backend of apps used by many people, so the default is one of them, used slowly; point this at your own
    /// Overpass instance where you can.
    /// </summary>
    public string StreetLightOverpassUrls { get; set; } = "https://overpass-api.de/api/interpreter";
    /// <summary>Street light data is prefetched (and cached for good) up to this distance from the camera.</summary>
    public double StreetLightDataRadiusKm { get; set; } = 15;
    /// <summary>1 m reflector posts along roads where there are no street lights, near the camera.</summary>
    public bool ReflectorPosts { get; set; } = true;

    /// <summary>
    /// Far field: simplified buildings from coarser tiles beyond the loaded ones, out to FarRadiusKm, streaming up to
    /// FarMaxAltitudeKm (the detailed tiles stop at MaxCameraAltitudeKm).
    /// </summary>
    public bool FarBuildings { get; set; } = true;
    public double FarRadiusKm { get; set; } = 35;
    public double FarMaxAltitudeKm { get; set; } = 80;
    public double FarPlacementBudgetMs { get; set; } = 2;

    /// <summary>
    /// Sentinel-2 cloudless satellite imagery (EOX, 2016, CC BY 4.0) over the ground when seen from altitude: fades in
    /// from SatelliteFadeFromM to four times that height.
    /// </summary>
    public bool SatelliteImagery { get; set; } = true;
    public double SatelliteFadeFromM { get; set; } = 1500;

    /// <summary>Vapour boiling off the cryogenic tanks of launch complexes.</summary>
    public bool TankVapor { get; set; } = true;

    /// <summary>Facade lights and street lamps after sunset, nearest first.</summary>
    public bool NightLights { get; set; } = true;
    /// <summary>The game's clustered lighting takes 1024 lights per frame in total, vehicles included.</summary>
    public int MaxLights { get; set; } = 200;
    public double LightIntensity { get; set; } = 300;
    public double LightRangeMeters { get; set; } = 28;
    public string SunId { get; set; } = "Sol";

    /// <summary>Glowing windows, street lamps and city lights at night that stay visible up to orbit.</summary>
    public bool CityGlow { get; set; } = true;
    public double GlowBrightness { get; set; } = 1.5;
    /// <summary>Sprites per frame; beyond it lights are thinned out more.</summary>
    public int MaxGlowSprites { get; set; } = 250000;
    /// <summary>City lights from coarse map tiles around the camera when it is above 5 km, for the view from orbit.</summary>
    public bool RegionalLights { get; set; } = true;
    public double RegionalRadiusKm { get; set; } = 1000;
    /// <summary>Major cities over the whole visible night side, from very coarse tiles, beyond RegionalRadiusKm.</summary>
    public bool GlobalLights { get; set; } = true;

    /// <summary>TileJSON of an OpenMapTiles vector tile source; its zoom 14 tiles carry the buildings.</summary>
    public string TileJsonUrl { get; set; } = "https://tiles.openfreemap.org/planet";
    public int DownloadThreads { get; set; } = 4;

    /// <summary>Extra depth below the lowest terrain sample, hides gaps on slopes and terrain mismatches.</summary>
    public double FoundationMeters { get; set; } = 4;
    /// <summary>Main-thread time per frame spent sampling terrain heights for new buildings.</summary>
    public double PlacementBudgetMs { get; set; } = 4;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Error($"could not read {path}, using defaults: {e.Message}");
        }
        var settings = new Settings();
        settings.Save(path);
        return settings;
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Log.Error($"could not write {path}: {e.Message}");
        }
    }
}
