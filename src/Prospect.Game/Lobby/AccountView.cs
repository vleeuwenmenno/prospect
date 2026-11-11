using System.Text;

namespace Prospect.Game.Lobby;

/// <summary>Where the player's account stands.</summary>
public enum AccountState : byte
{
    /// <summary>There is no listing service (<c>--listing off</c>), so there are no accounts.</summary>
    Off,

    /// <summary>Not signed in. Guest and local play are unaffected.</summary>
    SignedOut,

    /// <summary>Opening the local port and asking the service for an attempt.</summary>
    Starting,

    /// <summary>The browser is open at Discord; the game waits for it to come back.</summary>
    Waiting,

    /// <summary>The browser came back; the game is exchanging the code for a session.</summary>
    Finishing,

    /// <summary>Signed in. The friends lists are those of the last refresh.</summary>
    SignedIn,
}

/// <summary>The last thing worth telling the player. The interface picks the words.</summary>
public enum AccountNotice : byte
{
    None,
    SignedIn,
    SignedOut,
    SigninCancelled,
    SigninDenied,
    SigninFailed,
    SigninTimedOut,
    BrowserNotOpened,
    PortUnavailable,
    Expired,
    Unavailable,
    RateLimited,
    Unreachable,
    BadReply,
    Refreshed,
    InvalidCode,
    RequestSent,
    RequestSettled,
    UnknownCode,
    CannotAdd,
    LimitReached,
    Accepted,
    Declined,
    RequestCancelled,
    Removed,
    Gone,
}

/// <summary>Another account in one of the friends lists, checked and cleaned.</summary>
public sealed class AccountFriend
{
    internal AccountFriend(string id, string name, DateTimeOffset since)
    {
        Id = id;
        Name = name;
        NameUtf8 = Encoding.UTF8.GetBytes(name);
        Since = since;
        SinceUtf8 = Encoding.ASCII.GetBytes(since.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Key = ulong.Parse(id.AsSpan(0, 16), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture)
            ^ ulong.Parse(id.AsSpan(16, 16), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The account's id: 32 lower-case hex characters.</summary>
    public string Id { get; }

    /// <summary>The cleaned display name. Untrusted: draw it as text only, never as a label, id or format.</summary>
    public string Name { get; }

    /// <summary><see cref="Name"/> as UTF-8, made once, for drawing without allocating.</summary>
    public byte[] NameUtf8 { get; }

    /// <summary>When the friendship was accepted, or the request made.</summary>
    public DateTimeOffset Since { get; }

    /// <summary><see cref="Since"/> as a UTC date (yyyy-MM-dd) in ASCII, for drawing.</summary>
    public byte[] SinceUtf8 { get; }

    /// <summary>A 64-bit key from the id, for scoping a row's buttons to this account.</summary>
    public ulong Key { get; }
}

/// <summary>
/// Everything the interface shows about the account at one moment. Never changes once made, so the render thread
/// reads it every frame without a lock. The session token is not in it.
/// </summary>
public sealed class AccountView
{
    internal static readonly AccountFriend[] None = [];

    internal AccountView(AccountState state)
    {
        State = state;
    }

    public AccountState State { get; private init; }

    /// <summary>True while an operation runs; the interface offers no other until it ends.</summary>
    public bool Busy { get; private init; }

    /// <summary>The account's id, or empty when signed out.</summary>
    public string Id { get; private init; } = "";

    /// <summary>The cleaned display name, or empty when signed out. Draw as text only.</summary>
    public string Name { get; private init; } = "";

    public byte[] NameUtf8 { get; private init; } = [];

    /// <summary>The player's own friend code, 16 upper-case hex characters, or empty.</summary>
    public string FriendCode { get; private init; } = "";

    public AccountFriend[] Friends { get; private init; } = None;
    public AccountFriend[] Incoming { get; private init; } = None;
    public AccountFriend[] Outgoing { get; private init; } = None;

    /// <summary>True once a friends list has arrived for this sign-in.</summary>
    public bool Listed { get; private init; }

    /// <summary>The page the browser was sent to while waiting, for copying when it did not open; else null.</summary>
    public string? BrowserUrl { get; private init; }

    public AccountNotice Notice { get; private init; }

    /// <summary>Counts notices, so the same notice twice is still new.</summary>
    public int NoticeCount { get; private init; }

    internal long ListedAt { get; private init; }

    internal AccountView With(
        AccountState? state = null,
        bool? busy = null,
        AccountNotice? notice = null,
        string? browserUrl = null,
        bool clearBrowserUrl = false
    ) =>
        new(state ?? State)
        {
            Busy = busy ?? Busy,
            Id = Id,
            Name = Name,
            NameUtf8 = NameUtf8,
            FriendCode = FriendCode,
            Friends = Friends,
            Incoming = Incoming,
            Outgoing = Outgoing,
            Listed = Listed,
            ListedAt = ListedAt,
            BrowserUrl = clearBrowserUrl ? null : browserUrl ?? BrowserUrl,
            Notice = notice ?? Notice,
            NoticeCount = notice is null ? NoticeCount : NoticeCount + 1,
        };

    internal AccountView WithAccount(string id, string name, string friendCode) =>
        new(AccountState.SignedIn)
        {
            Busy = Busy,
            Id = id,
            Name = name,
            NameUtf8 = Encoding.UTF8.GetBytes(name),
            FriendCode = friendCode,
            Notice = Notice,
            NoticeCount = NoticeCount,
        };

    internal AccountView WithLists(AccountFriend[] friends, AccountFriend[] incoming, AccountFriend[] outgoing, long at) =>
        new(State)
        {
            Busy = Busy,
            Id = Id,
            Name = Name,
            NameUtf8 = NameUtf8,
            FriendCode = FriendCode,
            Friends = friends,
            Incoming = incoming,
            Outgoing = outgoing,
            Listed = true,
            ListedAt = at,
            BrowserUrl = BrowserUrl,
            Notice = Notice,
            NoticeCount = NoticeCount,
        };

    /// <summary>A signed-out view that keeps only the notice count, so nothing of the old account remains.</summary>
    internal AccountView SignedOut(AccountNotice notice, AccountState state = AccountState.SignedOut) =>
        new(state) { Notice = notice, NoticeCount = NoticeCount + 1 };
}
