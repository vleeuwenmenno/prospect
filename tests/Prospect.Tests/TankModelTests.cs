using System.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The models of fuel tanks: an open frame with round vessels inside, a pure function of the tank's box. A vessel is a
/// sphere or a capsule of the smallest side's radius, never stretched; a slab holds capsules side by side.
/// </summary>
public sealed class TankModelTests
{
    private static readonly GameContent Content = FleetContent.Value;
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly GridSize One = new(1, 1, 1);

    private static HullDesign Design(string type, GridSize size, BlockFacing facing = BlockFacing.Back) =>
        TestDesigns.Of([new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")),
            new DesignBox(new GridSize(1, 0, 0), size, Content.Blocks.Get(type), BlockShape.Box, facing)]);

    private static (List<MeshVertex> Vertices, List<uint> Indices) Model(HullDesign design, int block)
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        Assert.True(PropulsionModels.Add(design, block, Look, vertices, indices, null));
        return (vertices, indices);
    }

    private static Vector3 P(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    private static float Length(MeshVertex v) => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length();

    private static float ToSegment(Vector3 p, in TankVessel vessel)
    {
        var along = Math.Clamp(p[vessel.Axis] - vessel.Center[vessel.Axis], -vessel.Half, vessel.Half);
        var nearest = vessel.Center;
        nearest[vessel.Axis] += along;
        return Vector3.Distance(p, nearest);
    }

    [Fact]
    public void EveryTankTypeShipsWithAModelAndOnlyAsABox()
    {
        Assert.NotNull(Look.Tanks);
        foreach (var type in Content.Blocks.Types)
        {
            if (type.Tank == FuelKind.None)
            {
                Assert.Null(PropulsionModels.TankOf(type, BlockShape.Box, Look));
                continue;
            }
            Assert.NotNull(PropulsionModels.TankOf(type, BlockShape.Box, Look));
            Assert.True(PropulsionModels.HasModel(type, BlockShape.Box, Look));
            Assert.Null(PropulsionModels.TankOf(type, BlockShape.WedgeXPlusPlus, Look));
        }
    }

    [Theory]
    [InlineData(1, 1, 1, 1, false)]
    [InlineData(2, 2, 2, 1, false)]
    [InlineData(2, 1, 1, 1, true)]
    [InlineData(1, 1, 3, 1, true)]
    [InlineData(1, 4, 1, 1, true)]
    [InlineData(2, 2, 1, 2, true)]
    [InlineData(3, 2, 1, 2, true)]
    [InlineData(4, 4, 1, 4, true)]
    [InlineData(1, 3, 3, 3, true)]
    [InlineData(3, 3, 2, 2, true)]
    public void AVesselIsASphereOrACapsuleOfTheSmallestSidesRadiusNeverStretched(int x, int y, int z, int vessels, bool capsule)
    {
        var size = new GridSize(x, y, z);
        var design = Design("bipropellantTank", size);
        var half = design.BlockHalfExtents[1];
        Span<TankVessel> layout = stackalloc TankVessel[PropulsionModels.MaxTankVessels];
        var count = PropulsionModels.TankLayout(Look.Tanks!, half, BlockFacing.Back, layout, out var beam, out var across);
        Assert.Equal(vessels, count);
        var small = MathF.Min(half.X, MathF.Min(half.Y, half.Z));
        var longest = MathF.Max(half.X, MathF.Max(half.Y, half.Z));
        for (var k = 0; k < count; k++)
        {
            var vessel = layout[k];
            // Along the longest side, of one radius from the smallest side, and the same for every vessel.
            Assert.Equal(longest, half[vessel.Axis]);
            Assert.Equal(layout[0].Radius, vessel.Radius);
            Assert.True(vessel.Radius > 0 && vessel.Radius <= small - beam, $"radius {vessel.Radius}");
            Assert.Equal(capsule, vessel.Half > 0);
            // It fits its box, and its neighbours' strips.
            Assert.True(MathF.Abs(vessel.Center[vessel.Axis]) + vessel.Half + vessel.Radius <= half[vessel.Axis] - beam + 1e-5f);
            Assert.True(MathF.Abs(vessel.Center[across]) + vessel.Radius <= half[across] - beam + 1e-5f);
            if (k > 0)
                Assert.True(vessel.Center[across] - layout[k - 1].Center[across] >= 2f * vessel.Radius);
        }

        // Every painted vertex lies on a vessel: its radius from the vessel's axis segment, so a sphere is round and
        // a capsule's ends are hemispheres of the cylinder's radius.
        var (vertices, _) = Model(design, 1);
        var centre = design.BlockOffsets[1];
        var painted = 0;
        foreach (var vertex in vertices)
        {
            if (!PropulsionModels.PaintOf(Length(vertex), out _))
                continue;
            painted++;
            var p = P(vertex) - centre;
            var nearest = float.MaxValue;
            for (var k = 0; k < count; k++)
                nearest = MathF.Min(nearest, MathF.Abs(ToSegment(p, layout[k]) - layout[k].Radius));
            Assert.True(nearest < 1e-4f, $"a painted vertex is {nearest} off its vessel");
        }
        Assert.True(painted > 0);
    }

    [Fact]
    public void ACubesSpherePointsItsPolesAlongTheFacingAndAnElongatedTankItsLength()
    {
        Span<TankVessel> layout = stackalloc TankVessel[PropulsionModels.MaxTankVessels];
        foreach (var facing in new[] { BlockFacing.Right, BlockFacing.Up, BlockFacing.Front })
        {
            PropulsionModels.TankLayout(Look.Tanks!, new Vector3(0.5f), facing, layout, out _, out _);
            Assert.Equal(Facings.Axis(facing), layout[0].Axis);
            Assert.Equal(0f, layout[0].Half);
        }
        // A 2 by 1 by 1 tank is a capsule along x whatever it faces.
        PropulsionModels.TankLayout(Look.Tanks!, new Vector3(1f, 0.5f, 0.5f), BlockFacing.Up, layout, out var beam, out _);
        Assert.Equal(0, layout[0].Axis);
        Assert.True(layout[0].Half > 0);
        Assert.Equal(Look.Tanks!.TankShare * (0.5f - beam), layout[0].Radius, 5);
    }

    [Theory]
    [InlineData("bipropellantTank", 1, 1, 1)]
    [InlineData("hydrazineTank", 2, 1, 1)]
    [InlineData("nitrogenTank", 3, 2, 1)]
    [InlineData("microNitrogenTank", 1, 2, 2)]
    public void AModelStaysInsideItsBoxIsFlatShadedLowPolyAndTheSameEveryTime(string type, int x, int y, int z)
    {
        foreach (var facing in new[] { BlockFacing.Back, BlockFacing.Up, BlockFacing.Left })
        {
            var design = Design(type, new GridSize(x, y, z), facing);
            var (vertices, indices) = Model(design, 1);
            var centre = design.BlockOffsets[1];
            var half = design.BlockHalfExtents[1];
            foreach (var vertex in vertices)
            {
                var p = P(vertex) - centre;
                Assert.True(MathF.Abs(p.X) <= half.X + 1e-4f && MathF.Abs(p.Y) <= half.Y + 1e-4f && MathF.Abs(p.Z) <= half.Z + 1e-4f, $"{type} {facing}: {p}");
            }
            for (var i = 0; i < indices.Count; i += 3)
            {
                var a = vertices[(int)indices[i]];
                var b = vertices[(int)indices[i + 1]];
                var c = vertices[(int)indices[i + 2]];
                Assert.Equal(a.Normal, b.Normal);
                Assert.Equal(a.Normal, c.Normal);
                var facet = Vector3.Normalize(Vector3.Cross(P(b) - P(a), P(c) - P(a)));
                Assert.True(Vector3.Dot(facet, Vector3.Normalize(new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z))) > 0.999f, $"triangle {i / 3}");
            }
            Assert.InRange(vertices.Count, 300, 8000);

            var (again, againIndices) = Model(design, 1);
            Assert.Equal(vertices, again);
            Assert.Equal(indices, againIndices);
        }
    }

    [Fact]
    public void EachFuelHasItsPaintAndAStripeRoundTheMiddle()
    {
        foreach (var (type, fuel) in new[] { ("nitrogenTank", "nitrogen"), ("hydrazineTank", "hydrazine"), ("bipropellantTank", "bipropellant") })
        {
            var paint = Look.Tanks!.Fuels[fuel];
            var body = PropulsionModels.Paint(paint.Body);
            var stripe = PropulsionModels.Paint(paint.Stripe);
            Assert.NotEqual(body, stripe);
            Assert.True(PropulsionModels.PaintOf(body, out var rgb));
            Assert.True(Vector3.Distance(rgb, new Vector3(paint.Body[0], paint.Body[1], paint.Body[2])) < 0.03f);
            var (vertices, _) = Model(Design(type, new GridSize(2, 1, 1)), 1);
            var bodies = 0;
            var stripes = 0;
            foreach (var vertex in vertices)
            {
                var length = Length(vertex);
                if (length == body) bodies++;
                else if (length == stripe) stripes++;
            }
            Assert.True(bodies > stripes && stripes > 0, $"{type}: {bodies} body, {stripes} stripe");
        }
        // Bare metal never reads as paint, and paint never as metal.
        Assert.False(PropulsionModels.PaintOf(PropulsionModels.Tag(0.99f), out _));
        Assert.True(PropulsionModels.Paint([0, 0, 0]) >= 3.5f);
    }

    [Fact]
    public void AFaceANeighbourTouchesIsClosedInsideTheFrame()
    {
        // The tank's -x face touches the core: a plate faces into the tank there; its +x face is open.
        var design = Design("nitrogenTank", One);
        Assert.Equal(0b10, design.TouchedFaces[1]);
        var (vertices, _) = Model(design, 1);
        var low = design.BlockOffsets[1].X - design.BlockHalfExtents[1].X;
        var high = design.BlockOffsets[1].X + design.BlockHalfExtents[1].X;
        var plate = PropulsionModels.Tag(Look.Tanks!.PlateShade);
        int closed = 0, open = 0;
        foreach (var vertex in vertices)
        {
            if (MathF.Abs(Length(vertex) - plate) > 1e-4f || vertex.Normal.X == 0)
                continue;
            if (vertex.Normal.X > 0 && P(vertex).X - low < 0.1f) closed++;
            if (high - P(vertex).X < 0.1f) open++;
        }
        Assert.True(closed > 0);
        Assert.Equal(0, open);
    }

    [Fact]
    public void ATankModelIsBuiltOnAWorkerThenFoundWithoutTakingMemory()
    {
        using var jobs = new JobSystem(1, "Tanks");
        var models = new PartModels(jobs, Look);
        var tank = Content.Blocks.Get("hydrazineTank");
        var half = new Vector3(1f, 0.5f, 0.5f);
        models.Begin();
        Assert.False(models.TryGet(tank, BlockShape.Box, half, BlockFacing.Back, 1f, out _));
        Assert.False(models.TryGet(tank, BlockShape.WedgeXPlusPlus, half, BlockFacing.Back, 1f, out _));
        models.Flush();
        jobs.CompleteAll();
        models.Begin();
        Assert.True(models.TryGet(tank, BlockShape.Box, half, BlockFacing.Back, 1f, out var model));
        Assert.Equal(0, model.Length % 3);

        // The same model a design's own tank gets with no neighbour, centred on its box.
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        PropulsionModels.BuildPart(tank, new PartBox(Vector3.Zero, half, BlockFacing.Back, 1f), Look, vertices, indices);
        Assert.Equal(indices.Count, model.Length);

        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            models.Begin();
            models.TryGet(tank, BlockShape.Box, half, BlockFacing.Back, 1f, out _);
            models.Flush();
        }
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void AHullMeshAndADamagedHullDrawTheirTanksAsModels()
    {
        static int Painted(IEnumerable<MeshVertex> vertices) => vertices.Count(v => PropulsionModels.PaintOf(Length(v), out _));
        var design = Content.Pod;
        var mesh = MeshBuilder.Hull(design, Look);
        Assert.True(Painted(mesh.Vertices) > 0);

        // A damaged hull draws the same vessels while it has the tanks, and none once they are lost.
        var alive = design.FullSet.ToArray();
        var whole = DamageMesh.Build(design, alive, default, 400000, 0.2f)!;
        Assert.Equal(Painted(mesh.Indices.Select(i => mesh.Vertices[(int)i])), Painted(whole.Original));
        for (var b = 0; b < design.BlockCount; b++)
            if (design.Types[b].Tank != FuelKind.None)
                BlockSet.Remove(alive, b);
        var without = DamageMesh.Build(design, alive, default, 400000, 0.2f)!;

        // What paint is left is not a vessel's: the caps of the cold gas arrays on the blocks the hull still has.
        var caps = 0;
        for (var m = 0; m < design.Mounts.Length; m++)
        {
            if (design.Types[design.Mounts[m].Parent].Tank != FuelKind.None)
                continue;
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            if (PropulsionModels.AddMount(design, m, Look, vertices, indices, null))
                caps += Painted(indices.Select(i => vertices[(int)i]));
        }

        Assert.Equal(caps, Painted(without.Original));
    }
}
