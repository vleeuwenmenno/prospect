using System.Net;
using System.Net.Sockets;
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
using Prospect.Game.Lobby;
using Prospect.Listing.Accounts;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The real account service (src/Prospect.Listing, accounts enabled, its own SQLite file) with a fake Discord in
/// front of it, both in the test process. The fake provider checks the client credentials, the redirect address
/// and the service's PKCE verifier, and names the configured application, as Discord does.
/// </summary>
internal sealed class AccountService : IAsyncDisposable
{
    private WebApplication _app = null!;
    private WebApplication _provider = null!;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prospect-account-client-" + Guid.NewGuid());
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Challenge, string User)> _codes = [];

    public string Url { get; private set; } = "";
    public ListingConfig Config => ListingConfig.TryCreate(Url, out var config, out _) ? config! : throw new InvalidOperationException();

    /// <summary>The Discord user the next consent signs in as.</summary>
    public string User = "1001";

    public static async Task<AccountService> StartAsync()
    {
        var service = new AccountService();
        Directory.CreateDirectory(service._directory);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        service._provider = builder.Build();
        service._provider.MapPost("/token", async (HttpContext c) =>
        {
            var form = await c.Request.ReadFormAsync();
            if (c.Request.Headers.Authorization.ToString() != "Basic " + Convert.ToBase64String("client:secret"u8)
                || form["redirect_uri"] != "https://prospect.mvl.sh/v1/accounts/discord/callback"
                || form["grant_type"] != "authorization_code")
                return Results.StatusCode(401);
            (string Challenge, string User) issued;
            lock (service._gate)
            {
                if (!service._codes.Remove(form["code"].ToString(), out issued))
                    return Results.StatusCode(400);
            }

            // Discord checks the service's own PKCE verifier against the challenge from the authorize request.
            if (AccountClient.Challenge(form["code_verifier"].ToString()) != issued.Challenge)
                return Results.StatusCode(400);
            return Results.Json(new { access_token = "access-" + issued.User, token_type = "Bearer" });
        });
        service._provider.MapGet("/authorization", () => Results.Json(new { application = new { id = "client" }, scopes = new[] { "identify" } }));
        service._provider.MapGet("/user", (HttpContext c) =>
        {
            var user = c.Request.Headers.Authorization.ToString()["Bearer access-".Length..];
            return Results.Json(new { id = user, global_name = "Pilot " + user, username = "handle-" + user });
        });
        await service._provider.StartAsync();
        await service.RestartAsync();
        return service;
    }

    /// <summary>Stops the service and starts it again on the same database; in-flight sign-ins are lost.</summary>
    public async Task RestartAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        var provider = new Uri(Address(_provider));
        var values = new Dictionary<string, string?>
        {
            ["Accounts:Enabled"] = "true",
            ["Accounts:ClientId"] = "client",
            ["Accounts:ClientSecret"] = "secret",
            ["Accounts:DatabasePath"] = Path.Combine(_directory, "accounts.db"),
            ["Accounts:AttemptsPerAddress"] = "1000",
            ["Listing:AuthPerMinute"] = "100000",
            ["Listing:AccountPerMinute"] = "100000",
            ["Listing:GlobalRequestsPerSecond"] = "100000",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        _app = Prospect.Listing.ListingApp.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(values);
            b.Services.AddSingleton(new DiscordEndpoints(new(provider, "/token"), new(provider, "/user"), new(provider, "/authorization")));
            b.Logging.ClearProviders();
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await _app.StartAsync();
        Url = Address(_app);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    /// <summary>Discord's consent page: checks the request, issues a one-use code bound to its PKCE challenge.</summary>
    public string Consent(Uri authorize)
    {
        Assert.Equal("https://discord.com/oauth2/authorize", authorize.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal("client", query["client_id"]);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://prospect.mvl.sh/v1/accounts/discord/callback", query["redirect_uri"]);
        var code = "provider-" + Guid.NewGuid().ToString("N");
        lock (_gate)
            _codes[code] = (query["code_challenge"].ToString(), User);
        return code;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _provider.StopAsync();
        await _provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}

/// <summary>
/// The player's browser, by hand: given the page the game opens, it follows the service's redirect to the fake
/// Discord, consents, comes back through the service's callback and is redirected to the game's local port.
/// Each step can be stopped short so a test can act in between.
/// </summary>
internal sealed class FakeBrowser : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
    private readonly TaskCompletionSource<Uri> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Opens;

    /// <summary>What the game is given as its browser opener.</summary>
    public bool Open(Uri page)
    {
        Interlocked.Increment(ref Opens);
        _opened.TrySetResult(page);
        return true;
    }

    public Task<Uri> Opened => _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Opens the page, consents (or declines) and returns where the service sends the browser back to.</summary>
    public async Task<Uri> ConsentAsync(AccountService service, Uri page, bool decline = false)
    {
        var start = await _http.GetAsync(page);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        var state = QueryHelpers.ParseQuery(authorize.Query)["state"].ToString();
        var code = service.Consent(authorize);
        var back = decline ? $"error=access_denied&state={state}" : $"code={code}&state={state}";
        var callback = await _http.GetAsync(new Uri(new Uri(service.Url), "/v1/accounts/discord/callback?" + back));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var local = callback.Headers.Location!;
        Assert.Equal("127.0.0.1", local.Host);
        Assert.Equal("/prospect/signin", local.AbsolutePath);
        return local;
    }

    /// <summary>Follows the redirect to the game's port and returns the page's status and body.</summary>
    public async Task<(HttpStatusCode Status, HttpResponseMessage Response, string Body)> ReturnAsync(Uri local)
    {
        var response = await _http.GetAsync(local);
        return (response.StatusCode, response, await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The game's account client against the real account service, signing in through the real callback flow, and
/// against misbehaving services. Nothing sets a signed-in state by hand.
/// </summary>
[Collection(nameof(AccountClientTests))]
public sealed class AccountClientTests
{
    private static async Task Until(AccountClient client, Func<AccountView, bool> done, string what)
    {
        for (var i = 0; i < 1000; i++)
        {
            var view = client.View;
            if (done(view))
                return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}; state {client.View.State}, notice {client.View.Notice}, busy {client.View.Busy}.");
    }

    private static Task Idle(AccountClient client) => Until(client, v => !v.Busy, "the operation to end");

    private static async Task<AccountClient> SignInAsync(AccountService service, string user)
    {
        using var browser = new FakeBrowser();
        var client = new AccountClient(service.Config, browser.Open);
        service.User = user;
        Assert.True(client.SignIn());
        var page = await browser.Opened;
        var local = await browser.ConsentAsync(service, page);
        var (status, _, _) = await browser.ReturnAsync(local);
        Assert.Equal(HttpStatusCode.OK, status);
        await Until(client, v => v.State == AccountState.SignedIn && !v.Busy, "sign-in");
        Assert.True(client.View.Listed);
        return client;
    }

    private static async Task<bool> PortOpen(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>True once nothing listens on the port any more; the owner closes it as its operation unwinds.</summary>
    private static async Task<bool> Closed(int port)
    {
        for (var i = 0; i < 200; i++)
        {
            if (!await PortOpen(port))
                return true;
            await Task.Delay(10);
        }

        return false;
    }

    /// <summary>One raw request to the game's port, exactly as written, and the status line that comes back.</summary>
    private static async Task<string> RawAsync(int port, string request)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port);
        // Latin-1, so a character outside ASCII arrives as the single byte a hostile client would send.
        await socket.SendAsync(Encoding.Latin1.GetBytes(request));
        var buffer = new byte[8192];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(length));
                if (read == 0)
                    break;
                length += read;
            }
        }
        catch (SocketException)
        {
            // The receiver closes on a request it did not finish reading; the reset may follow its answer.
        }

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        return text.Length == 0 ? "" : text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task SignInThroughTheBrowserAndAServiceRestartKeepTheSameAccountAndFriends()
    {
        await using var service = await AccountService.StartAsync();
        using var browser = new FakeBrowser();
        var a = new AccountClient(service.Config, browser.Open);
        Assert.Equal(AccountState.SignedOut, a.View.State);
        service.User = "1001";
        Assert.True(a.SignIn());
        Assert.False(a.SignIn());

        // The page opened is built from the configured service and the attempt, nothing else.
        var page = await browser.Opened;
        Assert.StartsWith(service.Url + "/v1/accounts/signin/", page.AbsoluteUri);
        Assert.True(AccountClient.IsCanonicalSecret(page.Segments[^1]));
        await Until(a, v => v.State == AccountState.Waiting, "waiting");
        Assert.Equal(page.AbsoluteUri, a.View.BrowserUrl);

        var local = await browser.ConsentAsync(service, page);
        var (status, response, body) = await browser.ReturnAsync(local);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("default-src 'none'", string.Join(';', response.Headers.GetValues("Content-Security-Policy")));
        var code = QueryHelpers.ParseQuery(local.Query)["code"].ToString();
        Assert.DoesNotContain(code, body);
        Assert.DoesNotContain("http", body.Replace("<!doctype html>", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        await Until(a, v => v.State == AccountState.SignedIn && v.Listed && !v.Busy, "sign-in");
        Assert.Equal("Pilot 1001", a.View.Name);
        Assert.True(AccountClient.IsAccountId(a.View.Id));
        Assert.True(AccountClient.IsFriendCode(a.View.FriendCode));
        Assert.Null(a.View.BrowserUrl);
        // The port closed with the one answer: the same redirect again reaches nothing.
        Assert.True(await Closed(local.Port));

        var b = await SignInAsync(service, "2002");
        Assert.NotEqual(a.View.Id, b.View.Id);

        // A request by friend code, typed in lower case; the other side sees it after a refresh and accepts.
        Assert.True(a.AddFriend(b.View.FriendCode.ToLowerInvariant()));
        await Idle(a);
        Assert.Equal(AccountNotice.RequestSent, a.View.Notice);
        Assert.Equal(b.View.Id, Assert.Single(a.View.Outgoing).Id);
        Assert.True(b.Refresh());
        await Idle(b);
        Assert.Equal(a.View.Id, Assert.Single(b.View.Incoming).Id);
        Assert.True(b.Accept(a.View.Id));
        await Idle(b);
        Assert.Equal(AccountNotice.Accepted, b.View.Notice);
        Assert.Equal("Pilot 1001", Assert.Single(b.View.Friends).Name);
        Assert.Empty(b.View.Incoming);

        // Both games close and the service restarts. Signing in again with the same Discord account restores the
        // same account and its friend.
        var id = a.View.Id;
        a.Dispose();
        b.Dispose();
        await service.RestartAsync();
        using var again = await SignInAsync(service, "1001");
        Assert.Equal(id, again.View.Id);
        var friend = Assert.Single(again.View.Friends);
        Assert.Equal(b.View.Id, friend.Id);
        Assert.Equal("Pilot 2002", friend.Name);
    }

    [Fact]
    public async Task RequestsAreDeclinedCancelledRemovedAndRefusalsAreNamed()
    {
        await using var service = await AccountService.StartAsync();
        using var a = await SignInAsync(service, "1");
        using var b = await SignInAsync(service, "2");

        async Task Do(AccountClient client, Func<AccountClient, bool> action, AccountNotice expected)
        {
            Assert.True(action(client));
            await Idle(client);
            Assert.Equal(expected, client.View.Notice);
        }

        // Refused here, before anything is sent.
        Assert.False(a.AddFriend("not a code"));
        Assert.Equal(AccountNotice.InvalidCode, a.View.Notice);
        Assert.False(a.AddFriend("0123456789ABCDEFG"));
        Assert.False(a.Accept("../../v1/accounts/me"));
        await Do(a, c => c.AddFriend("0000000000000000"), AccountNotice.UnknownCode);
        await Do(a, c => c.AddFriend(c.View.FriendCode), AccountNotice.CannotAdd);

        // Declined.
        await Do(a, c => c.AddFriend(b.View.FriendCode), AccountNotice.RequestSent);
        await Do(a, c => c.AddFriend(b.View.FriendCode), AccountNotice.RequestSettled);
        await Do(b, c => c.Refresh(), AccountNotice.Refreshed);
        await Do(b, c => c.Decline(a.View.Id), AccountNotice.Declined);
        Assert.Empty(b.View.Incoming);
        await Do(a, c => c.Refresh(), AccountNotice.Refreshed);
        Assert.Empty(a.View.Outgoing);

        // Withdrawn by the sender.
        await Do(a, c => c.AddFriend(b.View.FriendCode), AccountNotice.RequestSent);
        await Do(a, c => c.CancelRequest(b.View.Id), AccountNotice.RequestCancelled);
        Assert.Empty(a.View.Outgoing);
        await Do(b, c => c.Refresh(), AccountNotice.Refreshed);
        Assert.Empty(b.View.Incoming);

        // Crossed requests: the second one accepts the first.
        await Do(a, c => c.AddFriend(b.View.FriendCode), AccountNotice.RequestSent);
        await Do(b, c => c.AddFriend(a.View.FriendCode), AccountNotice.RequestSettled);
        Assert.Single(b.View.Friends);
        await Do(a, c => c.AddFriend(b.View.FriendCode), AccountNotice.CannotAdd);

        // Removed, then a stale accept is gone rather than bringing the friendship back.
        await Do(a, c => c.Remove(b.View.Id), AccountNotice.Removed);
        Assert.Empty(a.View.Friends);
        await Do(b, c => c.Accept(a.View.Id), AccountNotice.Gone);
        Assert.Empty(b.View.Friends);
    }

    [Fact]
    public async Task StrayAndHostileRequestsToTheLocalPortNeitherEndNorUseTheSignIn()
    {
        await using var service = await AccountService.StartAsync();
        using var browser = new FakeBrowser();
        using var client = new AccountClient(service.Config, browser.Open);
        Assert.True(client.SignIn());
        var local = await browser.ConsentAsync(service, await browser.Opened);
        var port = local.Port;
        var query = QueryHelpers.ParseQuery(local.Query);
        var attempt = query["attempt"].ToString();
        var code = query["code"].ToString();
        var host = $"Host: 127.0.0.1:{port}\r\n";
        string Get(string target, string headers) => $"GET {target} HTTP/1.1\r\n{headers}Connection: close\r\n\r\n";

        var other = AccountClient.Encode(new byte[32]);
        var hostile = new[]
        {
            Get($"/prospect/signin?attempt={other}&code={code}", host),
            Get($"/prospect/signin?attempt={attempt}&attempt={attempt}&code={code}", host),
            Get($"/prospect/signin?attempt={attempt}&code={code}&code={code}", host),
            Get($"/prospect/signin?attempt={attempt}&code={code}&error=failed", host),
            Get($"/prospect/signin?attempt={attempt}&code={code}&next=x", host),
            Get($"/prospect/signin?attempt={attempt}&error=%3Cscript%3E", host),
            Get($"/prospect/signin?attempt={attempt}&error=evil", host),
            Get($"/prospect/signin?attempt={attempt}&code={code[..42]}", host),
            Get($"/prospect/signin?attempt={attempt}", host),
            Get($"/prospect/signin/?attempt={attempt}&code={code}", host),
            Get($"/PROSPECT/signin?attempt={attempt}&code={code}", host),
            Get($"http://127.0.0.1:{port}/prospect/signin?attempt={attempt}&code={code}", host),
            Get($"/prospect/signin?attempt={attempt}&code={code}", ""),
            Get($"/prospect/signin?attempt={attempt}&code={code}", $"Host: localhost:{port}\r\n"),
            Get($"/prospect/signin?attempt={attempt}&code={code}", host + host),
            Get($"/prospect/signin?attempt={attempt}&code={code}", $"Host: 127.0.0.1:{port}\r\nX-Bad: é\r\n"),
            $"POST /prospect/signin?attempt={attempt}&code={code} HTTP/1.1\r\n{host}Content-Length: 0\r\n\r\n",
            $"GET /prospect/signin?attempt={attempt}&code={code} HTTP/1.0\r\n{host}\r\n",
            $"get /prospect/signin?attempt={attempt}&code={code} HTTP/1.1\r\n{host}\r\n",
        };
        foreach (var request in hostile)
            Assert.StartsWith("HTTP/1.1 400", await RawAsync(port, request));

        // A head too long for the receiver is cut off without an answer that counts.
        Assert.DoesNotContain("200", await RawAsync(port, $"GET /prospect/signin?attempt={attempt}&code={code} HTTP/1.1\r\n{host}X-Pad: {new string('a', 5000)}\r\n\r\n"));
        // A connection that sends nothing does not hold the sign-in up.
        using (var idle = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await idle.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            Assert.Equal(AccountState.Waiting, client.View.State);

            // None of it ended or used up the sign-in: the real redirect still completes it.
            var (status, _, _) = await browser.ReturnAsync(local);
            Assert.Equal(HttpStatusCode.OK, status);
        }

        await Until(client, v => v.State == AccountState.SignedIn && !v.Busy, "sign-in");
        Assert.True(await Closed(port));
    }

    [Fact]
    public async Task CancelDenialTimeoutAndDisposalCloseThePortAndSignNobodyIn()
    {
        await using var service = await AccountService.StartAsync();

        // Cancel while the browser is at Discord.
        using (var browser = new FakeBrowser())
        using (var client = new AccountClient(service.Config, browser.Open))
        {
            Assert.True(client.SignIn());
            var local = await browser.ConsentAsync(service, await browser.Opened);
            Assert.True(await PortOpen(local.Port));
            client.CancelSignIn();
            Assert.Equal(AccountState.SignedOut, client.View.State);
            Assert.Equal(AccountNotice.SigninCancelled, client.View.Notice);
            await Until(client, v => !v.Busy, "the cancelled sign-in to end");
            Assert.True(await Closed(local.Port));
            Assert.Equal(AccountState.SignedOut, client.View.State);
            // A new sign-in can start straight away.
            Assert.True(client.SignIn());
        }

        // The player declines at Discord: the service sends the fixed error back and the port closes.
        using (var browser = new FakeBrowser())
        using (var client = new AccountClient(service.Config, browser.Open))
        {
            Assert.True(client.SignIn());
            var local = await browser.ConsentAsync(service, await browser.Opened, decline: true);
            Assert.Equal("access_denied", QueryHelpers.ParseQuery(local.Query)["error"]);
            var (status, _, body) = await browser.ReturnAsync(local);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("cancelled", body);
            await Until(client, v => v.State == AccountState.SignedOut && !v.Busy, "the declined sign-in");
            Assert.Equal(AccountNotice.SigninDenied, client.View.Notice);
            Assert.True(await Closed(local.Port));
        }

        // The tab is closed and nothing comes back: the wait ends on its own.
        using (var browser = new FakeBrowser())
        using (var client = new AccountClient(service.Config, browser.Open, TimeSpan.FromSeconds(1.5)))
        {
            Assert.True(client.SignIn());
            var local = await browser.ConsentAsync(service, await browser.Opened);
            await Until(client, v => v.State == AccountState.SignedOut && !v.Busy, "the sign-in to time out");
            Assert.Equal(AccountNotice.SigninTimedOut, client.View.Notice);
            Assert.True(await Closed(local.Port));
        }

        // The game closes mid sign-in.
        using (var browser = new FakeBrowser())
        {
            var client = new AccountClient(service.Config, browser.Open);
            Assert.True(client.SignIn());
            var local = await browser.ConsentAsync(service, await browser.Opened);
            client.Dispose();
            Assert.True(await Closed(local.Port));
            Assert.False(client.SignIn());
            Assert.NotEqual(AccountState.SignedIn, client.View.State);
        }
    }

    [Fact]
    public async Task SigningOutForgetsTheAccountAtOnceAndRevokesTheSession()
    {
        await using var service = await AccountService.StartAsync();
        var a = await SignInAsync(service, "77");
        using var b = await SignInAsync(service, "78");
        Assert.True(a.AddFriend(b.View.FriendCode));
        await Idle(a);

        a.SignOut();
        Assert.Equal(AccountState.SignedOut, a.View.State);
        Assert.Equal(AccountNotice.SignedOut, a.View.Notice);
        Assert.Equal("", a.View.Id);
        Assert.Empty(a.View.Outgoing);
        Assert.False(a.Refresh());
        a.Dispose();

        // The same Discord account signs in again: the request it sent is still there.
        using var again = await SignInAsync(service, "77");
        Assert.Single(again.View.Outgoing);
    }

    // -----------------------------------------------------------------------------------------------------------
    // Against a service that misbehaves
    // -----------------------------------------------------------------------------------------------------------

    private const string Account = """{"id":"0123456789abcdef0123456789abcdef","name":"Pilot","friendCode":"0123456789ABCDEF"}""";
    private static readonly string Token = AccountClient.Encode(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly string Attempt = AccountClient.Encode(Enumerable.Repeat((byte)9, 32).ToArray());

    private static string Entry(string id, string name = "Friend", string since = "2026-11-01T10:00:00+00:00") =>
        $$"""{"id":"{{id}}","name":{{JsonSerializer.Serialize(name)}},"since":"{{since}}"}""";

    private static string Lists(string friends = "", string incoming = "", string outgoing = "") =>
        $$"""{"friends":[{{friends}}],"incoming":[{{incoming}}],"outgoing":[{{outgoing}}]}""";

    /// <summary>
    /// Signs in against a fake service: it answers the attempt and the redemption, the test plays the browser by
    /// calling the port named in the request. Later requests go to <paramref name="after"/>.
    /// </summary>
    private static async Task<(FakeListing Fake, AccountClient Client, FakeBrowser Browser)> FakeSignInAsync(
        Func<FakeRequest, int, FakeAnswer> after,
        Func<string, Task>? responseReceived = null)
    {
        var browser = new FakeBrowser();
        var fake = new FakeListing((request, index) => request.Path switch
        {
            "/v1/accounts/signin" => FakeAnswer.Json(201, $$"""{"attempt":"{{Attempt}}","expiresIn":600}"""),
            "/v1/accounts/sessions" => FakeAnswer.Json(201, $$"""{"token":"{{Token}}","expiresIn":86400,"account":{{Account}}}"""),
            _ => after(request, index),
        });
        var client = new AccountClient(ListingConfig.Parse("{}"u8.ToArray(), "test", fake.Url), browser.Open) { ResponseReceived = responseReceived };
        Assert.True(client.SignIn());
        await browser.Opened;
        var port = JsonDocument.Parse(fake.Requests[0].Body).RootElement.GetProperty("port"u8).GetInt32();
        var code = AccountClient.Encode(Enumerable.Repeat((byte)3, 32).ToArray());
        var (status, _, _) = await browser.ReturnAsync(new Uri($"http://127.0.0.1:{port}/prospect/signin?attempt={Attempt}&code={code}"));
        Assert.Equal(HttpStatusCode.OK, status);
        await Until(client, v => v.State == AccountState.SignedIn && !v.Busy, "sign-in");
        return (fake, client, browser);
    }

    [Fact]
    public async Task ABusyServiceIsRetriedBrieflyAndAnExpiredSessionSignsOut()
    {
        var friends = 0;
        var mode = "busy-twice";
        var (fake, client, browser) = await FakeSignInAsync((request, _) =>
        {
            if (request.Path == "/v1/friends")
            {
                Assert.Equal("Bearer " + Token, request.Authorization);
                var n = Interlocked.Increment(ref friends);
                return mode switch
                {
                    "busy-twice" when n <= 2 => FakeAnswer.Json(503, """{"error":"Accounts unavailable."}"""),
                    "busy" => FakeAnswer.Json(503, """{"error":"Accounts unavailable."}"""),
                    "expired" => FakeAnswer.Json(401, """{"error":"Account request failed."}"""),
                    _ => FakeAnswer.Json(200, Lists(Entry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))),
                };
            }

            return request.Path == "/v1/friends/requests" ? FakeAnswer.Json(429, """{"error":"Account request failed."}""") : FakeAnswer.Json(204, "");
        });
        using (fake)
        using (client)
        using (browser)
        {
            // Two 503s, then the list: three requests, and signed in with the list.
            Assert.Equal(3, friends);
            Assert.Single(client.View.Friends);

            // Busy every time: gives up after three tries with a notice, still signed in, the old list kept.
            mode = "busy";
            friends = 0;
            Assert.True(client.Refresh());
            await Idle(client);
            Assert.Equal(3, friends);
            Assert.Equal(AccountNotice.Unavailable, client.View.Notice);
            Assert.Equal(AccountState.SignedIn, client.View.State);
            Assert.Single(client.View.Friends);

            // A limit is not retried and does not sign out.
            Assert.True(client.AddFriend("0123456789ABCDEF"));
            await Idle(client);
            Assert.Equal(AccountNotice.LimitReached, client.View.Notice);
            Assert.Equal(1, fake.Count("POST") - 2);

            // 401: the session is over. Signed out, nothing of the account left.
            mode = "expired";
            Assert.True(client.Refresh());
            await Idle(client);
            Assert.Equal(AccountState.SignedOut, client.View.State);
            Assert.Equal(AccountNotice.Expired, client.View.Notice);
            Assert.Equal("", client.View.Id);
            Assert.Empty(client.View.Friends);
            // An expired session is not revoked: there is nothing to end.
            Assert.Equal(0, fake.Count("DELETE"));
        }
    }

    [Theory]
    [InlineData("""{"friends":[],"friends":[],"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"friends":{},"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"friends":[{"id":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","name":"x","since":"2026-11-01T10:00:00+00:00"}],"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"friends":[{"id":"../../v1/accounts/sessions/curr","name":"x","since":"2026-11-01T10:00:00+00:00"}],"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"friends":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":7,"since":"2026-11-01T10:00:00+00:00"}],"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"friends":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"x","since":"2026-11-01T10:00:00+01:00"}],"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"friends":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"x","since":"yesterday"}],"incoming":[],"outgoing":[]}""")]
    [InlineData("""{"friends":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"x","since":"2026-11-01T10:00:00+00:00"},{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"y","since":"2026-11-01T10:00:00+00:00"}],"incoming":[],"outgoing":[]}""")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("too-many")]
    [InlineData("too-large")]
    public async Task AFriendsReplyThatBreaksAnyRuleIsRefusedAndTheLastListKept(string reply)
    {
        if (reply == "too-many")
            reply = Lists(string.Join(',', Enumerable.Range(0, AccountClient.MaxListEntries + 1).Select(i => Entry(i.ToString("x32")))));
        else if (reply == "too-large")
            reply = Lists() + new string(' ', AccountClient.MaxListBytes);
        var first = true;
        var (fake, client, browser) = await FakeSignInAsync((request, _) =>
        {
            if (first)
            {
                first = false;
                return FakeAnswer.Json(200, Lists(Entry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Kept")));
            }

            return FakeAnswer.Json(200, reply);
        });
        using (fake)
        using (client)
        using (browser)
        {
            Assert.True(client.Refresh());
            await Idle(client);
            Assert.Equal(AccountNotice.BadReply, client.View.Notice);
            Assert.Equal("Kept", Assert.Single(client.View.Friends).Name);
            Assert.Equal(AccountState.SignedIn, client.View.State);
        }
    }

    [Fact]
    public async Task ALateSuccessfulAnswerCannotChangeTheReplacementOrFinishItsOperation()
    {
        var hold = false;
        var starts = 0;
        var oldReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (fake, client, browser) = await FakeSignInAsync(
            (request, _) => request.Method == "DELETE" ? FakeAnswer.Json(204, "")
                : FakeAnswer.Json(200, Lists(Entry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Old friend"))),
            async path =>
            {
                if (path == "v1/friends" && hold)
                {
                    oldReceived.SetResult();
                    await releaseOld.Task;
                }
                else if (path == "v1/accounts/signin" && ++starts == 2)
                {
                    newReceived.SetResult();
                    await releaseNew.Task;
                }
            });
        using (fake)
        using (client)
        using (browser)
        {
            CancellationTokenSource? oldOperation = null, newOperation = null;
            try
            {
                hold = true;
                Assert.True(client.Refresh());
                oldOperation = CurrentOperation(client);
                // A real successful HTTP response has arrived; only its continuation is paused.
                await oldReceived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                client.SignOut();
                Assert.True(client.SignIn());
                newOperation = CurrentOperation(client);
                await newReceived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                var replacement = client.View;
                Assert.Equal(AccountState.Starting, replacement.State);
                Assert.True(replacement.Busy);
                Assert.Equal("", replacement.Id);
                Assert.Empty(replacement.Friends);
                Assert.Equal(AccountNotice.SignedOut, replacement.Notice);

                releaseOld.SetResult();
                await OperationStopped(oldOperation);
                // This includes both the late list publication and RunAsync's finally block.
                Assert.Same(replacement, client.View);
                Assert.Same(newOperation, CurrentOperation(client));
            }
            finally
            {
                client.CancelSignIn();
                releaseOld.TrySetResult();
                releaseNew.TrySetResult();
                if (oldOperation is not null)
                    await OperationStopped(oldOperation);
                if (newOperation is not null)
                    await OperationStopped(newOperation);
            }

            Assert.Equal(1, browser.Opens);

            // The session is ended at the service with the token it was given, and only there.
            for (var i = 0; i < 200 && fake.Count("DELETE") == 0; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            var revoke = Assert.Single(fake.Requests, r => r.Method == "DELETE");
            Assert.Equal("/v1/accounts/sessions/current", revoke.Path);
            Assert.Equal("Bearer " + Token, revoke.Authorization);
            Assert.DoesNotContain(fake.Requests, r => r.Path.Contains(Token, StringComparison.Ordinal) || r.Body.Contains(Token, StringComparison.Ordinal));
        }
    }

    private static CancellationTokenSource CurrentOperation(AccountClient client) =>
        (CancellationTokenSource)typeof(AccountClient).GetField("_operation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;

    private static async Task OperationStopped(CancellationTokenSource operation)
    {
        // The source is disposed only after RunAsync has finished its cleanup, not merely when work returns.
        for (var i = 0; i < 1000; i++)
        {
            try
            {
                _ = operation.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The account operation did not finish its cleanup.");
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("signout")]
    [InlineData("dispose")]
    public async Task BrowserLaunchAndAbandonmentAreSerialized(string action)
    {
        using var fake = new FakeListing((_, _) => FakeAnswer.Json(201, $$"""{"attempt":"{{Attempt}}","expiresIn":600}"""));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var client = new AccountClient(ListingConfig.Parse("{}"u8.ToArray(), "test", fake.Url), _ =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                throw new TimeoutException("The test did not release the browser opener.");
            return true;
        });
        Assert.True(client.SignIn());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var operation = CurrentOperation(client);
        var stop = Task.Run(() =>
        {
            stopping.SetResult();
            switch (action)
            {
                case "cancel": client.CancelSignIn(); break;
                case "signout": client.SignOut(); break;
                default: client.Dispose(); break;
            }
        }, TestContext.Current.CancellationToken);
        var returnedDuringLaunch = false;
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            returnedDuringLaunch = await Task.WhenAny(stop, Task.Delay(250, TestContext.Current.CancellationToken)) == stop;
        }
        finally
        {
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await OperationStopped(operation);
        }

        Assert.False(returnedDuringLaunch, "Abandonment returned while the browser was still being launched.");
        Assert.NotEqual(AccountState.SignedIn, client.View.State);
    }

    [Fact]
    public async Task CancellingAfterWaitingWasPublishedPreventsTheBrowserLaunch()
    {
        using var fake = new FakeListing((_, _) => FakeAnswer.Json(201, $$"""{"attempt":"{{Attempt}}","expiresIn":600}"""));
        using var browser = new FakeBrowser();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new AccountClient(ListingConfig.Parse("{}"u8.ToArray(), "test", fake.Url), browser.Open)
        {
            BeforeBrowserLaunch = async () =>
            {
                ready.SetResult();
                await release.Task;
            },
        };
        Assert.True(client.SignIn());
        var operation = CurrentOperation(client);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(AccountState.Waiting, client.View.State);
            client.CancelSignIn();
        }
        finally
        {
            client.CancelSignIn();
            release.TrySetResult();
            await OperationStopped(operation);
        }

        Assert.Equal(0, browser.Opens);
        Assert.Equal(AccountState.SignedOut, client.View.State);
        Assert.Equal(AccountNotice.SigninCancelled, client.View.Notice);
    }

    [Theory]
    [InlineData(503, "{\"error\":\"Accounts disabled.\"}", AccountNotice.Unavailable)]
    [InlineData(429, "{\"error\":\"Too many requests.\"}", AccountNotice.RateLimited)]
    [InlineData(201, "{\"attempt\":\"https://evil.example/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"expiresIn\":600}", AccountNotice.BadReply)]
    [InlineData(201, "{\"attempt\":\"../../../../../../../../../../../../../../x\",\"expiresIn\":600}", AccountNotice.BadReply)]
    [InlineData(201, "{\"attempt\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB\",\"expiresIn\":600}", AccountNotice.BadReply)]
    [InlineData(201, "{\"attempt\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"expiresIn\":99999}", AccountNotice.BadReply)]
    [InlineData(302, "", AccountNotice.SigninFailed)]
    public async Task ASigninTheServiceRefusesOrAnswersStrangelyNeverOpensABrowser(int status, string body, AccountNotice notice)
    {
        using var fake = new FakeListing((_, _) => status == 302 ? new FakeAnswer { Status = 302, Location = "https://evil.example/" } : FakeAnswer.Json(status, body));
        using var browser = new FakeBrowser();
        using var client = new AccountClient(ListingConfig.Parse("{}"u8.ToArray(), "test", fake.Url), browser.Open);
        Assert.True(client.SignIn());
        await Until(client, v => v.State == AccountState.SignedOut && !v.Busy, "the refused sign-in");
        Assert.Equal(notice, client.View.Notice);
        Assert.Equal(0, browser.Opens);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public void WithoutAListingServiceThereAreNoAccountsAndGuestsAreUntouched()
    {
        var opened = 0;
        using var client = new AccountClient(null, _ => ++opened > 0);
        Assert.Equal(AccountState.Off, client.View.State);
        Assert.False(client.SignIn());
        Assert.False(client.Refresh());
        client.SignOut();
        Assert.Equal(AccountState.Off, client.View.State);
        Assert.Equal(0, opened);
    }

    [Fact]
    public void HostileNamesAreCleanedAndBoundedAndSamplesUseTheSameParser()
    {
        Assert.Equal("##", AccountClient.CleanName("##"));
        Assert.Equal("### %s %n", AccountClient.CleanName("### %s %n"));
        Assert.Equal("Pilot One", AccountClient.CleanName("Pilot\u202e One"));
        Assert.Equal("Pilot", AccountClient.CleanName("\u0000\u200b\u2028"));
        Assert.Equal("東京 ☄", AccountClient.CleanName("東京 ☄"));
        Assert.Equal(24, AccountClient.CleanName(new string('W', 80)).Length);
        // A character outside the basic plane is never cut in half at the limit.
        var emoji = AccountClient.CleanName(new string('a', 23) + "\U0001F680");
        Assert.Equal(new string('a', 23), emoji);
        Assert.Equal("Pilot", AccountClient.CleanName("\ud800"));

        var friends = Lists(Entry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "%s%n ### {0}\u202e"));
        using var sample = AccountClient.Sample(AccountState.SignedIn, Encoding.UTF8.GetBytes(Account), Encoding.UTF8.GetBytes(friends), AccountNotice.None, null);
        var friend = Assert.Single(sample.View.Friends);
        Assert.Equal("%s%n ### {0}", friend.Name);
        Assert.Equal("2026-11-01"u8.ToArray(), friend.SinceUtf8);
        Assert.Equal(AccountState.SignedIn, sample.View.State);
        Assert.False(sample.SignIn());
        Assert.False(sample.Refresh());
        Assert.False(sample.Remove(friend.Id));
        Assert.Throws<InvalidDataException>(() => AccountClient.Sample(AccountState.SignedIn, "{}"u8, default, AccountNotice.None, null));
    }

    [Fact]
    public void CaptureSamplesGoThroughTheSameParserAndRefuseEveryAction()
    {
        foreach (var kind in new[] { "signed-out", "waiting", "signed-in", "friends", "failed", "off" })
        {
            using var sample = Prospect.Client.Screens.ScreenHost.AccountSample(kind);
            Assert.False(sample.SignIn());
            Assert.False(sample.Refresh());
            Assert.False(sample.AddFriend("0123456789ABCDEF"));
        }

        using var full = Prospect.Client.Screens.ScreenHost.AccountSample("friends");
        Assert.Equal((200, 200, 50), (full.View.Friends.Length, full.View.Incoming.Length, full.View.Outgoing.Length));
        Assert.Contains(full.View.Friends, f => f.Name == "##");
        Assert.Contains(full.View.Friends, f => f.Name == "RTL override");
        Assert.All(full.View.Friends, f => Assert.True(f.Name.Length <= AccountClient.MaxNameChars));
    }

    [Theory]
    [InlineData("https://prospect.mvl.sh/v1/accounts/signin/abc", true)]
    [InlineData("http://127.0.0.1:8080/v1/accounts/signin/abc", true)]
    [InlineData("http://prospect.mvl.sh/v1/accounts/signin/abc", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pass@prospect.mvl.sh/", false)]
    [InlineData("https://prospect.mvl.sh/#frag", false)]
    [InlineData("https://prospect.mvl.sh/a\"b c`d", true)]
    // A quote that escaping leaves in place is refused outright.
    [InlineData("https://prospect.mvl.sh/a'b", false)]
    public void TheBrowserIsOnlyEverGivenAPlainWebAddress(string address, bool safe)
    {
        Assert.Equal(safe, Uri.TryCreate(address, UriKind.Absolute, out var uri) && Prospect.Client.Platform.BrowserLauncher.IsSafe(uri));
        // What reaches the opener is escaped: no quote, space or backtick a command line could read.
        if (safe)
            Assert.DoesNotContain(uri!.AbsoluteUri, c => c is '"' or ' ' or '\'' or '`');
    }

    [Fact]
    public void VerifiersAndChallengesFollowRfc7636()
    {
        // The RFC 7636 appendix B example.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", AccountClient.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.True(AccountClient.IsCanonicalSecret(AccountClient.Encode(new byte[32])));
        Assert.False(AccountClient.IsCanonicalSecret("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB"));
        Assert.False(AccountClient.IsCanonicalSecret(AccountClient.Encode(new byte[32]) + "A"));
        Assert.False(AccountClient.IsCanonicalSecret("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+"));
    }
}
