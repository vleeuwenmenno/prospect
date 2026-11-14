using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Large complete health records must not starve later ships of fresh poses.</summary>
public sealed class SnapshotFairnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiftyDamagedShipsStayVisibleAndTheLastShipStaysInCollisionPrediction(bool contactAndRefusal)
    {
        using var files = new ContentCopy();
        files.Replace("rules/exploration.json", "5000", "20000");
        var content = files.Load();
        var boxes = new DesignBox[HullRecord.MaxHealth];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new DesignBox(new GridSize(i % 35 * 2, i / 35 * 2, 0), new GridSize(2, 2, 2), content.Blocks.Get(i == 0 ? "core" : i == 1 ? "sensor" : "hull"));
        var design = HullDesign.Of(new BoxDesign("Health snapshot fixture", 1, boxes, content.Blocks.Limits, "snapshot"), content.Rules);
        content.Designs[content.PodDesign] = design;
        using var rig = new ScriptedServer(content, maxPlayers: 1);
        var peer = rig.Join(1);
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Server)!;
        for (var i = 1; i < hulls.Slots; i++) if (hulls.IsAliveAt(i)) hulls.Destroy(hulls.HandleAt(i));
        var origin = new Double3(1e9, 1e9, 1e9);
        hulls.State(0).Position = origin;
        var ids = new uint[50];
        ids[0] = hulls.HandleAt(0).Packed;
        for (var i = 1; i < ids.Length; i++)
        {
            var position = i == 49 ? origin + new Vector3(0, 100, 0) : origin + new Vector3(i * 250, 0, 0);
            ids[i] = hulls.Create((uint)(100 + i), (byte)content.PodDesign, design, position).Packed;
        }
        foreach (var id in ids)
        {
            var index = (int)(id & 65535);
            var state = hulls.Info(index).BlockState;
            for (var b = 0; b < design.BlockCount; b++) hulls.BlockStates.SetHitPoints(state, b, design.IntactHitPoints[b] * .5f);
            hulls.State(index).Hull = (float)hulls.BlockStates.HitTotal(state);
        }
        using var world = new ClientWorld(rig.Server.Content);
        world.SetPlayer(1);
        world.SetOwnHull(ids[0]);
        var near = (HullStore)typeof(ClientWorld).GetField("_near", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        var seen = new long[50];
        Array.Fill(seen, -1);
        var packets = 0;
        rig.Net.ObserveSnapshot = (int recipient, ReadOnlySpan<byte> payload) =>
        {
            Assert.Equal(peer, recipient);
            Assert.InRange(payload.Length, 1, 65536);
            var reader = new NetReader(payload[1..]);
            Assert.True(SnapshotHeader.TryRead(ref reader, out var header));
            Assert.True(header.HullCount > 0);
            for (var i = 0; i < header.HullCount; i++)
            {
                Assert.True(HullRecord.TryRead(ref reader, out var record));
                if (i == 0) Assert.Equal(ids[0], record.Id);
                Assert.True(record.HasHealth);
                Assert.Equal(HullRecord.MaxHealth, record.HealthCount);
                var index = Array.IndexOf(ids, record.Id);
                Assert.True(index >= 0);
                seen[index] = header.ServerTick;
            }
            Assert.Equal(0, reader.Remaining);
            reader = new NetReader(payload[1..]);
            world.OnSnapshot(ref reader, header.ServerTick);
            packets++;
        };
        var views = new HullView[64];
        var words = new ulong[50 * BlockSet.WordCount(design.BlockCount)];
        for (var i = 0; i < 160; i++)
        {
            if (contactAndRefusal)
                foreach (var id in ids) hulls.Info((int)(id & 65535)).ContactTick = rig.Server.Status.Tick;
            if (contactAndRefusal && i == 20) rig.Net.SnapshotsToRefuse = 1;
            rig.Tick();
            var tick = rig.Server.Status.Tick - 1;
            var count = world.BuildViews(views, ref words, tick, tick, out var own);
            if (i < 8) continue;
            Assert.Equal(50, count);
            Assert.Equal(ids[0], views[own].Id);
            foreach (var last in seen) Assert.InRange(tick - last, 0, 12);
            Assert.Contains(Enumerable.Range(0, near.Slots), index => near.IsAliveAt(index) && near.Info(index).Owner == 149);
        }
        Assert.Equal(contactAndRefusal ? 159 : 160, packets);
    }
}
