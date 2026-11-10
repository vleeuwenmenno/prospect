using System.Diagnostics;

namespace Prospect.Client.Diagnostics;

/// <summary>Frame timing and allocation figures for the performance overlay and the benchmark.</summary>
internal sealed class FrameStats
{
    public const int HistoryLength = 240;

    private readonly float[] _history = new float[HistoryLength];
    private int _cursor;
    private long _frameStart;
    private long _threadAllocatedAtStart;
    private long _processAllocatedAtStart;
    private float _windowSeconds;
    private int _windowFrames;

    /// <summary>Frame times in milliseconds, oldest first after <see cref="HistoryStart"/>.</summary>
    public ReadOnlySpan<float> History => _history;
    public int HistoryStart => _cursor;

    public float LastFrameMs { get; private set; }
    public float FramesPerSecond { get; private set; }

    /// <summary>Managed bytes the render thread allocated during the last frame.</summary>
    public long ThreadAllocatedBytes { get; private set; }

    /// <summary>Managed bytes every thread in the process allocated during the last frame.</summary>
    public long ProcessAllocatedBytes { get; private set; }
    public long FrameCount { get; private set; }

    public void BeginFrame()
    {
        _frameStart = Stopwatch.GetTimestamp();
        _threadAllocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        _processAllocatedAtStart = GC.GetTotalAllocatedBytes();
    }

    public void EndFrame()
    {
        ThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _threadAllocatedAtStart;
        ProcessAllocatedBytes = GC.GetTotalAllocatedBytes() - _processAllocatedAtStart;

        var seconds = (float)Stopwatch.GetElapsedTime(_frameStart).TotalSeconds;
        LastFrameMs = seconds * 1000f;
        _history[_cursor] = LastFrameMs;
        _cursor = (_cursor + 1) % HistoryLength;
        FrameCount++;

        _windowSeconds += seconds;
        _windowFrames++;
        if (_windowSeconds >= 0.5f)
        {
            FramesPerSecond = _windowFrames / _windowSeconds;
            _windowSeconds = 0;
            _windowFrames = 0;
        }
    }
}
