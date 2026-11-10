using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Xunit;

namespace Prospect.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void WelcomeRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new NetWriter(buffer);
        new WelcomeMessage
        {
            PlayerId = 42,
            TickRate = 60,
            ServerTick = 123456789012,
            GalaxySeed = 0xDEADBEEFCAFEF00D,
            ServerName = "Ceres Station",
        }.Write(ref writer);

        Assert.Equal((byte)MessageId.Welcome, writer.Written[0]);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(WelcomeMessage.TryRead(ref reader, out var welcome));
        Assert.Equal(42u, welcome.PlayerId);
        Assert.Equal(60, welcome.TickRate);
        Assert.Equal(123456789012, welcome.ServerTick);
        Assert.Equal(0xDEADBEEFCAFEF00D, welcome.GalaxySeed);
        Assert.Equal("Ceres Station", welcome.ServerName);
    }

    [Theory]
    [InlineData("  Ada Lovelace  ", "Ada Lovelace")]
    [InlineData("<script>alert(1)</script>", "scriptalert1script")]
    [InlineData("", "Prospector")]
    [InlineData("\u0007\u001b[31m", "31m")]
    [InlineData("an-extremely-long-callsign-that-keeps-going", "an-extremely-long-callsi")]
    public void NamesAreSanitized(string raw, string expected)
    {
        Assert.Equal(expected, PlayerNames.Sanitize(raw));
    }
}

/// <summary>Runs real servers and clients on their own threads, over the in-process link and over UDP.</summary>
public sealed class SessionTests
{
    static SessionTests()
    {
        Logs.Initialize(LogLevel.Warning);
    }

    [Fact]
    public void LocalGameReachesActiveAndKeepsClocksInStep()
    {
        using var session = GameSession.StartLocal(new ServerConfig { Name = "Test world", MaxPlayers = 1 }, "Tester");

        var snapshot = WaitFor(session, s => s.State == SessionState.Active && s.RoundTripMs >= 0);
        Assert.Equal("Test world", snapshot.ServerName);
        Assert.Equal(1u, snapshot.PlayerId);
        Assert.Equal(1, snapshot.PlayerCount);
        Assert.True(session.IsLocal);

        // The client's estimate of the server tick tracks the real one closely.
        Thread.Sleep(500);
        snapshot = session.Client.AcquireSnapshot();
        var actual = session.LocalServerStatus!.Value.Tick;
        Assert.InRange(snapshot.ServerTick - actual, -3, 3);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void LeavingEndsTheSessionAndFreesTheSlot()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, "Tester");
        WaitFor(session, s => s.State == SessionState.Active);

        session.Client.RequestLeave();
        var ended = WaitFor(session, s => s.State == SessionState.Ended);

        Assert.Equal(SessionEndReason.LeftByPlayer, ended.EndReason);
        WaitUntil(() => session.LocalServerStatus!.Value.PlayerCount == 0);
    }

    [Fact]
    public void RemoteClientsJoinOverUdpAndAFullServerTurnsTheExtraOneAway()
    {
        var port = (ushort)(41000 + Environment.ProcessId % 2000);
        using var server = new ServerHost(new ServerConfig { Name = "UDP world", MaxPlayers = 2 }, RakNetTransport.Listen(port, 8));
        server.Start();

        using var first = GameSession.Join("127.0.0.1", port, "One");
        using var second = GameSession.Join("127.0.0.1", port, "Two");
        var a = WaitFor(first, s => s.State == SessionState.Active && s.RoundTripMs >= 0);
        var b = WaitFor(second, s => s.State == SessionState.Active && s.PlayerCount == 2);
        Assert.Equal("UDP world", a.ServerName);
        Assert.NotEqual(a.PlayerId, b.PlayerId);
        Assert.False(first.IsLocal);
        Assert.Equal(2, server.Status.PlayerCount);

        using var third = GameSession.Join("127.0.0.1", port, "Three");
        var refused = WaitFor(third, s => s.State == SessionState.Ended);
        Assert.Equal(SessionEndReason.ServerFull, refused.EndReason);

        // A slot frees up when a player leaves, and the next client gets it.
        first.Client.RequestLeave();
        WaitUntil(() => server.Status.PlayerCount == 1);
        using var fourth = GameSession.Join("127.0.0.1", port, "Four");
        WaitFor(fourth, s => s.State == SessionState.Active);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ClientLearnsWhenTheServerShutsDown()
    {
        var port = (ushort)(43000 + Environment.ProcessId % 2000);
        var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, RakNetTransport.Listen(port, 8));
        server.Start();
        using var client = GameSession.Join("127.0.0.1", port, "One");
        WaitFor(client, s => s.State == SessionState.Active);

        server.Dispose();

        var ended = WaitFor(client, s => s.State == SessionState.Ended);
        Assert.Contains(ended.EndReason, new[] { SessionEndReason.ServerClosed, SessionEndReason.ConnectionLost });
    }

    [Fact]
    public void GarbageFromAClientDoesNotHurtTheServer()
    {
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 4 }, link.Server);
        server.Start();

        var collector = new Collector();
        var random = new Random(1234);
        var junk = new byte[64];
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline && !collector.Disconnected)
        {
            link.Client.Poll(collector);
            if (!collector.Connected)
                continue;
            random.NextBytes(junk);
            link.Client.Send(0, NetChannel.Reliable, junk.AsSpan(0, random.Next(0, junk.Length)));
        }

        // A connection that does not open with a valid hello is refused and closed; the server carries on.
        Assert.True(collector.Disconnected);
        Assert.True(server.IsRunning);
        Assert.Null(server.Fault);
        Assert.Equal(0, server.Status.PlayerCount);
    }

    private sealed class Collector : ITransportHandler
    {
        public bool Connected;
        public bool Disconnected;

        public void OnConnected(ITransport transport, int peer) => Connected = true;

        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => Disconnected = true;

        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) { }
    }

    private static SessionSnapshot WaitFor(GameSession session, Func<SessionSnapshot, bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
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

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException("Condition was not met in time");
            Thread.Sleep(5);
        }
    }
}
