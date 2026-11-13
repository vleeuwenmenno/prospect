using Prospect.Client.Input;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>The passive sector panel, toggled by its binding and reset for each game.</summary>
internal sealed class SectorInfoToggle
{
    /// <summary>True while the sector panel is shown.</summary>
    public bool Open { get; private set; }

    /// <summary>Closes the panel, for a new game.</summary>
    public void Reset() => Open = false;

    /// <summary>
    /// Toggles the panel on the frame its key goes down, unless <paramref name="allowed"/> is false (a menu,
    /// Controls or the social workspace has the input) or a text field has the keyboard. The key is not consumed:
    /// the panel is passive, so the same frame's flight input still reaches the hull. True when it toggled.
    /// </summary>
    public bool HandleShortcut(ActionMap actions, InputDevices devices, bool allowed, bool typing, bool pointerOverInterface)
    {
        if (!allowed || typing || !actions.InterfaceShortcutPressed(devices, InputAction.ToggleSectorInfo, false, pointerOverInterface))
            return false;
        Open = !Open;
        return true;
    }

    /// <summary>The panel begins directly below the navigation bar.</summary>
    public static float PanelTop(float scale) => HudLayout.Top(scale);

    /// <summary>A hidden panel reserves no height.</summary>
    public static float Bottom(float scale, bool open, float panelHeight) =>
        HudLayout.Top(scale) + (open ? panelHeight : 0f);

    /// <summary>Fits the sector details and party cards into the column before either is drawn.</summary>
    public static (float Bottom, bool Compact, bool Cards) Column(float scale, bool open, float fullHeight, float compactHeight, float partyHeight, float limit)
    {
        var gap = MathF.Round(Theme.Space3 * scale);
        var compact = open && partyHeight > 0f && Bottom(scale, open, fullHeight) + gap + partyHeight > limit;
        var bottom = Bottom(scale, open, compact ? compactHeight : fullHeight);
        return (bottom, compact, bottom + gap + partyHeight <= limit);
    }

}
