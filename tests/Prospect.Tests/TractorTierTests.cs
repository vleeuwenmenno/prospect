using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class TractorTierTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Theory]
    [InlineData("Common", ModuleQuality.Common, 40f, 4f, 100000f, 1f)]
    [InlineData("Uncommon", ModuleQuality.Uncommon, 48f, 5f, 150000f, 1.1f)]
    [InlineData("Rare", ModuleQuality.Rare, 56f, 6f, 250000f, 1.2f)]
    [InlineData("Epic", ModuleQuality.Epic, 68f, 8f, 400000f, 1.35f)]
    [InlineData("Legendary", ModuleQuality.Legendary, 80f, 10f, 600000f, 1.5f)]
    [InlineData("Alien Tech", ModuleQuality.AlienTech, 100f, 14f, 1000000f, 1.7f)]
    public void EveryTierFeedsSharedReachPullPowerAndPreview(string name, ModuleQuality quality, float range, float pull, float mass, float power)
    {
        // Only the Common tractor ships, as only Common sensors do; the other tiers are the same block at another quality.
        using var copy = new ContentCopy();
        var root = JsonNode.Parse(copy.Read("blocks/blocks.json"))!;
        root["blocks"]!["tractorBeam"]!["tractor"]!["quality"] = name;
        copy.Write("blocks/blocks.json", root.ToJsonString());
        var content = copy.Load();
        var type = content.Blocks.Get("tractorBeam"); var spec = type.Tractor!;
        Assert.Equal(quality, spec.Quality);
        Assert.Equal(range, spec.Range, 4); Assert.Equal(pull, spec.Acceleration, 4);
        Assert.Equal(mass, spec.MaxMass, 2);
        Assert.Equal(2 * power, spec.BasePower, 4); Assert.Equal(.5f * power, spec.PowerPerTonne, 4);
        Assert.Equal(3 * power, spec.Power(2000), 4);
        Assert.NotNull(PropulsionModels.ModelOf(type, BlockShape.Box, PropulsionLook.Load()));
        DesignBox[] boxes = [new(new(0, 0, 0), new(1, 1, 1), content.Blocks.Get("core")),
            new(new(2, 1, 0), new(1, 1, 1), content.Blocks.Get("cargo"), BlockShape.Box, BlockFacing.Left)];
        DesignMount[] mounts = [new(0, BlockFacing.Up, 2, 2, type)];
        var emitter = TractorReach.Emitter(boxes, mounts[0], 1);
        Assert.True(TractorReach.Container(boxes, [], 1, mounts[0], emitter + Vector3.UnitY * (range - .01f)));
        Assert.False(TractorReach.Container(boxes, [], 1, mounts[0], emitter + Vector3.UnitY * (range + .01f)));
        var design = TestDesigns.Mounted(boxes, mounts);
        using var hulls = new HullStore();
        var hull = hulls.Create(1, 0, design, Double3.Zero);
        var field = new HullField(hulls, [design]);
        var bodies = new FreeContainers();
        Assert.True(bodies.Add(content, new FreeContainer
        {
            Position = Double3.Zero + emitter + Vector3.UnitY * (range - 1) - (design.CenterOfMass + new Vector3(.5f)),
            Orientation = Quaternion.Identity,
            Resource = -1,
            Hull = 50
        }, out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        tractor.Plan(bodies, content, field, scratch, 0);
        Assert.Equal(hull.Packed, bodies.Items[0].TractorHull);
        var demand = spec.Power(content.Cargo.SpaceShellKg);
        Assert.Equal(demand, hulls.Info(hull.Index).TractorDemand);
        var flight = design.Flight; flight.EnergyStorage = 100; flight.IdlePower = 0; flight.PowerGenerated = 0; flight.TractorPower = demand;
        var state = HullState.Spawn(Double3.Zero, flight); state.Energy = 100;
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out var flow);
        Assert.Equal(demand, flow.Tractor);
        Assert.Equal(100 - demand * HullSim.Dt, state.Energy, 4);
        hulls.Info(hull.Index).Power = flow;
        tractor.Pull(bodies, content, field, scratch);
        Assert.Equal(pull * HullSim.Dt, bodies.Items[0].Velocity.Length(), 4);

        boxes[1] = new(new((int)range - 1, 1, 0), new(1, 1, 1), content.Blocks.Get("cargo"), BlockShape.Box, BlockFacing.Left);
        var draft = BuildDraft.FromBlueprint(TestDesigns.Mounted(boxes, mounts), content);
        var preview = new TractorPreview(1); preview.Update(draft, 1);
        Assert.True(preview.Complete); Assert.True(preview.Linked(0, 1));
        boxes[1] = new(new((int)range + 2, 1, 0), new(1, 1, 1), content.Blocks.Get("cargo"), BlockShape.Box, BlockFacing.Left);
        draft = BuildDraft.FromBlueprint(TestDesigns.Mounted(boxes, mounts), content);
        preview.Update(draft, 1);
        Assert.False(preview.Linked(0, 1));
    }

    [Theory]
    [InlineData("range", "0")]
    [InlineData("acceleration", "-1")]
    [InlineData("maxMass", "1e100")]
    [InlineData("power", "0")]
    [InlineData("range", "1000")]
    public void InvalidTierMultipliersRefuseContent(string key, string value)
    {
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.Content("blocks", "blocks.json")))!;
        root["tractorTiers"]!["Common"]![key] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => BlockCatalog.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()), "test"));
    }

    [Fact]
    public void UnknownQualityIsRefusedAndTierRetunesChangeBothIdentities()
    {
        using var copy = new ContentCopy();
        var root = JsonNode.Parse(copy.Read("blocks/blocks.json"))!;
        root["blocks"]!["tractorBeam"]!["tractor"]!["quality"] = "Unknown";
        Assert.Throws<InvalidDataException>(() => BlockCatalog.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()), "test"));
        root["blocks"]!["tractorBeam"]!["tractor"]!["quality"] = "Common";
        root["tractorTiers"]!["Common"]!["power"] = 2;
        copy.Write("blocks/blocks.json", root.ToJsonString());
        var changed = copy.Load();
        Assert.NotEqual(Content.Identity, changed.Identity);
        Assert.NotEqual(Content.SaveIdentity, changed.SaveIdentity);
    }
}
