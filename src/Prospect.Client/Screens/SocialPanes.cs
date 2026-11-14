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
    // Charts, Inventory and Player start open: interface mode shows every window that is open and not pinned.
    private readonly FloatingWindow[] _windows =
    [
        new(), new(), new(), new(), new(), new(),
        new() { Open = true }, new() { Open = true }, new() { Open = true },
        new(),
    ];

    /// <summary>The names windows are saved under in the client settings.</summary>
    public static string NameOf(SocialPane pane) => pane switch
    {
        SocialPane.Navigator => "Social",
        SocialPane.Friends => "Friends",
        SocialPane.Party => "Party",
        SocialPane.Players => "Players",
        SocialPane.Chat => "Chat",
        SocialPane.Detail => "Detail",
        SocialPane.Charts => "Charts",
        SocialPane.Inventory => "Inventory",
        SocialPane.Gas => "Gas",
        _ => "Player",
    };

    /// <summary>The number of windows.</summary>
    public const int Count = 10;

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
    /// True when the window is drawn: the navigator while the workspace is; any other open window in the workspace,
    /// pinned, or chat while it is being typed in. Party, Players, Chat, Charts, Inventory and Player need a game; the card needs something to show.
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
            case SocialPane.Party or SocialPane.Players or SocialPane.Chat or SocialPane.Charts or SocialPane.Inventory or SocialPane.Player or SocialPane.Gas when !inGame:
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
