using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Input deadlines, atomic admission and world-time scheduling.</summary>
public sealed class InputTimelineTests
{
    [Fact]
    public void AdmissionIsAtomicTargetTaggedAndOverflowSafe()
    {
        var window = new InputTimeline();
        var record = new InputRecord { TargetTick = 110, Sequence = 8, OrderId = 1 };
        Assert.True(window.Accept([record], 100));
        Assert.True(window.Accept([record], 100));
        Assert.True(window.Accept([new() { TargetTick = 105, Sequence = 2 }], 100));
        var conflict = record;
        conflict.Input.Boost = true;
        Assert.False(window.Accept([new() { TargetTick = 106 }, conflict], 100));
        Assert.False(window.Take(106, out _));
        Assert.True(window.Take(105, out _));
        Assert.True(window.Take(110, out var actual));
        Assert.False(actual.Input.Boost);
        Assert.False(window.Accept([new() { TargetTick = long.MaxValue }], 100));
        Assert.False(window.Accept([new() { TargetTick = -1 }], 100));
        Assert.True(window.Accept([record], 111));
        Assert.False(window.Take(142, out _));
        Assert.True(window.Accept([new() { TargetTick = long.MaxValue }], long.MaxValue - 31));
        Assert.True(InputTimeline.Newer(0, ushort.MaxValue));
        Assert.False(InputTimeline.Newer(ushort.MaxValue, 0));
    }

    [Fact]
    public void WireCarriesEveryOrderAndEpochWithinMaximumSize()
    {
        var bytes = new byte[InputMessage.MaxWireSize];
        var records = new InputRecord[4];
        for (var i = 0; i < 4; i++)
            records[i] = new InputRecord { TargetTick = 50 + i, OrderId = (ushort)(i + 1), Order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(i, i, i) } };
        var writer = new NetWriter(bytes);
        InputMessage.Write(ref writer, records, 71);
        Assert.Equal(bytes.Length, writer.Length);
        var reader = new NetReader(writer.Written[1..]);
        Span<InputRecord> decoded = stackalloc InputRecord[4];
        Assert.True(InputMessage.TryRead(ref reader, decoded, out var count, out var epoch));
        Assert.Equal(4, count);
        Assert.Equal(71u, epoch);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(records[i].TargetTick, decoded[i].TargetTick);
            Assert.Equal(records[i].Order, decoded[i].Order);
        }
    }

    private static HullStore Hulls(ServerHost host) => (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;

    [Fact]
    public void TwoPlayersUnequalBatchesProduceIdenticalPhysicalIntervals()
    {
        var content = GameContent.Load();
        using var single = new ScriptedServer(content, maxPlayers: 2);
        using var batch = new ScriptedServer(content, maxPlayers: 2);
        var a = single.Join(1);
        var b = single.Join(2);
        var c = batch.Join(1);
        var d = batch.Join(2);
        var left = Hulls(single.Server);
        var right = Hulls(batch.Server);
        Assert.Equal(single.Server.Status.Tick, batch.Server.Status.Tick);
        // Both peers' intent is known. This checks authoritative scheduling, not prediction of unknown remote controls.
        for (var i = 0; i < left.Slots; i++)
        {
            var position = new Double3(i * 4, 0, 0);
            left.State(i).Position = right.State(i).Position = position;
        }
        var start = single.Server.Status.Tick;
        for (var interval = 0; interval < 120; interval++)
        {
            Send(single, a, start + interval, 1);
            Send(single, b, start + interval, -1);
            if (interval % 2 == 0)
            {
                // Later target arrives first, and player two arrives before player one.
                Send(batch, d, start + interval + 1, -1);
                Send(batch, c, start + interval + 1, 1);
                Send(batch, d, start + interval, -1);
                Send(batch, c, start + interval, 1);
            }
            single.Tick();
            batch.Tick();
            Assert.Equal(left.Slots, right.Slots);
            for (var i = 0; i < left.Slots; i++)
            {
                Assert.Equal(left.HandleAt(i), right.HandleAt(i));
                Assert.Equal(left.State(i), right.State(i));
                Assert.Equal(left.Info(i).Order, right.Info(i).Order);
                Assert.Equal(left.Revision(i), right.Revision(i));
            }
        }
    }

    [Fact]
    public void ReturningToTheSameHullRejectsOldEpochInputs()
    {
        var content = GameContent.Load();
        using var rig = new ScriptedServer(content);
        var peer = rig.Join(1);
        var hulls = Hulls(rig.Server);
        var own = hulls.HandleAt(0).Packed;
        var fleet = hulls.HandleAt(1).Packed;
        SnapshotHeader latest = default;
        rig.Net.ObserveSnapshot = (int _, ReadOnlySpan<byte> bytes) =>
        {
            var reader = new NetReader(bytes[1..]);
            Assert.True(SnapshotHeader.TryRead(ref reader, out latest));
        };
        Control(fleet);
        rig.Tick();
        Assert.Equal(2u, latest.ControlEpoch);
        Control(own);
        rig.Tick();
        Assert.Equal(3u, latest.ControlEpoch);
        Assert.Equal(own, latest.OwnHull);
        Send(rig, peer, rig.Server.Status.Tick, 127);
        rig.Tick();
        Assert.Equal(0u, latest.AckedInput);
        Assert.Equal(default, hulls.State(0).Velocity);

        void Control(uint id)
        {
            var bytes = new byte[5];
            var writer = new NetWriter(bytes);
            writer.WriteByte((byte)MessageId.Control);
            writer.WriteUInt32(id);
            rig.Net.Deliver(peer, bytes, writer.Length, NetChannel.Reliable);
        }
    }

    [Fact]
    public void ReusedSlotIsNotACollisionParticipantUntilTheNextRound()
    {
        var content = GameContent.Load();
        using var hulls = new HullStore();
        var own = hulls.Create(1, (byte)content.PodDesign, content.Pod, Double3.Zero);
        var old = hulls.Create(2, (byte)content.PodDesign, content.Pod, Double3.Zero);
        var field = new HullField(hulls, content.Designs);
        field.BeginRound();
        hulls.Destroy(old);
        var replacement = hulls.Create(2, (byte)content.PodDesign, content.Pod, Double3.Zero);
        Assert.Equal(old.Index, replacement.Index);
        Assert.NotEqual(old.Generation, replacement.Generation);
        var before = hulls.State(replacement.Index);
        field.Step(own.Index, InputTimeline.Fallback, tick: 1);
        Assert.Equal(before, hulls.State(replacement.Index));
    }

    private static void Send(ScriptedServer rig, int peer, long target, sbyte forward)
    {
        var bytes = new byte[InputMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        InputMessage.Write(ref writer, [new InputRecord { TargetTick = target, Sequence = (uint)target, Input = new FlightInput { Forward = forward, Dampeners = true } }], 1);
        rig.Net.Deliver(peer, bytes, writer.Length, NetChannel.Unreliable);
    }
}
