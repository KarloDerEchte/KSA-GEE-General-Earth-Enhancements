using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace GEE.Tests;

/// <summary>
/// SatelliteImagery.Pack / Twist are private static members of a class that also holds Vulkan state, so the file cannot be compiled
/// into the test project. They are called by reflection on a built GEE.dll instead (run-tests.ps1 builds src to the scratchpad and
/// passes it in GEE_TEST_DLL; without it the installed mod's GEE.dll is used). Neither method touches a game type.
/// </summary>
public class SatellitePackTests
{
    private delegate void PackFn(ReadOnlySpan<float> values, Span<uint> words, out float low, out float span);
    private delegate float TwistFn(ReadOnlySpan<float> values);

    private const int HeightPoints = 9;
    private static readonly Lazy<(PackFn Pack, TwistFn Twist)> Methods = new(() => Load());

    private static (PackFn, TwistFn) Load([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        string? path = Environment.GetEnvironmentVariable("GEE_TEST_DLL");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "GEE.dll"));   // the installed mod
        Assert.True(File.Exists(path), "no GEE.dll to test the satellite packing against (run tests/run-tests.ps1)");

        string ksa = Environment.GetEnvironmentVariable("KSA_DIR") ?? @"C:\Program Files\Ahwoo\Games\Kitten Space Agency";
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string dir in new[] { ksa, Path.Combine(Path.GetDirectoryName(path)!, "..", "StarMap") })
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var type = asm.GetType("GEE.SatelliteImagery", throwOnError: true)!;
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var pack = (PackFn)Delegate.CreateDelegate(typeof(PackFn), type.GetMethod("Pack", flags)!);
        var twist = (TwistFn)Delegate.CreateDelegate(typeof(TwistFn), type.GetMethod("Twist", flags)!);
        return (pack, twist);
    }

    private static uint[] Words(float[] values, out float low, out float span)
    {
        var words = new uint[28];   // seven uint4 attributes
        Methods.Value.Pack(values, words, out low, out span);
        return words;
    }

    /// <summary>What the vertex shader does (SatelliteTechnique gridHeight): bits [10 * (i % 3), +10) of word i / 3.</summary>
    private static float Unpack(uint[] words, int i, float low, float span) =>
        low + (float)((words[i / 3] >> (10 * (i % 3))) & 1023u) / 1023f * span;

    [Fact]
    public void RoundTrip_ErrorIsAtMostHalfAQuantisationStep()
    {
        var rng = new Random(99);
        for (int round = 0; round < 50; round++)
        {
            double scale = Math.Pow(10, rng.Next(0, 4));   // 1 m to 1000 m of relief
            var v = new float[HeightPoints * HeightPoints];
            for (int i = 0; i < v.Length; i++) v[i] = (float)(rng.NextDouble() * scale - 120);
            var words = Words(v, out float low, out float span);
            Assert.Equal(v.Min(), low);
            Assert.Equal(v.Max() - v.Min(), span, 3);
            float step = span / 1023f;
            for (int i = 0; i < v.Length; i++)
                Assert.True(Math.Abs(Unpack(words, i, low, span) - v[i]) <= step / 2 + 1e-3f, $"point {i} off by {Unpack(words, i, low, span) - v[i]}");
        }
    }

    [Fact]
    public void ExtremesMapToZeroAnd1023_AndTopTwoBitsOfEveryWordStayClear()
    {
        var v = new float[81];
        for (int i = 0; i < 81; i++) v[i] = i % 2 == 0 ? -5f : 2000f;
        var words = Words(v, out float low, out float span);
        Assert.Equal(-5f, low); Assert.Equal(2005f, span);
        Assert.Equal(0u, words[0] & 1023u);
        Assert.Equal(1023u, (words[0] >> 10) & 1023u);
        Assert.All(words, w => Assert.Equal(0u, w >> 30));
        Assert.Equal(0u, words[27]);                                            // 81 values need 27 words; the 28th (last uint4 slot) is unused
    }

    [Fact]
    public void FlatGrid_PacksToAllZero_WithZeroSpan()
    {
        var words = Words(Enumerable.Repeat(123.5f, 81).ToArray(), out float low, out float span);
        Assert.Equal(123.5f, low); Assert.Equal(0f, span);
        Assert.All(words, w => Assert.Equal(0u, w));
    }

    [Fact]
    public void Pack_OverwritesStaleWords()
    {
        var words = Enumerable.Repeat(uint.MaxValue, 28).ToArray();
        Methods.Value.Pack(new float[81], words, out _, out _);
        Assert.All(words, w => Assert.Equal(0u, w));
    }

    [Fact]
    public void Pack_Monotone_RampKeepsItsOrder()
    {
        var v = Enumerable.Range(0, 81).Select(i => i * 12.5f).ToArray();
        var words = Words(v, out float low, out float span);
        float previous = float.MinValue;
        for (int i = 0; i < 81; i++)
        {
            float u = Unpack(words, i, low, span);
            Assert.True(u >= previous);
            previous = u;
        }
    }

    [Fact]
    public void Twist_IsZeroOnAPlane_AndAQuarterOfTheCellTwistOtherwise()
    {
        var plane = new float[81];
        for (int j = 0; j < 9; j++) for (int i = 0; i < 9; i++) plane[j * 9 + i] = 3 * i + 5 * j;
        Assert.Equal(0f, Methods.Value.Twist(plane), 3);
        var twisted = (float[])plane.Clone();
        twisted[4 * 9 + 4] += 8;   // one corner of four cells lifted by 8 m
        Assert.Equal(2f, Methods.Value.Twist(twisted), 3);
    }
}
