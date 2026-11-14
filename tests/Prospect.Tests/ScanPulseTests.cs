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
        Assert.True(pulse.Shell(out var start));
        Assert.Equal(0f, start.Strength);
        pulse.Observe(Scan(ScanState.Completed), new Double3(5000, 0, 0), Frame);
        Assert.True(pulse.Shell(out var first));
        Assert.True(first.Strength > 0f);
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

        // The front ends after its seconds; the pulse goes on for the longest ripple it may have started last.
        var end = Look.Seconds + Look.WaveLongest;
        Assert.InRange(elapsed, end - 2 * Frame, end + 2 * Frame);
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

    [Fact]
    public void ARippleStartsWhenTheFrontTouchesTheObject()
    {
        var waves = new ScanWaves(Look);
        const float distance = 1200f, radius = 150f;
        var touched = Look.ShareAt(distance - radius) * Look.Seconds;
        Assert.False(waves.Wave(distance, radius, touched - 0.05f, out _));
        Assert.True(waves.Wave(distance, radius, touched + 0.01f, out var early));
        Assert.True(waves.Wave(distance, radius, touched + 0.3f, out var later));
        Assert.True(early.X < later.X);
        Assert.True(later.Z > 0f);
        Assert.Equal(radius * Look.WaveBand, later.Y, 3);

        // It crawls to the far side in its crossing time, then ends.
        var crossing = Look.CrossingSeconds(radius);
        Assert.True(waves.Wave(distance, radius, touched + crossing, out var across));
        Assert.Equal(MathF.PI * radius, across.X, 1);
        Assert.False(waves.Wave(distance, radius, touched + Look.WaveLongest + Look.Seconds, out _));
    }

    [Fact]
    public void LargerObjectsAreCrossedFasterInMetresAndSlowerInTime()
    {
        var small = Look.CrossingSeconds(40f);
        var large = Look.CrossingSeconds(800f);
        Assert.True(large >= small);
        Assert.True(MathF.PI * 800f / large > MathF.PI * 40f / small);
        Assert.InRange(Look.CrossingSeconds(1e6f), Look.WaveMinSeconds, Look.WaveMaxSeconds);
        Assert.InRange(Look.CrossingSeconds(0.1f), Look.WaveMinSeconds, Look.WaveMaxSeconds);
    }

    [Fact]
    public void AnObjectThePulseStartedInsideHasNoRipple()
    {
        var waves = new ScanWaves(Look);
        Assert.False(waves.Wave(10f, 40f, 0.5f, out _));
        Assert.False(waves.Wave(Look.Reach + 500f, 100f, 2f, out _));
    }

    /// <summary>A pulse age just after the front touched an object of radius 50 m whose centre is 1000 m out.</summary>
    private static float JustTouched => Look.ShareAt(950f) * Look.Seconds + 0.1f;

    private static System.Numerics.Vector3 Ahead(float metres) => new(0, 0, -metres);

    [Fact]
    public void TheLargestOnScreenKeepTheirRipples()
    {
        var waves = new ScanWaves(Look);
        waves.Begin(true, JustTouched, System.Numerics.Vector3.Zero);
        for (var i = 0; i < 100; i++)
            waves.Offer(Ahead(1000f - i * 0.01f), 50f, Look.WaveMinPixels + i);
        Assert.Equal(ScanWaves.Capacity, waves.Count);
        // The 32 largest are those offered last, largest first.
        Assert.Equal(1000f - 99 * 0.01f, -waves.Items[0].Sphere.Z, 3);
        Assert.Equal(1000f - 68 * 0.01f, -waves.Items[^1].Sphere.Z, 3);

        // Too small on screen, or with no pulse, nothing is kept.
        waves.Begin(true, JustTouched, System.Numerics.Vector3.Zero);
        waves.Offer(Ahead(1000f), 50f, Look.WaveMinPixels - 1);
        Assert.Equal(0, waves.Count);
        waves.Begin(false, JustTouched, System.Numerics.Vector3.Zero);
        waves.Offer(Ahead(1000f), 50f, 500f);
        Assert.Equal(0, waves.Count);
    }

    [Fact]
    public void RipplesAreOrderedLargestFirst()
    {
        var waves = new ScanWaves(Look);
        waves.Begin(true, JustTouched, System.Numerics.Vector3.Zero);
        float[] sizes = [40, 300, 90, 500, 60];
        for (var i = 0; i < sizes.Length; i++)
            waves.Offer(Ahead(1000f - i * 0.01f), 50f, sizes[i]);
        Assert.Equal(sizes.Length, waves.Count);
        Assert.Equal(1000f - 3 * 0.01f, -waves.Items[0].Sphere.Z, 3);
        Assert.Equal(1000f - 1 * 0.01f, -waves.Items[1].Sphere.Z, 3);
        Assert.Equal(1000f, -waves.Items[^1].Sphere.Z, 3);
    }

    [Fact]
    public void OfferingRipplesTakesNoMemory()
    {
        var waves = new ScanWaves(Look);
        var probe = new AllocationProbe();
        for (var frame = 0; frame < 20; frame++)
        {
            while (probe.Measuring())
            {
                waves.Begin(true, JustTouched + frame * 0.01f, System.Numerics.Vector3.Zero);
                for (var i = 0; i < 200; i++)
                    waves.Offer(Ahead(1000f - i * 0.01f), 50f, Look.WaveMinPixels + i % 90);
            }
        }

        Assert.Equal(0, probe.Bytes);
        Assert.Equal(ScanWaves.Capacity, waves.Count);
    }
}
