using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;

namespace Prospect.Client.Screens;

/// <summary>
/// The notices in the session: what posts them, where the stack goes, and what a click does. The rules are
/// <see cref="NoticeBoard"/>'s, the words, colours and durations the content file's, the drawing
/// <see cref="NoticePanel"/>'s; this is only the wiring to what the screens know.
/// </summary>
internal sealed partial class ScreenHost
{
    private NoticeCatalogue _noticeCatalogue = null!;
    private NoticeBoard _notices = null!;
    private readonly NoticeTracker _noticeTracker = new();
    private readonly Action _claimNoticesForMap;
    private readonly float[] _noticeHeights = new float[NoticeBoard.MaxVisible];
    private HudRect _noticeRect;
    private bool _noticesShown;

    private void InitializeNotices()
    {
        _noticeCatalogue = NoticeCatalogue.Load(_app.Actions);
        _notices = new NoticeBoard(_noticeCatalogue) { Enabled = !_app.Devices.Muted || _app.NoticesInScriptedRuns };
    }

    /// <summary>The notices, for code that posts them and tests that read them.</summary>
    public NoticeBoard Notices => _notices;

    /// <summary>Posts a notice of a kind from <see cref="NoticeKinds"/>: the one call other code needs. Does nothing while notices are off.</summary>
    public void Notify(string kind) => _notices.Post(kind);

    /// <summary>True while the stack is drawn over the game: the session is on screen and nothing covers it.</summary>
    private bool NoticesVisible => _screen == Screen.Session && _notices.Visible > 0 && !_sessionMenuOpen && !_controlsOpen;

    /// <summary>
    /// Posts this frame's notices from what the snapshot reports and ages the shown ones. Called once per frame with
    /// the game on screen, before anything is laid out.
    /// </summary>
    private void TrackNotices(SessionSnapshot snapshot, WorldView world)
    {
        if (!_notices.Enabled)
            return;
        _noticeTracker.Update(_notices, snapshot.Modules.Scan, snapshot.Exploration.Scan, snapshot.NewShip, world.Own.Id);
        // Only while they can be read: a notice does not run out behind a menu.
        if (NoticesVisible)
            _notices.Update(_ui.DeltaTime);
    }

    /// <summary>
    /// Places the stack before the map and the panels are laid out, so the map can keep its gestures off its
    /// rectangle. It sits under the navigation bar at the right margin, under the performance overlay and the command
    /// view's fleet list when they are there, and left of the ship editor's details panel.
    /// </summary>
    private void PlanNotices(SessionSnapshot snapshot, WorldView world)
    {
        _noticesShown = NoticesVisible;
        if (!_noticesShown)
            return;

        var top = HudTop;
        var display = _ui.DisplaySize;
        if (_app.ShowPerformance)
            top += Kit.Px(PerformanceHeight + Theme.Space2);
        // The fleet list is drawn in the command view (not over the map), from the same figures.
        if (!_mapOpen && world.CommandBlend > 0.5f)
        {
            var fleet = FleetListSize(snapshot, out _);
            top = Math.Max(top, FleetListMin(fleet).Y + fleet.Y + Kit.Px(Theme.Space2));
        }

        var right = display.X - Kit.Px(Theme.Space5);
        if (world.Editor.Shown)
        {
            // The editor's details panel takes the right edge; the stack stays left of it.
            right = EditorLayout.Of(display, Kit.Scale).Details.Left - Kit.Px(Theme.Space3);
        }

        _noticeRect = NoticePanel.Layout(_notices, right, top, display.Y - Kit.Px(Theme.Space5), _noticeHeights);
    }

    /// <summary>Draws the stack planned this frame, last of the game's surfaces so nothing covers it, and runs a click.</summary>
    private void DrawNotices()
    {
        if (!_noticesShown || _notices.Visible == 0)
            return;
        // While the pointer is hidden for flight the stack takes no input; otherwise it is a window like any other.
        var result = NoticePanel.Draw(_notices, _noticeRect, _noticeHeights, !_app.PointerCaptured, _mapOpen ? _claimNoticesForMap : null);
        if (result.ClosedRow >= 0)
        {
            _notices.Close(result.ClosedRow);
        }
        else if (result.ActionRow >= 0)
        {
            var action = _notices.NoticeAt(result.ActionRow).Action;
            _notices.Close(result.ActionRow);
            RunNoticeAction(action);
        }
    }

    private void RunNoticeAction(NoticeAction action)
    {
        switch (action)
        {
            case NoticeAction.OpenGasWindow:
                OpenPane(SocialPane.Gas);
                break;
            case NoticeAction.OpenChartsWindow:
                OpenPane(SocialPane.Charts);
                break;
        }
    }

    /// <summary>Gives the stack's area to the map's pointer, so a press on a notice is never a press on the map.</summary>
    private void BlockMapForNotices()
    {
        if (_noticesShown)
            _mapPointer.Block(new float2(_noticeRect.Left, _noticeRect.Top), new float2(_noticeRect.Width, _noticeRect.Height));
    }

    /// <summary>Forgets the session's notices when it ends: the queue, and what the tracker last saw.</summary>
    private void ResetNotices()
    {
        _notices.Clear();
        _noticeTracker.Reset();
        _noticesShown = false;
    }

    /// <summary>
    /// Posts the sample notices for <c>--notices-sample</c>: one of each kind with an action first, enough to fill the
    /// stack and leave some waiting.
    /// </summary>
    public void PostSampleNotices()
    {
        Notify(NoticeKinds.GasResults);
        Notify(NoticeKinds.SectorCharted);
        Notify(NoticeKinds.AsteroidResults);
        Notify(NoticeKinds.ModuleNoPower);
        Notify(NoticeKinds.LocatorResults);
        Notify(NoticeKinds.ShipBuilt);
    }
}
