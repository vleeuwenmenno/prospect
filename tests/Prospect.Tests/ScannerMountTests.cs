using System.Numerics;
using System.Text.Json;
using Prospect.Client.Render;
using Prospect.Game.Persistence;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The module scanners (gas scanner, asteroid scanner, asteroid locator) are face mounts, like the thrusters and the
/// tractor beam: a part on a block's face with no hit points of its own, equipment exactly while its block is there.
/// Old design files that name one as a block are converted on load; old saved worlds are refused by their layout.
/// </summary>
public sealed class ScannerMountTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly GridSize One = new(1, 1, 1);
    private static readonly string[] Scanners = ["gasScanner", "asteroidScanner", "asteroidLocator"];
    private static readonly BlockFacing[] Directions = [BlockFacing.Right, BlockFacing.Left, BlockFacing.Up, BlockFacing.Down, BlockFacing.Back, BlockFacing.Front];

    private const string Head = "\"name\": \"T\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [1, 1, 1], \"blocks\": [{ \"at\": [0, 0, 0], \"type\": \"core\" }, ";

    private static BoxDesign Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return BoxDesign.Parse(document.RootElement, Content.Blocks, "test.json");
    }

    private static HullDesign Design(DesignBox[] boxes, DesignMount[] mounts) => TestDesigns.Mounted(boxes, mounts);

    /// <summary>The shipped block catalogue with one edit made to its JSON, read the way the game reads it.</summary>
    private static BlockCatalog Catalogue(Action<System.Text.Json.Nodes.JsonNode> edit)
    {
        using var copy = new ContentCopy();
        var root = System.Text.Json.Nodes.JsonNode.Parse(copy.Read("blocks/blocks.json"))!;
        edit(root);
        return BlockCatalog.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()), "test");
    }

    [Fact]
    public void EveryScannerIsAMountWithItsModuleFiguresAndNoBlockRules()
    {
        foreach (var id in Scanners)
        {
            var type = Content.Blocks.Get(id);
            Assert.NotNull(type.Mount);
            Assert.NotEqual(ModuleFamily.None, type.Module.Family);
            Assert.Equal(0f, type.Hull);
            Assert.False(type.BoxOnly);
            Assert.Empty(type.SizeRules);
            Assert.Equal(0, type.MaxSide);
            Assert.InRange(type.Mount!.Side, 4, 6);
            Assert.Equal("hull", type.Mount.LegacyBlock);
            Assert.Equal(5, type.Mount.LastBlockVersion);
            Assert.True(type.Mass > 0 && type.PowerDraw > 0);

            // The part list offers it as a face mount, in its own Sensors category.
            Assert.Equal(Prospect.Client.World.PartRole.Module, Prospect.Client.World.EditorGeometry.Role(type));
        }

        // The sector scanner and the passive sensor stay blocks.
        Assert.Null(Content.Blocks.Get("charting").Mount);
        Assert.Null(Content.Blocks.Get("sensor").Mount);
    }

    [Fact]
    public void AScannerCannotBeABlockOfADesignOrACatalogueOfMountsThatBreakTheRules()
    {
        foreach (var id in Scanners)
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                new BoxDesign("T", 1, [new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")), new DesignBox(new GridSize(1, 0, 0), One, Content.Blocks.Get(id))], Content.Blocks.Limits, "test"));
            Assert.Contains("mounts on a face and cannot be a block", error.Message);
        }

        // A mounted scanner with a size rule, boxOnly or thrust is refused when the content is read.
        foreach (var (key, value) in new[] { ("boxOnly", (object)true), ("thrust", 5), ("crew", 1) })
            Assert.Throws<InvalidDataException>(() => Catalogue(root => root["blocks"]!["gasScanner"]![key] = System.Text.Json.Nodes.JsonValue.Create(value)));
        Assert.Throws<InvalidDataException>(() => Catalogue(root => root["blocks"]!["gasScanner"]!["mount"]!["blockThrough"] = BoxDesign.FormatVersion));
    }

    [Fact]
    public void ADesignFileOfTheCurrentVersionMayNotNameAScannerAsABlock()
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 6, " + Head + "{ \"at\": [1, 0, 0], \"type\": \"gasScanner\" }] }"));
        Assert.Contains("mounts on a face; list it under \"mounts\"", error.Message);
    }

    [Theory]
    [InlineData("", "gasScanner")]
    [InlineData("\"version\": 2, ", "asteroidScanner")]
    [InlineData("\"version\": 3, ", "asteroidLocator")]
    [InlineData("\"version\": 4, ", "gasScanner")]
    [InlineData("\"version\": 5, ", "asteroidLocator")]
    public void AnOldFilesScannerBlockBecomesPlatingCarryingOneScannerInTheMiddleOfItsTopFace(string version, string id)
    {
        var type = Content.Blocks.Get(id);
        var old = Parse("{ " + version + Head + "{ \"at\": [1, 0, 0], \"type\": \"" + id + "\" }] }");
        Assert.Equal(new DesignBox(new GridSize(1, 0, 0), One, Content.Blocks.Get("hull")), old.Blocks[1]);

        // The part is five tenths of a cell wide: its low corner is two tenths in, which leaves it centred to within a tenth.
        var side = type.Mount!.Side;
        Assert.Equal([new DesignMount(1, BlockFacing.Up, (10 - side) / 2, (10 - side) / 2, type)], old.Mounts.ToArray());

        // It is equipment of its family, as the block was.
        var sums = new BoxHullSums(old);
        sums.Evaluate(sums.Intact, Content.Rules, out _, out var stats);
        var equipment = stats.Module(type.Module.Family);
        Assert.Equal(1, equipment.Mount);
        Assert.Equal(type.Module.Range, equipment.Range);
        Assert.Equal(type.Module.PowerDraw, equipment.PowerDraw);

        // Written out, it is a current file that loads to the same design with nothing left to convert.
        using var stream = new MemoryStream();
        old.Write(stream);
        var again = BoxDesign.Read(stream.ToArray(), Content.Blocks, "again.json");
        Assert.Equal(old.Blocks.ToArray(), again.Blocks.ToArray());
        Assert.Equal(old.Mounts.ToArray(), again.Mounts.ToArray());
        Assert.Contains("\"version\":7", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void ASizedOldScannerStillMakesOnePartAndAsFarAsTheFaceGoesItIsCentred()
    {
        var wide = Parse("{ \"version\": 2, " + Head + "{ \"at\": [1, 0, 0], \"type\": \"asteroidScanner\", \"size\": [3, 2, 4] }] }");
        var mount = Assert.Single(wide.Mounts.ToArray());
        var side = Content.Blocks.Get("asteroidScanner").Mount!.Side;

        // The top face of a 3 by 2 by 4 block measures 4 by 3 cells across its two axes (Z then X).
        Assert.Equal((1, BlockFacing.Up, (40 - side) / 2, (30 - side) / 2), (mount.Parent, mount.Face, mount.U, mount.V));
    }

    [Fact]
    public void AnOldScannerWhoseTopIsCoveredTakesTheNextFaceWithRoomAndOneWithNoRoomIsRefusedNotDropped()
    {
        const string scanner = "{ \"at\": [1, 0, 0], \"type\": \"gasScanner\" }";
        var topCovered = Parse("{ \"version\": 5, " + Head + scanner + ", { \"at\": [1, 1, 0], \"type\": \"hull\" }] }");
        Assert.Equal(BlockFacing.Right, Assert.Single(topCovered.Mounts.ToArray()).Face);

        // Two scanners side by side each take a face of their own: neither mount may share volume with the other.
        var pair = Parse("{ \"version\": 5, " + Head + scanner + ", { \"at\": [1, 1, 0], \"type\": \"hull\" }, { \"at\": [3, 0, 0], \"type\": \"asteroidScanner\" }] }");
        Assert.Equal(2, pair.Mounts.Length);

        // Core to the left, plating on the other five sides: nowhere to put it.
        var boxed = "{ \"version\": 5, " + Head + scanner
            + ", { \"at\": [1, 1, 0], \"type\": \"hull\" }, { \"at\": [2, 0, 0], \"type\": \"hull\" }, { \"at\": [1, -1, 0], \"type\": \"hull\" }, { \"at\": [1, 0, 1], \"type\": \"hull\" }, { \"at\": [1, 0, -1], \"type\": \"hull\" }] }";
        var refused = Assert.Throws<InvalidDataException>(() => Parse(boxed));
        Assert.Contains("block 1", refused.Message);
        Assert.Contains("Gas scanner cannot be converted to a mounted part: every face of the block is covered", refused.Message);

        // The content's count of mounted parts is respected too.
        var capped = Catalogue(root => root["limits"]!["maxMounts"] = 0);
        using var document = JsonDocument.Parse("{ \"version\": 5, " + Head + scanner + "] }");
        var many = Assert.Throws<InvalidDataException>(() => BoxDesign.Parse(document.RootElement, capped, "test.json"));
        Assert.Contains("too many mounted parts", many.Message);
    }

    [Fact]
    public void ASavedDesignFileWithAScannerBlockLoadsConvertedThroughTheDraft()
    {
        // What a player's saved design is read with, a version 5 file written before scanners mounted.
        const string text = "{ \"version\": 5, \"name\": \"Old scout\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [1, 1, 1], \"blocks\": ["
            + "{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [1, 0, 0], \"type\": \"gasScanner\" }, { \"at\": [0, 0, 1], \"type\": \"asteroidLocator\" }], \"mounts\": [] }";
        var design = BoxDesign.Read(System.Text.Encoding.UTF8.GetBytes(text), Content.Blocks, "saved.json");
        Assert.Equal(2, design.Mounts.Length);
        Assert.All(design.Blocks.ToArray().Skip(1), box => Assert.Equal("hull", box.Type.Id));
        var draft = BuildDraft.From(HullDesign.Of(design, Content.Rules), HullDesign.Of(design, Content.Rules).FullSet, Content);
        Assert.Equal(2, draft.Mounts.Length);
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        var sums = new BoxHullSums(design);
        sums.Evaluate(sums.Intact, Content.Rules, out _, out var stats);
        Assert.NotEqual(0, stats.GasScanner.Mount);
        Assert.NotEqual(0, stats.AsteroidLocator.Mount);
    }

    [Fact]
    public void AMountedScannerIsEquipmentExactlyWhileItsBlockIsAndTheLowestSurvivingOneIsChosen()
    {
        var gas = Content.Blocks.Get("gasScanner");
        var boxes = new DesignBox[] { new(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")), new(new GridSize(1, 0, 0), One, Content.Blocks.Get("hull")), new(new GridSize(3, 0, 0), One, Content.Blocks.Get("hull")) };
        var sums = new BoxHullSums(new BoxDesign("T", 1, boxes, Content.Blocks.Limits, "test", mounts: [new DesignMount(1, BlockFacing.Up, 2, 2, gas), new DesignMount(2, BlockFacing.Up, 2, 2, gas)]));
        var live = new ulong[BlockSet.WordCount(boxes.Length)];
        BlockSet.Fill(live, boxes.Length);
        var running = sums.Intact.ToArray();
        sums.Evaluate(running, Content.Rules, out var flight, out var stats);
        Assert.Equal(1, stats.GasScanner.Mount);
        var whole = flight.IdlePower;
        var mass = stats.Mass;

        // Losing the block that carries the first takes it, its mass and its standby draw: the second is chosen.
        BlockSet.Remove(live, 1);
        sums.Lose(running, live, 1);
        sums.Evaluate(running, Content.Rules, out flight, out stats);
        Assert.Equal(2, stats.GasScanner.Mount);
        Assert.Equal(whole - gas.PowerDraw, flight.IdlePower, 4);
        Assert.Equal(mass - Content.Blocks.Get("hull").Mass - gas.Mass, stats.Mass, 4);

        BlockSet.Remove(live, 2);
        sums.Lose(running, live, 2);
        sums.Evaluate(running, Content.Rules, out _, out stats);
        Assert.Equal(default, stats.GasScanner);
        Assert.Equal(default, stats.AsteroidScanner);
    }

    [Fact]
    public void TheDraftMountsAScannerOnAFaceOnlyOnceAndNeverAsABlock()
    {
        var draft = BuildDraft.FromBlueprint(Content.Pod, Content);
        var gas = Content.Blocks.Get("gasScanner");
        Assert.Equal(DraftEdit.WrongKind, draft.Add(new GridSize(5, 5, 5), One, gas));

        var parent = -1;
        var face = BlockFacing.Up;
        for (var i = 0; i < draft.Boxes.Length && parent < 0; i++)
            foreach (var candidate in Directions)
                if (draft.CanAddMount(i, candidate, 2, 2, gas) == DraftEdit.Done)
                {
                    parent = i;
                    face = candidate;
                    break;
                }
        Assert.True(parent >= 0, "the pod has a face with room for a scanner");
        var count = draft.Mounts.Length;
        Assert.Equal(DraftEdit.Done, draft.AddMount(parent, face, 2, 2, gas));
        Assert.Equal(count + 1, draft.Mounts.Length);
        Assert.Equal(DraftEdit.Overlaps, draft.AddMount(parent, face, 3, 3, Content.Blocks.Get("asteroidScanner")));
        Assert.True(draft.Undo());
        Assert.Equal(count, draft.Mounts.Length);

        // The part palette follows the craft: a scanner is no drone part.
        var drone = BuildDraft.FromBlueprint(Content.Designs[Content.DroneDesign], Content);
        Assert.Equal(DraftEdit.WrongCraft, drone.CanAddMount(0, BlockFacing.Up, 2, 2, gas));
    }

    [Fact]
    public void EveryScannerHasAModelThatStaysInsideItsSmallBoxAndFacesOutOfItsBlock()
    {
        foreach (var id in Scanners)
        {
            var type = Content.Blocks.Get(id);
            Assert.NotNull(PropulsionModels.ScannerOf(type, Look));
            foreach (var face in Directions)
            {
                var design = Design(
                    [new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")), new DesignBox(new GridSize(2, 0, 0), One, Content.Blocks.Get("hull"))],
                    [new DesignMount(1, face, 2, 2, type)]);
                var vertices = new List<MeshVertex>();
                var indices = new List<uint>();
                var nozzles = new List<ThrustNozzle>();
                Assert.True(PropulsionModels.AddMount(design, 0, Look, vertices, indices, nozzles));
                Assert.NotEmpty(indices);
                Assert.Empty(nozzles);

                // All of it inside the box the mount fills, which stands out 0.2 of a block from the face.
                var half = design.MountHalfExtents[0];
                var axis = Facings.Axis(face);
                Assert.Equal(0.1f, half[axis], 5);
                var lo = design.MountOffsets[0] - half - new Vector3(1e-4f);
                var hi = design.MountOffsets[0] + half + new Vector3(1e-4f);
                foreach (var v in vertices)
                {
                    var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                    Assert.True(p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z, $"{id} {face}: {p}");
                }

                // Every triangle winds toward its normal, whichever way the face turns the frame.
                for (var i = 0; i < indices.Count; i += 3)
                {
                    var a = vertices[(int)indices[i]];
                    var b = vertices[(int)indices[i + 1]];
                    var c = vertices[(int)indices[i + 2]];
                    var pa = new Vector3(a.Position.X, a.Position.Y, a.Position.Z);
                    var cross = Vector3.Cross(new Vector3(b.Position.X, b.Position.Y, b.Position.Z) - pa, new Vector3(c.Position.X, c.Position.Y, c.Position.Z) - pa);
                    Assert.True(Vector3.Dot(cross, new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z)) > 0, $"{id} {face} triangle {i / 3}");
                }

                // The hull's mesh has it, and the hull that lost the block has lost it too.
                var mesh = MeshBuilder.Hull(design, Look);
                Assert.True(mesh.Vertices.Count > 0 && mesh.Nozzles.Length == 0);
                var alive = design.FullSet.ToArray();
                var whole = DamageMesh.Build(design, alive, default, 40000, 0.2f)!;
                BlockSet.Remove(alive, 1);
                var cored = DamageMesh.Build(design, alive, default, 40000, 0.2f)!;
                Assert.True(cored.Original.Length < whole.Original.Length, $"{id} {face}");

                // The ghost and the far hulls drawn block by block build the same model by its box alone.
                var ghost = new List<MeshVertex>();
                var ghostIndices = new List<uint>();
                PropulsionModels.BuildPart(type, new PartBox(Vector3.Zero, half, face, 1f), Look, ghost, ghostIndices);
                Assert.Equal(indices.Count, ghostIndices.Count);
                Assert.True(PropulsionModels.HasModel(type, BlockShape.Box, Look));
            }
        }
    }

    [Fact]
    public void ATiersColourPaintsTheRimOfItsScannerAndTheLookIsRefusedWhenOutOfRange()
    {
        var type = Content.Blocks.Get("gasScanner");
        var design = Design(
            [new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")), new DesignBox(new GridSize(2, 0, 0), One, Content.Blocks.Get("hull"))],
            [new DesignMount(1, BlockFacing.Up, 2, 2, type)]);
        var vertices = new List<MeshVertex>();
        PropulsionModels.AddMount(design, 0, Look, vertices, [], null);
        Assert.Contains(vertices, v => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length() >= 3f);

        var bad = new ScannerLook { Shape = "mast" };
        Assert.Throws<InvalidDataException>(() => bad.Check("test", "gasScanner"));
        Assert.Throws<InvalidDataException>(() => new ScannerLook { BaseHeight = 1.2f }.Check("test", "gasScanner"));
        Assert.Throws<InvalidDataException>(() => new ScannerLook { Sides = 3 }.Check("test", "gasScanner"));
    }

    [Fact]
    public void ASavedWorldOfTheOldLayoutIsRefusedWithItsLayoutNamedAndNothingIsConverted()
    {
        var world = WorldSnapshot.NewWorld(Content, Guid.NewGuid(), "Old", DateTimeOffset.UnixEpoch, 1, Content.SettingCatalog.Defaults, default);
        Assert.Equal(20, WorldSnapshotCodec.Version);
        Assert.True(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(world), SaveLimits.Default, out _, out _));
        world.Compatibility = world.Compatibility with { SaveVersion = 19 };
        Assert.False(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(world), SaveLimits.Default, out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.Equal("save layout 19, this build reads 20", error);
    }
}
