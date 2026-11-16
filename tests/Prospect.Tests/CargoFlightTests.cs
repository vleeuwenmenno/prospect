using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class CargoFlightTests
{
    [Fact]
    public void CargoSlowsFlightAndPredictionMatchesIncludingRackLoss()
    {
        var content = GameContent.Load();
        var design = content.Pod;
        var rack = Array.FindIndex(design.Types, t => t.Cargo > 0);
        Assert.True(rack >= 0);
        using var server = new HullStore();
        using var prediction = new HullStore();
        using var empty = new HullStore();
        var a = server.Create(1, (byte)content.PodDesign, design, default).Index;
        var b = prediction.Create(1, (byte)content.PodDesign, design, default).Index;
        var c = empty.Create(1, (byte)content.PodDesign, design, default).Index;
        var state = server.Info(a).BlockState;
        var size = CargoPacking.Largest(content.Cargo, design.Boxes[rack].Size, design.BlockSize);
        var unit = (double)content.Cargo.UnitSize;
        var kg = content.Cargo.Sizes[size].Units * unit * unit * unit * content.Cargo.Density(content.Galaxy.Resources[0].Id);
        Assert.True(server.BlockStates.TryAddCargo(state, content, new(rack, size, 0, kg, content.Cargo.Sizes[size].Hull, default),
            new byte[CargoPacking.Capacity(content.Cargo, design.Boxes[rack].Size, design.BlockSize)]));
        prediction.BlockStates.CopyFrom(prediction.Info(b).BlockState, server.BlockStates, state);
        prediction.Info(b).PredictDamage = true;
        var authoritative = new HullField(server, content.Designs);
        var predicted = new HullField(prediction, content.Designs, authoritative: false);
        var unloaded = new HullField(empty, content.Designs);
        var input = new FlightInput { Forward = 127 };
        for (var tick = 0; tick < 60; tick++)
        {
            authoritative.Step(a, input);
            predicted.Step(b, input);
            unloaded.Step(c, input);
            Assert.Equal(server.State(a), prediction.State(b));
        }
        Assert.Equal(design.Flight.Mass + (float)(kg / 1000), server.Flight(a).Mass, 3);
        Assert.True(server.State(a).Velocity.LengthSquared() < empty.State(c).Velocity.LengthSquared());
        server.BlockStates.Lose(state, rack);
        prediction.BlockStates.Lose(prediction.Info(b).BlockState, rack);
        authoritative.Step(a, input);
        predicted.Step(b, input);
        Assert.Equal(server.State(a), prediction.State(b));
        Assert.Equal(0, server.BlockStates.CargoKilograms(state));
        Assert.Equal(server.Flight(a), prediction.Flight(b));
    }
}
