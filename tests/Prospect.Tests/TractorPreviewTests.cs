using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class TractorPreviewTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static DesignBox Box(string type, int x, int y) => new(new(x, y, 0), new(1, 1, 1), Content.Blocks.Get(type));

    [Fact]
    public void PreviewUsesSharedReachAndRefreshesAfterAnEditWithoutAllocating()
    {
        var design = TestDesigns.Mounted([Box("core", 0, 0), Box("cargo", 2, 1), Box("cargo", 0, -2)],
            [new(0, BlockFacing.Up, 2, 2, Content.Blocks.Get("tractorBeam"))]);
        var draft = BuildDraft.From(design, design.FullSet, Content);
        var preview = new TractorPreview(Content.Blocks.Limits.MaxMounts);
        preview.Update(draft, 1);
        Assert.False(preview.Complete);
        Assert.True(preview.Linked(0, 1));
        preview.Update(draft, 1);
        Assert.True(preview.Complete);
        Assert.Equal(2, preview.Racks);
        Assert.Equal(1, preview.Unreached);
        for (var r = 1; r <= 2; r++)
        {
            var reached = TractorReach.Rack(draft.Boxes, draft.Mounts, [], draft.BlockSize, 0, r, out _, out var side);
            Assert.Equal(reached, preview.Reached(r));
            if (reached) Assert.Equal(side, preview.Face(0, r));
        }
        Assert.Equal(DraftEdit.Done, draft.Add(new(1, 1, 0), new(1, 1, 1), Content.Blocks.Get("hull")));
        preview.Update(draft, 2);
        Assert.Equal(2, preview.Unreached);
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (probe.Measuring())
            {
                preview.Update(null, 2);
                preview.Update(draft, 2);
            }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void RacksWithoutTractorsAreUnreachedAndTractorHasItsPaletteRole()
    {
        var design = TestDesigns.Of(Box("core", 0, 0), Box("cargo", 1, 0));
        var preview = new TractorPreview(1);
        preview.Update(BuildDraft.From(design, design.FullSet, Content), 1);
        Assert.True(preview.Complete);
        Assert.Equal(1, preview.Unreached);
        Assert.Equal(PartRole.Tractor, EditorGeometry.Role(Content.Blocks.Get("tractorBeam")));
    }

    [Fact]
    public void BeamAppearanceComesFromContentAndRejectsInvalidValues()
    {
        var look = PropulsionLook.Load().Tractor;
        Assert.NotNull(look);
        look.Check();
        Assert.Throws<InvalidDataException>(() => new TractorLook().Check());
        Assert.Throws<InvalidDataException>(() => new TractorLook
        {
            BeamColor = 1,
            WarningColor = 1,
            CoreWidth = 1,
            GlowWidth = 2,
            Opacity = float.NaN,
            WarningTint = .5f,
            PreviewPairsPerFrame = 128,
        }.Check());
        Assert.Throws<InvalidDataException>(() => new TractorLook
        {
            BeamColor = 1,
            WarningColor = 1,
            CoreWidth = 1,
            GlowWidth = 2,
            Opacity = .8f,
            WarningTint = .5f,
            PreviewPairsPerFrame = 128,
            HazeOpacity = -0.1f,
        }.Check());
        Assert.Throws<InvalidDataException>(() => new TractorLook
        {
            BeamColor = 1,
            WarningColor = 1,
            CoreWidth = 1,
            GlowWidth = 2,
            Opacity = .8f,
            WarningTint = .5f,
            PreviewPairsPerFrame = 128,
            StrandCount = 0,
        }.Check());
    }

    [Fact]
    public void PreviewAndServerFitAgreeOnARackThatStraddlesTheFacePlane()
    {
        var design = TestDesigns.Mounted([Box("core", 0, 0), new(new(1, -1, 0), new(1, 3, 1), Content.Blocks.Get("cargo"))],
            [new(0, BlockFacing.Up, 2, 2, Content.Blocks.Get("tractorBeam"))]);
        var draft = BuildDraft.From(design, design.FullSet, Content);
        var preview = new TractorPreview(Content.Blocks.Limits.MaxMounts);
        preview.Update(draft, 10);
        Assert.True(preview.Complete);
        Assert.True(preview.Linked(0, 1));
        Assert.Equal(0, preview.Unreached);
        var aim = TractorReach.Aim(draft.Boxes, draft.Mounts[0], draft.BlockSize, 1, preview.Face(0, 1), preview.Nearest(0, 1));
        Assert.True(TractorReach.Rack(draft.Boxes, draft.Mounts, [], draft.BlockSize, 0, 1, out var point, out _));
        Assert.Equal(point, aim);
        Assert.Equal(1, TractorReach.ChooseRack(Content.Cargo, draft.Boxes, draft.Mounts, [], draft.BlockSize, 0, [], [], 0, new byte[4096]));
    }
}
