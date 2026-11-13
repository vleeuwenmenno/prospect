namespace Prospect.Listing.Accounts;

/// <summary>The account and absolute expiry (Unix seconds) of the session a ticket is issued against.</summary>
internal readonly record struct SessionGrant(string Account, long Expires);

/// <summary>
/// The account a redeemed ticket names, and its session's absolute expiry (Unix seconds). Only the id and the name are
/// ever sent; the friend code of <see cref="Account"/> is deliberately not here.
/// </summary>
internal sealed record TicketHolder(string Id, string Name, long Expires);

/// <summary>
/// Short-lived, single-use game tickets, held in memory only.
/// </summary>
/// <remarks>
/// <para>
/// A signed-in client asks for a ticket bound to a challenge. Whoever presents the ticket together with the verifier
/// behind that challenge learns which account asked. That is all a ticket says: it proves possession of the verifier
/// tied to an authenticated account session. It does not identify a game server, and it is not the player's consent
/// to anything; those are for the game protocol to establish.
/// </para>
/// <para>
/// Neither the bearer token nor the ticket is kept: an entry is keyed by the ticket's hash and holds the issuing
/// session's hash, the account id, the challenge's hash and a monotonic deadline.
/// </para>
/// <para>
/// A redemption is consumed at one point: <see cref="Complete"/>, which <see cref="AccountStore.Run{T}"/> calls after its
/// transaction has committed and before it admits another database operation. A logout or eviction that committed
/// first is seen by the redemption's read and refuses it; one that commits later cannot take a consumed ticket back.
/// The ticket lock is never held across database work: a redemption reserves the entry under the lock, lets go, runs
/// the transaction, and takes the lock again only to settle.
/// </para>
/// <para>
/// A reserved entry keeps counting against both caps, is never swept and never changes hands, until the operation
/// that reserved it settles it, exactly once. A reservation does not extend the ticket's life: the deadline and the
/// session's expiry are read fresh at the moment of consumption.
/// </para>
/// </remarks>
internal sealed class GameTickets(AccountsOptions options, ListingOptions listing, TimeProvider time, AccountStore store)
    : BackgroundService
{
    /// <summary>Where a test may pause an operation. Each is reached with no ticket lock held.</summary>
    internal enum Checkpoint
    {
        /// <summary>Issuing: the session was read and its transaction is over; the ticket is not yet published.</summary>
        IssueAuthenticated,

        /// <summary>Redeeming: the entry is reserved; the database has not been asked yet.</summary>
        RedeemReserved,

        /// <summary>Redeeming: the session was read; the transaction is still open and the database slot is held.</summary>
        RedeemRead,

        /// <summary>Redeeming: the reservation is settled, whatever the outcome; no answer has been sent yet.</summary>
        RedeemSettled,
    }

    /// <summary>One redemption's claim on one entry. Only the operation holding it may settle it.</summary>
    internal sealed class Reservation(Entry entry)
    {
        internal readonly Entry Entry = entry;
        internal bool Settled;
    }

    internal sealed class Entry(string key, string account, byte[] session, byte[] proof, long deadline)
    {
        internal readonly string Key = key, Account = account;
        internal readonly byte[] Session = session, Proof = proof;
        internal readonly long Deadline = deadline;

        // Null once the entry has left the store. Set and cleared only by Publish and Remove.
        internal LinkedListNode<Entry>? Node;
        internal Reservation? Reserved;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    // Oldest first. Every ticket lives equally long, so this is also the order of the deadlines.
    private readonly LinkedList<Entry> order = new();
    private readonly Dictionary<string, int> held = new(StringComparer.Ordinal);

    /// <summary>Tests pause or fail an operation here to order it against another. Never set in production.</summary>
    internal volatile Action<Checkpoint>? Probe;

    /// <summary>Tickets held right now, reserved ones included.</summary>
    internal int Count
    {
        get
        {
            lock (gate) return entries.Count;
        }
    }

    /// <summary>Accounts that hold at least one ticket right now.</summary>
    internal int Accounts
    {
        get
        {
            lock (gate) return held.Count;
        }
    }

    /// <summary>
    /// Issues a ticket for the session behind <paramref name="token"/>, bound to <paramref name="challenge"/>. Both
    /// must already be canonical. Returns the ticket and the seconds it can still be redeemed.
    /// </summary>
    internal (string Ticket, int ExpiresIn) Issue(string token, string challenge)
    {
        var session = AccountCrypto.Hash(token);
        var grant = store.Run(db => store.Grant(db, session));
        Probe?.Invoke(Checkpoint.IssueAuthenticated);

        // The session may have ended since it was read. Publishing anyway is harmless: redemption asks again.
        var ticket = AccountCrypto.Random();
        return (ticket, Publish(ticket, grant, session, challenge));
    }

    /// <summary>
    /// Stores <paramref name="ticket"/> and returns the seconds it can still be redeemed. The clocks are read here,
    /// after any wait for the database, so a slow read neither shortens nor lengthens a ticket's life.
    /// </summary>
    internal int Publish(string ticket, SessionGrant grant, byte[] session, string challenge)
    {
        lock (gate)
        {
            var now = time.GetTimestamp();
            Sweep(now);
            var remaining = grant.Expires - store.Now;
            if (remaining <= 0) throw new AccountFailure(401);
            if (entries.Count >= options.MaxTickets || held.GetValueOrDefault(grant.Account) >= options.TicketsPerAccount)
                throw new AccountFailure(429);
            var entry = new Entry(Key(ticket), grant.Account, session, AccountCrypto.Hash(challenge),
                now + options.TicketSeconds * time.TimestampFrequency);

            // 256 random bits do not collide; if they ever did, the entry already there must stay as it is.
            if (!entries.TryAdd(entry.Key, entry)) throw new AccountFailure(503);
            entry.Node = order.AddLast(entry);
            held[grant.Account] = held.GetValueOrDefault(grant.Account) + 1;

            // The session's own expiry stays the authority, so the answer never promises longer than it has left.
            return (int)Math.Min(options.TicketSeconds, remaining);
        }
    }

    /// <summary>
    /// Redeems <paramref name="ticket"/> with <paramref name="verifier"/>, both already canonical, at most once.
    /// </summary>
    /// <remarks>
    /// Once the entry is reserved the operation runs to its end and settles the reservation whether or not the caller
    /// is still connected, so a lost answer can cost the ticket but never leaves it half-used.
    /// </remarks>
    internal TicketHolder Redeem(string ticket, string verifier)
    {
        var reservation = Reserve(ticket, verifier);
        var entry = reservation.Entry;
        TicketHolder? holder = null;
        try
        {
            Probe?.Invoke(Checkpoint.RedeemReserved);
            store.Run<TicketHolder?>(db =>
            {
                var found = store.Holder(db, entry.Session, entry.Account);
                Probe?.Invoke(Checkpoint.RedeemRead);
                return found;
            }, found => holder = Complete(reservation, found));
        }
        catch (AccountFailure failure) when (failure.Status == 503)
        {
            // The database was busy or failed before anything was consumed: the ticket may be tried again.
            Release(reservation);
            throw;
        }
        catch
        {
            // Nothing is known about how far an unexpected failure got. Dropping the ticket is the safe side.
            Discard(reservation);
            throw;
        }
        finally
        {
            Probe?.Invoke(Checkpoint.RedeemSettled);
        }

        return holder ?? throw new AccountFailure(404);
    }

    /// <summary>
    /// Claims the entry for one redemption. An unknown, expired or already used ticket and a wrong verifier are the
    /// same 404, and a wrong verifier changes nothing. A ticket another redemption holds right now is 503: its fate
    /// is not settled yet.
    /// </summary>
    internal Reservation Reserve(string ticket, string verifier)
    {
        var key = Key(ticket);
        var proof = AccountCrypto.Hash(AccountCrypto.Challenge(verifier));
        lock (gate)
        {
            var now = time.GetTimestamp();
            Sweep(now);
            if (!entries.TryGetValue(key, out var entry) || now >= entry.Deadline || !AccountCrypto.Equal(entry.Proof, proof))
                throw new AccountFailure(404);
            if (entry.Reserved is not null) throw new AccountFailure(503);
            return entry.Reserved = new Reservation(entry);
        }
    }

    /// <summary>
    /// The one point where a ticket is consumed. <paramref name="holder"/> is what the committed transaction read:
    /// null when the session is gone, expired or no longer the account's. The entry leaves the store either way; the
    /// holder is returned only if the ticket and the session are both still unexpired now.
    /// </summary>
    internal TicketHolder? Complete(Reservation reservation, TicketHolder? holder)
    {
        lock (gate)
        {
            if (!Settle(reservation)) return null;
            var entry = reservation.Entry;
            Remove(entry);
            return holder is not null && time.GetTimestamp() < entry.Deadline && store.Now < holder.Expires ? holder : null;
        }
    }

    /// <summary>Gives the entry back unused. It stays redeemable only if it has not expired in the meantime.</summary>
    internal void Release(Reservation reservation)
    {
        lock (gate)
        {
            if (!Settle(reservation)) return;
            var entry = reservation.Entry;
            entry.Reserved = null;
            if (time.GetTimestamp() >= entry.Deadline) Remove(entry);
        }
    }

    /// <summary>Drops the reserved entry without redeeming it.</summary>
    internal void Discard(Reservation reservation)
    {
        lock (gate)
        {
            if (Settle(reservation)) Remove(reservation.Entry);
        }
    }

    /// <summary>Removes expired entries that no redemption holds. Returns how many tickets remain.</summary>
    internal int Sweep()
    {
        lock (gate)
        {
            Sweep(time.GetTimestamp());
            return entries.Count;
        }
    }

    // A reservation settles once, and only while it is the entry's current claim. A completion that arrives late or
    // twice therefore changes nothing: it can neither free nor consume an entry that someone else now holds.
    private static bool Settle(Reservation reservation)
    {
        if (reservation.Settled || !ReferenceEquals(reservation.Entry.Reserved, reservation)) return false;
        reservation.Settled = true;
        return true;
    }

    // Walks the expired head of the list. A reserved entry is stepped over, not stopped at, so one slow redemption
    // never keeps the expired tickets behind it alive; it is removed by its own operation when that settles.
    private void Sweep(long now)
    {
        for (var node = order.First; node is not null && node.Value.Deadline <= now;)
        {
            var next = node.Next;
            if (node.Value.Reserved is null) Remove(node.Value);
            node = next;
        }
    }

    // The only way out of the store: the index, the expiry order and the account's count always change together.
    private void Remove(Entry entry)
    {
        if (entry.Node is not { } node) return;
        entry.Node = null;
        entries.Remove(entry.Key);
        order.Remove(node);
        var count = held[entry.Account] - 1;
        if (count == 0) held.Remove(entry.Account);
        else held[entry.Account] = count;
    }

    private static string Key(string ticket) => AccountCrypto.Encode(AccountCrypto.Hash(ticket));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Expired tickets are also swept by every issue and redemption; this frees their memory when none arrives.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(listing.SweepIntervalMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) Sweep();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
