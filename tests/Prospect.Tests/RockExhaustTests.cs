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
    public void KindRulesRejectHeatInColdGasAndDustInIon()
    {
        foreach (var plume in PropulsionLook.Shared.PlumeOrder) plume.Rock.Check(plume.Kind);
        Assert.Throws<InvalidDataException>(() => new RockExhaustLook { DustRate = float.NaN }.Check("flame"));
        Assert.Throws<InvalidDataException>(() => PropulsionLook.Shared.Plumes["engine"].Rock.Check("puff"));
        Assert.Throws<InvalidDataException>(() => PropulsionLook.Shared.Plumes["cold"].Rock.Check("ion"));
    }
}
