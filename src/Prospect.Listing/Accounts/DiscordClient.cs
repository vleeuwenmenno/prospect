using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Prospect.Listing.Accounts;

// Only tests can replace the fixed endpoints through DI; there is no deployment URL option.
internal sealed record DiscordEndpoints(Uri Token, Uri User, Uri Authorization)
{
    public DiscordEndpoints() : this(new("https://discord.com/api/oauth2/token"),
        new("https://discord.com/api/users/@me"), new("https://discord.com/api/oauth2/@me"))
    { }
}

internal sealed class DiscordClient : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        UseCookies = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim slots;
    private readonly AccountsOptions options;
    private readonly DiscordEndpoints endpoints;
    public DiscordClient(AccountsOptions options, DiscordEndpoints endpoints)
    {
        this.options = options;
        this.endpoints = endpoints;
        slots = new(options.DiscordConcurrency);
    }

    internal async Task<(string Id, string? Name)> Exchange(string code, string verifier, CancellationToken abort)
    {
        if (!slots.Wait(0)) throw new AccountFailure(503);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(abort);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.DiscordTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    Uri.EscapeDataString(options.ClientId) + ":" + Uri.EscapeDataString(options.ClientSecret))));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = options.Callback,
                ["code_verifier"] = verifier,
            });
            using var token = await Send(request, timeout.Token);
            var access = token.RootElement.GetProperty("access_token").GetString();
            if (access is not { Length: > 0 and <= 4096 }
                || !string.Equals(token.RootElement.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new AccountFailure(502);
            using var authorizationRequest = new HttpRequestMessage(HttpMethod.Get, endpoints.Authorization);
            authorizationRequest.Headers.Authorization = new("Bearer", access);
            using var authorization = await Send(authorizationRequest, timeout.Token);
            if (authorization.RootElement.GetProperty("application").GetProperty("id").GetString() != options.ClientId
                || !authorization.RootElement.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).SequenceEqual(["identify"]))
                throw new AccountFailure(502);
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, endpoints.User);
            userRequest.Headers.Authorization = new("Bearer", access);
            using var user = await Send(userRequest, timeout.Token);
            var id = user.RootElement.GetProperty("id").GetString();
            if (id is not { Length: > 0 and <= 20 } || !id.All(char.IsAsciiDigit)) throw new AccountFailure(502);
            var name = user.RootElement.TryGetProperty("global_name", out var n) ? n.GetString() : null;
            return (id, name);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new AccountFailure(502);
        }
        finally { slots.Release(); }
    }

    private async Task<JsonDocument> Send(HttpRequestMessage request, CancellationToken cancel)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536
            || response.Content.Headers.ContentEncoding.Count != 0) throw new AccountFailure(502);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        var bytes = new byte[65537];
        var count = 0;
        while (count < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(count), cancel);
            if (n == 0) break;
            count += n;
        }
        if (count > 65536) throw new AccountFailure(502);
        // The serializer rejects duplicate properties before the document is consumed.
        var element = JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(0, count), AccountCrypto.Json);
        return JsonDocument.Parse(element.GetRawText());
    }

    public void Dispose() { client.Dispose(); slots.Dispose(); }
}
