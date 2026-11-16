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
    private static PlumeInstance Plume() => new()
    {
        ExitRadius = new float4(0, 0, 0, 0.2f),
        AxisLength = new float4(0, 0, 1, 4),
        EdgeKind = new float4(1, 1, 1, 2),
        Shape = new float4(1, 0, 0, 0),
        Flow = new float4(-1, 10, 1, 1),
    };

    [Fact]
    public void FirstForeignShellStopsPlumeOwnAndMissesDoNotAndFrameAllocatesNothing()
    {
        ExhaustCapsule[] sources = [new(1, Vector3.Zero, Vector3.UnitZ, 4, 0.4f, 1, Vector3.One)];
        PlumeInstance[] plumes = [Plume()];
        ShieldDraw[] shields = [new() { Hull = 1, Center = Vector3.UnitZ, Radius = 2, IndexCount = 3 },
            new() { Hull = 2, Center = Vector3.UnitZ * 2, Radius = 2, IndexCount = 3 },
            new() { Hull = 3, Center = Vector3.UnitZ * 3, Radius = 2, IndexCount = 3 }];
        var instances = new ShieldInstance[3];
        for (var i = 0; i < instances.Length; i++) instances[i] = new ShieldInstance
        {
            Row0 = new float4(1, 0, 0, 0),
            Row1 = new float4(0, 1, 0, 0),
            Row2 = new float4(0, 0, 1, i + 1),
            Params = new float4(1, 0, 0, 0),
        };
        MeshVertex[] vertices = [new() { Position = new float3(-2, -2, 0), Normal = new float3(0, 0, -1) },
            new() { Position = new float3(2, -2, 0), Normal = new float3(0, 0, -1) },
            new() { Position = new float3(0, 2, 0), Normal = new float3(0, 0, -1) }];
        ushort[] indices = [0, 1, 2];
        var output = new float4[3 * ShieldExhaust.VectorsPerShield];
        void Frame() => ShieldExhaust.Apply(sources, plumes, shields, instances, vertices, indices, output, 1);
        Frame();
        Assert.Equal(new float4(0, 0, -1, 2), plumes[0].ShieldClip);
        Assert.Equal(0, output[0].X);
        Assert.Equal(1, output[ShieldExhaust.VectorsPerShield].X);
        Assert.Equal(0, output[2 * ShieldExhaust.VectorsPerShield].X);
        Assert.Equal(0.45f, output[ShieldExhaust.VectorsPerShield + 1].W, 5); // radius at contact, spread 1.5
        var allocation = new AllocationProbe();
        while (allocation.Measuring()) Frame();
        Assert.Equal(0, allocation.Bytes);
        sources[0] = sources[0] with { Length = 0.5f };
        Frame();
        Assert.Equal(default, plumes[0].ShieldClip);
        sources[0] = sources[0] with { Length = 4, Start = new Vector3(30, 0, 0) };
        Frame();
        Assert.Equal(default, plumes[0].ShieldClip);
        sources[0] = sources[0] with { Start = new Vector3(1.9f, 1.9f, 0) };
        Frame(); // inside sphere, outside the actual triangle
        Assert.Equal(default, plumes[0].ShieldClip);
        sources[0] = sources[0] with { Start = Vector3.Zero, Intensity = 0 };
        Frame();
        Assert.Equal(default, plumes[0].ShieldClip);
    }

    [Fact]
    public void RotatedStretchedShellUsesTheSameTransformAsDrawing()
    {
        ExhaustCapsule[] sources = [new(1, Vector3.Zero, Vector3.UnitX, 4, 0.4f, 1, Vector3.One)];
        PlumeInstance[] plumes = [Plume()];
        ShieldDraw[] shields = [new() { Hull = 2, Center = Vector3.UnitX * 2, Radius = 3, IndexCount = 3 }];
        ShieldInstance[] instances = [new()
        {
            Row0 = new float4(0, 0, 2, 2),
            Row1 = new float4(0, 1, 0, 0),
            Row2 = new float4(-1, 0, 0, 0),
            Params = new float4(1, 0, 0, 0),
        }];
        MeshVertex[] vertices = [new() { Position = new float3(-2, -2, 0), Normal = new float3(0, 0, -1) },
            new() { Position = new float3(2, -2, 0), Normal = new float3(0, 0, -1) },
            new() { Position = new float3(0, 2, 0), Normal = new float3(0, 0, -1) }];
        ushort[] indices = [0, 1, 2];
        var output = new float4[ShieldExhaust.VectorsPerShield];
        ShieldExhaust.Apply(sources, plumes, shields, instances, vertices, indices, output, 0);
        Assert.Equal(new float4(-1, 0, 0, 2), plumes[0].ShieldClip);
        Assert.Equal(2, output[1].X);
        instances[0].Params.X = 0;
        ShieldExhaust.Apply(sources, plumes, shields, instances, vertices, indices, output, 0);
        Assert.Equal(default, plumes[0].ShieldClip);
        Assert.Equal(0, output[0].X);
    }

    [Fact]
    public void EightNearestContactsRemainAndThePlumeProfileFadesAtItsRealEnd()
    {
        var distances = new float[8];
        var output = new float4[ShieldExhaust.VectorsPerShield];
        for (var i = 12; i > 0; i--) ShieldExhaust.Insert(0, i, new Vector3(i, 0, 0), Vector3.UnitZ, 1, 1, Vector3.One, distances, output);
        Assert.Equal(8, output[0].X);
        for (var i = 0; i < 8; i++) Assert.Equal(i + 1, output[1 + i * 3].X);
        var plume = Plume();
        Assert.Equal(0.3f, ShieldExhaust.RadiusAt(plume, 2), 5);
        Assert.Equal(0.25f, ShieldExhaust.WeightAt(plume, 2), 5);
        Assert.Equal(0, ShieldExhaust.WeightAt(plume, 4));
        Assert.Equal(0, ShieldExhaust.WeightAt(plume, 5));
        plume.Flow.X = 0.3f; // the last gas has cleared the first three metres
        Assert.Equal(0, ShieldExhaust.WeightAt(plume, 2));
    }

    [Fact]
    public void GroupBoundsHandleParallelRaysAndTriangleEdges()
    {
        var bounds = new ExhaustBounds(new Vector3(-2, -2, 2), new Vector3(2, 2, 2));
        Assert.True(ShieldExhaust.Meets(bounds, Vector3.Zero, Vector3.UnitZ, 4));
        Assert.True(ShieldExhaust.Meets(bounds, new Vector3(2, 0, 0), Vector3.UnitZ, 2));
        Assert.False(ShieldExhaust.Meets(bounds, new Vector3(3, 0, 0), Vector3.UnitZ, 4));
        Assert.False(ShieldExhaust.Meets(bounds, Vector3.Zero, Vector3.UnitZ, 1));
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
        Assert.Equal(output[0].AxisLength.W, full.Length);
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
    [InlineData("ShieldSpread", 0.9f)]
    [InlineData("ShieldSpread", 4.1f)]
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
            Assert.True(plume.ShieldStrength > 0);
            var old = plume.ShieldColor[0];
            plume.ShieldColor[0] = float.NaN;
            Assert.Throws<InvalidDataException>(() => plume.Check("test", "bad"));
            plume.ShieldColor[0] = old;
        }
        Assert.True(look.Plumes["ion"].ShieldStrength < look.Plumes["engine"].ShieldStrength);
    }
}
