using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>What the player asked for that the "Discard changes?" question stands in front of.</summary>
internal enum DiscardThen : byte
{
    /// <summary>Only to leave the edit (the editor key, Escape, Cancel).</summary>
    Cancel,

    /// <summary>Leave the session from the in-game menu, once the edit is let go.</summary>
    Leave,

    /// <summary>Quit to the desktop from the in-game menu, once the edit is let go.</summary>
    Quit,
}

/// <summary>The player's answer to the "Discard changes?" question.</summary>
internal enum DiscardAnswer : byte
{
    /// <summary>No button was pressed this frame.</summary>
    None,

    /// <summary>Keep editing: the draft stays as it was.</summary>
    Keep,

    /// <summary>Discard: the draft is thrown away and what was asked for goes on.</summary>
    Discard,
}

/// <summary>
/// What the "Discard changes?" question shows inside its panel: two buttons, Discard and Keep editing, the second
/// the primary one. A helper of its own, with the window and the panel's frame left to the caller, so the frame order
/// tests draw exactly what the game draws. Enter and Escape are not read here: they are
/// <see cref="World.ShipEditor.HandleKeys"/>'s, and both keep editing.
/// </summary>
internal static class DiscardPanel
{
    /// <summary>The panel's width and height in design pixels, before it is narrowed to fit the window.</summary>
    public const float PanelWidth = 460f;
    public const float PanelHeight = 220f;

    /// <summary>The panel's rectangle: centred a little above the middle, narrowed to fit the window.</summary>
    public static HudRect Panel(float2 display, float scale)
    {
        float Px(float design) => MathF.Round(design * scale);
        var margin = Px(FloatingWindow.Margin);
        var width = Math.Min(Px(PanelWidth), Math.Max(0f, display.X - margin * 2));
        var height = Math.Min(Px(PanelHeight), Math.Max(0f, display.Y - margin * 2));
        var left = MathF.Round((display.X - width) * 0.5f);
        var top = Math.Max(margin, MathF.Round(display.Y * 0.45f - height * 0.5f));
        return new HudRect(left, top, left + width, top + height);
    }

    /// <summary>Draws the question at the cursor, inside a panel as <c>BeginPanel</c> opens one, and says which button was pressed.</summary>
    public static DiscardAnswer DrawBody(UiContext ui)
    {
        var width = ImGui.GetContentRegionAvail().X;
        Kit.Caption("SHIP EDITOR"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Discard changes?"u8);
        Kit.Space(Theme.Space3);
        Kit.Paragraph(ui.Sans, Theme.FontBody, Theme.Text, "You have changes that are not applied. If you leave now they are lost."u8, width / Kit.Scale);
        Kit.Space(Theme.Space4);
        var answer = DiscardAnswer.None;
        if (Kit.Button("Discard"u8, ButtonKind.Secondary, 140))
            answer = DiscardAnswer.Discard;
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (Kit.Button("Keep editing"u8, ButtonKind.Primary, 180))
            answer = DiscardAnswer.Keep;
        return answer;
    }
}
