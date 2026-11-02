using System.Reflection;
using System.Runtime.Loader;

namespace GEE;

/// <summary>
/// HTTP lives in GEE.Net.dll, loaded on first use: this assembly must not reference System.Net
/// types, because StarMap scans it while loading mods (same constraint as RocketSpawnApi).
/// </summary>
internal static class NetLoader
{
    /// <summary>Returns get(url, userAgent) => body.</summary>
    public static Func<string, string, byte[]> CreateGet(string modDir) =>
        Method(modDir, "Get").CreateDelegate<Func<string, string, byte[]>>();

    /// <summary>Returns getRange(url, userAgent, offset, length) => those bytes of the body.</summary>
    public static Func<string, string, long, long, byte[]> CreateGetRange(string modDir) =>
        Method(modDir, "GetRange").CreateDelegate<Func<string, string, long, long, byte[]>>();

    private static Assembly? _net;
    private static readonly object Lock = new();

    private static MethodInfo Method(string modDir, string name)
    {
        lock (Lock)
        {
            if (_net == null)
            {
                var self = typeof(NetLoader).Assembly;
                var alc = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
                _net = alc.LoadFromAssemblyPath(Path.Combine(modDir, "GEE.Net.dll"));
            }
            return _net.GetType("GEE.Net.Http", throwOnError: true)!.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException($"GEE.Net.dll has no {name}: it is older than GEE.dll, rebuild src/net/GEE.Net.csproj");
        }
    }
}
