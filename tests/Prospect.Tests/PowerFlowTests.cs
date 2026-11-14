using System.Text.Json.Nodes;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The power flow the pilot's readout shows is the simulation's own split of a tick, and the shipped designs spend
/// enough on shield recovery for it to matter: a collapsed shield's restart outruns the generators and empties
/// the battery, and an ordinary top-up takes a visible share of what they make.
/// </summary>
public sealed class PowerFlowTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static FlightParams Of(int design) => Content.Designs[design].Flight;

    /// <summary>The designs a player flies in the shipped game, by name.</summary>
    public static TheoryData<string> Designs => new() { "pod", "mining", "survey", "drone" };

    private static FlightParams Shipped(string name) => Of(name switch
    {
        "pod" => Content.PodDesign,
        "mining" => Content.StartingFleet[0],
        "survey" => Content.StartingFleet[1],
        _ => Content.DroneDesign,
    });

    [Fact]
    public void TheFlowSumsToTheBatterysChangeWhileTheBatteryCanTakeIt()
    {
        var flight = Of(Content.PodDesign);
        var inputs = new[]
        {
            default(FlightInput),
            new FlightInput { Forward = 127 },
            new FlightInput { Forward = 127, Boost = true },
            new FlightInput { Strafe = -60, Vertical = 127, Dampeners = true },
        };
        foreach (var offline in new[] { false, true })
        {
            foreach (var input in inputs)
            {
                var state = HullState.Spawn(Double3.Zero, flight);
                state.Energy = flight.EnergyStorage * 0.5f;
                state.Shield = flight.ShieldCapacity * 0.5f;
                state.ShieldOffline = offline;
                state.Shield = offline ? flight.ShieldCapacity * 0.1f : state.Shield;
                var before = state.Energy;
                FlightModel.Step(ref state, input, flight, HullSim.Dt, out var flow);
                Assert.Equal(flow.Net * HullSim.Dt, state.Energy - before, 4);
                Assert.Equal(flight.PowerGenerated, flow.Generated);
                Assert.Equal(flight.IdlePower, flow.Idle);
            }
        }
    }

    [Fact]
    public void ARechargingShieldIsInTheFlowEvenWhenAFullBatteryHidesIt()
    {
        var flight = Of(Content.PodDesign);
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = flight.ShieldCapacity * 0.7f;
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out var flow);

        // The battery did not move, which is all the old readout could say.
        Assert.Equal(flight.EnergyStorage, state.Energy);
        Assert.Equal(flight.ShieldPower, flow.Shield, 4);
        Assert.Equal(flight.PowerGenerated - flight.IdlePower - flight.ShieldPower, flow.Net, 4);
        Assert.True(flow.Net > 0f);

        // Full, the shield draws nothing.
        state.Shield = flight.ShieldCapacity;
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out flow);
        Assert.Equal(0f, flow.Shield);
        Assert.Equal(flight.PowerGenerated - flight.IdlePower, flow.Net, 4);
    }

    [Fact]
    public void ABrokenShieldDrawsRestartPowerAndTheFlowTurnsBackToOrdinaryAtItsThreshold()
    {
        var flight = Of(Content.PodDesign);
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 0f;
        state.ShieldOffline = true;
        state.ShieldCooldown = 0f;
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out var flow);
        Assert.Equal(flight.ShieldRestartPower, flow.Shield, 3);
        Assert.True(flow.Net < flight.PowerGenerated - flight.IdlePower);

        for (var i = 0; state.ShieldOffline && i < 3600; i++)
            FlightModel.Step(ref state, default, flight, HullSim.Dt, out flow);
        Assert.False(state.ShieldOffline);
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out flow);
        Assert.Equal(flight.ShieldPower, flow.Shield, 3);
    }

    [Fact]
    public void ThrustAndBoostAreInTheFlowAsSuppliedAndCutBackWhenThePowerRunsOut()
    {
        var flight = Of(Content.PodDesign);
        var state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, new FlightInput { Forward = 127 }, flight, HullSim.Dt, out var flow);
        Assert.Equal(flight.ThrustPower, flow.Thrust, 4);

        state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, new FlightInput { Forward = 127, Boost = true }, flight, HullSim.Dt, out flow);
        Assert.Equal(flight.ThrustPower * flight.BoostPowerMultiplier, flow.Thrust, 4);

        // A flat battery leaves only what the generator makes beyond idle for thrust, and the flow says so.
        flight.PowerGenerated = flight.IdlePower + 2f;
        state = HullState.Spawn(Double3.Zero, flight);
        state.Energy = 0f;
        FlightModel.Step(ref state, new FlightInput { Forward = 127 }, flight, HullSim.Dt, out flow);
        Assert.Equal(2f, flow.Thrust, 3);
        Assert.Equal(0f, flow.Net, 3);
    }

    [Fact]
    public void TheJumpDrivesChargeIsInTheFlowAndNothingFlowsInTheTunnel()
    {
        var flight = Of(Content.PodDesign);
        flight.FtlPower = 7f;
        flight.FtlEnergy = 70f;
        flight.FtlCalculateSeconds = 0f;
        var state = HullState.Spawn(Double3.Zero, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };
        var body = default(HullBody);
        var drew = false;
        for (var i = 0; i < 2000 && state.Jump != JumpPhase.Tunnel; i++)
        {
            HullSim.Step(ref state, ref order, default, flight, ref body);
            if (state.Jump == JumpPhase.Charge)
            {
                drew = true;
                Assert.Equal(flight.FtlPower, body.Power.Ftl, 3);
                Assert.Equal(body.Power.Generated - body.Power.Idle - body.Power.Shield - body.Power.Thrust - flight.FtlPower, body.Power.Net, 3);
            }
        }

        Assert.True(drew);
        Assert.Equal(JumpPhase.Tunnel, state.Jump);
        HullSim.Step(ref state, ref order, default, flight, ref body);
        Assert.Equal(0f, body.Power.Generated);
        Assert.Equal(0f, body.Power.Net);
    }

    [Fact]
    public void ABlendMovesEachFigureAShareOfTheWayAndNeverOvershoots()
    {
        var shown = default(PowerFlow);
        var sample = new PowerFlow { Generated = 6f, Idle = 0.7f, Thrust = 4f, Shield = 3f, Ftl = 1f };
        for (var i = 0; i < 80; i++)
        {
            shown.Blend(sample, 0.2f);
            Assert.True(shown.Generated <= 6f && shown.Shield <= 3f);
        }

        Assert.Equal(sample.Net, shown.Net, 3);
        Assert.Equal(6f - 0.7f - 4f - 3f - 1f, shown.Net, 3);
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public void ARestartOutrunsTheGeneratorsSurplusOnEveryShippedDesign(string name)
    {
        var flight = Shipped(name);
        Assert.True(flight.ShieldRestartPower > flight.PowerGenerated - flight.IdlePower, $"{name}: restart {flight.ShieldRestartPower} MW against surplus {flight.PowerGenerated - flight.IdlePower} MW");
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public void OrdinaryRechargeTakesAVisibleShareOfTheSurplusOnEveryShippedDesign(string name)
    {
        var flight = Shipped(name);
        var share = flight.ShieldPower / (flight.PowerGenerated - flight.IdlePower);
        Assert.InRange(share, 0.2f, 0.7f);
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public void RestartingAtRestDrainsTheBatteryOfEveryShippedDesign(string name)
    {
        var flight = Shipped(name);
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 0f;
        state.ShieldOffline = true;
        state.ShieldCooldown = flight.ShieldDelay;
        var lowest = state.Energy;
        var restartTicks = 0;
        for (var i = 0; i < 60 * 240 && state.ShieldOffline; i++)
        {
            FlightModel.Step(ref state, default, flight, HullSim.Dt);
            if (state.ShieldCooldown <= 0f)
            {
                lowest = Math.Min(lowest, state.Energy);
                restartTicks++;
            }
        }

        Assert.False(state.ShieldOffline);
        Assert.True(lowest <= flight.EnergyStorage * 0.7f, $"{name}: the battery only fell to {lowest} of {flight.EnergyStorage} MJ");
        Assert.True(restartTicks * HullSim.Dt < 30f, $"{name}: restart took {restartTicks * HullSim.Dt} s");
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public void FlightAndBoostStayUsableWhileAShieldRestarts(string name)
    {
        var flight = Shipped(name);

        // Propulsion has priority over the shield: a restart in progress takes nothing from thrust.
        var restarting = HullState.Spawn(Double3.Zero, flight);
        restarting.Shield = 0f;
        restarting.ShieldOffline = true;
        restarting.ShieldCooldown = 0f;
        var idle = HullState.Spawn(Double3.Zero, flight);
        var input = new FlightInput { Forward = 127 };
        for (var i = 0; i < 300; i++)
        {
            FlightModel.Step(ref restarting, input, flight, HullSim.Dt);
            FlightModel.Step(ref idle, input, flight, HullSim.Dt);
        }

        Assert.Equal(idle.Velocity.Length(), restarting.Velocity.Length(), 3);

        // Boost still starts on a battery the restart has not touched.
        var boost = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref boost, new FlightInput { Forward = 127, Boost = true }, flight, HullSim.Dt);
        Assert.True(boost.Boosting);
    }

    /// <summary>The shipped command pod with all three scan modules added, at one quality, as the module tests fit it.</summary>
    private static GameContent PodWithModules(string quality)
    {
        using var copy = new ContentCopy();
        var pod = JsonNode.Parse(copy.Read("ships/command_pod.json"))!;
        var blocks = pod["blocks"]!.AsArray();
        var ids = new[] { "gasScanner", "asteroidScanner", "asteroidLocator" };
        for (var i = 0; i < ids.Length; i++) blocks.Add(new JsonObject { ["at"] = new JsonArray(-3 - i, 0, 0), ["type"] = ids[i] });
        copy.Write("ships/command_pod.json", pod.ToJsonString());
        var catalogue = JsonNode.Parse(copy.Read("blocks/blocks.json"))!;
        foreach (var id in ids) catalogue["blocks"]![id]!["module"]!["quality"] = quality;
        copy.Write("blocks/blocks.json", catalogue.ToJsonString());
        return copy.Load();
    }

    [Theory]
    [InlineData(ModuleFamily.GasScanner)]
    [InlineData(ModuleFamily.AsteroidScanner)]
    [InlineData(ModuleFamily.AsteroidLocator)]
    public void AScanTurnsTheNetFlowNegativeOnThePodWithoutEmptyingItsBattery(ModuleFamily family)
    {
        var content = PodWithModules("Common");
        var flight = content.Pod.Flight;
        var equipment = content.Pod.Stats.Module(family);
        var state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out var quiet);
        Assert.Equal(0f, quiet.Modules);
        Assert.True(quiet.Net > 0f);

        // The draw is a clear share of what the generator has to spare, so the net figure shows it.
        var share = equipment.PowerDraw / (quiet.Generated - quiet.Idle);
        Assert.InRange(share, 1.1, 2.5);

        state = HullState.Spawn(Double3.Zero, flight);
        var lowest = state.Energy;
        for (var tick = 0; tick < ModuleScanRules.Ticks(equipment.ScanSeconds); tick++)
        {
            FlightModel.Step(ref state, default, flight, HullSim.Dt, out var flow);
            Assert.True(ModuleScanRules.Spend(ref state.Energy, equipment, ref flow), $"{family}: the pod's battery ran dry after {tick} ticks");
            Assert.Equal((float)equipment.PowerDraw, flow.Modules);
            Assert.Equal(quiet.Net - (float)equipment.PowerDraw, flow.Net, 4);
            Assert.True(flow.Net < -FlightConsoleSteady, $"{family}: net {flow.Net} MW does not read as a draw");
            lowest = Math.Min(lowest, state.Energy);
        }

        Assert.True(lowest >= flight.EnergyStorage * 0.5f, $"{family}: one scan took the battery down to {lowest} of {flight.EnergyStorage} MJ");
    }

    [Theory]
    [InlineData("Common")]
    [InlineData("Uncommon")]
    [InlineData("Rare")]
    [InlineData("Epic")]
    [InlineData("Legendary")]
    [InlineData("Alien Tech")]
    public void EveryQualityCanScanOnAFullPodBatteryInTurn(string quality)
    {
        var content = PodWithModules(quality);
        var flight = content.Pod.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        foreach (var family in new[] { ModuleFamily.GasScanner, ModuleFamily.AsteroidScanner, ModuleFamily.AsteroidLocator })
        {
            var equipment = content.Pod.Stats.Module(family);
            for (var tick = 0; tick < ModuleScanRules.Ticks(equipment.ScanSeconds); tick++)
            {
                FlightModel.Step(ref state, default, flight, HullSim.Dt, out var flow);
                Assert.True(ModuleScanRules.Spend(ref state.Energy, equipment, ref flow), $"{quality} {family}: no power at tick {tick}");
            }
        }

        Assert.True(state.Energy > 0f);
    }

    private const float FlightConsoleSteady = 0.05f;
}
