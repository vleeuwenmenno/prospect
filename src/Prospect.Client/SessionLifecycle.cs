using System.Diagnostics;
using Prospect.Engine.Diagnostics;
using Prospect.Game.Client;
using Prospect.Game.Persistence;

namespace Prospect.Client;

/// <summary>What opening a session came to, on the worker that opened it: a session, or a sentence for the player.</summary>
/// <param name="Session">The session, started; null when it could not be opened.</param>
/// <param name="Error">Why it could not be opened; empty when it was.</param>
internal readonly record struct SessionStart(GameSession? Session, string Error)
{
    /// <summary>
    /// For a saved world: completes once the world's store has been disposed, which is when its lease is free
    /// again. Null for a session that holds no world.
    /// </summary>
    public Task? Released { get; init; }

    /// <summary>The library folder of the world the session runs, or null.</summary>
    public string? Folder { get; init; }

    /// <summary>A session that could not be opened.</summary>
    public static SessionStart Failed(string error) => new(null, error);

    /// <summary>An open that stopped because it was no longer wanted, having left nothing behind.</summary>
    public static SessionStart Cancelled => new(null, "Cancelled.");
}

/// <summary>Where the application's play session stands.</summary>
internal enum SessionPhase : byte
{
    /// <summary>No session, nothing on its way.</summary>
    Idle,

    /// <summary>A session runs and the frame reads it.</summary>
    Playing,

    /// <summary>A session is being opened on a worker: a world read from disk, a server started, a socket opened.</summary>
    Opening,

    /// <summary>A session was asked to close and is saving; the frame no longer reads it.</summary>
    Closing,

    /// <summary>A closed session is being disposed on a worker, which releases its world.</summary>
    Releasing,

    /// <summary>
    /// A session is asked for, and waits until a world this game let go of without waiting (see
    /// <see cref="SessionLifecycle.StopWaiting"/>) has really been released.
    /// </summary>
    Waiting,
}

/// <summary>What became of a session that was closed, for the player, when there is something to tell.</summary>
/// <param name="Title">What the session was: the world's name or the server's address.</param>
/// <param name="Status">How its close ended.</param>
/// <param name="Fault">Why it stopped, when it stopped by itself; null when the player ended it.</param>
/// <param name="WasQuitting">True when the close was for leaving the game, which is held back until the player has read this.</param>
internal sealed record CloseReport(string Title, CloseStatus Status, string? Fault, bool WasQuitting)
{
    /// <summary>True when the world's files could not be confirmed released: disposing its store failed.</summary>
    public bool ReleaseFailed { get; init; }

    /// <summary>True when the session was one the player had stopped waiting for, and it closed later.</summary>
    public bool Late { get; init; }
}

/// <summary>The headline of a close report: what can be said for certain about the save.</summary>
internal enum CloseHeadline : byte
{
    /// <summary>The session stopped by itself; the body says what became of the save.</summary>
    Stopped,

    /// <summary>The final save is committed, with a warning (its tidy-up, its recovery copy or its release).</summary>
    SavedWithWarning,

    /// <summary>The final save is known not to have been written.</summary>
    NotSaved,

    /// <summary>Whether the final save was written is not known: <see cref="FinalCommitOutcome.Unknown"/> or <see cref="FinalCommitOutcome.Abandoned"/>.</summary>
    Unconfirmed,
}

/// <summary>
/// The application's one play session, from opening to release. Every way the player ends a session goes
/// through here: leaving, quitting, closing the window, a session thread's fault, and replacing it with another
/// (a new world, a loaded one, a join). The rules it keeps:
/// <list type="bullet">
/// <item>Nothing that touches a disk or the network runs on the frame: opening and disposing run on workers.</item>
/// <item>A session is closed with <see cref="GameSession.BeginClose"/>, which freezes and saves a local world
/// before its client is let go; the frame stops reading it at that moment, before anything of it is disposed.</item>
/// <item>A new session is opened only after the old one has closed and its world has been released (its lease
/// too). A world let go of without waiting is tracked until it really is released, and an open waits for it
/// unless the player explicitly opens anyway.</item>
/// <item>One piece of background work at a time. A request that comes while one runs waits, and only the latest
/// waits. An open that is no longer wanted is cancelled; a session it still produced is closed again, exactly
/// once, never left running, also when nobody waits for it any more, and how that close went is reported as
/// any other.</item>
/// <item>At most <see cref="MaxLingering"/> pieces of work let go of without waiting at once. At that bound
/// nothing more is opened or let go of; the player can still cancel, and quit.</item>
/// <item>A close that did not save cleanly is reported and stops what was to follow (a replacement or quitting)
/// until the player has acknowledged it.</item>
/// <item>Nothing waits without an end the player can reach: work that does not stop when asked offers
/// <see cref="StopWaiting"/> after <see cref="PatienceSeconds"/>.</item>
/// </list>
/// Used from the main thread only, through <see cref="Update"/> once a frame. It allocates only when the phase
/// changes. An exception that ends the application's own loop goes to <see cref="Dispose"/>, which saves
/// nothing.
/// </summary>
internal sealed class SessionLifecycle : IDisposable
{
    /// <summary>How long work that was asked to stop may take before the player is offered to stop waiting for it.</summary>
    public const double PatienceSeconds = 10;

    /// <summary>
    /// The most sessions or worlds let go of without waiting that may be outstanding at once. Each can hold a
    /// server's threads and a world's capture buffer until it finishes, so their number is bounded, not their pace.
    /// </summary>
    public const int MaxLingering = 2;

    /// <summary>
    /// The most reports that can wait at once: two (a close and a release) for each piece of work let go of without
    /// waiting, which quitting may raise by one past <see cref="MaxLingering"/>, plus the foreground session's one.
    /// A late outcome that finds no room stays with its work until there is, and the foreground always has room.
    /// </summary>
    private const int MaxReports = 2 * (MaxLingering + 1) + 1;

    /// <summary>How long a session nobody wants any more is given to save and close before it is disposed regardless.</summary>
    private const int UnwantedCloseMilliseconds = 30_000;

    private readonly Func<long> _clock;
    private GameSession? _playing;
    private Task? _playingReleased;
    private string? _playingFolder;
    private string _playingTitle = "";
    private GameSession? _closing;
    private Task? _closingReleased;
    private string? _closingFolder;
    private string _closingTitle = "";
    private string? _closingFault;
    private long _stageSince;
    private Task<CloseStatus>? _release;
    private Task<SessionStart>? _opening;
    private CancellationTokenSource? _openingCancel;
    private Request? _openingRequest;
    private bool _openingCancelled;
    private Request? _next;
    private bool _openAnyway;
    private bool _quit;
    private bool _quitAnyway;
    private readonly List<Lingering> _lingering = [];
    private readonly Queue<CloseReport> _reports = new(MaxReports);

    /// <summary>A lifecycle timed by the stopwatch.</summary>
    public SessionLifecycle()
        : this(Stopwatch.GetTimestamp) { }

    /// <summary>A lifecycle timed by <paramref name="clock"/>, in <see cref="Stopwatch"/> ticks; for tests.</summary>
    internal SessionLifecycle(Func<long> clock) => _clock = clock;

    /// <summary>How long <see cref="Dispose"/> waits for background work before handing it to its own cleanup.</summary>
    internal TimeSpan DisposeWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Called by <see cref="Dispose"/> with the open right after its wait ran out, before it is handed over; for tests.</summary>
    internal Action<Task>? DisposeWaitElapsed { get; init; }

    /// <summary>The session the frame may read, or null. Never a session that is closing or being opened.</summary>
    public GameSession? Session => _playing;

    /// <summary>What the lifecycle is doing.</summary>
    public SessionPhase Phase { get; private set; }

    /// <summary>True while something runs in the background or a request waits: nothing else may be asked of a session meanwhile.</summary>
    public bool Busy => Phase is SessionPhase.Opening or SessionPhase.Closing or SessionPhase.Releasing or SessionPhase.Waiting;

    /// <summary>True while the session being opened was cancelled, and its worker has not returned yet.</summary>
    public bool Cancelling => Phase == SessionPhase.Opening && _openingCancelled;

    /// <summary>The name of what is being opened, closed, waited for or played, for the screen that shows it.</summary>
    public string Activity =>
        Phase switch
        {
            SessionPhase.Opening => _openingRequest?.Title ?? "",
            SessionPhase.Waiting => _next?.Title ?? ReleasingTitle ?? "",
            SessionPhase.Closing or SessionPhase.Releasing => _closingTitle,
            SessionPhase.Playing => _playingTitle,
            _ => "",
        };

    /// <summary>The name of a world this game let go of without waiting and that is not released yet, or null.</summary>
    public string? ReleasingTitle => _lingering.Count > 0 ? _lingering[0].Title : null;

    /// <summary>How many sessions or worlds let go of without waiting are still being closed or released.</summary>
    public int LingeringCount => _lingering.Count;

    /// <summary>True when <see cref="MaxLingering"/> pieces of work let go of without waiting are outstanding: nothing more is started or let go of until one ends.</summary>
    public bool AtLimit => _lingering.Count >= MaxLingering;

    /// <summary>True when a world folder is still held by a session this game let go of without waiting.</summary>
    public bool IsReleasing(string folder)
    {
        foreach (var lingering in _lingering)
        {
            if (lingering.Folder == folder && !lingering.Released.IsCompleted)
                return true;
        }

        return false;
    }

    /// <summary>How the closing session's saves stand; off when nothing is closing. Takes no memory.</summary>
    public SaveStatus ClosingSave => Phase == SessionPhase.Closing && _closing is { } closing ? closing.Save : SaveStatus.Off;

    /// <summary>How the close stands; running when nothing is closing. Takes no memory.</summary>
    public CloseStatus ClosingStatus => Phase == SessionPhase.Closing && _closing is { } closing ? closing.Close : CloseStatus.Running;

    /// <summary>True once the current close, cancelled open or wait has taken <see cref="PatienceSeconds"/> or more.</summary>
    public bool Patient =>
        (Phase is SessionPhase.Closing or SessionPhase.Waiting || Cancelling) && Stopwatch.GetElapsedTime(_stageSince, _clock()).TotalSeconds >= PatienceSeconds;

    /// <summary>
    /// True when the player may stop waiting (<see cref="StopWaiting"/>): a close, a cancelled open or a wait for a
    /// release has taken <see cref="PatienceSeconds"/> or more, and letting go of it stays within
    /// <see cref="MaxLingering"/>. When quitting, the bound does not apply: the process takes it all with it.
    /// </summary>
    public bool CanStopWaiting => Patient && (_quit || !AtLimit);

    /// <summary>True when the close in progress is for quitting the game.</summary>
    public bool Quitting => _quit;

    /// <summary>
    /// The close the player should hear about now, until a screen clears it with <see cref="Acknowledge"/>. Reports
    /// never replace one another: one that comes while another is shown waits behind it, in the order they came.
    /// </summary>
    public CloseReport? Report => _reports.Count > 0 ? _reports.Peek() : null;

    /// <summary>How many reports wait, the one shown included.</summary>
    public int PendingReports => _reports.Count;

    /// <summary>
    /// True when the game may exit: quitting was asked, every session was closed and disposed, no report waits to
    /// be read, and work let go of without waiting has finished, or the player chose to quit without it.
    /// </summary>
    public bool QuitReady => _quit && Phase == SessionPhase.Idle && Report is null && (_quitAnyway || _lingering.Count == 0);

    /// <summary>Completes when every piece of work let go of without waiting has closed and been released; for tests.</summary>
    internal Task Settled => Task.WhenAll(_lingering.ConvertAll(static lingering => Task.WhenAll(lingering.Closed, lingering.Released)));

    /// <summary>
    /// Asks for a session in place of the current one, if any. The current one is closed and released first, and
    /// one still being opened is cancelled; then <paramref name="open"/> runs on a worker. On success the session
    /// is played; otherwise <paramref name="failed"/> is called on the main thread with the sentence. A later
    /// request before this one has started replaces it. Ignored while quitting or while a report waits.
    /// </summary>
    /// <param name="title">What is being opened, for the screen that shows it.</param>
    /// <param name="folder">The library folder it opens when that is known beforehand, or null.</param>
    /// <param name="open">Opens the session on a worker; should give up when its token is cancelled. An exception is caught and reported.</param>
    /// <param name="failed">Called on the main thread when the session could not be opened.</param>
    public void Enter(string title, string? folder, Func<CancellationToken, SessionStart> open, Action<string> failed)
    {
        if (_quit || Report is not null)
            return;
        _next = new Request(title, folder, open, failed);
        _openAnyway = false;
        if (_playing is not null)
            BeginClosing(null);
        else if (Phase == SessionPhase.Opening)
            CancelOpening();
        Update();
    }

    /// <summary>Closes the session that is played, cancels one that is being opened or drops a waiting request. Nothing follows it.</summary>
    public void Leave()
    {
        _next = null;
        if (Phase == SessionPhase.Opening)
            CancelOpening();
        else if (_playing is not null)
            BeginClosing(null);
        else if (Phase == SessionPhase.Waiting && !_quit)
            Phase = SessionPhase.Idle;
    }

    /// <summary>Closes a session that stopped by itself, and reports why with how its close ended.</summary>
    public void Fail(string fault)
    {
        _next = null;
        if (_playing is not null)
            BeginClosing(fault);
    }

    /// <summary>
    /// Closes everything for leaving the game; <see cref="QuitReady"/> says when that is done. A report that
    /// waits is kept, marked as holding back quitting: only <see cref="Acknowledge"/> clears it. A second call
    /// changes nothing.
    /// </summary>
    public void Quit()
    {
        _quit = true;
        _next = null;
        MarkQuitting(true);
        if (_playing is not null)
            BeginClosing(null);
        else if (Phase == SessionPhase.Opening)
            CancelOpening();
        Update();
    }

    /// <summary>
    /// Clears the report shown. When it held back quitting, <paramref name="quit"/> says whether to quit after all;
    /// either way the next report that waits is shown before anything else happens, and quitting is only done once
    /// every report is read and the work let go of without waiting has ended (see <see cref="QuitReady"/>).
    /// </summary>
    public void Acknowledge(bool quit = false)
    {
        if (_reports.Count > 0)
            _reports.Dequeue();
        MarkQuitting(quit);
        if (_reports.Count > 0)
            return;
        _quit = quit;
        if (!quit)
            _quitAnyway = false;
    }

    /// <summary>Marks every waiting report as holding back quitting, or not. Only on a request, never per frame.</summary>
    private void MarkQuitting(bool quitting)
    {
        for (var i = _reports.Count; i > 0; i--)
        {
            var report = _reports.Dequeue();
            _reports.Enqueue(report.WasQuitting == quitting ? report : report with { WasQuitting = quitting });
        }
    }

    /// <summary>
    /// Stops waiting, once <see cref="CanStopWaiting"/>. For a close: the session is disposed now and its close is
    /// reported as it then stands (abandoned, when its final save had not finished); a world whose save worker
    /// still holds it stays tracked as releasing. For a cancelled open: its worker is left to finish, and whatever
    /// it opens is closed and released by itself, and reported. For a wait on a release: the request is opened
    /// anyway, or, when quitting, the game quits without waiting.
    /// </summary>
    public void StopWaiting()
    {
        if (!CanStopWaiting)
            return;
        switch (Phase)
        {
            case SessionPhase.Closing:
                BeginRelease();
                break;
            case SessionPhase.Opening:
                AbandonOpening();
                Phase = SessionPhase.Idle;
                break;
            case SessionPhase.Waiting when _quit:
                _quitAnyway = true;
                break;
            case SessionPhase.Waiting:
                _openAnyway = true;
                break;
        }

        Update();
    }

    /// <summary>
    /// Moves the lifecycle on: called once a frame on the main thread, before the frame reads <see cref="Session"/>,
    /// also while nothing is drawn. Returns true in the frame a requested session has just started to be played.
    /// </summary>
    public bool Update()
    {
        for (var i = _lingering.Count - 1; i >= 0; i--)
        {
            if (Settle(_lingering[i]))
                _lingering.RemoveAt(i);
        }

        var entered = false;
        if (Phase == SessionPhase.Closing && _closing!.Close.Completed)
            BeginRelease();

        if (Phase == SessionPhase.Releasing && _release!.IsCompleted)
            EndRelease();

        if (Phase == SessionPhase.Opening && _opening!.IsCompleted)
            entered = EndOpening();

        if (Phase is SessionPhase.Idle or SessionPhase.Waiting)
        {
            var wait = _quit
                ? Report is null && _lingering.Count > 0 && !_quitAnyway
                : _next is not null && Report is null && !(_lingering.Count == 0 || (_openAnyway && _lingering.Count < MaxLingering));
            if (wait)
            {
                if (Phase != SessionPhase.Waiting)
                {
                    Phase = SessionPhase.Waiting;
                    _stageSince = _clock();
                }
            }
            else if (!_quit && _next is not null && Report is null)
            {
                StartOpening();
            }
            else
            {
                Phase = SessionPhase.Idle;
            }
        }

        return entered;
    }

    /// <summary>
    /// Ends whatever is left without a final save: for an application whose own loop ended by an exception. A
    /// session that is played is disposed, which loses what was not saved yet. Background work is waited for a
    /// bounded time; an open whose wait runs out is handed to its own cleanup, which closes and disposes whatever
    /// it produces, exactly once, also when it finished just as the wait ran out. That cleanup is not guaranteed
    /// to finish before the process ends.
    /// </summary>
    public void Dispose()
    {
        _next = null;
        _quit = true;
        if (_playing is { } playing)
        {
            _playing = null;
            DisposeQuietly(playing);
        }

        if (_opening is { } opening)
        {
            _openingCancel?.Cancel();

            // One decision on the wait's result: a timeout always hands the open over, even when it completes
            // in between, so that exactly one party owns what it produces.
            if (opening.Wait(DisposeWait))
            {
                if (opening.Result.Session is { } opened)
                    DisposeQuietly(opened);
                _opening = null;
            }
            else
            {
                DisposeWaitElapsed?.Invoke(opening);
                AbandonOpening();
            }
        }

        if (_closing is { } closing && _release is null)
            DisposeQuietly(closing);
        _release?.Wait(DisposeWait);
        _closing = null;
        _release = null;
        Phase = SessionPhase.Idle;
        try
        {
            Settled.Wait(DisposeWait);
        }
        catch (AggregateException e)
        {
            // A cleanup that failed has already said so in the log; the application is going down regardless.
            Logs.App.Error($"Cleaning up after the game failed: {e.InnerException}");
        }
    }

    private static SessionStart Open(Request request, CancellationToken token)
    {
        try
        {
            return request.Open(token);
        }
        catch (OperationCanceledException)
        {
            return SessionStart.Cancelled;
        }
        catch (Exception e)
        {
            Logs.App.Error($"Opening '{request.Title}' failed: {e}");
            return SessionStart.Failed(e is IOException or InvalidDataException ? e.Message : "The game could not be started.");
        }
    }

    // Apart from Update, so the closure it needs is made only when a session is opened, not on every frame.
    private void StartOpening()
    {
        var next = _next!;
        _next = null;
        _openAnyway = false;
        _openingRequest = next;
        _openingCancelled = false;
        _openingCancel = new CancellationTokenSource();
        var token = _openingCancel.Token;
        Phase = SessionPhase.Opening;
        _opening = Task.Run(() => Open(next, token));
    }

    private void CancelOpening()
    {
        if (!_openingCancelled)
        {
            _openingCancelled = true;
            _stageSince = _clock();
        }

        _openingCancel?.Cancel();
    }

    /// <summary>
    /// Hands the running open to its own cleanup and forgets it: whatever session it produces is closed and
    /// released on a worker, exactly once. How that close went comes back through <see cref="Update"/> and is
    /// reported as any other; the world is tracked as releasing until its store is disposed.
    /// </summary>
    private void AbandonOpening()
    {
        var opening = _opening!;
        var request = _openingRequest!;
        _opening = null;
        _openingRequest = null;
        _openingCancel = null;
        Logs.App.Warning($"Stopped waiting for '{request.Title}' to open; it is closed again when it does");
        var closed = opening.ContinueWith(static task => CloseUnwanted(task.Result), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        var released = opening.ContinueWith(static task => task.Result.Released ?? Task.CompletedTask, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _lingering.Add(new Lingering(request.Title, request.Folder, closed, released));
    }

    /// <summary>
    /// Closes a session nobody wants: saved as any other close, for a bounded time, then disposed. Runs on a
    /// worker. Returns how the close ended, or null when there was no session or nothing of it was saved.
    /// </summary>
    private static CloseStatus? CloseUnwanted(SessionStart start)
    {
        if (start.Session is not { } session)
            return null;
        session.BeginClose();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * UnwantedCloseMilliseconds / 1000;
        while (!session.Close.Completed && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(20);
        DisposeQuietly(session);
        var status = session.Close;
        Logs.App.Info($"A session nobody waited for closed with {status.FinalCommit}");
        return status.FinalCommit == FinalCommitOutcome.NotPersisted ? null : status;
    }

    /// <summary>
    /// Takes what a piece of work let go of without waiting has come to: its close is reported when it needs to be,
    /// and a release that failed is reported too, since the world's files may still be held. True once it is over
    /// and told. A report waits while another is shown.
    /// </summary>
    private bool Settle(Lingering lingering)
    {
        // The last place is the foreground session's: a late outcome without room waits with its work.
        var room = _reports.Count < MaxReports - 1;
        if (!lingering.CloseTold && lingering.Closed.IsCompleted)
        {
            if (!room)
                return false;
            lingering.CloseTold = true;
            CloseStatus? status = lingering.Closed.IsCompletedSuccessfully
                ? lingering.Closed.Result
                : new CloseStatus(ClosePhase.Completed, FinalCommitOutcome.Unknown, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, "Closing it failed.");
            lingering.Status = status;
            if (status is { } closed && (!closed.Clean || closed.Degraded))
                Publish(new CloseReport(lingering.Title, closed, null, _quit) { Late = true });
        }

        if (!lingering.CloseTold || !lingering.Released.IsCompleted)
            return false;
        if (lingering.Released.IsCompletedSuccessfully)
            return true;
        if (_reports.Count >= MaxReports - 1)
            return false;

        // The store's own dispose failed: whether its lease is free is not known, so it is not claimed to be.
        Logs.App.Error($"'{lingering.Title}' could not be confirmed released: {lingering.Released.Exception}");
        Publish(new CloseReport(lingering.Title, lingering.Status ?? CloseStatus.Running with { Phase = ClosePhase.Completed }, null, _quit) { ReleaseFailed = true, Late = true });
        return true;
    }

    /// <summary>Adds a report behind any that wait: what was to follow waits for the player, quitting included.</summary>
    private void Publish(CloseReport report)
    {
        Logs.App.Warning($"'{report.Title}' closed with {report.Status.FinalCommit}: {report.Status.Error}");
        var quitting = _quit || (_reports.Count > 0 && _reports.Peek().WasQuitting);
        _reports.Enqueue(report.WasQuitting == quitting ? report : report with { WasQuitting = quitting });
        _next = null;
        _quit = false;
        _quitAnyway = false;
    }

    private bool EndOpening()
    {
        var request = _openingRequest!;
        var result = _opening!.Result;
        var cancelled = _openingCancelled;
        _opening = null;
        _openingRequest = null;
        _openingCancel = null;
        Phase = SessionPhase.Idle;

        if (result.Session is { } session)
        {
            _playing = session;
            _playingTitle = request.Title;
            _playingReleased = result.Released;
            _playingFolder = result.Folder;
            if (cancelled || _quit || _next is not null)
            {
                // Nobody wants this session any more, but it may hold a world: close it as any other.
                BeginClosing(null);
                return false;
            }

            Phase = SessionPhase.Playing;
            return true;
        }

        if (!cancelled && !_quit && _next is null)
            request.Failed(_lingering.Count > 0 ? $"{result.Error} A world this game closed without waiting may still be releasing; try again in a moment." : result.Error);
        return false;
    }

    private void BeginClosing(string? fault)
    {
        var session = _playing!;
        _playing = null;
        _closing = session;
        _closingTitle = _playingTitle;
        _closingReleased = _playingReleased;
        _closingFolder = _playingFolder;
        _playingReleased = null;
        _playingFolder = null;
        _closingFault = fault;
        _stageSince = _clock();
        Phase = SessionPhase.Closing;

        // The world is frozen and saved before its client is let go: see GameSession.BeginClose.
        session.BeginClose();
    }

    private void BeginRelease()
    {
        var session = _closing!;
        Phase = SessionPhase.Releasing;
        _release = Task.Run(() =>
        {
            DisposeQuietly(session);

            // Read after disposing: a close that was abandoned only knows that now.
            return session.Close;
        });
    }

    private void EndRelease()
    {
        var status = _release!.Result;
        var released = _closingReleased;
        _release = null;
        _closing = null;
        _closingReleased = null;
        Phase = SessionPhase.Idle;

        // A save worker that did not stop in time still holds the world: it is released when the worker ends.
        var releaseFailed = false;
        if (released is not null)
        {
            if (!released.IsCompleted)
                _lingering.Add(new Lingering(_closingTitle, _closingFolder, Task.FromResult<CloseStatus?>(null), released) { CloseTold = true, Status = status });
            else
                releaseFailed = !released.IsCompletedSuccessfully;
        }
        _closingFolder = null;

        var persisted = status.FinalCommit != FinalCommitOutcome.NotPersisted;
        if (_closingFault is null && !releaseFailed && (!persisted || (status.Clean && !status.Degraded)))
            return;
        if (releaseFailed)
            Logs.App.Error($"'{_closingTitle}' could not be confirmed released: {released!.Exception}");

        // Something to tell: what was to follow waits for the player.
        Publish(new CloseReport(_closingTitle, status, _closingFault, _quit) { ReleaseFailed = releaseFailed });
    }

    /// <summary>The headline a close report gets: certain only where the outcome is.</summary>
    public static CloseHeadline Headline(CloseReport report)
    {
        if (report.Fault is not null)
            return CloseHeadline.Stopped;
        return report.Status.FinalCommit switch
        {
            FinalCommitOutcome.Committed => CloseHeadline.SavedWithWarning,
            FinalCommitOutcome.Unknown or FinalCommitOutcome.Abandoned => CloseHeadline.Unconfirmed,
            _ when report.ReleaseFailed => CloseHeadline.Unconfirmed,
            _ => CloseHeadline.NotSaved,
        };
    }

    private static void DisposeQuietly(GameSession session)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception e)
        {
            Logs.App.Error($"Disposing a session failed: {e}");
        }
    }

    private sealed record Request(string Title, string? Folder, Func<CancellationToken, SessionStart> Open, Action<string> Failed);

    /// <summary>A piece of work let go of without waiting: how its close ended, and when its world is released.</summary>
    private sealed class Lingering(string title, string? folder, Task<CloseStatus?> closed, Task released)
    {
        public string Title { get; } = title;
        public string? Folder { get; } = folder;
        public Task<CloseStatus?> Closed { get; } = closed;
        public Task Released { get; } = released;
        public CloseStatus? Status { get; set; }
        public bool CloseTold { get; set; }
    }
}
