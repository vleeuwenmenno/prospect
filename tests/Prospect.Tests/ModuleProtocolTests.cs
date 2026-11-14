using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class ModuleProtocolTests
{
    [Fact]
    public void ExactBoundsRoundTripAndEveryTruncationIsRejected()
    {
        var records = new ModuleObservation[ModuleMessage.MaxObservations];
        for (var i = 0; i < records.Length; i++) records[i] = new()
        { Owner = 3, Family = ModuleFamily.AsteroidScanner, Sector = new(1, 2, 3), Index = i, RecordedTick = 9, Radius = 10, Mass = 100, Resource = 2, Grade = .5f };
        var bytes = new byte[ModuleMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        var scan = new ModuleScanView { Serial = 4, Family = ModuleFamily.AsteroidScanner, State = ModuleScanState.Completed, StartedTick = 1, CompletesTick = 5, CooldownUntilTick = 10 };
        ModuleMessage.Write(ref writer, scan, records);
        Assert.Equal(bytes.Length, writer.Length);
        var decoded = new ModuleObservation[records.Length];
        for (var length = 1; length < bytes.Length; length++)
        {
            var truncated = new NetReader(bytes.AsSpan(1, length - 1));
            Assert.False(ModuleMessage.Read(ref truncated, decoded, out _, out _));
        }
        var reader = new NetReader(bytes.AsSpan(1));
        Assert.True(ModuleMessage.Read(ref reader, decoded, out var actual, out var count));
        Assert.Equal(scan.Serial, actual.Serial); Assert.Equal(records.Length, count);
        Assert.Equal(records[0].Position, decoded[0].Position);
        Assert.Equal(records[0].Grade, decoded[0].Grade);
        records[0].Mass = float.NaN;
        writer = new NetWriter(bytes); ModuleMessage.Write(ref writer, scan, records);
        reader = new NetReader(bytes.AsSpan(1));
        Assert.False(ModuleMessage.Read(ref reader, decoded, out _, out _));
    }

    [Fact]
    public void RequestIsBoundedAndRejectsUnknownFamilyAndTrailingBytes()
    {
        Span<byte> bytes = stackalloc byte[ModuleMessage.RequestWireSize + 1];
        var writer = new NetWriter(bytes);
        var request = new ModuleScanRequest(ModuleFamily.AsteroidScanner, new(1, -2, 3), 4);
        ModuleMessage.WriteRequest(ref writer, request);
        Assert.Equal(ModuleMessage.RequestWireSize, writer.Length);
        var reader = new NetReader(bytes.Slice(1, writer.Length - 1));
        Assert.True(ModuleMessage.ReadRequest(ref reader, out var actual)); Assert.Equal(request, actual);
        reader = new NetReader(bytes[1..]); Assert.False(ModuleMessage.ReadRequest(ref reader, out _));
        bytes[1] = 255; reader = new NetReader(bytes.Slice(1, writer.Length - 1));
        Assert.False(ModuleMessage.ReadRequest(ref reader, out _));
    }
}
