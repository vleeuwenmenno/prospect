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
    private const float MapMinCell = 38f;
    private const float MapMaxCell = 120f;

    private readonly SectorSummary[] _mapCache = new SectorSummary[MapCacheSize];
    private readonly bool[] _mapCacheValid = new bool[MapCacheSize];
    private Galaxy? _mapGalaxy;
    private bool _mapOpen;
    private float _mapTime;
    private float2 _mapPan;
    private float _mapCell = 64f;
    private int _mapLayer;
    private SectorCoord _mapHover;
    private string _mapHoverName = "";
    private bool _mapHasHover;

    /// <summary>True while the player is in space with nothing open over it, so input belongs to the world.</summary>
    public bool WorldInputEnabled => _screen == Screen.Session && !_sessionMenuOpen && !_mapOpen;

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

        BeginRoot("##world"u8, ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        var draw = ImGui.GetWindowDrawList();
        DrawSectorPanel(draw, snapshot, world);
        if (!_mapOpen)
        {
            var pilot = 1f - world.CommandBlend;
            if (pilot > 0.01f)
                DrawFlightReadouts(draw, snapshot, world, pilot);
            if (world.CommandBlend > 0.01f)
                DrawCommandOverlay(draw, snapshot, world, world.CommandBlend);
        }

        EndRoot();

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

        // Speed and heat, bottom centre.
        var center = new float2(display.X * 0.5f, display.Y - Kit.Px(118));
        var speedSize = Kit.Measure(_ui.Mono, 44f, Kit.F($"{speed:0}"));
        Kit.Text(draw, _ui.Mono, 44f, center - new float2(speedSize.X * 0.5f, 0), Theme.Text, Kit.F($"{speed:0}"));
        var unit = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "M/S"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, center + new float2(-unit.X * 0.5f, Kit.Px(52)), Theme.TextMute, "M/S"u8);

        var barSize = new float2(Kit.Px(220), Kit.Px(6));
        var barMin = center + new float2(-barSize.X * 0.5f, Kit.Px(76));
        var heatColor = own.BoostLocked ? Theme.Bad : own.Heat > 0.7f ? Theme.Accent : Theme.Info;
        draw.AddRectFilled(barMin, barMin + barSize, Kit.Color(Theme.Void, 0.7f));
        draw.AddRectFilled(barMin, barMin + new float2(barSize.X * own.Heat, barSize.Y), Kit.Color(heatColor));
        draw.AddRect(barMin - new float2(1, 1), barMin + barSize + new float2(1, 1), Kit.Color(Theme.LineStrong));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, barMin + new float2(-Kit.Px(56), -Kit.Px(4)), own.BoostLocked ? Theme.Bad : Theme.TextMute, own.BoostLocked ? "COOLING"u8 : "BOOST"u8);

        var dampeners = snapshot.Dampeners;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, barMin + new float2(barSize.X + Kit.Px(14), -Kit.Px(4)), dampeners ? Theme.Good : Theme.Accent, dampeners ? "DAMPENERS ON"u8 : "DAMPENERS OFF"u8);

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
        Kit.KeyHint(draw, hint, pad ? "START"u8 : "ESC"u8, "Menu"u8);
        Kit.Fade = previousFade;
    }

    /// <summary>Markers and readouts for the command view: hulls, their orders and the point under the pointer.</summary>
    private void DrawCommandOverlay(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var previousFade = Kit.Fade;
        Kit.Fade = alpha;
        var display = _ui.DisplaySize;
        var planeHeight = world.CommandFocus.Y;

        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var position = hull.IsOwn ? world.OwnPosition : hull.Position;
            if (!world.Project(position, out var screen))
                continue;

            // A stalk down to the command plane shows how far above or below it the hull is.
            var onPlane = new Double3(position.X, planeHeight, position.Z);
            if (Math.Abs(position.Y - planeHeight) > 1.0 && world.Project(onPlane, out var foot))
            {
                draw.AddLine(screen, foot, Kit.Color(Theme.Info, 0.5f));
                draw.AddCircleFilled(foot, Kit.Px(2.5f), Kit.Color(Theme.Info, 0.7f), 8);
            }

            var color = hull.IsOwn ? Theme.Accent : Theme.Info;
            var s = Kit.Px(hull.IsOwn ? 9 : 7);
            Span<float2> diamond = [screen + new float2(0, -s), screen + new float2(s, 0), screen + new float2(0, s), screen + new float2(-s, 0)];
            draw.AddPolyline(diamond, Kit.Color(color), ImDrawFlags.Closed, Kit.Px(1.5f));

            var label = screen + new float2(s + Kit.Px(8), -Kit.Px(14));
            if (hull.IsOwn)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, label, color, "COMMAND POD"u8);
            else
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, label, color, Kit.F($"PLAYER {hull.Owner}"));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, label + new float2(0, Kit.Px(15)), Theme.TextDim, Kit.F($"{hull.Velocity.Length():0} M/S"));

            if (hull.Order.Kind == OrderKind.MoveTo && world.Project(hull.Order.Target, out var target))
            {
                draw.AddLine(screen, target, Kit.Color(color, 0.55f), Kit.Px(1.5f));
                DrawTargetMarker(draw, target, color);
                var distance = hull.Order.Target.RelativeTo(position).Length();
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, target + new float2(Kit.Px(14), -Kit.Px(6)), color, distance >= 1000f ? Kit.F($"{distance / 1000f:0.00} KM") : Kit.F($"{distance:0} M"));
            }
        }

        // The point a move order would go to.
        var pointer = _app.Devices.MousePosition;
        if (WorldInputEnabled && world.GroundPoint(pointer, out var ground) && world.Project(ground, out var groundScreen))
        {
            draw.AddCircle(groundScreen, Kit.Px(7), Kit.Color(Theme.Text, 0.7f), 20, Kit.Px(1.2f));
            var range = ground.RelativeTo(world.OwnPosition).Length();
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, groundScreen + new float2(Kit.Px(14), Kit.Px(8)), Theme.TextDim, range >= 1000f ? Kit.F($"{range / 1000f:0.00} KM") : Kit.F($"{range:0} M"));
        }

        var title = "COMMAND"u8;
        var titleSize = Kit.Measure(_ui.SansMedium, Theme.FontSmall, title);
        var top = new float2(display.X * 0.5f, Kit.Px(Theme.Space5));
        Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, top - new float2(titleSize.X * 0.5f, 0), Theme.Accent, title);
        var status = world.CommandFollowing
            ? Kit.F($"FOLLOWING POD   RANGE {world.CommandDistance / 1000f:0.00} KM")
            : Kit.F($"FREE CAMERA   RANGE {world.CommandDistance / 1000f:0.00} KM");
        var statusSize = Kit.Measure(_ui.Mono, Theme.FontCaption, status);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, top + new float2(-statusSize.X * 0.5f, Kit.Px(22)), Theme.TextDim, status);

        var hint = new float2(Kit.Px(Theme.Space5), display.Y - Kit.Px(56));
        hint.X += Kit.KeyHint(draw, hint, "RMB"u8, "Move here"u8);
        hint.X += Kit.KeyHint(draw, hint, "RMB DRAG"u8, "Orbit"u8);
        hint.X += Kit.KeyHint(draw, hint, "WHEEL"u8, "Zoom"u8);
        hint.X += Kit.KeyHint(draw, hint, "WASD"u8, "Pan"u8);
        hint.X += Kit.KeyHint(draw, hint, "SPACE / CTRL"u8, "Plane"u8);
        hint.X += Kit.KeyHint(draw, hint, "F"u8, "Follow"u8);
        hint.X += Kit.KeyHint(draw, hint, "H"u8, "Halt"u8);
        Kit.KeyHint(draw, hint, "N"u8, "Pilot"u8);
        Kit.Fade = previousFade;
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
        DrawDim(0.86f * fade);
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

        // Screen x runs with sector X, screen y with sector Z, so "up" on the map is -Z, the way hulls face.
        var firstX = here.X + (int)MathF.Floor((0 - origin.X) / cell + 0.5f);
        var lastX = here.X + (int)MathF.Floor((display.X - origin.X) / cell + 0.5f);
        var firstZ = here.Z + (int)MathF.Floor((0 - origin.Y) / cell + 0.5f);
        var lastZ = here.Z + (int)MathF.Floor((display.Y - origin.Y) / cell + 0.5f);

        for (var z = firstZ; z <= lastZ; z++)
            for (var x = firstX; x <= lastX; x++)
            {
                var coord = new SectorCoord(x, _mapLayer, z);
                ref var summary = ref MapSummary(galaxy, coord);
                var center = origin + new float2((x - here.X) * cell, (z - here.Z) * cell);
                var min = center - new float2(cell * 0.5f, cell * 0.5f);
                var max = center + new float2(cell * 0.5f, cell * 0.5f);
                var hovered = mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y;

                draw.AddRect(min, max, Kit.Color(Theme.Line, 0.5f));
                var richness = summary.AsteroidCount / maxRocks;
                if (summary.AsteroidCount > 0)
                {
                    var inset = cell * 0.5f * (0.34f + 0.3f * richness);
                    draw.AddRect(center - new float2(inset, inset), center + new float2(inset, inset), Kit.Color(Theme.Info, 0.18f + 0.5f * richness));
                }

                var star = galaxy.StarClasses[summary.StarClass];
                draw.AddCircleFilled(center, cell * (0.06f + 0.012f * summary.StarClass), Kit.Color(StarColor(star.Color)), 16);

                if (hovered)
                {
                    draw.AddRect(min, max, Kit.Color(Theme.Text, 0.9f), 0, default, Kit.Px(1.5f));
                    if (!_mapHasHover || _mapHover != coord)
                        _mapHoverName = galaxy.NameOf(coord);
                    _mapHover = coord;
                    _mapHasHover = true;
                }
            }

        // The player's sector and heading.
        if (_mapLayer == here.Y)
        {
            var local = world.OwnPosition - galaxy.CenterOf(here);
            var marker = origin + new float2((float)(local.X / galaxy.SectorSize) * cell, (float)(local.Z / galaxy.SectorSize) * cell);
            draw.AddRect(origin - new float2(cell * 0.5f, cell * 0.5f), origin + new float2(cell * 0.5f, cell * 0.5f), Kit.Color(Theme.Accent), 0, default, Kit.Px(2));
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
        }

        return ref _mapCache[index];
    }

    /// <summary>A star's light colour as an interface colour.</summary>
    private static uint StarColor(Vector3 color) =>
        (uint)(Math.Clamp(color.X, 0f, 1f) * 255f) << 16 | (uint)(Math.Clamp(color.Y, 0f, 1f) * 255f) << 8 | (uint)(Math.Clamp(color.Z, 0f, 1f) * 255f);
}
