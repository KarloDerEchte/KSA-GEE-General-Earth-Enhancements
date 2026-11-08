using Brutal.Numerics;

// Tiny stand-ins for game-side types that pure-logic sources refer to. The real ones live in Tiles.cs / Water.cs, which
// also hold Vulkan code and so cannot be compiled without the game. Keep the members in step with the originals.
namespace GEE
{
    /// <summary>Collects what BuildingDetail adds to a tile (real: Tiles.cs Tile).</summary>
    internal sealed class Tile
    {
        public readonly List<BuildingInstance> Instances = new();
        public void Add(BuildingInstance instance) => Instances.Add(instance);
    }

    /// <summary>Real: Water.cs.</summary>
    public struct WaterVertex
    {
        public float X, Y, Z, Wx, Wy, Across, Kind;
    }

    /// <summary>Real: Water.cs.</summary>
    public sealed class LakeInfo
    {
        public int FirstVertex, VertexCount;
        public double AreaM2;
        public double InsideMedian = double.NaN;
        public double[] InsideGeo = Array.Empty<double>(), ShoreGeo = Array.Empty<double>();
        public double ShoreM;
        public double[] Shore = Array.Empty<double>();
        public int Edges;
        public bool IsRiver, Intermittent, IsTidal;
        public List<double[]>? HoleGeo;
    }

    /// <summary>Real: Water.cs (without the loading state machine).</summary>
    internal sealed class WaterTile(int x, int y)
    {
        public const int Zoom = 13;
        public readonly int X = x, Y = y;
        public WaterVertex[] Vertices = Array.Empty<WaterVertex>();
        public int Count;
        public List<LakeInfo> Lakes = new();
        public List<RiverLine> Rivers = new();
        public List<double[]> RiverAreas = new();
        public double3 CenterCcf;
        public double RadiusM;
        public (double S, double W, double N, double E) Bounds => VectorTile.Bounds(Zoom, X, Y);
    }
}

namespace GEE
{
    /// <summary>Real: Log.cs (writes to the game's log through Brutal.Logging). Here: collected for tests.</summary>
    internal static class Log
    {
        public static bool Debug;
        public static readonly List<string> Lines = new();
        public static void Info(string message) { lock (Lines) Lines.Add(message); }
        public static void Notice(string message) { lock (Lines) Lines.Add(message); }
        public static void Error(string message) { lock (Lines) Lines.Add("ERROR " + message); }
    }
}
