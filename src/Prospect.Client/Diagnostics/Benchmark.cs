using Prospect.Client.Render;

namespace Prospect.Client.Diagnostics;

/// <summary>
/// Measures steady-state frame pacing and allocation for a fixed time and judges them against the project's
/// targets: at least 144 frames per second and no managed allocation in the frame loop.
/// </summary>
internal sealed class Benchmark(float seconds, int resizes, int moduleScansWanted = 0, bool fpsNotJudged = false)
{
    private const float TargetFps = 144f;

    /// <summary>
    /// How many module scans a <c>--module-repeat</c> run must complete. Without the count a scan that never started
    /// (no target, no cloud in reach, no module on the ship) would leave a run that measured nothing and passed.
    /// </summary>
    public const int ModuleScansRequired = 5;

    private readonly float[] _frameMs = new float[(int)(seconds * 20000)];
    private int _frames;
    private double _elapsedMs;
    private long _threadBytes;
    private int _allocatingFrames;
    private long _processBytesAtStart = -1;
    private readonly int _gen0AtStart = GC.CollectionCount(0);
    private int _resizesAsked, _rebuildsAsked;
    private int _swapchainRebuildsAtStart, _swapchainRebuilds;
    private int _targetRebuildsAtStart, _targetRebuilds;

    public bool Finished => _elapsedMs >= seconds * 1000.0 || _frames == _frameMs.Length;

    /// <summary>
    /// True once for each resize the run was asked to make (<c>--bench-resize</c>), evenly spaced over the
    /// measurement and never in its last frames, so that the rebuilding a resize causes is measured whole.
    /// </summary>
    public bool ResizeDue() => Due(ref _resizesAsked, 0.25);

    /// <summary>
    /// True once after each of those resizes, halfway to the next: the swapchain and the render targets are then
    /// rebuilt at the size they have. That is the rebuild of a swapchain that went out of date or changed present
    /// mode, and it measures a resize's work on a machine whose window manager will not resize the window.
    /// </summary>
    public bool RebuildDue() => Due(ref _rebuildsAsked, 0.75);

    private bool Due(ref int asked, double phase)
    {
        if (asked >= resizes || _processBytesAtStart < 0 || _elapsedMs < (asked + phase) * seconds * 1000.0 / resizes)
            return false;
        asked++;
        return true;
    }

    /// <summary>
    /// Adds the frame that just ended. <paramref name="swapchainRebuilds"/> and <paramref name="targetRebuilds"/>
    /// are the renderer's running counts; the report says how many fell inside the measurement.
    /// </summary>
    public void Record(FrameStats stats, int swapchainRebuilds, int targetRebuilds)
    {
        if (Finished)
            return;

        if (_processBytesAtStart < 0)
        {
            // An exact process-wide count pauses every thread, so it is taken only at the two ends of the run.
            _processBytesAtStart = GC.GetTotalAllocatedBytes(precise: true);
            _swapchainRebuildsAtStart = swapchainRebuilds;
            _targetRebuildsAtStart = targetRebuilds;
            return;
        }

        _swapchainRebuilds = swapchainRebuilds - _swapchainRebuildsAtStart;
        _targetRebuilds = targetRebuilds - _targetRebuildsAtStart;

        _frameMs[_frames++] = stats.LastFrameMs;
        _elapsedMs += stats.LastFrameMs;
        _threadBytes += stats.ThreadAllocatedBytes;
        if (stats.ThreadAllocatedBytes > 0)
            _allocatingFrames++;
    }

    /// <summary>Prints the result and returns whether both targets were met.</summary>
    public bool Report(GraphicsDevice gpu, Swapchain swapchain, int moduleScans = 0)
    {
        var processBytes = GC.GetTotalAllocatedBytes(precise: true) - _processBytesAtStart;
        var times = _frameMs.AsSpan(0, _frames);
        times.Sort();
        var average = _elapsedMs / _frames;
        var fps = 1000.0 / average;
        var p99 = times[Math.Min(_frames - 1, (int)(_frames * 0.99))];
        var worst = times[^1];
        var fpsOk = fpsNotJudged || fps >= TargetFps;
        var allocOk = _threadBytes == 0 && processBytes == 0;
        var validationOk = GraphicsDevice.ValidationErrorCount == 0;
        var scansOk = moduleScans >= moduleScansWanted;
        // The forced rebuilds always happen, so a run that asked for them and did not measure them has failed. The
        // resizes are the window system's to grant: how many it granted is reported, not required.
        var resizeOk = _resizesAsked == resizes && _rebuildsAsked == resizes && _swapchainRebuilds >= resizes && _targetRebuilds >= resizes;
        var resized = Math.Max(0, _targetRebuilds - _rebuildsAsked);

        Console.WriteLine();
        Console.WriteLine("PROSPECT benchmark");
        Console.WriteLine($"  GPU                  {gpu.DeviceName}");
        Console.WriteLine($"  Resolution           {swapchain.Extent.Width}x{swapchain.Extent.Height}, {swapchain.PresentMode}");
        Console.WriteLine($"  Frames               {_frames} in {_elapsedMs / 1000.0:F1} s");
        Console.WriteLine($"  Average              {fps:F0} fps ({average:F3} ms){(fpsNotJudged ? "   (not judged)" : $"   target >= {TargetFps:F0}   {(fpsOk ? "PASS" : "FAIL")}")}");
        Console.WriteLine($"  99th percentile      {p99:F3} ms ({1000.0 / p99:F0} fps)");
        Console.WriteLine($"  Worst frame          {worst:F3} ms");
        Console.WriteLine($"  Allocated, render    {_threadBytes} bytes in {_allocatingFrames} frames");
        Console.WriteLine($"  Allocated, process   {processBytes} bytes (all threads)   target 0   {(allocOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"  Gen-0 collections    {GC.CollectionCount(0) - _gen0AtStart}");
        Console.WriteLine($"  Rebuilt, measured    swapchain {_swapchainRebuilds}, render targets {_targetRebuilds}{(resizes > 0 ? $"   target >= {resizes} each (forced)   {(resizeOk ? "PASS" : "FAIL")}" : "")}");
        if (resizes > 0)
            Console.WriteLine($"  Resized, measured    {resized} times for {resizes} asked{(resized < resizes ? "   (the window manager kept the window's size)" : "")}");
        if (moduleScansWanted > 0)
            Console.WriteLine($"  Module scans         {moduleScans} completed   target >= {moduleScansWanted}   {(scansOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"  Validation           {(gpu.ValidationEnabled ? $"{GraphicsDevice.ValidationErrorCount} errors" : "layers not active")}   {(validationOk ? "PASS" : "FAIL")}");
        Console.WriteLine();
        return fpsOk && allocOk && validationOk && resizeOk && scansOk;
    }
}
