using Prospect.Client.Render;

namespace Prospect.Client.Diagnostics;

/// <summary>
/// Measures steady-state frame pacing and allocation for a fixed time and judges them against the project's
/// targets: at least 144 frames per second and no managed allocation in the frame loop.
/// </summary>
internal sealed class Benchmark(float seconds)
{
    private const float TargetFps = 144f;

    private readonly float[] _frameMs = new float[(int)(seconds * 20000)];
    private int _frames;
    private double _elapsedMs;
    private long _threadBytes;
    private int _allocatingFrames;
    private long _processBytesAtStart = -1;
    private readonly int _gen0AtStart = GC.CollectionCount(0);

    public bool Finished => _elapsedMs >= seconds * 1000.0 || _frames == _frameMs.Length;

    public void Record(FrameStats stats)
    {
        if (Finished)
            return;

        if (_processBytesAtStart < 0)
        {
            // An exact process-wide count pauses every thread, so it is taken only at the two ends of the run.
            _processBytesAtStart = GC.GetTotalAllocatedBytes(precise: true);
            return;
        }

        _frameMs[_frames++] = stats.LastFrameMs;
        _elapsedMs += stats.LastFrameMs;
        _threadBytes += stats.ThreadAllocatedBytes;
        if (stats.ThreadAllocatedBytes > 0)
            _allocatingFrames++;
    }

    /// <summary>Prints the result and returns whether both targets were met.</summary>
    public bool Report(GraphicsDevice gpu, Swapchain swapchain)
    {
        var processBytes = GC.GetTotalAllocatedBytes(precise: true) - _processBytesAtStart;
        var times = _frameMs.AsSpan(0, _frames);
        times.Sort();
        var average = _elapsedMs / _frames;
        var fps = 1000.0 / average;
        var p99 = times[Math.Min(_frames - 1, (int)(_frames * 0.99))];
        var worst = times[^1];
        var fpsOk = fps >= TargetFps;
        var allocOk = _threadBytes == 0 && processBytes == 0;
        var validationOk = GraphicsDevice.ValidationErrorCount == 0;

        Console.WriteLine();
        Console.WriteLine("PROSPECT benchmark");
        Console.WriteLine($"  GPU                  {gpu.DeviceName}");
        Console.WriteLine($"  Resolution           {swapchain.Extent.Width}x{swapchain.Extent.Height}, {swapchain.PresentMode}");
        Console.WriteLine($"  Frames               {_frames} in {_elapsedMs / 1000.0:F1} s");
        Console.WriteLine($"  Average              {fps:F0} fps ({average:F3} ms)   target >= {TargetFps:F0}   {(fpsOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"  99th percentile      {p99:F3} ms ({1000.0 / p99:F0} fps)");
        Console.WriteLine($"  Worst frame          {worst:F3} ms");
        Console.WriteLine($"  Allocated, render    {_threadBytes} bytes in {_allocatingFrames} frames");
        Console.WriteLine($"  Allocated, process   {processBytes} bytes (all threads)   target 0   {(allocOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"  Gen-0 collections    {GC.CollectionCount(0) - _gen0AtStart}");
        Console.WriteLine($"  Validation           {(gpu.ValidationEnabled ? $"{GraphicsDevice.ValidationErrorCount} errors" : "layers not active")}   {(validationOk ? "PASS" : "FAIL")}");
        Console.WriteLine();
        return fpsOk && allocOk && validationOk;
    }
}
