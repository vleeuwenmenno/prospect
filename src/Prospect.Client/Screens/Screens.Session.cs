using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private bool _sessionMenuOpen;

    /// <summary>Height of the performance overlay in a session, in design pixels.</summary>
    private const float PerformanceHeight = 238f;

    /// <summary>The password typed on the session-ended panel, to join the same server again with it.</summary>
    private readonly ImInputString _rejoinPassword = new(PasswordBytes);
    private bool _rejoinPasswordShown;
    private bool _rejoinFocus;
    private string _rejoinError = "";

    /// <summary>The session the ended panel was last set up for, so its countdown and focus start once per session.</summary>
    private GameSession? _endedSession;

    /// <summary>Interface time at which a server that counted too many wrong passwords takes the next one.</summary>
    private float _retryAt;

    private void DrawSession(SessionSnapshot snapshot, float fade)
    {
        switch (snapshot.State)
        {
            case SessionState.Connecting or SessionState.Handshaking or SessionState.Downloading:
                DrawBackdrop();
                DrawConnecting(fade);
                break;
            case SessionState.Ended:
                DrawBackdrop();
                DrawSessionEnded(snapshot, fade);
                break;
            default:
                if (!_app.MeshesReady)
                {
                    DrawBackdrop();
                    DrawConnecting(fade);
                    break;
                }
                if (snapshot.OwnHull < 0 || snapshot.Galaxy is null)
                    break;
                if (_controlsOpen)
                {
                    DrawControls();
                    break;
                }
                if (_controlsHandled)
                    break;
                var mapWasOpen = _mapOpen;
                TrackSocial(snapshot);
                DrawWorldInterface(snapshot);
                DrawSaveWarning();
                // The social windows over the world but under the map and the menu, which close them first.
                if (!_mapOpen && !_sessionMenuOpen)
                    DrawSocialWindows(snapshot);
                if (!_sessionMenuOpen)
                    DrawTopBar(snapshot);

                // Escape backs out one level at a time: the map closes itself, chat and the social windows close
                // (never a pinned one), the command view returns to the pilot view, and only from the pilot view
                // does it open the menu.
                // Escape that cancelled the ship editor this frame was its, not the menu's.
                if (!_editorHandled && !mapWasOpen && !_mapOpen && BackPressed() && !SocialBack())
                {
                    switch (NextBackStep(_shipInfoOpen, _worldSettingsOpen, _sessionMenuOpen, _app.World.CourseMenu.IsOpen, _app.World.Mode))
                    {
                        case BackStep.ShipInfo:
                            _shipInfoOpen = false;
                            break;
                        case BackStep.WorldSettings:
                            _worldSettingsOpen = false;
                            break;
                        case BackStep.SessionMenu:
                            _sessionMenuOpen = false;
                            break;
                        case BackStep.CourseMenu:
                            _app.World.CourseMenu.Close();
                            break;
                        case BackStep.PilotView:
                            _app.World.SetMode(World.ViewMode.Pilot);
                            break;
                        default:
                            _sessionMenuOpen = true;
                            break;
                    }
                }
                if (_worldSettingsRequested)
                    OpenWorldSettings(snapshot);
                if (_sessionMenuOpen && _worldSettingsOpen)
                    DrawWorldSettings(snapshot);
                else if (_sessionMenuOpen)
                    DrawSessionMenu(snapshot);
                break;
        }
    }

    /// <summary>What one press of Escape closes in the world, from the innermost surface outwards.</summary>
    internal enum BackStep : byte
    {
        ShipInfo,
        WorldSettings,
        SessionMenu,

        /// <summary>The command view's course menu: the view stays.</summary>
        CourseMenu,
        PilotView,

        /// <summary>Nothing is open in the pilot view: the menu opens.</summary>
        OpenMenu,
    }

    /// <summary>The step Escape takes: it backs out one level at a time, and the course menu goes before the command view does.</summary>
    internal static BackStep NextBackStep(bool shipInfo, bool worldSettings, bool sessionMenu, bool courseMenu, World.ViewMode mode) =>
        shipInfo ? BackStep.ShipInfo
        : worldSettings ? BackStep.WorldSettings
        : sessionMenu ? BackStep.SessionMenu
        : courseMenu ? BackStep.CourseMenu
        : mode == World.ViewMode.Command ? BackStep.PilotView
        : BackStep.OpenMenu;

    private void DrawConnecting(float fade)
    {
        BeginRoot("##connecting"u8);
        var draw = ImGui.GetWindowDrawList();
        var center = _ui.DisplaySize * 0.5f + new float2(0, Slide(fade));
        Kit.Spinner(draw, center - new float2(0, Kit.Px(70)), Kit.Px(22));

        var title = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "CONNECTING"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, center - new float2(title.X * 0.5f, Kit.Px(24)), Theme.Accent, "CONNECTING"u8);
        var target = _app.MeshError ?? _app.Session?.Title ?? "";
        var targetSize = Kit.Measure(_ui.Sans, Theme.FontHeading, target);
        Kit.Text(draw, _ui.Sans, Theme.FontHeading, center - new float2(targetSize.X * 0.5f, 0), Theme.Text, target);

        ImGui.SetCursorScreenPos(center + new float2(-Kit.Px(70), Kit.Px(64)));
        if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140) || BackPressed())
            _app.LeaveSession();
        EndRoot();
    }

    private void DrawSessionEnded(SessionSnapshot snapshot, float fade)
    {
        var reason = snapshot.EndReason;
        var asksPassword =
            (reason is SessionEndReason.PasswordRequired or SessionEndReason.WrongPassword or SessionEndReason.TooManyAttempts)
            && _app.Session is { RemoteHost: not null };
        if (!ReferenceEquals(_endedSession, _app.Session))
        {
            _endedSession = _app.Session;
            _rejoinPassword.Clear();
            _rejoinPasswordShown = false;
            _rejoinError = "";
            _retryAt = _ui.Time + (reason == SessionEndReason.TooManyAttempts ? snapshot.RetryAfterSeconds : 0);
            _rejoinFocus = asksPassword;
        }

        BeginRoot("##ended"u8);
        var size = new float2(Kit.Px(560), Math.Min(Kit.Px(asksPassword ? 352 : 260), _ui.DisplaySize.Y - Kit.Px(24)));
        var min = (_ui.DisplaySize - size) * 0.5f + new float2(0, Slide(fade));
        BeginPanel("##endedpanel"u8, min, size);

        Kit.Caption("SESSION ENDED"u8, Theme.Bad);
        Kit.Space(Theme.Space1);
        Kit.Heading(
            reason switch
            {
                SessionEndReason.ConnectFailed => "No answer from the server"u8,
                SessionEndReason.ConnectionLost => "Connection lost"u8,
                SessionEndReason.ServerClosed => "The server closed the session"u8,
                SessionEndReason.ServerFull => "The server is full"u8,
                SessionEndReason.VersionMismatch => "Game versions do not match"u8,
                SessionEndReason.Refused => "The server refused the connection"u8,
                SessionEndReason.PasswordRequired => "This server needs a password"u8,
                SessionEndReason.WrongPassword => "Wrong password"u8,
                SessionEndReason.TooManyAttempts => "Too many attempts"u8,
                SessionEndReason.ContentMismatch => "Game files do not match"u8,
                _ => "Session closed"u8,
            }
        );
        Kit.Space(Theme.Space3);
        Kit.Body(
            reason switch
            {
                SessionEndReason.ConnectFailed => "Check the address and port, and that the server is running."u8,
                SessionEndReason.ConnectionLost => "The server stopped responding. Your network may have dropped."u8,
                SessionEndReason.ServerClosed => "The host shut the server down or removed you from it."u8,
                SessionEndReason.ServerFull => "Every player slot is taken. Try again later."u8,
                SessionEndReason.VersionMismatch => "Update the game so it matches the server's version."u8,
                SessionEndReason.Refused => "The server did not accept this client."u8,
                SessionEndReason.PasswordRequired => "Enter the password its host gave you."u8,
                SessionEndReason.WrongPassword => "That is not this server's password. Try again."u8,
                SessionEndReason.TooManyAttempts => "The server makes this address wait before the next try."u8,
                SessionEndReason.ContentMismatch => "Same version, other content files. Use the server's files."u8,
                _ => "You have left the session."u8,
            }
        );
        Kit.Space(asksPassword ? Theme.Space4 : Theme.Space6);

        var leave = false;
        if (asksPassword)
        {
            var wait = (int)MathF.Ceiling(_retryAt - _ui.Time);
            if (_rejoinFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _rejoinFocus = false;
            }

            var submit = Kit.PasswordField("PASSWORD"u8, "##rejoinpassword"u8, _rejoinPassword, ref _rejoinPasswordShown, 420);
            Kit.Space(Theme.Space2);
            if (wait > 0)
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Accent, Kit.F($"You can try again in {wait} s."));
            else
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Bad, _rejoinError.Length > 0 ? _rejoinError : " ");
            Kit.Space(Theme.Space3);

            var ready = wait <= 0 && !_rejoinPassword.IsEmpty;
            if (Kit.Button("Join"u8, ButtonKind.Primary, 160, ready) || (submit && ready))
            {
                var password = _rejoinPassword.ToString();
                _rejoinPassword.Clear();
                if (!_app.RejoinWithPassword(password, out var error))
                    _rejoinError = error;
            }

            ImGui.SameLine(0, Kit.Px(Theme.Space3));
            leave = Kit.Button("Back to Menu"u8, ButtonKind.Secondary, 200);
        }
        else
        {
            leave = Kit.Button("Back to Menu"u8, ButtonKind.Primary, 200);
        }

        if (leave || BackPressed())
            _app.LeaveSession();

        EndPanel();
        EndRoot();
    }

    /// <summary>Opens the in-game menu.</summary>
    public void OpenSessionMenu() => _sessionMenuOpen = true;

    private void DrawSessionMenu(SessionSnapshot snapshot)
    {
        DrawDim(0.6f);
        BeginRoot("##sessionmenu"u8);
        var size = new float2(Kit.Px(424), Math.Min(Kit.Px(512), _ui.DisplaySize.Y - Kit.Px(24)));
        var min = (_ui.DisplaySize - size) * 0.5f;
        BeginPanel("##sessionmenupanel"u8, min, size);

        Kit.Caption(_app.Session is { IsLocal: true } ? "LOCAL GAME"u8 : "MULTIPLAYER"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Menu"u8);
        Kit.Space(Theme.Space2);
        DrawGameStatus(snapshot);
        Kit.Space(Theme.Space3);

        if (Kit.MenuItem("Resume"u8, 360))
            _sessionMenuOpen = false;
        if (Kit.MenuItem("World Settings"u8, 360))
            OpenWorldSettings(snapshot);
        if (Kit.MenuItem("Settings"u8, 360))
            OpenSettings(Screen.Session);
        // A saved world is saved on the way out; the labels say so, since there is no other save to make.
        var saved = _app.Session is { IsPersistent: true };
        if (Kit.MenuItem(saved ? "Save and Leave"u8 : "Leave Session"u8, 360))
        {
            _sessionMenuOpen = false;
            _app.LeaveSession();
        }

        if (Kit.MenuItem(saved ? "Save and Quit"u8 : "Quit to Desktop"u8, 360))
            _app.Quit();

        EndPanel();
        EndRoot();
    }

    /// <summary>
    /// The game's name, who can join it, and for a public game this process hosts whether the listing server has
    /// it right now, so a host can see that listing failed. The name comes from the server and is drawn as text.
    /// </summary>
    private void DrawGameStatus(SessionSnapshot snapshot)
    {
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var at = ImGui.GetCursorScreenPos();
        var lineHeight = Kit.Measure(_ui.Sans, Theme.FontBody, "Ag"u8).Y;
        Kit.ClippedText(draw, _ui.Sans, Theme.FontBody, at, Theme.Text, snapshot.ServerName, width);
        ImGui.Dummy(new float2(width, lineHeight));
        Kit.Space(Theme.Space1);

        var visibility = snapshot.Visibility switch
        {
            ServerVisibility.Public => "PUBLIC"u8,
            ServerVisibility.Unlisted => "UNLISTED"u8,
            _ => "PRIVATE"u8,
        };
        var players = snapshot.MaxPlayers > 0 ? Kit.F($"{visibility}   {snapshot.PlayerCount}/{snapshot.MaxPlayers} PLAYERS") : Kit.F($"{visibility}   {snapshot.PlayerCount} PLAYERS");
        Kit.Label(_ui.Mono, Theme.FontCaption, Theme.TextDim, players);
        if (snapshot.PasswordProtected)
        {
            var lockAt = ImGui.GetItemRectMax();
            Kit.Lock(draw, new float2(lockAt.X + Kit.Px(14), (ImGui.GetItemRectMin().Y + lockAt.Y) * 0.5f), Theme.TextDim);
        }

        if (_app.Session is not { IsLocal: true } session)
            return;
        DrawSaveStatus(session, snapshot.Visibility);
        if (snapshot.Visibility != ServerVisibility.Public)
            return;

        Kit.Space(Theme.Space1);
        var host = _app.Listing?.Url.Host ?? "";
        switch (session.LocalAnnounce)
        {
            case AnnounceState.Listed:
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Good, Kit.F($"Listed on {host}."));
                break;
            case AnnounceState.Announcing:
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.TextDim, Kit.F($"Announcing to {host}..."));
                break;
            case AnnounceState.Retrying:
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Bad, Kit.F($"Not listed: {host} did not take it. Trying again."));
                break;
            default:
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Bad, "Not listed: the server list is turned off."u8);
                break;
        }
    }

    /// <summary>The performance overlay: frame pacing, simulation load and allocation, drawn over any screen.</summary>
    private void DrawPerformance(SessionSnapshot? snapshot)
    {
        var draw = ImGui.GetForegroundDrawList();
        var stats = _app.Stats;
        var size = new float2(Kit.Px(330), Kit.Px(snapshot is null ? 154 : PerformanceHeight));
        // Under the navigation bar, so both stay readable.
        var top = HudTop;
        var min = new float2(_ui.DisplaySize.X - size.X - Kit.Px(Theme.Space5), top);
        Kit.PanelFrame(draw, min, min + size);

        var line = Kit.Px(18);
        var cursor = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
        Kit.Text(draw, _ui.Mono, Theme.FontHeading, cursor, Theme.Text, Kit.F($"{stats.FramesPerSecond:0}"));
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor + new float2(Kit.Px(96), Kit.Px(12)), Theme.TextDim, Kit.F($"FPS   {stats.LastFrameMs:0.00} MS"));
        cursor.Y += Kit.Px(38);

        // Frame-time history, one bar per frame, scaled so a 60 Hz frame fills the graph.
        var graph = new float2(size.X - Kit.Px(Theme.Space4 * 2), Kit.Px(36));
        draw.AddRectFilled(cursor, cursor + graph, Kit.Color(Theme.Void, 0.8f));
        var history = stats.History;
        var barWidth = graph.X / history.Length;
        for (var i = 0; i < history.Length; i++)
        {
            var ms = history[(stats.HistoryStart + i) % history.Length];
            var height = Math.Min(1f, ms / 16.67f) * graph.Y;
            var x = cursor.X + i * barWidth;
            draw.AddRectFilled(
                new float2(x, cursor.Y + graph.Y - height),
                new float2(x + Math.Max(1f, barWidth), cursor.Y + graph.Y),
                Kit.Color(ms > 16.67f ? Theme.Bad : ms > 6.94f ? Theme.Accent : Theme.Good, 0.85f)
            );
        }

        cursor.Y += graph.Y + Kit.Px(Theme.Space2);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, stats.ThreadAllocatedBytes > 0 ? Theme.Bad : Theme.TextDim, Kit.F($"ALLOC  {stats.ThreadAllocatedBytes} B FRAME   {stats.ProcessAllocatedBytes} B ALL"));
        cursor.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, Theme.TextDim, Kit.F($"JOBS   {_app.Jobs.Completed} DONE   {_app.Jobs.Pending} PENDING   {_app.Jobs.WorkerCount} WORKERS"));
        cursor.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, Theme.TextMute, _app.GpuLabel);

        if (snapshot is null)
            return;

        cursor.Y += line + Kit.Px(Theme.Space2);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, Theme.TextDim, Kit.F($"CLIENT {snapshot.Loop.AverageMs:0.000} MS AVG   {snapshot.Loop.MaxMs:0.00} MAX   {snapshot.Loop.Overruns} LATE"));
        cursor.Y += line;
        if (_app.Session?.LocalServerStatus is { } server)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, Theme.TextDim, Kit.F($"SERVER {server.Loop.AverageMs:0.000} MS AVG   {server.Loop.MaxMs:0.00} MAX   {server.Loop.Overruns} LATE"));
        else
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, Theme.TextDim, Kit.F($"SERVER REMOTE   RTT {snapshot.RoundTripMs:0.0} MS"));
        cursor.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, Theme.TextMute, Kit.F($"TICK   CLIENT {snapshot.ClientTick}   SERVER {snapshot.ServerTick}"));
        cursor.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, cursor, snapshot.PredictionError > 0.05f ? Theme.Bad : Theme.TextMute, Kit.F($"PREDICT  OFF BY {snapshot.PredictionError:0.000} M"));
    }
}
