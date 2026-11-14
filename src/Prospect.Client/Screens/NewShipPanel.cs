using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

/// <summary>
/// What the New ship dialog shows inside its panel: the title, the name field with its live check, one status line
/// (progress, the server's reason for a refusal, the name's own problem or the rule) and the Cancel and Build
/// buttons. A helper of its own, with the window and the panel's frame left to the caller, so the frame order tests
/// draw exactly what the game draws. Enter and Escape are not read here: they are <see cref="NewShipDialog.HandleKeys"/>'s.
/// </summary>
internal static class NewShipPanel
{
    /// <summary>The server's reason for not making a ship, in a sentence for the dialog.</summary>
    public static ReadOnlySpan<byte> Reason(EditorReason reason) => reason switch
    {
        EditorReason.BadName => "That name cannot be used. Type 1 to 32 printable characters."u8,
        EditorReason.DuplicateName => "You already have a ship with that name."u8,
        EditorReason.ShipLimit => "You have as many ships as this server allows."u8,
        EditorReason.NoClearSpot => "No clear space ahead of the drone. Move away from rocks and ships and try again."u8,
        EditorReason.NotPiloting => "Only the drone can build a new ship."u8,
        EditorReason.Jumping => "A ship cannot be built during a jump or with a jump ordered."u8,
        EditorReason.Busy => "The server is busy. Try again in a moment."u8,
        EditorReason.TooSoon => "Too soon after the last ship. Try again shortly."u8,
        EditorReason.CatalogueFull => "This server has no room for more designs until it restarts."u8,
        EditorReason.HullLimit => "The world has no room for another ship."u8,
        EditorReason.ClientNotReady => "Your game was not ready to show the new ship in time."u8,
        EditorReason.ClientFailed => "Your game could not prepare the new ship."u8,
        EditorReason.Unavailable => "The server did not answer. Try again."u8,
        _ => "The server could not make the ship."u8,
    };

    /// <summary>What is wrong with the typed name, in a sentence.</summary>
    public static ReadOnlySpan<byte> Problem(NameProblem problem) => problem switch
    {
        NameProblem.Empty => "Type a name."u8,
        NameProblem.TooLong => "That name is longer than 32 characters."u8,
        _ => "That name has a character a ship's name cannot hold."u8,
    };

    /// <summary>Draws the panel's content at the cursor, inside a panel as <c>BeginPanel</c> opens one.</summary>
    public static void DrawBody(NewShipDialog dialog, ImInputString name, SessionSnapshot snapshot, PlayerInputBridge bridge, UiContext ui)
    {
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        Kit.Caption("NEW SHIP"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Name your ship"u8);
        Kit.Space(Theme.Space2);
        Kit.Paragraph(ui.Sans, Theme.FontBody, Theme.TextDim, "It starts as one block just ahead of the drone. You build the rest in the editor."u8, width / Kit.Scale);
        Kit.Space(Theme.Space3);

        var busy = dialog.Busy;
        if (busy)
            ImGui.BeginDisabled();
        // The caret stays in the field whenever nothing else is being used, so typing always reaches it.
        if (!busy && !ImGui.IsAnyItemActive())
            ImGui.SetKeyboardFocusHere();
        Kit.TextField("NAME"u8, "##newshipname"u8, name, width / Kit.Scale, ImGuiInputTextFlags.NoUndoRedo);
        if (busy)
            ImGui.EndDisabled();

        Span<char> chars = stackalloc char[NewShipDialog.NameBytes];
        var problem = NewShipDialog.Check(name.ValueSpan, chars, out var length);
        Kit.Space(Theme.Space2);
        DrawStatus(draw, dialog, name, ui, width, problem);
        Kit.Space(Theme.Space3);

        var ready = !busy && problem == NameProblem.None;
        if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140, !busy))
            dialog.Cancel();
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (Kit.Button("Build ship"u8, ButtonKind.Primary, 180, ready) && ready)
            dialog.Submit(chars[..length], snapshot, bridge);
    }

    /// <summary>The line under the field: progress, the server's refusal, the name's own problem, or the rule and the length.</summary>
    private static void DrawStatus(ImDrawListPtr draw, NewShipDialog dialog, ImInputString name, UiContext ui, float width, NameProblem problem)
    {
        var at = ImGui.GetCursorScreenPos();
        var height = Kit.Measure(ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        // Two lines are kept whatever the line says, so the buttons below never move.
        ImGui.Dummy(new float2(width, height * 2f + Kit.Px(Theme.Space1)));
        if (dialog.Busy)
        {
            Kit.Spinner(draw, at + new float2(Kit.Px(6), height * 0.5f + Kit.Px(1)), Kit.Px(6));
            var line = dialog.Stage == NewShipStage.Waiting ? "Asking the server to make the ship..."u8 : "Ship made. Opening the editor..."u8;
            Kit.ClippedText(draw, ui.Sans, Theme.FontSmall, at + new float2(Kit.Px(20), 0), Theme.Info, line, width - Kit.Px(20));
            return;
        }

        if (dialog.Refusal != EditorReason.None)
        {
            Wrapped(draw, ui, at, Theme.Bad, Reason(dialog.Refusal), width);
            return;
        }

        var typed = !name.IsEmpty;
        var text = width;
        if (typed)
        {
            var counter = Kit.F($"{CountScalars(name.ValueSpan)}/{ShipName.MaxChars}");
            var counterWidth = Kit.Measure(ui.Mono, Theme.FontCaption, counter).X;
            Kit.Text(draw, ui.Mono, Theme.FontCaption, at + new float2(width - counterWidth, Kit.Px(2)), problem == NameProblem.None ? Theme.TextMute : Theme.Bad, counter);
            text -= counterWidth + Kit.Px(Theme.Space3);
        }

        if (typed && problem != NameProblem.None)
            Kit.ClippedText(draw, ui.Sans, Theme.FontSmall, at, Theme.Bad, Problem(problem), text);
        else
            Wrapped(draw, ui, at, Theme.TextMute, "1 to 32 characters, different from your other ships. Enter builds, Esc cancels."u8, text);
    }

    /// <summary>Small text wrapped to a width where it is, in the room the status line has already reserved.</summary>
    private static void Wrapped(ImDrawListPtr draw, UiContext ui, float2 at, uint color, ReadOnlySpan<byte> text, float width)
    {
        ImGui.PushFont(ui.Sans, Theme.FontSmall);
        draw.AddText(ui.Sans, ImGui.GetFontSize(), new float2(MathF.Round(at.X), MathF.Round(at.Y)), Kit.Color(color), text, width);
        ImGui.PopFont();
    }

    /// <summary>How many characters UTF-8 text holds: the bytes that start one.</summary>
    private static int CountScalars(ReadOnlySpan<byte> utf8)
    {
        var count = 0;
        foreach (var value in utf8)
            if ((value & 0xC0) != 0x80)
                count++;
        return count;
    }
}
