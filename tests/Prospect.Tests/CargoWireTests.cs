using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class CargoWireTests
{
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
