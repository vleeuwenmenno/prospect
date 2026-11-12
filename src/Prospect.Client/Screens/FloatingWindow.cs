using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>How much of the player's input a floating window may take this frame.</summary>
internal enum FloatingInput : byte
{
    /// <summary>Shown only: the pointer is captured for flight, so the window takes nothing.</summary>
    Passive,

    /// <summary>The pointer may press and drag it, but keyboard navigation never rests in it, so W and Space still fly.</summary>
    Pointer,

    /// <summary>A surface the player opened: pointer and keyboard navigation both.</summary>
    Full,
}

/// <summary>What one frame of a floating window's title bar did, and where the window is.</summary>
internal readonly record struct FloatingFrame(float2 Min, float2 Size, float Title, bool Close, bool PinToggled, bool CloseHot, bool PinHot, bool Dragging, bool Focused);

/// <summary>
/// One movable window of the social interface: open, pinned, and where it is, for the life of the game process.
/// It is a plain Dear ImGui window with a title bar the helper lays out itself: the bar's drag area, pin and close
/// are Dear ImGui items, so a drag is the bar's only while Dear ImGui holds it active, as any other press. The look
/// is <c>ScreenHost</c>'s; this part has no fonts, so the frame order tests run it as the game does.
/// <para>
/// A drag Dear ImGui held last frame is applied before the window begins, so the rectangle a frame lays out, draws
/// and hit-tests is the one the window is at, never last frame's. The window is kept inside the screen and under
/// the navigation bar every frame, so a smaller window or a larger interface scale never leaves its title bar off
/// screen.
/// </para>
/// </summary>
internal sealed class FloatingWindow
{
    /// <summary>Title bar height, and the pin and close squares in it, in unscaled pixels.</summary>
    public const float TitleHeight = 34f;
    public const float ButtonSize = 26f;

    /// <summary>The gap kept between a window and the screen's edges, in unscaled pixels.</summary>
    public const float Margin = 8f;

    // A window the player brings forward by clicking it, so no NoBringToFrontOnFocus; and a window opening never
    // takes keyboard focus from a text field.
    private const ImGuiWindowFlags Flags = (ScreenHost.RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus)
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing;

    /// <summary>True while the window is part of the workspace; it is drawn while the workspace is open.</summary>
    public bool Open;

    /// <summary>True when the window stays on screen after the workspace closes.</summary>
    public bool Pinned;

    /// <summary>The top left corner, in pixels. Set on first show from the window's default place.</summary>
    public float2 Min;

    /// <summary>True once the window has been placed.</summary>
    public bool Placed;

    private uint _titleItem;
    private int _preparedFrame = -1;

    /// <summary>Takes the window away, pinned or not: the player's explicit close.</summary>
    public void Close()
    {
        Open = false;
        Pinned = false;
    }

    /// <summary>The children's flags for the input a window may take: no keyboard navigation unless it is a surface.</summary>
    public static ImGuiWindowFlags ChildFlags(FloatingInput input) =>
        ImGuiWindowFlags.NoBackground | (input == FloatingInput.Full ? default : ImGuiWindowFlags.NoNav);

    /// <summary>A window's size, cut to the room under the bar and inside the margins.</summary>
    public static float2 Fit(float2 size, float2 display, float scale)
    {
        var margin = MathF.Round(Margin * scale);
        var top = HudLayout.TopBarBottom(scale) + margin;
        return new float2(
            Math.Max(1f, Math.Min(size.X, display.X - margin * 2)),
            Math.Max(1f, Math.Min(size.Y, display.Y - top - margin)));
    }

    /// <summary>A corner that keeps a window of <paramref name="size"/> wholly on screen and under the bar.</summary>
    public static float2 Clamp(float2 min, float2 size, float2 display, float scale)
    {
        var margin = MathF.Round(Margin * scale);
        var top = HudLayout.TopBarBottom(scale) + margin;
        return new float2(
            Math.Clamp(min.X, margin, Math.Max(margin, display.X - size.X - margin)),
            Math.Clamp(min.Y, top, Math.Max(top, display.Y - size.Y - margin)));
    }

    /// <summary>
    /// Places the window for this frame, once: its first place, the drag Dear ImGui held on its title bar last
    /// frame (the bar's active press, so ownership stays Dear ImGui's) and the fit to the screen under the bar.
    /// Heads-up markers reserve exactly this rectangle, and <see cref="Begin"/> draws and hit-tests it; calling it
    /// again in the same frame moves nothing, so the drag is applied once whoever asks first.
    /// </summary>
    public HudRect Prepare(float2 size, float2 fallback, float2 display, float scale, FloatingInput input)
    {
        var frame = ImGui.GetFrameCount();
        if (_preparedFrame != frame)
        {
            _preparedFrame = frame;
            if (!Placed)
            {
                Min = fallback;
                Placed = true;
            }

            if (input != FloatingInput.Passive && _titleItem != 0 && ImGui.GetCurrentContext().ActiveId == _titleItem && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                Min += ImGui.GetIO().MouseDelta;
        }

        size = Fit(size, display, scale);
        Min = Clamp(Min, size, display, scale);
        return new HudRect(Min.X, Min.Y, Min.X + size.X, Min.Y + size.Y);
    }

    /// <summary>
    /// Opens the window: applies a held drag, keeps it on screen, begins it and submits the title bar's drag area,
    /// pin (when <paramref name="pinnable"/>) and close. The cursor is left at the top of the content, under the
    /// title. Pair with <see cref="End"/>.
    /// </summary>
    public FloatingFrame Begin(ImString id, float2 size, float2 fallback, float2 display, float scale, FloatingInput input, bool pinnable)
    {
        // The rectangle this frame's markers reserved: the held drag is applied there, before anything is laid out.
        var rect = Prepare(size, fallback, display, scale, input);
        size = new float2(rect.Width, rect.Height);

        ImGui.SetNextWindowPos(Min);
        ImGui.SetNextWindowSize(size);
        var flags = Flags | input switch
        {
            FloatingInput.Passive => ImGuiWindowFlags.NoInputs,
            FloatingInput.Pointer => ImGuiWindowFlags.NoNav,
            _ => default,
        };
        ImGui.Begin(id, flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());

        var title = MathF.Round(TitleHeight * scale);
        var button = MathF.Round(ButtonSize * scale);
        var inset = (title - button) * 0.5f;
        var closeMin = new float2(Min.X + size.X - inset - button, Min.Y + inset);
        var pinMin = closeMin - new float2(button + MathF.Round(Theme.Space1 * scale), 0);
        var dragWidth = Math.Max(1f, (pinnable ? pinMin.X : closeMin.X) - Min.X - inset);

        ImGui.SetCursorScreenPos(Min);
        ImGui.InvisibleButton("##title"u8, new float2(dragWidth, title));
        _titleItem = ImGui.GetItemID();
        var dragging = ImGui.IsItemActive();

        var pin = false;
        var pinHot = false;
        if (pinnable)
        {
            ImGui.SetCursorScreenPos(pinMin);
            pin = ImGui.InvisibleButton("##pin"u8, new float2(button, button));
            pinHot = ImGui.IsItemHovered();
            if (pin)
                Pinned = !Pinned;
        }

        ImGui.SetCursorScreenPos(closeMin);
        var close = ImGui.InvisibleButton("##close"u8, new float2(button, button));
        var closeHot = ImGui.IsItemHovered();
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        ImGui.SetCursorScreenPos(new float2(Min.X, Min.Y + title));
        return new FloatingFrame(Min, size, title, close, pin, closeHot, pinHot, dragging, focused);
    }

    public static void End() => ImGui.End();

    /// <summary>
    /// Lets go of the keyboard the interface held: a text field being typed in, and keyboard navigation's window.
    /// Called when the workspace closes, so pinned windows left on screen hold no keys and the next frame's flight
    /// keys reach the hull.
    /// </summary>
    public static void ReleaseKeyboard()
    {
        ImGui.Internal.ClearActiveID();
        ImGui.Internal.FocusWindow(default);
    }
}
