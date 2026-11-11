using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Prospect.Listing;

/// <summary>What became of an announcement.</summary>
public enum AnnounceOutcome
{
    /// <summary>A new entry was listed.</summary>
    Created,

    /// <summary>The caller's own entry was refreshed.</summary>
    Refreshed,

    /// <summary>The address and port are listed and no token came with the request.</summary>
    AlreadyListed,

    /// <summary>The address and port are listed and the token does not match.</summary>
    WrongToken,

    /// <summary>The address already holds as many entries as it may.</summary>
    AddressFull,

    /// <summary>The list holds as many entries as it may.</summary>
    ListFull,
}

/// <summary>What became of a removal.</summary>
public enum RemoveOutcome
{
    /// <summary>The entry is gone.</summary>
    Removed,

    /// <summary>No entry has that id (or it has expired).</summary>
    NotFound,

    /// <summary>The token does not match.</summary>
    WrongToken,
}

/// <summary>The answer to an announcement: the outcome and, when listed, the entry's id and token.</summary>
public readonly record struct AnnounceResult(AnnounceOutcome Outcome, string Id, string Token);

/// <summary>One entry as clients see it. Never carries the token.</summary>
public sealed record ListedServer(
    string Name, string Address, int Port, int Players, int MaxPlayers, bool Password, string Version);

/// <summary>
/// The in-memory list of game servers. One lock guards it: the list is small, every operation is short, and a single
/// lock keeps the limits exact under concurrent posts. Memory is bounded by <see cref="ListingOptions.MaxEntries"/>.
/// </summary>
public sealed class ListingStore(ListingOptions options, TimeProvider time)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Address, int Port), Entry> byEndpoint = [];
    private readonly Dictionary<string, int> perGroup = new(StringComparer.Ordinal);
    private readonly TimeSpan ttl = TimeSpan.FromSeconds(options.EntryTtlSeconds);

    /// <summary>Entries held, including expired ones not yet swept.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return byId.Count;
            }
        }
    }

    /// <summary>
    /// Lists a server at <paramref name="address"/>, or refreshes it when the same address and port are listed and the
    /// announcement carries their token. A token for an address and port that are not listed (an entry that expired)
    /// is ignored and a new entry is made, so a server that lost its listing gets back on with one request.
    /// </summary>
    public AnnounceResult Announce(Announcement announcement, IPAddress address)
    {
        address = AddressRules.Normalize(address);
        var addressText = address.ToString();
        var group = AddressRules.GroupKey(address, options.Ipv6PrefixLength);
        var now = time.GetUtcNow();

        lock (gate)
        {
            if (byEndpoint.TryGetValue((addressText, announcement.Port), out var existing))
            {
                if (IsExpired(existing, now))
                {
                    RemoveLocked(existing);
                }
                else if (announcement.Token is null)
                {
                    return new AnnounceResult(AnnounceOutcome.AlreadyListed, "", "");
                }
                else if (!TokenMatches(existing, announcement.Token))
                {
                    return new AnnounceResult(AnnounceOutcome.WrongToken, "", "");
                }
                else
                {
                    existing.Update(announcement, now);
                    return new AnnounceResult(AnnounceOutcome.Refreshed, existing.Id, announcement.Token);
                }
            }

            if (AtGroupLimit(group) || byId.Count >= options.MaxEntries)
            {
                // Entries past their time may still be waiting for the sweeper; they must not hold a slot.
                SweepLocked(now);
                if (AtGroupLimit(group))
                {
                    return new AnnounceResult(AnnounceOutcome.AddressFull, "", "");
                }

                if (byId.Count >= options.MaxEntries)
                {
                    return new AnnounceResult(AnnounceOutcome.ListFull, "", "");
                }
            }

            var id = NewSecret(16);
            while (byId.ContainsKey(id))
            {
                id = NewSecret(16);
            }

            var token = NewSecret(32);
            var entry = new Entry(id, HashToken(token), addressText, group, announcement, now);
            byId.Add(id, entry);
            byEndpoint.Add((addressText, announcement.Port), entry);
            perGroup[group] = perGroup.GetValueOrDefault(group) + 1;
            return new AnnounceResult(AnnounceOutcome.Created, id, token);
        }
    }

    /// <summary>Removes an entry by id when <paramref name="token"/> is its token, whatever address the request comes from.</summary>
    public RemoveOutcome Remove(string id, string token)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (!byId.TryGetValue(id, out var entry) || IsExpired(entry, now))
            {
                return RemoveOutcome.NotFound;
            }

            if (!TokenMatches(entry, token))
            {
                return RemoveOutcome.WrongToken;
            }

            RemoveLocked(entry);
            return RemoveOutcome.Removed;
        }
    }

    /// <summary>Live entries for one protocol, newest heartbeat first, at most <see cref="ListingOptions.MaxListResults"/>.</summary>
    public List<ListedServer> List(uint protocol)
    {
        var now = time.GetUtcNow();
        var matching = new List<(DateTimeOffset Seen, ListedServer Server)>();
        lock (gate)
        {
            foreach (var entry in byId.Values)
            {
                if (entry.Protocol == protocol && !IsExpired(entry, now))
                {
                    matching.Add((entry.LastSeen, entry.ToListed()));
                }
            }
        }

        matching.Sort(static (a, b) => b.Seen.CompareTo(a.Seen));
        var count = Math.Min(matching.Count, options.MaxListResults);
        var result = new List<ListedServer>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(matching[i].Server);
        }

        return result;
    }

    /// <summary>Drops every entry whose heartbeat is older than the time to live. Returns how many went.</summary>
    public int Sweep()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            return SweepLocked(now);
        }
    }

    /// <summary>True when the three indexes agree with each other; for tests that hammer the store concurrently.</summary>
    internal bool IsConsistent()
    {
        lock (gate)
        {
            if (byId.Count != byEndpoint.Count)
            {
                return false;
            }

            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in byId.Values)
            {
                if (!byEndpoint.TryGetValue((entry.Address, entry.Port), out var same) || !ReferenceEquals(same, entry))
                {
                    return false;
                }

                groups[entry.Group] = groups.GetValueOrDefault(entry.Group) + 1;
            }

            if (groups.Count != perGroup.Count)
            {
                return false;
            }

            foreach (var (group, count) in groups)
            {
                if (perGroup.GetValueOrDefault(group) != count || count > options.MaxEntriesPerAddress)
                {
                    return false;
                }
            }

            return byId.Count <= options.MaxEntries;
        }
    }

    private bool AtGroupLimit(string group) => perGroup.GetValueOrDefault(group) >= options.MaxEntriesPerAddress;

    private bool IsExpired(Entry entry, DateTimeOffset now) => now - entry.LastSeen > ttl;

    private int SweepLocked(DateTimeOffset now)
    {
        List<Entry>? expired = null;
        foreach (var entry in byId.Values)
        {
            if (IsExpired(entry, now))
            {
                (expired ??= []).Add(entry);
            }
        }

        if (expired is null)
        {
            return 0;
        }

        foreach (var entry in expired)
        {
            RemoveLocked(entry);
        }

        return expired.Count;
    }

    private void RemoveLocked(Entry entry)
    {
        byId.Remove(entry.Id);
        byEndpoint.Remove((entry.Address, entry.Port));
        var left = perGroup[entry.Group] - 1;
        if (left == 0)
        {
            perGroup.Remove(entry.Group);
        }
        else
        {
            perGroup[entry.Group] = left;
        }
    }

    // Tokens are kept only as hashes and compared hash to hash: the comparison takes the same time however many bytes
    // match, and a memory dump of the service holds no usable token.
    private static bool TokenMatches(Entry entry, string token) =>
        CryptographicOperations.FixedTimeEquals(entry.TokenHash, HashToken(token));

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string NewSecret(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    private sealed class Entry(
        string id, byte[] tokenHash, string address, string group, Announcement announcement, DateTimeOffset now)
    {
        public string Id { get; } = id;
        public byte[] TokenHash { get; } = tokenHash;
        public string Address { get; } = address;
        public string Group { get; } = group;
        public int Port { get; } = announcement.Port;
        public uint Protocol { get; private set; } = announcement.Protocol;
        public DateTimeOffset LastSeen { get; private set; } = now;
        private Announcement current = announcement with { Token = null };

        public void Update(Announcement announcement, DateTimeOffset now)
        {
            current = announcement with { Token = null };
            Protocol = announcement.Protocol;
            LastSeen = now;
        }

        public ListedServer ToListed() => new(
            current.Name, Address, Port, current.Players, current.MaxPlayers, current.Password, current.Version);
    }
}
