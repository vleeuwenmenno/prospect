using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Social;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The social interface over the world: the player list and party panel, chat, the party list at the top left,
/// the recent chat lines and the quick buttons at the top right. Everything is read from
/// <see cref="SessionSnapshot.Social"/> and every request goes through <see cref="PlayerInputBridge"/>; the server
/// decides. Names and chat text from the network only ever go to draw calls, never into widget labels, ids or
/// format strings.
/// </summary>
internal sealed partial class ScreenHost
{
    /// <summary>Height of the quick buttons at the top right, in design pixels.</summary>
    private const float QuickBarHeight = 32f;

    /// <summary>Seconds a chat line stays in the recent lines shown while chat is closed.</summary>
    private const float ChatFeedSeconds = 12f;

    /// <summary>A party member whose status is older than this is shown as out of contact.</summary>
    private const float PartyStaleSeconds = 2f;

    /// <summary>Seconds a notice (a refusal, a failed send, a request sent) stays readable.</summary>
    private const float NoticeSeconds = 6f;

    private bool _socialOpen;
    private bool _socialParty;
    private bool _chatOpen;
    private bool _chatFocus;
    private bool _socialHandled;
    private bool _quickBarShown;
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

    private float QuickBarBottom => Kit.Px(Theme.Space5 + QuickBarHeight);

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

    /// <summary>Opens the player list, or with <paramref name="party"/> the party panel.</summary>
    public void OpenSocial(bool party)
    {
        _chatOpen = _chatFocus = false;
        _socialOpen = true;
        _socialParty = party;
    }

    /// <summary>Opens chat with its text field focused.</summary>
    public void OpenChat()
    {
        // A fresh open starts at the newest line.
        _chatTotalSeen = -1;
        // The opening key must not also activate a focused button in the player panel.
        _socialOpen = false;
        _chatOpen = true;
        _chatFocus = true;
    }

    private void CloseSocialSurfaces()
    {
        _socialOpen = _chatOpen = false;
        _chatFocus = false;
    }

    /// <summary>Forgets everything of the last session: open panels, the unsent line, notices.</summary>
    private void ResetSocial()
    {
        CloseSocialSurfaces();
        _chatInput.Clear();
        _chatChannel = ChatChannel.Everyone;
        _chatSeenTick = long.MinValue;
        _chatTotalSeen = -1;
        _chatFollowing = true;
        _seenRefusals = -1;
        _noticeUntil = 0;
        _quickBarShown = false;
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
            ToggleSocial(false);
            _socialHandled = true;
        }
        else if (actions.InterfaceShortcutPressed(devices, InputAction.ToggleSocial, typing, pointerOwned))
        {
            ToggleSocial(true);
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

    private void ToggleSocial(bool party)
    {
        if (_socialOpen && _socialParty == party)
            _socialOpen = false;
        else
            OpenSocial(party);
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

        if (_chatOpen && social.ChatCount > 0)
            _chatSeenTick = Math.Max(_chatSeenTick, social.Chat[social.ChatCount - 1].Tick);
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
    // Always shown: party list, recent chat, quick buttons
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Where the flight readouts at the bottom centre begin; nothing on the left may reach below it.</summary>
    private float ReadoutsTop => _ui.DisplaySize.Y - Kit.Px(136);

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
        var width = Kit.Px(360);
        var y = top + Kit.Px(Theme.Space3);
        var pad = Kit.Px(Theme.Space3);

        if (social.Invitation.Player != 0)
        {
            var seconds = SocialView.SecondsUntil(social.Invitation.Tick, snapshot.ServerTick);
            var name = SocialView.Name(social, social.Invitation.Player);
            var height = cards ? Kit.Px(52) : Kit.Px(24);
            var min = new float2(x, y);
            Kit.ChamferFill(draw, min, min + new float2(width, height), Kit.Color(Theme.Panel, 0.92f), Kit.Px(Theme.Chamfer));
            Kit.ChamferOutline(draw, min, min + new float2(width, height), Kit.Color(Theme.Accent), Kit.Px(Theme.Chamfer));
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
        var captionHeight = Kit.Px(18);
        var rowHeight = Kit.Px(24);
        var step = (cards ? Kit.Px(58) : rowHeight) + Kit.Px(Theme.Space1);
        // At least one member is always shown; the rest that do not fit are counted in the caption.
        var fit = Math.Max(1, (int)((limit - y - captionHeight) / step));
        var shownMax = Math.Min(others, fit);
        var hidden = others - shownMax;
        var size = _app.Content.Social.PartySize;
        var caption = hidden > 0
            ? Kit.F($"PARTY   {social.PartyCount}/{size}   +{hidden} MORE   {_socialKey} FOR ALL")
            : Kit.F($"PARTY   {social.PartyCount}/{size}");
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, new float2(x, y), Theme.Accent, caption, width);
        y += captionHeight;

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

            if (!cards)
            {
                // One line: a leader mark, the name, then shield over hull at the right.
                var max = min + new float2(width, rowHeight);
                Kit.ChamferFill(draw, min, max, Kit.Color(Theme.Panel, 0.86f), Kit.Px(Theme.Chamfer * 0.6f));
                if (leader)
                    draw.AddRectFilled(min, new float2(min.X + Kit.Px(3), max.Y), Kit.Color(Theme.Accent));
                var barWidth = Kit.Px(110);
                var nameAt = min + new float2(pad, (rowHeight - Kit.Measure(_ui.SansMedium, Theme.FontSmall, "Ag"u8).Y) * 0.5f);
                Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, nameAt, stale ? Theme.Bad : Theme.Text, nameText, width - barWidth - pad * 3);
                var gaugeAt = new float2(max.X - pad - barWidth, min.Y + rowHeight * 0.5f - Kit.Px(4));
                Gauge(draw, gaugeAt, new float2(barWidth, Kit.Px(3)), member.Shield, Theme.Info);
                Gauge(draw, gaugeAt + new float2(0, Kit.Px(5)), new float2(barWidth, Kit.Px(3)), member.Hull, Theme.Good);
                y += step;
                continue;
            }

            var cardMax = min + new float2(width, Kit.Px(58));
            Kit.ChamferFill(draw, min, cardMax, Kit.Color(Theme.Panel, 0.86f), Kit.Px(Theme.Chamfer));
            Kit.ChamferOutline(draw, min, cardMax, Kit.Color(Theme.Line), Kit.Px(Theme.Chamfer));

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
    /// the background layer: nothing here takes the pointer.
    /// </summary>
    private void DrawChatFeed(ImDrawListPtr draw, SessionSnapshot snapshot, float top)
    {
        var social = snapshot.Social;
        if (social.ChatCount == 0)
            return;

        var display = _ui.DisplaySize;
        var width = Math.Min(Kit.Px(440), display.X - Kit.Px(48));
        var x = Kit.Px(Theme.Space5);
        // Under the flight readouts at the bottom centre when there is room beside them, above them when not.
        var readoutsLeft = display.X * 0.5f - Kit.Px(96 + 300);
        var bottom = x + width + Kit.Px(Theme.Space3) < readoutsLeft ? display.Y - Kit.Px(Theme.Space5) : ReadoutsTop - Kit.Px(Theme.Space3);

        ImGui.PushFont(_ui.Sans, Theme.FontSmall);
        var fontSize = ImGui.GetFontSize();
        var y = bottom;
        var shown = 0;
        for (var i = social.ChatCount - 1; i >= 0 && shown < 5; i--)
        {
            ref var line = ref social.Chat[i];
            var age = SocialView.SecondsSince(line.Tick, snapshot.ServerTick);
            if (age > ChatFeedSeconds)
                break;

            var length = ComposeChatLine(social, line, out var head);
            var bytes = _chatLine.AsSpan(0, length);
            var height = ImGui.CalcTextSize(bytes, false, width - Kit.Px(Theme.Space3)).Y;
            y -= height + Kit.Px(6);
            // Never over the sector panel or the party list above.
            if (y < top + Kit.Px(Theme.Space3))
                break;

            var alpha = Math.Clamp((ChatFeedSeconds - age) / 2f, 0f, 1f);
            var min = new float2(x, y);
            draw.AddRectFilled(min, min + new float2(width, height + Kit.Px(4)), Kit.Color(Theme.Void, 0.55f * alpha));
            var at = min + new float2(Kit.Px(6), Kit.Px(2));
            draw.AddText(_ui.Sans, fontSize, at, Kit.Color(Theme.Text, alpha), bytes, width - Kit.Px(Theme.Space3));
            draw.AddText(_ui.Sans, fontSize, at, Kit.Color(LineColor(snapshot, line), alpha), _chatLine.AsSpan(0, head), width - Kit.Px(Theme.Space3));
            shown++;
        }

        ImGui.PopFont();
    }

    /// <summary>
    /// Players, Social and Chat at the top right. They answer the pointer only while it is shown (the release key
    /// held, or a surface open); a captured pointer is not seen by the interface at all, so they are drawn dimmed.
    /// </summary>
    private void DrawQuickBar(SessionSnapshot snapshot)
    {
        var social = snapshot.Social;
        var unread = false;
        for (var i = social.ChatCount - 1; i >= 0; i--)
        {
            if (social.Chat[i].Tick <= _chatSeenTick)
                break;
            if (social.Chat[i].Player != snapshot.PlayerId)
            {
                unread = true;
                break;
            }
        }

        var gap = Kit.Px(Theme.Space2);
        var w0 = QuickButtonWidth("PLAYERS"u8, _playersKey);
        var w1 = QuickButtonWidth("SOCIAL"u8, _socialKey);
        var w2 = QuickButtonWidth("CHAT"u8, _chatKey);
        var size = new float2(w0 + w1 + w2 + gap * 2, Kit.Px(QuickBarHeight));
        var min = new float2(_ui.DisplaySize.X - Kit.Px(Theme.Space5) - size.X, Kit.Px(Theme.Space5));
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##quickbar"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar);
        // Above the panels it opens, so it can close them again.
        ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        var previousFade = Kit.Fade;
        Kit.Fade = _app.PointerCaptured ? 0.6f : 1f;
        ImGui.SetCursorScreenPos(min);
        if (QuickButton("##players"u8, "PLAYERS"u8, _playersKey, w0, _socialOpen && !_socialParty, social.Invitation.Player != 0))
            ToggleSocial(false);
        ImGui.SameLine(0, gap);
        if (QuickButton("##social"u8, "SOCIAL"u8, _socialKey, w1, _socialOpen && _socialParty, false))
            ToggleSocial(true);
        ImGui.SameLine(0, gap);
        if (QuickButton("##chat"u8, "CHAT"u8, _chatKey, w2, _chatOpen, unread && !_chatOpen))
        {
            if (_chatOpen)
                _chatOpen = false;
            else
                OpenChat();
        }

        Kit.Fade = previousFade;
        ImGui.End();
        _quickBarShown = true;
    }

    private float QuickButtonWidth(ReadOnlySpan<byte> label, ReadOnlySpan<byte> key)
    {
        var width = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label).X + Kit.Px(Theme.Space3) * 2;
        if (key.Length > 0)
            width += Kit.Measure(_ui.Mono, Theme.FontCaption, key).X + Kit.Px(Theme.Space2);
        return width;
    }

    private bool QuickButton(ImString id, ReadOnlySpan<byte> label, ReadOnlySpan<byte> key, float width, bool active, bool badge)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(width, Kit.Px(QuickBarHeight));
        var pressed = ImGui.InvisibleButton(id, size);
        var hot = Kit.Animate(ImGui.GetItemID(), ImGui.IsItemHovered() ? 1f : 0f);
        var lit = Math.Max(hot, active ? 1f : 0f);
        var max = position + size;
        var chamfer = Kit.Px(Theme.Chamfer * 0.7f);
        Kit.ChamferFill(draw, position, max, Kit.Color(Kit.Mix(Theme.Panel, Theme.PanelRaised, lit), 0.92f), chamfer);
        Kit.ChamferOutline(draw, position, max, Kit.Color(Kit.Mix(Theme.Line, Theme.Accent, lit)), chamfer);
        if (active)
            draw.AddRectFilled(new float2(position.X, max.Y - Kit.Px(2)), new float2(max.X - chamfer, max.Y), Kit.Color(Theme.Accent));

        var labelSize = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label);
        var text = position + new float2(Kit.Px(Theme.Space3), (size.Y - labelSize.Y) * 0.5f);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, Kit.Mix(Theme.TextDim, Theme.Text, lit), label);
        if (key.Length > 0)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(labelSize.X + Kit.Px(Theme.Space2), 0), Theme.TextMute, key);
        if (badge)
            draw.AddCircleFilled(new float2(max.X - Kit.Px(7), position.Y + Kit.Px(7)), Kit.Px(3.5f), Kit.Color(Theme.Accent), 12);
        return pressed;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The player list and party panel
    // ---------------------------------------------------------------------------------------------------------------

    private void DrawSocialPanel(SessionSnapshot snapshot)
    {
        var display = _ui.DisplaySize;
        var top = QuickBarBottom + Kit.Px(Theme.Space2);
        var size = new float2(Math.Min(Kit.Px(620), display.X - Kit.Px(24)), Math.Min(Kit.Px(560), display.Y - top - Kit.Px(12)));
        var min = new float2(MathF.Round((display.X - size.X) * 0.5f), Math.Max(top, MathF.Round((display.Y - size.Y) * 0.5f)));
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##social"u8, RootFlags | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        RaiseIfAppearing();
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var pad = Kit.Px(Theme.Space5);
        ImGui.SetCursorScreenPos(min + new float2(pad, pad));
        ImGui.BeginChild("##socialbody"u8, size - new float2(pad * 2, pad * 2), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);

        var social = snapshot.Social;
        var width = ImGui.GetContentRegionAvail().X;
        Kit.Caption("THIS SERVER"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var online = 0;
        for (var i = 0; i < social.RosterCount; i++)
        {
            if (social.Roster[i].Value != 0)
                online++;
        }

        var tabsAt = ImGui.GetCursorScreenPos();
        if (TabButton("##tabplayers"u8, Kit.F($"Players {online}"), !_socialParty))
            _socialParty = false;
        ImGui.SameLine(0, Kit.Px(Theme.Space5));
        if (TabButton("##tabparty"u8, social.PartyCount > 0 ? Kit.F($"Party {social.PartyCount}/{_app.Content.Social.PartySize}") : "Party"u8, _socialParty))
            _socialParty = true;
        var closeWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "CLOSE"u8).X + Kit.Px(Theme.Space2 * 2);
        var afterTabs = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new float2(tabsAt.X + width - closeWidth, tabsAt.Y + Kit.Px(4)));
        if (Kit.LinkButton("CLOSE"u8))
            _socialOpen = false;
        ImGui.SetCursorScreenPos(afterTabs);
        Kit.Space(Theme.Space3);

        var footer = Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y + Kit.Px(Theme.Space2);
        var rowsHeight = Math.Max(Kit.Px(60), ImGui.GetContentRegionAvail().Y - footer);
        ImGui.BeginChild("##socialrows"u8, new float2(width, rowsHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        var rowWidth = ImGui.GetContentRegionAvail().X - Kit.Px(Theme.Space2);
        if (_socialParty)
            DrawPartyRows(snapshot, rowWidth);
        else
            DrawPlayerRows(snapshot, rowWidth);
        ImGui.EndChild();

        Kit.Space(Theme.Space2);
        DrawNoticeLine(width);
        ImGui.EndChild();
        ImGui.End();
    }

    private bool TabButton(ImString id, ReadOnlySpan<byte> text, bool active)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var textSize = Kit.Measure(_ui.SansBold, Theme.FontHeading, text);
        var size = new float2(textSize.X, textSize.Y + Kit.Px(Theme.Space2));
        var pressed = ImGui.InvisibleButton(id, size);
        var hot = Kit.Animate(ImGui.GetItemID(), Kit.IsHot() ? 1f : 0f);
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, position, active ? Theme.Text : Kit.Mix(Theme.TextMute, Theme.TextDim, hot), text);
        if (active || hot > 0.01f)
            draw.AddRectFilled(new float2(position.X, position.Y + size.Y - Kit.Px(3)), new float2(position.X + textSize.X, position.Y + size.Y - Kit.Px(1)), Kit.Color(Theme.Accent, active ? 1f : hot));
        return pressed;
    }

    private void DrawPlayerRows(SessionSnapshot snapshot, float width)
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
                DrawPlayerRow(snapshot, entry, width);
            }
        }

        Kit.Space(Theme.Space3);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Players on this server only. Names last for this session; there are no friends or factions yet."u8, width / Kit.Scale);
    }

    private void DrawPlayerRow(SessionSnapshot snapshot, in SocialMessage entry, float width)
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
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, Kit.F($"Invite players from the Players list. A party has up to {_app.Content.Social.PartySize} members, shares a chat channel and shows its members at the top left."), width / Kit.Scale);
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
        if (Kit.Button("Leave Party"u8, ButtonKind.Secondary, 160) && Bridge is { } leave)
            Request(leave.LeaveParty(), "Leaving the party."u8, default);
        Kit.Space(Theme.Space2);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, leader ? "You lead this party: you invite and remove members."u8 : "The leader invites and removes members."u8, width / Kit.Scale);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Chat
    // ---------------------------------------------------------------------------------------------------------------

    private void DrawChat(SessionSnapshot snapshot)
    {
        var social = snapshot.Social;
        var display = _ui.DisplaySize;
        var size = new float2(Math.Min(Kit.Px(560), display.X - Kit.Px(48)), Math.Min(Kit.Px(360), display.Y - QuickBarBottom - Kit.Px(Theme.Space5 + Theme.Space3)));
        var min = new float2(Kit.Px(Theme.Space5), display.Y - size.Y - Kit.Px(Theme.Space5));
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##chat"u8, RootFlags | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        RaiseIfAppearing();
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var pad = Kit.Px(Theme.Space4);
        ImGui.SetCursorScreenPos(min + new float2(pad, pad));
        ImGui.BeginChild("##chatbody"u8, size - new float2(pad * 2, pad * 2), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);
        var width = ImGui.GetContentRegionAvail().X;

        // Header: the channel to send on, and close. Chat's buttons take the pointer only: keyboard navigation
        // never rests on them, so Enter, which opens chat and sends a line, cannot also press one.
        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        var headerAt = ImGui.GetCursorScreenPos();
        Kit.Caption("CHAT"u8, Theme.Accent);
        ImGui.SetCursorScreenPos(headerAt + new float2(Kit.Measure(_ui.SansMedium, Theme.FontCaption, "CHAT"u8).X + Kit.Px(Theme.Space5), -Kit.Px(Theme.Space1)));
        // Picking a channel puts the caret back in the field, so the key that sends is never left on a button.
        if (ChannelButton("##everyone"u8, "EVERYONE"u8, _chatChannel == ChatChannel.Everyone, true))
        {
            _chatChannel = ChatChannel.Everyone;
            _chatFocus = true;
        }

        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        if (ChannelButton("##party"u8, "PARTY"u8, _chatChannel == ChatChannel.Party, social.PartyCount > 0))
        {
            _chatChannel = ChatChannel.Party;
            _chatFocus = true;
        }
        var closeWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "CLOSE"u8).X + Kit.Px(Theme.Space2 * 2);
        ImGui.SetCursorScreenPos(new float2(headerAt.X + width - closeWidth, headerAt.Y - Kit.Px(Theme.Space1)));
        if (Kit.LinkButton("CLOSE"u8))
            _chatOpen = false;
        ImGui.PopItemFlag();
        ImGui.SetCursorScreenPos(headerAt + new float2(0, Kit.Px(26)));

        var lineHeight = Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        var inputHeight = Kit.Px(Theme.ControlHeight);
        var logHeight = Math.Max(Kit.Px(40), ImGui.GetContentRegionAvail().Y - inputHeight - lineHeight - Kit.Px(Theme.Space2 * 2));
        DrawChatLog(snapshot, width, logHeight);

        Kit.Space(Theme.Space2);
        DrawChatStatus(width);
        Kit.Space(Theme.Space1);
        DrawChatInput(snapshot, width);

        ImGui.EndChild();
        ImGui.End();
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

    private void DrawChatLog(SessionSnapshot snapshot, float width, float height)
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

        ImGui.BeginChild("##chatlog"u8, new float2(width, height), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
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

        // Enter on an empty line closes chat, back to flying; Enter on a line sends it and closes. The Send button
        // keeps chat open with the caret in the field.
        if (_chatInput.IsEmpty)
        {
            if (submitted)
                _chatOpen = false;
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
                _chatOpen = false;
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
