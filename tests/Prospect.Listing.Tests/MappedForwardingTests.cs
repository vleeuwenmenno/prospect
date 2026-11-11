using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;
using static Prospect.Listing.Tests.ListingHost;

namespace Prospect.Listing.Tests;

public sealed class MappedForwardingTests
{
    [Theory]
    [InlineData("8.8.8.8, ::ffff:1.1.1.1", "1.1.1.1")]
    [InlineData("8.8.8.8, 1.1.1.1", "1.1.1.1")]
    [InlineData("::ffff:1.1.1.1", "1.1.1.1")]
    [InlineData("8.8.8.8, \"[::ffff:1.1.1.1]:1234\"", "1.1.1.1")]
    [InlineData("8.8.8.8, [::ffff:1.1.1.1]:1234", "1.1.1.1")]
    [InlineData("8.8.8.8, \"[2a00:1450::1]:1234\"", "2a00:1450::1")]
    public async Task MappedClientStopsTheTrustWalk(string header, string expected)
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1, fc00::/7"), ("AllowPrivateAddresses", "false"));
        await host.ListAsync(Body(), forwardedFor: header);
        var (_, list) = await host.GetListAsync(forwardedFor: "1.1.1.1");
        Assert.Equal(expected, Assert.Single(list.Servers).Address);
    }

    [Theory]
    [InlineData("fd53:26b5:89f8::/64")]
    [InlineData("fd53:26b5:89f8::/128")]
    public async Task MappedZeroIsNotAnIpv6Proxy(string network)
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1, " + network));
        await host.ListAsync(Body(), forwardedFor: "8.8.8.8, ::ffff:0.0.0.0");
        var (_, list) = await host.GetListAsync(forwardedFor: "1.1.1.1");
        Assert.Equal("0.0.0.0", Assert.Single(list.Servers).Address);
    }

    [Theory]
    [InlineData("fd53:26b5::/33")]
    [InlineData("")]
    public async Task UntrustedMappedSocketCannotForgeAnAddress(string proxies)
    {
        await using var host = await StartMappedAsync(("TrustedProxies", proxies));
        await host.ListAsync(Body(), forwardedFor: "8.8.8.8");
        var (_, list) = await host.GetListAsync(forwardedFor: "8.8.8.8");
        Assert.Equal("127.0.0.1", Assert.Single(list.Servers).Address);
    }

    [Fact]
    public async Task MappedTrustedIpv4ProxiesStillForward()
    {
        await using var host = await StartMappedAsync(
            ("TrustedProxies", "::ffff:127.0.0.1/128, ::ffff:10.1.0.0/112, fc00::/7"),
            ("AllowPrivateAddresses", "false"));
        await host.ListAsync(Body(), forwardedFor: "8.8.8.8, ::ffff:1.1.1.1, [::ffff:10.1.2.3]:1234");
        var (_, list) = await host.GetListAsync(forwardedFor: "1.1.1.1");
        Assert.Equal("1.1.1.1", Assert.Single(list.Servers).Address);
    }

    [Theory]
    [InlineData("::ffff:1.1.1.1", "1.1.1.1")]
    [InlineData("1.1.1.1", "::ffff:1.1.1.1")]
    public async Task PlainAndMappedClientsShareOneRateLimit(string first, string second)
    {
        await using var host = await StartAsync(
            ("TrustedProxies", "127.0.0.1, fc00::/7"), ("PostsPerMinute", "1"));
        await host.ListAsync(Body(), forwardedFor: first);
        using var response = await host.AnnounceAsync(Body(port: 2), forwardedFor: second);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, host.Limiter.TrackedCount);
    }

    [Theory]
    [InlineData("\"8.8.8.8, 10.1.2.3\", ::ffff:10.1.2.3", null)]
    [InlineData("::ffff:8.8.8.8, \"1.1.1.1, 10.1.2.3\"", null)]
    [InlineData("\"8.8.8.8, ::ffff:10.1.2.3\"", null)]
    [InlineData("garbage, ::ffff:10.1.2.3", null)]
    [InlineData("::ffff:1.1.1.1, garbage", null)]
    [InlineData("\"unterminated, ::ffff:10.1.2.3", null)]
    [InlineData("::ffff:10.1.2.3, \"unterminated, 8.8.8.8", null)]
    [InlineData("\"\"8.8.8.8, 10.1.2.3\"\", ::ffff:10.1.2.3", null)]
    [InlineData("garbage, ::ffff:1.1.1.1", "1.1.1.1")]
    [InlineData("\"8.8.8.8, 10.1.2.3\", ::ffff:1.1.1.1", "1.1.1.1")]
    [InlineData("\"[::ffff:1.1.1.1]:1234\", ::ffff:10.1.2.3", "1.1.1.1")]
    public async Task InvalidValuesNeverBecomeExtraHops(string header, string? expected)
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1, 10.1.0.0/16, fc00::/7"));
        using var response = await host.AnnounceAsync(Body(), forwardedFor: header);
        Assert.Equal(expected is null ? HttpStatusCode.BadRequest : HttpStatusCode.Created, response.StatusCode);
        if (expected is null)
        {
            Assert.Equal(0, host.Limiter.TrackedCount);
        }
        else
        {
            var (_, list) = await host.GetListAsync(forwardedFor: "1.1.1.1");
            Assert.Equal(expected, Assert.Single(list.Servers).Address);
        }
    }

    [Theory]
    [InlineData("8.8.8.8", "::ffff:1.1.1.1", "1.1.1.1")]
    [InlineData("\"8.8.8.8, 10.1.2.3\"", "::ffff:10.1.2.3", null)]
    [InlineData("\"unterminated, 8.8.8.8", "::ffff:10.1.2.3", null)]
    public async Task SeparateHeaderLinesKeepTheirValueBoundaries(string first, string second, string? expected)
    {
        await using var host = await StartAsync(("TrustedProxies", "127.0.0.1, 10.1.0.0/16, fc00::/7"));
        // HttpClient may join values onto one line, so send the two header lines explicitly over the wire.
        using var socket = new TcpClient();
        await socket.ConnectAsync("127.0.0.1", host.Client.BaseAddress!.Port, Cancel);
        await using var stream = socket.GetStream();
        var body = JsonSerializer.Serialize(Body());
        var request = $"POST /v1/servers HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n"
            + $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
            + $"X-Forwarded-For: {first}\r\nX-Forwarded-For: {second}\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), Cancel);
        using var reader = new StreamReader(stream);
        var response = await reader.ReadToEndAsync(Cancel);
        Assert.StartsWith(expected is null ? "HTTP/1.1 400 " : "HTTP/1.1 201 ", response);
        if (expected is not null)
        {
            var (_, list) = await host.GetListAsync(forwardedFor: "1.1.1.1");
            Assert.Equal(expected, Assert.Single(list.Servers).Address);
        }
    }
}
