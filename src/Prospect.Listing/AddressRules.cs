using System.Net;
using System.Net.Sockets;

namespace Prospect.Listing;

/// <summary>How the listing server reads, groups and judges the address a request came from.</summary>
public static class AddressRules
{
    /// <summary>
    /// One form per address. An IPv4 client reaching a dual-stack socket shows up as <c>::ffff:a.b.c.d</c>; it is
    /// turned back into plain IPv4 so the list shows <c>a.b.c.d</c>, the limits count it once, and a game client can
    /// reach it over IPv4. IPv6 scope ids mean nothing off the host and are dropped.
    /// </summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }

    /// <summary>
    /// The key the per-address limits count by: the IPv4 address itself, or the IPv6 network of
    /// <paramref name="ipv6PrefixLength"/> bits it sits in, since one host usually holds a whole IPv6 prefix.
    /// </summary>
    public static string GroupKey(IPAddress address, int ipv6PrefixLength)
    {
        address = Normalize(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || ipv6PrefixLength >= 128)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        for (var bit = ipv6PrefixLength; bit < 128; bit++)
        {
            bytes[bit >> 3] &= (byte)~(0x80 >> (bit & 7));
        }

        return $"{new IPAddress(bytes)}/{ipv6PrefixLength}";
    }

    // The IANA IPv4 special-purpose address registry, every block in it. Some are marked globally reachable there
    // (192.0.0.9, 192.31.196.0/24, 192.52.193.0/24, 192.175.48.0/24) but are anycast services, not game servers.
    private static readonly IPNetwork[] Ipv4Refused =
    [
        IPNetwork.Parse("0.0.0.0/8"), // "this network"
        IPNetwork.Parse("10.0.0.0/8"), // private
        IPNetwork.Parse("100.64.0.0/10"), // carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"), // loopback
        IPNetwork.Parse("169.254.0.0/16"), // link-local
        IPNetwork.Parse("172.16.0.0/12"), // private
        IPNetwork.Parse("192.0.0.0/24"), // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"), // documentation (TEST-NET-1)
        IPNetwork.Parse("192.31.196.0/24"), // AS112
        IPNetwork.Parse("192.52.193.0/24"), // AMT
        IPNetwork.Parse("192.88.99.0/24"), // deprecated 6to4 relay anycast
        IPNetwork.Parse("192.168.0.0/16"), // private
        IPNetwork.Parse("192.175.48.0/24"), // AS112 direct delegation
        IPNetwork.Parse("198.18.0.0/15"), // benchmarking
        IPNetwork.Parse("198.51.100.0/24"), // documentation (TEST-NET-2)
        IPNetwork.Parse("203.0.113.0/24"), // documentation (TEST-NET-3)
        IPNetwork.Parse("224.0.0.0/3"), // multicast, reserved and limited broadcast: everything from 224.0.0.0 up
    ];

    // IPv6 is an allow rule: only global unicast can be a game server on the internet. That alone refuses loopback,
    // link-local, unique-local, multicast, discard (100::/64), IPv4-mapped and -compatible forms and the NAT64
    // prefixes (64:ff9b::/96, 64:ff9b:1::/48), whose addresses only mean something inside the operator's network.
    private static readonly IPNetwork Ipv6GlobalUnicast = IPNetwork.Parse("2000::/3");

    // The blocks of the IANA IPv6 special-purpose registry that sit inside global unicast. 6to4 and Teredo are
    // refused outright rather than judged by the IPv4 address they embed: both are deprecated tunnels, and a player's
    // client would have to reach the server through a public relay that may not exist.
    private static readonly IPNetwork[] Ipv6Refused =
    [
        IPNetwork.Parse("2001::/23"), // IETF protocol assignments: Teredo, benchmarking, AMT, AS112, ORCHID, DRIP
        IPNetwork.Parse("2001:db8::/32"), // documentation
        IPNetwork.Parse("2002::/16"), // 6to4
        IPNetwork.Parse("2620:4f:8000::/48"), // AS112 direct delegation
        IPNetwork.Parse("3fff::/20"), // documentation
    ];

    /// <summary>
    /// True only for an address a game server on the internet can have: IPv4 outside every block of the IANA IPv4
    /// special-purpose registry, or IPv6 global unicast (2000::/3) outside the special-purpose blocks within it.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !AnyContains(Ipv4Refused, address);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && Ipv6GlobalUnicast.Contains(address)
            && !AnyContains(Ipv6Refused, address);
    }

    private static bool AnyContains(IPNetwork[] networks, IPAddress address)
    {
        foreach (var network in networks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
