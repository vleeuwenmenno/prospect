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

    /// <summary>
    /// False for addresses that cannot be reached across the internet: unspecified, loopback, private, carrier-grade
    /// NAT, link-local, unique-local, multicast and reserved ranges.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            return !(b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && (b[1] & 0xC0) == 64)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && (b[1] & 0xFE) == 18)
                || b[0] >= 224);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        return !(address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || address.IsIPv6Multicast);
    }
}
