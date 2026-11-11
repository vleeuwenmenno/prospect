using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Prospect.Listing.Tests;

/// <summary>A clock the tests move by hand, so expiry is tested without waiting.</summary>
public sealed class ManualClock : TimeProvider
{
    private long ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}

/// <summary>The real listing service, running in the test process on a loopback port, with an HttpClient pointed at it.</summary>
public sealed class ListingHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private ListingHost(WebApplication app, HttpClient client, ManualClock clock)
    {
        this.app = app;
        Client = client;
        Clock = clock;
    }

    /// <summary>A client for the service.</summary>
    public HttpClient Client { get; }

    /// <summary>The service's clock.</summary>
    public ManualClock Clock { get; }

    /// <summary>The service's store, to check its invariants directly.</summary>
    public ListingStore Store => app.Services.GetRequiredService<ListingStore>();

    /// <summary>The cancellation token of the running test.</summary>
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Starts the service with test defaults (private addresses allowed, since the tests connect over loopback) and
    /// <paramref name="settings"/> on top, keyed without the <c>Listing:</c> prefix.
    /// </summary>
    public static async Task<ListingHost> StartAsync(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Listing:AllowPrivateAddresses"] = "true",
            ["Listing:SweepIntervalMilliseconds"] = "60000",
        };
        foreach (var (key, value) in settings)
        {
            values["Listing:" + key] = value;
        }

        var clock = new ManualClock();
        var app = ListingApp.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(values);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync(Cancel);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
        return new ListingHost(app, client, clock);
    }

    /// <summary>An announcement body with sensible values; callers override fields by passing them.</summary>
    public static Dictionary<string, object?> Body(
        string name = "Ceres Station", int port = 27420, uint protocol = 10, string? token = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["port"] = port,
            ["players"] = 3,
            ["maxPlayers"] = 16,
            ["protocol"] = protocol,
            ["version"] = "0.1.0",
            ["password"] = false,
        };
        if (token is not null)
        {
            body["token"] = token;
        }

        return body;
    }

    /// <summary>Posts an announcement, optionally as if forwarded by a proxy for <paramref name="forwardedFor"/>.</summary>
    public Task<HttpResponseMessage> AnnounceAsync(object body, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/servers") { Content = JsonContent.Create(body) };
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        return Client.SendAsync(request, Cancel);
    }

    /// <summary>Posts a raw body with the given content type.</summary>
    public Task<HttpResponseMessage> PostRawAsync(string body, string contentType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return Client.PostAsync("/v1/servers", content, Cancel);
    }

    /// <summary>Announces and returns the issued id and token; fails the test unless the entry was created.</summary>
    public async Task<AnnounceReply> ListAsync(object body, string? forwardedFor = null)
    {
        using var response = await AnnounceAsync(body, forwardedFor);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AnnounceReply>(Cancel))!;
    }

    /// <summary>Removes an entry with a token.</summary>
    public Task<HttpResponseMessage> RemoveAsync(string id, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/servers/" + Uri.EscapeDataString(id));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return Client.SendAsync(request, Cancel);
    }

    /// <summary>The list for one protocol, as raw JSON and parsed.</summary>
    public async Task<(string Json, ListReply Reply)> GetListAsync(uint protocol = 10)
    {
        using var response = await Client.GetAsync($"/v1/servers?protocol={protocol}", Cancel);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(Cancel);
        return (json, System.Text.Json.JsonSerializer.Deserialize<ListReply>(json, Web)!);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
    }
}

/// <summary>The answer to an announcement.</summary>
public sealed record AnnounceReply(string Id, string Token, int TtlSeconds);

/// <summary>One listed server as clients see it.</summary>
public sealed record ServerReply(
    string Name, string Address, int Port, int Players, int MaxPlayers, bool Password, string Version);

/// <summary>The answer to a list request.</summary>
public sealed record ListReply(List<ServerReply> Servers);
