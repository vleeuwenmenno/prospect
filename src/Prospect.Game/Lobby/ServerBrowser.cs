using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;

namespace Prospect.Game.Lobby;

/// <summary>One public game from the list, checked and cleaned. Nothing in it has been contacted.</summary>
/// <param name="Name">The server's name, cleaned by <see cref="ServerNames"/>.</param>
/// <param name="Address">The address as text, exactly as <see cref="IPAddress.ToString()"/> writes it. Never a host name.</param>
/// <param name="Port">The UDP port to join on.</param>
/// <param name="Players">Players there now, as the server last reported.</param>
/// <param name="MaxPlayers">Player slots.</param>
/// <param name="Password">True when joining takes a password.</param>
/// <param name="Version">The game version the server reports.</param>
public sealed record ListedGame(string Name, string Address, ushort Port, int Players, int MaxPlayers, bool Password, string Version);

/// <summary>What the browser has to show.</summary>
public enum BrowserState : byte
{
    /// <summary>Nothing has been fetched yet.</summary>
    Idle,

    /// <summary>A fetch is under way. The entries of the fetch before it, if any, are still there.</summary>
    Loading,

    /// <summary>The last fetch succeeded.</summary>
    Ready,

    /// <summary>The last fetch failed; <see cref="ServerList.Error"/> says why.</summary>
    Failed,

    /// <summary>There is no listing server: it was turned off.</summary>
    Off,
}

/// <summary>The result of one fetch. Never changes once made, so any thread may read it.</summary>
public sealed class ServerList
{
    internal static readonly ServerList Empty = new(BrowserState.Idle, [], "", 0, 0);

    internal ServerList(BrowserState state, ListedGame[] games, string error, int dropped, int sequence)
    {
        State = state;
        Games = games;
        Error = error;
        Dropped = dropped;
        Sequence = sequence;
    }

    /// <summary><see cref="BrowserState.Idle"/>, <see cref="BrowserState.Ready"/> or <see cref="BrowserState.Failed"/>.</summary>
    public BrowserState State { get; }

    /// <summary>The games, in the list's order: newest heartbeat first. After a failed fetch, those of the last good one.</summary>
    public ListedGame[] Games { get; }

    /// <summary>One sentence for the player when the fetch failed; empty otherwise.</summary>
    public string Error { get; }

    /// <summary>Entries the list had that were left out: broken, not joinable from this game, or beyond the cap.</summary>
    public int Dropped { get; }

    /// <summary>Counts fetches that finished, so a screen can tell a new result from the one it already showed.</summary>
    public int Sequence { get; }
}

/// <summary>
/// Fetches the list of public games for the server browser.
///
/// <see cref="Refresh"/> starts a fetch on the thread pool and returns at once. The result is handed over as one
/// immutable <see cref="ServerList"/> behind a volatile reference: the render thread reads <see cref="State"/>
/// and <see cref="List"/> every frame without a lock, without waiting and without allocating.
///
/// The list is untrusted (docs/listing-server.md). The fetch is bounded in time and size; the number of entries
/// and every length are capped again here; names are cleaned; an address must be a literal IP address in its
/// normal form, and a public one unless the listing server itself runs on this machine. Nothing is ever sent to
/// a listed game from here: that only happens when the player picks one and the game joins it.
/// </summary>
public sealed class ServerBrowser : IDisposable
{
    /// <summary>The most games shown. The service sends at most this many; more than that is cut off.</summary>
    public const int MaxGames = 200;

    /// <summary>
    /// The largest list read. 200 entries with the longest names, written with every character escaped, come
    /// to about 150 KB.
    /// </summary>
    public const int MaxListBytes = 256 * 1024;

    /// <summary>The longest raw name looked at; a real one is at most <see cref="ServerNames.MaxChars"/> units.</summary>
    private const int MaxRawNameLength = 256;
    private const int MaxAddressLength = 45;
    private const int MaxVersionLength = 32;

    private readonly ListingConfig? _config;
    private readonly ListingHttp? _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Task> _fetch;
    private ServerList _list = ServerList.Empty;
    private int _loading;
    private int _disposed;

    /// <param name="config">The listing server, or null when it is turned off.</param>
    public ServerBrowser(ListingConfig? config)
    {
        _config = config;
        _http = config is null ? null : new ListingHttp(config);
        _fetch = FetchAsync;
        if (config is null)
            _list = new ServerList(BrowserState.Off, [], "", 0, 0);
    }

    /// <summary>False when there is no listing server to ask.</summary>
    public bool Enabled => _config is not null;

    /// <summary>The listing server's host name, for display; empty when turned off.</summary>
    public string Host => _config?.Url.Host ?? "";

    /// <summary>What to show right now. Safe from any thread.</summary>
    public BrowserState State => Volatile.Read(ref _loading) != 0 ? BrowserState.Loading : Volatile.Read(ref _list).State;

    /// <summary>The result of the last fetch that finished. Safe from any thread; never blocks.</summary>
    public ServerList List => Volatile.Read(ref _list);

    /// <summary>
    /// Starts fetching the list. Returns at once: false when a fetch is already under way or there is no
    /// listing server. Call it when the browser opens and when the player asks.
    /// </summary>
    public bool Refresh()
    {
        if (_http is null || Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _loading, 1, 0) != 0)
            return false;
        Task.Run(_fetch);
        return true;
    }

    /// <summary>Abandons a fetch that is under way. Does not wait for it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _http?.Dispose();
    }

    private async Task FetchAsync()
    {
        var previous = Volatile.Read(ref _list);
        ServerList result;
        try
        {
            var reply = await _http!
                .SendAsync(HttpMethod.Get, $"v1/servers?protocol={ProtocolInfo.Version}", default, null, MaxListBytes, _stop.Token)
                .ConfigureAwait(false);
            result = reply.Outcome switch
            {
                ListingOutcome.Unreachable => Failure(previous, "The server list cannot be reached."),
                ListingOutcome.TimedOut => Failure(previous, "The server list did not answer in time."),
                ListingOutcome.TooLarge => Failure(previous, "The server list sent more than a list can be."),
                _ when reply.Status == 429 => Failure(previous, "The server list is busy. Try again in a moment."),
                _ when reply.Status != 200 => Failure(previous, "The server list is not available right now."),
                _ => Parse(reply.Body, _config!.IsLocal, previous.Sequence + 1) ?? Failure(previous, "The server list sent something that is not a list."),
            };
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            // Whatever went wrong, the browser shows a sentence and the game goes on.
            Logs.Net.Warning($"Server list: {e.GetType().Name}: {e.Message}");
            result = Failure(previous, "The server list could not be read.");
        }

        if (result.State == BrowserState.Failed)
            Logs.Net.Info($"Server list ({_config!.Url.Host}): {result.Error}");
        Volatile.Write(ref _list, result);
        Volatile.Write(ref _loading, 0);
    }

    private static ServerList Failure(ServerList previous, string error) =>
        new(BrowserState.Failed, previous.Games, error, 0, previous.Sequence + 1);

    /// <summary>
    /// Reads a list answer. Returns null when the answer as a whole is not a list; an entry that breaks any
    /// rule is left out and counted, and the rest stand.
    /// </summary>
    /// <param name="json">The answer's body.</param>
    /// <param name="allowPrivateAddresses">True when the list comes from this machine, whose games may be on this network.</param>
    /// <param name="sequence">The number the result is given.</param>
    public static ServerList? Parse(ReadOnlyMemory<byte> json, bool allowPrivateAddresses, int sequence = 1)
    {
        try
        {
            // Strict: one value and nothing after it, no comments, no trailing commas, no name given twice.
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("servers"u8, out var servers) || servers.ValueKind != JsonValueKind.Array)
                return null;

            var games = new List<ListedGame>(Math.Min(servers.GetArrayLength(), MaxGames));
            var dropped = 0;
            foreach (var entry in servers.EnumerateArray())
            {
                if (games.Count < MaxGames && TryReadGame(entry, allowPrivateAddresses, out var game) && !Contains(games, game!))
                    games.Add(game!);
                else
                    dropped++;
            }

            return new ServerList(BrowserState.Ready, [.. games], "", dropped, sequence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Contains(List<ListedGame> games, ListedGame game)
    {
        foreach (var other in games)
        {
            if (other.Port == game.Port && other.Address == game.Address)
                return true;
        }

        return false;
    }

    private static bool TryReadGame(JsonElement entry, bool allowPrivateAddresses, out ListedGame? game)
    {
        game = null;
        if (entry.ValueKind != JsonValueKind.Object)
            return false;

        if (!Text(entry, "name"u8, MaxRawNameLength, out var rawName) || !ServerNames.TryNormalize(rawName, out var name))
            return false;
        if (!Text(entry, "address"u8, MaxAddressLength, out var address) || !IsJoinableAddress(address, allowPrivateAddresses))
            return false;
        if (!Whole(entry, "port"u8, 1, ushort.MaxValue, out var port))
            return false;
        if (!Whole(entry, "maxPlayers"u8, 0, 255, out var maxPlayers) || !Whole(entry, "players"u8, 0, maxPlayers, out var players))
            return false;
        if (!entry.TryGetProperty("password"u8, out var password) || password.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        if (!Text(entry, "version"u8, MaxVersionLength, out var version) || !IsVersion(version))
            return false;

        game = new ListedGame(name, address, (ushort)port, players, maxPlayers, password.GetBoolean(), version);
        return true;
    }

    /// <summary>
    /// True for an address this game may offer to join: a literal IPv4 or IPv6 address written the one normal
    /// way (no host names, no brackets, no port, no zone, no odd spellings such as <c>127.1</c>), that a
    /// connection can go to, that the transport can reach, and that is public unless private ones are allowed.
    /// </summary>
    public static bool IsJoinableAddress(string text, bool allowPrivateAddresses)
    {
        if (text.Length is 0 or > MaxAddressLength || !IPAddress.TryParse(text, out var address) || address.ToString() != text)
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);

            // Never: "this network", multicast, and the reserved block that ends in the broadcast address.
            if (b[0] == 0 || b[0] >= 224)
                return false;
            return allowPrivateAddresses
                || !(b[0] == 10
                    || b[0] == 127
                    || (b[0] == 100 && (b[1] & 0xC0) == 64)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && (b[1] & 0xF0) == 16)
                    || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 198 && (b[1] & 0xFE) == 18));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || !RakNetTransport.SupportsIpv6)
            return false;
        if (address.ScopeId != 0 || address.IsIPv4MappedToIPv6 || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast)
            return false;
        return allowPrivateAddresses
            || !(address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal);
    }

    private static bool Text(JsonElement entry, ReadOnlySpan<byte> name, int maxLength, out string text)
    {
        text = "";
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString()!;
        return text.Length <= maxLength;
    }

    private static bool Whole(JsonElement entry, ReadOnlySpan<byte> name, int minimum, int maximum, out int number)
    {
        number = 0;
        return entry.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out number)
            && number >= minimum
            && number <= maximum;
    }

    private static bool IsVersion(string version)
    {
        if (version.Length == 0)
            return false;
        foreach (var c in version)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-'))
                return false;
        }

        return true;
    }
}
