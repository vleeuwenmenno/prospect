using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The names of the keys the ten action groups are bound to, upper case and encoded once. Rebuilt only when the
/// bindings change (<see cref="ActionMap.Version"/>), so drawing a frame takes no memory.
/// </summary>
internal sealed class ActionGroupKeys
{
    private readonly byte[][] _names = new byte[ActionGroups.Count][];
    private int _version = -1;

    public ActionGroupKeys()
    {
        for (var g = 0; g < _names.Length; g++) _names[g] = [];
    }

    /// <summary>Reads the bindings again if they changed since the last call.</summary>
    public void Update(ActionMap actions)
    {
        if (_version == actions.Version) return;
        _version = actions.Version;
        for (var group = 1; group <= ActionGroups.Count; group++)
        {
            var bindings = actions.Bindings(ActionGroupStrip.ActionOf(group));
            _names[group - 1] = System.Text.Encoding.UTF8.GetBytes(bindings.Length == 0 ? "UNBOUND" : Short(bindings[0].DisplayName).ToUpperInvariant());
        }
    }

    /// <summary>A key's name for a cell or a sentence: a number-row key by its digit ("Number1" is "1"), any other as it is.</summary>
    public static string Short(string name) => name.Length == 7 && name.StartsWith("Number", StringComparison.Ordinal) && char.IsAsciiDigit(name[6]) ? name[6..] : name;

    /// <summary>The name of the key of <paramref name="group"/> (1 to 10).</summary>
    public ReadOnlySpan<byte> Name(int group) => (uint)(group - 1) < (uint)_names.Length ? _names[group - 1] : default;
}

/// <summary>
/// Drawing of the action groups: the strip beside the flight console, its tooltip, and the section that changes a
/// laser's group without a rebuild. Layout and state are the font-free <see cref="ActionGroupStrip"/>; a press is
/// reported, never applied, except in the section, which asks the server through the bridge as the editor's requests do.
/// Nothing here allocates: text is formatted through <see cref="Kit.F"/>.
/// </summary>
internal static class ActionGroupPanel
{
    private const ImGuiWindowFlags Flags = ScreenHost.RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>The cell a frame's drawing found the pointer over and the cell that was pressed: groups 1 to 10, or 0 for none.</summary>
    public readonly record struct StripResult(int Hovered, int Pressed);

    // The strip

    /// <summary>
    /// Draws the strip's cells in <paramref name="rect"/> (from <see cref="ActionGroupStrip.Place"/>). The cells take
    /// a click only where the pointer is the interface's (the flight view's captured pointer gives Dear ImGui none).
    /// </summary>
    public static StripResult DrawStrip(ActionGroupStrip strip, ActionGroupKeys keys, in HudRect rect, float cellWidth, ActionGroupLook look)
    {
        if (strip.Count == 0) return default;
        ImGui.SetNextWindowPos(new float2(rect.Left, rect.Top));
        ImGui.SetNextWindowSize(new float2(rect.Width, rect.Height));
        ImGui.Begin("##actiongroups"u8, Flags);
        var draw = ImGui.GetWindowDrawList();
        var gap = Kit.Px(look.Gap);
        var hovered = 0;
        var pressed = 0;
        var cells = strip.Cells;
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var at = ActionGroupStrip.CellRect(rect, i, cellWidth, gap);
            var min = new float2(at.Left, at.Top);
            var size = new float2(at.Width, at.Height);
            ImGui.PushID(cell.Group);
            ImGui.SetCursorScreenPos(min);
            var click = ImGui.InvisibleButton("##cell"u8, size);
            var hot = ImGui.IsItemHovered();
            ImGui.PopID();
            DrawCell(draw, cell, keys.Name(cell.Group), min, size, hot, look);
            if (hot) hovered = cell.Group;
            if (click) pressed = cell.Group;
        }

        ImGui.End();
        return new StripResult(hovered, pressed);
    }

    private static void DrawCell(ImDrawListPtr draw, in ActionGroupCell cell, ReadOnlySpan<byte> key, float2 min, float2 size, bool hot, ActionGroupLook look)
    {
        var ui = Kit.Ui;
        // Overheated wins over on and firing: the group fires nothing until a laser has cooled.
        var overheated = cell.Overheated;
        var tone = overheated ? look.OverheatedRgb : cell.Firing ? look.FiringRgb : cell.On ? look.OnRgb : look.OffRgb;
        var alpha = overheated ? look.OverheatedAlpha : cell.Firing ? look.FiringAlpha : cell.On ? look.OnAlpha : look.OffAlpha;
        var previous = Kit.Fade;
        Kit.Fade = previous * (hot ? 1f : alpha);
        var max = min + size;
        Hud.Plate(draw, min, max, tone, cell.On || hot || overheated);
        draw.AddRectFilled(min + new float2(0, Kit.Px(4)), min + new float2(Kit.Px(3), size.Y - Kit.Px(4)), Kit.Color(tone));
        if (cell.On)
            draw.AddRectFilled(min + new float2(Kit.Px(3), Kit.Px(3)), max - new float2(Kit.Px(1), Kit.Px(3)), Kit.Color(tone, cell.Firing ? 0.3f : 0.14f));
        var pad = Kit.Px(Theme.Space2) + Kit.Px(3);
        var keySize = Kit.Measure(ui.Mono, Theme.FontCaption, key);
        var y = min.Y + MathF.Round((size.Y - keySize.Y) * 0.5f);
        Kit.Text(draw, ui.Mono, Theme.FontCaption, new float2(min.X + pad, y), cell.On ? tone : Theme.TextDim, key);
        var labelLeft = min.X + pad + keySize.X + Kit.Px(Theme.Space2);
        // The mark's room is kept whether or not it shows, so every cell of a strip words itself the same way.
        var room = max.X - labelLeft - Kit.Px(Theme.Space2) - Kit.Px(look.MarkPixels) - Kit.Px(Theme.Space1);
        var kind = look.Kind(cell.Kind);
        // The whole wording while it fits; only the count where it does not. The widest wording is measured, so cells agree.
        var whole = Kit.Measure(ui.SansMedium, Theme.FontCaption, Kit.F($"{kind} x{ActionGroups.Count}")).X <= room;
        var label = overheated ? (whole && Kit.Measure(ui.SansMedium, Theme.FontCaption, look.OverheatedLabel).X <= room ? look.OverheatedLabel : look.OverheatedShortLabel)
            : whole ? Kit.F($"{kind} x{cell.Parts}") : Kit.F($"x{cell.Parts}");
        Kit.ClippedText(draw, ui.SansMedium, Theme.FontCaption, new float2(labelLeft, y), overheated ? tone : cell.On ? Theme.Text : Theme.TextDim, label, Math.Max(0f, room));
        Kit.Fade = previous;

        // The heat of the group's hottest laser, along the cell's foot. Like the cutting mark it does not dim with the
        // cell: a group that is off still has to cool. A shut-down group shows the mark it must cool to.
        if (cell.Heat > 0f || overheated)
        {
            // Clear of the chamfered corner at the cell's lower left.
            var barLeft = min.X + Kit.Px(Theme.Chamfer);
            var barRight = max.X - Kit.Px(1);
            var barBottom = max.Y - Kit.Px(3);
            var barTop = barBottom - Kit.Px(look.HeatBarPixels);
            var heatTone = overheated ? look.OverheatedRgb : look.HeatRgb(cell.Heat);
            draw.AddRectFilled(new float2(barLeft, barTop), new float2(barRight, barBottom), Kit.Color(heatTone, look.HeatTrackAlpha));
            draw.AddRectFilled(new float2(barLeft, barTop), new float2(barLeft + MathF.Round((barRight - barLeft) * Math.Clamp(cell.Heat, 0f, 1f)), barBottom), Kit.Color(heatTone));
            if (cell.Locked > 0 && cell.UnlockHeat > 0f)
            {
                var markX = barLeft + MathF.Round((barRight - barLeft) * cell.UnlockHeat);
                draw.AddRectFilled(new float2(markX, barTop - Kit.Px(2)), new float2(markX + Kit.Px(1), barBottom), Kit.Color(Theme.Text));
            }
        }

        // A laser of the group is cutting rock: a mark that does not dim with the cell.
        if (cell.Cutting)
        {
            var half = Kit.Px(look.MarkPixels) * 0.5f;
            var centre = new float2(max.X - Kit.Px(Theme.Space2) - half, min.Y + size.Y * 0.5f);
            draw.AddRectFilled(centre - new float2(half, half), centre + new float2(half, half), Kit.Color(look.CuttingRgb));
        }
    }

    /// <summary>
    /// The tooltip of the cell of <paramref name="cell"/> under the pointer: its key and state, each laser in it with
    /// its figures, and what a click does. Drawn on <paramref name="draw"/> (the foreground list), it takes no input.
    /// </summary>
    /// <remarks>
    /// Each laser's line ends with its heat, or says it is overheated and the heat it fires again at. While the hull
    /// is inside a cold cloud (<paramref name="cold"/>) a line says how much faster heat is shed there.
    /// </remarks>
    public static void DrawTip(ImDrawListPtr draw, SessionSnapshot snapshot, uint hull, HullDesign design, in ActionGroupCell cell, ReadOnlySpan<byte> key,
        in HudRect cellRect, float2 display, ActionGroupLook look, bool cold = false, float coldCooling = 1f)
    {
        var ui = Kit.Ui;
        var pad = Kit.Px(Theme.Space3);
        var line = Kit.Px(16);
        var width = Math.Min(Kit.Px(look.TipWidth), display.X - Kit.Px(Theme.Space4) * 2);
        var height = pad * 2 + Kit.Px(18) + cell.Parts * line + (cold ? line : 0f) + line + Kit.Px(4);
        var left = Math.Clamp(cellRect.Left + cellRect.Width * 0.5f - width * 0.5f, Kit.Px(Theme.Space4), Math.Max(Kit.Px(Theme.Space4), display.X - width - Kit.Px(Theme.Space4)));
        var top = Math.Max(Kit.Px(Theme.Space4), cellRect.Top - Kit.Px(Theme.Space2) - height);
        var min = new float2(left, top);
        var overheated = cell.Overheated;
        var tone = overheated ? look.OverheatedRgb : cell.Firing ? look.FiringRgb : cell.On ? look.OnRgb : look.OffRgb;
        Hud.Plate(draw, min, min + new float2(width, height), tone, cell.On || overheated);
        var inner = width - pad * 2;
        var at = min + new float2(pad, pad);
        var state = overheated ? "OVERHEATED"u8 : cell.Firing ? "FIRING"u8 : cell.On ? "ON"u8 : "OFF"u8;
        Kit.ClippedText(draw, ui.SansMedium, Theme.FontCaption, at, cell.On || overheated ? tone : Theme.Text, Kit.F($"GROUP {key}   {state}"), inner);
        at.Y += Kit.Px(22);
        var parts = Math.Min(design.Lasers.Length, ActionGroupsMessage.MaxParts);
        for (var part = 0; part < parts; part++)
        {
            if (snapshot.GroupOf(hull, design, part) != cell.Group) continue;
            var laser = design.Mounts[design.Lasers[part]].Type.Laser!;
            var cutting = ActionGroupStrip.IsCutting(snapshot, hull, part);
            var heat = ActionGroupStrip.HeatOf(snapshot, hull, part) * 100f;
            var kind = look.Kind(ActionPartKind.Laser);
            if (ActionGroupStrip.IsLocked(snapshot, hull, part))
                Kit.ClippedText(draw, ui.Mono, Theme.FontCaption, at, look.OverheatedRgb, Kit.F($"{kind} {part + 1}   OVERHEATED {heat:0} %   FIRES AGAIN AT {laser.UnlockHeat * 100f:0} %"), inner);
            else
                Kit.ClippedText(draw, ui.Mono, Theme.FontCaption, at, cutting ? look.CuttingRgb : Theme.TextDim,
                    cutting ? Kit.F($"{kind} {part + 1}   CUTTING   HEAT {heat:0} %") : Kit.F($"{kind} {part + 1}   RANGE {laser.Range:0} M   {laser.Power:0.#} MW   HEAT {heat:0} %"), inner);
            at.Y += line;
        }

        if (cold)
        {
            Kit.ClippedText(draw, ui.Mono, Theme.FontCaption, at, look.ColdRgb, Kit.F($"COLD CLOUD: COOLING \u00d7{coldCooling:0.#}"), inner);
            at.Y += line;
        }

        Kit.ClippedText(draw, ui.Sans, Theme.FontCaption, at + new float2(0, Kit.Px(4)), Theme.TextMute, "CLICK TO CHANGE WHICH GROUP A LASER FIRES WITH"u8, inner);
    }

    // The section: the lasers of the flown hull and their groups

    /// <summary>The height of one laser's row in the section, in pixels at the current scale.</summary>
    public static float RowHeight(ActionGroupLook look) => Kit.Px(20) + Kit.Px(look.SelectorHeight) + Kit.Px(Theme.Space3);

    /// <summary>How many lasers the section lists for <paramref name="design"/>.</summary>
    public static int Rows(HullDesign design) => Math.Min(design.Lasers.Length, ActionGroupsMessage.MaxParts);

    // The width of each selector cell as a share of one group cell: the ten groups, then NONE and DESIGN.
    private const float NoneWeight = 1.7f, DesignWeight = 2.1f;

    /// <summary>
    /// Lists every laser of the flown hull <paramref name="hull"/> with the group it fires with, whether that group is
    /// set on the hull or is the design's, and a selector: the ten groups (named by their keys), NONE, and DESIGN,
    /// which drops the hull's own choice. A choice asks the server through <paramref name="bridge"/>; the row shows
    /// the server's answer when it arrives, never the click. It draws into the current Dear ImGui window from the
    /// cursor, <paramref name="width"/> pixels wide, and moves the cursor down; the popup over the strip hosts it, and
    /// the Ship window calls it from its own body.
    /// </summary>
    public static void DrawActionGroupsSection(SessionSnapshot snapshot, uint hull, HullDesign design, PlayerInputBridge bridge, ActionGroupKeys keys, float width, ActionGroupLook look)
    {
        var ui = Kit.Ui;
        var draw = ImGui.GetWindowDrawList();
        var rows = Rows(design);
        if (rows == 0)
        {
            Kit.Caption("THIS SHIP HAS NO LASERS"u8);
            return;
        }

        var rowHeight = RowHeight(look);
        var selector = Kit.Px(look.SelectorHeight);
        var gap = Kit.Px(3);
        var unit = Math.Max(1f, (width - gap * (ActionGroupSelector.Options - 1)) / (ActionGroups.Count + NoneWeight + DesignWeight));
        for (var part = 0; part < rows; part++)
        {
            var origin = ImGui.GetCursorScreenPos();
            var overridden = ActionGroupSelector.OverrideOf(snapshot, hull, part);
            var group = snapshot.GroupOf(hull, design, part);
            var chosen = ActionGroupSelector.Chosen(overridden);
            var cutting = ActionGroupStrip.IsCutting(snapshot, hull, part);
            var kind = look.Kind(ActionPartKind.Laser);
            var titleSize = Kit.Measure(ui.SansMedium, Theme.FontCaption, Kit.F($"{kind} {part + 1}"));
            Kit.Text(draw, ui.SansMedium, Theme.FontCaption, origin, Theme.Text, Kit.F($"{kind} {part + 1}"));
            var x = origin.X + Math.Max(titleSize.X + Kit.Px(Theme.Space3), Kit.Px(84));
            var room = origin.X + width - x;

            // The laser's heat at the row's right end, left of the cutting mark; the words before it give way.
            var heat = ActionGroupStrip.HeatOf(snapshot, hull, part);
            var locked = ActionGroupStrip.IsLocked(snapshot, hull, part);
            if (heat > 0f || locked)
            {
                var heatText = locked ? Kit.F($"OVERHEATED {heat * 100f:0} %") : Kit.F($"HEAT {heat * 100f:0} %");
                var heatWidth = Kit.Measure(ui.Mono, Theme.FontCaption, heatText).X;
                var heatX = origin.X + width - Kit.Px(look.MarkPixels) - Kit.Px(Theme.Space2) - heatWidth;
                Kit.Text(draw, ui.Mono, Theme.FontCaption, new float2(heatX, origin.Y), locked ? look.OverheatedRgb : look.HeatRgb(heat), heatText);
                room = Math.Max(0f, heatX - Kit.Px(Theme.Space2) - x);
            }
            if (group == ActionGroups.None)
                Kit.ClippedText(draw, ui.Mono, Theme.FontCaption, new float2(x, origin.Y), Theme.TextMute, overridden == ActionGroups.None ? "DOES NOT FIRE   SET ON THIS SHIP"u8 : "DOES NOT FIRE   DESIGN"u8, room);
            else
                Kit.ClippedText(draw, ui.Mono, Theme.FontCaption, new float2(x, origin.Y), overridden == ActionGroups.NotOverridden ? Theme.TextDim : Theme.Accent,
                    overridden == ActionGroups.NotOverridden ? Kit.F($"FIRES WITH {keys.Name(group)}   DESIGN") : Kit.F($"FIRES WITH {keys.Name(group)}   SET ON THIS SHIP"), room);
            if (cutting)
            {
                var half = Kit.Px(look.MarkPixels) * 0.5f;
                var centre = new float2(origin.X + width - half, origin.Y + titleSize.Y * 0.5f);
                draw.AddRectFilled(centre - new float2(half, half), centre + new float2(half, half), Kit.Color(look.CuttingRgb));
            }

            var cx = origin.X;
            var top = origin.Y + Kit.Px(20);
            for (var option = 0; option < ActionGroupSelector.Options; option++)
            {
                var w = MathF.Round(option == ActionGroupSelector.NoneOption ? unit * NoneWeight : option == ActionGroupSelector.DesignOption ? unit * DesignWeight : unit);
                var min = new float2(cx, top);
                var size = new float2(w, selector);
                ImGui.PushID(part * ActionGroupSelector.Options + option);
                ImGui.SetCursorScreenPos(min);
                var pressed = ImGui.InvisibleButton("##option"u8, size);
                var hot = ImGui.IsItemHovered();
                ImGui.PopID();
                if (pressed) bridge.SetActionGroup(hull, part, ActionGroupSelector.ValueOf(option));
                var picked = option == chosen;
                // While the design's group stands, the group it names is outlined so the row says where the part fires.
                var effective = group != ActionGroups.None && option == group - 1;
                draw.AddRectFilled(min, min + size, Kit.Color(picked ? Theme.Accent : hot ? Theme.PanelRaised : Theme.Panel, picked ? 0.95f : 0.9f));
                draw.AddRect(min, min + size, Kit.Color(picked ? Theme.AccentBright : effective ? Theme.Accent : hot ? Theme.LineStrong : Theme.Line, effective && !picked ? 0.9f : 1f));
                ReadOnlySpan<byte> label = option == ActionGroupSelector.NoneOption ? "NONE"u8 : option == ActionGroupSelector.DesignOption ? "DESIGN"u8 : keys.Name(option + 1);
                var text = Kit.Measure(ui.Mono, Theme.FontCaption, label);
                var textX = min.X + MathF.Round(Math.Max(Kit.Px(2), (size.X - text.X) * 0.5f));
                Kit.ClippedText(draw, ui.Mono, Theme.FontCaption, new float2(textX, min.Y + MathF.Round((size.Y - text.Y) * 0.5f)), picked ? Theme.AccentInk : hot ? Theme.Text : Theme.TextDim, label, Math.Max(0f, size.X - Kit.Px(4)));
                cx += w + gap;
            }

            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new float2(width, rowHeight));
        }
    }

    // The popup

    /// <summary>The popup's rectangle over the strip: centred on it, above it, kept on the screen.</summary>
    public static HudRect PopupRect(in HudRect strip, float2 display, float scale, int rows, ActionGroupLook look)
    {
        float Px(float design) => MathF.Round(design * scale);
        var width = Math.Min(Px(look.PopupWidth), display.X - Px(FlightConsole.Side) * 2);
        var rowHeight = Px(20) + Px(look.SelectorHeight) + Px(Theme.Space3);
        var fixedHeight = Px(34) + Px(Theme.Space3) * 2 + Px(18);
        var wanted = fixedHeight + rowHeight * Math.Clamp(rows, 1, look.PopupVisibleRows);
        var bottom = strip.Top - Px(Theme.Space2);
        var top = Math.Max(HudLayout.Top(scale) + Px(Theme.Space2), bottom - wanted);
        var left = Math.Clamp(MathF.Round((strip.Left + strip.Right) * 0.5f - width * 0.5f), Px(FlightConsole.Side), Math.Max(Px(FlightConsole.Side), display.X - width - Px(FlightConsole.Side)));
        return new HudRect(left, top, left + width, Math.Max(top + fixedHeight, bottom));
    }

    /// <summary>
    /// Draws the popup that holds the section, in <paramref name="rect"/> (from <see cref="PopupRect"/>); true when its
    /// close button was pressed. The window takes no input while the pilot's pointer is captured, as every window over
    /// the flight view, and Escape closes it through the host.
    /// </summary>
    public static bool DrawPopup(SessionSnapshot snapshot, uint hull, HullDesign design, PlayerInputBridge bridge, ActionGroupKeys keys, in HudRect rect, ActionGroupLook look)
    {
        var ui = Kit.Ui;
        var min = new float2(rect.Left, rect.Top);
        var size = new float2(rect.Width, rect.Height);
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##actiongroupspopup"u8, Flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var pad = Kit.Px(Theme.Space3);
        Kit.Text(draw, ui.SansBold, Theme.FontSmall, min + new float2(pad, Kit.Px(10)), Theme.Text, "ACTION GROUPS"u8);

        var closeSize = Kit.Px(24);
        var closeMin = new float2(min.X + size.X - pad - closeSize + Kit.Px(4), min.Y + Kit.Px(6));
        ImGui.SetCursorScreenPos(closeMin);
        var closed = ImGui.InvisibleButton("##close"u8, new float2(closeSize, closeSize));
        var hot = ImGui.IsItemHovered();
        var tone = Kit.Color(hot ? Theme.Text : Theme.TextDim);
        var centre = closeMin + new float2(closeSize, closeSize) * 0.5f;
        var arm = Kit.Px(5);
        draw.AddLine(centre - new float2(arm, arm), centre + new float2(arm, arm), tone, 1.5f);
        draw.AddLine(centre + new float2(-arm, arm), centre + new float2(arm, -arm), tone, 1.5f);

        var listTop = min.Y + Kit.Px(34);
        var listHeight = rect.Bottom - Kit.Px(18) - Kit.Px(Theme.Space3) - listTop;
        ImGui.SetCursorScreenPos(new float2(min.X + pad, listTop));
        ImGui.BeginChild("##actiongroupslist"u8, new float2(size.X - pad * 2, Math.Max(Kit.Px(40), listHeight)), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
        DrawActionGroupsSection(snapshot, hull, design, bridge, keys, size.X - pad * 2 - Kit.Px(12), look);
        ImGui.EndChild();
        Kit.ClippedText(draw, ui.Sans, Theme.FontCaption, new float2(min.X + pad, rect.Bottom - Kit.Px(18) - Kit.Px(Theme.Space2)), Theme.TextMute,
            "SET HERE TAKES EFFECT AT ONCE, NO REBUILD. DESIGN GOES BACK TO THE DESIGN'S GROUP."u8, size.X - pad * 2);
        ImGui.End();
        return closed;
    }
}
