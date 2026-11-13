using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Prospect.Client;
using Prospect.Game.Client;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Saved worlds as the client plays them: the library on a real disk (a fresh temporary folder per test), and
/// the session lifecycle that opens, closes and replaces sessions around real servers. Nothing here touches the
/// player's own saves.
/// </summary>
public sealed class SavedWorldPlayTests : IDisposable
{
    private static readonly GameContent Content = GameContent.Load();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prospect-worlds-" + Guid.NewGuid().ToString("N"));
    private readonly WorldLibrary _library;
    private readonly SessionLifecycle _lifecycle;
    private readonly List<string> _failures = [];

    // Added to the lifecycle's clock, so a test can make it impatient without waiting.
    private long _skew;

    public SavedWorldPlayTests()
    {
        _library = new WorldLibrary(_root);
        _lifecycle = new SessionLifecycle(() => Stopwatch.GetTimestamp() + Volatile.Read(ref _skew));
    }

    public void Dispose()
    {
        _lifecycle.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private SaveCompatibility Expected => SaveCompatibility.Of(Content);

    [Fact]
    public void FolderIdsAreOnePlainFolderAndNeverAPath()
    {
        Assert.True(WorldLibrary.IsFolderId(WorldLibrary.NewFolderId(Guid.NewGuid())));
        Assert.True(WorldLibrary.IsFolderId("copied world"));
        foreach (var bad in new[] { "", ".", "..", "a/b", "a\\b", "../x", " x", "x ", "c:", "a\0b", new string('a', WorldLibrary.MaxFolderIdLength + 1) })
            Assert.False(WorldLibrary.IsFolderId(bad), bad);
        Assert.Throws<ArgumentException>(() => _library.FolderOf(".."));
    }

    [Fact]
    public void ANewWorldIsSavedWithItsChoicesAndListedAsTheLastWorldOnlyOnceItRuns()
    {
        var settings = Content.SettingCatalog.Defaults;
        var changed = Content.SettingCatalog.Definitions.ToArray().First(d => d.Kind != SettingKind.Choice && !d.IsUpper && d.Upper is null && d.Maximum > d.Default);
        settings.Set(changed, changed.Maximum);
        var spawn = new SectorCoord(8, 0, -8);

        Assert.Equal(WorldList.Empty.Worlds, _library.Scan(Expected).Worlds);
        Enter("Alpha", cancel => LocalWorlds.Create(_library, Content, "Alpha", settings, spawn, 30, "Tester", cancel));
        var session = Play();
        Assert.True(session.IsPersistent);
        var snapshot = WaitFor(session, s => s.State == SessionState.Active && s.OwnHull >= 0);
        Assert.Equal(changed.Maximum, snapshot.Content.Settings.Get(changed));

        var folder = Assert.Single(Directory.GetDirectories(_root));
        Assert.Equal(Path.GetFileName(folder), _library.LastWorld());

        // While it runs it is listed as in use, with its reason; the folder name is its id, not its name.
        var running = Assert.Single(_library.Scan(Expected).Worlds);
        Assert.Equal(WorldAvailability.Busy, running.Availability);
        Assert.NotEmpty(running.Reason);

        LeaveAndSettle();
        Assert.Null(_lifecycle.Report);

        // Closed cleanly and released: listed as ready under its name, and it opens with its own seed, settings
        // and spawn sector whatever the launch would have said.
        var listed = Assert.Single(_library.Scan(Expected).Worlds);
        Assert.Equal((WorldAvailability.Ready, "Alpha", true), (listed.Availability, listed.Name, listed.CleanClose));
        Assert.NotEmpty(listed.SavedText);
        var opened = _library.Open(listed.FolderId, Expected, TestContext.Current.CancellationToken);
        Assert.Equal(OpenOutcome.Opened, opened.Outcome);
        using (opened.Store)
        {
            Assert.Equal(spawn, opened.Snapshot!.SpawnSector);
            Assert.Equal(new ServerConfig().GalaxySeed, opened.Snapshot.Seed);
            Assert.Contains(changed.Maximum, opened.Snapshot.SettingValues.AsSpan(0, opened.Snapshot.SettingCount).ToArray());
            Assert.True(opened.WasCleanClose);
        }

        // Continued through the client: the open path takes nothing from the launch, so the world's seed, settings
        // and spawn sector are what the client plays by, whatever the next New Game would have used.
        Enter("Alpha", cancel => LocalWorlds.Continue(_library, Content, 30, "Tester", cancel));
        var continued = Play();
        var played = WaitFor(continued, s => s.State == SessionState.Active && s.OwnHull >= 0 && s.Galaxy is not null);
        Assert.Equal(changed.Maximum, played.Content.Settings.Get(changed));
        Assert.Equal(new ServerConfig().GalaxySeed, played.Galaxy!.Seed);
        Assert.Equal(spawn, played.Galaxy.SectorOf(played.Hulls[played.OwnHull].Position));
        LeaveAndSettle();
    }

    [Fact]
    public void ASaveTimeThatCannotBeADateMarksThatWorldDamagedAndListsTheRest()
    {
        var good = CreateClosed("Good");
        var bad = CreateClosed("Bad");
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, bad, SqliteWorldStore.DatabaseFile)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE world SET saved_ms = 9223372036854775807";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var list = _library.Scan(Expected);
        Assert.Null(list.Error);
        Assert.Equal(bad, list.LastWorld);
        Assert.Equal(WorldAvailability.Ready, list.Find(good)!.Availability);
        Assert.Equal(WorldAvailability.Damaged, list.Find(bad)!.Availability);
    }

    [Fact]
    public void ReplacingASessionWaitsForTheOldWorldToBeSavedAndReleased()
    {
        Enter("Alpha", cancel => LocalWorlds.Create(_library, Content, "Alpha", Content.SettingCatalog.Defaults, default, 30, "Tester", cancel));
        var first = Play();
        WaitFor(first, s => s.State == SessionState.Active && s.OwnHull >= 0);
        var folder = _library.LastWorld()!;

        // Opening the very world that runs: only possible once the running one has closed and let go of it.
        Enter("Alpha", cancel => LocalWorlds.Open(_library, Content, folder, 30, "Tester", cancel));
        Assert.Null(_lifecycle.Session);
        Assert.True(_lifecycle.Busy);
        Assert.False(first.Close.Phase == ClosePhase.Open);
        var second = Play();
        Assert.NotSame(first, second);
        Assert.Empty(_failures);
        Assert.Null(_lifecycle.Report);
        Assert.Equal(FinalCommitOutcome.Committed, first.Close.FinalCommit);
        WaitFor(second, s => s.State == SessionState.Active && s.OwnHull >= 0);
        LeaveAndSettle();
    }

    [Fact]
    public void AWorldThatCannotBeOpenedIsReportedLeftAsItIsAndHoldsNoLease()
    {
        // A damaged world: listed with its reason, refused on open, its bytes untouched.
        var damaged = Path.Combine(_root, "broken");
        Directory.CreateDirectory(damaged);
        var garbage = new byte[4096];
        Random.Shared.NextBytes(garbage);
        File.WriteAllBytes(Path.Combine(damaged, SqliteWorldStore.DatabaseFile), garbage);

        var listed = Assert.Single(_library.Scan(Expected).Worlds);
        Assert.Equal(WorldAvailability.Damaged, listed.Availability);
        Assert.False(listed.Ready);

        Enter("broken", cancel => LocalWorlds.Open(_library, Content, "broken", 30, "Tester", cancel));
        Settle();
        Assert.Null(_lifecycle.Session);
        Assert.Equal(WorldLibrary.Explain(OpenOutcome.Damaged), Assert.Single(_failures));
        Assert.Equal(garbage, File.ReadAllBytes(Path.Combine(damaged, SqliteWorldStore.DatabaseFile)));
        Assert.Null(_library.LastWorld());

        // A last world that is gone is reported, and nothing is made in its place.
        _library.RememberLastWorld("gone");
        _failures.Clear();
        Enter("gone", cancel => LocalWorlds.Continue(_library, Content, 30, "Tester", cancel));
        Settle();
        Assert.Equal(WorldLibrary.Explain(OpenOutcome.Missing), Assert.Single(_failures));
        Assert.False(Directory.Exists(Path.Combine(_root, "gone")));

        // A world saved with other content: listed as such, refused, and still openable by the content it had.
        _failures.Clear();
        var alpha = CreateClosed("Alpha");
        var other = Expected with { GeneratorVersion = Expected.GeneratorVersion + 1 };
        Assert.Equal(WorldAvailability.Incompatible, _library.Scan(other).Worlds.Single(w => w.FolderId == alpha).Availability);
        var refused = _library.Open(alpha, other, TestContext.Current.CancellationToken);
        Assert.Equal(OpenOutcome.Incompatible, refused.Outcome);
        var reopened = _library.Open(alpha, Expected, TestContext.Current.CancellationToken);
        Assert.Equal(OpenOutcome.Opened, reopened.Outcome);
        reopened.Store!.Dispose();
    }

    [Fact]
    public void ACancelledOpenStopsBeforeTheDiskOrClosesWhatItAlreadyOpened()
    {
        var alpha = CreateClosed("Alpha");
        var generation = Summary(alpha).Generation;

        // Cancelled before the world is read: nothing is leased, opened or written.
        using (var gate = new ManualResetEventSlim())
        {
            Enter("Alpha", cancel =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return LocalWorlds.Open(_library, Content, alpha, 30, "Tester", cancel);
            });
            Assert.Equal(SessionPhase.Opening, _lifecycle.Phase);
            _lifecycle.Leave();
            Assert.True(_lifecycle.Cancelling);
            gate.Set();
            Settle();
        }

        Assert.Null(_lifecycle.Session);
        Assert.Empty(_failures);
        Assert.Equal((generation, true), (Summary(alpha).Generation, Summary(alpha).WasCleanClose));

        // An open that ignores the cancel: the session it still produces is closed again, unseen, and saved.
        using (var gate = new ManualResetEventSlim())
        {
            Enter("Alpha", _ =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return LocalWorlds.Open(_library, Content, alpha, 30, "Tester");
            });
            _lifecycle.Leave();
            gate.Set();
            Settle();
        }

        Assert.Null(_lifecycle.Session);
        Assert.Empty(_failures);
        Assert.Null(_lifecycle.Report);
        Assert.True(Summary(alpha).WasCleanClose);
        Assert.True(Summary(alpha).Generation > generation);
    }

    [Fact]
    public void AnOpenThatDoesNotStopCanBeAbandonedAndItsSessionIsClosedOnceWhenItReturns()
    {
        var alpha = CreateClosed("Alpha");
        using var gate = new ManualResetEventSlim();
        GameSession? late = null;
        Enter("Alpha", _ =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            var start = LocalWorlds.Open(_library, Content, alpha, 30, "Tester");
            late = start.Session;
            return start;
        });

        // Cancelling does not end the wait by itself, nor does stopping too early; after the patience it may.
        _lifecycle.Leave();
        _lifecycle.StopWaiting();
        Assert.True(_lifecycle.Cancelling);
        Volatile.Write(ref _skew, Stopwatch.Frequency * (long)(SessionLifecycle.PatienceSeconds + 1));
        Assert.True(_lifecycle.CanStopWaiting);
        _lifecycle.StopWaiting();
        Assert.Equal((SessionPhase.Idle, 1), (_lifecycle.Phase, _lifecycle.LingeringCount));

        // The same world is asked for again: it waits for the abandoned open, not misreported as in use.
        var opens = 0;
        Enter("Alpha", cancel =>
        {
            Interlocked.Increment(ref opens);
            return LocalWorlds.Open(_library, Content, alpha, 30, "Tester", cancel);
        });
        Assert.Equal(SessionPhase.Waiting, _lifecycle.Phase);
        _lifecycle.Update();
        Assert.Equal(0, Volatile.Read(ref opens));

        gate.Set();
        var second = Play();
        Assert.Equal(1, opens);
        Assert.NotNull(late);
        Assert.NotSame(late, second);
        Assert.Equal(FinalCommitOutcome.Committed, late!.Close.FinalCommit);
        Assert.Empty(_failures);

        // A late close that went well, and its release, tell the player nothing.
        Until(() => _lifecycle.LingeringCount == 0);
        Assert.Null(_lifecycle.Report);
        LeaveAndSettle();
    }

    [Fact]
    public async Task DisposingTheApplicationHandsALateOpenToItsOwnCleanup()
    {
        var alpha = CreateClosed("Alpha");
        using var gate = new ManualResetEventSlim();
        var lifecycle = new SessionLifecycle { DisposeWait = TimeSpan.FromMilliseconds(100) };
        lifecycle.Enter("Alpha", null, _ =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return LocalWorlds.Open(_library, Content, alpha, 30, "Tester");
        }, _failures.Add);

        lifecycle.Dispose();
        Assert.Equal(1, lifecycle.LingeringCount);
        var settled = lifecycle.Settled;
        Assert.False(settled.IsCompleted);

        // The open finishes long after: its session is closed and its world released without anyone waiting.
        gate.Set();
        await settled.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var reopened = _library.Open(alpha, Expected, TestContext.Current.CancellationToken);
        Assert.Equal(OpenOutcome.Opened, reopened.Outcome);
        reopened.Store!.Dispose();
        Assert.Empty(_failures);
    }

    [Fact]
    public void AFinalSaveThatFailsIsReportedAndHoldsBackQuitting()
    {
        // A store in memory that refuses every commit, behind a real server and client.
        var folder = SaveRig.NewWorld(Content);
        folder.FailCommits = 1000;
        Enter("Memory", Memory(folder));
        var session = Play();
        WaitFor(session, s => s.State == SessionState.Active && s.OwnHull >= 0);

        _lifecycle.Quit();
        Assert.Null(_lifecycle.Session);
        Settle();
        var report = Assert.IsType<CloseReport>(_lifecycle.Report);
        Assert.Equal(FinalCommitOutcome.Failed, report.Status.FinalCommit);
        Assert.True(report.WasQuitting);
        Assert.False(_lifecycle.QuitReady);
        Assert.False(folder.Leased);

        // Closing the window again, or asking for another world, does not get past the report.
        _lifecycle.Quit();
        _lifecycle.Update();
        Assert.Equal(report, _lifecycle.Report);
        Assert.False(_lifecycle.QuitReady);
        _lifecycle.Enter("Other", null, _ => SessionStart.Failed("must not run"), _failures.Add);
        _lifecycle.Update();
        Assert.Equal(SessionPhase.Idle, _lifecycle.Phase);

        _lifecycle.Acknowledge(quit: true);
        Assert.True(_lifecycle.QuitReady);
        Assert.Empty(_failures);
    }

    [Fact]
    public void AReplacementWaitsBehindAFailedCloseAndIsDropped()
    {
        var folder = SaveRig.NewWorld(Content);
        Enter("Memory", Memory(folder));
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);

        folder.FailCommits = 1000;
        var opens = 0;
        Enter("Next", _ =>
        {
            Interlocked.Increment(ref opens);
            return SessionStart.Failed("must not run");
        });
        Settle();
        Assert.Equal(FinalCommitOutcome.Failed, _lifecycle.Report!.Status.FinalCommit);
        Assert.False(_lifecycle.Report.WasQuitting);
        Assert.Equal((0, SessionPhase.Idle), (opens, _lifecycle.Phase));
        Assert.Null(_lifecycle.Session);
        Assert.Empty(_failures);
    }

    [Fact]
    public void AnUnknownOrDegradedCloseIsReportedForWhatItIs()
    {
        var unknown = SaveRig.NewWorld(Content);
        Enter("Unknown", Memory(unknown));
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);
        unknown.FailWith = CommitOutcome.Unknown;
        unknown.FailCommits = 1000;
        LeaveAndSettle();
        Assert.Equal(FinalCommitOutcome.Unknown, _lifecycle.Report!.Status.FinalCommit);
        _lifecycle.Acknowledge();

        var degraded = SaveRig.NewWorld(Content);
        degraded.Checkpoint = CheckpointOutcome.Failed;
        Enter("Degraded", Memory(degraded));
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);
        LeaveAndSettle();
        var report = _lifecycle.Report!;
        Assert.Equal((FinalCommitOutcome.Committed, true, CheckpointOutcome.Failed), (report.Status.FinalCommit, report.Status.Degraded, report.Status.Checkpoint));
        Assert.True(degraded.Clean);
        _lifecycle.Acknowledge();
    }

    [Fact]
    public void AClosePlayerStoppedWaitingForKeepsItsWorldReleasingUntilTheWorkerLetsGo()
    {
        var folder = SaveRig.NewWorld(Content);
        Enter("Memory", Memory(folder));
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);

        // The final save hangs inside the store.
        using var gate = new ManualResetEventSlim();
        folder.Entered.Reset();
        folder.Gate = gate;
        _lifecycle.Leave();
        Assert.Equal(SessionPhase.Closing, _lifecycle.Phase);
        Assert.False(_lifecycle.CanStopWaiting);
        Volatile.Write(ref _skew, Stopwatch.Frequency * (long)(SessionLifecycle.PatienceSeconds + 1));
        Assert.True(_lifecycle.CanStopWaiting);
        _lifecycle.StopWaiting();
        Settle();

        // Reported as abandoned, and the world is not claimed to be free: the worker still holds it.
        Assert.Equal(FinalCommitOutcome.Abandoned, _lifecycle.Report!.Status.FinalCommit);
        Assert.Equal(1, _lifecycle.LingeringCount);
        Assert.True(folder.Leased);
        _lifecycle.Acknowledge();

        // Opening it again waits for the release instead of failing as busy.
        var opens = 0;
        var memory = Memory(folder);
        Enter("Memory", cancel =>
        {
            Interlocked.Increment(ref opens);
            return memory(cancel);
        });
        _lifecycle.Update();
        Assert.Equal((SessionPhase.Waiting, 0), (_lifecycle.Phase, Volatile.Read(ref opens)));

        folder.Gate = null;
        gate.Set();
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);
        Assert.Equal(1, opens);
        Assert.Equal(0, _lifecycle.LingeringCount);
        Assert.Empty(_failures);
        LeaveAndSettle();
    }

    [Fact]
    public async Task AnOpenThatFinishesJustAsTheEmergencyWaitRunsOutIsClosedOnceByItsCleanup()
    {
        var folder = SaveRig.NewWorld(Content);
        using var gate = new ManualResetEventSlim();
        CountingStore? counted = null;
        GameSession? produced = null;

        // The open completes after the wait has given up and before it is handed over: the race the handover covers.
        var lifecycle = new SessionLifecycle
        {
            DisposeWait = TimeSpan.FromMilliseconds(50),
            DisposeWaitElapsed = opening =>
            {
                gate.Set();
                opening.Wait(TimeSpan.FromSeconds(20));
            },
        };
        lifecycle.Enter("Memory", null, Memory(folder, gate, store => counted = new CountingStore(store), start => produced = start.Session), _failures.Add);
        lifecycle.Dispose();

        await lifecycle.Settled.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(produced);
        Assert.Equal(1, counted!.Disposes);
        Assert.Equal(1, folder.FinalCommits);
        Assert.Equal(FinalCommitOutcome.Committed, produced!.Close.FinalCommit);
        Assert.False(folder.Leased);
        Assert.Empty(_failures);
    }

    [Theory]
    [InlineData(CommitOutcome.Failed, false)]
    [InlineData(CommitOutcome.Unknown, true)]
    public void ALateCloseOfAnAbandonedOpenIsReportedAndHoldsBackWhatFollows(CommitOutcome outcome, bool quit)
    {
        var folder = SaveRig.NewWorld(Content);
        using var gate = new ManualResetEventSlim();
        Enter("Late", Memory(folder, gate));
        _lifecycle.Leave();
        Impatient();
        _lifecycle.StopWaiting();
        Assert.Equal((SessionPhase.Idle, 1), (_lifecycle.Phase, _lifecycle.LingeringCount));

        // Something waits behind it: another world, or quitting.
        var opens = 0;
        if (quit)
            _lifecycle.Quit();
        else
            Enter("Next", _ =>
            {
                Interlocked.Increment(ref opens);
                return SessionStart.Failed("must not run");
            });
        Assert.Equal(SessionPhase.Waiting, _lifecycle.Phase);
        Assert.False(_lifecycle.QuitReady);

        // The abandoned open comes back with a session whose final save does not go through.
        folder.FailWith = outcome;
        folder.FailCommits = 1000;
        gate.Set();
        Until(() => _lifecycle.Report is not null);
        var report = _lifecycle.Report!;
        Assert.True(report.Late);
        Assert.Equal(outcome == CommitOutcome.Unknown ? FinalCommitOutcome.Unknown : FinalCommitOutcome.Failed, report.Status.FinalCommit);
        Assert.Equal(quit, report.WasQuitting);
        Assert.Equal((0, SessionPhase.Idle), (opens, _lifecycle.Phase));
        Assert.False(_lifecycle.QuitReady);

        _lifecycle.Acknowledge(quit);
        Until(() => _lifecycle.LingeringCount == 0);
        Assert.Equal(quit, _lifecycle.QuitReady);
        Assert.False(folder.Leased);
        Assert.Empty(_failures);
    }

    [Fact]
    public async Task WorkLetGoOfWithoutWaitingIsBoundedButCancelAndQuitStillWork()
    {
        var a = SaveRig.NewWorld(Content);
        var b = SaveRig.NewWorld(Content);
        using var gateA = new ManualResetEventSlim();
        using var gateB = new ManualResetEventSlim();

        // Twice: open, cancel, stop waiting. The second goes through Open Anyway.
        Enter("A", Memory(a, gateA));
        _lifecycle.Leave();
        Impatient();
        _lifecycle.StopWaiting();
        Enter("B", Memory(b, gateB));
        Assert.Equal(SessionPhase.Waiting, _lifecycle.Phase);
        Impatient();
        _lifecycle.StopWaiting();
        Assert.Equal(SessionPhase.Opening, _lifecycle.Phase);
        _lifecycle.Leave();
        Impatient();
        _lifecycle.StopWaiting();
        Assert.Equal(SessionLifecycle.MaxLingering, _lifecycle.LingeringCount);
        Assert.True(_lifecycle.AtLimit);

        // At the bound nothing more starts, however long the player waits; cancelling still works.
        var opens = 0;
        Func<CancellationToken, SessionStart> counted = _ =>
        {
            Interlocked.Increment(ref opens);
            return SessionStart.Failed("must not run");
        };
        Enter("C", counted);
        Impatient();
        Assert.True(_lifecycle.Patient);
        Assert.False(_lifecycle.CanStopWaiting);
        _lifecycle.StopWaiting();
        _lifecycle.Update();
        Assert.Equal((SessionPhase.Waiting, 0), (_lifecycle.Phase, opens));
        _lifecycle.Leave();
        Assert.Equal(SessionPhase.Idle, _lifecycle.Phase);

        // Quitting at the bound waits for the work, and may quit without it after the patience.
        Enter("C", counted);
        _lifecycle.Quit();
        Assert.Equal(SessionPhase.Waiting, _lifecycle.Phase);
        Assert.False(_lifecycle.QuitReady);
        Impatient();
        Assert.True(_lifecycle.CanStopWaiting);
        _lifecycle.StopWaiting();
        Assert.True(_lifecycle.QuitReady);
        Assert.Equal(0, opens);

        // The work let go of still ends by itself, its worlds released.
        gateA.Set();
        gateB.Set();
        await _lifecycle.Settled.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(a.Leased);
        Assert.False(b.Leased);
    }

    [Fact]
    public void AStoreThatCannotBeConfirmedReleasedIsReportedNotClaimedFree()
    {
        var folder = SaveRig.NewWorld(Content);
        Enter("Memory", Memory(folder, wrap: store => new CountingStore(store, throwOnDispose: true)));
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);
        LeaveAndSettle();
        var report = _lifecycle.Report!;
        Assert.True(report.ReleaseFailed);
        Assert.Equal(FinalCommitOutcome.Committed, report.Status.FinalCommit);
        Assert.Equal(CloseHeadline.SavedWithWarning, SessionLifecycle.Headline(report));
    }

    [Fact]
    public void AReportsHeadlineClaimsOnlyWhatIsCertain()
    {
        static CloseReport With(FinalCommitOutcome outcome, string? fault = null, bool releaseFailed = false) =>
            new("World", new CloseStatus(ClosePhase.Completed, outcome, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, null), fault, false) { ReleaseFailed = releaseFailed };

        Assert.Equal(CloseHeadline.Unconfirmed, SessionLifecycle.Headline(With(FinalCommitOutcome.Unknown)));
        Assert.Equal(CloseHeadline.Unconfirmed, SessionLifecycle.Headline(With(FinalCommitOutcome.Abandoned)));
        Assert.Equal(CloseHeadline.NotSaved, SessionLifecycle.Headline(With(FinalCommitOutcome.Failed)));
        Assert.Equal(CloseHeadline.NotSaved, SessionLifecycle.Headline(With(FinalCommitOutcome.SkippedAfterFault)));
        Assert.Equal(CloseHeadline.SavedWithWarning, SessionLifecycle.Headline(With(FinalCommitOutcome.Committed)));
        Assert.Equal(CloseHeadline.SavedWithWarning, SessionLifecycle.Headline(With(FinalCommitOutcome.Committed, releaseFailed: true)));
        Assert.Equal(CloseHeadline.Stopped, SessionLifecycle.Headline(With(FinalCommitOutcome.Unknown, "The game stopped unexpectedly.")));
    }

    [Fact]
    public void AFrameWithNothingToDrawStillOpensClosesAndQuits()
    {
        // The production frame entry, with a framebuffer of zero size as a minimised window has.
        var entered = 0;
        var polls = 0;
        var pump = new SessionPump(_lifecycle, new WorldShelf(null, Expected), false, () => entered++);
        Brutal.Numerics.int2 Minimised()
        {
            polls++;
            return default;
        }

        Enter("Local", _ => new SessionStart(GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Tester"), ""));
        var quit = false;
        Spin(() => pump.Next(ref quit, Minimised) == FrameStep.Skip && entered > 0);
        Assert.Equal(SessionPhase.Playing, _lifecycle.Phase);

        // Closing the window while minimised: the session closes and the loop is told to end, without drawing.
        quit = true;
        var last = FrameStep.Skip;
        Spin(() => (last = pump.Next(ref quit, Minimised)) == FrameStep.Exit);
        Assert.False(quit);
        Assert.Null(_lifecycle.Session);
        Assert.True(polls > 1);

        static void Spin(Func<bool> done)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 20;
            while (!done())
            {
                Assert.True(Stopwatch.GetTimestamp() < deadline, "The frame did not move on.");
                Thread.Sleep(5);
            }
        }
    }

    [Fact]
    public void TwoLateFailuresWhileQuittingAreBothShownBeforeTheGameExits()
    {
        var a = SaveRig.NewWorld(Content);
        var b = SaveRig.NewWorld(Content);
        using var gateA = new ManualResetEventSlim();
        using var gateB = new ManualResetEventSlim();
        AbandonTwo(a, gateA, b, gateB);

        // Quit, then both abandoned opens come back with sessions whose final saves fail.
        var pump = new SessionPump(_lifecycle, new WorldShelf(null, Expected), false, () => { });
        var quit = true;
        a.FailCommits = b.FailCommits = 1000;
        gateA.Set();
        gateB.Set();
        var titles = new List<string>();
        for (var shown = 0; shown < 2; shown++)
        {
            Assert.NotEqual(FrameStep.Exit, PumpUntil(pump, ref quit, () => _lifecycle.Report is not null));
            var report = _lifecycle.Report!;
            Assert.Equal((FinalCommitOutcome.Failed, true, true), (report.Status.FinalCommit, report.Late, report.WasQuitting));
            titles.Add(report.Title);

            // The player chooses Quit on the report; the very next production frame must not end the game while
            // another outcome is still to come or to be shown.
            _lifecycle.Acknowledge(quit: true);
            if (shown == 0)
                Assert.NotEqual(FrameStep.Exit, pump.Next(ref quit, Drawable));
        }

        Assert.Equal(new[] { "A", "B" }, titles.Order().ToArray());
        Assert.Equal(FrameStep.Exit, PumpUntil(pump, ref quit, () => false));
        Assert.Null(_lifecycle.Report);
        Assert.False(a.Leased);
        Assert.False(b.Leased);
    }

    [Fact]
    public void ALateFailureAndAForegroundFailureAreBothKept()
    {
        var late = SaveRig.NewWorld(Content);
        var foreground = SaveRig.NewWorld(Content);
        using var gate = new ManualResetEventSlim();
        Enter("Late", Memory(late, gate));
        _lifecycle.Leave();
        Impatient();
        _lifecycle.StopWaiting();

        // Open Anyway into another world, which plays while the abandoned open is still out.
        Enter("Foreground", Memory(foreground));
        Impatient();
        _lifecycle.StopWaiting();
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);

        // The late close fails while the foreground world plays; then the foreground close fails too.
        late.FailCommits = foreground.FailCommits = 1000;
        gate.Set();
        Until(() => _lifecycle.Report is not null);
        LeaveAndSettle();
        Assert.Equal(2, _lifecycle.PendingReports);

        var first = _lifecycle.Report!;
        _lifecycle.Acknowledge();
        var second = _lifecycle.Report!;
        _lifecycle.Acknowledge();
        Assert.Null(_lifecycle.Report);
        Assert.Equal(("Late", true, FinalCommitOutcome.Failed), (first.Title, first.Late, first.Status.FinalCommit));
        Assert.Equal(("Foreground", false, FinalCommitOutcome.Failed), (second.Title, second.Late, second.Status.FinalCommit));
        Settle();
        Assert.Equal(0, _lifecycle.LingeringCount);
    }

    [Fact]
    public void AReleaseThatFailsAfterTheCloseWasLetGoOfIsStillReported()
    {
        var folder = SaveRig.NewWorld(Content);
        Enter("Memory", Memory(folder, wrap: store => new CountingStore(store, throwOnDispose: true)));
        WaitFor(Play(), s => s.State == SessionState.Active && s.OwnHull >= 0);

        // The final save hangs; the player stops waiting, so the release is still pending when the close ends.
        using var gate = new ManualResetEventSlim();
        folder.Gate = gate;
        _lifecycle.Leave();
        Impatient();
        _lifecycle.StopWaiting();
        Settle();
        Assert.Equal(FinalCommitOutcome.Abandoned, _lifecycle.Report!.Status.FinalCommit);
        Assert.False(_lifecycle.Report.ReleaseFailed);
        Assert.Equal(1, _lifecycle.LingeringCount);
        _lifecycle.Acknowledge();

        // The worker finishes later and its store fails to dispose: that is told, not taken as released.
        folder.Gate = null;
        gate.Set();
        Until(() => _lifecycle.Report is not null);
        var report = _lifecycle.Report!;
        Assert.True(report.ReleaseFailed);
        Assert.True(report.Late);
        Assert.Equal(0, _lifecycle.LingeringCount);
    }

    /// <summary>Two opens that do not stop, each cancelled and let go of, the second through Open Anyway.</summary>
    private void AbandonTwo(MemoryWorld a, ManualResetEventSlim gateA, MemoryWorld b, ManualResetEventSlim gateB)
    {
        Enter("A", Memory(a, gateA));
        _lifecycle.Leave();
        Impatient();
        _lifecycle.StopWaiting();
        Enter("B", Memory(b, gateB));
        Impatient();
        _lifecycle.StopWaiting();
        _lifecycle.Leave();
        Impatient();
        _lifecycle.StopWaiting();
        Assert.Equal(2, _lifecycle.LingeringCount);
    }

    private static Brutal.Numerics.int2 Drawable() => new(1280, 720);

    /// <summary>Runs production frames until <paramref name="done"/> or the loop is told to end; returns the last step.</summary>
    private static FrameStep PumpUntil(SessionPump pump, ref bool quit, Func<bool> done)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
        while (true)
        {
            var step = pump.Next(ref quit, Drawable);
            if (step == FrameStep.Exit || done())
                return step;
            Assert.True(Stopwatch.GetTimestamp() < deadline, "The frames did not move on.");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void QuittingAnUnsavedGameNeedsNoReport()
    {
        Enter("Local", _ => new SessionStart(GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Tester"), ""));
        var session = Play();
        Assert.False(session.IsPersistent);
        _lifecycle.Quit();
        Settle();
        Assert.Null(_lifecycle.Report);
        Assert.True(_lifecycle.QuitReady);
        Assert.Equal(FinalCommitOutcome.NotPersisted, session.Close.FinalCommit);
    }

    /// <summary>
    /// Opens a world kept in memory behind a real server and client, with short retries and a short stop. It
    /// may wait at a gate first, wrap the store, and say what it produced.
    /// </summary>
    private Func<CancellationToken, SessionStart> Memory(MemoryWorld folder, ManualResetEventSlim? gate = null, Func<IWorldStore, IWorldStore>? wrap = null, Action<SessionStart>? produced = null) =>
        _ =>
        {
            gate?.Wait(TimeSpan.FromSeconds(30));
            var opened = MemoryWorldStore.Open(folder, Expected);
            if (opened.Outcome != OpenOutcome.Opened)
                return SessionStart.Failed(opened.Outcome.ToString());
            var start = LocalWorlds.Start(
                wrap?.Invoke(opened.Store!) ?? opened.Store!,
                store => new WorldPersistence(store, opened.Snapshot!, opened.Generation) { RetrySeconds = 0.05, FinalRetryMilliseconds = 1, StopTimeoutMilliseconds = 300 },
                Content,
                "Tester"
            );
            produced?.Invoke(start);
            return start;
        };

    /// <summary>Makes the lifecycle's clock jump past its patience.</summary>
    private void Impatient() => Interlocked.Add(ref _skew, Stopwatch.Frequency * (long)(SessionLifecycle.PatienceSeconds + 1));

    /// <summary>A store that counts how often it is disposed, and can fail its dispose after doing it.</summary>
    private sealed class CountingStore(IWorldStore inner, bool throwOnDispose = false) : IWorldStore
    {
        public int Disposes;

        public CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final) => inner.Commit(snapshot, expectedGeneration, final);

        public BackupResult Backup(BackupReason reason) => inner.Backup(reason);

        public void Dispose()
        {
            inner.Dispose();
            Interlocked.Increment(ref Disposes);
            if (throwOnDispose)
                throw new IOException("the lock could not be let go of");
        }
    }

    private WorldSummary Summary(string folder) => SqliteWorldStore.Summarize(_library.FolderOf(folder));

    private string CreateClosed(string name)
    {
        Enter(name, cancel => LocalWorlds.Create(_library, Content, name, Content.SettingCatalog.Defaults, default, 30, "Tester", cancel));
        Play();
        LeaveAndSettle();
        return _library.LastWorld()!;
    }

    private void Enter(string title, Func<CancellationToken, SessionStart> open) => _lifecycle.Enter(title, null, open, _failures.Add);

    /// <summary>Ticks the lifecycle as the frame does until a session is played, and returns it.</summary>
    private GameSession Play()
    {
        Until(() => _lifecycle.Phase == SessionPhase.Playing || (!_lifecycle.Busy && _failures.Count > 0));
        Assert.Empty(_failures);
        return _lifecycle.Session!;
    }

    private void LeaveAndSettle()
    {
        _lifecycle.Leave();
        Assert.Null(_lifecycle.Session);
        Settle();
    }

    /// <summary>Ticks the lifecycle until nothing runs in the background.</summary>
    private void Settle() => Until(() => !_lifecycle.Busy);

    private void Until(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 20;
        while (true)
        {
            _lifecycle.Update();
            if (condition())
                return;
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"The lifecycle is still {_lifecycle.Phase}");
            Thread.Sleep(5);
        }
    }

    private static SessionSnapshot WaitFor(GameSession session, Func<SessionSnapshot, bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var snapshot = session.Client.AcquireSnapshot();
            if (condition(snapshot))
                return snapshot;
            Assert.Null(session.Fault);
            Thread.Sleep(5);
        }

        throw new TimeoutException($"The session is still {session.Client.AcquireSnapshot().State}");
    }
}
