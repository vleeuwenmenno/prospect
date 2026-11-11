using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
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
    private float2 _mapBoxStart;
    private bool _mapBoxing;
    private bool _mapBoxMoved;
    private float _mapCell = 44f;
    private readonly HashSet<SectorCoord> _discovered = [];
    private readonly SectorCoord[] _discoveredFrom = new SectorCoord[256];
    private Galaxy? _discoveredGalaxy;
    private int _mapLayer;
    private SectorCoord _mapHover;
    private string _mapHoverName = "";
    private bool _mapHasHover;
    private bool _mapMenuOpen;
    private SectorCoord _mapMenuSector;
    private string _mapMenuName = "";
    private float2 _mapMenuMin;
    private float2 _mapMenuSize;
    private float _mapMenuTime;

    /// <summary>True while the player is in space with nothing open over it, so input belongs to the world.</summary>
    public bool WorldInputEnabled => _screen == Screen.Session && !_sessionMenuOpen && !_mapOpen;

    public void OpenShipInfo() => _shipInfoOpen = true;

    /// <summary>Opens the map zoomed all the way out, centred on the galaxy's core.</summary>
    public void OpenGalaxyOverview()
    {
        OpenMap();
        var shape = _app.World.Galaxy!.Shape;
        _mapCenter = new float2(shape.Center.X, shape.Center.Z);
        _mapCell = Math.Max(MapMinCell, _ui.DisplaySize.Y / (shape.Radius * 2.5f));
    }

    public void OpenMap()
    {
        _mapOpen = true;
        _mapMenuOpen = false;
        _mapTime = 0;
        _mapCenter = new float2((float)(_app.World.OwnPosition.X / _app.World.Galaxy!.SectorSize), (float)(_app.World.OwnPosition.Z / _app.World.Galaxy!.SectorSize));
        _mapLayer = _app.World.Sector.Coord.Y;
    }

    /// <summary>Everything drawn over the world while in space: flight readouts, command markers, the map.</summary>
    private void DrawWorldInterface(SessionSnapshot snapshot)
    {
        var world = _app.World;
        UpdateDiscovery(snapshot, world);
        if (!_sessionMenuOpen && _app.Actions.Pressed(_app.Devices, InputAction.ToggleMap, !_ui.WantsTextInput))
        {
            if (_mapOpen)
                _mapOpen = false;
            else
                OpenMap();
        }

        if (WorldInputEnabled && _app.Actions.Pressed(_app.Devices, InputAction.ShipInfo, true))
            _shipInfoOpen = !_shipInfoOpen;

        BeginRoot("##world"u8, ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        var draw = ImGui.GetWindowDrawList();
        DrawSectorPanel(draw, snapshot, world);
        if (_shipInfoOpen && !_mapOpen)
        {
            // In the command view the sheet describes the first selected thing; otherwise the hull being flown.
            if (world.Mode == ViewMode.Command && world.Selection.Length == 0 && world.SelectedRocks.Length > 0)
            {
                DrawRockInfo(draw, world, world.SelectedRocks[0]);
                EndRoot();
                if (world.CommandBlend > 0.5f)
                    DrawFleetList(snapshot, world, world.CommandBlend);
                return;
            }

            var shown = world.Own;
            if (world.Mode == ViewMode.Command && world.Selection.Length > 0 && world.TryGetHull(world.Selection[0], out var picked))
                shown = picked;
            DrawShipInfo(draw, _app.Content.Designs[Math.Min(shown.Design, _app.Content.Designs.Length - 1)], shown.Blocks, world.Mode == ViewMode.Command);
        }
        if (!_mapOpen)
        {
            var pilot = 1f - world.CommandBlend;
            if (pilot > 0.01f)
                DrawFlightReadouts(draw, snapshot, world, pilot);
            if (world.CommandBlend > 0.01f)
                DrawCommandOverlay(draw, snapshot, world, world.CommandBlend);
        }

        EndRoot();

        if (!_mapOpen && world.CommandBlend > 0.5f)
            DrawFleetList(snapshot, world, world.CommandBlend);
        if (_mapOpen)
            DrawMap(snapshot, world);
    }

    private void DrawSectorPanel(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        var min = new float2(Kit.Px(Theme.Space5), Kit.Px(Theme.Space5));
        var size = new float2(Kit.Px(360), Kit.Px(96));
        Kit.PanelFrame(draw, min, min + size);

        var star = world.Galaxy!.StarClasses[world.Sector.StarClass];
        var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
        draw.AddCircleFilled(text + new float2(Kit.Px(5), Kit.Px(10)), Kit.Px(5), Kit.Color(StarColor(star.Color)), 20);
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text + new float2(Kit.Px(18), 0), Theme.Text, world.SectorName);

        var coord = world.Sector.Coord;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(30)), Theme.TextDim, Kit.F($"SECTOR {coord.X} {coord.Y} {coord.Z}   {world.Sector.AsteroidCount} ROCKS"));
        Kit.Text(
            draw,
            _ui.Mono,
            Theme.FontCaption,
            text + new float2(0, Kit.Px(50)),
            Theme.TextMute,
            snapshot.RoundTripMs < 0
                ? Kit.F($"{snapshot.ServerName}   PLAYERS {snapshot.PlayerCount}")
                : Kit.F($"{snapshot.ServerName}   PLAYERS {snapshot.PlayerCount}   RTT {snapshot.RoundTripMs:0} MS")
        );
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

        DrawTargeting(draw, snapshot, world);

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

        // Speed in the middle; what keeps the hull alive on the left, what drives it on the right.
        var flight = snapshot.Flight;
        var center = new float2(display.X * 0.5f, display.Y - Kit.Px(132));
        var speedSize = Kit.Measure(_ui.Mono, 44f, Kit.F($"{speed:0}"));
        Kit.Text(draw, _ui.Mono, 44f, center - new float2(speedSize.X * 0.5f, 0), Theme.Text, Kit.F($"{speed:0}"));
        var unit = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "M/S"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, center + new float2(-unit.X * 0.5f, Kit.Px(52)), Theme.TextMute, "M/S"u8);

        var barWidth = Kit.Px(300);
        var gap = Kit.Px(96);
        var row = Kit.Px(34);
        var leftMin = center + new float2(-gap - barWidth, Kit.Px(4));
        var rightMin = center + new float2(gap, Kit.Px(4));
        var hullFraction = own.Hull / flight.HullPoints;
        StatusBar(draw, leftMin, barWidth, "SHIELD"u8, own.Shield / flight.ShieldCapacity, Theme.Info, Kit.F($"{own.Shield:0} / {flight.ShieldCapacity:0}"));
        StatusBar(draw, leftMin + new float2(0, row), barWidth, "HULL"u8, hullFraction, hullFraction < 0.3f ? Theme.Bad : Theme.Good, Kit.F($"{own.Hull:0} / {flight.HullPoints:0}"));
        StatusBar(
            draw,
            rightMin,
            barWidth,
            "ENERGY"u8,
            own.Energy / flight.EnergyStorage,
            own.Energy < flight.EnergyStorage * 0.15f ? Theme.Bad : Theme.Accent,
            MathF.Abs(own.EnergyRate) < 0.05f
                ? Kit.F($"{own.Energy:0} / {flight.EnergyStorage:0} MJ")
                : Kit.F($"{own.Energy:0} / {flight.EnergyStorage:0} MJ   {own.EnergyRate:+0.0;-0.0} MW")
        );
        StatusBar(
            draw,
            rightMin + new float2(0, row),
            barWidth,
            own.BoostLocked ? "BOOST COOLING"u8 : "BOOST HEAT"u8,
            own.Heat,
            own.BoostLocked ? Theme.Bad : own.Heat > 0.7f ? Theme.Accent : Theme.TextDim,
            snapshot.Dampeners ? "DAMPENERS ON"u8 : "DAMPENERS OFF"u8
        );

        if (own.Jump is JumpPhase.Spool or JumpPhase.Tunnel)
        {
            // The jump drive charging: which drive, how far along, and how to stop it.
            var drive = flight.Ftl switch
            {
                FtlKind.Hyperspace => "HYPERSPACE DRIVE"u8,
                FtlKind.Wormhole => "WORMHOLE GENERATOR"u8,
                _ => "RIFT CUTTER"u8,
            };
            var spooling = own.Jump == JumpPhase.Spool;
            var driveSize = Kit.Measure(_ui.SansMedium, Theme.FontSmall, drive);
            var at = new float2(display.X * 0.5f, display.Y * 0.5f + Kit.Px(86));
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, at - new float2(driveSize.X * 0.5f, 0), Theme.Info, drive);
            var gauge = new float2(Kit.Px(260), Kit.Px(5));
            var gaugeMin = at + new float2(-gauge.X * 0.5f, Kit.Px(24));
            draw.AddRectFilled(gaugeMin, gaugeMin + gauge, Kit.Color(Theme.Void, 0.8f));
            draw.AddRectFilled(gaugeMin, gaugeMin + new float2(gauge.X * (spooling ? Math.Clamp(own.JumpTimer / flight.FtlSpoolSeconds, 0f, 1f) : 1f), gauge.Y), Kit.Color(Theme.Info));
            var note = spooling ? "SPOOLING  -  THRUST TO CANCEL"u8 : "IN TRANSIT"u8;
            var noteSize = Kit.Measure(_ui.Mono, Theme.FontCaption, note);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(-noteSize.X * 0.5f, Kit.Px(36)), Theme.TextDim, note);
        }

        if (own.Order.Kind == OrderKind.MoveTo)
        {
            var distance = own.Order.Target.RelativeTo(world.OwnPosition).Length();
            var label = distance >= 1000f ? Kit.F($"AUTOPILOT   {distance / 1000f:0.00} KM") : Kit.F($"AUTOPILOT   {distance:0} M");
            var labelSize = Kit.Measure(_ui.SansMedium, Theme.FontSmall, label);
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, center - new float2(labelSize.X * 0.5f, Kit.Px(34)), Theme.Accent, label);
            if (world.Project(own.Order.Target, out var target))
                DrawTargetMarker(draw, target, Theme.Accent);
        }

        var hint = new float2(Kit.Px(Theme.Space5), display.Y - Kit.Px(56));
        var pad = _app.Devices.PadConnected;
        hint.X += Kit.KeyHint(draw, hint, pad ? "BACK"u8 : "N"u8, "Command"u8);
        hint.X += Kit.KeyHint(draw, hint, pad ? "Y"u8 : "M"u8, "Map"u8);
        hint.X += Kit.KeyHint(draw, hint, pad ? "X"u8 : "Z"u8, "Dampeners"u8);
        hint.X += Kit.KeyHint(draw, hint, pad ? "L3"u8 : "SHIFT"u8, "Boost"u8);
        hint.X += Kit.KeyHint(draw, hint, "RMB"u8, "Free look"u8);
        hint.X += Kit.KeyHint(draw, hint, "MMB"u8, "Target"u8);
        hint.X += Kit.KeyHint(draw, hint, pad ? "D-DOWN"u8 : "T"u8, world.TargetHull != 0 ? "Take control"u8 : "Drone"u8);
        hint.X += Kit.KeyHint(draw, hint, pad ? "D-UP"u8 : "I"u8, "Ship data"u8);
        Kit.KeyHint(draw, hint, pad ? "START"u8 : "ESC"u8, "Menu"u8);
        Kit.Fade = previousFade;
    }

    /// <summary>Markers and readouts for the command view: hulls, selection, orders and the point under the pointer.</summary>
    private void DrawCommandOverlay(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var previousFade = Kit.Fade;
        Kit.Fade = alpha;
        var display = _ui.DisplaySize;
        var planeHeight = world.CommandFocus.Y;
        var designs = _app.Content.Designs;

        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var position = world.HullPosition(hull);
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

        var hint = new float2(Kit.Px(Theme.Space5), display.Y - Kit.Px(56));
        hint.X += Kit.KeyHint(draw, hint, "LMB / DRAG"u8, "Select"u8);
        hint.X += Kit.KeyHint(draw, hint, "RMB"u8, "Move"u8);
        hint.X += Kit.KeyHint(draw, hint, "RMB DRAG"u8, "Rotate"u8);
        hint.X += Kit.KeyHint(draw, hint, "WHEEL"u8, "Zoom"u8);
        hint.X += Kit.KeyHint(draw, hint, "WASD"u8, "Pan"u8);
        hint.X += Kit.KeyHint(draw, hint, "Q / E"u8, "Plane"u8);
        hint.X += Kit.KeyHint(draw, hint, "SPACE"u8, "Focus"u8);
        hint.X += Kit.KeyHint(draw, hint, "H"u8, "Halt"u8);
        Kit.KeyHint(draw, hint, "N"u8, "Pilot"u8);
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
        var min = new float2(_ui.DisplaySize.X - size.X - Kit.Px(Theme.Space5), Kit.Px(264));
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##fleet"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        Kit.Fade = alpha;
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3)), Theme.Accent, "FLEET"u8);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(Kit.Px(Theme.Space4 + 52), Kit.Px(Theme.Space3)), Theme.TextMute, Kit.F($"{owned} HULLS"));

        var designs = _app.Content.Designs;
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
    private void DrawTargeting(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        var designs = _app.Content.Designs;
        var display = _ui.DisplaySize;

        if (world.HoveredHull != 0 && world.HoveredHull != world.TargetHull && world.TryGetHull(world.HoveredHull, out var hovered))
        {
            var design = designs[Math.Min(hovered.Design, designs.Length - 1)];
            if (world.ProjectDisc(hovered.Position, design.Flight.Radius, out var screen, out var pixels, out _))
            {
                var reach = Math.Max(pixels * 1.3f, Kit.Px(16));
                Brackets(draw, screen - new float2(reach, reach), screen + new float2(reach, reach), Kit.Px(8), Kit.Color(Theme.Text, 0.85f));
                var mine = hovered.Owner == snapshot.PlayerId;
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, screen + new float2(reach + Kit.Px(8), -Kit.Px(7)), mine ? Theme.Accent : Theme.Info, mine ? Kit.F($"{design.Name} {(hovered.Id & 0xFFFF) + 1}") : Kit.F($"PLAYER {hovered.Owner}  {design.Name}"));
            }
        }

        if (world.TargetHull != 0 && world.TryGetHull(world.TargetHull, out var target))
        {
            var design = designs[Math.Min(target.Design, designs.Length - 1)];
            var mine = target.Owner == snapshot.PlayerId;
            var color = mine ? Theme.Accent : Theme.Info;
            if (world.ProjectDisc(target.Position, design.Flight.Radius, out var screen, out var pixels, out _))
            {
                var reach = Math.Max(pixels * 1.3f, Kit.Px(18));
                Brackets(draw, screen - new float2(reach, reach), screen + new float2(reach, reach), Kit.Px(10), Kit.Color(color));
                draw.AddCircle(screen, reach * 1.25f, Kit.Color(color, 0.5f), 40, Kit.Px(1.2f));
            }

            // The target's readout, top centre.
            var size = new float2(Kit.Px(340), Kit.Px(104));
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

        // Asteroids: a ring on the one under the reticle, a readout for the targeted one.
        if (world.HoveredRock is { } hoverRock && world.HoveredRock != world.TargetRock && world.TryGetRock(hoverRock, out var hoverPosition, out var hoverRadius) && world.ProjectDisc(hoverPosition, hoverRadius, out var hoverScreen, out var hoverPixels, out _))
            draw.AddCircle(hoverScreen, Math.Max(hoverPixels * 1.05f, Kit.Px(12)), Kit.Color(Theme.Text, 0.6f), 48, Kit.Px(1.2f));

        if (world.TargetRock is { } targetRock && world.TryGetRock(targetRock, out var rockPosition, out var rockRadius))
        {
            if (world.ProjectDisc(rockPosition, rockRadius, out var rockScreen, out var rockPixels, out _))
            {
                var ring = Math.Max(rockPixels * 1.08f, Kit.Px(14));
                draw.AddCircle(rockScreen, ring, Kit.Color(Theme.Info), 56, Kit.Px(1.5f));
                Brackets(draw, rockScreen - new float2(ring + 5, ring + 5), rockScreen + new float2(ring + 5, ring + 5), Kit.Px(9), Kit.Color(Theme.Info));
            }

            var size = new float2(Kit.Px(340), Kit.Px(92));
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
    }

    /// <summary>What to call an asteroid in a readout.</summary>
    private static ReadOnlySpan<byte> RockTitle(WorldView world, RockRef rock) =>
        world.TryGetRockOre(rock, out var resource, out var grade) && resource >= 0
            ? Kit.F($"{world.Galaxy!.Resources[resource].Name} asteroid   {grade * 100:0}% grade")
            : "Bare rock asteroid"u8;

    /// <summary>A labelled gauge: caption on the left, reading on the right, bar underneath.</summary>
    private void StatusBar(ImDrawListPtr draw, float2 min, float width, ImString label, float fraction, uint rgb, ImString reading)
    {
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min, Theme.TextDim, label);
        var readingSize = Kit.Measure(_ui.Mono, Theme.FontCaption, reading);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(width - readingSize.X, 0), Theme.Text, reading);

        var barMin = min + new float2(0, Kit.Px(18));
        var barMax = barMin + new float2(width, Kit.Px(7));
        draw.AddRectFilled(barMin, barMax, Kit.Color(Theme.Void, 0.75f));
        draw.AddRectFilled(barMin, new float2(barMin.X + width * Math.Clamp(fraction, 0f, 1f), barMax.Y), Kit.Color(rgb));
        draw.AddRect(barMin - new float2(1, 1), barMax + new float2(1, 1), Kit.Color(Theme.LineStrong));
    }

    /// <summary>An asteroid's data sheet.</summary>
    private void DrawRockInfo(ImDrawListPtr draw, WorldView world, RockRef rock)
    {
        if (!world.TryGetRock(rock, out var position, out var radius))
            return;

        var galaxy = world.Galaxy!;
        var line = Kit.Px(21);
        var size = new float2(Kit.Px(400), Kit.Px(Theme.Space5 * 2 + 46) + line * 9);
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
        Row("Diameter"u8, Kit.F($"{radius * 2:0} m"));
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
        var size = new float2(Kit.Px(400), Kit.Px(Theme.Space5 * 2 + 46) + line * 24);
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
        BeginRoot("##map"u8);
        var draw = ImGui.GetWindowDrawList();
        var display = _ui.DisplaySize;
        var io = ImGui.GetIO();
        var shape = galaxy.Shape;
        var here = world.Sector.Coord;
        var size = galaxy.SectorSize;
        var player = new float2((float)(world.OwnPosition.X / size), (float)(world.OwnPosition.Z / size));
        var middle = display * 0.5f;
        var mouse = io.MousePos;
        var devices = _app.Devices;

        // The course menu, while open, holds the map still and takes the clicks that land on it.
        var menuOpen = _mapMenuOpen;
        var overMenu = menuOpen && mouse.X >= _mapMenuMin.X && mouse.Y >= _mapMenuMin.Y && mouse.X < _mapMenuMin.X + _mapMenuSize.X && mouse.Y < _mapMenuMin.Y + _mapMenuSize.Y;

        // Input. W A S D, the arrow keys and middle-drag pan; zooming keeps the sector under the pointer in place.
        if (!menuOpen)
            PanMap(io, middle, mouse, here, player);

        // Left button: click picks one of the player's hulls, drag draws a selection box around several. With the
        // menu open a click elsewhere only closes it, and a click on it belongs to its items.
        if (devices.MousePressed(Brutal.GlfwApi.GlfwMouseButton.Left))
        {
            if (menuOpen)
            {
                if (!overMenu)
                    _mapMenuOpen = false;
            }
            else
            {
                _mapBoxing = true;
                _mapBoxMoved = false;
                _mapBoxStart = mouse;
            }
        }

        var boxReleased = false;
        if (_mapBoxing)
        {
            var moved = mouse - _mapBoxStart;
            if (MathF.Abs(moved.X) + MathF.Abs(moved.Y) > 5f)
                _mapBoxMoved = true;
            if (!devices.MouseDown(Brutal.GlfwApi.GlfwMouseButton.Left))
            {
                _mapBoxing = false;
                boxReleased = true;
            }
        }

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
        var maxRocks = (float)galaxy.MaxAsteroidsPerSector;
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
                    var richness = summary.AsteroidCount / maxRocks;

                    // Uncharted space shows only its stronger signals, and only once the cells are large enough.
                    if (!known && (richness < 0.3f || (cell < 34f && richness < 0.55f)))
                        continue;
                    var position = ToScreen(x, z);
                    var star = StarColor(galaxy.StarClasses[summary.StarClass].Color);

                    // Something is there whether or not it has been visited; what it is shows only once discovered.
                    var tint = known ? star : Kit.Mix(star, 0x66727F, 0.75f);
                    var glow = Math.Min(cell * (0.12f + 0.5f * richness * richness), 52f);
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

                    var sweep = MathF.Tau * Math.Clamp(richness, 0.08f, 1f);
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
        var boxMin = new float2(Math.Min(_mapBoxStart.X, mouse.X), Math.Min(_mapBoxStart.Y, mouse.Y));
        var boxMax = new float2(Math.Max(_mapBoxStart.X, mouse.X), Math.Max(_mapBoxStart.Y, mouse.Y));
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
            if (boxReleased && _mapBoxMoved && at.X >= boxMin.X && at.X <= boxMax.X && at.Y >= boxMin.Y && at.Y <= boxMax.Y)
                world.AddToSelection(hull.Id);
            var offset = at - mouse;
            if (boxReleased && !_mapBoxMoved && offset.X * offset.X + offset.Y * offset.Y < clickedDistance)
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
        if (_mapBoxing && _mapBoxMoved)
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
        var pointerOnMap = !overMenu && mouse.X >= 0 && mouse.Y >= 0 && mouse.X < display.X && mouse.Y < display.Y;
        if (pointerOnMap)
        {
            var at = ToScreen(hover.X, hover.Z);
            var corner = new float2(Math.Max(cell * 0.5f, 7f), Math.Max(cell * 0.5f, 7f));
            Brackets(draw, at - corner, at + corner, Math.Clamp(cell * 0.3f, 3f, Kit.Px(10)), Kit.Color(Theme.Text, 0.85f));
            if (!_mapMenuOpen)
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, mouse + new float2(Kit.Px(18), -Kit.Px(6)), Theme.Text, Kit.F($"{hover.X} : {hover.Z}"));

            // Right-click opens the course menu on the sector, or moves it there if it is already open.
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                OpenMapMenu(galaxy, hover, mouse);
        }

        // The sector the course menu is for.
        if (_mapMenuOpen && _mapMenuSector.Y == _mapLayer)
        {
            var at = ToScreen(_mapMenuSector.X, _mapMenuSector.Z);
            var corner = new float2(Math.Max(cell * 0.5f, 7f), Math.Max(cell * 0.5f, 7f));
            Brackets(draw, at - corner, at + corner, Math.Clamp(cell * 0.3f, 3f, Kit.Px(10)), Kit.Color(Theme.Accent));
        }

        // Title, layer and scale.
        var margin = Kit.Px(96);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(margin, Kit.Px(140)), Theme.Accent, "NAVIGATION"u8);
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, new float2(margin, Kit.Px(158)), Theme.Text, "Galaxy Map"u8);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(margin, Kit.Px(198)), Theme.TextDim, Kit.F($"LAYER {_mapLayer:+0;-0;0}   {display.X / cell:0} SECTORS ACROSS   {size / 1000:0} KM EACH"));
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(margin, Kit.Px(218)), Theme.TextMute, Kit.F($"{_discovered.Count} SECTORS DISCOVERED   {world.Selection.Length} SHIPS SELECTED"));

        if (pointerOnMap)
            DrawMapHoverPanel(draw, snapshot, world, galaxy, hover, here);

        var hint = new float2(margin, display.Y - Kit.Px(56));
        hint.X += Kit.KeyHint(draw, hint, "WASD"u8, "Pan"u8);
        hint.X += Kit.KeyHint(draw, hint, "WHEEL"u8, "Zoom"u8);
        hint.X += Kit.KeyHint(draw, hint, "LMB / DRAG"u8, "Select ships"u8);
        hint.X += Kit.KeyHint(draw, hint, "RMB"u8, "Move or jump"u8);
        hint.X += Kit.KeyHint(draw, hint, "PGUP / PGDN"u8, "Layer"u8);
        hint.X += Kit.KeyHint(draw, hint, "SPACE"u8, "Centre on me"u8);
        Kit.KeyHint(draw, hint, "M / ESC"u8, "Close"u8);
        EndRoot();

        if (_mapMenuOpen)
            DrawMapMenu(world, galaxy, fade);
        Kit.Fade = 1f;
    }

    /// <summary>Pans, zooms and changes layer from the keyboard, middle drag and wheel.</summary>
    private void PanMap(ImGuiIOPtr io, float2 middle, float2 mouse, SectorCoord here, float2 player)
    {
        var devices = _app.Devices;
        var actions = _app.Actions;
        var display = _ui.DisplaySize;
        var panX = Math.Clamp(actions.Axis(devices, InputAxis.Strafe, true) + actions.Axis(devices, InputAxis.Yaw, true), -1f, 1f);
        var panY = Math.Clamp(actions.Axis(devices, InputAxis.Forward, true) + actions.Axis(devices, InputAxis.Pitch, true), -1f, 1f);
        _mapCenter += new float2(panX, -panY) * (display.Y * 0.9f / _mapCell * _ui.DeltaTime);
        if (devices.MouseDown(Brutal.GlfwApi.GlfwMouseButton.Middle))
            _mapCenter -= io.MouseDelta / _mapCell;
        if (io.MouseWheel != 0)
        {
            var under = _mapCenter + (mouse - middle) / _mapCell;
            _mapCell = Math.Clamp(_mapCell * MathF.Pow(1.18f, io.MouseWheel), MapMinCell, MapMaxCell);
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
        var display = _ui.DisplaySize;
        var margin = Kit.Px(Theme.Space2);
        var gap = Kit.Px(6);
        _mapMenuSize = new float2(Kit.Px(300), Kit.Px(MapMenuHeader) + Kit.Px(MapMenuRow) * 2 + Kit.Px(Theme.Space2));

        // Below and right of the pointer; flipped to the other side where that would leave the screen.
        var min = at + new float2(gap, gap);
        if (min.X + _mapMenuSize.X > display.X - margin)
            min.X = at.X - _mapMenuSize.X - gap;
        if (min.Y + _mapMenuSize.Y > display.Y - margin)
            min.Y = at.Y - _mapMenuSize.Y - gap;
        min.X = Math.Clamp(min.X, margin, Math.Max(margin, display.X - _mapMenuSize.X - margin));
        min.Y = Math.Clamp(min.Y, margin, Math.Max(margin, display.Y - _mapMenuSize.Y - margin));

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

    private const float MapMenuHeader = 84f;
    private const float MapMenuRow = 48f;

    /// <summary>
    /// The course menu: where the sector is, then the two ways to get there. Moving is always possible; jumping
    /// only for the ships with a working drive that are not already in that sector.
    /// </summary>
    private void DrawMapMenu(WorldView world, Galaxy galaxy, float fade)
    {
        _mapMenuTime += _ui.DeltaTime;
        var appear = Kit.EaseOut(_mapMenuTime / 0.12f);
        var min = _mapMenuMin + new float2(0, (1f - appear) * -Kit.Px(6));
        var size = _mapMenuSize;
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        // Unlike the full-window surfaces this one may come to the front: a window created with
        // NoBringToFrontOnFocus is placed behind every other, which would put it under the map.
        ImGui.Begin("##mapmenu"u8, (RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus) | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
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
        var divider = min.Y + Kit.Px(MapMenuHeader) - 0.5f;
        draw.AddLine(new float2(min.X + Kit.Px(6), divider), new float2(min.X + size.X - Kit.Px(6), divider), Kit.Color(Theme.Line));

        world.CountJumpReady(target, out var ships, out var ready, out var inSector);
        var rowMin = new float2(min.X + Kit.Px(6), min.Y + Kit.Px(MapMenuHeader) + Kit.Px(Theme.Space1));
        var rowSize = new float2(size.X - Kit.Px(12), Kit.Px(MapMenuRow) - Kit.Px(4));
        var session = _app.Session;

        var moveCount = ships == 1 ? "1 ship"u8 : Kit.F($"{ships} ships");
        if (MapMenuItem(draw, rowMin, rowSize, "Move here"u8, "UNDER OWN ENGINES"u8, moveCount, true) && session is not null)
        {
            world.OrderMove(session.Client.Input, target);
            _mapMenuOpen = false;
        }

        rowMin.Y += Kit.Px(MapMenuRow);
        var canJump = ready > 0;
        var reason = canJump ? "FASTER THAN LIGHT"u8 : inSector == ships ? "ALREADY IN THIS SECTOR"u8 : "NO WORKING JUMP DRIVE"u8;
        var jumpCount = ships == 1 ? Kit.F($"{ready} of 1 ship") : Kit.F($"{ready} of {ships} ships");
        if (MapMenuItem(draw, rowMin, rowSize, "Jump here"u8, reason, jumpCount, canJump) && session is not null)
        {
            world.OrderJump(session.Client.Input, target);
            _mapMenuOpen = false;
        }

        ImGui.End();
    }

    /// <summary>
    /// One row of the course menu: a label with a short line under it and a count at the right. A disabled row is
    /// drawn muted, does not light up and cannot be chosen, though it still takes the click so the menu stays open.
    /// </summary>
    private bool MapMenuItem(ImDrawListPtr draw, float2 rowMin, float2 rowSize, ImString label, ImString detail, ImString count, bool enabled)
    {
        ImGui.SetCursorScreenPos(rowMin);
        var pressed = ImGui.InvisibleButton(label, rowSize);
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
        var panelSize = new float2(Kit.Px(340), Kit.Px(known ? 238 : 150));
        var panelMin = new float2(display.X - panelSize.X - Kit.Px(96), Kit.Px(140));
        Kit.PanelFrame(draw, panelMin, panelMin + panelSize);
        var text = panelMin + new float2(Kit.Px(Theme.Space5), Kit.Px(Theme.Space4));
        var line = Kit.Px(20);

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
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, y), Theme.TextDim, summary.AsteroidCount > 0 ? Kit.F($"ASTEROIDS     {summary.AsteroidCount}") : "ASTEROIDS     none"u8);
            y += line;
            if (summary.Vein >= 0)
            {
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, y), Theme.TextDim, Kit.F($"MAIN ORE      {galaxy.Resources[summary.Vein].Name}"));
                y += line;
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
