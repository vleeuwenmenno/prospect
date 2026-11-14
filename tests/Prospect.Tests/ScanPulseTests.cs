using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>When the scan pulse starts, how long it shows, and that following it takes no memory.</summary>
public sealed class ScanPulseTests
{
    private const float Frame = 1f / 60f;
    private static readonly ScanPulseLook Look = ScanPulseLook.Load();

    private static ScanView Scan(ScanState state, long started = 100) =>
        new() { State = state, Sector = new SectorCoord(1, 0, 2), StartedTick = started, CompletesTick = started + 300 };

    private static readonly Double3 Here = new(1000, 20, -500);

    [Fact]
    public void TheShippedLookLoads()
    {
        Assert.True(Look.Seconds is >= 1.5f and <= 3f);
        Assert.Equal(3, Look.Color.Length);
    }

    [Fact]
    public void APulseStartsWhenARunningScanCompletes()
    {
        var pulse = new ScanPulse(Look);
        pulse.Observe(Scan(ScanState.Scanning), Here, Frame);
        Assert.False(pulse.Active);
        var moved = new Double3(1200, 20, -500);
        pulse.Observe(Scan(ScanState.Completed), moved, Frame);
        Assert.True(pulse.Active);
        Assert.Equal(1, pulse.Started);
        Assert.Equal(moved, pulse.Origin);

        // The pulse stays where the scan completed while the ship flies on, and its front grows. It comes up from
        // nothing over its fade-in, so its first frame shows nothing.
        Assert.False(pulse.Shell(out _));
        pulse.Observe(Scan(ScanState.Completed), new Double3(5000, 0, 0), Frame);
        Assert.True(pulse.Shell(out var first));
        for (var i = 0; i < 30; i++) pulse.Observe(Scan(ScanState.Completed), new Double3(5000, 0, 0), Frame);
        Assert.True(pulse.Shell(out var later));
        Assert.Equal(moved, later.Origin);
        Assert.True(later.Radius > first.Radius);
        Assert.True(later.Radius <= Look.Reach);
    }

    [Fact]
    public void OneCompletionIsOnePulse()
    {
        var pulse = new ScanPulse(Look);
        pulse.Observe(Scan(ScanState.Scanning), Here, Frame);
        var frames = (int)(Look.Seconds * 3 / Frame);
        for (var i = 0; i < frames; i++)
            pulse.Observe(Scan(ScanState.Completed), Here, Frame);
        Assert.Equal(1, pulse.Started);
        Assert.False(pulse.Active);
        Assert.False(pulse.Shell(out _));
    }

    [Fact]
    public void ThePulseEndsAfterItsSeconds()
    {
        var pulse = new ScanPulse(Look);
        pulse.Observe(Scan(ScanState.Scanning), Here, Frame);
        pulse.Observe(Scan(ScanState.Completed), Here, Frame);
        var elapsed = 0f;
        while (pulse.Active && elapsed < 10f)
        {
            pulse.Observe(Scan(ScanState.Completed), Here, Frame);
            elapsed += Frame;
        }

        Assert.InRange(elapsed, Look.Seconds - 2 * Frame, Look.Seconds + 2 * Frame);
    }

    [Theory]
    [InlineData(ScanState.Cancelled)]
    [InlineData(ScanState.NoEquipment)]
    [InlineData(ScanState.ChartLimit)]
    [InlineData(ScanState.Cooldown)]
    [InlineData(ScanState.Idle)]
    public void ACancelledOrRefusedScanStartsNone(ScanState end)
    {
        var pulse = new ScanPulse(Look);
        pulse.Observe(Scan(ScanState.Scanning), Here, Frame);
        pulse.Observe(Scan(end), Here, Frame);
        // Even a completed state for the same scan after that is not a completion this client saw.
        pulse.Observe(Scan(ScanState.Completed), Here, Frame);
        Assert.Equal(0, pulse.Started);
        Assert.False(pulse.Active);
    }

    [Fact]
    public void ACompletionNeverSeenRunningStartsNone()
    {
        // A session joined after its last scan ended: the first snapshot already says completed.
        var pulse = new ScanPulse(Look);
        pulse.Observe(Scan(ScanState.Completed), Here, Frame);
        pulse.Observe(Scan(ScanState.Completed), Here, Frame);
        Assert.Equal(0, pulse.Started);
    }

    [Fact]
    public void EachNewScanPulsesAgain()
    {
        var pulse = new ScanPulse(Look);
        pulse.Observe(Scan(ScanState.Scanning, 100), Here, Frame);
        pulse.Observe(Scan(ScanState.Completed, 100), Here, Frame);
        pulse.Observe(Scan(ScanState.Scanning, 900), Here, Frame);
        pulse.Observe(Scan(ScanState.Completed, 900), Here, Frame);
        Assert.Equal(2, pulse.Started);
    }

    [Fact]
    public void ResetForgetsARunningScan()
    {
        var pulse = new ScanPulse(Look);
        pulse.Observe(Scan(ScanState.Scanning), Here, Frame);
        pulse.Reset();
        pulse.Observe(Scan(ScanState.Completed), Here, Frame);
        Assert.Equal(0, pulse.Started);
        Assert.False(pulse.Active);
    }

    [Fact]
    public void NothingShowsWhileAScanRuns()
    {
        var pulse = new ScanPulse(Look);
        for (var i = 0; i < 400; i++)
        {
            pulse.Observe(Scan(ScanState.Scanning), Here, Frame);
            Assert.False(pulse.Shell(out _));
        }
    }

    [Fact]
    public void FollowingTheScanTakesNoMemory()
    {
        var pulse = new ScanPulse(Look);
        var scanning = Scan(ScanState.Scanning);
        var completed = Scan(ScanState.Completed);
        pulse.Observe(scanning, Here, Frame);
        pulse.Shell(out _);
        var probe = new AllocationProbe();
        for (var i = 0; i < 200; i++)
        {
            var scan = i % 50 < 25 ? scanning with { StartedTick = 100 + i / 50 } : completed with { StartedTick = 100 + i / 50 };
            while (probe.Measuring())
            {
                pulse.Observe(scan, Here, Frame);
                pulse.Shell(out _);
            }
        }

        Assert.Equal(0, probe.Bytes);
        Assert.True(pulse.Started > 0);
    }

    [Fact]
    public void ALookOutOfRangeIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scan-{Guid.NewGuid():N}.json");
        try
        {
            var text = File.ReadAllText(AppPaths.Content("visuals", "scan.json"));
            File.WriteAllText(path, text.Replace("\"seconds\": 2.6", "\"seconds\": 40"));
            Assert.Throws<InvalidDataException>(() => ScanPulseLook.Load(path));
            File.WriteAllText(path, text.Replace("\"lineWidth\": 1.2", "\"lineWidth\": 0"));
            Assert.Throws<InvalidDataException>(() => ScanPulseLook.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
