using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The Charts window and the two placeholder windows. Charts belong to the player, not to a place in the world:
/// the window lists what <see cref="SessionSnapshot.Exploration"/> says they own, and its two actions go through the
/// map and the jump contract everything else uses (<see cref="ShowChartOnMap"/>, <see cref="JumpPlan"/>).
/// </summary>
internal sealed partial class ScreenHost
{
    // Unscaled pixels of a chart row: the name, a line of where it is, a line of what it holds.
    private const float ChartRowHeight = 82f;
    private const float ChartButtonWidth = 66f;

    private ChartCatalogue? _chartList;
    private readonly ImInputString _chartFilter = new(48);
    private readonly char[] _chartFilterChars = new char[48];
    private int _chartHoverJump = -1;
    private int _chartHoverJumpFrame = -1;

    private ChartCatalogue ChartList(ExplorationView exploration, Galaxy galaxy)
    {
        if (_chartList is null || _chartListCapacity != exploration.Charts.Length)
        {
            _chartList = new ChartCatalogue(exploration.Charts.Length, galaxy.MaxNameLength);
            _chartListCapacity = exploration.Charts.Length;
        }

        return _chartList;
    }

    private int _chartListCapacity;

    /// <summary>
    /// Leaves interface mode, opens the galaxy map centred on a charted sector on its layer, with the sector's card
    /// pinned, as a click on it would.
    /// </summary>
    private void ShowChartOnMap(SectorCoord sector)
    {
        if (_app.World.Galaxy is null)
            return;
        CloseWorkspace();
        OpenMap();
        _mapCenter = new float2(sector.X, sector.Z);
        _mapNav.Reset(sector.Y);
        _mapCard.Pin(sector);
    }

    private void DrawChartsPane(SessionSnapshot snapshot)
    {
        var world = _app.World;
        var galaxy = snapshot.Galaxy;
        if (galaxy is null)
            return;
        var exploration = snapshot.Exploration;
        var list = ChartList(exploration, galaxy);
        list.Update(exploration, galaxy, world.Sector.Coord);
        var capacity = snapshot.Content.Exploration.ChartsPerOwner;
        if (!BeginPane(SocialPane.Charts, "##charts"u8, Kit.F($"Charts   {list.Total} / {capacity}"), false, out var width, out var input))
            return;

        var interactive = input != FloatingInput.Passive;
        DrawChartHeader(list, width, interactive);
        var footer = Kit.Px(34);
        var rowsHeight = Math.Max(Kit.Px(ChartRowHeight), ImGui.GetContentRegionAvail().Y - footer);
        _chartHoverJump = -1;
        DrawChartRows(snapshot, world, galaxy, list, width, rowsHeight, input);
        Kit.Space(Theme.Space1);
        DrawChartFooter(snapshot, world, galaxy, list, width);
        EndPane();
    }

    /// <summary>The sort chips and the name filter.</summary>
    private void DrawChartHeader(ChartCatalogue list, float width, bool interactive)
    {
        var sort = list.Sort;
        var desc = list.Descending;
        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        Kit.Label(_ui.SansMedium, Theme.FontCaption, Theme.TextMute, "SORT"u8);
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (ChannelButton("##chartsortdistance"u8, sort == ChartSort.Distance && desc ? "FARTHEST"u8 : "NEAREST"u8, sort == ChartSort.Distance, interactive))
            list.Choose(ChartSort.Distance);
        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        if (ChannelButton("##chartsortage"u8, sort == ChartSort.Age && desc ? "OLDEST"u8 : "NEWEST"u8, sort == ChartSort.Age, interactive))
            list.Choose(ChartSort.Age);
        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        if (ChannelButton("##chartsortname"u8, sort == ChartSort.Name && desc ? "Z-A"u8 : "A-Z"u8, sort == ChartSort.Name, interactive))
            list.Choose(ChartSort.Name);
        ImGui.PopItemFlag();
        Kit.Space(Theme.Space1);
        if (Kit.TextField("FILTER BY NAME"u8, "##chartfilter"u8, _chartFilter, width / Kit.Scale))
            ImGui.Internal.ClearActiveID();
        if (_chartFilter.TryFormat(_chartFilterChars, out var written, default, null))
            list.SetFilter(_chartFilterChars.AsSpan(0, written));
        Kit.Space(Theme.Space2);
    }

    /// <summary>The scrolling list: only the rows on screen are drawn, each with its two actions.</summary>
    private void DrawChartRows(SessionSnapshot snapshot, WorldView world, Galaxy galaxy, ChartCatalogue list, float width, float height, FloatingInput input)
    {
        var look = MapLook;
        var rowHeight = Kit.Px(ChartRowHeight);
        ImGui.BeginChild("##chartrows"u8, new float2(width, height), ImGuiChildFlags.None, FloatingWindow.ChildFlags(input));
        var inner = ImGui.GetContentRegionAvail().X - Kit.Px(Theme.Space3);
        var draw = ImGui.GetWindowDrawList();
        if (list.Count == 0)
        {
            var empty = list.Total == 0
                ? "No charts yet. Scan a sector from the flight view or the map to chart it."u8
                : "No chart has that name."u8;
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, empty, inner / Kit.Scale);
            ImGui.EndChild();
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new float2(inner, rowHeight * list.Count));
        var top = ImGui.GetWindowPos().Y;
        var first = Math.Max(0, (int)MathF.Floor((top - origin.Y) / rowHeight));
        var last = Math.Min(list.Count, (int)MathF.Ceiling((top + height - origin.Y) / rowHeight));
        for (var row = first; row < last; row++)
            DrawChartRow(draw, snapshot, world, galaxy, list, row, new float2(origin.X, origin.Y + row * rowHeight), inner, input, look);
        ImGui.EndChild();
    }

    private void DrawChartRow(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, Galaxy galaxy, ChartCatalogue list, int row, float2 at, float width, FloatingInput input, MapTuning look)
    {
        var index = list.ChartAt(row);
        var sector = snapshot.Exploration.Charts[index].Sector;
        var rowHeight = Kit.Px(ChartRowHeight);
        var buttons = Kit.Px(ChartButtonWidth);
        var pad = Kit.Px(Theme.Space3);
        var textWidth = width - buttons - pad * 3 - Kit.Px(12);
        var interactive = input != FloatingInput.Passive;
        draw.AddLine(new float2(at.X, at.Y + rowHeight - 1), new float2(at.X + width, at.Y + rowHeight - 1), Kit.Color(Theme.Line, 0.5f));

        var hasDetail = list.TryDetail(index, out var detail);
        if (hasDetail)
        {
            var star = galaxy.StarClasses[Math.Clamp(detail.StarClass, 0, galaxy.StarClasses.Length - 1)];
            DrawStarIcon(draw, at + new float2(Kit.Px(10), Kit.Px(16)), Math.Min(Kit.Px(look.StarIconSize), Kit.Px(7)), look.ShapeOf(star.Name), StarColor(star.Color), look);
        }

        var nameAt = at + new float2(Kit.Px(24), Kit.Px(5));
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, nameAt, Theme.Text, MapName(galaxy, sector), textWidth - Kit.Px(18));

        var meters = (float)ChartCatalogue.MetersTo(galaxy, sector, world.OwnPosition);
        var here = world.Sector.Coord;
        var line1 = nameAt + new float2(0, Kit.Px(24));
        // The figure is built before the line that holds it: a line cannot be built inside another.
        var distance = Distance(meters);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, line1, Theme.TextDim,
            sector == here ? Kit.F($"{sector.X} : {sector.Z}   LAYER {sector.Y:+0;-0;0}   YOU ARE HERE") : Kit.F($"{sector.X} : {sector.Z}   LAYER {sector.Y:+0;-0;0}   {distance}"), textWidth);
        var age = list.AgeSeconds(index, snapshot.ServerTick);
        var line2 = line1 + new float2(0, Kit.Px(19));
        if (hasDetail)
        {
            var gas = detail.Clouds == 0 ? "NO GAS"
                : detail.Lightning ? "GAS LIGHTNING"
                : detail.Cold ? "GAS COLD"
                : "GAS";
            // A cloud centred here that the player has sampled says so; the GAS window and the sector card have its mixture.
            if (_gasLedger.Find(sector) >= 0)
                gas = detail.Lightning ? "GAS LIGHTNING, SAMPLED" : detail.Cold ? "GAS COLD, SAMPLED" : "GAS, SAMPLED";
            var fields = Kit.F($"{detail.Fields} {(detail.Fields == 1 ? "FIELD" : "FIELDS")}");
            var fieldsWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, fields).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, line2, detail.Fields > 0 ? Theme.Accent : Theme.TextMute, fields);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, line2 + new float2(fieldsWidth + Kit.Px(Theme.Space3), 0),
                detail.Lightning ? Theme.AccentBright : detail.Cold ? Theme.Info : detail.Clouds > 0 ? Theme.TextDim : Theme.TextMute, gas, Math.Max(0f, textWidth - fieldsWidth - Kit.Px(Theme.Space3)));
        }
        else
        {
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, line2, Theme.TextMute, "READING CHART"u8);
        }

        var surveyed = age < 120f ? Kit.F($"SURVEYED {age:0} S AGO, NOT LIVE") : Kit.F($"SURVEYED {age / 60f:0} MIN AGO, NOT LIVE");
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, line2 + new float2(0, Kit.Px(19)), Theme.TextMute, surveyed, textWidth);

        // The two actions, stacked at the right: the map, and a jump through the one plan every menu reads.
        var buttonX = at.X + width - buttons - Kit.Px(Theme.Space1);
        ImGui.PushID(index);
        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        ImGui.SetCursorScreenPos(new float2(buttonX, at.Y + Kit.Px(6)));
        if (ChartAction("##chartmap"u8, "SHOW ON MAP"u8, true, buttons, interactive))
            ShowChartOnMap(sector);
        var target = galaxy.CenterOf(sector);
        var plan = ChartActions.Plan(snapshot, world, target);
        var plots = plan.IsPlot(world.Selection.Length == 0 || world.JumpPlotsOwnOnly);
        ImGui.SetCursorScreenPos(new float2(buttonX, at.Y + Kit.Px(6) + Kit.Px(28)));
        var jump = ChartAction("##chartjump"u8, plots ? "PLOT JUMP"u8 : "JUMP HERE"u8, plan.Ready > 0, buttons, interactive);
        if (ImGui.IsItemHovered())
        {
            _chartHoverJump = index;
            _chartHoverJumpFrame = ImGui.GetFrameCount();
        }

        ImGui.PopItemFlag();
        ImGui.PopID();
        if (jump)
        {
            if (Bridge is { } bridge)
                ChartActions.Jump(world, bridge, target);
            SetNotice(plots ? "Jump plotted. Leave interface mode, aim at NAV and boost."u8 : "Jump ordered."u8, default, false);
        }
    }

    /// <summary>A small action button: outlined, amber while hot, dim when it cannot be pressed.</summary>
    private bool ChartAction(ImString id, ReadOnlySpan<byte> label, bool enabled, float width, bool interactive)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var height = Kit.Px(24);
        var size = new float2(width, height);
        var active = enabled && interactive;
        var pressed = ImGui.InvisibleButton(id, size);
        var hot = Kit.Animate(ImGui.GetItemID(), interactive && ImGui.IsItemHovered() ? 1f : 0f);
        draw.AddRect(position, position + size, Kit.Color(active ? Kit.Mix(Theme.Line, Theme.Accent, hot) : Theme.Line, active ? 1f : 0.6f));
        var text = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, position + new float2(MathF.Round((width - text.X) * 0.5f), MathF.Round((height - text.Y) * 0.5f)),
            active ? Kit.Mix(Theme.TextDim, Theme.Text, hot) : Theme.TextMute, label);
        return pressed && active;
    }

    /// <summary>
    /// The line under the list: a notice from an action, else the jump the hovered row's button would make, else what
    /// the actions are.
    /// </summary>
    private void DrawChartFooter(SessionSnapshot snapshot, WorldView world, Galaxy galaxy, ChartCatalogue list, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var hover = _chartHoverJumpFrame == ImGui.GetFrameCount() ? _chartHoverJump : -1;
        if (NoticeShown)
            Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, at, _noticeBad ? Theme.Bad : Theme.Good, _notice.AsSpan(0, _noticeLength), width);
        else if (hover >= 0 && hover < snapshot.Exploration.ChartCount)
        {
            var plan = ChartActions.Plan(snapshot, world, galaxy.CenterOf(snapshot.Exploration.Charts[hover].Sector));
            var summary = JumpSummary(snapshot, plan, out var color);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, color, Kit.F($"JUMP         {summary}"), width);
        }
        else
            Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, at, Theme.TextMute, "Charts are yours: no ship or station holds them."u8, width);
        ImGui.Dummy(new float2(width, Kit.Measure(_ui.Sans, Theme.FontSmall, "Ag"u8).Y));
    }

    /// <summary>A window that says what is coming: the player asked for it to exist before there is anything to show.</summary>
    private void DrawPlaceholderPane(SocialPane pane, ImString id, ReadOnlySpan<byte> title, ReadOnlySpan<byte> text)
    {
        if (!BeginPane(pane, id, title, false, out var width, out _))
            return;
        Kit.Label(_ui.SansMedium, Theme.FontCaption, Theme.Accent, "COMING SOON"u8);
        Kit.Space(Theme.Space2);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, text, width / Kit.Scale);
        EndPane();
    }
}
