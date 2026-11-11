using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit;
using static Prospect.Listing.Tests.ListingHost;

namespace Prospect.Listing.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task AnnouncedServerIsListedWithTheRequestAddress()
    {
        await using var host = await StartAsync();
        var reply = await host.ListAsync(Body());

        Assert.Equal(90, reply.TtlSeconds);
        Assert.False(string.IsNullOrEmpty(reply.Id));
        Assert.True(reply.Token.Length >= 40);

        var (_, list) = await host.GetListAsync();
        var server = Assert.Single(list.Servers);
        Assert.Equal(new ServerReply("Ceres Station", "127.0.0.1", 27420, 3, 16, false, "0.1.0"), server);
    }

    [Fact]
    public async Task AddressInTheBodyIsIgnored()
    {
        await using var host = await StartAsync();
        var body = Body();
        body["address"] = "203.0.113.1";
        await host.ListAsync(body);

        var (_, list) = await host.GetListAsync();
        Assert.Equal("127.0.0.1", Assert.Single(list.Servers).Address);
    }

    [Fact]
    public async Task ListFiltersByProtocolAndNeverCarriesTokens()
    {
        await using var host = await StartAsync();
        var a = await host.ListAsync(Body(name: "Ten", port: 1000, protocol: 10));
        var b = await host.ListAsync(Body(name: "Eleven", port: 1001, protocol: 11));

        var (json10, list10) = await host.GetListAsync(10);
        var (json11, list11) = await host.GetListAsync(11);
        var (_, list12) = await host.GetListAsync(12);

        Assert.Equal("Ten", Assert.Single(list10.Servers).Name);
        Assert.Equal("Eleven", Assert.Single(list11.Servers).Name);
        Assert.Empty(list12.Servers);
        foreach (var json in new[] { json10, json11 })
        {
            Assert.DoesNotContain(a.Token, json);
            Assert.DoesNotContain(b.Token, json);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(a.Id, json);
        }
    }

    [Fact]
    public async Task ListIsNewestHeartbeatFirstAndCapped()
    {
        await using var host = await StartAsync(("MaxListResults", "2"));
        var first = await host.ListAsync(Body(name: "First", port: 1));
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.ListAsync(Body(name: "Second", port: 2));
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.ListAsync(Body(name: "Third", port: 3));
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        using (var refresh = await host.AnnounceAsync(Body(name: "First", port: 1, token: first.Token)))
        {
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        }

        var (_, list) = await host.GetListAsync();
        Assert.Equal(["First", "Third"], list.Servers.Select(s => s.Name));
    }

    [Fact]
    public async Task RefreshWithTokenUpdatesTheEntry()
    {
        await using var host = await StartAsync();
        var reply = await host.ListAsync(Body());
        var update = Body(name: "Renamed", token: reply.Token);
        update["players"] = 9;

        using var response = await host.AnnounceAsync(update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = await response.Content.ReadFromJsonAsync<AnnounceReply>(Cancel);
        Assert.Equal(reply.Id, refreshed!.Id);
        Assert.Equal(reply.Token, refreshed.Token);

        var (_, list) = await host.GetListAsync();
        var server = Assert.Single(list.Servers);
        Assert.Equal("Renamed", server.Name);
        Assert.Equal(9, server.Players);
    }

    [Fact]
    public async Task AnnouncingAListedEndpointWithoutTokenIsAConflict()
    {
        await using var host = await StartAsync();
        await host.ListAsync(Body());

        using var response = await host.AnnounceAsync(Body(name: "Impostor"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (_, list) = await host.GetListAsync();
        Assert.Equal("Ceres Station", Assert.Single(list.Servers).Name);
    }

    [Fact]
    public async Task EntryExpiresWithoutHeartbeatAndStaysWhileRefreshed()
    {
        await using var host = await StartAsync();
        var kept = await host.ListAsync(Body(name: "Kept", port: 1));
        await host.ListAsync(Body(name: "Dropped", port: 2));

        host.Clock.Advance(TimeSpan.FromSeconds(60));
        using (var refresh = await host.AnnounceAsync(Body(name: "Kept", port: 1, token: kept.Token)))
        {
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        }

        host.Clock.Advance(TimeSpan.FromSeconds(31));
        var (_, list) = await host.GetListAsync();
        Assert.Equal("Kept", Assert.Single(list.Servers).Name);

        host.Clock.Advance(TimeSpan.FromSeconds(60));
        (_, list) = await host.GetListAsync();
        Assert.Empty(list.Servers);
    }

    [Fact]
    public async Task ExpiredEntryIsSweptWithoutAnyRequest()
    {
        await using var host = await StartAsync(("SweepIntervalMilliseconds", "20"));
        await host.ListAsync(Body());
        Assert.Equal(1, host.Store.Count);

        host.Clock.Advance(TimeSpan.FromSeconds(91));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Store.Count != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Cancel);
        }

        Assert.Equal(0, host.Store.Count);
    }

    [Fact]
    public async Task ExpiredEntryCanBeAnnouncedAgainWithItsOldToken()
    {
        await using var host = await StartAsync();
        var old = await host.ListAsync(Body());
        host.Clock.Advance(TimeSpan.FromSeconds(120));

        using var response = await host.AnnounceAsync(Body(token: old.Token));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fresh = await response.Content.ReadFromJsonAsync<AnnounceReply>(Cancel);
        Assert.NotEqual(old.Token, fresh!.Token);
        Assert.NotEqual(old.Id, fresh.Id);
    }

    [Fact]
    public async Task RemoveWithTokenDeletesTheEntry()
    {
        await using var host = await StartAsync();
        var reply = await host.ListAsync(Body());

        using (var response = await host.RemoveAsync(reply.Id, reply.Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var (_, list) = await host.GetListAsync();
        Assert.Empty(list.Servers);
        Assert.Equal(0, host.Store.Count);

        using var again = await host.RemoveAsync(reply.Id, reply.Token);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task HealthCheckAnswers()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.GetAsync("/healthz", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public sealed class TokenTests
{
    [Fact]
    public async Task WrongTokenCannotRefreshAnotherServersEntry()
    {
        await using var host = await StartAsync();
        var owner = await host.ListAsync(Body());
        var other = await host.ListAsync(Body(port: 27421));

        using var response = await host.AnnounceAsync(Body(name: "Hijacked", token: other.Token));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var garbage = await host.AnnounceAsync(Body(name: "Hijacked", token: owner.Token + "x"));
        Assert.Equal(HttpStatusCode.Forbidden, garbage.StatusCode);

        var (_, list) = await host.GetListAsync();
        Assert.DoesNotContain(list.Servers, s => s.Name == "Hijacked");
    }

    [Fact]
    public async Task WrongTokenCannotRemoveAnotherServersEntry()
    {
        await using var host = await StartAsync();
        var owner = await host.ListAsync(Body());
        var other = await host.ListAsync(Body(port: 27421));

        using (var response = await host.RemoveAsync(owner.Id, other.Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var noHeader = await host.Client.DeleteAsync("/v1/servers/" + owner.Id, Cancel))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noHeader.StatusCode);
        }

        Assert.Equal(2, host.Store.Count);
    }

    [Fact]
    public async Task RemovalWorksFromAnotherAddressWithTheToken()
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1"));
        var reply = await host.ListAsync(Body(), forwardedFor: "203.0.113.10");

        using var response = await host.RemoveAsync(reply.Id, reply.Token, forwardedFor: "198.51.100.20");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}

public sealed class ValidationTests
{
    // The valid tail of an announcement, after name and port.
    private const string Rest = "\"players\":0,\"maxPlayers\":1,\"protocol\":10,\"version\":\"1\",\"password\":false}";

    public static TheoryData<string, object?> BadFields => new()
    {
        { "name", "" },
        { "name", "   " },
        { "name", "\u0001\u0002​" },
        { "name", new string('x', 49) },
        { "name", null },
        { "port", 0 },
        { "port", 65536 },
        { "port", -1 },
        { "players", 17 },
        { "players", -1 },
        { "maxPlayers", 256 },
        { "protocol", -1 },
        { "protocol", 4294967296 },
        { "version", "" },
        { "version", new string('1', 33) },
        { "version", "1.0 <b>" },
        { "password", null },
        { "token", "" },
    };

    [Theory]
    [MemberData(nameof(BadFields))]
    public async Task BadFieldIsRefusedAndNothingStored(string field, object? value)
    {
        await using var host = await StartAsync();
        var body = Body();
        body[field] = value;

        using var response = await host.AnnounceAsync(body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("\"error\"", text);
        if (value is string s && s.Length > 0)
        {
            Assert.DoesNotContain(s, text);
        }

        Assert.Equal(0, host.Store.Count);
    }

    [Theory]
    [InlineData("port")]
    [InlineData("players")]
    [InlineData("maxPlayers")]
    [InlineData("protocol")]
    [InlineData("version")]
    [InlineData("password")]
    public async Task MissingFieldIsRefused(string field)
    {
        await using var host = await StartAsync();
        var body = Body();
        body.Remove(field);

        using var response = await host.AnnounceAsync(body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Store.Count);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("")]
    [InlineData("{\"name\":\"A\",\"name\":\"B\",\"port\":1," + Rest)]
    [InlineData("{\"name\":\"A\",\"port\":\"1\"," + Rest)]
    [InlineData("{\"name\":\"A\",\"port\":1.5," + Rest)]
    [InlineData("{\"Name\":\"A\",\"port\":1," + Rest)]
    [InlineData("{\"name\":\"A\",\"port\":1," + Rest + "{}")]
    public async Task MalformedJsonIsRefused(string json)
    {
        await using var host = await StartAsync();
        using var response = await host.PostRawAsync(json, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Store.Count);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/json; charset=utf-16")]
    public async Task WrongContentTypeIsRefused(string contentType)
    {
        await using var host = await StartAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(Body());
        using var response = await host.PostRawAsync(json, contentType);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, host.Store.Count);
    }

    [Fact]
    public async Task OversizedBodyIsRefused()
    {
        await using var host = await StartAsync(("MaxBodyBytes", "512"));
        var body = Body();
        body["padding"] = new string('x', 600);

        using var response = await host.AnnounceAsync(body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Store.Count);
    }

    [Fact]
    public async Task OversizedChunkedBodyIsRefused()
    {
        await using var host = await StartAsync(("MaxBodyBytes", "512"));
        var body = Body();
        body["padding"] = new string('x', 2000);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);
        // A stream of unknown length goes out chunked, with no Content-Length to refuse up front.
        var content = new StreamContent(new UnseekableStream(bytes));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        using var response = await host.Client.PostAsync("/v1/servers", content, Cancel);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Store.Count);
    }

    [Fact]
    public async Task NameIsSanitised()
    {
        await using var host = await StartAsync();
        await host.ListAsync(Body(name: "  ‮Evil\u0007 \t\n  Station​  "));

        var (_, list) = await host.GetListAsync();
        Assert.Equal("Evil Station", Assert.Single(list.Servers).Name);
    }

    [Fact]
    public async Task NameOfFortyEightCharactersIncludingEmojiIsAccepted()
    {
        await using var host = await StartAsync();
        var name = new string('a', 47) + "\U0001F680";
        await host.ListAsync(Body(name: name));

        var (_, list) = await host.GetListAsync();
        Assert.Equal(name, Assert.Single(list.Servers).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?protocol=")]
    [InlineData("?protocol=-1")]
    [InlineData("?protocol=abc")]
    [InlineData("?protocol=1&protocol=2")]
    [InlineData("?protocol=4294967296")]
    public async Task BadListQueryIsRefused(string query)
    {
        await using var host = await StartAsync();
        using var response = await host.Client.GetAsync("/v1/servers" + query, Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

public sealed class LimitTests
{
    [Fact]
    public async Task EntriesPerAddressAreCapped()
    {
        await using var host = await StartAsync(("MaxEntriesPerAddress", "2"));
        await host.ListAsync(Body(port: 1));
        await host.ListAsync(Body(port: 2));

        using var response = await host.AnnounceAsync(Body(port: 3));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(2, host.Store.Count);
    }

    [Fact]
    public async Task ExpiredEntriesFreeTheirAddressSlotsAtOnce()
    {
        await using var host = await StartAsync(("MaxEntriesPerAddress", "1"));
        await host.ListAsync(Body(port: 1));
        host.Clock.Advance(TimeSpan.FromSeconds(91));

        await host.ListAsync(Body(port: 2));
        Assert.Equal(1, host.Store.Count);
    }

    [Fact]
    public async Task Ipv6AddressesCountByPrefix()
    {
        await using var host = await StartAsync(("MaxEntriesPerAddress", "1"), ("TrustedProxies", "127.0.0.1"));
        await host.ListAsync(Body(), forwardedFor: "2001:db8:1:2::1");

        using var samePrefix = await host.AnnounceAsync(Body(), forwardedFor: "2001:db8:1:2::ffff");
        Assert.Equal(HttpStatusCode.TooManyRequests, samePrefix.StatusCode);
        await host.ListAsync(Body(), forwardedFor: "2001:db8:1:3::1");
    }

    [Fact]
    public async Task TotalEntriesAreCapped()
    {
        await using var host = await StartAsync(("MaxEntries", "2"), ("TrustedProxies", "127.0.0.1"));
        await host.ListAsync(Body(), forwardedFor: "203.0.113.1");
        await host.ListAsync(Body(), forwardedFor: "203.0.113.2");

        using var response = await host.AnnounceAsync(Body(), forwardedFor: "203.0.113.3");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(2, host.Store.Count);
    }

    [Fact]
    public async Task PostsAreRateLimitedPerAddress()
    {
        await using var host = await StartAsync(("PostsPerMinute", "3"), ("TrustedProxies", "127.0.0.1"));
        for (var port = 1; port <= 3; port++)
        {
            using var ok = await host.AnnounceAsync(Body(port: port), forwardedFor: "203.0.113.1");
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var limited = await host.AnnounceAsync(Body(port: 4), forwardedFor: "203.0.113.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);

        // Another address has its own budget.
        using var other = await host.AnnounceAsync(Body(port: 4), forwardedFor: "203.0.113.2");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(4, host.Store.Count);
    }

    [Fact]
    public async Task InvalidPostsCountTowardsTheRateLimit()
    {
        await using var host = await StartAsync(("PostsPerMinute", "2"));
        for (var i = 0; i < 2; i++)
        {
            using var bad = await host.PostRawAsync("{", "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        using var limited = await host.AnnounceAsync(Body());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task ListRequestsAreRateLimitedPerAddress()
    {
        await using var host = await StartAsync(("ListsPerMinute", "2"));
        await host.GetListAsync();
        await host.GetListAsync();

        using var limited = await host.Client.GetAsync("/v1/servers?protocol=10", Cancel);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task PrivateAddressesAreRefusedUnlessAllowed()
    {
        await using var host = await StartAsync(("AllowPrivateAddresses", "false"));
        using var response = await host.AnnounceAsync(Body());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Store.Count);
    }

    [Fact]
    public async Task ManyConcurrentPostsLeaveAConsistentStore()
    {
        await using var host = await StartAsync(
            ("MaxEntries", "50"),
            ("MaxEntriesPerAddress", "2"),
            ("PostsPerMinute", "100000"),
            ("TrustedProxies", "127.0.0.1"));

        // 100 addresses, three ports each: at most two per address may list, and only 50 in all.
        var posts = Enumerable.Range(0, 300).Select(async i =>
        {
            var address = $"203.0.113.{i / 3}";
            var port = 1 + (i % 3);
            using var response = await host.AnnounceAsync(Body(port: port), forwardedFor: address);
            var reply = response.StatusCode == HttpStatusCode.Created
                ? await response.Content.ReadFromJsonAsync<AnnounceReply>(Cancel)
                : null;
            return (Reply: reply, Address: address, Port: port);
        });
        var created = (await Task.WhenAll(posts)).Where(r => r.Reply is not null).ToList();

        Assert.Equal(50, created.Count);
        Assert.Equal(50, host.Store.Count);
        Assert.True(host.Store.IsConsistent());
        Assert.Equal(50, created.Select(r => r.Reply!.Id).Distinct().Count());

        // Remove half while the other half refresh under a new name, all at once.
        var mixed = created.Select(async (entry, i) =>
        {
            var reply = entry.Reply!;
            if (i % 2 == 0)
            {
                using var removed = await host.RemoveAsync(reply.Id, reply.Token, forwardedFor: entry.Address);
                return removed.StatusCode == HttpStatusCode.NoContent;
            }

            using var refreshed = await host.AnnounceAsync(
                Body(name: "Refreshed", port: entry.Port, token: reply.Token), forwardedFor: entry.Address);
            return refreshed.StatusCode == HttpStatusCode.OK
                && (await refreshed.Content.ReadFromJsonAsync<AnnounceReply>(Cancel))!.Id == reply.Id;
        });
        Assert.All(await Task.WhenAll(mixed), Assert.True);
        Assert.Equal(25, host.Store.Count);
        Assert.True(host.Store.IsConsistent());

        var (_, list) = await host.GetListAsync(forwardedFor: "198.51.100.1");
        Assert.Equal(25, list.Servers.Count);
        Assert.All(list.Servers, s => Assert.Equal("Refreshed", s.Name));
    }
}

public sealed class ForwardedAddressTests
{
    [Theory]
    [InlineData("fd53:26b5:89f8::", true)]
    [InlineData("fd53:26b5:89f8:0:ffff:ffff:ffff:ffff", true)]
    [InlineData("fd53:26b5:89f7:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("fd53:26b5:89f8:1::", false)]
    public async Task Ipv6ForwardingWalkStopsAtTheConfiguredBoundary(string hop, bool trusted)
    {
        await using var host = await StartIpv6Async(
            ("TrustedProxies", "::1/128, fd53:26b5:89f8::/64"), ("AllowPrivateAddresses", "false"));
        using var response = await host.AnnounceAsync(Body(), forwardedFor: $"{Routable.V4}, {hop}");
        Assert.Equal(trusted ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode);
        var (_, list) = await host.GetListAsync(forwardedFor: Routable.V4);
        if (trusted)
        {
            Assert.Equal(Routable.V4, Assert.Single(list.Servers).Address);
        }
        else
        {
            Assert.Empty(list.Servers);
        }
    }

    [Fact]
    public async Task UntrustedIpv6PeerCannotSpoofAForwardedAddress()
    {
        await using var host = await StartIpv6Async(
            ("TrustedProxies", "fd53:26b5:89f8::/64"), ("AllowPrivateAddresses", "false"));
        using var response = await host.AnnounceAsync(Body(), forwardedFor: Routable.V4);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var (_, list) = await host.GetListAsync();
        Assert.Empty(list.Servers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("\"unterminated, fd53:26b5:89f8::1")]
    [InlineData("fd53:26b5:89f8::1")]
    public async Task Ipv6ProxyWithoutAClientAddressIsRefused(string? header)
    {
        await using var host = await StartIpv6Async(("TrustedProxies", "::1/128, fd53:26b5:89f8::/64"));
        using var response = await host.AnnounceAsync(Body(), forwardedFor: header);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Limiter.TrackedCount);
    }

    [Fact]
    public async Task HeaderFromADirectClientIsIgnored()
    {
        await using var host = await StartAsync();
        await host.ListAsync(Body(), forwardedFor: "203.0.113.50");

        var (_, list) = await host.GetListAsync();
        Assert.Equal("127.0.0.1", Assert.Single(list.Servers).Address);
    }

    [Fact]
    public async Task HeaderIsIgnoredWhenTheProxyIsNotTheConnectingAddress()
    {
        await using var host = await StartAsync(("TrustedProxies", "10.0.0.1, 192.168.0.0/16"));
        await host.ListAsync(Body(), forwardedFor: "203.0.113.50");

        var (_, list) = await host.GetListAsync();
        Assert.Equal("127.0.0.1", Assert.Single(list.Servers).Address);
    }

    [Fact]
    public async Task HeaderIsUsedFromATrustedProxy()
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.0/8"));
        await host.ListAsync(Body(), forwardedFor: "203.0.113.50");

        var (_, list) = await host.GetListAsync(forwardedFor: "198.51.100.1");
        Assert.Equal("203.0.113.50", Assert.Single(list.Servers).Address);
    }

    [Fact]
    public async Task SpoofedEntryLeftOfTheProxysOwnIsIgnored()
    {
        // The client sent "X-Forwarded-For: 198.51.100.66"; the proxy appended the address it really saw.
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1"));
        await host.ListAsync(Body(), forwardedFor: "198.51.100.66, 203.0.113.50");

        var (_, list) = await host.GetListAsync(forwardedFor: "198.51.100.1");
        Assert.Equal("203.0.113.50", Assert.Single(list.Servers).Address);
    }

    // Each header leaves the trust walk on a trusted proxy: nothing to read, nothing readable, an unmatched quote a
    // client sent in front of the proxy's own entry (which makes the whole value unreadable), or a chain made only of
    // trusted proxies. Private addresses are allowed here, so only the proxy rule can refuse them.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("not an address")]
    [InlineData("\"198.51.100.66, 203.0.113.50")]
    [InlineData("\"unterminated, 10.1.2.3")]
    [InlineData("10.1.2.3")]
    [InlineData("10.1.2.3, 10.1.9.9")]
    [InlineData("garbage, 10.1.2.3")]
    public async Task RequestThatResolvesToATrustedProxyIsRefused(string? header)
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1, 10.1.0.0/16"));
        var listed = await host.ListAsync(Body(port: 1), forwardedFor: "203.0.113.50");

        using (var announce = await host.AnnounceAsync(Body(port: 2), forwardedFor: header))
        {
            Assert.Equal(HttpStatusCode.BadRequest, announce.StatusCode);
            Assert.Contains("proxy", await announce.Content.ReadAsStringAsync(Cancel));
        }

        using (var refresh = await host.AnnounceAsync(Body(port: 1, token: listed.Token), forwardedFor: header))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        }

        using (var remove = await host.RemoveAsync(listed.Id, listed.Token, forwardedFor: header))
        {
            Assert.Equal(HttpStatusCode.BadRequest, remove.StatusCode);
        }

        using (var list = await host.RequestListAsync(forwardedFor: header))
        {
            Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);
        }

        // Nothing changed, and only the one real client holds rate-limit state.
        var (_, servers) = await host.GetListAsync(forwardedFor: "203.0.113.50");
        Assert.Equal("203.0.113.50", Assert.Single(servers.Servers).Address);
        Assert.Equal(1, host.Limiter.TrackedCount);
    }

    [Fact]
    public async Task HealthCheckIsAnsweredForTheProxyItself()
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1"));
        using var response = await host.Client.GetAsync("/healthz", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChainOfTrustedProxiesIsWalked()
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1, 10.1.0.0/16"));
        await host.ListAsync(Body(), forwardedFor: "203.0.113.50, 10.1.2.3");

        var (_, list) = await host.GetListAsync(forwardedFor: "198.51.100.1");
        Assert.Equal("203.0.113.50", Assert.Single(list.Servers).Address);
    }

    [Fact]
    public async Task Ipv6AndMappedAddressesAreShownInTheirNormalForm()
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1"));
        await host.ListAsync(Body(port: 1), forwardedFor: "2001:DB8:0:0::7");
        await host.ListAsync(Body(port: 2), forwardedFor: "::ffff:203.0.113.9");

        var (_, list) = await host.GetListAsync(forwardedFor: "198.51.100.1");
        Assert.Equal(["2001:db8::7", "203.0.113.9"], list.Servers.Select(s => s.Address).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ForwardedAddressDecidesTheRateLimitAndThePrivateCheck()
    {
        await using var host = await StartAsync(
            ("TrustedProxies", "127.0.0.1"), ("AllowPrivateAddresses", "false"), ("PostsPerMinute", "1"));

        using (var publicClient = await host.AnnounceAsync(Body(), forwardedFor: Routable.V4))
        {
            Assert.Equal(HttpStatusCode.Created, publicClient.StatusCode);
        }

        using (var privateClient = await host.AnnounceAsync(Body(), forwardedFor: "192.168.1.5"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, privateClient.StatusCode);
        }

        using (var documentation = await host.AnnounceAsync(Body(), forwardedFor: "203.0.113.1"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, documentation.StatusCode);
        }

        using var again = await host.AnnounceAsync(Body(port: 2), forwardedFor: Routable.V4);
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
    }

    [Fact]
    public async Task RoutableIpv6IsListedAndNat64IsRefused()
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1"), ("AllowPrivateAddresses", "false"));
        await host.ListAsync(Body(), forwardedFor: Routable.V6);

        using var nat64 = await host.AnnounceAsync(Body(), forwardedFor: "64:ff9b::808:808");
        Assert.Equal(HttpStatusCode.Forbidden, nat64.StatusCode);
        var (_, list) = await host.GetListAsync(forwardedFor: Routable.V4);
        Assert.Equal(Routable.V6, Assert.Single(list.Servers).Address);
    }
}

public sealed class AddressRuleTests
{
    [Theory]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("fe80::1%3", "fe80::1")]
    [InlineData("203.0.113.4", "203.0.113.4")]
    public void NormalizeGivesOneForm(string input, string expected) =>
        Assert.Equal(expected, AddressRules.Normalize(IPAddress.Parse(input)).ToString());

    // Every block of the IANA special-purpose registries the rule refuses: its first and last address are refused,
    // and its neighbours on either side are allowed (unless a neighbour is itself in a refused block or off the end).
    [Theory]
    // IPv4 special-purpose registry.
    [InlineData("0.0.0.0", false)]
    [InlineData("0.255.255.255", false)]
    [InlineData("1.0.0.0", true)]
    [InlineData("9.255.255.255", true)]
    [InlineData("10.0.0.0", false)]
    [InlineData("10.255.255.255", false)]
    [InlineData("11.0.0.0", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("100.64.0.0", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.0", true)]
    [InlineData("126.255.255.255", true)]
    [InlineData("127.0.0.0", false)]
    [InlineData("127.255.255.255", false)]
    [InlineData("128.0.0.0", true)]
    [InlineData("169.253.255.255", true)]
    [InlineData("169.254.0.0", false)]
    [InlineData("169.254.255.255", false)]
    [InlineData("169.255.0.0", true)]
    [InlineData("172.15.255.255", true)]
    [InlineData("172.16.0.0", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.0", true)]
    [InlineData("191.255.255.255", true)]
    [InlineData("192.0.0.0", false)]
    [InlineData("192.0.0.9", false)]
    [InlineData("192.0.0.255", false)]
    [InlineData("192.0.1.0", true)]
    [InlineData("192.0.1.255", true)]
    [InlineData("192.0.2.0", false)]
    [InlineData("192.0.2.255", false)]
    [InlineData("192.0.3.0", true)]
    [InlineData("192.31.195.255", true)]
    [InlineData("192.31.196.0", false)]
    [InlineData("192.31.196.255", false)]
    [InlineData("192.31.197.0", true)]
    [InlineData("192.52.192.255", true)]
    [InlineData("192.52.193.0", false)]
    [InlineData("192.52.193.255", false)]
    [InlineData("192.52.194.0", true)]
    [InlineData("192.88.98.255", true)]
    [InlineData("192.88.99.0", false)]
    [InlineData("192.88.99.255", false)]
    [InlineData("192.88.100.0", true)]
    [InlineData("192.167.255.255", true)]
    [InlineData("192.168.0.0", false)]
    [InlineData("192.168.255.255", false)]
    [InlineData("192.169.0.0", true)]
    [InlineData("192.175.47.255", true)]
    [InlineData("192.175.48.0", false)]
    [InlineData("192.175.48.255", false)]
    [InlineData("192.175.49.0", true)]
    [InlineData("198.17.255.255", true)]
    [InlineData("198.18.0.0", false)]
    [InlineData("198.19.255.255", false)]
    [InlineData("198.20.0.0", true)]
    [InlineData("198.51.99.255", true)]
    [InlineData("198.51.100.0", false)]
    [InlineData("198.51.100.255", false)]
    [InlineData("198.51.101.0", true)]
    [InlineData("203.0.112.255", true)]
    [InlineData("203.0.113.0", false)]
    [InlineData("203.0.113.255", false)]
    [InlineData("203.0.114.0", true)]
    [InlineData("223.255.255.255", true)]
    [InlineData("224.0.0.0", false)]
    [InlineData("239.255.255.255", false)]
    [InlineData("240.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    // IPv4 seen through IPv6 is judged as IPv4.
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:192.0.2.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    // IPv6: only global unicast, 2000::/3.
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("::8.8.8.8", false)]
    [InlineData("1fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2000::", true)]
    [InlineData("3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("4000::", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("ff02::1", false)]
    // Discard, and the NAT64 translation prefixes, with a public and a private IPv4 address inside.
    [InlineData("100::", false)]
    [InlineData("100::ffff:ffff:ffff:ffff", false)]
    [InlineData("64:ff9b::", false)]
    [InlineData("64:ff9b::808:808", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b::ffff:ffff", false)]
    [InlineData("64:ff9b:1::", false)]
    [InlineData("64:ff9b:1:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("64:ff9b:1::808:808", false)]
    // 2001::/23, IETF protocol assignments: Teredo (2001::/32), benchmarking (2001:2::/48), AMT, AS112, ORCHID.
    [InlineData("2000:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001::", false)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)]
    [InlineData("2001:2::", false)]
    [InlineData("2001:2:0:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2001:1ff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2001:200::", true)]
    // 2001:db8::/32 documentation.
    [InlineData("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db8::", false)]
    [InlineData("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2001:db9::", true)]
    // 2002::/16 6to4, refused even when the embedded IPv4 address is public.
    [InlineData("2001:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("2002::", false)]
    [InlineData("2002:808:808::1", false)]
    [InlineData("2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2003::", true)]
    // 2620:4f:8000::/48 AS112 direct delegation.
    [InlineData("2620:4f:7fff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("2620:4f:8000::", false)]
    [InlineData("2620:4f:8000:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2620:4f:8001::", true)]
    // 3fff::/20 documentation.
    [InlineData("3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("3fff::", false)]
    [InlineData("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("3fff:1000::", true)]
    // Ordinary routable addresses.
    [InlineData("8.8.8.8", true)]
    [InlineData(Routable.V4, true)]
    [InlineData(Routable.V6, true)]
    [InlineData("2a00:1450::1", true)]
    public void OnlyAddressesAGameServerCanHaveOnTheInternetArePublic(string input, bool expected) =>
        Assert.Equal(expected, AddressRules.IsPublic(IPAddress.Parse(input)));

    [Fact]
    public void Ipv6GroupsByPrefix()
    {
        Assert.Equal("2001:db8:1:2::/64", AddressRules.GroupKey(IPAddress.Parse("2001:db8:1:2:3:4:5:6"), 64));
        Assert.Equal("2001:db8::/48", AddressRules.GroupKey(IPAddress.Parse("2001:db8:0:ffff::1"), 48));
        Assert.Equal("192.0.2.1", AddressRules.GroupKey(IPAddress.Parse("::ffff:192.0.2.1"), 64));
    }

    [Fact]
    public void BadProxyListFailsAtStart()
    {
        var options = new ListingOptions { TrustedProxies = "10.0.0.0/8, not-an-address" };
        Assert.Throws<InvalidOperationException>(options.Validate);
        var negative = new ListingOptions { MaxEntries = 0 };
        Assert.Throws<InvalidOperationException>(negative.Validate);
    }

    [Fact]
    public void SanitiseKeepsOrdinaryText() =>
        Assert.Equal("Ceres Station #2 (EU)", Announcement.SanitizeName("Ceres Station #2 (EU)"));
}
