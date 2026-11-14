using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Prospect.Game.Lobby;

/// <summary>
/// The player's account and friends at the account service beside the listing server (docs/accounts.md): Discord
/// sign-in through the browser and a <see cref="LoopbackReceiver"/>, then the friends routes with the session token.
/// <para>
/// One instance lives as long as the game, so the sign-in survives joining and leaving games. The token stays in
/// this object's memory: it is never written to disk, logged, shown, or given to a game server. Every request goes
/// through <see cref="ListingHttp"/> on the thread pool; nothing here runs on the frame loop or the tick.
/// </para>
/// <para>
/// One operation runs at a time; a request while one runs is refused (false), never queued. The interface reads
/// <see cref="View"/>, an immutable snapshot swapped behind a volatile reference. Signing out, cancelling and
/// disposing advance a generation, and an operation from an older generation can no longer change the view or the
/// token, so a late answer never brings an old account back. Every answer is checked as untrusted.
/// </para>
/// </summary>
public sealed partial class AccountClient : IDisposable
{
    /// <summary>The most entries read from each friends list; a reply with more is refused as a whole.</summary>
    public const int MaxListEntries = 400;

    /// <summary>The largest friends reply read. 1,200 entries with every character escaped fit.</summary>
    public const int MaxListBytes = 1024 * 1024;

    /// <summary>The longest name kept, in UTF-16 units; the service sends at most 24.</summary>
    public const int MaxNameChars = 24;

    private const int MaxSmallBytes = 16 * 1024;
    private const int Tries = 3;
    private static readonly TimeSpan MaxSigninTime = TimeSpan.FromMinutes(10);
    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    private readonly ListingConfig? _config;
    private readonly ListingHttp? _http;
    private readonly Func<Uri, bool> _openBrowser;
    private readonly TimeSpan _signinTimeout;
    private readonly bool _sample;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private AccountView _view;
    private string? _token;

    /// <summary>When <see cref="_token"/> stops counting as a session, on <see cref="Time"/>'s monotonic clock.</summary>
    private long _tokenExpires;
    private int _generation;
    private CancellationTokenSource? _operation;
    private bool _disposed;

    /// <param name="config">The listing server, whose origin also serves accounts; null when it is turned off.</param>
    /// <param name="openBrowser">
    /// Opens a page in the player's browser and says whether it started. Only ever given the service's own
    /// sign-in page, built here from <paramref name="config"/>; tests capture it instead.
    /// </param>
    /// <param name="signinTimeout">How long to wait for the browser; at most the service's ten minutes.</param>
    public AccountClient(ListingConfig? config, Func<Uri, bool> openBrowser, TimeSpan? signinTimeout = null)
    {
        _config = config;
        _http = config is null ? null : new ListingHttp(config);
        _openBrowser = openBrowser;
        _signinTimeout = signinTimeout is { } limit && limit < MaxSigninTime ? limit : MaxSigninTime;
        _view = new AccountView(config is null ? AccountState.Off : AccountState.SignedOut);
    }

    private AccountClient(AccountView view)
    {
        _sample = true;
        _openBrowser = static _ => false;
        _view = view;
    }

    /// <summary>What to show now. Safe from any thread; never blocks.</summary>
    public AccountView View => Volatile.Read(ref _view);

    // Tests pause after transport completion to exercise replies arriving across cancellation and replacement.
    internal Func<string, Task>? ResponseReceived { get; init; }

    // This boundary reproduces cancellation after Waiting is published but before the opener is called.
    internal Func<Task>? BeforeBrowserLaunch { get; init; }

    // The monotonic clock a session's lifetime is measured on, and the timers the wait for the browser runs on;
    // tests move it instead of waiting a day, and fire the browser wait by hand instead of racing a real timer.
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The account service's host name, for display; empty when turned off.</summary>
    public string Host => _config?.Url.Host ?? (_sample ? "sample" : "");

    /// <summary>
    /// A fixed account view for captures (<c>--account-sample</c>), read with the same parsers as real answers.
    /// It never touches the network or a browser, and every action on it is refused.
    /// </summary>
    /// <param name="state">The state to show.</param>
    /// <param name="account">An account object as <c>GET /v1/accounts/me</c> returns it, or empty for none.</param>
    /// <param name="friends">A friends reply as <c>GET /v1/friends</c> returns it, or empty for none.</param>
    /// <param name="notice">The notice to show.</param>
    /// <param name="browserUrl">The page shown as waiting in the browser, or null.</param>
    public static AccountClient Sample(AccountState state, ReadOnlySpan<byte> account, ReadOnlySpan<byte> friends, AccountNotice notice, string? browserUrl)
    {
        var view = new AccountView(state);
        if (!account.IsEmpty)
        {
            if (!TryParseAccount(account.ToArray(), out var id, out var name, out var code))
                throw new InvalidDataException("The account sample is not an account.");
            view = view.WithAccount(id, name, code).With(state);
        }

        if (!friends.IsEmpty)
        {
            if (!TryParseFriends(friends.ToArray(), out var accepted, out var incoming, out var outgoing))
                throw new InvalidDataException("The friends sample is not a friends list.");
            view = view.WithLists(accepted, incoming, outgoing, 0);
        }

        return new AccountClient(view.With(notice: notice, browserUrl: browserUrl));
    }

    /// <summary>
    /// Starts a Discord sign-in: opens the local port, asks the service for an attempt and opens the browser at the
    /// service's sign-in page. Returns false when signed in, already signing in, busy, or there is no service.
    /// Call only for an explicit request from the player.
    /// </summary>
    public bool SignIn() => Start(static v => v.State == AccountState.SignedOut, SignInAsync, AccountState.Starting);

    /// <summary>
    /// True only while this client holds a session the service issued, within the lifetime the service gave it,
    /// measured from before the session was asked for on a monotonic clock. A sample, a sign-in under way and a
    /// signed-out or turned-off client never have one, whatever <see cref="View"/> says. A session found expired
    /// here signs out with <see cref="AccountNotice.Expired"/>. Takes the lock: call it for an action, not every
    /// frame. The service can still end a session sooner (sign-out elsewhere, eviction); that is only learnt from
    /// the next request it answers with 401.
    /// </summary>
    public bool HasCurrentSession()
    {
        lock (_gate)
            return !_sample && !_disposed && _token is not null && !ExpireIfDue();
    }

    /// <summary>Abandons a sign-in under way: the local port closes at once and the player is signed out.</summary>
    public void CancelSignIn()
    {
        lock (_gate)
        {
            if (_sample || _disposed || _view.State is not (AccountState.Starting or AccountState.Waiting or AccountState.Finishing))
                return;
            Abandon();
            Publish(_view.SignedOut(AccountNotice.SigninCancelled));
        }
    }

    /// <summary>
    /// Signs out at once: the token is forgotten and the view emptied before this returns. The service is then told
    /// to end the session, off the caller's thread; whether it answers changes nothing here.
    /// </summary>
    public void SignOut()
    {
        string? token;
        lock (_gate)
        {
            if (_sample || _disposed || _view.State is AccountState.Off)
                return;
            token = _token;
            Abandon();
            Publish(_view.SignedOut(AccountNotice.SignedOut));
        }

        if (token is not null)
            _ = RevokeAsync(token);
    }

    /// <summary>Fetches the friends lists again. False when not signed in or busy.</summary>
    public bool Refresh() => Start(static v => v.State == AccountState.SignedIn, op => ListAsync(op, AccountNotice.Refreshed));

    /// <summary>Fetches the friends lists when the last ones are older than <paramref name="age"/>; for opening a screen.</summary>
    public bool RefreshIfStale(TimeSpan age)
    {
        var view = View;
        if (view.State != AccountState.SignedIn || view.Busy || (view.Listed && Stopwatch.GetElapsedTime(view.ListedAt) < age))
            return false;
        return Start(static v => v.State == AccountState.SignedIn, op => ListAsync(op, null));
    }

    /// <summary>
    /// Sends a friend request to a friend code (16 hex characters; lower case is taken as upper). A code that
    /// cannot be one is refused here with <see cref="AccountNotice.InvalidCode"/>.
    /// </summary>
    public bool AddFriend(ReadOnlySpan<char> code)
    {
        code = code.Trim();
        Span<char> upper = stackalloc char[16];
        if (code.Length != 16 || code.ToUpperInvariant(upper) != 16 || !IsFriendCode(upper))
        {
            lock (_gate)
            {
                if (!_sample && !_disposed && _view.State == AccountState.SignedIn && !_view.Busy)
                    Publish(_view.With(notice: AccountNotice.InvalidCode));
            }

            return false;
        }

        var text = new string(upper);
        var body = Write(w => w.WriteString("friendCode"u8, text));
        return Start(static v => v.State == AccountState.SignedIn, op => ChangeAsync(op, HttpMethod.Post, "v1/friends/requests", body, Change.Add));
    }

    /// <summary>Accepts an incoming request from the account with this id.</summary>
    public bool Accept(string id) => Mutate(id, HttpMethod.Post, $"v1/friends/requests/{id}/accept", Change.Accept);

    /// <summary>Declines an incoming request from the account with this id.</summary>
    public bool Decline(string id) => Mutate(id, HttpMethod.Post, $"v1/friends/requests/{id}/decline", Change.Decline);

    /// <summary>Withdraws the player's own request to the account with this id.</summary>
    public bool CancelRequest(string id) => Mutate(id, HttpMethod.Delete, $"v1/friends/requests/{id}", Change.Cancel);

    /// <summary>Ends the friendship with the account with this id.</summary>
    public bool Remove(string id) => Mutate(id, HttpMethod.Delete, $"v1/friends/{id}", Change.Remove);

    /// <summary>Abandons whatever runs and closes the local port. The session is left to expire at the service.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Abandon();
        }

        _stop.Cancel();
        _http?.Dispose();
    }

    // -----------------------------------------------------------------------------------------------------------
    // Operations
    // -----------------------------------------------------------------------------------------------------------

    private enum Change : byte
    {
        Add,
        Accept,
        Decline,
        Cancel,
        Remove,
    }

    private sealed class Operation(int generation, string? token, CancellationToken cancel)
    {
        public readonly int Generation = generation;
        public string? Token = token;
        public readonly CancellationToken Cancel = cancel;
    }

    private bool Mutate(string id, HttpMethod method, string path, Change change) =>
        IsAccountId(id) && Start(static v => v.State == AccountState.SignedIn, op => ChangeAsync(op, method, path, default, change));

    /// <summary>Starts one operation when none runs and the view allows it.</summary>
    private bool Start(Func<AccountView, bool> allowed, Func<Operation, Task> work, AccountState? state = null)
    {
        Operation op;
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_sample || _disposed || _http is null || _operation is not null || ExpireIfDue() || !allowed(_view))
                return false;
            cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _operation = cancel;
            op = new Operation(_generation, _token, cancel.Token);
            Publish(_view.With(state: state, busy: true));
        }

        _ = Task.Run(() => RunAsync(op, cancel, work));
        return true;
    }

    private async Task RunAsync(Operation op, CancellationTokenSource cancel, Func<Operation, Task> work)
    {
        try
        {
            await work(op).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by sign-out, cancel or disposal, which already set the view.
        }
        catch (Exception e)
        {
            // Nothing may escape to the thread pool; the message is not shown, the type is enough for a log.
            Prospect.Engine.Diagnostics.Logs.Net?.Warning($"Account operation failed: {e.GetType().Name}");
            Commit(op, v => v.State == AccountState.SignedIn ? v.With(notice: AccountNotice.BadReply) : v.SignedOut(AccountNotice.SigninFailed));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_operation, cancel))
                {
                    _operation = null;
                    Publish(_view.With(busy: false));
                }
            }

            cancel.Dispose();
        }
    }

    private async Task SignInAsync(Operation op)
    {
        LoopbackReceiver receiver;
        try
        {
            receiver = LoopbackReceiver.Start();
        }
        catch (IOException)
        {
            Commit(op, v => v.SignedOut(AccountNotice.PortUnavailable));
            return;
        }

        using (receiver)
        {
            var verifier = Encode(RandomNumberGenerator.GetBytes(32));
            var port = receiver.Port;
            var create = Write(w =>
            {
                w.WriteString("challenge"u8, Challenge(verifier));
                w.WriteNumber("port"u8, port);
            });
            // Not retried: a disabled service answers 503 too, and the player can simply try again.
            var reply = await _http!.SendAsync(HttpMethod.Post, "v1/accounts/signin", create, null, MaxSmallBytes, op.Cancel).ConfigureAwait(false);
            if (ResponseReceived is { } received)
                await received("v1/accounts/signin").ConfigureAwait(false);
            if (!reply.Is(201) || !TryParseAttempt(reply.Body, out var attempt, out var expires))
            {
                Commit(op, v => v.SignedOut(reply.Is(201) ? AccountNotice.BadReply : Failure(reply, AccountNotice.SigninFailed)));
                return;
            }

            receiver.Expect(attempt);
            // Built here from the configured service and a checked attempt; nothing in the reply chooses the page.
            var page = new Uri(_config!.Url, "v1/accounts/signin/" + attempt);
            if (!Commit(op, v => v.With(state: AccountState.Waiting, browserUrl: page.AbsoluteUri)))
                return;

            if (BeforeBrowserLaunch is { } launching)
                await launching().ConfigureAwait(false);

            bool opened;
            lock (_gate)
            {
                // Cancellation must either precede the launch or wait for it; an unlocked check leaves a race.
                if (op.Generation != _generation || _disposed)
                    return;
                try
                {
                    opened = _openBrowser(page);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    opened = false;
                }
            }

            if (!opened)
                Commit(op, v => v.With(notice: AccountNotice.BrowserNotOpened));

            LoopbackResult result;
            try
            {
                var wait = TimeSpan.FromSeconds(expires) < _signinTimeout ? TimeSpan.FromSeconds(expires) : _signinTimeout;
                result = await receiver.Result.WaitAsync(wait, Time, op.Cancel).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Commit(op, v => v.SignedOut(AccountNotice.SigninTimedOut));
                return;
            }

            // The port closes as soon as the one answer is in.
            receiver.Dispose();
            if (result.Error is { } error)
            {
                Commit(op, v => v.SignedOut(error == "access_denied" ? AccountNotice.SigninDenied : AccountNotice.SigninFailed));
                return;
            }

            if (!Commit(op, v => v.With(state: AccountState.Finishing, clearBrowserUrl: true)))
                return;

            var redeem = Write(w =>
            {
                w.WriteString("attempt"u8, attempt);
                w.WriteString("code"u8, result.Code);
                w.WriteString("verifier"u8, verifier);
            });
            // The lifetime counts from before the request, so the local clock never outlasts the service's.
            var asked = Time.GetTimestamp();
            reply = await SendAsync(HttpMethod.Post, "v1/accounts/sessions", redeem, null, MaxSmallBytes, op.Cancel).ConfigureAwait(false);
            if (!reply.Is(201) || !TryParseSession(reply.Body, out var token, out var lifetime, out var id, out var name, out var code))
            {
                Commit(op, v => v.SignedOut(reply.Is(201) ? AccountNotice.BadReply : Failure(reply, AccountNotice.SigninFailed)));
                return;
            }

            lock (_gate)
            {
                if (op.Generation != _generation || _disposed)
                    return;
                _token = token;
                _tokenExpires = asked + lifetime * Time.TimestampFrequency;
                Publish(_view.WithAccount(id, name, code).With(notice: AccountNotice.SignedIn));
            }

            op.Token = token;
        }

        await ListAsync(op, null).ConfigureAwait(false);
    }

    /// <summary>Fetches the friends lists. A notice is set on success only when given; a failure always says so.</summary>
    private async Task ListAsync(Operation op, AccountNotice? success)
    {
        var reply = await SendAsync(HttpMethod.Get, "v1/friends", default, op.Token, MaxListBytes, op.Cancel).ConfigureAwait(false);
        if (reply.Is(401))
        {
            Expire(op);
            return;
        }

        if (reply.Is(200) && TryParseFriends(reply.Body, out var friends, out var incoming, out var outgoing))
        {
            var at = Stopwatch.GetTimestamp();
            Commit(op, v => success is { } notice ? v.WithLists(friends, incoming, outgoing, at).With(notice: notice) : v.WithLists(friends, incoming, outgoing, at));
            return;
        }

        Commit(op, v => v.With(notice: reply.Is(200) ? AccountNotice.BadReply : Failure(reply, AccountNotice.Unavailable)));
    }

    private async Task ChangeAsync(Operation op, HttpMethod method, string path, ReadOnlyMemory<byte> body, Change change)
    {
        var reply = await SendAsync(method, path, body, op.Token, MaxSmallBytes, op.Cancel).ConfigureAwait(false);
        if (reply.Is(401))
        {
            Expire(op);
            return;
        }

        var notice = reply.Outcome != ListingOutcome.Answered
            ? Failure(reply, AccountNotice.Unreachable)
            : (change, reply.Status) switch
            {
                (Change.Add, 201) => AccountNotice.RequestSent,
                (Change.Add, 200) => AccountNotice.RequestSettled,
                (Change.Add, 404) => AccountNotice.UnknownCode,
                (Change.Add, 409) => AccountNotice.CannotAdd,
                (Change.Add, 429) => AccountNotice.LimitReached,
                (Change.Add, 400) => AccountNotice.InvalidCode,
                (Change.Accept, 200) => AccountNotice.Accepted,
                (Change.Decline, 204) => AccountNotice.Declined,
                (Change.Cancel, 204) => AccountNotice.RequestCancelled,
                (Change.Remove, 204) => AccountNotice.Removed,
                (_, 404) => AccountNotice.Gone,
                _ => Failure(reply, AccountNotice.Unavailable),
            };
        if (!Commit(op, v => v.With(notice: notice)))
            return;

        // Whatever the service decided, the lists are read again so they show where things stand. Nothing is sent
        // twice except on 503, which the service answers without changing anything.
        if (reply.Outcome == ListingOutcome.Answered && reply.Status is (>= 200 and < 300) or 404 or 409)
            await ListAsync(op, null).ConfigureAwait(false);
    }

    /// <summary>
    /// One request, sent again after a short, bounded wait while the service says 503 (busy: nothing was done),
    /// at most <see cref="Tries"/> times. Every other answer, and no answer at all, is final.
    /// </summary>
    private async Task<ListingReply> SendAsync(HttpMethod method, string path, ReadOnlyMemory<byte> body, string? token, int maxBytes, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            var reply = await _http!.SendAsync(method, path, body, token, maxBytes, cancel).ConfigureAwait(false);
            if (ResponseReceived is { } received)
                await received(path).ConfigureAwait(false);
            if (!reply.Is(503) || attempt == Tries)
                return reply;
            var wait = reply.RetryAfter ?? TimeSpan.FromMilliseconds(400 * attempt);
            wait = wait < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait;
            await Task.Delay(wait, cancel).ConfigureAwait(false);
        }
    }

    private async Task RevokeAsync(string token)
    {
        try
        {
            await _http!.SendAsync(HttpMethod.Delete, "v1/accounts/sessions/current", default, token, MaxSmallBytes, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The game is closing; the session expires at the service.
        }
    }

    private static AccountNotice Failure(in ListingReply reply, AccountNotice otherwise) =>
        reply.Outcome switch
        {
            ListingOutcome.Unreachable or ListingOutcome.TimedOut => AccountNotice.Unreachable,
            ListingOutcome.TooLarge => AccountNotice.BadReply,
            _ when reply.Status == 503 => AccountNotice.Unavailable,
            _ when reply.Status == 429 => AccountNotice.RateLimited,
            _ => otherwise,
        };

    /// <summary>The service says the session is no longer valid: signed out, the lists forgotten.</summary>
    private void Expire(Operation op)
    {
        lock (_gate)
        {
            if (op.Generation != _generation || _disposed)
                return;
            _token = null;
            _generation++;
            Publish(_view.SignedOut(AccountNotice.Expired));
        }
    }

    /// <summary>
    /// Under the lock: when the session's lifetime has run out, signs out as the service's 401 would and returns
    /// true. Nothing is sent; the service ends the session by itself at the same time.
    /// </summary>
    private bool ExpireIfDue()
    {
        if (_token is null || Time.GetTimestamp() < _tokenExpires)
            return false;
        Abandon();
        Publish(_view.SignedOut(AccountNotice.Expired));
        return true;
    }

    /// <summary>Applies a change made by an operation, unless the generation it began in has passed.</summary>
    private bool Commit(Operation op, Func<AccountView, AccountView> change)
    {
        lock (_gate)
        {
            if (op.Generation != _generation || _disposed)
                return false;
            Publish(change(_view));
            return true;
        }
    }

    /// <summary>Ends the current generation: the token is dropped and a running operation cancelled. Under the lock.</summary>
    private void Abandon()
    {
        _generation++;
        _token = null;
        _operation?.Cancel();
        _operation = null;
    }

    private void Publish(AccountView view) => Volatile.Write(ref _view, view);

    // -----------------------------------------------------------------------------------------------------------
    // Formats
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>Unpadded base64url, as the service writes tokens, codes, attempts and verifiers.</summary>
    internal static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The S256 challenge of a verifier: base64url of SHA-256 of its ASCII text (RFC 7636).</summary>
    internal static string Challenge(string verifier) => Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>True when every character is a base64url character.</summary>
    internal static bool IsBase64UrlText(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                return false;
        }

        return true;
    }

    /// <summary>True for 32 random bytes in canonical unpadded base64url: 43 characters, the last one canonical.</summary>
    internal static bool IsCanonicalSecret(string? text)
    {
        if (text is not { Length: 43 } || !IsBase64UrlText(text))
            return false;
        Span<byte> bytes = stackalloc byte[33];
        return Convert.TryFromBase64String(text.Replace('-', '+').Replace('_', '/') + "=", bytes, out var written)
            && written == 32
            && Encode(bytes[..32]) == text;
    }

    /// <summary>True for an account id: 32 lower-case hex characters.</summary>
    public static bool IsAccountId(ReadOnlySpan<char> text)
    {
        if (text.Length != 32)
            return false;
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>True for a friend code: 16 upper-case hex characters.</summary>
    public static bool IsFriendCode(ReadOnlySpan<char> text)
    {
        if (text.Length != 16)
            return false;
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigitUpper(c) && !char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Cleans a name from the service for display: control, format (bidirectional overrides among them) and
    /// separator characters and broken surrogates removed, cut to <see cref="MaxNameChars"/> without splitting a
    /// character, "Pilot" when nothing is left. Characters a font lacks stay; the interface shows its fallback.
    /// </summary>
    public static string CleanName(string raw)
    {
        var clean = new StringBuilder(MaxNameChars);
        foreach (var rune in raw.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar && !raw.Contains('\uFFFD'))
                continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
                continue;
            if (clean.Length + rune.Utf16SequenceLength > MaxNameChars)
                break;
            clean.Append(rune.IsBmp ? (char)rune.Value : rune.ToString());
        }

        var text = clean.ToString().Trim();
        return text.Length == 0 ? "Pilot" : text;
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParseAttempt(ReadOnlyMemory<byte> json, out string attempt, out int expires)
    {
        attempt = "";
        expires = 0;
        try
        {
            using var document = JsonDocument.Parse(json, Strict);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "attempt"u8, 43, out attempt)
                && IsCanonicalSecret(attempt)
                && Whole(root, "expiresIn"u8, 1, 600, out expires);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseSession(ReadOnlyMemory<byte> json, out string token, out int lifetime, out string id, out string name, out string code)
    {
        token = id = name = code = "";
        lifetime = 0;
        try
        {
            using var document = JsonDocument.Parse(json, Strict);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "token"u8, 43, out token)
                && IsCanonicalSecret(token)
                && Whole(root, "expiresIn"u8, 1, 86400, out lifetime)
                && root.TryGetProperty("account"u8, out var account)
                && TryReadAccount(account, out id, out name, out code);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads an account object, <c>{id, name, friendCode}</c>, as <c>GET /v1/accounts/me</c> returns it.</summary>
    internal static bool TryParseAccount(ReadOnlyMemory<byte> json, out string id, out string name, out string code)
    {
        id = name = code = "";
        try
        {
            using var document = JsonDocument.Parse(json, Strict);
            return TryReadAccount(document.RootElement, out id, out name, out code);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadAccount(JsonElement account, out string id, out string name, out string code)
    {
        id = name = code = "";
        if (account.ValueKind != JsonValueKind.Object
            || !Text(account, "id"u8, 32, out id)
            || !IsAccountId(id)
            || !Text(account, "name"u8, 64, out var raw)
            || !Text(account, "friendCode"u8, 16, out code)
            || !IsFriendCode(code))
            return false;
        name = CleanName(raw);
        return true;
    }

    /// <summary>
    /// Reads a friends reply, <c>{friends, incoming, outgoing}</c>. The whole reply is refused when any part breaks
    /// a rule: a list too long, an entry with a bad id or time, the same account twice in a list.
    /// </summary>
    internal static bool TryParseFriends(ReadOnlyMemory<byte> json, out AccountFriend[] friends, out AccountFriend[] incoming, out AccountFriend[] outgoing)
    {
        friends = incoming = outgoing = AccountView.None;
        try
        {
            using var document = JsonDocument.Parse(json, Strict);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && List(root, "friends"u8, out friends)
                && List(root, "incoming"u8, out incoming)
                && List(root, "outgoing"u8, out outgoing);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool List(JsonElement root, ReadOnlySpan<byte> name, out AccountFriend[] list)
    {
        list = AccountView.None;
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxListEntries)
            return false;
        var entries = new AccountFriend[array.GetArrayLength()];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !Text(entry, "id"u8, 32, out var id)
                || !IsAccountId(id)
                || !seen.Add(id)
                || !Text(entry, "name"u8, 64, out var raw)
                || !Text(entry, "since"u8, 40, out var sinceText)
                || !DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var since)
                || since.Offset != TimeSpan.Zero
                || since.Year is < 2020 or > 2200)
                return false;
            entries[count++] = new AccountFriend(id, CleanName(raw), since);
        }

        list = entries;
        return true;
    }

    private static bool Text(JsonElement entry, ReadOnlySpan<byte> name, int maxLength, out string text)
    {
        text = "";
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString()!;
        return text.Length <= maxLength;
    }

    private static bool Whole(JsonElement entry, ReadOnlySpan<byte> name, int minimum, int maximum, out int number)
    {
        number = 0;
        return entry.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out number)
            && number >= minimum
            && number <= maximum;
    }
}
