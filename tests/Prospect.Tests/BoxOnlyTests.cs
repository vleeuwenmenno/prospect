using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Parts that are never slanted (<see cref="BlockType.BoxOnly"/>): tanks, cargo racks, sensors and the sector scanner (module scanners are face mounts). One rule,
/// <see cref="BoxDesign.ShapeAllowed"/>, refuses a wedge of one in a design file, in a received design and in every
/// edit of the editor's draft that could make one.
/// </summary>
public sealed class BoxOnlyTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly GridSize One = new(1, 1, 1);

    private static readonly string[] BoxOnlyTypes =
        ["cargo", "bipropellantTank", "hydrazineTank", "nitrogenTank", "microBipropellantTank", "microNitrogenTank", "sensor", "charting"];

    private static BuildDraft Draft(params DesignBox[] boxes)
    {
        var design = HullDesign.Of(new BoxDesign("test", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        return BuildDraft.From(design, design.FullSet, Content);
    }

    private static DesignBox Box(string type, int x, int y, int z, BlockShape shape = BlockShape.Box) =>
        new(new GridSize(x, y, z), One, Content.Blocks.Get(type), shape);

    [Fact]
    public void EveryTankSensorAndScannerIsBoxOnlyAndNothingElseIs()
    {
        foreach (var type in Content.Blocks.Types)
        {
            var expected = type.Cargo > 0 || type.Tank != FuelKind.None || type.Sensor || type.Charting || type.Bay is not null || type.OreCapacity > 0 || type.Hopper;
            Assert.True(expected == type.BoxOnly, type.Id);
            Assert.True(BoxDesign.ShapeAllowed(type, BlockShape.Box), type.Id);
            Assert.Equal(!expected, BoxDesign.ShapeAllowed(type, BlockShape.WedgeZPlusPlus));
        }
        foreach (var id in BoxOnlyTypes)
            Assert.True(Content.Blocks.Get(id).BoxOnly, id);
    }

    [Fact]
    public void NoShippedDesignHasASlantedBoxOnlyPart()
    {
        foreach (var design in Content.Designs)
            foreach (var box in design.Boxes)
                Assert.True(BoxDesign.ShapeAllowed(box.Type, box.Shape), $"{design.Name}: {box.Type.Id}");
    }

    [Fact]
    public void ADesignWithASlantedTankOrSensorIsRefusedAndAWedgeOfPlatingIsNot()
    {
        foreach (var id in BoxOnlyTypes)
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                new BoxDesign("test", 1, [Box("core", 0, 0, 0), Box(id, 1, 0, 0, BlockShape.WedgeXPlusPlus)], Content.Blocks.Limits, "test"));
            Assert.Contains("never slanted", error.Message);
        }
        _ = new BoxDesign("test", 1, [Box("core", 0, 0, 0), Box("hull", 1, 0, 0, BlockShape.WedgeXPlusPlus)], Content.Blocks.Limits, "test");
    }

    [Fact]
    public void ADesignFileWithASlantedTankIsRefusedWithItsReason()
    {
        const string File = """
            { "version": 5, "name": "Test", "class": "ship", "blockSize": 1.0, "color": [0.3, 0.3, 0.3], "blocks": [
              { "at": [0, 0, 0], "type": "core" },
              { "at": [1, 0, 0], "type": "nitrogenTank", "shape": "SHAPE" } ] }
            """;
        var wedge = BlockShapes.Name(BlockShape.WedgeYPlusMinus);
        using (var ok = JsonDocument.Parse(File.Replace("SHAPE", "box")))
            Assert.Equal(BlockShape.Box, BoxDesign.Parse(ok.RootElement, Content.Blocks, "test.json").Blocks[1].Shape);
        using var bad = JsonDocument.Parse(File.Replace("SHAPE", wedge));
        var error = Assert.Throws<InvalidDataException>(() => BoxDesign.Parse(bad.RootElement, Content.Blocks, "test.json"));
        Assert.Contains("block 1", error.Message);
        Assert.Contains("never slanted", error.Message);
    }

    [Fact]
    public void AReceivedDesignWithASlantedTankIsRefused()
    {
        // The same bytes a client would send, with the tank's shape byte set to a wedge: the encoding is unchanged,
        // only what the server accepts.
        var design = HullDesign.Of(new BoxDesign("test", 1, [Box("core", 0, 0, 0), Box("hydrazineTank", 1, 0, 0)], Content.Blocks.Limits, "test"), Content.Rules);
        var bytes = DesignBytes.Encode(design);
        var copy = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
        Assert.Equal(design.Boxes.ToArray(), copy.Boxes.ToArray());

        var name = Encoding.UTF8.GetBytes("hydrazineTank");
        var at = bytes.AsSpan().IndexOf(name);
        Assert.True(at > 0);
        // After the name: three positions and three sizes of four bytes, then the shape.
        var shape = at + name.Length + 24;
        Assert.Equal((byte)BlockShape.Box, bytes[shape]);
        bytes[shape] = (byte)BlockShape.WedgeXPlusPlus;
        var error = Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content));
        Assert.Contains("never slanted", error.Message);
    }

    [Fact]
    public void TheDraftRefusesASlantedTankFromEveryEdit()
    {
        var tank = Content.Blocks.Get("bipropellantTank");
        var draft = Draft(Box("core", 0, 0, 0), Box("hull", 0, 1, 0));
        var version = draft.Version;

        // Placing, and asking first.
        Assert.Equal(DraftEdit.BoxOnly, draft.CanAdd(new GridSize(1, 0, 0), One, tank, BlockShape.WedgeXPlusPlus));
        Assert.Equal(DraftEdit.BoxOnly, draft.Add(new GridSize(1, 0, 0), One, tank, BlockShape.WedgeXPlusPlus));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(new GridSize(1, 0, 0), One, tank));

        // Pasting, replacing, mirroring.
        Assert.Equal(DraftEdit.BoxOnly, draft.CanPaste([Box("bipropellantTank", 1, 0, 0, BlockShape.WedgeZMinusMinus)], default));
        Assert.Equal(DraftEdit.BoxOnly, draft.Paste([Box("hull", 2, 0, 0), Box("sensor", 1, 0, 0, BlockShape.WedgeZMinusMinus)], default));
        Assert.Equal(DraftEdit.BoxOnly, draft.Replace([Box("core", 0, 0, 0), Box("charting", 1, 0, 0, BlockShape.WedgeXMinusPlus)], default));
        Assert.Equal(DraftEdit.BoxOnly, draft.AddMirrored(new DesignBox(new GridSize(2, 0, 0), One, tank, BlockShape.WedgeXPlusPlus), MirrorAxes.X, out var skipped));
        Assert.Equal(0, skipped);
        Span<DraftEdit> checks = stackalloc DraftEdit[1];
        draft.CheckBoxes([new DesignBox(new GridSize(1, 0, 0), One, tank, BlockShape.WedgeYPlusPlus)], checks);
        Assert.Equal(DraftEdit.BoxOnly, checks[0]);
        Assert.Equal(version, draft.Version);

        // A wedge of plating is still fine.
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(1, 0, 0), One, Content.Blocks.Get("hull"), BlockShape.WedgeXPlusPlus));
    }

    [Fact]
    public void ATankPlacedTurnedMergedOrMirroredStaysABox()
    {
        var tank = Content.Blocks.Get("nitrogenTank");
        var draft = Draft(Box("core", 0, 0, 0));
        Assert.Equal(DraftEdit.Done, draft.AddMirrored(new DesignBox(new GridSize(2, 0, 0), new GridSize(1, 1, 2), tank), MirrorAxes.X, out var skipped));
        Assert.Equal(0, skipped);
        Assert.Equal(3, draft.Boxes.Length);
        foreach (var box in draft.Boxes)
            Assert.Equal(BlockShape.Box, box.Shape);

        // A turn of a box is a box: a tank can never be turned into a slant.
        for (var axis = 0; axis < 3; axis++)
        {
            Assert.Contains(draft.Rotate(1, axis), new[] { DraftEdit.Done, DraftEdit.Unchanged });
            Assert.Equal(BlockShape.Box, draft.Boxes[1].Shape);
        }

        // Two tanks side by side merge into one box.
        var pair = Draft(Box("core", 0, 0, 0), Box("nitrogenTank", 1, 0, 0), Box("nitrogenTank", 2, 0, 0));
        Assert.Equal(DraftEdit.Done, pair.Merge([1, 2]));
        Assert.Equal(new GridSize(2, 1, 1), pair.Boxes[^1].Size);
        Assert.Equal(BlockShape.Box, pair.Boxes[^1].Shape);
        Assert.True(pair.Export().Ok);
    }
}
