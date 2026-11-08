using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>
/// Makes the river maps around the camera (RiverMap) from the rivers and river banks of the water tiles (WaterField), on a worker thread: a
/// near window (small, fine) and a far one (80 km, only the big rivers). A window is made again when the camera has moved a good way or
/// other tiles with rivers have loaded; a map equal to the one in use is not uploaded. LakeOcean uploads them, the terrain's colour shader paints them.
/// </summary>
internal static class RiverBuilder
{
    public static Settings? Settings;
    private static string _nearStatus = "", _farStatus = "";
    public static string Status => $"near {_nearStatus}; far {_farStatus}";
    /// <summary>The windows the GPU has (set when they are uploaded).</summary>
    public static volatile RiverMap.Window? Current, CurrentFar;

    private sealed class Track
    {
        public volatile RiverMap.Window? Ready;
        public int Building, Hash;
        public bool Have, LastEmpty;
        public double Lat, Lon, LastAt = -100;
    }

    private static readonly Track Near = new(), Far = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    public static RiverMap.Window? TakeReady() => Take(Near);

    public static RiverMap.Window? TakeReadyFar() => Take(Far);

    private static RiverMap.Window? Take(Track track)
    {
        var window = track.Ready;
        track.Ready = null;
        return window;
    }

    /// <summary>Main thread, once per frame.</summary>
    public static void Update(Celestial body, double3 cameraCcf)
    {
        if (Settings is not { Enabled: true, Rivers: true } || LakeOcean.Field is not { Active: true } field)
            return;
        double r = Math.Sqrt(double3.Dot(cameraCcf, cameraCcf)), planetR = body.MeanRadius;
        double lat = Math.Asin(Math.Clamp(cameraCcf.Z / r, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(cameraCcf.Y, cameraCcf.X) * 180 / Math.PI;
        double now = Clock.Elapsed.TotalSeconds;
        var tiles = field.Snapshot;

        // A fingerprint of the tiles with rivers near each window (no allocation).
        int nearHash = 17, farHash = 17;
        foreach (var tile in tiles)
        {
            if (tile.Rivers.Count == 0 && tile.RiverAreas.Count == 0)
                continue;
            double distance = (tile.CenterCcf - cameraCcf).Length();
            int id = (tile.X * 73856093) ^ (tile.Y * 19349663);
            if (distance <= RiverMap.NearMeters * 0.9 + tile.RadiusM)
                nearHash += id;
            if (distance <= RiverMap.FarMeters * 0.75 + tile.RadiusM)
                farHash += id;
        }

        if (Wants(Near, lat, lon, planetR, nearHash, now, 3000, 1.5))
            Start(Near, lat, lon, planetR, nearHash, now, tiles, cameraCcf, RiverMap.NearMeters, 0.9, 0);
        if (Wants(Far, lat, lon, planetR, farHash, now, 10000, 3))
            Start(Far, lat, lon, planetR, farHash, now, tiles, cameraCcf, RiverMap.FarMeters, 0.75, RiverMap.FarMinWidth);
    }

    private static bool Wants(Track track, double lat, double lon, double planetR, int hash, double now, double moveMeters, double debounce)
    {
        double dLat = (lat - track.Lat) * Math.PI / 180 * planetR, dLon = (lon - track.Lon) * Math.PI / 180 * planetR * Math.Cos(lat * Math.PI / 180);
        bool moved = !track.Have || Math.Sqrt(dLat * dLat + dLon * dLon) > moveMeters;
        return (moved || (hash != track.Hash && now - track.LastAt > debounce)) && Volatile.Read(ref track.Building) == 0;
    }

    private static void Start(Track track, double lat, double lon, double planetR, int hash, double now, WaterTile[] tiles, double3 cameraCcf,
        double windowMeters, double reach, double minWidth)
    {
        if (Interlocked.CompareExchange(ref track.Building, 1, 0) != 0)
            return;
        track.Have = true;
        track.Lat = lat;
        track.Lon = lon;
        track.Hash = hash;
        track.LastAt = now;
        // The lines and areas of the tiles in reach (collected now, the tile lists are not changed after a tile is ready).
        var lines = new List<RiverLine>();
        var areas = new List<double[]>();
        foreach (var tile in tiles)
        {
            if ((tile.CenterCcf - cameraCcf).Length() > windowMeters * reach + tile.RadiusM)
                continue;
            foreach (var line in tile.Rivers)
                if (line.Width >= minWidth)
                    lines.Add(line);
            areas.AddRange(tile.RiverAreas);
        }
        // Nothing in reach now and nothing in the last window either: it would be the same empty map again (the shader sees no window).
        bool empty = lines.Count == 0 && areas.Count == 0;
        if (empty && track.LastEmpty)
        {
            Interlocked.Exchange(ref track.Building, 0);
            return;
        }
        track.LastEmpty = empty;
        Task.Run(() =>
        {
            try
            {
                var window = RiverMap.Build(lat, lon, planetR, lines, areas, windowMeters, minWidth);
                var current = windowMeters == RiverMap.NearMeters ? Current : CurrentFar;
                // The same map as the one in use: nothing to upload.
                if (current == null || current.Signature != window.Signature)
                    track.Ready = window;
                if (windowMeters == RiverMap.NearMeters)
                    _nearStatus = $"{window.Lines} lines, {window.Areas} areas";
                else
                    _farStatus = $"{window.Lines} lines, {window.Areas} areas";
            }
            catch (Exception e)
            {
                Log.Error($"rivers: map failed: {e}");
            }
            finally
            {
                Interlocked.Exchange(ref track.Building, 0);
            }
        });
    }
}
