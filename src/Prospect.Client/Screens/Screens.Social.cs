using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Social;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>The windows of the social workspace.</summary>
internal enum SocialPane : byte
{
    /// <summary>The narrow list that opens the others; open exactly while the workspace is.</summary>
    Navigator,
    Friends,
    Party,
    Players,
    Chat,

    /// <summary>One account or one player, chosen in Friends, Players or Party.</summary>
    Detail,
}

/// <summary>
/// The social interface: the workspace of movable windows (a navigator, Friends, Party, Players, Chat and a detail
/// card), the party list at the top left and the recent chat lines. A window can be pinned, which keeps it on
/// screen when the workspace closes and the player flies on; a pinned window then takes the pointer only while the
/// pointer is out (release key, command view) and never keyboard navigation, so W and Space still fly the hull.
/// Session state is read from <see cref="SessionSnapshot.Social"/> and every request goes through
/// <see cref="PlayerInputBridge"/>; the server decides. Names and chat text from the network only ever go to draw
/// calls, never into widget labels, ids or format strings.
/// </summary>
internal sealed partial class ScreenHost
{
    /// <summary>Seconds a chat line stays in the recent lines shown while chat is closed.</summary>
    private const float ChatFeedSeconds = 12f;

    /// <summary>A party member whose status is older than this is shown as out of contact.</summary>
    private const float PartyStaleSeconds = 2f;

    /// <summary>Seconds a notice (a refusal, a failed send, a request sent) stays readable.</summary>
    private const float NoticeSeconds = 6f;

    private const int PaneCount = 6;

    /// <summary>The workspace is open: the navigator and every open window, with the pointer shown.</summary>
    private bool _socialOpen;

    /// <summary>Chat has the keyboard: opened with its key, it closes again after a line is sent.</summary>
    private bool _chatOpen;
    private bool _chatFocus;
    private bool _socialHandled;
    private readonly FloatingWindow[] _panes = [new(), new(), new(), new(), new(), new()];
    private readonly HudRect[] _paneRects = new HudRect[PaneCount];
    private int _paneRectCount;
    private SocialPane _focusedPane;
    private SocialPane _raisePane = SocialPane.Navigator;
    private bool _raisePending;
    private readonly SocialDetail _detail = new();
    private ChatChannel _chatChannel;
    private ImInputString _chatInput = null!;
    private long _chatSeenTick = long.MinValue;
    private int _seenRefusals = -1;
    private readonly byte[] _chatLine = new byte[2048];
    private readonly float[] _chatHeights = new float[64];
    private int _chatHeightCount;
    private long _chatTotalSeen = -1;
    private float _chatScrollY;
    private bool _chatFollowing = true;
    private readonly byte[] _notice = new byte[256];
    private int _noticeLength;
    private bool _noticeBad;
    private float _noticeUntil;
    private byte[] _playersKey = [];
    private byte[] _socialKey = [];
    private byte[] _chatKey = [];

    /// <summary>True when a social shortcut was consumed this frame, so the world must not read it.</summary>
    public bool SocialHandled => _socialHandled;

    private bool SocialSurfaceOpen => _socialOpen || _chatOpen;

    /// <summary>The workspace is drawn: open in a game, or the Friends screen out of one.</summary>
    private bool Workspace => _socialOpen || _screen == Screen.Account;

    private FloatingWindow Pane(SocialPane pane) => _panes[(int)pane];

    private void InitializeSocial()
    {
        _chatInput = new ImInputString(_app.Content.Social.ChatBytes + 1);
        _playersKey = KeyName(InputAction.TogglePlayers);
        _socialKey = KeyName(InputAction.ToggleSocial);
        _chatKey = KeyName(InputAction.ToggleChat);
    }

    /// <summary>The first control bound to an action, as UTF-8 for drawing; empty when it is unbound.</summary>
    private byte[] KeyName(InputAction action)
    {
        var bindings = _app.Actions.Bindings(action);
        return bindings.Length == 0 ? [] : Encoding.UTF8.GetBytes(bindings[0].DisplayName.ToUpperInvariant());
    }

    /// <summary>Opens the workspace on the Players window, or with <paramref name="party"/> the Party window.</summary>
    public void OpenSocial(bool party) => OpenPane(party ? SocialPane.Party : SocialPane.Players);

    /// <summary>Opens the workspace on the Friends window.</summary>
    public void OpenFriends()
    {
        OpenPane(SocialPane.Friends);
        EnterAccount();
    }

    /// <summary>Opens the workspace, with <paramref name="pane"/> open and in front.</summary>
    private void OpenPane(SocialPane pane)
    {
        _socialOpen = true;
        Pane(pane).Open = true;
        Raise(pane);
    }

    /// <summary>Opens the workspace as the Social tab does: on what needs an answer, else as it was left.</summary>
    private void OpenWorkspace(SessionSnapshot? snapshot)
    {
        _socialOpen = true;
        if (snapshot is { Social.Invitation.Player: not 0 })
            OpenPane(SocialPane.Party);
        else if (_app.Accounts.View.Incoming.Length > 0)
            OpenPane(SocialPane.Friends);
        else if (!Pane(SocialPane.Friends).Open && !Pane(SocialPane.Party).Open && !Pane(SocialPane.Players).Open && !Pane(SocialPane.Chat).Open && !Pane(SocialPane.Detail).Open)
            OpenPane(SocialPane.Friends);
        if (Pane(SocialPane.Friends).Open)
            EnterAccount();
    }

    private void Raise(SocialPane pane)
    {
        _raisePane = pane;
        _raisePending = true;
        _focusedPane = pane;
    }

    /// <summary>Opens chat with its text field focused.</summary>
    public void OpenChat()
    {
        // A fresh open starts at the newest line.
        if (!_chatOpen && !PaneShown(SocialPane.Chat))
            _chatTotalSeen = -1;
        _chatOpen = true;
        _chatFocus = true;
        Pane(SocialPane.Chat).Open = true;
        Raise(SocialPane.Chat);
        // The key that opens chat must not also press a button keyboard navigation left focused in the workspace.
        var context = ImGui.GetCurrentContext();
        context.NavActivateId = default;
        context.NavActivateDownId = default;
        context.NavActivatePressedId = default;
    }

    /// <summary>Gives the keyboard back to flight. Chat stays on screen only in the workspace or pinned.</summary>
    private void EndChatTyping()
    {
        var typing = _chatOpen;
        _chatOpen = false;
        _chatFocus = false;
        if (!_socialOpen && !Pane(SocialPane.Chat).Pinned)
            Pane(SocialPane.Chat).Open = false;
        // Back to flight: a pinned chat left on screen must not keep keyboard focus into the next frame.
        if (typing && !_socialOpen)
            FloatingWindow.ReleaseKeyboard();
    }

    /// <summary>
    /// Closes the workspace. Pinned windows stay, without the keyboard: any text field or keyboard focus the
    /// workspace held is let go now, so the next frame's flight keys reach the hull.
    /// </summary>
    private void CloseWorkspace()
    {
        _socialOpen = false;
        EndChatTyping();
        FloatingWindow.ReleaseKeyboard();
    }

    private void ClosePane(SocialPane pane)
    {
        switch (pane)
        {
            case SocialPane.Navigator:
                if (_screen == Screen.Account)
                    Show(Screen.MainMenu);
                else
                    CloseWorkspace();
                return;
            case SocialPane.Chat:
                Pane(pane).Close();
                _chatOpen = _chatFocus = false;
                break;
            case SocialPane.Detail:
                Pane(pane).Close();
                _detail.Clear();
                break;
            case SocialPane.Friends when _screen == Screen.Account:
                // Friends is what the Friends screen is: closing it goes back to the menu.
                Pane(pane).Close();
                Show(Screen.MainMenu);
                return;
            default:
                Pane(pane).Close();
                break;
        }

        if (_focusedPane == pane)
            _focusedPane = SocialPane.Navigator;
    }

    /// <summary>
    /// Escape in a game: chat typing first, then the focused window if it is not pinned, then the workspace.
    /// Returns false when there was nothing social to close. Pinned windows are never closed by Escape.
    /// </summary>
    private bool SocialBack()
    {
        if (_chatOpen)
        {
            EndChatTyping();
            return true;
        }

        if (!_socialOpen)
            return false;
        var focused = _focusedPane;
        if (focused != SocialPane.Navigator && Pane(focused).Open && !Pane(focused).Pinned)
            ClosePane(focused);
        else
            CloseWorkspace();
        return true;
    }

    /// <summary>
    /// Forgets everything of the last session: the open workspace, the unsent line, notices and a player shown on
    /// the card. Where windows are and which are pinned is kept for the life of the game.
    /// </summary>
    private void ResetSocial()
    {
        _socialOpen = _chatOpen = _chatFocus = false;
        _chatInput.Clear();
        _chatChannel = ChatChannel.Everyone;
        _chatSeenTick = long.MinValue;
        _chatTotalSeen = -1;
        _chatFollowing = true;
        _seenRefusals = -1;
        _noticeUntil = 0;
        if (_detail.ShowsPlayer)
        {
            _detail.Clear();
            Pane(SocialPane.Detail).Close();
        }
    }

    /// <summary>
    /// Consumes the player list, party and chat shortcuts before flight reads this frame's input. Letter keys are
    /// left to a text field that has focus; the function keys and Escape are not, as with Controls.
    /// </summary>
    public void HandleSocialShortcuts(bool active)
    {
        _socialHandled = false;
        if (!active || _screen != Screen.Session || _controlsHandled || _controlsOpen || _sessionMenuOpen || _mapOpen)
            return;

        var typing = _ui.WantsTextInput;
        var pointerOwned = SocialSurfaceOpen || _ui.PointerOverInterface;
        var actions = _app.Actions;
        var devices = _app.Devices;
        if (actions.InterfaceShortcutPressed(devices, InputAction.TogglePlayers, typing, pointerOwned))
        {
            TogglePane(SocialPane.Players);
            _socialHandled = true;
        }
        else if (actions.InterfaceShortcutPressed(devices, InputAction.ToggleSocial, typing, pointerOwned))
        {
            TogglePane(SocialPane.Party);
            _socialHandled = true;
        }
        else if (actions.InterfaceShortcutPressed(devices, InputAction.ToggleChat, typing, pointerOwned))
        {
            // Open, or put the caret back in the field of a chat that is open; never closes, so the key that
            // sends a line cannot also close the window under the player's hands.
            OpenChat();
            _socialHandled = true;
        }
    }

    /// <summary>A window's key: opens the workspace on it, or closes the workspace when it is open there.</summary>
    private void TogglePane(SocialPane pane)
    {
        if (_socialOpen && Pane(pane).Open && _focusedPane == pane)
            CloseWorkspace();
        else
            OpenPane(pane);
    }

    /// <summary>Notices refusals that arrived since the last frame and keeps the chat channel valid.</summary>
    private void TrackSocial(SessionSnapshot snapshot)
    {
        var social = snapshot.Social;
        if (_seenRefusals >= 0 && social.RefusalCount > _seenRefusals)
            SetNotice(SocialView.Describe(social.LastRefusal), default, true);
        _seenRefusals = social.RefusalCount;

        if (_chatChannel == ChatChannel.Party && social.PartyCount == 0)
            _chatChannel = ChatChannel.Everyone;

        if ((_chatOpen || PaneShown(SocialPane.Chat)) && social.ChatCount > 0)
            _chatSeenTick = Math.Max(_chatSeenTick, social.Chat[social.ChatCount - 1].Tick);
    }

    /// <summary>True when a chat line from someone else arrived since chat was last on screen.</summary>
    private bool ChatUnread(SessionSnapshot snapshot)
    {
        var social = snapshot.Social;
        for (var i = social.ChatCount - 1; i >= 0; i--)
        {
            if (social.Chat[i].Tick <= _chatSeenTick)
                return false;
            if (social.Chat[i].Player != snapshot.PlayerId)
                return true;
        }

        return false;
    }

    /// <summary>Sets the notice: a sentence, optionally followed by a name drawn as text.</summary>
    private void SetNotice(ReadOnlySpan<byte> text, ReadOnlySpan<char> name, bool bad)
    {
        var buffer = _notice.AsSpan();
        var length = Math.Min(text.Length, buffer.Length);
        text[..length].CopyTo(buffer);
        if (!name.IsEmpty)
        {
            var room = buffer.Length - length;
            var chars = name[..Math.Min(name.Length, room / 3)];
            length += Encoding.UTF8.GetBytes(chars, buffer[length..]);
        }

        _noticeLength = length;
        _noticeBad = bad;
        _noticeUntil = _ui.Time + NoticeSeconds;
    }

    private bool NoticeShown => _noticeLength > 0 && _ui.Time < _noticeUntil;

    /// <summary>The notice as a line in the current window, or an empty line of the same height.</summary>
    private void DrawNoticeLine(float width)
    {
        var at = ImGui.GetCursorScreenPos();
        var height = Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        if (NoticeShown)
            Kit.ClippedText(ImGui.GetWindowDrawList(), _ui.Sans, Theme.FontSmall, at, _noticeBad ? Theme.Bad : Theme.Good, _notice.AsSpan(0, _noticeLength), width);
        ImGui.Dummy(new float2(width, height));
    }

    /// <summary>Sends a request through the bridge and tells the player when the queue would not take it.</summary>
    private void Request(bool queued, ReadOnlySpan<byte> sent, ReadOnlySpan<char> name)
    {
        if (queued)
            SetNotice(sent, name, false);
        else
            SetNotice(SocialView.Describe(ChatSendResult.QueueFull), default, true);
    }

    private PlayerInputBridge? Bridge => _app.Session?.Client.Input;

    // ---------------------------------------------------------------------------------------------------------------
    // The workspace's windows
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A window's size before it is fitted to the screen, in pixels.</summary>
    private static float2 PaneSize(SocialPane pane) => pane switch
    {
        SocialPane.Navigator => new float2(Kit.Px(240), Kit.Px(FloatingWindow.TitleHeight + Theme.Space2 + 4 * 40 + Theme.Space4 + 40)),
        SocialPane.Friends => new float2(Kit.Px(460), Kit.Px(600)),
        SocialPane.Party => new float2(Kit.Px(400), Kit.Px(420)),
        SocialPane.Players => new float2(Kit.Px(440), Kit.Px(480)),
        SocialPane.Chat => new float2(Kit.Px(540), Kit.Px(340)),
        _ => new float2(Kit.Px(360), Kit.Px(420)),
    };

    /// <summary>Where a window first appears: the navigator at the left, the lists beside it, the card at the right.</summary>
    private float2 PaneFallback(SocialPane pane)
    {
        var display = _ui.DisplaySize;
        var top = HudTop + Kit.Px(Theme.Space5);
        var left = Kit.Px(HudLayout.Margin);
        var beside = left + Kit.Px(240 + Theme.Space4);
        var size = PaneSize(pane);
        return pane switch
        {
            SocialPane.Navigator => new float2(left, top),
            SocialPane.Friends => new float2(beside, top),
            SocialPane.Players => new float2(beside + Kit.Px(Theme.Space6), top + Kit.Px(Theme.Space6)),
            SocialPane.Party => new float2(MathF.Round(display.X * 0.5f), display.Y - size.Y - left),
            SocialPane.Chat => new float2(left, display.Y - size.Y - left),
            _ => new float2(display.X - size.X - left, top),
        };
    }

    /// <summary>True when the window is drawn this frame.</summary>
    private bool PaneShown(SocialPane pane)
    {
        if (pane == SocialPane.Navigator)
            return Workspace;
        var window = Pane(pane);
        if (!window.Open)
            return false;
        var inGame = _screen == Screen.Session;
        switch (pane)
        {
            case SocialPane.Party or SocialPane.Players or SocialPane.Chat when !inGame:
                return false;
            case SocialPane.Detail when !(_detail.ShowsFriend || (inGame && _detail.ShowsPlayer)):
                return false;
        }

        return Workspace || window.Pinned || (pane == SocialPane.Chat && _chatOpen);
    }

    /// <summary>
    /// What a window may take: everything on a surface the player opened (the workspace, chat while typing, the
    /// Friends screen); the pointer only, never keyboard navigation, for a pinned window while the pointer is out;
    /// nothing while flight has the pointer.
    /// </summary>
    private FloatingInput PaneInput(SocialPane pane) =>
        _screen != Screen.Session || _socialOpen || (pane == SocialPane.Chat && _chatOpen) ? FloatingInput.Full
        : _app.PointerCaptured ? FloatingInput.Passive : FloatingInput.Pointer;

    /// <summary>The rectangles of this frame's social windows over the view, for the heads-up markers to keep clear of.</summary>
    private void CollectPaneRects()
    {
        _paneRectCount = 0;
        for (var i = 0; i < PaneCount; i++)
        {
            var pane = (SocialPane)i;
            if (PaneShown(pane))
                _paneRects[_paneRectCount++] = Pane(pane).Rect(PaneSize(pane), PaneFallback(pane), _ui.DisplaySize, Kit.Scale);
        }
    }

    /// <summary>
    /// Every social window drawn this frame: the workspace's, or only the pinned ones (and chat while typing) once
    /// it is closed. <paramref name="snapshot"/> is null out of a game, where only Friends and the card exist.
    /// </summary>
    private void DrawSocialWindows(SessionSnapshot? snapshot)
    {
        ScrubAccount();
        if (_detail.Scrub(_app.Accounts.View, _app.Session) || (!_detail.ShowsFriend && !_detail.ShowsPlayer))
            Pane(SocialPane.Detail).Open = false;

        if (snapshot is not null)
        {
            if (PaneShown(SocialPane.Chat))
                DrawChatPane(snapshot);
            if (PaneShown(SocialPane.Party))
                DrawPartyPane(snapshot);
            if (PaneShown(SocialPane.Players))
                DrawPlayersPane(snapshot);
        }

        if (PaneShown(SocialPane.Friends))
            DrawFriendsPane();
        if (PaneShown(SocialPane.Detail))
            DrawDetailPane(snapshot);
        if (PaneShown(SocialPane.Navigator))
            DrawNavigator(snapshot);
    }

    /// <summary>
    /// Begins a window with its frame and title bar, and a body under the title. False when the window was closed
    /// this frame; otherwise pair with <see cref="EndPane"/>. <paramref name="title"/> is drawn as text only.
    /// </summary>
    private bool BeginPane(SocialPane pane, ImString id, ReadOnlySpan<byte> title, bool scroll, out float width, out FloatingInput input)
    {
        var window = Pane(pane);
        input = PaneInput(pane);
        var pinnable = pane != SocialPane.Navigator && _screen == Screen.Session;
        var frame = window.Begin(id, PaneSize(pane), PaneFallback(pane), _ui.DisplaySize, Kit.Scale, input, pinnable);
        if (_raisePending && _raisePane == pane)
        {
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
            _raisePending = false;
        }

        if (frame.Focused && input == FloatingInput.Full)
            _focusedPane = pane;
        DrawPaneChrome(frame, title, pinnable, window.Pinned, input);
        if (frame.Close)
        {
            ClosePane(pane);
            FloatingWindow.End();
            width = 0;
            return false;
        }

        var pad = Kit.Px(Theme.Space4);
        var top = frame.Title + Kit.Px(Theme.Space2);
        ImGui.SetCursorScreenPos(frame.Min + new float2(pad, top));
        var body = new float2(Math.Max(1f, frame.Size.X - pad * 2), Math.Max(1f, frame.Size.Y - top - Kit.Px(Theme.Space3)));
        var flags = FloatingWindow.ChildFlags(input) | (scroll ? default : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.BeginChild("##body"u8, body, ImGuiChildFlags.None, flags);
        width = ImGui.GetContentRegionAvail().X - (scroll ? Kit.Px(Theme.Space3) : 0f);
        return true;
    }

    private static void EndPane()
    {
        ImGui.EndChild();
        FloatingWindow.End();
    }

    /// <summary>
    /// A window's frame: the heads-up panel with its amber tick, a hairline under the title, the drag grip, the
    /// title and the pin and close marks. The border warms only for the window that has focus in the workspace.
    /// </summary>
    private void DrawPaneChrome(in FloatingFrame frame, ReadOnlySpan<byte> title, bool pinnable, bool pinned, FloatingInput input)
    {
        var draw = ImGui.GetWindowDrawList();
        var min = frame.Min;
        var max = min + frame.Size;
        var chamfer = Kit.Px(Theme.Chamfer);
        Kit.ChamferFill(draw, min, max, Kit.Color(Theme.Panel, 0.94f), chamfer);
        Kit.ChamferOutline(draw, min, max, Kit.Color(Hud.Border(Theme.Accent, frame.Focused && input == FloatingInput.Full)), chamfer);
        draw.AddRectFilled(min, min + new float2(Kit.Px(Hud.TickWidth), Kit.Px(Hud.TickHeight)), Kit.Color(Theme.Accent));
        draw.AddLine(new float2(min.X + Kit.Px(1), min.Y + frame.Title), new float2(max.X - Kit.Px(1), min.Y + frame.Title), Kit.Color(Theme.Line, 0.8f));

        // The grip: two columns of three dots, amber while dragged.
        var dot = Kit.Px(2);
        var step = Kit.Px(4);
        var grip = new float2(min.X + Kit.Px(Theme.Space3), min.Y + MathF.Round(frame.Title * 0.5f) - step - dot * 0.5f);
        var gripColor = Kit.Color(frame.Dragging ? Theme.Accent : Theme.TextMute);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 2; column++)
                draw.AddRectFilled(grip + new float2(column * step, row * step), grip + new float2(column * step + dot, row * step + dot), gripColor);

        var button = Kit.Px(FloatingWindow.ButtonSize);
        var inset = (frame.Title - button) * 0.5f;
        var closeMin = new float2(max.X - inset - button, min.Y + inset);
        var pinMin = closeMin - new float2(button + Kit.Px(Theme.Space1), 0);
        var textLeft = grip.X + step + dot + Kit.Px(Theme.Space3);
        var textRight = (pinnable ? pinMin.X : closeMin.X) - Kit.Px(Theme.Space2);
        var textHeight = Kit.Measure(_ui.SansMedium, Theme.FontBody, "Ag"u8).Y;
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, new float2(textLeft, min.Y + (frame.Title - textHeight) * 0.5f), Theme.Text, title, Math.Max(0f, textRight - textLeft));

        var center = closeMin + new float2(button, button) * 0.5f;
        var arm = Kit.Px(4.5f);
        var closeColor = Kit.Color(frame.CloseHot ? Theme.Text : Theme.TextDim);
        draw.AddLine(center - new float2(arm, arm), center + new float2(arm, arm), closeColor, Kit.Px(1.5f));
        draw.AddLine(center + new float2(-arm, arm), center + new float2(arm, -arm), closeColor, Kit.Px(1.5f));
        if (!pinnable)
            return;

        // A push pin: its head, the bar it is pressed by and the needle. Amber and filled while pinned.
        var pin = pinMin + new float2(button, button) * 0.5f;
        var tone = pinned ? Theme.Accent : frame.PinHot ? Theme.Text : Theme.TextDim;
        var color = Kit.Color(tone);
        var headMin = pin + new float2(-Kit.Px(3), -Kit.Px(6));
        var headMax = pin + new float2(Kit.Px(3), -Kit.Px(1));
        if (pinned)
            draw.AddRectFilled(headMin, headMax, color);
        else
            draw.AddRect(headMin, headMax, color);
        draw.AddRectFilled(pin + new float2(-Kit.Px(5), -Kit.Px(1)), pin + new float2(Kit.Px(5), Kit.Px(1)), color);
        draw.AddLine(pin + new float2(0, Kit.Px(1)), pin + new float2(0, Kit.Px(6)), color, Kit.Px(1.2f));
    }

    /// <summary>
    /// The navigator: one row per window, lit while that window is open. Party, players and chat belong to a game;
    /// out of one they are shown but cannot be opened.
    /// </summary>
    private void DrawNavigator(SessionSnapshot? snapshot)
    {
        if (!BeginPane(SocialPane.Navigator, "##socialnav"u8, "SOCIAL"u8, false, out var width, out _))
            return;

        var view = _app.Accounts.View;
        var inGame = snapshot is not null && _screen == Screen.Session;
        var incoming = view.State == AccountState.SignedIn ? view.Incoming.Length : 0;
        if (NavRow("##navfriends"u8, "Friends"u8, incoming > 0 ? Kit.F($"+{incoming}") : default, SocialPane.Friends, true, incoming > 0))
            NavigatorToggle(SocialPane.Friends);

        var party = default(ReadOnlySpan<byte>);
        var online = 0;
        var invited = false;
        var unread = false;
        if (snapshot is not null)
        {
            var social = snapshot.Social;
            party = social.PartyCount > 0 ? Kit.F($"{social.PartyCount}/{_app.Content.Social.PartySize}") : default;
            for (var i = 0; i < social.RosterCount; i++)
                online += social.Roster[i].Value != 0 ? 1 : 0;
            invited = social.Invitation.Player != 0;
            unread = ChatUnread(snapshot) && !PaneShown(SocialPane.Chat);
        }

        if (NavRow("##navparty"u8, "Party"u8, party, SocialPane.Party, inGame, invited))
            NavigatorToggle(SocialPane.Party);
        if (NavRow("##navplayers"u8, "Players"u8, inGame ? Kit.F($"{online}") : default, SocialPane.Players, inGame, false))
            NavigatorToggle(SocialPane.Players);
        if (NavRow("##navchat"u8, "Chat"u8, default, SocialPane.Chat, inGame, unread))
        {
            if (Pane(SocialPane.Chat).Open)
                ClosePane(SocialPane.Chat);
            else
                OpenChat();
        }

        Kit.Space(Theme.Space2);
        Kit.Paragraph(_ui.Sans, Theme.FontCaption, Theme.TextMute, inGame
            ? "Pin a window to keep it over the view in flight. Hold the release key to use it."u8
            : "Party, players and chat open in a game."u8, width / Kit.Scale);
        EndPane();
    }

    private void NavigatorToggle(SocialPane pane)
    {
        if (Pane(pane).Open)
        {
            ClosePane(pane);
            return;
        }

        OpenPane(pane);
        if (pane == SocialPane.Friends)
            EnterAccount();
    }

    /// <summary>A navigator row: amber bar and raised fill while its window is open, a count and a dot for news.</summary>
    private bool NavRow(ImString id, ReadOnlySpan<byte> label, ReadOnlySpan<byte> count, SocialPane pane, bool enabled, bool badge)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(ImGui.GetContentRegionAvail().X, Kit.Px(40));
        if (!enabled)
            ImGui.BeginDisabled();
        var pressed = ImGui.InvisibleButton(id, size);
        var hot = Kit.Animate(ImGui.GetItemID(), enabled && Kit.IsHot() ? 1f : 0f);
        if (!enabled)
            ImGui.EndDisabled();

        var open = enabled && Pane(pane).Open;
        var max = position + size;
        if (open || hot > 0.01f)
            draw.AddRectFilled(position, max, Kit.Color(Theme.PanelRaised, open ? 0.9f : 0.9f * hot));
        if (open)
            draw.AddRectFilled(position, new float2(position.X + Kit.Px(3), max.Y), Kit.Color(Theme.Accent));
        var textHeight = Kit.Measure(_ui.SansMedium, Theme.FontBody, label).Y;
        var text = new float2(position.X + Kit.Px(Theme.Space4), position.Y + (size.Y - textHeight) * 0.5f);
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text, !enabled ? Theme.TextMute : open ? Theme.Text : Kit.Mix(Theme.TextDim, Theme.Text, hot), label);
        if (!count.IsEmpty)
        {
            var countSize = Kit.Measure(_ui.Mono, Theme.FontCaption, count);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(max.X - Kit.Px(Theme.Space3) - countSize.X, position.Y + (size.Y - countSize.Y) * 0.5f), badge ? Theme.Accent : Theme.TextDim, count);
        }
        else if (badge)
        {
            draw.AddCircleFilled(new float2(max.X - Kit.Px(Theme.Space4), position.Y + size.Y * 0.5f), Kit.Px(3.5f), Kit.Color(Theme.Accent), 12);
        }

        return pressed && enabled;
    }

    /// <summary>The players of this session, connected first and then those who left; a name opens the card.</summary>
    private void DrawPlayersPane(SessionSnapshot snapshot)
    {
        var online = 0;
        for (var i = 0; i < snapshot.Social.RosterCount; i++)
            online += snapshot.Social.Roster[i].Value != 0 ? 1 : 0;
        if (!BeginPane(SocialPane.Players, "##socialplayers"u8, Kit.F($"Players   {online} on this server"), true, out var width, out var input))
            return;
        DrawNoticeLine(width);
        Kit.Space(Theme.Space1);
        DrawPlayerRows(snapshot, width, input);
        EndPane();
    }

    /// <summary>The party: members, the invitation waiting for an answer, and the way to invite or leave.</summary>
    private void DrawPartyPane(SessionSnapshot snapshot)
    {
        var social = snapshot.Social;
        var title = social.PartyCount > 0 ? Kit.F($"Party   {social.PartyCount}/{_app.Content.Social.PartySize}") : "Party"u8;
        if (!BeginPane(SocialPane.Party, "##socialparty"u8, title, true, out var width, out _))
            return;
        DrawNoticeLine(width);
        Kit.Space(Theme.Space1);
        DrawPartyRows(snapshot, width);
        EndPane();
    }

    /// <summary>The card: one account from the friends lists, or one player of this session.</summary>
    private void DrawDetailPane(SessionSnapshot? snapshot)
    {
        if (_detail.ShowsFriend)
        {
            var view = _app.Accounts.View;
            var friend = _detail.Friend(view, out var relation);
            if (!BeginPane(SocialPane.Detail, "##socialdetail"u8, friend is null ? "Account"u8 : friend.NameUtf8, true, out var width, out _))
                return;
            DrawFriendDetail(view, friend, relation, width);
            EndPane();
            return;
        }

        if (snapshot is null)
            return;
        var index = _detail.Roster(_app.Session, snapshot.Social);
        var title = index < 0 ? "Player"u8 : PlayerTitle(snapshot.Social.Roster[index]);
        if (!BeginPane(SocialPane.Detail, "##socialdetail"u8, title, true, out var playerWidth, out _))
            return;
        DrawPlayerDetail(snapshot, index, playerWidth);
        EndPane();
    }

    private static ReadOnlySpan<byte> PlayerTitle(in SocialMessage entry) =>
        entry.Length == 0 ? Kit.F($"Player {entry.Player}") : Kit.F($"{(ReadOnlySpan<char>)entry.Text[..entry.Length]}");

    /// <summary>
    /// What this session knows of a player: their session name, whether they are still here, party standing and,
    /// for a party member, ship and sector. A session name is what they typed when joining, not an account, so it
    /// is never taken for a friend. The actions are the ones the server would take from this player now.
    /// </summary>
    private void DrawPlayerDetail(SessionSnapshot snapshot, int index, float width)
    {
        var social = snapshot.Social;
        var wrap = width / Kit.Scale;
        if (index < 0)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "This player is no longer in this session's list."u8, wrap);
            return;
        }

        var entry = social.Roster[index];
        var me = snapshot.PlayerId;
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var nameHeight = Kit.Measure(_ui.SansBold, Theme.FontHeading, "Ag"u8).Y;
        Kit.ClippedText(draw, _ui.SansBold, Theme.FontHeading, at, Theme.Text, PlayerTitle(entry), width);
        ImGui.Dummy(new float2(width, nameHeight));
        Kit.Space(Theme.Space1);
        var connected = entry.Value != 0;
        var member = SocialView.IsMember(social, entry.Player);
        ReadOnlySpan<byte> standing =
            entry.Player == me ? "YOU"u8
            : !connected ? "LEFT THE SERVER"u8
            : member && social.Leader == entry.Player ? "IN YOUR PARTY   LEADER"u8
            : member ? "IN YOUR PARTY"u8
            : social.Invitation.Player == entry.Player ? "INVITED YOU"u8
            : "ON THIS SERVER"u8;
        Kit.Label(_ui.SansMedium, Theme.FontCaption, entry.Player == me || member ? Theme.Accent : connected ? Theme.TextDim : Theme.TextMute, standing);
        Kit.Space(Theme.Space2);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "A session name, chosen when joining. It is not an account and is never matched to your friends."u8, wrap);

        for (var i = 0; i < social.PartyCount; i++)
        {
            ref var status = ref social.Party[i];
            if (status.Player != entry.Player)
                continue;
            Kit.Space(Theme.Space3);
            var designs = snapshot.Content.Designs;
            var design = designs[Math.Min((int)status.Value, designs.Length - 1)];
            var age = SocialView.SecondsSince(status.Tick, snapshot.ServerTick);
            var here = _app.World.Sector.Coord;
            Kit.Label(_ui.Mono, Theme.FontCaption, age > PartyStaleSeconds ? Theme.Bad : Theme.TextDim,
                age > PartyStaleSeconds ? Kit.F($"{design.Name}   NO SIGNAL FOR {age:0} S")
                : status.X == here.X && status.Y == here.Y && status.Z == here.Z ? Kit.F($"{design.Name}   YOUR SECTOR")
                : Kit.F($"{design.Name}   SECTOR {status.X} {status.Y} {status.Z}"));
            Kit.Space(Theme.Space2);
            var bar = new float2(Math.Min(Kit.Px(140), (width - Kit.Px(Theme.Space3)) * 0.5f), Kit.Px(3));
            var barMin = ImGui.GetCursorScreenPos();
            Gauge(draw, barMin, bar, status.Shield, Theme.Info);
            Gauge(draw, barMin + new float2(bar.X + Kit.Px(Theme.Space3), 0), bar, status.Hull, Theme.Good);
            ImGui.Dummy(new float2(width, bar.Y));
        }

        Kit.Space(Theme.Space4);
        var name = entry.Text[..entry.Length];
        // Every button is scoped to the player's session id, so a press cannot complete on another player's card.
        ImGui.PushID(unchecked((int)entry.Player));
        if (SocialView.CanInvite(social, me, entry, _app.Content.Social.PartySize))
        {
            if (Kit.Button("Invite to party"u8, ButtonKind.Primary, 180) && Bridge is { } bridge)
                Request(bridge.Invite(entry.Player), "Invitation sent to "u8, name);
        }
        else if (member && entry.Player != me && SocialView.IsLeader(social, me))
        {
            if (Kit.Button("Remove from party"u8, ButtonKind.Secondary, 200) && Bridge is { } bridge)
                Request(bridge.RemoveMember(entry.Player), "Removing from the party: "u8, name);
        }

        ImGui.PopID();
        if (social.Invitation.Player == entry.Player)
        {
            Kit.Space(Theme.Space3);
            DrawInvitationCard(snapshot, width);
        }

        Kit.Space(Theme.Space2);
        DrawNoticeLine(width);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Always shown: party list, recent chat, quick buttons
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The lowest a column from <paramref name="left"/> to <paramref name="right"/> may reach over the pilot
    /// console and, while it is drawn, the jump strip.
    /// </summary>
    private float ReservedTop(float left, float right) =>
        HudLayout.ReservedTop(_ui.DisplaySize, Kit.Scale, left, right, _jumpPrompt != JumpPrompt.None);

    /// <summary>
    /// The height the party list takes as full cards: the invitation, the caption and one card per other member.
    /// Zero when there is neither a party nor an invitation.
    /// </summary>
    private float PartyHudCardsHeight(SessionSnapshot snapshot)
    {
        var social = snapshot.Social;
        var height = 0f;
        if (social.Invitation.Player != 0)
            height += Kit.Px(52) + Kit.Px(Theme.Space2);
        if (social.PartyCount >= 2)
            height += Kit.Px(18) + OtherMembers(snapshot) * (Kit.Px(58) + Kit.Px(Theme.Space1));
        return height;
    }

    private static int OtherMembers(SessionSnapshot snapshot) =>
        snapshot.Social.PartyCount - (SocialView.IsMember(snapshot.Social, snapshot.PlayerId) ? 1 : 0);

    /// <summary>
    /// The party at the top left, under the sector panel: every other member's name, ship, shield and hull, and
    /// where they are, as cards. Where the cards do not fit above <paramref name="limit"/>, one-line rows with name,
    /// shield and hull, as many as fit and never fewer than one, with the rest counted in the caption; the Social
    /// panel has the details. A pending invitation shows above it. This is presence on this server only. Returns
    /// where the list ends.
    /// </summary>
    private float DrawPartyHud(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float top, float limit, bool cards)
    {
        var social = snapshot.Social;
        var me = snapshot.PlayerId;
        var x = Kit.Px(Theme.Space5);
        var width = HudLayout.ColumnWidth(_ui.DisplaySize, Kit.Scale);
        var y = top + Kit.Px(Theme.Space3);
        var pad = Kit.Px(Theme.Space3);
        var rows = HudLayout.PartyRows(top, limit, cards, social.Invitation.Player != 0,
            social.PartyCount >= 2 ? OtherMembers(snapshot) : 0, Kit.Scale);

        if (social.Invitation.Player != 0)
        {
            var seconds = SocialView.SecondsUntil(social.Invitation.Tick, snapshot.ServerTick);
            var name = SocialView.Name(social, social.Invitation.Player);
            var height = cards ? Kit.Px(52) : Kit.Px(24);
            var min = new float2(x, y);
            // An invitation waits for an answer: the one party piece lit in amber, the colour that means act here.
            Hud.Plate(draw, min, min + new float2(width, height), Theme.Accent, true);
            if (cards)
            {
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(pad, Kit.Px(8)), Theme.Accent, Kit.F($"PARTY INVITATION   {seconds} S"));
                var line = _playersKey.Length > 0 ? Kit.F($"{name} invites you. {_playersKey} to answer.") : Kit.F($"{name} invites you. Open Players to answer.");
                Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, min + new float2(pad, Kit.Px(26)), Theme.Text, line, width - pad * 2);
            }
            else
            {
                Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(pad, Kit.Px(6)), Theme.Accent, Kit.F($"INVITE FROM {name}   {seconds} S   {_playersKey}"), width - pad * 2);
            }

            y += height + Kit.Px(Theme.Space2);
        }

        if (social.PartyCount < 2)
            return y;

        var others = OtherMembers(snapshot);
        var rowHeight = Kit.Px(24);
        var step = rows.Step;
        var shownMax = rows.Count;
        var hidden = others - shownMax;
        var size = _app.Content.Social.PartySize;
        var caption = hidden > 0
            ? Kit.F($"PARTY   {social.PartyCount}/{size}   +{hidden} MORE   {_socialKey} FOR ALL")
            : Kit.F($"PARTY   {social.PartyCount}/{size}");
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, new float2(x, y), Theme.Accent, caption, width);
        y = rows.Top;

        var shown = 0;
        var designs = snapshot.Content.Designs;
        var here = world.Sector.Coord;
        // The leader first, so the one who invites and removes is never among those counted as more.
        for (var n = 0; n < social.PartyCount * 2 && shown < shownMax; n++)
        {
            ref var member = ref social.Party[n % social.PartyCount];
            if (member.Player == me || (member.Player == social.Leader) != (n < social.PartyCount))
                continue;

            shown++;
            var leader = member.Player == social.Leader;
            var name = SocialView.Name(social, member.Player);
            var nameText = name.IsEmpty ? Kit.F($"Player {member.Player}") : Kit.F($"{name}");
            var age = SocialView.SecondsSince(member.Tick, snapshot.ServerTick);
            var stale = age > PartyStaleSeconds;
            var min = new float2(x, y);
            // The lit edge: red out of contact, amber for the leader, cyan for everyone else.
            var tone = stale ? Theme.Bad : leader ? Theme.Accent : Theme.Info;

            if (!cards)
            {
                // One line: a leader mark, the name, then shield over hull at the right.
                var max = min + new float2(width, rowHeight);
                Hud.Plate(draw, min, max, tone);
                // The bars give way first in a narrowed column, so the name keeps its room.
                var barWidth = Math.Min(Kit.Px(110), MathF.Round(width * 0.3f));
                var nameAt = min + new float2(pad, (rowHeight - Kit.Measure(_ui.SansMedium, Theme.FontSmall, "Ag"u8).Y) * 0.5f);
                Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, nameAt, stale ? Theme.Bad : Theme.Text, nameText, width - barWidth - pad * 3);
                var gaugeAt = new float2(max.X - pad - barWidth, min.Y + rowHeight * 0.5f - Kit.Px(4));
                Gauge(draw, gaugeAt, new float2(barWidth, Kit.Px(3)), member.Shield, Theme.Info);
                Gauge(draw, gaugeAt + new float2(0, Kit.Px(5)), new float2(barWidth, Kit.Px(3)), member.Hull, Theme.Good);
                y += step;
                continue;
            }

            var cardMax = min + new float2(width, Kit.Px(58));
            Hud.Plate(draw, min, cardMax, tone);

            var text = min + new float2(pad, Kit.Px(7));
            var tagWidth = leader ? Kit.Measure(_ui.SansMedium, Theme.FontCaption, "LEADER"u8).X : 0f;
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, text, Theme.Text, nameText, width - pad * 3 - tagWidth);
            if (leader)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(cardMax.X - pad - tagWidth, text.Y + Kit.Px(2)), Theme.Accent, "LEADER"u8);

            var design = designs[Math.Min((int)member.Value, designs.Length - 1)];
            var status = text + new float2(0, Kit.Px(19));
            ReadOnlySpan<byte> where;
            if (stale)
                where = Kit.F($"{design.Name}   NO SIGNAL {age:0} S");
            else if (member.X == here.X && member.Y == here.Y && member.Z == here.Z)
                where = Kit.F($"{design.Name}   YOUR SECTOR");
            else
                where = Kit.F($"{design.Name}   SECTOR {member.X} {member.Y} {member.Z}");
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, status, stale ? Theme.Bad : Theme.TextDim, where, width - pad * 2);

            var bar = new float2((width - pad * 3) * 0.5f, Kit.Px(3));
            var barMin = new float2(text.X, cardMax.Y - Kit.Px(10));
            Gauge(draw, barMin, bar, member.Shield, Theme.Info);
            Gauge(draw, barMin + new float2(bar.X + pad, 0), bar, member.Hull, Theme.Good);
            y += step;
        }

        return y;
    }

    private static void Gauge(ImDrawListPtr draw, float2 min, float2 size, float fraction, uint rgb)
    {
        draw.AddRectFilled(min, min + size, Kit.Color(Theme.Void));
        draw.AddRectFilled(min, min + new float2(size.X * Math.Clamp(fraction, 0f, 1f), size.Y), Kit.Color(rgb));
    }

    /// <summary>
    /// The last few chat lines at the bottom left while chat is closed, fading out after a few seconds. Drawn on
    /// the background layer: nothing here takes the pointer. The measurement pass reserves the same bounds without painting.
    /// </summary>
    private HudRect DrawChatFeed(ImDrawListPtr draw, SessionSnapshot snapshot, float top, bool paint)
    {
        var social = snapshot.Social;
        if (social.ChatCount == 0)
            return default;

        var display = _ui.DisplaySize;
        var x = Kit.Px(Theme.Space5);
        var width = HudLayout.ChatWidth(display, Kit.Scale, x, Math.Min(Kit.Px(440), display.X - Kit.Px(48)));
        // Never into the middle of the view. Beside the flight readouts at the bottom when there is room; above them
        // when not, narrowed to stay beside the jump strip where that leaves a readable line.
        var readoutsLeft = FlightConsole.Layout(display, Kit.Scale).LeftX;
        var beside = x + width + Kit.Px(Theme.Space3) < readoutsLeft;
        if (!beside)
            width = HudLayout.FeedWidth(display, Kit.Scale, x, width, Kit.Px(300));
        var bottom = beside ? display.Y - Kit.Px(Theme.Space5) : ReservedTop(x, x + width) - Kit.Px(Theme.Space3);

        ImGui.PushFont(_ui.Sans, Theme.FontSmall);
        var fontSize = ImGui.GetFontSize();
        var y = bottom;
        var feedTop = bottom;
        var shown = 0;
        for (var i = social.ChatCount - 1; i >= 0 && shown < 5; i--)
        {
            ref var line = ref social.Chat[i];
            var age = SocialView.SecondsSince(line.Tick, snapshot.ServerTick);
            if (age >= ChatFeedSeconds)
                break;

            var length = ComposeChatLine(social, line, out var head);
            var bytes = _chatLine.AsSpan(0, length);
            var height = ImGui.CalcTextSize(bytes, false, width - Kit.Px(Theme.Space3)).Y;
            y -= height + Kit.Px(6);
            // Never over the sector panel or the party list above.
            if (y < top + Kit.Px(Theme.Space3))
                break;

            if (paint)
            {
                var alpha = Math.Clamp((ChatFeedSeconds - age) / 2f, 0f, 1f);
                var min = new float2(x, y);
                draw.AddRectFilled(min, min + new float2(width, height + Kit.Px(4)), Kit.Color(Theme.Void, 0.55f * alpha));
                var at = min + new float2(Kit.Px(6), Kit.Px(2));
                draw.AddText(_ui.Sans, fontSize, at, Kit.Color(Theme.Text, alpha), bytes, width - Kit.Px(Theme.Space3));
                draw.AddText(_ui.Sans, fontSize, at, Kit.Color(LineColor(snapshot, line), alpha), _chatLine.AsSpan(0, head), width - Kit.Px(Theme.Space3));
            }
            feedTop = y;
            shown++;
        }

        ImGui.PopFont();
        return shown > 0 ? new HudRect(x, feedTop, x + width, bottom) : default;
    }

    private void DrawPlayerRows(SessionSnapshot snapshot, float width, FloatingInput input)
    {
        var social = snapshot.Social;
        var me = snapshot.PlayerId;
        DrawInvitationCard(snapshot, width);

        if (social.PartyCount > 0 && social.Leader != me)
        {
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.TextDim, "Only the party leader can invite."u8);
            Kit.Space(Theme.Space2);
        }

        // Connected players first, then those who left recently.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < social.RosterCount; i++)
            {
                ref var entry = ref social.Roster[i];
                if ((entry.Value != 0) != (pass == 0))
                    continue;
                DrawPlayerRow(snapshot, entry, width, input);
            }
        }

        Kit.Space(Theme.Space3);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Players on this server, and who left it this session. These are session names, not accounts: friends are in Friends. Select a name for its card."u8, width / Kit.Scale);
    }

    private void DrawPlayerRow(SessionSnapshot snapshot, in SocialMessage entry, float width, FloatingInput input)
    {
        var social = snapshot.Social;
        var me = snapshot.PlayerId;
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var height = Kit.Px(48);
        var connected = entry.Value != 0;
        var canInvite = SocialView.CanInvite(social, me, entry, _app.Content.Social.PartySize);
        var buttonWidth = Kit.Px(104);

        var name = entry.Text[..entry.Length];
        var nameText = entry.Length == 0 ? Kit.F($"Player {entry.Player}") : Kit.F($"{(ReadOnlySpan<char>)name}");
        var nameWidth = width - Kit.Px(Theme.Space3) - (canInvite ? buttonWidth + Kit.Px(Theme.Space3) : 0);
        var selected = _detail.Player == entry.Player && PaneShown(SocialPane.Detail);
        // The name opens the card. Scoped to the session id, so a press cannot complete on another player's row.
        ImGui.PushID(unchecked((int)entry.Player));
        if (input != FloatingInput.Passive && ImGui.InvisibleButton("##card"u8, new float2(Math.Max(1f, nameWidth), height)) && _app.Session is { } session)
        {
            _detail.SelectPlayer(session, entry.Player);
            OpenPane(SocialPane.Detail);
        }

        var hot = input != FloatingInput.Passive && ImGui.IsItemHovered();
        ImGui.PopID();
        if (selected || hot)
            draw.AddRectFilled(position, position + new float2(width, height), Kit.Color(Theme.PanelRaised, selected ? 0.9f : 0.6f));
        if (selected)
            draw.AddRectFilled(position, position + new float2(Kit.Px(3), height), Kit.Color(Theme.Accent));
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, position + new float2(Kit.Px(Theme.Space3), Kit.Px(4)), connected ? Theme.Text : Theme.TextMute, nameText, nameWidth);

        ReadOnlySpan<byte> tag =
            entry.Player == me ? (social.Leader == me && social.PartyCount > 0 ? "YOU   PARTY LEADER"u8 : "YOU"u8)
            : !connected ? "LEFT THE SERVER"u8
            : SocialView.IsMember(social, entry.Player) ? (social.Leader == entry.Player ? "IN YOUR PARTY   LEADER"u8 : "IN YOUR PARTY"u8)
            : social.Invitation.Player == entry.Player ? "INVITED YOU"u8
            : "CONNECTED"u8;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, position + new float2(Kit.Px(Theme.Space3), Kit.Px(27)), entry.Player == me ? Theme.Accent : connected ? Theme.TextDim : Theme.TextMute, tag);

        if (canInvite)
        {
            ImGui.SetCursorScreenPos(new float2(position.X + width - buttonWidth, position.Y + (height - Kit.Px(Theme.ControlHeight)) * 0.5f));
            ImGui.PushID((int)entry.Player);
            if (Kit.Button("Invite"u8, ButtonKind.Secondary, 104) && Bridge is { } bridge)
                Request(bridge.Invite(entry.Player), "Invitation sent to "u8, name);
            ImGui.PopID();
        }

        draw.AddLine(position + new float2(0, height - 0.5f), position + new float2(width, height - 0.5f), Kit.Color(Theme.Line, 0.6f));
        ImGui.SetCursorScreenPos(position);
        ImGui.Dummy(new float2(width, height));
    }

    /// <summary>
    /// A pending invitation with Accept and Decline. Both answer the invitation drawn here, by the identity it
    /// carries in this frame's snapshot, so a newer invitation that arrives meanwhile is never answered blindly.
    /// </summary>
    private void DrawInvitationCard(SessionSnapshot snapshot, float width)
    {
        var invitation = snapshot.Social.Invitation;
        if (invitation.Player == 0)
            return;

        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var height = Kit.Px(112);
        var pad = Kit.Px(Theme.Space3);
        Kit.ChamferFill(draw, position, position + new float2(width, height), Kit.Color(Theme.PanelRaised, 0.9f), Kit.Px(Theme.Chamfer));
        Kit.ChamferOutline(draw, position, position + new float2(width, height), Kit.Color(Theme.Accent), Kit.Px(Theme.Chamfer));
        var seconds = SocialView.SecondsUntil(invitation.Tick, snapshot.ServerTick);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, position + new float2(pad, pad), Theme.Accent, Kit.F($"PARTY INVITATION   EXPIRES IN {seconds} S"));
        var name = SocialView.Name(snapshot.Social, invitation.Player);
        var line = name.IsEmpty ? Kit.F($"Player {invitation.Player} invites you to their party.") : Kit.F($"{name} invites you to their party.");
        Kit.ClippedText(draw, _ui.Sans, Theme.FontBody, position + new float2(pad, pad + Kit.Px(18)), Theme.Text, line, width - pad * 2);

        var id = invitation.InvitationId;
        // A replacement invitation must not inherit a press that began on the previous card.
        Kit.PushId(id);
        ImGui.SetCursorScreenPos(position + new float2(pad, height - pad - Kit.Px(Theme.ControlHeight)));
        if (Kit.Button("Accept"u8, ButtonKind.Primary, 120) && Bridge is { } accept)
            Request(accept.AcceptInvitation(id), "Accepted. Joining the party."u8, default);
        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        if (Kit.Button("Decline"u8, ButtonKind.Secondary, 120) && Bridge is { } decline)
            Request(decline.DeclineInvitation(id), "Invitation declined."u8, default);
        Kit.PopId();

        ImGui.SetCursorScreenPos(position);
        ImGui.Dummy(new float2(width, height));
        Kit.Space(Theme.Space3);
    }

    private void DrawPartyRows(SessionSnapshot snapshot, float width)
    {
        var social = snapshot.Social;
        var me = snapshot.PlayerId;
        DrawInvitationCard(snapshot, width);

        if (social.PartyCount == 0)
        {
            Kit.Body("You are not in a party."u8);
            Kit.Space(Theme.Space2);
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, Kit.F($"Invite players from Players. A party has up to {_app.Content.Social.PartySize} members, shares a chat channel and shows its members at the top left."), width / Kit.Scale);
            Kit.Space(Theme.Space3);
            if (Kit.Button("Invite players"u8, ButtonKind.Secondary, 180))
                OpenPane(SocialPane.Players);
            return;
        }

        var leader = SocialView.IsLeader(social, me);
        var designs = snapshot.Content.Designs;
        var here = _app.World.Sector.Coord;
        for (var i = 0; i < social.PartyCount; i++)
        {
            ref var member = ref social.Party[i];
            var draw = ImGui.GetWindowDrawList();
            var position = ImGui.GetCursorScreenPos();
            var height = Kit.Px(64);
            var canRemove = leader && member.Player != me;
            var buttonWidth = Kit.Px(104);
            var textWidth = width - Kit.Px(Theme.Space3) - (canRemove ? buttonWidth + Kit.Px(Theme.Space3) : 0);

            var name = SocialView.Name(social, member.Player);
            var nameText = name.IsEmpty ? Kit.F($"Player {member.Player}") : Kit.F($"{name}");
            var text = position + new float2(Kit.Px(Theme.Space3), Kit.Px(4));
            var drawn = Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, text, Theme.Text, nameText, textWidth * 0.6f);
            ReadOnlySpan<byte> tag = member.Player == me ? (member.Player == social.Leader ? "YOU   LEADER"u8 : "YOU"u8) : member.Player == social.Leader ? "LEADER"u8 : default;
            if (!tag.IsEmpty)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text + new float2(drawn + Kit.Px(Theme.Space3), Kit.Px(4)), Theme.Accent, tag);

            var design = designs[Math.Min((int)member.Value, designs.Length - 1)];
            var age = SocialView.SecondsSince(member.Tick, snapshot.ServerTick);
            ReadOnlySpan<byte> where =
                age > PartyStaleSeconds ? Kit.F($"{design.Name}   NO SIGNAL FOR {age:0} S")
                : member.X == here.X && member.Y == here.Y && member.Z == here.Z ? Kit.F($"{design.Name}   YOUR SECTOR")
                : Kit.F($"{design.Name}   SECTOR {member.X} {member.Y} {member.Z}");
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(24)), age > PartyStaleSeconds ? Theme.Bad : Theme.TextDim, where, textWidth);
            var bar = new float2(Math.Min(Kit.Px(140), (textWidth - Kit.Px(Theme.Space3)) * 0.5f), Kit.Px(3));
            var barMin = text + new float2(0, Kit.Px(44));
            Gauge(draw, barMin, bar, member.Shield, Theme.Info);
            Gauge(draw, barMin + new float2(bar.X + Kit.Px(Theme.Space3), 0), bar, member.Hull, Theme.Good);

            if (canRemove)
            {
                ImGui.SetCursorScreenPos(new float2(position.X + width - buttonWidth, position.Y + (height - Kit.Px(Theme.ControlHeight)) * 0.5f));
                ImGui.PushID((int)member.Player);
                if (Kit.Button("Remove"u8, ButtonKind.Secondary, 104) && Bridge is { } bridge)
                    Request(bridge.RemoveMember(member.Player), "Removing from the party: "u8, name);
                ImGui.PopID();
            }

            draw.AddLine(position + new float2(0, height - 0.5f), position + new float2(width, height - 0.5f), Kit.Color(Theme.Line, 0.6f));
            ImGui.SetCursorScreenPos(position);
            ImGui.Dummy(new float2(width, height));
        }

        Kit.Space(Theme.Space4);
        if (leader && social.PartyCount < _app.Content.Social.PartySize)
        {
            if (Kit.Button("Invite players"u8, ButtonKind.Secondary, 180))
                OpenPane(SocialPane.Players);
            ImGui.SameLine(0, Kit.Px(Theme.Space2));
        }

        if (Kit.Button("Leave Party"u8, ButtonKind.Secondary, 160) && Bridge is { } leave)
            Request(leave.LeaveParty(), "Leaving the party."u8, default);
        Kit.Space(Theme.Space2);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, leader ? "You lead this party: you invite and remove members."u8 : "The leader invites and removes members."u8, width / Kit.Scale);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Chat
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Chat: the channel, the log and, while chat has the keyboard or the workspace is open, the line being typed.
    /// Pinned in flight it shows the log and how to start typing; the field is not there, so it cannot hold keys.
    /// </summary>
    private void DrawChatPane(SessionSnapshot snapshot)
    {
        var social = snapshot.Social;
        if (!BeginPane(SocialPane.Chat, "##chat"u8, "Chat"u8, false, out var width, out var input))
            return;

        // The channel buttons take the pointer only: keyboard navigation never rests on them, so Enter, which
        // opens chat and sends a line, cannot also press one.
        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        var headerAt = ImGui.GetCursorScreenPos();
        // Picking a channel puts the caret back in the field, so the key that sends is never left on a button.
        if (ChannelButton("##everyone"u8, "EVERYONE"u8, _chatChannel == ChatChannel.Everyone, true))
        {
            _chatChannel = ChatChannel.Everyone;
            _chatFocus = _chatOpen || _socialOpen;
        }

        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        if (ChannelButton("##party"u8, "PARTY"u8, _chatChannel == ChatChannel.Party, social.PartyCount > 0))
        {
            _chatChannel = ChatChannel.Party;
            _chatFocus = _chatOpen || _socialOpen;
        }

        ImGui.PopItemFlag();
        ImGui.SetCursorScreenPos(headerAt + new float2(0, Kit.Px(30)));

        var typing = _chatOpen || _socialOpen;
        var lineHeight = Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        var inputHeight = typing ? Kit.Px(Theme.ControlHeight) + Kit.Px(Theme.Space1) : 0f;
        var logHeight = Math.Max(Kit.Px(40), ImGui.GetContentRegionAvail().Y - inputHeight - lineHeight - Kit.Px(Theme.Space2));
        DrawChatLog(snapshot, width, logHeight, FloatingWindow.ChildFlags(input));

        Kit.Space(Theme.Space2);
        if (typing)
        {
            DrawChatStatus(width);
            Kit.Space(Theme.Space1);
            DrawChatInput(snapshot, width);
        }
        else
        {
            var at = ImGui.GetCursorScreenPos();
            Kit.ClippedText(ImGui.GetWindowDrawList(), _ui.Sans, Theme.FontSmall, at, Theme.TextMute,
                _chatKey.Length > 0 ? Kit.F($"{_chatKey} to type a line.") : "Open Social to type a line."u8, width);
            ImGui.Dummy(new float2(width, lineHeight));
        }

        EndPane();
    }

    private bool ChannelButton(ImString id, ReadOnlySpan<byte> label, bool active, bool enabled)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var textSize = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label);
        var size = textSize + new float2(Kit.Px(Theme.Space3) * 2, Kit.Px(Theme.Space1) * 2);
        if (!enabled)
            ImGui.BeginDisabled();
        var pressed = ImGui.InvisibleButton(id, size);
        var hot = Kit.Animate(ImGui.GetItemID(), enabled && Kit.IsHot() ? 1f : 0f);
        if (!enabled)
            ImGui.EndDisabled();
        var lit = Math.Max(hot, active ? 1f : 0f);
        if (active)
            draw.AddRectFilled(position, position + size, Kit.Color(Theme.Accent, 0.18f));
        draw.AddRect(position, position + size, Kit.Color(enabled ? Kit.Mix(Theme.Line, Theme.Accent, lit) : Theme.Line));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, position + new float2(Kit.Px(Theme.Space3), Kit.Px(Theme.Space1)), !enabled ? Theme.TextMute : Kit.Mix(Theme.TextDim, Theme.Text, lit), label);
        return pressed && enabled;
    }

    private void DrawChatLog(SessionSnapshot snapshot, float width, float height, ImGuiWindowFlags flags)
    {
        var social = snapshot.Social;
        // The log keeps the newest 64 lines: each new line past that pushes the oldest off the front, moving the
        // rest up. A reader scrolled back keeps their place: the scroll moves up by the height of what left.
        var justOpened = _chatTotalSeen < 0;
        var dropped = justOpened ? 0 : (int)Math.Clamp(social.ChatTotal - _chatTotalSeen - (social.ChatCount - _chatHeightCount), 0, _chatHeightCount);
        if (dropped > 0 && !_chatFollowing)
        {
            var removed = 0f;
            for (var i = 0; i < dropped; i++)
                removed += _chatHeights[i];
            ImGui.SetNextWindowScroll(new float2(0, Math.Max(0f, _chatScrollY - removed)));
        }

        ImGui.BeginChild("##chatlog"u8, new float2(width, height), ImGuiChildFlags.None, flags);
        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - Kit.Px(4);
        var wrap = ImGui.GetContentRegionAvail().X - Kit.Px(Theme.Space2);
        if (social.ChatCount == 0)
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.TextMute, "No messages yet."u8);

        var draw = ImGui.GetWindowDrawList();
        ImGui.PushFont(_ui.Sans, Theme.FontSmall);
        var fontSize = ImGui.GetFontSize();
        for (var i = 0; i < social.ChatCount; i++)
        {
            ref var line = ref social.Chat[i];
            var length = ComposeChatLine(social, line, out var head);
            var bytes = _chatLine.AsSpan(0, length);
            var position = ImGui.GetCursorScreenPos();
            var size = ImGui.CalcTextSize(bytes, false, wrap);
            draw.AddText(_ui.Sans, fontSize, position, Kit.Color(Theme.Text), bytes, wrap);
            draw.AddText(_ui.Sans, fontSize, position, Kit.Color(LineColor(snapshot, line)), _chatLine.AsSpan(0, head), wrap);
            ImGui.Dummy(new float2(wrap, size.Y + Kit.Px(3)));
            if (i < _chatHeights.Length)
                _chatHeights[i] = size.Y + Kit.Px(3);
        }

        ImGui.PopFont();
        _chatHeightCount = Math.Min(social.ChatCount, _chatHeights.Length);
        _chatTotalSeen = social.ChatTotal;
        // Follow new lines while the log is at its end; a player scrolled back to read stays where they are.
        if (atBottom || justOpened)
            ImGui.SetScrollHereY(1f);
        _chatFollowing = atBottom || justOpened;
        _chatScrollY = ImGui.GetScrollY();
        ImGui.EndChild();
    }

    /// <summary>The notice, or the length of the line being typed once it nears the limit.</summary>
    private void DrawChatStatus(float width)
    {
        var at = ImGui.GetCursorScreenPos();
        var height = Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        var draw = ImGui.GetWindowDrawList();
        var rules = _app.Content.Social;
        var bytes = _chatInput.ValueSpan;
        var chars = Encoding.UTF8.GetCharCount(bytes);
        var over = chars > rules.ChatChars || bytes.Length > rules.ChatBytes;
        if (over || chars > rules.ChatChars * 3 / 4)
        {
            var counter = Kit.F($"{chars}/{rules.ChatChars}");
            var counterWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, counter).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + width - counterWidth, at.Y + Kit.Px(2)), over ? Theme.Bad : Theme.TextMute, counter);
            width -= counterWidth + Kit.Px(Theme.Space3);
        }

        if (NoticeShown)
            Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, at, _noticeBad ? Theme.Bad : Theme.Good, _notice.AsSpan(0, _noticeLength), width);
        else
            Kit.Text(draw, _ui.Sans, Theme.FontSmall, at, Theme.TextMute, _chatChannel == ChatChannel.Party ? "To your party. Enter sends, Esc closes."u8 : "To everyone on this server. Enter sends, Esc closes."u8);
        ImGui.Dummy(new float2(width, height));
    }

    private void DrawChatInput(SessionSnapshot snapshot, float width)
    {
        var sendWidth = Kit.Px(96);
        if (_chatFocus)
        {
            ImGui.SetKeyboardFocusHere();
            _chatFocus = false;
        }

        ImGui.SetNextItemWidth(width - sendWidth - Kit.Px(Theme.Space2));
        ImGui.PushFont(_ui.Sans, Theme.FontBody);
        var submitted = ImGui.InputText("##chatinput"u8, _chatInput.Buffer, ImGuiInputTextFlags.EnterReturnsTrue);
        _chatInput.EvaluateLength();
        ImGui.PopFont();
        if (ImGui.IsItemActive())
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), Kit.Color(Theme.Accent));

        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        var clicked = Kit.Button("Send"u8, ButtonKind.Primary, 96, !_chatInput.IsEmpty);
        ImGui.PopItemFlag();
        if (!submitted && !clicked)
            return;

        // Enter on an empty line gives the keyboard back to flight; Enter on a line sends it and does the same. The
        // Send button keeps the caret in the field. Chat stays on screen in the workspace or pinned.
        if (_chatInput.IsEmpty)
        {
            if (submitted)
                EndChatTyping();
            return;
        }

        if (Bridge is not { } bridge)
            return;
        var result = SocialView.SendChat(bridge, snapshot.Social, _chatChannel, _chatInput.ValueSpan, _app.Content.Social);
        if (result == ChatSendResult.Sent)
        {
            _chatInput.Clear();
            _noticeUntil = 0;
            if (submitted)
                EndChatTyping();
            else
                _chatFocus = true;
        }
        else
        {
            SetNotice(SocialView.Describe(result), default, true);
            _chatFocus = true;
        }
    }

    /// <summary>
    /// Writes "[Party] Name: text" as UTF-8 into the line buffer and returns its length; <paramref name="head"/> is
    /// the length of the part drawn in the sender's colour. The text is the server's cleaned line, shown as it is.
    /// </summary>
    private int ComposeChatLine(SocialState social, in SocialMessage line, out int head)
    {
        var buffer = _chatLine.AsSpan();
        var length = 0;
        if ((ChatChannel)line.Value == ChatChannel.Party)
        {
            "[Party] "u8.CopyTo(buffer);
            length = 8;
        }

        var name = SocialView.Name(social, line.Player);
        if (name.IsEmpty)
        {
            "Player "u8.CopyTo(buffer[length..]);
            length += 7;
            line.Player.TryFormat(buffer[length..], out var written);
            length += written;
        }
        else
        {
            length += Encoding.UTF8.GetBytes(name[..Math.Min(name.Length, 256)], buffer[length..]);
        }

        head = length;
        ": "u8.CopyTo(buffer[length..]);
        length += 2;
        length += Encoding.UTF8.GetBytes(line.Text[..Math.Min(line.Length, 256)], buffer[length..]);
        return length;
    }

    private static uint LineColor(SessionSnapshot snapshot, in SocialMessage line) =>
        line.Player == snapshot.PlayerId ? Theme.Accent : (ChatChannel)line.Value == ChatChannel.Party ? Theme.Info : Theme.TextDim;
}
