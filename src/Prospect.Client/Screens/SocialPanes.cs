using Prospect.Client.Social;

namespace Prospect.Client.Screens;

/// <summary>What the screen must do after a social window's close.</summary>
internal enum PaneClose : byte
{
    /// <summary>The window closed; the screen stays.</summary>
    Closed,

    /// <summary>The navigator closed in a game: the whole workspace closes, pinned windows stay.</summary>
    CloseWorkspace,

    /// <summary>The Friends screen's navigator or Friends closed: back to the main menu.</summary>
    LeaveFriendsScreen,
}

/// <summary>
/// The social windows' lifecycle without drawing: which are open or pinned, what a close does, what Escape closes,
/// which are on screen, and what a new session forgets. <c>ScreenHost</c> draws them; tests drive this as it is.
/// </summary>
internal sealed class SocialPanes
{
    private readonly FloatingWindow[] _windows = [new(), new(), new(), new(), new(), new()];

    public FloatingWindow this[SocialPane pane] => _windows[(int)pane];

    /// <summary>
    /// The player's explicit close of <paramref name="pane"/> (its X, or its navigator row). It removes the window,
    /// pinned or not, except the navigator, whose close only closes the workspace and leaves every window as it was.
    /// On the Friends screen (<paramref name="friendsScreen"/>), closing the navigator or Friends leaves the screen.
    /// </summary>
    public PaneClose Close(SocialPane pane, bool friendsScreen)
    {
        if (pane == SocialPane.Navigator)
            return friendsScreen ? PaneClose.LeaveFriendsScreen : PaneClose.CloseWorkspace;
        this[pane].Close();
        return friendsScreen && pane == SocialPane.Friends ? PaneClose.LeaveFriendsScreen : PaneClose.Closed;
    }

    /// <summary>
    /// What Escape closes in the workspace: the focused window when it is open and not pinned, else null for the
    /// workspace itself. Escape never closes a pinned window.
    /// </summary>
    public SocialPane? BackTarget(SocialPane focused) =>
        focused != SocialPane.Navigator && this[focused].Open && !this[focused].Pinned ? focused : null;

    /// <summary>
    /// True when the window is drawn: the navigator while the workspace is; any other open window in the workspace,
    /// pinned, or chat while it is being typed in. Party, Players and Chat need a game; the card needs something to show.
    /// </summary>
    public bool Shown(SocialPane pane, bool workspace, bool inGame, bool chatTyping, SocialDetail detail)
    {
        if (pane == SocialPane.Navigator)
            return workspace;
        var window = this[pane];
        if (!window.Open)
            return false;
        switch (pane)
        {
            case SocialPane.Party or SocialPane.Players or SocialPane.Chat when !inGame:
                return false;
            case SocialPane.Detail when !(detail.ShowsFriend || (inGame && detail.ShowsPlayer)):
                return false;
        }

        return workspace || window.Pinned || (pane == SocialPane.Chat && chatTyping);
    }

    /// <summary>
    /// What a session's end forgets: a player shown on the card. Where windows are and which are pinned is kept for
    /// the life of the game, so a window closed by the player stays closed and a pinned one comes back pinned.
    /// </summary>
    public void ResetSession(SocialDetail detail)
    {
        if (!detail.ShowsPlayer)
            return;
        detail.Clear();
        this[SocialPane.Detail].Close();
    }
}
