using Prospect.Engine.Diagnostics;

namespace Prospect.Game.Persistence;

/// <summary>
/// The one thread that uses a world's store, and the one capture buffer it shares with the tick. The buffer has
/// exactly one owner at a time and moves in a circle: free, then the tick's while it copies the world into it,
/// then the worker's while it is written, then free again. The tick only ever takes it when it is free, so a
/// save that is slow delays the next one and nothing queues up behind it; the worker only ever reads it while
/// it is its own, so it never sees a world half copied or changed under it. The hand-over in each direction is
/// one volatile write after the last touch, which is what makes the other side's reads safe.
/// <para>
/// Everything the worker learns is published as one immutable <see cref="Report"/>, swapped whole: reading it
/// from the tick or the interface takes no lock and no memory. The worker disposes the store itself, on its own
/// thread, when it is told to stop and has written what it holds. Its thread is a background thread: a store
/// that never returns cannot keep the process from exiting.
/// </para>
/// </summary>
internal sealed class SaveWorker
{
    /// <summary>Who owns the capture buffer.</summary>
    internal enum Owner
    {
        Free,
        Capturing,
        Worker,
    }

    /// <summary>What the worker knows, as of its last commit.</summary>
    /// <param name="Generation">The generation of the last completed save, or the one the world was opened at.</param>
    /// <param name="SavedTick">The world interval that save begins at; -1 before this run's first.</param>
    /// <param name="SavedRevision">
    /// The world's count of changes that save holds. It is the capture's, not the newest: a save of an older
    /// state that completes late does not make a world that has changed since look saved.
    /// </param>
    /// <param name="SavedAtUnixMs">When that save was durable.</param>
    /// <param name="Failures">Commits that failed since the last that succeeded.</param>
    /// <param name="Error">Why the newest commit failed.</param>
    /// <param name="Halted">True once the store refused for good; nothing is committed after that.</param>
    /// <param name="Backup">How the newest recovery copy ended, its generation and its error.</param>
    /// <param name="FinalDone">True once the final save has been tried as often as allowed, or succeeded.</param>
    /// <param name="FinalCommitted">Whether it succeeded.</param>
    /// <param name="Checkpoint">How making the store portable went after the final commit.</param>
    /// <param name="FinalBackup">How the recovery copy of the final state ended.</param>
    /// <param name="CheckpointError">Why the final checkpoint failed, independently of the commit and backup.</param>
    /// <param name="CloseError">Combined maintenance diagnostics, prepared off the tick and frame threads.</param>
    /// <param name="Unknown">True once a commit ended without the store knowing whether it happened; always with <paramref name="Halted"/>.</param>
    internal sealed record Report(
        long Generation,
        long SavedTick,
        long SavedRevision,
        long SavedAtUnixMs,
        int Failures,
        string? Error,
        bool Halted,
        BackupOutcome Backup,
        long BackupGeneration,
        string? BackupError,
        bool FinalDone,
        bool FinalCommitted,
        CheckpointOutcome Checkpoint,
        BackupOutcome FinalBackup,
        string? CheckpointError = null,
        string? CloseError = null,
        bool Unknown = false,
        long OperationId = 0,
        CommitOutcome? Outcome = null
    );

    private readonly IWorldStore _store;
    private readonly WorldSnapshot _buffer;
    private readonly TimeProvider _clock;
    private readonly int _finalAttempts;
    private readonly int _finalRetryMilliseconds;
    private readonly long _backupInterval;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);

    private int _owner;
    private volatile bool _stop;
    private volatile Report _report;

    // Written by the tick before it hands the buffer over, read by the worker after it has it.
    private bool _final;
    private long _revision;
    private long _operationId;

    // The worker's own.
    private long _generation;
    private bool _backedUp;
    private long _lastBackup;

    /// <summary>
    /// A worker over a store and a capture buffer of the world's limits, taken here, once. Its thread starts at
    /// once and waits; it costs nothing until a capture is handed to it.
    /// </summary>
    public SaveWorker(WorldPersistence world, TimeProvider clock, int charts = 0)
    {
        _store = world.Store;
        _buffer = new WorldSnapshot(world.Limits, charts);
        _clock = clock;
        _generation = world.Generation;
        _finalAttempts = Math.Max(1, world.FinalAttempts);
        _finalRetryMilliseconds = Math.Max(0, world.FinalRetryMilliseconds);
        _backupInterval = (long)(world.BackupSeconds * clock.TimestampFrequency);
        _report = new Report(world.Generation, -1, 0, 0, 0, null, false, BackupOutcome.NotAttempted, 0, null, false, false, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted);
        _thread = new Thread(Run) { Name = "Save", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The worker's newest report. Safe from any thread; takes no memory.</summary>
    public Report Latest => _report;

    /// <summary>Who owns the buffer right now. Safe from any thread.</summary>
    public Owner BufferOwner => (Owner)Volatile.Read(ref _owner);

    /// <summary>
    /// Takes the buffer for a capture if it is free. False while an earlier capture is still being written:
    /// the caller tries again later and captures the state the world has then. Tick thread only.
    /// </summary>
    public bool TryBeginCapture(out WorldSnapshot buffer)
    {
        buffer = _buffer;
        return Interlocked.CompareExchange(ref _owner, (int)Owner.Capturing, (int)Owner.Free) == (int)Owner.Free;
    }

    /// <summary>
    /// Hands a filled buffer to the worker. <paramref name="revision"/> is the world's count of changes the
    /// capture holds. A <paramref name="final"/> capture is committed as the world's clean close, and tried
    /// again a bounded number of times if it fails. Tick thread only, after <see cref="TryBeginCapture"/>.
    /// </summary>
    public long Submit(bool final, long revision)
    {
        var operation = ++_operationId;
        _final = final;
        _revision = revision;
        Volatile.Write(ref _owner, (int)Owner.Worker);
        _wake.Set();
        return operation;
    }

    /// <summary>Gives the buffer back unwritten, for a capture that could not be completed. Tick thread only.</summary>
    public void Abandon() => Volatile.Write(ref _owner, (int)Owner.Free);

    /// <summary>
    /// Tells the worker to stop once it has written the capture it holds, if any, and to dispose the store.
    /// Nothing is captured for it here: what the world looked like when it stopped is the caller's business.
    /// </summary>
    public void RequestStop()
    {
        _stop = true;
        _wake.Set();
    }

    /// <summary>Waits for the worker to stop, at most this long. False if it has not: the store did not return.</summary>
    public bool Join(int milliseconds) => _thread.Join(milliseconds);

    private void Run()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne();
                if (Volatile.Read(ref _owner) == (int)Owner.Worker)
                {
                    Write();

                    // Only now, after the last read of the buffer and with the report out, is it the tick's again.
                    Volatile.Write(ref _owner, (int)Owner.Free);
                }

                if (_stop && Volatile.Read(ref _owner) != (int)Owner.Worker)
                    return;
            }
        }
        finally
        {
            try
            {
                _store.Dispose();
            }
            catch (Exception e)
            {
                Logs.Sim.Warning($"Closing the world store failed: {e.Message}");
            }
        }
    }

    private void Write()
    {
        var final = _final;
        var previous = _report;
        var committed = false;
        var halted = previous.Halted;
        var unknown = previous.Unknown;
        var error = previous.Error;
        // A halted worker may receive a capture raced with its last report; no attempted commit means no success.
        var result = new CommitResult(unknown ? CommitOutcome.Unknown : previous.Outcome is CommitOutcome.Conflict ? CommitOutcome.Conflict : CommitOutcome.Rejected,
            _generation, _buffer.NextWorldTick, _clock.GetUtcNow(), Error: error);

        // The final state does not change while it is tried again, so the same capture is committed. An
        // ordinary save is tried once: by the time it would be retried the world has moved on, and the tick
        // captures that newer state instead.
        for (var attempt = 1; !halted; attempt++)
        {
            try
            {
                result = _store.Commit(_buffer, _generation, final);
            }
            catch (Exception e)
            {
                // A store says "failed" when it knows nothing changed. One that throws has said nothing, so
                // nothing is assumed: not that the save happened, not that it did not.
                result = new CommitResult(CommitOutcome.Unknown, _generation, _buffer.NextWorldTick, _clock.GetUtcNow(), Error: e.Message);
            }

            if (result.Outcome == CommitOutcome.Committed)
            {
                committed = true;
                error = null;
                _generation = result.Generation;
                break;
            }

            error = result.Error ?? result.Outcome.ToString();
            unknown |= result.Outcome == CommitOutcome.Unknown;
            if (result.Outcome is CommitOutcome.Conflict or CommitOutcome.Rejected or CommitOutcome.Unknown)
                halted = true;
            if (halted || !final || attempt >= _finalAttempts)
                break;
            Thread.Sleep(_finalRetryMilliseconds);
        }

        // Publish the exact commit before maintenance, but retain the buffer until Write returns.
        _report = previous with
        {
            Generation = committed ? result.Generation : previous.Generation,
            SavedTick = committed ? result.CapturedTick : previous.SavedTick,
            SavedRevision = committed ? _revision : previous.SavedRevision,
            SavedAtUnixMs = committed ? result.CompletedAt.ToUnixTimeMilliseconds() : previous.SavedAtUnixMs,
            Failures = committed ? 0 : previous.Failures + 1,
            Error = error,
            Halted = halted,
            Unknown = unknown,
            OperationId = _operationId,
            Outcome = result.Outcome,
        };

        var backup = previous.Backup;
        var backupGeneration = previous.BackupGeneration;
        var backupError = previous.BackupError;
        var finalBackup = BackupOutcome.NotAttempted;
        var now = _clock.GetTimestamp();
        if (committed && (final || !_backedUp || now - _lastBackup >= _backupInterval))
        {
            BackupResult copy;
            try
            {
                copy = _store.Backup(final ? BackupReason.Final : _backedUp ? BackupReason.Interval : BackupReason.Initial);
            }
            catch (Exception e)
            {
                copy = new BackupResult(BackupOutcome.Failed, backupGeneration, e.Message);
            }

            backup = copy.Outcome;
            backupError = copy.Outcome == BackupOutcome.Failed ? copy.Error ?? "the recovery copy failed" : null;
            if (copy.Outcome == BackupOutcome.Completed)
                backupGeneration = copy.Generation;

            // A copy that failed is tried again after the next commit, not after another interval.
            if (copy.Outcome != BackupOutcome.Failed)
            {
                _backedUp = true;
                _lastBackup = now;
            }

            if (final)
                finalBackup = copy.Outcome;
        }

        if (!committed)
            Logs.Sim.Warning($"Saving the world failed{(final ? " at its close" : "")}: {error}");
        else if (backup == BackupOutcome.Failed)
            Logs.Sim.Warning($"The world is saved, but its recovery copy failed: {backupError}");

        var checkpointError = final && committed && result.Checkpoint == CheckpointOutcome.Failed
            ? result.Error ?? "The final checkpoint failed."
            : previous.CheckpointError;
        var closeError = checkpointError is null ? backupError : backupError is null ? checkpointError : $"{checkpointError}; {backupError}";
        _report = new Report(
            committed ? result.Generation : previous.Generation,
            committed ? result.CapturedTick : previous.SavedTick,
            committed ? _revision : previous.SavedRevision,
            committed ? result.CompletedAt.ToUnixTimeMilliseconds() : previous.SavedAtUnixMs,
            committed ? 0 : previous.Failures + 1,
            error,
            halted,
            backup,
            backupGeneration,
            backupError,
            previous.FinalDone || final,
            previous.FinalCommitted || (final && committed),
            final && committed ? result.Checkpoint : previous.Checkpoint,
            final ? finalBackup : previous.FinalBackup,
            checkpointError,
            closeError,
            unknown,
            _operationId,
            result.Outcome
        );
    }
}
