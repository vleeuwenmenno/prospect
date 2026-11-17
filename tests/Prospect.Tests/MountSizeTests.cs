using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The size of a mounted part (<see cref="DesignMount.Size"/>): a cold gas array of one, two by two or three by three
/// nozzles. Its footprint and figures follow the size, the one rule of where it fits is still <see cref="FaceMounts"/>,
/// a design file and the canonical bytes keep it, an old file flies as it did, the draft places and resizes it, and
/// the model has a cup and a plume for every nozzle, with lights that follow the hull's thrust.
/// </summary>
public sealed class MountSizeTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly GridSize One = new(1, 1, 1);
    private static BlockType Cold => Content.Blocks.Get("coldGasThruster");
    private static BlockType Hot => Content.Blocks.Get("hotThruster");

    private static DesignBox Box(string type, GridSize at, GridSize? size = null) => new(at, size ?? One, Content.Blocks.Get(type));

    // A core and, apart from it, a block of plating with every face free.
    private static DesignBox[] Plate => [Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(2, 0, 0))];

    private static HullDesign Design(params DesignMount[] mounts) => TestDesigns.Mounted(Plate, mounts);

    private static BuildDraft Draft(params DesignBox[] boxes)
    {
        var design = HullDesign.Of(new BoxDesign("test", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        return BuildDraft.From(design, design.FullSet, Content);
    }

    [Fact]
    public void AColdGasArrayIsMadeInThreeSizesAndTwoByTwoIsWhatTheOldThrusterWas()
    {
        var spec = Cold.Mount!;
        Assert.Equal([1, 2, 3], spec.Sizes);
        Assert.Equal(2, spec.DefaultSize);
        Assert.True(spec.Sizable);
        Assert.Equal((2, 4, 6), (spec.SideOf(1), spec.SideOf(2), spec.SideOf(3)));

        // The figures are one nozzle's; four of them are exactly what one mount of the old part was.
        Assert.Equal((100f, 0.0075f, 0.0375f, 0.45f), (Cold.Thrust * 4, Cold.FuelBurn * 4, Cold.PowerDraw * 4, Cold.Mass * 4));

        // Every other mount has the one size, and is refused another.
        Assert.False(Hot.Mount!.Sizable);
        Assert.True(FaceMounts.SizeAllowed(Hot, 1));
        Assert.False(FaceMounts.SizeAllowed(Hot, 2));
        Assert.False(FaceMounts.SizeAllowed(Cold, 4));
        Assert.False(FaceMounts.SizeAllowed(Cold, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ThrustBurnPowerAndMassFollowTheNozzleCount(int size)
    {
        var bare = Design().Flight;
        var mount = new DesignMount(1, BlockFacing.Up, 0, 0, Cold, Size: (byte)size);
        var flight = Design(mount).Flight;
        var nozzles = size * size;
        Assert.Equal(nozzles, FaceMounts.Units(mount));
        Assert.Equal(bare.Mass + Cold.Mass * nozzles, flight.Mass, 4);
        // A nozzle on a top face pushes the hull down.
        Assert.Equal(Cold.Thrust * nozzles / flight.Mass, flight.Push(BlockFacing.Down), 4);
        Assert.Equal(Cold.FuelBurn * nozzles, flight.Burn.Sum(BlockFacing.Down), 6);
        Assert.Equal(Cold.PowerDraw * nozzles, flight.ThrustPowerByFuel.Nitrogen, 6);

        // The box it fills is its type's side times its size, as deep as ever.
        var extent = BoxDesign.MountSize(mount);
        Assert.Equal(new Vector3(0.2f * size, 0.2f, 0.2f * size), extent);
    }

    [Fact]
    public void ALargerArrayMustFitItsFaceAndClearItsNeighbours()
    {
        var blocks = Plate;
        DraftEdit Check(DesignMount mount, params DesignMount[] others) => FaceMounts.Check(blocks, others, mount);

        // Three by three is six tenths wide: it fits from the corner to four tenths in, and no further.
        Assert.Equal(DraftEdit.Done, Check(new DesignMount(1, BlockFacing.Up, 4, 4, Cold, Size: 3)));
        Assert.Equal(DraftEdit.OffFace, Check(new DesignMount(1, BlockFacing.Up, 5, 4, Cold, Size: 3)));
        Assert.Equal(DraftEdit.Done, Check(new DesignMount(1, BlockFacing.Up, 8, 8, Cold, Size: 1)));
        Assert.Equal(DraftEdit.OffFace, Check(new DesignMount(1, BlockFacing.Up, 9, 8, Cold, Size: 1)));

        // Its whole footprint counts against another mount: one that cleared a two by two is under a three by three.
        var beside = new DesignMount(1, BlockFacing.Up, 4, 0, Cold, Size: 2);
        Assert.Equal(DraftEdit.Done, Check(new DesignMount(1, BlockFacing.Up, 0, 0, Cold, Size: 2), beside));
        Assert.Equal(DraftEdit.Overlaps, Check(new DesignMount(1, BlockFacing.Up, 0, 0, Cold, Size: 3), beside));

        // A size the part is not made in is refused before anything else, by the draft's check and by a design.
        Assert.Equal(DraftEdit.NoSize, Check(new DesignMount(1, BlockFacing.Up, 0, 0, Cold, Size: 4)));
        Assert.Equal(DraftEdit.NoSize, Check(new DesignMount(1, BlockFacing.Up, 0, 0, Hot, Size: 2)));
        var refused = Assert.Throws<InvalidDataException>(() => Design(new DesignMount(1, BlockFacing.Up, 0, 0, Cold, Size: 4)));
        Assert.Contains("size 4", refused.Message);
        Assert.Throws<InvalidDataException>(() => Design(new DesignMount(1, BlockFacing.Up, 5, 5, Cold, Size: 3)));
    }

    [Fact]
    public void ADesignFileKeepsASizeAndAnOlderFileFliesAsItDid()
    {
        static BoxDesign Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return BoxDesign.Parse(document.RootElement, Content.Blocks, "test.json");
        }

        const string blocks = "\"name\": \"T\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [1, 1, 1], \"blocks\": [{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [2, 0, 0], \"type\": \"hull\" }]";
        static string File(int version, string mounts) => "{ \"version\": " + version + ", " + blocks + ", \"mounts\": [" + mounts + "] }";

        // A file from before sizes lists none: its cold gas thruster is the two by two array, which pushes, burns,
        // draws and weighs what the old single thruster did.
        var old = Parse(File(7, "{ \"on\": 1, \"face\": \"up\", \"at\": [3, 3], \"type\": \"coldGasThruster\" }, { \"on\": 1, \"face\": \"down\", \"at\": [2, 2], \"type\": \"hotThruster\" }"));
        Assert.Equal((Cold, 2), (old.Mounts[0].Type, old.Mounts[0].Size));
        Assert.Equal((Hot, 1), (old.Mounts[1].Type, old.Mounts[1].Size));
        var flight = HullDesign.Of(old, Content.Rules).Flight;
        Assert.Equal(100f / flight.Mass, flight.Push(BlockFacing.Down), 4);
        Assert.Equal(0.0075f, flight.Burn.Sum(BlockFacing.Down), 7);
        Assert.Equal(0.0375f, flight.ThrustPowerByFuel.Nitrogen, 7);

        // Only a file of version 8 may name a size, and only one the part is made in.
        Assert.Contains("version 8", Assert.Throws<InvalidDataException>(() => Parse(File(7, "{ \"on\": 1, \"face\": \"up\", \"at\": [0, 0], \"type\": \"coldGasThruster\", \"size\": 3 }"))).Message);
        Assert.Throws<InvalidDataException>(() => Parse(File(8, "{ \"on\": 1, \"face\": \"up\", \"at\": [0, 0], \"type\": \"coldGasThruster\", \"size\": 4 }")));
        Assert.Throws<InvalidDataException>(() => Parse(File(8, "{ \"on\": 1, \"face\": \"up\", \"at\": [0, 0], \"type\": \"hotThruster\", \"size\": 2 }")));
        Assert.Throws<InvalidDataException>(() => Parse(File(8, "{ \"on\": 1, \"face\": \"up\", \"at\": [0, 0], \"type\": \"coldGasThruster\", \"size\": 0 }")));

        // Written, a sized part names its size and a part with one size names none; read back it is the same design.
        var sized = Parse(File(8, "{ \"on\": 1, \"face\": \"up\", \"at\": [0, 0], \"type\": \"coldGasThruster\", \"size\": 3 }, { \"on\": 1, \"face\": \"right\", \"at\": [4, 4], \"type\": \"coldGasThruster\", \"size\": 1 }, { \"on\": 1, \"face\": \"down\", \"at\": [2, 2], \"type\": \"hotThruster\" }"));
        Assert.Equal([1, 1, 3], sized.Mounts.ToArray().Select(m => (int)m.Size).Order());
        using var stream = new MemoryStream();
        sized.Write(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\":8", text);
        Assert.Equal(2, text.Split("\"size\":").Length - 1 - text.Split("\"size\":[").Length + 1);
        var again = BoxDesign.Read(stream.ToArray(), Content.Blocks, "again.json");
        Assert.Equal(sized.Mounts.ToArray(), again.Mounts.ToArray());
    }

    [Fact]
    public void DesignBytesKeepEachMountsSizeAndRefuseOneItsPartIsNotMadeIn()
    {
        var design = Design(new DesignMount(1, BlockFacing.Up, 0, 0, Cold, Size: 3), new DesignMount(1, BlockFacing.Right, 4, 4, Cold, Size: 1), new DesignMount(1, BlockFacing.Down, 2, 2, Hot));
        var bytes = DesignBytes.Encode(design);
        var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
        Assert.Equal(design.Mounts.ToArray(), decoded.Mounts.ToArray());
        Assert.Equal(design.Flight.Mass, decoded.Flight.Mass);

        // The size is the last byte of a mount, and the hot thruster on the bottom face is the last mount.
        Assert.Equal(1, bytes[^1]);
        var forged = (byte[])bytes.Clone();
        forged[^1] = 2;
        Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(forged, SHA256.HashData(forged), Content));
    }

    [Fact]
    public void TheDraftPlacesAnArrayInTheSizeAskedAndInItsDefaultWhenNoneIs()
    {
        var draft = Draft(Plate);
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 0, 0, Cold));
        Assert.Equal(2, draft.Mounts[0].Size);
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 4, 0, Cold, 3));
        Assert.Equal(3, draft.Mounts[1].Size);
        Assert.Equal(DraftEdit.Done, draft.AddMount(1, BlockFacing.Up, 0, 4, Cold, 1));
        Assert.Equal(DraftEdit.Overlaps, draft.CanAddMount(1, BlockFacing.Up, 0, 5, Cold, 2));
        Assert.Equal(DraftEdit.NoSize, draft.CanAddMount(1, BlockFacing.Down, 0, 0, Cold, 4));
        Assert.Equal(DraftEdit.NoSize, draft.CanAddMount(1, BlockFacing.Down, 0, 0, Hot, 2));

        // The pointer's place is the middle of the square, whatever its size, and the square stays on the face.
        draft.SnapMount(1, BlockFacing.Down, Cold, new Vector3(2f, -0.5f, 0f), out var u, out var v, 3);
        Assert.Equal((2, 2), (u, v));
        draft.SnapMount(1, BlockFacing.Down, Cold, new Vector3(2.5f, -0.5f, 0.5f), out u, out v, 3);
        Assert.Equal((4, 4), (u, v));
        draft.SnapMount(1, BlockFacing.Down, Cold, new Vector3(2.5f, -0.5f, 0.5f), out u, out v, 1);
        Assert.Equal((8, 8), (u, v));

        // Exported, the sizes are in the design and its bytes.
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.Equal([1, 2, 3], export.Design!.Mounts.ToArray().Select(m => (int)m.Size).Order());
        Assert.Equal(export.Bytes, BuildDraft.FromBlueprint(export.Design, Content).Export().Bytes);
    }

    [Fact]
    public void ResizingAPlacedArrayKeepsItsMiddleIsOneStepAndIsRefusedWithoutRoom()
    {
        var draft = Draft(Plate);
        draft.AddMount(1, BlockFacing.Up, 3, 3, Cold);
        var version = draft.Version;

        // Asking changes nothing, not even the version.
        Assert.Equal(DraftEdit.Done, draft.CanSetMountSizes([0], 3));
        Assert.Equal(DraftEdit.Unchanged, draft.CanSetMountSizes([0], 2));
        Assert.Equal(DraftEdit.NoSize, draft.CanSetMountSizes([0], 4));
        Assert.Equal(version, draft.Version);
        Assert.Equal(2, draft.Mounts[0].Size);

        // Two by two at 3 covers 3 to 7: three by three about the same middle covers 2 to 8, and one by one 4 to 6.
        Assert.Equal(DraftEdit.Done, draft.SetMountSize(0, 3));
        Assert.Equal((3, 2, 2), (draft.Mounts[0].Size, draft.Mounts[0].U, draft.Mounts[0].V));
        Assert.True(draft.Undo());
        Assert.Equal((2, 3, 3), (draft.Mounts[0].Size, draft.Mounts[0].U, draft.Mounts[0].V));
        Assert.Equal(DraftEdit.Done, draft.SetMountSize(0, 1));
        Assert.Equal((1, 4, 4), (draft.Mounts[0].Size, draft.Mounts[0].U, draft.Mounts[0].V));
        Assert.True(draft.Undo());

        // Near an edge it is moved in to stay on the face.
        draft.AddMount(1, BlockFacing.Down, 6, 6, Cold);
        Assert.Equal(DraftEdit.Done, draft.SetMountSize(1, 3));
        Assert.Equal((3, 4, 4), (draft.Mounts[1].Size, draft.Mounts[1].U, draft.Mounts[1].V));
        Assert.True(draft.Undo());

        // With a neighbour in the way it is refused and nothing changes; two that would grow into each other too.
        draft.AddMount(1, BlockFacing.Up, 7, 3, Cold, 1);
        var before = draft.Mounts.ToArray();
        version = draft.Version;
        Assert.Equal(DraftEdit.Overlaps, draft.SetMountSize(0, 3));
        Assert.Equal(DraftEdit.Overlaps, draft.SetMountSizes([0, 2], 3));
        Assert.Equal(before, draft.Mounts.ToArray());
        Assert.Equal(version, draft.Version);

        // A part with one size has no other.
        draft.AddMount(1, BlockFacing.Right, 2, 2, Hot);
        Assert.Equal(DraftEdit.NoSize, draft.SetMountSize(draft.Mounts.Length - 1, 2));
        Assert.Equal(DraftEdit.NoSuchBlock, draft.SetMountSize(99, 2));
    }

    [Fact]
    public void MirroredCopiesKeepTheSizeAndAnEditTakesAwayAnArrayItLeavesNoRoom()
    {
        var draft = Draft(Box("core", new GridSize(0, 0, 0)), Box("hull", new GridSize(1, 0, 0)), Box("hull", new GridSize(-1, 0, 0)));
        Assert.Equal(DraftEdit.Done, draft.AddMountMirrored(1, BlockFacing.Up, 4, 1, Cold, MirrorAxes.X, out var skipped, 3));
        Assert.Equal(0, skipped);
        var mounts = draft.Mounts.ToArray();
        Assert.Equal(2, mounts.Length);
        // On a top face the first position runs along z and the second along x: the x copy is reflected in V.
        Assert.Contains(new DesignMount(1, BlockFacing.Up, 4, 1, Cold, Size: 3), mounts);
        Assert.Contains(new DesignMount(2, BlockFacing.Up, 4, 3, Cold, Size: 3), mounts);

        // A block put on the face covers the array, which goes in the same step and comes back with it.
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(1, 1, 0), One, Content.Blocks.Get("hull")));
        Assert.Equal(new DesignMount(2, BlockFacing.Up, 4, 3, Cold, Size: 3), draft.Mounts[0]);
        Assert.Single(draft.Mounts.ToArray());
        Assert.True(draft.Undo());
        Assert.Equal(mounts, draft.Mounts.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnArraysModelHasACupAndANozzleForEveryUnitInsideItsBox(int size)
    {
        var model = Look.Models["coldGasThruster"];
        Assert.NotNull(model.Array);
        foreach (var face in new[] { BlockFacing.Right, BlockFacing.Left, BlockFacing.Up, BlockFacing.Down, BlockFacing.Back, BlockFacing.Front })
        {
            var design = Design(new DesignMount(1, face, 2, 2, Cold, Size: (byte)size));
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            var nozzles = new List<ThrustNozzle>();
            Assert.True(PropulsionModels.AddMount(design, 0, Look, vertices, indices, nozzles));
            var axis = Facings.Axis(face);
            var half = design.MountHalfExtents[0];
            var centre = design.MountOffsets[0];
            var lo = centre - half - new Vector3(1e-4f);
            var hi = centre + half + new Vector3(1e-4f);
            foreach (var vertex in vertices)
            {
                var p = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                Assert.True(p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z, $"{size} {face}: {p}");
            }

            // One nozzle a unit, each with an entry of its own, on the outer face, a unit apart, pointing out.
            Assert.Equal(size * size, nozzles.Count);
            Assert.Equal(nozzles.Count, nozzles.Select(n => n.Exit).Distinct().Count());
            Assert.Equal(Enumerable.Range(0, nozzles.Count), nozzles.Select(n => n.Index));
            var pitch = 2f * half[(axis + 1) % 3] / size;
            foreach (var nozzle in nozzles)
            {
                Assert.Equal((face, 1, FuelKind.Nitrogen), (nozzle.Facing, nozzle.Block, nozzle.Fuel));
                Assert.Equal(centre[axis] + Facings.Sign(face) * half[axis], nozzle.Exit[axis], 4);
                Assert.True(nozzle.ExitRadius > 0 && nozzle.ExitRadius < 0.5f * pitch);
                foreach (var other in nozzles)
                    Assert.True(other.Index == nozzle.Index || Vector3.Distance(other.Exit, nozzle.Exit) >= pitch - 1e-4f);
            }

            // The cups are the same size on an array of any size: a nozzle is as wide on one as on nine.
            Assert.Equal(0.5f * 0.2f * model.ExitShare / (1f + model.Lip), nozzles[0].ExitRadius, 5);

            // The hull's mesh lists every nozzle, so every cup shows its own plume.
            Assert.Equal(size * size, MeshBuilder.Hull(design, Look).Nozzles.Length);

            // The bands that brighten with thrust carry the way the nozzle points, leaning to their own side; the
            // caps are painted and the slots give off a steady light.
            var facing = Facings.Vector(face);
            var lit = 0;
            var painted = 0;
            var steady = 0;
            foreach (var vertex in vertices)
            {
                var normal = new Vector3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z);
                var length = normal.Length();
                if (length >= 5.5f)
                {
                    lit++;
                    Assert.True(Vector3.Dot(normal / length, facing) > 0.99f, $"{size} {face}: {normal}");
                }
                else if (length >= 4.5f)
                    steady++;
                else if (PropulsionModels.PaintOf(length, out _))
                    painted++;
            }

            // Two bands a cup, each a ring of four-cornered facets; four slots; caps on four corners.
            Assert.Equal(size * size * 2 * model.Sides * 4, lit);
            Assert.Equal(4 * 4, steady);
            Assert.True(painted >= 4 * 3 * 4);
        }
    }

    [Fact]
    public void TheLightsOfAnArrayFollowWhatTheHullFires()
    {
        var effects = new ThrustEffects(Look);
        var gain = Look.Models["coldGasThruster"].Array!.GlowFiring;
        Assert.True(gain > 0);

        // Idle, or pushing on another fuel: nothing is lit.
        Assert.Equal(new Vector3(0), Xyz(effects.Glow(Content, default)));
        Assert.Equal(new Vector3(0), Xyz(effects.Glow(Content, new ThrustState { X = 127, Flags = 4 })));

        // Pushed to the right on nitrogen, the nozzles that face left fire: the instance names that way at full
        // strength, and a band whose nozzle points there is lit by the gain.
        var glow = effects.Glow(Content, new ThrustState { X = 127, Flags = 1 });
        Assert.Equal(gain, glow.W);
        Assert.Equal(1f, Vector3.Dot(Xyz(glow), Facings.Vector(BlockFacing.Left)), 3);
        Assert.Equal(0f, Vector3.Dot(Xyz(glow), Facings.Vector(BlockFacing.Up)), 3);
        Assert.True(Vector3.Dot(Xyz(glow), Facings.Vector(BlockFacing.Right)) < 0);

        // Half the throttle, half the light; two axes at once light both.
        var half = effects.Glow(Content, new ThrustState { Y = -64, Z = 127, Flags = 1 });
        Assert.Equal(64f / 127f, Vector3.Dot(Xyz(half), Facings.Vector(BlockFacing.Up)), 2);
        Assert.Equal(1f, Vector3.Dot(Xyz(half), Facings.Vector(BlockFacing.Front)), 3);

        // Asked every frame for every hull: it takes no memory.
        var state = new ThrustState { X = -127, Flags = 1 };
        var allocation = new AllocationProbe();
        while (allocation.Measuring())
            effects.Glow(Content, state);
        Assert.Equal(0, allocation.Bytes);

        static Vector3 Xyz(Brutal.Numerics.float4 v) => new(v.X, v.Y, v.Z);
    }

    [Fact]
    public void AnArrayLookOutOfRangeIsRefused()
    {
        var path = Prospect.Engine.IO.AppPaths.Content("visuals", "propulsion.json");
        var text = File.ReadAllText(path);
        Assert.Contains("\"glowFiring\"", text);
        foreach (var (from, to) in new[]
        {
            ("\"stepHeight\": 0.54", "\"stepHeight\": 0.2"),
            ("\"cupBase\": 0.84", "\"cupBase\": 1.4"),
            ("\"glowColor\": [0.3, 0.85, 1.0]", "\"glowColor\": [0.3, 0.85]"),
            ("\"floorDepth\": 0.62", "\"floorDepth\": 0.1"),
        })
        {
            Assert.Contains(from, text);
            var scratch = Path.Combine(Path.GetTempPath(), $"prospect-array-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(scratch, text.Replace(from, to));
                Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(scratch));
            }
            finally
            {
                File.Delete(scratch);
            }
        }
    }
}
