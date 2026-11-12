using Prospect.Game.Client;
using Prospect.Game.Lobby;

namespace Prospect.Client.Social;

/// <summary>Which friends list an account is in.</summary>
internal enum FriendRelation : byte
{
    /// <summary>They asked the player; the player may accept or decline.</summary>
    Incoming,

    /// <summary>An accepted friend.</summary>
    Friends,

    /// <summary>The player asked them; the player may cancel.</summary>
    Outgoing,
}

/// <summary>
/// What the detail card shows: one account from the friends lists, or one player of this session. A selection is
/// held by identity, never by row or name: an account by its key and the account that chose it, a player by their
/// session id and the session they were chosen in. It is read again from the current lists every frame, so an
/// action always goes to the account or player shown, and a selection that no longer holds reads as gone.
/// Signing out, another account signing in, or another session clears it. No drawing, so tests use it as it is.
/// </summary>
internal sealed class SocialDetail
{
    private ulong _friend;
    private string _account = "";
    private uint _player;
    private object? _session;

    /// <summary>True while an account is selected (it may since have left the lists).</summary>
    public bool ShowsFriend => _friend != 0;

    /// <summary>True while a player is selected (they may since have left).</summary>
    public bool ShowsPlayer => _player != 0;

    /// <summary>The selected player's session id, or zero.</summary>
    public uint Player => _player;

    /// <summary>The selected account's key, or zero.</summary>
    public ulong FriendKey => _friend;

    /// <summary>Shows an account, chosen while <paramref name="view"/> was the account's state.</summary>
    public void SelectFriend(AccountView view, AccountFriend friend)
    {
        _player = 0;
        _session = null;
        _friend = friend.Key;
        _account = view.Id;
    }

    /// <summary>Shows a player of <paramref name="session"/>.</summary>
    public void SelectPlayer(object session, uint player)
    {
        _friend = 0;
        _account = "";
        _player = player;
        _session = session;
    }

    public void Clear()
    {
        _friend = 0;
        _account = "";
        _player = 0;
        _session = null;
    }

    /// <summary>
    /// Forgets an account chosen under another sign-in (signed out, or someone else signed in), and a player of
    /// another session. Returns true when something was forgotten, so the card can close.
    /// </summary>
    public bool Scrub(AccountView view, object? session)
    {
        var scrubbed = false;
        if (_friend != 0 && (view.State != AccountState.SignedIn || view.Id != _account))
        {
            _friend = 0;
            _account = "";
            scrubbed = true;
        }

        if (_player != 0 && !ReferenceEquals(session, _session))
        {
            _player = 0;
            _session = null;
            scrubbed = true;
        }

        return scrubbed;
    }

    /// <summary>The selected account as the current lists have it, or null when it is no longer in any of them.</summary>
    public AccountFriend? Friend(AccountView view, out FriendRelation relation)
    {
        relation = FriendRelation.Friends;
        if (_friend == 0 || view.State != AccountState.SignedIn || view.Id != _account)
            return null;
        return Find(view, _friend, out relation);
    }

    /// <summary>
    /// The selected player's roster index in <paramref name="social"/>, or -1 when they are not in it. A departed
    /// player stays in the roster, marked as gone, until newer departures replace them.
    /// </summary>
    public int Roster(object? session, SocialState social) =>
        _player == 0 || !ReferenceEquals(session, _session) ? -1 : SocialView.FindRoster(social, _player);

    /// <summary>The account with <paramref name="key"/> in the lists of <paramref name="view"/>, and which list.</summary>
    public static AccountFriend? Find(AccountView view, ulong key, out FriendRelation relation)
    {
        foreach (var friend in view.Incoming)
        {
            if (friend.Key == key)
            {
                relation = FriendRelation.Incoming;
                return friend;
            }
        }

        foreach (var friend in view.Friends)
        {
            if (friend.Key == key)
            {
                relation = FriendRelation.Friends;
                return friend;
            }
        }

        foreach (var friend in view.Outgoing)
        {
            if (friend.Key == key)
            {
                relation = FriendRelation.Outgoing;
                return friend;
            }
        }

        relation = FriendRelation.Friends;
        return null;
    }
}
