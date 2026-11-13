using Prospect.Client.Render;
using Xunit;

namespace Prospect.Tests;

/// <summary>CPU lattice data and allocation contracts only. GPU interpolation and resource lifetime need validation captures.</summary>
public sealed class GasNoiseLatticeTests
{
    [Fact]
    public void PackedWarpChannelsArePeriodicTranslationsOfTheBaseField()
    {
        var data = new byte[GasNoiseLattice.ValueCount];
        GasNoiseLattice.Fill(data);
        var side = GasNoiseLattice.Side;
        int At(int x, int y, int z)
        {
            x &= side - 1; y &= side - 1; z &= side - 1;
            return ((z * side + y) * side + x) * 4;
        }
        // Include all three wrap boundaries; RGB must equal three scalar fetches even across a texture seam.
        foreach (var x in new[] { 0, 7, 31, 63 })
            foreach (var y in new[] { 0, 15, 61, 63 })
                foreach (var z in new[] { 0, 23, 60, 63 })
                {
                    var at = At(x, y, z);
                    Assert.Equal(data[At(x + 13, y + 13, z + 13) + 3], data[at]);
                    Assert.Equal(data[At(x + 31, y + 31, z + 31) + 3], data[at + 1]);
                    Assert.Equal(data[At(x + 57, y + 57, z + 57) + 3], data[at + 2]);
                }
    }

    [Fact]
    public void ProvisionedFillIsRepeatableNondegenerateAndDoesNotAllocate()
    {
        var first = new byte[GasNoiseLattice.ValueCount];
        var second = new byte[GasNoiseLattice.ValueCount];
        GasNoiseLattice.Fill(first);
        GasNoiseLattice.Fill(second);
        var before = GC.GetAllocatedBytesForCurrentThread();
        GasNoiseLattice.Fill(second);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(first.AsSpan().SequenceEqual(second));
        Assert.Equal(1024 * 1024, GasNoiseLattice.ByteCount);
        long sum = 0;
        var low = 0; var high = 0;
        for (var i = 3; i < first.Length; i += 4)
        {
            sum += first[i];
            if (first[i] < byte.MaxValue / 4) low++;
            if (first[i] > byte.MaxValue * 3 / 4) high++;
        }
        var texels = GasNoiseLattice.ValueCount / 4;
        Assert.InRange((double)sum / texels / byte.MaxValue, 0.45, 0.55);
        Assert.InRange(low, texels / 5, texels / 3);
        Assert.InRange(high, texels / 5, texels / 3);
    }

    [Fact]
    public void InvalidStorageCannotProduceAPartiallyUploadedLattice()
    {
        Assert.Throws<ArgumentException>(() => GasNoiseLattice.Fill(Span<byte>.Empty));
        var shortStorage = new byte[GasNoiseLattice.ValueCount - 1];
        shortStorage[^1] = 123;
        Assert.Throws<ArgumentException>(() => GasNoiseLattice.Fill(shortStorage));
        Assert.Equal(123, shortStorage[^1]);
    }
}
