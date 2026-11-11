using System.Buffers.Binary;
using System.Net;

namespace Prospect.Listing;

/// <summary>
/// Every tunable number of the listing server, bound from the <c>Listing</c> configuration section
/// (<c>appsettings.json</c>, or environment variables such as <c>Listing__MaxEntries=500</c>).
/// </summary>
public sealed class ListingOptions
{
    /// <summary>The configuration section the options are read from.</summary>
    public const string Section = "Listing";

    /// <summary>Seconds an entry stays listed without a heartbeat. Game servers heartbeat every 30.</summary>
    public int EntryTtlSeconds { get; set; } = 90;

    /// <summary>Milliseconds between sweeps that drop expired entries, so expiry never waits for a request.</summary>
    public int SweepIntervalMilliseconds { get; set; } = 5000;

    /// <summary>Entries the whole list may hold. Announcements past it are refused until entries expire.</summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>Entries one address may hold. An IPv6 address counts by its prefix (see <see cref="Ipv6PrefixLength"/>).</summary>
    public int MaxEntriesPerAddress { get; set; } = 4;

    /// <summary>Most entries one list request returns, newest heartbeat first.</summary>
    public int MaxListResults { get; set; } = 200;

    /// <summary>Largest request body accepted, in bytes. An announcement is well under 1 KB.</summary>
    public int MaxBodyBytes { get; set; } = 4096;

    /// <summary>
    /// Largest request line, and separately largest total size of the request's headers, in bytes. Kestrel checks the
    /// two apart, so a request may carry up to twice this in all.
    /// </summary>
    public int MaxHeaderBytes { get; set; } = 8192;

    /// <summary>Open connections the server accepts at once.</summary>
    public int MaxConnections { get; set; } = 1000;

    /// <summary>Seconds a client has to send its request headers before the connection is closed.</summary>
    public int RequestHeadersTimeoutSeconds { get; set; } = 10;

    /// <summary>Announcements and removals one address may make per minute.</summary>
    public int PostsPerMinute { get; set; } = 20;

    /// <summary>List requests one address may make per minute.</summary>
    public int ListsPerMinute { get; set; } = 60;

    /// <summary>
    /// Requests per second the whole service accepts, from all addresses together: a burst of this many, then this
    /// many per second. A request refused by its own address's limit does not use any of it.
    /// </summary>
    public int GlobalRequestsPerSecond { get; set; } = 200;

    /// <summary>
    /// Addresses (or IPv6 prefixes) that get rate-limit state of their own. The memory is reserved at start and never
    /// grows. Past it, a new address shares one of the <see cref="RateLimitOverflowBuckets"/> until a slot frees up.
    /// </summary>
    public int RateLimitAddresses { get; set; } = 16384;

    /// <summary>Shared buckets for addresses that arrive while every slot of <see cref="RateLimitAddresses"/> is taken.</summary>
    public int RateLimitOverflowBuckets { get; set; } = 64;

    /// <summary>
    /// IPv6 addresses sharing this many leading bits count as one address for the limits. One home or server usually
    /// gets a whole /64, so limiting single IPv6 addresses would limit nothing.
    /// </summary>
    public int Ipv6PrefixLength { get; set; } = 64;

    /// <summary>
    /// Proxies whose <c>X-Forwarded-For</c> header is believed: addresses or networks (<c>10.0.0.0/8</c>), separated by
    /// commas. Empty trusts none, and every request's address is the one it connected from. A request that still
    /// resolves to one of these after the header is read is refused: a trusted proxy is never a client.
    /// </summary>
    public string TrustedProxies { get; set; } = "";

    /// <summary>
    /// Lets loopback, private and link-local addresses announce. Off on the internet, where such an address in the
    /// list would send players' clients to machines on their own network; on for running on one machine or a LAN.
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }

    /// <summary>
    /// The fewest rate-limit slots that cannot all be held at once: the burst plus a minute of requests at the
    /// service-wide rate, a minute being the longest a slot stays taken after its address's last request.
    /// </summary>
    public long SlotsThatCannotAllBeHeld => (long)GlobalRequestsPerSecond * 61;

    /// <summary>The parsed <see cref="TrustedProxies"/>; filled by <see cref="Validate"/>.</summary>
    public IReadOnlyList<IPNetwork> TrustedNetworks { get; private set; } = [];

    /// <summary>Checks every value and parses the proxy list. Throws with the offending key so a bad deployment fails at start.</summary>
    public void Validate()
    {
        Require(EntryTtlSeconds, 5, 3600, nameof(EntryTtlSeconds));
        Require(SweepIntervalMilliseconds, 10, 600_000, nameof(SweepIntervalMilliseconds));
        Require(MaxEntries, 1, 1_000_000, nameof(MaxEntries));
        Require(MaxEntriesPerAddress, 1, 1_000_000, nameof(MaxEntriesPerAddress));
        Require(MaxListResults, 1, 100_000, nameof(MaxListResults));
        Require(MaxBodyBytes, 256, 1 << 20, nameof(MaxBodyBytes));
        Require(MaxHeaderBytes, 1024, 1 << 20, nameof(MaxHeaderBytes));
        Require(MaxConnections, 1, 1_000_000, nameof(MaxConnections));
        Require(RequestHeadersTimeoutSeconds, 1, 600, nameof(RequestHeadersTimeoutSeconds));
        Require(PostsPerMinute, 1, 1_000_000, nameof(PostsPerMinute));
        Require(ListsPerMinute, 1, 1_000_000, nameof(ListsPerMinute));
        Require(GlobalRequestsPerSecond, 1, 1_000_000, nameof(GlobalRequestsPerSecond));
        Require(RateLimitAddresses, 1, 1_000_000, nameof(RateLimitAddresses));
        Require(RateLimitOverflowBuckets, 1, 65_536, nameof(RateLimitOverflowBuckets));
        Require(Ipv6PrefixLength, 16, 128, nameof(Ipv6PrefixLength));
        TrustedNetworks = ParseNetworks(TrustedProxies);
    }

    private static void Require(int value, int min, int max, string key)
    {
        if (value < min || value > max)
        {
            throw new InvalidOperationException($"{Section}:{key} is {value}; it must be between {min} and {max}.");
        }
    }

    private static readonly IPAddress MappedIpv4Start = IPAddress.Parse("::ffff:0.0.0.0");

    private static bool OverlapsMappedIpv4(IPNetwork network)
    {
        // Contains takes an IPv4 comparison path for mapped probes on .NET 10, even against an IPv6 network.
        // Compare the actual prefix bits instead; /0 needs its own mask because a 128-bit shift wraps to zero.
        var mask = network.PrefixLength == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - network.PrefixLength);
        var start = BinaryPrimitives.ReadUInt128BigEndian(MappedIpv4Start.GetAddressBytes());
        var address = BinaryPrimitives.ReadUInt128BigEndian(network.BaseAddress.GetAddressBytes());
        return network.PrefixLength < 96 && address == (start & mask);
    }

    private static IPNetwork[] ParseNetworks(string list)
    {
        var parts = list.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var networks = new IPNetwork[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Contains('/'))
            {
                if (!IPNetwork.TryParse(part, out networks[i]))
                {
                    throw new InvalidOperationException($"{Section}:{nameof(TrustedProxies)} has '{part}', which is not a network.");
                }

                // An IPv4 network written in its IPv6-mapped form would never match a plain IPv4 peer, and the
                // proxy would then be taken for a client. It is kept as the IPv4 network it means.
                var network = networks[i];
                if (network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96)
                {
                    networks[i] = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
                }
                else if (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && OverlapsMappedIpv4(network))
                {
                    // Wider than the mapped range: it reads as covering IPv4 proxies and never would.
                    throw new InvalidOperationException($"{Section}:{nameof(TrustedProxies)} has '{part}', which mixes IPv4-mapped and other IPv6 addresses; write the IPv4 network and the IPv6 network separately.");
                }
            }
            else if (IPAddress.TryParse(part, out var address))
            {
                address = AddressRules.Normalize(address);
                networks[i] = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            }
            else
            {
                throw new InvalidOperationException($"{Section}:{nameof(TrustedProxies)} has '{part}', which is not an address.");
            }
        }

        return networks;
    }
}
