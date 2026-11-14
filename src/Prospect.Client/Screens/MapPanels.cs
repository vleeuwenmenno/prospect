using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Settings;

namespace Prospect.Client.Screens;

/// <summary>The galaxy map's panels the player can move: the unit list, the map index and the layer bar.</summary>
internal enum MapPanel : byte
{
    Units,
    Legend,
    Layers,
    Count,
}

/// <summary>
/// Where the galaxy map's panels are: each panel's default place plus the shift the player dragged it to, kept per
/// client in <see cref="ClientSettings.PanelPositions"/> by the rules the tip panels use (<see cref="PanelShift"/>,
/// <see cref="PanelOffsets"/>). A panel is dragged by its <see cref="Grip"/>, an invisible item over the title
/// strip that leaves out every control in it, so a press on a button or a row is never a drag, and a press on the
/// grip is never the map's: Dear ImGui holds the grip active until the button is let go, and the map's surface
/// cannot take a press while another item is active. A drag Dear ImGui held last frame is applied in
/// <see cref="Place"/>, before the frame is laid out, so the rectangle the map blocks, draws and hit-tests is the
/// one the panel is at. The position is clamped every frame, so a resize or a new interface scale never strands a
/// panel. The list is saved once the grip is let go (<see cref="TakeChanged"/>), not every frame. No fonts here, so
/// the frame order tests run it as the game does, and nothing allocates after a panel's first move.
/// </summary>
internal sealed class MapPanels
{
    private static readonly string[] Keys = ["Map.Units", "Map.Legend", "Map.Layers"];

    private readonly ClientSettings _settings;
    private readonly uint[] _grip = new uint[(int)MapPanel.Count];
    private bool _dirty;

    public MapPanels(ClientSettings settings)
    {
        _settings = settings;
    }

    /// <summary>The name a panel's position is saved under.</summary>
    public static string KeyOf(MapPanel panel) => Keys[(int)panel];

    /// <summary>The shift, in unscaled pixels, the player has dragged a panel to; zero when never moved.</summary>
    public float2 ShiftOf(MapPanel panel) => PanelOffsets.Get(_settings.PanelPositions, Keys[(int)panel]);

    /// <summary>True while the player holds the panel's grip, as Dear ImGui saw it at the start of this frame.</summary>
    public bool Held(MapPanel panel)
    {
        var item = _grip[(int)panel];
        return item != 0 && ImGui.GetCurrentContext().ActiveId == item && ImGui.IsMouseDown(ImGuiMouseButton.Left);
    }

    /// <summary>
    /// Where the panel goes this frame, once: the drag held last frame is applied here, then the place is kept
    /// wholly on screen under the navigation bar. <paramref name="home"/> is where the game puts the panel with no
    /// shift. Call before the panel's rectangle is blocked or drawn.
    /// </summary>
    public float2 Place(MapPanel panel, float2 home, float2 size, float2 display, float scale)
    {
        var key = Keys[(int)panel];
        var shift = PanelOffsets.Get(_settings.PanelPositions, key);
        if (Held(panel))
        {
            shift = PanelShift.Dragged(home, size, display, scale, shift, ImGui.GetIO().MouseDelta);
            if (PanelOffsets.Set(_settings.PanelPositions, key, shift, ClientSettings.MaxPanelPositions))
                _dirty = true;
        }

        return PanelShift.Place(home, size, display, scale, shift);
    }

    /// <summary>
    /// Submits the panel's grip, an item of <paramref name="size"/> pixels at <paramref name="min"/> in the panel's
    /// window, drawn nothing. Submit it before the controls that sit beside it, and give it only the part of the
    /// title strip they leave free.
    /// </summary>
    public void Grip(MapPanel panel, float2 min, float2 size)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##mapgrip"u8, new float2(Math.Max(size.X, 1f), Math.Max(size.Y, 1f)));
        _grip[(int)panel] = ImGui.GetItemID();
        if (ImGui.IsItemActive() || ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
    }

    /// <summary>Puts every panel back at its default place. Saved with the next <see cref="TakeChanged"/>.</summary>
    public void Reset()
    {
        if (_settings.PanelPositions.Count == 0)
            return;
        _settings.PanelPositions.Clear();
        _dirty = true;
    }

    /// <summary>
    /// The reset key: puts every panel back when the action is pressed. False while a text field has the keyboard
    /// (<paramref name="keyboardAvailable"/>). True when a press was taken.
    /// </summary>
    public bool HandleKey(ActionMap actions, InputDevices devices, bool keyboardAvailable)
    {
        if (!keyboardAvailable || !actions.Pressed(devices, InputAction.ResetMapPanels, true))
            return false;
        Reset();
        return true;
    }

    /// <summary>True once after a position changed and no grip is held, so the caller can save the settings.</summary>
    public bool TakeChanged()
    {
        if (!_dirty)
            return false;
        for (var i = 0; i < _grip.Length; i++)
        {
            if (Held((MapPanel)i))
                return false;
        }

        _dirty = false;
        return true;
    }
}
