using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prospect.Listing.Accounts;
using Xunit;

namespace Prospect.Listing.Tests;

internal sealed class AccountHost : IAsyncDisposable
{
    private WebApplication app = null!, provider = null!;
    internal HttpClient Client = null!;
    internal readonly ManualClock Clock = new();
    internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "prospect-accounts-" + Guid.NewGuid());
    internal string Database => Path.Combine(DirectoryPath, "accounts.db");
    internal string ProviderMode = "", UserId = "123456789", DisplayName = "Pilot\u202e One";
    internal int Exchanges;
    internal readonly TaskCompletionSource ProviderEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource ReleaseProvider = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? ExpectedProviderChallenge;
    internal readonly List<string> Logs = [];
    internal AccountStore Store => app.Services.GetRequiredService<AccountStore>();
    internal GameTickets Tickets => app.Services.GetRequiredService<GameTickets>();

    internal static async Task<AccountHost> Start(params (string, string)[] settings)
    {
        var h = new AccountHost();
        Directory.CreateDirectory(h.DirectoryPath);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        h.provider = builder.Build();
        h.provider.MapPost("/token", async (HttpContext c) =>
        {
            Interlocked.Increment(ref h.Exchanges);
            if (h.ProviderMode == "stall")
            {
                h.ProviderEntered.TrySetResult();
                await h.ReleaseProvider.Task.WaitAsync(c.RequestAborted);
            }
            var form = await c.Request.ReadFormAsync();
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client:secret")), c.Request.Headers.Authorization.ToString());
            Assert.Equal("https://prospect.mvl.sh/v1/accounts/discord/callback", form["redirect_uri"].ToString());
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            Assert.Equal(h.ExpectedProviderChallenge, AccountCrypto.Challenge(form["code_verifier"].ToString()));
            return h.ProviderMode switch
            {
                "redirect" => Results.Redirect("/user"),
                "oversize" => Results.Text(new string('x', 65537)),
                "duplicate" => Results.Text("""{"access_token":"a","access_token":"b","token_type":"Bearer"}""", "application/json"),
                "malformed" => Results.Text("{", "application/json"),
                "invalid-token" => Results.Json(new { access_token = "invalid\r\nheader", token_type = "Bearer" }),
                _ => Results.Json(new { access_token = "provider-secret", token_type = "Bearer" }),
            };
        });
        h.provider.MapGet("/authorization", (HttpContext c) =>
        {
            Assert.Equal("Bearer provider-secret", c.Request.Headers.Authorization.ToString());
            return Results.Json(new { application = new { id = h.ProviderMode == "wrong-client" ? "wrong" : "client" }, scopes = new[] { "identify" } });
        });
        h.provider.MapGet("/user", (HttpContext c) =>
        {
            Assert.Equal("Bearer provider-secret", c.Request.Headers.Authorization.ToString());
            return Results.Json(new { id = h.UserId, global_name = h.DisplayName, username = "never-public-handle" });
        });
        await h.provider.StartAsync(ListingHost.Cancel);
        await h.Restart(settings);
        return h;
    }
    internal async Task Restart(params (string, string)[] settings)
    {
        if (app is not null) { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
        var values = new Dictionary<string, string?>
        {
            ["Accounts:Enabled"] = "true",
            ["Accounts:ClientId"] = "client",
            ["Accounts:ClientSecret"] = "secret",
            ["Accounts:DatabasePath"] = Database,
            ["Accounts:AttemptsPerAddress"] = "1000",
            ["Listing:AuthPerMinute"] = "100000",
            ["Listing:AccountPerMinute"] = "100000",
            ["Listing:TicketIssuesPerMinute"] = "100000",
            ["Listing:TicketRedeemsPerMinute"] = "100000",
            ["Listing:GlobalRequestsPerSecond"] = "100000",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in settings) values[key] = value;
        var uri = new Uri(Address(provider));
        app = ListingApp.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(values);
            b.Services.AddSingleton<TimeProvider>(Clock);
            b.Services.AddSingleton(new DiscordEndpoints(new(uri, "/token"), new(uri, "/user"), new(uri, "/authorization")));
            b.Logging.ClearProviders();
            b.Logging.AddProvider(new CaptureLogger(Logs));
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync(ListingHost.Cancel);
        Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { BaseAddress = new(Address(app)) };
    }
    private static string Address(WebApplication a) => a.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    internal async Task<(string Attempt, string Verifier, string State)> Begin(int port = 12345)
    {
        var verifier = AccountCrypto.Random();
        var body = await Json(await Client.PostAsJsonAsync("/v1/accounts/signin", new { challenge = AccountCrypto.Challenge(verifier), port }), 201);
        var attempt = body.GetProperty("attempt").GetString()!;
        var start = await Client.GetAsync("/v1/accounts/signin/" + attempt);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal("consent", query["prompt"]);
        ExpectedProviderChallenge = query["code_challenge"];
        Assert.NotEqual(AccountCrypto.Challenge(verifier), ExpectedProviderChallenge);
        return (attempt, verifier, query["state"].ToString());
    }
    internal async Task<string> Complete(string state)
    {
        var callback = await Client.GetAsync("/v1/accounts/discord/callback?code=provider-code&state=" + state);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("127.0.0.1", callback.Headers.Location!.Host);
        Assert.Equal(12345, callback.Headers.Location.Port);
        Assert.Equal("/prospect/signin", callback.Headers.Location.AbsolutePath);
        Assert.Equal("no-store", callback.Headers.CacheControl!.ToString());
        return QueryHelpers.ParseQuery(callback.Headers.Location.Query)["code"].ToString();
    }
    internal async Task<SessionReply> Login(string discord = "123456789")
    {
        UserId = discord;
        var a = await Begin();
        var code = await Complete(a.State);
        var response = await Redeem(a.Attempt, code, a.Verifier);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionReply>())!;
    }
    internal Task<HttpResponseMessage> Redeem(string attempt, string code, string verifier) =>
        Client.PostAsJsonAsync("/v1/accounts/sessions", new { attempt, code, verifier });
    internal Task<HttpResponseMessage> Send(string method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(new(method), path);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }
    internal static async Task<JsonElement> Json(HttpResponseMessage response, int status = 200)
    {
        Assert.Equal(status, (int)response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync(); await app.DisposeAsync();
        await provider.StopAsync(); await provider.DisposeAsync();
        Directory.Delete(DirectoryPath, true);
    }
    internal sealed record SessionReply(string Token, int ExpiresIn, Account Account);
    private sealed class CaptureLogger(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);
        public void Dispose() { }
        private sealed class Logger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { lock (lines) lines.Add(formatter(state, exception)); }
        }
    }
}
