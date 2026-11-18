using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Blocks placed between cells: a block's place is whole quarters of a cell (<see cref="GridPoint"/>) and its size
/// whole cells. The snap steps, overlap and touching, connectivity, mounts, mirroring, the file and the wire, mass,
/// the drawn hull and the shield shell, each with a block set half or a quarter cell over.
/// </summary>
public sealed class SubCellPlacementTests
{
    private static readonly GameContent Content = TestDesigns.Content;
    private static readonly GridSize One = new(1, 1, 1);
    private static BlockType Core => Content.Blocks.Get("core");
    private static BlockType Plate => Content.Blocks.Get("hull");
    private static BlockType Engine => Content.Blocks.Get("engine");
    private static BlockType Cold => Content.Blocks.Get("coldGasThruster");

    private static BuildDraft Draft(params DesignBox[] boxes)
    {
        var design = TestDesigns.Of(boxes);
        return BuildDraft.From(design, design.FullSet, Content);
    }

    [Fact]
    public void APlaceIsKeptInQuartersAndWholeCellsMeanWhatTheyDid()
    {
        // The constructor and a whole-cell size both name a whole cell; only Quarters names a place between cells.
        var whole = new GridPoint(1, -2, 3);
        Assert.Equal((4, -8, 12), (whole.X4, whole.Y4, whole.Z4));
        Assert.Equal(whole, (GridPoint)new GridSize(1, -2, 3));
        Assert.True(whole.IsWhole);
        Assert.Equal(new Vector3(1, -2, 3), whole.InCells);

        var between = GridPoint.Quarters(2, -1, 5);
        Assert.False(between.IsWhole);
        Assert.Equal(new Vector3(0.5f, -0.25f, 1.25f), between.InCells);
        Assert.Equal((0.5f, -0.25f, 1.25f), (between.X, between.Y, between.Z));
        Assert.Equal(GridPoint.Quarters(6, -9, 17), between + whole);
        Assert.Equal(between, between + whole - whole);
        Assert.Equal(GridPoint.Quarters(2, 7, 5), between.With(1, 7));
        Assert.Equal(-1, between.Of(1));

        // A box's edges, in quarter cells: its place and its place plus four a cell of size.
        var box = new DesignBox(between, new GridSize(2, 1, 3), Plate);
        Assert.Equal((2, 10), (box.Low4(0), box.High4(0)));
        Assert.Equal((-1, 3), (box.Low4(1), box.High4(1)));
        Assert.Equal((5, 17), (box.Low4(2), box.High4(2)));

        // A place too far to count in quarters stays far, so a limit still refuses it.
        Assert.Equal(int.MinValue, new GridPoint(int.MinValue, 0, 0).X4);
        Assert.Equal(int.MaxValue, new GridPoint(int.MaxValue, 0, 0).X4);
    }

    [Fact]
    public void AHalfStepCentresAOneCellPartOnATwoCellSideAndAQuarterStepsFiner()
    {
        // A plate two cells wide along x, under the core's own cell.
        var draft = Draft(new DesignBox(new(0, 0, 0), One, Core), new DesignBox(new(0, 1, 0), new GridSize(2, 1, 1), Plate));
        const int Up = 2;

        // With no place named the part is centred: a whole cell rounds towards negative, a half is exact.
        Assert.Equal(new GridPoint(0, 2, 0), draft.Snap(1, Up, One));
        Assert.Equal(GridPoint.Quarters(2, 8, 0), draft.Snap(1, Up, One, step: 2));
        Assert.Equal(GridPoint.Quarters(2, 8, 0), draft.Snap(1, Up, One, step: 1));

        // The pointer at x = 0.7 in grid space, where cell 0 spans -0.5 to 0.5 and cell 1 from 0.5 to 1.5.
        var point = new Vector3(0.7f, 1.5f, 0f);
        var target = draft.Boxes[1];
        Assert.Equal(new GridPoint(1, 1, 0), BuildDraft.FaceCell(target, Up, point));
        Assert.Equal(GridPoint.Quarters(2, 4, 0), BuildDraft.FaceCell(target, Up, point, 2));
        Assert.Equal(GridPoint.Quarters(3, 4, 0), BuildDraft.FaceCell(target, Up, point, 1));
        // At the step of a cell the place is a cell of the face. At a finer one its middle stays on the face, so
        // it reaches at most half a cell past an edge: the way a part is centred across two blocks side by side.
        Assert.Equal(new GridPoint(1, 1, 0), BuildDraft.FaceCell(target, Up, new Vector3(9f, 1.5f, 0f)));
        Assert.Equal(new GridPoint(0, 1, 0), BuildDraft.FaceCell(target, Up, new Vector3(-9f, 1.5f, 0f)));
        Assert.Equal(GridPoint.Quarters(6, 4, 0), BuildDraft.FaceCell(target, Up, new Vector3(9f, 1.5f, 0f), 1));
        Assert.Equal(GridPoint.Quarters(-2, 4, 0), BuildDraft.FaceCell(target, Up, new Vector3(-9f, 1.5f, 0f), 2));
        Assert.Equal(GridPoint.Quarters(6, 4, 0), BuildDraft.FaceCell(target, Up, new Vector3(1.45f, 1.5f, 0f), 2));
        var hung = draft.Snap(1, Up, One, GridPoint.Quarters(6, 4, 0), 2);
        Assert.Equal(GridPoint.Quarters(6, 8, 0), hung);
        Assert.Equal(DraftEdit.Done, draft.CanAdd(hung, One, Engine));

        // The ghost lands where the place says, at each step, and is placed there.
        foreach (var (step, x4) in new[] { (4, 4), (2, 2), (1, 3) })
        {
            var near = BuildDraft.FaceCell(target, Up, point, step);
            var at = draft.Snap(1, Up, One, near, step);
            Assert.Equal(GridPoint.Quarters(x4, 8, 0), at);
            Assert.Equal(DraftEdit.Done, draft.CanAdd(at, One, Engine));
            Assert.Equal(DraftEdit.Done, draft.Add(at, One, Engine));
            Assert.Equal(at, draft.Boxes[^1].At);
            Assert.Equal(One, draft.Boxes[^1].Size);
            Assert.True(draft.Undo());
        }

        // A step that is none of the three is refused loudly: it is a programming error, not a player's.
        Assert.Throws<ArgumentOutOfRangeException>(() => draft.Snap(1, Up, One, step: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildDraft.FaceCell(target, Up, point, 0));
        Assert.Equal([4, 2, 1], BuildDraft.SnapSteps.ToArray());
    }

    [Fact]
    public void TheStepCountsFromTheTargetsCornerSoAWholeStepLinesUpWithABlockSetBetweenCells()
    {
        // The plate itself sits a quarter cell over in x and half a cell in z.
        var draft = Draft(new DesignBox(new(0, 0, 0), One, Core), new DesignBox(GridPoint.Quarters(1, 4, 2), new GridSize(3, 1, 2), Plate));
        const int Up = 2;
        var target = draft.Boxes[1];

        // At the step of a cell every place across the face is whole cells from the plate's own corner.
        for (var x = -0.4f; x < 3.2f; x += 0.37f)
        {
            var at = draft.Snap(1, Up, One, BuildDraft.FaceCell(target, Up, new Vector3(x, 1.5f, 0.6f)));
            Assert.Equal(0, (at.X4 - target.Low4(0)) % 4);
            Assert.Equal(0, (at.Z4 - target.Low4(2)) % 4);
            Assert.Equal(target.High4(1), at.Y4);
            Assert.InRange(at.X4, target.Low4(0), target.High4(0) - 4);
        }

        // Against a side, flush with it: the new block starts exactly where the plate ends, a quarter cell off the grid.
        var beside = draft.Snap(1, 0, One);
        Assert.Equal(target.High4(0), beside.X4);
        Assert.Equal(DraftEdit.Done, draft.Add(beside, One, Plate));
        Assert.True(HullConnectivity.SharesFace(draft.Boxes[1], draft.Boxes[2]));
    }

    [Fact]
    public void OverlapIsRefusedAndTouchingAllowedToTheQuarterCell()
    {
        var draft = Draft(new DesignBox(new(0, 0, 0), One, Core));
        // Half a cell up beside the core: it touches half of the core's +x face.
        var upper = GridPoint.Quarters(4, 2, 0);
        Assert.Equal(DraftEdit.Done, draft.Add(upper, One, Plate));
        // Under it, flush: the two cover the core's face between them, and touch each other.
        var lower = GridPoint.Quarters(4, -2, 0);
        Assert.Equal(DraftEdit.Done, draft.Add(lower, One, Plate));

        // A quarter cell into either is refused, from every side; flush against them is not.
        Assert.Equal(DraftEdit.Overlaps, draft.CanAdd(new GridPoint(1, 0, 0), One, Plate));
        Assert.Equal(DraftEdit.Overlaps, draft.CanAdd(GridPoint.Quarters(7, 2, 0), One, Plate));
        Assert.Equal(DraftEdit.Overlaps, draft.CanAdd(GridPoint.Quarters(4, 5, 0), One, Plate));
        Assert.Equal(DraftEdit.Overlaps, draft.CanAdd(GridPoint.Quarters(4, 2, 3), One, Plate));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(GridPoint.Quarters(8, 2, 0), One, Plate));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(GridPoint.Quarters(4, 6, 0), One, Plate));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(GridPoint.Quarters(4, 2, 4), One, Plate));

        // A move by a quarter cell is checked the same way: into the core is refused, away is taken.
        Assert.Equal(DraftEdit.Overlaps, draft.CanMove([1, 2], GridPoint.Quarters(-1, 0, 0)));
        Assert.Equal(DraftEdit.Done, draft.Move([1, 2], GridPoint.Quarters(0, 0, 1)));
        Assert.Equal(GridPoint.Quarters(4, 2, 1), draft.Boxes[1].At);
        Assert.True(draft.Undo());

        // A design of them is admitted by the same rules a received one is, and a quarter cell of overlap is not.
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        var overlapping = Assert.Throws<InvalidDataException>(() => TestDesigns.Of(
            new DesignBox(new(0, 0, 0), One, Core), new DesignBox(GridPoint.Quarters(3, 0, 0), One, Plate)));
        Assert.Contains("overlaps", overlapping.Message);

        // The limits are counted in the same quarters.
        var edge = Content.Blocks.Limits.MaxDesignSide;
        Assert.Equal(DraftEdit.OutsideLimits, draft.CanAdd(GridPoint.Quarters(4 * edge - 3, 0, 0), One, Plate));
        Assert.Equal(DraftEdit.OutsideLimits, draft.CanMove([1], GridPoint.Quarters(0, 4 * edge - 5, 0)));
    }

    [Fact]
    public void TouchedAndCoveredFacesAndConnectivityAreExactForBlocksBetweenCells()
    {
        var core = new DesignBox(new(0, 0, 0), One, Core);
        var upper = new DesignBox(GridPoint.Quarters(4, 2, 0), One, Plate);
        var lower = new DesignBox(GridPoint.Quarters(4, -2, 0), One, Plate);
        // A quarter cell clear of the upper block: no face is shared across the gap.
        var apart = new DesignBox(GridPoint.Quarters(9, 2, 0), One, Plate);

        var both = TestDesigns.Of(core, upper, lower);
        // The core's +x face is touched, and wholly covered by the two halves together: it is not drawn.
        Assert.Equal(1, both.TouchedFaces[0] & 1);
        Assert.Equal(0, both.ExteriorFaces[0] & 1);
        // Each block's -x face is half against the core and half open: touched, and still drawn.
        Assert.NotEqual(0, both.TouchedFaces[1] & 2);
        Assert.NotEqual(0, both.ExteriorFaces[1] & 2);
        // The two meet along y, face to whole face.
        Assert.Equal(0, both.ExteriorFaces[1] & (1 << 3));
        Assert.Equal(0, both.ExteriorFaces[2] & (1 << 2));
        Assert.Equal(OpenFaces.Touched(both.Boxes, 1, upper.At, upper.Size), both.TouchedFaces[1]);

        var half = TestDesigns.Of(core, upper);
        Assert.Equal(1, half.TouchedFaces[0] & 1);
        Assert.NotEqual(0, half.ExteriorFaces[0] & 1);

        Assert.True(HullConnectivity.SharesFace(core, upper));
        Assert.True(HullConnectivity.SharesFace(upper, lower));
        Assert.False(HullConnectivity.SharesFace(upper, apart));
        // Only an edge in common: upper and a block diagonally off the core's corner.
        Assert.False(HullConnectivity.SharesFace(core, new DesignBox(GridPoint.Quarters(4, 4, 0), One, Plate)));

        var gapped = TestDesigns.Of(core, upper, apart);
        var connectivity = new HullConnectivity(gapped.BlockCount);
        connectivity.Find(gapped, gapped.FullSet);
        Assert.Equal(2, connectivity.Count);
        Assert.Equal(connectivity.Pieces[0], connectivity.Pieces[1]);
        Assert.NotEqual(connectivity.Pieces[0], connectivity.Pieces[2]);
        connectivity.Find(both, both.FullSet);
        Assert.Equal(1, connectivity.Count);
    }

    [Fact]
    public void AMountOnABlockBetweenCellsFitsAndIsRefusedToTheTwentiethOfACell()
    {
        var core = new DesignBox(new(0, 0, 0), One, Core);
        var offset = new DesignBox(GridPoint.Quarters(4, 2, 0), One, Plate);
        // On top of the core, three quarters of a cell wide over it: it covers x from 0 to 7.5 tenths there.
        var over = new DesignBox(GridPoint.Quarters(-1, 4, 0), One, Plate);
        DesignBox[] boxes = [core, offset, over];

        // A two by two array (four tenths) on the offset block's top, at its corner.
        var mount = new DesignMount(1, BlockFacing.Up, 0, 0, Cold, Size: 2);
        Assert.Equal(DraftEdit.Done, FaceMounts.Check(boxes, [], mount));
        var design = TestDesigns.Mounted(boxes, [mount]);
        var middle = design.Pivot;
        var box = new BoxDesign("T", 1f, boxes, Content.Blocks.Limits, "t", mounts: [mount]);
        // Its box: from the block's corner (x 1, top at 1.5 on the grid's edges), four tenths wide and two deep.
        var at = box.MountMiddle(mount);
        Assert.Equal(1.2 - 0.5, at.X, 12);
        Assert.Equal(1.6 - 0.5, at.Y, 12);
        Assert.Equal(0.2 - 0.5, at.Z, 12);
        Assert.Equal((float)((at.X - middle.X) * 1f), design.MountOffsets[0].X, 5);

        // On the core's top beside the block over it: a one-nozzle array is two tenths wide. The up face counts U
        // along z and V along x. From 8 along x it is clear of the block's 7.5; from 7 or 6 it is half a tenth or
        // more inside it.
        var small = new DesignMount(0, BlockFacing.Up, 0, 8, Cold, Size: 1);
        Assert.Equal(DraftEdit.Done, FaceMounts.Check(boxes, [], small));
        Assert.Equal(DraftEdit.Overlaps, FaceMounts.Check(boxes, [], small with { V = 7 }));
        Assert.Equal(DraftEdit.Overlaps, FaceMounts.Check(boxes, [], small with { V = 6 }));
        Assert.Equal(DraftEdit.OffFace, FaceMounts.Check(boxes, [], small with { V = 9 }));

        // On the core's right side, under the offset block that starts half a cell up: a square up to 5 tenths is
        // clear of it, one from 4 to 6 is not.
        var onCore = new DesignMount(0, BlockFacing.Right, 3, 0, Cold, Size: 1);
        Assert.Equal(DraftEdit.Done, FaceMounts.Check(boxes, [], onCore));
        Assert.Equal(DraftEdit.Overlaps, FaceMounts.Check(boxes, [], onCore with { U = 4 }));

        // In a draft: the mount goes on, the pointer's place on the face is counted from the block's own corner,
        // and a block moved over it takes it away in the same step.
        var draft = Draft(core, offset);
        draft.SnapMount(1, BlockFacing.Up, Cold, new Vector3(1.0f, 1f, 0f), out var u, out var v);
        // The face spans x from 0.5 to 1.5 in grid space; the pointer at 1.0 is its middle: (10 - 4) / 2 tenths in.
        Assert.Equal((3, 3), (u, v));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, u, v, Cold));
        Assert.Equal(DraftEdit.Done, draft.Add(GridPoint.Quarters(4, 8, 0), One, Plate));
        Assert.Single(draft.Mounts.ToArray());
        Assert.Equal(DraftEdit.Done, draft.Move([2], GridPoint.Quarters(0, -2, 0)));
        Assert.Empty(draft.Mounts.ToArray());
        Assert.True(draft.Undo());
        Assert.Single(draft.Mounts.ToArray());
    }

    [Fact]
    public void ResizeSplitAndMergeKeepAPlaceBetweenCellsAndAMountWhereItIs()
    {
        var draft = Draft(new DesignBox(new(0, 0, 0), One, Core), new DesignBox(GridPoint.Quarters(4, 2, 1), new GridSize(1, 1, 3), Content.Blocks.Get("nitrogenTank")));
        // A mount on the tank's top, over its second cell along z (the up face counts U along z and V along x).
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 13, 3, Cold, 1));

        // Growing the minus-z side by a cell moves the low corner a whole cell and the mount stays where it is.
        Assert.Equal(DraftEdit.Done, draft.Resize(1, 5, 4));
        Assert.Equal(GridPoint.Quarters(4, 2, -3), draft.Boxes[1].At);
        Assert.Equal(new GridSize(1, 1, 4), draft.Boxes[1].Size);
        Assert.Equal((23, 3), (draft.Mounts[0].U, draft.Mounts[0].V));
        Assert.True(draft.Undo());

        // Split along z: three tanks a cell long, each a cell further, the mount on the piece under it.
        Assert.Equal(DraftEdit.Done, draft.Split([1], 2));
        Assert.Equal(4, draft.Boxes.Length);
        Assert.Equal(GridPoint.Quarters(4, 2, 1), draft.Boxes[1].At);
        Assert.Equal(GridPoint.Quarters(4, 2, 5), draft.Boxes[2].At);
        Assert.Equal(GridPoint.Quarters(4, 2, 9), draft.Boxes[3].At);
        Assert.Equal((2, 3, 3), (draft.Mounts[0].Parent, draft.Mounts[0].U, draft.Mounts[0].V));

        // And merged back: the one tank it was, with the mount where it was.
        Assert.Equal(DraftEdit.Done, draft.Merge([1, 2, 3]));
        Assert.Equal(GridPoint.Quarters(4, 2, 1), draft.Boxes[1].At);
        Assert.Equal(new GridSize(1, 1, 3), draft.Boxes[1].Size);
        Assert.Equal((1, 13, 3), (draft.Mounts[0].Parent, draft.Mounts[0].U, draft.Mounts[0].V));

        // Two blocks half a cell out of line fill no box of whole cells.
        var loose = Draft(new DesignBox(new(0, 0, 0), One, Core), new DesignBox(new(1, 0, 0), One, Plate), new DesignBox(GridPoint.Quarters(8, 2, 0), One, Plate));
        Assert.Equal(DraftEdit.NotABox, loose.CanMerge([1, 2], out _));
    }

    [Fact]
    public void AMirroredCopyOfABlockBetweenCellsLandsExactlyAndCarriesItsMount()
    {
        var draft = Draft(new DesignBox(new(0, 0, 0), One, Core), new DesignBox(new(1, 0, 0), One, Plate), new DesignBox(new(-1, 0, 0), One, Plate));
        Assert.True(draft.TryMirrorFrame(out var centre2, out var low, out var high));
        // Twice the core's centre on the grid's edges, in quarter cells: its edges are 0 and 4.
        Assert.Equal(GridPoint.Quarters(4, 4, 4), centre2);
        Assert.Equal((GridPoint.Quarters(-4, 0, 0), GridPoint.Quarters(8, 4, 4)), (low, high));

        // An engine on top of the right plate, a quarter cell further out and half a cell back.
        var engine = new DesignBox(GridPoint.Quarters(5, 4, 2), One, Engine, Facing: BlockFacing.Right);
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(engine, MirrorAxes.X, out var skipped));
        Assert.Equal(0, skipped);
        Assert.Equal(5, draft.Boxes.Length);
        var copy = draft.Boxes[4];
        // Reflected about x = 0.5 cells: the engine's far edge 9 becomes the copy's near edge -5.
        Assert.Equal(GridPoint.Quarters(-5, 4, 2), copy.At);
        Assert.Equal(BlockFacing.Left, copy.Facing);
        Assert.Equal(engine.Low4(0) + engine.High4(0) - centre2.X4, centre2.X4 - (copy.Low4(0) + copy.High4(0)));
        Assert.True(draft.Undo());

        // With X and Z on: four copies, and one that lands on itself along z (its middle on the plane) is one block.
        var onPlane = new DesignBox(GridPoint.Quarters(5, 4, 0), One, Plate);
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(onPlane, MirrorAxes.X | MirrorAxes.Z, out skipped));
        Assert.Equal(5, draft.Boxes.Length);
        Assert.True(draft.Undo());
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(engine, MirrorAxes.X | MirrorAxes.Z, out skipped));
        Assert.Equal(7, draft.Boxes.Length);
        Assert.Contains(draft.Boxes.ToArray(), b => b.At == GridPoint.Quarters(5, 4, -2));
        Assert.Contains(draft.Boxes.ToArray(), b => b.At == GridPoint.Quarters(-5, 4, -2));
        Assert.True(draft.Undo());

        // A mount on the engine goes on its mirrored copy too, reflected across the face.
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(engine, MirrorAxes.X, out _));
        Assert.Equal(DraftEdit.Done, draft.AddMountMirrored(3, BlockFacing.Up, 1, 2, Cold, MirrorAxes.X, out skipped, 2));
        Assert.Equal(0, skipped);
        var mounts = draft.Mounts.ToArray();
        Assert.Equal(2, mounts.Length);
        Assert.Equal((3, 1, 2), (mounts[0].Parent, mounts[0].U, mounts[0].V));
        // On a face a cell wide a square of four tenths one tenth in is five tenths in from the other side. The up
        // face's axes are z then x, so the mirrored one is V.
        Assert.Equal((4, 1, 10 - 2 - 4), (mounts[1].Parent, mounts[1].U, mounts[1].V));
        Span<int> a = stackalloc int[3], b = stackalloc int[3], c = stackalloc int[3], d = stackalloc int[3];
        FaceMounts.Box(draft.Boxes[3], mounts[0], a, b);
        FaceMounts.Box(draft.Boxes[4], mounts[1], c, d);
        // In twentieths, about the plane at x = 0.5 cells (10): each edge of one is the other's, reflected.
        Assert.Equal((20 - b[0], 20 - a[0]), (c[0], d[0]));
        Assert.Equal((a[1], b[1], a[2], b[2]), (c[1], d[1], c[2], d[2]));

        // Deleting the engine with the mirror on takes its copy and both mounts.
        Assert.Equal(DraftEdit.Done, draft.DeleteMirrored([3], [], MirrorAxes.X));
        Assert.Equal(3, draft.Boxes.Length);
        Assert.Empty(draft.Mounts.ToArray());
    }

    private const string Head = "\"name\": \"T\", \"class\": \"ship\", \"blockSize\": 2, \"color\": [0.5, 0.5, 0.5], \"blocks\": [";

    private static BoxDesign Parse(string json) => BoxDesign.Read(Encoding.UTF8.GetBytes(json), Content.Blocks, "test.json");

    [Fact]
    public void ADesignFileKeepsPlacesBetweenCellsAndAnOlderFileLoadsAsItDid()
    {
        // An older file: whole cells, which mean what they always did. Written again it is the current version and
        // still whole numbers; read again it is the same design.
        var old = Parse("{ \"version\": 8, " + Head + "{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [1, 0, -2], \"type\": \"hull\", \"size\": [2, 1, 1] }], \"mounts\": [{ \"on\": 1, \"face\": \"up\", \"at\": [3, 2], \"type\": \"coldGasThruster\", \"size\": 2 }] }");
        Assert.Equal(new GridPoint(1, 0, -2), old.Blocks[1].At);
        Assert.True(old.Blocks[1].At.IsWhole);
        using var stream = new MemoryStream();
        old.Write(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\":10", text);
        Assert.Contains("\"at\":[1,0,-2]", text);
        var again = Parse(text);
        Assert.Equal(old.Blocks.ToArray(), again.Blocks.ToArray());
        Assert.Equal(old.Mounts.ToArray(), again.Mounts.ToArray());
        Assert.Equal(old.Center, again.Center);

        // The current version: a place may be a number of quarters, and is written as one.
        var between = Parse("{ \"version\": 9, " + Head + "{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [1, 0.5, -0.25], \"type\": \"engine\", \"facing\": \"back\" }, { \"at\": [-1.75, 0, 0], \"type\": \"hull\" }] }");
        Assert.Equal(GridPoint.Quarters(4, 2, -1), between.Blocks[1].At);
        Assert.Equal(GridPoint.Quarters(-7, 0, 0), between.Blocks[2].At);
        stream.SetLength(0);
        between.Write(stream);
        text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"at\":[1,0.5,-0.25]", text);
        Assert.Contains("\"at\":[-1.75,0,0]", text);
        Assert.Equal(between.Blocks.ToArray(), Parse(text).Blocks.ToArray());

        // Refused: a place between cells in an older file, a place that is no quarter, one far out of range, and a
        // size that is not whole cells.
        var body = "{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [1, 0.5, 0], \"type\": \"hull\" }] }";
        Assert.Contains("requires version 9", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 8, " + Head + body)).Message);
        Assert.Contains("quarter", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 9, " + Head + body.Replace("0.5", "0.1"))).Message);
        Assert.Contains("quarter", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 9, " + Head + body.Replace("0.5", "1e300"))).Message);
        Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 9, " + Head + "{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [1, 0, 0], \"type\": \"hull\", \"size\": [1.5, 1, 1] }] }"));
        Assert.Equal(10, BoxDesign.FormatVersion);
    }

    [Fact]
    public void DesignBytesCarryPlacesInQuartersAndRoundTripToTheBit()
    {
        var design = TestDesigns.Mounted(
            [new DesignBox(new(0, 0, 0), One, Core), new DesignBox(GridPoint.Quarters(4, 2, -1), One, Engine, Facing: BlockFacing.Left), new DesignBox(GridPoint.Quarters(-7, 0, 0), new GridSize(1, 2, 1), Plate)],
            [new DesignMount(1, BlockFacing.Up, 3, 3, Cold, Size: 2)]);
        var bytes = DesignBytes.Encode(design);
        Assert.Equal(7, DesignBytes.Version);
        var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
        Assert.Equal(design.Boxes.ToArray(), decoded.Boxes.ToArray());
        Assert.Equal(design.Mounts.ToArray(), decoded.Mounts.ToArray());
        Assert.Equal(design.BlockOffsets, decoded.BlockOffsets);
        Assert.Equal(design.MountOffsets, decoded.MountOffsets);
        Assert.Equal(design.Pivot, decoded.Pivot);
        Assert.Equal(bytes, DesignBytes.Encode(decoded));

        // A whole-cell place travels as four times its cell.
        var whole = DesignBytes.Encode(TestDesigns.Of(new DesignBox(new(3, -2, 5), One, Core)));
        var name = Encoding.UTF8.GetByteCount("core");
        var at = whole.AsSpan(whole.Length - 4 /* mount count */ - 2 /* shape, facing */ - 24);
        Assert.Equal((12, -8, 20), (BitConverter.ToInt32(at), BitConverter.ToInt32(at[4..]), BitConverter.ToInt32(at[8..])));
        Assert.Equal((1, 1, 1), (BitConverter.ToInt32(at[12..]), BitConverter.ToInt32(at[16..]), BitConverter.ToInt32(at[20..])));
        Assert.Equal((byte)name, whole[whole.Length - 4 - 2 - 24 - name - 1]);

        // An exported draft with a block between cells is admitted through the same decoding.
        var draft = Draft(new DesignBox(new(0, 0, 0), One, Core));
        Assert.Equal(DraftEdit.Done, draft.Add(GridPoint.Quarters(4, 1, 2), One, Plate));
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.Equal(GridPoint.Quarters(4, 1, 2), export.Design!.Boxes[1].At);
    }

    [Fact]
    public void MassCentreAndInertiaFollowABlockSetBetweenCells()
    {
        var core = new DesignBox(new(0, 0, 0), One, Core);
        var design = TestDesigns.Of(core, new DesignBox(GridPoint.Quarters(4, 2, 0), One, Plate));
        var (mc, mp) = ((double)Core.Mass, (double)Plate.Mass);
        Assert.Equal(mp * 1.0 / (mc + mp), design.Pivot.X, 12);
        Assert.Equal(mp * 0.5 / (mc + mp), design.Pivot.Y, 12);
        Assert.Equal(0.0, design.Pivot.Z, 12);
        Assert.Equal(new Vector3((float)(1.0 - design.Pivot.X), (float)(0.5 - design.Pivot.Y), 0f), design.BlockOffsets[1]);
        Assert.Equal(new Vector3(2f, 1.5f, 1f), design.Stats.Size);

        // The same hull moved as a whole by part of a cell is the same hull: every figure that follows from its
        // blocks, to rounding.
        DesignBox[] hull =
        [
            core, new DesignBox(new(1, 0, 0), new GridSize(2, 1, 1), Plate), new DesignBox(GridPoint.Quarters(6, 4, 0), One, Engine),
            new DesignBox(new(0, 0, 1), One, Content.Blocks.Get("gyro")), new DesignBox(new(0, -1, 0), One, Content.Blocks.Get("generator")),
        ];
        var shift = GridPoint.Quarters(1, -2, 3);
        var moved = hull.Select(b => b with { At = b.At + shift }).ToArray();
        var a = TestDesigns.Of(hull);
        var b = TestDesigns.Of(moved);
        Assert.Equal(a.Flight.Mass, b.Flight.Mass);
        Assert.Equal(a.Stats.Size, b.Stats.Size);
        Assert.Equal(a.Flight.TurnRate.X, b.Flight.TurnRate.X, 4);
        Assert.Equal(a.Flight.TurnRate.Y, b.Flight.TurnRate.Y, 4);
        Assert.Equal(a.Flight.TurnRate.Z, b.Flight.TurnRate.Z, 4);
        Assert.Equal(a.Flight.Radius, b.Flight.Radius, 4);
        for (var i = 0; i < hull.Length; i++)
            Assert.True((a.BlockOffsets[i] - b.BlockOffsets[i]).Length() < 1e-5f);
        Assert.Equal(a.ExteriorFaces, b.ExteriorFaces);
        Assert.Equal(a.TouchedFaces, b.TouchedFaces);
    }

    [Fact]
    public void TheDrawnHullAndTheEditorsPickFollowABlockBetweenCells()
    {
        var design = TestDesigns.Of(new DesignBox(new(0, 0, 0), One, Core), new DesignBox(GridPoint.Quarters(4, 2, 0), One, Plate));
        var mesh = MeshBuilder.Hull(design);
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        foreach (var vertex in mesh.Vertices)
        {
            var p = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
            low = Vector3.Min(low, p);
            high = Vector3.Max(high, p);
        }

        // Two cells wide, one and a half high: the plate stands half a cell above the core.
        Assert.True((high - low - new Vector3(2f, 1.5f, 1f)).Length() < 1e-5f);
        // Every face of both is drawn: the two only half cover each other.
        Assert.Equal(12 * 4, mesh.Vertices.Count);

        // A ray down onto the plate's top hits it at its own height, and a ray at the core's top beside it hits the core.
        DesignBox[] boxes = [.. design.Boxes];
        Assert.True(EditorGeometry.Pick(boxes, new Vector3(1f, 9f, 0f), -Vector3.UnitY, out var hit));
        Assert.Equal((1, 2), (hit.Block, hit.Side));
        Assert.Equal(9f - 1f, hit.Distance, 5);
        Assert.Equal(GridPoint.Quarters(4, 2, 0), hit.Cell);
        Assert.True(EditorGeometry.Pick(boxes, new Vector3(0f, 9f, 0f), -Vector3.UnitY, out hit));
        Assert.Equal((0, 2), (hit.Block, hit.Side));
        Assert.Equal(9f - 0.5f, hit.Distance, 5);
        // From the side, level with the plate's lower quarter: the plate, not the core behind it.
        Assert.True(EditorGeometry.Pick(boxes, new Vector3(9f, 0.25f, 0f), -Vector3.UnitX, out hit));
        Assert.Equal((1, 0), (hit.Block, hit.Side));
        // Under the plate's lower edge, at the core's level: nothing of the plate is there, the ray reaches the core.
        Assert.True(EditorGeometry.Pick(boxes, new Vector3(9f, -0.25f, 0f), -Vector3.UnitX, out hit));
        Assert.Equal((0, 0), (hit.Block, hit.Side));
        Assert.Equal(new Vector3(0.5f, 0f, -0.5f), EditorGeometry.Middle(boxes[1]) - new Vector3(0.5f, 0.5f, 0.5f));
    }

    public static TheoryData<int, int, int> Shifts => new() { { 2, 0, 0 }, { 1, 0, 0 }, { 3, 2, 1 }, { 0, 1, 2 }, { 2, 2, 2 } };

    [Theory]
    [MemberData(nameof(Shifts))]
    public void TheShieldShellEnclosesABlockSetBetweenCells(int x4, int y4, int z4)
    {
        // A plate two cells wide with an engine on it, set over by the shift; a wedge beside it, shifted too.
        var shift = GridPoint.Quarters(x4, y4, z4);
        var design = TestDesigns.Of(
            new DesignBox(new(0, 0, 0), One, Core),
            new DesignBox(new(0, 1, 0), new GridSize(2, 1, 1), Plate),
            new DesignBox(new GridPoint(0, 2, 0) + shift, One, Engine),
            new DesignBox(new GridPoint(3, 0, 0) + shift, new GridSize(1, 2, 1), Plate, BlockShape.WedgeZPlusPlus));
        var usage = GeometryAdmission.Measure(design, Content.Blocks.Limits.SurfaceVertices, Content.Blocks.Limits.Shield);
        Assert.True(usage.Fits, $"{usage.Vertices} of {usage.Limit}, {usage.Overflow}");
        SmoothShieldTests.Holds(design, design.FullSet, SmoothShieldTests.Intact(design));

        // And with a block lost: the damaged hull's own shell holds what is left.
        var alive = design.FullSet.ToArray();
        BlockSet.Remove(alive, 1);
        var damaged = SmoothShieldTests.Triangles(design, alive, ShellShape.Default);
        Assert.NotNull(damaged);
        SmoothShieldTests.Holds(design, alive, damaged);
    }

    [Fact]
    public void AWholeHullMovedByPartOfACellHasAShellThatHoldsAndAnIsolatedBoxKeepsItsExactRoundedShell()
    {
        // One block alone is a rounded box, triangulated exactly, wherever it sits: the same vertices about its
        // centre, to rounding of the change of frame.
        var alone = TestDesigns.Of(new DesignBox(new(0, 0, 0), new GridSize(2, 1, 3), Core));
        var moved = TestDesigns.Of(new DesignBox(GridPoint.Quarters(1, 2, 3), new GridSize(2, 1, 3), Core));
        var (a, boxesA) = Shell(alone);
        var (b, boxesB) = Shell(moved);
        Assert.Equal((1, 1), (boxesA, boxesB));
        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
        {
            Assert.True((a[i].Position - b[i].Position).Length() < 1e-4f, $"vertex {i}");
            Assert.True((a[i].Normal - b[i].Normal).Length() < 1e-4f, $"normal {i}");
        }

        // Two blocks half a cell out of line fill no box: they are sampled, and the shell still holds both.
        var stepped = TestDesigns.Of(new DesignBox(new(0, 0, 0), One, Core), new DesignBox(GridPoint.Quarters(4, 2, 0), One, Plate));
        var (shell, rounded) = Shell(stepped);
        Assert.Equal(0, rounded);
        SmoothShieldTests.Holds(stepped, stepped.FullSet, shell);

        // A larger mixed hull, every block moved by the same part of a cell.
        var mixed = BoxDesignTests.Generate(40, 7);
        var shifted = mixed.Select(box => box with { At = box.At + GridPoint.Quarters(3, 1, 2) }).ToArray();
        var design = TestDesigns.Of(shifted);
        var whole = SmoothShieldTests.Triangles(design, design.FullSet, ShellShape.Default);
        Assert.NotNull(whole);
        SmoothShieldTests.Holds(design, design.FullSet, whole);
    }

    /// <summary>
    /// The intact shell of a design as triangles, and how many rounded boxes the build drew, read under the one
    /// lock the build ran under: other tests use the shared builder at the same time.
    /// </summary>
    private static (ShellVertex[] Triangles, int RoundedBoxes) Shell(HullDesign design) =>
        ShieldSurface.WithShared(design, static (builder, hull) =>
        {
            var vertices = new ShellVertex[GeometryAdmission.MaxVertices];
            var indices = new ushort[GeometryAdmission.MaxIndices];
            var count = builder.Build(hull, hull.FullSet, ShellShape.Default, vertices, indices, out var indexCount);
            Assert.True(count >= 0, builder.Work.Overflow);
            return (SmoothShieldTests.Expand(vertices.AsSpan(0, count), indices.AsSpan(0, indexCount)), builder.Work.RoundedBoxes);
        });

    [Fact]
    public void ARackABayAndAHopperWorkOnAPlaceBetweenCells()
    {
        // Nothing here needs whole cells: a rack's packing is counted from its own corner, and the tractor's view of
        // it from the blocks' real edges.
        var rack = new DesignBox(GridPoint.Quarters(6, 4, 1), new GridSize(2, 2, 2), Content.Blocks.Get("cargo"), BlockShape.Box, BlockFacing.Left);
        DesignBox[] boxes = [new DesignBox(new(0, 0, 0), One, Core), new DesignBox(new(1, 0, 0), new GridSize(2, 1, 1), Plate), rack];
        var tractor = new DesignMount(0, BlockFacing.Up, 2, 2, Content.Blocks.Get("tractorBeam"));
        var design = TestDesigns.Mounted(boxes, [tractor]);
        Assert.True(TractorReach.Rack(design.Boxes, design.Mounts, design.FullSet, design.BlockSize, 0, 2, out var point, out _, out _));
        // The aim point is on the rack's real box, which starts a cell and a half out.
        var middle = TractorReach.Middle(rack, 1f);
        Assert.Equal(new Vector3(2.5f, 2f, 1.25f), middle);
        Assert.True(Vector3.Abs(point - middle).X <= 1f + 1e-4f && Vector3.Abs(point - middle).Y <= 1f + 1e-4f && Vector3.Abs(point - middle).Z <= 1f + 1e-4f);

        // A bay and a hopper keep their facing and their place.
        var draft = Draft(new DesignBox(new(0, 0, 0), One, Core));
        Assert.Equal(DraftEdit.Done, draft.Add(GridPoint.Quarters(4, 2, 0), One, Content.Blocks.Get("droneBay"), facing: BlockFacing.Right));
        Assert.Equal(DraftEdit.Done, draft.Add(GridPoint.Quarters(-4, -1, 0), One, Content.Blocks.Get("oreHopper"), facing: BlockFacing.Up));
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.Single(export.Design!.Bays);
        Assert.Single(export.Design.Hoppers);
    }
}
