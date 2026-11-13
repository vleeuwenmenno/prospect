using Xunit;
using Xunit.Sdk;

namespace Prospect.Tests;

/// <summary>
/// The probe every allocation test measures with: it reads what code takes exactly, and it measures again only when
/// a garbage collection paused a stretch that read more than zero.
/// </summary>
public sealed class AllocationProbeTests
{
    private static object? _kept;

    [Fact]
    public void WorkThatTakesNothingReadsZero()
    {
        var allocation = new AllocationProbe();
        var sum = 0;
        for (var i = 0; i < 100; i++)
            while (allocation.Measuring()) sum += i;
        Assert.Equal(0, allocation.Bytes);
        Assert.True(sum >= 4950);
    }

    [Fact]
    public void ARealAllocationIsReadExactlyAndAddsUp()
    {
        var allocation = new AllocationProbe();
        for (var i = 0; i < 3; i++)
            while (allocation.Measuring()) _kept = new object[1];

        // An array of one reference: header, method table, length and the element, 8 bytes each.
        Assert.Equal(3 * 32, allocation.Bytes);
    }

    [Fact]
    public void AStretchACollectionPausedIsMeasuredAgain()
    {
        var allocation = new AllocationProbe();
        var attempts = 0;
        while (allocation.Measuring())
        {
            _kept = new object[1];
            if (++attempts == 1)
                GC.Collect(0);
        }

        Assert.Equal(2, attempts);
        Assert.Equal(32, allocation.Bytes);
    }

    [Fact]
    public void AScenarioWithAStepInDoubtIsRunAgainFromItsStart()
    {
        var allocation = new AllocationProbe();
        var runs = 0;
        while (allocation.Run())
        {
            runs++;
            for (var step = 0; step < 3; step++)
            {
                allocation.Start();
                _kept = new object[1];
                if (runs == 1 && step == 1)
                    GC.Collect(0);
                allocation.Stop();
            }
        }

        Assert.Equal(2, runs);
        Assert.Equal(3 * 32, allocation.Bytes);
    }

    [Fact]
    public void ACollectionInEveryAttemptFailsTheTestInsteadOfGuessing()
    {
        var allocation = new AllocationProbe();
        var attempts = 0;
        var failure = Assert.Throws<FailException>(() =>
        {
            while (allocation.Measuring())
            {
                attempts++;
                _kept = new object[1];
                GC.Collect(0);
            }
        });

        Assert.Equal(AllocationProbe.Attempts, attempts);
        Assert.Contains("each of 10 attempts", failure.Message);
    }
}
