using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Event ages follow snapshot time even when the server consumes an irregular input stream.</summary>
public sealed class ShieldClockTests
{
    [Theory]
    [InlineData(10, false)]
    [InlineData(0, false)]
    [InlineData(10, true)]
    public void SparseAndStarvedInputsAgePilotAndFleetOnSnapshotTime(int interval, bool tunnel)
    {
        CheckClock(interval, tunnel, false);
    }

    [Fact]
    public void MultipleQueuedInputsAgeOnlyOnceInTheirAuthoritativeTick()
    {
        CheckClock(0, false, true);
    }

    private static void CheckClock(int interval, bool tunnel, bool batch)
    {
        var content = GameContent.Load();
        using var rig = new ScriptedServer(content);
        var peer = rig.Join(0x1_C000_0201);
        var observerPeer = rig.Join(0x1_C000_0202);
        // Accumulate input credit without advancing physical flight.
        rig.Tick(8);
        var store = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Server)!;
        var eventTick = 9L;
        var pilot = -1;
        for (var i = 0; i < store.Slots; i++)
        {
            if (!store.IsAliveAt(i)) continue;
            if (store.Info(i).Piloted && pilot < 0) pilot = i;
            ref var state = ref store.State(i);
            state.Position = new Double3(i * 1000, 100000, 0);
            var body = new HullBody { Design = content.Designs[store.Info(i).Design], Alive = store.Blocks(i) };
            state.Shield = 1;
            HullSim.Damage(ref state, store.Flight(i), ref body, [1], 1, 1);
            state.ShieldCooldown = 1000;
            if (tunnel)
            {
                state.Jump = JumpPhase.Tunnel;
                state.JumpTunnelSeconds = 600;
                store.Info(i).Order = new HullOrder { Kind = OrderKind.Jump, Target = state.Position + new Double3(10000, 0, 0) };
            }
        }
        Assert.True(pilot >= 0);
        using var observer = new ClientWorld(content);
        observer.SetPlayer(uint.MaxValue);
        using var lateObserver = new ClientWorld(content);
        var lateSaw = false;
        var words = Array.Empty<ulong>();
        var views = new HullView[ClientWorld.MaxViews];
        var snapshots = 0;
        rig.Net.ObserveSnapshot = (who, bytes) =>
        {
            if (who != peer && who != observerPeer) return;
            var reader = new NetReader(bytes[1..]);
            Assert.True(SnapshotHeader.TryRead(ref reader, out var header));
            var expected = (byte)Math.Min(120, header.ServerTick - eventTick);
            for (var i = 0; i < header.HullCount; i++)
            {
                Assert.True(HullRecord.TryRead(ref reader, out var record));
                Assert.Equal(expected, record.State.ShieldHit.AgeTicks);
                Assert.Equal(expected, record.State.ShieldChangeTicks);
            }
            if (who != observerPeer || header.HullCount < store.Slots) return;
            // A first-sight observer receives the same wire bytes, using its delayed display moment.
            reader = new NetReader(bytes[1..]);
            observer.OnSnapshot(ref reader, header.ServerTick);
            var count = observer.BuildViews(views, ref words, header.ServerTick + 6, header.ServerTick, out _);
            Assert.True(count > 1);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal(expected * HullSim.Dt, views[i].ShieldHitAge, 5);
                Assert.Equal(expected * HullSim.Dt, views[i].ShieldChangeAge, 5);
            }
            if (header.ServerTick - eventTick > 130)
            {
                reader = new NetReader(bytes[1..]);
                lateObserver.OnSnapshot(ref reader, header.ServerTick);
                count = lateObserver.BuildViews(views, ref words, header.ServerTick + 6, header.ServerTick, out _);
                Assert.True(count > 1);
                for (var i = 0; i < count; i++)
                {
                    Assert.True(views[i].ShieldHitAge >= 2);
                    Assert.True(views[i].ShieldChangeAge >= 2);
                }
                lateSaw = true;
            }
            snapshots++;
        };
        uint sequence = 0;
        for (var elapsed = 1; elapsed <= (batch ? 3 : 150); elapsed++)
        {
            if (batch && elapsed == 1)
            {
                var records = new InputRecord[4];
                for (var j = 0; j < records.Length; j++)
                    records[j] = new InputRecord { Sequence = ++sequence, Input = new FlightInput { Dampeners = true } };
                var buffer = new byte[256];
                var writer = new NetWriter(buffer);
                InputMessage.Write(ref writer, records, 0, default);
                rig.Net.Deliver(peer, buffer, writer.Length, NetChannel.Unreliable);
            }
            else if (interval > 0 && elapsed % interval == 0)
                rig.Input(peer, ++sequence);
            rig.Tick();
        }
        Assert.True(snapshots > 0);
        if (batch) Assert.Equal(4u, rig.Net[peer].AckedInput);
        else Assert.True(lateSaw);
        // Recharge/cooldown still follow physical steps, not the cosmetic world clock.
        var steps = batch ? 4 : interval > 0 ? 150 / interval + 4 : 150 - 5;
        var expectedCooldown = 1000f;
        if (!tunnel)
            for (var i = 0; i < steps; i++) expectedCooldown -= HullSim.Dt;
        Assert.Equal(expectedCooldown, store.State(pilot).ShieldCooldown, 3);
        Assert.Null(rig.Server.Fault);
    }
}
