using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Sessions log through the shared categories, so logging is set up before any test runs. The console shows
    /// warnings and worse. Everything from information up is also written, as a running game writes it, to a
    /// counter: a test can then see how much was logged, and what logging a line costs is really spent.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void Initialize() =>
        Logs.Initialize(
            LogLevel.Information,
            builder =>
            {
                builder.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, LogLevel.Warning);
                builder.AddProvider(new CountingLogs());
            }
        );
}

/// <summary>Counts the lines the game logs on each thread, and keeps none of them.</summary>
internal sealed class CountingLogs : ILoggerProvider, ILogger
{
    [ThreadStatic]
    private static long _onThisThread;

    /// <summary>Lines logged so far by the thread that asks.</summary>
    public static long OnThisThread => _onThisThread;

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Formatted as a real log would, so the cost of a line is the cost it has in a running game.
        _ = formatter(state, exception);
        _onThisThread++;
    }
}

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(7f, 2d, true, JumpPhase.Tunnel)]
    [InlineData(-1f, 0d, false, JumpPhase.Tunnel)]
    [InlineData(float.NaN, 0d, false, JumpPhase.Tunnel)]
    [InlineData(float.PositiveInfinity, 0d, false, JumpPhase.Tunnel)]
    [InlineData(float.MaxValue, 0d, false, JumpPhase.Tunnel)]
    [InlineData(7f, -1d, false, JumpPhase.Tunnel)]
    [InlineData(7f, double.NaN, false, JumpPhase.Tunnel)]
    [InlineData(7f, double.PositiveInfinity, false, JumpPhase.Tunnel)]
    [InlineData(7f, 8d, false, JumpPhase.Tunnel)]
    [InlineData(7f, 1d, false, JumpPhase.None)]
    [InlineData(7f, 1d, false, JumpPhase.Align)]
    [InlineData(7f, 1d, false, JumpPhase.Charge)]
    public void TransitWireBounds(float duration, double elapsed, bool valid, JumpPhase phase)
    {
        Span<byte> buffer = stackalloc byte[HullRecord.MaxWireSize];
        var writer = new NetWriter(buffer);
        new HullRecord
        {
            State = new HullState
            {
                Orientation = Quaternion.Identity,
                Jump = phase,
                JumpTunnelSeconds = duration,
                JumpTunnelElapsed = elapsed,
                JumpTimer = (float)elapsed
            }
        }.Write(ref writer);
        var reader = new NetReader(writer.Written);
        Assert.Equal(valid, HullRecord.TryRead(ref reader, out var copy));
        if (valid)
        {
            Assert.Equal(duration, copy.State.JumpTunnelSeconds);
            Assert.Equal(elapsed, copy.State.JumpTunnelElapsed);
        }
        Assert.True(18 + byte.MaxValue * HullRecord.MaxWireSize < 65536);
    }

    [Theory]
    [InlineData(3f, true)]
    [InlineData(-1f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(float.NegativeInfinity, false)]
    public void CalculationWireValueIsBoundedAndRecordSizeIsExact(float calculation, bool valid)
    {
        Span<byte> buffer = stackalloc byte[HullRecord.MaxWireSize];
        var writer = new NetWriter(buffer);
        new HullRecord
        {
            State = new HullState { Orientation = Quaternion.Identity, Jump = JumpPhase.Charge, JumpCalculation = calculation },
            Order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) }
        }.Write(ref writer);
        Assert.Equal(153, HullRecord.MaxWireSize);
        Assert.Equal(HullRecord.MaxWireSize, writer.Length);
        var reader = new NetReader(writer.Written);
        Assert.Equal(valid, HullRecord.TryRead(ref reader, out var copy));
        if (valid) Assert.Equal(calculation, copy.State.JumpCalculation);
        for (var length = 0; length < writer.Length; length++)
        {
            reader = new NetReader(writer.Written[..length]);
            Assert.False(HullRecord.TryRead(ref reader, out _));
        }
        Assert.Equal(20u, ProtocolInfo.Version);
    }

    [Theory]
    [InlineData((byte)0, 10f, 0f, true)]
    [InlineData(ShieldHit.MaxAgeTicks, 10f, 0f, true)]
    [InlineData((byte)(ShieldHit.MaxAgeTicks + 1), 10f, 0f, false)]
    [InlineData((byte)255, 10f, 0f, false)]
    [InlineData((byte)3, -1f, 0f, false)]
    [InlineData((byte)3, float.NaN, 0f, false)]
    [InlineData((byte)3, 10f, -0.5f, false)]
    public void ShieldStateTravelsWithTheHullAndBadValuesAreRefused(byte age, float shield, float cooldown, bool valid)
    {
        Span<byte> buffer = stackalloc byte[HullRecord.MaxWireSize];
        var writer = new NetWriter(buffer);
        var hit = new ShieldHit { Serial = 251, Strength = 77, X = -128, Y = 127, Z = -3, AgeTicks = age };
        new HullRecord
        {
            State = new HullState
            {
                Orientation = Quaternion.Identity,
                Jump = JumpPhase.Arrive,
                Boosting = true,
                Shield = shield,
                ShieldCooldown = cooldown,
                ShieldOffline = true,
                ShieldHit = hit,
            },
            Order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(5, 6, 7) },
        }.Write(ref writer);
        var reader = new NetReader(writer.Written);
        Assert.Equal(valid, HullRecord.TryRead(ref reader, out var copy));
        if (!valid)
            return;

        // The offline flag shares a byte with the order, the jump stage and boost; none of them may bleed.
        Assert.True(copy.State.ShieldOffline);
        Assert.True(copy.State.Boosting);
        Assert.Equal(JumpPhase.Arrive, copy.State.Jump);
        Assert.Equal(OrderKind.Jump, copy.Order.Kind);
        Assert.Equal(new Double3(5, 6, 7), copy.Order.Target);
        Assert.Equal(hit, copy.State.ShieldHit);
        Assert.Equal(shield, copy.State.Shield);

        writer = new NetWriter(buffer);
        new HullRecord { State = new HullState { Orientation = Quaternion.Identity, Shield = shield } }.Write(ref writer);
        reader = new NetReader(writer.Written);
        Assert.True(HullRecord.TryRead(ref reader, out copy));
        Assert.False(copy.State.ShieldOffline);
    }

    [Theory]
    [InlineData((byte)0, true)]
    [InlineData(ShieldHit.MaxAgeTicks, true)]
    [InlineData((byte)(ShieldHit.MaxAgeTicks + 1), false)]
    [InlineData((byte)255, false)]
    public void TheShieldsLastChangeTravelsAndAnImpossibleAgeIsRefused(byte ticks, bool valid)
    {
        Span<byte> buffer = stackalloc byte[HullRecord.MaxWireSize];
        var writer = new NetWriter(buffer);
        new HullRecord { State = new HullState { Orientation = Quaternion.Identity, ShieldChangeTicks = ticks } }.Write(ref writer);
        var reader = new NetReader(writer.Written);
        Assert.Equal(valid, HullRecord.TryRead(ref reader, out var copy));
        if (valid)
            Assert.Equal(ticks, copy.State.ShieldChangeTicks);
    }

    [Fact]
    public void WelcomeRoundTrips()
    {
        var catalog = GameContent.Load().SettingCatalog;
        var settings = catalog.Defaults;
        catalog.Assign(ref settings, "collisionDamage=2.5");
        catalog.Assign(ref settings, "collisionShields=None");

        Span<byte> buffer = stackalloc byte[WelcomeMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        new WelcomeMessage
        {
            PlayerId = 42,
            TickRate = 60,
            ServerTick = 123456789012,
            GalaxySeed = 0xDEADBEEFCAFEF00D,
            ServerName = "Ceres Station",
            Settings = settings,
        }.Write(ref writer, catalog);

        Assert.Equal((byte)MessageId.Welcome, writer.Written[0]);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(WelcomeMessage.TryRead(ref reader, catalog, out var welcome));
        Assert.Equal(42u, welcome.PlayerId);
        Assert.Equal(60, welcome.TickRate);
        Assert.Equal(123456789012, welcome.ServerTick);
        Assert.Equal(0xDEADBEEFCAFEF00D, welcome.GalaxySeed);
        Assert.Equal("Ceres Station", welcome.ServerName);
        Assert.Equal(settings, welcome.Settings);
        Assert.NotEqual(catalog.Defaults, welcome.Settings);
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

/// <summary>The client's predicted world, fed the snapshots a server would send it.</summary>
public sealed class ClientPredictionTests : IDisposable
{
    private static readonly GameContent Content = GameContent.Load();
    private const uint Player = 1;
    private const uint OwnHull = 1 << 16;

    private readonly ClientWorld _world = new(Content);
    private readonly LoopbackLink _link = new();
    private readonly PlayerInputBridge _input = new();
    private readonly HullView[] _views = new HullView[ClientWorld.MaxViews];
    private readonly byte[] _buffer = new byte[1024];
    private long _serverTick = 100;

    public ClientPredictionTests()
    {
        _world.SetPlayer(Player);
        _world.SetOwnHull(OwnHull);
    }

    public void Dispose()
    {
        _world.Dispose();
        _link.Dispose();
    }

    private static HullRecord Hull(uint id, uint owner, int design, Double3 position, Vector3 velocity = default)
    {
        var state = HullState.Spawn(position, Content.Designs[design].Flight);
        state.Velocity = velocity;
        return new HullRecord
        {
            Id = id,
            Owner = owner,
            Design = (byte)design,
            Blocks = Content.Designs[design].FullMask,
            State = state,
        };
    }

    /// <summary>Delivers a snapshot the server took at a tick. The first hull is the player's.</summary>
    private void Snapshot(long tick, uint acked, params HullRecord[] hulls)
    {
        var writer = new NetWriter(_buffer);
        new SnapshotHeader
        {
            ServerTick = tick,
            AckedInput = acked,
            OwnHull = hulls[0].Id,
            HullCount = (byte)hulls.Length,
        }.Write(ref writer);
        foreach (var hull in hulls)
            hull.Write(ref writer);

        var reader = new NetReader(writer.Written[1..]);
        _world.OnSnapshot(ref reader, _serverTick);
    }

    /// <summary>Runs one client tick with nobody at the controls, which sends one input.</summary>
    private void Tick() => _world.Tick(_input, _link.Client, ++_serverTick);

    /// <summary>The player's hull as the client now predicts it.</summary>
    private HullView Own()
    {
        _world.BuildViews(_views, _serverTick, _serverTick, out var own);
        return _views[own];
    }

    [Fact]
    public void AnotherPlayersHullCrossingThePlayersBetweenTwoTicksStrikesItInThePrediction()
    {
        // A drone belonging to someone else, coming down on the pod at 400 m/s: more than six metres a tick,
        // so it is above the pod at one tick and below it at the next, and at neither is it touching.
        var pod = Content.Pod.Flight;
        var drone = Hull(OwnHull + 1, Player + 1, Content.DroneDesign, new Double3(0, 3.3, 0), new Vector3(0, -400, 0));
        Snapshot(_serverTick, 0, Hull(OwnHull, Player, Content.PodDesign, Double3.Zero), drone);
        Assert.Equal(pod.ShieldCapacity, Own().Shield);

        Tick();

        // The server has yet to say a word about it, and the pod has already been knocked down and hurt.
        Assert.True(Own().Velocity.Y < -1f, $"the pod moves at {Own().Velocity}");
        Assert.True(Own().Shield < pod.ShieldCapacity, "the pod's shield should have taken the blow");
    }

    [Fact]
    public void ANewerSnapshotCorrectsThePredictionWithoutANewAcknowledgementAndALateOneDoesNot()
    {
        var pod = Content.Pod.Flight;
        var rest = Hull(OwnHull, Player, Content.PodDesign, Double3.Zero);
        Snapshot(_serverTick, 0, rest);
        for (var i = 0; i < 3; i++)
            Tick();
        Snapshot(_serverTick, 3, rest);
        Assert.Equal(Vector3.Zero, Own().Velocity);

        // Something ran into the pod on the server. No input of the player's had a part in it, so the
        // acknowledgement stands where it was; the tick is what makes this the newer truth.
        var struck = rest;
        struck.State.Velocity = new Vector3(12, 0, 0);
        struck.State.Shield = pod.ShieldCapacity * 0.5f;
        Snapshot(_serverTick + 1, 3, struck);
        Assert.Equal(struck.State.Velocity, Own().Velocity);
        Assert.Equal(struck.State.Shield, Own().Shield);

        // The snapshot from before the blow turns up late. It is older, whatever it acknowledges.
        Snapshot(_serverTick, 3, rest);
        Assert.Equal(struck.State.Velocity, Own().Velocity);
        Assert.Equal(struck.State.Shield, Own().Shield);

        // So does the very same one again.
        var twice = struck;
        twice.State.Velocity = new Vector3(-30, 0, 0);
        Snapshot(_serverTick + 1, 3, twice);
        Assert.Equal(struck.State.Velocity, Own().Velocity);
    }
}

/// <summary>Runs real servers and clients on their own threads, over the in-process link and over UDP.</summary>
public sealed class SessionTests
{
    private static readonly GameContent Content = GameContent.Load();


    [Fact]
    public void LocalGameReachesActiveAndKeepsClocksInStep()
    {
        using var session = GameSession.StartLocal(new ServerConfig { Name = "Test world", MaxPlayers = 1 }, Content, "Tester");

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
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Tester");
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
        using var server = new ServerHost(new ServerConfig { Name = "UDP world", MaxPlayers = 2 }, Content, RakNetTransport.Listen(port, 8));
        server.Start();

        using var first = GameSession.Join("127.0.0.1", port, Content, "One");
        using var second = GameSession.Join("127.0.0.1", port, Content, "Two");
        var a = WaitFor(first, s => s.State == SessionState.Active && s.RoundTripMs >= 0);
        var b = WaitFor(second, s => s.State == SessionState.Active && s.PlayerCount == 2);
        Assert.Equal("UDP world", a.ServerName);
        Assert.NotEqual(a.PlayerId, b.PlayerId);
        Assert.False(first.IsLocal);
        Assert.Equal(2, server.Status.PlayerCount);

        using var third = GameSession.Join("127.0.0.1", port, Content, "Three");
        var refused = WaitFor(third, s => s.State == SessionState.Ended);
        Assert.Equal(SessionEndReason.ServerFull, refused.EndReason);

        // A slot frees up when a player leaves, and the next client gets it.
        first.Client.RequestLeave();
        WaitUntil(() => server.Status.PlayerCount == 1);
        using var fourth = GameSession.Join("127.0.0.1", port, Content, "Four");
        WaitFor(fourth, s => s.State == SessionState.Active);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ClientLearnsWhenTheServerShutsDown()
    {
        var port = (ushort)(43000 + Environment.ProcessId % 2000);
        var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, Content, RakNetTransport.Listen(port, 8));
        server.Start();
        using var client = GameSession.Join("127.0.0.1", port, Content, "One");
        WaitFor(client, s => s.State == SessionState.Active);

        server.Dispose();

        var ended = WaitFor(client, s => s.State == SessionState.Ended);
        Assert.Contains(ended.EndReason, new[] { SessionEndReason.ServerClosed, SessionEndReason.ConnectionLost });
    }

    [Fact]
    public void GarbageFromAClientDoesNotHurtTheServer()
    {
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 4 }, Content, link.Server);
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
