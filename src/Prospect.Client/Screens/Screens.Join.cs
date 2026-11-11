using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

/// <summary>
/// What the server browser shows instead of the listing server's answer, for scripted captures without the
/// network (<c>--browser-sample</c>).
/// </summary>
internal enum BrowserSample
{
    None,
    Ready,
    Loading,
    Failed,
    Empty,
    Off,

    /// <summary>The sample list with the password prompt open over it.</summary>
    Password,
}

internal sealed partial class ScreenHost
{
    private const float JoinFormWidth = 340;
    private const float BrowserRowHeight = 34;
    private const float LockColumn = 28;
    private const float PlayersColumn = 84;
    private const float VersionColumn = 96;

    /// <summary>A password is at most 64 UTF-16 units, so at most 192 bytes of UTF-8, and the field's terminator.</summary>
    private const int PasswordBytes = Passwords.MaxChars * 3 + 1;

    private readonly ImInputString _joinName = new(ProtocolInfo.MaxNameBytes);
    private readonly ImInputString _joinHost = new(128);
    private readonly ImInputString _joinPort = new(8);
    private readonly ImInputString _joinPassword = new(PasswordBytes);
    private readonly ImInputString _listedPassword = new(PasswordBytes);
    private readonly GameOrder _gameOrder = new();
    private int[] _gameOrderIndex = new int[ServerBrowser.MaxGames];
    private int _gameOrderCount;
    private ServerList? _gameOrderList;
    private string _joinError = "";
    private bool _joinPasswordShown;
    private bool _listedPasswordShown;
    private bool _listedPasswordFocus;
    private string _listedError = "";

    /// <summary>The width of the version column this frame; zero when the list is too narrow to show it.</summary>
    private float _versionColumn;

    /// <summary>The listed game the player picked, by its endpoint, so a refresh that reorders the list keeps it.</summary>
    private string? _selectedAddress;
    private ushort _selectedPort;

    /// <summary>The listed game the password prompt is for, while it is open.</summary>
    private ListedGame? _passwordFor;

    private BrowserSample _browserSample;
    private ServerList? _sampleList;

    private void InitializeJoin()
    {
        _joinName.SetValue(_app.Settings.PlayerName);
        _joinHost.SetValue(_app.Settings.LastHost);
        _joinPort.SetValue(_app.Settings.LastPort.ToString());
    }

    /// <summary>Feeds the browser a fixed list instead of the listing server's, for captures without the network.</summary>
    public void UseBrowserSample(BrowserSample sample)
    {
        _browserSample = sample;
        _sampleList = sample is BrowserSample.Ready or BrowserSample.Password ? ServerBrowser.Parse(SampleListJson(), allowPrivateAddresses: false) : null;
    }

    /// <summary>
    /// Opens the Join screen with an address, port and password already filled in and a reason shown, as for a
    /// <c>--join</c> that could not go ahead (no sign-in yet). The password stays in the field's memory only.
    /// </summary>
    public void OpenJoin(string host, ushort port, string? password, string error)
    {
        _joinHost.SetValue(host);
        _joinPort.SetValue(port.ToString());
        if (password is not null)
            _joinPassword.SetValue(password);
        Show(Screen.Join);
        _joinError = error;
    }

    /// <summary>Called when the Join screen opens: asks for a fresh list, and opens the sample prompt if asked.</summary>
    private void EnterJoin()
    {
        _joinError = "";
        _passwordFor = null;
        if (_browserSample == BrowserSample.None)
        {
            _app.Browser.Refresh();
            return;
        }

        if (_browserSample == BrowserSample.Password && _sampleList is { Games.Length: > 0 })
        {
            foreach (var game in _sampleList.Games)
            {
                if (game.Password)
                {
                    Select(game);
                    OpenPasswordPrompt(game);
                    // A placeholder, shown masked: the capture shows what a typed password looks like.
                    _listedPassword.SetValue("placeholder"u8);
                    break;
                }
            }
        }
    }

    /// <summary>What the browser has to show now: the listing server's answer, or the sample.</summary>
    private void ReadBrowser(out BrowserState state, out ServerList? list, out string error)
    {
        switch (_browserSample)
        {
            case BrowserSample.None:
                state = _app.Browser.State;
                list = _app.Browser.List;
                error = state == BrowserState.Failed ? list.Error : "";
                return;
            case BrowserSample.Loading:
                state = BrowserState.Loading;
                break;
            case BrowserSample.Failed:
                state = BrowserState.Failed;
                break;
            case BrowserSample.Off:
                state = BrowserState.Off;
                break;
            default:
                state = BrowserState.Ready;
                break;
        }

        list = _sampleList;
        error = state == BrowserState.Failed ? "The server list cannot be reached." : "";
    }

    private string BrowserHost => _browserSample == BrowserSample.None ? _app.Browser.Host : "sample list";

    private void DrawJoin(float fade)
    {
        ReadBrowser(out var state, out var list, out var error);
        var browsing = state != BrowserState.Off;
        var modal = _passwordFor is not null;

        BeginRoot("##join"u8);
        if (modal)
            ImGui.BeginDisabled();

        var display = _ui.DisplaySize;
        var left = Math.Min(Kit.Px(96), display.X * 0.06f);
        var width = browsing ? Math.Min(Kit.Px(1180), display.X - left * 2) : Kit.Px(JoinFormWidth + Theme.Space6 * 2 + 14);
        var min = new float2(left, display.Y * 0.08f + Slide(fade));
        var size = new float2(width, Math.Max(Kit.Px(160), Math.Min(Kit.Px(760), display.Y * 0.92f - Kit.Px(72))));
        BeginPanel("##joinpanel"u8, min, size);

        // A short window keeps one line of title, so the list keeps the room.
        if (Compact)
        {
            Kit.Caption("MULTIPLAYER  /  JOIN SERVER"u8, Theme.Accent);
            Kit.Space(Theme.Space3);
        }
        else
        {
            Kit.Caption("MULTIPLAYER"u8, Theme.Accent);
            Kit.Space(Theme.Space1);
            Kit.Heading("Join Server"u8);
            Kit.Space(Theme.Space4);
        }

        var footer = Kit.Px(Theme.ControlHeight + Theme.Space3);
        var columnHeight = Math.Max(Kit.Px(60), ImGui.GetContentRegionAvail().Y - footer);
        var gap = Kit.Px(Theme.Space6);
        var formWidth = Kit.Px(JoinFormWidth + 14);
        var listWidth = ImGui.GetContentRegionAvail().X - formWidth - gap;

        var join = false;
        // Joining needs a sign-in: the buttons wait for it, and a press that gets through anyway (Enter, a row)
        // is refused by the app with the reason.
        var signedIn = _app.Accounts.View.State == AccountState.SignedIn;
        if (browsing)
        {
            ImGui.BeginChild("##browser"u8, new float2(listWidth, columnHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            join = DrawBrowser(state, list, error, listWidth);
            ImGui.EndChild();
            ImGui.SameLine(0, gap);
        }

        ImGui.BeginChild("##direct"u8, new float2(browsing ? formWidth : 0, columnHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        DrawDirectConnect(browsing, signedIn);
        ImGui.EndChild();

        Kit.Space(Theme.Space3);
        var selected = Selected(list);
        if (browsing)
        {
            if (Kit.Button("Join"u8, ButtonKind.Primary, 160, selected is not null && signedIn) && selected is not null)
                join = true;
            ImGui.SameLine(0, Kit.Px(Theme.Space3));
            if (Kit.Button("Refresh"u8, ButtonKind.Secondary, 140, state != BrowserState.Loading) && _browserSample == BrowserSample.None)
                _app.Browser.Refresh();
            ImGui.SameLine(0, Kit.Px(Theme.Space3));
        }

        if ((Kit.Button("Back"u8, ButtonKind.Secondary, 120) || BackPressed()) && !modal)
            Show(Screen.MainMenu);

        EndPanel();
        if (modal)
            ImGui.EndDisabled();
        DrawHints(back: true);
        EndRoot();

        if (join && selected is not null && !modal)
            JoinListed(selected);

        if (_passwordFor is not null)
            DrawListedPassword();
    }

    /// <summary>
    /// The list of public games: a status line, column heads and the rows, which scroll. Returns true when the
    /// player asked to join the selected game from the list itself (Enter on a row, or a double-click).
    /// </summary>
    private bool DrawBrowser(BrowserState state, ServerList? list, string error, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var games = list?.Games ?? [];
        SortGames(list);

        // One line: the caption, then what the list is doing, or why it failed, and how many entries were left out.
        var lineHeight = Kit.Px(20);
        var top = ImGui.GetCursorScreenPos();
        var captionSize = Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, top + new float2(0, Kit.Px(4)), Theme.TextMute, "PUBLIC GAMES"u8);
        var line = top + new float2(captionSize.X + Kit.Px(Theme.Space4), 0);
        width -= captionSize.X + Kit.Px(Theme.Space4);
        switch (state)
        {
            case BrowserState.Loading or BrowserState.Idle:
                Kit.Spinner(draw, line + new float2(Kit.Px(7), lineHeight * 0.5f), Kit.Px(6));
                Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, line + new float2(Kit.Px(22), Kit.Px(2)), Theme.TextDim, Kit.F($"Asking {BrowserHost} for games..."), width - Kit.Px(22));
                break;
            case BrowserState.Failed:
                if (games.Length > 0)
                    Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, line + new float2(0, Kit.Px(2)), Theme.Bad, Kit.F($"{error} Showing the last list."), width);
                else
                    Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, line + new float2(0, Kit.Px(2)), Theme.Bad, error, width);
                break;
            default:
                var listed = list?.Dropped > 0
                    ? Kit.F($"{games.Length} {(games.Length == 1 ? "game" : "games")} on {BrowserHost}.  {list!.Dropped} left out as invalid.")
                    : Kit.F($"{games.Length} {(games.Length == 1 ? "game" : "games")} on {BrowserHost}.");
                Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, line + new float2(0, Kit.Px(2)), Theme.TextDim, listed, width);
                break;
        }

        width += captionSize.X + Kit.Px(Theme.Space4);
        ImGui.Dummy(new float2(width, lineHeight));
        Kit.Space(Theme.Space2);

        // Column heads. A narrow list drops the version, which is the same for every game it lists.
        _versionColumn = width < Kit.Px(440) ? 0 : Kit.Px(VersionColumn);
        var nameWidth = width - Kit.Px(LockColumn + PlayersColumn + Theme.Space3 * 2) - _versionColumn;
        var head = ImGui.GetCursorScreenPos();
        var captionHeight = Kit.Px(18);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, head + new float2(Kit.Px(LockColumn), 0), Theme.TextMute, "NAME"u8);
        var playersHead = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "PLAYERS"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, head + new float2(Kit.Px(LockColumn) + nameWidth + Kit.Px(PlayersColumn) - playersHead.X, 0), Theme.TextMute, "PLAYERS"u8);
        if (_versionColumn > 0)
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, head + new float2(width - _versionColumn + Kit.Px(Theme.Space3), 0), Theme.TextMute, "VERSION"u8);
        draw.AddLine(head + new float2(0, captionHeight - 0.5f), head + new float2(width - Kit.Px(Theme.Space3), captionHeight - 0.5f), Kit.Color(Theme.LineStrong));
        ImGui.Dummy(new float2(width, captionHeight));

        var rowsHeight = Math.Max(Kit.Px(BrowserRowHeight), ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild("##games"u8, new float2(width, rowsHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        var join = false;
        if (_gameOrderCount == 0)
        {
            Kit.Space(Theme.Space3);
            var empty = state switch
            {
                BrowserState.Loading or BrowserState.Idle => "The list is on its way."u8,
                BrowserState.Failed => "No list to show. Try Refresh, or join by address."u8,
                _ => "No public games right now. Host one from New Game, or join by address."u8,
            };
            Kit.Paragraph(_ui.Sans, Theme.FontBody, Theme.TextDim, empty, width / Kit.Scale - Theme.Space3);
        }

        for (var row = 0; row < _gameOrderCount; row++)
        {
            var index = _gameOrderIndex[row];
            join |= DrawGameRow(games[index], index, width, nameWidth);
        }

        ImGui.EndChild();
        return join;
    }

    /// <summary>
    /// One listed game. Its id is its index in the list; its name is untrusted and only ever drawn as text,
    /// clipped to its column. Returns true when the player asked to join it from the row.
    /// </summary>
    private bool DrawGameRow(ListedGame game, int index, float width, float nameWidth)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(width - Kit.Px(Theme.Space3), Kit.Px(BrowserRowHeight));
        var selected = IsSelected(game);

        ImGui.PushID(index);
        var pressed = ImGui.InvisibleButton("##row"u8, size);
        var hot = Kit.Animate(ImGui.GetItemID(), Kit.IsHot() || selected ? 1f : 0f);
        var doubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        var keyboard = pressed && !ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        if (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible && !selected)
            Select(game);
        ImGui.PopID();

        if (pressed)
            Select(game);

        draw.AddRectFilled(position, position + size, Kit.Color(Theme.PanelRaised, (selected ? 0.9f : 0.55f) * hot));
        if (selected)
            draw.AddRectFilled(position, position + new float2(Kit.Px(2), size.Y), Kit.Color(Theme.Accent));
        draw.AddLine(position + new float2(0, size.Y - 0.5f), position + new float2(size.X, size.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));

        var full = game.MaxPlayers > 0 && game.Players >= game.MaxPlayers;
        var textY = (size.Y - Kit.Measure(_ui.Sans, Theme.FontBody, "Ag"u8).Y) * 0.5f;
        if (game.Password)
            Kit.Lock(draw, position + new float2(Kit.Px(LockColumn) * 0.5f, size.Y * 0.5f), selected ? Theme.Accent : Theme.TextDim);
        Kit.ClippedText(draw, _ui.Sans, Theme.FontBody, position + new float2(Kit.Px(LockColumn), textY), full ? Theme.TextMute : Theme.Text, game.Name, nameWidth - Kit.Px(Theme.Space2));

        var players = Kit.F($"{game.Players}/{game.MaxPlayers}");
        var playersSize = Kit.Measure(_ui.Mono, Theme.FontSmall, players);
        var monoY = (size.Y - playersSize.Y) * 0.5f;
        Kit.Text(draw, _ui.Mono, Theme.FontSmall, position + new float2(Kit.Px(LockColumn) + nameWidth + Kit.Px(PlayersColumn) - playersSize.X, monoY), full ? Theme.Bad : Theme.TextDim, players);
        if (_versionColumn > 0)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontSmall, position + new float2(width - _versionColumn + Kit.Px(Theme.Space3), monoY), Theme.TextMute, game.Version, _versionColumn - Kit.Px(Theme.Space3 * 2));

        return doubleClicked || keyboard;
    }

    /// <summary>Joining by address: callsign, address, port and an optional password.</summary>
    private void DrawDirectConnect(bool browsing, bool signedIn)
    {
        var field = JoinFormWidth - 64;
        if (!signedIn)
        {
            DrawSignInGate(false, Kit.Px(JoinFormWidth - 20));
            Kit.Space(Theme.Space5);
        }

        Kit.Caption(browsing ? "DIRECT CONNECT"u8 : "JOIN BY ADDRESS"u8);
        Kit.Space(Theme.Space2);
        if (!browsing)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "The server list is turned off. Join a game by its address."u8, JoinFormWidth - 20);
            Kit.Space(Theme.Space3);
        }

        var submit = Kit.TextField("CALLSIGN"u8, "##name"u8, _joinName, field);
        Kit.Space(Theme.Space3);
        submit |= Kit.TextField("ADDRESS"u8, "##host"u8, _joinHost, field);
        Kit.Space(Theme.Space3);
        submit |= Kit.TextField("PORT"u8, "##port"u8, _joinPort, 140, ImGuiInputTextFlags.CharsDecimal);
        Kit.Space(Theme.Space3);
        submit |= Kit.PasswordField("PASSWORD (IF IT HAS ONE)"u8, "##joinpassword"u8, _joinPassword, ref _joinPasswordShown, field);
        Kit.Space(Theme.Space3);

        // The error line is reserved so the button does not move when it appears.
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Bad, _joinError.Length > 0 ? _joinError : " ", JoinFormWidth - 20);
        Kit.Space(Theme.Space2);
        if (Kit.Button("Connect"u8, ButtonKind.Secondary, 160, signedIn) || submit)
            TryJoin();
        Kit.Space(Theme.Space3);
    }

    private void TryJoin()
    {
        var host = _joinHost.ToString().Trim();
        if (host.Length == 0)
        {
            _joinError = "Enter the server's address.";
            return;
        }

        if (!ushort.TryParse(_joinPort.ToString(), out var port) || port == 0)
        {
            _joinError = "The port must be a number from 1 to 65535.";
            return;
        }

        _joinError = "";
        SaveCallsign();
        var settings = _app.Settings;
        settings.LastHost = host;
        settings.LastPort = port;
        settings.Save();

        var password = _joinPassword.ToString();
        if (!_app.JoinGame(host, port, password, out var error))
            _joinError = error;
        else
            _joinPassword.Clear();
    }

    private void SaveCallsign()
    {
        var settings = _app.Settings;
        settings.PlayerName = PlayerNames.Sanitize(_joinName.ToString());
        settings.Save();
        _joinName.SetValue(settings.PlayerName);
    }

    /// <summary>Joins a listed game, asking for its password first when the list says it has one.</summary>
    private void JoinListed(ListedGame game)
    {
        // Checked before the password prompt, so nobody types a password that cannot be used yet.
        if (!NetworkPlay.Allowed(_app.Accounts, false, out var refused))
        {
            _joinError = refused;
            return;
        }

        if (game.Password)
        {
            OpenPasswordPrompt(game);
            return;
        }

        SaveCallsign();
        if (!_app.JoinGame(game, null, out var error))
            _joinError = error;
    }

    private void OpenPasswordPrompt(ListedGame game)
    {
        _passwordFor = game;
        _listedPassword.Clear();
        _listedPasswordShown = false;
        _listedPasswordFocus = true;
        _listedError = "";
    }

    /// <summary>The prompt for a listed game's password, over the browser.</summary>
    private void DrawListedPassword()
    {
        var game = _passwordFor!;
        // A full-window surface over the browser: it dims what is under it and takes the pointer from it. Unlike
        // a screen's root it may come to the front, or it would be created behind the browser.
        ImGui.SetNextWindowPos(float2.Zero);
        ImGui.SetNextWindowSize(_ui.DisplaySize);
        ImGui.Begin("##listedpassword"u8, RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.GetWindowDrawList().AddRectFilled(float2.Zero, _ui.DisplaySize, Kit.Opaque(Theme.Void, 0.6f));
        var size = new float2(Kit.Px(480), Math.Min(Kit.Px(296), _ui.DisplaySize.Y - Kit.Px(24)));
        var min = (_ui.DisplaySize - size) * 0.5f;
        Kit.ChamferFill(ImGui.GetWindowDrawList(), min, min + size, Kit.Opaque(Theme.Panel), Kit.Px(Theme.Chamfer));
        BeginPanel("##listedpasswordpanel"u8, min, size);

        Kit.Caption("PASSWORD"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail().X;
        Kit.ClippedText(draw, _ui.SansBold, Theme.FontHeading, at, Theme.Text, game.Name, available);
        ImGui.Dummy(new float2(available, Kit.Px(Theme.FontHeading) * 1.3f));
        Kit.Space(Theme.Space1);
        Kit.Body("This game needs a password. Its host can give it to you."u8);
        Kit.Space(Theme.Space4);

        if (_listedPasswordFocus)
        {
            ImGui.SetKeyboardFocusHere();
            _listedPasswordFocus = false;
        }

        var submit = Kit.PasswordField("PASSWORD"u8, "##listedpasswordfield"u8, _listedPassword, ref _listedPasswordShown, 340);
        Kit.Space(Theme.Space2);
        Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Bad, _listedError.Length > 0 ? _listedError : " ");
        Kit.Space(Theme.Space3);

        if (Kit.Button("Join"u8, ButtonKind.Primary, 160, !_listedPassword.IsEmpty) || (submit && !_listedPassword.IsEmpty))
        {
            SaveCallsign();
            var password = _listedPassword.ToString();
            _listedPassword.Clear();
            if (_app.JoinGame(game, password, out var error))
                _passwordFor = null;
            else
                _listedError = error;
        }

        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 120) || BackPressed())
        {
            _listedPassword.Clear();
            _passwordFor = null;
        }

        EndPanel();
        EndRoot();
    }

    private bool IsSelected(ListedGame game) => game.Port == _selectedPort && game.Address == _selectedAddress;

    private void Select(ListedGame game)
    {
        _selectedAddress = game.Address;
        _selectedPort = game.Port;
    }

    /// <summary>The selected game if it is still in the list.</summary>
    private ListedGame? Selected(ServerList? list)
    {
        if (list is null || _selectedAddress is null)
            return null;
        foreach (var game in list.Games)
        {
            if (IsSelected(game))
                return game;
        }

        return null;
    }

    /// <summary>
    /// Orders a new list once, when it arrives: games with room first, then the busiest, then by name. The
    /// order is kept as indices into the list, so drawing it each frame allocates nothing.
    /// </summary>
    private void SortGames(ServerList? list)
    {
        if (ReferenceEquals(list, _gameOrderList))
            return;
        _gameOrderList = list;
        var games = list?.Games ?? [];
        if (_gameOrderIndex.Length < games.Length)
            _gameOrderIndex = new int[games.Length];
        for (var i = 0; i < games.Length; i++)
            _gameOrderIndex[i] = i;
        _gameOrderCount = games.Length;
        _gameOrder.Games = games;
        Array.Sort(_gameOrderIndex, 0, games.Length, _gameOrder);

        // The first game is picked when the picked one is gone, so Join and Enter work straight away. Picking
        // sends nothing; only joining does.
        if (games.Length > 0 && Selected(list) is null)
            Select(games[_gameOrderIndex[0]]);
    }

    private sealed class GameOrder : IComparer<int>
    {
        public ListedGame[] Games = [];

        public int Compare(int x, int y)
        {
            var a = Games[x];
            var b = Games[y];
            var aFull = a.Players >= a.MaxPlayers;
            var bFull = b.Players >= b.MaxPlayers;
            if (aFull != bFull)
                return aFull ? 1 : -1;
            if (a.Players != b.Players)
                return b.Players.CompareTo(a.Players);
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : x.CompareTo(y);
        }
    }

    /// <summary>
    /// The list the sample captures show, in the listing server's own format so it goes through the browser's
    /// real checks. It holds what the screen must survive: a name with ImGui's id markers and format
    /// characters, a name that fills its 48 characters, one in Japanese with a symbol the fonts lack, a game with a password,
    /// a full one, and an entry the browser must drop. The addresses are from the ranges kept for documentation.
    /// </summary>
    private static byte[] SampleListJson() =>
        Encoding.UTF8.GetBytes(
            """
            {"servers":[
              {"name":"Ceres Station","address":"203.0.113.10","port":27420,"players":3,"maxPlayers":8,"password":true,"version":"0.1.0"},
              {"name":"Free ore ## here ###id %s %n {0}","address":"203.0.113.11","port":27420,"players":5,"maxPlayers":16,"password":false,"version":"0.1.0"},
              {"name":"WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW","address":"203.0.113.12","port":27421,"players":0,"maxPlayers":4,"password":false,"version":"0.1.0"},
              {"name":"Vesta Yard","address":"198.51.100.20","port":27420,"players":8,"maxPlayers":8,"password":false,"version":"0.1.0"},
              {"name":"小惑星帯の採掘基地 ⛏ Ærø Øst","address":"198.51.100.21","port":27420,"players":1,"maxPlayers":6,"password":false,"version":"0.1.0"},
              {"name":"Pallas Deep Survey Cooperative of the Outer Belt","address":"198.51.100.22","port":27420,"players":2,"maxPlayers":12,"password":true,"version":"0.1.0"},
              {"name":"Not joinable","address":"localhost","port":27420,"players":0,"maxPlayers":8,"password":false,"version":"0.1.0"}
            ]}
            """
        );
}
