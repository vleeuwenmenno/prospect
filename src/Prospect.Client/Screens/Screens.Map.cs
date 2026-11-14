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
    private MapTuning? _mapTuning;
    private readonly MapNavigation _mapNav = new();
    private readonly MapCardState _mapCard = new();
    private ChartIndex? _charts;
    private readonly SensorCoverage _coverage = new();
    private readonly ScanTracker _scan = new();

    // What a charted sector shows of the natural world: field and cloud outlines, worked out on a worker.
    private MapFeatureCache? _featureCache;

    // The legend names the star types in capitals; made once per galaxy, never per frame.
    private string[] _starNamesUpper = [];
    private Galaxy? _starNamesGalaxy;

    private string StarNameUpper(Galaxy galaxy, int index)
    {
        if (!ReferenceEquals(galaxy, _starNamesGalaxy))
        {
            _starNamesGalaxy = galaxy;
            _starNamesUpper = [.. galaxy.StarClasses.Select(star => star.Name.ToUpperInvariant())];
        }

        return _starNamesUpper[index];
    }

    private MapTuning MapLook => _mapTuning ??= MapTuning.Load();

    /// <summary>
    /// Keeps the client's picture of what the player knows up to date: the chart lookup, live sensor coverage and the
    /// scan readout. Called every frame in space, map open or not.
    /// </summary>
    private void UpdateExploration(SessionSnapshot snapshot, WorldView world)
    {
        var exploration = snapshot.Exploration;
        if (_charts is null || _charts.Count > exploration.Charts.Length || _chartsCapacity != exploration.Charts.Length)
        {
            _charts = new ChartIndex(exploration.Charts.Length);
            _chartsCapacity = exploration.Charts.Length;
        }

        _charts.Refresh(exploration);
        _exploration = exploration;
        _coverage.Collect(snapshot, world);
        _scan.Update(exploration.Scan, world.Own.Id, world.Sector.Coord, _coverage.OwnCanChart, _ui.DeltaTime);
    }

    private int _chartsCapacity;

    /// <summary>This frame's snapshot's exploration view, for the parts of the map drawn without the snapshot at hand.</summary>
    private ExplorationView? _exploration;

    /// <summary>True when the player holds a chart of the sector.</summary>
    private bool Charted(SectorCoord sector) => _charts?.Contains(sector) == true;

    /// <summary>The map's projection for this frame's centre, zoom, layer and tilt.</summary>
    private MapProjection Projection(MapTuning look, float2 display) =>
        new(display * 0.5f, _mapCenter, _mapCell, _mapNav.Layer, Kit.EaseOut(_mapNav.TiltBlend) * look.TiltDegrees * (MathF.PI / 180f), look.TiltLayerSpacing);

    /// <summary>
    /// The galaxy map. Every grid cell is a sector of the chosen layer. What the player knows decides what shows:
    /// uncharted space lies under a veil and is an anonymous destination; a charted sector shows its star, name and
    /// surveyed asteroid fields as recorded, never later changes; live sensor coverage and the contacts in it are
    /// drawn apart from both, and lost contacts as fading markers. Hover a sector for its card, click to pin it,
    /// Escape or a click outside the galaxy dismisses it. Page Up and Page Down change layer, the tilt key or the
    /// layer bar tilts the map to show the layers around the chosen one, Space centres on the player, right-click a
    /// sector for the course menu.
    /// </summary>
    private void DrawMap(SessionSnapshot snapshot, WorldView world)
    {
        var galaxy = world.Galaxy!;
        if (!ReferenceEquals(galaxy, _mapGalaxy))
        {
            _mapGalaxy = galaxy;
            Array.Clear(_mapCacheValid);
        }

        var look = MapLook;
        _mapTime += _ui.DeltaTime;
        var fade = Kit.EaseOut(_mapTime / Theme.ScreenEnter);
        Kit.Fade = fade;
        CoversWorld = WorldCover.MapHides(fade);
        var display = _ui.DisplaySize;
        var io = ImGui.GetIO();

        // Everything drawn over the map is placed before any input is read, so the map never takes a press, wheel
        // or drag that lands on it, even on the frame it first appears.
        world.PruneSelection();
        var units = LayoutMapUnits(snapshot, world);
        _mapPointer.Block(units.Min, units.Size);
        _mapPointer.Block(float2.Zero, new float2(display.X, HudLayout.TopBarBottom(Kit.Scale)));
        var legend = MapLegend.Layout(display, Kit.Scale, units, MapLegend.FeatureRows + galaxy.StarClasses.Length, look.LegendRowHeight);
        if (legend.Shown)
            _mapPointer.Block(legend.Min, legend.Size);
        var layerMin = MapLayerBar.Min(display, HudTop, Kit.Scale);
        var layerSize = MapLayerBar.Size(Kit.Scale);
        _mapPointer.Block(layerMin, layerSize);
        BlockMapForTip();
        if (_mapMenuOpen)
        {
            _mapMenuTime += _ui.DeltaTime;
            _mapPointer.Block(MapCourseMenu.Shown(_mapMenuMin, _mapMenuTime, Kit.Scale), _mapMenuSize);
        }

        var cardMin = float2.Zero;
        var cardSize = float2.Zero;
        if (_mapCard.Pinned)
        {
            cardSize = CardSize(snapshot, world, galaxy, _mapCard.Sector, false);
            cardMin = PlaceCard(Projection(look, display), _mapCard.Sector, cardSize);
            _mapPointer.Block(cardMin, cardSize);
        }

        _mapPointer.Begin(display);
        var pointer = _mapPointer;
        var draw = ImGui.GetWindowDrawList();
        var shape = galaxy.Shape;
        var here = world.Sector.Coord;
        var size = galaxy.SectorSize;
        var middle = display * 0.5f;
        var mouse = _mapHoverPinned ? middle : io.MousePos;
        var menuOpen = _mapMenuOpen;

        // Input. W A S D, the arrow keys and middle-drag pan; zooming keeps the sector under the pointer in place.
        if (!menuOpen)
            PanMap(pointer, mouse, here, world);
        MapBounds.Layers(shape, look.EdgeFalloff, out var lowLayer, out var highLayer);
        _mapNav.Layer = Math.Clamp(_mapNav.Layer, Math.Min(lowLayer, here.Y), Math.Max(highLayer, here.Y));
        _mapNav.Advance(_ui.DeltaTime, look.TiltSeconds);

        if (pointer.Pressed && menuOpen)
        {
            _mapMenuOpen = false;
            pointer.CancelBox();
        }

        var boxReleased = pointer.BoxReleased;
        if (Bridge is { } switchBridge)
            world.SwitchToSelectedFromMap(switchBridge, WorldView.MapSwitchKeyAvailable(_sessionMenuOpen, SocialSurfaceOpen, _controlsOpen, _ui.WantsTextInput));

        // Escape closes the course menu first, then the pinned card, and the map only after that.
        if (BackPressed())
        {
            if (_mapMenuOpen)
                _mapMenuOpen = false;
            else if (!_mapCard.Dismiss())
                _mapOpen = false;
        }

        var view = Projection(look, display);
        var cell = view.Cell;
        var cellZ = view.CellZ;
        var layer = _mapNav.Layer;

        DrawMapBackdrop(draw, view, shape, display);
        DrawMapFog(draw, view, galaxy, display, look);

        // Grid. Sector lines fade in as the cells grow; every tenth and hundredth line is always the stronger.
        var topLeft = view.OnLayer(float2.Zero);
        var bottomRight = view.OnLayer(display);
        var origin = view.ToScreen(0, 0);
        var cells = new float2(cell, cellZ);
        DrawMapGrid(draw, display, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y, 1, Math.Clamp((cell - 9f) / 26f, 0f, 1f) * 0.2f, origin, cells);
        DrawMapGrid(draw, display, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y, 10, Math.Clamp((cell * 10f - 30f) / 120f, 0f, 1f) * 0.34f, origin, cells);
        DrawMapGrid(draw, display, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y, 100, Math.Clamp((cell * 100f - 30f) / 200f, 0f, 1f) * 0.4f, origin, cells);

        DrawMapBounds(draw, view, shape, look, lowLayer, highLayer);
        if (view.Tilted)
            DrawTiltLayers(draw, view, galaxy, look);
        DrawCoverage(draw, view, galaxy, look);
        if (cell >= MapSectorDetailCell)
            DrawChartedSectors(draw, view, galaxy, topLeft, bottomRight, look);

        // The player's hulls. Releasing the left button selects among them: the nearest to a click, or all inside a
        // dragged box. A click that hits no hull keeps the selection, as it pins a sector card instead.
        var boxMin = new float2(Math.Min(pointer.BoxStart.X, mouse.X), Math.Min(pointer.BoxStart.Y, mouse.Y));
        var boxMax = new float2(Math.Max(pointer.BoxStart.X, mouse.X), Math.Max(pointer.BoxStart.Y, mouse.Y));
        var additive = io.KeyShift || io.KeyCtrl;
        if (boxReleased && pointer.BoxMoved && !additive)
            world.ClearSelection();
        var clicked = 0u;
        var clickedDistance = Kit.Px(14) * Kit.Px(14);
        var unitHover = new MapUnitHover(Kit.Px(look.UnitHoverPixels));
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var position = world.HullPosition(hull);
            var hullLayer = galaxy.SectorOf(position).Y;
            var mine = hull.Owner == snapshot.PlayerId;
            if (!LayerShown(view, hullLayer, look))
            {
                if (mine)
                    DrawLayerMarker(draw, view, (float)(position.X / size), (float)(position.Z / size), hullLayer - layer, Theme.Accent);
                continue;
            }

            var at = view.ToScreen((float)(position.X / size), (float)(position.Y / size), (float)(position.Z / size));
            DrawStalk(draw, view, position, size, at, mine ? Theme.Accent : Theme.Info);
            if (!mine)
            {
                // A hull the server lets the player see is live: a filled marker in the sensor colour.
                DrawContactMarker(draw, at, Kit.Px(5), Theme.Info, 1f, true, snapshot.Content.Designs[Math.Min(hull.Design, snapshot.Content.Designs.Length - 1)].Class);
                continue;
            }

            unitHover.Consider(hull.Id, at, mouse);
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

        DrawLostContacts(draw, snapshot, view, galaxy, look);

        // The unit under the pointer is named beside it, small and muted, with no box. The sector's preview card
        // stays away while it is: the pointer is on the unit, not asking about the sector beneath it.
        var overUnit = unitHover.Hit && (_mapHoverPinned || pointer.Hovered) && !menuOpen && !pointer.Boxing;
        if (overUnit && world.TryGetHull(unitHover.Hull, out var named))
        {
            var designs = snapshot.Content.Designs;
            var label = Kit.F($"{designs[Math.Min(named.Design, designs.Length - 1)].Name} {(named.Id & 0xFFFF) + 1}");
            Kit.Text(draw, _ui.Sans, Theme.FontCaption, unitHover.At + new float2(Kit.Px(12), -Kit.Px(7)), Theme.TextDim, label, look.UnitLabelAlpha);
        }

        // The plotted jump: a dotted route underneath the hull's arrow, visible before its order starts.
        var player = new float2((float)(world.OwnPosition.X / size), (float)(world.OwnPosition.Z / size));
        var playerScreen = view.ToScreen(player.X, (float)(world.OwnPosition.Y / size), player.Y);
        if (world.NavTarget is { } nav)
        {
            var navSector = galaxy.SectorOf(nav);
            var end = view.ToScreen((float)(nav.X / size), (float)(nav.Y / size), (float)(nav.Z / size));
            if (view.Tilted ? LayerShown(view, navSector.Y, look) : MapRoute.Shows(here.Y, navSector.Y, layer, out _))
            {
                var line = view.Tilted ? LayerShown(view, here.Y, look) : here.Y == layer && navSector.Y == layer;
                if (line)
                    MapRoute.Dotted(draw, playerScreen, end, new HudRect(0f, 0f, display.X, display.Y), Kit.Px(MapRoute.Dash), Kit.Px(MapRoute.Space), Kit.Color(Theme.Info, 0.8f), Kit.Px(1.5f));
                DrawTargetMarker(draw, end, Theme.Info);
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, end + new float2(Kit.Px(18), -Kit.Px(7)), Theme.Info, "NAV"u8);
            }
            else
            {
                DrawLayerMarker(draw, view, (float)(nav.X / size), (float)(nav.Z / size), navSector.Y - layer, Theme.Info);
            }
        }

        // The hull being flown, as an arrow along its heading.
        if (LayerShown(view, here.Y, look))
        {
            var heading = Vector3.Transform(-Vector3.UnitZ, world.OwnOrientation);
            var flat = new float2(heading.X, heading.Z * view.Cos);
            var length = MathF.Sqrt(flat.X * flat.X + flat.Y * flat.Y);
            flat = length > 0.01f ? flat / length : new float2(0, -1);
            var side = new float2(-flat.Y, flat.X);
            var s = Kit.Px(8);
            draw.AddTriangleFilled(playerScreen + flat * s * 1.4f, playerScreen - flat * s * 0.7f + side * s * 0.8f, playerScreen - flat * s * 0.7f - side * s * 0.8f, Kit.Color(Theme.Accent));
            if (cell >= MapSectorDetailCell && here.Y == layer)
                SectorBrackets(draw, view, here, Kit.Color(Theme.Accent), 2f);
        }

        // A standing order's destination.
        var order = world.Own.Order;
        if (order.Kind != OrderKind.None && LayerShown(view, galaxy.SectorOf(order.Target).Y, look))
        {
            var destination = view.ToScreen((float)(order.Target.X / size), (float)(order.Target.Y / size), (float)(order.Target.Z / size));
            draw.AddLine(playerScreen, destination, Kit.Color(Theme.Accent, 0.5f));
            DrawTargetMarker(draw, destination, Theme.Accent);
        }

        // The sector under the pointer, and the card: a click pins it, outside the galaxy dismisses it.
        var hover = view.SectorAt(mouse);
        var pointerOnMap = _mapHoverPinned || pointer.Hovered;
        var inBounds = MapBounds.Contains(shape, look.EdgeFalloff, hover);
        if (pointerOnMap)
        {
            SectorBrackets(draw, view, hover, Kit.Color(Theme.Text, _mapCard.Pinned ? 0.45f : 0.85f), 7f);
            if (pointer.RightClicked)
                OpenMapMenu(galaxy, hover, mouse);
        }

        if (boxReleased && !pointer.BoxMoved && !menuOpen)
            _mapCard.Click(hover, inBounds);

        if (_mapMenuOpen && _mapMenuSector.Y == layer)
            SectorBrackets(draw, view, _mapMenuSector, Kit.Color(Theme.Accent), 7f);
        if (_mapCard.Pinned)
        {
            if (_mapCard.Sector.Y == layer)
                SectorBrackets(draw, view, _mapCard.Sector, Kit.Color(Theme.Text), 7f);
            else
                DrawLayerMarker(draw, view, _mapCard.Sector.X, _mapCard.Sector.Z, _mapCard.Sector.Y - layer, Theme.Text);
        }

        // The world interface is beneath the map, so the map shows the sector panel itself.
        if (_sectorInfo.Open)
            DrawSectorPanel(draw, snapshot, world);
        DrawMapHeading(draw, world, units, display, size);
        if (legend.Shown)
            DrawMapLegend(draw, galaxy, legend, look);

        if (world.ControlOutOfSector)
        {
            var notice = Kit.F($"Outside your sector - travel there to take control ({world.SwitchKeyName})");
            var noticeSize = Kit.Measure(_ui.SansMedium, Theme.FontSmall, notice);
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, new float2((display.X - noticeSize.X) * 0.5f, display.Y - Kit.Px(56)), Theme.Bad, notice);
        }

        // A preview while nothing is pinned. It is drawn on the map, not as a window, so the pointer cannot take it.
        if (!_mapCard.Pinned && pointerOnMap && !overUnit && !_mapMenuOpen && _mapCard.Shown(true, hover, out var previewSector, out _))
        {
            var previewSize = CardSize(snapshot, world, galaxy, previewSector, true);
            DrawSectorCard(draw, snapshot, world, galaxy, previewSector, PlaceCard(view, previewSector, previewSize), previewSize, true);
        }

        EndRoot();

        DrawMapUnits(snapshot, world, galaxy, units, fade);
        DrawMapLayerBar(world, galaxy, layerMin, layerSize, lowLayer, highLayer, fade);
        if (_mapCard.Pinned)
            DrawPinnedCard(snapshot, world, galaxy, cardMin, cardSize, fade);
        if (_mapMenuOpen)
            DrawMapMenu(snapshot, world, galaxy, fade);
        Kit.Fade = 1f;
    }

    /// <summary>Pans and zooms from the keyboard and the map's own middle drag and wheel; changes layer and tilt.</summary>
    private void PanMap(MapPointer pointer, float2 mouse, SectorCoord here, WorldView world)
    {
        var devices = _app.Devices;
        var actions = _app.Actions;
        var display = _ui.DisplaySize;
        var look = MapLook;
        var keyboard = !_ui.WantsTextInput;
        var panX = Math.Clamp(actions.Axis(devices, InputAxis.Strafe, keyboard) + actions.Axis(devices, InputAxis.Yaw, keyboard), -1f, 1f);
        var panY = Math.Clamp(actions.Axis(devices, InputAxis.Forward, keyboard) + actions.Axis(devices, InputAxis.Pitch, keyboard), -1f, 1f);
        var view = Projection(look, display);
        _mapCenter += new float2(panX, -panY) * (display.Y * 0.9f / _mapCell * _ui.DeltaTime);
        _mapCenter -= new float2(pointer.Pan.X / view.Cell, pointer.Pan.Y / view.CellZ);
        if (pointer.Wheel != 0)
        {
            var under = view.OnLayer(mouse);
            _mapCell = Math.Clamp(_mapCell * MathF.Pow(1.18f, pointer.Wheel), MapMinCell, MapMaxCell);
            var after = Projection(look, display).OnLayer(mouse);
            _mapCenter += under - after;
        }

        if (_mapNav.HandleKeys(actions, devices, keyboard))
            Flattened();
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
        {
            var size = world.Galaxy!.SectorSize;
            _mapCenter = new float2((float)(world.OwnPosition.X / size), (float)(world.OwnPosition.Z / size));
            _mapNav.Layer = here.Y;
        }
    }

    /// <summary>Returning from the tilted view shows the pinned sector's layer, as the card is what was chosen.</summary>
    private void Flattened()
    {
        if (_mapCard.Pinned)
            _mapNav.Layer = _mapCard.Sector.Y;
    }

    /// <summary>Whether a layer is drawn in full: the chosen one, or in the tilted view one of those around it.</summary>
    private bool LayerShown(in MapProjection view, int layer, MapTuning look) =>
        layer == view.Layer || (view.Tilted && Math.Abs(layer - view.Layer) <= look.TiltLayers);

    /// <summary>Black, the galaxy, and a field of far stars that slides more slowly than the map.</summary>
    private void DrawMapBackdrop(ImDrawListPtr draw, in MapProjection view, GalaxyShape shape, float2 display)
    {
        draw.AddRectFilled(float2.Zero, display, Kit.Color(0x020305));
        var reach = shape.Radius * Render.GalaxyImage.Extent;
        var galaxyAlpha = Math.Clamp(1.1f - view.Cell / 60f, 0.22f, 1f) * shape.LayerFalloff(view.Layer);
        draw.AddImage(_app.GalaxyImageRef, view.ToScreen(shape.Center.X - reach, shape.Center.Z - reach), view.ToScreen(shape.Center.X + reach, shape.Center.Z + reach),
            float2.Zero, float2.One, Kit.Color(0xFFFFFF, galaxyAlpha));

        const float tile = 150f;
        var slide = view.Center * (view.Cell * 0.3f);
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
    }

    /// <summary>
    /// The veil over uncharted space, and each charted sector of the chosen layer lifted out of it: the galaxy
    /// image drawn again, undimmed, over the sector's cell, with a tint and an outline. Far out, where cells are a
    /// few pixels, a charted sector is a lit square.
    /// </summary>
    private void DrawMapFog(ImDrawListPtr draw, in MapProjection view, Galaxy galaxy, float2 display, MapTuning look)
    {
        draw.AddRectFilled(float2.Zero, display, Kit.Color(0x020305, look.FogAlpha));
        if (_charts is null)
            return;
        if (_exploration is not { } exploration)
            return;

        var shape = galaxy.Shape;
        var reach = shape.Radius * Render.GalaxyImage.Extent;
        var imageMin = new float2(shape.Center.X - reach, shape.Center.Z - reach);
        var span = reach * 2f;
        var galaxyAlpha = Math.Clamp(1.1f - view.Cell / 60f, 0.22f, 1f) * shape.LayerFalloff(view.Layer);
        var half = new float2(view.Cell * 0.5f, view.CellZ * 0.5f);
        var minimum = Kit.Px(2f);
        for (var i = 0; i < exploration.ChartCount; i++)
        {
            var sector = exploration.Charts[i].Sector;
            if (sector.Y != view.Layer)
                continue;
            var center = view.ToScreen(sector.X, sector.Z);
            var extent = new float2(Math.Max(half.X, minimum), Math.Max(half.Y, minimum));
            if (center.X + extent.X < 0 || center.Y + extent.Y < 0 || center.X - extent.X > display.X || center.Y - extent.Y > display.Y)
                continue;
            var uvMin = new float2((sector.X - 0.5f - imageMin.X) / span, (sector.Z - 0.5f - imageMin.Y) / span);
            var uvMax = new float2((sector.X + 0.5f - imageMin.X) / span, (sector.Z + 0.5f - imageMin.Y) / span);
            draw.AddRectFilled(center - extent, center + extent, Kit.Color(0x020305, look.FogAlpha));
            draw.AddImage(_app.GalaxyImageRef, center - extent, center + extent, uvMin, uvMax, Kit.Color(0xFFFFFF, galaxyAlpha));
            draw.AddRectFilled(center - extent, center + extent, Kit.Color(Theme.Info, look.ChartedAlpha));
            if (view.Cell >= 9f)
                draw.AddRect(center - extent, center + extent, Kit.Color(Theme.Info, 0.35f));
        }
    }

    /// <summary>
    /// Live sensor coverage on the chosen layer: each sensor's sphere cut by the layer's middle plane, filled and
    /// ringed. In the tilted view each sphere's outline also shows, around the sensor at its own height.
    /// </summary>
    private void DrawCoverage(ImDrawListPtr draw, in MapProjection view, Galaxy galaxy, MapTuning look)
    {
        var size = galaxy.SectorSize;
        var plane = view.Layer * size;
        var minimum = Kit.Px(look.MinCoveragePixels);
        for (var i = 0; i < _coverage.Count; i++)
        {
            var p = _coverage.Positions[i];
            var center = view.ToScreen((float)(p.X / size), view.Layer, (float)(p.Z / size));
            if (view.Tilted)
            {
                var sphere = view.ToScreen((float)(p.X / size), (float)(p.Y / size), (float)(p.Z / size));
                draw.AddCircle(sphere, Math.Max((float)(_coverage.Radius / size) * view.Cell, minimum), Kit.Color(Theme.Good, look.LiveRingAlpha * 0.35f), 48, Kit.Px(1f));
            }

            var radius = _coverage.CrossSection(i, plane);
            if (radius <= 0)
                continue;
            var rx = Math.Max((float)(radius / size) * view.Cell, minimum);
            var ry = Math.Max((float)(radius / size) * view.CellZ, minimum * view.Cos);
            Ellipse(draw, center, rx, ry, Theme.Good, look.LiveFillAlpha, Theme.Good, look.LiveRingAlpha, Kit.Px(1f));
        }
    }

    /// <summary>
    /// The playable bounds: the galaxy's rim on the chosen layer, where the disc holds no more matter. Nothing stops
    /// flight there yet; the line says where charts can be useful.
    /// </summary>
    private void DrawMapBounds(ImDrawListPtr draw, in MapProjection view, GalaxyShape shape, MapTuning look, int low, int high)
    {
        var edge = MapBounds.EdgeRadius(shape);
        var center = view.ToScreen(shape.Center.X, shape.Center.Z);
        var outside = view.Layer < low || view.Layer > high;
        Ellipse(draw, center, edge * view.Cell, edge * view.CellZ, 0, 0f, outside ? Theme.Bad : Theme.LineStrong, 0.8f, Kit.Px(1.5f));
        var label = center + new float2(0, -edge * view.CellZ - Kit.Px(18));
        if (label.Y > HudTop && label.Y < _ui.DisplaySize.Y)
        {
            var text = outside ? "OUTSIDE THE GALAXY'S LAYERS"u8 : "GALAXY EDGE"u8;
            var width = Kit.Measure(_ui.Mono, Theme.FontCaption, text).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, label - new float2(width * 0.5f, 0), outside ? Theme.Bad : Theme.TextMute, text);
        }
    }

    /// <summary>
    /// The tilted view's other layers: a frame a few sectors across around the view's centre on each, with its
    /// number, and the charted sectors on it as outlines, fainter the further from the chosen layer.
    /// </summary>
    private void DrawTiltLayers(ImDrawListPtr draw, in MapProjection view, Galaxy galaxy, MapTuning look)
    {
        var blend = Math.Clamp(view.Sin / MathF.Sin(look.TiltDegrees * (MathF.PI / 180f)), 0f, 1f);
        var cx = MathF.Round(view.Center.X);
        var cz = MathF.Round(view.Center.Y);
        const float frame = 1.5f;
        var exploration = _exploration;
        for (var offset = -look.TiltLayers; offset <= look.TiltLayers; offset++)
        {
            var y = view.Layer + offset;
            var alpha = blend * (offset == 0 ? 0.9f : 0.55f / Math.Abs(offset));
            var a = view.ToScreen(cx - frame, y, cz - frame);
            var b = view.ToScreen(cx + frame, y, cz + frame);
            var color = offset == 0 ? Theme.Accent : y > view.Layer ? Theme.Info : Theme.TextDim;
            draw.AddRect(a, b, Kit.Color(color, alpha * 0.6f), 0f, ImDrawFlags.None, Kit.Px(offset == 0 ? 1.5f : 1f));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(a.X - Kit.Px(30), (a.Y + b.Y) * 0.5f - Kit.Px(7)), color, Kit.F($"{y:+0;-0;0}"), alpha);
            if (offset == 0 || exploration is null)
                continue;
            var half = new float2(view.Cell * 0.5f, view.CellZ * 0.5f);
            for (var i = 0; i < exploration.ChartCount; i++)
            {
                var sector = exploration.Charts[i].Sector;
                if (sector.Y != y)
                    continue;
                var center = view.ToScreen(sector.X, y, sector.Z);
                draw.AddRect(center - half, center + half, Kit.Color(Theme.Info, alpha * 0.7f));
            }
        }
    }

    /// <summary>
    /// What a charted sector of the chosen layer holds, as surveyed: its name, a small muted star icon in the
    /// corner, and close in the real outlines of the asteroid fields and gas clouds that reach it. Nothing is drawn
    /// for an uncharted sector: it is an anonymous destination. A field or cloud is drawn only inside charted
    /// sectors: the part of one that lies across a wall in a sector the player has not charted stays hidden until
    /// that sector is charted, and a feature across two charted sectors is drawn whole, each sector its own half.
    /// </summary>
    private void DrawChartedSectors(ImDrawListPtr draw, in MapProjection view, Galaxy galaxy, float2 topLeft, float2 bottomRight, MapTuning look)
    {
        var firstX = (int)MathF.Floor(topLeft.X + 0.5f);
        var lastX = (int)MathF.Floor(bottomRight.X + 0.5f);
        var firstZ = (int)MathF.Floor(topLeft.Y + 0.5f);
        var lastZ = (int)MathF.Floor(bottomRight.Y + 0.5f);
        var cell = view.Cell;
        _featureCache ??= new MapFeatureCache(job => _app.Jobs.Schedule(job), look.FieldCoreShare);
        _featureCache.Update(galaxy);
        for (var z = firstZ; z <= lastZ; z++)
            for (var x = firstX; x <= lastX; x++)
            {
                var coord = new SectorCoord(x, view.Layer, z);
                if (!Charted(coord))
                    continue;
                ref var summary = ref MapSummary(galaxy, coord);
                var position = view.ToScreen(x, z);
                var half = new float2(view.Cell * 0.5f, view.CellZ * 0.5f);

                if (cell >= look.FieldDetailCell && _featureCache.TryGet(galaxy, coord, out var slot))
                    DrawSectorFeatures(draw, view, galaxy, coord, slot, position - half, position + half, look);

                var radius = Math.Min(Kit.Px(look.StarIconSize), cell * 0.12f);
                var iconAt = position + new float2(half.X - radius - Kit.Px(7), -half.Y + radius + Kit.Px(7));
                ref readonly var starClass = ref galaxy.StarClasses[summary.StarClass];
                DrawStarIcon(draw, iconAt, radius, look.ShapeOf(starClass.Name), StarColor(starClass.Color), look);

                if (cell >= 118f)
                {
                    var name = MapName(galaxy, coord);
                    var nameSize = Kit.Measure(_ui.Sans, Theme.FontCaption, name);
                    Kit.Text(draw, _ui.Sans, Theme.FontCaption, new float2(position.X - nameSize.X * 0.5f, position.Y + view.CellZ * 0.27f), Theme.TextDim, name);
                }
            }
    }

    /// <summary>
    /// A charted sector's gas clouds, then its asteroid fields, as their real outlines from the cache, clipped to the
    /// sector's cell: the cloud's fringe and denser body, the field's support and denser core. Each outline point is
    /// projected here, so the tilted view squashes them like everything else on the layer.
    /// </summary>
    private void DrawSectorFeatures(ImDrawListPtr draw, in MapProjection view, Galaxy galaxy, SectorCoord sector, int slot, float2 clipMin, float2 clipMax, MapTuning look)
    {
        var cache = _featureCache!;
        var features = cache.Features(slot);
        var size = (float)galaxy.SectorSize;
        Span<float2> outer = stackalloc float2[MapFootprint.MaxVertices];
        Span<float2> inner = stackalloc float2[MapFootprint.MaxVertices];
        var line = Kit.Px(1f);
        draw.PushClipRect(clipMin, clipMax, true);
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < features.Length; i++)
            {
                var cloud = features[i].Kind == MapFeatureKind.Cloud;
                if (cloud != (pass == 0))
                    continue;
                var whole = Project(view, sector, size, cache.Outer(slot, i), outer);
                var core = Project(view, sector, size, cache.Inner(slot, i), inner);
                if (cloud)
                {
                    var rgb = look.CloudRgb;
                    if (whole.Length >= 3)
                    {
                        draw.AddConvexPolyFilled(whole, Kit.Color(rgb, look.CloudFringeAlpha));
                        draw.AddPolyline(whole, Kit.Color(rgb, look.CloudLineAlpha * 0.4f), ImDrawFlags.Closed, line);
                    }

                    if (core.Length >= 3)
                    {
                        draw.AddConvexPolyFilled(core, Kit.Color(rgb, look.CloudBodyAlpha));
                        draw.AddPolyline(core, Kit.Color(rgb, look.CloudLineAlpha), ImDrawFlags.Closed, line);
                    }
                }
                else
                {
                    var rgb = look.FieldRgb;
                    if (whole.Length < 3)
                        continue;
                    draw.AddConcavePolyFilled(whole, Kit.Color(rgb, look.FieldFillAlpha));
                    if (core.Length >= 3)
                        draw.AddConcavePolyFilled(core, Kit.Color(rgb, look.FieldCoreAlpha));
                    draw.AddPolyline(whole, Kit.Color(rgb, look.FieldLineAlpha), ImDrawFlags.Closed, line);
                }
            }

        draw.PopClipRect();
    }

    /// <summary>Turns outline points (metres from the sector's centre) into screen points on the chosen layer.</summary>
    private static Span<float2> Project(in MapProjection view, SectorCoord sector, float sectorSize, ReadOnlySpan<System.Numerics.Vector2> points, Span<float2> into)
    {
        for (var i = 0; i < points.Length; i++)
            into[i] = view.ToScreen(sector.X + points[i].X / sectorSize, sector.Z + points[i].Y / sectorSize);
        return into[..points.Length];
    }

    /// <summary>
    /// A star's icon: its class's shape in a muted outline, and a small dot in the star's colour at its centre. Small
    /// and quiet on purpose: it is a note on the sector, not a thing to look at.
    /// </summary>
    private static void DrawStarIcon(ImDrawListPtr draw, float2 at, float r, StarIconShape shape, uint rgb, MapTuning look)
    {
        var color = Kit.Color(Theme.TextDim, look.StarIconAlpha);
        var thickness = Kit.Px(1f);
        switch (shape)
        {
            case StarIconShape.Square:
                draw.AddRect(at - new float2(r * 0.85f, r * 0.85f), at + new float2(r * 0.85f, r * 0.85f), color, 0f, ImDrawFlags.None, thickness);
                break;
            case StarIconShape.Diamond:
                draw.AddQuad(at + new float2(0, -r * 1.15f), at + new float2(r * 1.15f, 0), at + new float2(0, r * 1.15f), at + new float2(-r * 1.15f, 0), color, thickness);
                break;
            case StarIconShape.Sparkle:
                {
                    var tip = r * 1.3f;
                    var waist = r * 0.38f;
                    Span<float2> points =
                    [
                        at + new float2(0, -tip), at + new float2(waist, -waist), at + new float2(tip, 0), at + new float2(waist, waist),
                    at + new float2(0, tip), at + new float2(-waist, waist), at + new float2(-tip, 0), at + new float2(-waist, -waist),
                ];
                    draw.AddPolyline(points, color, ImDrawFlags.Closed, thickness);
                    break;
                }

            case StarIconShape.Asterisk:
                {
                    var across = r * 1.15f;
                    var slant = new float2(across * 0.866f, across * 0.5f);
                    draw.AddLine(at + new float2(0, -across), at + new float2(0, across), color, thickness);
                    draw.AddLine(at - slant, at + slant, color, thickness);
                    draw.AddLine(at + new float2(-slant.X, slant.Y), at + new float2(slant.X, -slant.Y), color, thickness);
                    break;
                }

            default:
                draw.AddCircle(at, r, color, 16, thickness);
                break;
        }

        draw.AddCircleFilled(at, Math.Max(1.2f, r * 0.34f), Kit.Color(rgb, look.StarDotAlpha), 8);
    }

    /// <summary>
    /// How many asteroid fields reach a charted sector, once the worker has its outlines; false while it has not.
    /// </summary>
    private bool TryFieldCount(Galaxy galaxy, SectorCoord sector, out int count)
    {
        count = 0;
        if (_featureCache is not { } cache || !Charted(sector) || !cache.TryGet(galaxy, sector, out var slot))
            return false;
        foreach (var feature in cache.Features(slot))
            count += feature.Kind == MapFeatureKind.Field ? 1 : 0;
        return true;
    }

    /// <summary>
    /// Lost ship contacts and stale stations: a hollow marker at the last observed position, fading as the contact
    /// ages, labelled with how long ago it was seen. A remembered marker is never a live target.
    /// </summary>
    private void DrawLostContacts(ImDrawListPtr draw, SessionSnapshot snapshot, in MapProjection view, Galaxy galaxy, MapTuning look)
    {
        var exploration = snapshot.Exploration;
        var size = galaxy.SectorSize;
        var designs = snapshot.Content.Designs;
        var lifetime = Math.Max(1, snapshot.Content.Exploration.ContactTicks);
        for (var i = 0; i < exploration.ContactCount; i++)
        {
            ref var contact = ref exploration.Contacts[i];
            if (contact.Live || contact.Owner == snapshot.PlayerId)
                continue;
            var sectorY = galaxy.SectorOf(contact.Position).Y;
            if (!LayerShown(view, sectorY, look))
                continue;
            var design = designs[Math.Min(contact.Design, designs.Length - 1)];
            var station = design.Class == HullClass.Station;
            var age = snapshot.ServerTick - contact.LostTick;
            var alpha = look.StaleFade * (station ? 1f : Math.Clamp(1f - (float)age / lifetime, 0.15f, 1f));
            var at = view.ToScreen((float)(contact.Position.X / size), (float)(contact.Position.Y / size), (float)(contact.Position.Z / size));
            DrawContactMarker(draw, at, Kit.Px(5), Theme.TextDim, alpha, false, design.Class);
            if (view.Cell >= 34f)
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(Kit.Px(9), -Kit.Px(6)), Theme.TextMute, Kit.F($"LOST {Math.Max(0, age) / (float)Prospect.Engine.Time.TickLoop.SimulationHz:0}S"), alpha);
        }
    }

    /// <summary>A contact's marker: filled when live, hollow when remembered; its shape by hull class.</summary>
    private static void DrawContactMarker(ImDrawListPtr draw, float2 at, float s, uint rgb, float alpha, bool live, HullClass hullClass)
    {
        Span<float2> shape = hullClass switch
        {
            HullClass.Station => [at + new float2(-s, -s), at + new float2(s, -s), at + new float2(s, s), at + new float2(-s, s)],
            HullClass.Fighter => [at + new float2(0, -s), at + new float2(s, s), at + new float2(-s, s)],
            _ => [at + new float2(0, -s), at + new float2(s, 0), at + new float2(0, s), at + new float2(-s, 0)],
        };
        if (live)
            draw.AddConvexPolyFilled(shape, Kit.Color(rgb, alpha));
        else
            draw.AddPolyline(shape, Kit.Color(rgb, alpha), ImDrawFlags.Closed, Kit.Px(1.2f));
    }

    /// <summary>In the tilted view, a line from a hull down or up to the chosen layer's plane, so its height reads.</summary>
    private static void DrawStalk(ImDrawListPtr draw, in MapProjection view, Double3 position, double size, float2 at, uint rgb)
    {
        if (!view.Tilted)
            return;
        var foot = view.ToScreen((float)(position.X / size), view.Layer, (float)(position.Z / size));
        if (MathF.Abs(foot.Y - at.Y) < 1f)
            return;
        draw.AddLine(at, foot, Kit.Color(rgb, 0.45f));
        draw.AddCircleFilled(foot, Kit.Px(2f), Kit.Color(rgb, 0.6f), 8);
    }

    /// <summary>
    /// A marker in the flat view for something on another layer: a chevron pointing up for above, down for below,
    /// with how many layers away.
    /// </summary>
    private void DrawLayerMarker(ImDrawListPtr draw, in MapProjection view, float x, float z, int layers, uint rgb)
    {
        if (layers == 0 || view.Tilted)
            return;
        var at = view.ToScreen(x, z);
        var display = _ui.DisplaySize;
        if (at.X < 0 || at.Y < 0 || at.X > display.X || at.Y > display.Y)
            return;
        var s = Kit.Px(5);
        var up = layers > 0;
        var tip = at + new float2(0, up ? -s : s);
        draw.AddTriangle(tip, at + new float2(-s, up ? s * 0.6f : -s * 0.6f), at + new float2(s, up ? s * 0.6f : -s * 0.6f), Kit.Color(rgb, 0.85f), Kit.Px(1.4f));
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(s + Kit.Px(3), -Kit.Px(7)), rgb, Kit.F($"{layers:+0;-0}"), 0.85f);
    }

    /// <summary>Corner brackets around a sector of the chosen layer.</summary>
    private static void SectorBrackets(ImDrawListPtr draw, in MapProjection view, SectorCoord sector, ImColor8 color, float minimum)
    {
        var at = view.ToScreen(sector.X, sector.Z);
        var corner = new float2(Math.Max(view.Cell * 0.5f, minimum), Math.Max(view.CellZ * 0.5f, minimum));
        Brackets(draw, at - corner, at + corner, Math.Clamp(Math.Min(view.Cell, view.CellZ) * 0.3f, 3f, Kit.Px(10)), color);
    }

    /// <summary>An ellipse, filled and outlined, as a polygon: the map's circles squashed by the tilt.</summary>
    private static void Ellipse(ImDrawListPtr draw, float2 center, float rx, float ry, uint fill, float fillAlpha, uint line, float lineAlpha, float thickness)
    {
        const int segments = 64;
        Span<float2> points = stackalloc float2[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = i * (MathF.Tau / segments);
            points[i] = center + new float2(MathF.Cos(angle) * rx, MathF.Sin(angle) * ry);
        }

        if (fillAlpha > 0f)
            draw.AddConvexPolyFilled(points, Kit.Color(fill, fillAlpha));
        if (lineAlpha > 0f)
            draw.AddPolyline(points, Kit.Color(line, lineAlpha), ImDrawFlags.Closed, thickness);
    }

    /// <summary>Title, layer and scale, clear of the sector panel above them.</summary>
    private void DrawMapHeading(ImDrawListPtr draw, WorldView world, in MapUnitLayout units, float2 display, double size)
    {
        var layer = _mapNav.Layer;
        var across = display.X / _mapCell;
        var tilted = _mapNav.Tilt ? "TILTED"u8 : "FLAT"u8;
        if (units.ShortHeading)
        {
            var line = new float2(units.Min.X + Kit.Px(Theme.Space4), units.Min.Y - Kit.Px(28));
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, line, Theme.TextDim, Kit.F($"LAYER {layer:+0;-0;0}   {tilted}   {across:0} SECTORS ACROSS"), units.Size.X - Kit.Px(Theme.Space4) * 2);
            return;
        }

        var at = new float2(Kit.Px(HudLayout.Margin + Theme.Space4), MapUnitList.HeadingTop(SectorColumnBottom(world, false), Kit.Scale));
        var width = units.Size.X - Kit.Px(Theme.Space4) * 2;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.Accent, "NAVIGATION"u8);
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, at + new float2(0, Kit.Px(16)), Theme.Text, "Galaxy Map"u8);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(50)), Theme.TextDim, Kit.F($"LAYER {layer:+0;-0;0}   {tilted}   {across:0} SECTORS ACROSS   {size / 1000:0} KM EACH"), width);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(68)), Theme.TextMute, Kit.F($"{_charts?.Count ?? 0} SECTORS CHARTED   {world.Selection.Length} SHIPS SELECTED"), width);
    }

    /// <summary>
    /// The map's index on the left, under the unit list: what the shades and shapes mean, one row each: charted,
    /// live sensor coverage, uncharted, a lost contact, asteroid fields, gas clouds, then the star types. Rows
    /// that do not fit are left off the end, so the star types go first.
    /// </summary>
    private void DrawMapLegend(ImDrawListPtr draw, Galaxy galaxy, in MapLegendLayout legend, MapTuning look)
    {
        var min = legend.Min;
        Kit.PanelFrame(draw, min, min + legend.Size);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3)), Theme.Accent, "MAP INDEX"u8);
        var swatch = new float2(Kit.Px(14), Kit.Px(10));
        var left = min.X + Kit.Px(Theme.Space4);
        var textX = left + Kit.Px(24);
        var rowTop = min.Y + legend.Header;
        for (var row = 0; row < legend.Rows; row++)
        {
            var middle = new float2(left + swatch.X * 0.5f, rowTop + legend.Row * (row + 0.5f));
            var swatchMin = middle - swatch * 0.5f;
            var textAt = new float2(textX, middle.Y - Kit.Px(7));
            switch (row)
            {
                case 0:
                    draw.AddRectFilled(swatchMin, swatchMin + swatch, Kit.Color(Theme.Info, look.ChartedAlpha + 0.2f));
                    draw.AddRect(swatchMin, swatchMin + swatch, Kit.Color(Theme.Info, 0.6f));
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, textAt, Theme.TextDim, "CHARTED"u8);
                    break;
                case 1:
                    draw.AddCircleFilled(middle, swatch.Y * 0.5f, Kit.Color(Theme.Good, look.LiveFillAlpha + 0.15f), 12);
                    draw.AddCircle(middle, swatch.Y * 0.5f, Kit.Color(Theme.Good, look.LiveRingAlpha), 12, Kit.Px(1.2f));
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, textAt, Theme.TextDim, "LIVE SENSORS"u8);
                    break;
                case 2:
                    draw.AddRectFilled(swatchMin, swatchMin + swatch, Kit.Color(0x10151C, 1f));
                    draw.AddRect(swatchMin, swatchMin + swatch, Kit.Color(Theme.LineStrong, 0.8f));
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, textAt, Theme.TextDim, "UNEXPLORED"u8);
                    break;
                case 3:
                    DrawContactMarker(draw, middle, swatch.Y * 0.45f, Theme.TextDim, 0.9f, false, HullClass.Ship);
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, textAt, Theme.TextDim, "LOST CONTACT"u8);
                    break;
                case 4:
                    {
                        // A bent ribbon, as a field is drawn: its support, its denser core and its outline.
                        Span<float2> ribbon =
                        [
                            middle + new float2(-swatch.X * 0.5f, swatch.Y * 0.1f), middle + new float2(-swatch.X * 0.15f, -swatch.Y * 0.5f), middle + new float2(swatch.X * 0.5f, -swatch.Y * 0.2f),
                        middle + new float2(swatch.X * 0.15f, swatch.Y * 0.5f),
                    ];
                        var rgb = look.FieldRgb;
                        draw.AddConcavePolyFilled(ribbon, Kit.Color(rgb, look.FieldFillAlpha + 0.25f));
                        draw.AddPolyline(ribbon, Kit.Color(rgb, Math.Max(look.FieldLineAlpha, 0.7f)), ImDrawFlags.Closed, Kit.Px(1f));
                        Kit.Text(draw, _ui.Mono, Theme.FontCaption, textAt, Theme.TextDim, "ASTEROID FIELD"u8);
                        break;
                    }

                case 5:
                    {
                        var rgb = look.CloudRgb;
                        Ellipse(draw, middle, swatch.X * 0.5f, swatch.Y * 0.5f, rgb, look.CloudBodyAlpha + 0.2f, rgb, Math.Max(look.CloudLineAlpha, 0.7f), Kit.Px(1f));
                        Kit.Text(draw, _ui.Mono, Theme.FontCaption, textAt, Theme.TextDim, "GAS CLOUD"u8);
                        break;
                    }

                default:
                    {
                        var star = row - MapLegend.FeatureRows;
                        ref readonly var starClass = ref galaxy.StarClasses[star];
                        DrawStarIcon(draw, middle, Math.Min(Kit.Px(look.StarIconSize), swatch.Y * 0.6f), look.ShapeOf(starClass.Name), StarColor(starClass.Color), look);
                        Kit.Text(draw, _ui.Mono, Theme.FontCaption, textAt, Theme.TextDim, Kit.F($"{StarNameUpper(galaxy, star)}"));
                        break;
                    }
            }
        }
    }

    /// <summary>The layer bar's look and its buttons, which do what Page Up, Page Down and the tilt key do.</summary>
    private void DrawMapLayerBar(WorldView world, Galaxy galaxy, float2 min, float2 size, int low, int high, float fade)
    {
        MapLayerBar.Begin(min, size, _mapPointer);
        Kit.Fade = fade;
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var scale = Kit.Scale;
        var layer = _mapNav.Layer;
        var here = world.Sector.Coord.Y;
        var text = min + new float2(Kit.Px(Theme.Space3), Kit.Px(Theme.Space2));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, Theme.Accent, "LAYERS"u8);
        var current = Kit.F($"{layer:+0;-0;0}");
        var currentWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, current).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + size.X - Kit.Px(Theme.Space3) - currentWidth, text.Y), Theme.Text, current);

        var buttonSize = MapLayerBar.ButtonSize(scale);
        var upMin = MapLayerBar.UpMin(min, scale);
        var downMin = MapLayerBar.DownMin(min, scale);
        var up = MapLayerBar.Button("##layerup"u8, upMin, buttonSize) && layer < Math.Max(high, here);
        LayerButton(draw, upMin, buttonSize, true, layer < Math.Max(high, here));
        var down = MapLayerBar.Button("##layerdown"u8, downMin, buttonSize) && layer > Math.Min(low, here);
        LayerButton(draw, downMin, buttonSize, false, layer > Math.Min(low, here));

        // The ruler: the layers around the chosen one, with what is on each.
        var navLayer = world.NavTarget is { } nav ? galaxy.SectorOf(nav).Y : int.MinValue;
        for (var offset = MapLayerBar.RulerLayers; offset >= -MapLayerBar.RulerLayers; offset--)
        {
            var y = layer + offset;
            var top = MapLayerBar.RulerRowTop(min, offset, scale);
            var inside = y >= low && y <= high;
            var rowMin = new float2(upMin.X, top);
            if (offset == 0)
                draw.AddRectFilled(rowMin, rowMin + new float2(buttonSize.X, Kit.Px(MapLayerBar.RulerRow)), Kit.Color(Theme.PanelRaised, 0.95f));
            var color = offset == 0 ? Theme.Text : inside ? Theme.TextDim : Theme.Bad;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, rowMin + new float2(Kit.Px(4), Kit.Px(2)), color, Kit.F($"{y:+0;-0;0}"), inside ? 1f : 0.6f);
            var mark = rowMin + new float2(Kit.Px(48), Kit.Px(9));
            var charted = 0;
            if (_exploration is { } exploration)
                for (var i = 0; i < exploration.ChartCount; i++)
                    charted += exploration.Charts[i].Sector.Y == y ? 1 : 0;
            if (y == here)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, mark - new float2(0, Kit.Px(7)), Theme.Accent, "YOU"u8);
            if (y == navLayer)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, mark + new float2(Kit.Px(32), -Kit.Px(7)), Theme.Info, "NAV"u8);
            if (_mapCard.Pinned && _mapCard.Sector.Y == y)
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, mark + new float2(Kit.Px(64), -Kit.Px(7)), Theme.Text, "CARD"u8);
            if (charted > 0)
            {
                var count = Kit.F($"{charted}");
                var width = Kit.Measure(_ui.Mono, Theme.FontCaption, count).X;
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(rowMin.X + buttonSize.X - Kit.Px(4) - width, rowMin.Y + Kit.Px(2)), Theme.Info, count);
            }
            else if (!inside)
            {
                draw.AddLine(rowMin + new float2(Kit.Px(48), Kit.Px(9)), rowMin + new float2(buttonSize.X - Kit.Px(4), Kit.Px(9)), Kit.Color(Theme.Bad, 0.3f));
            }
        }

        var toggle = MapLayerBar.ToggleSize(scale);
        var flatMin = MapLayerBar.FlatMin(min, scale);
        var tiltMin = MapLayerBar.TiltMin(min, scale);
        var flat = MapLayerBar.Button("##mapflat"u8, flatMin, toggle);
        var tilt = MapLayerBar.Button("##maptilt"u8, tiltMin, toggle);
        ToggleHalf(draw, flatMin, toggle, "FLAT"u8, !_mapNav.Tilt);
        ToggleHalf(draw, tiltMin, toggle, "TILT"u8, _mapNav.Tilt);
        if (MapLayerBar.Apply(_mapNav, up, down, flat, tilt))
            Flattened();
        MapLayerBar.End();
    }

    private void LayerButton(ImDrawListPtr draw, float2 min, float2 size, bool up, bool enabled)
    {
        var hot = Kit.Animate(ImGui.GetItemID(), enabled && ImGui.IsItemHovered() ? 1f : 0f);
        draw.AddRectFilled(min, min + size, Kit.Color(Theme.PanelRaised, 0.6f + 0.4f * hot));
        draw.AddRect(min, min + size, Kit.Color(Kit.Mix(Theme.Line, Theme.Accent, hot)));
        var c = min + size * 0.5f;
        var s = Kit.Px(5);
        var color = Kit.Color(enabled ? Kit.Mix(Theme.TextDim, Theme.Accent, hot) : Theme.TextMute, enabled ? 1f : 0.5f);
        if (up)
            draw.AddTriangleFilled(c + new float2(-Kit.Px(40), s * 0.6f), c + new float2(-Kit.Px(40) + s, -s * 0.6f), c + new float2(-Kit.Px(40) + s * 2, s * 0.6f), color);
        else
            draw.AddTriangleFilled(c + new float2(-Kit.Px(40), -s * 0.6f), c + new float2(-Kit.Px(40) + s * 2, -s * 0.6f), c + new float2(-Kit.Px(40) + s, s * 0.6f), color);
        var label = up ? "UP   PGUP"u8 : "DOWN   PGDN"u8;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, c + new float2(-Kit.Px(24), -Kit.Px(7)), enabled ? Theme.TextDim : Theme.TextMute, label);
    }

    private void ToggleHalf(ImDrawListPtr draw, float2 min, float2 size, ReadOnlySpan<byte> label, bool on)
    {
        var hot = Kit.Animate(ImGui.GetItemID(), ImGui.IsItemHovered() ? 1f : 0f);
        draw.AddRectFilled(min, min + size, Kit.Color(on ? Theme.Accent : Theme.PanelRaised, on ? 0.9f : 0.6f + 0.4f * hot));
        var width = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + new float2((size.X - width) * 0.5f, (size.Y - Kit.Px(14)) * 0.5f), on ? Theme.AccentInk : Theme.TextDim, label);
    }

    /// <summary>Grid lines every <paramref name="every"/> sectors, drawn on sector boundaries.</summary>
    private static void DrawMapGrid(ImDrawListPtr draw, float2 display, float left, float top, float right, float bottom, int every, float alpha, float2 origin, float2 cell)
    {
        if (alpha <= 0.01f)
            return;

        // Boundaries sit half a sector from the sector centres; stronger lines fall on multiples of the spacing.
        var color = Kit.Color(0x8FB4C8, alpha);
        for (var x = MathF.Ceiling((left - 0.5f) / every) * every; x - 0.5f <= right; x += every)
        {
            var sx = MathF.Round(origin.X + (x - 0.5f) * cell.X);
            draw.AddLine(new float2(sx, 0), new float2(sx, display.Y), color);
        }

        for (var z = MathF.Ceiling((top - 0.5f) / every) * every; z - 0.5f <= bottom; z += every)
        {
            var sy = MathF.Round(origin.Y + (z - 0.5f) * cell.Y);
            draw.AddLine(new float2(0, sy), new float2(display.X, sy), color);
        }
    }
}
