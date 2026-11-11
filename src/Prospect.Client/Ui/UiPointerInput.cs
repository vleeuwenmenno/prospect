using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Internal;
using Brutal.Numerics;

namespace Prospect.Client.Ui;

/// <summary>
/// What Dear ImGui is told of the pointer, given whether it is captured for flight. <see cref="UiPlatform"/> calls
/// these from its window callbacks; the tests call the same code on a headless context. A captured pointer is
/// invisible: the interface sees no position, no press and no wheel from it, so nothing hidden can be hovered,
/// clicked or scrolled.
/// </summary>
internal static class UiPointerInput
{
    /// <summary>The position ImGui reads as "no pointer".</summary>
    public static readonly float2 Absent = new(-float.MaxValue, -float.MaxValue);

    public static void Position(ImGuiIOPtr io, float2 position, bool captured)
    {
        if (!captured)
            io.AddMousePosEvent(position.X, position.Y);
    }

    /// <summary>Presses reach the interface only from a shown pointer. Releases always do, so nothing stays held.</summary>
    public static void Button(ImGuiIOPtr io, int button, bool press, bool captured)
    {
        if ((uint)button < 5 && !(press && captured))
            io.AddMouseButtonEvent(button, press);
    }

    public static void Wheel(ImGuiIOPtr io, float2 offset, bool captured)
    {
        if (!captured)
            io.AddMouseWheelEvent(offset.X, offset.Y);
    }

    /// <summary>
    /// The pointer changes owner. Captured, the interface loses the pointer and every press or drag it had begun
    /// ends without a click, including events still waiting in ImGui's queue. Shown again, the interface learns
    /// where the pointer is before the next motion event.
    /// </summary>
    public static void Captured(ImGuiIOPtr io, bool captured, float2 cursor)
    {
        if (captured)
        {
            // Appending releases is too late: ImGui can deliver an already queued click on a later frame.
            // Compact only mouse events out, preserving keyboard, text, gamepad and focus events in order.
            var context = ImGui.GetCurrentContext();
            ref var events = ref context.InputEventsQueue;
            var kept = 0;
            for (var i = 0; i < events.Count; i++)
            {
                var input = events[i];
                if (input.Source != ImGuiInputSource.Mouse)
                    events[kept++] = input;
            }
            events.Count = kept;
            if (context.ActiveIdSource == ImGuiInputSource.Mouse)
                ImGui.Internal.ClearActiveID();
            io.ClearInputMouse();
        }
        else
        {
            io.AddMousePosEvent(cursor.X, cursor.Y);
        }
    }
}
