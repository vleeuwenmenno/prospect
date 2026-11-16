using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class FreeContainerWireTests
{
    [Fact]
    public void JettisonRequestHasExactBudgetAndRejectsTruncationAndInvalidSlot()
    {
        var bytes = new byte[EditorRequestMessage.JettisonWireSize];
        var request = new EditorRequestMessage
        {
            Kind = EditorRequestKind.JettisonContainer,
            Token = 3,
            Hull = 7,
            CargoRack = 42,
            CargoIndex = 254,
            CargoTick = 100
        };
        var writer = new NetWriter(bytes); request.Write(ref writer, default);
        Assert.Equal(bytes.Length, writer.Written.Length);
        var reader = new NetReader(bytes.AsSpan(1));
        Assert.True(EditorRequestMessage.TryRead(ref reader, default, out var actual));
        Assert.Equal(request.Kind, actual.Kind); Assert.Equal(request.Token, actual.Token);
        Assert.Equal(request.Hull, actual.Hull); Assert.Equal(request.CargoRack, actual.CargoRack);
        Assert.Equal(request.CargoIndex, actual.CargoIndex); Assert.Equal(request.CargoTick, actual.CargoTick);
        for (var length = 1; length < bytes.Length; length++)
        {
            reader = new NetReader(bytes.AsSpan(1, length - 1));
            Assert.False(EditorRequestMessage.TryRead(ref reader, default, out _));
        }
        bytes[14] = 255;
        reader = new NetReader(bytes.AsSpan(1));
        Assert.False(EditorRequestMessage.TryRead(ref reader, default, out _));
    }

    [Fact]
    public void MaximumSetRoundTripsWithinItsSeparateBudgetWithoutAllocation()
    {
        var content = GameContent.Load();
        var items = new FreeContainer[FreeContainers.Capacity];
        var output = new FreeContainer[items.Length];
        var bytes = new byte[FreeContainerMessage.MaxWireSize];
        for (var i = 0; i < items.Length; i++)
            items[i] = new()
            {
                Id = (uint)i + 1,
                Position = new(1e12 + i, -3, 4),
                Orientation = Quaternion.Identity,
                Velocity = new(1, 2, 3),
                AngularVelocity = new(0, .2f, 0),
                Resource = -1,
                Hull = 50
            };
        var warmWriter = new NetWriter(bytes);
        FreeContainerMessage.Write(ref warmWriter, 17, items);
        var warmReader = new NetReader(warmWriter.Written[1..]);
        Assert.True(FreeContainerMessage.TryRead(ref warmReader, content, output, out var warmTick, out var warmCount));
        Assert.Equal(17L, warmTick); Assert.Equal(items.Length, warmCount);
        Assert.Equal(bytes.Length, warmWriter.Length);
        var probe = new AllocationProbe();
        var valid = false;
        var length = 0;
        var tick = 0L;
        var count = 0;
        while (probe.Measuring())
        {
            var writer = new NetWriter(bytes);
            FreeContainerMessage.Write(ref writer, 17, items);
            length = writer.Written.Length;
            var reader = new NetReader(writer.Written[1..]);
            valid = FreeContainerMessage.TryRead(ref reader, content, output, out tick, out count);
        }
        Assert.True(valid);
        Assert.Equal(bytes.Length, length);
        Assert.Equal(17L, tick);
        Assert.Equal(items.Length, count);
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(21003, bytes.Length);
        for (var i = 0; i < items.Length; i++) Assert.Equal(items[i], output[i]);
        for (var n = 1; n < bytes.Length; n++)
        {
            var reader = new NetReader(bytes.AsSpan(1, n - 1));
            Assert.False(FreeContainerMessage.TryRead(ref reader, content, output, out _, out _));
        }
        items[1].Id = items[0].Id;
        var badWriter = new NetWriter(bytes);
        FreeContainerMessage.Write(ref badWriter, 18, items);
        var badReader = new NetReader(badWriter.Written[1..]);
        Assert.False(FreeContainerMessage.TryRead(ref badReader, content, output, out _, out _));
    }
}
