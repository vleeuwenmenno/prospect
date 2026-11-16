using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class WreckageTests
{
    internal static GameContent Content()
    {
        var content = GameContent.Load();
        DesignBox[] boxes = new DesignBox[7];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new(new GridSize(i, 0, 0), new GridSize(1, 1, 1), content.Blocks.Get(i == 0 ? "core" : i == 6 ? "nitrogenTank" : "hull"));
        var design = HullDesign.Of(new BoxDesign("Wreck fixture", 1, boxes, content.Blocks.Limits, "test"), content.Rules);
        return content.WithCatalogue([.. content.Designs, design], content.Designs.Length, content.DroneDesign, []);
    }

    internal static Action Tick(ServerHost server) => typeof(ServerHost).GetMethod("TickWreckage", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(server);

    internal static int Wreck(HullStore hulls)
    {
        for (var i = 0; i < hulls.Slots; i++) if (hulls.IsAliveAt(i) && hulls.Info(i).Wreck) return i;
        Assert.Fail("No wreck was created.");
        return -1;
    }

    [Fact]
    public void WreckHullUsesExistingWireRecordWithoutAnOwner()
    {
        var content = Content();
        var record = new HullRecord { Id = 65536, Owner = 0, Design = (byte)content.PodDesign, State = HullState.Spawn(default, content.Pod.Flight) };
        record.State.Energy = record.State.Shield = 0;
        Span<byte> bytes = stackalloc byte[HullRecord.MaxWireSize];
        var writer = new NetWriter(bytes);
        record.Write(ref writer);
        var reader = new NetReader(writer.Written);
        Assert.True(HullRecord.TryRead(ref reader, out var copy));
        Assert.Equal(0u, copy.Owner);
        Assert.Equal(record.Design, copy.Design);
        Assert.Equal(0, copy.State.Energy);
        Assert.Equal(0, copy.State.Shield);
    }

    [Fact]
    public void MutableSettingsWireRoundTripRejectsTruncationAndInvalidValues()
    {
        Span<byte> bytes = stackalloc byte[WreckSettingsMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        var settings = new WreckSettingsMessage(0, 7);
        settings.Write(ref writer);
        Assert.Equal(bytes.Length, writer.Length);
        var reader = new NetReader(bytes[1..]);
        Assert.True(WreckSettingsMessage.TryRead(ref reader, out var read));
        Assert.Equal(settings, read);
        for (var length = 0; length < bytes.Length - 1; length++)
        {
            reader = new NetReader(bytes.Slice(1, length));
            Assert.False(WreckSettingsMessage.TryRead(ref reader, out _));
        }
        Assert.False(new WreckSettingsMessage(-1, 7).IsValid);
        Assert.False(new WreckSettingsMessage(1, 0).IsValid);
        Assert.False(new WreckSettingsMessage(1, 256).IsValid);
    }

    [Fact]
    public void LifetimePausesNearPlayerAndSectorCapRemovesOldest()
    {
        var content = Content();
        using var rig = new SaveRig(SaveRig.NewWorld(content), content, edit: snapshot =>
        {
            snapshot.SettingValues[Array.IndexOf(snapshot.SettingIds, "wreckLifetimeMinutes")] = 1;
            snapshot.SettingValues[Array.IndexOf(snapshot.SettingIds, "wreckMaxPerSector")] = 1;
            return snapshot;
        });
        rig.Join();
        var tick = Tick(rig.Server);
        rig.Hulls.BlockStates.Lose(rig.Hulls.Info(0).BlockState, 2);
        tick();
        var wreck = Wreck(rig.Hulls);
        var handle = rig.Hulls.HandleAt(wreck);
        rig.Hulls.Info(wreck).WreckAge = 3599;
        tick();
        Assert.True(rig.Hulls.IsAlive(handle));
        Assert.Equal(3599, rig.Hulls.Info(wreck).WreckAge);
        rig.Hulls.BlockStates.Lose(rig.Hulls.Info(wreck).BlockState, 4);
        tick();
        Assert.False(rig.Hulls.IsAlive(handle));
        wreck = Wreck(rig.Hulls);
        handle = rig.Hulls.HandleAt(wreck);
        rig.Hulls.Info(wreck).WreckAge = 3599;
        rig.Hulls.State(wreck).Position += new Vector3(20000, 0, 0);
        tick();
        Assert.False(rig.Hulls.IsAlive(handle));
    }

    [Fact]
    public void SplitKeepsHealthFuelSpinAndPointVelocityThenSplitsAgainAndRemovesEmptyWreck()
    {
        var content = Content();
        using var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        var hulls = rig.Hulls;
        var source = hulls.Info(0).BlockState;
        hulls.BlockStates.SetHitPoints(source, 6, 17);
        hulls.BlockStates.TankLevels(source)[0] = 12.345;
        hulls.State(0).AngularVelocity = new Vector3(0, 1, 0);
        hulls.State(0).Velocity = new Vector3(2, 3, 4);
        hulls.BlockStates.Lose(source, 2);
        rig.Field.Refit(0);
        var parent = hulls.State(0);
        var center = hulls.Flight(0).CenterOfMass;
        Tick(rig.Server)();
        var wreck = Wreck(hulls);
        var info = hulls.Info(wreck);
        Assert.Equal(0u, info.Owner);
        Assert.False(info.Piloted);
        Assert.Equal(hulls.Info(0).Design, info.Design);
        Assert.Equal(parent.Position, hulls.State(wreck).Position);
        Assert.Equal(parent.AngularVelocity, hulls.State(wreck).AngularVelocity);
        Assert.Equal(parent.Velocity + HullRotation.Rotate(Vector3.Cross(parent.AngularVelocity, hulls.Flight(wreck).CenterOfMass - center), parent.Orientation), hulls.State(wreck).Velocity);
        Assert.Equal(17, hulls.BlockHp(wreck)[6]);
        Assert.Equal(12.345, hulls.BlockStates.Tanks(info.BlockState)[0]);
        Assert.Equal(0, hulls.BlockStates.Tanks(source)[0]);
        Assert.Equal(0, hulls.Flight(wreck).PowerGenerated);
        Assert.Equal(0, hulls.Flight(wreck).ShieldCapacity);
        Assert.False(BlockSet.Has(hulls.Blocks(0), 6));
        hulls.BlockStates.Lose(info.BlockState, 4);
        Tick(rig.Server)();
        Assert.Equal(3, hulls.Count);
        var handle = hulls.HandleAt(wreck);
        for (var i = 0; i < 7; i++) hulls.BlockStates.Lose(info.BlockState, i);
        Tick(rig.Server)();
        Assert.False(hulls.IsAlive(handle));
    }

    [Fact]
    public void SplitPathTakesNoManagedMemory()
    {
        var content = Content();
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            using var rig = new SaveRig(SaveRig.NewWorld(content), content);
            rig.Join();
            var tick = Tick(rig.Server);
            rig.Hulls.BlockStates.Lose(rig.Hulls.Info(0).BlockState, 2);
            probe.Start();
            tick();
            probe.Stop();
            Assert.Equal(2, rig.Hulls.Count);
        }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void LiveCenterMovesInStraightLineWhileDesignOriginOrbitsIt()
    {
        var flight = new FlightParams { Mass = 1, CenterOfMass = new Vector3(4, 0, 0), Inertia = Vector3.One };
        var state = HullState.Spawn(default, flight);
        state.AngularVelocity = new Vector3(0, 1, 0);
        state.Velocity = new Vector3(2, 0, 0);
        for (var i = 0; i < 60; i++) FlightModel.Step(ref state, default, flight, HullSim.Dt);
        var center = state.Position + HullRotation.Rotate(flight.CenterOfMass, state.Orientation);
        Assert.InRange(center.X, 5.999, 6.001);
        Assert.InRange(Math.Abs(center.Z), 0, 0.001);
    }
}
