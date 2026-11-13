using System.Text.Json;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Server;

namespace Prospect.Client.Diagnostics;

/// <summary>Bounded CPU frame samples. File IO and formatting occur only after the window run.</summary>
internal sealed class PopulatedMeasurement(int capacity = 200000)
{
    private readonly Sample[] _samples = new Sample[capacity];
    private int _count;
    private uint _dentedOwn;
    private bool _repairReset;
    private long _processStart = -1;
    internal long Overflow { get; private set; }
    internal float EndSeconds { get; private set; }
    internal bool Complete => Overflow == 0 && _count > 0 && _samples[_count - 1].Seconds == EndSeconds;
    internal ReadOnlySpan<Sample> Samples => _samples.AsSpan(0, _count);
    internal record struct Sample(float Seconds, float Ms, long Bytes, int Hulls, int Visible, int Dents, int Draws,
        int Sparks, int Upload, int Fallbacks, int Rocks, int VisibleRocks, double WorkerMs, float TickMs, bool Drawn);

    public void Record(float seconds, FrameStats frame, SessionSnapshot snapshot, WorldView world, SceneRenderer scene, float tickMs, bool drawn)
    {
        if (drawn && snapshot.OwnHull >= 0)
        {
            var own = snapshot.Hulls[snapshot.OwnHull];
            if (own.Visual.Count > 0) _dentedOwn = own.Id;
            if (_dentedOwn != 0 && own.Id != _dentedOwn && own.Visual.Count == 0) _repairReset = true;
        }
        if (_processStart < 0) _processStart = GC.GetTotalAllocatedBytes(false);
        Record(new Sample(seconds, frame.LastFrameMs, frame.ThreadAllocatedBytes, snapshot.HullCount,
            world.VisibleHulls, world.DrawnDents, scene.DamageDraws, scene.SparkVertices, scene.DynamicBytes,
            world.DamageFallbacks, world.GeneratedRocks, world.VisibleRocks, world.DamageWorkerMs, tickMs, drawn));
    }

    internal void Record(Sample sample)
    {
        EndSeconds = sample.Seconds;
        if (_count == _samples.Length) { Overflow++; return; }
        // Snapshot population and residency are state; draw and upload counters describe only this frame.
        if (!sample.Drawn) sample = sample with { Visible = 0, Dents = 0, Draws = 0, Sparks = 0, Upload = 0, Fallbacks = 0, VisibleRocks = 0 };
        _samples[_count++] = sample;
    }

    internal bool Verify(VisualBenchmarkScene scenario, int errors)
    {
        var dents = false; var sparks = false; var population = false; var maxDraws = 0; var lateSparks = false;
        foreach (var s in Samples)
        {
            if (!s.Drawn) continue;
            maxDraws = Math.Max(maxDraws, s.Draws);
            dents |= s.Draws > 0 && s.Dents > 0;
            sparks |= s.Sparks > 0;
            lateSparks |= s.Seconds > 10 && s.Sparks > 0;
            population |= s.Hulls == scenario.Hulls;
        }
        return Complete && population && (scenario.View != "near" || scenario.Activity == "clean" || dents)
            && (scenario.View != "near" || scenario.Activity != "impacts" || sparks) && (scenario.View == "near" || !dents && !sparks)
            && (scenario.Activity != "dents" || scenario.View != "near" || maxDraws == scenario.Hulls - 1)
            && (scenario.Activity != "impacts" || EndSeconds < 12 || lateSparks)
            && (scenario.Activity != "repair" || _repairReset) && errors == 0;
    }

    public bool Report(VisualBenchmarkScene scenario, GraphicsDevice gpu, Swapchain swapchain, float endSeconds)
    {
        EndSeconds = Math.Max(EndSeconds, endSeconds);
        var process = _processStart < 0 ? 0 : GC.GetTotalAllocatedBytes(false) - _processStart;
        var path = Environment.GetEnvironmentVariable("PROSPECT_VISUAL_REPORT") ?? "/tmp/prospect-populated.csv";
        using var writer = new StreamWriter(path);
        writer.WriteLine("seconds,cpu_frame_ms,render_bytes,hulls,visible_hulls,drawn_dents,damage_draws,spark_vertices,dynamic_bytes,fallbacks,loaded_sector_rocks,visible_rocks,last_worker_ms,tick_ms,drawn");
        var warm = new List<float>(); long thread = 0;
        foreach (var s in Samples)
        {
            writer.WriteLine(FormattableString.Invariant($"{s.Seconds},{s.Ms},{s.Bytes},{s.Hulls},{s.Visible},{s.Dents},{s.Draws},{s.Sparks},{s.Upload},{s.Fallbacks},{s.Rocks},{s.VisibleRocks},{s.WorkerMs},{s.TickMs},{(s.Drawn ? 1 : 0)}"));
            if (s.Seconds >= 2) { warm.Add(s.Ms); thread += s.Bytes; }
        }
        warm.Sort();
        var ok = Verify(scenario, GraphicsDevice.ValidationErrorCount);
        File.WriteAllText(path + ".meta.json", JsonSerializer.Serialize(new { samples = _count, overflow = Overflow, end_seconds = EndSeconds, complete = Complete, verified = ok }));
        Console.WriteLine(FormattableString.Invariant($"POPULATED {scenario} resolution={swapchain.Extent.Width}x{swapchain.Extent.Height} validation={gpu.ValidationEnabled} verified={ok} complete={Complete} overflow={Overflow} end_seconds={EndSeconds} repair_reset={_repairReset} render_bytes={thread} process_bytes_including_cold={process}"));
        if (warm.Count > 0) Console.WriteLine(FormattableString.Invariant($"CPU recorded frame ms mean={warm.Average():F3} p95={warm[(int)((warm.Count - 1) * .95)]:F3} p99={warm[(int)((warm.Count - 1) * .99)]:F3} max={warm[^1]:F3}; not GPU timestamps; raw={path}"));
        return ok;
    }
}
