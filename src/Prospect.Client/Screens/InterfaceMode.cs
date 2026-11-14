using Prospect.Client.Input;

namespace Prospect.Client.Screens;

/// <summary>
/// Interface mode: latched on by one key press and off by the next (or by Escape), where the pointer is free and every
/// window that is open and not pinned is shown. While it is on the interface owns the pointer and flight input is off,
/// so a click never reaches the world; the pointer router sees it as an open surface. This is only the latch and its
/// key, font-free, so the frame order tests drive the production key as <c>ScreenHost</c> does.
/// </summary>
internal sealed class InterfaceMode
{
    /// <summary>True while interface mode is on.</summary>
    public bool Active { get; set; }

    /// <summary>
    /// Reads the interface mode key (Tab by default): it turns the mode on, or off when it is on. A key does not act
    /// while a text field has the keyboard, as other interface shortcuts do not. Returns true when the key was this
    /// frame's, so the world must not read it too.
    /// </summary>
    public bool HandleKey(ActionMap actions, InputDevices devices, bool typing, bool pointerOwned)
    {
        if (!actions.InterfaceShortcutPressed(devices, InputAction.ToggleInterface, typing, pointerOwned))
            return false;
        Active = !Active;
        return true;
    }

    /// <summary>
    /// True when a window is drawn: open, and either interface mode is on or the player pinned it. A pinned window is
    /// the only one that stays after the mode ends.
    /// </summary>
    public bool Shows(bool open, bool pinned) => open && (Active || pinned);
}
