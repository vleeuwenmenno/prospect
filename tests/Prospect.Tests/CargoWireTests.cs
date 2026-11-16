using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class CargoWireTests
{
    [Fact]
    public void DebugBridgeAndParserTakeNoMemory()
    {
        var bridge = new Prospect.Game.Client.PlayerInputBridge();
        Span<byte> bytes = stackalloc byte[EditorRequestMessage.MaxWireSize];
        Span<byte> hash = stackalloc byte[32];
        bridge.AddContainer(42, 5, 1, -1);
        bridge.ConsumeEdit(out _, out _);
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            bridge.AddContainer(42, 5, 1, -1);
            Assert.True(bridge.ConsumeEdit(out _, out _) == Prospect.Game.Client.PlayerInputBridge.EditIntent.AddContainer);
            var request = bridge.TakeCargoAdd();
            var writer = new NetWriter(bytes);
            request.Write(ref writer, hash);
            var reader = new NetReader(writer.Written[1..]);
            Assert.True(EditorRequestMessage.TryRead(ref reader, hash, out _));
        }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void DebugAddRoundTripAndTruncation()
    {
        var request = new EditorRequestMessage { Kind = EditorRequestKind.AddContainer, Token = 42, CargoRack = 12345, CargoSize = 3, CargoResource = -1 };
        Span<byte> bytes = stackalloc byte[EditorRequestMessage.MaxWireSize];
        Span<byte> hash = stackalloc byte[32];
        var writer = new NetWriter(bytes);
        request.Write(ref writer, hash);
        Assert.Equal(EditorRequestMessage.AddContainerWireSize, writer.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(EditorRequestMessage.TryRead(ref reader, hash, out var copy));
        Assert.Equal((request.Kind, request.Token, request.CargoRack, request.CargoSize, request.CargoResource),
            (copy.Kind, copy.Token, copy.CargoRack, copy.CargoSize, copy.CargoResource));
        for (var n = 1; n < writer.Length; n++)
        {
            reader = new NetReader(writer.Written[1..n]);
            Assert.False(EditorRequestMessage.TryRead(ref reader, hash, out _));
        }
    }

    [Fact]
    public void MaximumHoldRoundTripsAndTruncationIsRefused()
    {
        var cargo = new HoldContainer[BlockStateStore.MaxContainers];
        for (var i = 0; i < cargo.Length; i++) cargo[i] = new(i, i % 8, i % 2 == 0 ? -1 : 0, i % 2 == 0 ? 0 : 42.25, 1, new(i, 0, 0));
        var bytes = new byte[CargoMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        CargoMessage.Write(ref writer, 65536, 2, 99, cargo);
        Assert.Equal(CargoMessage.MaxWireSize, writer.Length);
        var read = new HoldContainer[cargo.Length];
        var reader = new NetReader(bytes.AsSpan(1));
        Assert.True(CargoMessage.TryRead(ref reader, read, out var hull, out var design, out var tick, out var count));
        Assert.Equal((65536u, (byte)2, 99L, cargo.Length), (hull, design, tick, count));
        Assert.Equal(cargo, read);
        for (var n = 0; n < bytes.Length - 1; n++)
        {
            reader = new NetReader(bytes.AsSpan(1, n));
            Assert.False(CargoMessage.TryRead(ref reader, read, out _, out _, out _, out _));
        }
        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            writer = new NetWriter(bytes);
            CargoMessage.Write(ref writer, 65536, 2, 99, cargo);
            reader = new NetReader(bytes.AsSpan(1));
            CargoMessage.TryRead(ref reader, read, out _, out _, out _, out _);
        }
        Assert.Equal(0, allocation.Bytes);
    }
}
