using System.Numerics;
using System.Reflection;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Render;
using Prospect.Client.Settings;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class RockVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverCapacityKeepsLargeRockRegardlessOfTraversal(bool reverse)
    {
        var selection = new RockSelection(SceneRenderer.MaxInstances);
        selection.Begin(SceneRenderer.MaxInstances - 3);
        var important = new RockRef(new SectorCoord(1, 0, 0), 0);
        if (reverse) selection.Offer(important, 1, 100);
        for (var i = 0; i < 40959; i++)
            selection.Offer(new RockRef(new SectorCoord(i / 20480, 0, 0), i % 20480 + 1), 0, 1);
        if (!reverse) selection.Offer(important, 1, 100);
        Assert.Equal(SceneRenderer.MaxInstances - 3, selection.Items.Length);
        Assert.Contains(selection.Items.ToArray(), e => e.Id == important);
    }

    [Fact]
    public void OrbitAcrossYRowKeepsFocusRequested()
    {
        var requests = new SectorRequests();
        requests.Update(new SectorCoord(0, 1, 2), default(SectorCoord));
        Assert.True(requests.Contains(default));
        requests.Update(new SectorCoord(0, 2, 2), default(SectorCoord));
        Assert.True(requests.Contains(default));
        Assert.InRange(requests.Coordinates.Length, 75, 102);
        Assert.Equal(default, requests.Coordinates[0]);
    }

    [Fact]
    public void TiesAndCameraSectorTraversalCannotChangeRetainedIdentities()
    {
        var a = new RockSelection(32);
        var b = new RockSelection(32);
        a.Begin(32);
        b.Begin(32);
        for (var i = 0; i < 100; i++)
        {
            a.Offer(new RockRef(new SectorCoord(i / 50, 0, 0), i % 50), i / 50, 10);
            var j = 99 - i;
            b.Offer(new RockRef(new SectorCoord(j / 50, 0, 0), j % 50), j / 50, 10);
        }
        Assert.Equal(a.Items.ToArray().Select(x => x.Id).OrderBy(x => x.Index), b.Items.ToArray().Select(x => x.Id).OrderBy(x => x.Index));
        Assert.All(a.Items.ToArray(), x => Assert.Equal(0, x.Id.Sector.X));
    }

    [Fact]
    public void NarrowPreferenceStopsCutoffJitterButYieldsToMeaningfulGrowth()
    {
        var selection = new RockSelection(1);
        var retained = new RockRef(default, 1);
        var challenger = new RockRef(default, 0);
        selection.Begin(1);
        selection.Offer(retained, 0, 10, true);
        selection.Offer(challenger, 0, 10.1f);
        Assert.Equal(retained, selection.Items[0].Id);
        selection.Begin(1);
        selection.Offer(retained, 0, 10, true);
        selection.Offer(challenger, 0, 10.3f);
        Assert.Equal(challenger, selection.Items[0].Id);
        selection.Begin(0);
        selection.Offer(retained, 0, 100);
        Assert.Empty(selection.Items.ToArray());
    }

    [Fact]
    public void PanAndReturnToPilotChangeOnlyTheDesiredBoundedNeighborhood()
    {
        var requests = new SectorRequests();
        var camera = new SectorCoord(0, 2, 2);
        var focus = new SectorCoord(10, 0, 0);
        requests.Update(camera, focus);
        Assert.Equal(102, requests.Coordinates.Length);
        Assert.Equal(focus, requests.Coordinates[0]);
        Assert.Equal(camera, requests.Coordinates[1]);
        Assert.Equal(102, requests.Coordinates.ToArray().Distinct().Count());
        requests.Update(camera, focus.Offset(1, 0, 0));
        Assert.False(requests.Contains(focus.Offset(-1, 0, 0)));
        requests.Update(camera, null);
        Assert.Equal(75, requests.Coordinates.Length);
        Assert.Equal(camera, requests.Coordinates[0]);
        Assert.False(requests.Contains(focus));
    }

    [Fact]
    public void DelayedGenerationIsNeverReusedAndColdFocusIsFirst()
    {
        var galaxy = GameContent.Load().Galaxy;
        var requests = new SectorRequests();
        var focus = new SectorCoord(10, 0, 0);
        requests.Update(default, focus);
        var slots = Enumerable.Range(0, 102).Select(_ => new WorldView.SectorSlot()).ToArray();
        foreach (var coord in requests.Coordinates)
        {
            var slot = WorldView.FindStreamSlot(slots, galaxy, requests, coord, out var present);
            Assert.False(present);
            Assert.NotNull(slot);
            slot.Galaxy = galaxy;
            slot.Coord = coord;
            slot.State = WorldView.SectorSlot.Generating;
        }
        Assert.Equal(focus, slots[0].Coord);
        requests.Update(new SectorCoord(0, 2, 2), focus);
        Assert.Null(WorldView.FindStreamSlot(slots, galaxy, requests, new SectorCoord(0, 2, 2), out _));
        foreach (var slot in slots) slot.State = WorldView.SectorSlot.Ready;
        foreach (var coord in requests.Coordinates)
        {
            var slot = WorldView.FindStreamSlot(slots, galaxy, requests, coord, out var present);
            if (present) continue;
            Assert.NotNull(slot);
            Assert.False(requests.Contains(slot.Coord));
            slot.Coord = coord;
            slot.State = WorldView.SectorSlot.Generating;
        }
        Assert.Equal(focus, slots[0].Coord);
        Assert.Equal(WorldView.SectorSlot.Ready, slots[0].State);
        foreach (var coord in requests.Coordinates)
        {
            Assert.Null(WorldView.FindStreamSlot(slots, galaxy, requests, coord, out var present));
            Assert.True(present);
        }
    }

    [Fact]
    public void SelectionAndRequestsAllocateNothingAfterConstruction()
    {
        var selection = new RockSelection(32768);
        var requests = new SectorRequests();
        void Run()
        {
            selection.Begin(32765);
            for (var i = 0; i < 40960; i++) selection.Offer(new RockRef(new SectorCoord(i / 20480, 0, 0), i % 20480), 0, i % 201 + 1);
            requests.Update(default, new SectorCoord(10, 0, 0));
            requests.Update(new SectorCoord(0, 2, 0), default(SectorCoord));
        }
        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ProductionScanKeepsInteriorLargeRockAcrossCameraBoundaryAndSlotOrder()
    {
        var content = GameContent.Load();
        using var jobs = new JobSystem(1, "VisibilityTest");
        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        var world = new WorldView(new InputDevices(), actions, new ClientSettings(), jobs, null!, content.Designs);
        void Set(string name, object value) => typeof(WorldView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(world, value);
        Set("_galaxy", content.Galaxy);
        Set("_viewport", new float2(1280, 720));
        var slots = (WorldView.SectorSlot[])typeof(WorldView).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        for (var s = 0; s < 2; s++)
        {
            var slot = slots[s];
            slot.Galaxy = content.Galaxy;
            slot.Coord = new SectorCoord(s, 0, 0);
            slot.State = WorldView.SectorSlot.Ready;
            slot.Displaced = true; // Bypass generated bounds, since this fixture supplies synthetic world positions.
            slot.Offsets = new Vector3[20480];
            slot.Content.Asteroids = new Asteroid[20480];
            slot.Content.AsteroidCount = 20480;
            var local = new Double3(20000, 0, -10000).RelativeTo(content.Galaxy.CenterOf(slot.Coord));
            for (var i = 0; i < 20480; i++)
                slot.Content.Asteroids[i] = new Asteroid { Radius = s == 1 && i == 0 ? 1000 : 30, LocalPosition = local, Rotation = Quaternion.Identity };
        }
        var important = slots[1];
        foreach (var x in new[] { 19999, 20001 })
        {
            world.PrepareRockSelection(new Double3(x, 0, 0), 1280f / 720, 3);
            Assert.True(BlockSet.Has(important.Drawn, 0));
            var retained = slots.Sum(slot => slot.Drawn.Sum(bits => BitOperations.PopCount(bits)));
            Assert.Equal(SceneRenderer.MaxInstances - 3, retained);
            (slots[0], slots[1]) = (slots[1], slots[0]);
        }
        world.PrepareRockSelection(new Double3(20001, 0, 0), 1280f / 720, 3);
        var before = GC.GetAllocatedBytesForCurrentThread();
        world.PrepareRockSelection(new Double3(20001, 0, 0), 1280f / 720, 3);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        world.PrepareRockSelection(new Double3(20001, 0, 0), 1280f / 720, SceneRenderer.MaxInstances);
        Assert.Equal(0, world.EligibleRocks);
        Assert.Empty(world.RetainedRocks.ToArray());
        Assert.All(slots, slot => Assert.All(slot.Drawn, bits => Assert.Equal(0UL, bits)));
    }

    [Fact]
    public void PickerHonorsRetainedMaskWithoutIndependentLogicalPixelCutoff()
    {
        Asteroid[] rocks = [new() { Radius = 100, LocalPosition = new Vector3(0, 0, 1000), Rotation = Quaternion.Identity },
            new() { Radius = 100, LocalPosition = new Vector3(0, 0, 2000), Rotation = Quaternion.Identity }];
        ulong[] drawn = [2];
        PickSector[] sectors = [new(default, default, rocks, 2, null, Drawn: drawn)];
        var hits = new PickHit[2];
        foreach (var height in new[] { 360f, 720f })
        {
            var count = TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, height, [], sectors, hits);
            Assert.Equal(1, count);
            Assert.Equal(new RockRef(default, 1), hits[0].Rock);
        }
        drawn[0] = 0;
        Assert.Equal(0, TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 720, [], sectors, hits));
    }
}
