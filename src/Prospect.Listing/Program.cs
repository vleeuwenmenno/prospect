namespace Prospect.Listing;

/// <summary>The listing server: game servers advertise themselves to it and clients ask it for the public games.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // The runtime image has no curl or wget, so the container's health check runs the service's own binary.
        if (args is ["--health-check", ..])
        {
            return await HealthCheck();
        }

        var app = ListingApp.Build(args);
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> HealthCheck()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var port = string.IsNullOrWhiteSpace(ports) ? "8080" : ports.Split([',', ';'])[0].Trim();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
