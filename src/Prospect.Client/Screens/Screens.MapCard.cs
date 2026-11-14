using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    // Unscaled pixels of the sector card's parts.
    private const float CardCaption = 18f;
    private const float CardName = 36f;
    private const float CardLine = 19f;
    private const float CardScanPinned = 58f;
    private const float CardShipHeader = 24f;
    private const float CardShipRow = 36f;
    private const int CardPreviewRows = 3;
    private const int CardShipCapacity = 256;

    /// <summary>Who is in the card's sector, in the order listed: the player's own, then live contacts, then lost ones.</summary>
    private enum CardShip : byte
    {
        Own,
        Live,
        LiveContact,
        Lost,
    }

    private readonly CardShip[] _cardKinds = new CardShip[CardShipCapacity];
    private readonly int[] _cardIndices = new int[CardShipCapacity];
    private int _cardCount;
    private int _cardTotal;
    private int _cardOwn;
    private SectorCoord _cardShipsSector;
    private int _cardShipsFrame = -1;

    /// <summary>The card's measured parts, in pixels from its top.</summary>
    private struct CardParts
    {
        public int InfoLines;
        public float Scan;
        public float Course;
        public float ShipsTop;
        public int VisibleRows;
        public bool MoreLine;
        public float Height;
        public bool Compact;
    }

    /// <summary>Where the card for a sector goes: beside the sector's cell, kept on screen under the bar.</summary>
    private float2 PlaceCard(in MapProjection view, SectorCoord sector, float2 size)
    {
        var anchor = view.ToScreen(sector.X, sector.Y, sector.Z);
        return MapSectorCard.Place(anchor, Math.Max(view.Cell * 0.5f, 7f), size, _ui.DisplaySize, HudTop, Kit.Px(MapLook.CardGap), Kit.Px(HudLayout.Margin));
    }

    private float2 CardSize(SessionSnapshot snapshot, WorldView world, Galaxy galaxy, SectorCoord sector, bool preview) =>
        new(Kit.Px(MapLook.CardWidth), MeasureCard(snapshot, world, galaxy, sector, preview).Height);

    /// <summary>
    /// Lays the card out: header, what is known, the scan in the player's own sector, the course rows when pinned,
    /// the ships. Where that is taller than the room under the bar, the card turns compact (coordinates and distance
    /// on one line, no course rows, which the right-click menu still has, no closing hint), then lists fewer ships,
    /// and at last is cut to the room it has.
    /// </summary>
    private CardParts MeasureCard(SessionSnapshot snapshot, WorldView world, Galaxy galaxy, SectorCoord sector, bool preview)
    {
        CollectCardShips(snapshot, world, galaxy, sector);
        var room = _ui.DisplaySize.Y - HudTop - Kit.Px(HudLayout.Margin);
        var parts = LayOutCard(world, galaxy, sector, preview, false, preview ? CardPreviewRows : MapLook.CardListRows);
        if (parts.Height > room)
            parts = LayOutCard(world, galaxy, sector, preview, true, preview ? CardPreviewRows : MapLook.CardListRows);
        if (parts.Height > room)
        {
            var spare = (parts.Height - room) / Kit.Px(CardShipRow);
            parts = LayOutCard(world, galaxy, sector, preview, true, Math.Max(1, parts.VisibleRows - (int)MathF.Ceiling(spare)));
        }

        parts.Height = Math.Min(parts.Height, MathF.Round(room));
        return parts;
    }

    private CardParts LayOutCard(WorldView world, Galaxy galaxy, SectorCoord sector, bool preview, bool compact, int rows)
    {
        var parts = new CardParts { Compact = compact };
        var charted = Charted(sector);
        // Coordinates, distance, sensors, then the chart's star, fields, gas and age, or one line saying there is
        // none; and the jump line. Compact, coordinates and distance share a line.
        parts.InfoLines = (compact ? 2 : 3) + (charted ? 3 + (MapSummary(galaxy, sector).GasCloudCount > 0 ? 1 : 0) : 1) + 1;
        var y = Kit.Px(Theme.Space4) + Kit.Px(CardCaption) + Kit.Px(CardName) + Kit.Px(CardLine) * parts.InfoLines + Kit.Px(Theme.Space2);
        parts.Scan = -1;
        if (sector == world.Sector.Coord)
        {
            parts.Scan = y;
            y += preview ? Kit.Px(CardLine) + Kit.Px(Theme.Space2) : Kit.Px(CardScanPinned);
        }

        parts.Course = -1;
        if (!preview && !compact)
        {
            parts.Course = y;
            y += Kit.Px(Theme.Space1) + MathF.Round(MapCourseMenu.RowHeight * Kit.Scale) * 2 + Kit.Px(Theme.Space2);
        }

        parts.ShipsTop = y;
        parts.VisibleRows = Math.Max(1, Math.Min(_cardCount, rows));
        parts.MoreLine = preview ? _cardTotal > CardPreviewRows : _cardTotal > _cardCount;
        y += Kit.Px(CardShipHeader) + Kit.Px(CardShipRow) * parts.VisibleRows + (parts.MoreLine ? Kit.Px(CardLine) : 0f);
        y += (compact ? 0f : Kit.Px(CardLine)) + Kit.Px(Theme.Space3);
        parts.Height = MathF.Round(y);
        return parts;
    }

    /// <summary>
    /// Lists who is in a sector, as far as the player may know: their own hulls, hulls the server lets them see,
    /// live contacts beyond the snapshot's hull views, and lost contacts at their last position. Done once a frame
    /// per sector, into arrays made once.
    /// </summary>
    private void CollectCardShips(SessionSnapshot snapshot, WorldView world, Galaxy galaxy, SectorCoord sector)
    {
        var frame = ImGui.GetFrameCount();
        if (_cardShipsFrame == frame && _cardShipsSector == sector)
            return;
        _cardShipsFrame = frame;
        _cardShipsSector = sector;
        _cardCount = _cardTotal = _cardOwn = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < snapshot.HullCount; i++)
            {
                ref var hull = ref snapshot.Hulls[i];
                var mine = hull.Owner == snapshot.PlayerId;
                if (mine != (pass == 0) || galaxy.SectorOf(world.HullPosition(hull)) != sector)
                    continue;
                AddCardShip(mine ? CardShip.Own : CardShip.Live, i);
                _cardOwn += mine ? 1 : 0;
            }

        var exploration = snapshot.Exploration;
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < exploration.ContactCount; i++)
            {
                ref var contact = ref exploration.Contacts[i];
                if (contact.Owner == snapshot.PlayerId || contact.Live != (pass == 0) || galaxy.SectorOf(contact.Position) != sector)
                    continue;
                if (contact.Live && InHullViews(snapshot, contact.Hull))
                    continue;
                AddCardShip(contact.Live ? CardShip.LiveContact : CardShip.Lost, i);
            }
    }

    private void AddCardShip(CardShip kind, int index)
    {
        _cardTotal++;
        if (_cardCount == CardShipCapacity)
            return;
        _cardKinds[_cardCount] = kind;
        _cardIndices[_cardCount++] = index;
    }

    private static bool InHullViews(SessionSnapshot snapshot, uint hull)
    {
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Id == hull)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The card's frame and everything that is only read: what kind of sector it is, what the player knows of it,
    /// the scan readout, and for a preview the first ships. The pinned card adds its buttons and scrolling list.
    /// </summary>
    private CardParts DrawSectorCard(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, Galaxy galaxy, SectorCoord sector, float2 min, float2 size, bool preview)
    {
        var parts = MeasureCard(snapshot, world, galaxy, sector, preview);
        var look = MapLook;
        Kit.PanelFrame(draw, min, min + size);
        draw.PushClipRect(min, min + size, true);
        var here = world.Sector.Coord;
        var charted = Charted(sector);
        var inner = size.X - Kit.Px(Theme.Space4) * 2;
        var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space4));

        // Header: what kind of sector, and whether the card is pinned.
        var kind = sector == here ? "YOU ARE HERE"u8 : charted ? "CHARTED"u8 : "UNEXPLORED"u8;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, sector == here ? Theme.Accent : charted ? Theme.Info : Theme.TextMute, kind);
        // The pinned card's close button takes the top right corner, so its label sits to the left of it.
        var state = preview ? "CLICK TO PIN"u8 : "PINNED"u8;
        var stateWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, state).X;
        var stateRight = min.X + size.X - Kit.Px(Theme.Space4) - (preview ? 0f : Kit.Px(MapSectorCard.CloseSize));
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(stateRight - stateWidth, text.Y), Theme.TextMute, state);
        var nameAt = text + new float2(0, Kit.Px(CardCaption));
        if (charted)
            Kit.ClippedText(draw, _ui.SansBold, Theme.FontHeading, nameAt, Theme.Text, MapName(galaxy, sector), inner);
        else
            Kit.Text(draw, _ui.SansBold, Theme.FontHeading, nameAt, Theme.TextDim, "Uncharted sector"u8);

        // Where it is and what is known.
        var line = Kit.Px(CardLine);
        var at = nameAt + new float2(0, Kit.Px(CardName));
        var range = galaxy.CenterOf(sector).RelativeTo(world.OwnPosition).Length() / 1000f;
        var layers = sector.Y - here.Y;
        if (parts.Compact)
        {
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, Kit.F($"{sector.X} : {sector.Z}   LAYER {sector.Y:+0;-0;0}   {range:0.0} KM"), inner);
            at.Y += line;
        }
        else
        {
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, Kit.F($"COORDINATES  {sector.X} : {sector.Z}   LAYER {sector.Y:+0;-0;0}"), inner);
            at.Y += line;
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim,
                layers == 0 ? Kit.F($"DISTANCE     {range:0.0} KM") : Kit.F($"DISTANCE     {range:0.0} KM   {Math.Abs(layers)} {(Math.Abs(layers) == 1 ? "LAYER" : "LAYERS")} {(layers > 0 ? "UP" : "DOWN")}"), inner);
            at.Y += line;
        }
        var reach = _coverage.Reach(galaxy, sector);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, reach > 0 ? Theme.Good : Theme.TextMute,
            reach == 2 ? "SENSORS      LIVE, IN RANGE"u8 : reach == 1 ? "SENSORS      LIVE AT ITS EDGE"u8 : "SENSORS      OUT OF RANGE, NOTHING LIVE"u8, inner);
        at.Y += line;
        if (charted)
        {
            ref var summary = ref MapSummary(galaxy, sector);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, Kit.F($"STAR         {galaxy.StarClasses[summary.StarClass].Name}"), inner);
            at.Y += line;
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim,
                TryFieldCount(galaxy, sector, out var fields) ? Kit.F($"FIELDS       {fields} IN REACH") : "FIELDS       READING CHART"u8, inner);
            at.Y += line;
            if (summary.GasCloudCount > 0)
            {
                Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, "GAS          CLOUD CENTRED HERE"u8, inner);
                at.Y += line;
            }

            var age = ChartAgeSeconds(snapshot, sector);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextMute,
                age < 120f ? Kit.F($"SURVEYED     {age:0} S AGO, NOT LIVE") : Kit.F($"SURVEYED     {age / 60f:0} MIN AGO, NOT LIVE"), inner);
            at.Y += line;
        }
        else
        {
            Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at, Theme.TextMute, "No chart. Name, star and fields unknown until scanned."u8, inner);
            at.Y += line;
        }

        // The jump, in the words the course menus use: charted or blind, the range, and a blind jump's risk.
        var jump = JumpSummary(snapshot, world.PlanJump(galaxy.CenterOf(sector)), out var jumpColor);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, jumpColor, Kit.F($"JUMP         {jump}"), inner);

        if (parts.Scan >= 0)
            DrawScanReadout(draw, snapshot, world, new float2(text.X, min.Y + parts.Scan), inner, preview, charted);

        // Ships: the heading, and for a preview the first rows; the pinned card scrolls its own.
        var shipsAt = new float2(text.X, min.Y + parts.ShipsTop);
        draw.AddLine(new float2(min.X + Kit.Px(6), shipsAt.Y - Kit.Px(4)), new float2(min.X + size.X - Kit.Px(6), shipsAt.Y - Kit.Px(4)), Kit.Color(Theme.Line));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, shipsAt, Theme.Accent, "SHIPS"u8);
        var others = _cardTotal - _cardOwn;
        var count = Kit.F($"{_cardOwn} YOURS   {others} {(others == 1 ? "CONTACT" : "CONTACTS")}");
        var countWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, count).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + size.X - Kit.Px(Theme.Space4) - countWidth, shipsAt.Y), Theme.TextDim, count);
        var rowsAt = shipsAt + new float2(0, Kit.Px(CardShipHeader));
        if (_cardCount == 0)
        {
            Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, rowsAt + new float2(0, Kit.Px(10)), Theme.TextMute,
                reach > 0 ? "None known. Sensors would show ships here."u8 : "None known. Out of sensor range."u8, inner);
        }
        else if (preview)
        {
            for (var i = 0; i < Math.Min(_cardCount, CardPreviewRows); i++)
                DrawCardShip(draw, snapshot, world, i, new float2(min.X + Kit.Px(Theme.Space2), rowsAt.Y + i * Kit.Px(CardShipRow)), size.X - Kit.Px(Theme.Space2) * 2, look);
        }

        var bottom = min.Y + size.Y - Kit.Px(Theme.Space3) - (parts.Compact ? 0f : line);
        if (parts.MoreLine)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(text.X, bottom - line), Theme.TextMute,
                preview ? Kit.F($"+{_cardTotal - CardPreviewRows} MORE, PIN TO SCROLL") : Kit.F($"+{_cardTotal - _cardCount} MORE NOT LISTED"));
        if (!parts.Compact)
            Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, new float2(text.X, bottom), Theme.TextMute,
                preview ? "Click to pin. Right-click to move or jump here."u8 : "Escape, the X or a click outside the galaxy closes."u8, inner);
        draw.PopClipRect();
        return parts;
    }

    /// <summary>The pinned card: a window with the read-only card, the scan button, the course rows and the scrolling ship list.</summary>
    private void DrawPinnedCard(SessionSnapshot snapshot, WorldView world, Galaxy galaxy, float2 min, float2 size, float fade)
    {
        MapSectorCard.Begin(min, size, _mapPointer);
        Kit.Fade = fade;
        var draw = ImGui.GetWindowDrawList();
        var sector = _mapCard.Sector;
        var parts = DrawSectorCard(draw, snapshot, world, galaxy, sector, min, size, false);
        var inner = size.X - Kit.Px(Theme.Space4) * 2;
        var left = min.X + Kit.Px(Theme.Space4);

        // Close: the same as Escape. Drawn over the header, which keeps clear of it.
        var closeMin = MapSectorCard.CloseMin(min, size, Kit.Scale);
        var closeSize = MapSectorCard.CloseExtent(Kit.Scale);
        var close = MapSectorCard.Close(min, size, Kit.Scale);
        var closeHot = Kit.Animate(ImGui.GetItemID(), ImGui.IsItemHovered() ? 1f : 0f);
        draw.AddRectFilled(closeMin, closeMin + closeSize, Kit.Color(Theme.PanelRaised, 0.9f * closeHot));
        var cross = closeSize * 0.3f;
        var crossColor = Kit.Color(Kit.Mix(Theme.TextMute, Theme.Text, closeHot));
        var crossMid = closeMin + closeSize * 0.5f;
        draw.AddLine(crossMid - cross, crossMid + cross, crossColor, Kit.Px(1.4f));
        draw.AddLine(crossMid + new float2(-cross.X, cross.Y), crossMid + new float2(cross.X, -cross.Y), crossColor, Kit.Px(1.4f));
        if (close)
            _mapCard.Dismiss();

        if (parts.Scan >= 0)
        {
            var buttonMin = new float2(left, min.Y + parts.Scan);
            var buttonSize = new float2(inner, Kit.Px(28));
            var gate = ScanRules.Gate(snapshot.Exploration.Scan, world.Sector.Coord, snapshot.ServerTick);
            var scanning = gate == ScanGate.Running;
            var enabled = gate == ScanGate.Ready && _coverage.OwnCanChart;
            var pressed = MapSectorCard.Button("##scan"u8, buttonMin, buttonSize);
            var hovered = ImGui.IsItemHovered();
            if (!scanning)
                ScanButton(draw, buttonMin, buttonSize, enabled, Charted(sector), ScanRules.CooldownSeconds(snapshot.Exploration.Scan, world.Sector.Coord, snapshot.ServerTick));
            if (hovered)
                DrawScanTip(ImGui.GetForegroundDrawList(), snapshot, world, buttonMin, buttonSize);
            if (pressed && enabled && Bridge is { } bridge)
                bridge.RequestScan();
        }

        if (parts.Course >= 0)
        {
            var target = galaxy.CenterOf(sector);
            var rowsMin = new float2(min.X, min.Y + parts.Course);
            var rowSize = MapCourseMenu.RowSize(new float2(size.X, 0), Kit.Scale);
            DrawCourseRows(draw, snapshot, world, target, rowsMin, rowSize);
        }

        var listTop = parts.ShipsTop + Kit.Px(CardShipHeader);
        if (_cardCount > 0 && listTop + Kit.Px(CardShipRow) <= size.Y)
        {
            var listMin = new float2(min.X + Kit.Px(Theme.Space2), min.Y + listTop);
            var listSize = new float2(size.X - Kit.Px(Theme.Space2) * 2, Math.Min(Kit.Px(CardShipRow) * parts.VisibleRows, size.Y - listTop - Kit.Px(Theme.Space2)));
            MapSectorCard.BeginList(listMin, listSize, _mapPointer);
            var rows = ImGui.GetWindowDrawList();
            var look = MapLook;
            for (var i = 0; i < _cardCount; i++)
            {
                var rowMin = ImGui.GetCursorScreenPos();
                MapSectorCard.Row(i, Kit.Px(CardShipRow));
                DrawCardShip(rows, snapshot, world, i, rowMin, listSize.X, look);
            }

            MapSectorCard.EndList();
        }

        MapSectorCard.End();
    }

    /// <summary>The scan's readout in the card: progress while it runs, otherwise the last result for this sector or what a scan does.</summary>
    private void DrawScanReadout(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float2 at, float width, bool preview, bool charted)
    {
        var scan = snapshot.Exploration.Scan;
        var seconds = snapshot.Content.Exploration.ScanTicks / (float)TickLoop.SimulationHz;
        if (scan.State == ScanState.Scanning)
        {
            var progress = ScanTracker.Progress(scan, snapshot.ServerTick);
            var barHeight = Kit.Px(preview ? 6 : 28);
            draw.AddRectFilled(at, at + new float2(width, barHeight), Kit.Color(Theme.PanelRaised));
            draw.AddRectFilled(at, at + new float2(width * progress, barHeight), Kit.Color(Theme.Info, 0.85f));
            var left = (1f - progress) * seconds;
            var label = Kit.F($"SCANNING {progress * 100f:0}%   {left:0.0} S LEFT");
            if (preview)
                Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(8)), Theme.Info, label, width);
            else
            {
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(Kit.Px(Theme.Space2), Kit.Px(7)), Theme.Text, label);
                Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at + new float2(0, Kit.Px(32)), Theme.TextMute, "Flight continues. Leaving the sector or switching ship cancels."u8, width);
            }

            return;
        }

        var resultAt = preview ? at : at + new float2(0, Kit.Px(32));
        var result = ScanResult(scan, world.Sector.Coord, out var color);
        if (result.Length > 0)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, resultAt, color, result, width);
        else
        {
            var cooling = ScanRules.CooldownSeconds(scan, world.Sector.Coord, snapshot.ServerTick);
            ReadOnlySpan<byte> hint = !_coverage.OwnCanChart ? "This ship has no charting block."u8
                : cooling > 0 ? Kit.F($"Scanned a moment ago: ready again in {cooling} s.")
                : charted ? Kit.F($"Rescan refreshes the chart: {seconds:0} s, flight continues.")
                : Kit.F($"A scan charts this sector in {seconds:0} s; flight continues.");
            Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, resultAt, Theme.TextMute, hint, width);
        }
    }

    /// <summary>The last scan's result for the player's sector, or nothing; a cancellation with its inferred reason.</summary>
    private ReadOnlySpan<byte> ScanResult(in ScanView scan, SectorCoord here, out uint color)
    {
        color = Theme.TextMute;
        switch (scan.State)
        {
            case ScanState.Completed when scan.Sector == here:
                color = Theme.Good;
                return "CHARTED: FIELDS AND STAR RECORDED"u8;
            case ScanState.Cancelled when scan.Sector == here || _scan.SinceResult < MapLook.ScanResultSeconds:
                color = Theme.Bad;
                return _scan.Stop switch
                {
                    ScanStop.LeftSector => "CANCELLED: LEFT THE SECTOR"u8,
                    ScanStop.ControlChanged => "CANCELLED: SWITCHED SHIP"u8,
                    ScanStop.ChartingLost => "CANCELLED: CHARTING BLOCK LOST"u8,
                    _ => "CANCELLED"u8,
                };
            case ScanState.NoEquipment:
                color = Theme.Bad;
                return "REFUSED: NO CHARTING BLOCK ON THIS SHIP"u8;
            case ScanState.ChartLimit:
                color = Theme.Bad;
                return "REFUSED: CHART LIMIT REACHED"u8;
            case ScanState.Cooldown:
                return "REFUSED: SCANNED TOO RECENTLY"u8;
            default:
                return default;
        }
    }

    private void ScanButton(ImDrawListPtr draw, float2 min, float2 size, bool enabled, bool charted, int cooldown)
    {
        var hot = Kit.Animate(ImGui.GetItemID(), enabled && ImGui.IsItemHovered() ? 1f : 0f);
        draw.AddRectFilled(min, min + size, Kit.Color(enabled ? Kit.Mix(Theme.PanelRaised, Theme.Accent, 0.25f + 0.75f * hot) : Theme.PanelRaised, enabled ? 1f : 0.6f));
        draw.AddRect(min, min + size, Kit.Color(enabled ? Theme.Accent : Theme.Line));
        var label = cooldown > 0 ? Kit.F($"SCAN READY IN {cooldown} S") : !enabled ? "SCAN UNAVAILABLE"u8 : charted ? "RESCAN SECTOR"u8 : "SCAN SECTOR"u8;
        var width = Kit.Measure(_ui.SansMedium, Theme.FontSmall, label).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, min + new float2((size.X - width) * 0.5f, (size.Y - Kit.Px(17)) * 0.5f), enabled ? Kit.Mix(Theme.Text, Theme.AccentInk, hot) : Theme.TextMute, label);
    }

    /// <summary>
    /// One ship of the card's list: its marker, who or what it is, its hull class, and what it is doing. A live
    /// contact says so; a lost one is drawn faded with how long ago it was seen, so it never reads as current.
    /// </summary>
    private void DrawCardShip(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, int row, float2 min, float width, MapTuning look)
    {
        var designs = snapshot.Content.Designs;
        var kind = _cardKinds[row];
        var index = _cardIndices[row];
        var rowSize = new float2(width, Kit.Px(CardShipRow));
        draw.AddLine(new float2(min.X, min.Y + rowSize.Y - 0.5f), new float2(min.X + width, min.Y + rowSize.Y - 0.5f), Kit.Color(Theme.Line, 0.5f));
        var textX = min.X + Kit.Px(22);
        var edge = min.X + width - Kit.Px(Theme.Space2);
        var top = min.Y + Kit.Px(3);
        var second = min.Y + Kit.Px(19);
        var icon = new float2(min.X + Kit.Px(10), min.Y + Kit.Px(11));
        byte design;
        uint owner;
        if (kind is CardShip.Own or CardShip.Live)
        {
            ref var hull = ref snapshot.Hulls[index];
            design = hull.Design;
            owner = hull.Owner;
        }
        else
        {
            ref var contact = ref snapshot.Exploration.Contacts[index];
            design = contact.Design;
            owner = contact.Owner;
        }

        var hullDesign = designs[Math.Min(design, designs.Length - 1)];
        var alpha = kind == CardShip.Lost ? look.StaleFade : 1f;
        var markerColor = kind == CardShip.Own ? Theme.Accent : kind == CardShip.Lost ? Theme.TextDim : Theme.Info;
        DrawContactMarker(draw, icon, Kit.Px(5), markerColor, alpha, kind != CardShip.Lost, hullDesign.Class);

        ReadOnlySpan<byte> tag = hullDesign.Class switch
        {
            HullClass.Fighter => "FIGHTER"u8,
            HullClass.Station => "STATION"u8,
            _ => "SHIP"u8,
        };
        if (kind == CardShip.Own && snapshot.Hulls[index].IsOwn)
            tag = "YOU"u8;
        var tagWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tag).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(edge - tagWidth, top + Kit.Px(1)), kind == CardShip.Own ? Theme.Accent : Theme.TextMute, tag, alpha);
        var nameWidth = Math.Max(0f, edge - tagWidth - Kit.Px(Theme.Space2) - textX);
        if (kind == CardShip.Own)
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, new float2(textX, top), Theme.Text, Kit.F($"{hullDesign.Name} {(snapshot.Hulls[index].Id & 0xFFFF) + 1}"), nameWidth);
        else
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, new float2(textX, top), kind == CardShip.Lost ? Theme.TextDim : Theme.Text, OwnerName(snapshot, owner), nameWidth);

        // What it is doing, as far as is known now.
        ReadOnlySpan<byte> activity;
        uint color;
        if (kind is CardShip.Own or CardShip.Live)
        {
            ref var hull = ref snapshot.Hulls[index];
            var mine = kind == CardShip.Own;
            if (hull.Jump != JumpPhase.None || (mine && hull.Order.Kind == OrderKind.Jump))
            {
                color = Theme.Info;
                activity = mine ? "JUMPING"u8 : "LIVE   JUMPING"u8;
            }
            else if (mine && hull.Order.Kind == OrderKind.MoveTo)
            {
                color = Theme.Accent;
                var distance = hull.Order.Target.RelativeTo(world.HullPosition(hull)).Length();
                activity = distance >= 1000f ? Kit.F($"MOVING {distance / 1000f:0.0} KM") : Kit.F($"MOVING {distance:0} M");
            }
            else
            {
                var speed = hull.Velocity.Length();
                color = mine ? Theme.TextMute : Theme.Good;
                activity = speed > 1f ? mine ? Kit.F($"DRIFTING {speed:0} M/S") : Kit.F($"LIVE   {hullDesign.Name}   {speed:0} M/S") : mine ? "HOLDING"u8 : Kit.F($"LIVE   {hullDesign.Name}   HOLDING");
            }
        }
        else if (kind == CardShip.LiveContact)
        {
            color = Theme.Good;
            activity = Kit.F($"LIVE   {hullDesign.Name}   ACTIVITY UNKNOWN");
        }
        else
        {
            ref var contact = ref snapshot.Exploration.Contacts[index];
            var age = Math.Max(0, snapshot.ServerTick - contact.LostTick) / (float)TickLoop.SimulationHz;
            color = Theme.TextMute;
            activity = hullDesign.Class == HullClass.Station
                ? age < 120f ? Kit.F($"{hullDesign.Name}   LAST KNOWN {age:0} S AGO") : Kit.F($"{hullDesign.Name}   LAST KNOWN {age / 60f:0} MIN AGO")
                : Kit.F($"{hullDesign.Name}   LOST {age:0} S AGO, LAST POSITION");
        }

        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(textX, second), color, activity, Math.Max(0f, edge - textX));
    }

    /// <summary>A player's session name from the roster, or their number; drawn as untrusted text.</summary>
    private static ReadOnlySpan<byte> OwnerName(SessionSnapshot snapshot, uint owner)
    {
        var social = snapshot.Social;
        for (var i = 0; i < social.RosterCount; i++)
        {
            ref var entry = ref social.Roster[i];
            if (entry.Player == owner && entry.Length > 0)
                return Kit.F($"{(ReadOnlySpan<char>)entry.Text[..entry.Length]}");
        }

        return Kit.F($"PLAYER {owner}");
    }

    /// <summary>Seconds since the player's chart of a sector was recorded, by the server's clock.</summary>
    private static float ChartAgeSeconds(SessionSnapshot snapshot, SectorCoord sector)
    {
        var exploration = snapshot.Exploration;
        for (var i = 0; i < exploration.ChartCount; i++)
        {
            if (exploration.Charts[i].Sector == sector)
                return Math.Max(0, snapshot.ServerTick - exploration.Charts[i].RecordedTick) / (float)TickLoop.SimulationHz;
        }

        return 0f;
    }
}
