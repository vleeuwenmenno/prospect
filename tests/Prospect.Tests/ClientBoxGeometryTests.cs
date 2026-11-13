using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Mixed-box geometry through the production mesh, picking and shield paths.</summary>
public sealed class ClientBoxGeometryTests
{
    private static HullDesign Long() => TestDesigns.Of(new DesignBox(new(0, 0, 0), new(64, 3, 2), TestDesigns.Content.Blocks.Get("core")));

    [Fact]
    public void AdmissionKeepsTheIntactShellAndEveryDamageSetGetsAWholeShellOrTheIntactOne()
    {
        var content = TestDesigns.Content;
        var boxes = new DesignBox[8];
        boxes[0] = new(new(0, 0, 0), new(12, 1, 12), content.Blocks.Get("core"));
        for (var i = 1; i < boxes.Length; i++)
            boxes[i] = new(new((i - 1) % 4 * 3, 1, (i - 1) / 4 * 6), new(2, 2, 5), content.Blocks.Get("hull"));
        var design = TestDesigns.Of(boxes);
        GeometryAdmission.Validate(design);
        Assert.False(design.IntactShell.IsEmpty);
        var vertices = new ShellVertex[GeometryAdmission.MaxVertices];
        var alive = new ulong[1];
        for (ulong subset = 0; subset < 256; subset++)
        {
            alive[0] = subset;
            // Either its own whole shell within the room, or none, and the hull keeps the intact one.
            var count = ShieldSurface.WithShared(b => b.Build(design, alive, ShellShape.Default, vertices, new ushort[GeometryAdmission.MaxIndices], out _));
            Assert.True(count == -1 || count is >= 0 and <= GeometryAdmission.MaxVertices);
        }
        Assert.Throws<InvalidDataException>(() => GeometryAdmission.Count(300, content.Blocks.Limits with { GeometryPairTests = 539999 }));
        Assert.Throws<InvalidDataException>(() => GeometryAdmission.Count(300, content.Blocks.Limits with { SurfaceVertices = 10799 }));
    }

    /// <summary>
    /// A solid body of boxes of mixed sizes packed face to face, as a player builds one: the cells of a region
    /// are filled in order, each with the largest box of a seeded choice that still fits. With
    /// <paramref name="gaps"/> some cells are left empty, so neighbours cover faces only in part.
    /// </summary>
    internal static DesignBox[] Packed(GridSize region, uint seed, int gaps = 0)
    {
        var content = TestDesigns.Content;
        GridSize[] sizes = [new(1, 1, 1), new(2, 1, 1), new(1, 1, 2), new(2, 2, 1), new(1, 2, 1), new(3, 1, 2), new(2, 2, 2)];
        string[] types = ["hull", "hull", "hull", "cargo", "thruster", "generator", "battery", "shield", "gyro", "engine"];
        var taken = new bool[region.X, region.Y, region.Z];
        var boxes = new List<DesignBox>();
        var random = new Random((int)seed);
        for (var y = 0; y < region.Y; y++)
            for (var z = 0; z < region.Z; z++)
                for (var x = 0; x < region.X; x++)
                {
                    if (taken[x, y, z]) continue;
                    if (boxes.Count > 0 && gaps > 0 && random.Next(gaps) == 0) { taken[x, y, z] = true; continue; }
                    var size = sizes[random.Next(sizes.Length)];
                    bool Fits()
                    {
                        if (x + size.X > region.X || y + size.Y > region.Y || z + size.Z > region.Z) return false;
                        for (var i = 0; i < size.X; i++)
                            for (var j = 0; j < size.Y; j++)
                                for (var k = 0; k < size.Z; k++)
                                    if (taken[x + i, y + j, z + k]) return false;
                        return true;
                    }
                    if (!Fits()) size = new GridSize(1, 1, 1);
                    for (var i = 0; i < size.X; i++)
                        for (var j = 0; j < size.Y; j++)
                            for (var k = 0; k < size.Z; k++)
                                taken[x + i, y + j, z + k] = true;
                    boxes.Add(new DesignBox(new GridSize(x, y, z), size, content.Blocks.Get(boxes.Count == 0 ? "core" : types[random.Next(types.Length)])));
                }
        return [.. boxes];
    }

    [Fact]
    public void ADenseBuildOfAboutEightyEightMixedBoxesIsAdmittedWithItsIntactShell()
    {
        var content = TestDesigns.Content;
        var boxes = Packed(new GridSize(6, 4, 7), 88);
        Assert.InRange(boxes.Length, 80, 96);
        var design = TestDesigns.Of(boxes);

        var usage = GeometryAdmission.Measure(design, content.Blocks.Limits.SurfaceVertices, content.Blocks.Limits.Shield);
        Assert.True(usage.Fits, $"{usage.Vertices} of {usage.Limit}");
        Assert.Equal(usage.Vertices, design.IntactShell.Length);
        GeometryAdmission.Validate(design, content.Blocks.Limits);

        // It goes through the door a committed design goes through, and comes out the same design, admitted.
        var bytes = DesignBytes.Encode(design);
        var decoded = DesignBytes.Decode(bytes, System.Security.Cryptography.SHA256.HashData(bytes), content);
        Assert.Equal(bytes, DesignBytes.Encode(decoded));
        Assert.Equal(design.IntactShell.Length, decoded.IntactShell.Length);

        // Damaged, a hull gets its own whole shell within the room or keeps the intact one; never part of one.
        var output = new ShellVertex[GeometryAdmission.MaxVertices];
        var random = new Random(5);
        var alive = design.FullSet.ToArray();
        for (var round = 0; round < 60; round++)
        {
            design.FullSet.CopyTo(alive);
            var losses = random.Next(1, design.BlockCount);
            for (var n = 0; n < losses; n++) BlockSet.Remove(alive, random.Next(design.BlockCount));
            Assert.InRange(ShieldSurface.WithShared(b => b.Build(design, alive, ShellShape.Default, output, new ushort[GeometryAdmission.MaxIndices], out _)), -1, GeometryAdmission.MaxVertices);
        }

        // A design that does not fit says how much it needs, and the measure names the same figures.
        var tight = GeometryAdmission.Measure(design, (int)usage.Vertices - 6);
        Assert.False(tight.Fits);
        Assert.Equal(usage.Vertices, tight.Vertices);
        var refused = Assert.Throws<InvalidDataException>(() => GeometryAdmission.Validate(design, (int)usage.Vertices - 6));
        Assert.Contains($"{usage.Vertices} of {usage.Vertices - 6}", refused.Message);
    }

    [Theory]
    [InlineData(1u, 0)]
    [InlineData(2u, 3)]
    [InlineData(3u, 2)]
    [InlineData(4u, 4)]
    public void EverySetOfSurvivorsOfPackedAndGappedBoxesGetsAWholeShellOrTheIntactOne(uint seed, int gaps)
    {
        // Few enough boxes to try every set of survivors, gapped so that neighbours meet in part and unevenly.
        var all = Packed(new GridSize(3, 2, 3), seed, gaps);
        var design = TestDesigns.Of(all.Length > 10 ? all[..10] : all);
        var usage = GeometryAdmission.Measure(design);
        Assert.True(usage.Fits);
        var output = new ShellVertex[GeometryAdmission.MaxVertices];
        var alive = new ulong[1];
        var most = 0;
        for (ulong subset = 1; subset < 1UL << design.BlockCount; subset++)
        {
            alive[0] = subset;
            var count = ShieldSurface.WithShared(b => b.Build(design, alive, ShellShape.Default, output, new ushort[GeometryAdmission.MaxIndices], out _));
            Assert.True(count > 0, $"subset {subset:X}: {ShieldSurface.WithShared(b => b.Work.Overflow)}");
            most = Math.Max(most, count);
        }
        Assert.InRange(most, 1, GeometryAdmission.MaxVertices);
    }

    [Fact]
    public void LongBoxMeshesSixFacesWithoutExpandingCells()
    {
        var design = Long();
        var mesh = MeshBuilder.Hull(design);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        foreach (var vertex in mesh.Vertices)
        {
            Assert.Equal(32f, Math.Abs(vertex.Position.X));
            Assert.Equal(1.5f, Math.Abs(vertex.Position.Y));
            Assert.Equal(1f, Math.Abs(vertex.Position.Z));
        }
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0.7f, 3f)]
    public void PickerHitsLongFaceAfterRotationAndStretch(float angle, float stretch)
    {
        var design = Long();
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
        var local = new Vector3(25, 0, -20);
        var camera = Vector3.Transform(local, rotation);
        var ray = Vector3.Transform(Vector3.UnitZ, rotation);
        var hull = new PickHull(1, default, rotation, design.BlockOffsets, design.BlockSize,
            design.FullSet.ToArray(), design.Flight.Radius, stretch, HalfExtents: design.BlockHalfExtents);
        var hits = new PickHit[1];
        Assert.Equal(1, TargetPicking.Pick(ray, new(camera.X, camera.Y, camera.Z), ray, 1000, [hull], [], hits));
        Assert.InRange(hits[0].Distance, 20 - stretch - 0.001f, 20 - stretch + 0.001f);
        hull = hull with { CutNormal = Vector3.Transform(Vector3.UnitX, rotation) };
        Assert.Equal(0, TargetPicking.Pick(ray, new(camera.X, camera.Y, camera.Z), ray, 1000, [hull], [], hits));
    }

    [Fact]
    public void PartialNeighbourKeepsExposedFaceButFullCoverageRemovesIt()
    {
        var core = TestDesigns.Content.Blocks.Get("core");
        var plate = TestDesigns.Content.Blocks.Get("hull");
        var partial = TestDesigns.Of(new(new(0, 0, 0), new(4, 2, 2), core), new(new(4, 0, 0), new(1, 1, 2), plate));
        Assert.NotEqual(0, partial.ExteriorFaces[0] & 1);
        var full = TestDesigns.Of(new(new(0, 0, 0), new(4, 2, 2), core),
            new(new(4, 0, 0), new(1, 1, 2), plate), new(new(4, 1, 0), new(1, 1, 2), plate));
        Assert.Equal(0, full.ExteriorFaces[0] & 1);
    }

    [Fact]
    public void MixedShieldKeepsEveryBoxAndLossAtBit129WithoutAllocating()
    {
        var boxes = new DesignBox[130];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new(new(i * 4, 0, 0), new(2, 1, 1), TestDesigns.Content.Blocks.Get(i == 0 ? "core" : "hull"));
        var design = TestDesigns.Of(boxes);
        GeometryAdmission.Validate(design);
        var builder = new ShieldSurface();
        var alive = design.FullSet.ToArray();
        var output = new ShellVertex[GeometryAdmission.MaxVertices];
        var indices = new ushort[GeometryAdmission.MaxIndices];
        var whole = builder.Build(design, alive, ShellShape.Default, output, indices, out _);
        Assert.Equal(design.IntactShell.Length, whole);
        alive[2] &= ~(1UL << 1);
        var damaged = builder.Build(design, alive, ShellShape.Default, output, indices, out _);
        var reach = float.MinValue;
        for (var i = 0; i < damaged; i++) reach = Math.Max(reach, output[i].Position.X);
        Assert.True(damaged > 0);
        // The row ends a box sooner: the far end of the shell moved in by that box's length.
        Assert.True(reach < design.BlockOffsets[129].X, $"the shell still reaches {reach}");
        for (var i = 0; i < 3; i++) builder.Build(design, alive, ShellShape.Default, output, indices, out _);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 3; i++)
            while (allocation.Measuring()) builder.Build(design, alive, ShellShape.Default, output, indices, out _);
        Assert.Equal(0, allocation.Bytes);
        Assert.Throws<InvalidDataException>(() => GeometryAdmission.Validate(design, maxVertices: whole - 1));
    }

    [Fact]
    public void ASizedShellStandsTheMarginOffTheBoxsActualFaces()
    {
        var design = Long();
        var output = new ShellVertex[GeometryAdmission.MaxVertices];
        var count = ShieldSurface.WithShared(b => b.Build(design, design.FullSet, ShellShape.Default, output, new ushort[GeometryAdmission.MaxIndices], out _));
        Assert.True(count > 0);
        // Flat faces are exact: the shell's extent is the box's grown by the margin, to the last rounding.
        var high = new Vector3(float.MinValue);
        foreach (var vertex in output.AsSpan(0, count))
            high = Vector3.Max(high, Vector3.Abs(vertex.Position));
        Assert.Equal(32.3f, high.X, 3);
        Assert.Equal(1.8f, high.Y, 3);
        Assert.Equal(1.3f, high.Z, 3);
    }
    [Fact]
    public void RuntimeCatalogueAndShieldCacheUseVariableWordsAcrossDamageAndSessions()
    {
        var design = TestDesigns.Cubes(130);
        using var jobs = new JobSystem(1, "Geometry");
        var worker = new CatalogueMeshes(jobs);
        HullDesign[] designs = [design];
        worker.Prepare(designs);
        jobs.CompleteAll();
        Assert.NotNull(worker.Prepare(designs));
        worker.Accept(designs);
        var alive = design.FullSet.ToArray();
        HullView[] hulls = [new() { Id = 1, Blocks = alive, Stats = design.Stats, Shield = design.Stats.ShieldCapacity }];
        var shields = new ShieldVisuals(ShieldLook.Load());
        var draws = new ShieldDraw[1];
        var instances = new ShieldInstance[1];
        var vertices = new MeshVertex[ShieldVisuals.ShellVertices];
        var indices = new ushort[ShieldVisuals.ShellIndices];
        void Frame(long publication)
        {
            shields.Update(hulls, designs, publication, 0.01f);
            shields.Begin(draws);
            shields.Add(1, default, Quaternion.Identity, 1, 1, instances, vertices, indices);
        }
        Frame(1);
        Assert.Single(shields.Draws.ToArray());
        var initial = vertices.AsSpan(0, draws[0].VertexCount).ToArray();
        alive[2] &= ~(1UL << 1);
        hulls[0].Revision++;
        Frame(2);
        Assert.False(initial.AsSpan().SequenceEqual(vertices.AsSpan(0, draws[0].VertexCount)));
        for (var i = 0; i < 10; i++) Frame(2);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 30; i++)
            while (allocation.Measuring()) Frame(2);
        Assert.Equal(0, allocation.Bytes);
        shields.Clear();
        designs = [Long()];
        hulls[0].Blocks = new ulong[] { 1 };
        hulls[0].Stats = design.Stats;
        Frame(3);
        var expected = ShieldSurface.WithShared(b => b.Build(designs[0], [1UL], ShellShape.Default, new ShellVertex[GeometryAdmission.MaxVertices], new ushort[GeometryAdmission.MaxIndices], out _));
        Assert.Equal(expected, draws[0].VertexCount);
    }

    [Fact]
    public void AUnionHasNoInternalShellAndDamageOpensTheGap()
    {
        var core = TestDesigns.Content.Blocks.Get("core");
        var design = TestDesigns.Of(new(new(0, 0, 0), new(4, 2, 2), core),
            new(new(4, 0, 0), new(2, 2, 2), core));
        var output = new ShellVertex[GeometryAdmission.MaxVertices];
        var count = ShieldSurface.WithShared(b => b.Build(design, design.FullSet, ShellShape.Default, output, new ushort[GeometryAdmission.MaxIndices], out _));
        // A rectangular union has no shell between its blocks: nothing lies inside their joined box.
        var low = design.BlockOffsets[0] - design.BlockHalfExtents[0];
        var high = design.BlockOffsets[1] + design.BlockHalfExtents[1];
        for (var i = 0; i < count; i++)
        {
            var p = output[i].Position;
            Assert.False(p.X > low.X && p.X < high.X && p.Y > low.Y && p.Y < high.Y && p.Z > low.Z && p.Z < high.Z, $"shell inside the union at {p}");
        }
        var one = ShieldSurface.WithShared(b => b.Build(design, [1UL], ShellShape.Default, output, new ushort[GeometryAdmission.MaxIndices], out _));
        Assert.True(one > 0);
        // With the second box lost, nothing of the shell is past the first box's margin.
        for (var i = 0; i < one; i++)
            Assert.True(output[i].Position.X < design.BlockOffsets[0].X + design.BlockHalfExtents[0].X + design.BlockSize * 0.6f);
    }

    [Fact]
    public void DamagedDrawUsesVectorDimensionsRotationStretchAndTint()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        var size = new Vector3(64, 3, 2);
        var offset = new Vector3(5, 8, 10);
        var tint = new Vector3(0.1f, 0.4f, 0.7f);
        var instance = WorldView.MeshTransform(offset, rotation, size, tint, 2, 3);
        foreach (var vertex in MeshBuilder.Cube().Vertices)
        {
            var p = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
            var drawn = new Vector3(
                instance.Row0.X * p.X + instance.Row0.Y * p.Y + instance.Row0.Z * p.Z + instance.Row0.W,
                instance.Row1.X * p.X + instance.Row1.Y * p.Y + instance.Row1.Z * p.Z + instance.Row1.W,
                instance.Row2.X * p.X + instance.Row2.Y * p.Y + instance.Row2.Z * p.Z + instance.Row2.W);
            var expected = offset + Vector3.Transform(p * size * new Vector3(1, 1, 3), rotation);
            Assert.InRange(Vector3.Distance(drawn, expected), 0, 0.00001f);
        }
        Assert.Equal(tint.X, instance.Tint.X);
        Assert.Equal(tint.Y, instance.Tint.Y);
        Assert.Equal(tint.Z, instance.Tint.Z);
    }

}
