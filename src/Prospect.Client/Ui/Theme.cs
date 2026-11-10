namespace Prospect.Client.Ui;

/// <summary>
/// The visual language of the game's interface: colour, type scale, spacing and motion. Every widget reads these;
/// nothing in the UI uses a Dear ImGui default.
/// </summary>
internal static class Theme
{
    // Colours are 0xRRGGBB; Kit.Color turns them into draw colours with the current fade applied.
    public const uint Void = 0x05070A;
    public const uint Backdrop = 0x0A0F15;
    public const uint Panel = 0x0E141B;
    public const uint PanelRaised = 0x151D27;
    public const uint Line = 0x243140;
    public const uint LineStrong = 0x3B4C61;

    public const uint Text = 0xE8EEF4;
    public const uint TextDim = 0x94A2B3;
    public const uint TextMute = 0x596778;

    /// <summary>Signal amber: the one colour that means "act here".</summary>
    public const uint Accent = 0xFFB020;
    public const uint AccentBright = 0xFFCB66;
    public const uint AccentInk = 0x1B1300;

    public const uint Info = 0x45C8DC;
    public const uint Good = 0x4CD38A;
    public const uint Bad = 0xFF5A52;

    // Type scale, in unscaled pixels.
    public const float FontCaption = 12f;
    public const float FontSmall = 14f;
    public const float FontBody = 16f;
    public const float FontMenu = 22f;
    public const float FontHeading = 26f;
    public const float FontTitle = 76f;

    // Spacing steps, in unscaled pixels.
    public const float Space1 = 4f;
    public const float Space2 = 8f;
    public const float Space3 = 12f;
    public const float Space4 = 16f;
    public const float Space5 = 24f;
    public const float Space6 = 32f;
    public const float Space7 = 48f;

    /// <summary>The cut corner shared by panels and buttons.</summary>
    public const float Chamfer = 9f;
    public const float ControlHeight = 40f;

    // Motion.
    /// <summary>How fast hover and focus states settle, as an exponential rate per second.</summary>
    public const float HoverRate = 16f;

    /// <summary>Seconds a screen takes to fade and slide in.</summary>
    public const float ScreenEnter = 0.22f;
}
