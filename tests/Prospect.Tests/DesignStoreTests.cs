using System.Numerics;
using System.Text;
using Prospect.Client.Designs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The saved designs of the ship editor: the store's files and names, and the draft's one-step replacement.</summary>
public sealed class DesignStoreTests : IDisposable
{
    private static readonly GameContent Content = GameContent.Load();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "prospect-designs-" + Guid.NewGuid().ToString("N"));
    private readonly DesignStore _store;

    public DesignStoreTests() => _store = new DesignStore(Path.Combine(_folder, "designs"), Content);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private static BuildDraft PodDraft() => BuildDraft.FromBlueprint(Content.Pod, Content);

    private DesignSaveResult SavePod(string name, bool overwrite = false, float? blockSize = null)
    {
        var draft = PodDraft();
        return _store.Save(name, blockSize ?? draft.BlockSize, draft.Class, draft.Color, draft.Boxes.ToArray(), draft.Mounts.ToArray(), overwrite);
    }

    [Fact]
    public void ADraftRoundTripsThroughTheFileWithItsBlocksAndMounts()
    {
        var draft = PodDraft();
        Assert.NotEmpty(draft.Mounts.ToArray());
        Assert.Equal(DesignSaveStatus.Saved, SavePod("Scout Mk II").Status);

        var entries = _store.List();
        var entry = Assert.Single(entries);
        Assert.Equal("Scout Mk II.json", entry.File);
        Assert.Equal("Scout Mk II", Encoding.UTF8.GetString(entry.Name));
        Assert.Equal(draft.Boxes.Length, entry.Blocks);
        Assert.Equal(draft.BlockSize, entry.BlockSize);
        Assert.Equal(DesignFileState.Readable, entry.State);

        var read = _store.Read(entry.File);
        Assert.NotNull(read.Design);
        Assert.Equal(draft.Boxes.ToArray(), read.Design!.Blocks.ToArray());
        Assert.Equal(draft.Mounts.ToArray().OrderBy(m => (m.Parent, m.Face, m.U, m.V)), read.Design.Mounts.ToArray().OrderBy(m => (m.Parent, m.Face, m.U, m.V)));

        // The same format as the shipped files: version 5, with blocks and mounts.
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(_store.PathOf("Scout Mk II")));
        Assert.Equal(5, json.RootElement.GetProperty("version"u8).GetInt32());
        Assert.True(json.RootElement.TryGetProperty("blocks"u8, out _));
        Assert.True(json.RootElement.TryGetProperty("mounts"u8, out _));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("a/b\\c")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("name\u0000with\u0007controls")]
    [InlineData("C:evil")]
    public void NamesNeverBecomeAPathOrReachOutOfTheFolder(string name)
    {
        var stem = DesignStore.FileStem(name);
        Assert.DoesNotContain('/', stem);
        Assert.DoesNotContain('\\', stem);
        Assert.DoesNotContain("..", stem);
        Assert.DoesNotContain(':', stem);
        Assert.DoesNotContain('\0', stem);
        if (stem.Length > 0)
            Assert.StartsWith(_store.Root + Path.DirectorySeparatorChar, Path.GetFullPath(_store.PathOf(stem)));
    }

    [Fact]
    public void EmptyReservedAndLongNamesAreHandled()
    {
        Assert.Equal("", DesignStore.FileStem(""));
        Assert.Equal("", DesignStore.FileStem("   "));
        Assert.Equal("", DesignStore.FileStem("..."));
        Assert.Equal("", DesignStore.FileStem("///"));
        Assert.Equal("_CON", DesignStore.FileStem("CON"));
        Assert.Equal("_nul", DesignStore.FileStem("nul"));
        Assert.Equal("_COM1", DesignStore.FileStem("COM1"));
        Assert.Equal("Scout", DesignStore.FileStem("  Scout  "));
        Assert.Equal(DesignStore.MaxStemChars, DesignStore.FileStem(new string('a', 500)).Length);

        Assert.Equal(DesignSaveStatus.BadName, SavePod("").Status);
        Assert.Equal(DesignSaveStatus.BadName, SavePod("...").Status);
        Assert.Equal(DesignSaveStatus.BadName, SavePod(new string('x', 33)).Status);
        Assert.False(Directory.Exists(_store.Root), "a refused name makes nothing");
    }

    [Fact]
    public void ANameWithPathPiecesSavesInsideTheFolderOnly()
    {
        Assert.Equal(DesignSaveStatus.Saved, SavePod("../escape").Status);
        Assert.Empty(Directory.GetFiles(_folder));
        var file = Assert.Single(Directory.GetFiles(_store.Root));
        Assert.EndsWith(".json", file);
    }

    [Fact]
    public void OverwritingAsksFirstAndLeavesNoTemporaryFile()
    {
        Assert.Equal(DesignSaveStatus.Saved, SavePod("Scout").Status);
        var first = File.ReadAllBytes(_store.PathOf("Scout"));
        Assert.Equal(DesignSaveStatus.Exists, SavePod("Scout", blockSize: 2f).Status);
        Assert.Equal(first, File.ReadAllBytes(_store.PathOf("Scout")));
        Assert.Equal(DesignSaveStatus.Saved, SavePod("Scout", overwrite: true, blockSize: 2f).Status);
        Assert.NotEqual(first, File.ReadAllBytes(_store.PathOf("Scout")));
        Assert.Equal("Scout.json", Path.GetFileName(Assert.Single(Directory.GetFiles(_store.Root))));
    }

    [Fact]
    public void ADraftThatCannotBeADesignIsNotWritten()
    {
        var draft = PodDraft();
        var coreless = draft.Boxes.ToArray().Where(b => !b.Type.Core).ToArray();
        var result = _store.Save("No core", draft.BlockSize, draft.Class, draft.Color, coreless, [], false);
        Assert.Equal(DesignSaveStatus.Invalid, result.Status);
        Assert.False(File.Exists(_store.PathOf("No core")));
    }

    [Fact]
    public void BadFilesAreListedAsUnreadableAndNeverThrow()
    {
        Assert.Equal(DesignSaveStatus.Saved, SavePod("Good").Status);
        var overs = new byte[DesignStore.MaxFileBytes + 1];
        Array.Fill(overs, (byte)' ');
        File.WriteAllBytes(Path.Combine(_store.Root, "big.json"), overs);
        File.WriteAllText(Path.Combine(_store.Root, "garbage.json"), "this is not json {{{");
        File.WriteAllText(Path.Combine(_store.Root, "array.json"), "[1,2,3]");
        File.WriteAllText(Path.Combine(_store.Root, "empty.json"), "");
        File.WriteAllText(Path.Combine(_store.Root, "future.json"), "{\"version\":99,\"name\":\"x\",\"class\":\"ship\",\"blockSize\":1,\"color\":[1,1,1],\"blocks\":[]}");
        File.WriteAllText(Path.Combine(_store.Root, "types.json"), "{\"version\":5,\"name\":\"x\",\"class\":\"ship\",\"blockSize\":1,\"color\":[1,1,1],\"blocks\":[{\"type\":\"nope\",\"at\":[0,0,0]}]}");
        File.WriteAllText(Path.Combine(_store.Root, "deep.json"), new string('[', 5000));
        File.WriteAllText(Path.Combine(_store.Root, "wrongkinds.json"), "{\"version\":5,\"name\":7,\"class\":[],\"blockSize\":\"big\",\"color\":1,\"blocks\":{}}");
        File.WriteAllBytes(Path.Combine(_store.Root, "binary.json"), [0xFF, 0xFE, 0x00, 0x80, 0x12]);
        File.WriteAllText(Path.Combine(_store.Root, "notes.txt"), "ignored");

        var entries = _store.List();
        Assert.Equal(10, entries.Length);
        Assert.Equal(["Good.json"], entries.Where(e => e.State == DesignFileState.Readable).Select(e => e.File).ToArray());
        foreach (var entry in entries.Where(e => e.State == DesignFileState.Unreadable))
        {
            Assert.NotEmpty(entry.Name);
            var read = _store.Read(entry.File);
            Assert.Null(read.Design);
            Assert.NotEmpty(read.Message);
        }
    }

    [Fact]
    public void AFileThatIsNotAListedNameIsNeverRead()
    {
        Assert.Equal(DesignSaveStatus.Saved, SavePod("Good").Status);
        Assert.Null(_store.Read("../designs/Good.json").Design);
        Assert.Null(_store.Read(Path.Combine(_store.Root, "Good.json")).Design);
        Assert.Null(_store.Read("Good.txt").Design);
        Assert.Null(_store.Read("").Design);
        Assert.NotNull(_store.Read("Good.json").Design);
    }

    [Fact]
    public void ALinkIsNotFollowed()
    {
        Assert.Equal(DesignSaveStatus.Saved, SavePod("Real").Status);
        var link = Path.Combine(_store.Root, "link.json");
        try { File.CreateSymbolicLink(link, _store.PathOf("Real")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        Assert.Equal(DesignFileState.Unreadable, _store.List().Single(e => e.File == "link.json").State);
        Assert.Null(_store.Read("link.json").Design);
    }

    [Fact]
    public void ADesignOfAnotherBlockSizeIsListedWithItsSize()
    {
        Assert.Equal(DesignSaveStatus.Saved, SavePod("Big blocks", blockSize: 2f).Status);
        var entry = Assert.Single(_store.List());
        Assert.Equal(2f, entry.BlockSize);
        Assert.NotEqual(PodDraft().BlockSize, entry.BlockSize);
    }

    [Fact]
    public void AMissingFolderListsNothing() => Assert.Empty(_store.List());

    [Fact]
    public void AListIsSortedAndCapped()
    {
        Directory.CreateDirectory(_store.Root);
        for (var i = 0; i < DesignStore.MaxListed + 5; i++)
            File.WriteAllText(Path.Combine(_store.Root, $"f{i:000}.json"), "x");
        var entries = _store.List();
        Assert.Equal(DesignStore.MaxListed, entries.Length);
        Assert.Equal(entries.Select(e => e.File).OrderBy(f => f, StringComparer.OrdinalIgnoreCase), entries.Select(e => e.File));
    }

    [Fact]
    public void ReplacingTheDraftIsOneStepAndUndoBringsTheOldOneBack()
    {
        var draft = PodDraft();
        var boxes = draft.Boxes.ToArray();
        var mounts = draft.Mounts.ToArray();
        var hull = Content.Blocks.Get("hull");
        Assert.Equal(DraftEdit.Done, draft.Add(draft.Snap(0, 0, new GridSize(1, 1, 1)), new GridSize(1, 1, 1), hull));
        var larger = draft.Boxes.ToArray();
        Assert.True(draft.Undo());

        // Replace by the larger set, then by the first again: each one step, each undone and redone whole.
        var version = draft.Version;
        Assert.Equal(DraftEdit.Done, draft.Replace(larger, []));
        Assert.True(draft.Version > version);
        Assert.Equal(larger, draft.Boxes.ToArray());
        Assert.Empty(draft.Mounts.ToArray());
        Assert.True(draft.Undo());
        Assert.Equal(boxes, draft.Boxes.ToArray());
        Assert.Equal(mounts, draft.Mounts.ToArray());
        Assert.True(draft.Redo());
        Assert.Equal(larger, draft.Boxes.ToArray());
        Assert.Empty(draft.Mounts.ToArray());
        Assert.True(draft.Undo());
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void AReplacementThatDoesNotFitLeavesTheDraftAndItsHistoryAsTheyWere()
    {
        var draft = PodDraft();
        var boxes = draft.Boxes.ToArray();
        var mounts = draft.Mounts.ToArray();
        var version = draft.Version;
        var core = boxes[0];
        Assert.Equal(DraftEdit.Overlaps, draft.Replace([core, core], []));
        Assert.Equal(DraftEdit.Unchanged, draft.Replace([], []));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.Replace(boxes, [mounts[0] with { Parent = boxes.Length }]));
        Assert.Equal(boxes, draft.Boxes.ToArray());
        Assert.Equal(mounts, draft.Mounts.ToArray());
        Assert.Equal(version, draft.Version);
        Assert.False(draft.CanUndo);
    }
}
