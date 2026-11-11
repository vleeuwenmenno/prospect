using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Lobby;

namespace Prospect.Client.Screens;

/// <summary>
/// The account and friends: the Friends screen from the main menu and the Friends tab of the in-game Social panel
/// draw the same parts. Everything is read from <see cref="AccountClient.View"/>, an immutable snapshot, and every
/// action goes to <see cref="App.Accounts"/>, which runs it off this thread. Names come from the account service
/// and only ever go to draw calls, never into labels, ids or format strings. Friends are accounts, not players on
/// this server: nothing here says whether a friend is online.
/// </summary>
internal sealed partial class ScreenHost
{
    /// <summary>Seconds an account notice stays readable.</summary>
    private const float AccountNoticeSeconds = 8f;

    /// <summary>Seconds a Remove stays armed for its confirming second press.</summary>
    private const float ConfirmSeconds = 4f;

    /// <summary>Friends lists older than this are fetched again when a friends view opens.</summary>
    private static readonly TimeSpan FriendsMaxAge = TimeSpan.FromSeconds(30);

    private const float AccountRowHeight = 46;
    private const float AccountButtonWidth = 104;

    /// <summary>A friend code is 16 hex characters, and the field's terminator.</summary>
    private readonly ImInputString _friendCode = new(17);
    private int _accountNoticeSeen = -1;
    private float _accountNoticeUntil;
    private ulong _confirmRemove;
    private float _confirmUntil;

    /// <summary>
    /// A fixed account for captures (<c>--account-sample</c>), read by the same parser as the service's answers:
    /// names that look like ImGui ids and format strings, a script the font lacks, a bidirectional override, the
    /// longest name, and in <c>friends</c> full lists (200 friends, 200 requests to you, 50 sent).
    /// </summary>
    internal static AccountClient AccountSample(string kind)
    {
        string[] names =
        [
            "##", "###", "%s %n %d", "{0} {1} {{x}}", "WWWWWWWWWWWWWWWWWWWWWWWW", "東京の宇宙港パイロット",
            "Ünïcödé Pïlöt", "\u202eRTL override", "Pilot-3F2A", "Ceres Hauler", "Vesta Night Shift", "Ωmega ☄ Miner",
        ];
        byte[] Lists(int friends, int incoming, int outgoing)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                var serial = 0;
                foreach (var (list, count) in new[] { ("friends", friends), ("incoming", incoming), ("outgoing", outgoing) })
                {
                    writer.WriteStartArray(list);
                    for (var i = 0; i < count; i++, serial++)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id"u8, (serial + 1).ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
                        writer.WriteString("name"u8, names[serial % names.Length]);
                        writer.WriteString("since"u8, new DateTimeOffset(2026, 1 + serial % 11, 1 + serial % 27, 12, 0, 0, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        var account = """{"id":"0123456789abcdef0123456789abcdef","name":"Kestrel ## %s","friendCode":"3F2A9C0D71B4E58A"}"""u8;
        return kind switch
        {
            "off" => AccountClient.Sample(AccountState.Off, default, default, AccountNotice.None, null),
            "waiting" => AccountClient.Sample(AccountState.Waiting, default, default, AccountNotice.None,
                "https://prospect.mvl.sh/v1/accounts/signin/SampleAttemptSampleAttemptSampleAttemptSamp"),
            "failed" => AccountClient.Sample(AccountState.SignedOut, default, default, AccountNotice.SigninFailed, null),
            "signed-in" => AccountClient.Sample(AccountState.SignedIn, account, Lists(3, 1, 1), AccountNotice.RequestSent, null),
            "friends" => AccountClient.Sample(AccountState.SignedIn, account, Lists(200, 200, 50), AccountNotice.None, null),
            _ => AccountClient.Sample(AccountState.SignedOut, default, default, AccountNotice.None, null),
        };
    }

    /// <summary>Called when a friends view opens: fetches the lists if they are old. Never signs in by itself.</summary>
    private void EnterAccount() => _app.Accounts.RefreshIfStale(FriendsMaxAge);

    private void DrawAccountScreen(float fade)
    {
        BeginRoot("##account"u8);
        var display = _ui.DisplaySize;
        var left = Math.Min(Kit.Px(96), display.X * 0.06f);
        var min = new float2(left, display.Y * 0.08f + Slide(fade));
        var view = _app.Accounts.View;
        // Signed out there is one short card to show; signed in, the lists take the room.
        var signedIn = view.State == AccountState.SignedIn;
        var size = new float2(
            Math.Min(Kit.Px(signedIn ? 1080 : 640), display.X - left * 2),
            Math.Max(Kit.Px(160), Math.Min(Kit.Px(signedIn ? 760 : 460), display.Y * 0.92f - Kit.Px(72)))
        );
        BeginPanel("##accountpanel"u8, min, size);

        if (Compact)
        {
            Kit.Caption("ACCOUNT  /  FRIENDS"u8, Theme.Accent);
            Kit.Space(Theme.Space3);
        }
        else
        {
            Kit.Caption("ACCOUNT"u8, Theme.Accent);
            Kit.Space(Theme.Space1);
            Kit.Heading("Friends"u8);
            Kit.Space(Theme.Space4);
        }

        var lineHeight = Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        var footer = Kit.Px(Theme.ControlHeight + Theme.Space3 * 2) + lineHeight;
        var height = Math.Max(Kit.Px(80), ImGui.GetContentRegionAvail().Y - footer);
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Kit.Px(Theme.Space6);
        var side = Kit.Px(380);
        if (width >= side + gap + Kit.Px(360) && view.State == AccountState.SignedIn)
        {
            // Wide: the account and the code field on the left, the lists on the right, each scrolling on its own.
            ImGui.BeginChild("##accountside"u8, new float2(side, height), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
            DrawAccountCard(view, side - Kit.Px(Theme.Space3));
            DrawAddFriend(view, side - Kit.Px(Theme.Space3));
            ImGui.EndChild();
            ImGui.SameLine(0, gap);
            var listWidth = width - side - gap;
            ImGui.BeginChild("##accountlists"u8, new float2(listWidth, height), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
            DrawFriendLists(view, listWidth - Kit.Px(Theme.Space3), false);
            ImGui.EndChild();
        }
        else
        {
            ImGui.BeginChild("##accountbody"u8, new float2(width, height), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
            DrawAccountBody(Math.Min(width, Kit.Px(720)) - Kit.Px(Theme.Space3), true);
            ImGui.EndChild();
        }

        Kit.Space(Theme.Space2);
        DrawAccountNotice(width);
        Kit.Space(Theme.Space2);
        if (Kit.Button("Back"u8, ButtonKind.Secondary, 120) || BackPressed())
            Show(Screen.MainMenu);

        EndPanel();
        DrawHints(back: true);
        EndRoot();
    }

    /// <summary>
    /// The sign-in that joining a server or hosting for others needs, drawn on the screen that needs it so what
    /// the player chose there stays put while they sign in: the reason and the button, the wait with Cancel and
    /// Copy link, or why there are no accounts. Signing in only changes the account; the player then presses Join
    /// or Start again. Draws nothing when signed in.
    /// </summary>
    /// <param name="hosting">True on the New Game screen, false on Join Server.</param>
    /// <param name="width">The width to lay out in.</param>
    private void DrawSignInGate(bool hosting, float width)
    {
        var view = _app.Accounts.View;
        var wrap = width / Kit.Scale;
        switch (view.State)
        {
            case AccountState.SignedIn:
                return;
            case AccountState.Off:
                Kit.Label(_ui.SansMedium, Theme.FontBody, Theme.Text, "Accounts are off"u8);
                Kit.Space(Theme.Space2);
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, hosting
                    ? "Hosting for others needs a Discord sign-in, and this game was started without the server list and accounts (--listing off). Private games work as usual."u8
                    : "Joining a server needs a Discord sign-in, and this game was started without the server list and accounts (--listing off). Private games work as usual."u8, wrap);
                return;
            case AccountState.SignedOut:
                Kit.Label(_ui.SansMedium, Theme.FontBody, Theme.Text, "Sign in to play with others"u8);
                Kit.Space(Theme.Space2);
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, hosting
                    ? "Hosting a game others can join needs a Discord sign-in; a private game does not. Your browser opens, and PROSPECT asks Discord only for your display name."u8
                    : "Joining a server needs a Discord sign-in; private games do not. Your browser opens, and PROSPECT asks Discord only for your display name."u8, wrap);
                Kit.Space(Theme.Space3);
                if (Kit.Button("Sign in with Discord"u8, ButtonKind.Primary, 240, !view.Busy))
                    _app.Accounts.SignIn();
                break;
            default:
                DrawAccountCard(view, width);
                break;
        }

        Kit.Space(Theme.Space2);
        DrawAccountNotice(width);
    }

    /// <summary>The account, the code field and the lists in one column, in the current scrolling window.</summary>
    private void DrawAccountBody(float width, bool menu)
    {
        var view = _app.Accounts.View;
        DrawAccountCard(view, width);
        if (view.State != AccountState.SignedIn)
        {
            if (!menu)
            {
                Kit.Space(Theme.Space3);
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Signing in is optional. This game, its chat and its party work the same without it."u8, width / Kit.Scale);
            }

            return;
        }

        DrawAddFriend(view, width);
        DrawFriendLists(view, width, true);
    }

    private void DrawAccountCard(AccountView view, float width)
    {
        var accounts = _app.Accounts;
        var draw = ImGui.GetWindowDrawList();
        var wrap = width / Kit.Scale;
        switch (view.State)
        {
            case AccountState.Off:
                Kit.Label(_ui.SansMedium, Theme.FontBody, Theme.Text, "Accounts are off"u8);
                Kit.Space(Theme.Space2);
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "Accounts and friends live beside the server list, which this game was started without (--listing off). Private games work as usual; joining and hosting for others need accounts."u8, wrap);
                break;

            case AccountState.SignedOut:
                Kit.Label(_ui.SansMedium, Theme.FontBody, Theme.Text, "Not signed in"u8);
                Kit.Space(Theme.Space2);
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "Sign in with Discord to keep a friends list across games and restarts, and to join or host games with others. PROSPECT asks Discord only for your display name; your browser opens for it."u8, wrap);
                Kit.Space(Theme.Space3);
                if (Kit.Button("Sign in with Discord"u8, ButtonKind.Primary, 240, !view.Busy))
                    accounts.SignIn();
                break;

            case AccountState.Starting or AccountState.Waiting or AccountState.Finishing:
                {
                    var at = ImGui.GetCursorScreenPos();
                    var lineHeight = Kit.Measure(_ui.SansMedium, Theme.FontBody, "Ag"u8).Y;
                    Kit.Spinner(draw, at + new float2(Kit.Px(8), lineHeight * 0.5f), Kit.Px(7));
                    ReadOnlySpan<byte> title = view.State switch
                    {
                        AccountState.Starting => "Preparing the sign-in..."u8,
                        AccountState.Waiting => "Finish signing in in your browser"u8,
                        _ => "Signing in..."u8,
                    };
                    Kit.Text(draw, _ui.SansMedium, Theme.FontBody, at + new float2(Kit.Px(24), 0), Theme.Text, title);
                    ImGui.Dummy(new float2(width, lineHeight));
                    Kit.Space(Theme.Space2);
                    if (view.State == AccountState.Waiting)
                    {
                        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "Discord asks you to allow PROSPECT, then sends you back to the game. The game waits up to ten minutes; Cancel stops waiting."u8, wrap);
                        Kit.Space(Theme.Space3);
                    }

                    if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140))
                        accounts.CancelSignIn();
                    if (view.State == AccountState.Waiting && view.BrowserUrl is { } url)
                    {
                        ImGui.SameLine(0, Kit.Px(Theme.Space3));
                        // For a browser that did not open: the page is the service's own sign-in page, no secret.
                        if (Kit.Button("Copy link"u8, ButtonKind.Secondary, 140))
                            ImGui.SetClipboardText(url);
                    }

                    break;
                }

            case AccountState.SignedIn:
                {
                    Kit.Caption("SIGNED IN WITH DISCORD"u8);
                    Kit.Space(Theme.Space1);
                    var at = ImGui.GetCursorScreenPos();
                    var nameHeight = Kit.Measure(_ui.SansBold, Theme.FontHeading, "Ag"u8).Y;
                    Kit.ClippedText(draw, _ui.SansBold, Theme.FontHeading, at, Theme.Text, view.NameUtf8, width);
                    ImGui.Dummy(new float2(width, nameHeight));
                    Kit.Space(Theme.Space3);

                    Kit.Caption("YOUR FRIEND CODE"u8);
                    Kit.Space(Theme.Space1);
                    at = ImGui.GetCursorScreenPos();
                    var code = Kit.Text(draw, _ui.Mono, Theme.FontHeading, at, Theme.Accent, Kit.F($"{view.FriendCode}"));
                    ImGui.SetCursorScreenPos(new float2(at.X + code.X + Kit.Px(Theme.Space3), at.Y + (code.Y - Kit.Measure(_ui.SansMedium, Theme.FontCaption, "COPY"u8).Y) * 0.5f - Kit.Px(Theme.Space1)));
                    if (Kit.LinkButton("COPY"u8))
                        ImGui.SetClipboardText(view.FriendCode);
                    ImGui.SetCursorScreenPos(new float2(at.X, at.Y + code.Y));
                    ImGui.Dummy(new float2(width, 0));
                    Kit.Space(Theme.Space1);
                    Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "Give this code to a friend so they can add you."u8, wrap);
                    Kit.Space(Theme.Space3);
                    if (Kit.Button("Refresh"u8, ButtonKind.Secondary, 120, !view.Busy))
                        accounts.Refresh();
                    ImGui.SameLine(0, Kit.Px(Theme.Space3));
                    if (Kit.Button("Sign out"u8, ButtonKind.Secondary, 120))
                    {
                        accounts.SignOut();
                        _friendCode.Clear();
                    }

                    break;
                }
        }
    }

    private void DrawAddFriend(AccountView view, float width)
    {
        Kit.Space(Theme.Space5);
        var fieldWidth = Math.Min(220f, (width - Kit.Px(Theme.Space2) - Kit.Px(140)) / Kit.Scale);
        var submitted = Kit.TextField("ADD A FRIEND BY CODE"u8, "##friendcode"u8, _friendCode, fieldWidth, ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.CharsNoBlank);
        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        // The pointer only: Enter belongs to the field, so it can never also press this button.
        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        var clicked = Kit.Button("Send request"u8, ButtonKind.Primary, 140, !view.Busy && !_friendCode.IsEmpty);
        ImGui.PopItemFlag();
        if ((submitted || clicked) && !view.Busy && !_friendCode.IsEmpty)
        {
            // Text made only on this press, never per frame.
            if (_app.Accounts.AddFriend(Encoding.UTF8.GetString(_friendCode.ValueSpan)))
                _friendCode.Clear();
        }
    }

    /// <param name="view">The account.</param>
    /// <param name="width">The width to lay out in.</param>
    /// <param name="below">True when the lists follow other parts; false when they start a column of their own.</param>
    private void DrawFriendLists(AccountView view, float width, bool below)
    {
        var gap = below ? Theme.Space5 : 0f;
        if (!view.Listed)
        {
            Kit.Space(gap);
            Kit.Body(view.Busy ? "Fetching your friends..."u8 : "Your friends could not be fetched. Try Refresh."u8);
            return;
        }

        if (view.Incoming.Length > 0)
        {
            Kit.Space(gap);
            gap = Theme.Space5;
            Kit.Caption(Kit.F($"REQUESTS TO YOU   {view.Incoming.Length}"), Theme.Accent);
            Kit.Space(Theme.Space1);
            ImGui.PushID("in"u8);
            foreach (var friend in view.Incoming)
                FriendRow(view, friend, width, FriendList.Incoming);
            ImGui.PopID();
        }

        Kit.Space(gap);
        Kit.Caption(Kit.F($"FRIENDS   {view.Friends.Length}"));
        Kit.Space(Theme.Space1);
        if (view.Friends.Length == 0)
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "No friends yet. Share your code, or add theirs."u8, width / Kit.Scale);
        ImGui.PushID("friends"u8);
        foreach (var friend in view.Friends)
            FriendRow(view, friend, width, FriendList.Friends);
        ImGui.PopID();

        if (view.Outgoing.Length > 0)
        {
            Kit.Space(Theme.Space5);
            Kit.Caption(Kit.F($"SENT   {view.Outgoing.Length}"));
            Kit.Space(Theme.Space1);
            ImGui.PushID("out"u8);
            foreach (var friend in view.Outgoing)
                FriendRow(view, friend, width, FriendList.Outgoing);
            ImGui.PopID();
        }

        Kit.Space(Theme.Space4);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Friends belong to your account and last across games. Whether a friend is online, or on this server, is not shown yet."u8, width / Kit.Scale);
    }

    private enum FriendList : byte
    {
        Incoming,
        Friends,
        Outgoing,
    }

    /// <summary>
    /// One account in a list: name, date and its actions, scoped to the account's own id, so a press that began
    /// on one row cannot complete on another that takes its place. Rows out of view are skipped.
    /// </summary>
    private void FriendRow(AccountView view, AccountFriend friend, float width, FriendList list)
    {
        var size = new float2(width, Kit.Px(AccountRowHeight));
        if (!ImGui.IsRectVisible(size))
        {
            ImGui.Dummy(size);
            return;
        }

        var accounts = _app.Accounts;
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var buttons = list == FriendList.Incoming ? 2 : 1;
        var buttonsWidth = buttons * Kit.Px(AccountButtonWidth) + (buttons - 1) * Kit.Px(Theme.Space2);
        var textWidth = width - buttonsWidth - Kit.Px(Theme.Space3 * 2);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, position + new float2(Kit.Px(Theme.Space3), Kit.Px(3)), Theme.Text, friend.NameUtf8, textWidth);
        ReadOnlySpan<byte> since = list switch
        {
            FriendList.Incoming => Kit.F($"ASKED {friend.SinceUtf8}"),
            FriendList.Outgoing => Kit.F($"SENT {friend.SinceUtf8}"),
            _ => Kit.F($"FRIENDS SINCE {friend.SinceUtf8}"),
        };
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, position + new float2(Kit.Px(Theme.Space3), Kit.Px(25)), Theme.TextMute, since, textWidth);

        Kit.PushId(friend.Key);
        ImGui.SetCursorScreenPos(new float2(position.X + width - buttonsWidth, position.Y + (size.Y - Kit.Px(Theme.ControlHeight)) * 0.5f));
        var enabled = !view.Busy;
        switch (list)
        {
            case FriendList.Incoming:
                if (Kit.Button("Accept"u8, ButtonKind.Primary, AccountButtonWidth, enabled))
                    accounts.Accept(friend.Id);
                ImGui.SameLine(0, Kit.Px(Theme.Space2));
                if (Kit.Button("Decline"u8, ButtonKind.Secondary, AccountButtonWidth, enabled))
                    accounts.Decline(friend.Id);
                break;
            case FriendList.Outgoing:
                if (Kit.Button("Cancel"u8, ButtonKind.Secondary, AccountButtonWidth, enabled))
                    accounts.CancelRequest(friend.Id);
                break;
            default:
                // Removing cannot be undone without the friend's code, so it takes a second press.
                var armed = _confirmRemove == friend.Key && _ui.Time < _confirmUntil;
                if (Kit.Button(armed ? "Confirm"u8 : "Remove"u8, armed ? ButtonKind.Primary : ButtonKind.Secondary, AccountButtonWidth, enabled))
                {
                    if (armed)
                    {
                        _confirmRemove = 0;
                        accounts.Remove(friend.Id);
                    }
                    else
                    {
                        _confirmRemove = friend.Key;
                        _confirmUntil = _ui.Time + ConfirmSeconds;
                    }
                }

                break;
        }

        Kit.PopId();
        draw.AddLine(position + new float2(0, size.Y - 0.5f), position + new float2(width, size.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));
        ImGui.SetCursorScreenPos(position);
        ImGui.Dummy(size);
    }

    /// <summary>The latest account notice as a line, for a few seconds after it changes; an empty line otherwise.</summary>
    private void DrawAccountNotice(float width)
    {
        var view = _app.Accounts.View;
        if (view.NoticeCount != _accountNoticeSeen)
        {
            _accountNoticeSeen = view.NoticeCount;
            _accountNoticeUntil = _ui.Time + AccountNoticeSeconds;
        }

        var at = ImGui.GetCursorScreenPos();
        var height = Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        if (view.Notice != AccountNotice.None && (_ui.Time < _accountNoticeUntil || view.State == AccountState.SignedOut))
        {
            var text = Describe(view.Notice, out var bad);
            Kit.ClippedText(ImGui.GetWindowDrawList(), _ui.Sans, Theme.FontSmall, at, bad ? Theme.Bad : Theme.Good, text, width);
        }

        ImGui.Dummy(new float2(width, height));
    }

    /// <summary>What a notice says to the player. Fixed text: nothing the service sends is shown.</summary>
    private static ReadOnlySpan<byte> Describe(AccountNotice notice, out bool bad)
    {
        bad = notice is not (AccountNotice.SignedIn or AccountNotice.SignedOut or AccountNotice.Refreshed or AccountNotice.RequestSent
            or AccountNotice.RequestSettled or AccountNotice.Accepted or AccountNotice.Declined or AccountNotice.RequestCancelled or AccountNotice.Removed);
        return notice switch
        {
            AccountNotice.SignedIn => "Signed in."u8,
            AccountNotice.SignedOut => "Signed out. A game you are in keeps running."u8,
            AccountNotice.SigninCancelled => "Sign-in cancelled."u8,
            AccountNotice.SigninDenied => "The sign-in was cancelled in the browser."u8,
            AccountNotice.SigninFailed => "The sign-in did not work. Try again."u8,
            AccountNotice.SigninTimedOut => "Nothing came back from the browser in time. Sign in again when ready."u8,
            AccountNotice.BrowserNotOpened => "The browser did not open. Copy the link and open it yourself."u8,
            AccountNotice.PortUnavailable => "The game could not open a local port for the sign-in."u8,
            AccountNotice.Expired => "Your sign-in expired. Sign in again."u8,
            AccountNotice.Unavailable => "Accounts are unavailable right now. Private games still work."u8,
            AccountNotice.RateLimited => "Too many requests. Wait a moment and try again."u8,
            AccountNotice.Unreachable => "No answer from the account service. Check your connection and try again."u8,
            AccountNotice.BadReply => "The account service sent something unexpected. Try again later."u8,
            AccountNotice.Refreshed => "Friends list updated."u8,
            AccountNotice.InvalidCode => "A friend code is 16 characters: 0 to 9 and A to F."u8,
            AccountNotice.RequestSent => "Friend request sent."u8,
            AccountNotice.RequestSettled => "Already asked, or they had asked you and are now your friend."u8,
            AccountNotice.UnknownCode => "No pilot has that friend code."u8,
            AccountNotice.CannotAdd => "That is your own code, or you are friends already."u8,
            AccountNotice.LimitReached => "A friend or request limit is reached, or too many requests. Try later."u8,
            AccountNotice.Accepted => "Friend request accepted."u8,
            AccountNotice.Declined => "Friend request declined."u8,
            AccountNotice.RequestCancelled => "Request withdrawn."u8,
            AccountNotice.Removed => "Friend removed."u8,
            AccountNotice.Gone => "That request or friend is no longer there. The list is updated."u8,
            _ => default,
        };
    }
}
