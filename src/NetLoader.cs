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
    public static Func<string, string, byte[]> CreateGet(string modDir)
    {
        var self = typeof(NetLoader).Assembly;
        var alc = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
        var net = alc.LoadFromAssemblyPath(Path.Combine(modDir, "GEE.Net.dll"));
        var method = net.GetType("GEE.Net.Http", throwOnError: true)!
            .GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!;
        return method.CreateDelegate<Func<string, string, byte[]>>();
    }
}
