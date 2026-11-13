using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class BuildDraftTests
{
    private static readonly GameContent Content = GameContent.Load();
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
        Assert.Equal("gyro", draft.Boxes[9].Type.Id);
        Assert.Equal(DraftEdit.Done, draft.Delete(9));
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
}
