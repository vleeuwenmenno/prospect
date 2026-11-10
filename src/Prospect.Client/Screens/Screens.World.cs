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
    private const float MapMinCell = 72f;
    private const float MapMaxCell = 220f;

    private readonly SectorSummary[] _mapCache = new SectorSummary[MapCacheSize];
    private readonly bool[] _mapCacheValid = new bool[MapCacheSize];
    private readonly string?[] _mapNames = new string?[MapCacheSize];
    private Galaxy? _mapGalaxy;
    private bool _mapOpen;
    private bool _shipInfoOpen;
    private float _mapTime;
    private float2 _mapPan;
    private float _mapCell = 128f;
    private int _mapLayer;
    private SectorCoord _mapHover;
    private string _mapHoverName = "";
    private bool _mapHasHover;

    /// <summary>True while the player is in space with nothing open over it, so input belongs to the world.</summary>
    public bool WorldInputEnabled => _screen == Screen.Session && !_sessionMenuOpen && !_mapOpen;

    public void OpenShipInfo() => _shipInfoOpen = true;

    public void OpenMap()
    {
        _mapOpen = true;
        _mapTime = 0;
        _mapPan = float2.Zero;
        _mapLayer = _app.World.Sector.Coord.Y;
    }

    /// <summary>Everything drawn over the world while in space: flight readouts, command markers, the map.</summary>
    private void DrawWorldInterface(SessionSnapshot snapshot)
    {
        var world = _app.World;
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
            // In the command view the sheet describes the first selected hull; otherwise the one being flown.
            var shown = world.Own;
            if (world.Mode == ViewMode.Command && world.Selection.Length > 0 && world.TryGetHull(world.Selection[0], out var picked))
                shown = picked;
            DrawShipInfo(draw, _app.Content.Designs[Math.Min(shown.Design, _app.Content.Designs.Length - 1)], world.Mode == ViewMode.Command);
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
            DrawMap(world);
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

            var s = Kit.Px(7);
            Span<float2> diamond = [screen + new float2(0, -s), screen + new float2(s, 0), screen + new float2(0, s), screen + new float2(-s, 0)];
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
            var design = designs[Math.Min(hull.Design, designs.Length - 1)];
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
            ? Kit.F($"{world.Selection.Length} SELECTED   FOLLOWING   RANGE {world.CommandDistance / 1000f:0.00} KM")
            : Kit.F($"{world.Selection.Length} SELECTED   FREE CAMERA   RANGE {world.CommandDistance / 1000f:0.00} KM");
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
            if (hull.IsOwn)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text + new float2(rowSize.X - Kit.Px(70), Kit.Px(2)), Theme.Accent, "YOU"u8);

            if (hull.Order.Kind == OrderKind.MoveTo)
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

    /// <summary>The hull's data sheet: every figure follows from the blocks it is built from.</summary>
    private void DrawShipInfo(ImDrawListPtr draw, HullDesign design, bool besideFleetList)
    {
        var stats = design.Stats;
        var line = Kit.Px(21);
        var size = new float2(Kit.Px(400), Kit.Px(Theme.Space5 * 2 + 46) + line * 23);
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
        Row("Blocks"u8, Kit.F($"{stats.Blocks}"));
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
    /// The galaxy map: one horizontal layer of sectors at a time, each drawn from the galaxy's seed. Drag to pan,
    /// wheel to zoom, Page Up and Page Down to change layer, right-click a sector to fly there.
    /// </summary>
    private void DrawMap(WorldView world)
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
        DrawDim(0.93f * fade);
        BeginRoot("##map"u8);
        var draw = ImGui.GetWindowDrawList();
        var display = _ui.DisplaySize;
        var io = ImGui.GetIO();
        var here = world.Sector.Coord;

        // Input: pan, zoom, layer, close.
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            _mapPan += io.MouseDelta;
        if (io.MouseWheel != 0)
            _mapCell = Math.Clamp(_mapCell * MathF.Pow(1.12f, io.MouseWheel), MapMinCell, MapMaxCell);
        if (ImGui.IsKeyPressed(ImGuiKey.PageUp))
            _mapLayer++;
        if (ImGui.IsKeyPressed(ImGuiKey.PageDown))
            _mapLayer--;
        if (BackPressed())
            _mapOpen = false;

        var cell = Kit.Px(_mapCell);
        var origin = display * 0.5f + _mapPan;
        var maxRocks = (float)galaxy.MaxAsteroidsPerSector;
        var mouse = io.MousePos;
        _mapHasHover = false;

        // The map is an island in the middle of the screen: sectors fade out toward its edge instead of
        // filling an ultrawide display from side to side.
        var islandY = display.Y * 0.42f;
        var islandX = Math.Min(display.X * 0.46f, islandY * 1.7f);
        var showNames = _mapCell >= 96f;

        // Screen x runs with sector X, screen y with sector Z, so "up" on the map is -Z, the way hulls face.
        var firstX = here.X + (int)MathF.Floor((display.X * 0.5f - islandX - origin.X) / cell + 0.5f);
        var lastX = here.X + (int)MathF.Floor((display.X * 0.5f + islandX - origin.X) / cell + 0.5f);
        var firstZ = here.Z + (int)MathF.Floor((display.Y * 0.5f - islandY - origin.Y) / cell + 0.5f);
        var lastZ = here.Z + (int)MathF.Floor((display.Y * 0.5f + islandY - origin.Y) / cell + 0.5f);

        for (var z = firstZ; z <= lastZ; z++)
            for (var x = firstX; x <= lastX; x++)
            {
                var center = origin + new float2((x - here.X) * cell, (z - here.Z) * cell);
                var fromMiddle = new float2((center.X - display.X * 0.5f) / islandX, (center.Y - display.Y * 0.5f) / islandY);
                var edge = 1f - Math.Clamp((MathF.Sqrt(fromMiddle.X * fromMiddle.X + fromMiddle.Y * fromMiddle.Y) - 0.62f) / 0.38f, 0f, 1f);
                if (edge <= 0.01f)
                    continue;

                var coord = new SectorCoord(x, _mapLayer, z);
                ref var summary = ref MapSummary(galaxy, coord);
                var half = cell * 0.5f;
                var min = center - new float2(half, half);
                var max = center + new float2(half, half);
                var hovered = mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y;

                // A small tick at each cell corner marks the grid without ruling lines across the whole map.
                var tick = Kit.Px(4);
                var tickColor = Kit.Color(Theme.LineStrong, 0.55f * edge);
                draw.AddLine(min - new float2(tick, 0), min + new float2(tick, 0), tickColor);
                draw.AddLine(min - new float2(0, tick), min + new float2(0, tick), tickColor);

                if (hovered)
                {
                    draw.AddRectFilled(min + new float2(3, 3), max - new float2(3, 3), Kit.Color(Theme.PanelRaised, 0.8f));
                    draw.AddRect(min + new float2(3, 3), max - new float2(3, 3), Kit.Color(Theme.LineStrong));
                    if (!_mapHasHover || _mapHover != coord)
                        _mapHoverName = galaxy.NameOf(coord);
                    _mapHover = coord;
                    _mapHasHover = true;
                }

                // The star: a soft halo and a core in the star's colour, larger for hotter classes.
                var star = galaxy.StarClasses[summary.StarClass];
                var starColor = StarColor(star.Color);
                var starRadius = Kit.Px(3.2f + 0.9f * summary.StarClass);
                var starCenter = center - new float2(0, showNames ? cell * 0.1f : 0);
                draw.AddCircleFilled(starCenter, starRadius * 2.6f, Kit.Color(starColor, 0.12f * edge), 24);
                draw.AddCircleFilled(starCenter, starRadius, Kit.Color(starColor, edge), 20);

                // Asteroids: an arc around the star, longer the richer the sector. Empty sectors have none.
                if (summary.AsteroidCount > 0)
                {
                    var sweep = MathF.Tau * Math.Clamp(summary.AsteroidCount / maxRocks, 0.08f, 1f);
                    draw.PathArcTo(starCenter, starRadius * 2.6f + Kit.Px(4), -MathF.PI / 2, -MathF.PI / 2 + sweep, 32);
                    draw.PathStroke(Kit.Color(Theme.Info, 0.75f * edge), ImDrawFlags.None, Kit.Px(1.5f));
                }

                if (showNames)
                {
                    ref var name = ref MapName(galaxy, coord);
                    var nameSize = Kit.Measure(_ui.Sans, Theme.FontCaption, name);
                    Kit.Text(draw, _ui.Sans, Theme.FontCaption, new float2(center.X - nameSize.X * 0.5f, center.Y + cell * 0.2f), hovered ? Theme.Text : Theme.TextDim, name, edge);
                }
            }

        // The player's sector and heading.
        if (_mapLayer == here.Y)
        {
            var local = world.OwnPosition - galaxy.CenterOf(here);
            var marker = origin + new float2((float)(local.X / galaxy.SectorSize) * cell, (float)(local.Z / galaxy.SectorSize) * cell);
            Brackets(draw, origin - new float2(cell * 0.5f - 3, cell * 0.5f - 3), origin + new float2(cell * 0.5f - 3, cell * 0.5f - 3), Kit.Px(12), Kit.Color(Theme.Accent));
            var heading = Vector3.Transform(-Vector3.UnitZ, world.OwnOrientation);
            var flat = new float2(heading.X, heading.Z);
            var length = MathF.Sqrt(flat.X * flat.X + flat.Y * flat.Y);
            flat = length > 0.01f ? flat / length : new float2(0, -1);
            var side = new float2(-flat.Y, flat.X);
            var s = Kit.Px(8);
            draw.AddTriangleFilled(marker + flat * s * 1.4f, marker - flat * s * 0.7f + side * s * 0.8f, marker - flat * s * 0.7f - side * s * 0.8f, Kit.Color(Theme.Accent));
        }

        // A standing order's destination.
        var order = world.Own.Order;
        if (order.Kind == OrderKind.MoveTo && galaxy.SectorOf(order.Target).Y == _mapLayer)
        {
            var local = order.Target - galaxy.CenterOf(here);
            var marker = origin + new float2((float)(local.X / galaxy.SectorSize) * cell, (float)(local.Z / galaxy.SectorSize) * cell);
            DrawTargetMarker(draw, marker, Theme.Accent);
        }

        if (_mapHasHover && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            _app.Session?.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.MoveTo, Target = galaxy.CenterOf(_mapHover) });

        // Title, layer and the hovered sector's details.
        var left = Kit.Px(96);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(left, Kit.Px(140)), Theme.Accent, "NAVIGATION"u8);
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, new float2(left, Kit.Px(158)), Theme.Text, "Galaxy Map"u8);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(left, Kit.Px(198)), Theme.TextDim, Kit.F($"LAYER {_mapLayer:+0;-0;0}   {galaxy.SectorSize / 1000:0} KM PER SECTOR"));

        if (_mapHasHover)
        {
            var panelSize = new float2(Kit.Px(340), Kit.Px(196));
            var panelMin = new float2(display.X - panelSize.X - Kit.Px(96), Kit.Px(140));
            Kit.PanelFrame(draw, panelMin, panelMin + panelSize);
            ref var hover = ref MapSummary(galaxy, _mapHover);
            var star = galaxy.StarClasses[hover.StarClass];
            var text = panelMin + new float2(Kit.Px(Theme.Space5), Kit.Px(Theme.Space4));
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, Theme.Accent, _mapHover == here ? "YOU ARE HERE"u8 : "SECTOR"u8);
            Kit.Text(draw, _ui.SansBold, Theme.FontHeading, text + new float2(0, Kit.Px(18)), Theme.Text, _mapHoverName);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(62)), Theme.TextDim, Kit.F($"COORDINATES   {_mapHover.X} {_mapHover.Y} {_mapHover.Z}"));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(82)), Theme.TextDim, Kit.F($"STAR          {star.Name}"));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(102)), Theme.TextDim, Kit.F($"ASTEROIDS     {hover.AsteroidCount}"));
            var range = galaxy.CenterOf(_mapHover).RelativeTo(world.OwnPosition).Length() / 1000f;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(122)), Theme.TextDim, Kit.F($"DISTANCE      {range:0.0} KM"));
            Kit.Text(draw, _ui.Sans, Theme.FontCaption, text + new float2(0, Kit.Px(150)), Theme.TextMute, "Right-click to set course"u8);
        }

        var hint = new float2(left, display.Y - Kit.Px(56));
        var legend = new float2(left, Kit.Px(236));
        draw.AddCircleFilled(legend + new float2(Kit.Px(5), Kit.Px(7)), Kit.Px(4), Kit.Color(Theme.Text), 16);
        Kit.Text(draw, _ui.Sans, Theme.FontCaption, legend + new float2(Kit.Px(20), 0), Theme.TextMute, "Star, coloured by class"u8);
        draw.PathArcTo(legend + new float2(Kit.Px(5), Kit.Px(29)), Kit.Px(6), -MathF.PI / 2, MathF.PI, 20);
        draw.PathStroke(Kit.Color(Theme.Info), ImDrawFlags.None, Kit.Px(1.5f));
        Kit.Text(draw, _ui.Sans, Theme.FontCaption, legend + new float2(Kit.Px(20), Kit.Px(22)), Theme.TextMute, "Asteroids: a longer arc means more rock"u8);
        draw.AddTriangleFilled(legend + new float2(Kit.Px(5), Kit.Px(45)), legend + new float2(Kit.Px(1), Kit.Px(56)), legend + new float2(Kit.Px(9), Kit.Px(56)), Kit.Color(Theme.Accent));
        Kit.Text(draw, _ui.Sans, Theme.FontCaption, legend + new float2(Kit.Px(20), Kit.Px(44)), Theme.TextMute, "You, and the way you are facing"u8);

        hint.X += Kit.KeyHint(draw, hint, "DRAG"u8, "Pan"u8);
        hint.X += Kit.KeyHint(draw, hint, "WHEEL"u8, "Zoom"u8);
        hint.X += Kit.KeyHint(draw, hint, "PGUP / PGDN"u8, "Layer"u8);
        hint.X += Kit.KeyHint(draw, hint, "RMB"u8, "Set course"u8);
        Kit.KeyHint(draw, hint, "M / ESC"u8, "Close"u8);
        EndRoot();
        Kit.Fade = 1f;
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
