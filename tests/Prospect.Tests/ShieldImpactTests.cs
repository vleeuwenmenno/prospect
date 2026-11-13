using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Visual hit locations follow actual collision surfaces without changing collision damage.</summary>
public sealed class ShieldImpactTests
{
    private static readonly GameContent Content = GameContent.Load();
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
