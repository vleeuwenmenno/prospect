using System.Buffers;
using System.Text.Json;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;

namespace Prospect.Game.Lobby;

/// <summary>Where a public server stands with the listing server.</summary>
public enum AnnounceState : byte
{
    /// <summary>This server does not announce itself: it is not public, or there is no listing server.</summary>
    Off,

    /// <summary>The first announcement is under way.</summary>
    Announcing,

    /// <summary>The server is on the list and its entry is being kept alive.</summary>
    Listed,

    /// <summary>The last attempt failed; the next one is scheduled.</summary>
    Retrying,
}

/// <summary>
/// Keeps a public server on the listing server's list (docs/listing-server.md): announces it, refreshes the
/// entry with the token it was given, and removes the entry when the server shuts down.
///
/// It runs as one task on the thread pool and shares nothing with the simulation but a player count read
/// through a delegate, so the tick neither waits for the network nor allocates for it. Whatever the listing
/// server does (down, slow, a 409 because an entry from an earlier run is still there, an answer that is not
/// JSON or far too large), the announcer logs one line when the situation changes, waits and tries again; it
/// never throws into the game.
/// </summary>
public sealed class ListingAnnouncer : IDisposable
{
    /// <summary>The largest answer to an announcement this game reads. A real one is about a hundred bytes.</summary>
    private const int MaxReplyBytes = 4096;

    /// <summary>How long a shutdown waits for the entry to be removed before giving up on it.</summary>
    private static readonly TimeSpan RemovalTime = TimeSpan.FromSeconds(3);

    private readonly ListingConfig _config;
    private readonly ListingHttp _http;
    private readonly string _name;
    private readonly ushort _port;
    private readonly int _maxPlayers;
    private readonly bool _password;
    private readonly Func<int> _players;
    private readonly CancellationTokenSource _stop = new();
    private readonly ArrayBufferWriter<byte> _body = new(256);

    private Task? _task;
    private string? _id;
    private string? _token;
    private int _failures;
    private string _situation = "";
    private volatile AnnounceState _state = AnnounceState.Announcing;
    private int _disposed;

    /// <param name="config">The listing server.</param>
    /// <param name="name">The server's name, already checked with <see cref="ServerNames.TryNormalize"/>.</param>
    /// <param name="port">The UDP port players connect to.</param>
    /// <param name="maxPlayers">Player slots.</param>
    /// <param name="password">Whether joining takes a password.</param>
    /// <param name="players">The number of players now. Called from the announcer's task; must be safe from any thread.</param>
    public ListingAnnouncer(ListingConfig config, string name, ushort port, int maxPlayers, bool password, Func<int> players)
    {
        _config = config;
        // Players reach the game over the transport's address family, so that is the one to be listed under.
        _http = new ListingHttp(config, ipv4Only: !RakNetTransport.SupportsIpv6);
        _name = name;
        _port = port;
        _maxPlayers = maxPlayers;
        _password = password;
        _players = players;
    }

    /// <summary>Where the server stands with the list. Safe to read from any thread.</summary>
    public AnnounceState State => _state;

    /// <summary>Starts announcing. Returns at once; the work happens on the thread pool.</summary>
    public void Start() => _task ??= Task.Run(RunAsync);

    /// <summary>
    /// Asks the announcer to stop and remove the entry, without waiting for it. <see cref="Dispose"/> then
    /// waits; calling this first lets the removal run while the rest of a server shuts down.
    /// </summary>
    public void BeginStop()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _stop.Cancel();
    }

    /// <summary>
    /// Stops announcing and removes the entry from the list. Waits a few seconds at most for the removal; an
    /// entry that could not be removed expires on the listing server by itself.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop.Cancel();
        try
        {
            _task?.Wait(RemovalTime + TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The task catches everything itself; a failure here has nothing left to tell.
        }

        _http.Dispose();
        _stop.Dispose();
        _state = AnnounceState.Off;
    }

    private async Task RunAsync()
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await AnnounceAsync(stop).ConfigureAwait(false);
                await Task.Delay(wait, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // Nothing the listing server or the network does may reach the game. Say what it was and go on.
                wait = Failed($"announcing failed unexpectedly ({e.GetType().Name}: {e.Message})");
                try
                {
                    await Task.Delay(wait, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await RemoveAsync().ConfigureAwait(false);
    }

    /// <summary>Makes one announcement or refresh and returns how long to wait before the next.</summary>
    private async Task<TimeSpan> AnnounceAsync(CancellationToken stop)
    {
        var refresh = TimeSpan.FromSeconds(_config.RefreshSeconds);
        var refreshing = _token is not null;
        var reply = await _http.SendAsync(HttpMethod.Post, "v1/servers", WriteBody(), null, MaxReplyBytes, stop).ConfigureAwait(false);

        switch (reply.Outcome)
        {
            case ListingOutcome.Unreachable:
                return Failed("the listing server cannot be reached");
            case ListingOutcome.TimedOut:
                return Failed($"the listing server did not answer within {_config.TimeoutSeconds:0.#} s");
            case ListingOutcome.TooLarge:
                return Failed("the listing server's answer is too large to be one");
        }

        switch (reply.Status)
        {
            case 200 or 201:
                if (!TryReadEntry(reply.Body, out var id, out var token))
                    return Failed("the listing server's answer is not what the contract says");

                // A 201 on a refresh means the old entry had expired; either way these are the ones to keep.
                _id = id;
                _token = token;
                _failures = 0;
                _state = AnnounceState.Listed;
                Say(refreshing && reply.Status == 201 ? "listed again; the earlier entry had expired" : "listed", warning: false);
                return refresh;

            case 409:
                // An entry from an earlier run of this server, whose token is gone. It expires by itself.
                _state = AnnounceState.Retrying;
                Say("this address and port are still listed from an earlier run; announcing again once that entry has expired", warning: false);
                return refresh;

            case 403 when refreshing:
                // The entry is no longer this server's: forget the token and start over as a new server.
                _id = null;
                _token = null;
                _state = AnnounceState.Retrying;
                Say("the listing server no longer accepts this server's token; announcing as new", warning: true);
                return refresh;

            case 429:
                {
                    var wait = Failed($"the listing server is limiting requests: {Reason(reply)}");
                    var maximum = TimeSpan.FromSeconds(_config.MaxRetrySeconds);
                    return reply.RetryAfter is { } asked && asked > wait ? (asked < maximum ? asked : maximum) : wait;
                }

            case >= 400 and < 500:
                // The service understood and said no: this address may not be listed, or it does not accept
                // what this version sends. Asking again soon changes nothing.
                Failed($"the listing server refused the announcement ({reply.Status}): {Reason(reply)}");
                return TimeSpan.FromSeconds(_config.MaxRetrySeconds);

            default:
                return Failed($"the listing server answered {reply.Status}");
        }
    }

    /// <summary>Removes the entry, if there is one, within a short time of its own.</summary>
    private async Task RemoveAsync()
    {
        if (_id is null || _token is null)
            return;

        try
        {
            var reply = await _http
                .SendAsync(HttpMethod.Delete, "v1/servers/" + Uri.EscapeDataString(_id), default, _token, MaxReplyBytes, CancellationToken.None, RemovalTime)
                .ConfigureAwait(false);
            if (reply.Is(204) || reply.Is(404))
                Logs.Net.Info($"Listing: '{_name}' removed from {_config.Url.Host}");
            else
                Logs.Net.Warning($"Listing: could not remove '{_name}' from {_config.Url.Host}; the entry expires by itself");
        }
        catch (Exception e)
        {
            Logs.Net.Warning($"Listing: could not remove '{_name}' from {_config.Url.Host} ({e.GetType().Name}); the entry expires by itself");
        }

        _id = null;
        _token = null;
    }

    private ReadOnlyMemory<byte> WriteBody()
    {
        // The service takes at most 255 players; a larger server reports itself as that.
        var maxPlayers = Math.Clamp(_maxPlayers, 0, 255);
        _body.Clear();
        using (var writer = new Utf8JsonWriter(_body))
        {
            writer.WriteStartObject();
            writer.WriteString("name"u8, _name);
            writer.WriteNumber("port"u8, _port);
            writer.WriteNumber("players"u8, Math.Clamp(_players(), 0, maxPlayers));
            writer.WriteNumber("maxPlayers"u8, maxPlayers);
            writer.WriteNumber("protocol"u8, ProtocolInfo.Version);
            writer.WriteString("version"u8, ProtocolInfo.GameVersion);
            writer.WriteBoolean("password"u8, _password);
            if (_token is not null)
                writer.WriteString("token"u8, _token);
            writer.WriteEndObject();
        }

        return _body.WrittenMemory;
    }

    /// <summary>
    /// Reads <c>{"id": "...", "token": "..."}</c>. Both end up in a request later (the id in a path, the token
    /// in a header), so only short, plain, printable ones are accepted.
    /// </summary>
    private static bool TryReadEntry(ReadOnlyMemory<byte> json, out string id, out string token)
    {
        id = token = "";
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id"u8, out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("token"u8, out var tokenValue)
                || tokenValue.ValueKind != JsonValueKind.String)
                return false;

            id = idValue.GetString()!;
            token = tokenValue.GetString()!;
            return IsPlain(id, 64) && IsPlain(token, 128);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPlain(string text, int maxLength)
    {
        if (text.Length == 0 || text.Length > maxLength)
            return false;
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '+' or '/' or '='))
                return false;
        }

        return true;
    }

    private static string Reason(in ListingReply reply) => ListingHttp.ErrorText(reply.Body) is { Length: > 0 } text ? text : "no reason given";

    /// <summary>Notes a failed attempt and returns the wait before the next: doubling from the first retry up to the maximum.</summary>
    private TimeSpan Failed(string situation)
    {
        _failures++;
        _state = AnnounceState.Retrying;
        Say(situation, warning: true);
        var seconds = _config.RetrySeconds * Math.Pow(2, Math.Min(_failures - 1, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, _config.MaxRetrySeconds));
    }

    /// <summary>Logs a line when the situation is not the one already reported, so a long outage is one line, not hundreds.</summary>
    private void Say(string situation, bool warning)
    {
        if (situation == _situation)
            return;
        _situation = situation;
        if (warning)
            Logs.Net.Warning($"Listing ({_config.Url.Host}): {situation}");
        else
            Logs.Net.Info($"Listing ({_config.Url.Host}): {situation}");
    }
}
