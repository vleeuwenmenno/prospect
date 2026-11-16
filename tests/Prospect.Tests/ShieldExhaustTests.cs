using System.Numerics;
using System.Text.Json;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class ShieldExhaustTests
{
    private static ExhaustCapsule Plume(uint hull, float distance) => new(hull, new Vector3(distance, 0, -1), Vector3.UnitZ, 4, 1, 1, Vector3.One);

    [Fact]
    public void NearestEightExcludeOwnAndRejectOutsideSphere()
    {
        var plumes = new ExhaustCapsule[12];
        for (var i = 0; i < 10; i++) plumes[i] = Plume(2, 10 - i);
        plumes[10] = Plume(1, 0);
        plumes[11] = Plume(2, 100);
        var output = new float4[ShieldExhaust.VectorsPerShield];
        Assert.Equal(8, ShieldExhaust.Select(1, Vector3.Zero, 10, plumes, output));
        for (var i = 0; i < 8; i++) Assert.Equal(i + 1, output[1 + i * 3].X);
        Assert.Equal(0, ShieldExhaust.Select(1, Vector3.Zero, 0.1f, plumes.AsSpan(10), output));
        Assert.Equal(0, output[0].X);
        var allocation = new AllocationProbe();
        while (allocation.Measuring()) ShieldExhaust.Select(1, Vector3.Zero, 10, plumes, output);
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void EnvelopeFadesAlongAndAcrossTheStreamAndScalesLinearly()
    {
        var plume = Plume(2, 0);
        var near = plume.StrengthAt(Vector3.Zero);
        Assert.True(near > plume.StrengthAt(Vector3.UnitZ));
        Assert.True(near > plume.StrengthAt(Vector3.UnitX * 0.5f));
        Assert.Equal(near * 0.25f, (plume with { Intensity = 0.25f }).StrengthAt(Vector3.Zero));
        Assert.Equal(0, plume.StrengthAt(Vector3.UnitX * 2));
        Assert.Equal(0, plume.StrengthAt(Vector3.UnitZ * 4));
        Assert.Equal(0, plume.StrengthAt(-Vector3.UnitZ * 2));
    }

    [Theory]
    [InlineData("engine", 4)]
    [InlineData("hot", 2)]
    [InlineData("cold", 1)]
    [InlineData("ion", 16)]
    public void CapsulesReuseDrawnGeometryAndScaleWithReplicatedThrottle(string kind, byte flags)
    {
        var look = PropulsionLook.Load();
        var effects = new ThrustEffects(look);
        var nozzle = new ThrustNozzle(0, kind switch { "engine" => FuelKind.Bipropellant, "hot" => FuelKind.Hydrazine, "cold" => FuelKind.Nitrogen, _ => FuelKind.None }, BlockFacing.Back, Vector3.Zero, 0.4f, 0.2f, look.PlumeIndex(kind), 0);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var hull = new HullView { Id = 7, Blocks = new ulong[] { 1 }, Thrust = new ThrustState { Z = -127, Flags = flags } };
        effects.Begin(10, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, 0.6f, 1.6f, 1080);
        effects.Add(effects.Observe(hull), hull, [nozzle], new Vector3(0, 0, -30), Quaternion.Identity, 1, output);
        var full = effects.Capsules[0];
        Assert.Equal(output[0].AxisLength.W * look.Plumes[kind].ShieldReach, full.Length);
        Assert.Equal(output[0].ExitRadius.W * (1 + output[0].Shape.X), full.Radius);
        Assert.Equal(look.Plumes[kind].ShieldStrength, full.Intensity);
        hull.Thrust.Z = -64;
        effects.Begin(11, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, 0.6f, 1.6f, 1080);
        effects.Add(effects.Observe(hull), hull, [nozzle], new Vector3(0, 0, -30), Quaternion.Identity, 1, output);
        Assert.Equal(full.Intensity * (64f / 127), effects.Capsules[0].Intensity, 5);
    }

    [Theory]
    [InlineData("ShieldStrength", -0.1f)]
    [InlineData("ShieldStrength", 4.1f)]
    [InlineData("ShieldReach", 0f)]
    [InlineData("ShieldReach", 1.1f)]
    public void EveryKindRejectsInvalidReactionRanges(string property, float value)
    {
        foreach (var plume in PropulsionLook.Load().PlumeOrder)
        {
            var json = JsonSerializer.SerializeToNode(plume)!;
            json[property] = value;
            var invalid = json.Deserialize<PlumeLook>()!;
            Assert.Throws<InvalidDataException>(() => invalid.Check("test", "invalid"));
        }
    }

    [Fact]
    public void EveryKindValidatesItsShieldData()
    {
        var look = PropulsionLook.Load();
        foreach (var plume in look.PlumeOrder)
        {
            plume.Check("test", "valid");
            Assert.True(plume.ShieldReach > 0);
            Assert.True(plume.ShieldStrength > 0);
            var old = plume.ShieldColor[0];
            plume.ShieldColor[0] = float.NaN;
            Assert.Throws<InvalidDataException>(() => plume.Check("test", "bad"));
            plume.ShieldColor[0] = old;
        }
        Assert.True(look.Plumes["ion"].ShieldStrength < look.Plumes["engine"].ShieldStrength);
    }
}
