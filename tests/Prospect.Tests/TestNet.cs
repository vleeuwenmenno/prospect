using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Waiting and port numbers shared by the tests that run real servers and clients.</summary>
internal static class TestNet
{
    /// <summary>First port of the test range: below Linux' ephemeral range (32768 and up), where any outgoing socket may sit.</summary>
    public const int PortBase = 16384;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ushort> Ports = new();

    /// <summary>
    /// A UDP port for one test. Every test process has its own block of 32, so two runs on one machine (two
    /// worktrees, say) do not meet, and every test in a run has its own slot in the block. The range lies below
    /// the operating system's ephemeral ports, which clients and HTTP connections take at random, and a port
    /// something already holds is skipped by whole blocks. The same slot always gives the same port in a run.
    /// </summary>
    public static ushort Port(int slot) => Ports.GetOrAdd(slot, static s =>
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var port = (ushort)(PortBase + (Environment.ProcessId + attempt) % 500 * 32 + s);
            if (IsFree(port)) return port;
        }

        throw new IOException($"No free UDP port for test slot {s}");
    });

    /// <summary>True when nothing holds the UDP port on any address, as RakNet binds it.</summary>
    public static bool IsFree(ushort port)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static SessionSnapshot WaitFor(GameSession session, Func<SessionSnapshot, bool> condition, int seconds = 15)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var snapshot = session.Client.AcquireSnapshot();
            if (condition(snapshot))
                return snapshot;
            Assert.Null(session.Fault);
            Thread.Sleep(5);
        }

        var last = session.Client.AcquireSnapshot();
        throw new TimeoutException($"Session did not reach the expected state; last was {last.State} ({last.EndReason})");
    }

    public static void WaitUntil(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"Not in time: {what}");
            Thread.Sleep(5);
        }
    }

    /// <summary>A listing configuration for a test: the given address, with waits short enough to test.</summary>
    public static ListingConfig Listing(string url, double refresh = 0.25, double timeout = 2, double retry = 0.1, double maxRetry = 0.4)
    {
        Assert.True(ListingConfig.TryCreate(url, out var config, out var error), error);
        return config! with
        {
            RefreshSeconds = refresh,
            TimeoutSeconds = timeout,
            RetrySeconds = retry,
            MaxRetrySeconds = maxRetry,
        };
    }
}

/// <summary>
/// A client driven by hand over real UDP, to send a server exactly the bytes a test wants, in the order it
/// wants, and see exactly what comes back.
/// </summary>
internal sealed class RawClient : ITransportHandler, IDisposable
{
    private static readonly GameContent Shipped = GameContent.Load();
    private readonly RakNetTransport _transport;
    private readonly Queue<byte[]> _messages = new();

    public RawClient(ushort port)
    {
        _transport = RakNetTransport.Connect("127.0.0.1", port);
        Wait(() => Connected || Disconnected, "the connection");
        Assert.True(Connected, "the server should accept the UDP connection");
    }

    public bool Connected { get; private set; }
    public bool Disconnected { get; private set; }

    public void Dispose() => _transport.Dispose();

    public void Send(ReadOnlySpan<byte> payload, NetChannel channel = NetChannel.Reliable) => _transport.Send(0, channel, payload);

    /// <summary>A hello as a client of this build sends it, with the shipped content unless the test gives another.</summary>
    public void SendHello(uint version = ProtocolInfo.Version, string name = "Raw", GameContent? content = null)
    {
        Span<byte> buffer = stackalloc byte[HelloMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        new HelloMessage { ProtocolVersion = version, PlayerName = name, Content = (content ?? Shipped).Identity }.Write(ref writer);
        Send(writer.Written);
    }

    public void SendProof(ReadOnlySpan<byte> proof)
    {
        Span<byte> buffer = stackalloc byte[ProofMessage.WireSize];
        var writer = new NetWriter(buffer);
        ProofMessage.Write(ref writer, proof);
        Send(writer.Written);
    }

    /// <summary>The next message of a kind from the server, without its first byte. Others before it are dropped.</summary>
    public byte[] Expect(MessageId id, int seconds = 10)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            // Seen before the poll: whatever the server sent ahead of closing has then been read.
            var closed = Disconnected;
            _transport.Poll(this);
            while (_messages.TryDequeue(out var message))
            {
                if (message[0] == (byte)id)
                    return message[1..];
            }

            if (closed)
                break;
            Thread.Sleep(2);
        }

        throw new TimeoutException($"The server did not send {id}{(Disconnected ? "; it closed the connection" : "")}");
    }

    /// <summary>The server's challenge: its salt and the random bytes for this connection.</summary>
    public (byte[] Salt, byte[] Challenge) ExpectChallenge()
    {
        var reader = new NetReader(Expect(MessageId.Challenge));
        var salt = new byte[Passwords.SaltSize];
        var challenge = new byte[Passwords.ChallengeSize];
        Assert.True(ChallengeMessage.TryRead(ref reader, salt, challenge));
        return (salt, challenge);
    }

    public RejectMessage ExpectReject()
    {
        var reader = new NetReader(Expect(MessageId.Reject));
        Assert.True(RejectMessage.TryRead(ref reader, out var reject));
        return reject;
    }

    /// <summary>Waits for the server to close the connection.</summary>
    public void ExpectClosed() => Wait(() => Disconnected, "the server closing the connection");

    private void Wait(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"Not in time: {what}");
            _transport.Poll(this);
            Thread.Sleep(2);
        }
    }

    void ITransportHandler.OnConnected(ITransport transport, int peer) => Connected = true;

    void ITransportHandler.OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => Disconnected = true;

    void ITransportHandler.OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty)
            _messages.Enqueue(payload.ToArray());
    }
}

/// <summary>A clock the tests move by hand, so the listing service's expiry is tested without waiting.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// The real listing service (src/Prospect.Listing), running in the test process on a loopback port. The game's
/// announcer and browser are pointed at it, so both sides of the contract are tested against each other.
/// </summary>
internal sealed class RealListing : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RealListing(WebApplication app, string url, ManualClock clock)
    {
        _app = app;
        Url = url;
        Clock = clock;
    }

    public string Url { get; }

    public ManualClock Clock { get; }

    public Prospect.Listing.ListingStore Store => _app.Services.GetRequiredService<Prospect.Listing.ListingStore>();

    /// <summary>Everything the service lists for this game's protocol, as the service itself has it.</summary>
    public List<Prospect.Listing.ListedServer> Listed => Store.List(ProtocolInfo.Version);

    public static async Task<RealListing> StartAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            // The tests connect over loopback and refresh far more often than a real server does.
            ["Listing:AllowPrivateAddresses"] = "true",
            ["Listing:PostsPerMinute"] = "100000",
            ["Listing:ListsPerMinute"] = "100000",
            ["Listing:SweepIntervalMilliseconds"] = "60000",
        };
        var clock = new ManualClock();
        var app = Prospect.Listing.ListingApp.Build(
            [],
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(values);
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.WebHost.UseUrls("http://127.0.0.1:0");
            }
        );
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RealListing(app, url, clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>One request a <see cref="FakeListing"/> received.</summary>
internal sealed record FakeRequest(string Method, string Path, string Authorization, string Body);

/// <summary>What a <see cref="FakeListing"/> does with a request.</summary>
internal sealed class FakeAnswer
{
    public int Status = 200;
    public string Body = "";

    /// <summary>Raw bytes sent instead of an HTTP answer.</summary>
    public byte[]? Raw;

    /// <summary>Accept the request and never answer.</summary>
    public bool Hang;

    /// <summary>Send headers that promise no length, then bytes for as long as the client keeps reading.</summary>
    public bool Endless;

    /// <summary>Claim this many bytes in Content-Length, whatever the body is.</summary>
    public long? ClaimedLength;

    /// <summary>A Location header, as a redirect carries.</summary>
    public string? Location;

    public static FakeAnswer Json(int status, string body) => new() { Status = status, Body = body };

    public static FakeAnswer Listed(string id = "entry-1", string token = "token-1") =>
        Json(201, $$"""{"id":"{{id}}","token":"{{token}}","ttlSeconds":90}""");
}

/// <summary>
/// A listing server that misbehaves on request: a bare TCP listener that reads one HTTP request per connection
/// and answers whatever the test says, including nothing at all, rubbish, and far too much.
/// </summary>
internal sealed class FakeListing : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<FakeRequest> _requests = [];
    private readonly Lock _gate = new();
    private long _endlessBytes;

    public FakeListing(Func<FakeRequest, int, FakeAnswer> answer)
    {
        Answer = answer;
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>Decides the answer from the request and how many requests came before it.</summary>
    public Func<FakeRequest, int, FakeAnswer> Answer { get; set; }

    public string Url { get; }

    /// <summary>Bytes sent in answers that never end; it stops growing once the client stops reading.</summary>
    public long EndlessBytes => Interlocked.Read(ref _endlessBytes);

    public List<FakeRequest> Requests
    {
        get
        {
            lock (_gate)
                return [.. _requests];
        }
    }

    public int Count(string method) => Requests.Count(r => r.Method == method);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is null)
                    return;

                int index;
                lock (_gate)
                {
                    index = _requests.Count;
                    _requests.Add(request);
                }

                var answer = Answer(request, index);
                if (answer.Hang)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }

                if (answer.Raw is not null)
                {
                    await stream.WriteAsync(answer.Raw, _stop.Token);
                    return;
                }

                if (answer.Endless)
                {
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"servers\":["u8.ToArray(), _stop.Token);
                    var chunk = Encoding.ASCII.GetBytes(new string(' ', 8192));
                    while (!_stop.IsCancellationRequested)
                    {
                        await stream.WriteAsync(chunk, _stop.Token);
                        Interlocked.Add(ref _endlessBytes, chunk.Length);
                    }

                    return;
                }

                var body = Encoding.UTF8.GetBytes(answer.Body);
                var location = answer.Location is null ? "" : $"Location: {answer.Location}\r\n";
                var head = $"HTTP/1.1 {answer.Status} X\r\nContent-Type: application/json\r\n{location}Content-Length: {answer.ClaimedLength ?? body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
    }

    private async Task<FakeRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[16 * 1024];
        var length = 0;
        var headEnd = -1;
        while (headEnd < 0 && length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), _stop.Token);
            if (read == 0)
                return null;
            length += read;
            headEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        if (headEnd < 0)
            return null;

        var lines = Encoding.ASCII.GetString(buffer, 0, headEnd).Split("\r\n");
        var start = lines[0].Split(' ');
        var contentLength = 0;
        var authorization = "";
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(value);
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                authorization = value;
        }

        var bodyStart = headEnd + 4;
        while (length - bodyStart < contentLength && length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), _stop.Token);
            if (read == 0)
                break;
            length += read;
        }

        return new FakeRequest(start[0], start.Length > 1 ? start[1] : "", authorization, Encoding.UTF8.GetString(buffer, bodyStart, Math.Min(contentLength, length - bodyStart)));
    }
}
