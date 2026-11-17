using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class BuildDraftTests
{
    private static readonly GameContent Content = FleetContent.Value;
    private static readonly GridSize One = new(1, 1, 1);

    private static BuildDraft Draft(params DesignBox[] boxes)
    {
        var design = HullDesign.Of(new BoxDesign("test", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        return BuildDraft.From(design, design.FullSet, Content);
    }

    private static DesignBox Box(string type, GridSize at, GridSize size) => new(at, size, Content.Blocks.Get(type));

    private static int Of(GridSize g, int axis) => axis switch { 0 => g.X, 1 => g.Y, _ => g.Z };

    [Fact]
    public void SnapsFlushAndCentredOnEverySideOfAnUnevenBoxAtNegativeCoordinates()
    {
        var target = Box("core", new GridSize(-5, -3, -7), new GridSize(3, 2, 5));
        var draft = Draft(target);
        var size = new GridSize(2, 3, 4);
        GridSize[] expected = [new(-2, -4, -7), new(-7, -4, -7), new(-5, -1, -7), new(-5, -6, -7), new(-5, -4, -2), new(-5, -4, -11)];
        for (var side = 0; side < 6; side++)
        {
            var at = draft.Snap(0, side, size);
            Assert.Equal(expected[side], at);
            Assert.Equal(DraftEdit.Done, draft.Add(at, size, Content.Blocks.Get("hull")));
            // Flush along the face's axis, and sharing face area on both others.
            var normal = side / 2;
            var placed = draft.Boxes[1];
            Assert.Equal(side % 2 == 0 ? Of(target.At, normal) + Of(target.Size, normal) : Of(target.At, normal),
                side % 2 == 0 ? Of(placed.At, normal) : Of(placed.At, normal) + Of(placed.Size, normal));
            for (var axis = 0; axis < 3; axis++)
                if (axis != normal)
                    Assert.True(Of(placed.At, axis) < Of(target.At, axis) + Of(target.Size, axis) && Of(placed.At, axis) + Of(placed.Size, axis) > Of(target.At, axis));
            Assert.True(draft.Undo());
        }

        // A pointed cell centres the box on it, but never so far that it stops touching the face.
        Assert.Equal(new GridSize(-2, -2, -5), draft.Snap(0, 0, One, new GridSize(0, -2, -5)));
        Assert.Equal(new GridSize(-2, -2, -3), draft.Snap(0, 0, new GridSize(4, 4, 4), new GridSize(100, 100, 100)));
        Assert.Equal(new GridSize(-2, -6, -10), draft.Snap(0, 0, new GridSize(4, 4, 4), new GridSize(-100, -100, -100)));
    }

    [Fact]
    public void RefusedEditsLeaveTheDraftAndItsHistoryAsTheyWere()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), new GridSize(2, 2, 2)), Box("hull", new GridSize(2, 0, 0), One));
        var hull = Content.Blocks.Get("hull");
        var limits = Content.Blocks.Limits;
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(-1, 0, 0), One, hull));
        Assert.True(draft.Undo());
        var before = draft.Boxes.ToArray();
        var version = draft.Version;

        Assert.Equal(DraftEdit.Overlaps, draft.Add(new GridSize(1, 1, 1), One, hull));
        Assert.Equal(DraftEdit.BadSize, draft.Add(new GridSize(3, 0, 0), new GridSize(limits.MaxBoxSide + 1, 1, 1), hull));
        Assert.Equal(DraftEdit.OutsideLimits, draft.Add(new GridSize(limits.MaxDesignSide, 0, 0), One, hull));
        Assert.Equal(DraftEdit.OutsideLimits, draft.Add(new GridSize(-limits.MaxDesignSide, 0, 0), One, hull));
        Assert.Equal(DraftEdit.Overlaps, draft.Resize(0, 0, 3));
        Assert.Equal(DraftEdit.BadSize, draft.Resize(0, 1, 0));
        Assert.Equal(DraftEdit.Unchanged, draft.Resize(0, 1, 2));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.Delete(2));

        Assert.Equal(before, draft.Boxes.ToArray());
        Assert.Equal(version, draft.Version);
        Assert.False(draft.CanUndo);
        Assert.True(draft.CanRedo);
    }

    [Fact]
    public void ResizeMovesTheChosenSideAndKeepsTheOpposite()
    {
        var draft = Draft(Box("core", new GridSize(-3, 1, 0), new GridSize(2, 1, 1)));
        Assert.Equal(DraftEdit.Done, draft.Resize(0, 0, 5));
        Assert.Equal((new GridSize(-3, 1, 0), new GridSize(5, 1, 1)), (draft.Boxes[0].At, draft.Boxes[0].Size));
        Assert.Equal(DraftEdit.Done, draft.Resize(0, 1, 2));
        Assert.Equal((new GridSize(0, 1, 0), new GridSize(2, 1, 1)), (draft.Boxes[0].At, draft.Boxes[0].Size));
        Assert.Equal(DraftEdit.Done, draft.Resize(0, 3, 4));
        Assert.Equal((new GridSize(0, -2, 0), new GridSize(2, 4, 1)), (draft.Boxes[0].At, draft.Boxes[0].Size));
    }

    [Fact]
    public void DeleteUndoAndRedoKeepBlockOrderAndANewEditDropsRedo()
    {
        var draft = BuildDraft.From(Content.Pod, Content.Pod.FullSet, Content, historyLimit: 2);
        var original = draft.Boxes.ToArray();
        Assert.Equal(DraftEdit.Done, draft.Delete(1));
        Assert.Equal(original.Where((_, i) => i != 1), draft.Boxes.ToArray());
        Assert.True(draft.Undo());
        Assert.Equal(original, draft.Boxes.ToArray());
        Assert.True(draft.Redo());
        Assert.Equal(original.Length - 1, draft.Boxes.Length);
        Assert.True(draft.Undo());
        Assert.Equal(DraftEdit.Done, draft.Resize(2, 5, 2));
        Assert.False(draft.CanRedo);

        // Only the newest two edits are kept.
        Assert.Equal(DraftEdit.Done, draft.Delete(0));
        Assert.Equal(DraftEdit.Done, draft.Delete(0));
        Assert.True(draft.Undo());
        Assert.True(draft.Undo());
        Assert.False(draft.Undo());
        Assert.Equal(original.Length, draft.Boxes.Length);
    }

    [Fact]
    public void StartsFromTheSurvivingBlocksInOrderWithTheDesignsMetadata()
    {
        var design = Content.Designs[Content.StartingFleet[0]];
        var alive = design.FullSet.ToArray();
        BlockSet.Remove(alive, 1);
        BlockSet.Remove(alive, design.BlockCount - 1);
        var draft = BuildDraft.From(design, alive, Content);
        Assert.Equal(design.Boxes.ToArray().Where((_, i) => i != 1 && i != design.BlockCount - 1), draft.Boxes.ToArray());
        Assert.Equal((design.Name, design.Class, design.BlockSize, design.Color), (draft.Name, draft.Class, draft.BlockSize, draft.Color));
        Assert.True(draft.HasCore);
    }

    [Fact]
    public void MountsArePlacedOnFacesCarriedByTheirBlocksAndTakenAwayWhenAnEditLeavesThemNoRoom()
    {
        var cold = Content.Blocks.Get("coldGasThruster");
        var hot = Content.Blocks.Get("hotThruster");
        // A core, a plate two cells wide beside it and a lone plate further on.
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), new GridSize(2, 1, 1)), Box("hull", new GridSize(5, 0, 0), One));
        DesignMount[] Mounts() => draft.Mounts.ToArray();

        // Where one may go, and where not.
        Assert.Equal(DraftEdit.Done, draft.CanAddMount(1, BlockFacing.Up, 0, 0, cold));
        Assert.Equal(DraftEdit.WrongKind, draft.CanAddMount(1, BlockFacing.Up, 0, 0, Content.Blocks.Get("engine")));
        Assert.Equal(DraftEdit.WrongKind, draft.CanAdd(new GridSize(0, 1, 0), One, cold));
        Assert.Equal(DraftEdit.WrongKind, draft.Add(new GridSize(0, 1, 0), One, cold));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.CanAddMount(3, BlockFacing.Up, 0, 0, cold));
        Assert.Equal(DraftEdit.OffFace, draft.CanAddMount(1, BlockFacing.Up, 7, 0, cold));
        Assert.Equal(DraftEdit.OffFace, draft.CanAddMount(1, BlockFacing.Up, 0, 17, cold));
        Assert.Equal(DraftEdit.Overlaps, draft.CanAddMount(0, BlockFacing.Right, 3, 3, cold));
        Assert.Equal(0, draft.Version);

        // On a top face the first position runs along z and the second along x: two on the wide plate, one on the core.
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 3, 2, cold));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 3, 14, hot));
        Assert.Equal(DraftEdit.Done, draft.AddMount(0, BlockFacing.Left, 3, 3, cold));
        Assert.Equal(DraftEdit.Overlaps, draft.AddMount(1, BlockFacing.Up, 5, 4, cold));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 3, 6, cold));
        Assert.True(draft.Undo());
        Assert.Equal(3, Mounts().Length);
        var placed = Mounts();

        // The pointer's place on a face becomes a square on its grid of tenths, kept on the face.
        draft.SnapMount(1, BlockFacing.Up, cold, new System.Numerics.Vector3(1.73f, 0.5f, 0.02f), out var u, out var v);
        Assert.Equal((3, 10), (u, v));
        draft.SnapMount(1, BlockFacing.Up, cold, new System.Numerics.Vector3(9f, 0.5f, -9f), out u, out v);
        Assert.Equal((0, 16), (u, v));
        draft.SnapMount(2, BlockFacing.Left, hot, new System.Numerics.Vector3(4.5f, 0f, 0f), out u, out v);
        Assert.Equal((3, 3), (u, v));

        // Exported, the design has them in its own order, and its bytes decode to the same design.
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.Equal([placed[2], placed[0], placed[1]], export.Design!.Mounts.ToArray());
        Assert.Equal(export.Bytes, DesignBytes.Encode(export.Design));
        Assert.Equal(placed.Sum(mount => mount.Type.Mass) + 9f, export.Design.Flight.Mass, 3);
        var again = BuildDraft.FromBlueprint(export.Design, Content);
        Assert.Equal(export.Bytes, again.Export().Bytes);

        // A block moved takes what is mounted on it along: the mounts name it and say where on its face they are.
        Assert.Equal(DraftEdit.Done, draft.Move([1], new GridSize(0, 3, 0)));
        Assert.Equal(placed, Mounts());
        Assert.True(draft.Undo());

        // A block put where a mount stands takes that mount away, in the same step; undo brings both changes back.
        Assert.Equal(DraftEdit.Done, draft.CanAdd(new GridSize(1, 1, 0), One, Content.Blocks.Get("hull")));
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(1, 1, 0), One, Content.Blocks.Get("hull")));
        Assert.Equal([placed[1], placed[2]], Mounts());
        Assert.True(draft.Undo());
        Assert.Equal(placed, Mounts());
        Assert.Equal(3, draft.Boxes.Length);
        Assert.True(draft.Redo());
        Assert.Equal([placed[1], placed[2]], Mounts());
        Assert.True(draft.Undo());

        // Deleting a block deletes its mounts, and the mounts of later blocks still name their own.
        Assert.Equal(DraftEdit.Done, draft.AddMount(2, BlockFacing.Front, 3, 3, cold));
        var four = Mounts();
        Assert.Equal(DraftEdit.Done, draft.Delete(1));
        Assert.Equal([four[2], four[3] with { Parent = 1 }], Mounts());
        Assert.True(draft.Undo());
        Assert.Equal(four, Mounts());
        Assert.Equal(new GridSize(2, 1, 1), draft.Boxes[1].Size);

        // A mount is deleted alone, or with blocks, as one step. One named twice, or not there, is refused.
        Assert.Equal(DraftEdit.BadSelection, draft.Delete(default, [0, 0]));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.Delete(default, [4]));
        Assert.Equal(DraftEdit.Done, draft.Delete([2], [0]));
        Assert.Equal([four[1], four[2]], Mounts());
        Assert.Equal(2, draft.Boxes.Length);
        Assert.True(draft.Undo());
        Assert.Equal(four, Mounts());
        Assert.Equal(DraftEdit.Done, draft.DeleteMount(3));
        Assert.Equal(placed, Mounts());

        // Resized from its low side, a block's mounts stay where they are in the ship; one the face no longer
        // reaches goes. Here the plate loses its first cell, and with it the thruster that stood there.
        Assert.Equal(DraftEdit.Done, draft.Resize(1, 1, 1));
        Assert.Equal(new GridSize(2, 0, 0), draft.Boxes[1].At);
        Assert.Equal([placed[1] with { V = 4 }, placed[2]], Mounts());
        Assert.True(draft.Undo());
        Assert.Equal(placed, Mounts());
        Assert.Equal(DraftEdit.Done, draft.Resize(1, 0, 1));
        Assert.Equal([placed[0], placed[2]], Mounts());
        Assert.True(draft.Undo());

        // Turned, a block's faces are other faces: its mounts are taken off.
        Assert.Equal(DraftEdit.Done, draft.Rotate(1, 2));
        Assert.Equal([placed[2]], Mounts());
        Assert.True(draft.Undo());
        Assert.Equal(placed, Mounts());

        // Merged blocks keep what was mounted on them, counted from the one block's corner.
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(3, 0, 0), One, Content.Blocks.Get("hull")));
        Assert.Equal(DraftEdit.Done, draft.AddMount(3, BlockFacing.Up, 3, 3, cold));
        Assert.Equal(DraftEdit.Done, draft.Merge([3, 1]));
        Assert.Equal(new GridSize(3, 1, 1), draft.Boxes[1].Size);
        Assert.Equal([placed[0], placed[1], placed[2], new DesignMount(1, BlockFacing.Up, 3, 23, cold)], Mounts());
        Assert.True(draft.Export().Ok);
        Assert.True(draft.Undo());
        Assert.Equal(3, Mounts().Count(mount => mount.Parent == 1) + Mounts().Count(mount => mount.Parent == 3));
        Assert.Equal(new DesignMount(3, BlockFacing.Up, 3, 3, cold), Mounts()[^1]);

        // A pasted block brings its mounts, each on the copy; one with no room where the copy lands is left behind.
        Assert.Equal(DraftEdit.Done, draft.Paste([draft.Boxes[1]], new GridSize(0, 3, 0), [placed[0] with { Parent = 0 }, placed[1] with { Parent = 0 }]));
        Assert.Equal([placed[0] with { Parent = 4 }, placed[1] with { Parent = 4 }], Mounts()[^2..]);
        Assert.True(draft.Undo());
        Assert.Equal(DraftEdit.Done, draft.Paste([draft.Boxes[1]], new GridSize(0, -1, 0), [new DesignMount(0, BlockFacing.Up, 3, 2, cold), new DesignMount(0, BlockFacing.Down, 3, 2, cold)]));
        Assert.Equal(new DesignMount(4, BlockFacing.Down, 3, 2, cold), Mounts()[^1]);
        Assert.Equal(5, Mounts().Length);
        Assert.True(draft.Undo());

        // A draft of what a damaged hull has left has the mounts of the blocks it still has, under their new numbers.
        var whole = draft.Export().Design!;
        var alive = whole.FullSet.ToArray();
        BlockSet.Remove(alive, 1);
        var left = BuildDraft.From(whole, alive, Content);
        Assert.Equal(whole.BlockCount - 1, left.Boxes.Length);
        Assert.All(left.Mounts.ToArray(), mount => Assert.NotEqual(whole.Boxes[1], left.Boxes[mount.Parent]));
        Assert.Equal(whole.Mounts.ToArray().Count(mount => mount.Parent != 1), left.Mounts.Length);

        // The content's count is the most a draft takes: with room for forty, a vessel that has forty takes no more.
        using var files = new ContentCopy(fleet: true);
        files.Replace("blocks/blocks.json", "\"maxMounts\": 1024", "\"maxMounts\": 40");
        var tight = files.Load();
        var full = BuildDraft.FromBlueprint(tight.Designs[1], tight);
        Assert.Equal(40, full.Mounts.Length);
        var plating = full.Boxes.ToArray().ToList().FindIndex(box => box.Type.Id == "hull");
        Assert.Equal(DraftEdit.TooManyMounts, full.CanAddMount(plating, BlockFacing.Down, 3, 3, tight.Blocks.Get("coldGasThruster")));
        Assert.Equal(DraftEdit.Done, full.DeleteMount(0));
        Assert.Equal(DraftEdit.Done, full.CanAddMount(plating, BlockFacing.Down, 3, 3, tight.Blocks.Get("coldGasThruster")));
    }

    [Fact]
    public void ExportsOnlyAValidDesignAsCanonicalBytes()
    {
        var draft = BuildDraft.From(Content.Pod, Content.Pod.FullSet, Content);
        var same = draft.Export();
        Assert.True(same.Ok, same.Problem);
        Assert.Equal(DesignBytes.Encode(Content.Pod), same.Bytes);

        var at = draft.Snap(2, 5, One);
        Assert.Equal(new GridSize(0, 0, -3), at);
        Assert.Equal(DraftEdit.Done, draft.Add(at, new GridSize(1, 1, 1), Content.Blocks.Get("hull")));
        var grown = draft.Export();
        Assert.True(grown.Ok, grown.Problem);
        Assert.Equal(Content.Pod.BlockCount + 1, grown.Design!.BlockCount);
        Assert.Equal(draft.Boxes.ToArray(), grown.Design.Boxes.ToArray());
        Assert.Equal(grown.Bytes, DesignBytes.Encode(grown.Design));

        Assert.Equal(DraftEdit.Done, draft.Delete(0));
        Assert.False(draft.HasCore);
        var coreless = draft.Export();
        Assert.False(coreless.Ok);
        Assert.Null(coreless.Bytes);
        Assert.Contains("core", coreless.Problem!);
        Assert.True(draft.Undo());
        Assert.True(draft.HasCore);

        // What a hull can do is no condition: without its gyro the design is accepted, and says it cannot turn.
        Assert.Equal("gyro", draft.Boxes[7].Type.Id);
        Assert.Equal(DraftEdit.Done, draft.Delete(7));
        var grounded = draft.Export();
        Assert.True(grounded.Ok, grounded.Problem);
        Assert.Equal(System.Numerics.Vector3.Zero, grounded.Design!.Flight.TurnRate);
        Assert.True(grounded.Design.Flight.Acceleration.Z > 0);
    }

    [Fact]
    public void ACoreAloneExportsAsAHullThatCanDoNothingWithEveryFigureFinite()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One));
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(1, 0, 0), new GridSize(2, 1, 1), Content.Blocks.Get("hull")));
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        var flight = export.Design!.Flight;
        Assert.Equal((System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, 0f, 0f, 0f, 0f, 0f),
            (flight.Acceleration, flight.TurnRate, flight.MaxSpeed, flight.PowerGenerated, flight.EnergyStorage, flight.ShieldCapacity, flight.ReverseAcceleration));
        Assert.True(flight.IdlePower > 0 && flight.Mass > 0 && flight.HullPoints > 0);
        Assert.Equal(export.Bytes, DesignBytes.Encode(export.Design));
    }

    [Fact]
    public void TheBlueprintDraftHoldsEveryBlockTheDesignWasBuiltWithWhateverTheHullHasLost()
    {
        var design = Content.Designs[Content.StartingFleet[0]];
        var alive = design.FullSet.ToArray();
        BlockSet.Remove(alive, 1);
        var blueprint = BuildDraft.FromBlueprint(design, Content);
        Assert.Equal(design.Boxes.ToArray(), blueprint.Boxes.ToArray());
        Assert.Equal(design.BlockCount - 1, BuildDraft.From(design, alive, Content).Boxes.Length);
        Assert.False(blueprint.CanUndo);

        // Unchanged, it exports as the very design, which a server answers with "no change": a repair is asked for by name.
        Assert.Equal(DesignBytes.Encode(design), blueprint.Export().Bytes);
    }

    [Fact]
    public void AnExportSaysWhatItsShieldSurfaceNeedsAlsoWhenThatIsTooMuch()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), new GridSize(2, 1, 1)), Box("hull", new GridSize(2, 0, 0), One));
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        var usage = export.Surface!.Value;
        Assert.True(usage.Fits);
        Assert.Equal(Content.Blocks.Limits.SurfaceVertices, usage.Limit);
        Assert.InRange(usage.Vertices, 48, usage.Limit);
        Assert.Equal(usage, GeometryAdmission.Measure(export.Design!, usage.Limit));

        // A build past the room is refused, and says so: 800 separate cubes each need a rounded box of 80 vertices,
        // more vertices than a hull has room for.
        var heavy = new DesignBox[800];
        for (var i = 0; i < heavy.Length; i++)
            heavy[i] = new DesignBox(new GridSize(i * 4, 0, 0), One, Content.Blocks.Get(i == 0 ? "core" : "hull"));
        var refused = BuildDraft.FromBlueprint(HullDesign.Of(new BoxDesign("test", 1, heavy, Content.Blocks.Limits, "test"), Content.Rules), Content).Export();
        Assert.False(refused.Ok);
        Assert.Contains("shield surface", refused.Problem!);
        var over = refused.Surface!.Value;
        Assert.False(over.Fits);
        Assert.True(over.Vertices > over.Limit);
        Assert.Contains($"more than {over.Limit} vertices", refused.Problem);
    }

    [Fact]
    public void AGroupEditChangesEverythingOrNothingAndIsOneStepOfHistory()
    {
        var hull = Content.Blocks.Get("hull");
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), new GridSize(2, 1, 1)), Box("cargo", new GridSize(0, 1, 0), One));
        var start = draft.Boxes.ToArray();
        var version = draft.Version;
        DesignBox[] clip = [new(new GridSize(0, 0, 0), new GridSize(1, 2, 1), hull), new(new GridSize(1, 0, 0), One, Content.Blocks.Get("gyro"))];

        // Refused whole: the second box lands on the core, the group overlaps itself, a side is too long, it leaves the grid.
        Assert.Equal(DraftEdit.Overlaps, draft.Paste(clip, new GridSize(-1, 0, 0)));
        Assert.Equal(DraftEdit.Overlaps, draft.Paste([clip[0], clip[0]], new GridSize(5, 0, 0)));
        Assert.Equal(DraftEdit.BadSize, draft.Paste([clip[0], new(new GridSize(4, 0, 0), new GridSize(Content.Blocks.Limits.MaxBoxSide + 1, 1, 1), hull)], new GridSize(5, 0, 0)));
        Assert.Equal(DraftEdit.OutsideLimits, draft.Paste(clip, new GridSize(Content.Blocks.Limits.MaxDesignSide, 0, 0)));
        Assert.Equal(DraftEdit.OutsideLimits, draft.Paste(clip, new GridSize(int.MaxValue, 0, 0)));
        Assert.Equal(DraftEdit.Unchanged, draft.Paste([], new GridSize(5, 0, 0)));
        Assert.Equal(DraftEdit.Overlaps, draft.CanPaste(clip, new GridSize(-1, 0, 0)));
        Assert.Equal(DraftEdit.Done, draft.CanPaste(clip, new GridSize(3, 0, 0)));
        Assert.Equal(start, draft.Boxes.ToArray());
        Assert.Equal((version, false), (draft.Version, draft.CanUndo));

        // Taken whole, at the end, in order; one undo takes all of it back and one redo makes all of it again.
        Assert.Equal(DraftEdit.Done, draft.Paste(clip, new GridSize(3, 0, 0)));
        DesignBox[] pasted = [.. start, clip[0] with { At = new GridSize(3, 0, 0) }, clip[1] with { At = new GridSize(4, 0, 0) }];
        Assert.Equal(pasted, draft.Boxes.ToArray());
        Assert.True(draft.Undo());
        Assert.Equal(start, draft.Boxes.ToArray());
        Assert.False(draft.CanUndo);
        Assert.True(draft.Redo());
        Assert.Equal(pasted, draft.Boxes.ToArray());

        // A selection in any order; the blocks left keep their order, and undo puts every one back where it was.
        Assert.Equal(DraftEdit.Done, draft.Delete([3, 0, 2]));
        Assert.Equal(new[] { pasted[1], pasted[4] }, draft.Boxes.ToArray());
        Assert.False(draft.HasCore);
        Assert.True(draft.Undo());
        Assert.Equal(pasted, draft.Boxes.ToArray());
        Assert.True(draft.HasCore);
        Assert.True(draft.Redo());
        Assert.Equal(2, draft.Boxes.Length);
        Assert.True(draft.Undo());

        // A selection that names no block, or one twice, is refused before anything is touched.
        version = draft.Version;
        Assert.Equal(DraftEdit.NoSuchBlock, draft.Delete([1, 5]));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.Delete([-1]));
        Assert.Equal(DraftEdit.BadSelection, draft.Delete([1, 2, 1]));
        Assert.Equal(DraftEdit.Unchanged, draft.Delete([]));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.Move([5], new GridSize(1, 0, 0)));
        Assert.Equal(DraftEdit.BadSelection, draft.Move([2, 2], new GridSize(1, 0, 0)));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.Merge([0, 9]));
        Assert.Equal(DraftEdit.BadSelection, draft.Merge([3, 3]));
        Assert.Equal(pasted, draft.Boxes.ToArray());
        Assert.Equal(version, draft.Version);
    }

    [Fact]
    public void AMovedGroupMayLandOnItsOwnOldPlaceButNotOnABlockThatStays()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), new GridSize(2, 1, 1)),
            Box("hull", new GridSize(3, 0, 0), One), Box("cargo", new GridSize(6, 0, 0), One));
        var start = draft.Boxes.ToArray();
        var version = draft.Version;

        // One cell along: each lands where it or the other stood. Alone, the first would land on the second.
        Assert.Equal(DraftEdit.Overlaps, draft.CanMove([1], new GridSize(1, 0, 0)));
        Assert.Equal(DraftEdit.Done, draft.CanMove([2, 1], new GridSize(1, 0, 0)));
        Assert.Equal(DraftEdit.Overlaps, draft.CanMove([1, 2], new GridSize(3, 0, 0)));
        Assert.Equal(DraftEdit.Overlaps, draft.CanMove([1, 2], new GridSize(-1, 0, 0)));
        Assert.Equal(DraftEdit.Unchanged, draft.CanMove([1, 2], default));
        Assert.Equal(DraftEdit.Unchanged, draft.CanMove([], new GridSize(1, 0, 0)));
        Assert.Equal(DraftEdit.OutsideLimits, draft.CanMove([1, 2], new GridSize(0, Content.Blocks.Limits.MaxDesignSide, 0)));
        Assert.Equal(DraftEdit.OutsideLimits, draft.CanMove([1, 2], new GridSize(0, int.MinValue, 0)));
        Assert.Equal(start, draft.Boxes.ToArray());
        Assert.Equal((version, false), (draft.Version, draft.CanUndo));

        // The moved blocks keep their indices, so the selection still names them.
        Assert.Equal(DraftEdit.Done, draft.Move([2, 1], new GridSize(1, 0, 0)));
        DesignBox[] moved = [start[0], start[1] with { At = new GridSize(2, 0, 0) }, start[2] with { At = new GridSize(4, 0, 0) }, start[3]];
        Assert.Equal(moved, draft.Boxes.ToArray());
        Assert.Equal(DraftEdit.Done, draft.Move([2, 1], new GridSize(0, 3, -2)));
        Assert.True(draft.Undo());
        Assert.Equal(moved, draft.Boxes.ToArray());
        Assert.True(draft.Undo());
        Assert.Equal(start, draft.Boxes.ToArray());
        Assert.True(draft.Redo());
        Assert.Equal(moved, draft.Boxes.ToArray());

        // A preview costs nothing once the draft has seen a selection of the size.
        int[] selection = [2, 1];
        var by = new GridSize(0, 1, 0);
        draft.CanMove(selection, by);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
        {
            while (allocation.Measuring())
            {
                draft.CanMove(selection, by);
                draft.CanPaste(start, new GridSize(0, 9, 0));
                draft.CanMerge(selection, out _);
            }
        }
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void MergeMakesOneBoxOfBlocksOfOneTypeThatFillItExactlyAndUndoRestoresThemInOrder()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One),
            Box("hull", new GridSize(1, 0, 0), new GridSize(2, 1, 1)), Box("cargo", new GridSize(1, 1, 0), One),
            Box("hull", new GridSize(1, 0, 1), One), Box("hull", new GridSize(2, 0, 1), One),
            Box("hull", new GridSize(3, 0, 0), new GridSize(1, 1, 2)), Box("hull", new GridSize(5, 0, 0), One),
            Box("cargo", new GridSize(2, 1, 0), One));
        var start = draft.Boxes.ToArray();
        var version = draft.Version;

        // A gap, an L, two types, one block, and blocks that touch only at an edge.
        Assert.Equal(DraftEdit.NotABox, draft.CanMerge([5, 6], out _));
        Assert.Equal(DraftEdit.NotABox, draft.CanMerge([1, 3], out _));
        Assert.Equal(DraftEdit.MixedTypes, draft.CanMerge([2, 1], out _));
        Assert.Equal(DraftEdit.Unchanged, draft.CanMerge([4], out _));
        Assert.Equal(DraftEdit.NotABox, draft.Merge([3, 5]));
        Assert.Equal(start, draft.Boxes.ToArray());
        Assert.Equal((version, false), (draft.Version, draft.CanUndo));

        // Four blocks of three sizes that fill a box of 3 by 1 by 2: one block, where the lowest of them was.
        Assert.Equal(DraftEdit.Done, draft.CanMerge([5, 1, 4, 3], out var merged));
        Assert.Equal(new DesignBox(new GridSize(1, 0, 0), new GridSize(3, 1, 2), Content.Blocks.Get("hull")), merged);
        Assert.Equal(version, draft.Version);
        Assert.Equal(DraftEdit.Done, draft.Merge([5, 1, 4, 3]));
        Assert.Equal(new[] { start[0], merged, start[2], start[6], start[7] }, draft.Boxes.ToArray());

        // Undo brings back the very blocks at their places in the order; redo merges them again.
        Assert.True(draft.Undo());
        Assert.Equal(start, draft.Boxes.ToArray());
        Assert.True(draft.Redo());
        Assert.Equal(5, draft.Boxes.Length);
        Assert.Equal(merged, draft.Boxes[1]);
        Assert.True(draft.Undo());

        // Two cargo blocks side by side merge too, and keep their type.
        Assert.Equal(DraftEdit.Done, draft.Merge([7, 2]));
        Assert.Equal(new DesignBox(new GridSize(1, 1, 0), new GridSize(2, 1, 1), Content.Blocks.Get("cargo")), draft.Boxes[2]);
        Assert.Equal(start.Length - 1, draft.Boxes.Length);
    }

    [Fact]
    public void MergeRefusesABoxLongerThanABlockMayBe()
    {
        var side = Content.Blocks.Limits.MaxBoxSide;
        var draft = Draft(Box("core", new GridSize(0, 0, 0), new GridSize(side, 1, 1)), Box("core", new GridSize(side, 0, 0), One));
        Assert.Equal(DraftEdit.BadSize, draft.Merge([0, 1]));
        Assert.Equal(2, draft.Boxes.Length);
        Assert.False(draft.CanUndo);

        // Two cores that do fit become one, and the draft still has its core.
        var cores = Draft(Box("core", new GridSize(0, 0, 0), One), Box("core", new GridSize(0, 1, 0), One));
        Assert.Equal(DraftEdit.Done, cores.Merge([1, 0]));
        Assert.True(cores.HasCore);
        Assert.True(cores.Undo());
        Assert.Equal(2, cores.Boxes.Length);
    }

    [Fact]
    public void MirroredPlacementIsOneUndoStepAndSkipsWhatDoesNotFit()
    {
        var hull = Content.Blocks.Get("hull");
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One));
        var version = draft.Version;

        // Two planes: the block, and its copies across x, across y and across both. The centre of the core is the middle of all of them.
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(1, 1, 0), One, hull), MirrorAxes.X | MirrorAxes.Y, out var skipped));
        Assert.Equal(0, skipped);
        Assert.Equal([new GridSize(1, 1, 0), new GridSize(-1, 1, 0), new GridSize(1, -1, 0), new GridSize(-1, -1, 0)], draft.Boxes[1..].ToArray().Select(box => box.At).ToArray());
        Assert.True(draft.Undo());
        Assert.Single(draft.Boxes.ToArray());
        Assert.False(draft.CanUndo);
        Assert.True(draft.Redo());
        Assert.Equal(5, draft.Boxes.Length);
        Assert.True(draft.Version > version);

        // A copy that would sit on another block is left out and counted; the rest are placed, in one step.
        var next = Draft(Box("core", new GridSize(0, 0, 0), new GridSize(3, 1, 1)), Box("hull", new GridSize(-1, 0, 0), One));
        Assert.Equal(DraftEdit.Done, next.AddMirrored(new DesignBox(new GridSize(3, 0, 0), One, hull), MirrorAxes.X, out skipped));
        Assert.Equal(1, skipped);
        Assert.Equal(3, next.Boxes.Length);
        Assert.True(next.Undo());
        Assert.Equal(2, next.Boxes.Length);

        // The block itself not fitting refuses the whole edit and changes nothing.
        var before = next.Version;
        Assert.Equal(DraftEdit.Overlaps, next.AddMirrored(new DesignBox(new GridSize(1, 0, 0), One, hull), MirrorAxes.X, out skipped));
        Assert.Equal(before, next.Version);
        Assert.Equal(2, next.Boxes.Length);
    }

    [Fact]
    public void ACopyOnAPlaneIsTheBlockItselfAndIsNotAddedTwice()
    {
        var hull = Content.Blocks.Get("hull");
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One));
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(0, 1, 0), One, hull), MirrorAxes.All, out var skipped));
        Assert.Equal(0, skipped);
        // Across x and z it is on both planes, so only the copy below across y is another block.
        Assert.Equal([new GridSize(0, 1, 0), new GridSize(0, -1, 0)], draft.Boxes[1..].ToArray().Select(box => box.At).ToArray());

        // The same with one more plane in the way: none of the copies is a second one.
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(0, 2, 0), One, hull), MirrorAxes.All, out skipped));
        Assert.Equal(0, skipped);
        Assert.Equal(5, draft.Boxes.Length);
        Assert.Equal(new GridSize(0, -2, 0), draft.Boxes[4].At);
    }

    [Fact]
    public void WithoutACoreOrWithNoPlanesOnlyTheBlockIsPlaced()
    {
        var hull = Content.Blocks.Get("hull");
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), One));
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(0, 1, 0), One, hull), MirrorAxes.None, out _));
        Assert.Equal(3, draft.Boxes.Length);
        Assert.Equal(DraftEdit.Done, draft.Delete(0));
        Assert.False(draft.TryMirrorFrame(out _, out _, out _));
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(4, 0, 0), One, hull), MirrorAxes.All, out var skipped));
        Assert.Equal(0, skipped);
        Assert.Equal(3, draft.Boxes.Length);
    }

    [Fact]
    public void AMirroredDirectionalPartFacesTheMirroredWayAndAWedgeKeepsItsSlantOnTheOtherSide()
    {
        var engine = Content.Blocks.Get("engine");
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One));
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(1, 1, 0), One, engine, BlockShape.Box, BlockFacing.Right), MirrorAxes.X | MirrorAxes.Y, out _));
        // Across x it faces left, across y only it still faces right, across both it faces left.
        Assert.Equal([BlockFacing.Right, BlockFacing.Left, BlockFacing.Right, BlockFacing.Left], draft.Boxes[1..].ToArray().Select(box => box.Facing).ToArray());
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(0, 0, 1), One, engine, BlockShape.Box, BlockFacing.Back), MirrorAxes.Z, out _));
        Assert.Equal([BlockFacing.Back, BlockFacing.Front], draft.Boxes[5..].ToArray().Select(box => box.Facing).ToArray());

        // For every wedge and every set of planes the mirrored shape is the reflected solid: same corners, reflected.
        var half = new System.Numerics.Vector3(1.5f, 1f, 0.5f);
        Span<System.Numerics.Vector3> own = stackalloc System.Numerics.Vector3[8];
        Span<System.Numerics.Vector3> other = stackalloc System.Numerics.Vector3[8];
        for (var shape = BlockShape.WedgeXPlusPlus; shape <= BlockShape.WedgeZMinusMinus; shape++)
        {
            for (var mask = 0; mask < 8; mask++)
            {
                var reflected = Mirroring.Shape(shape, mask);
                var count = BlockShapes.Vertices(shape, half, own);
                Assert.Equal(count, BlockShapes.Vertices(reflected, half, other));
                foreach (var corner in own[..count])
                {
                    var mirrored = new System.Numerics.Vector3((mask & 1) != 0 ? -corner.X : corner.X, (mask & 2) != 0 ? -corner.Y : corner.Y, (mask & 4) != 0 ? -corner.Z : corner.Z);
                    Assert.True(other[..count].Contains(mirrored), $"{shape} across {mask}");
                }
            }
        }
    }

    [Fact]
    public void MirroredMountsGoOnTheMirroredBlockAndFaceAtTheMirroredPlaceAsOneStep()
    {
        var cold = Content.Blocks.Get("coldGasThruster");
        // A core with a plate on each side along x.
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), One), Box("hull", new GridSize(-1, 0, 0), One));
        Assert.Equal(DraftEdit.Done, draft.AddMountMirrored(1, BlockFacing.Up, 1, 2, cold, MirrorAxes.X | MirrorAxes.Z, out var skipped));
        Assert.Equal(0, skipped);
        var mounts = draft.Mounts.ToArray();
        // On the right plate at the place asked, and its z copy on the same plate; the x copies on the left plate.
        Assert.Contains(new DesignMount(1, BlockFacing.Up, 1, 2, cold), mounts);
        Assert.Contains(new DesignMount(1, BlockFacing.Up, 5, 2, cold), mounts);
        Assert.Contains(new DesignMount(2, BlockFacing.Up, 1, 4, cold), mounts);
        Assert.Contains(new DesignMount(2, BlockFacing.Up, 5, 4, cold), mounts);
        Assert.Equal(4, mounts.Length);
        Assert.True(draft.Undo());
        Assert.Empty(draft.Mounts.ToArray());

        // Across x a side face turns to the opposite side.
        Assert.Equal(DraftEdit.Done, draft.AddMountMirrored(1, BlockFacing.Right, 3, 3, cold, MirrorAxes.X, out _));
        Assert.Contains(new DesignMount(2, BlockFacing.Left, 3, 3, cold), draft.Mounts.ToArray());

        // With no mirrored block to carry the copy, it is left out and counted; the part itself is placed.
        var lone = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), One));
        Assert.Equal(DraftEdit.Done, lone.AddMountMirrored(1, BlockFacing.Up, 3, 3, cold, MirrorAxes.X, out skipped));
        Assert.Equal(1, skipped);
        Assert.Single(lone.Mounts.ToArray());

        // On a plane the copy is the part itself.
        var centred = Draft(Box("core", new GridSize(0, 0, 0), One));
        Assert.Equal(DraftEdit.Done, centred.AddMountMirrored(0, BlockFacing.Up, 3, 3, cold, MirrorAxes.X | MirrorAxes.Z, out skipped));
        Assert.Equal(0, skipped);
        Assert.Single(centred.Mounts.ToArray());
    }

    [Fact]
    public void MirroredDeletionTakesTheCopiesTheDraftHasWithTheirMountsAsOneStep()
    {
        var cold = Content.Blocks.Get("coldGasThruster");
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), One), Box("hull", new GridSize(-1, 0, 0), One), Box("hull", new GridSize(0, 1, 0), One));
        Assert.Equal(DraftEdit.Done, draft.AddMountMirrored(1, BlockFacing.Up, 3, 3, cold, MirrorAxes.X, out _));
        Assert.Equal(2, draft.Mounts.Length);

        // The right plate goes with the left one and what they carried; the plate above has no copy and stays.
        Assert.Equal(DraftEdit.Done, draft.DeleteMirrored([1], default, MirrorAxes.X));
        Assert.Equal(2, draft.Boxes.Length);
        Assert.Empty(draft.Mounts.ToArray());
        Assert.True(draft.Undo());
        Assert.Equal(4, draft.Boxes.Length);
        Assert.Equal(2, draft.Mounts.Length);

        // A mounted part goes with its mirror.
        Assert.Equal(DraftEdit.Done, draft.DeleteMirrored(default, [0], MirrorAxes.X));
        Assert.Empty(draft.Mounts.ToArray());
        Assert.Equal(4, draft.Boxes.Length);
        Assert.True(draft.Undo());
        Assert.Equal(2, draft.Mounts.Length);

        // Without planes it is the plain delete, and a deletion that would take every block is refused.
        Assert.Equal(DraftEdit.Done, draft.DeleteMirrored([3], default, MirrorAxes.None));
        Assert.Equal(3, draft.Boxes.Length);
        var pair = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), One), Box("hull", new GridSize(-1, 0, 0), One));
        Assert.Equal(DraftEdit.Unchanged, pair.DeleteMirrored([0, 1], default, MirrorAxes.X));
        Assert.Equal(3, pair.Boxes.Length);
    }

    [Fact]
    public void MirroredChecksAndGhostsTakeNoMemoryOnceTheDraftHasRoom()
    {
        var hull = Content.Blocks.Get("hull");
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One));
        var box = new DesignBox(new GridSize(1, 1, 1), One, hull);
        var copies = new DesignBox[Mirroring.MaxCopies];
        var checks = new DraftEdit[Mirroring.MaxCopies];
        // Reserves the draft's list once, as the first placement would.
        draft.CheckBoxes(copies.AsSpan(0, draft.MirrorBoxes(box, MirrorAxes.All, copies)), checks);
        var count = draft.MirrorBoxes(box, MirrorAxes.All, copies);
        Assert.Equal(Mirroring.MaxCopies, count);
        var version = draft.Version;
        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            draft.MirrorBoxes(box, MirrorAxes.All, copies);
            draft.CheckBoxes(copies, checks);
        }

        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(version, draft.Version);
        Assert.Single(draft.Boxes.ToArray());
    }

    private static DesignBox Tank(string type, GridSize at, GridSize size) => Box(type, at, size);

    [Fact]
    public void FuelMenuQueriesAllocateNothing()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Tank("bipropellantTank", new GridSize(1, 0, 0), One));
        int[] selection = [1];
        Span<FuelKind> fuels = stackalloc FuelKind[Fuels.Count];
        var count = draft.TankFuels(fuels);
        for (var i = 0; i < count; i++)
            draft.CanSwapTank(selection, fuels[i]);
        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            count = draft.TankFuels(fuels);
            for (var i = 0; i < count; i++)
                draft.CanSwapTank(selection, fuels[i]);
        }

        Assert.Equal(3, count);
        Assert.Equal(0, allocation.Bytes);
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void ChangingATanksFuelSwapsTheTypeInPlaceAsOneUndoStep()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Tank("bipropellantTank", new GridSize(1, 0, 0), new GridSize(2, 1, 1)),
            Tank("bipropellantTank", new GridSize(0, 1, 0), One), Box("hull", new GridSize(0, 2, 0), One));
        var before = draft.Boxes.ToArray();

        // Every fuel the content has a ship tank for is offered, found from the data.
        Span<FuelKind> fuels = stackalloc FuelKind[8];
        var count = draft.TankFuels(fuels);
        Assert.Equal(3, count);
        Assert.Contains(FuelKind.Nitrogen, fuels[..count].ToArray());
        Assert.Contains(FuelKind.Hydrazine, fuels[..count].ToArray());
        Assert.Contains(FuelKind.Bipropellant, fuels[..count].ToArray());

        Assert.Equal(DraftEdit.Done, draft.SwapTank([1, 2], FuelKind.Hydrazine));
        var after = draft.Boxes.ToArray();
        foreach (var block in new[] { 1, 2 })
        {
            Assert.Equal(Content.Blocks.Get("hydrazineTank"), after[block].Type);
            Assert.Equal(before[block] with { Type = after[block].Type }, after[block]);
        }

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[3], after[3]);

        // One step: a single undo gives back both tanks, and there is nothing older.
        Assert.True(draft.Undo());
        Assert.Equal(before, draft.Boxes.ToArray());
        Assert.False(draft.CanUndo);
        Assert.True(draft.Redo());
        Assert.Equal(after, draft.Boxes.ToArray());
    }

    [Fact]
    public void ChangingFuelRefusesWhatIsNotATankKeepsTheDraftAndSkipsTanksAlreadyOfTheFuel()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Tank("bipropellantTank", new GridSize(1, 0, 0), One), Box("hull", new GridSize(0, 1, 0), One));
        var before = draft.Boxes.ToArray();
        var version = draft.Version;
        Assert.Equal(DraftEdit.NotATank, draft.SwapTank([1, 2], FuelKind.Nitrogen));
        Assert.Equal(DraftEdit.Unchanged, draft.SwapTank([1], FuelKind.Bipropellant));
        Assert.Equal(DraftEdit.Unchanged, draft.CanSwapTank([], FuelKind.Nitrogen));
        Assert.Equal(DraftEdit.WrongKind, draft.CanSwapTank([1], FuelKind.None));
        Assert.Equal(DraftEdit.BadSelection, draft.CanSwapTank([1, 1], FuelKind.Nitrogen));
        Assert.Equal(before, draft.Boxes.ToArray());
        Assert.Equal(version, draft.Version);
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void TheDronesFuelsAreItsOwnTankTypes()
    {
        var drone = BuildDraft.FromBlueprint(Content.Designs[Content.DroneDesign], Content);
        Span<FuelKind> fuels = stackalloc FuelKind[8];
        var count = drone.TankFuels(fuels);
        Assert.Equal(2, count);
        Assert.Contains(FuelKind.Bipropellant, fuels[..count].ToArray());
        Assert.Contains(FuelKind.Nitrogen, fuels[..count].ToArray());
        Assert.DoesNotContain(FuelKind.Hydrazine, fuels[..count].ToArray());
        Assert.True(drone.TankType(FuelKind.Nitrogen)!.Drone);
        Assert.Null(drone.TankType(FuelKind.Hydrazine));
    }

    [Fact]
    public void SplittingATankMakesOneCellPiecesAsOneUndoStepAndCarriesTheMountsThatStillFit()
    {
        var cold = Content.Blocks.Get("coldGasThruster");
        var side = cold.Mount!.Side;
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Tank("bipropellantTank", new GridSize(1, 0, 0), new GridSize(3, 1, 1)));
        // On the top face, counted along x from the tank's low corner (V): in the first cell, in the second, and
        // across the cut between the first two. And one on the +x end.
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 3, 1, cold));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 3, 14, cold));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 3, 8, cold));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Right, 3, 3, cold));
        var mountsBefore = draft.Mounts.ToArray();
        var boxesBefore = draft.Boxes.ToArray();

        Assert.Equal(DraftEdit.Unchanged, draft.CanSplit([1], 1));
        Assert.Equal(DraftEdit.NotATank, draft.CanSplit([0], 0));
        Assert.Equal(DraftEdit.Done, draft.CanSplit([1], 0));
        Assert.Equal(DraftEdit.Done, draft.Split([1], 0));

        // Three tanks one cell long, the first keeping the index, the rest added low to high; nothing else moved.
        var boxes = draft.Boxes.ToArray();
        Assert.Equal(4, boxes.Length);
        Assert.Equal(boxesBefore[0], boxes[0]);
        for (var k = 0; k < 3; k++)
        {
            var index = k == 0 ? 1 : 1 + k;
            Assert.Equal(new DesignBox(new GridSize(1 + k, 0, 0), One, boxesBefore[1].Type), boxes[index]);
        }

        // The mount in the first cell stayed; the one in the second moved to its piece, keeping its place on the face;
        // the one on the end face went with the piece at that end.
        Assert.Contains(draft.Mounts.ToArray(), m => m.Parent == 1 && m.V == 1);
        Assert.Contains(draft.Mounts.ToArray(), m => m.Parent == 2 && m.V == 14 - FaceMounts.PerCell);
        // One across the cut had no room on either piece and went.
        Assert.True(8 + side > FaceMounts.PerCell);
        Assert.DoesNotContain(draft.Mounts.ToArray(), m => m.Face == BlockFacing.Up && (m.V == 8 || m.V == 8 - FaceMounts.PerCell));
        Assert.Contains(draft.Mounts.ToArray(), m => m.Face == BlockFacing.Right && m.Parent == (Facings.Sign(BlockFacing.Right) > 0 ? 3 : 1));
        Assert.Equal(3, draft.Mounts.Length);

        // One step to take back, giving the one long tank and its mounts again.
        Assert.True(draft.Undo());
        Assert.Equal(boxesBefore, draft.Boxes.ToArray());
        Assert.Equal(mountsBefore, draft.Mounts.ToArray());
        Assert.True(draft.Redo());
        Assert.True(draft.Undo());
    }

    [Fact]
    public void SplittingEachSelectedTankAlongAnAxisSkipsThoseOneCellThickAndHonoursTheTankLimit()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Tank("bipropellantTank", new GridSize(1, 0, 0), new GridSize(2, 2, 1)),
            Tank("nitrogenTank", new GridSize(0, 3, 0), new GridSize(1, 1, 4)));
        Assert.Equal(DraftEdit.Done, draft.Split([1, 2], 2));
        // The 2 x 2 x 1 tank is one cell thick along z and stays whole; the other is cut into four.
        Assert.Equal(1 + 1 + 4, draft.Boxes.Length);
        Assert.Equal(new GridSize(2, 2, 1), draft.Boxes[1].Size);
        Assert.True(draft.Undo());
        Assert.Equal(3, draft.Boxes.Length);
    }

    [Fact]
    public void ResizingAFaceInSeveralSmallEditsJoinsIntoOneUndoStepAndStopsWhereTheDraftSaysNo()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(1, 0, 0), One), Box("hull", new GridSize(5, 0, 0), One));
        var before = draft.Boxes.ToArray();
        // Two cells out along +x, then a third: each a step of its own until they are joined.
        Assert.Equal(DraftEdit.Done, draft.Resize(1, 0, 2));
        Assert.Equal(DraftEdit.Done, draft.Resize(1, 0, 3));
        Assert.True(draft.JoinLatest());
        // The next cell would run into the lone plate at x = 5; the draft says so and the box stays at three.
        Assert.Equal(DraftEdit.Overlaps, draft.CanResize(1, 0, 5));
        Assert.Equal(DraftEdit.Done, draft.CanResize(1, 0, 4));
        Assert.Equal(new GridSize(3, 1, 1), draft.Boxes[1].Size);
        Assert.True(draft.Undo());
        Assert.Equal(before, draft.Boxes.ToArray());
        Assert.False(draft.CanUndo);

        // A minus face moves the low corner with it and keeps the opposite face.
        var tall = Draft(Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(3, 0, 0), One));
        Assert.Equal(DraftEdit.Done, tall.Resize(1, 1, 2));
        Assert.Equal(new DesignBox(new GridSize(2, 0, 0), new GridSize(2, 1, 1), tall.Boxes[1].Type), tall.Boxes[1]);
        Assert.Equal(DraftEdit.Done, tall.Resize(1, 1, 2 + 1));
        Assert.True(tall.JoinLatest());
        Assert.Equal(new GridSize(1, 0, 0), tall.Boxes[1].At);

        // Past the type's largest side nothing fits.
        var limit = BoxDesign.MaxSide(tall.Boxes[1].Type, Content.Blocks.Limits);
        Assert.Equal(DraftEdit.BadSize, tall.CanResize(1, 0, limit + 1));

        // A gesture that is put back leaves nothing to redo.
        Assert.True(tall.Retract());
        Assert.False(tall.CanRedo);
        Assert.Equal(new GridSize(3, 0, 0), tall.Boxes[1].At);
    }
}
