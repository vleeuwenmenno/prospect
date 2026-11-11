using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Prospect.Client.Ui;

internal enum ButtonKind
{
    Primary,
    Secondary,
}

/// <summary>
/// The game's widget set. Each widget lays itself out through Dear ImGui (which supplies hit-testing, focus and
/// keyboard and gamepad navigation) and draws itself entirely with the theme, so no stock ImGui visuals appear.
/// </summary>
internal static class Kit
{
    private struct AnimationSlot
    {
        public uint Id;
        public float Value;
        public int LastFrame;
    }

    private const int AnimationSlots = 512;
    private static readonly AnimationSlot[] Animations = new AnimationSlot[AnimationSlots];
    private static UiContext _ui = null!;
    private static int _frame;

    /// <summary>Opacity applied to everything drawn through the kit; screens fade in by animating this.</summary>
    public static float Fade { get; set; } = 1f;

    public static UiContext Ui => _ui;
    public static float Scale => _ui.Scale;

    public static void BeginFrame(UiContext ui)
    {
        _ui = ui;
        _frame = ImGui.GetFrameCount();
        Fade = 1f;
        UiText.ResetArena();
    }

    /// <summary>
    /// Formats an interpolated string for a kit text call without allocating: <c>Kit.F($"TICK {tick}")</c>.
    /// The result is valid until the end of the frame.
    /// </summary>
    public static ReadOnlySpan<byte> F(UiText text) => text.Span;

    /// <summary>Converts design pixels to screen pixels.</summary>
    public static float Px(float designPixels) => MathF.Round(designPixels * _ui.Scale);

    /// <summary>A draw colour from an 0xRRGGBB theme colour, with the current fade applied.</summary>
    public static ImColor8 Color(uint rgb, float alpha = 1f) => Opaque(rgb, alpha * Fade);

    /// <summary>A draw colour from an 0xRRGGBB theme colour, ignoring the current fade.</summary>
    public static ImColor8 Opaque(uint rgb, float alpha = 1f) =>
        new(
            (byte)(rgb >> 16),
            (byte)(rgb >> 8),
            (byte)rgb,
            (byte)(Math.Clamp(alpha, 0f, 1f) * 255f + 0.5f)
        );

    /// <summary>An 0xRRGGBB theme colour packed the way multi-colour draw calls expect it, ignoring the fade.</summary>
    public static uint Packed(uint rgb, float alpha = 1f) =>
        (uint)(Math.Clamp(alpha, 0f, 1f) * 255f + 0.5f) << 24 | (rgb & 0xFF) << 16 | (rgb & 0xFF00) | rgb >> 16 & 0xFF;

    /// <summary>Blends two theme colours.</summary>
    public static uint Mix(uint from, uint to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var r = (int)((from >> 16 & 0xFF) + ((int)(to >> 16 & 0xFF) - (int)(from >> 16 & 0xFF)) * t);
        var g = (int)((from >> 8 & 0xFF) + ((int)(to >> 8 & 0xFF) - (int)(from >> 8 & 0xFF)) * t);
        var b = (int)((from & 0xFF) + ((int)(to & 0xFF) - (int)(from & 0xFF)) * t);
        return (uint)(r << 16 | g << 8 | b);
    }

    public static float EaseOut(float t)
    {
        t = 1f - Math.Clamp(t, 0f, 1f);
        return 1f - t * t * t;
    }

    /// <summary>
    /// A value that eases toward <paramref name="target"/> over successive frames, remembered per widget id. This
    /// is what gives hover and focus their motion.
    /// </summary>
    public static float Animate(uint id, float target, float rate = Theme.HoverRate)
    {
        var start = (int)(id % AnimationSlots);
        var free = -1;
        for (var probe = 0; probe < 8; probe++)
        {
            var index = (start + probe) % AnimationSlots;
            ref var slot = ref Animations[index];
            if (slot.Id == id && _frame - slot.LastFrame <= 1)
            {
                slot.Value += (target - slot.Value) * (1f - MathF.Exp(-rate * _ui.DeltaTime));
                slot.LastFrame = _frame;
                return slot.Value;
            }

            if (free < 0 && _frame - slot.LastFrame > 1)
                free = index;
        }

        // First sighting (or the widget was not drawn last frame): start from rest.
        if (free >= 0)
            Animations[free] = new AnimationSlot { Id = id, LastFrame = _frame };
        return 0f;
    }

    public static float2 Measure(ImFontPtr font, float size, ImString text)
    {
        ImGui.PushFont(font, size);
        var result = ImGui.CalcTextSize(text);
        ImGui.PopFont();
        return result;
    }

    /// <summary>Draws text at a screen position and returns its size.</summary>
    public static float2 Text(ImDrawListPtr draw, ImFontPtr font, float size, float2 position, uint rgb, ImString text, float alpha = 1f)
    {
        ImGui.PushFont(font, size);
        var measured = ImGui.CalcTextSize(text);
        draw.AddText(new float2(MathF.Round(position.X), MathF.Round(position.Y)), Color(rgb, alpha), text);
        ImGui.PopFont();
        return measured;
    }

    /// <summary>Lays out a line of text in the current window and advances the cursor past it.</summary>
    public static void Label(ImFontPtr font, float size, uint rgb, ImString text)
    {
        var position = ImGui.GetCursorScreenPos();
        var measured = Text(ImGui.GetWindowDrawList(), font, size, position, rgb, text);
        ImGui.Dummy(measured);
    }

    /// <summary>A small upper-case caption, the label style used above fields and panel sections.</summary>
    public static void Caption(ImString text, uint rgb = Theme.TextMute) => Label(_ui.SansMedium, Theme.FontCaption, rgb, text);

    public static void Body(ImString text, uint rgb = Theme.TextDim) => Label(_ui.Sans, Theme.FontBody, rgb, text);

    public static void Heading(ImString text) => Label(_ui.SansBold, Theme.FontHeading, Theme.Text, text);

    public static void Space(float designPixels) => ImGui.Dummy(new float2(0, Px(designPixels)));

    /// <summary>Fills a rectangle whose top-right and bottom-left corners are cut, the kit's signature shape.</summary>
    public static void ChamferFill(ImDrawListPtr draw, float2 min, float2 max, ImColor8 color, float chamfer)
    {
        Span<float2> points = stackalloc float2[6];
        ChamferPoints(points, min, max, chamfer);
        draw.AddConvexPolyFilled(points, color);
    }

    public static void ChamferOutline(ImDrawListPtr draw, float2 min, float2 max, ImColor8 color, float chamfer, float thickness = 1f)
    {
        Span<float2> points = stackalloc float2[6];
        ChamferPoints(points, min + new float2(0.5f, 0.5f), max - new float2(0.5f, 0.5f), chamfer);
        draw.AddPolyline(points, color, ImDrawFlags.Closed, thickness);
    }

    /// <summary>Draws a panel body: fill, hairline border and an accent tick at the top-left corner.</summary>
    public static void PanelFrame(ImDrawListPtr draw, float2 min, float2 max)
    {
        var chamfer = Px(Theme.Chamfer);
        ChamferFill(draw, min, max, Color(Theme.Panel, 0.94f), chamfer);
        ChamferOutline(draw, min, max, Color(Theme.Line), chamfer);
        draw.AddRectFilled(min, min + new float2(Px(28), Px(2)), Color(Theme.Accent));
    }

    /// <summary>A main-menu entry: large type, with an accent bar that slides in when hovered or focused.</summary>
    public static bool MenuItem(ImString label, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(Px(width), Px(46));
        var pressed = ImGui.InvisibleButton(label, size);
        var t = Animate(ImGui.GetItemID(), IsHot() ? 1f : 0f);

        if (t > 0.01f)
        {
            draw.AddRectFilled(position, position + new float2(size.X * (0.35f + 0.65f * t), size.Y), Color(Theme.PanelRaised, 0.9f * t));
            draw.AddRectFilled(position, position + new float2(Px(3), size.Y), Color(Theme.Accent, t));
        }

        var textHeight = Measure(_ui.SansMedium, Theme.FontMenu, label).Y;
        Text(
            draw,
            _ui.SansMedium,
            Theme.FontMenu,
            position + new float2(Px(Theme.Space4 + 10f * t), (size.Y - textHeight) * 0.5f),
            Mix(Theme.TextDim, Theme.Text, t),
            label
        );
        return pressed;
    }

    /// <summary>
    /// A chamfered push button. A width of zero sizes it to its label. A disabled button is drawn muted and
    /// cannot be pressed or focused.
    /// </summary>
    public static bool Button(ImString label, ButtonKind kind = ButtonKind.Secondary, float width = 0f, bool enabled = true)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var textSize = Measure(_ui.SansMedium, Theme.FontBody, label);
        var size = new float2(width > 0 ? Px(width) : textSize.X + Px(Theme.Space5 * 2), Px(Theme.ControlHeight));
        if (!enabled)
            ImGui.BeginDisabled();
        var pressed = ImGui.InvisibleButton(label, size);
        var t = Animate(ImGui.GetItemID(), enabled && IsHot() ? 1f : 0f);
        var held = ImGui.IsItemActive();
        if (!enabled)
            ImGui.EndDisabled();

        var max = position + size;
        var chamfer = Px(Theme.Chamfer);
        uint textColor;
        if (!enabled)
        {
            ChamferFill(draw, position, max, Color(Theme.Panel), chamfer);
            ChamferOutline(draw, position, max, Color(Theme.Line), chamfer);
            textColor = Theme.TextMute;
        }
        else if (kind == ButtonKind.Primary)
        {
            ChamferFill(draw, position, max, Color(held ? Theme.Accent : Mix(Theme.Accent, Theme.AccentBright, t)), chamfer);
            textColor = Theme.AccentInk;
        }
        else
        {
            ChamferFill(draw, position, max, Color(Mix(Theme.Panel, Theme.PanelRaised, t)), chamfer);
            ChamferOutline(draw, position, max, Color(Mix(Theme.Line, Theme.Accent, t)), chamfer);
            textColor = Mix(Theme.TextDim, Theme.Text, t);
        }

        Text(draw, _ui.SansMedium, Theme.FontBody, position + (size - textSize) * 0.5f, textColor, label);
        return pressed;
    }

    /// <summary>A single-line text field with a caption above it. Returns true when Enter is pressed in it.</summary>
    public static bool TextField(ImString caption, ImString id, ImInputString value, float width, ImGuiInputTextFlags flags = default)
    {
        Caption(caption);
        Space(Theme.Space1);
        ImGui.SetNextItemWidth(Px(width));
        ImGui.PushFont(_ui.Mono, Theme.FontBody);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Fade);
        var submitted = ImGui.InputText(id, value.Buffer, flags | ImGuiInputTextFlags.EnterReturnsTrue);
        value.EvaluateLength();
        ImGui.PopStyleVar();
        ImGui.PopFont();

        // The field draws its own border; overdraw it in the accent colour while focused.
        if (ImGui.IsItemActive())
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), Color(Theme.Accent));
        return submitted;
    }

    /// <summary>
    /// A text field for a password: masked unless the player shows it with the SHOW link beside it. Returns true
    /// when Enter is pressed in it. <paramref name="shown"/> is the screen's own, so each field remembers its state.
    /// </summary>
    public static bool PasswordField(ImString caption, ImString id, ImInputString value, ref bool shown, float width)
    {
        var flags = ImGuiInputTextFlags.NoUndoRedo | (shown ? default : ImGuiInputTextFlags.Password);
        var submitted = TextField(caption, id, value, width, flags);
        var fieldMax = ImGui.GetItemRectMax();
        var fieldMin = ImGui.GetItemRectMin();
        ImGui.SameLine(0, Px(Theme.Space2));
        var linkHeight = Measure(_ui.SansMedium, Theme.FontCaption, "SHOW"u8).Y + Px(Theme.Space1 * 2);
        ImGui.SetCursorScreenPos(new float2(ImGui.GetCursorScreenPos().X, fieldMin.Y + (fieldMax.Y - fieldMin.Y - linkHeight) * 0.5f));
        ImGui.PushID(id);
        if (LinkButton(shown ? "HIDE"u8 : "SHOW"u8))
            shown = !shown;
        ImGui.PopID();
        ImGui.SetCursorScreenPos(new float2(fieldMin.X, fieldMax.Y));
        ImGui.Dummy(float2.Zero);
        return submitted;
    }

    /// <summary>
    /// Draws untrusted text, such as a server's name from the list, cut to a width with "..." when it does not
    /// fit. The text only ever goes to a draw call: never into a widget label, an id or a format string, so
    /// <c>##</c> and <c>%</c> in it are shown as they are. A character the font lacks is drawn as the font's
    /// fallback glyph, with that glyph's width, so the layout holds. Returns the width drawn.
    /// </summary>
    public static float ClippedText(ImDrawListPtr draw, ImFontPtr font, float size, float2 position, uint rgb, string text, float maxWidth)
    {
        // A name is at most 96 UTF-16 units; anything longer is cut before it is measured.
        var chars = text.AsSpan(0, Math.Min(text.Length, 128));
        Span<byte> utf8 = stackalloc byte[128 * 3];
        var length = System.Text.Encoding.UTF8.GetBytes(chars, utf8);
        return ClippedText(draw, font, size, position, rgb, (ReadOnlySpan<byte>)utf8[..length], maxWidth);
    }

    /// <summary>As <see cref="ClippedText(ImDrawListPtr, ImFontPtr, float, float2, uint, string, float)"/>, for UTF-8 text such as <see cref="F"/> makes.</summary>
    public static float ClippedText(ImDrawListPtr draw, ImFontPtr font, float size, float2 position, uint rgb, ReadOnlySpan<byte> bytes, float maxWidth)
    {
        if (bytes.Length > 512)
            bytes = bytes[..512];
        position = new float2(MathF.Round(position.X), MathF.Round(position.Y));

        ImGui.PushFont(font, size);
        var full = ImGui.CalcTextSize(bytes).X;
        float drawn;
        if (full <= maxWidth)
        {
            draw.AddText(position, Color(rgb), bytes);
            drawn = full;
        }
        else
        {
            // The longest prefix, cut between characters, that leaves room for the dots: a binary search over
            // the offsets where characters start.
            Span<int> starts = stackalloc int[bytes.Length + 1];
            var count = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if ((bytes[i] & 0xC0) != 0x80)
                    starts[count++] = i;
            }

            starts[count] = bytes.Length;
            var room = maxWidth - ImGui.CalcTextSize("..."u8).X;
            int low = 0, high = count;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (ImGui.CalcTextSize(bytes[..starts[middle]]).X <= room)
                    low = middle;
                else
                    high = middle - 1;
            }

            low = starts[low];
            var prefix = bytes[..low];
            var prefixWidth = ImGui.CalcTextSize(prefix).X;
            draw.PushClipRect(position - new float2(0, size), position + new float2(maxWidth, size * 3), true);
            draw.AddText(position, Color(rgb), prefix);
            draw.AddText(position + new float2(prefixWidth, 0), Color(rgb), "..."u8);
            draw.PopClipRect();
            drawn = maxWidth;
        }

        ImGui.PopFont();
        return drawn;
    }

    /// <summary>A small padlock drawn from shapes, so it never depends on a glyph in the font.</summary>
    public static void Lock(ImDrawListPtr draw, float2 center, uint rgb)
    {
        var body = new float2(Px(10), Px(8));
        var bodyMin = center + new float2(-body.X * 0.5f, -Px(1));
        draw.AddRectFilled(bodyMin, bodyMin + body, Color(rgb));
        var radius = Px(3.5f);
        draw.PathArcTo(new float2(center.X, bodyMin.Y), radius, MathF.PI, MathF.PI * 2f, 12);
        draw.PathStroke(Color(rgb), ImDrawFlags.None, Px(1.6f));
    }

    /// <summary>An on/off switch with its label on the left. Returns true when toggled.</summary>
    public static bool Toggle(ImString label, ref bool value, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(Px(width), Px(Theme.ControlHeight));
        var pressed = ImGui.InvisibleButton(label, size);
        if (pressed)
            value = !value;

        var id = (uint)ImGui.GetItemID();
        var hot = Animate(id, IsHot() ? 1f : 0f);
        var on = Animate(id ^ 0x5F3759DF, value ? 1f : 0f);
        RowBackground(draw, position, size, hot);

        var textHeight = Measure(_ui.Sans, Theme.FontBody, label).Y;
        Text(draw, _ui.Sans, Theme.FontBody, position + new float2(Px(Theme.Space3), (size.Y - textHeight) * 0.5f), Mix(Theme.TextDim, Theme.Text, hot), label);

        var track = new float2(Px(40), Px(18));
        var trackMin = position + new float2(size.X - track.X - Px(Theme.Space3), (size.Y - track.Y) * 0.5f);
        draw.AddRectFilled(trackMin, trackMin + track, Color(Mix(Theme.Void, Theme.Accent, on * 0.35f)));
        draw.AddRect(trackMin, trackMin + track, Color(Mix(Theme.LineStrong, Theme.Accent, on)));
        var knob = new float2(Px(14), Px(12));
        var knobMin = trackMin + new float2(Px(3) + (track.X - knob.X - Px(6)) * on, (track.Y - knob.Y) * 0.5f);
        draw.AddRectFilled(knobMin, knobMin + knob, Color(Mix(Theme.TextMute, Theme.Accent, on)));
        return pressed;
    }

    /// <summary>
    /// A value picked from a short list with previous/next arrows, usable with mouse, keyboard and gamepad.
    /// Returns true when the selection changed.
    /// </summary>
    public static bool Choice(ImString label, ReadOnlySpan<string> options, ref int index, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(Px(width), Px(Theme.ControlHeight));
        var arrow = Px(34);
        var valueWidth = Px(150);
        var before = index;

        // The row itself is the navigation target: left/right change the value while it is focused.
        ImGui.InvisibleButton(label, new float2(size.X - arrow * 2 - valueWidth, size.Y));
        var id = (uint)ImGui.GetItemID();
        var focused = ImGui.IsItemFocused() && ImGui.GetIO().NavVisible;
        if (focused)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) || ImGui.IsKeyPressed(ImGuiKey.GamepadDpadLeft))
                index--;
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) || ImGui.IsKeyPressed(ImGuiKey.GamepadDpadRight))
                index++;
        }

        var rowHot = ImGui.IsItemHovered() || focused;

        ImGui.PushID(label);
        ImGui.SameLine();
        if (ImGui.InvisibleButton("<"u8, new float2(arrow, size.Y)))
            index--;
        var leftHot = ImGui.IsItemHovered();
        ImGui.SameLine(0, valueWidth);
        if (ImGui.InvisibleButton(">"u8, new float2(arrow, size.Y)))
            index++;
        var rightHot = ImGui.IsItemHovered();
        ImGui.PopID();

        index = Math.Clamp(index, 0, options.Length - 1);

        var hot = Animate(id, rowHot || leftHot || rightHot ? 1f : 0f);
        RowBackground(draw, position, size, hot);
        var textHeight = Measure(_ui.Sans, Theme.FontBody, label).Y;
        var baseline = (size.Y - textHeight) * 0.5f;
        Text(draw, _ui.Sans, Theme.FontBody, position + new float2(Px(Theme.Space3), baseline), Mix(Theme.TextDim, Theme.Text, hot), label);

        var valueMin = position + new float2(size.X - arrow - valueWidth, 0);
        var valueSize = Measure(_ui.Mono, Theme.FontSmall, options[index]);
        Text(draw, _ui.Mono, Theme.FontSmall, valueMin + new float2((valueWidth - valueSize.X) * 0.5f, (size.Y - valueSize.Y) * 0.5f), Theme.Text, options[index]);
        Arrow(draw, valueMin + new float2(-arrow * 0.5f, size.Y * 0.5f), -1f, index > 0 ? (leftHot ? Theme.Accent : Theme.TextDim) : Theme.Line);
        Arrow(draw, valueMin + new float2(valueWidth + arrow * 0.5f, size.Y * 0.5f), 1f, index < options.Length - 1 ? (rightHot ? Theme.Accent : Theme.TextDim) : Theme.Line);
        return index != before;
    }

    /// <summary>
    /// A number picked on a track between a minimum and a maximum, in steps, with its value shown as text. Drag on
    /// the track with the mouse, or focus the row and press left and right. Returns true when the value changed.
    /// </summary>
    public static bool Slider(ImString label, ref float value, float minimum, float maximum, float step, ImString valueText, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(Px(width), Px(Theme.ControlHeight));
        var trackArea = Px(210);
        var valueWidth = Px(72);
        var before = value;
        if (step <= 0)
            step = (maximum - minimum) / 20f;

        // As with a choice, the row is the navigation target and left/right move the value while it is focused.
        ImGui.InvisibleButton(label, new float2(size.X - trackArea - valueWidth, size.Y));
        var id = (uint)ImGui.GetItemID();
        var focused = ImGui.IsItemFocused() && ImGui.GetIO().NavVisible;
        if (focused)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) || ImGui.IsKeyPressed(ImGuiKey.GamepadDpadLeft))
                value -= step;
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) || ImGui.IsKeyPressed(ImGuiKey.GamepadDpadRight))
                value += step;
        }

        var rowHot = ImGui.IsItemHovered() || focused;

        // The track takes the mouse only, so keyboard navigation does not stop on it as a second item.
        ImGui.PushID(label);
        ImGui.SameLine(0, valueWidth);
        ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
        ImGui.InvisibleButton("track"u8, new float2(trackArea, size.Y));
        ImGui.PopItemFlag();
        var trackHot = ImGui.IsItemHovered();
        var dragging = ImGui.IsItemActive();
        ImGui.PopID();

        var trackMin = position + new float2(size.X - trackArea + Px(Theme.Space3), size.Y * 0.5f);
        var trackLength = trackArea - Px(Theme.Space3 * 2);
        if (dragging && trackLength > 0)
        {
            var t = Math.Clamp((ImGui.GetMousePos().X - trackMin.X) / trackLength, 0f, 1f);
            value = minimum + MathF.Round(t * (maximum - minimum) / step) * step;
        }

        value = Math.Clamp(value, minimum, maximum);

        var hot = Animate(id, rowHot || trackHot || dragging ? 1f : 0f);
        RowBackground(draw, position, size, hot);
        var textHeight = Measure(_ui.Sans, Theme.FontBody, label).Y;
        Text(draw, _ui.Sans, Theme.FontBody, position + new float2(Px(Theme.Space3), (size.Y - textHeight) * 0.5f), Mix(Theme.TextDim, Theme.Text, hot), label);

        var valueSize = Measure(_ui.Mono, Theme.FontSmall, valueText);
        var valueMin = position + new float2(size.X - trackArea - valueWidth, 0);
        Text(draw, _ui.Mono, Theme.FontSmall, valueMin + new float2(valueWidth - valueSize.X, (size.Y - valueSize.Y) * 0.5f), Theme.Text, valueText);

        var fill = maximum > minimum ? (value - minimum) / (maximum - minimum) : 0f;
        var thickness = Px(2);
        draw.AddRectFilled(trackMin - new float2(0, thickness * 0.5f), trackMin + new float2(trackLength, thickness * 0.5f), Color(Theme.LineStrong));
        draw.AddRectFilled(trackMin - new float2(0, thickness * 0.5f), trackMin + new float2(trackLength * fill, thickness * 0.5f), Color(Mix(Theme.TextDim, Theme.Accent, hot)));
        var knob = new float2(Px(8), Px(16));
        var knobMin = trackMin + new float2(trackLength * fill - knob.X * 0.5f, -knob.Y * 0.5f);
        draw.AddRectFilled(knobMin, knobMin + knob, Color(Mix(Theme.Text, Theme.Accent, hot)));
        return value != before;
    }

    /// <summary>A small text-only button, for secondary actions beside other content. Returns true when pressed.</summary>
    public static bool LinkButton(ImString label)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var textSize = Measure(_ui.SansMedium, Theme.FontCaption, label);
        var pad = new float2(Px(Theme.Space2), Px(Theme.Space1));
        var pressed = ImGui.InvisibleButton(label, textSize + pad * 2);
        var t = Animate(ImGui.GetItemID(), IsHot() ? 1f : 0f);
        if (t > 0.01f)
            draw.AddRect(position, position + textSize + pad * 2, Color(Theme.Accent, t));
        Text(draw, _ui.SansMedium, Theme.FontCaption, position + pad, Mix(Theme.TextDim, Theme.Accent, t), label);
        return pressed;
    }

    /// <summary>Lays out text wrapped to a width in the current window and advances the cursor past it.</summary>
    public static void Paragraph(ImFontPtr font, float size, uint rgb, ImString text, float width)
    {
        var position = ImGui.GetCursorScreenPos();
        var wrap = Px(width);
        ImGui.PushFont(font, size);
        var measured = ImGui.CalcTextSize(text, false, wrap);
        ImGui.GetWindowDrawList().AddText(font, ImGui.GetFontSize(), new float2(MathF.Round(position.X), MathF.Round(position.Y)), Color(rgb), text, wrap);
        ImGui.PopFont();
        ImGui.Dummy(new float2(wrap, measured.Y));
    }

    /// <summary>A key cap followed by what the key does, for the hint bar at the bottom of a screen.</summary>
    public static float KeyHint(ImDrawListPtr draw, float2 position, ImString key, ImString text)
    {
        var keySize = Measure(_ui.Mono, Theme.FontCaption, key);
        var pad = Px(6);
        var capMax = position + new float2(keySize.X + pad * 2, keySize.Y + pad);
        draw.AddRect(position, capMax, Color(Theme.LineStrong));
        Text(draw, _ui.Mono, Theme.FontCaption, position + new float2(pad, pad * 0.5f), Theme.TextDim, key);
        var textSize = Text(draw, _ui.Sans, Theme.FontCaption, new float2(capMax.X + Px(Theme.Space2), position.Y + pad * 0.5f), Theme.TextMute, text);
        return capMax.X + Px(Theme.Space2) + textSize.X - position.X + Px(Theme.Space5);
    }

    /// <summary>A rotating arc that shows work in progress.</summary>
    public static void Spinner(ImDrawListPtr draw, float2 center, float radius)
    {
        var angle = _ui.Time * 4.2f;
        draw.AddCircle(center, radius, Color(Theme.Line), 48, Px(2));
        draw.PathArcTo(center, radius, angle, angle + 1.7f, 24);
        draw.PathStroke(Color(Theme.Accent), ImDrawFlags.None, Px(2));
    }

    /// <summary>True when the last item is under the mouse or holds keyboard or gamepad focus.</summary>
    public static bool IsHot() => ImGui.IsItemHovered() || (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible);

    private static void RowBackground(ImDrawListPtr draw, float2 position, float2 size, float hot)
    {
        draw.AddRectFilled(position, position + size, Color(Theme.PanelRaised, 0.55f * hot));
        draw.AddRectFilled(position, position + new float2(Px(2), size.Y), Color(Theme.Accent, hot));
        draw.AddLine(position + new float2(0, size.Y - 0.5f), position + new float2(size.X, size.Y - 0.5f), Color(Theme.Line, 0.6f));
    }

    private static void Arrow(ImDrawListPtr draw, float2 center, float direction, uint rgb)
    {
        var s = Px(5);
        draw.AddTriangleFilled(
            center + new float2(direction * s, 0),
            center + new float2(-direction * s * 0.6f, -s),
            center + new float2(-direction * s * 0.6f, s),
            Color(rgb)
        );
    }

    private static void ChamferPoints(Span<float2> points, float2 min, float2 max, float chamfer)
    {
        points[0] = min;
        points[1] = new float2(max.X - chamfer, min.Y);
        points[2] = new float2(max.X, min.Y + chamfer);
        points[3] = max;
        points[4] = new float2(min.X + chamfer, max.Y);
        points[5] = new float2(min.X, max.Y - chamfer);
    }
}
