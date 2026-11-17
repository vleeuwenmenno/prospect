using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// What a mining laser and the ore it frees look like: the laser's model fits its thin box, points out of its face and
/// has its lens; its look is refused when out of range; a beam's shader instance is built without memory; loose ore
/// chunks are carried between the server's sets and drawn from one closed mesh.
/// </summary>
public sealed class LaserVisualTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly GridSize One = new(1, 1, 1);
    private static readonly BlockFacing[] Directions = [BlockFacing.Right, BlockFacing.Left, BlockFacing.Up, BlockFacing.Down, BlockFacing.Back, BlockFacing.Front];

    private static HullDesign Mounted(BlockFacing face) =>
        TestDesigns.Mounted(
            [new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")), new DesignBox(new GridSize(2, 0, 0), One, Content.Blocks.Get("hull"))],
            [new DesignMount(1, face, 3, 3, Content.Blocks.Get("miningLaser"))]);

    private static Vector3 Position(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    [Fact]
    public void TheShippedLaserLookLoadsAndHasAModelThatEveryPathAsksFor()
    {
        Assert.NotNull(Look.Laser);
        var type = Content.Blocks.Get("miningLaser");
        Assert.True(PropulsionModels.HasModel(type, BlockShape.Box, Look));
        Assert.False(PropulsionModels.HasModel(type, BlockShape.WedgeXPlusPlus, Look));
        Assert.Same(Look.Laser, PropulsionModels.LaserOf(type, Look));
        // A part that is no laser is none; the model is built for the editor's ghost and far hulls too.
        Assert.Null(PropulsionModels.LaserOf(Content.Blocks.Get("hull"), Look));
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        PropulsionModels.BuildPart(type, new PartBox(Vector3.Zero, new Vector3(0.3f, 0.1f, 0.3f), BlockFacing.Up, 1f), Look, vertices, indices);
        Assert.NotEmpty(indices);
    }

    [Fact]
    public void AMountedLasersModelStaysInsideItsBoxPointsOutOfItsFaceAndHasItsLens()
    {
        foreach (var face in Directions)
        {
            var design = Mounted(face);
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            Assert.True(PropulsionModels.AddMount(design, 0, Look, vertices, indices, null));
            Assert.NotEmpty(indices);
            var axis = Facings.Axis(face);
            var sign = Facings.Sign(face);
            var half = design.MountHalfExtents[0];
            var lo = design.MountOffsets[0] - half - new Vector3(1e-4f);
            var hi = design.MountOffsets[0] + half + new Vector3(1e-4f);
            var outer = design.MountOffsets[0][axis] + sign * half[axis];
            var back = design.MountOffsets[0][axis] - sign * half[axis];
            foreach (var v in vertices)
            {
                var p = Position(v);
                Assert.True(p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z, $"{face}: {p}");
            }

            // Every triangle is wound to face the way its normal says.
            for (var i = 0; i < indices.Count; i += 3)
            {
                var a = vertices[(int)indices[i]];
                var cross = Vector3.Cross(Position(vertices[(int)indices[i + 1]]) - Position(a), Position(vertices[(int)indices[i + 2]]) - Position(a));
                Assert.True(Vector3.Dot(cross, new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z)) > 0, $"{face} triangle {i / 3}");
            }

            // The lens is the one part that gives off light (a normal of 4.5 and over), a disc facing out of the face, set
            // back from the outer face by the look's share of the depth and in the middle of the box.
            var lens = vertices.Where(v => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length() >= 4.5f).ToArray();
            Assert.NotEmpty(lens);
            var depth = outer - back;
            foreach (var v in lens)
            {
                var n = Vector3.Normalize(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z));
                Assert.Equal(sign, n[axis], 4);
                Assert.Equal(back + sign * Look.Laser!.LensDepth * MathF.Abs(depth), Position(v)[axis], 4);
            }

            var centre = lens.Select(Position).Aggregate(Vector3.Zero, (sum, p) => sum + p) / lens.Length;
            for (var k = 0; k < 3; k++)
                if (k != axis)
                    Assert.Equal(design.MountOffsets[0][k], centre[k], 3);
            // Nothing stands out past the face the beam leaves from.
            Assert.True(vertices.All(v => (Position(v)[axis] - outer) * sign <= 1e-4f));
            Assert.Contains(vertices, v => MathF.Abs((Position(v)[axis] - outer) * sign) < 1e-4f);
        }
    }

    [Fact]
    public void ALaserLookOutOfRangeIsRefused()
    {
        Look.Laser!.Check("test");
        new LaserLook().Check("test");
        Assert.Throws<InvalidDataException>(() => new LaserLook { Sides = 4 }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { Coils = 0 }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { LensDepth = 0.5f, MuzzleStart = 0.6f }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { LensRadius = 0.9f, MuzzleRadius = 0.8f }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { LensColor = 0x1000000 }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { Beam = new LaserBeamLook { Sparks = LaserBeamLook.MaxSparks + 1 } }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { Beam = new LaserBeamLook { CoreWidth = 0.5f, GlowWidth = 0.1f } }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { Beam = new LaserBeamLook { FadeStart = 1f } }.Check("test"));
        Assert.Throws<InvalidDataException>(() => new LaserLook { Beam = new LaserBeamLook { SparkSeconds = 0f } }.Check("test"));
    }

    [Fact]
    public void ABeamInstanceCarriesItsEndsItsStateAndTheLooksFiguresAndTakesNoMemory()
    {
        var look = Look.Laser!.Beam;
        var from = new float3(1, 2, 3);
        var to = new float3(4, 5, -60);
        var cutting = look.Instance(from, to, true, 0.25f);
        var free = look.Instance(from, to, false, 0.25f);
        Assert.Equal((1f, 2f, 3f), (cutting.FromCore.X, cutting.FromCore.Y, cutting.FromCore.Z));
        Assert.Equal(look.CoreWidth, cutting.FromCore.W);
        Assert.Equal((4f, 5f, -60f), (cutting.ToState.X, cutting.ToState.Y, cutting.ToState.Z));
        Assert.Equal(1f, cutting.ToState.W);
        Assert.Equal(0f, free.ToState.W);
        Assert.Equal(look.GlowWidth, cutting.GlowColor.W);
        Assert.Equal(look.Sparks, cutting.HitColor.W);
        // Linear light: the red of a warm glow is far above its blue.
        Assert.True(cutting.GlowColor.X > 0.5f && cutting.GlowColor.Z < 0.1f);

        var probe = new AllocationProbe();
        LaserInstance last = default;
        while (probe.Measuring())
            last = look.Instance(from, to, true, 0.5f);
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(1f, last.ToState.W);
    }

    private static OreChunk Chunk(uint id, double x, Vector3 velocity) => new()
    {
        Id = id,
        Position = new Double3(x, 0, 0),
        Velocity = velocity,
        Orientation = Quaternion.Identity,
        AngularVelocity = new Vector3(0, 0, 0),
        Resource = 0,
        Grade = 0.5f,
        Mass = 100,
    };

    [Fact]
    public void ChunksAreInterpolatedBetweenSetsFlownOnBoundedAndMatchedById()
    {
        var view = new OreChunkVisuals();
        var items = new[] { Chunk(3, 1e12, new Vector3(60, 0, 0)), Chunk(5, 2e12, Vector3.Zero) };
        Assert.True(view.Update(items, 6));
        items = [Chunk(5, 2e12 + 6, Vector3.Zero), Chunk(3, 1e12 + 6, new Vector3(60, 0, 0)), Chunk(8, 5, Vector3.Zero)];
        Assert.True(view.Update(items, 12));
        Assert.False(view.Update(items, 12));
        Assert.False(view.Update(items, 7));
        Assert.Equal(3, view.Items.Length);
        // Chunk 5 is first in the new set: its place in the old one is found by id, not by index.
        Assert.Equal(2e12 + 3, view.Pose(0, 9).Position.X, 6);
        Assert.Equal(1e12 + 3, view.Pose(1, 9).Position.X, 6);
        Assert.Equal(1e12, view.Pose(1, 0).Position.X, 6);
        // A chunk that is new has nothing to blend from and is shown where it is.
        Assert.Equal(5, view.Pose(2, 9).Position.X, 6);
        // Past the newest set a chunk flies on at its velocity (a metre a tick), no further than MaxAheadTicks.
        Assert.Equal(1e12 + 6 + 3, view.Pose(1, 15).Position.X, 6);
        Assert.Equal(1e12 + 6 + OreChunkVisuals.MaxAheadTicks, view.Pose(1, 1000).Position.X, 6);
        view.Clear();
        Assert.True(view.Items.IsEmpty);
        Assert.True(view.Update([], 1));
    }

    [Fact]
    public void ASpinningChunkTurnsBetweenSetsAndPastTheNewest()
    {
        var view = new OreChunkVisuals();
        var spin = new OreChunk { Id = 1, Orientation = Quaternion.Identity, AngularVelocity = new Vector3(0, 0, MathF.PI) };
        view.Update([spin], 6);
        var next = spin;
        next.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.1f);
        view.Update([next], 12);
        var between = view.Pose(0, 9).Orientation;
        Assert.Equal(MathF.PI * 0.05f, 2f * MathF.Atan2(between.Z, between.W), 3);
        // Pi radians a second for half a second (30 ticks) is a quarter turn.
        var ahead = view.Pose(0, 12 + 30).Orientation;
        var expected = Quaternion.Normalize(next.Orientation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f));
        Assert.True(MathF.Abs(Quaternion.Dot(ahead, expected)) > 0.9999f);
    }

    [Fact]
    public void TheDrawnInstantIsTheOwnHullsAndTheMaterialCarriesTheGrade()
    {
        var hulls = new[] { new HullView { Id = 7, IsOwn = true, ShownTick = 17 } };
        Assert.Equal(16.5, OreChunkVisuals.ShownTick(hulls, 0, 14, .5f));
        Assert.Equal(14.5, OreChunkVisuals.ShownTick(hulls, -1, 14, .5f));
        // mesh.frag reads a material between 0.02 and 0.125 as an ore chunk, its grade rising with it.
        Assert.InRange(OreChunkVisuals.Material(0f), 0.02f, 0.125f);
        Assert.InRange(OreChunkVisuals.Material(1f), 0.02f, 0.125f);
        Assert.True(OreChunkVisuals.Material(0.8f) > OreChunkVisuals.Material(0.2f));
        Assert.Equal(OreChunkVisuals.Material(1f), OreChunkVisuals.Material(7f));
    }

    [Fact]
    public void ChunkPosesAndColoursTakeNoMemory()
    {
        var view = new OreChunkVisuals();
        var items = new OreChunk[OreChunks.Capacity];
        for (var i = 0; i < items.Length; i++)
            items[i] = Chunk((uint)i + 1, 1e9 + i, new Vector3(1, 0, 0)) with { AngularVelocity = new Vector3(0, 1, 0) };
        view.Update(items, 6);
        var tick = 6L;
        var probe = new AllocationProbe();
        var sum = 0.0;
        while (probe.Measuring())
        {
            view.Update(items, ++tick + 6);
            for (var i = 0; i < view.Items.Length; i++)
            {
                sum += view.Pose(i, tick + 3.5).Position.X;
                sum += OreChunkVisuals.Material(items[i].Grade);
            }
        }

        Assert.Equal(0, probe.Bytes);
        Assert.True(sum > 0);
    }

    [Fact]
    public void EveryChunkLumpIsAClosedBrokenMeshWithCrystalsOnRicherOnes()
    {
        var look = OreChunkModelLook.Load(AppPaths.Content("visuals", "ore_chunk_models.json"));
        for (var variant = 0; variant < look.Variants; variant++)
        {
            var previous = 0;
            foreach (var crystals in look.CrystalCounts)
            {
                var soup = OreChunkModels.Build(look, variant, crystals);
                Assert.True(soup.Length >= 36 && soup.Length % 3 == 0);
                var edges = new Dictionary<(Vector3, Vector3), int>();
                var tagged = 0;
                for (var t = 0; t < soup.Length; t += 3)
                {
                    var a = Position(soup[t]);
                    var b = Position(soup[t + 1]);
                    var c = Position(soup[t + 2]);
                    // Flat shaded: a unit normal, or the crystal tag's length, and the same on all three corners.
                    var normal = new Vector3(soup[t].Normal.X, soup[t].Normal.Y, soup[t].Normal.Z);
                    var length = normal.Length();
                    Assert.True(MathF.Abs(length - 1f) < 1e-3f || MathF.Abs(length - OreChunkModels.CrystalTag) < 1e-3f);
                    if (length > 1.1f) tagged++;
                    // Wound counter-clockwise seen from outside: the cross product follows the normal.
                    Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0);
                    foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                    {
                        var key = Compare(p, q) < 0 ? (p, q) : (q, p);
                        edges[key] = edges.GetValueOrDefault(key) + 1;
                    }
                }

                // Closed: every edge is shared by exactly two triangles, so the faces meet with no gap.
                Assert.True(edges.Values.All(count => count == 2), $"variant {variant}, crystals {crystals}: open edges {string.Join(" | ", edges.Where(e => e.Value != 2).Select(e => $"{e.Key.Item1}->{e.Key.Item2} x{e.Value}"))}");
                Assert.Equal(crystals == 0, tagged == 0);
                Assert.True(tagged >= previous);
                previous = tagged;
                foreach (var v in soup)
                {
                    var p = Position(v);
                    Assert.InRange(MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z))), 0.1f, 1.9f);
                }
            }
        }

        Assert.NotEqual(OreChunkModels.Build(look, 0, 0)[10].Position, OreChunkModels.Build(look, 1, 0)[10].Position);
        Assert.Equal(OreChunkModels.Build(look, 2, 4)[20].Position, OreChunkModels.Build(look, 2, 4)[20].Position);
    }

    [Fact]
    public void TheOreChunkLookIsValidatedAndGradeBandsFollowItsSteps()
    {
        var look = OreChunkModelLook.Load(AppPaths.Content("visuals", "ore_chunk_models.json"));
        Assert.Equal(0, look.Band(0f));
        Assert.Equal(look.CrystalGrades.Length, look.Band(1f));
        Assert.Equal(look.CrystalCounts.Length, look.CrystalGrades.Length + 1);
        Assert.Throws<InvalidDataException>(() => new OreChunkModelLook { CrystalCounts = [0, 3], CrystalGrades = [0.3f, 0.6f] }.Validate());
        Assert.Throws<InvalidDataException>(() => new OreChunkModelLook { BoxHalfMin = 0.9f, BoxHalfMax = 0.7f }.Validate());
        Assert.Throws<InvalidDataException>(() => new OreChunkModelLook { VeinCoverMax = 2f }.Validate());
    }

    private static int Compare(Vector3 a, Vector3 b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);
}
