using Brutal.Numerics;

namespace OsmBuildings;

public enum LightKind { Building, Lamp, Regional }

/// <summary>One glowing point, relative to its set's centre (CCF axes).</summary>
public struct LightPoint
{
    public float3 Offset;
    /// <summary>Glow radius in metres.</summary>
    public float Radius;
    /// <summary>Linear RGB in the low three bytes, intensity / 16 in the high byte.</summary>
    public uint Color;
    /// <summary>Random in [0, 1); far away only the lights below the set's keep fraction are drawn.</summary>
    public float Level;
}

/// <summary>
/// Lights of one tile and kind. Offsets are floats around a double centre, so a frame only adds the
/// centre's camera-relative position instead of transforming every light in doubles.
/// </summary>
public sealed class LightSet(LightKind kind, double3 center, float spacing)
{
    public readonly LightKind Kind = kind;
    public readonly double3 Center = center;
    /// <summary>Typical distance between neighbouring lights; sets how far they can be thinned out.</summary>
    public readonly float Spacing = spacing;
    /// <summary>Key of the finer regional tile covering this set (RegionalLights.Key); it is left out where that tile is ready.</summary>
    public int CoverKey = -1;
    /// <summary>Bounding radius around Center, glow included.</summary>
    public double Radius;
    public LightPoint[] Points = Array.Empty<LightPoint>();
    /// <summary>Written on the main thread only; readers use the first Count entries.</summary>
    public int Count;
    /// <summary>Points ascend by Level once the set is complete, so thinning can stop at the first one left out.</summary>
    public bool Sorted { get; private set; }

    /// <summary>Main thread, after the last Add.</summary>
    public void Finish()
    {
        Array.Sort(Points, 0, Count, LevelOrder.Instance);
        Sorted = true;
    }

    private sealed class LevelOrder : IComparer<LightPoint>
    {
        public static readonly LevelOrder Instance = new();
        public int Compare(LightPoint a, LightPoint b) => a.Level.CompareTo(b.Level);
    }

    public void Add(double3 positionCcf, float radius, uint color, uint seed)
    {
        if (Count == Points.Length)
        {
            var grown = new LightPoint[Math.Max(64, Points.Length * 2)];
            Array.Copy(Points, grown, Count);
            Points = grown;
        }
        double3 offset = positionCcf - Center;
        Radius = Math.Max(Radius, Math.Sqrt(double3.Dot(offset, offset)) + radius);
        Points[Count] = new LightPoint
        {
            Offset = new float3((float)offset.X, (float)offset.Y, (float)offset.Z),
            Radius = radius,
            Color = color,
            Level = (Hash(seed) >> 8) / 16777216f,
        };
        Count++;
    }

    public static uint Hash(uint h)
    {
        h ^= h >> 16;
        h *= 0x7feb352du;
        h ^= h >> 15;
        h *= 0x846ca68bu;
        h ^= h >> 16;
        return h;
    }

    public static uint Hash(long a, long b = 0, long c = 0)
        => Hash((uint)a ^ Hash((uint)(a >> 32) ^ Hash((uint)b ^ Hash((uint)c + 0x9e3779b9u))));

    /// <summary>Packs a linear colour (0-255 per channel) and an intensity (0-15.9) into LightPoint.Color.</summary>
    public static uint Pack((int R, int G, int B) rgb, double intensity)
    {
        uint i = (uint)Math.Clamp(Math.Round(intensity * 16), 1, 255);
        return (uint)rgb.R | (uint)rgb.G << 8 | (uint)rgb.B << 16 | i << 24;
    }

    // Light colours, roughly: high pressure sodium, warm interiors, neutral and cool LEDs.
    public static readonly (int, int, int) Sodium = (255, 140, 50);
    public static readonly (int, int, int) Warm = (255, 185, 110);
    public static readonly (int, int, int) Neutral = (240, 225, 200);
    public static readonly (int, int, int) CoolLed = (205, 215, 255);
    // Airfield lighting.
    public static readonly (int, int, int) RunwayWhite = (255, 245, 225);
    public static readonly (int, int, int) TaxiwayBlue = (60, 110, 255);
    public static readonly (int, int, int) ThresholdGreen = (60, 255, 110);

    /// <summary>Window light: mostly warm, some neutral, few cool.</summary>
    public static (int, int, int) WindowColor(uint hash) => (hash % 10) switch
    {
        < 6 => Warm,
        < 9 => Neutral,
        _ => CoolLed,
    };

    /// <summary>Street lighting: sodium with a growing share of LEDs.</summary>
    public static (int, int, int) StreetColor(uint hash) => (hash % 10) switch
    {
        < 6 => Sodium,
        < 8 => Warm,
        _ => Neutral,
    };
}
