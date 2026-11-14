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
    public static void Initialize()
    {
        // Parallel synchronous tests occupy pool threads while game jobs need additional workers to finish.
        ThreadPool.GetMinThreads(out var workers, out var completion);
        ThreadPool.SetMinThreads(Math.Max(workers, 4 * Environment.ProcessorCount), completion);
        Logs.Initialize(
            LogLevel.Information,
            builder =>
            {
                builder.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, LogLevel.Warning);
                builder.AddProvider(new CountingLogs());
            }
        );
    }
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
        Assert.True(SnapshotHeader.MaxWireSize + byte.MaxValue * (HullRecord.MaxWireSize - HullRecord.MaxHealth * 6) < 65536);
        Assert.Equal(11, (65536 - SnapshotHeader.MaxWireSize) / HullRecord.MaxWireSize);
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
        var record = new HullRecord
        {
            HasHealth = true,
            HealthCount = HullRecord.MaxHealth,
            LossCount = HullRecord.MaxLosses,
            Revision = HullRecord.MaxLosses,
            State = new HullState { Orientation = Quaternion.Identity, Jump = JumpPhase.Charge, JumpCalculation = calculation },
            Order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) }
        };
        for (var block = 0; block < record.HealthCount; block++)
        {
            record.DamagedBlocks[block] = (ushort)block;
            record.RemainingHealth[block] = 1;
        }
        record.Write(ref writer);
        Assert.Equal(5707, HullRecord.MaxWireSize);
        Assert.Equal(HullRecord.MaxWireSize, writer.Length);
        var reader = new NetReader(writer.Written);
        Assert.Equal(valid, HullRecord.TryRead(ref reader, out var copy));
        if (valid) Assert.Equal(calculation, copy.State.JumpCalculation);
        for (var length = 0; length < writer.Length; length++)
        {
            reader = new NetReader(writer.Written[..length]);
            Assert.False(HullRecord.TryRead(ref reader, out _));
        }
        Assert.Equal(42u, ProtocolInfo.Version);
    }

    [Fact]
    public void EditorMessagesHaveExactSizesAndRefuseEveryTruncationAndUnknownValue()
    {
        Span<byte> buffer = stackalloc byte[2048];
        Span<byte> hash = stackalloc byte[32];
        Span<byte> read = stackalloc byte[32];
        for (var i = 0; i < hash.Length; i++) hash[i] = (byte)(i + 1);

        // A commit is 42 bytes; the Unicode new-ship name now defines the maximum request.
        var writer = new NetWriter(buffer);
        new EditorRequestMessage { Kind = EditorRequestKind.Commit, Token = 7, Total = 1234 }.Write(ref writer, hash);
        Assert.Equal(135, EditorRequestMessage.MaxWireSize);
        Assert.Equal(42, writer.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(EditorRequestMessage.TryRead(ref reader, read, out var request));
        Assert.Equal((EditorRequestKind.Commit, 7u, 1234), (request.Kind, request.Token, request.Total));
        Assert.True(read.SequenceEqual(hash));
        for (var length = 1; length < writer.Length; length++)
        {
            reader = new NetReader(writer.Written[1..length]);
            Assert.False(EditorRequestMessage.TryRead(ref reader, read, out _));
        }
        foreach (var kind in new[] { EditorRequestKind.Begin, EditorRequestKind.Cancel, EditorRequestKind.Repair })
        {
            writer = new NetWriter(buffer);
            new EditorRequestMessage { Kind = kind, Hull = 9, Token = 9 }.Write(ref writer, hash);
            Assert.Equal(6, writer.Length);
            reader = new NetReader(writer.Written[1..]);
            Assert.True(EditorRequestMessage.TryRead(ref reader, read, out request));
            Assert.Equal((kind, 9u), (request.Kind, kind == EditorRequestKind.Begin ? request.Hull : request.Token));
            reader = new NetReader([.. writer.Written[1..], 0]);
            Assert.False(EditorRequestMessage.TryRead(ref reader, read, out _));
        }
        reader = new NetReader([5, 0, 0, 0, 0]);
        Assert.False(EditorRequestMessage.TryRead(ref reader, read, out _));

        // Chunks: the header, then exactly the bytes it counts, one to a chunk's worth.
        var bytes = new byte[EditorDataMessage.MaxChunk];
        writer = new NetWriter(buffer);
        EditorDataMessage.Write(ref writer, 7, 4096, bytes);
        Assert.Equal(EditorDataMessage.HeaderBytes + EditorDataMessage.MaxChunk, writer.Length);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(EditorDataMessage.TryReadHeader(ref reader, out var token, out var offset, out var count));
        Assert.Equal((7u, 4096, EditorDataMessage.MaxChunk), (token, offset, count));
        reader = new NetReader(writer.Written[1..^1]);
        Assert.False(EditorDataMessage.TryReadHeader(ref reader, out _, out _, out _));
        reader = new NetReader([.. writer.Written[1..], 0]);
        Assert.False(EditorDataMessage.TryReadHeader(ref reader, out _, out _, out _));
        writer = new NetWriter(buffer);
        EditorDataMessage.Write(ref writer, 7, 0, []);
        reader = new NetReader(writer.Written[1..]);
        Assert.False(EditorDataMessage.TryReadHeader(ref reader, out _, out _, out _));

        // The answer.
        writer = new NetWriter(buffer);
        new EditorStateMessage { Phase = EditorPhase.Applied, Reason = EditorReason.HullLimit, Token = 3, Hull = 0x00020005, ReplacedHull = 0x00010004, WaitSeconds = 30 }.Write(ref writer);
        Assert.Equal(18, EditorStateMessage.WireSize);
        Assert.Equal(EditorStateMessage.WireSize, writer.Length);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(EditorStateMessage.TryRead(ref reader, out var state));
        Assert.Equal((EditorPhase.Applied, EditorReason.HullLimit, 3u, 0x00020005u, 0x00010004u, (ushort)30), (state.Phase, state.Reason, state.Token, state.Hull, state.ReplacedHull, state.WaitSeconds));
        for (var length = 1; length < writer.Length; length++)
        {
            reader = new NetReader(writer.Written[1..length]);
            Assert.False(EditorStateMessage.TryRead(ref reader, out _));
        }
        var wrong = writer.Written.ToArray();
        wrong[1] = (byte)EditorPhase.Applied + 1;
        reader = new NetReader(wrong.AsSpan(1));
        Assert.False(EditorStateMessage.TryRead(ref reader, out _));
        wrong[1] = 0;
        // A reason only a client gives itself never comes from a server.
        wrong[2] = (byte)EditorReason.Unavailable;
        reader = new NetReader(wrong.AsSpan(1));
        Assert.False(EditorStateMessage.TryRead(ref reader, out _));

        // A design's announcement and the client's word on it.
        writer = new NetWriter(buffer);
        DesignNoticeMessage.Write(ref writer, 200, 32768, hash);
        Assert.Equal(38, DesignNoticeMessage.WireSize);
        Assert.Equal(DesignNoticeMessage.WireSize, writer.Length);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(DesignNoticeMessage.TryRead(ref reader, out var design, out var size, read));
        Assert.Equal((200, 32768), (design, size));
        Assert.True(read.SequenceEqual(hash));
        reader = new NetReader(writer.Written[1..^1]);
        Assert.False(DesignNoticeMessage.TryRead(ref reader, out _, out _, read));
        writer = new NetWriter(buffer);
        DesignReadyMessage.Write(ref writer, 200, true);
        Assert.Equal(3, DesignReadyMessage.WireSize);
        Assert.Equal(DesignReadyMessage.WireSize, writer.Length);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(DesignReadyMessage.TryRead(ref reader, out design, out var ready));
        Assert.Equal((200, true), (design, ready));
        reader = new NetReader([200, 2]);
        Assert.False(DesignReadyMessage.TryRead(ref reader, out _, out _));
        reader = new NetReader([200]);
        Assert.False(DesignReadyMessage.TryRead(ref reader, out _, out _));

        // What a client may send in one tick besides social traffic still fits the server's allowance for it.
        Assert.Equal(EditorRequestMessage.MaxWireSize + DesignReadyMessage.WireSize, ClientSession.EditorBytesPerTick);
        Assert.True(ClientSession.SocialMessagesPerTick * SocialMessage.MaxWireSize + FleetOrdersMessage.MaxWireSize + 5 + ClientSession.EditorBytesPerTick <= ServerHost.PlayerBytesPerTick);
        Assert.True(ClientSession.SocialMessagesPerTick + 4 <= ServerHost.PlayerMessagesPerTick);
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
    private ulong[] _blockWords = [];
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
            HasHealth = true,
            State = state,
        };
    }

    private static void Lose(ref HullRecord hull, params ReadOnlySpan<int> blocks)
    {
        foreach (var block in blocks) hull.Losses[hull.LossCount++] = block;
        hull.Revision = hull.LossCount;
    }

    private static ulong Mask(HullRecord hull)
    {
        var mask = Content.Designs[hull.Design].FullMask();
        for (var i = 0; i < hull.LossCount; i++) mask &= ~(1UL << hull.Losses[i]);
        return mask;
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
        _world.BuildViews(_views, ref _blockWords, _serverTick, _serverTick, out var own);
        return _views[own];
    }

    private void AssertPredictionCopy(int slot, BlockStateHandle authoritative)
    {
        var source = _world.BlockStates;
        var predicted = _world.Near.BlockStates;
        var copy = _world.Near.Info(slot).BlockState;
        Assert.NotSame(source, predicted);
        Assert.True(_world.Near.Info(slot).OwnsBlockState);
        Assert.True(predicted.IsLive(copy));
        Assert.False(source.Alive(authoritative).Overlaps(predicted.Alive(copy)));
        Assert.False(source.HitPoints(authoritative).Overlaps(predicted.HitPoints(copy)));
        Assert.True(BlockSet.Equal(source.Alive(authoritative), predicted.Alive(copy)));
        Assert.True(source.HitPoints(authoritative).SequenceEqual(predicted.HitPoints(copy)));
        Assert.Equal(source.Revision(authoritative), predicted.Revision(copy));
    }

    [Fact]
    public void ShieldAgesRebaseOnSnapshotTimeThenReplayPendingPredictedTicks()
    {
        var hull = Hull(OwnHull, Player, Content.PodDesign, Double3.Zero);
        hull.State.ShieldHit = new ShieldHit { Serial = 1, Strength = 50, AgeTicks = 10 };
        hull.State.ShieldChangeTicks = 10;
        Snapshot(100, 0, hull);
        Assert.Equal(10 * HullSim.Dt, Own().ShieldHitAge);
        for (var i = 0; i < 4; i++)
            Tick();
        // The first sample targets 104 (estimated server tick 101 plus three ticks of lead).
        // Four samples reach 107; fallback intervals 101–103 age the shield too.
        Assert.Equal(107d, Own().ShownTick);
        Assert.Equal((10 + 107 - 100) * HullSim.Dt, Own().ShieldChangeAge);

        // No input targeted 101. Its authoritative age advances once, then intervals 102–107 replay.
        hull.State.ShieldHit.AgeTicks = 11;
        hull.State.ShieldChangeTicks = 11;
        Snapshot(101, 0, hull);
        Assert.Equal((11 + 107 - 101) * HullSim.Dt, Own().ShieldHitAge);
        Assert.Equal((11 + 107 - 101) * HullSim.Dt, Own().ShieldChangeAge);
        Tick();
        Assert.Equal((11 + 108 - 101) * HullSim.Dt, Own().ShieldHitAge);

        // A later authoritative reset replaces the predicted clock, including during transit.
        hull.State.ShieldHit.AgeTicks = 0;
        hull.State.ShieldChangeTicks = 0;
        hull.State.Jump = JumpPhase.Tunnel;
        hull.State.JumpTunnelSeconds = 600;
        Snapshot(102, 0, hull);
        Assert.Equal((108 - 102) * HullSim.Dt, Own().ShieldChangeAge);
        Tick();
        Assert.Equal((109 - 102) * HullSim.Dt, Own().ShieldChangeAge);
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
        Snapshot(_serverTick, 0, rest);
        Assert.Equal(Vector3.Zero, Own().Velocity);

        // Something ran into the pod on the server. No input of the player's had a part in it, so the
        // acknowledgement stands where it was; the tick is what makes this the newer truth.
        var struck = rest;
        struck.State.Velocity = new Vector3(12, 0, 0);
        struck.State.Shield = pod.ShieldCapacity * 0.5f;
        // Three client samples reach 106; the correction at 104 replays intervals 105 and 106.
        var expected = struck.State;
        var order = default(HullOrder);
        for (var tick = 105; tick <= 106; tick++)
        {
            HullSim.AdvanceShieldClock(ref expected);
            HullSim.Step(ref expected, ref order, InputTimeline.Fallback, pod, tick);
        }
        Snapshot(_serverTick + 1, 0, struck);
        Assert.Equal(106d, Own().ShownTick);
        Assert.Equal(expected.Position, _world.Near.State(0).Position);
        Assert.Equal(expected.Velocity, Own().Velocity);
        Assert.Equal(expected.Shield, Own().Shield);

        // The snapshot from before the blow turns up late. It is older, whatever it acknowledges.
        Snapshot(_serverTick, 0, rest);
        Assert.Equal(expected.Velocity, Own().Velocity);
        Assert.Equal(expected.Shield, Own().Shield);

        // So does the very same one again.
        var twice = struck;
        twice.State.Velocity = new Vector3(-30, 0, 0);
        Snapshot(_serverTick + 1, 0, twice);
        Assert.Equal(expected.Velocity, Own().Velocity);
    }

    [Fact]
    public void ALateSnapshotThatCostsANearHullABlockLeavesItsPredictedFiguresFittingItsBlocks()
    {
        var design = Content.Designs[Content.PodDesign];
        var own = Hull(OwnHull, Player, Content.PodDesign, Double3.Zero);
        var other = Hull(OwnHull + 1, Player + 1, Content.PodDesign, new Double3(10, 0, 0));
        Snapshot(100, 0, own, other);

        // A newer snapshot with the player's hull alone is taken first; the other hull is still predicted.
        Snapshot(102, 0, own);
        Assert.Equal(2, _world.Near.Count);

        // Then the snapshot from before it arrives, in which the other hull has lost a block. It is too old to
        // rebuild the prediction from, and it is still the newest word on the other hull.
        var lost = -1;
        for (var b = 0; b < design.BlockCount; b++)
        {
            if (!BlockSet.Has(design.CoreSet, b))
                lost = b;
        }

        Assert.True(lost >= 0);
        Lose(ref other, lost);
        other.State.Position = new Double3(15, 0, 0);
        Snapshot(101, 0, own, other);

        // The isolated prediction copy must receive the late loss and refit to the same geometry.
        Assert.Equal(2, _world.Near.Count);
        Assert.True(_world.TryGetBlockState(OwnHull + 1, out var state));
        Assert.Equal(1u, _world.BlockStates.Revision(state));
        AssertPredictionCopy(1, state);
        design.Evaluate(_world.BlockStates.Alive(state), out var fitting, out _);
        Assert.True(fitting.Mass < design.Flight.Mass);
        Assert.Equal(fitting.Mass, _world.Near.Flight(1).Mass);
        Assert.Equal(fitting.Inertia, _world.Near.Flight(1).Inertia);
        Assert.Equal(1u, _world.Near.Info(1).FittedRevision);
        Assert.Equal(other.State.Position, _world.Near.State(1).Position);
    }

    [Fact]
    public void AKnownHullsBlockStateIsKeptAcrossSnapshotsAndCopiedIntoIsolatedPrediction()
    {
        var own = Hull(OwnHull, Player, Content.PodDesign, Double3.Zero);
        var other = Hull(OwnHull + 1, Player + 1, Content.PodDesign, new Double3(10, 0, 0));
        Snapshot(_serverTick, 0, own, other);

        // One block state per hull the world knows, taken when it first heard of the hull.
        var states = _world.BlockStates;
        Assert.True(_world.TryGetBlockState(OwnHull, out var ownState));
        Assert.True(_world.TryGetBlockState(OwnHull + 1, out var otherState));
        Assert.False(_world.TryGetBlockState(OwnHull + 2, out _));
        Assert.Equal(2, states.Count);
        var ownBlocks = states.Alive(ownState);
        var otherBlocks = states.Alive(otherState);
        Assert.True(states.HitPoints(ownState).SequenceEqual(Content.Pod.IntactHitPoints));
        Assert.True(states.HitPoints(otherState).SequenceEqual(Content.Pod.IntactHitPoints));

        // Prediction owns independent copies; rebuilding them must not move authoritative storage.
        void Copied()
        {
            Assert.Equal(2, _world.Near.Count);
            AssertPredictionCopy(0, ownState);
            AssertPredictionCopy(1, otherState);
            Assert.Equal(2, _world.Near.BlockStates.Count);
            Assert.Equal(2, states.Count);
        }

        void Kept(BlockStateHandle state, ReadOnlySpan<ulong> where, uint id, uint revision)
        {
            Assert.True(_world.TryGetBlockState(id, out var now));
            Assert.Equal(state, now);
            Assert.True(states.IsLive(state));
            Assert.True(where.Overlaps(states.Alive(state), out var offset));
            Assert.Equal(0, offset);
            Assert.Equal(revision, states.Revision(state));
        }

        Copied();

        // Rebuilding prediction leaves authoritative handles and their memory unchanged, with no losses.
        for (var i = 1; i <= 8; i++)
        {
            Tick();
            Snapshot(_serverTick, (uint)i, own, other);
            Copied();
            Kept(ownState, ownBlocks, OwnHull, 0);
            Kept(otherState, otherBlocks, OwnHull + 1, 0);
        }

        Assert.Equal(Content.Pod.FullMask(), Own().Mask());
        Assert.Equal(0u, Own().Revision);

        // The server says both have lost blocks. The blocks leave the stored sets where they are, each loss is
        // counted, and the player's hull flies on what is left.
        Lose(ref own, 5, 6);
        Lose(ref other, 3);
        Tick();
        Snapshot(_serverTick, 9, own, other);
        Copied();
        Kept(ownState, ownBlocks, OwnHull, 2);
        Kept(otherState, otherBlocks, OwnHull + 1, 1);
        Assert.Equal(Mask(own), BlockMasks.Word(ownBlocks));
        Assert.Equal(Mask(other), BlockMasks.Word(otherBlocks));
        Content.Pod.Evaluate(ownBlocks, out var left, out _);
        Assert.Equal(left, _world.Flight);
        Assert.True(left.Mass < Content.Pod.Flight.Mass);
        Assert.Equal(left, _world.Near.Flight(0));

        // The same masks again are no further loss, and a block the hull lost stays lost.
        Tick();
        Snapshot(_serverTick, 10, own, other);
        Kept(ownState, ownBlocks, OwnHull, 2);
        Kept(otherState, otherBlocks, OwnHull + 1, 1);

        // What is published is a copy with the count beside it, not the stored memory.
        var view = Own();
        Assert.Equal(Mask(own), view.Mask());
        Assert.Equal(2u, view.Revision);
        Assert.False(view.Blocks.Span.Overlaps(ownBlocks));
    }

    [Fact]
    public void AnExistingHullCannotRebindItsDesignOrReplaceItsBlockState()
    {
        var own = Hull(OwnHull, Player, Content.PodDesign, Double3.Zero);
        var other = Hull(OwnHull + 1, Player + 1, Content.PodDesign, new Double3(10, 0, 0));
        Snapshot(_serverTick, 0, own, other);
        Assert.True(_world.TryGetBlockState(OwnHull + 1, out var first));
        Assert.Equal(2, _world.Near.Count);

        // A hostile design change must not reinterpret the existing block ids or release their storage.
        var changed = Hull(OwnHull + 1, Player + 1, Content.DroneDesign, new Double3(10, 0, 0));
        var writer = new NetWriter(_buffer);
        new SnapshotHeader { ServerTick = _serverTick + 1, AckedInput = 0, OwnHull = OwnHull, HullCount = 1 }.Write(ref writer);
        changed.Write(ref writer);
        var reader = new NetReader(writer.Written[1..]);
        _world.OnSnapshot(ref reader, _serverTick);

        Assert.True(_world.TryGetBlockState(OwnHull + 1, out var second));
        Assert.Equal(first, second);
        Assert.True(_world.BlockStates.IsLive(first));
        Assert.Equal(Content.Pod.BlockCount, _world.BlockStates.Blocks(second));
        Assert.Equal(2, _world.BlockStates.Count);
        Assert.Equal(2, _world.Near.Count);
        Tick();

        // The original immutable binding continues to predict after the rejected packet.
        Snapshot(_serverTick + 2, 1, own, other);
        Assert.Equal(2, _world.Near.Count);
        Assert.True(_world.TryGetBlockState(OwnHull + 1, out var stillKnown));
        Assert.Equal(second, stillKnown);
        Assert.Equal((byte)Content.PodDesign, _world.Near.Info(1).Design);
        AssertPredictionCopy(1, second);
        Tick();
    }

    [Fact]
    public void ApplyingSnapshotsAndPublishingViewsTakesNoMemoryOnceWarmEvenWhenBlocksAreLost()
    {
        // The losses happen once in a run, so a run in doubt is made again from the start, by a fresh client world.
        var allocation = new AllocationProbe();
        while (allocation.Run())
        {
            using var run = new ClientPredictionTests();
            run.ApplyAndPublishLosingBlocks(allocation);
        }

        Assert.Equal(0L, allocation.Bytes);
    }

    private void ApplyAndPublishLosingBlocks(AllocationProbe allocation)
    {
        HullRecord[] hulls =
        [
            Hull(OwnHull, Player, Content.PodDesign, Double3.Zero),
            Hull(OwnHull + 1, Player + 1, Content.PodDesign, new Double3(10, 0, 0)),
            Hull(OwnHull + 2, Player, Content.StartingFleet[0], new Double3(0, 0, 40)),
        ];
        for (var i = 1; i <= 200; i++)
        {
            Tick();

            // Halfway, two of the hulls lose blocks, and later the third: the stored sets change in place, the
            // sums follow and the figures published with each view are read off them again.
            if (i == 120)
            {
                Lose(ref hulls[0], 5);
                Lose(ref hulls[1], 3, 4);
            }

            if (i == 160)
                Lose(ref hulls[2], 7);

            // The first sixty are the warm-up; from there every step is measured.
            if (i > 60)
                allocation.Start();
            Snapshot(_serverTick, (uint)i, hulls);
            var count = _world.BuildViews(_views, ref _blockWords, _serverTick, _serverTick, out _);
            if (i > 60)
                allocation.Stop();
            Assert.Equal(3, count);
        }

        Assert.Equal(1u, Own().Revision);
        Assert.Equal(3, _world.BlockStates.Count);

        // What was published without taking memory is each hull's data sheet for the blocks it has left.
        _world.BuildViews(_views, ref _blockWords, _serverTick, _serverTick, out _);
        foreach (var hull in hulls)
        {
            var view = _views.First(v => v.Id == hull.Id);
            Figures.AssertIdentical(Content.Designs[hull.Design].StatsOf([Mask(hull)]), view.Stats);
            Assert.True(view.Stats.Blocks < Content.Designs[hull.Design].BlockCount);
        }
    }

    [Fact]
    public void AHullFirstHeardOfWithLossesAndOneThatLosesThemOverSnapshotsHaveTheSameFigures()
    {
        var pod = Content.Pod;
        var own = Hull(OwnHull, Player, Content.PodDesign, Double3.Zero);
        var early = Hull(OwnHull + 1, Player + 1, Content.PodDesign, new Double3(-400, 0, 0));
        var late = Hull(OwnHull + 2, Player + 1, Content.PodDesign, new Double3(400, 0, 0));

        HullView View(uint id)
        {
            var count = _world.BuildViews(_views, ref _blockWords, _serverTick, _serverTick, out _);
            return _views.Take(count).First(v => v.Id == id);
        }

        // Intact, every hull is published with its design's own data sheet.
        Snapshot(_serverTick, 0, own, early);
        Figures.AssertIdentical(pod.Stats, View(OwnHull).Stats);
        Figures.AssertIdentical(pod.Stats, View(OwnHull + 1).Stats);

        // One hull is known from the start and loses three blocks over three snapshots, the highest first.
        uint acked = 0;
        foreach (var block in new[] { 6, 3, 5 })
        {
            Lose(ref early, block);
            Tick();
            Snapshot(_serverTick, ++acked, own, early);
        }

        // The other is first heard of with the same three blocks gone already.
        late.Losses = early.Losses;
        late.LossCount = early.LossCount;
        late.Revision = early.Revision;
        Tick();
        Snapshot(_serverTick, ++acked, own, early, late);

        var states = _world.BlockStates;
        Assert.True(_world.TryGetBlockState(OwnHull + 1, out var earlyState));
        Assert.True(_world.TryGetBlockState(OwnHull + 2, out var lateState));
        Assert.Equal(3u, states.Revision(earlyState));
        Assert.Equal(3u, states.Revision(lateState));
        Assert.True(BlockSet.Equal(states.Alive(earlyState), states.Alive(lateState)));
        Assert.True(Figures.SameSums(states.Sums(earlyState), states.Sums(lateState)), "the two hulls have the same blocks and different sums");

        // What is published with each view is what its sums say, and what sums built afresh for those blocks say.
        pod.Figures(states.Sums(earlyState), out _, out var fromSums);
        var rebuilt = pod.StatsOf([Mask(early)]);
        Figures.AssertIdentical(fromSums, View(OwnHull + 1).Stats);
        Figures.AssertIdentical(fromSums, View(OwnHull + 2).Stats);
        Figures.AssertIdentical(rebuilt, fromSums);
        Assert.Equal(pod.BlockCount - 3, fromSums.Blocks);
        Assert.True(fromSums.Mass < pod.Stats.Mass);

        // The player's own hull loses a block: its view and its flight figures follow in the same snapshot.
        Lose(ref own, 4);
        Tick();
        Snapshot(_serverTick, ++acked, own, early, late);
        Assert.True(_world.TryGetBlockState(OwnHull, out var ownState));
        pod.Figures(states.Sums(ownState), out var ownFlight, out var ownStats);
        Figures.AssertIdentical(ownStats, View(OwnHull).Stats);
        Figures.AssertIdentical(ownFlight, _world.Flight);
        Figures.AssertIdentical(pod.StatsOf([Mask(own)]), ownStats);
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
