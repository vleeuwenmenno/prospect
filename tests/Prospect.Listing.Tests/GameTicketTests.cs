using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Prospect.Listing.Accounts;
using Xunit;
using Checkpoint = Prospect.Listing.Accounts.GameTickets.Checkpoint;

namespace Prospect.Listing.Tests;

/// <summary>
/// Game tickets through the real service: issue and redeem over HTTP, ordered against logout, eviction, expiry and
/// database trouble by pausing the service at its checkpoints, never by waiting and hoping.
/// </summary>
public sealed class GameTicketTests
{
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // How long a paused operation stays put if its test never resumes it: far longer than any test takes.
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);

    private static Task<HttpResponseMessage> Issue(AccountHost h, string token, string challenge) =>
        h.Send("POST", "/v1/game/tickets", token, new { challenge });

    private static Task<HttpResponseMessage> Redeem(AccountHost h, string ticket, string verifier) =>
        RedeemUntil(h, ticket, verifier, ListingHost.Cancel);

    // A caller that may hang up: its own token ends the request, as a game server giving up would.
    private static Task<HttpResponseMessage> RedeemUntil(AccountHost h, string ticket, string verifier, CancellationToken hangUp) =>
        h.Client.PostAsJsonAsync("/v1/game/tickets/redeem", new { ticket, verifier }, hangUp);

    private static async Task<(string Ticket, string Verifier)> Ticket(AccountHost h, string token)
    {
        var verifier = AccountCrypto.Random();
        var body = await AccountHost.Json(await Issue(h, token, AccountCrypto.Challenge(verifier)), 201);
        return (body.GetProperty("ticket").GetString()!, verifier);
    }

    private static async Task<HttpStatusCode> Status(Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        Private(response);
        return response.StatusCode;
    }

    // Every answer of a ticket route, a refusal as much as a success, is kept out of caches and referrers.
    private static void Private(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    private static int Failure(Action act) => Assert.Throws<AccountFailure>(act).Status;

    /// <summary>What the service does when it reaches a checkpoint; each step is taken by the first operation to get there.</summary>
    private sealed class Script : IDisposable
    {
        private readonly Dictionary<Checkpoint, Action> steps = [];
        private readonly ManualResetEventSlim resume = new();
        private readonly CancellationToken cancel = ListingHost.Cancel;

        internal Script(AccountHost h) => h.Tickets.Probe = at =>
        {
            Action? step;
            lock (steps)
            {
                steps.Remove(at, out step);
            }

            step?.Invoke();
        };

        internal void On(Checkpoint at, Action step)
        {
            lock (steps)
            {
                steps[at] = step;
            }
        }

        /// <summary>Holds the operation at <paramref name="at"/> until <see cref="Resume"/>; the task ends once it is there.</summary>
        internal Task Pause(Checkpoint at)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resume.Reset();
            On(at, () =>
            {
                reached.TrySetResult();
                resume.Wait(Hold, cancel);
            });
            return reached.Task.WaitAsync(Patience, cancel);
        }

        /// <summary>A task that ends when an operation passes <paramref name="at"/>.</summary>
        internal Task Passed(Checkpoint at)
        {
            var passed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            On(at, () => passed.TrySetResult());
            return passed.Task.WaitAsync(Patience, cancel);
        }

        internal void Resume() => resume.Set();

        // Never leaves a request thread of the service waiting when a test fails half way.
        public void Dispose() => resume.Set();
    }

    /// <summary>Occupies the service's one database slot, as a slow operation of another request would.</summary>
    private static async Task<Func<Task>> HoldDatabase(AccountHost h)
    {
        var cancel = ListingHost.Cancel;
        var store = h.Store;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leave = new ManualResetEventSlim();
        var holding = Task.Run(() =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    store.Run(_ =>
                    {
                        entered.TrySetResult();
                        leave.Wait(Hold, cancel);
                        return 0;
                    });
                    return;
                }
                catch (AccountFailure)
                {
                    // The service's own sweep had the slot for a moment.
                    Thread.Yield();
                }
            }
        }, cancel);
        await entered.Task.WaitAsync(Patience, cancel);
        return async () =>
        {
            leave.Set();
            await holding;
        };
    }

    [Fact]
    public async Task TicketNamesTheAccountOnceAndNeverItsFriendCode()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var verifier = AccountCrypto.Random();
        using var issued = await Issue(h, a.Token, AccountCrypto.Challenge(verifier));
        Private(issued);
        var grant = await AccountHost.Json(issued, 201);
        Assert.Equal(new[] { "ticket", "expiresIn" }, grant.EnumerateObject().Select(p => p.Name));
        Assert.Equal(30, grant.GetProperty("expiresIn").GetInt32());
        var ticket = grant.GetProperty("ticket").GetString()!;
        Assert.True(AccountCrypto.Canonical(ticket));
        Assert.NotEqual(a.Token, ticket);
        Assert.Equal(1, h.Tickets.Count);

        using var redeemed = await Redeem(h, ticket, verifier);
        Private(redeemed);
        var body = await AccountHost.Json(redeemed);
        Assert.Equal(new[] { "account" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "id", "name" }, body.GetProperty("account").EnumerateObject().Select(p => p.Name));
        Assert.Equal(a.Account.Id, body.GetProperty("account").GetProperty("id").GetString());
        Assert.Equal(a.Account.Name, body.GetProperty("account").GetProperty("name").GetString());
        Assert.DoesNotContain(a.Account.FriendCode, body.GetRawText());

        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, ticket, verifier)));
        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(0, h.Tickets.Accounts);

        // The session is untouched by its tickets, and nothing secret was written to the log.
        Assert.Equal(HttpStatusCode.OK, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
        var logs = string.Join('\n', h.Logs);
        foreach (var secret in new[] { a.Token, ticket, verifier, AccountCrypto.Challenge(verifier) })
            Assert.DoesNotContain(secret, logs);
    }

    [Fact]
    public async Task WrongVerifierOrUnknownTicketIsTheSameRefusalAndLeavesTheTicketUsable()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        using var wrong = await Redeem(h, t.Ticket, AccountCrypto.Random());
        using var unknown = await Redeem(h, AccountCrypto.Random(), t.Verifier);
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(ListingHost.Cancel), await wrong.Content.ReadAsStringAsync(ListingHost.Cancel));

        // The challenge is not the verifier, and the ticket is not its own verifier.
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, AccountCrypto.Challenge(t.Verifier))));
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, t.Ticket)));
        Assert.Equal(1, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public async Task ParallelRedemptionsOfAHealthyTicketSucceedExactlyOnce()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Redeem(h, t.Ticket, t.Verifier)));
        var statuses = new List<HttpStatusCode>();
        foreach (var reply in replies)
        {
            // 503 says only that the ticket was held, or the database busy, at that instant. Every answer is in by
            // now, so nothing holds the ticket any more and a retry is settled one way or the other.
            var status = reply.StatusCode;
            for (var tries = 0; status == HttpStatusCode.ServiceUnavailable && tries < 50; tries++)
                status = await Status(Redeem(h, t.Ticket, t.Verifier));
            statuses.Add(status);
        }

        Assert.Single(statuses, s => s == HttpStatusCode.OK);
        Assert.Equal(7, statuses.Count(s => s == HttpStatusCode.NotFound));
        Assert.Equal(0, h.Tickets.Count);
    }

    [Fact]
    public async Task LogoutBeforeConsumptionRefusesAndLogoutAfterItCannotTakeItBack()
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);

        // Logout commits while the redemption has reserved the ticket but not yet asked the database.
        var a = await h.Login("1");
        var t = await Ticket(h, a.Token);
        var reserved = script.Pause(Checkpoint.RedeemReserved);
        var pending = Redeem(h, t.Ticket, t.Verifier);
        await reserved;
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/accounts/sessions/current", a.Token)).StatusCode);
        script.Resume();
        Assert.Equal(HttpStatusCode.NotFound, await Status(pending));
        Assert.Equal(0, h.Tickets.Count);

        // Logout commits after the ticket was consumed, before the answer has left the service.
        var b = await h.Login("2");
        t = await Ticket(h, b.Token);
        var settled = script.Pause(Checkpoint.RedeemSettled);
        pending = Redeem(h, t.Ticket, t.Verifier);
        await settled;
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/accounts/sessions/current", b.Token)).StatusCode);
        script.Resume();
        using var late = await pending;
        Assert.Equal(b.Account.Id, (await AccountHost.Json(late)).GetProperty("account").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("GET", "/v1/accounts/me", b.Token)).StatusCode);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("eviction")]
    [InlineData("expiry")]
    public async Task IssuanceCrossedByTheEndOfItsSessionYieldsNothingRedeemable(string ending)
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);
        var a = await h.Login("1");
        var verifier = AccountCrypto.Random();
        var authenticated = script.Pause(Checkpoint.IssueAuthenticated);
        var pending = Issue(h, a.Token, AccountCrypto.Challenge(verifier));
        await authenticated;
        switch (ending)
        {
            case "logout":
                Assert.Equal(HttpStatusCode.NoContent, (await h.Send("DELETE", "/v1/accounts/sessions/current", a.Token)).StatusCode);
                break;
            case "eviction":
                for (var i = 0; i < 10; i++) await h.Login("1");
                break;
            default:
                h.Clock.AdvanceUtc(TimeSpan.FromDays(1));
                break;
        }

        script.Resume();
        using var issued = await pending;
        Private(issued);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
        if (ending == "expiry")
        {
            // The wall clock is read again after the database: an expired session gets no ticket at all.
            Assert.Equal(HttpStatusCode.Unauthorized, issued.StatusCode);
        }
        else
        {
            // The session ended after it was read. A ticket goes out, and redemption finds the session gone.
            var ticket = (await AccountHost.Json(issued, 201)).GetProperty("ticket").GetString()!;
            Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, ticket, verifier)));
        }

        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(0, h.Tickets.Accounts);
    }

    [Fact]
    public async Task TicketLifeIsCountedFromPublicationNotFromTheRequest()
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);
        var a = await h.Login();
        var verifier = AccountCrypto.Random();
        var authenticated = script.Pause(Checkpoint.IssueAuthenticated);
        var pending = Issue(h, a.Token, AccountCrypto.Challenge(verifier));
        await authenticated;
        h.Clock.Advance(Life - TimeSpan.FromSeconds(1));
        script.Resume();
        var grant = await AccountHost.Json(await pending, 201);
        Assert.Equal(30, grant.GetProperty("expiresIn").GetInt32());
        h.Clock.Advance(Life - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, grant.GetProperty("ticket").GetString()!, verifier)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryWhileTheDatabaseWorkIsOpenRefusesTheRedemption(bool sessionExpires)
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);
        var a = await h.Login();
        var t = await Ticket(h, a.Token);

        // The session has been read as valid; only the fresh readings at consumption can still refuse.
        var read = script.Pause(Checkpoint.RedeemRead);
        var pending = Redeem(h, t.Ticket, t.Verifier);
        await read;
        if (sessionExpires) h.Clock.AdvanceUtc(TimeSpan.FromDays(1));
        else h.Clock.AdvanceMonotonic(Life);
        script.Resume();
        Assert.Equal(HttpStatusCode.NotFound, await Status(pending));
        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(sessionExpires ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
            (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
    }

    [Fact]
    public async Task TicketLifeFollowsTheMonotonicClockAndSessionExpiryTheWallClock()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();

        // The wall clock stepping back or forward neither revives nor ends a ticket.
        var t = await Ticket(h, a.Token);
        h.Clock.AdvanceUtc(TimeSpan.FromHours(-1));
        h.Clock.AdvanceMonotonic(Life - TimeSpan.FromTicks(1));
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));
        t = await Ticket(h, a.Token);
        h.Clock.AdvanceUtc(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));

        // The deadline itself is too late, whatever the wall clock says.
        t = await Ticket(h, a.Token);
        h.Clock.AdvanceUtc(TimeSpan.FromHours(-2));
        h.Clock.AdvanceMonotonic(Life);
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, t.Verifier)));
        Assert.Equal(HttpStatusCode.OK, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);

        // Ten seconds before the session ends, a ticket promises ten seconds, and the session's end ends it although
        // the monotonic clock has not moved.
        h.Clock.AdvanceUtc(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(86400 - 10));
        var verifier = AccountCrypto.Random();
        var grant = await AccountHost.Json(await Issue(h, a.Token, AccountCrypto.Challenge(verifier)), 201);
        Assert.Equal(10, grant.GetProperty("expiresIn").GetInt32());
        h.Clock.AdvanceUtc(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, grant.GetProperty("ticket").GetString()!, verifier)));
        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(Issue(h, a.Token, AccountCrypto.Challenge(verifier))));
    }

    [Fact]
    public async Task SessionMustStillBelongToTheAccountTheTicketNames()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login("1");
        var b = await h.Login("2");
        var t = await Ticket(h, a.Token);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = h.Database, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE sessions SET account=$b WHERE account=$a";
            command.Parameters.AddWithValue("$a", a.Account.Id);
            command.Parameters.AddWithValue("$b", b.Account.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        // The session is alive and valid, but for another account: the ticket names neither.
        Assert.Equal(b.Account.Id, (await AccountHost.Json(await h.Send("GET", "/v1/accounts/me", a.Token))).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, t.Verifier)));
        Assert.Equal(0, h.Tickets.Count);
    }

    [Fact]
    public async Task BusyDatabaseShedsWorkAtOnceAndKeepsTheTicket()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        var release = await HoldDatabase(h);
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Redeem(h, t.Ticket, t.Verifier)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Redeem(h, t.Ticket, t.Verifier)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Issue(h, a.Token, AccountCrypto.Random())));
        }
        finally
        {
            await release();
        }

        Assert.Equal(1, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public async Task DatabaseLockedFromOutsideKeepsTheTicket()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = h.Database, Pooling = false }.ToString()))
        {
            db.Open();
            using var transaction = db.BeginTransaction(deferred: false);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Redeem(h, t.Ticket, t.Verifier)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Issue(h, a.Token, AccountCrypto.Random())));
            Assert.Equal("ok", await h.Client.GetStringAsync("/healthz", ListingHost.Cancel));
        }

        Assert.Equal(1, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public void CommitFinalizationRunsAfterDisposalInsideTheDatabaseSlot()
    {
        var directory = Directory.CreateTempSubdirectory("prospect-commit-boundary-");
        try
        {
            var options = new AccountsOptions { DatabasePath = Path.Combine(directory.FullName, "accounts.db") };
            var store = new AccountStore(options, TimeProvider.System);
            SqliteConnection? original = null;
            var finalized = false;
            store.Run(db =>
            {
                original = db;
                AccountStore.Execute(db, "CREATE TABLE boundary(value INTEGER NOT NULL)");
                AccountStore.Execute(db, "INSERT INTO boundary VALUES(7)");
                return 7;
            }, value =>
            {
                Assert.Equal(7, value);
                Assert.NotNull(original);
                Assert.Equal(System.Data.ConnectionState.Closed, original.State);
                using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = options.DatabasePath,
                    Pooling = false,
                }.ToString());
                reader.Open();
                using var command = reader.CreateCommand();
                command.CommandText = "SELECT value FROM boundary";
                Assert.Equal(7L, command.ExecuteScalar());
                Assert.Equal(503, Failure(() => store.Run(_ => 0)));
                finalized = true;
            });
            Assert.True(finalized);

            // A deferred foreign-key violation fails at COMMIT, after the work callback has returned.
            var workReturned = false;
            finalized = false;
            Assert.Equal(503, Failure(() => store.Run(db =>
            {
                AccountStore.Execute(db, "PRAGMA defer_foreign_keys=ON");
                AccountStore.Execute(db, "INSERT INTO sessions VALUES(randomblob(32),'missing-account',0,9223372036854775807)");
                workReturned = true;
                return 0;
            }, _ => finalized = true)));
            Assert.True(workReturned);
            Assert.False(finalized);
            Assert.Equal(8, store.Run(_ => 8));

            Assert.Throws<InvalidOperationException>(() => store.Run(_ => 0,
                _ => throw new InvalidOperationException("finalizer failed")));
            Assert.Equal(9, store.Run(_ => 9));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailedTransactionKeepsTheTicketAndAnUnexpectedFaultDropsIt()
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);
        var a = await h.Login();

        // A database error before the commit: nothing was consumed, so the ticket can be tried again.
        var t = await Ticket(h, a.Token);
        script.On(Checkpoint.RedeemRead, () => throw new SqliteException("disk I/O error", 10));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Redeem(h, t.Ticket, t.Verifier)));
        Assert.Equal(1, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));

        // A fault nobody planned for: one fixed sentence, and the ticket is gone rather than in doubt.
        t = await Ticket(h, a.Token);
        script.On(Checkpoint.RedeemRead, () => throw new NotSupportedException("PRIVATE_MARKER"));
        using var fault = await Redeem(h, t.Ticket, t.Verifier);
        Private(fault);
        Assert.Equal(HttpStatusCode.InternalServerError, fault.StatusCode);
        Assert.Equal("""{"error":"Internal error."}""", await fault.Content.ReadAsStringAsync(ListingHost.Cancel));
        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, t.Verifier)));

        // The database slot was given back: the service goes on.
        t = await Ticket(h, a.Token);
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public async Task ReservationReleasedAfterTheDeadlineDoesNotOutliveTheTicket()
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        var reserved = script.Pause(Checkpoint.RedeemReserved);
        var pending = Redeem(h, t.Ticket, t.Verifier);
        await reserved;
        var release = await HoldDatabase(h);
        try
        {
            // Reserved in time, then refused by a busy database after the deadline: 503 releases, but not to reuse.
            h.Clock.AdvanceMonotonic(Life);
            script.Resume();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(pending));
        }
        finally
        {
            await release();
        }

        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public async Task LostAnswerCostsTheTicketButNeverLeavesItHalfUsed()
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        var reserved = script.Pause(Checkpoint.RedeemReserved);
        var settled = script.Passed(Checkpoint.RedeemSettled);
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(ListingHost.Cancel);
        var pending = RedeemUntil(h, t.Ticket, t.Verifier, gone.Token);
        await reserved;

        // The caller hangs up while its redemption is under way. The service finishes it all the same.
        await gone.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        script.Resume();
        await settled;
        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public async Task AbandonedRedemptionThatMeetsABusyDatabaseKeepsTheTicket()
    {
        await using var h = await AccountHost.Start();
        using var script = new Script(h);
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        var reserved = script.Pause(Checkpoint.RedeemReserved);
        var settled = script.Passed(Checkpoint.RedeemSettled);
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(ListingHost.Cancel);
        var pending = RedeemUntil(h, t.Ticket, t.Verifier, gone.Token);
        await reserved;
        var release = await HoldDatabase(h);
        try
        {
            // While the abandoned redemption holds the ticket, nobody else can use or free it.
            await gone.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Redeem(h, t.Ticket, t.Verifier)));
            script.Resume();
            await settled;
        }
        finally
        {
            await release();
        }

        // Its own operation released it, unconsumed, exactly once.
        Assert.Equal(1, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public async Task ReservedTicketPastItsDeadlineStaysChargedButNeverBlocksCleanup()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxTickets", "4"), ("Accounts:TicketsPerAccount", "3"));
        using var script = new Script(h);
        var a = await h.Login("1");
        var b = await h.Login("2");
        var head = await Ticket(h, a.Token);
        var reserved = script.Pause(Checkpoint.RedeemReserved);
        var pending = Redeem(h, head.Ticket, head.Verifier);
        await reserved;
        for (var round = 0; round < 20; round++)
        {
            // The held ticket is the oldest. Each round's tickets expire behind it and must all be reclaimed.
            for (var i = 0; i < 3; i++) await Ticket(h, b.Token);
            Assert.Equal(4, h.Tickets.Count);
            Assert.Equal(HttpStatusCode.TooManyRequests, await Status(Issue(h, a.Token, AccountCrypto.Random())));
            h.Clock.Advance(Life);
            Assert.Equal(1, h.Tickets.Sweep());
            Assert.Equal(1, h.Tickets.Accounts);
        }

        // It was reserved in time and is long expired now: the reservation bought it no extra life.
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, head.Ticket, head.Verifier)));
        script.Resume();
        Assert.Equal(HttpStatusCode.NotFound, await Status(pending));
        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(0, h.Tickets.Accounts);
    }

    [Fact]
    public async Task CapsBoundTicketsPerAccountAndInAllAndFreeOnRedemptionAndExpiry()
    {
        await using var h = await AccountHost.Start(("Accounts:MaxTickets", "3"), ("Accounts:TicketsPerAccount", "2"));
        var a = await h.Login("1");
        var b = await h.Login("2");
        var first = await Ticket(h, a.Token);
        await Ticket(h, a.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, await Status(Issue(h, a.Token, AccountCrypto.Random())));
        await Ticket(h, b.Token);

        // The account has room, the service has not.
        Assert.Equal(HttpStatusCode.TooManyRequests, await Status(Issue(h, b.Token, AccountCrypto.Random())));
        Assert.Equal(3, h.Tickets.Count);
        Assert.Equal(2, h.Tickets.Accounts);

        Assert.Equal(HttpStatusCode.OK, await Status(Redeem(h, first.Ticket, first.Verifier)));
        await Ticket(h, b.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, await Status(Issue(h, a.Token, AccountCrypto.Random())));

        // A refused request left nothing behind, and expiry frees every slot without a sweep in between.
        Assert.Equal(3, h.Tickets.Count);
        h.Clock.Advance(Life);
        await Ticket(h, a.Token);
        await Ticket(h, a.Token);
        Assert.Equal(2, h.Tickets.Count);
        Assert.Equal(1, h.Tickets.Accounts);
    }

    [Fact]
    public async Task ExpiredTicketsAreSweptWithoutAnyRequest()
    {
        await using var h = await AccountHost.Start(("Listing:SweepIntervalMilliseconds", "20"));
        var a = await h.Login();
        await Ticket(h, a.Token);
        await Ticket(h, a.Token);
        Assert.Equal(2, h.Tickets.Count);
        h.Clock.Advance(Life);
        var waited = Stopwatch.StartNew();
        while (h.Tickets.Count != 0 && waited.Elapsed < Patience) await Task.Delay(10, ListingHost.Cancel);
        Assert.Equal(0, h.Tickets.Count);
        Assert.Equal(0, h.Tickets.Accounts);
    }

    [Fact]
    public async Task TicketRoutesDrawOnTheirOwnAllowances()
    {
        await using var h = await AccountHost.Start(("Listing:TicketIssuesPerMinute", "2"), ("Listing:TicketRedeemsPerMinute", "1"));
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        await Ticket(h, a.Token);
        using (var limited = await Issue(h, a.Token, AccountCrypto.Random()))
        {
            Private(limited);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(30), limited.Headers.RetryAfter?.Delta);
        }

        // A refused redemption spends allowance like any other; the next one waits its turn.
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, AccountCrypto.Random())));
        using (var limited = await Redeem(h, t.Ticket, t.Verifier))
        {
            Private(limited);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);
        }

        // Neither touches the account allowance, and each refills at its own rate: an issue every 30 s, a
        // redemption every 60.
        Assert.Equal(HttpStatusCode.OK, (await h.Send("GET", "/v1/accounts/me", a.Token)).StatusCode);
        h.Clock.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        await Ticket(h, a.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, await Status(Issue(h, a.Token, AccountCrypto.Random())));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Status(Redeem(h, t.Ticket, t.Verifier)));
        h.Clock.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.NotFound, await Status(Redeem(h, t.Ticket, t.Verifier)));
    }

    [Fact]
    public void EveryRequestKindHasABucketOfItsOwn()
    {
        var options = new ListingOptions
        {
            PostsPerMinute = 1,
            ListsPerMinute = 1,
            AuthPerMinute = 1,
            AccountPerMinute = 1,
            TicketIssuesPerMinute = 1,
            TicketRedeemsPerMinute = 1,
        };
        options.Validate();
        var limiter = new RequestLimiter(options, new ManualClock());
        foreach (var kind in Enum.GetValues<RequestKind>()) Assert.True(limiter.TryAcquire("a", kind).Allowed);
        foreach (var kind in Enum.GetValues<RequestKind>()) Assert.False(limiter.TryAcquire("a", kind).Allowed);
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire("a", (RequestKind)99));
    }

    [Fact]
    public async Task MalformedIssueIsRefusedWithoutDatabaseWork()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var challenge = AccountCrypto.Encode([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);
        Assert.True(AccountCrypto.Canonical(challenge));

        // With the one database slot taken, anything that reached the database would answer 503.
        var release = await HoldDatabase(h);
        try
        {
            foreach (var (body, status) in new (string, int)[]
            {
                ("", 400),
                ("{", 400),
                ("{}", 400),
                ("null", 400),
                ("[\"" + challenge + "\"]", 400),
                ("\"" + challenge + "\"", 400),
                ("{\"challenge\":null}", 400),
                ("{\"challenge\":7}", 400),
                ("{\"challenge\":[\"" + challenge + "\"]}", 400),
                ("{\"Challenge\":\"" + challenge + "\"}", 400),
                ("{\"challenge\":\"" + challenge + "\",\"purpose\":\"join\"}", 400),
                ("{\"challenge\":\"" + challenge + "\",\"challenge\":\"" + challenge + "\"}", 400),
                ("{\"challenge\":\"" + challenge + "\"} {}", 400),
                ("{\"challenge\":\"" + challenge[..42] + "\"}", 400),
                ("{\"challenge\":\"" + challenge + "=\"}", 400),
                ("{\"challenge\":\"" + challenge + "A\"}", 400),

                // 43 characters whose last one carries bits that 32 bytes never produce.
                ("{\"challenge\":\"" + challenge[..42] + "B\"}", 400),
                ("{\"challenge\":\"" + challenge[..42] + "+\"}", 400),
                ("{\"challenge\":\" " + challenge[..42] + "\"}", 400),
                ("{\"challenge\":\"\\ud800" + challenge[..42] + "\"}", 400),
                ("{\"challenge\":\"" + new string('A', 5000) + "\"}", 413),
            })
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/v1/game/tickets")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new("Bearer", a.Token);

                // The service refuses an oversized body unread and then closes the connection; the client must not
                // put it back in its pool for the next request.
                request.Headers.ConnectionClose = status == 413;
                using var response = await h.Client.SendAsync(request, ListingHost.Cancel);
                Private(response);
                Assert.Equal(status, (int)response.StatusCode);
                Assert.DoesNotContain(a.Token, await response.Content.ReadAsStringAsync(ListingHost.Cancel));
            }
        }
        finally
        {
            await release();
        }

        Assert.Equal(0, h.Tickets.Count);

        // The same shape with nothing wrong is the one that gets a ticket.
        var good = new HttpRequestMessage(HttpMethod.Post, "/v1/game/tickets")
        {
            Content = new StringContent("{\"challenge\":\"" + challenge + "\"}", Encoding.UTF8, "application/json"),
        };
        good.Headers.Authorization = new("Bearer", a.Token);
        Assert.Equal(HttpStatusCode.Created, (await h.Client.SendAsync(good, ListingHost.Cancel)).StatusCode);
    }

    [Fact]
    public async Task IssueNeedsExactlyOneCanonicalBearerHeaderAndJson()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var challenge = AccountCrypto.Random();
        var json = JsonSerializer.Serialize(new { challenge });
        var release = await HoldDatabase(h);
        try
        {
            foreach (var header in new[]
            {
                "",
                "Authorization: Basic " + a.Token + "\r\n",
                "Authorization: " + a.Token + "\r\n",
                "Authorization: Bearer " + a.Token[..42] + "\r\n",
                "Authorization: Bearer " + a.Token + "=\r\n",
                "Authorization: Bearer  " + a.Token + "\r\n",
                "Authorization: Bearer " + a.Token + ", Bearer " + a.Token + "\r\n",
                "Authorization: Bearer " + a.Token + "\r\nAuthorization: Bearer " + a.Token + "\r\n",
            })
            {
                var reply = await Raw(h, "/v1/game/tickets", header + "Content-Type: application/json\r\n", json);
                Assert.StartsWith("HTTP/1.1 401 ", reply);
                Assert.Contains("Cache-Control: no-store", reply);
                Assert.Contains("Referrer-Policy: no-referrer", reply);
                Assert.DoesNotContain(a.Token, reply);
            }

            // A token anywhere but the header is no token; a good header with no JSON is not read as one.
            var good = "Authorization: Bearer " + a.Token + "\r\n";
            Assert.StartsWith("HTTP/1.1 401 ", await Raw(h, "/v1/game/tickets?token=" + a.Token, "Content-Type: application/json\r\n", json));
            Assert.StartsWith("HTTP/1.1 415 ", await Raw(h, "/v1/game/tickets", good + "Content-Type: text/plain\r\n", json));
            Assert.StartsWith("HTTP/1.1 415 ", await Raw(h, "/v1/game/tickets", good, json));
        }
        finally
        {
            await release();
        }

        // The same request, well formed, is the one that reaches the database.
        Assert.StartsWith("HTTP/1.1 201 ", await Raw(h, "/V1/GAME/TICKETS/",
            "authorization: bearer " + a.Token + "\r\nContent-Type: application/json; charset=utf-8\r\n", json));
        Assert.Equal(1, h.Tickets.Count);
    }

    private static async Task<string> Raw(AccountHost h, string path, string headers, string body)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(h.Client.BaseAddress!.Host, h.Client.BaseAddress.Port, ListingHost.Cancel);
        using var stream = socket.GetStream();
        var request = $"POST {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n{headers}"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), ListingHost.Cancel);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ListingHost.Cancel);
    }

    [Fact]
    public async Task MalformedRedemptionIsRefusedAndLeavesTheTicketUsable()
    {
        await using var h = await AccountHost.Start();
        var a = await h.Login();
        var t = await Ticket(h, a.Token);
        var release = await HoldDatabase(h);
        try
        {
            foreach (var body in new[]
            {
                "{}",
                "[]",
                "{\"ticket\":\"" + t.Ticket + "\"}",
                "{\"verifier\":\"" + t.Verifier + "\"}",
                "{\"ticket\":\"" + t.Ticket + "\",\"verifier\":null}",
                "{\"ticket\":7,\"verifier\":\"" + t.Verifier + "\"}",
                "{\"ticket\":\"" + t.Ticket + "\",\"Verifier\":\"" + t.Verifier + "\"}",
                "{\"ticket\":\"" + t.Ticket + "\",\"verifier\":\"" + t.Verifier + "\",\"account\":\"" + a.Account.Id + "\"}",
                "{\"ticket\":\"" + t.Ticket + "\",\"ticket\":\"" + t.Ticket + "\",\"verifier\":\"" + t.Verifier + "\"}",
                "{\"ticket\":\"" + t.Ticket[..42] + "\",\"verifier\":\"" + t.Verifier + "\"}",
                "{\"ticket\":\"" + t.Ticket + "\",\"verifier\":\"" + t.Verifier + "=\"}",
                "{\"ticket\":\"" + t.Ticket + "\",\"verifier\":\"" + t.Verifier[..42] + "/\"}",
            })
            {
                using var response = await h.Client.PostAsync("/v1/game/tickets/redeem",
                    new StringContent(body, Encoding.UTF8, "application/json"), ListingHost.Cancel);
                Private(response);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var text = await response.Content.ReadAsStringAsync(ListingHost.Cancel);
                Assert.DoesNotContain(t.Ticket, text);
                Assert.DoesNotContain(t.Verifier, text);
            }

            using var plain = await h.Client.PostAsync("/v1/game/tickets/redeem",
                new StringContent(JsonSerializer.Serialize(new { ticket = t.Ticket, verifier = t.Verifier }), Encoding.UTF8, "text/plain"), ListingHost.Cancel);
            Private(plain);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        }
        finally
        {
            await release();
        }

        // A bearer token is neither needed nor a way in: the ticket and the verifier are the whole proof.
        Assert.Equal(1, h.Tickets.Count);
        Assert.Equal(HttpStatusCode.OK, await Status(h.Send("POST", "/v1/game/tickets/redeem", AccountCrypto.Random(),
            new { ticket = t.Ticket, verifier = t.Verifier })));
    }

    [Fact]
    public async Task DisabledAccountsAndUnnamedProxyAddressesRefuseBeforeAnyWork()
    {
        await using var h = await AccountHost.Start(("Accounts:Enabled", "false"), ("Accounts:ClientSecret", ""));
        var token = AccountCrypto.Random();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Issue(h, token, AccountCrypto.Random())));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(Redeem(h, AccountCrypto.Random(), AccountCrypto.Random())));
        Assert.False(File.Exists(h.Database));

        await h.Restart(("Listing:TrustedProxies", "127.0.0.1"));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Issue(h, token, AccountCrypto.Random())));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Redeem(h, AccountCrypto.Random(), AccountCrypto.Random())));

        // Signed out, expired or never issued: one answer, and still no ticket.
        await h.Restart();
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(Issue(h, token, AccountCrypto.Random())));
        Assert.Equal(0, h.Tickets.Count);
    }

    private static (GameTickets Tickets, ManualClock Clock, SessionGrant Grant) Bare(int max = 10000, int perAccount = 4)
    {
        var clock = new ManualClock();
        var options = new AccountsOptions { MaxTickets = max, TicketsPerAccount = perAccount };
        var tickets = new GameTickets(options, new ListingOptions(), clock, new AccountStore(options, clock));
        return (tickets, clock, new SessionGrant("account", clock.GetUtcNow().ToUnixTimeSeconds() + 86400));
    }

    [Fact]
    public void LateOrRepeatedSettlementChangesNothing()
    {
        var (tickets, _, grant) = Bare();
        var ticket = AccountCrypto.Random();
        var verifier = AccountCrypto.Random();
        var holder = new TicketHolder(grant.Account, "Pilot", grant.Expires);
        tickets.Publish(ticket, grant, AccountCrypto.Hash("session"), AccountCrypto.Challenge(verifier));

        var first = tickets.Reserve(ticket, verifier);
        Assert.Equal(503, Failure(() => tickets.Reserve(ticket, verifier)));
        tickets.Release(first);
        var second = tickets.Reserve(ticket, verifier);

        // The first claim is over. Arriving late, it can neither consume, free nor drop what the second now holds.
        Assert.Null(tickets.Complete(first, holder));
        tickets.Release(first);
        tickets.Discard(first);
        Assert.Equal(1, tickets.Count);
        Assert.Equal(503, Failure(() => tickets.Reserve(ticket, verifier)));

        Assert.Same(holder, tickets.Complete(second, holder));
        Assert.Null(tickets.Complete(second, holder));
        tickets.Release(second);
        Assert.Equal(0, tickets.Count);
        Assert.Equal(0, tickets.Accounts);
        Assert.Equal(404, Failure(() => tickets.Reserve(ticket, verifier)));
    }

    [Fact]
    public void CollidingTicketNeverReplacesTheOneAlreadyHeld()
    {
        var (tickets, _, grant) = Bare();
        var ticket = AccountCrypto.Random();
        var verifier = AccountCrypto.Random();
        var other = AccountCrypto.Random();
        tickets.Publish(ticket, grant, AccountCrypto.Hash("session"), AccountCrypto.Challenge(verifier));
        Assert.Equal(503, Failure(() => tickets.Publish(ticket, grant with { Account = "intruder" }, AccountCrypto.Hash("other"),
            AccountCrypto.Challenge(other))));
        Assert.Equal(1, tickets.Count);
        Assert.Equal(1, tickets.Accounts);
        Assert.Equal(404, Failure(() => tickets.Reserve(ticket, other)));
        Assert.Equal("account", tickets.Reserve(ticket, verifier).Entry.Account);
    }

    [Fact]
    public void ConsumptionReadsBothClocksAfresh()
    {
        var (tickets, clock, grant) = Bare();
        var holder = new TicketHolder(grant.Account, "Pilot", grant.Expires);
        (string Ticket, string Verifier) Fresh()
        {
            var verifier = AccountCrypto.Random();
            var ticket = AccountCrypto.Random();
            tickets.Publish(ticket, grant, AccountCrypto.Hash("session"), AccountCrypto.Challenge(verifier));
            return (ticket, verifier);
        }

        // Reserved in time on both clocks; what counts is the moment of consumption.
        var t = Fresh();
        var reservation = tickets.Reserve(t.Ticket, t.Verifier);
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.Same(holder, tickets.Complete(reservation, holder));

        t = Fresh();
        reservation = tickets.Reserve(t.Ticket, t.Verifier);
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.Null(tickets.Complete(reservation, holder));

        t = Fresh();
        reservation = tickets.Reserve(t.Ticket, t.Verifier);
        clock.AdvanceUtc(TimeSpan.FromSeconds(86400));
        Assert.Null(tickets.Complete(reservation, holder));

        // A session that the database no longer has ends the ticket too; a released one past its deadline is not kept.
        clock.AdvanceUtc(TimeSpan.FromSeconds(-86400));
        t = Fresh();
        Assert.Null(tickets.Complete(tickets.Reserve(t.Ticket, t.Verifier), null));
        t = Fresh();
        reservation = tickets.Reserve(t.Ticket, t.Verifier);
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        tickets.Release(reservation);
        Assert.Equal(0, tickets.Count);
        Assert.Equal(0, tickets.Accounts);
    }

    [Fact]
    public void LimitsAreValidatedAtStart()
    {
        AccountsOptions Enabled() => new() { Enabled = true, ClientId = "x", ClientSecret = "y" };
        Enabled().Validate();
        foreach (var bad in new Action<AccountsOptions>[]
        {
            o => o.TicketSeconds = 0,
            o => o.TicketSeconds = 301,
            o => o.MaxTickets = 0,
            o => o.MaxTickets = 1000001,
            o => o.TicketsPerAccount = 0,
            o => { o.MaxTickets = 3; o.TicketsPerAccount = 4; },
        })
        {
            var options = Enabled();
            bad(options);
            Assert.Throws<InvalidOperationException>(options.Validate);
        }

        Assert.Contains("TicketIssuesPerMinute", Assert.Throws<InvalidOperationException>(new ListingOptions { TicketIssuesPerMinute = 0 }.Validate).Message);
        Assert.Contains("TicketRedeemsPerMinute", Assert.Throws<InvalidOperationException>(new ListingOptions { TicketRedeemsPerMinute = 0 }.Validate).Message);
        var defaults = new ListingOptions();
        Assert.Equal((30, 240), (defaults.TicketIssuesPerMinute, defaults.TicketRedeemsPerMinute));
        Assert.Equal((30, 10000, 4), (Enabled().TicketSeconds, Enabled().MaxTickets, Enabled().TicketsPerAccount));
    }
}
