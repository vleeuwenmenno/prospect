using Microsoft.Data.Sqlite;

namespace Prospect.Listing.Accounts;

internal sealed class AccountStore(AccountsOptions options, TimeProvider time)
{
    private readonly SemaphoreSlim slot = new(1);
    private bool initialized;

    // SQLite is synchronous. Immediate admission bounds contention to one request thread, including external locks.
    //
    // committed runs only after the transaction has committed and the connection is closed, and before the next
    // operation is admitted. What it does is therefore ordered against every other database operation (a logout, a
    // session eviction) exactly as this transaction was. It must be brief and must not start database work.
    internal T Run<T>(Func<SqliteConnection, T> work, Action<T>? committed = null)
    {
        if (!slot.Wait(0)) throw new AccountFailure(503);
        try
        {
            var result = Transact(work);
            committed?.Invoke(result);
            return result;
        }
        finally { slot.Release(); }
    }

    private T Transact<T>(Func<SqliteConnection, T> work)
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Pooling = false,
                DefaultTimeout = 1,
                ForeignKeys = true,
            }.ToString());
            db.Open();
            Execute(db, "PRAGMA busy_timeout=1000");
            if (!initialized)
            {
                Execute(db, "PRAGMA journal_mode=WAL");
                using var migration = db.BeginTransaction(deferred: false);
                var version = Number(db, "PRAGMA user_version");
                if (version == 0)
                {
                    Execute(db, """
                        CREATE TABLE accounts(id TEXT PRIMARY KEY COLLATE BINARY, discord TEXT NOT NULL UNIQUE,
                            name TEXT NOT NULL, code TEXT NOT NULL UNIQUE);
                        CREATE TABLE sessions(hash BLOB PRIMARY KEY, account TEXT NOT NULL REFERENCES accounts(id),
                            created INTEGER NOT NULL, expires INTEGER NOT NULL);
                        CREATE INDEX sessions_expiry ON sessions(expires);
                        CREATE INDEX sessions_account ON sessions(account,created);
                        CREATE TABLE pairs(a TEXT NOT NULL REFERENCES accounts(id), b TEXT NOT NULL REFERENCES accounts(id),
                            requester TEXT NOT NULL REFERENCES accounts(id), accepted INTEGER NOT NULL CHECK(accepted IN (0,1)),
                            since INTEGER NOT NULL, CHECK(a < b), CHECK(requester = a OR requester = b), PRIMARY KEY(a,b));
                        CREATE INDEX pairs_b ON pairs(b);
                        CREATE INDEX pairs_expiry ON pairs(accepted,since);
                        PRAGMA user_version=1;
                        """);
                }
                else if (version != 1) throw new AccountFailure(503);
                migration.Commit();
                initialized = true;
            }
            using var transaction = db.BeginTransaction(deferred: false);
            Cleanup(db);
            var result = work(db);
            transaction.Commit();
            return result;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new AccountFailure(503);
        }
    }

    internal long Now => time.GetUtcNow().ToUnixTimeSeconds();
    private void Cleanup(SqliteConnection db)
    {
        Execute(db, "DELETE FROM sessions WHERE rowid IN (SELECT rowid FROM sessions WHERE expires <= $n LIMIT 256)", ("$n", Now));
        Execute(db, "DELETE FROM pairs WHERE rowid IN (SELECT rowid FROM pairs WHERE accepted=0 AND since <= $n LIMIT 256)",
            ("$n", Now - options.PendingDays * 86400L));
    }

    internal object Session(string discord, string? name)
    {
        return Run(db =>
        {
            var id = Scalar(db, "SELECT id FROM accounts WHERE discord=$d", ("$d", discord)) as string;
            if (id is null)
            {
                id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                Execute(db, "INSERT INTO accounts VALUES($i,$d,$n,$c)", ("$i", id), ("$d", discord),
                    ("$n", AccountCrypto.Name(name, id)), ("$c", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8))));
            }
            else Execute(db, "UPDATE accounts SET name=$n WHERE id=$i", ("$n", AccountCrypto.Name(name, id)), ("$i", id));
            Execute(db, "DELETE FROM sessions WHERE hash IN (SELECT hash FROM sessions WHERE account=$i ORDER BY created DESC,rowid DESC LIMIT -1 OFFSET 9)", ("$i", id));
            var token = AccountCrypto.Random();
            Execute(db, "INSERT INTO sessions VALUES($h,$i,$n,$e)", ("$h", AccountCrypto.Hash(token)), ("$i", id), ("$n", Now), ("$e", Now + 86400));
            return (object)new { token, expiresIn = 86400, account = ReadAccount(db, id) };
        });
    }

    internal Account Authenticate(SqliteConnection db, string token)
    {
        if (!AccountCrypto.Canonical(token)) throw new AccountFailure(401);
        var id = Scalar(db, "SELECT account FROM sessions WHERE hash=$h AND expires>$n",
            ("$h", AccountCrypto.Hash(token)), ("$n", Now)) as string;
        return id is null ? throw new AccountFailure(401) : ReadAccount(db, id);
    }

    // The account and absolute expiry of a live session, named by its hash: what a game ticket is issued against.
    internal SessionGrant Grant(SqliteConnection db, byte[] hash)
    {
        using var command = Command(db, "SELECT account,expires FROM sessions WHERE hash=$h AND expires>$n", ("$h", hash), ("$n", Now));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetInt64(1)) : throw new AccountFailure(401);
    }

    // Redemption asks for the exact pair a ticket was issued to: the session must still exist, still belong to that
    // account and still be unexpired. An account alone is not enough, since logout and eviction end one session.
    // Expiry is tested here because Cleanup reclaims only some expired rows per operation.
    internal TicketHolder? Holder(SqliteConnection db, byte[] hash, string account)
    {
        using var command = Command(db, """
            SELECT a.name,s.expires FROM sessions s JOIN accounts a ON a.id=s.account
            WHERE s.hash=$h AND s.account=$i AND s.expires>$n
            """, ("$h", hash), ("$i", account), ("$n", Now));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(account, reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static Account ReadAccount(SqliteConnection db, string id)
    {
        using var command = Command(db, "SELECT name,code FROM accounts WHERE id=$i", ("$i", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new AccountFailure(401);
        return new(id, reader.GetString(0), reader.GetString(1));
    }

    internal object List(SqliteConnection db, string id)
    {
        var friends = new List<Friend>();
        var incoming = new List<Friend>();
        var outgoing = new List<Friend>();
        using var command = Command(db, """
            SELECT u.id,u.name,p.since,p.accepted,p.requester FROM pairs p
            JOIN accounts u ON u.id=CASE WHEN p.a=$i THEN p.b ELSE p.a END
            WHERE (p.a=$i OR p.b=$i) AND (p.accepted=1 OR p.since>$cut) ORDER BY p.since,u.id
            """, ("$i", id), ("$cut", Now - options.PendingDays * 86400L));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new Friend(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
            (reader.GetInt64(3) == 1 ? friends : reader.GetString(4) == id ? outgoing : incoming).Add(item);
        }
        return new { friends, incoming, outgoing };
    }

    internal int Request(SqliteConnection db, string self, string code)
    {
        var other = Scalar(db, "SELECT id FROM accounts WHERE code=$c", ("$c", code)) as string;
        if (other is null) return 404;
        if (other == self) return 409;
        return Change(db, self, other, "request");
    }

    internal int Change(SqliteConnection db, string self, string other, string action)
    {
        var a = string.CompareOrdinal(self, other) < 0 ? self : other;
        var b = a == self ? other : self;
        var parameters = new (string, object)[] { ("$a", a), ("$b", b) };
        Execute(db, "DELETE FROM pairs WHERE a=$a AND b=$b AND accepted=0 AND since<=$n",
            ("$a", a), ("$b", b), ("$n", Now - options.PendingDays * 86400L));
        string? requester = null;
        bool accepted = false;
        using (var command = Command(db, "SELECT requester,accepted FROM pairs WHERE a=$a AND b=$b", parameters))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { requester = reader.GetString(0); accepted = reader.GetInt64(1) == 1; }
        if (action == "request" && accepted) return 409;
        if (action == "accept" && accepted) return 200;
        if (action is "decline" or "cancel" or "remove")
        {
            if (requester is null) return 204;
            if (action == "remove" ? !accepted : accepted || (action == "cancel" ? requester != self : requester == self)) return 404;
            Execute(db, "DELETE FROM pairs WHERE a=$a AND b=$b", parameters);
            return 204;
        }
        if (requester is not null && requester == self) return action == "request" ? 200 : 404;
        if (requester is null && action == "accept") return 404;
        if (Count(db, self, "accepted=1") >= options.MaxFriends || Count(db, other, "accepted=1") >= options.MaxFriends) return 429;
        if (requester is not null)
        {
            Execute(db, "UPDATE pairs SET accepted=1,since=$n WHERE a=$a AND b=$b", ("$a", a), ("$b", b), ("$n", Now));
            return 200;
        }
        if (Count(db, self, "accepted=0 AND requester=$i") >= options.MaxOutgoing
            || Count(db, other, "accepted=0 AND requester<>$i") >= options.MaxIncoming) return 429;
        Execute(db, "INSERT INTO pairs VALUES($a,$b,$i,0,$n)", ("$a", a), ("$b", b), ("$i", self), ("$n", Now));
        return 201;
    }

    private long Count(SqliteConnection db, string id, string predicate) => Number(db,
        "SELECT count(*) FROM pairs WHERE (a=$i OR b=$i) AND (accepted=1 OR since>$cut) AND " + predicate,
        ("$i", id), ("$cut", Now - options.PendingDays * 86400L));
    internal static SqliteCommand Command(SqliteConnection db, string sql, params (string Key, object Value)[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        return command;
    }
    internal static void Execute(SqliteConnection db, string sql, params (string Key, object Value)[] values)
    { using var command = Command(db, sql, values); command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql, params (string Key, object Value)[] values)
    { using var command = Command(db, sql, values); return command.ExecuteScalar(); }
    private static long Number(SqliteConnection db, string sql, params (string Key, object Value)[] values) =>
        Convert.ToInt64(Scalar(db, sql, values), System.Globalization.CultureInfo.InvariantCulture);
}
