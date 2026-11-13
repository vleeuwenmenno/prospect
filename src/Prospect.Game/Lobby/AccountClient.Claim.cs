using System.Text.Json;
using Prospect.Game.Persistence;

namespace Prospect.Game.Lobby;

public sealed partial class AccountClient
{
    // Captured only for explicit local claim intent. No credential leaves AccountClient.
    internal sealed class ClaimIntent(AccountClient client, int generation, string authority, string account)
    {
        internal readonly string Authority = authority;
        internal readonly string Account = account;
        internal bool Current => Volatile.Read(ref client._generation) == generation && !Volatile.Read(ref client._disposed);

        internal Task<string?> TicketAsync(string authority, string purpose, string challenge, CancellationToken cancel) =>
            client.LocalTicketAsync(this, authority, purpose, challenge, cancel);
    }

    internal ClaimIntent? LocalClaimIntent(string authority)
    {
        lock (_gate)
        {
            if (_sample || _disposed || _token is null || _operation is not null || ExpireIfDue()
                || _config?.Url.AbsoluteUri != authority || !ClaimAuthority(_config))
                return null;
            return new ClaimIntent(this, _generation, authority, _view.Id);
        }
    }

    internal static bool ClaimAuthority(ListingConfig? config) => config is not null
        && new SavedAccount(config.Url.AbsoluteUri, new Guid("00112233-4455-6677-8899-aabbccddeeff")).IsValid;

    private async Task<string?> LocalTicketAsync(ClaimIntent intent, string authority, string purpose, string challenge, CancellationToken cancel)
    {
        if (purpose != "local-claim" || authority != intent.Authority || !IsCanonicalSecret(challenge))
            return null;
        Operation op;
        CancellationTokenSource operation;
        lock (_gate)
        {
            if (!intent.Current || _token is null || _operation is not null || ExpireIfDue())
                return null;
            operation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancel);
            _operation = operation;
            op = new Operation(_generation, _token, operation.Token);
            Publish(_view.With(busy: true));
        }
        string? ticket = null;
        await RunAsync(op, operation, async current =>
        {
            var body = Write(w => w.WriteString("challenge"u8, challenge));
            var reply = await SendAsync(HttpMethod.Post, "v1/game/tickets", body, current.Token, MaxSmallBytes, current.Cancel).ConfigureAwait(false);
            if (reply.Is(401)) { Expire(current); return; }
            if (!reply.Is(201)) return;
            using var json = JsonDocument.Parse(reply.Body, Strict);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ticket"u8, out var value)
                || value.ValueKind != JsonValueKind.String || !IsCanonicalSecret(value.GetString())
                || !root.TryGetProperty("expiresIn"u8, out var expires) || !expires.TryGetInt32(out var seconds) || seconds is < 1 or > 300)
                return;
            lock (_gate)
                if (intent.Current && !ExpireIfDue()) ticket = value.GetString();
        }).ConfigureAwait(false);
        return intent.Current ? ticket : null;
    }
}
