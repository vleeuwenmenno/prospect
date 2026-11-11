using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Prospect.Listing.Accounts;
using Xunit;

namespace Prospect.Listing.Tests;

/// <summary>Real HTTP OAuth exchanges and durable account/friend invariants.</summary>
public sealed class AccountTests
{
    [Fact]
    public async Task LoginPersistsWithoutExposingProviderIdentityAndRevokes()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        Assert.Equal("Pilot One", a.Account.Name);
        Assert.Equal(86400, a.ExpiresIn);
        Assert.Equal(43, a.Token.Length);
        Assert.DoesNotContain("123456789", JsonSerializer.Serialize(a));
        Assert.DoesNotContain("never-public-handle", JsonSerializer.Serialize(a));
        Assert.DoesNotContain("provider-secret", JsonSerializer.Serialize(a));
        await h.Restart();
        Assert.Equal(a.Account.Id, (await AccountHost.Json(await h.Send("GET", "/v1/accounts/me", a.Token))).GetProperty("id").GetString());
        var b = await h.Login();
        Assert.Equal(a.Account, b.Account);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/accounts/sessions/current", a.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("GET", "/v1/accounts/me", b.Token)).StatusCode);
        var logs = string.Join('\n', h.Logs);
        foreach (var secret in new[] { a.Token, "provider-secret", "provider-code", "123456789", "never-public-handle" })
            Assert.DoesNotContain(secret, logs);
    }

    [Fact]
    public async Task HandoffRequiresBothSecretsAndRedemptionIsSingleUse()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Begin();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/v1/accounts/signin/" + a.Attempt, ListingHost.Cancel)).StatusCode);
        var code = await h.Complete(a.State);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/v1/accounts/discord/callback?code=x&state=" + a.State, ListingHost.Cancel)).StatusCode);
        Assert.Equal(1, h.Exchanges);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Redeem(a.Attempt, code, AccountCrypto.Random())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Redeem(a.Attempt, AccountCrypto.Random(), a.Verifier)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync("/v1/accounts/sessions", new { attempt = a.Attempt, code }, ListingHost.Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/v1/accounts/signin/" + a.Attempt + "/poll", ListingHost.Cancel)).StatusCode);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Redeem(a.Attempt, code, a.Verifier)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("wrong-client")]
    [InlineData("invalid-token")]
    public async Task ProviderFailureReturnsFixedErrorAndReleasesAttempt(string mode)
    {
        await using var h = await AccountHost.Start(("Accounts:MaxAttempts", "1"), ("Accounts:AttemptsPerAddress", "1"));
        var a = await h.Begin();
        h.ProviderMode = mode;
        var response = await h.Client.GetAsync("/v1/accounts/discord/callback?code=x&state=" + a.State, ListingHost.Cancel);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("http://127.0.0.1:12345/prospect/signin?attempt=" + a.Attempt + "&error=failed",
            response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/v1/accounts/discord/callback?code=x&state=" + a.State, ListingHost.Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Redeem(a.Attempt, AccountCrypto.Random(), a.Verifier)).StatusCode);
        await h.Begin();
    }

    [Fact]
    public async Task ConsentCancellationReturnsOnlyFixedErrorToStoredLoopback()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxAttempts", "1"), ("Accounts:AttemptsPerAddress", "1"));
        var a = await h.Begin(23456);
        var response = await h.Client.GetAsync("/v1/accounts/discord/callback?state=" + a.State
            + "&error=access_denied&error_description=PRIVATE_MARKER&return_url=https://evil.example", ListingHost.Cancel);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("http://127.0.0.1:23456/prospect/signin?attempt=" + a.Attempt + "&error=access_denied",
            response.Headers.Location!.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(0, h.Exchanges);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync(
            "/v1/accounts/discord/callback?state=" + a.State + "&code=x", ListingHost.Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Redeem(a.Attempt, AccountCrypto.Random(), a.Verifier)).StatusCode);
        await h.Begin();
    }

    [Theory]
    [InlineData("", "POST")]
    [InlineData("/", "POST")]
    [InlineData("", "post")]
    public async Task ProtectedRoutesMatchRouterCaseInsensitivePaths(string suffix, string method)
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login("1");
        var b = await h.Login("2");
        Assert.Equal(a.Account.Id, (await AccountHost.Json(await h.Send("GET", "/V1/ACCOUNTS/ME" + suffix, a.Token)))
            .GetProperty("id").GetString());
        if (method == "post")
        {
            // HttpClient normalizes known method names, so preserve the lowercase bytes with a socket.
            using var socket = new TcpClient();
            await socket.ConnectAsync(h.Client.BaseAddress!.Host, h.Client.BaseAddress.Port, ListingHost.Cancel);
            using var stream = socket.GetStream();
            var body = JsonSerializer.Serialize(new { friendCode = b.Account.FriendCode });
            var request = $"post /V1/FRIENDS/REQUESTS{suffix} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n"
                + $"Authorization: Bearer {a.Token}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), ListingHost.Cancel);
            using var reader = new StreamReader(stream);
            Assert.StartsWith("HTTP/1.1 201 ", await reader.ReadToEndAsync(ListingHost.Cancel));
        }
        else
            Assert.Equal(HttpStatusCode.Created, (await h.Send(method, "/V1/FRIENDS/REQUESTS" + suffix, a.Token,
                new { friendCode = b.Account.FriendCode })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Send("POST", "/V1/FRIENDS/REQUESTS/" + a.Account.Id + "/ACCEPT" + suffix, b.Token)).StatusCode);
        Assert.Single((await AccountHost.Json(await h.Send("GET", "/V1/FRIENDS" + suffix, a.Token))).GetProperty("friends").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/V1/ACCOUNTS/SESSIONS/CURRENT" + suffix, a.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
    }

    [Fact]
    public async Task ForgedExpiredAndDuplicateStateNeverRedirectOrConsumeValidAttempt()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxAttempts", "1"), ("Accounts:AttemptsPerAddress", "1"));
        var a = await h.Begin();
        foreach (var query in new[] { "state=" + AccountCrypto.Random(), "state=" + a.State + "&state=" + a.State + "&code=x" })
        {
            var response = await h.Client.GetAsync("/v1/accounts/discord/callback?" + query, ListingHost.Cancel);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        var code = await h.Complete(a.State);
        Assert.Equal(HttpStatusCode.Created, (await h.Redeem(a.Attempt, code, a.Verifier)).StatusCode);
        var expired = await h.Begin();
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        var responseAfterExpiry = await h.Client.GetAsync("/v1/accounts/discord/callback?code=x&state=" + expired.State, ListingHost.Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, responseAfterExpiry.StatusCode);
        Assert.Null(responseAfterExpiry.Headers.Location);
        Assert.Equal(1, h.Exchanges);
        await h.Begin();
    }

    [Theory]
    [InlineData("")]
    [InlineData("&iss=https://evil.test&code=x")]
    [InlineData("&code=x&code=y")]
    [InlineData("&error=%3Cscript%3EPRIVATE_MARKER%3C/script%3E")]
    [InlineData("&error=access_denied&code=x")]
    public async Task InvalidCallbackWithValidStateReturnsFixedErrorAndReleasesAttempt(string query)
    {
        await using var h = await AccountHost.Start(("Accounts:MaxAttempts", "1"), ("Accounts:AttemptsPerAddress", "1"));
        var a = await h.Begin();
        var response = await h.Client.GetAsync("/v1/accounts/discord/callback?state=" + a.State + query, ListingHost.Cancel);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("http://127.0.0.1:12345/prospect/signin?attempt=" + a.Attempt + "&error=failed",
            response.Headers.Location!.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.DoesNotContain("PRIVATE_MARKER", await response.Content.ReadAsStringAsync(ListingHost.Cancel));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync(
            "/v1/accounts/discord/callback?state=" + a.State + "&code=x", ListingHost.Cancel)).StatusCode);
        Assert.Equal(0, h.Exchanges);
        await h.Begin();
    }

    [Fact]
    public async Task ExpiryDuringProviderExchangeReturnsFailureAndReleasesAttempt()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxAttempts", "1"), ("Accounts:AttemptsPerAddress", "1"));
        var a = await h.Begin();
        h.ProviderMode = "stall";
        var pending = h.Client.GetAsync("/v1/accounts/discord/callback?state=" + a.State + "&code=x", ListingHost.Cancel);
        await h.ProviderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ListingHost.Cancel);
        try { h.Clock.Advance(TimeSpan.FromMinutes(10)); }
        finally { h.ReleaseProvider.TrySetResult(); }
        var response = await pending;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("http://127.0.0.1:12345/prospect/signin?attempt=" + a.Attempt + "&error=failed",
            response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Redeem(a.Attempt, AccountCrypto.Random(), a.Verifier)).StatusCode);
        await h.Begin();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public async Task InvalidLoopbackPortRejected(int port)
    {
        await using var h = await AccountHost.Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync("/v1/accounts/signin",
            new { challenge = AccountCrypto.Random(), port }, ListingHost.Cancel)).StatusCode);
    }

    [Fact]
    public async Task ParsingAndDisabledAndProxyGatesPrecedeWork()
    {
        await using var h = await AccountHost.Start();
        var challenge = AccountCrypto.Random();
        var duplicate = "{\"challenge\":\"" + challenge + "\",\"port\":12345,\"port\":12346}";
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/v1/accounts/signin", new StringContent(duplicate, Encoding.UTF8, "application/json"), ListingHost.Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await h.Client.PostAsync("/v1/accounts/signin",
            new StringContent(new string('x', 5000), Encoding.UTF8, "application/json"), ListingHost.Cancel)).StatusCode);
        await h.Restart(("Accounts:Enabled", "false"), ("Accounts:ClientSecret", ""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Client.GetAsync("/v1/accounts/me", ListingHost.Cancel)).StatusCode);
        Assert.False(File.Exists(h.Database));
        await h.Restart(("Listing:TrustedProxies", "127.0.0.1"));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/v1/accounts/discord/callback?code=x&state=x", ListingHost.Cancel)).StatusCode);
        Assert.Equal(0, h.Exchanges);
    }

    [Fact]
    public async Task FriendsCrossAcceptAuthorizePersistAndRemove()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login("1");
        var b = await h.Login("2");
        var c = await h.Login("3");
        Assert.Equal(HttpStatusCode.Created, (await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = b.Account.FriendCode })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = b.Account.FriendCode })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Send("POST", "/v1/friends/requests/" + b.Account.Id + "/accept", a.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/friends/requests/" + a.Account.Id, c.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Send("DELETE", "/v1/friends/requests/" + a.Account.Id, b.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Send("POST", "/v1/friends/requests", b.Token, new { friendCode = a.Account.FriendCode })).StatusCode);
        await h.Restart();
        var list = await AccountHost.Json(await h.Send("GET", "/v1/friends", a.Token));
        Assert.Single(list.GetProperty("friends").EnumerateArray());
        Assert.Equal(b.Account.Id, list.GetProperty("friends")[0].GetProperty("id").GetString());
        Assert.DoesNotContain("friendCode", list.GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, (await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = b.Account.FriendCode })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/friends/" + b.Account.Id, a.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/friends/" + b.Account.Id, a.Token)).StatusCode);
    }

    [Fact]
    public async Task CapsPendingExpiryAndSessionEviction()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxOutgoing", "1"), ("Accounts:MaxIncoming", "1"), ("Accounts:MaxFriends", "1"));
        var a = await h.Login("1");
        var b = await h.Login("2");
        var c = await h.Login("3");
        Assert.Equal(HttpStatusCode.Created, (await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = b.Account.FriendCode })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = c.Account.FriendCode })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await h.Send("POST", "/v1/friends/requests", c.Token, new { friendCode = b.Account.FriendCode })).StatusCode);
        for (var i = 0; i < 10; i++) await h.Login("1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
        h.Clock.Advance(TimeSpan.FromDays(31));
        a = await h.Login("1");
        Assert.Empty((await AccountHost.Json(await h.Send("GET", "/v1/friends", a.Token))).GetProperty("outgoing").EnumerateArray());
    }

    [Fact]
    public async Task DatabaseFaultDoesNotBreakListing()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        File.Delete(h.Database);
        Directory.CreateDirectory(h.Database);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
        Assert.Equal("ok", await h.Client.GetStringAsync("/healthz", ListingHost.Cancel));
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync("/v1/servers?protocol=16", ListingHost.Cancel)).StatusCode);
    }

    [Fact]
    public async Task AttemptCapsReclaimExpiredStates()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxAttempts", "2"), ("Accounts:AttemptsPerAddress", "2"));
        var a = await h.Begin();
        await h.Begin();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await h.Client.PostAsJsonAsync("/v1/accounts/signin",
            new { challenge = AccountCrypto.Random(), port = 12345 }, ListingHost.Cancel)).StatusCode);
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/accounts/discord/callback?code=x&state=" + a.State);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.SendAsync(request, ListingHost.Cancel)).StatusCode);
        await h.Begin();
    }

    [Fact]
    public async Task AcceptRemovalRacesPreserveCapsAndUniquePairs()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxFriends", "1"));
        var a = await h.Login("1");
        var b = await h.Login("2");
        var c = await h.Login("3");
        await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = b.Account.FriendCode });
        await h.Send("POST", "/v1/friends/requests", c.Token, new { friendCode = b.Account.FriendCode });
        var replies = await Task.WhenAll(
            h.Send("POST", "/v1/friends/requests/" + a.Account.Id + "/accept", b.Token),
            h.Send("POST", "/v1/friends/requests/" + c.Account.Id + "/accept", b.Token));
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(replies, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable }));
        Assert.Single((await AccountHost.Json(await h.Send("GET", "/v1/friends", b.Token))).GetProperty("friends").EnumerateArray());
        await Task.WhenAll(
            h.Send("DELETE", "/v1/friends/" + a.Account.Id, b.Token),
            h.Send("POST", "/v1/friends/requests/" + a.Account.Id + "/accept", b.Token));
        var friends = (await AccountHost.Json(await h.Send("GET", "/v1/friends", b.Token))).GetProperty("friends");
        Assert.True(friends.GetArrayLength() <= 1);
    }

    [Fact]
    public async Task DeclineCancelSelfAndUnknownCodesHaveSafeRetries()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login("1");
        var b = await h.Login("2");
        Assert.Equal(HttpStatusCode.Conflict, (await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = a.Account.FriendCode })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = "0000000000000000" })).StatusCode);
        await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = b.Account.FriendCode });
        Assert.Equal(HttpStatusCode.NotFound, (await h.Send("POST", "/v1/friends/requests/" + b.Account.Id + "/decline", a.Token)).StatusCode);
        for (var n = 0; n < 2; n++)
            Assert.Equal(HttpStatusCode.NoContent, (await h.Send("POST", "/v1/friends/requests/" + a.Account.Id + "/decline", b.Token)).StatusCode);
        await h.Send("POST", "/v1/friends/requests", a.Token, new { friendCode = b.Account.FriendCode });
        for (var n = 0; n < 2; n++)
            Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/friends/requests/" + b.Account.Id, a.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.GetAsync("/v1/accounts/me?token=" + a.Token, ListingHost.Cancel)).StatusCode);
    }

    [Fact]
    public async Task LockedDatabaseImmediatelyShedsExtraWorkWhileListingWorks()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + h.Database);
        db.Open();
        using var transaction = db.BeginTransaction(deferred: false);
        var requests = Enumerable.Range(0, 8).Select(_ => h.Send("GET", "/v1/accounts/me", a.Token)).ToArray();
        Assert.Equal("ok", await h.Client.GetStringAsync("/healthz", ListingHost.Cancel));
        var replies = await Task.WhenAll(requests);
        Assert.All(replies, r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode));
    }

    [Theory]
    [InlineData(RequestKind.Auth)]
    [InlineData(RequestKind.Account)]
    public void NewLimiterBucketsSurviveOverflowAndRequireAllRefills(RequestKind kind)
    {
        var clock = new ManualClock();
        var options = new ListingOptions { RateLimitAddresses = 1, RateLimitOverflowBuckets = 1, AuthPerMinute = 1, AccountPerMinute = 1 };
        var limiter = new RequestLimiter(options, clock);
        Assert.True(limiter.TryAcquire("a", kind).Allowed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.TryAcquire("b", kind).Allowed);
        Assert.False(limiter.TryAcquire("a", kind).Allowed);
        Assert.False(limiter.TryAcquire("c", kind).Allowed);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(limiter.TryAcquire("c", RequestKind.List).Allowed);
        Assert.False(limiter.TryAcquire("c", kind).Allowed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("c", kind).Allowed);
    }

    [Fact]
    public async Task ProviderConcurrencyHasNoQueueAndTotalTimeoutIsBounded()
    {
        await using var h = await AccountHost.Start(("Accounts:DiscordConcurrency", "1"), ("Accounts:DiscordTimeoutSeconds", "1"));
        var a = await h.Begin();
        var b = await h.Begin();
        h.ProviderMode = "stall";
        var pending = h.Client.GetAsync("/v1/accounts/discord/callback?code=x&state=" + a.State, ListingHost.Cancel);
        await h.ProviderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ListingHost.Cancel);
        try
        {
            var busy = await h.Client.GetAsync("/v1/accounts/discord/callback?code=x&state=" + b.State, ListingHost.Cancel);
            Assert.Equal(HttpStatusCode.Redirect, busy.StatusCode);
            Assert.Equal("http://127.0.0.1:12345/prospect/signin?attempt=" + b.Attempt + "&error=failed", busy.Headers.Location!.OriginalString);
            Assert.Equal("ok", await h.Client.GetStringAsync("/healthz", ListingHost.Cancel));
            var timedOut = await pending;
            Assert.Equal(HttpStatusCode.Redirect, timedOut.StatusCode);
            Assert.Equal("http://127.0.0.1:12345/prospect/signin?attempt=" + a.Attempt + "&error=failed", timedOut.Headers.Location!.OriginalString);
            Assert.Equal(1, h.Exchanges);
        }
        finally { h.ReleaseProvider.TrySetResult(); }
    }

    [Fact]
    public async Task CallbackMayChangeAddressButCannotChangeFixedOriginOrLoopbackPort()
    {
        await using var h = await AccountHost.Start(("Listing:TrustedProxies", "127.0.0.1"));
        h.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.1");
        h.Client.DefaultRequestHeaders.Host = "evil.example";
        h.Client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example");
        var a = await h.Begin();
        h.Client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        h.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "2001:db8::1");
        var code = await h.Complete(a.State);
        Assert.Equal(HttpStatusCode.Created, (await h.Redeem(a.Attempt, code, a.Verifier)).StatusCode);
    }

    [Fact]
    public void ConfigurationAndNameFallbackAreBounded()
    {
        foreach (var url in new[] { "http://example.com", "https://x/a", "https://x/?a=b", "https://x/#f", "https://user@x", "http://localhost", "http://127.1", "http://2130706433" })
            Assert.Throws<InvalidOperationException>(() => new AccountsOptions { Enabled = true, PublicUrl = url, ClientId = "x", ClientSecret = "y" }.Validate());
        foreach (var url in new[] { "https://example.com", "http://127.0.0.1:1234", "http://[::1]:1234" })
            new AccountsOptions { Enabled = true, PublicUrl = url, ClientId = "x", ClientSecret = "y" }.Validate();
        Assert.Equal("Pilot-ABCD", AccountCrypto.Name("\u202e\n", "abcd"));
        Assert.True(AccountCrypto.Name(new string('x', 100), "abcd").Length <= 24);
    }
}
