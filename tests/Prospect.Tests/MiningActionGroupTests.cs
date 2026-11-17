using System.Numerics;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The per-hull action group overrides: who may change a part's group, what the server and the client then say about
/// it, how the changes steer which laser fires, and when a hull stops having them.
/// </summary>
public sealed class MiningActionGroupTests
{
    private static readonly Vector3 Up = Vector3.UnitY;

    private static byte Raw(MiningRig rig, int slot, int part) =>
        MiningFixtures.Field<byte[]>(rig.Server, "_groupOverrides")[slot * ActionGroupsMessage.MaxParts + part];

    private static int[] Fired(MiningRig rig)
    {
        var s = rig.Snap(0);
        return s.LaserBeams.AsSpan(0, s.LaserBeamCount).ToArray().Select(b => (int)b.Part).OrderBy(p => p).ToArray();
    }

    private static MiningRig Aimed(out RockRef rock)
    {
        var rig = new MiningRig();
        rock = MiningFixtures.FindRock(rig.Rocks).Rock;
        rig.Place(0, rock, Up, 25);
        return rig;
    }

    [Fact]
    public void TheOwnersChangeSteersWhichGroupFiresALaserAndClearingItBringsTheDesignsGroupBack()
    {
        using var rig = Aimed(out _);
        var design = rig.DesignOf();
        var (a, b) = (MiningFixtures.PartA(design), MiningFixtures.PartB(design));
        var hull = rig.Own();

        // The design's groups: A is group 1.
        rig.Aim(0, MiningFixtures.Mask(1), Up);
        rig.Step(20);
        Assert.Equal([a], Fired(rig));
        Assert.Equal(1, rig.Snap().GroupOf(hull, design, a));
        Assert.Equal(1, rig.Server.GroupOf(rig.Slot(), design, a));

        // Moved to group 5: group 1 no longer fires it, group 5 does, and B is unaffected.
        rig.Input().SetActionGroup(hull, a, 5);
        rig.Step(20);
        Assert.Equal(5, rig.Server.GroupOf(rig.Slot(), design, a));
        Assert.Equal(5, rig.Snap().GroupOf(hull, design, a));
        Assert.Equal(2, rig.Snap().GroupOf(hull, design, b));
        Assert.Empty(Fired(rig));
        rig.Aim(0, MiningFixtures.Mask(5), Up);
        rig.Step(20);
        Assert.Equal([a], Fired(rig));
        rig.Aim(0, MiningFixtures.Mask(2, 5), Up);
        rig.Step(20);
        Assert.Equal(new[] { a, b }.OrderBy(p => p), Fired(rig));

        // Cleared: the design's group again.
        rig.Input().SetActionGroup(hull, a, ActionGroups.NotOverridden);
        rig.Step(20);
        Assert.Equal(1, rig.Server.GroupOf(rig.Slot(), design, a));
        Assert.Equal(1, rig.Snap().GroupOf(hull, design, a));
        Assert.Equal([b], Fired(rig));
        Assert.Equal(ActionGroups.NotOverridden, Raw(rig, rig.Slot(), a));
    }

    [Fact]
    public void AGroupMayBeSetToNoneAndStopsTheLaserFromFiringWithEveryGroupOn()
    {
        using var rig = Aimed(out _);
        var design = rig.DesignOf();
        var a = MiningFixtures.PartA(design);
        rig.Input().SetActionGroup(rig.Own(), a, ActionGroups.None);
        rig.Step(10);
        Assert.Equal(ActionGroups.None, Raw(rig, rig.Slot(), a));
        Assert.Equal(0, rig.Snap().GroupOf(rig.Own(), design, a));
        rig.Aim(0, ActionGroups.AllMask, Up);
        rig.Step(20);
        Assert.DoesNotContain(a, Fired(rig));
        Assert.Contains(MiningFixtures.PartB(design), Fired(rig));
    }

    [Fact]
    public void AnotherPlayersChangeToAHullThatIsNotTheirsIsRefused()
    {
        using var rig = Aimed(out _);
        var design = rig.DesignOf();
        var a = MiningFixtures.PartA(design);
        rig.Input(1).SetActionGroup(rig.Own(0), a, 7);
        rig.Step(20);
        Assert.Equal(ActionGroups.NotOverridden, Raw(rig, rig.Slot(0), a));
        Assert.Equal(1, rig.Server.GroupOf(rig.Slot(0), design, a));
        Assert.Equal(1, rig.Snap(0).GroupOf(rig.Own(0), design, a));

        // A player's own other hull is theirs to change, and a hull that is not there is no one's.
        rig.Input(0).SetActionGroup(0x7FFF_0000u | 5, a, 7);
        rig.Step(20);
        Assert.Equal(ActionGroups.NotOverridden, Raw(rig, rig.Slot(0), a));
    }

    [Fact]
    public void APartPastTheDesignsLasersOrAGroupThatIsNoGroupChangesNothing()
    {
        using var rig = Aimed(out _);
        var design = rig.DesignOf();
        var hull = rig.Own();
        var slot = rig.Slot();
        Assert.Equal(3, design.Lasers.Length);

        // The bridge keeps nonsense off the wire; a message that gets past it is the server's to refuse. Both are checked.
        foreach (var part in new[] { design.Lasers.Length, design.Lasers.Length + 1, 63 })
            rig.Input().SetActionGroup(hull, part, 4);
        rig.Input().SetActionGroup(hull, MiningFixtures.PartA(design), 11);
        rig.Input().SetActionGroup(hull, MiningFixtures.PartA(design), 200);
        rig.Input().SetActionGroup(hull, ActionGroupsMessage.MaxParts, 4);
        rig.Input().SetActionGroup(0, 0, 4);
        rig.Step(30);
        for (var part = 0; part < ActionGroupsMessage.MaxParts; part++)
            Assert.Equal(ActionGroups.NotOverridden, Raw(rig, slot, part));

        // Straight at the server, as a hostile client would send it: a part the design has not, and every value the wire allows but the server must judge.
        foreach (var (part, group) in new (byte, byte)[] { (3, 4), (10, 4), (63, 1), (3, 0), (3, ActionGroups.NotOverridden) })
            Send(rig, new SetActionGroupMessage(hull, part, group));
        rig.Step(10);
        for (var part = 0; part < ActionGroupsMessage.MaxParts; part++)
            Assert.Equal(ActionGroups.NotOverridden, Raw(rig, slot, part));

        // A real laser, a real group: taken, and nothing else moved.
        rig.Input().SetActionGroup(hull, MiningFixtures.PartC(design), 9);
        rig.Step(10);
        Assert.Equal(9, Raw(rig, slot, MiningFixtures.PartC(design)));
        Assert.Equal(ActionGroups.NotOverridden, Raw(rig, slot, MiningFixtures.PartA(design)));
    }

    /// <summary>Delivers a message to the server as client 0's connection, past everything the client side checks.</summary>
    private static void Send(MiningRig rig, SetActionGroupMessage message)
    {
        var buffer = new byte[SetActionGroupMessage.MaxWireSize];
        var writer = new Prospect.Engine.Net.NetWriter(buffer);
        message.Write(ref writer);
        var connection = ((Array)MiningFixtures.Field<Array>(rig.Server, "_connections")).Cast<object>()
            .First(c => (uint)c.GetType().GetField("PlayerId")!.GetValue(c)! == rig.Snap(0).PlayerId);
        var handle = typeof(ServerHost).GetMethod("ReceiveSetActionGroup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handle.Invoke(rig.Server, [connection, message]);
    }

    [Fact]
    public void TheActionGroupsMessageReachesTheClientWithOneEntryPerLaserAndATickingRevision()
    {
        using var rig = Aimed(out _);
        var design = rig.DesignOf();
        var hull = rig.Own();
        rig.Until(() => rig.Snap().ActionGroupHull == hull);
        var first = rig.Snap();
        Assert.Equal(design.Lasers.Length, first.ActionGroupCount);
        Assert.All(first.ActionGroupOverrides.AsSpan(0, design.Lasers.Length).ToArray(), g => Assert.Equal(ActionGroups.NotOverridden, g));
        var revision = first.ActionGroupRevision;

        rig.Input().SetActionGroup(hull, MiningFixtures.PartB(design), 3);
        rig.Until(() => rig.Snap().ActionGroupRevision != revision);
        var changed = rig.Snap();
        Assert.Equal((byte)3, changed.ActionGroupOverrides[MiningFixtures.PartB(design)]);
        Assert.Equal(ActionGroups.NotOverridden, changed.ActionGroupOverrides[MiningFixtures.PartA(design)]);
        Assert.Equal(3, changed.GroupOf(hull, design, MiningFixtures.PartB(design)));

        // A hull the snapshot does not hold the overrides of reads the design's group.
        Assert.Equal(2, changed.GroupOf(hull + 1, design, MiningFixtures.PartB(design)));

        // The other player is told nothing of this hull's groups: theirs are their own hull's.
        Assert.NotEqual(hull, rig.Snap(1).ActionGroupHull);
        Assert.All(rig.Snap(1).ActionGroupOverrides.AsSpan(0, rig.Snap(1).ActionGroupCount).ToArray(), g => Assert.Equal(ActionGroups.NotOverridden, g));
    }

    [Fact]
    public void AHullThatAnEditReplacesStartsWithNoOverridesAndTheClientIsToldSo()
    {
        using var rig = Aimed(out _);
        var design = rig.DesignOf();
        var a = MiningFixtures.PartA(design);
        var old = rig.Own();
        rig.Input().SetActionGroup(old, a, 6);
        rig.Until(() => rig.Snap().GroupOf(old, design, a) == 6);
        Assert.Equal(6, rig.Server.GroupOf(rig.Slot(), design, a));

        // Lose the plating at the top of the wall, so there is something to repair, and rebuild the hull as its own design.
        var slot = rig.Slot();
        var blocks = rig.Hulls.Info(slot).BlockState;
        var top = Enumerable.Range(0, design.BlockCount).First(i => design.Boxes[i] is { At: { X: 3, Y: 3, Z: 0 } });
        Assert.True(rig.Hulls.BlockStates.Lose(blocks, top));
        rig.Hulls.State(slot).Hull = (float)rig.Hulls.BlockStates.HitTotal(blocks);
        var serial = rig.Snap().Editor.Serial;
        rig.Input().RequestEdit(old);
        rig.Until(() => rig.Snap().Editor.Serial != serial);
        Assert.Equal(EditorPhase.Open, rig.Snap().Editor.Phase);
        rig.Input().RepairEdit(rig.Snap().Editor.Token);
        rig.Until(() => rig.Own() != old);

        var now = rig.Own();
        Assert.NotEqual(old, now);
        Assert.Equal(1, rig.Server.GroupOf(rig.Slot(), design, a));
        Assert.Equal(ActionGroups.NotOverridden, Raw(rig, rig.Slot(), a));
        rig.Until(() => rig.Snap().ActionGroupHull == now);
        Assert.Equal(1, rig.Snap().GroupOf(now, design, a));
        Assert.All(rig.Snap().ActionGroupOverrides.AsSpan(0, rig.Snap().ActionGroupCount).ToArray(), g => Assert.Equal(ActionGroups.NotOverridden, g));
    }
}
