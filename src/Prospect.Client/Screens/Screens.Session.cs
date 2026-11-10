using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Game.Client;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private bool _sessionMenuOpen;

    private void DrawSession(SessionSnapshot snapshot, float fade)
    {
        switch (snapshot.State)
        {
            case SessionState.Connecting or SessionState.Handshaking:
                DrawBackdrop();
                DrawConnecting(fade);
                break;
            case SessionState.Ended:
                DrawBackdrop();
                DrawSessionEnded(snapshot, fade);
                break;
            default:
                if (snapshot.OwnHull < 0 || snapshot.Galaxy is null)
                    break;
                var mapWasOpen = _mapOpen;
                DrawWorldInterface(snapshot);
                // Escape backs out one level at a time: the map closes itself, the command view returns to the
                // pilot view, and only from the pilot view does it open the menu.
                if (!mapWasOpen && !_mapOpen && BackPressed())
                {
                    if (_sessionMenuOpen)
                        _sessionMenuOpen = false;
                    else if (_app.World.Mode == World.ViewMode.Command)
                        _app.World.SetMode(World.ViewMode.Pilot);
                    else
                        _sessionMenuOpen = true;
                }
                if (_sessionMenuOpen)
                    DrawSessionMenu();
                break;
        }
    }

    private void DrawConnecting(float fade)
    {
        BeginRoot("##connecting"u8);
        var draw = ImGui.GetWindowDrawList();
        var center = _ui.DisplaySize * 0.5f + new float2(0, Slide(fade));
        Kit.Spinner(draw, center - new float2(0, Kit.Px(70)), Kit.Px(22));

        var title = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "CONNECTING"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, center - new float2(title.X * 0.5f, Kit.Px(24)), Theme.Accent, "CONNECTING"u8);
        var target = _app.Session?.Title ?? "";
        var targetSize = Kit.Measure(_ui.Sans, Theme.FontHeading, target);
        Kit.Text(draw, _ui.Sans, Theme.FontHeading, center - new float2(targetSize.X * 0.5f, 0), Theme.Text, target);

        ImGui.SetCursorScreenPos(center + new float2(-Kit.Px(70), Kit.Px(64)));
        if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140) || BackPressed())
            _app.LeaveSession();
        EndRoot();
    }

    private void DrawSessionEnded(SessionSnapshot snapshot, float fade)
    {
        BeginRoot("##ended"u8);
        var size = new float2(Kit.Px(560), Kit.Px(260));
        var min = (_ui.DisplaySize - size) * 0.5f + new float2(0, Slide(fade));
        BeginPanel("##endedpanel"u8, min, size);

        Kit.Caption("SESSION ENDED"u8, Theme.Bad);
        Kit.Space(Theme.Space1);
        Kit.Heading(
            snapshot.EndReason switch
            {
                SessionEndReason.ConnectFailed => "No answer from the server"u8,
                SessionEndReason.ConnectionLost => "Connection lost"u8,
                SessionEndReason.ServerClosed => "The server closed the session"u8,
                SessionEndReason.ServerFull => "The server is full"u8,
                SessionEndReason.VersionMismatch => "Game versions do not match"u8,
                SessionEndReason.Refused => "The server refused the connection"u8,
                _ => "Session closed"u8,
            }
        );
        Kit.Space(Theme.Space3);
        Kit.Body(
            snapshot.EndReason switch
            {
                SessionEndReason.ConnectFailed => "Check the address and port, and that the server is running."u8,
                SessionEndReason.ConnectionLost => "The server stopped responding. Your network may have dropped."u8,
                SessionEndReason.ServerClosed => "The host shut the server down or removed you from it."u8,
                SessionEndReason.ServerFull => "Every player slot is taken. Try again later."u8,
                SessionEndReason.VersionMismatch => "Update the game so it matches the server's version."u8,
                SessionEndReason.Refused => "The server did not accept this client."u8,
                _ => "You have left the session."u8,
            }
        );
        Kit.Space(Theme.Space6);
        if (Kit.Button("Back to Menu"u8, ButtonKind.Primary, 200) || BackPressed())
            _app.LeaveSession();

        EndPanel();
        EndRoot();
    }

    private void DrawSessionMenu()
    {
        DrawDim(0.6f);
        BeginRoot("##sessionmenu"u8);
        var size = new float2(Kit.Px(424), Kit.Px(330));
        var min = (_ui.DisplaySize - size) * 0.5f;
        BeginPanel("##sessionmenupanel"u8, min, size);

        Kit.Caption(_app.Session is { IsLocal: true } ? "LOCAL GAME"u8 : "MULTIPLAYER"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Menu"u8);
        Kit.Space(Theme.Space4);

        if (Kit.MenuItem("Resume"u8, 360))
            _sessionMenuOpen = false;
        if (Kit.MenuItem("Settings"u8, 360))
            OpenSettings(Screen.Session);
        if (Kit.MenuItem("Leave Session"u8, 360))
        {
            _sessionMenuOpen = false;
            _app.LeaveSession();
        }

        if (Kit.MenuItem("Quit to Desktop"u8, 360))
            _app.Quit();

        EndPanel();
        EndRoot();
    }

    /// <summary>The performance overlay: frame pacing, simulation load and allocation, drawn over any screen.</summary>
    private void DrawPerformance(SessionSnapshot? snapshot)
    {
        var draw = ImGui.GetForegroundDrawList();
        var stats = _app.Stats;
        var size = new float2(Kit.Px(330), Kit.Px(snapshot is null ? 154 : 220));
        var min = new float2(_ui.DisplaySize.X - size.X - Kit.Px(Theme.Space5), Kit.Px(Theme.Space5));
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
    }
}
