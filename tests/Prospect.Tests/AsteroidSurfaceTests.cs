using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class AsteroidSurfaceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    [InlineData(4000)]
    public void ContactMatchesDrawnTriangles(float radius)
    {
        var random = new Random(123);
        var worst = 0f;
        var oldGaps = new List<float>();
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
        {
            var mesh = MeshBuilder.Asteroid(shape, AsteroidMesh.Subdivisions);
            for (var sample = 0; sample < 3000; sample++)
            {
                var triangle = random.Next(mesh.Indices.Count / 3) * 3;
                Vector3 Vertex(int i)
                {
                    var v = mesh.Vertices[(int)mesh.Indices[triangle + i]].Position;
                    return new Vector3(v.X, v.Y, v.Z);
                }
                var a = Vertex(0); var b = Vertex(1); var c = Vertex(2);
                var u = (float)random.NextDouble(); var v = (float)random.NextDouble();
                if (u + v > 1) { u = 1 - u; v = 1 - v; }
                var point = a + (b - a) * u + (c - a) * v;
                var direction = Vector3.Normalize(point);
                var gap = radius * Math.Abs(AsteroidShape.RadiusAlong(shape, direction) - point.Length());
                worst = Math.Max(worst, gap);
                Assert.True(gap < 0.05f, $"Shape {shape}, radius {radius}, gap {gap}");
                oldGaps.Add(radius * (AsteroidShape.SmoothRadiusAlong(shape, direction) - point.Length()));
                var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                Assert.True(Vector3.Dot(normal, AsteroidShape.Normal(shape, point, radius, 0)) > 0.999f);
            }
        }
        oldGaps.Sort();
        output.WriteLine($"Radius {radius}: old signed median {oldGaps[oldGaps.Count / 2]} m, max {oldGaps[^1]} m; new max absolute {worst} m");
    }

    [Fact]
    public void HullSettlesOnGeneratedGiantFacet()
    {
        var content = GameContent.Load();
        var galaxy = content.Galaxy.WithSeed(ServerConfig.DefaultSeed);
        var rocks = new RockField(galaxy);
        var coord = new SectorCoord(3, 0, 1);
        var sector = rocks.Sector(coord);
        var rock = sector.Asteroids.Take(sector.AsteroidCount).MaxBy(a => a.Radius);
        Assert.True(rock.Radius > 3000);
        var mesh = MeshBuilder.Asteroid(rock.Shape, AsteroidMesh.Subdivisions);
        var gaps = new List<float>();
        var maxNewGap = 0f;
        for (var triangle = 0; triangle < mesh.Indices.Count; triangle += 3)
        {
            var point = Vector3.Zero;
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = mesh.Vertices[(int)mesh.Indices[triangle + corner]].Position;
                point += new Vector3(vertex.X, vertex.Y, vertex.Z) / 3;
            }
            var direction = Vector3.Normalize(point);
            gaps.Add(rock.Radius * (AsteroidShape.SmoothRadiusAlong(rock.Shape, direction) - point.Length()));
            maxNewGap = Math.Max(maxNewGap, rock.Radius * Math.Abs(AsteroidShape.RadiusAlong(rock.Shape, direction) - point.Length()));
        }
        gaps.Sort();
        output.WriteLine($"Giant triangle centres: old signed gap min {gaps[0]}, median {gaps[gaps.Count / 2]}, max {gaps[^1]} m; new max absolute {maxNewGap} m");
        Vector3 Vertex(int i)
        {
            var v = mesh.Vertices[(int)mesh.Indices[i]].Position;
            return Vector3.Transform(new Vector3(v.X, v.Y, v.Z) * rock.Radius, rock.Rotation);
        }
        var a = Vertex(0); var b = Vertex(1); var c = Vertex(2);
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        var surface = (a + b + c) / 3;
        var design = content.Pod;
        var rotation = Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitY, normal), 1 + normal.Y));
        var bottom = float.PositiveInfinity;
        for (var i = 0; i < design.BlockCount; i++)
            bottom = Math.Min(bottom, design.BlockOffsets[i].Y - design.BlockHalfExtents[i].Y);
        var center = galaxy.CenterOf(coord) + rock.LocalPosition;
        var state = HullState.Spawn(center + surface + normal * (-bottom - 0.02f), design.Flight);
        state.Orientation = rotation;
        using var states = new BlockStateStore();
        var body = states.Body(design);
        var order = default(HullOrder);
        for (var tick = 0; tick < 30; tick++)
            HullSim.Step(ref state, ref order, default, design.Flight, ref body, rocks, tick);
        var settled = state.Position.RelativeTo(center);
        var gap = Vector3.Dot(settled - surface, normal) + bottom;
        output.WriteLine($"Generated giant radius {rock.Radius}, shape {rock.Shape}: settled hull gap {gap} m");
        Assert.InRange(gap, -0.05f, 0.05f);
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(60, true)]
    [InlineData(600, false)]
    [InlineData(600, true)]
    [InlineData(600, false, -1)]
    [InlineData(60, false, -1)]
    [InlineData(600, false, -1, 400)]
    public void LongHullCannotLeaveBlockCentresInsideRock(int count, bool sideways, int sign = 1, float inset = 0)
    {
        var content = GameContent.Load();
        var galaxy = content.Galaxy.WithSeed(ServerConfig.DefaultSeed);
        var rocks = PowerAndDamageTests.ContactFixture(galaxy);
        var sector = rocks.Sector(default);
        sector.Asteroids[0].Radius = 4000;
        sector.BoundsRadius = 4000;
        sector.Rocks.Reset(1, galaxy.SectorSize);
        sector.Rocks.Add(0, Vector3.Zero, 4000);
        var rock = sector.Asteroids[0];
        var type = new BlockType { Id = "line", Name = "Line", Core = true, Mass = 1, Hull = 100000, Shield = 100000 };
        var boxes = new DesignBox[count];
        for (var i = 0; i < count; i++) boxes[i] = new(new(i, 0, 0), new(1, 1, 1), type);
        var design = HullDesign.Of(new BoxDesign("Line", 1, boxes, content.Blocks.Limits, "line"), content.Rules);
        using var states = new BlockStateStore();
        var body = states.Body(design);
        var direction = (sideways ? Vector3.UnitY : Vector3.UnitX) * sign;
        var surface = direction * (rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, direction));
        var center = galaxy.CenterOf(default);
        var state = HullState.Spawn(center + surface + direction * ((sideways ? 2 : count * 0.5f + 2) - inset), design.Flight);
        var order = default(HullOrder);
        var probe = new AllocationProbe();
        for (var tick = 0; tick < 120; tick++)
        {
            state.Velocity = -direction * 10;
            HullSim.Step(ref state, ref order, default, design.Flight, ref body, rocks, tick);
            for (var block = 0; block < count; block++)
            {
                var point = state.Position.RelativeTo(center) + Vector3.Transform(design.BlockOffsets[block], state.Orientation);
                var distance = point.Length();
                var gap = distance - rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, point / distance);
                Assert.True(gap >= -0.005f, $"Tick {tick}, block {block}/{count}, sideways {sideways}: centre is {-gap} m inside");
            }
        }
        while (probe.Measuring())
            HullSim.Step(ref state, ref order, default, design.Flight, ref body, rocks, 121);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void ContactQueriesAllocateNothing()
    {
        AsteroidShape.Prepare();
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (probe.Measuring())
            {
                _ = AsteroidShape.RadiusAlong(i % Galaxy.RockShapes, Vector3.UnitY);
                _ = AsteroidShape.Normal(i % Galaxy.RockShapes, Vector3.UnitY, 4000, 0);
            }
        Assert.Equal(0, probe.Bytes);
    }
}
