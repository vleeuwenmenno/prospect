using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    // Edits taken into the bricks on the simulation thread in one tick at most; a rock with more waiting is
    // replayed on its own thread instead, so a long list that comes into view never holds a tick up.
    private const int RockEditsInlinePerTick = 8;

    private RockEdits? _rockEdits;
    private RockEditReplay? _rockReplay;
    private bool _rockEditsBehind;

    /// <summary>The edits the server has sent of the rocks in view; the render side reads it as it grows. Null before the galaxy is known.</summary>
    internal RockEditLog? RockEditLog => _rockEdits?.Log;

    /// <summary>Whether every edit received is in the bricks that prediction collides with.</summary>
    internal bool RockEditsSettled => !_rockEditsBehind;

    private void ResetRockEdits()
    {
        _rockReplay?.Dispose();
        _rockEdits = new RockEdits(_content.RockEdits);
        _rockReplay = new RockEditReplay(_rockEdits.Log);
        _rockEditsBehind = false;
        if (_rocks is not null) _rocks.Edits = _rockEdits;
    }

    /// <summary>
    /// Takes a run of a rock's edits from the server. The run must continue what is held: edits already held are
    /// passed over, a run that starts beyond them is dropped whole. Every edit is checked against the rules and the
    /// rock this galaxy has there; one that fails drops the run.
    /// </summary>
    internal void OnRockEdits(ref NetReader reader)
    {
        Span<RockScrape> edits = stackalloc RockScrape[RockEditsMessage.MaxEdits];
        if (_rocks is null || _rockEdits is null || !RockEditsMessage.TryRead(ref reader, out var rock, out var first, edits, out var count))
            return;
        if ((uint)rock.Index >= (uint)_rocks.Galaxy.MaxAsteroidsPerSector) return;
        var sector = _rocks.Sector(rock.Sector);
        if (rock.Index >= sector.AsteroidCount || !RockEditRules.Scrapable(sector.Asteroids[rock.Index].Shape)) return;
        var log = _rockEdits.Log;
        var slot = log.SlotOf(rock);
        var held = slot < 0 ? 0 : log.EditsOf(slot);
        if (first > held) return;
        var radius = sector.Asteroids[rock.Index].Radius;
        for (var i = 0; i < count; i++)
            if (!_content.RockEdits.Valid(edits[i], radius)) return;
        for (var i = held - first; i < count; i++)
        {
            if (log.Append(rock, edits[i]) < 0) break;
            _rockEditsBehind = true;
        }
    }

    // Brings the bricks up to the edits received: a few inline, a long list on the worker.
    private void TickRockEdits()
    {
        if (!_rockEditsBehind || _rockEdits is null || _rockReplay is null) return;
        var log = _rockEdits.Log;
        var bricks = _rockEdits.Bricks;
        var limit = log.Count;
        if (_rockReplay.TakeDone(out var replayed))
            bricks.CopyRock(_rockReplay.Bricks, replayed);
        var budget = RockEditsInlinePerTick;
        var behind = false;
        for (var slot = 0; slot < log.RockCount; slot++)
        {
            var waiting = log.EditsOf(slot) - bricks.Applied(slot);
            if (waiting == 0) continue;
            if (_rockReplay.Running(slot))
            {
                behind = true;
                continue;
            }

            if (waiting <= budget)
            {
                budget -= waiting;
                for (var next = bricks.NextPending(slot, limit); next >= 0; next = bricks.NextPending(slot, limit))
                    bricks.Apply(next);
                continue;
            }

            behind = true;
            if (waiting > RockEditsInlinePerTick) _rockReplay.Start(slot, limit);
        }

        _rockEditsBehind = behind;
    }
}

/// <summary>
/// Works a rock's whole edit list into bricks of its own on a thread of its own, for the simulation thread to copy in
/// when it is done. One rock at a time; starting and collecting take no memory.
/// <para>
/// The thread is made with the replay, so with the session, and sleeps between rocks. It is not a pool thread: the
/// pool makes a worker the first time it needs one more, which took about a kilobyte in the middle of play on a
/// machine of four cores or fewer. It sleeps on a plain object's monitor, whose waits take no managed memory.
/// </para>
/// </summary>
internal sealed class RockEditReplay : IDisposable
{
    private const int Idle = 0, Busy = 1, Done = 2;
    private readonly RockEditLog _log;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private int _state;
    private int _slot = -1;
    private int _limit;
    private bool _work;
    private bool _stop;

    /// <summary>A replay with room for the most bricks one rock may hold, and its thread, asleep.</summary>
    public RockEditReplay(RockEditLog log)
    {
        _log = log;
        Bricks = new RockBricks(log, log.Rules.MaxBricksPerRock);
        _thread = new Thread(Run) { Name = "RockReplay", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The worker's own bricks. Read them only between <see cref="TakeDone"/> and the next <see cref="Start"/>.</summary>
    public RockBricks Bricks { get; }

    /// <summary>Whether the worker is on this rock, or done with it and not collected yet.</summary>
    public bool Running(int slot) => Volatile.Read(ref _state) != Idle && _slot == slot;

    /// <summary>Starts replaying a rock's edits below a count read from the log. Does nothing while another replay is out.</summary>
    public void Start(int slot, int limit)
    {
        if (Interlocked.CompareExchange(ref _state, Busy, Idle) != Idle) return;

        // The thread is asleep until it is told of work, so its bricks are this thread's to clear.
        if (_slot >= 0) Bricks.Clear(_slot);
        _slot = slot;
        _limit = limit;
        lock (_gate)
        {
            _work = true;
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>True once, when a replay has finished: <paramref name="slot"/> is the rock whose bricks are ready.</summary>
    public bool TakeDone(out int slot)
    {
        slot = _slot;
        return Interlocked.CompareExchange(ref _state, Idle, Done) == Done;
    }

    /// <summary>Ends the thread, after the rock it is on.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            Monitor.Pulse(_gate);
        }

        _thread.Join();
    }

    private void Run()
    {
        while (true)
        {
            lock (_gate)
            {
                while (!_work && !_stop) Monitor.Wait(_gate);
                if (_stop) return;
                _work = false;
            }

            for (var next = Bricks.NextPending(_slot, _limit); next >= 0; next = Bricks.NextPending(_slot, _limit))
                Bricks.Apply(next);
            Volatile.Write(ref _state, Done);
        }
    }
}
