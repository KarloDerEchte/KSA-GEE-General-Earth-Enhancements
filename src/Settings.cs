using System.Text.Json;

namespace GEE;

/// <summary>User settings, stored as settings.json next to the mod.</summary>
public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool ShowWindow { get; set; } = false;
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
    public double RenderDistanceKm { get; set; } = 100;
    public bool ShowRoads { get; set; } = true;
    /// <summary>Road slabs are solid for vehicles (RoadCollision), with sloped shoulders.</summary>
    public bool RoadCollision { get; set; } = true;
    /// <summary>Plain OSM buildings (boxes with facades and roofs). Off leaves roads, lights and the launch complexes.</summary>
    public bool ShowBuildings { get; set; } = true;
    /// <summary>The modelled launch complexes (LC-39A/B towers, VAB, hangar, crew arm, pipelines, tank farm).</summary>
    public bool LaunchComplexModels { get; set; } = true;
    public double RoadDrawDistanceKm { get; set; } = 5;
    public int MaxInstances { get; set; } = 150000;
    /// <summary>Gabled roofs on small houses (tiles must be reloaded).</summary>
    public bool Roofs { get; set; } = true;
    /// <summary>Rounded curb corners at road junctions (tiles must be reloaded).</summary>
    public bool JunctionFillets { get; set; } = true;
    /// <summary>Crosswalks and stop lines at junctions in built-up areas (tiles must be reloaded).</summary>
    public bool JunctionMarkings { get; set; } = true;
    /// <summary>Runways, taxiways and aprons with their lights (tiles must be reloaded).</summary>
    public bool Airports { get; set; } = true;
    /// <summary>Window pattern on facades, lit at night, up to this distance.</summary>
    public bool Windows { get; set; } = true;
    public double WindowDistanceKm { get; set; } = 6;
    /// <summary>Plinths, entrances, cornices, parapets and roof units on buildings, up to this distance.</summary>
    public bool BuildingDetails { get; set; } = true;
    public double DetailDistanceKm { get; set; } = 0.8;
    /// <summary>Lamp posts and sign posts/gantries (RoadFurniture) are drawn up to this distance (at least DetailDistanceKm); they are tall, so they stay visible far longer than other relief.</summary>
    public double FurnitureDistanceKm { get; set; } = 3.5;

    /// <summary>
    /// No plain buildings within this distance of a launch pad (roads, the launch complex models and the buildings of
    /// a launch complex stay); 0 turns it off.
    /// </summary>
    public double LaunchPadClearRadiusKm { get; set; } = 5;
    /// <summary>Removes the game's trees, shrubs and grass from roads and buildings.</summary>
    public bool ClearClutter { get; set; } = true;
    /// <summary>Most a tree's trunk keeps away from a road edge or wall; small clutter keeps less.</summary>
    public double TreeClearanceMeters { get; set; } = 3;
    /// <summary>
    /// Keeps the game's trees, shrubs and grass only where ESA WorldCover land cover says they grow (forest, shrubland,
    /// meadows), with a few lone trees in open land; removes them from water, fields, rock and snow. Switching it off
    /// brings the removed ones back after the game is loaded again.
    /// </summary>
    public bool RealisticTrees { get; set; } = true;
    /// <summary>
    /// Mixes the trees of the Low Poly Forest Tree Pack into the game's trees (TreePack.cs). Takes effect at the
    /// next game start (the game builds its clutter objects once).
    /// </summary>
    public bool ForestTreePack { get; set; } = true;
    /// <summary>
    /// Rocks, pebbles, bushes, wildflower patches and fallen logs scattered as ground clutter (Scatter.cs), placed by
    /// land cover, slope and noise. Takes effect at the next game start; needs RealisticTrees.
    /// </summary>
    public bool ScatterClutter { get; set; } = true;
    /// <summary>Multiplies the share of scatter objects that stay (0 = none, 1 = default, 2 = twice as many); applies to cells sorted from now on.</summary>
    public double ScatterDensity { get; set; } = 1;
    /// <summary>
    /// The game's own grass (GrassBoost.cs; all of it takes effect at the next game start, it is baked into the
    /// clutter data and shaders): denser (GrassDensity x the game's instances per area), visible up to GrassRangeMeters
    /// (game: 80 m; thinned out with distance down to the share GrassFarKeep to keep the cost), and coloured like the
    /// satellite-tinted terrain (needs GroundColorFromSatellite).
    /// </summary>
    public bool GrassBoost { get; set; } = true;
    public double GrassRangeMeters { get; set; } = 200;
    public double GrassDensity { get; set; } = 1.6;
    public double GrassFarKeep { get; set; } = 0.3;
    public bool GrassSatelliteColor { get; set; } = true;
    /// <summary>
    /// Own grass layer (GrassLayer.cs): camera-centred blade clumps out to GrassLayerRadiusM that sway in the wind and
    /// shine through towards the sun; not on roads, buildings, pads, water, snow or steep slopes. Every setting here
    /// applies at once. Density is the share of 1 m cells that grow a clump.
    /// </summary>
    public bool GrassLayer { get; set; } = true;
    public double GrassLayerRadiusM { get; set; } = 50;
    public double GrassLayerDensity { get; set; } = 0.8;
    public double GrassLayerHeightM { get; set; } = 0.45;
    public double GrassLayerClumpMeters { get; set; } = 0.55;
    public double GrassLayerSway { get; set; } = 0.6;
    public double GrassLayerTranslucency { get; set; } = 0.7;
    public double GrassLayerSlopeMaxDeg { get; set; } = 38;
    /// <summary>Colour and density by ESA WorldCover class (downloads blocks as the tree cover does); off: one grass everywhere.</summary>
    public bool GrassLayerLandCover { get; set; } = true;
    /// <summary>
    /// Colour grade after the game's tonemapper (PostFx.cs; SDR output only, next game start). Contrast 0..1 is the
    /// strength of an S-curve, saturation 1 = unchanged, vibrance lifts dull colours more, warmth -1 (cool) .. 1 (warm),
    /// vignette darkens the corners.
    /// </summary>
    public bool PostGrading { get; set; } = true;
    /// <summary>
    /// Road wear in the static object shaders (RoadWear.cs; next game start): brightness patches per piece, darker wheel
    /// tracks, oil drips, repair patches and brake marks at junctions. Strength 1 = default, 0 = none.
    /// </summary>
    public bool RoadWear { get; set; } = true;
    public double RoadWearStrength { get; set; } = 1.0;
    public double GradeContrast { get; set; } = 0.25;
    public double GradeSaturation { get; set; } = 1.05;
    public double GradeVibrance { get; set; } = 0.25;
    public double GradeWarmth { get; set; } = 0;
    public double GradeVignette { get; set; } = 0.12;
    /// <summary>
    /// Lakes and rivers (OpenStreetMap water from the vector tiles) with their own water shader (Water.cs): the game
    /// has only the ocean. Loaded up to WaterRadiusKm around the camera, below WaterMaxAltitudeKm.
    /// </summary>
    public bool Water { get; set; } = true;
    /// <summary>
    /// Real elevation (AWS Terrain Tiles, zoom 12, about 38 m) in an 80 km window around the camera instead of the game's coarse
    /// heightmap, with the game's own surface detail on top (Elevation.cs). Roads and buildings stand on it too. Needs a restart.
    /// </summary>
    public bool RealTerrain { get; set; } = true;
    /// <summary>
    /// Real elevation only at the lakes (Elevation.cs): the terrain stays the game's heightmap everywhere else, but near a lake
    /// (up to ShoreBlendMeters) it eases into the real shore, and the water level comes from the real data. Needs a restart.
    /// </summary>
    public bool RealShores { get; set; } = false;
    /// <summary>Sand and a dark wet edge on the ground at lake shores, a sandy bed under the water (LakeOcean.PatchPlanetShore). Needs a restart.</summary>
    public bool WetShores { get; set; } = true;
    /// <summary>
    /// Mirror reflections on the game's water (sea, lagoons, lakes): sun glint, sky reflection, Fresnel. Off (default) the ocean shader keeps its
    /// colour, depth absorption, refraction, waves and foam but draws no reflection. Applies to every ocean pass. Needs a restart.
    /// </summary>
    public bool WaterReflections { get; set; } = true;
    /// <summary>
    /// Rivers, streams and canals (OpenStreetMap, from the vector tiles) painted on the terrain as smooth, dark water in a 12 km window around
    /// the camera (RiverMap, LakeOcean.PatchPlanetShore). Needs a restart.
    /// </summary>
    public bool Rivers { get; set; } = true;
    public double WaterRadiusKm { get; set; } = 80;
    /// <summary>Lakes exist up to this altitude (the real elevation that gives their levels stops at 100 km, Elevation.MaxAltitudeKm). The lake map is an 80 km window around the camera, so from high up only the lakes below it show.</summary>
    public double WaterMaxAltitudeKm { get; set; } = 20;
    /// <summary>Water tiles within this distance of the camera are loaded whatever direction it looks (the rest only inside the view).</summary>
    public double WaterRingKm { get; set; } = 36;
    /// <summary>Smallest lake that is drawn (the whole lake over all its tiles, in km2); ponds and small lakes are left out.</summary>
    public double MinLakeAreaKm2 { get; set; } = 3;
    /// <summary>Lakes tagged as dry part of the year (Walker Lake, salt pans) are drawn from this size on (km2); smaller ones are skipped.</summary>
    public double MinIntermittentLakeAreaKm2 { get; set; } = 10;
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
    /// Roadside furniture near the camera (RoadFurniture): lamp posts under the lit street lamps (also on bridges),
    /// exit signs and cantilever sign gantries before motorway exits.
    /// </summary>
    public bool RoadFurniture { get; set; } = true;
    /// <summary>Guard rails along motorway/trunk carriageways, concrete median barriers, parapets on bridges (near the camera).</summary>
    public bool RoadBarriers { get; set; } = true;

    /// <summary>
    /// Far field: simplified buildings from coarser tiles beyond the loaded ones, out to FarRadiusKm, streaming up to
    /// FarMaxAltitudeKm (the detailed tiles stop at MaxCameraAltitudeKm).
    /// </summary>
    public bool FarBuildings { get; set; } = true;
    public double FarRadiusKm { get; set; } = 120;
    public double FarMaxAltitudeKm { get; set; } = 20;

    /// <summary>
    /// Sentinel-2 cloudless satellite imagery (EOX, 2016, CC BY 4.0) over the ground when seen from altitude: fades in
    /// from SatelliteFadeFromM to four times that height.
    /// </summary>
    public bool SatelliteImagery { get; set; } = true;
    public double SatelliteFadeFromM { get; set; } = 1500;

    /// <summary>
    /// Colours the game's terrain with the satellite imagery at every height (the game's ground textures keep their
    /// detail, the photo gives the colour), so fields, dry ground, forests and town edges show near the ground too.
    /// Takes effect at the next game start (the terrain shader is patched when the game compiles it).
    /// </summary>
    public bool GroundColorFromSatellite { get; set; } = true;
    /// <summary>0 = the satellite ground colour at every height. Default 20: the ground colour fades out between GroundTintFadeEndKm and this camera altitude (km) and the plain photo drape (SatelliteImagery) takes over above it.</summary>
    public double GroundTintFadeStartKm { get; set; } = 20;
    public double GroundTintFadeEndKm { get; set; } = 8;
    /// <summary>How much of the photo's colour the terrain takes (0 to 1).</summary>
    public double GroundColorStrength { get; set; } = 0.85;
    /// <summary>How much of the photo's brightness variation (shadows, wet patches in the imagery) the ground takes: 0 = colour only, 1 = all of it (next game start).</summary>
    public double GroundBrightnessFromSatellite { get; set; } = 0.3;
    /// <summary>Land cover (ESA WorldCover) shapes the ground detail (GroundClasses.cs; next game start, needs the satellite ground colour): cropland furrow rows, gravel grain and ripples on bare ground, wet pools, snow crust.</summary>
    public bool GroundClasses { get; set; } = true;
    public double GroundClassStrength { get; set; } = 0.8;
    /// <summary>Extra ground detail in the terrain shader (GroundDetail.cs; next game start, needs the satellite ground colour): non-tiling texture detail, macro variation, slope materials, creases.</summary>
    public bool GroundDetail { get; set; } = true;
    public double GroundDetailStrength { get; set; } = 0.6;
    public double GroundMacroStrength { get; set; } = 0.35;
    public double GroundSlopeStrength { get; set; } = 0.5;
    public double GroundCavityStrength { get; set; } = 0.35;
    /// <summary>Small photographed grass/dirt/rock textures in the terrain shader within GroundTextureRangeM of the camera (GroundTexture.cs; next game start, needs the satellite ground colour): luminance detail that keeps the satellite colour, plus a detail normal. Scale: length of the near tile in metres.</summary>
    public bool GroundTexture { get; set; } = true;
    public double GroundTextureStrength { get; set; } = 0.8;
    public double GroundTextureNormalStrength { get; set; } = 0.6;
    public double GroundTextureScaleM { get; set; } = 1.0;
    public double GroundTextureRangeM { get; set; } = 150;
    // Photo-to-game colour correction, baked into the terrain shader (next game start). Warmth scales the
    // (1.25, 1, 0.6) tint exponent; vegetation (green) and bare ground (brown/other) get their own factor on top;
    // bright pixels are warmed less. Saturation 1 = photo's, Gamma > 1 = more contrast in the photo's brightness.
    public double GroundColorWarmth { get; set; } = 1.0;
    public double GroundColorVegetationWarmth { get; set; } = 1.15;
    public double GroundColorBareWarmth { get; set; } = 0.85;
    public double GroundColorSaturation { get; set; } = 0.85;
    public double GroundColorGamma { get; set; } = 1.0;

    /// <summary>Buildings, roofs and tanks cast shadows from the sun (within the game's 1.5 km shadow range).</summary>
    public bool BuildingShadows { get; set; } = true;

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
