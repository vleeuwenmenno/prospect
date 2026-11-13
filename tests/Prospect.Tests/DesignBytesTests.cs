using System.Buffers.Binary;
using System.Security.Cryptography;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class DesignBytesTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void CanonicalBytesPreserveFileOrderAndFigures()
    {
        foreach (var source in Content.Designs)
        {
            var bytes = DesignBytes.Encode(source);
            var copy = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
            Assert.Equal(bytes, DesignBytes.Encode(copy));
            Assert.Equal(source.Boxes.ToArray(), copy.Boxes.ToArray());
            Assert.Equal(source.Flight, copy.Flight);
            Assert.Equal(source.Stats, copy.Stats);
        }
    }

    [Fact]
    public void RejectsBadHashTruncationOversizeAndTrailingData()
    {
        var bytes = DesignBytes.Encode(Content.Pod);
        Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(bytes, new byte[32], Content));
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = bytes[..length];
            Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(truncated, SHA256.HashData(truncated), Content));
        }
        var extra = new byte[bytes.Length + 1];
        bytes.CopyTo(extra, 0);
        Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(extra, SHA256.HashData(extra), Content));
        var oversized = new byte[Content.BlockTransfers.MaxBytes + 1];
        Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(oversized, SHA256.HashData(oversized), Content));
    }

    [Fact]
    public void RejectsMalformedDataEvenWithItsCorrectHash()
    {
        var original = DesignBytes.Encode(Content.Pod);
        var metadata = 5 + original[4];
        var firstBlock = metadata + 1 + 4 * 5;
        var firstPosition = firstBlock + 1 + original[firstBlock];
        foreach (var (offset, value) in new[]
        {
            (0, 99),
            (metadata + 1, BitConverter.SingleToInt32Bits(float.NaN)),
            (metadata + 5, BitConverter.SingleToInt32Bits(float.PositiveInfinity)),
            (metadata + 17, int.MaxValue),
            (firstPosition, int.MaxValue),
            (firstPosition + 12, 2),
        })
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
            Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content));
        }
    }

    [Fact]
    public void HashChangesWithFileOrderEvenWhenGeometryDoesNot()
    {
        var source = Content.Pod;
        var boxes = source.Boxes.ToArray();
        (boxes[0], boxes[^1]) = (boxes[^1], boxes[0]);
        var reordered = HullDesign.Of(new BoxDesign(source.Name, source.BlockSize, boxes, Content.Blocks.Limits,
            "reordered", source.Class, source.Color), Content.Rules);
        Assert.NotEqual(SHA256.HashData(DesignBytes.Encode(source)), SHA256.HashData(DesignBytes.Encode(reordered)));
    }

    private static DesignBox Box(string type, int x) => new(new GridSize(x, 0, 0), new GridSize(1, 1, 1), Content.Blocks.Get(type));

    [Fact]
    public void AShipTakesDrivesOfOneKindInFilesReceivedDesignsAndDrafts()
    {
        // No shipped design mixes kinds.
        foreach (var design in Content.Designs)
            Assert.True(BoxDesign.MixedDrive(design.Boxes, out _) < 0, design.Name);

        // Several drives of one kind, normal and compact alike, pass the canonical decode; two kinds do not, before
        // the design is evaluated or its shield built.
        var same = HullDesign.Of(new BoxDesign("Same", 1, [Box("core", 0), Box("riftcutter", 1), Box("riftdrive", 2), Box("riftcutter", 3)], Content.Blocks.Limits, "test"), Content.Rules);
        var bytes = DesignBytes.Encode(same);
        Assert.Equal(FtlKind.Rift, DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content).Flight.Ftl);
        // The constructor still builds a mixed set for the arithmetic tests of drive precedence; admission refuses it.
        var mixed = HullDesign.Of(new BoxDesign("Mixed", 1, [Box("core", 0), Box("hyperdrive", 1), Box("riftdrive", 2)], Content.Blocks.Limits, "test"), Content.Rules);
        bytes = DesignBytes.Encode(mixed);
        var error = Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content));
        Assert.Contains("one kind", error.Message);
        Assert.Contains("block 2 is a rift drive and block 1 a hyperspace drive", error.Message);

        // The design files: the same check, with the file named.
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "version": 2, "name": "Mixed", "class": "ship", "blockSize": 1, "color": [1, 1, 1], "blocks": [ { "at": [0, 0, 0], "type": "core" }, { "at": [1, 0, 0], "type": "wormholedrive" }, { "at": [2, 0, 0], "type": "wormholedrive" }, { "at": [3, 0, 0], "type": "hyperdrive" } ] }""");
            error = Assert.Throws<InvalidDataException>(() => BoxDesign.Load(path, Content.Blocks));
            Assert.StartsWith(path, error.Message);
            Assert.Contains("block 3 is a hyperspace drive and block 1 a wormhole drive", error.Message);
            File.WriteAllText(path, """{ "version": 2, "name": "Twin", "class": "ship", "blockSize": 1, "color": [1, 1, 1], "blocks": [ { "at": [0, 0, 0], "type": "core" }, { "at": [1, 0, 0], "type": "wormholedrive" }, { "at": [2, 0, 0], "type": "wormholedrive" } ] }""");
            Assert.Equal(3, BoxDesign.Load(path, Content.Blocks).Blocks.Length);
        }
        finally { File.Delete(path); }

        // A draft refuses a part of another kind at once, whole for a paste, and passes the same kind.
        var draft = BuildDraft.FromBlueprint(Content.Pod, Content);
        var blocks = draft.Boxes.Length;
        var one = new GridSize(1, 1, 1);
        var at = new GridSize(20, 0, 0);
        var far = new GridSize(-20, 0, 0);
        Assert.Equal(FtlKind.Hyperspace, Content.Pod.Flight.Ftl);
        Assert.Equal(DraftEdit.MixedDrives, draft.CanAdd(at, one, Content.Blocks.Get("riftcutter")));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(at, one));
        Assert.Equal(DraftEdit.MixedDrives, draft.Add(at, one, Content.Blocks.Get("wormholedrive")));
        Assert.Equal(blocks, draft.Boxes.Length);
        Assert.Equal(DraftEdit.MixedDrives, draft.Paste([Box("hull", 0), Box("riftdrive", 1)], at));
        Assert.Equal(blocks, draft.Boxes.Length);
        Assert.Equal(DraftEdit.Done, draft.Add(at, one, Content.Blocks.Get("hyperdrive")));
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.Equal(FtlKind.Hyperspace, export.Design!.Flight.Ftl);

        // With every drive gone, any kind may come; then that kind is the ship's, a pasted group of two kinds is not.
        var drives = new List<int>();
        for (var i = 0; i < draft.Boxes.Length; i++)
            if (draft.Boxes[i].Type.Ftl != FtlKind.None) drives.Add(i);
        Assert.Equal(DraftEdit.Done, draft.Delete(drives.ToArray()));
        Assert.Equal(DraftEdit.MixedDrives, draft.CanPaste([Box("hyperdrive", 0), Box("riftdrive", 1)], far));
        Assert.Equal(DraftEdit.Done, draft.Add(far, one, Content.Blocks.Get("riftcutter")));
        Assert.Equal(DraftEdit.MixedDrives, draft.CanAdd(at, one, Content.Blocks.Get("hyperdrive")));
        // Undo brings back the old kind only once the new one is gone again.
        Assert.True(draft.Undo());
        Assert.True(draft.Undo());
        Assert.Equal(DraftEdit.Done, draft.CanAdd(new GridSize(20, 5, 0), one, Content.Blocks.Get("hyperdrive")));

        // A draft of a mixed design says why it cannot be exported, in the editor's words.
        export = BuildDraft.FromBlueprint(mixed, Content).Export();
        Assert.False(export.Ok);
        Assert.Contains("one kind", export.Problem);
    }
}
