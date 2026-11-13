using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class BlockShapeTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static HullDesign Design(BlockShape shape, GridSize size) =>
        HullDesign.Of(new BoxDesign("shape", 1, [new(default, size, Content.Blocks.Get("core"), shape)], Content.Blocks.Limits, "shape"), Content.Rules);

    [Fact]
    public void EveryOrientationHasSixVerticesAndFourTurnsRestoreIt()
    {
        for (var code = 1; code <= 12; code++)
        {
            var shape = (BlockShape)code;
            Assert.Equal(shape, BlockShapes.Parse(BlockShapes.Name(shape)));
            var vertices = new Vector3[8];
            var h = new Vector3(1, 2, 3);
            Assert.Equal(6, BlockShapes.Vertices(shape, h, vertices));
            foreach (var p in vertices.AsSpan(0, 6)) Assert.True(Vector3.Dot(BlockShapes.Slant(shape, h), p) <= 0);
            for (var axis = 0; axis < 3; axis++)
            {
                var rotated = shape;
                for (var turn = 0; turn < 4; turn++) rotated = BlockShapes.Rotate(rotated, axis);
                Assert.Equal(shape, rotated);
            }
            var design = Design(shape, new(2, 4, 6));
            Assert.False(design.UsesRoundedUnitShell);
            Assert.Equal(24, design.Stats.Volume);
            var centroid = BoxDesign.Centroid(design.Boxes[0]);
            BlockShapes.Decode(shape, out var e, out var u, out var v, out var su, out var sv);
            double[] c = [centroid.X, centroid.Y, centroid.Z];
            double[] mid = [0.5, 1.5, 2.5]; double[] size = [2, 4, 6];
            Assert.Equal(mid[e], c[e]); Assert.Equal(mid[u] - su * size[u] / 6, c[u]); Assert.Equal(mid[v] - sv * size[v] / 6, c[v]);
            var mass = design.Flight.Mass;
            var variance = new Vector3((float)(4.0 / (e == 0 ? 12 : 18)), (float)(16.0 / (e == 1 ? 12 : 18)), (float)(36.0 / (e == 2 ? 12 : 18)));
            Assert.True(Vector3.Distance(design.Flight.Inertia, mass * new Vector3(variance.Y + variance.Z, variance.X + variance.Z, variance.X + variance.Y)) < 0.01f);
        }
        var unit = Design(BlockShape.Box, new(1, 1, 1));
        var wedge = Design(BlockShape.WedgeZPlusPlus, new(1, 1, 2));
        Assert.Equal(unit.Stats.Mass, wedge.Stats.Mass);
        Assert.Equal(unit.Stats.Volume, wedge.Stats.Volume);
        Assert.Equal(unit.Stats.HullPoints, wedge.Stats.HullPoints);
        Assert.Equal(unit.Stats.PowerGenerated, wedge.Stats.PowerGenerated);
        Assert.Equal(unit.Stats.Crew, wedge.Stats.Crew);
        foreach (var type in Content.Pod.Types)
            foreach (var stat in BoxDesign.StatNames)
                Assert.Equal(BoxDesign.Scale(type, stat, 17, 1), BoxDesign.ScaleHalf(type, stat, 17, 2));
        Assert.Equal(unit.BlockHitPoints(0), wedge.BlockHitPoints(0));
    }

    [Fact]
    public void JsonAndCanonicalNetworkBytesPreserveShapesAndRejectHostileCodes()
    {
        for (var code = 0; code <= 12; code++)
        {
            var design = Design((BlockShape)code, new(2, 3, 4));
            var bytes = DesignBytes.Encode(design);
            var copy = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
            Assert.Equal(design.Boxes.ToArray(), copy.Boxes.ToArray());
            Assert.Equal(design.Flight, copy.Flight);
            using var stream = new MemoryStream();
            new BoxDesign("shape", 1, design.Boxes.ToArray(), Content.Blocks.Limits, "test").Write(stream);
            using var json = JsonDocument.Parse(stream.ToArray());
            var parsed = BoxDesign.Parse(json.RootElement, Content.Blocks, "test");
            Assert.Equal(design.Boxes.ToArray(), parsed.Blocks.ToArray());
            foreach (var hostile in new byte[] { 13, 127, 255 })
            {
                bytes[^1] = hostile;
                Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content));
            }
        }
        Assert.Throws<InvalidDataException>(() => BlockShapes.Parse("wedge-z"));
    }

    [Fact]
    public void CollisionUsesFilledHalfForEveryOrientationScaleAndRotation()
    {
        for (var code = 1; code <= 12; code++)
        {
            var shape = (BlockShape)code;
            var h = new Vector3(2, 3, 4);
            BlockShapes.Decode(shape, out _, out var u, out var v, out var su, out var sv);
            var empty = Vector3.Zero; empty[u] = su * h[u] * 0.7f; empty[v] = sv * h[v] * 0.7f;
            foreach (var rotation in new[] { Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.67f) })
            {
                Assert.False(BoxCollision.Contact(-Vector3.Transform(empty, rotation), h, rotation, new Vector3(0.1f), shape, BlockShape.Box, out _, out _));
                Assert.True(BoxCollision.Contact(Vector3.Transform(empty, rotation), h, rotation, new Vector3(0.1f), shape, BlockShape.Box, out var normal, out var depth));
                Assert.False(BoxCollision.Contact(Vector3.Transform(empty, rotation) + normal * (depth + 0.001f), h, rotation, new Vector3(0.1f), shape, BlockShape.Box, out _, out _));
                var rock = new Asteroid { Radius = 0.2f, Rotation = Quaternion.Identity, Shape = 0 };
                Assert.False(BoxCollision.Rock(-Vector3.Transform(empty, rotation), h, rotation, rock, out _, out _, out _, shape));
                Assert.True(BoxCollision.Rock(Vector3.Transform(empty, rotation), h, rotation, rock, out _, out _, out _, shape));
            }
        }
    }

    [Fact]
    public void WarmWedgeCollisionAllocatesNothing()
    {
        static void Run()
        {
            BoxCollision.Contact(new Vector3(0.3f), new Vector3(2, 3, 4), Quaternion.Identity, new Vector3(1), BlockShape.WedgeZPlusPlus, BlockShape.WedgeXMinusPlus, out _, out _);
            var rock = new Asteroid { Radius = 2, Rotation = Quaternion.Identity };
            BoxCollision.Rock(Vector3.One, new Vector3(2, 3, 4), Quaternion.Identity, rock, out _, out _, out _, BlockShape.WedgeYMinusMinus);
        }
        for (var i = 0; i < 100; i++) Run();
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (allocation.Measuring()) Run();
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void DraftPreservesShapeAndRotationIsAtomic()
    {
        var design = Design(BlockShape.WedgeZPlusPlus, new(1, 2, 3));
        var draft = BuildDraft.From(design, design.FullSet, Content);
        Assert.Equal(DraftEdit.Done, draft.Rotate(0, 2));
        Assert.Equal(default, draft.Boxes[0].At);
        Assert.Equal(new GridSize(2, 1, 3), draft.Boxes[0].Size);
        Assert.Equal(BlockShapes.Rotate(design.Boxes[0].Shape, 2), draft.Boxes[0].Shape);
        Assert.True(draft.Undo()); Assert.Equal(design.Boxes[0], draft.Boxes[0]); Assert.True(draft.Redo());
        var clipboard = draft.Boxes.ToArray();
        Assert.Equal(DraftEdit.Done, draft.Paste(clipboard, new(4, 0, 0)));
        Assert.Equal(clipboard[0].Shape, draft.Boxes[1].Shape);
        Assert.Equal(DraftEdit.NotABox, draft.CanMerge([0, 1], out _));
        Assert.Equal(DraftEdit.Done, draft.Resize(1, 0, 3));
        Assert.Equal(clipboard[0].Shape, draft.Boxes[1].Shape);
        Assert.Equal(DraftEdit.Done, draft.Add(new(0, 1, 0), new(1, 1, 1), Content.Blocks.Get("hull")));
        Assert.Equal(DraftEdit.Done, draft.Move([1], new(0, 5, 0)));
        Assert.Equal(clipboard[0].Shape, draft.Boxes[1].Shape);
        var frozen = draft.Copy().Export();
        Assert.NotNull(frozen.Design);
        Assert.Equal(draft.Boxes.ToArray(), frozen.Design.Boxes.ToArray());
        var version = draft.Version;
        Assert.Equal(DraftEdit.Overlaps, draft.Rotate(0, 2));
        Assert.Equal(version, draft.Version);
        Assert.Equal(clipboard[0], draft.Boxes[0]);
    }
}
