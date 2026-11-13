using System.Security.Cryptography;
using System.Text.Json;
using Prospect.Game.Lobby;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;

namespace Prospect.Game.Server;

/// <summary>Local proof progress and the separate durable outcome. Only Committed and AlreadyLinked mean linked.</summary>
public enum LocalClaimState
{
    /// <summary>No claim was requested.</summary>
    None,
    /// <summary>The bounded proof worker has not yet settled.</summary>
    Proving,
    /// <summary>Proof was verified; the candidate save has not yet settled.</summary>
    Pending,
    /// <summary>The exact candidate save committed.</summary>
    Committed,
    /// <summary>The save failed without changing durable ownership.</summary>
    Failed,
    /// <summary>The store generation conflicted; further writes are halted.</summary>
    Conflict,
    /// <summary>The candidate was rejected.</summary>
    Rejected,
    /// <summary>The store cannot establish whether the candidate committed; reopen to resolve it.</summary>
    Unknown,
    /// <summary>The same verified account was already durably linked.</summary>
    AlreadyLinked,
    /// <summary>The proof or claim was refused.</summary>
    Refused,
    /// <summary>The proof's connection, authority, account generation or deadline is no longer current.</summary>
    Cancelled,
}

public sealed partial class ServerHost
{
    private readonly ListingConfig? _accountAuthority;
    private LocalProof? _localProof;
    private long _localEpoch, _nextLocalEpoch;
    private Connection? _localConnection;
    private long _localGeneration;
    private int _localClaimState;
    private bool _localClaimStaged;

    /// <summary>Proof and durable outcome; reads no mutable simulation state.</summary>
    public LocalClaimState LocalClaim => (LocalClaimState)Volatile.Read(ref _localClaimState);

    // Only GameSession's in-process action calls this. Nothing on a transport can enqueue proof work.
    internal bool RequestLocalClaim(AccountClient account)
    {
        var epoch = Volatile.Read(ref _localEpoch);
        if (_visibility != ServerVisibility.Private || _save is null || epoch == 0
            || Volatile.Read(ref _closePhase) != (int)ClosePhase.Open || _tickFaulted
            || !AccountClient.ClaimAuthority(_accountAuthority) || Volatile.Read(ref _localProof) is not null
            || LocalClaim == LocalClaimState.Pending)
            return false;
        var intent = account.LocalClaimIntent(_accountAuthority!.Url.AbsoluteUri);
        if (intent is null) return false;
        var proof = new LocalProof(intent, _accountAuthority, epoch, _clock);
        if (Interlocked.CompareExchange(ref _localProof, proof, null) is not null) return false;
        Volatile.Write(ref _localClaimState, (int)LocalClaimState.Proving);
        proof.Start();
        return true;
    }

    private void ServiceLocalClaim()
    {
        Connection? current = null;
        if (_visibility == ServerVisibility.Private && _save is not null && !_tickFaulted
            && Volatile.Read(ref _closePhase) == (int)ClosePhase.Open)
            foreach (var connection in _connections)
                if (connection.State == ConnectionState.Active && connection.Transport.IsInProcess
                    && connection.OwnerSlot >= 0 && _owners[connection.OwnerSlot].Kind == OwnerKind.Local)
                { current = connection; break; }
        if (current != _localConnection || (current is not null && current.Generation != _localGeneration))
        {
            _localConnection = current;
            _localGeneration = current?.Generation ?? 0;
            Volatile.Write(ref _localEpoch, current is null ? 0 : ++_nextLocalEpoch);
        }
        PublishLocalClaim();
        var proof = Volatile.Read(ref _localProof);
        if (proof is null) return;
        var valid = current is not null && proof.Epoch == _localEpoch && proof.Intent.Current
            && proof.Authority == _accountAuthority?.Url.AbsoluteUri
            && _clock.GetTimestamp() < proof.Deadline && !_save!.Latest.Halted && !_captureBroken;
        if (valid)
            for (var i = 0; i < _ownerCount; i++)
                if (_owners[i].Account != default && _owners[i].Account.Authority != proof.Authority) valid = false;
        if (!valid) Volatile.Write(ref proof.Abandoned, true);
        if (!proof.Done)
        {
            if (valid) Volatile.Write(ref proof.Authorized, true);
            return; // A cancelled slot stays occupied until its worker really ends.
        }
        if (valid && !proof.Abandoned && proof.Account != default)
        {
            var outcome = StageVerifiedLocalClaim(proof.Account);
            _localClaimStaged = outcome == ClaimState.Pending;
            Volatile.Write(ref _localClaimState, (int)MapClaim(outcome));
        }
        else Volatile.Write(ref _localClaimState, (int)(valid ? LocalClaimState.Refused : LocalClaimState.Cancelled));
        Interlocked.CompareExchange(ref _localProof, null, proof);
    }

    private void PublishLocalClaim()
    {
        if (!_localClaimStaged) return;
        ProcessClaimResult();
        Volatile.Write(ref _localClaimState, (int)MapClaim(_claimState));
        if (_claimState != ClaimState.Pending) _localClaimStaged = false;
    }

    private static LocalClaimState MapClaim(ClaimState state) => state switch
    {
        ClaimState.Pending => LocalClaimState.Pending,
        ClaimState.Committed => LocalClaimState.Committed,
        ClaimState.Failed => LocalClaimState.Failed,
        ClaimState.Conflict => LocalClaimState.Conflict,
        ClaimState.Rejected => LocalClaimState.Rejected,
        ClaimState.Unknown => LocalClaimState.Unknown,
        ClaimState.AlreadyLinked => LocalClaimState.AlreadyLinked,
        ClaimState.Cancelled => LocalClaimState.Cancelled,
        _ => LocalClaimState.Refused,
    };

    private void StopLocalClaim()
    {
        Volatile.Write(ref _localEpoch, 0);
        if (Volatile.Read(ref _localProof) is { } proof) Volatile.Write(ref proof.Abandoned, true);
    }

    // One worker owns all secrets, I/O and allocations. The tick only authorizes and consumes its immutable result.
    private sealed class LocalProof(AccountClient.ClaimIntent intent, ListingConfig config, long epoch, TimeProvider clock)
    {
        internal readonly AccountClient.ClaimIntent Intent = intent;
        internal readonly string Authority = config.Url.AbsoluteUri;
        internal readonly long Epoch = epoch;
        internal readonly long Deadline = clock.GetTimestamp() + 30 * clock.TimestampFrequency;
        internal bool Authorized, Abandoned;
        internal volatile bool Done;
        internal SavedAccount Account;

        internal void Start() => _ = Task.Run(RunAsync);

        private async Task RunAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!Volatile.Read(ref Authorized))
                {
                    if (Volatile.Read(ref Abandoned) || !Intent.Current) return;
                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
                if (Volatile.Read(ref Abandoned) || !Intent.Current) return;
                var verifier = AccountClient.Encode(RandomNumberGenerator.GetBytes(32));
                var ticket = await Intent.TicketAsync(Authority, "local-claim", AccountClient.Challenge(verifier), timeout.Token).ConfigureAwait(false);
                if (ticket is null || Volatile.Read(ref Abandoned) || !Intent.Current) return;
                var body = JsonSerializer.SerializeToUtf8Bytes(new { ticket, verifier });
                using var http = new ListingHttp(config);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var reply = await http.SendAsync(HttpMethod.Post, "v1/game/tickets/redeem", body, null, 4096, timeout.Token).ConfigureAwait(false);
                    if (reply.Is(503) && attempt < 2)
                    {
                        var delay = Math.Clamp(reply.RetryAfter?.TotalMilliseconds ?? 400, 250, 5000);
                        await Task.Delay(TimeSpan.FromMilliseconds(delay), timeout.Token).ConfigureAwait(false);
                        if (Volatile.Read(ref Abandoned) || !Intent.Current) return;
                        continue;
                    }
                    if (!reply.Is(200)) return; // A lost response needs a fresh proof, never an invented success.
                    using var json = JsonDocument.Parse(reply.Body, new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("account"u8, out var account)
                        || account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("id"u8, out var id)
                        || id.ValueKind != JsonValueKind.String) return;
                    var text = id.GetString();
                    if (!AccountClient.IsAccountId(text) || text != Intent.Account || !Guid.TryParseExact(text, "N", out var uuid)
                        || uuid == Guid.Empty || !Intent.Current) return;
                    Account = new SavedAccount(Authority, uuid);
                    return;
                }
            }
            catch (Exception e) when (e is OperationCanceledException or JsonException or InvalidOperationException or IOException)
            {
                // Failure is deliberately secret-free and never becomes a durable claim.
            }
            finally { Done = true; }
        }
    }
}
