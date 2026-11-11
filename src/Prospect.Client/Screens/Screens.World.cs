using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private const int MapCacheSize = 1 << 14;
    private const float MapMinCell = 1.4f;
    private const float MapMaxCell = 190f;

    /// <summary>Cells at least this many pixels wide are drawn sector by sector; smaller ones are sampled.</summary>
    private const float MapSectorDetailCell = 20f;

    private readonly SectorSummary[] _mapCache = new SectorSummary[MapCacheSize];
    private readonly bool[] _mapCacheValid = new bool[MapCacheSize];
    private readonly string?[] _mapNames = new string?[MapCacheSize];
    private Galaxy? _mapGalaxy;
    private bool _mapOpen;
    private bool _shipInfoOpen;
    private HullDesign? _sheetDesign;
    private ulong _sheetBlocks;
    private HullStats _sheetStats;
    private float _mapTime;
    private float2 _mapCenter;
    private readonly MapPointer _mapPointer = new();
    private float _mapCell = 44f;
    private readonly HashSet<SectorCoord> _discovered = [];
    private readonly SectorCoord[] _discoveredFrom = new SectorCoord[256];
    private Galaxy? _discoveredGalaxy;
    private int _mapLayer;
    private SectorCoord _mapHover;
    private string _mapHoverName = "";
    private bool _mapHasHover;
    private bool _mapHoverPinned;
    private bool _mapMenuOpen;
    private SectorCoord _mapMenuSector;
    private string _mapMenuName = "";
    private float2 _mapMenuMin;
    private float2 _mapMenuSize;
    private float _mapMenuTime;

    // The target marker's state from frame to frame: which target it shows, whether that target is out of view,
    // how far the bracket has turned into the border arrow (0 to 1), and the arrow's direction in radians.
    private uint _indicatorHull;
    private RockRef? _indicatorRock;
    private TargetCue _targetCue;

    /// <summary>
    /// Where the left column (sector panel and party list) ended when last drawn. The markers are drawn before the
    /// panels that cover them, so they keep clear of the column as it was a frame ago.
    /// </summary>
    private float _columnBottom;

    /// <summary>True while the player is in space with nothing open over it, so input belongs to the world.</summary>
    public bool WorldInputEnabled => _screen == Screen.Session && !_sessionMenuOpen && !_mapOpen && !_shipInfoOpen && !_controlsOpen && !_socialOpen && !_chatOpen;

    public void OpenShipInfo() => _shipInfoOpen = true;

    /// <summary>Opens the map zoomed all the way out, centred on the galaxy's core.</summary>
    public void OpenGalaxyOverview()
    {
        OpenMap();
        var shape = _app.World.Galaxy!.Shape;
        _mapCenter = new float2(shape.Center.X, shape.Center.Z);
        _mapCell = Math.Max(MapMinCell, _ui.DisplaySize.Y / (shape.Radius * 2.5f));
    }

    /// <summary>
    /// Opens the map with the pointer taken to be on the player's own sector, so that the panel describing the
    /// hovered sector shows. Used by the scripted <c>--open map-hover</c> view, where the real pointer is muted.
    /// </summary>
    public void OpenMapHover()
    {
        OpenMap();
        _mapHoverPinned = true;
    }

    public void OpenMap()
    {
        _mapOpen = true;
        _mapHoverPinned = false;
        _mapMenuOpen = false;
        _mapTime = 0;
        _mapPointer.Reset();

        // The map orders exactly the selected hulls. With nothing selected it starts on the hull being flown, as
        // its orders used to go there; clearing the list afterwards leaves nothing to order.
        var world = _app.World;
        world.PruneSelection();
        if (world.Selection.Length == 0 && world.Own.Id != 0)
            world.Select(world.Own.Id, false);
        _mapCenter = new float2((float)(_app.World.OwnPosition.X / _app.World.Galaxy!.SectorSize), (float)(_app.World.OwnPosition.Z / _app.World.Galaxy!.SectorSize));
        _mapLayer = _app.World.Sector.Coord.Y;
    }

    /// <summary>Everything drawn over the world while in space: flight readouts, command markers, the map.</summary>
    private void DrawWorldInterface(SessionSnapshot snapshot)
    {
        var world = _app.World;
        UpdateDiscovery(snapshot, world);
        if (!_sessionMenuOpen && !SocialSurfaceOpen && _app.Actions.Pressed(_app.Devices, InputAction.ToggleMap, !_ui.WantsTextInput))
        {
            if (_mapOpen)
                _mapOpen = false;
            else
                OpenMap();
        }

        if (!_sessionMenuOpen && !_mapOpen && !SocialSurfaceOpen && _app.Actions.Pressed(_app.Devices, InputAction.ShipInfo, !_ui.WantsTextInput))
            _shipInfoOpen = !_shipInfoOpen;

        // Everything shown over the world goes on ImGui's background layer, which is drawn beneath every window.
        // Menus, the world settings, the map and the fleet list are windows, so they always cover it, and a menu's
        // dim, drawn on the same layer afterwards, dims it too. Within the layer, what is drawn later is on top:
        // markers that move with the world first, then the panels and sheets they must never cover.
        var draw = ImGui.GetBackgroundDrawList();
        if (!_mapOpen)
        {
            var pilot = 1f - world.CommandBlend;
            _jumpPrompt = pilot > 0.01f ? PromptFor(world) : JumpPrompt.None;
            if (pilot > 0.01f)
                DrawFlightReadouts(draw, snapshot, world, pilot);
            if (world.CommandBlend > 0.01f)
                DrawCommandOverlay(draw, snapshot, world, world.CommandBlend);
            if (pilot > 0.01f)
                DrawTargetReadout(draw, snapshot, world, pilot);
            // The party list must keep at least one member on screen above the flight readouts, and above the
            // jump strip while it is drawn and shares the list's width. When the full sector panel leaves no room
            // for the whole list, the panel drops its ore and server lines (they are on the map and in the menu),
            // and if that is still not enough the list turns into one-line rows.
            var limit = pilot > 0.01f ? ReservedTop(Kit.Px(HudLayout.Margin), HudLayout.ColumnRight(_ui.DisplaySize, Kit.Scale)) : _ui.DisplaySize.Y - Kit.Px(Theme.Space5);
            var party = PartyHudCardsHeight(snapshot);
            var gap = Kit.Px(Theme.Space3);
            var compactSector = party > 0 && Kit.Px(Theme.Space5) + SectorPanelHeight(world, false) + gap + party > limit;
            var cards = !compactSector || Kit.Px(Theme.Space5) + SectorPanelHeight(world, true) + gap + party <= limit;
            var sectorBottom = DrawSectorPanel(draw, snapshot, world, compactSector);
            var partyBottom = DrawPartyHud(draw, snapshot, world, sectorBottom, limit, cards);
            _columnBottom = partyBottom;
            if (!_chatOpen)
                DrawChatFeed(draw, snapshot, partyBottom);
            if (_shipInfoOpen)
                DrawSheet(draw, snapshot, world);
            if (world.CommandBlend > 0.5f)
                DrawFleetList(snapshot, world, world.CommandBlend);
        }
        else
        {
            DrawMap(snapshot, world);
        }
    }

    /// <summary>
    /// The ship data sheet, or in the command view the sheet of the first selected thing: a hull, or an asteroid
    /// when no hull is selected.
    /// </summary>
    private void DrawSheet(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        if (world.Mode == ViewMode.Command && world.Selection.Length == 0 && world.SelectedRocks.Length > 0)
        {
            DrawRockInfo(draw, world, world.SelectedRocks[0]);
            return;
        }

        var shown = world.Own;
        if (world.Mode == ViewMode.Command && world.Selection.Length > 0 && world.TryGetHull(world.Selection[0], out var picked))
            shown = picked;
        DrawShipInfo(draw, snapshot.Content.Designs[Math.Min(shown.Design, snapshot.Content.Designs.Length - 1)], shown.Blocks, world.Mode == ViewMode.Command);
    }

    /// <summary>The sector panel's height: full, or compact with only the name and one line of figures.</summary>
    private float SectorPanelHeight(WorldView world, bool compact)
    {
        if (compact)
            return Kit.Px(64);
        var hasRock = world.Sector.AsteroidCount > 0 && !_mapOpen;
        var inner = HudLayout.ColumnWidth(_ui.DisplaySize, Kit.Scale) - Kit.Px(Theme.Space4) * 2;
        return Kit.Px(116) + (hasRock ? SectorCharacterHeight(world.Galaxy!, world.Sector.Character, inner) + Kit.Px(Theme.Space2) : 0f);
    }

    /// <summary>
    /// Draws the sector panel at the top left and returns where it ends, for what goes under it. Compact, it keeps
    /// the name, the coordinates, the rocks and the players, and leaves out the server line and the ore.
    /// </summary>
    private float DrawSectorPanel(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, bool compact = false)
    {
        // The panel grows by what there is to say about the sector's ore; a sector without asteroids has none.
        // Under the map it stays short: the map's own heading sits below it, and the map describes sectors itself.
        var min = new float2(Kit.Px(Theme.Space5), Kit.Px(Theme.Space5));
        var width = HudLayout.ColumnWidth(_ui.DisplaySize, Kit.Scale);
        var inner = width - Kit.Px(Theme.Space4) * 2;
        var hasRock = world.Sector.AsteroidCount > 0 && !_mapOpen;
        var size = new float2(width, SectorPanelHeight(world, compact));
        Hud.Plate(draw, min, min + size, Theme.Accent);
        // Narrowed to keep the middle of the view clear, the panel cuts what does not fit rather than spill into it.
        draw.PushClipRect(min, min + size, true);
        // A rule under the name, and in the full panel one over the sector's character, so the panel reads as
        // three groups: where, which server, what is here.
        var rule = Kit.Color(Theme.Line, 0.9f);
        var ruleLeft = min.X + Kit.Px(Theme.Space4);
        var ruleRight = min.X + size.X - Kit.Px(Theme.Chamfer);
        draw.AddLine(new float2(ruleLeft, min.Y + Kit.Px(37)), new float2(ruleRight, min.Y + Kit.Px(37)), rule, 1f);
        if (!compact && hasRock)
            draw.AddLine(new float2(ruleLeft, min.Y + Kit.Px(101)), new float2(ruleRight, min.Y + Kit.Px(101)), rule, 1f);

        var star = world.Galaxy!.StarClasses[world.Sector.StarClass];
        var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
        if (compact)
        {
            draw.AddCircleFilled(text + new float2(Kit.Px(5), Kit.Px(10)), Kit.Px(5), Kit.Color(StarColor(star.Color)), 20);
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, text + new float2(Kit.Px(18), 0), Theme.Text, world.SectorName, inner - Kit.Px(18));
            var at = world.Sector.Coord;
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(28)), Theme.TextDim,
                Kit.F($"SECTOR {at.X} {at.Y} {at.Z}   {world.Sector.AsteroidCount} ROCKS   PLAYERS {snapshot.PlayerCount}/{snapshot.MaxPlayers}"), inner);
            draw.PopClipRect();
            return min.Y + size.Y;
        }

        if (hasRock)
            DrawSectorCharacter(draw, text + new float2(0, Kit.Px(94)), inner, world.Galaxy!, world.Sector.Character);
        draw.AddCircleFilled(text + new float2(Kit.Px(5), Kit.Px(10)), Kit.Px(5), Kit.Color(StarColor(star.Color)), 20);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, text + new float2(Kit.Px(18), 0), Theme.Text, world.SectorName, inner - Kit.Px(18));

        var coord = world.Sector.Coord;
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(30)), Theme.TextDim, Kit.F($"SECTOR {coord.X} {coord.Y} {coord.Z}   {world.Sector.AsteroidCount} ROCKS"), inner);

        // The server: its name on a line of its own, cut off at the panel's edge since a host may choose a long
        // one, then who can join it and how full it is.
        draw.PushClipRect(text, new float2(text.X + inner, min.Y + size.Y), true);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(50)), Theme.TextDim, Kit.F($"{snapshot.ServerName}"));
        draw.PopClipRect();

        var visibility = snapshot.Visibility switch
        {
            ServerVisibility.Public => snapshot.PasswordProtected ? "PUBLIC, PASSWORD"u8 : "PUBLIC"u8,
            ServerVisibility.Unlisted => snapshot.PasswordProtected ? "UNLISTED, PASSWORD"u8 : "UNLISTED"u8,
            _ => "PRIVATE"u8,
        };
        var status = text + new float2(0, Kit.Px(70));
        var shown = Kit.Text(draw, _ui.Mono, Theme.FontCaption, status, snapshot.Visibility == ServerVisibility.Private ? Theme.TextMute : Theme.Accent, visibility).X + Kit.Px(Theme.Space3);
        Kit.ClippedText(
            draw,
            _ui.Mono,
            Theme.FontCaption,
            status + new float2(shown, 0),
            Theme.TextMute,
            snapshot.RoundTripMs < 0
                ? Kit.F($"PLAYERS {snapshot.PlayerCount}/{snapshot.MaxPlayers}")
                : Kit.F($"PLAYERS {snapshot.PlayerCount}/{snapshot.MaxPlayers}   RTT {snapshot.RoundTripMs:0} MS"),
            inner - shown
        );
        draw.PopClipRect();
        return min.Y + size.Y;
    }

    /// <summary>The pilot's instruments: speed, boost heat, dampeners, where the hull is going.</summary>
    private void DrawFlightReadouts(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var previousFade = Kit.Fade;
        Kit.Fade = alpha;
        var display = _ui.DisplaySize;
        var own = world.Own;
        var speed = own.Velocity.Length();

        // Nose marker and, once moving, the direction of travel.
        if (world.Project(world.OwnPosition + Vector3.Transform(-Vector3.UnitZ, world.OwnOrientation) * 2000f, out var nose))
        {
            draw.AddCircle(nose, Kit.Px(9), Kit.Color(Theme.Text, 0.55f), 24, Kit.Px(1.5f));
            draw.AddCircleFilled(nose, Kit.Px(1.5f), Kit.Color(Theme.Text, 0.8f), 8);
        }

        if (speed > 2f && world.Project(world.OwnPosition + own.Velocity / speed * 2000f, out var prograde))
        {
            var r = Kit.Px(6);
            var color = Kit.Color(Theme.Accent, 0.9f);
            draw.AddCircle(prograde, r, color, 16, Kit.Px(1.5f));
            draw.AddLine(prograde - new float2(r * 2, 0), prograde - new float2(r, 0), color, Kit.Px(1.5f));
            draw.AddLine(prograde + new float2(r, 0), prograde + new float2(r * 2, 0), color, Kit.Px(1.5f));
            draw.AddLine(prograde - new float2(0, r * 2), prograde - new float2(0, r), color, Kit.Px(1.5f));
        }

        // One clear room for every marker that moves with the world: the target's and the plotted jump's.
        var room = new HudCueRoom(display, Kit.Scale, _columnBottom, world.TargetHull != 0 || world.TargetRock is not null, _jumpPrompt != JumpPrompt.None);
        DrawTargetMarkers(draw, snapshot, world, room);
        DrawNavTarget(draw, world, room, _jumpPrompt);

        // The view line: where the hull will point once it is under thrust.
        var middle = display * 0.5f;
        var reticle = Kit.Color(world.FreeLook ? Theme.Info : Theme.Accent, 0.8f);
        draw.AddLine(middle - new float2(Kit.Px(14), 0), middle - new float2(Kit.Px(5), 0), reticle, Kit.Px(1.5f));
        draw.AddLine(middle + new float2(Kit.Px(5), 0), middle + new float2(Kit.Px(14), 0), reticle, Kit.Px(1.5f));
        draw.AddLine(middle + new float2(0, Kit.Px(5)), middle + new float2(0, Kit.Px(14)), reticle, Kit.Px(1.5f));
        if (world.FreeLook)
        {
            var free = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "FREE LOOK"u8);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, middle + new float2(-free.X * 0.5f, Kit.Px(22)), Theme.Info, "FREE LOOK"u8);
        }

        // Speed in the middle; what keeps the hull alive on the left, what drives it on the right. Above it, the
        // jump strip: what the drive is doing and what the pilot can do, kept off the middle of the view.
        DrawFlightConsole(draw, snapshot, own, speed);
        DrawJumpStrip(draw, snapshot, world, _jumpPrompt);

        if (own.Order.Kind == OrderKind.MoveTo && world.Project(own.Order.Target, out var target))
            DrawTargetMarker(draw, target, Theme.Accent);

        Kit.Fade = previousFade;
    }

    /// <summary>Markers and readouts for the command view: hulls, selection, orders and the point under the pointer.</summary>
    private void DrawCommandOverlay(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var previousFade = Kit.Fade;
        Kit.Fade = alpha;
        var display = _ui.DisplaySize;
        var planeHeight = world.CommandFocus.Y;
        var designs = snapshot.Content.Designs;

        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (!world.IsDrawn(hull))
                continue;
            var position = world.DrawnPosition(hull);
            if (!world.Project(position, out var screen))
                continue;

            var mine = hull.Owner == snapshot.PlayerId;
            var selected = mine && world.IsSelected(hull.Id);
            var color = mine ? Theme.Accent : Theme.Info;

            // A stalk down to the command plane shows how far above or below it the hull is.
            var onPlane = new Double3(position.X, planeHeight, position.Z);
            if (Math.Abs(position.Y - planeHeight) > 1.0 && world.Project(onPlane, out var foot))
            {
                draw.AddLine(screen, foot, Kit.Color(Theme.Info, 0.5f));
                draw.AddCircleFilled(foot, Kit.Px(2.5f), Kit.Color(Theme.Info, 0.7f), 8);
            }

            // The marker's shape says what class of hull it is: diamond ship, small triangle fighter, square station.
            var design = designs[Math.Min(hull.Design, designs.Length - 1)];
            var s = Kit.Px(design.Class == HullClass.Fighter ? 5 : 7);
            Span<float2> diamond = design.Class switch
            {
                HullClass.Fighter => [screen + new float2(0, -s), screen + new float2(s, s), screen + new float2(-s, s), screen + new float2(-s, s)],
                HullClass.Station => [screen + new float2(-s, -s), screen + new float2(s, -s), screen + new float2(s, s), screen + new float2(-s, s)],
                _ => [screen + new float2(0, -s), screen + new float2(s, 0), screen + new float2(0, s), screen + new float2(-s, 0)],
            };
            if (selected)
            {
                draw.AddConvexPolyFilled(diamond, Kit.Color(color));
                var reach = new float2(Kit.Px(17), Kit.Px(17));
                Brackets(draw, screen - reach, screen + reach, Kit.Px(7), Kit.Color(Theme.Text));
            }
            else
            {
                draw.AddPolyline(diamond, Kit.Color(color, mine ? 1f : 0.8f), ImDrawFlags.Closed, Kit.Px(1.5f));
            }

            var label = screen + new float2(Kit.Px(24), -Kit.Px(14));
            if (mine)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, label, selected ? Theme.Text : color, Kit.F($"{design.Name} {(hull.Id & 0xFFFF) + 1}"));
            else
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, label, color, Kit.F($"PLAYER {hull.Owner}  {design.Name}"));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, label + new float2(0, Kit.Px(15)), Theme.TextDim, Kit.F($"{hull.Velocity.Length():0} M/S"));

            if (mine && hull.Order.Kind == OrderKind.MoveTo && world.Project(hull.Order.Target, out var target))
            {
                draw.AddLine(screen, target, Kit.Color(color, selected ? 0.7f : 0.3f), Kit.Px(1.5f));
                DrawTargetMarker(draw, target, color);
            }
        }

        // Selected asteroids: a ring at their outline with what they are.
        foreach (var rock in world.SelectedRocks)
        {
            if (!world.TryGetRock(rock, out var rockPosition, out var radius) || !world.ProjectDisc(rockPosition, radius, out var rockScreen, out var pixels, out _))
                continue;
            var ring = Math.Max(pixels * 1.08f, Kit.Px(10));
            draw.AddCircle(rockScreen, ring, Kit.Color(Theme.Text, 0.9f), 48, Kit.Px(1.5f));
            var reach = new float2(ring + Kit.Px(5), ring + Kit.Px(5));
            Brackets(draw, rockScreen - reach, rockScreen + reach, Kit.Px(8), Kit.Color(Theme.Info));

            var label = rockScreen + new float2(ring + Kit.Px(12), -Kit.Px(14));
            var mass = world.Galaxy!.AsteroidDensity * 4.18879f * MathF.Pow(radius * RockField.SolidFraction, 3);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, label, Theme.Text, RockTitle(world, rock));
            Kit.Text(
                draw,
                _ui.Mono,
                Theme.FontCaption,
                label + new float2(0, Kit.Px(15)),
                Theme.TextDim,
                mass >= 1e6f ? Kit.F($"R {radius:0} M   {mass / 1e6f:0.00} MT") : Kit.F($"R {radius:0} M   {mass / 1e3f:0.0} KT")
            );
            var drift = world.RockDrift(rock);
            if (drift > 0.005f)
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, label + new float2(0, Kit.Px(30)), Theme.Accent, Kit.F($"DRIFTING {drift:0.00} M/S"));
        }

        var pointer = _app.Devices.MousePosition;
        if (world.BoxSelecting)
        {
            // The selection box being dragged.
            var min = new float2(Math.Min(world.BoxStart.X, world.BoxEnd.X), Math.Min(world.BoxStart.Y, world.BoxEnd.Y));
            var max = new float2(Math.Max(world.BoxStart.X, world.BoxEnd.X), Math.Max(world.BoxStart.Y, world.BoxEnd.Y));
            draw.AddRectFilled(min, max, Kit.Color(Theme.Accent, 0.08f));
            draw.AddRect(min, max, Kit.Color(Theme.Accent, 0.9f));
        }
        else if (WorldInputEnabled && !_ui.PointerOverInterface && world.GroundPoint(pointer, out var ground) && world.Project(ground, out var groundScreen))
        {
            // The point a move order would go to.
            draw.AddCircle(groundScreen, Kit.Px(7), Kit.Color(Theme.Text, 0.7f), 20, Kit.Px(1.2f));
            var range = ground.RelativeTo(world.CommandFocus).Length();
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, groundScreen + new float2(Kit.Px(14), Kit.Px(8)), Theme.TextDim, range >= 1000f ? Kit.F($"{range / 1000f:0.00} KM") : Kit.F($"{range:0} M"));
        }

        var title = "COMMAND"u8;
        var titleSize = Kit.Measure(_ui.SansMedium, Theme.FontSmall, title);
        var top = new float2(display.X * 0.5f, Kit.Px(Theme.Space5));
        Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, top - new float2(titleSize.X * 0.5f, 0), Theme.Accent, title);
        var status = world.CommandFollowing
            ? Kit.F($"{world.Selection.Length + world.SelectedRocks.Length} SELECTED   FOLLOWING   RANGE {world.CommandDistance / 1000f:0.00} KM")
            : Kit.F($"{world.Selection.Length + world.SelectedRocks.Length} SELECTED   FREE CAMERA   RANGE {world.CommandDistance / 1000f:0.00} KM");
        var statusSize = Kit.Measure(_ui.Mono, Theme.FontCaption, status);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, top + new float2(-statusSize.X * 0.5f, Kit.Px(22)), Theme.TextDim, status);

        Kit.Fade = previousFade;
    }

    /// <summary>
    /// The list of the player's hulls at the right edge of the command view. Click a row to select the hull,
    /// Shift-click to add it, double-click to send the camera to it.
    /// </summary>
    private void DrawFleetList(SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var owned = 0;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Owner == snapshot.PlayerId)
                owned++;
        }

        var rowHeight = Kit.Px(44);
        var size = new float2(Kit.Px(300), Kit.Px(54) + rowHeight * owned);
        // Under the quick buttons, and under the performance overlay when it is shown.
        var top = QuickBarBottom + Kit.Px(Theme.Space3) + (_app.ShowPerformance ? Kit.Px(PerformanceHeight + Theme.Space2) : 0f);
        var min = new float2(_ui.DisplaySize.X - size.X - Kit.Px(Theme.Space5), top);
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##fleet"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        RaiseIfAppearing();
        Kit.Fade = alpha;
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3)), Theme.Accent, "FLEET"u8);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(Kit.Px(Theme.Space4 + 52), Kit.Px(Theme.Space3)), Theme.TextMute, Kit.F($"{owned} HULLS"));

        var designs = snapshot.Content.Designs;
        var shift = ImGui.GetIO().KeyShift || ImGui.GetIO().KeyCtrl;
        var y = min.Y + Kit.Px(42);
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Owner != snapshot.PlayerId)
                continue;

            var rowMin = new float2(min.X + Kit.Px(6), y);
            var rowSize = new float2(size.X - Kit.Px(12), rowHeight - Kit.Px(4));
            ImGui.SetCursorScreenPos(rowMin);
            ImGui.PushID((int)hull.Id);
            if (ImGui.InvisibleButton("##hull"u8, rowSize))
                world.Select(hull.Id, shift);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                world.FocusOn(hull.Id);
            var hot = Kit.Animate(ImGui.GetItemID(), ImGui.IsItemHovered() ? 1f : 0f);
            ImGui.PopID();

            var selected = world.IsSelected(hull.Id);
            draw.AddRectFilled(rowMin, rowMin + rowSize, Kit.Color(Theme.PanelRaised, selected ? 0.95f : 0.5f * hot));
            if (selected)
                draw.AddRectFilled(rowMin, rowMin + new float2(Kit.Px(3), rowSize.Y), Kit.Color(Theme.Accent));

            var design = designs[Math.Min(hull.Design, designs.Length - 1)];
            var text = rowMin + new float2(Kit.Px(Theme.Space3), Kit.Px(4));
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, text, selected ? Theme.Text : Theme.TextDim, Kit.F($"{design.Name} {(hull.Id & 0xFFFF) + 1}"));
            Kit.Text(
                draw,
                _ui.SansMedium,
                Theme.FontCaption,
                text + new float2(rowSize.X - Kit.Px(70), Kit.Px(2)),
                hull.IsOwn ? Theme.Accent : Theme.TextMute,
                hull.IsOwn ? "YOU"u8
                    : design.Class switch
                    {
                        HullClass.Fighter => "FIGHTER"u8,
                        HullClass.Station => "STATION"u8,
                        _ => "SHIP"u8,
                    }
            );

            if (hull.Order.Kind == OrderKind.Jump || hull.Jump != JumpPhase.None)
            {
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(19)), Theme.Info, "JUMPING"u8);
            }
            else if (hull.Order.Kind == OrderKind.MoveTo)
            {
                var distance = hull.Order.Target.RelativeTo(world.HullPosition(hull)).Length();
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(19)), Theme.Accent, distance >= 1000f ? Kit.F($"MOVING   {distance / 1000f:0.00} KM") : Kit.F($"MOVING   {distance:0} M"));
            }
            else
            {
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(19)), Theme.TextMute, Kit.F($"HOLDING   {hull.Velocity.Length():0} M/S"));
            }

            // Hull and shield at a glance.
            var flight = design.Flight;
            var gaugeMin = rowMin + new float2(rowSize.X - Kit.Px(70), Kit.Px(24));
            var gauge = new float2(Kit.Px(58), Kit.Px(3));
            draw.AddRectFilled(gaugeMin, gaugeMin + gauge, Kit.Color(Theme.Void));
            draw.AddRectFilled(gaugeMin, gaugeMin + new float2(gauge.X * Math.Clamp(hull.Shield / flight.ShieldCapacity, 0f, 1f), gauge.Y), Kit.Color(Theme.Info));
            gaugeMin.Y += Kit.Px(6);
            draw.AddRectFilled(gaugeMin, gaugeMin + gauge, Kit.Color(Theme.Void));
            draw.AddRectFilled(gaugeMin, gaugeMin + new float2(gauge.X * Math.Clamp(hull.Hull / flight.HullPoints, 0f, 1f), gauge.Y), Kit.Color(Theme.Good));
            y += rowHeight;
        }

        Kit.Fade = 1f;
        ImGui.End();
    }

    /// <summary>Pilot view: the hull under the reticle, the targeted hull and its readout.</summary>
    /// <summary>
    /// Pilot view markers that move with the world: brackets on the hull or asteroid under the reticle, and the
    /// marker on the target, which turns into an arrow at the screen's border while the target is out of view.
    /// </summary>
    private void DrawTargetMarkers(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, in HudCueRoom room)
    {
        var designs = snapshot.Content.Designs;
        if (world.HoveredHull != 0 && world.HoveredHull != world.TargetHull && world.TryGetHull(world.HoveredHull, out var hovered) && world.IsDrawn(hovered))
        {
            var design = designs[Math.Min(hovered.Design, designs.Length - 1)];
            if (world.ProjectDisc(world.DrawnPosition(hovered), design.Flight.Radius, out var screen, out var pixels, out _))
            {
                var reach = Math.Max(pixels * 1.3f, Kit.Px(16));
                Brackets(draw, screen - new float2(reach, reach), screen + new float2(reach, reach), Kit.Px(8), Kit.Color(Theme.Text, 0.85f));
                var mine = hovered.Owner == snapshot.PlayerId;
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, screen + new float2(reach + Kit.Px(8), -Kit.Px(7)), mine ? Theme.Accent : Theme.Info, mine ? Kit.F($"{design.Name} {(hovered.Id & 0xFFFF) + 1}") : Kit.F($"PLAYER {hovered.Owner}  {design.Name}"));
            }
        }

        if (world.HoveredRock is { } hoverRock && world.HoveredRock != world.TargetRock && world.TryGetRock(hoverRock, out var hoverPosition, out var hoverRadius) && world.ProjectDisc(hoverPosition, hoverRadius, out var hoverScreen, out var hoverPixels, out _))
            draw.AddCircle(hoverScreen, Math.Max(hoverPixels * 1.05f, Kit.Px(12)), Kit.Color(Theme.Text, 0.6f), 48, Kit.Px(1.2f));

        if (world.TargetHull != 0 && world.TryGetHull(world.TargetHull, out var target) && world.IsDrawn(target))
        {
            var design = designs[Math.Min(target.Design, designs.Length - 1)];
            DrawTargetIndicator(draw, world, room, world.DrawnPosition(target), design.Flight.Radius, target.Owner == snapshot.PlayerId ? Theme.Accent : Theme.Info, false);
        }
        else if (world.TargetRock is { } targetRock && world.TryGetRock(targetRock, out var rockPosition, out var rockRadius))
        {
            DrawTargetIndicator(draw, world, room, rockPosition, rockRadius, Theme.Info, true);
        }
        else
        {
            _indicatorHull = 0;
            _indicatorRock = null;
        }
    }

    /// <summary>
    /// The target's marker. While its point is clear of the panels it is the bracket on the target; behind, or
    /// under a panel, the strip or the console, it is an arrow at the edge of the clear room, on the side the camera
    /// must turn toward. Crossing between the two, the bracket shrinks into the arrow and slides to the edge, and the
    /// arrow grows back into the bracket as it travels in to the target. See <see cref="TargetCue"/>.
    /// </summary>
    private void DrawTargetIndicator(ImDrawListPtr draw, WorldView world, in HudCueRoom room, Double3 position, float radius, uint rgb, bool rock)
    {
        var view = world.ToView(position);
        var across = new float2(view.X, -view.Y);
        var acrossLength = MathF.Sqrt(across.X * across.X + across.Y * across.Y);
        var inFront = world.ProjectDisc(position, radius, out var screen, out var pixels, out _);

        // Close to straight behind, the direction across the view is noise: keep the side already shown.
        var settled = !(view.Z < 0f && acrossLength < -view.Z * 0.1f);
        var fresh = world.TargetHull != _indicatorHull || world.TargetRock != _indicatorRock;
        _indicatorHull = world.TargetHull;
        _indicatorRock = world.TargetRock;
        var morph = _targetCue.Step(room, fresh, inFront, screen, across, settled, Kit.Scale, _ui.DeltaTime);
        var at = _targetCue.At;
        var anchored = !_targetCue.Out;

        if (morph < 1f)
        {
            // The bracket, as it is in view, shrinking to the arrow's size as the arrow takes over.
            var shown = 1f - morph;
            var color = Kit.Color(rgb, shown);
            if (rock)
            {
                var ring = TargetCue.Size(Math.Max(inFront ? pixels * 1.08f : 0f, Kit.Px(14)), Kit.Px(10), room.Reach - Kit.Px(5), anchored, morph);
                draw.AddCircle(at, ring, color, 56, Kit.Px(1.5f));
                var reach = ring + Kit.Px(5);
                Brackets(draw, at - new float2(reach, reach), at + new float2(reach, reach), Kit.Px(9), color);
            }
            else
            {
                var reach = TargetCue.Size(Math.Max(inFront ? pixels * 1.3f : 0f, Kit.Px(18)), Kit.Px(12), MathF.Floor(room.Reach / 1.25f), anchored, morph);
                Brackets(draw, at - new float2(reach, reach), at + new float2(reach, reach), Kit.Px(10), color);
                draw.AddCircle(at, reach * 1.25f, Kit.Color(rgb, 0.5f * shown), 40, Kit.Px(1.2f));
            }
        }

        if (morph > 0f)
        {
            // The arrow: a solid head pointing the way to turn, with a faint ring round the point it turns about.
            var direction = new float2(MathF.Cos(_targetCue.Angle), MathF.Sin(_targetCue.Angle));
            var side = new float2(-direction.Y, direction.X);
            var size = Kit.Px(11);
            var tip = at + direction * size;
            var back = at - direction * (size * 0.45f);
            draw.AddTriangleFilled(tip, back + side * (size * 0.8f), back - side * (size * 0.8f), Kit.Color(rgb, morph));
            draw.AddCircle(at, size * 1.6f, Kit.Color(rgb, 0.4f * morph), 32, Kit.Px(1.2f));
        }
    }

    /// <summary>The readout of the targeted hull or asteroid, top centre.</summary>
    private void DrawTargetReadout(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var previousFade = Kit.Fade;
        Kit.Fade = alpha;
        var designs = snapshot.Content.Designs;
        var display = _ui.DisplaySize;

        if (world.TargetHull != 0 && world.TryGetHull(world.TargetHull, out var target))
        {
            var design = designs[Math.Min(target.Design, designs.Length - 1)];
            var mine = target.Owner == snapshot.PlayerId;
            var color = mine ? Theme.Accent : Theme.Info;
            var size = new float2(Kit.Px(HudLayout.TargetReadoutWidth), Kit.Px(HudLayout.TargetReadoutHeight));
            var min = new float2((display.X - size.X) * 0.5f, Kit.Px(Theme.Space5));
            Kit.PanelFrame(draw, min, min + size);
            var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, color, mine ? "TARGET  -  YOURS"u8 : "TARGET"u8);
            Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text + new float2(0, Kit.Px(16)), Theme.Text, mine ? Kit.F($"{design.Name} {(target.Id & 0xFFFF) + 1}") : Kit.F($"Player {target.Owner}  {design.Name}"));
            var range = target.Position.RelativeTo(world.OwnPosition).Length();
            var rangeText = range >= 1000f ? Kit.F($"{range / 1000f:0.00} KM") : Kit.F($"{range:0} M");
            var rangeSize = Kit.Measure(_ui.Mono, Theme.FontCaption, rangeText);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + size.X - rangeSize.X - Kit.Px(Theme.Space4), text.Y + Kit.Px(20)), Theme.TextDim, rangeText);

            var flight = design.Flight;
            var gauge = new float2(size.X - Kit.Px(Theme.Space4 * 2), Kit.Px(5));
            var gaugeMin = text + new float2(0, Kit.Px(48));
            draw.AddRectFilled(gaugeMin, gaugeMin + gauge, Kit.Color(Theme.Void));
            draw.AddRectFilled(gaugeMin, gaugeMin + new float2(gauge.X * Math.Clamp(target.Shield / flight.ShieldCapacity, 0f, 1f), gauge.Y), Kit.Color(Theme.Info));
            gaugeMin.Y += Kit.Px(10);
            draw.AddRectFilled(gaugeMin, gaugeMin + gauge, Kit.Color(Theme.Void));
            draw.AddRectFilled(gaugeMin, gaugeMin + new float2(gauge.X * Math.Clamp(target.Hull / flight.HullPoints, 0f, 1f), gauge.Y), Kit.Color(Theme.Good));
            Kit.Text(draw, _ui.Sans, Theme.FontCaption, gaugeMin + new float2(0, Kit.Px(10)), Theme.TextMute, mine ? "T takes control"u8 : "Not yours: cannot take control"u8);
        }
        else if (world.TargetRock is { } targetRock && world.TryGetRock(targetRock, out var rockPosition, out var rockRadius))
        {
            var size = new float2(Kit.Px(HudLayout.TargetReadoutWidth), Kit.Px(92));
            var min = new float2((display.X - size.X) * 0.5f, Kit.Px(Theme.Space5));
            Kit.PanelFrame(draw, min, min + size);
            var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
            var mass = world.Galaxy!.AsteroidDensity * 4.18879f * MathF.Pow(rockRadius * RockField.SolidFraction, 3);
            var range = rockPosition.RelativeTo(world.OwnPosition).Length();
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, Theme.Info, "TARGET"u8);
            Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text + new float2(0, Kit.Px(16)), Theme.Text, RockTitle(world, targetRock));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(44)), Theme.TextDim, mass >= 1e6f ? Kit.F($"DIAMETER {rockRadius * 2:0} M   {mass / 1e6f:0.00} MT") : Kit.F($"DIAMETER {rockRadius * 2:0} M   {mass / 1e3f:0.0} KT"));
            var rangeText = range >= 1000f ? Kit.F($"{range / 1000f:0.00} KM") : Kit.F($"{range:0} M");
            var rangeSize = Kit.Measure(_ui.Mono, Theme.FontCaption, rangeText);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + size.X - rangeSize.X - Kit.Px(Theme.Space4), text.Y + Kit.Px(20)), Theme.TextDim, rangeText);
        }

        if (world.ControlDenied)
        {
            var notice = "That hull is not yours to fly"u8;
            var noticeSize = Kit.Measure(_ui.SansMedium, Theme.FontSmall, notice);
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, new float2((display.X - noticeSize.X) * 0.5f, display.Y * 0.5f + Kit.Px(60)), Theme.Bad, notice);
        }

        Kit.Fade = previousFade;
    }

    private const float CharacterLine = 20f;

    /// <summary>Width of the label column the map's readouts share: fourteen characters of the caption font.</summary>
    private float CharacterLabel => Kit.Measure(_ui.Mono, Theme.FontCaption, "RESOURCES     "u8).X;

    /// <summary>How tall <see cref="DrawSectorCharacter"/> draws for a sector, in screen pixels.</summary>
    private float SectorCharacterHeight(Galaxy galaxy, in SectorCharacter character, float width) =>
        Kit.Px(CharacterLine) * (1 + ResourceLines(default, default, width, galaxy, character, false));

    /// <summary>
    /// A sector's character in words: how rich it is, and each resource that occurs there with its share of the
    /// sector's ore.
    /// </summary>
    private void DrawSectorCharacter(ImDrawListPtr draw, float2 at, float width, Galaxy galaxy, in SectorCharacter character)
    {
        // From barren (muted) through the ordinary levels to the richest (accent).
        var top = galaxy.RichnessLevels.Length - 1;
        var levelColor = character.Level == 0 ? Theme.TextMute : Kit.Mix(Theme.Text, Theme.Accent, top > 1 ? (character.Level - 1f) / (top - 1f) : 1f);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, "RICHNESS"u8);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(CharacterLabel, 0), levelColor, galaxy.RichnessLevels[character.Level].Name);
        ResourceLines(draw, at + new float2(0, Kit.Px(CharacterLine)), width, galaxy, character, true);
    }

    /// <summary>
    /// Lays the sector's resources out as a run of entries, a dot in the resource's colour, its name and its share,
    /// wrapping at <paramref name="width"/>. Returns the number of lines; draws only when asked to.
    /// </summary>
    private int ResourceLines(ImDrawListPtr draw, float2 at, float width, Galaxy galaxy, in SectorCharacter character, bool paint)
    {
        var label = CharacterLabel;
        if (paint)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, "RESOURCES"u8);
        if (character.Resources == 0)
        {
            if (paint)
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(label, 0), Theme.TextMute, "none"u8);
            return 1;
        }

        var lines = 1;
        var x = label;
        var dot = Kit.Px(11);
        var gap = Kit.Px(Theme.Space3);
        for (var i = 0; i < galaxy.Resources.Length; i++)
        {
            if (!character.Has(i))
                continue;
            var entry = Kit.F($"{galaxy.Resources[i].Name} {character.Shares[i] * 100f:0}%");
            var entryWidth = dot + Kit.Measure(_ui.Mono, Theme.FontCaption, entry).X;
            if (x > label && x + entryWidth > width)
            {
                x = label;
                lines++;
            }

            if (paint)
            {
                var line = at + new float2(x, Kit.Px(CharacterLine) * (lines - 1));
                draw.AddCircleFilled(line + new float2(Kit.Px(3), Kit.Px(7)), Kit.Px(3), Kit.Color(StarColor(galaxy.Resources[i].Color)), 12);
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, line + new float2(dot, 0), i == character.Main ? Theme.Text : Theme.TextDim, entry);
            }

            x += entryWidth + gap;
        }

        return lines;
    }

    /// <summary>What to call an asteroid in a readout.</summary>
    private static ReadOnlySpan<byte> RockTitle(WorldView world, RockRef rock) =>
        world.TryGetRockOre(rock, out var resource, out var grade) && resource >= 0
            ? Kit.F($"{world.Galaxy!.Resources[resource].Name} asteroid   {grade * 100:0}% grade")
            : "Bare rock asteroid"u8;

    /// <summary>An asteroid's data sheet.</summary>
    private void DrawRockInfo(ImDrawListPtr draw, WorldView world, RockRef rock)
    {
        if (!world.TryGetRock(rock, out var position, out var radius))
            return;

        var galaxy = world.Galaxy!;
        var line = Kit.Px(21);
        var size = new float2(Kit.Px(400), Kit.Px(Theme.Space5 * 2 + 46) + line * 10);
        var min = new float2(_ui.DisplaySize.X - size.X - Kit.Px(300 + Theme.Space5 * 2), (_ui.DisplaySize.Y - size.Y) * 0.5f);
        Kit.PanelFrame(draw, min, min + size);

        var left = min.X + Kit.Px(Theme.Space5);
        var right = min.X + size.X - Kit.Px(Theme.Space5);
        var y = min.Y + Kit.Px(Theme.Space5);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(left, y), Theme.Info, "SURVEY"u8);
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, new float2(left, y + Kit.Px(16)), Theme.Text, "Asteroid"u8);
        y += Kit.Px(60);

        void Row(ImString label, ImString value)
        {
            Kit.Text(draw, _ui.Sans, Theme.FontSmall, new float2(left, y), Theme.TextDim, label);
            var valueSize = Kit.Measure(_ui.Mono, Theme.FontSmall, value);
            Kit.Text(draw, _ui.Mono, Theme.FontSmall, new float2(right - valueSize.X, y), Theme.Text, value);
            y += line;
        }

        var mass = galaxy.AsteroidDensity * 4.18879f * MathF.Pow(radius * RockField.SolidFraction, 3);
        var range = position.RelativeTo(world.OwnPosition).Length();
        if (world.TryGetRockOre(rock, out var resource, out var grade) && resource >= 0)
        {
            Row("Resource"u8, galaxy.Resources[resource].Name);
            Row("Grade"u8, Kit.F($"{grade * 100:0} %"));
        }
        else
        {
            Row("Resource"u8, "None (bare rock)"u8);
        }

        Row("Sector"u8, Kit.F($"{rock.Sector.X} {rock.Sector.Y} {rock.Sector.Z}"));
        Row("Catalogue number"u8, Kit.F($"{rock.Index + 1}"));
        if (galaxy.AsteroidFields is { } fields)
            Row("Size"u8, fields.SizeName(radius));
        Row("Diameter"u8, radius >= 500f ? Kit.F($"{radius * 2 / 1000f:0.00} km") : Kit.F($"{radius * 2:0} m"));
        Row("Mass"u8, mass >= 1e6f ? Kit.F($"{mass / 1e6f:0.00} Mt") : Kit.F($"{mass / 1e3f:0.0} kt"));
        Row("Drift"u8, Kit.F($"{world.RockDrift(rock):0.00} m/s"));
        Row("Distance from pod"u8, range >= 1000f ? Kit.F($"{range / 1000f:0.00} km") : Kit.F($"{range:0} m"));
    }

    /// <summary>The hull's data sheet: every figure follows from the blocks it is built from.</summary>
    private void DrawShipInfo(ImDrawListPtr draw, HullDesign design, ulong blocks, bool besideFleetList)
    {
        // The sheet is cached per hull state; it only needs working out again when the hull loses blocks.
        if (!ReferenceEquals(design, _sheetDesign) || blocks != _sheetBlocks)
        {
            _sheetDesign = design;
            _sheetBlocks = blocks;
            design.Evaluate(blocks, out _, out _sheetStats);
        }

        var stats = _sheetStats;
        var line = Kit.Px(21);
        var size = new float2(Kit.Px(400), Kit.Px(Theme.Space5 * 2 + 46) + line * 25);
        var inset = besideFleetList ? Kit.Px(300 + Theme.Space5 * 2) : Kit.Px(Theme.Space5);
        var min = new float2(_ui.DisplaySize.X - size.X - inset, (_ui.DisplaySize.Y - size.Y) * 0.5f);
        Kit.PanelFrame(draw, min, min + size);

        var left = min.X + Kit.Px(Theme.Space5);
        var right = min.X + size.X - Kit.Px(Theme.Space5);
        var y = min.Y + Kit.Px(Theme.Space5);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(left, y), Theme.Accent, "SHIP DATA"u8);
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, new float2(left, y + Kit.Px(16)), Theme.Text, design.Name);
        y += Kit.Px(60);

        void Row(ImString label, ImString value, uint rgb = Theme.Text)
        {
            Kit.Text(draw, _ui.Sans, Theme.FontSmall, new float2(left, y), Theme.TextDim, label);
            var valueSize = Kit.Measure(_ui.Mono, Theme.FontSmall, value);
            Kit.Text(draw, _ui.Mono, Theme.FontSmall, new float2(right - valueSize.X, y), rgb, value);
            y += line;
        }

        Row("Hull"u8, Kit.F($"{stats.HullPoints:0} hp"));
        Row("Shield"u8, Kit.F($"{stats.ShieldCapacity:0} hp"));
        Row("Shield recharge"u8, Kit.F($"{stats.ShieldRecharge:0} hp/s"));
        Row("Mass"u8, Kit.F($"{stats.Mass:0.0} t"));
        Row("Volume"u8, Kit.F($"{stats.Volume:0} m³"));
        Row("W x H x L"u8, Kit.F($"{stats.Size.X:0} x {stats.Size.Y:0} x {stats.Size.Z:0} m"));
        Row("Blocks"u8, stats.Blocks == design.Cells.Length ? Kit.F($"{stats.Blocks}") : Kit.F($"{stats.Blocks} of {design.Cells.Length}"), stats.Blocks == design.Cells.Length ? Theme.Text : Theme.Bad);
        Row("Cargo hold"u8, Kit.F($"{stats.CargoHold:0.0} m³"));
        Row("Crew"u8, Kit.F($"{stats.Crew}"));
        y += line * 0.5f;

        Row("Acceleration"u8, Kit.F($"{stats.Acceleration:0.0} m/s²"));
        Row("Deceleration"u8, Kit.F($"{stats.Deceleration:0.0} m/s²"));
        Row("Sideways and vertical"u8, Kit.F($"{stats.LateralAcceleration:0.0} m/s²"));
        Row("Max velocity"u8, Kit.F($"{stats.MaxSpeed:0} m/s"));
        Row("Max velocity, boost"u8, Kit.F($"{stats.BoostMaxSpeed:0} m/s"));
        Row("Yaw (left / right)"u8, Kit.F($"{stats.TurnRate.Y:0.00} rad/s"));
        Row("Pitch (up / down)"u8, Kit.F($"{stats.TurnRate.X:0.00} rad/s"));
        Row("Roll"u8, Kit.F($"{stats.TurnRate.Z:0.00} rad/s"));
        y += line * 0.5f;

        // The margin between what the generators make and what everything running at once would draw.
        var margin = stats.PowerGenerated - stats.PowerRequired;
        Row("Req. energy"u8, Kit.F($"{stats.PowerRequired:0.00} MW"));
        Row("Generated energy"u8, Kit.F($"{stats.PowerGenerated:0.00} MW"));
        Row("Energy margin"u8, Kit.F($"{margin:+0.00;-0.00} MW"), margin < 0 ? Theme.Bad : Theme.Good);
        Row("Storable energy"u8, Kit.F($"{stats.EnergyStorage:0} MJ"));
        Row(
            "FTL drive"u8,
            stats.Ftl switch
            {
                FtlKind.Hyperspace => "Hyperspace"u8,
                FtlKind.Wormhole => "Wormhole"u8,
                FtlKind.Rift => "Rift"u8,
                _ => "None"u8,
            },
            stats.Ftl == FtlKind.None ? Theme.TextMute : Theme.Info
        );
        if (stats.Ftl != FtlKind.None)
            Row("Jump charge"u8, Kit.F($"{stats.FtlEnergy:0} MJ at {stats.FtlPower:0.0} MW"));
    }

    private static void DrawTargetMarker(ImDrawListPtr draw, float2 center, uint rgb)
    {
        var r = Kit.Px(8);
        var color = Kit.Color(rgb);
        draw.AddCircle(center, r, color, 20, Kit.Px(1.5f));
        draw.AddLine(center - new float2(r * 1.7f, 0), center - new float2(r * 0.5f, 0), color, Kit.Px(1.5f));
        draw.AddLine(center + new float2(r * 0.5f, 0), center + new float2(r * 1.7f, 0), color, Kit.Px(1.5f));
        draw.AddLine(center - new float2(0, r * 1.7f), center - new float2(0, r * 0.5f), color, Kit.Px(1.5f));
        draw.AddLine(center + new float2(0, r * 0.5f), center + new float2(0, r * 1.7f), color, Kit.Px(1.5f));
    }

    /// <summary>
    /// The galaxy map. Every grid cell is a sector. Far out it shows the whole galaxy; closer in, the grid and the
    /// individual sectors appear; closest, their stars, asteroid fields and names. What a sector holds is only
    /// shown once one of the player's hulls has been near it. Drag to pan, wheel to zoom at the pointer, Page Up
    /// and Page Down to change layer, Space to centre on the player, right-click a sector for the course menu.
    /// </summary>
    private void DrawMap(SessionSnapshot snapshot, WorldView world)
    {
        var galaxy = world.Galaxy!;
        if (!ReferenceEquals(galaxy, _mapGalaxy))
        {
            _mapGalaxy = galaxy;
            Array.Clear(_mapCacheValid);
        }

        _mapTime += _ui.DeltaTime;
        var fade = Kit.EaseOut(_mapTime / Theme.ScreenEnter);
        Kit.Fade = fade;
        var display = _ui.DisplaySize;
        var io = ImGui.GetIO();

        // The unit list's place is decided before any input is read, so the map never takes a press, wheel or
        // drag that lands on it, even on the frame it first appears.
        world.PruneSelection();
        var units = LayoutMapUnits(snapshot, world);
        _mapPointer.Block(units.Min, units.Size);
        if (_mapMenuOpen)
        {
            _mapMenuTime += _ui.DeltaTime;
            _mapPointer.Block(MapCourseMenu.Shown(_mapMenuMin, _mapMenuTime, Kit.Scale), _mapMenuSize);
        }

        _mapPointer.Begin(display);
        var pointer = _mapPointer;
        var draw = ImGui.GetWindowDrawList();
        var shape = galaxy.Shape;
        var here = world.Sector.Coord;
        var size = galaxy.SectorSize;
        var player = new float2((float)(world.OwnPosition.X / size), (float)(world.OwnPosition.Z / size));
        var middle = display * 0.5f;
        var mouse = _mapHoverPinned ? middle : io.MousePos;

        // The course menu, while open, holds the map still and takes the clicks that land on it.
        var menuOpen = _mapMenuOpen;

        // Input. W A S D, the arrow keys and middle-drag pan; zooming keeps the sector under the pointer in place.
        if (!menuOpen)
            PanMap(pointer, middle, mouse, here, player);

        // Left button: click picks one of the player's hulls, drag draws a selection box around several. With the
        // menu open a click on the map only closes it; a click on the menu or the unit list is theirs.
        if (pointer.Pressed && menuOpen)
        {
            _mapMenuOpen = false;
            pointer.CancelBox();
        }

        var boxReleased = pointer.BoxReleased;

        // Escape closes the menu first and the map only after that.
        if (BackPressed())
        {
            if (_mapMenuOpen)
                _mapMenuOpen = false;
            else
                _mapOpen = false;
        }

        var cell = _mapCell;
        var center = _mapCenter;
        float2 ToScreen(float sectorX, float sectorZ) => middle + new float2((sectorX - center.X) * cell, (sectorZ - center.Y) * cell);

        // Backdrop: black, the galaxy, and a field of far stars that slides more slowly than the map.
        draw.AddRectFilled(float2.Zero, display, Kit.Color(0x020305));
        var reach = shape.Radius * Render.GalaxyImage.Extent;
        var galaxyAlpha = Math.Clamp(1.1f - cell / 60f, 0.22f, 1f) * shape.LayerFalloff(_mapLayer);
        draw.AddImage(
            _app.GalaxyImageRef,
            ToScreen(shape.Center.X - reach, shape.Center.Z - reach),
            ToScreen(shape.Center.X + reach, shape.Center.Z + reach),
            float2.Zero,
            float2.One,
            Kit.Color(0xFFFFFF, galaxyAlpha)
        );

        const float tile = 150f;
        var slide = center * (cell * 0.3f);
        var firstTileX = (int)MathF.Floor(slide.X / tile);
        var firstTileY = (int)MathF.Floor(slide.Y / tile);
        for (var ty = firstTileY; (ty - firstTileY) * tile < display.Y + tile; ty++)
            for (var tx = firstTileX; (tx - firstTileX) * tile < display.X + tile; tx++)
            {
                var hash = (uint)(tx * 73856093 ^ ty * 19349663);
                for (var n = 0; n < 2; n++)
                {
                    hash = (hash ^ hash >> 15) * 2246822519u + 0x9E3779B9u;
                    var position = new float2((tx + (hash & 0xFFFF) / 65535f) * tile - slide.X, (ty + (hash >> 16) / 65535f) * tile - slide.Y);
                    var glint = 0.15f + 0.45f * ((hash >> 8 & 0xFF) / 255f);
                    draw.AddRectFilled(position, position + new float2(1.5f, 1.5f), Kit.Color(0xC8D6F0, glint));
                }
            }

        // Grid. Sector lines fade in as the cells grow; every tenth and hundredth line is always the stronger.
        var left = center.X - middle.X / cell;
        var top = center.Y - middle.Y / cell;
        var right = center.X + middle.X / cell;
        var bottom = center.Y + middle.Y / cell;
        DrawMapGrid(draw, display, left, top, right, bottom, 1, Math.Clamp((cell - 9f) / 26f, 0f, 1f) * 0.2f, ToScreen(0, 0), cell);
        DrawMapGrid(draw, display, left, top, right, bottom, 10, Math.Clamp((cell * 10f - 30f) / 120f, 0f, 1f) * 0.34f, ToScreen(0, 0), cell);
        DrawMapGrid(draw, display, left, top, right, bottom, 100, Math.Clamp((cell * 100f - 30f) / 200f, 0f, 1f) * 0.4f, ToScreen(0, 0), cell);

        var layerFalloff = shape.LayerFalloff(_mapLayer);
        var fullSignal = galaxy.MapFullSignalAsteroids;
        if (cell >= MapSectorDetailCell)
        {
            // Close enough that every sector on screen can be drawn for what it is.
            var firstX = (int)MathF.Floor(left + 0.5f);
            var lastX = (int)MathF.Floor(right + 0.5f);
            var firstZ = (int)MathF.Floor(top + 0.5f);
            var lastZ = (int)MathF.Floor(bottom + 0.5f);
            for (var z = firstZ; z <= lastZ; z++)
                for (var x = firstX; x <= lastX; x++)
                {
                    var coord = new SectorCoord(x, _mapLayer, z);
                    ref var summary = ref MapSummary(galaxy, coord);
                    if (summary.AsteroidCount == 0)
                        continue;

                    var known = _discovered.Contains(coord);
                    var signal = Math.Min(1f, summary.AsteroidCount / fullSignal);

                    // Uncharted space shows only its stronger signals, and only once the cells are large enough.
                    if (!known && (signal < 0.3f || (cell < 34f && signal < 0.55f)))
                        continue;
                    var position = ToScreen(x, z);
                    var star = StarColor(galaxy.StarClasses[summary.StarClass].Color);

                    // Something is there whether or not it has been visited; what it is shows only once discovered.
                    var tint = known ? star : Kit.Mix(star, 0x66727F, 0.75f);
                    var glow = Math.Min(cell * (0.12f + 0.5f * signal * signal), 52f);
                    if (known)
                    {
                        draw.AddCircleFilled(position, glow, Kit.Color(tint, 0.08f), 16);
                        draw.AddCircleFilled(position, glow * 0.5f, Kit.Color(tint, 0.16f), 12);
                        draw.AddCircleFilled(position, Math.Max(1.5f, glow * 0.16f), Kit.Color(tint, 0.95f), 8);
                    }
                    else
                    {
                        draw.AddCircleFilled(position, glow, Kit.Color(tint, 0.06f), 16);
                        draw.AddCircleFilled(position, Math.Max(1.2f, glow * 0.12f), Kit.Color(tint, 0.4f), 8);
                    }

                    if (!known || cell < 64f)
                        continue;

                    var sweep = MathF.Tau * Math.Clamp(signal, 0.08f, 1f);
                    draw.PathArcTo(position, cell * 0.2f, -MathF.PI / 2, -MathF.PI / 2 + sweep, 28);
                    draw.PathStroke(Kit.Color(Theme.Info, 0.75f), ImDrawFlags.None, 1.5f);
                    if (cell >= 118f)
                    {
                        ref var name = ref MapName(galaxy, coord);
                        var nameSize = Kit.Measure(_ui.Sans, Theme.FontCaption, name);
                        Kit.Text(draw, _ui.Sans, Theme.FontCaption, new float2(position.X - nameSize.X * 0.5f, position.Y + cell * 0.27f), Theme.TextDim, name);
                    }
                }
        }
        else if (cell >= 2.5f)
        {
            // Too many sectors on screen to draw each: sample the galaxy's density instead, so the map still
            // glitters where the rock is.
            var step = (int)MathF.Ceiling(MapSectorDetailCell / cell);
            var firstX = (int)MathF.Floor(left / step) * step;
            var firstZ = (int)MathF.Floor(top / step) * step;
            for (var z = firstZ; z <= bottom; z += step)
                for (var x = firstX; x <= right; x += step)
                {
                    var density = shape.Density(x, z) * layerFalloff;
                    var hash = (uint)(x * 73856093 ^ z * 19349663 ^ _mapLayer * 83492791);
                    hash = (hash ^ hash >> 15) * 2246822519u;
                    var chance = (hash >> 8 & 0xFFFF) / 65535f;
                    if (chance > density * 0.8f)
                        continue;

                    var jitter = new float2(((hash & 0xFF) / 255f - 0.5f) * step, ((hash >> 24) / 255f - 0.5f) * step);
                    var position = ToScreen(x + jitter.X, z + jitter.Y);
                    var tint = Kit.Mix(0xFFD9A0, 0x9FC4FF, (hash >> 12 & 0xFF) / 255f);
                    var glow = step * cell * (0.25f + 0.5f * density);
                    draw.AddCircleFilled(position, glow, Kit.Color(tint, 0.08f), 10);
                    draw.AddCircleFilled(position, Math.Max(1f, glow * 0.22f), Kit.Color(tint, 0.6f), 6);
                }
        }

        // The player's hulls on this layer. Releasing the left button selects among them: the nearest to a click,
        // or all inside a dragged box.
        var boxMin = new float2(Math.Min(pointer.BoxStart.X, mouse.X), Math.Min(pointer.BoxStart.Y, mouse.Y));
        var boxMax = new float2(Math.Max(pointer.BoxStart.X, mouse.X), Math.Max(pointer.BoxStart.Y, mouse.Y));
        var additive = io.KeyShift || io.KeyCtrl;
        if (boxReleased && !additive)
            world.ClearSelection();
        var clicked = 0u;
        var clickedDistance = Kit.Px(14) * Kit.Px(14);
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var position = world.HullPosition(hull);
            if (hull.Owner != snapshot.PlayerId || galaxy.SectorOf(position).Y != _mapLayer)
                continue;

            var at = ToScreen((float)(position.X / size), (float)(position.Z / size));
            if (boxReleased && pointer.BoxMoved && at.X >= boxMin.X && at.X <= boxMax.X && at.Y >= boxMin.Y && at.Y <= boxMax.Y)
                world.AddToSelection(hull.Id);
            var offset = at - mouse;
            if (boxReleased && !pointer.BoxMoved && offset.X * offset.X + offset.Y * offset.Y < clickedDistance)
            {
                clicked = hull.Id;
                clickedDistance = offset.X * offset.X + offset.Y * offset.Y;
            }

            if (world.IsSelected(hull.Id))
                draw.AddCircle(at, Kit.Px(9), Kit.Color(Theme.Text), 20, Kit.Px(1.5f));
            if (!hull.IsOwn)
                draw.AddCircleFilled(at, Kit.Px(3), Kit.Color(Theme.Accent, 0.9f), 10);
        }

        if (clicked != 0)
            world.Select(clicked, additive);
        if (pointer.Boxing && pointer.BoxMoved)
        {
            draw.AddRectFilled(boxMin, boxMax, Kit.Color(Theme.Accent, 0.08f));
            draw.AddRect(boxMin, boxMax, Kit.Color(Theme.Accent, 0.9f));
        }

        // The hull being flown, as an arrow along its heading.
        if (_mapLayer == here.Y)
        {

            var marker = ToScreen(player.X, player.Y);
            var heading = Vector3.Transform(-Vector3.UnitZ, world.OwnOrientation);
            var flat = new float2(heading.X, heading.Z);
            var length = MathF.Sqrt(flat.X * flat.X + flat.Y * flat.Y);
            flat = length > 0.01f ? flat / length : new float2(0, -1);
            var side = new float2(-flat.Y, flat.X);
            var s = Kit.Px(8);
            draw.AddTriangleFilled(marker + flat * s * 1.4f, marker - flat * s * 0.7f + side * s * 0.8f, marker - flat * s * 0.7f - side * s * 0.8f, Kit.Color(Theme.Accent));
            if (cell >= MapSectorDetailCell)
            {
                var corner = new float2(cell * 0.5f - 2, cell * 0.5f - 2);
                var home = ToScreen(here.X, here.Z);
                Brackets(draw, home - corner, home + corner, Math.Min(Kit.Px(10), cell * 0.3f), Kit.Color(Theme.Accent));
            }
        }

        // The plotted jump: a dotted route from the hull being flown to the NAV point, drawn from the moment it is
        // plotted until it is cleared, replaced or reached, whatever the hull's order or the selection.
        if (world.NavTarget is { } nav)
        {
            var navSector = galaxy.SectorOf(nav);
            var end = ToScreen((float)(nav.X / size), (float)(nav.Z / size));
            if (MapRoute.Shows(here.Y, navSector.Y, _mapLayer, out var line))
            {
                if (line)
                    MapRoute.Dotted(draw, ToScreen(player.X, player.Y), end, new HudRect(0f, 0f, display.X, display.Y), Kit.Px(MapRoute.Dash), Kit.Px(MapRoute.Space), Kit.Color(Theme.Info, 0.8f), Kit.Px(1.5f));
                DrawTargetMarker(draw, end, Theme.Info);
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, end + new float2(Kit.Px(18), -Kit.Px(7)), Theme.Info, "NAV"u8);
            }
        }

        // A standing order's destination.
        var order = world.Own.Order;
        if (order.Kind != OrderKind.None && galaxy.SectorOf(order.Target).Y == _mapLayer)
        {
            var destination = ToScreen((float)(order.Target.X / size), (float)(order.Target.Z / size));
            draw.AddLine(ToScreen(player.X, player.Y), destination, Kit.Color(Theme.Accent, 0.5f));
            DrawTargetMarker(draw, destination, Theme.Accent);
        }

        // The sector under the pointer.
        var hovered = _mapCenter + (mouse - middle) / cell;
        var hover = new SectorCoord((int)MathF.Floor(hovered.X + 0.5f), _mapLayer, (int)MathF.Floor(hovered.Y + 0.5f));
        var pointerOnMap = _mapHoverPinned || pointer.Hovered;
        if (pointerOnMap)
        {
            var at = ToScreen(hover.X, hover.Z);
            var corner = new float2(Math.Max(cell * 0.5f, 7f), Math.Max(cell * 0.5f, 7f));
            Brackets(draw, at - corner, at + corner, Math.Clamp(cell * 0.3f, 3f, Kit.Px(10)), Kit.Color(Theme.Text, 0.85f));
            if (!_mapMenuOpen)
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, mouse + new float2(Kit.Px(18), -Kit.Px(6)), Theme.Text, Kit.F($"{hover.X} : {hover.Z}"));

            // Right-click opens the course menu on the sector, or moves it there if it is already open.
            if (pointer.RightClicked)
                OpenMapMenu(galaxy, hover, mouse);
        }

        // The sector the course menu is for.
        if (_mapMenuOpen && _mapMenuSector.Y == _mapLayer)
        {
            var at = ToScreen(_mapMenuSector.X, _mapMenuSector.Z);
            var corner = new float2(Math.Max(cell * 0.5f, 7f), Math.Max(cell * 0.5f, 7f));
            Brackets(draw, at - corner, at + corner, Math.Clamp(cell * 0.3f, 3f, Kit.Px(10)), Kit.Color(Theme.Accent));
        }

        // The world interface is beneath the map, so the map shows the sector panel itself.
        DrawSectorPanel(draw, snapshot, world);

        // Title, layer and scale, clear of the sector panel above them. Where the unit list needs the room, only
        // the layer and scale stay, on one line under the sector panel.
        if (units.ShortHeading)
        {
            var line = new float2(units.Min.X + Kit.Px(Theme.Space4), units.Min.Y - Kit.Px(28));
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, line, Theme.TextDim, Kit.F($"LAYER {_mapLayer:+0;-0;0}   {display.X / cell:0} SECTORS ACROSS   {size / 1000:0} KM EACH"), units.Size.X - Kit.Px(Theme.Space4) * 2);
        }
        else
        {
            // In the column the sector panel and the unit list share, its text on their text's edge.
            var at = new float2(Kit.Px(HudLayout.Margin + Theme.Space4), MapUnitList.HeadingTop(Kit.Px(Theme.Space5) + SectorPanelHeight(world, false), Kit.Scale));
            var width = units.Size.X - Kit.Px(Theme.Space4) * 2;
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.Accent, "NAVIGATION"u8);
            Kit.Text(draw, _ui.SansBold, Theme.FontHeading, at + new float2(0, Kit.Px(16)), Theme.Text, "Galaxy Map"u8);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(50)), Theme.TextDim, Kit.F($"LAYER {_mapLayer:+0;-0;0}   {display.X / cell:0} SECTORS ACROSS   {size / 1000:0} KM EACH"), width);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(68)), Theme.TextMute, Kit.F($"{_discovered.Count} SECTORS DISCOVERED   {world.Selection.Length} SHIPS SELECTED"), width);
        }

        if (pointerOnMap)
            DrawMapHoverPanel(draw, snapshot, world, galaxy, hover, here);

        EndRoot();

        DrawMapUnits(snapshot, world, galaxy, units, fade);
        if (_mapMenuOpen)
            DrawMapMenu(world, galaxy, fade);
        Kit.Fade = 1f;
    }

    /// <summary>Pans, zooms and changes layer from the keyboard, and from the map's own middle drag and wheel.</summary>
    private void PanMap(MapPointer pointer, float2 middle, float2 mouse, SectorCoord here, float2 player)
    {
        var devices = _app.Devices;
        var actions = _app.Actions;
        var display = _ui.DisplaySize;
        var panX = Math.Clamp(actions.Axis(devices, InputAxis.Strafe, true) + actions.Axis(devices, InputAxis.Yaw, true), -1f, 1f);
        var panY = Math.Clamp(actions.Axis(devices, InputAxis.Forward, true) + actions.Axis(devices, InputAxis.Pitch, true), -1f, 1f);
        _mapCenter += new float2(panX, -panY) * (display.Y * 0.9f / _mapCell * _ui.DeltaTime);
        _mapCenter -= pointer.Pan / _mapCell;
        if (pointer.Wheel != 0)
        {
            var under = _mapCenter + (mouse - middle) / _mapCell;
            _mapCell = Math.Clamp(_mapCell * MathF.Pow(1.18f, pointer.Wheel), MapMinCell, MapMaxCell);
            _mapCenter = under - (mouse - middle) / _mapCell;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.PageUp))
            _mapLayer++;
        if (ImGui.IsKeyPressed(ImGuiKey.PageDown))
            _mapLayer--;
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
        {
            _mapCenter = player;
            _mapLayer = here.Y;
        }
    }

    /// <summary>Opens the course menu for a sector at a screen point, kept fully on screen.</summary>
    private void OpenMapMenu(Galaxy galaxy, SectorCoord sector, float2 at)
    {
        _mapMenuSize = MapCourseMenu.Size(Kit.Scale);
        var min = MapCourseMenu.Place(at, _mapMenuSize, _ui.DisplaySize, Kit.Scale);

        if (!_mapMenuOpen || _mapMenuSector != sector)
            _mapMenuTime = 0;

        // The name is looked up once here, not every frame the menu is drawn.
        _mapMenuName = _discovered.Contains(sector) ? MapName(galaxy, sector) : "Unexplored";
        _mapMenuMin = min;
        _mapMenuSector = sector;
        _mapMenuOpen = true;
    }

    /// <summary>
    /// Opens the map with the course menu on a sector offset from the player's, at that sector's place on screen.
    /// Used by the scripted <c>--open map-menu</c> view.
    /// </summary>
    public void OpenMapMenuNear(int dx, int dz)
    {
        OpenMap();
        var world = _app.World;
        var galaxy = world.Galaxy!;
        var sector = world.Sector.Coord.Offset(dx, 0, dz);
        var player = new float2((float)(world.OwnPosition.X / galaxy.SectorSize), (float)(world.OwnPosition.Z / galaxy.SectorSize));
        OpenMapMenu(galaxy, sector, _ui.DisplaySize * 0.5f + (new float2(sector.X, sector.Z) - player) * _mapCell);
    }

    /// <summary>The hover card's width, in unscaled pixels.</summary>
    private const float MapHoverWidth = 340f;

    /// <summary>
    /// The course menu: where the sector is, then the two ways to get there, for exactly the hulls selected in the
    /// unit list. Moving needs a selection; jumping also needs a selected ship with a working drive that is not
    /// already in that sector. With nothing selected both are off, and nothing is ordered.
    /// </summary>
    private void DrawMapMenu(WorldView world, Galaxy galaxy, float fade)
    {
        // Where the map blocked it this frame.
        var appear = MapCourseMenu.Appear(_mapMenuTime);
        var min = MapCourseMenu.Shown(_mapMenuMin, _mapMenuTime, Kit.Scale);
        var size = _mapMenuSize;
        MapCourseMenu.Begin(min, size, _mapPointer);
        Kit.Fade = fade * appear;
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);

        var sector = _mapMenuSector;
        var target = galaxy.CenterOf(sector);
        var known = _discovered.Contains(sector);
        var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, Theme.Accent, "SET COURSE"u8);
        var coords = Kit.F($"{sector.X} : {sector.Z}   LAYER {sector.Y:+0;-0;0}");
        var coordsWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, coords).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + size.X - Kit.Px(Theme.Space4) - coordsWidth, text.Y), Theme.TextMute, coords);
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text + new float2(0, Kit.Px(20)), known ? Theme.Text : Theme.TextDim, _mapMenuName);
        var range = target.RelativeTo(world.OwnPosition).Length() / 1000f;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(46)), Theme.TextDim, Kit.F($"DISTANCE   {range:0.0} KM"));
        var divider = min.Y + Kit.Px(MapCourseMenu.HeaderHeight) - 0.5f;
        draw.AddLine(new float2(min.X + Kit.Px(6), divider), new float2(min.X + size.X - Kit.Px(6), divider), Kit.Color(Theme.Line));

        world.CountJumpReady(target, out var ships, out var ready, out var inSector);
        var none = ships == 0;
        var rowSize = MapCourseMenu.RowSize(size, Kit.Scale);
        var session = _app.Session;

        var moveCount = ships == 1 ? "1 ship"u8 : Kit.F($"{ships} ships");
        if (MapMenuItem(draw, MapCourseMenu.RowMin(min, 0, Kit.Scale), rowSize, "Move here"u8, none ? "NO SHIPS SELECTED"u8 : "UNDER OWN ENGINES"u8, moveCount, !none) && session is not null)
        {
            world.OrderMoveSelected(session.Client.Input, target);
            _mapMenuOpen = false;
        }

        var canJump = ready > 0;
        // The ship being flown is only given the course; its pilot starts the jump.
        var plotOnly = world.JumpPlotsOwnOnly;
        var reason = !canJump ? ships == 0 ? "NO SHIPS SELECTED"u8 : inSector == ships ? "ALREADY IN THIS SECTOR"u8 : "NO WORKING JUMP DRIVE"u8
            : plotOnly ? Kit.F($"SET NAV TARGET, THEN AIM AND {world.BoostKeyName}")
            : "FASTER THAN LIGHT"u8;
        var jumpCount = ships == 1 ? Kit.F($"{ready} of 1 ship") : Kit.F($"{ready} of {ships} ships");
        if (MapMenuItem(draw, MapCourseMenu.RowMin(min, 1, Kit.Scale), rowSize, plotOnly ? "Plot jump"u8 : "Jump here"u8, reason, jumpCount, canJump) && session is not null)
        {
            world.OrderJump(session.Client.Input, target);
            _mapMenuOpen = false;
        }

        MapCourseMenu.End();
    }

    /// <summary>
    /// One row of the course menu: a label with a short line under it and a count at the right. A disabled row is
    /// drawn muted, does not light up and cannot be chosen, though it still takes the click so the menu stays open.
    /// </summary>
    private bool MapMenuItem(ImDrawListPtr draw, float2 rowMin, float2 rowSize, ImString label, ImString detail, ImString count, bool enabled)
    {
        var pressed = MapCourseMenu.Item(label, rowMin, rowSize);
        var hot = Kit.Animate(ImGui.GetItemID(), enabled && ImGui.IsItemHovered() ? 1f : 0f);
        if (hot > 0.01f)
        {
            draw.AddRectFilled(rowMin, rowMin + new float2(rowSize.X * (0.35f + 0.65f * hot), rowSize.Y), Kit.Color(Theme.PanelRaised, 0.9f * hot));
            draw.AddRectFilled(rowMin, rowMin + new float2(Kit.Px(3), rowSize.Y), Kit.Color(Theme.Accent, hot));
        }

        var text = rowMin + new float2(Kit.Px(Theme.Space3) + Kit.Px(6) * hot, Kit.Px(5));
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text, enabled ? Kit.Mix(Theme.TextDim, Theme.Text, 0.55f + 0.45f * hot) : Theme.TextMute, label, enabled ? 1f : 0.7f);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(22)), enabled ? Theme.TextMute : Theme.Bad, detail, enabled ? 1f : 0.75f);
        var countWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, count).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(rowMin.X + rowSize.X - Kit.Px(Theme.Space3) - countWidth, rowMin.Y + Kit.Px(8)), enabled ? Kit.Mix(Theme.TextDim, Theme.Accent, hot) : Theme.TextMute, count, enabled ? 1f : 0.7f);
        return pressed && enabled;
    }

    /// <summary>What is known about the sector under the pointer.</summary>
    private void DrawMapHoverPanel(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, Galaxy galaxy, SectorCoord hover, SectorCoord here)
    {
        var display = _ui.DisplaySize;
        var known = _discovered.Contains(hover);
        // What a discovered sector with asteroids holds takes a few more lines: its richness and its resources.
        var inner = Kit.Px(MapHoverWidth) - Kit.Px(Theme.Space4) * 2;
        var hasRock = known && MapSummary(galaxy, hover).AsteroidCount > 0;
        var characterHeight = hasRock ? SectorCharacterHeight(galaxy, MapSummary(galaxy, hover).Character, inner) : 0f;
        // Its height follows its lines: the caption and name, two lines of place, three more for a discovered
        // sector or one hint for an unexplored one, the sector's character, and the closing hint.
        var line = Kit.Px(20);
        var panelSize = new float2(Kit.Px(MapHoverWidth), Kit.Px(Theme.Space4 * 2 + 62 + 4 + 16) + line * (known ? 5 : 3) + characterHeight);
        var panelMin = new float2(MapUnitList.HoverCardLeft(display.X, Kit.Scale, MapHoverWidth, MapUnitList.HoverRight), Kit.Px(MapUnitList.HoverTop));
        Kit.PanelFrame(draw, panelMin, panelMin + panelSize);
        var text = panelMin + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space4));

        if (!_mapHasHover || _mapHover != hover)
        {
            _mapHover = hover;
            _mapHasHover = true;
            _mapHoverName = known ? galaxy.NameOf(hover) : "Unexplored";
        }
        else if (known && _mapHoverName == "Unexplored")
        {
            _mapHoverName = galaxy.NameOf(hover);
        }

        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, hover == here ? Theme.Accent : known ? Theme.Info : Theme.TextMute, hover == here ? "YOU ARE HERE"u8 : known ? "DISCOVERED"u8 : "UNEXPLORED"u8);
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, text + new float2(0, Kit.Px(18)), known ? Theme.Text : Theme.TextDim, _mapHoverName);
        var y = Kit.Px(62);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, y), Theme.TextDim, Kit.F($"COORDINATES   {hover.X} : {hover.Z}   LAYER {hover.Y:+0;-0;0}"));
        y += line;
        var range = galaxy.CenterOf(hover).RelativeTo(world.OwnPosition).Length() / 1000f;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, y), Theme.TextDim, Kit.F($"DISTANCE      {range:0.0} KM"));
        y += line;

        if (known)
        {
            ref var summary = ref MapSummary(galaxy, hover);
            var ships = 0;
            for (var i = 0; i < snapshot.HullCount; i++)
            {
                if (snapshot.Hulls[i].Owner == snapshot.PlayerId && galaxy.SectorOf(world.HullPosition(snapshot.Hulls[i])) == hover)
                    ships++;
            }

            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, y), Theme.TextDim, Kit.F($"STAR          {galaxy.StarClasses[summary.StarClass].Name}"));
            y += line;
            Kit.Text(
                draw,
                _ui.Mono,
                Theme.FontCaption,
                text + new float2(0, y),
                Theme.TextDim,
                summary.AsteroidCount > 0 ? Kit.F($"ASTEROIDS     {summary.AsteroidCount} IN {summary.FieldCount} {(summary.FieldCount == 1 ? "FIELD" : "FIELDS")}") : "ASTEROIDS     none"u8
            );
            y += line;
            if (hasRock)
            {
                DrawSectorCharacter(draw, text + new float2(0, y), inner, galaxy, summary.Character);
                y += characterHeight;
            }

            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, y), ships > 0 ? Theme.Accent : Theme.TextDim, Kit.F($"YOUR SHIPS    {ships}"));
            y += line;
        }
        else
        {
            Kit.Text(draw, _ui.Sans, Theme.FontCaption, text + new float2(0, y), Theme.TextMute, "Send a ship within one sector to chart it."u8);
            y += line;
        }

        Kit.Text(draw, _ui.Sans, Theme.FontCaption, text + new float2(0, y + Kit.Px(4)), Theme.TextMute, "Right-click to move or jump here"u8);
    }

    /// <summary>Grid lines every <paramref name="every"/> sectors, drawn on sector boundaries.</summary>
    private static void DrawMapGrid(ImDrawListPtr draw, float2 display, float left, float top, float right, float bottom, int every, float alpha, float2 origin, float cell)
    {
        if (alpha <= 0.01f)
            return;

        // Boundaries sit half a sector from the sector centres; stronger lines fall on multiples of the spacing.
        var color = Kit.Color(0x8FB4C8, alpha);
        for (var x = MathF.Ceiling((left - 0.5f) / every) * every; x - 0.5f <= right; x += every)
        {
            var sx = MathF.Round(origin.X + (x - 0.5f) * cell);
            draw.AddLine(new float2(sx, 0), new float2(sx, display.Y), color);
        }

        for (var z = MathF.Ceiling((top - 0.5f) / every) * every; z - 0.5f <= bottom; z += every)
        {
            var sy = MathF.Round(origin.Y + (z - 0.5f) * cell);
            draw.AddLine(new float2(0, sy), new float2(display.X, sy), color);
        }
    }

    /// <summary>
    /// Charts the sectors around the player's hulls: a hull's sensors reach the sectors next to the one it is in.
    /// </summary>
    private void UpdateDiscovery(SessionSnapshot snapshot, WorldView world)
    {
        var galaxy = world.Galaxy!;
        if (!ReferenceEquals(galaxy, _discoveredGalaxy))
        {
            _discoveredGalaxy = galaxy;
            _discovered.Clear();
            Array.Fill(_discoveredFrom, new SectorCoord(int.MinValue, 0, 0));
        }

        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Owner != snapshot.PlayerId)
                continue;
            var coord = galaxy.SectorOf(hull.Position);
            var slot = (int)(hull.Id & 0xFFFF) % _discoveredFrom.Length;
            if (_discoveredFrom[slot] == coord)
                continue;

            _discoveredFrom[slot] = coord;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        _discovered.Add(coord.Offset(dx, dy, dz));
        }
    }

    private ref SectorSummary MapSummary(Galaxy galaxy, SectorCoord coord)
    {
        var index = (int)((uint)(coord.X * 73856093 ^ coord.Y * 19349663 ^ coord.Z * 83492791) % MapCacheSize);
        if (!_mapCacheValid[index] || _mapCache[index].Coord != coord)
        {
            _mapCache[index] = galaxy.Summarize(coord);
            _mapCacheValid[index] = true;
            _mapNames[index] = null;
        }

        return ref _mapCache[index];
    }

    /// <summary>A sector's name for the map, made the first time the sector is shown with labels.</summary>
    private ref string MapName(Galaxy galaxy, SectorCoord coord)
    {
        MapSummary(galaxy, coord);
        var index = (int)((uint)(coord.X * 73856093 ^ coord.Y * 19349663 ^ coord.Z * 83492791) % MapCacheSize);
        _mapNames[index] ??= galaxy.NameOf(coord);
        return ref _mapNames[index]!;
    }

    /// <summary>Corner brackets around a rectangle.</summary>
    private static void Brackets(ImDrawListPtr draw, float2 min, float2 max, float length, ImColor8 color)
    {
        var t = Kit.Px(2);
        draw.AddLine(min, min + new float2(length, 0), color, t);
        draw.AddLine(min, min + new float2(0, length), color, t);
        draw.AddLine(new float2(max.X, min.Y), new float2(max.X - length, min.Y), color, t);
        draw.AddLine(new float2(max.X, min.Y), new float2(max.X, min.Y + length), color, t);
        draw.AddLine(new float2(min.X, max.Y), new float2(min.X + length, max.Y), color, t);
        draw.AddLine(new float2(min.X, max.Y), new float2(min.X, max.Y - length), color, t);
        draw.AddLine(max, max - new float2(length, 0), color, t);
        draw.AddLine(max, max - new float2(0, length), color, t);
    }

    /// <summary>A star's light colour as an interface colour.</summary>
    private static uint StarColor(Vector3 color) =>
        (uint)(Math.Clamp(color.X, 0f, 1f) * 255f) << 16 | (uint)(Math.Clamp(color.Y, 0f, 1f) * 255f) << 8 | (uint)(Math.Clamp(color.Z, 0f, 1f) * 255f);
}
