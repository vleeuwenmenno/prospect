using System.Numerics;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The ship editor's geometry and the draft's preview checks, without a renderer or an interface.</summary>
public sealed class ShipEditorTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly GridSize One = new(1, 1, 1);

    private static DesignBox Box(string type, GridSize at, GridSize size) => new(at, size, Content.Blocks.Get(type));

    private static BuildDraft Draft(params DesignBox[] boxes)
    {
        var design = HullDesign.Of(new BoxDesign("test", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        return BuildDraft.From(design, design.FullSet, Content);
    }

    [Theory]
    // An even box at negative coordinates, an odd one, and one straddling zero.
    [InlineData(-6, -4, -8, 4, 2, 6)]
    [InlineData(3, 5, 7, 3, 5, 1)]
    [InlineData(-2, -1, -3, 5, 3, 7)]
    public void ARayFromEachSideHitsThatFaceAtTheCellUnderIt(int ax, int ay, int az, int sx, int sy, int sz)
    {
        var box = Box("hull", new GridSize(ax, ay, az), new GridSize(sx, sy, sz));
        DesignBox[] boxes = [box];
        for (var side = 0; side < 6; side++)
        {
            var axis = side >> 1;
            var normal = EditorGeometry.Normal(side);
            // Every cell of the face, aimed at from straight out, is the cell the face reports.
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            for (var cu = EditorGeometry.Of(box.At, u); cu < EditorGeometry.Of(box.At, u) + EditorGeometry.Of(box.Size, u); cu++)
            {
                for (var cv = EditorGeometry.Of(box.At, v); cv < EditorGeometry.Of(box.At, v) + EditorGeometry.Of(box.Size, v); cv++)
                {
                    // A point a little off the cell's centre, towards its low corner, so rounding is tested too.
                    var target = EditorGeometry.Middle(box);
                    target[u] = cu - 0.4f;
                    target[v] = cv + 0.3f;
                    var origin = target + normal * 50f;
                    Assert.True(EditorGeometry.Pick(boxes, origin, -normal, out var hit), $"side {side}");
                    Assert.Equal((0, side), (hit.Block, hit.Side));
                    Assert.Equal(cu, EditorGeometry.Of(hit.Cell, u));
                    Assert.Equal(cv, EditorGeometry.Of(hit.Cell, v));
                    var outer = (side & 1) == 0 ? EditorGeometry.Of(box.At, axis) + EditorGeometry.Of(box.Size, axis) - 1 : EditorGeometry.Of(box.At, axis);
                    Assert.Equal(outer, EditorGeometry.Of(hit.Cell, axis));
                }
            }
        }
    }

    [Fact]
    public void TheNearestBoxIsPickedAndARayStartingInsideABoxSeesPastIt()
    {
        DesignBox[] boxes = [Box("core", new GridSize(0, 0, 0), One), Box("hull", new GridSize(0, 0, 3), One), Box("hull", new GridSize(0, 0, -3), One)];
        Assert.True(EditorGeometry.Pick(boxes, new Vector3(0, 0, 20), -Vector3.UnitZ, out var hit));
        Assert.Equal((1, 4), (hit.Block, hit.Side));
        // From inside the middle block the ray leaves it and meets the far one's +z face.
        Assert.True(EditorGeometry.Pick(boxes, Vector3.Zero, -Vector3.UnitZ, out hit));
        Assert.Equal((2, 4), (hit.Block, hit.Side));
        Assert.False(EditorGeometry.Pick(boxes, new Vector3(5, 5, 5), Vector3.UnitX, out _));
    }

    [Fact]
    public void TheGhostSitsOnThePickedCellAndChecksWithoutChangingTheDraft()
    {
        var draft = Draft(Box("core", new GridSize(-2, -1, -3), new GridSize(4, 2, 6)), Box("hull", new GridSize(2, -1, -3), One));
        var version = draft.Version;
        // Aim at the +y face over cell x = -2, z = 1.
        Assert.True(EditorGeometry.Pick(draft.Boxes, new Vector3(-2.2f, 30f, 1.1f), -Vector3.UnitY, out var hit));
        Assert.Equal((0, 2, new GridSize(-2, 0, 1)), (hit.Block, hit.Side, hit.Cell));
        var at = draft.Snap(hit.Block, hit.Side, One, hit.Cell);
        Assert.Equal(new GridSize(-2, 1, 1), at);
        Assert.Equal(DraftEdit.Done, draft.CanAdd(at, One));
        // Overlapping, too large and resizes into another block are all answered without an edit.
        Assert.Equal(DraftEdit.Overlaps, draft.CanAdd(new GridSize(0, 0, 0), One));
        Assert.Equal(DraftEdit.BadSize, draft.CanAdd(at, new GridSize(Content.Blocks.Limits.MaxBoxSide + 1, 1, 1)));
        Assert.Equal(DraftEdit.Overlaps, draft.CanResize(0, 0, 5));
        Assert.Equal(DraftEdit.Done, draft.CanResize(0, 2, 3));
        Assert.Equal(DraftEdit.Unchanged, draft.CanResize(0, 2, 2));
        Assert.Equal(version, draft.Version);
        Assert.False(draft.CanUndo);
        Assert.Equal(2, draft.Boxes.Length);
    }

    [Fact]
    public void AFrozenCopyExportsTheDraftAsItWasWhileTheDraftChangesOn()
    {
        var draft = BuildDraft.From(Content.Pod, Content.Pod.FullSet, Content);
        Assert.Equal(DraftEdit.Done, draft.Add(draft.Snap(2, 5, One), One, Content.Blocks.Get("hull")));
        var copy = draft.Copy();
        var expected = draft.Export();
        Assert.Equal(draft.Version, copy.Version);

        // Later edits do not reach the copy.
        Assert.Equal(DraftEdit.Done, draft.Delete(0));
        var frozen = copy.Export();
        Assert.True(frozen.Ok, frozen.Problem);
        Assert.Equal(expected.Bytes, frozen.Bytes);
        Assert.Equal(Content.Pod.BlockCount + 1, copy.BlockCount);
        Assert.False(draft.Export().Ok);
    }

    [Fact]
    public void EveryShippedPartHasARoleAndTheCoreIsNamedForWhatItIs()
    {
        var roles = new HashSet<PartRole>();
        foreach (var type in Content.Blocks.Types)
            roles.Add(EditorGeometry.Role(type));
        Assert.Equal(PartRole.Core, EditorGeometry.Role(Content.Blocks.Get("core")));
        Assert.Equal(PartRole.Engine, EditorGeometry.Role(Content.Blocks.Get("engine")));
        Assert.Equal(PartRole.Structure, EditorGeometry.Role(Content.Blocks.Get("hull")));
        Assert.True(roles.Count >= 6);
        // Plain structure keeps the hull's own colour; every other part is tinted.
        var hull = new Vector3(0.4f, 0.5f, 0.6f);
        Assert.Equal(hull, EditorGeometry.BlockColor(hull, Content.Blocks.Get("hull")));
        Assert.NotEqual(hull, EditorGeometry.BlockColor(hull, Content.Blocks.Get("core")));
    }
}
