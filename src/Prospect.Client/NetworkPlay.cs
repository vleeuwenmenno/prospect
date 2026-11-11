using Prospect.Game.Client;
using Prospect.Game.Lobby;

namespace Prospect.Client;

/// <summary>
/// The official client's rule for playing over the network (docs/accounts.md): joining a server and hosting an
/// unlisted or public game need a Discord sign-in; a private game never does. This is the client's own policy at
/// the moment a game is entered, not authentication: game servers do not see or check the account, the wire keeps
/// its session identities, and nothing here ends a game that is already running when the sign-in later ends.
/// </summary>
internal static class NetworkPlay
{
    /// <summary>
    /// True when the player may join or host for others now: <paramref name="accounts"/> holds a current session
    /// (<see cref="AccountClient.HasCurrentSession"/>). Otherwise false with a sentence for the player.
    /// </summary>
    /// <param name="hosting">True for hosting a game others join; false for joining one.</param>
    public static bool Allowed(AccountClient accounts, bool hosting, out string error)
    {
        error = "";
        if (accounts.HasCurrentSession())
            return true;
        if (accounts.View.State == AccountState.Off)
            error = hosting
                ? "Hosting for others needs a Discord sign-in, and accounts are off (--listing off). Choose Private to play alone."
                : "Joining a server needs a Discord sign-in, and accounts are off (--listing off). Private games still work.";
        else
            error = hosting ? "Sign in with Discord to host for others, or choose Private." : "Sign in with Discord to join a server.";
        return false;
    }

    /// <summary>
    /// Opens a session in place of the current one. A network session is checked with <see cref="Allowed"/>
    /// first, before <paramref name="leave"/> touches the session that runs, so a refused attempt leaves it as it
    /// was and opens no socket. Once allowed, the current session is left, then <paramref name="open"/> runs; a
    /// failure there returns false with its message, the old session already gone.
    /// </summary>
    /// <param name="accounts">The player's account.</param>
    /// <param name="network">True for a join, or a local game others can join; false for a private one.</param>
    /// <param name="hosting">For the sentence: true when the network session is one this game hosts.</param>
    /// <param name="leave">Ends the current session, if any.</param>
    /// <param name="open">Starts the new session; may throw <see cref="IOException"/> or <see cref="InvalidDataException"/>.</param>
    /// <param name="session">The new session, or null.</param>
    /// <param name="error">Why it was not opened, or empty.</param>
    public static bool TryEnter(AccountClient accounts, bool network, bool hosting, Action leave, Func<GameSession> open, out GameSession? session, out string error)
    {
        session = null;
        if (network && !Allowed(accounts, hosting, out error))
            return false;
        leave();
        try
        {
            session = open();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            error = e.Message;
            return false;
        }

        error = "";
        return true;
    }
}
