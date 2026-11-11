namespace Prospect.Listing.Accounts;

internal sealed class SigninAttempts(AccountsOptions options, TimeProvider time, AccountStore store) : BackgroundService
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
    internal sealed class Attempt(string id, string challenge, int port, string group, DateTimeOffset expires)
    {
        internal readonly string Id = id, Challenge = challenge, Group = group;
        internal readonly int Port = port;
        internal readonly DateTimeOffset Expires = expires;
        internal string? State, ProviderVerifier, Discord, Name;
        internal byte[]? CodeHash;
        internal bool Started, Redeeming;
    }

    internal string Create(string challenge, int port, string group)
    {
        lock (gate)
        {
            Sweep();
            if (attempts.Count >= options.MaxAttempts || attempts.Values.Count(a => a.Group == group) >= options.AttemptsPerAddress)
                throw new AccountFailure(429);
            var id = AccountCrypto.Random();
            attempts.Add(id, new(id, challenge, port, group, time.GetUtcNow().AddMinutes(10)));
            return id;
        }
    }
    internal Attempt Start(string id)
    {
        lock (gate)
        {
            Sweep();
            if (!attempts.TryGetValue(id, out var a) || a.Started) throw new AccountFailure(400);
            a.Started = true;
            a.State = AccountCrypto.Random();
            a.ProviderVerifier = AccountCrypto.Random();
            return a;
        }
    }
    internal Attempt ConsumeState(string state)
    {
        lock (gate)
        {
            Sweep();
            var a = attempts.Values.FirstOrDefault(a => a.State == state);
            if (a is null) throw new AccountFailure(400);
            a.State = null;
            return a;
        }
    }
    internal string Complete(Attempt a, string discord, string? name)
    {
        lock (gate)
        {
            if (a.Expires <= time.GetUtcNow() || !attempts.ContainsKey(a.Id)) throw new AccountFailure(400);
            var code = AccountCrypto.Random();
            a.Discord = discord;
            a.Name = name;
            a.ProviderVerifier = null;
            a.CodeHash = AccountCrypto.Hash(code);
            return code;
        }
    }
    internal void Abort(Attempt a)
    {
        lock (gate)
        {
            attempts.Remove(a.Id);
            a.ProviderVerifier = null;
        }
    }
    internal object Redeem(string id, string code, string verifier, AccountStore store)
    {
        Attempt a;
        lock (gate)
        {
            Sweep();
            if (!attempts.TryGetValue(id, out a!) || a.Redeeming || a.CodeHash is null
                || !AccountCrypto.Equal(a.CodeHash, AccountCrypto.Hash(code))
                || !AccountCrypto.Equal(AccountCrypto.Hash(a.Challenge), AccountCrypto.Hash(AccountCrypto.Challenge(verifier))))
                throw new AccountFailure(400);
            a.Redeeming = true;
        }
        try
        {
            var result = store.Session(a.Discord!, a.Name);
            lock (gate) attempts.Remove(id);
            return result;
        }
        catch
        {
            lock (gate) a.Redeeming = false;
            throw;
        }
    }
    private void Sweep()
    {
        foreach (var a in attempts.Values.ToArray())
            if (a.Expires <= time.GetUtcNow() && !a.Redeeming) attempts.Remove(a.Id);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (gate) Sweep();
                try { store.Run(_ => 0); }
                catch (AccountFailure) { /* A database outage must not stop memory expiry or listing. */ }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
