using Brutal.GlfwApi;

namespace Prospect.Client.Input;

/// <summary>How a capture ended.</summary>
internal enum CaptureResult : byte
{
    /// <summary>Still waiting for a press.</summary>
    Waiting,

    /// <summary>Escape or a left click: nothing is bound.</summary>
    Cancelled,

    /// <summary>A control was pressed; <see cref="ControlCapture.Binding"/> is it.</summary>
    Captured,
}

/// <summary>
/// Waits for the player to press the control they want to bind. While it waits the <see cref="ActionMap"/> is
/// suspended, so the press reaches no shortcut, no flight control and not Escape's usual "go back"; the map is only
/// let go at the end of the frame the capture finished in (<see cref="EndFrame"/>), so the finishing press is gone
/// before anything can read it. Escape cancels, and so does a left click, which the interface needs for itself and
/// so can never be bound.
/// </summary>
internal sealed class ControlCapture(ActionMap map)
{
    /// <summary>True from <see cref="Begin"/> until a press or a cancel ends it.</summary>
    public bool Active { get; private set; }

    /// <summary>The slot being bound.</summary>
    public ControlSlot Slot { get; private set; }

    /// <summary>Which of the slot's controls is replaced, or -1 to add one.</summary>
    public int Index { get; private set; }

    /// <summary>The control the last capture took; meaningful after <see cref="CaptureResult.Captured"/>.</summary>
    public InputBinding Binding { get; private set; }

    /// <summary>True in the frame a capture ended: whatever was pressed to end it must not start another.</summary>
    public bool EndedThisFrame { get; private set; }

    /// <summary>True when a left click ended this capture, so the same click does not open it again.</summary>
    public bool EndedByClick { get; private set; }

    /// <summary>Starts waiting for a press to bind to <paramref name="slot"/>.</summary>
    public void Begin(ControlSlot slot, int index)
    {
        Slot = slot;
        Index = index;
        Active = true;
        EndedThisFrame = false;
        EndedByClick = false;
        map.Suspended = true;
    }

    /// <summary>Reads this frame's presses. Call once a frame, before anything that reads actions.</summary>
    public CaptureResult Update(InputDevices devices)
    {
        if (!Active)
            return CaptureResult.Waiting;
        if (devices.KeyPressed(GlfwKey.Escape))
            return End(CaptureResult.Cancelled, false);
        if (devices.TryFirstPress(out var binding))
        {
            if (binding is { Device: InputBinding.Source.MouseButton, Code: (int)GlfwMouseButton.Left })
                return End(CaptureResult.Cancelled, true);
            Binding = binding;
            return End(CaptureResult.Captured, false);
        }

        return CaptureResult.Waiting;
    }

    private CaptureResult End(CaptureResult result, bool byClick)
    {
        Active = false;
        EndedThisFrame = true;
        EndedByClick = byClick;
        return result;
    }

    /// <summary>Ends the frame: lets the map answer again once no capture is waiting.</summary>
    public void EndFrame()
    {
        EndedThisFrame = false;
        if (!Active)
            map.Suspended = false;
    }

    /// <summary>Stops waiting and lets the map answer at once, for leaving the page.</summary>
    public void Cancel()
    {
        Active = false;
        EndedThisFrame = false;
        EndedByClick = false;
        map.Suspended = false;
    }
}
