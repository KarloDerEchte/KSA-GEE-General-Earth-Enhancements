using System.Net;

namespace OsmBuildings.Net;

/// <summary>Blocking HTTP for the tile worker threads. Loaded by reflection, see NetLoader.</summary>
public static class Http
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // Overpass queries for street lights can take a minute or two on the busy public servers.
        Timeout = TimeSpan.FromSeconds(200),
    };

    /// <summary>GETs a URL and returns the body; throws "HTTP code: ..." on failure.</summary>
    public static byte[] Get(string url, string userAgent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = Client.Send(request);
        using var stream = response.Content.ReadAsStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} for {url}");
        return memory.ToArray();
    }
}
