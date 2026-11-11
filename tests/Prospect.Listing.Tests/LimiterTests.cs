using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;
using static Prospect.Listing.Tests.ListingHost;

namespace Prospect.Listing.Tests;

public sealed class RequestLimiterTests
{
    private static (RequestLimiter Limiter, ManualClock Clock) Make(
        int postsPerMinute = 20,
        int listsPerMinute = 60,
        int global = 1_000_000,
        int addresses = 16384,
        int overflow = 64)
    {
        var options = new ListingOptions
        {
            PostsPerMinute = postsPerMinute,
            ListsPerMinute = listsPerMinute,
            GlobalRequestsPerSecond = global,
            RateLimitAddresses = addresses,
            RateLimitOverflowBuckets = overflow,
        };
        options.Validate();
        var clock = new ManualClock();
        return (new RequestLimiter(options, clock), clock);
    }

    private static int Burst(RequestLimiter limiter, string group, RequestKind kind = RequestKind.Post)
    {
        var allowed = 0;
        while (limiter.TryAcquire(group, kind).Allowed)
        {
            allowed++;
            Assert.True(allowed < 1_000_000);
        }

        return allowed;
    }

    [Fact]
    public void SustainedRateEqualsTheConfiguredRate()
    {
        var (limiter, clock) = Make(postsPerMinute: 20);
        Assert.Equal(20, Burst(limiter, "a"));

        // Ten minutes of asking ten times a second, after the burst: exactly 20 a minute get through, one every 3 s.
        var allowed = 0;
        for (var step = 0; step < 6000; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            if (limiter.TryAcquire("a", RequestKind.Post).Allowed)
            {
                allowed++;
                Assert.Equal(0, (step + 1) % 30);
            }
        }

        Assert.Equal(200, allowed);
    }

    [Fact]
    public void OnePerMinuteRefillsAfterAWholeMinute()
    {
        var (limiter, clock) = Make(postsPerMinute: 1);
        Assert.True(limiter.TryAcquire("a", RequestKind.Post).Allowed);

        clock.Advance(TimeSpan.FromSeconds(59.9));
        var refused = limiter.TryAcquire("a", RequestKind.Post);
        Assert.False(refused.Allowed);
        Assert.False(refused.Global);
        Assert.Equal(0.1, refused.RetryAfter.TotalSeconds, 6);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(limiter.TryAcquire("a", RequestKind.Post).Allowed);
        Assert.False(limiter.TryAcquire("a", RequestKind.Post).Allowed);
    }

    [Fact]
    public void BurstRefillsCompletelyAfterAMinute()
    {
        var (limiter, clock) = Make(postsPerMinute: 20);
        Assert.Equal(20, Burst(limiter, "a"));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(20, Burst(limiter, "a"));
    }

    [Fact]
    public void PostsAndListsAreSeparateAllowances()
    {
        var (limiter, _) = Make(postsPerMinute: 2, listsPerMinute: 3);
        Assert.Equal(2, Burst(limiter, "a", RequestKind.Post));
        Assert.Equal(3, Burst(limiter, "a", RequestKind.List));
    }

    [Fact]
    public void StateStopsGrowingAtTheCapAndKnownAddressesKeepTheirLimits()
    {
        var (limiter, clock) = Make(postsPerMinute: 4, addresses: 8, overflow: 2);
        var index = limiter.IndexCapacity;
        for (var k = 0; k < 8; k++)
        {
            Assert.Equal(4, Burst(limiter, $"known-{k}"));
        }

        Assert.Equal(8, limiter.TrackedCount);

        // Twenty seconds of a flood from 20,000 new addresses. No known slot is free to take: each has spent its burst,
        // which takes a minute to refill.
        for (var i = 0; i < 20_000; i++)
        {
            limiter.TryAcquire($"flood-{i}", RequestKind.Post);
            if (i % 1000 == 999)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        Assert.Equal(8, limiter.TrackedCount);
        Assert.Equal(index, limiter.IndexCapacity);

        // Each known address still has its own state: 20 s refilled one token (one every 15 s), not a fresh burst of 4.
        for (var k = 0; k < 8; k++)
        {
            Assert.Equal(1, Burst(limiter, $"known-{k}"));
        }
    }

    [Fact]
    public void NewAddressesShareTheOverflowBucketsWhenEverySlotIsTaken()
    {
        var (limiter, _) = Make(postsPerMinute: 2, addresses: 1, overflow: 1);
        Assert.True(limiter.TryAcquire("known", RequestKind.Post).Allowed);

        // One overflow bucket of two, shared by every newcomer.
        Assert.True(limiter.TryAcquire("new-1", RequestKind.Post).Allowed);
        Assert.True(limiter.TryAcquire("new-2", RequestKind.Post).Allowed);
        Assert.False(limiter.TryAcquire("new-3", RequestKind.Post).Allowed);
        Assert.False(limiter.TryAcquire("new-1", RequestKind.Post).Allowed);

        // The known address is untouched by the newcomers.
        Assert.True(limiter.TryAcquire("known", RequestKind.Post).Allowed);
        Assert.False(limiter.TryAcquire("known", RequestKind.Post).Allowed);
    }

    [Fact]
    public void MovingOutOfOverflowDoesNotGrantASecondBurst()
    {
        // One slot, one overflow bucket, two posts a minute. "a" holds the slot; "b" spends its two from overflow.
        var (limiter, clock) = Make(postsPerMinute: 2, addresses: 1, overflow: 1);
        Assert.True(limiter.TryAcquire("a", RequestKind.Post).Allowed);
        Assert.Equal(2, Burst(limiter, "b"));

        // Half a minute on, "a" has refilled and its slot goes to "b". Half a minute refills one post, not two.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Burst(limiter, "b"));
        Assert.Equal(1, limiter.TrackedCount);
    }

    [Theory]
    [InlineData("::ffff:10.1.2.3/128", "10.1.2.3", 32)]
    [InlineData("::ffff:10.1.0.0/112", "10.1.0.0", 16)]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3", 32)]
    public void MappedProxyNetworksAreKeptAsTheIpv4NetworksTheyMean(string configured, string address, int prefix)
    {
        var options = new ListingOptions { TrustedProxies = configured };
        options.Validate();
        var network = Assert.Single(options.TrustedNetworks);
        Assert.Equal(System.Net.IPAddress.Parse(address), network.BaseAddress);
        Assert.Equal(prefix, network.PrefixLength);
        Assert.True(network.Contains(System.Net.IPAddress.Parse(address)));
    }

    [Fact]
    public void TheDefaultSlotCountCannotAllBeHeldAtTheDefaultRate()
    {
        var options = new ListingOptions();
        options.Validate();
        Assert.True(options.RateLimitAddresses >= options.SlotsThatCannotAllBeHeld);
    }

    [Fact]
    public void AProxyNetworkThatMixesMappedAndOtherIpv6StopsTheService()
    {
        var options = new ListingOptions { TrustedProxies = "::ffff:0.0.0.0/64" };
        Assert.Contains("TrustedProxies", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void SlotIsFreedOnlyOnceItsBucketsAreFull()
    {
        var (limiter, clock) = Make(postsPerMinute: 2, listsPerMinute: 60, addresses: 1, overflow: 1);
        Assert.True(limiter.TryAcquire("old", RequestKind.Post).Allowed);

        // 29 s later the old address's post bucket is not yet full (one token takes 30 s), so it keeps its slot, and the
        // newcomer is limited by the overflow bucket.
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(limiter.TryAcquire("new", RequestKind.Post).Allowed);
        Assert.Equal(1, limiter.TrackedCount);
        Assert.True(limiter.TryAcquire("old", RequestKind.Post).Allowed);
        Assert.False(limiter.TryAcquire("old", RequestKind.Post).Allowed);

        // Once it is full again its state is the same as a new address's, and the slot goes to the next newcomer.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, Burst(limiter, "newer"));
        Assert.Equal(1, limiter.TrackedCount);
        Assert.Equal(2, Burst(limiter, "old"));
    }

    [Fact]
    public void GlobalLimitIsCheckedFirstButSpentOnlyByAllowedRequests()
    {
        var (limiter, clock) = Make(postsPerMinute: 1, global: 3, addresses: 16);

        // One address hammering is stopped by its own limit and uses one unit of the global allowance.
        Assert.True(limiter.TryAcquire("a", RequestKind.Post).Allowed);
        for (var i = 0; i < 100; i++)
        {
            var refused = limiter.TryAcquire("a", RequestKind.Post);
            Assert.False(refused.Allowed);
            Assert.False(refused.Global);
        }

        Assert.True(limiter.TryAcquire("b", RequestKind.Post).Allowed);
        Assert.True(limiter.TryAcquire("c", RequestKind.Post).Allowed);

        // The service's burst is spent: the next address is refused before it gets any state of its own.
        var busy = limiter.TryAcquire("d", RequestKind.Post);
        Assert.False(busy.Allowed);
        Assert.True(busy.Global);
        Assert.Equal(3, limiter.TrackedCount);

        // A third of a second later one more request may go.
        clock.Advance(TimeSpan.FromSeconds(1.0 / 3) + TimeSpan.FromTicks(1));
        Assert.True(limiter.TryAcquire("d", RequestKind.Post).Allowed);
        Assert.False(limiter.TryAcquire("e", RequestKind.Post).Allowed);
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public async Task PostAllowanceRefillsAtTheConfiguredRate()
    {
        await using var host = await StartAsync(("PostsPerMinute", "3"));
        for (var port = 1; port <= 3; port++)
        {
            await host.ListAsync(Body(port: port));
        }

        using (var limited = await host.AnnounceAsync(Body(port: 4)))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(20), limited.Headers.RetryAfter?.Delta);
        }

        host.Clock.Advance(TimeSpan.FromSeconds(19.9));
        using (var early = await host.AnnounceAsync(Body(port: 4)))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, early.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), early.Headers.RetryAfter?.Delta);
        }

        host.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await host.ListAsync(Body(port: 4));
        using var again = await host.AnnounceAsync(Body(port: 5));
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
    }

    [Fact]
    public async Task ServiceWideLimitRefusesEveryoneOnceSpent()
    {
        await using var host = await StartAsync(("GlobalRequestsPerSecond", "2"), ("TrustedProxies", "127.0.0.1"));
        await host.GetListAsync(forwardedFor: "198.51.100.1");
        await host.GetListAsync(forwardedFor: "198.51.100.2");

        using var busy = await host.RequestListAsync(forwardedFor: "198.51.100.3");
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Contains("busy", await busy.Content.ReadAsStringAsync(Cancel));
        Assert.NotNull(busy.Headers.RetryAfter);
        Assert.Equal(2, host.Limiter.TrackedCount);

        // The health check is never limited.
        using var health = await host.Client.GetAsync("/healthz", Cancel);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task RateLimitStateIsCappedUnderAFloodOfAddresses()
    {
        await using var host = await StartAsync(
            ("TrustedProxies", "127.0.0.1"),
            ("RateLimitAddresses", "16"),
            ("RateLimitOverflowBuckets", "2"),
            ("ListsPerMinute", "2"));
        var index = host.Limiter.IndexCapacity;

        // A client that was here first.
        await host.GetListAsync(forwardedFor: "198.51.100.1");

        // Requests from 200 IPv6 /64 prefixes inside one /48.
        for (var i = 0; i < 200; i++)
        {
            using var response = await host.RequestListAsync(forwardedFor: $"2001:db8:1:{i:x}::1");
        }

        Assert.Equal(16, host.Limiter.TrackedCount);
        Assert.Equal(index, host.Limiter.IndexCapacity);

        // The first client still has exactly its own allowance: one more, then refused.
        await host.GetListAsync(forwardedFor: "198.51.100.1");
        using var limited = await host.RequestListAsync(forwardedFor: "198.51.100.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}

public sealed class FrameworkSwitchTests
{
    private static WebApplication BuildWith(string key, string value) =>
        ListingApp.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });

    // ASPNETCORE_FORWARDEDHEADERS_ENABLED reaches configuration as this key. Left on, the framework would rewrite every
    // client address from X-Forwarded-For before TrustedProxies is consulted.
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    public void ForwardedHeadersSwitchStopsTheService(string value)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => BuildWith("ForwardedHeaders_Enabled", value));
        Assert.Contains("ForwardedHeaders_Enabled", refused.Message);
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", refused.Message);
    }

    [Fact]
    public async Task ForwardedHeadersSwitchSetToFalseIsAccepted()
    {
        await using var app = BuildWith("ForwardedHeaders_Enabled", "false");
    }

    [Fact]
    public void HostingStartupAssembliesStopTheService()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => BuildWith("hostingStartupAssemblies", "Some.Assembly"));
        Assert.Contains("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", refused.Message);
    }
}
