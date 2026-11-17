using System.Security.Cryptography;
using System.Text;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The action group of a mounted part in the build draft and in design files: placing, choosing, mirroring and saving it.</summary>
public sealed class MountGroupTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly GridSize One = new(1, 1, 1);
    private static BlockType Laser => Content.Blocks.Get("miningLaser");
    private static BlockType Cold => Content.Blocks.Get("coldGasThruster");

    private static BuildDraft Draft(params DesignBox[] boxes)
    {
        var design = HullDesign.Of(new BoxDesign("test", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        return BuildDraft.From(design, design.FullSet, Content);
    }

    private static DesignBox Box(string type, GridSize at) => new(at, One, Content.Blocks.Get(type));

    [Fact]
    public void ANewLaserIsInGroupOneAndOtherMountsHaveNone()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 2, 2, Laser));
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Down, 2, 2, Cold));
        Assert.Equal(1, draft.Mounts[0].Group);
        Assert.Equal(0, draft.Mounts[1].Group);
    }

    [Fact]
    public void ChoosingAGroupIsOneStepOfHistoryAndALaterChoiceOfTheSameGroupChangesNothing()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)));
        draft.AddMount(1, BlockFacing.Up, 2, 2, Laser);
        var version = draft.Version;
        Assert.Equal(DraftEdit.Done, draft.SetMountGroup(0, 3));
        Assert.Equal(3, draft.Mounts[0].Group);
        Assert.True(draft.Version > version);

        // The same group again is not an edit: nothing is recorded, so one undo still takes the choice back.
        version = draft.Version;
        Assert.Equal(DraftEdit.Unchanged, draft.SetMountGroup(0, 3));
        Assert.Equal(version, draft.Version);
        Assert.Equal(DraftEdit.Done, draft.SetMountGroup(0, ActionGroups.None));
        Assert.Equal(0, draft.Mounts[0].Group);
        Assert.True(draft.Undo());
        Assert.Equal(3, draft.Mounts[0].Group);
        Assert.True(draft.Undo());
        Assert.Equal(1, draft.Mounts[0].Group);
        Assert.True(draft.Redo());
        Assert.True(draft.Redo());
        Assert.Equal(0, draft.Mounts[0].Group);
        Assert.Single(draft.Mounts.ToArray());
    }

    [Fact]
    public void APartThatIsNoGroupPartRefusesEveryGroupAndTheDraftIsLeftAlone()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)));
        draft.AddMount(1, BlockFacing.Up, 2, 2, Laser);
        draft.AddMount(1, BlockFacing.Down, 2, 2, Cold);
        var version = draft.Version;
        Assert.Equal(DraftEdit.NoGroup, draft.SetMountGroup(1, 1));
        Assert.Equal(DraftEdit.NoGroup, draft.SetMountGroup(0, ActionGroups.Count + 1));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.SetMountGroup(5, 1));
        // A thruster in none is already as it is; a laser and a thruster together are refused whole.
        Assert.Equal(DraftEdit.Unchanged, draft.SetMountGroup(1, 0));
        Assert.Equal(DraftEdit.NoGroup, draft.SetMountGroups([0, 1], 2));
        Assert.Equal(version, draft.Version);
        Assert.Equal(1, draft.Mounts[0].Group);
    }

    [Fact]
    public void SeveralPartsTakeAGroupAsOneStep()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)));
        draft.AddMount(1, BlockFacing.Up, 2, 2, Laser);
        draft.AddMount(1, BlockFacing.Down, 2, 2, Laser);
        Assert.Equal(DraftEdit.Done, draft.SetMountGroups([0, 1], 7));
        Assert.All(draft.Mounts.ToArray(), mount => Assert.Equal(7, mount.Group));
        Assert.True(draft.Undo());
        Assert.All(draft.Mounts.ToArray(), mount => Assert.Equal(1, mount.Group));
    }

    [Fact]
    public void MirroredCopiesKeepTheGroupAndAMirroredDeleteFindsACopyThatIsInAnotherGroup()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)), Box("hull", new GridSize(-1, 0, 0)));
        Assert.Equal(DraftEdit.Done, draft.AddMountMirrored(1, BlockFacing.Up, 2, 2, Laser, MirrorAxes.X, out _));
        Assert.Equal(2, draft.Mounts.Length);
        Assert.All(draft.Mounts.ToArray(), mount => Assert.Equal(1, mount.Group));

        // Choosing is not mirrored; deleting still takes the copy, whatever group the player has put it in.
        Assert.Equal(DraftEdit.Done, draft.SetMountGroup(1, 4));
        Assert.Equal(DraftEdit.Done, draft.DeleteMirrored(default, [0], MirrorAxes.X));
        Assert.Empty(draft.Mounts.ToArray());
    }

    [Fact]
    public void APastedPartKeepsItsGroupAndTheGroupTravelsThroughExportAndTheWire()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)));
        draft.AddMount(1, BlockFacing.Up, 2, 2, Laser);
        draft.SetMountGroup(0, 10);
        Assert.Equal(DraftEdit.Done, draft.Paste([draft.Boxes[1]], new GridSize(0, 0, 3), [draft.Mounts[0] with { Parent = 0 }]));
        Assert.Equal(10, draft.Mounts[^1].Group);

        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.Equal([10, 10], export.Design!.Mounts.ToArray().Select(mount => (int)mount.Group));
        var bytes = export.Bytes!;
        var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
        Assert.Equal(export.Design.Mounts.ToArray(), decoded.Mounts.ToArray());
        Assert.Equal(bytes, DesignBytes.Encode(decoded));
    }

    [Fact]
    public void AGroupSurvivesAWrittenDesignFileAndOnlyAVersionSevenFileMayCarryOne()
    {
        var boxes = new[] { Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)) };
        var mounts = new[] { new DesignMount(1, BlockFacing.Up, 2, 2, Laser, 5), new DesignMount(1, BlockFacing.Down, 2, 2, Cold) };
        var design = new BoxDesign("test", 1, boxes, Content.Blocks.Limits, "test", mounts: mounts);
        using var stream = new MemoryStream();
        design.Write(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains($"\"version\":{BoxDesign.FormatVersion}", text);
        Assert.Contains("\"group\":5", text);
        Assert.DoesNotContain("\"group\":0", text);
        var again = BoxDesign.Read(stream.ToArray(), Content.Blocks, "again.json");
        Assert.Equal(mounts.OrderBy(mount => mount, Comparer<DesignMount>.Create(FaceMounts.Compare)), again.Mounts.ToArray());

        // A file of version 6 had no groups: one that names one is refused, as is a group outside 0 to 10 and one on a part that has none.
        var older = text.Replace($"\"version\":{BoxDesign.FormatVersion}", "\"version\":6");
        Assert.Contains("version 7", Assert.Throws<InvalidDataException>(() => BoxDesign.Read(Encoding.UTF8.GetBytes(older), Content.Blocks, "old.json")).Message);
        var wide = text.Replace("\"group\":5", "\"group\":11");
        Assert.Throws<InvalidDataException>(() => BoxDesign.Read(Encoding.UTF8.GetBytes(wide), Content.Blocks, "wide.json"));
        Assert.Throws<InvalidDataException>(() => new BoxDesign("test", 1, boxes, Content.Blocks.Limits, "test", mounts: [new DesignMount(1, BlockFacing.Up, 2, 2, Cold, 2)]));
    }
}
