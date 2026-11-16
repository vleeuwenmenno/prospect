using System.Numerics;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Visual hit locations follow actual collision surfaces without changing collision damage.</summary>
public sealed class ShieldImpactTests
{
    private static readonly GameContent Content = GameContent.Load();
    [Fact]
    public void EnergyCurveIsMonotonicAndDroneTapsAreFaint()
    {
        var drone = Content.Designs[Content.DroneDesign].Flight;
        var pod = Content.Pod.Flight;
        byte Strength(float speed, FlightParams flight) => ShieldImpact.Strength(0.5f * flight.Mass * speed * speed, 0, flight.ShieldCapacity, flight.ShieldImpactEnergyFraction);
        Console.WriteLine($"Shield calibration: drone mass {drone.Mass}, capacity {drone.ShieldCapacity}, tap {Strength(0.3f, drone)}, ram {Strength(8, drone)}; pod mass {pod.Mass}, capacity {pod.ShieldCapacity}, ram {Strength(8, pod)}");
        Assert.InRange(Strength(0.3f, drone), (byte)1, (byte)12);
        Assert.Equal(1, Strength(0, drone));
        Assert.Equal(255, Strength(8, pod));
        var previous = (byte)0;
        for (var i = 0; i < 1000; i++)
        {
            var next = Strength(i * 0.1f, drone);
            Assert.True(next >= previous);
            previous = next;
        }
        Assert.Equal(ShieldImpact.Strength(20, 0, 100, 2), ShieldImpact.Strength(20, 10, 100, 2));
    }

    [Fact]
    public void GiantRockPatchFollowsItsSurfaceAndCoversTheTouchingSide()
    {
        var rock = new Asteroid { Radius = 10000, Rotation = Quaternion.Identity, Shape = 0 };
        var sphere = AsteroidMesh.Sphere(AsteroidMesh.Subdivisions);
        var point = (AsteroidShape.Point(rock.Shape, sphere.Points[sphere.Indices[0]])
            + AsteroidShape.Point(rock.Shape, sphere.Points[sphere.Indices[1]])
            + AsteroidShape.Point(rock.Shape, sphere.Points[sphere.Indices[2]])) * (rock.Radius / 3);
        var direction = Vector3.Normalize(point);
        var patch = ShieldPatch.Rock(rock, point, Quaternion.Identity, 5, 0.45f);
        Assert.InRange(patch.Normal.Length(), 0.999f, 1.001f);
        Assert.True(Vector3.Dot(patch.Normal, direction) > 0.8f);
        Assert.Equal(10f, patch.Extent);
        var probe = new AllocationProbe();
        while (probe.Measuring())
            _ = ShieldPatch.Rock(rock, point, Quaternion.Identity, 5, 0.45f);
        Assert.Equal(0, probe.Bytes);
        var tangent = Vector3.Normalize(Vector3.Cross(patch.Normal, Vector3.UnitX));
        Assert.InRange(MathF.Abs(ShieldPatch.Distance(rock, point + tangent) - ShieldPatch.Distance(rock, point - tangent)), 0, 0.05f);
    }

    [Fact]
    public void HullPatchNormalSurvivesPointQuantizationOntoTheFace()
    {
        Assert.Equal(Vector3.UnitY, ShieldPatch.BlockNormal(new Vector3(0, 1, 0), Vector3.One, BlockShape.Box));
        Assert.Equal(Vector3.UnitY, ShieldPatch.BlockNormal(new Vector3(0, 0.99f, 0), Vector3.One, BlockShape.Box));
        Assert.Equal(Vector3.UnitY, ShieldPatch.BlockNormal(new Vector3(0, 1.01f, 0), Vector3.One, BlockShape.Box));
    }

    private static HullDesign Box(int width) => HullDesign.Of(new BoxDesign("Shield contact", 1,
        [new(new(0, 0, 0), new(width, 2, 2), new BlockType
        {
            Id = "test", Name = "Shielded core", Core = true, Mass = 1, Hull = 10000, Shield = 10000
        })], Content.Blocks.Limits, "shield contact"), Content.Rules);

    [Theory]
    [InlineData(1, 0f, 2f)]
    [InlineData(4, 0.7f, 2f)]
    [InlineData(4, 0.7f, 20f)]
    public void BothHullsReportTheirFacingSurfaceIncludingRotatedSizedBoxes(int width, float angle, float speed)
    {
        var design = Box(width);
        using var store = new HullStore(2);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
        var direction = Vector3.Transform(Vector3.UnitX, rotation);
        var a = store.Create(1, 0, design, new Double3(direction.X, direction.Y, direction.Z) * (width - 0.05));
        var b = store.Create(2, 0, design, Double3.Zero);
        store.State(a.Index).Orientation = store.State(b.Index).Orientation = rotation;
        store.State(a.Index).Velocity = -direction * speed;
        var field = new HullField(store, [design]);
        field.BeginRound();
        field.Step(a.Index, default);
        foreach (var (index, sign) in new[] { (a.Index, -1), (b.Index, 1) })
        {
            var hit = store.State(index).ShieldHit;
            Assert.True(hit.Strength > 0);
            var point = hit.Point(design.Flight.Radius);
            var tolerance = design.Flight.Radius / 127f + 0.001f;
            Assert.InRange(point.X * sign, width * 0.5f - tolerance, width * 0.5f + tolerance);
            Assert.InRange(MathF.Abs(point.Y), 0, tolerance);
            Assert.InRange(MathF.Abs(point.Z), 0, tolerance);
            if (speed < design.Flight.CollisionSafeSpeed)
            {
                Assert.Equal(design.Flight.ShieldCapacity, store.State(index).Shield);
                Assert.Equal(0, store.State(index).ShieldCooldown);
            }
        }
    }

    [Theory]
    [InlineData(1, 0f)]
    [InlineData(4, 0.7f)]
    public void AsteroidHitUsesTheBoxSurfaceSample(int width, float angle)
    {
        var design = Box(width);
        var galaxy = Content.Galaxy.WithSeed(11);
        var rocks = new RockField(galaxy);
        var sector = rocks.Sector(default);
        var rock = sector.Asteroids.Take(sector.AsteroidCount).First(a => a.Radius >= 80f);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
        var outward = -Vector3.Transform(Vector3.UnitX, rotation);
        var radius = rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(outward, Quaternion.Conjugate(rock.Rotation)));
        var center = galaxy.CenterOf(default) + rock.LocalPosition;
        var state = HullState.Spawn(center + outward * (radius + width * 0.5f - 0.1f), design.Flight);
        state.Orientation = rotation;
        state.Velocity = -outward * 2;
        using var states = new BlockStateStore();
        var body = states.Body(design);
        var order = default(HullOrder);
        for (var tick = 0; tick < 30 && state.ShieldHit.Strength == 0; tick++)
            HullSim.Step(ref state, ref order, default, design.Flight, ref body, rocks, tick);
        Assert.True(state.ShieldHit.Strength > 0);
        var point = state.ShieldHit.Point(design.Flight.Radius);
        var tolerance = design.Flight.Radius / 127f + 0.001f;
        var half = design.BlockHalfExtents[0];
        Assert.True(MathF.Abs(MathF.Abs(point.X) - half.X) <= tolerance
            || MathF.Abs(MathF.Abs(point.Y) - half.Y) <= tolerance
            || MathF.Abs(MathF.Abs(point.Z) - half.Z) <= tolerance, $"hit {point} remained inside box {half}");
        Assert.True(point.X > 0, "the hit should face the asteroid in hull coordinates");
        Assert.Equal(design.Flight.ShieldCapacity, state.Shield);
        Assert.Equal(0, state.ShieldCooldown);
    }
}
