using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Prospect.Engine.Time;

/// <summary>Work executed once per fixed simulation step on a <see cref="TickLoop"/> thread.</summary>
public interface ITickHandler
{
    /// <summary>Called once on the loop thread before the first tick.</summary>
    void OnStart();

    /// <summary>Advances the simulation by exactly one fixed step.</summary>
    void Tick(long tick);

    /// <summary>Called once on the loop thread after the last tick.</summary>
    void OnStop();
}

/// <summary>Timing figures for a tick loop, safe to read from any thread.</summary>
public struct TickStats
{
    public long Tick;
    public float LastMs;
    public float AverageMs;
    public float MaxMs;
    public long Overruns;
}

/// <summary>
/// Runs a handler at a fixed rate on its own thread. The simulation never depends on wall time: every tick is one
/// fixed step, and a loop that falls behind catches up a bounded number of steps before dropping the backlog.
/// </summary>
public sealed class TickLoop : IDisposable
{
    public const int SimulationHz = 60;
    public const double SimulationDt = 1.0 / SimulationHz;
    private const int MaxCatchUpSteps = 5;

    private readonly ITickHandler _handler;
    private readonly Thread _thread;
    private readonly long _stepTicks;
    private volatile bool _stopRequested;
    private bool _started;

    private long _tick;
    private float _lastMs;
    private float _averageMs;
    private float _maxMs;
    private long _overruns;
    private volatile Exception? _fault;

    public TickLoop(string name, int hz, ITickHandler handler)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hz, 1);
        _handler = handler;
        _stepTicks = Stopwatch.Frequency / hz;
        _thread = new Thread(Run) { Name = name, IsBackground = true };
    }

    /// <summary>The exception that stopped the loop, if a tick threw.</summary>
    public Exception? Fault => _fault;

    public bool IsRunning => _started && _thread.IsAlive;

    public TickStats Stats =>
        new()
        {
            Tick = Volatile.Read(ref _tick),
            LastMs = Volatile.Read(ref _lastMs),
            AverageMs = Volatile.Read(ref _averageMs),
            MaxMs = Volatile.Read(ref _maxMs),
            Overruns = Volatile.Read(ref _overruns),
        };

    public void Start()
    {
        if (_started)
            throw new InvalidOperationException("Tick loop already started");
        _started = true;
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        if (_started && Thread.CurrentThread != _thread)
            _thread.Join();
    }

    public void Dispose() => Stop();

    private void Run()
    {
        try
        {
            _handler.OnStart();
            var next = Stopwatch.GetTimestamp() + _stepTicks;
            var tick = 0L;
            var msPerTick = 1000.0 / Stopwatch.Frequency;

            while (!_stopRequested)
            {
                var begin = Stopwatch.GetTimestamp();
                _handler.Tick(tick);
                var end = Stopwatch.GetTimestamp();

                var ms = (float)((end - begin) * msPerTick);
                Volatile.Write(ref _lastMs, ms);
                Volatile.Write(ref _averageMs, _averageMs + (ms - _averageMs) * 0.05f);
                if (ms > _maxMs)
                    Volatile.Write(ref _maxMs, ms);
                Volatile.Write(ref _tick, ++tick);

                if (end - next > _stepTicks * MaxCatchUpSteps)
                {
                    // Too far behind to catch up without a visible burst: drop the backlog and resume in real time.
                    Volatile.Write(ref _overruns, _overruns + 1);
                    next = end;
                }

                PreciseSleep.Until(next);
                next += _stepTicks;
            }
        }
        catch (Exception e)
        {
            _fault = e;
        }
        finally
        {
            try
            {
                _handler.OnStop();
            }
            catch (Exception e)
            {
                _fault ??= e;
            }
        }
    }
}

/// <summary>Sleeps to a deadline with sub-millisecond accuracy: a coarse OS sleep followed by a short spin.</summary>
public static partial class PreciseSleep
{
    private static readonly long SpinWindowTicks = Stopwatch.Frequency / 1000 * 2; // 2 ms

    /// <summary>
    /// Raises the Windows timer resolution to 1 ms for the lifetime of the process. Without it Thread.Sleep rounds up
    /// to the 15.6 ms scheduler quantum, which is most of a 60 Hz step. A no-op on other platforms.
    /// </summary>
    public static void EnableHighResolutionTimer()
    {
        if (OperatingSystem.IsWindows())
            _ = TimeBeginPeriod(1);
    }

    public static void Until(long deadlineTimestamp)
    {
        while (true)
        {
            var remaining = deadlineTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return;

            if (remaining > SpinWindowTicks)
                Thread.Sleep((int)((remaining - SpinWindowTicks) * 1000 / Stopwatch.Frequency) + 1);
            else
                Thread.SpinWait(32);
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint milliseconds);
}
