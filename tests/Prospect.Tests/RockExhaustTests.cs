using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class RockExhaustTests
{
    [Fact]
    public void RaysHitSharedFacetsAndRespectReach()
    {
        var surface = new RockExhaustSurface();
        for (var shape = 0; shape <= Galaxy.RockShapes; shape++)
        {
            var direction = Vector3.Normalize(new Vector3(0.2f, 0.4f, 1));
            var radius = AsteroidShape.RadiusAlong(shape, direction);
            Assert.True(surface.Hit(shape, direction * 2, -direction, 3, out var distance, out var normal));
            Assert.InRange(Math.Abs(distance - (2 - radius)), 0, 0.00001f);
            Assert.True(float.IsFinite(normal.X));
            Assert.InRange(Vector3.Dot(normal, direction), 0.01f, 1.001f);
            Assert.False(surface.Hit(shape, direction * 2, -direction, distance - 0.01f, out _, out _));
            Assert.False(surface.Hit(shape, direction * 2, direction, 3, out _, out _));
        }
    }

    [Fact]
    public void NearbyMarksMergeAndOldestIsReplaced()
    {
        var effects = new RockExhaust();
        int Heat(int rock, Vector3 point) => effects.Heat(default, rock, 0, point, Vector3.UnitY, point, Vector3.UnitY, 1, 1);
        effects.Begin(1, default);
        Assert.Equal(0, Heat(0, Vector3.Zero));
        effects.Begin(2, default);
        Assert.Equal(0, Heat(0, Vector3.UnitX * 0.1f));
        for (var i = 1; i < RockExhaust.Marks; i++)
        {
            effects.Begin(2 + i, default);
            Assert.Equal(i, Heat(i, Vector3.Zero));
        }
        effects.Begin(100, default);
        Assert.Equal(0, Heat(100, Vector3.Zero));
    }

    [Fact]
    public void ActiveDustAndMarksAllocateNothingAndStayFinite()
    {
        var look = PropulsionLook.Shared;
        var thrust = new ThrustEffects(look);
        var effects = new RockExhaust();
        var hull = new HullView { Id = 4, Thrust = new ThrustState { Z = -127, Flags = 4 }, Blocks = new[] { ulong.MaxValue } };
        ThrustNozzle[] nozzles = [new(0, FuelKind.Bipropellant, BlockFacing.Back, Vector3.Zero, 0.3f, 0.3f, look.PlumeIndex("engine"), 0)];
        double clock = 1;
        var count = 0;
        void Frame()
        {
            clock += 0.016;
            thrust.Begin(clock, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 0.6f, 1.6f, 1080);
            var slot = thrust.Observe(hull);
            thrust.Add(slot, hull, nozzles, new Vector3(0, 0, 3), Quaternion.Identity, 1, thrust.Instances);
            thrust.End();
            effects.Begin(clock, default);
            effects.Offer(default, 1, 0, new Vector3(0, 0, 9), Quaternion.Identity, 5, new Vector3(0.3f), thrust);
            effects.Offer(default, 0, 0, new Vector3(0, 0, 8), Quaternion.Identity, 5, new Vector3(0.3f), thrust);
            count = effects.End(thrust, Vector3.UnitX, Vector3.UnitY);
        }
        for (var i = 0; i < 200; i++) Frame();
        Assert.True(count > 0);
        Assert.True(effects.MarkData[0].Glow.X > 0);
        var surfaceZ = 8 - 5 * AsteroidShape.RadiusAlong(0, -Vector3.UnitZ);
        Assert.InRange(Math.Abs(effects.MarkData[0].CenterRadius.Z - surfaceZ), 0, 0.03f);
        var probe = new AllocationProbe();
        while (probe.Measuring()) Frame();
        Assert.Equal(0, probe.Bytes);
        for (var i = 0; i < count; i++) Assert.True(float.IsFinite(effects.Instances[i].PositionSize.X));
    }

    [Fact]
    public void MarkFollowsRockRotationAndTranslationThenExpires()
    {
        var effects = new RockExhaust();
        var thrust = new ThrustEffects(PropulsionLook.Shared);
        var kind = PropulsionLook.Shared.PlumeIndex("engine");
        effects.Begin(1, default);
        effects.Heat(default, 0, kind, Vector3.UnitX, Vector3.UnitY, Vector3.UnitX, Vector3.UnitY, 1, 1);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        effects.Begin(2, default);
        effects.Offer(default, 0, 0, new Vector3(10, 0, 0), rotation, 5, Vector3.One, thrust);
        Assert.Equal(0, effects.End(thrust, Vector3.UnitX, Vector3.UnitY));
        Assert.InRange(effects.MarkData[0].CenterRadius.X, 9.98f, 10);
        Assert.InRange(effects.MarkData[0].CenterRadius.Y, 0.999f, 1.001f);
        effects.Begin(32, default);
        Assert.Equal(0, effects.End(thrust, Vector3.UnitX, Vector3.UnitY));
    }

    [Fact]
    public void FadingPlumeCannotEraseStoredMarkAndCoolingLeavesSoot()
    {
        var effects = new RockExhaust();
        var thrust = new ThrustEffects(PropulsionLook.Shared);
        var kind = PropulsionLook.Shared.PlumeIndex("engine");
        effects.Begin(1, default);
        effects.Heat(default, 0, kind, Vector3.Zero, Vector3.UnitY, Vector3.Zero, Vector3.UnitY, 1, 1);
        effects.Begin(2, default);
        effects.Heat(default, 0, kind, Vector3.Zero, Vector3.UnitY, Vector3.Zero, Vector3.UnitY, 1, 0.0001f);
        void At(double time)
        {
            effects.Begin(time, default);
            effects.Offer(default, 0, 0, Vector3.Zero, Quaternion.Identity, 10, Vector3.One, thrust, 7);
            effects.End(thrust, Vector3.UnitX, Vector3.UnitY);
        }
        At(5);
        Assert.True(effects.MarkData[0].Glow.X > 0.5f);
        Assert.Equal(7, effects.MarkDraws[0]);
        At(22);
        Assert.True(effects.MarkData[0].NormalScorch.W > 0.5f);
        Assert.Equal(0, effects.MarkData[0].Glow.X);
        At(32);
        Assert.Equal(-1, effects.MarkDraws[0]);
    }

    [Fact]
    public void AllMarkMaskBitsStayExactAndFinite()
    {
        var mask = new Brutal.Numerics.float4();
        for (var i = 0; i < RockExhaust.Marks; i++) RockExhaust.Include(ref mask, i);
        Assert.Equal(65535, mask.X);
        Assert.Equal(65535, mask.Y);
        Assert.Equal(65535, mask.Z);
        Assert.Equal(65535, mask.W);
    }

    [Fact]
    public void ALaserCutGlowsWhiteThenRedAndItsCracksOutlastTheGlow()
    {
        var look = PropulsionLook.Shared.Laser!.Cut;
        var effects = new RockExhaust();
        var thrust = new ThrustEffects(PropulsionLook.Shared);
        effects.Begin(1, default);
        effects.HeatCut(default, 0, Vector3.UnitX, Vector3.UnitY, Vector3.UnitX, Vector3.UnitY, 1.6f, 0.375f);
        void At(double time)
        {
            effects.Begin(time, default);
            effects.Offer(default, 0, 0, Vector3.Zero, Quaternion.Identity, 10, Vector3.One, thrust, 3);
            effects.End(thrust, Vector3.UnitX, Vector3.UnitY);
        }

        // Fresh: a white-hot glow, its fractures at their strongest, and the pattern seed carried to the shader.
        At(1.1);
        ref readonly var fresh = ref effects.MarkData[0];
        Assert.Equal(3, effects.MarkDraws[0]);
        Assert.True(fresh.Glow.Y > 0.6f * fresh.Glow.X, "fresh rock glows white-hot, its green near its red");
        Assert.InRange(fresh.Cut.X, look.CrackAlpha - 0.001f, look.CrackAlpha + 0.001f);
        Assert.Equal(1.6f * look.CrackReach, fresh.Cut.Z, 3);
        Assert.InRange(fresh.Cut.W, 0f, 1f);

        // Halfway through its cooling it is redder than it is white, and dimmer.
        At(1.1 + look.HotSeconds * 0.7);
        Assert.True(fresh.Glow.Y < 0.25f * fresh.Glow.X, "cooled rock glows dull red, its green well under its red");
        Assert.True(fresh.Glow.Z < 0.1f * fresh.Glow.X);
        Assert.True(fresh.Glow.X < 1f);
        Assert.Equal(0, fresh.NormalScorch.W);

        // Its fractures are held, then fade, and outlast the glow: the last of a cut is a dark, cold scar.
        At(1.1 + look.CrackSeconds * 0.5);
        Assert.Equal(0, fresh.Glow.X);
        Assert.True(fresh.Cut.X > 0.99f * look.CrackAlpha);
        At(1.1 + look.CrackSeconds);
        Assert.Equal(-1, effects.MarkDraws[0]);
    }

    [Fact]
    public void ABrokenChunkThrowsDustAndDebrisOutOfTheFixedPool()
    {
        var look = PropulsionLook.Shared.Laser!.Cut;
        var effects = new RockExhaust();
        var thrust = new ThrustEffects(PropulsionLook.Shared);
        effects.Begin(1, default);
        effects.Burst(new Double3(10, 20, 30), new Vector3(2, 0, 0), 0.28f, new Vector3(0.3f));
        var count = effects.End(thrust, Vector3.UnitX, Vector3.UnitY);
        Assert.Equal(look.BurstParticles + look.DebrisParticles, count);
        for (var i = 0; i < count; i++)
        {
            Assert.True(float.IsFinite(effects.Instances[i].PositionSize.X));
            Assert.True(effects.Instances[i].Color.W > 0);
            Assert.InRange(effects.Instances[i].PositionSize.X, 9.5f, 10.5f);
        }

        // Every burst takes the fixed pool round again, and none of it is memory.
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            effects.Begin(2, default);
            effects.Burst(new Double3(10, 20, 30), Vector3.Zero, 0.28f, new Vector3(0.3f));
            effects.End(thrust, Vector3.UnitX, Vector3.UnitY);
        }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void OnlyChunksTheNewestSetBroughtInAreNew()
    {
        var visuals = new OreChunkVisuals();
        var chunk = new OreChunk { Id = 7, Mass = 533, Grade = 0.75f, Resource = 0, Orientation = Quaternion.Identity };
        Assert.True(visuals.Update([chunk], 6));
        Assert.Equal(0, visuals.NewCount);

        // The same chunk, moved: nothing broke off.
        chunk.Position = new Double3(5, 0, 0);
        Assert.True(visuals.Update([chunk], 12));
        Assert.Equal(0, visuals.NewCount);

        // A second one beside it: one new chunk, at its place in the newest set.
        var second = chunk;
        second.Id = 8;
        Assert.True(visuals.Update([chunk, second], 18));
        Assert.Equal(1, visuals.NewCount);
        Assert.Equal(1, visuals.NewAt(0));
        visuals.ConsumeNew();
        Assert.Equal(0, visuals.NewCount);
        Assert.True(visuals.Update([chunk], 24));
        Assert.Equal(0, visuals.NewCount);
    }

    [Fact]
    public void KindRulesRejectHeatInColdGasAndDustInIon()
    {
        foreach (var plume in PropulsionLook.Shared.PlumeOrder) plume.Rock.Check(plume.Kind);
        Assert.Throws<InvalidDataException>(() => new RockExhaustLook { DustRate = float.NaN }.Check("flame"));
        Assert.Throws<InvalidDataException>(() => PropulsionLook.Shared.Plumes["engine"].Rock.Check("puff"));
        Assert.Throws<InvalidDataException>(() => PropulsionLook.Shared.Plumes["cold"].Rock.Check("ion"));

        // A laser's cut look is checked on its own figures, and the shipped one passes.
        PropulsionLook.Shared.Laser!.Cut.Check("cut");
        Assert.Throws<InvalidDataException>(() => new LaserCutLook { CrackReach = 0.5f }.Check("cut"));
        Assert.Throws<InvalidDataException>(() => new LaserCutLook { HotShare = 1f }.Check("cut"));
        Assert.Throws<InvalidDataException>(() => new LaserCutLook { CrackHoldSeconds = 99f }.Check("cut"));
        Assert.Throws<InvalidDataException>(() => new LaserCutLook { BurstSeconds = 0f }.Check("cut"));
    }
}
