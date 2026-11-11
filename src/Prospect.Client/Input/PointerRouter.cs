using Brutal.GlfwApi;

namespace Prospect.Client.Input;

/// <summary>The interface as the pointer router sees it: <c>UiContext</c> in the game, a headless context in tests.</summary>
internal interface IInterfacePointer
{
    /// <summary>Hands the pointer to flight (hidden, captured) or back to the interface.</summary>
    void SetPointerCaptured(bool captured);

    /// <summary>Starts the interface frame: takes this frame's queued input and decides what is hovered.</summary>
    void NewFrame();

    /// <summary>True while the pointer is over the interface, or a press the interface began is held.</summary>
    bool PointerOverInterface { get; }
}

/// <summary>What the frame loop knows, before the world reads input, about who should own the pointer.</summary>
internal struct PointerFrame
{
    /// <summary>The player's hull is in space and the world is drawn.</summary>
    public bool InSpace;

    /// <summary>The game window has keyboard focus.</summary>
    public bool Focused;

    /// <summary>
    /// A surface that is used with the pointer is open over the world: a menu, the map, a data sheet, Controls,
    /// the player panel or chat. The pointer is shown until it closes.
    /// </summary>
    public bool SurfaceOpen;

    /// <summary>
    /// The command view. Its pointer is always shown and the world uses it for selecting, boxing, ordering and the
    /// camera gestures, except over the interface.
    /// </summary>
    public bool CommandView;
}

/// <summary>
/// Decides, once per frame, whether the pointer is hidden and captured or shown, and what the world may read of the
/// mouse. The pointer is hidden and captured only in the pilot view, with focus, nothing open and the
/// hold-to-release key (Shift by default) up. The command view and every open surface show it; never does a frame in
/// which world input is merely blocked (a shortcut that opened something, interface keyboard ownership lingering for
/// one frame after a window closed). Those frames used to release the pointer and capture it again, which shows the
/// system cursor and moves it.
/// <list type="bullet">
/// <item>Pilot view: the hidden pointer steers. With the release key the pointer is shown and the world gets no
/// mouse at all, so it can only point at the interface.</item>
/// <item>Command view: the pointer is shown and selects, boxes, orders and turns the camera, except over the
/// interface. The release key changes nothing there, so pressing or letting go of it never ends a drag.</item>
/// <item>Anything open: the pointer is shown and the world gets no mouse.</item>
/// </list>
/// While captured the interface gets no presses and no position (see <c>UiPointerInput</c>), so nothing hidden can
/// be hovered or clicked. A button held through any change of owner stays masked for the world until it is let go.
/// <para>
/// Order matters. Window events are polled before anything here runs, so a single poll can hold a move onto a
/// button and a whole click, or the release key's press and the click it was meant for. <see cref="Update"/> therefore
/// decides capture first, quarantines the poll's mouse if the owner changed, and only then starts the interface
/// frame, so the interface's idea of what is under the pointer is this frame's, not the last one's, before the
/// world reads a single button. The view can also change inside the world's update (the command key, taking
/// control); the frame loop calls <see cref="RefreshCapture"/> again after it, so the pointer follows in the same
/// frame.
/// </para>
/// </summary>
internal sealed class PointerRouter(InputDevices devices, ActionMap actions, IInterfacePointer ui)
{
    /// <summary>True while the pointer is hidden and steers the view.</summary>
    public bool Captured { get; private set; }

    /// <summary>True while the hold-to-release key (Shift by default) is down this frame.</summary>
    public bool ReleaseHeld { get; private set; }

    private bool _commandView;
    private bool _cameraDrag;

    /// <summary>
    /// True when this frame's pointer belongs to the interface: over one of its windows, or holding a press it
    /// began. Read after <see cref="Update"/>, which starts the interface frame.
    /// </summary>
    public bool PointerOverInterface { get; private set; }

    /// <summary>
    /// Decides capture for this frame, then starts the interface frame and reads who the pointer belongs to. Call
    /// once per frame after events are polled, before shortcuts or the world read input.
    /// </summary>
    public void Update(in PointerFrame frame)
    {
        RefreshCapture(frame);
        ui.NewFrame();
        PointerOverInterface = !Captured && ui.PointerOverInterface;
    }

    /// <summary>Rechecks capture after shortcuts change the open surfaces, without starting another UI frame.</summary>
    public void RefreshCapture(in PointerFrame frame)
    {
        ReleaseHeld = actions.Held(devices, InputAction.ReleasePointer, true);
        _commandView = frame.CommandView;
        Apply(frame.InSpace && frame.Focused && !devices.Muted && !frame.SurfaceOpen && !frame.CommandView && !ReleaseHeld);
        PointerOverInterface = !Captured && ui.PointerOverInterface;
    }

    /// <summary>
    /// Releases the pointer at once when a surface was opened while the interface was drawn, so the frame that
    /// shows it also shows the pointer.
    /// </summary>
    public void SurfaceOpened()
    {
        if (Captured)
            Apply(false);
    }

    /// <summary>
    /// Starts the world's read of the mouse. Mouse buttons, motion and wheel are masked when world input is off,
    /// when the pointer is over the interface, and in the pilot view while the release key is held. A button held through any of those stays masked for the world until it is
    /// let go, so a click on the interface never becomes a world action and a drag ends when its owner changes.
    /// <para>
    /// One exception in the pilot view: with the release key held, a right press that begins over the world (not
    /// in the same poll as the key, which the quarantine drops) turns the camera as free look does, until the
    /// button or the key is let go. Only the camera's motion and wheel see it; every button stays masked.
    /// </para>
    /// </summary>
    public void BeginWorldMouse(bool worldInput)
    {
        devices.ReadingWorldMouse = true;
        var released = !_commandView && ReleaseHeld && worldInput && !Captured;
        _cameraDrag = released && devices.RawMouseDown(GlfwMouseButton.Right)
            && (_cameraDrag || (!PointerOverInterface && devices.FreshPress(GlfwMouseButton.Right)));
        devices.CameraDrag = _cameraDrag;
        var worldOwnsPointer = _commandView || !ReleaseHeld;
        devices.SuppressMouse = !worldInput || PointerOverInterface || !worldOwnsPointer;
    }

    /// <summary>Ends the world's read of the mouse, so the interface sees the devices unmasked.</summary>
    public void EndWorldMouse()
    {
        devices.SuppressMouse = false;
        devices.CameraDrag = false;
        devices.ReadingWorldMouse = false;
    }

    private void Apply(bool captured)
    {
        if (captured == Captured)
            return;
        Captured = captured;
        ui.SetPointerCaptured(captured);
        // What the poll held was meant for the previous owner; neither may act on it now.
        devices.QuarantineMouse();
    }
}
