using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Measures how much managed memory the calling thread takes over a stretch of code, for the tests that hold code
/// to taking none once warm. Every such test measures through this type, never through
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> directly; only a test with a budget far above 8 KiB may.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> also counts the unused rest of the thread's allocation buffer,
/// up to about 8 KiB, when a garbage collection pauses the thread and takes that buffer back. The pause need not
/// come from this test, and need not change <see cref="GC.CollectionCount"/> inside the stretch: a background
/// collection started earlier, often by the test's own setup, ends with a pause of its own. A full run of the suite,
/// where collections are frequent, then failed one allocation test or another with readings such as 8120 or 1392
/// bytes for code that takes nothing.
/// </para>
/// <para>
/// A pause only ever adds to the reading. So a stretch that reads zero took nothing, pause or not, and a stretch no
/// pause touched reads exactly what it took; only a stretch that a pause touched and that reads more than zero is in
/// doubt, and it is measured again. A real allocation shows in every attempt, so it is never measured away.
/// </para>
/// <para>
/// Repeatable work is measured with <see cref="Measuring"/>: <c>while (probe.Measuring()) Work();</c>, then
/// <see cref="Bytes"/>. A loop of it is best measured an iteration at a time, which adds up: a short stretch is
/// seldom paused. A scenario whose measured steps cannot simply be repeated (a block lost once, a save taken once)
/// is run whole inside <c>while (probe.Run())</c> with <see cref="Start"/> and <see cref="Stop"/> around each
/// measured step, and is run again from its start when a step is in doubt.
/// </para>
/// </remarks>
public sealed class AllocationProbe
{
    /// <summary>How many times a stretch is measured before a pause in every attempt fails the test.</summary>
    public const int Attempts = 10;

    private long _start;
    private TimeSpan _pauses;
    private int _collections;
    private bool _open;
    private bool _doubtful;
    private int _attempt;
    private int _runs;

    static AllocationProbe()
    {
        // The first call of each reading binds it; do that here, so it never happens inside a measured stretch.
        _ = GC.GetAllocatedBytesForCurrentThread();
        _ = GC.GetTotalPauseDuration();
        _ = GC.CollectionCount(0);
    }

    /// <summary>
    /// The bytes taken on this thread by every stretch measured so far, or in a scenario by those of the current run.
    /// </summary>
    public long Bytes { get; private set; }

    /// <summary>
    /// Whether to run the measured work (again): true when a stretch begins, and after an attempt that is in doubt
    /// (a garbage collection paused it and it read more than zero). Each call closes the attempt the previous one
    /// opened; when that attempt is not in doubt, what it took is added to <see cref="Bytes"/> and the stretch ends.
    /// </summary>
    public bool Measuring()
    {
        if (!_open)
        {
            _attempt = 1;
            Open();
            return true;
        }

        var bytes = Close(out var doubtful);
        if (!doubtful)
        {
            Bytes += bytes;
            return false;
        }

        if (_attempt == Attempts)
            Assert.Fail($"A garbage collection paused this thread during each of {Attempts} attempts at a measurement, and each read more than zero, so none of them tells what the code allocates. The last one read {bytes} bytes.");
        _attempt++;
        Open();
        return true;
    }

    /// <summary>
    /// Whether to run the scenario (again): true before the first run, and after a run in which a step measured with
    /// <see cref="Start"/> and <see cref="Stop"/> is in doubt. Counts each run from zero.
    /// </summary>
    public bool Run()
    {
        Assert.False(_open, "a measured step was started and not stopped");
        if (_runs > 0 && !_doubtful)
            return false;
        if (_runs == Attempts)
            Assert.Fail($"A garbage collection paused this thread during a measured step that read more than zero in each of {Attempts} runs, so none of them tells what the code allocates. The last run read {Bytes} bytes.");
        _runs++;
        _doubtful = false;
        Bytes = 0;
        return true;
    }

    /// <summary>Opens a measured step of a scenario.</summary>
    public void Start() => Open();

    /// <summary>Closes the measured step of a scenario and adds what it took to <see cref="Bytes"/>.</summary>
    public void Stop()
    {
        Bytes += Close(out var doubtful);
        _doubtful |= doubtful;
    }

    private void Open()
    {
        Assert.False(_open, "a measured stretch was opened twice");
        _open = true;

        // The pause readings come first here and last in Close, so any pause between the two byte readings shows.
        _pauses = GC.GetTotalPauseDuration();
        _collections = GC.CollectionCount(0);
        _start = GC.GetAllocatedBytesForCurrentThread();
    }

    private long Close(out bool doubtful)
    {
        var end = GC.GetAllocatedBytesForCurrentThread();
        var paused = GC.GetTotalPauseDuration() != _pauses || GC.CollectionCount(0) != _collections;
        Assert.True(_open, "a measured stretch was closed without being opened");
        _open = false;
        var bytes = end - _start;
        doubtful = paused && bytes > 0;
        return bytes;
    }
}
