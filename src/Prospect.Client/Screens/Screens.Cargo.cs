using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private readonly CargoHoldView _cargoView = new();
    private readonly OreHoldView _oreView = new();
    private bool _cargoSelectPending;

    /// <summary>
    /// The Cargo window, in two tabs. CONTAINERS shows the flown ship's hold as a grid of size-stepped,
    /// resource-coloured container tiles, with a details panel and Jettison / Jettison, keep claim actions. ORE shows
    /// the ore bins: a totals row per ore and a tile per bin.
    /// </summary>
    private void DrawCargoPane(SessionSnapshot snapshot)
    {
        _cargoView.Update(snapshot);
        var tab = OreHoldView.TabOf(Pane(SocialPane.Cargo).Tab);
        if (tab == CargoTab.Ore)
        {
            // A container selected before the tab changed would leave its callout on the ship with no tile to show for it.
            _cargoView.ClearSelection();
        }
        else if (_cargoSelectPending && _cargoView.ContainerCount > 0)
        {
            _cargoView.Select(0);
            _cargoSelectPending = false;
        }
        if (!BeginPane(SocialPane.Cargo, "##cargo"u8, "Cargo"u8, false, out var width, out var input))
            return;

        var draw = ImGui.GetWindowDrawList();
        var interactive = input != FloatingInput.Passive;

        if (!_cargoView.HasHull)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "No ship flown."u8, width / Kit.Scale);
            EndPane();
            return;
        }

        tab = DrawCargoTabs(draw, width, tab, interactive);
        if (tab == CargoTab.Ore)
        {
            DrawOreTab(snapshot, width);
            EndPane();
            return;
        }

        var hull = snapshot.Hulls[snapshot.OwnHull];
        var headerAt = ImGui.GetCursorScreenPos();
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, headerAt, Theme.TextDim,
            Kit.F($"HOLD   {_cargoView.UsedUnits} / {_cargoView.CapacityUnits} u"));
        var massText = Kit.F($"{_cargoView.TotalMassKg:0} kg");
        var massSize = Kit.Measure(_ui.Mono, Theme.FontCaption, massText);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(headerAt.X + width - massSize.X, headerAt.Y), Theme.Text, massText);
        var headerHeight = Kit.Px(24);
        draw.AddLine(new float2(headerAt.X, headerAt.Y + headerHeight - Kit.Px(4)),
            new float2(headerAt.X + width, headerAt.Y + headerHeight - Kit.Px(4)), Kit.Color(Theme.Line, 0.7f));
        ImGui.SetCursorScreenPos(headerAt);
        ImGui.Dummy(new float2(width, headerHeight));
        Kit.Space(Theme.Space1);

        if (_cargoView.IsDrone)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Escape drones have no cargo racks."u8, width / Kit.Scale);
            EndPane();
            return;
        }

        if (!_cargoView.HasRacks)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "This ship has no cargo racks."u8, width / Kit.Scale);
            EndPane();
            return;
        }

        if (_cargoView.ContainerCount == 0)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Hold is empty. Use a tractor beam to pick up containers."u8, width / Kit.Scale);
            EndPane();
            return;
        }

        var detailsHeight = Kit.Px(_cargoView.InFtlTransit ? 146 : 126);
        var gridWidth = Math.Max(1f, width - Kit.Px(Theme.Space2));
        var layoutHeight = _cargoView.Layout(hull.Cargo.Span, gridWidth, Kit.Scale);
        var room = ImGui.GetContentRegionAvail().Y - detailsHeight - Kit.Px(Theme.Space2);
        var gridHeight = Math.Clamp(layoutHeight + Kit.Px(Theme.Space2), Kit.Px(64), Math.Max(Kit.Px(64), room));
        DrawCargoGrid(snapshot, hull, width, gridWidth, layoutHeight, gridHeight, interactive);
        Kit.Space(Theme.Space2);
        DrawCargoDetails(snapshot, hull, width, interactive);
        EndPane();
    }

    /// <summary>
    /// The tab strip at the top of the Cargo window. A press on a tab is the window's saved state (<see cref="FloatingWindow.Tab"/>);
    /// returns the tab now shown.
    /// </summary>
    private CargoTab DrawCargoTabs(ImDrawListPtr draw, float width, CargoTab shown, bool interactive)
    {
        var look = CargoWindowLook.Current;
        var origin = ImGui.GetCursorScreenPos();
        var height = 0f;
        for (var i = 0; i < OreHoldView.TabCount; i++)
        {
            var tab = (CargoTab)i;
            var rect = OreHoldView.TabRect(tab, width, Kit.Scale, look);
            var min = origin + new float2(rect.X, rect.Y);
            var max = min + new float2(rect.Width, rect.Height);
            height = rect.Height;
            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(i);
            var pressed = ImGui.InvisibleButton("##cargotab"u8, new float2(rect.Width, rect.Height));
            var hot = interactive && ImGui.IsItemHovered();
            ImGui.PopID();
            if (pressed && interactive && shown != tab)
            {
                Pane(SocialPane.Cargo).Tab = i;
                shown = tab;
            }

            var selected = shown == tab;
            if (selected)
                draw.AddRectFilled(min, max, Kit.Color(Theme.PanelRaised, 0.9f));
            else if (hot)
                draw.AddRectFilled(min, max, Kit.Color(Theme.PanelRaised, 0.5f));
            draw.AddRectFilled(new float2(min.X, max.Y - Kit.Px(2)), max, Kit.Color(selected ? Theme.Accent : Theme.Line, selected ? 1f : 0.5f));
            ReadOnlySpan<byte> label = tab == CargoTab.Ore ? "ORE"u8 : "CONTAINERS"u8;
            var size = Kit.Measure(_ui.SansMedium, Theme.FontSmall, label);
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, min + new float2((rect.Width - size.X) * 0.5f, (rect.Height - size.Y) * 0.5f),
                selected ? Theme.Text : hot ? Theme.Text : Theme.TextDim, label);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new float2(width, height));
        Kit.Space(Theme.Space2);
        return shown;
    }

    /// <summary>
    /// The ORE tab: bins used and kilograms held, a row per ore (kilograms, grade weighted by mass, bins it fills of
    /// all the bins), then a tile per bin in the ore's colour with a fill bar. Read from <see cref="OreHoldView"/>,
    /// which reads only what the server told the client of the flown hull's bins.
    /// </summary>
    private void DrawOreTab(SessionSnapshot snapshot, float width)
    {
        _oreView.Update(snapshot);
        if (_oreView.BinCount == 0)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "This ship has no ore bins."u8, width / Kit.Scale);
            return;
        }

        var look = CargoWindowLook.Current;
        var flags = FloatingWindow.ChildFlags(FloatingInput.Passive);
        ImGui.BeginChild("##oretab"u8, new float2(width, Math.Max(Kit.Px(48), ImGui.GetContentRegionAvail().Y)), ImGuiChildFlags.None, flags);
        var draw = ImGui.GetWindowDrawList();
        var inner = Math.Max(1f, ImGui.GetContentRegionAvail().X - Kit.Px(Theme.Space2));
        var resources = (snapshot.Galaxy ?? snapshot.Content.Galaxy).Resources;

        var headerAt = ImGui.GetCursorScreenPos();
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, headerAt, Theme.TextDim, Kit.F($"BINS   {_oreView.BinsUsed} / {_oreView.BinCount} USED"));
        var heldText = Kit.F($"{_oreView.HeldKg:0} / {_oreView.CapacityKg:0} kg");
        var heldSize = Kit.Measure(_ui.Mono, Theme.FontCaption, heldText);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(headerAt.X + inner - heldSize.X, headerAt.Y), Theme.Text, heldText);
        var headerHeight = Kit.Px(24);
        draw.AddLine(new float2(headerAt.X, headerAt.Y + headerHeight - Kit.Px(4)), new float2(headerAt.X + inner, headerAt.Y + headerHeight - Kit.Px(4)), Kit.Color(Theme.Line, 0.7f));
        ImGui.Dummy(new float2(inner, headerHeight));

        // One row per ore held: swatch and name, then the figures in fixed columns from the right edge.
        var rowHeight = Kit.Px(look.OreRowHeight);
        var totals = _oreView.Totals;
        if (totals.Length == 0)
        {
            var at = ImGui.GetCursorScreenPos();
            Kit.Text(draw, _ui.Sans, Theme.FontSmall, at + new float2(0, Kit.Px(2)), Theme.TextMute, "No ore held."u8);
            ImGui.Dummy(new float2(inner, rowHeight));
        }

        for (var i = 0; i < totals.Length; i++)
        {
            var total = totals[i];
            var at = ImGui.GetCursorScreenPos();
            var color = StarColor(resources[total.Resource].Color);
            draw.AddCircleFilled(at + new float2(Kit.Px(5), rowHeight * 0.5f), Kit.Px(4), Kit.Color(color), 12);
            var binsText = Kit.F($"{total.Bins} / {_oreView.BinCount} bins");
            var binsSize = Kit.Measure(_ui.Mono, Theme.FontCaption, binsText);
            var binsLeft = at.X + inner - binsSize.X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(binsLeft, at.Y + (rowHeight - binsSize.Y) * 0.5f), Theme.TextDim, binsText);
            var gradeText = Kit.F($"grade {total.Grade * 100f:0} %");
            var gradeSize = Kit.Measure(_ui.Mono, Theme.FontCaption, gradeText);
            var gradeLeft = at.X + inner - Kit.Px(88) - gradeSize.X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(gradeLeft, at.Y + (rowHeight - gradeSize.Y) * 0.5f), Theme.TextDim, gradeText);
            var kgText = Kit.F($"{total.Kg:0} kg");
            var kgSize = Kit.Measure(_ui.Mono, Theme.FontCaption, kgText);
            var kgLeft = gradeLeft - Kit.Px(Theme.Space3) - kgSize.X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(kgLeft, at.Y + (rowHeight - kgSize.Y) * 0.5f), Theme.Text, kgText);
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, at + new float2(Kit.Px(14), Kit.Px(2)), Theme.Text, Kit.F($"{resources[total.Resource].Name}"), Math.Max(1f, kgLeft - at.X - Kit.Px(20)));
            ImGui.Dummy(new float2(inner, rowHeight));
        }

        Kit.Space(Theme.Space2);
        var origin = ImGui.GetCursorScreenPos();
        var layoutHeight = _oreView.Layout(inner, Kit.Scale, look);
        var tiles = _oreView.Tiles;
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            var min = origin + new float2(tile.X, tile.Y);
            var max = min + new float2(tile.Width, tile.Height);
            var empty = tile.IsEmpty;
            var color = empty ? Theme.TextMute : StarColor(resources[tile.Resource].Color);
            draw.AddRectFilled(min, max, Kit.Color(Theme.Panel, empty ? 0.88f * look.OreEmptyAlpha : 0.88f));
            if (!empty)
            {
                draw.AddRectFilled(min, max, Kit.Color(color, look.OreTintAlpha));
                draw.AddRectFilled(min, new float2(min.X + Kit.Px(3), max.Y), Kit.Color(color, 0.95f));
            }

            draw.AddRect(min, max, Kit.Color(Theme.Line, empty ? 0.6f : 1f));
            var left = min.X + Kit.Px(7);
            var textWidth = Math.Max(1f, tile.Width - Kit.Px(11));
            if (empty)
                Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, new float2(left, min.Y + Kit.Px(4)), Theme.TextMute, "EMPTY"u8, textWidth);
            else
            {
                var gradeText = Kit.F($"{tile.Grade * 100f:0} %");
                var gradeSize = Kit.Measure(_ui.Mono, Theme.FontCaption, gradeText);
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(max.X - Kit.Px(5) - gradeSize.X, min.Y + Kit.Px(5)), Theme.TextDim, gradeText);
                Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, new float2(left, min.Y + Kit.Px(4)), Theme.Text,
                    Kit.F($"{resources[tile.Resource].Name}"), Math.Max(1f, textWidth - gradeSize.X - Kit.Px(Theme.Space1)));
            }

            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(left, min.Y + Kit.Px(22)), empty ? Theme.TextMute : Theme.TextDim,
                Kit.F($"{tile.Kg:0} / {tile.Capacity:0} kg"), textWidth);
            var barPad = Kit.Px(5);
            var barHeight = Kit.Px(look.OreBarHeight);
            var barMin = new float2(min.X + Kit.Px(7), max.Y - barPad - barHeight);
            var barMax = new float2(max.X - barPad, max.Y - barPad);
            draw.AddRectFilled(barMin, barMax, Kit.Color(Theme.Line, 0.85f));
            if (tile.Fill > 0f)
                draw.AddRectFilled(barMin, new float2(barMin.X + (barMax.X - barMin.X) * tile.Fill, barMax.Y), Kit.Color(color, 0.95f));
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new float2(inner, Math.Max(1f, layoutHeight)));
        ImGui.EndChild();
    }

    private void DrawCargoGrid(SessionSnapshot snapshot, in HullView hull, float width, float gridWidth, float layoutHeight, float height, bool interactive)
    {
        var flags = FloatingWindow.ChildFlags(interactive ? FloatingInput.Full : FloatingInput.Passive);
        ImGui.BeginChild("##cargogrid"u8, new float2(width, height), ImGuiChildFlags.None, flags);
        var gridDraw = ImGui.GetWindowDrawList();
        var cargo = hull.Cargo.Span;
        var origin = ImGui.GetCursorScreenPos();
        var tiles = _cargoView.Tiles;
        var sizes = snapshot.Content.Cargo.Sizes;
        var resources = snapshot.Galaxy?.Resources ?? [];
        var motion = _app.World.CargoMotion;

        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        for (var i = 0; i < tiles.Length; i++)
        {
            var rect = tiles[i];
            var c = cargo[i];
            var tileMin = origin + new float2(rect.X, rect.Y);
            var tileMax = tileMin + new float2(rect.Width, rect.Height);
            ImGui.SetCursorScreenPos(tileMin);
            ImGui.PushID(i);
            var pressed = ImGui.InvisibleButton("##cargotile"u8, new float2(rect.Width, rect.Height));
            var hot = interactive && ImGui.IsItemHovered();
            ImGui.PopID();
            if (pressed && interactive)
            {
                // A second click on the selected tile lets go of it, and so of its callout on the ship.
                if (_cargoView.SelectedIndex == i)
                    _cargoView.ClearSelection();
                else
                    _cargoView.Select(i);
            }

            var selected = _cargoView.SelectedIndex == i;
            var resColor = (uint)c.Resource < (uint)resources.Length ? StarColor(resources[c.Resource].Color) : Theme.TextMute;
            gridDraw.AddRectFilled(tileMin, tileMax, Kit.Color(selected ? Theme.PanelRaised : Theme.Panel, selected ? 0.98f : 0.88f));
            gridDraw.AddRectFilled(tileMin, tileMax, Kit.Color(resColor, selected ? 0.22f : hot ? 0.16f : 0.10f));
            gridDraw.AddRectFilled(tileMin, new float2(tileMin.X + Kit.Px(3), tileMax.Y), Kit.Color(resColor, 0.95f));
            gridDraw.AddRect(tileMin, tileMax, Kit.Color(selected ? Theme.Accent : hot ? Theme.LineStrong : Theme.Line), 0f, ImDrawFlags.None, selected ? Kit.Px(1.5f) : 1f);

            var sizeName = (uint)c.Size < (uint)sizes.Length ? sizes[c.Size].Name : "?";
            var textLeft = tileMin.X + Kit.Px(7);
            var textWidth = Math.Max(1f, rect.Width - Kit.Px(11));
            Kit.ClippedText(gridDraw, _ui.SansMedium, Theme.FontSmall, new float2(textLeft, tileMin.Y + Kit.Px(4)), Theme.Text, Kit.F($"{sizeName}"), textWidth);

            // A container moving in its rack says so in place of its mass, until its locks hold (CargoMotion).
            var stage = motion.Records.Length == cargo.Length ? motion.Stage(i) : CargoStage.Stored;
            if (stage != CargoStage.Stored)
            {
                var line = new float2(textLeft, tileMin.Y + Kit.Px(20));
                gridDraw.AddRectFilled(tileMin, new float2(tileMax.X, tileMin.Y + Kit.Px(2)), Kit.Color(stage == CargoStage.Locked ? Theme.Good : Theme.Accent, 0.95f));
                if (stage == CargoStage.Locked)
                    Kit.ClippedText(gridDraw, _ui.SansMedium, Theme.FontCaption, line, Theme.Good, "LOCKED"u8, textWidth);
                else if (stage == CargoStage.Transferring)
                    Kit.ClippedText(gridDraw, _ui.SansMedium, Theme.FontCaption, line, Theme.Accent, "TRANSFERRING"u8, textWidth);
                else
                    Kit.ClippedText(gridDraw, _ui.SansMedium, Theme.FontCaption, line, Theme.Accent, "POSITIONING"u8, textWidth);
            }
            else if (c.Resource < 0)
                Kit.ClippedText(gridDraw, _ui.Sans, Theme.FontCaption, new float2(textLeft, tileMin.Y + Kit.Px(20)), Theme.TextMute, "Empty"u8, textWidth);
            else
                Kit.ClippedText(gridDraw, _ui.Mono, Theme.FontCaption, new float2(textLeft, tileMin.Y + Kit.Px(20)), Theme.TextDim, Kit.F($"{c.Amount:0} kg"), textWidth);

            var fill = CargoHoldView.FillFraction(snapshot, c);
            var barPad = Kit.Px(5);
            var barHeight = Kit.Px(4);
            var barMin = new float2(tileMin.X + Kit.Px(7), tileMax.Y - barPad - barHeight);
            var barMax = new float2(tileMax.X - barPad, tileMax.Y - barPad);
            gridDraw.AddRectFilled(barMin, barMax, Kit.Color(Theme.Line, 0.85f));
            if (fill > 0f)
                gridDraw.AddRectFilled(barMin, new float2(barMin.X + (barMax.X - barMin.X) * fill, barMax.Y), Kit.Color(resColor, 0.95f));
        }

        ImGui.PopItemFlag();
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new float2(gridWidth, Math.Max(1f, layoutHeight)));
        ImGui.EndChild();
    }

    private void DrawCargoDetails(SessionSnapshot snapshot, in HullView hull, float width, bool interactive)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        draw.AddLine(at, new float2(at.X + width, at.Y), Kit.Color(Theme.Line, 0.8f));
        var body = at + new float2(0, Kit.Px(Theme.Space2));

        if (_cargoView.SelectedIndex < 0 || _cargoView.SelectedIndex >= hull.Cargo.Length)
        {
            Kit.Text(draw, _ui.Sans, Theme.FontSmall, body, Theme.TextMute, "Select a container to inspect or jettison."u8);
            return;
        }

        var c = hull.Cargo.Span[_cargoView.SelectedIndex];
        var sizes = snapshot.Content.Cargo.Sizes;
        var resources = snapshot.Galaxy?.Resources ?? [];
        var hasSize = (uint)c.Size < (uint)sizes.Length;
        var sizeName = hasSize ? sizes[c.Size].Name : "?";
        var sizeUnits = hasSize ? sizes[c.Size].Units : 0;
        var maxHp = hasSize ? sizes[c.Size].Hull : 0f;
        var hasRes = (uint)c.Resource < (uint)resources.Length;
        var resName = hasRes ? resources[c.Resource].Name : "Empty";
        var resColor = hasRes ? StarColor(resources[c.Resource].Color) : Theme.TextMute;
        var fill = CargoHoldView.FillFraction(snapshot, c);
        var hpFrac = CargoHoldView.HealthFraction(snapshot, c);

        // Row 1: resource swatch + name, size, rack index.
        draw.AddCircleFilled(body + new float2(Kit.Px(5), Kit.Px(8)), Kit.Px(4), Kit.Color(resColor), 12);
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, body + new float2(Kit.Px(14), 0), Theme.Text,
            Kit.F($"{resName}   {sizeName} ({sizeUnits} u)"));
        var rackText = Kit.F($"Rack #{c.Rack}");
        var rackSize = Kit.Measure(_ui.Mono, Theme.FontCaption, rackText);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(body.X + width - rackSize.X, body.Y + Kit.Px(2)), Theme.TextDim, rackText);

        // Row 2: mass, fill %, HP.
        var row2 = body + new float2(0, Kit.Px(22));
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, row2, Theme.TextDim,
            Kit.F($"{c.Amount:0} kg   {fill * 100f:0}% full   HP {c.Hull:0}/{maxHp:0}"));

        // Row 3: HP bar.
        var barMin = body + new float2(0, Kit.Px(42));
        var barMax = barMin + new float2(width, Kit.Px(5));
        var hpColor = hpFrac > 0.5f ? Theme.Good : hpFrac > 0.25f ? Theme.Accent : Theme.Bad;
        draw.AddRectFilled(barMin, barMax, Kit.Color(Theme.Line, 0.85f));
        if (hpFrac > 0f)
            draw.AddRectFilled(barMin, new float2(barMin.X + width * hpFrac, barMax.Y), Kit.Color(hpColor, 0.95f));

        // Row 4: Jettison and Jettison, keep claim buttons.
        var btnY = body.Y + Kit.Px(56);
        var gap = Kit.Px(Theme.Space2);
        var abandonWidth = MathF.Floor((width - gap) * 0.42f);
        var claimWidth = width - gap - abandonWidth;
        var canJettison = interactive && !_cargoView.InFtlTransit && Bridge is not null;
        var delay = snapshot.Content.Cargo.PickupDelaySeconds;

        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        ImGui.SetCursorScreenPos(new float2(body.X, btnY));
        var abandonPressed = ChartAction("##jettisonabandon"u8, "JETTISON"u8, canJettison, abandonWidth, interactive);
        var abandonHot = interactive && ImGui.IsItemHovered();

        ImGui.SetCursorScreenPos(new float2(body.X + abandonWidth + gap, btnY));
        var claimPressed = ChartAction("##jettisonclaim"u8, "JETTISON, KEEP CLAIM"u8, canJettison, claimWidth, interactive);
        var claimHot = interactive && ImGui.IsItemHovered();
        ImGui.PopItemFlag();

        if (abandonHot)
            DrawCargoTooltip("Anyone can pick it up."u8, Kit.F($"Pickup waits {delay:0} s."));
        else if (claimHot)
            DrawCargoTooltip("Only your ships' tractors can pick it up."u8, Kit.F($"Anyone can still shoot it. Pickup waits {delay:0} s."));

        if (abandonPressed && canJettison)
            Bridge!.JettisonCargo(hull.Id, c.Rack, _cargoView.SelectedIndex, snapshot.OwnCargoTick, JettisonMode.Abandon);
        else if (claimPressed && canJettison)
            Bridge!.JettisonCargo(hull.Id, c.Rack, _cargoView.SelectedIndex, snapshot.OwnCargoTick, JettisonMode.KeepClaim);

        if (_cargoView.InFtlTransit)
            Kit.Text(draw, _ui.Sans, Theme.FontCaption, new float2(body.X, btnY + Kit.Px(28)), Theme.Accent, "Cannot jettison during FTL transit."u8);
    }

    private void DrawCargoTooltip(ReadOnlySpan<byte> line1, ReadOnlySpan<byte> line2)
    {
        var fore = ImGui.GetForegroundDrawList();
        var t1 = Kit.Measure(_ui.SansMedium, Theme.FontCaption, line1);
        var t2 = Kit.Measure(_ui.Sans, Theme.FontCaption, line2);
        var pad = Kit.Px(Theme.Space2);
        var box = new float2(MathF.Ceiling(Math.Max(t1.X, t2.X)) + pad * 2f, pad * 2f + Kit.Px(34));
        var pos = ImGui.GetMousePos() + new float2(Kit.Px(12), Kit.Px(14));
        pos.X = Math.Clamp(pos.X, Kit.Px(6), Math.Max(Kit.Px(6), _ui.DisplaySize.X - box.X - Kit.Px(6)));
        pos.Y = Math.Clamp(pos.Y, Kit.Px(6), Math.Max(Kit.Px(6), _ui.DisplaySize.Y - box.Y - Kit.Px(6)));
        Kit.PanelFrame(fore, pos, pos + box);
        Kit.Text(fore, _ui.SansMedium, Theme.FontCaption, pos + new float2(pad, pad), Theme.Text, line1);
        Kit.Text(fore, _ui.Sans, Theme.FontCaption, pos + new float2(pad, pad + Kit.Px(17)), Theme.TextDim, line2);
    }
}
