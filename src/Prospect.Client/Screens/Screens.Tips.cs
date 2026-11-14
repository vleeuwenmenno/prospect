using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The in-game tips in the session: which moment fires which trigger, where the shown tip goes, Escape, and the
/// Settings screen's reset. The rules are <see cref="TipBoard"/>'s, the words and places the content file's, the
/// panel <see cref="TipPanel"/>'s; this is only the wiring to what the screens know.
/// </summary>
internal sealed partial class ScreenHost
{
    /// <summary>The battery fraction below which a low-energy tip fires, and the one it must climb back to before it can again.</summary>
    private const float EnergyLowBelow = 0.15f;
    private const float EnergyLowRearm = 0.30f;

    private TipCatalogue _tipCatalogue = null!;
    private TipBoard _tips = null!;
    private readonly Action _claimTipForMap;

    /// <summary>True on a frame in which Escape closed a tip: no other surface takes that press.</summary>
    private bool _tipBack;

    private bool _tipShown;
    private bool _tipDragging;
    private HudRect _tipRect;

    // A tip placed against a point keeps the point it first showed at, so it does not run from the pointer that
    // is on its way to a button.
    private string? _tipPlacedId;
    private float2 _tipPoint;

    // What was true last frame, so a trigger fires on the change and not every frame it stays true.
    private readonly TipSurfaces _tipSurfaces = new();
    private bool _tipSawUncharted;
    private bool _tipSawShield;
    private bool _tipEnergyLow;

    // The Settings screen's reset: asking, and told, so far.
    private bool _tipResetAsk;
    private bool _tipResetDone;
    private bool _tipResetScroll;

    private void InitializeTips()
    {
        _tipCatalogue = TipCatalogue.Load(_app.Actions);
        _tips = new TipBoard(_tipCatalogue, _app.Settings) { Enabled = !_app.Devices.Muted || _app.TipsInScriptedRuns };
    }

    /// <summary>The tips, for code that fires them and tests that read them.</summary>
    public TipBoard Tips => _tips;

    /// <summary>
    /// Brings up every tip for a trigger that is not hidden: the one call other code needs. Does nothing while tips
    /// are off.
    /// </summary>
    public void FireTip(string trigger)
    {
        _tips.Fire(trigger);
    }

    /// <summary>True while a tip is drawn over the game: the session is on screen and nothing covers it.</summary>
    private bool TipVisible => _screen == Screen.Session && _tips.Active && !_sessionMenuOpen && !_controlsOpen;

    /// <summary>
    /// Escape closes the tip before anything else takes it, so it is read first of the frame's shortcuts. The
    /// frame's other surfaces ask <see cref="BackPressed"/>, which stays quiet on that press.
    /// </summary>
    public void HandleTipShortcut(bool active)
    {
        _tipBack = false;
        if (!active || !TipVisible || _screenTime <= 0.1f)
            return;
        _tipBack = _tips.Escape(true, _app.Actions.Pressed(_app.Devices, InputAction.Pause, true));
        if (_tipBack)
            SaveTips();
    }

    /// <summary>True when Escape closed a tip this frame.</summary>
    public bool TipHandled => _tipBack;

    /// <summary>Writes the settings when the hidden tips changed, so a tick survives a crash.</summary>
    private void SaveTips()
    {
        if (_tips.TakeChanged() && !_app.Devices.Muted)
            _app.Settings.Save();
    }

    /// <summary>
    /// Fires the triggers of this frame: each on the change into its state. Called once per frame with the game on
    /// screen, before anything is laid out, so a tip fired by the map opening is placed in the same frame.
    /// </summary>
    private void TrackTips(SessionSnapshot snapshot, WorldView world)
    {
        if (!_tips.Enabled)
            return;
        _tipSurfaces.Track(_tips, _mapOpen, world.Mode == ViewMode.Command, world.Editor.Shown,
            _mapOpen && _mapCard.Pinned && _mapCard.Sector == world.Sector.Coord, InterfaceModeOn);

        var own = world.Own;
        var flight = world.OwnFlight;

        // In the flight or command view, in a sector with no chart, in a ship that can scan: the scan key's tip.
        Edge(own.Id != 0 && !_mapOpen && !world.Editor.Shown && _coverage.OwnCanChart && !Charted(world.Sector.Coord), ref _tipSawUncharted, TipTriggers.UnchartedSector);
        Edge(own.Id != 0 && flight.ShieldCapacity > 0f && own.ShieldOffline, ref _tipSawShield, TipTriggers.ShieldOffline);

        // The modules' tips: each on the change into its state, only in the flight and command views.
        var inSpace = own.Id != 0 && !_mapOpen && !world.Editor.Shown;
        Edge(inSpace && _moduleGate[0] is ModuleGate.Ready or ModuleGate.OutOfRange, ref _tipSawGasScanner, TipTriggers.GasScannerReady);
        Edge(inSpace && _moduleGate[1] != ModuleGate.Absent && _moduleTargets.Rock is { } tipRock && FindAnalysis(snapshot, tipRock) < 0, ref _tipSawAsteroidTarget, TipTriggers.AsteroidScannerReady);
        Edge(inSpace && _moduleGate[2] != ModuleGate.Absent, ref _tipSawLocator, TipTriggers.LocatorReady);

        // The battery is low below one level and not again until it has recovered past a higher one, so a battery
        // hovering at the line does not bring the tip back every few seconds.
        if (own.Id != 0 && flight.EnergyStorage > 0f)
        {
            var fraction = own.Energy / flight.EnergyStorage;
            var low = _tipEnergyLow ? fraction < EnergyLowRearm : fraction < EnergyLowBelow;
            if (low && !_tipEnergyLow)
                _tips.Fire(TipTriggers.EnergyLow);
            _tipEnergyLow = low;
        }
    }

    private void Edge(bool now, ref bool before, string trigger)
    {
        if (now && !before)
            _tips.Fire(trigger);
        before = now;
    }

    /// <summary>
    /// Places the tip that is shown, before the map and the panels are laid out, so the map can keep its own
    /// gestures off the area the tip covers. Without one nothing is blocked and nothing is drawn.
    /// </summary>
    private void PlanTip(WorldView world)
    {
        _tipShown = TipVisible;
        if (!_tipShown)
        {
            _tipPlacedId = null;
            _tipDragging = false;
            _tips.Dragging = false;
            return;
        }

        var tip = _tips.Current;
        var size = TipPanel.Measure(_tipCatalogue, tip, _ui.DisplaySize.X);
        var known = TipArea(tip.Anchor, world, out var area);
        var shift = _tips.Shift(tip.Anchor, known);
        // The title bar was held last frame: this frame's pointer movement moves the panel, so it keeps up with the
        // pointer instead of a frame behind. The result is what stays on screen, and is what is remembered.
        if (_tipDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            shift = TipPlacement.Dragged(tip.Anchor, known, area, size, _ui.DisplaySize, Kit.Scale, shift, ImGui.GetIO().MouseDelta);
            _tips.SetShift(tip.Anchor, known, shift);
        }

        var at = TipPlacement.Place(tip.Anchor, known, area, size, _ui.DisplaySize, Kit.Scale, shift);
        _tipRect = new HudRect(at.X, at.Y, at.X + size.X, at.Y + size.Y);
    }

    /// <summary>The region a tip is placed against, as it is on screen this frame, if it is there.</summary>
    private bool TipArea(in TipAnchor anchor, WorldView world, out HudRect area)
    {
        area = default;
        var display = _ui.DisplaySize;
        switch (anchor.Region)
        {
            case TipRegion.Screen:
                return true;
            case TipRegion.Cursor:
            case TipRegion.Target:
                // The pointer or the target when the tip first shows, then that place; none while the pointer is
                // hidden for flight, which has no point to be beside.
                if (!ReferenceEquals(_tipPlacedId, _tips.Current.Id))
                {
                    if (!PointFor(anchor.Region, world, out _tipPoint))
                        return false;
                    _tipPlacedId = _tips.Current.Id;
                }

                area = new HudRect(_tipPoint.X, _tipPoint.Y, _tipPoint.X, _tipPoint.Y);
                return true;
            case TipRegion.Console:
                if (world.Mode != ViewMode.Pilot || _mapOpen || world.Editor.Shown)
                    return false;
                var console = FlightConsole.Layout(display, Kit.Scale);
                area = new HudRect(console.LeftX, console.Top, console.Right, console.Top + console.Height);
                return true;
            case TipRegion.NewShipDialog:
                if (!_newShip.Open)
                    return false;
                area = NewShipDialog.Panel(display, Kit.Scale);
                return true;
            default:
                if (!world.Editor.Shown)
                    return false;
                var layout = EditorLayout.Of(display, Kit.Scale);
                area = anchor.Region switch
                {
                    TipRegion.EditorParts => layout.Parts,
                    TipRegion.EditorDetails => layout.Details,
                    _ => layout.Bar,
                };
                return true;
        }
    }

    private bool PointFor(TipRegion region, WorldView world, out float2 point)
    {
        point = default;
        if (region == TipRegion.Cursor)
        {
            if (_app.PointerCaptured)
                return false;
            point = ImGui.GetIO().MousePos;
            return true;
        }

        return world.TargetHull != 0 && world.TryGetHull(world.TargetHull, out var hull) && world.Project(world.HullPosition(hull), out point);
    }

    /// <summary>Draws the tip planned this frame, last of the game's surfaces so nothing covers it.</summary>
    private void DrawTip()
    {
        if (!_tipShown || !_tips.Active)
            return;
        // While the pointer is hidden for flight the panel takes no input; otherwise it is a window like any other.
        _tipDragging = TipPanel.Draw(_tips, _tipCatalogue, _tipRect, !_app.PointerCaptured, _mapOpen ? _claimTipForMap : null);
        _tips.Dragging = _tipDragging;
        SaveTips();
    }

    /// <summary>Gives the tip's area to the map's pointer, so a press on the tip is never a press on the map.</summary>
    private void BlockMapForTip()
    {
        if (_tipShown)
            _mapPointer.Block(new float2(_tipRect.Left, _tipRect.Top), new float2(_tipRect.Width, _tipRect.Height));
    }

    /// <summary>
    /// Forgets the session's tips when it ends: the stack, and what each trigger last saw, so the next game fires
    /// them again on their own changes. What the player hid stays hidden.
    /// </summary>
    private void ResetTipTracking()
    {
        _tips.Clear();
        _tipShown = false;
        _tipDragging = false;
        _tips.Dragging = false;
        _tipPlacedId = null;
        _tipSurfaces.Reset();
        _tipSawUncharted = _tipSawShield = _tipEnergyLow = false;
        _tipSawGasScanner = _tipSawAsteroidTarget = _tipSawLocator = false;
        _moduleTrack.Reset();
        _gasTarget.Reset();
    }

    /// <summary>
    /// The Settings screen's tips section: a short description and a Reset tips button, which asks before it
    /// forgets what was hidden, and says so when it did.
    /// </summary>
    private void DrawTipSettings(float rowWidth)
    {
        var words = _tipCatalogue.Settings;
        Kit.Caption(words.Caption);
        Kit.Space(Theme.Space2);
        ImGui.Indent(Kit.Px(Theme.Space3));
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, words.Description, rowWidth - 24);
        ImGui.Unindent(Kit.Px(Theme.Space3));
        Kit.Space(Theme.Space2);
        if (_tipResetScroll)
        {
            _tipResetScroll = false;
            ImGui.SetScrollHereY(0.5f);
        }

        if (_tipResetAsk)
        {
            Kit.Space(Theme.Space2);
            Kit.Label(_ui.Sans, Theme.FontBody, Theme.Text, words.Confirm);
            Kit.Space(Theme.Space2);
            if (Kit.Button(words.ConfirmYes, ButtonKind.Primary, 160))
            {
                _tips.Reset();
                SaveTips();
                _tipResetAsk = false;
                _tipResetDone = true;
            }

            ImGui.SameLine(0, Kit.Px(Theme.Space3));
            if (Kit.Button(words.ConfirmNo, ButtonKind.Secondary, 160))
                _tipResetAsk = false;
        }
        else
        {
            if (Kit.Button(words.Reset, ButtonKind.Secondary, 200))
            {
                _tipResetAsk = true;
                _tipResetDone = false;
            }

            if (_tipResetDone)
            {
                ImGui.SameLine(0, Kit.Px(Theme.Space3));
                var height = Kit.Measure(_ui.Sans, Theme.FontSmall, words.Done).Y;
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (Kit.Px(Theme.ControlHeight) - height) * 0.5f);
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Good, words.Done);
            }
        }

        DrawPanelSettings(rowWidth);
    }

    /// <summary>
    /// Opens the reset confirmation, scrolled into view, for <c>--tips-sample reset</c>.
    /// </summary>
    public void PreviewTipReset()
    {
        _tipResetAsk = true;
        _tipResetScroll = true;
    }
}
