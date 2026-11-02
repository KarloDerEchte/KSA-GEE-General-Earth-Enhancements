using System.Net;

namespace GEE.Net;

/// <summary>Blocking HTTP for the tile worker threads. Loaded by reflection, see NetLoader.</summary>
public static class Http
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        // Detailed, far field and city light tiles all come from one server: up to 14 workers at once.
        MaxConnectionsPerServer = 16,
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

    /// <summary>GETs length bytes from offset (HTTP range request); throws "HTTP code: ..." on failure.</summary>
    public static byte[] GetRange(string url, string userAgent, long offset, long length)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
        using var response = Client.Send(request);
        using var stream = response.Content.ReadAsStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} for {url}");
        return memory.ToArray();
    }
}
