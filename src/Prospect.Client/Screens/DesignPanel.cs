using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Designs;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

/// <summary>
/// What the ship editor's Save design and Load design dialogs show inside their panel: the name field with its live
/// check, the list of saved designs with what each is, the two questions (overwrite, replace the draft) and their
/// buttons. A helper of its own, with the window and the panel's frame left to the caller, so the frame order tests
/// draw exactly what the game draws. Enter and Escape are not read here: they are <see cref="DesignDialog.HandleKeys"/>'s.
/// </summary>
internal static class DesignPanel
{
    private const float RowHeight = 36f;

    /// <summary>The panel's rectangle: centred a little above the middle, narrowed to fit the window.</summary>
    public static HudRect Panel(float2 display, float scale)
    {
        float Px(float design) => MathF.Round(design * scale);
        var margin = Px(FloatingWindow.Margin);
        var top = HudLayout.TopBarBottom(scale) + margin;
        var width = Math.Min(Px(DesignDialog.PanelWidth), Math.Max(0f, display.X - margin * 2));
        var height = Math.Min(Px(DesignDialog.PanelHeight), Math.Max(0f, display.Y - top - margin));
        var left = MathF.Round((display.X - width) * 0.5f);
        var at = Math.Max(top, MathF.Round(display.Y * 0.45f - height * 0.5f));
        return new HudRect(left, at, left + width, at + height);
    }

    /// <summary>Draws the dialog's content at the cursor, inside a panel as <c>BeginPanel</c> opens one.</summary>
    public static void DrawBody(DesignDialog dialog, DesignShelf shelf, ImInputString name, ShipEditor editor, NoticeBoard board, UiContext ui)
    {
        if (dialog.Stage is DesignStage.Naming or DesignStage.Saving or DesignStage.ConfirmOverwrite)
            DrawSave(dialog, shelf, name, editor, ui);
        else if (dialog.Stage == DesignStage.ConfirmReplace)
            DrawReplace(dialog, shelf, editor, board, ui);
        else
            DrawLoad(dialog, shelf, editor, ui);
    }

    private static void DrawSave(DesignDialog dialog, DesignShelf shelf, ImInputString name, ShipEditor editor, UiContext ui)
    {
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        Kit.Caption("SAVE DESIGN"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Name this design"u8);
        Kit.Space(Theme.Space2);
        Kit.Paragraph(ui.Sans, Theme.FontBody, Theme.TextDim, "It is saved on this computer, in your designs folder, and can be loaded into the editor later."u8, width / Kit.Scale);
        Kit.Space(Theme.Space3);

        var typing = dialog.Stage == DesignStage.Naming;
        if (!typing)
            ImGui.BeginDisabled();
        // The caret stays in the field whenever nothing else is being used, so typing always reaches it.
        if (typing && !ImGui.IsAnyItemActive())
            ImGui.SetKeyboardFocusHere();
        Kit.TextField("NAME"u8, "##designname"u8, name, width / Kit.Scale, ImGuiInputTextFlags.NoUndoRedo);
        if (!typing)
            ImGui.EndDisabled();

        Span<char> chars = stackalloc char[DesignDialog.NameBytes];
        var problem = DesignDialog.Check(name.ValueSpan, chars, out var length);
        Kit.Space(Theme.Space2);
        DrawSaveStatus(draw, dialog, name, ui, width, problem);
        Kit.Space(Theme.Space3);

        var busy = dialog.Stage == DesignStage.Saving;
        if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140, !busy))
            dialog.Back(shelf);
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (dialog.Stage == DesignStage.ConfirmOverwrite)
        {
            if (Kit.Button("Overwrite"u8, ButtonKind.Primary, 180))
                dialog.Submit(dialog.SavingName, shelf, editor, true);
        }
        else
        {
            var ready = !busy && problem == NameProblem.None;
            if (Kit.Button("Save design"u8, ButtonKind.Primary, 180, ready) && ready)
                dialog.Submit(chars[..length], shelf, editor, false);
        }
    }

    private static void DrawSaveStatus(ImDrawListPtr draw, DesignDialog dialog, ImInputString name, UiContext ui, float width, NameProblem problem)
    {
        var at = ImGui.GetCursorScreenPos();
        var height = Kit.Measure(ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        // Two lines are kept whatever the line says, so the buttons below never move.
        ImGui.Dummy(new float2(width, height * 2f + Kit.Px(Theme.Space1)));
        if (dialog.Stage == DesignStage.Saving)
        {
            Kit.Spinner(draw, at + new float2(Kit.Px(6), height * 0.5f + Kit.Px(1)), Kit.Px(6));
            Kit.ClippedText(draw, ui.Sans, Theme.FontSmall, at + new float2(Kit.Px(20), 0), Theme.Info, "Saving the design..."u8, width - Kit.Px(20));
            return;
        }

        if (dialog.Stage == DesignStage.ConfirmOverwrite)
        {
            var asked = Kit.F($"A design called {dialog.SavingName} is already saved. Overwrite it?");
            Wrapped(draw, ui, at, Theme.Accent, asked, width);
            return;
        }

        if (dialog.Problem.Length > 0)
        {
            Wrapped(draw, ui, at, Theme.Bad, dialog.Problem, width);
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
            Kit.ClippedText(draw, ui.Sans, Theme.FontSmall, at, Theme.Bad, NewShipPanel.Problem(problem), text);
        else
            Wrapped(draw, ui, at, Theme.TextMute, "1 to 32 characters. Enter saves, Esc cancels."u8, text);
    }

    private static void DrawReplace(DesignDialog dialog, DesignShelf shelf, ShipEditor editor, NoticeBoard board, UiContext ui)
    {
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        Kit.Caption("LOAD DESIGN"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Replace your draft?"u8);
        Kit.Space(Theme.Space1);
        if (dialog.Picked is { } picked)
            Kit.ClippedText(draw, ui.SansMedium, Theme.FontBody, ImGui.GetCursorScreenPos(), Theme.Accent, picked.Name, width);
        Kit.Space(Theme.Space5);
        Kit.Paragraph(ui.Sans, Theme.FontBody, Theme.Text, "This replaces your current draft. Your ship does not change until you press Apply."u8, width / Kit.Scale);
        Kit.Space(Theme.Space2);
        Kit.Paragraph(ui.Sans, Theme.FontSmall, Theme.TextMute, "You can undo the replacement with Ctrl+Z until you leave the editor."u8, width / Kit.Scale);
        Kit.Space(Theme.Space4);
        if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140))
            dialog.Back(shelf);
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (Kit.Button("Replace"u8, ButtonKind.Primary, 180))
            dialog.Replace(shelf, editor, board);
    }

    private static void DrawLoad(DesignDialog dialog, DesignShelf shelf, ShipEditor editor, UiContext ui)
    {
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        Kit.Caption("LOAD DESIGN"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Saved designs"u8);
        Kit.Space(Theme.Space2);
        if (editor.Draft is { } draft)
        {
            var note = Kit.F($"Your ship is built on {draft.BlockSize:0.##} m blocks. Only designs on blocks of that size can be loaded.");
            Kit.Paragraph(ui.Sans, Theme.FontSmall, Theme.TextDim, note, width / Kit.Scale);
        }

        Kit.Space(Theme.Space2);

        var line = Kit.Measure(ui.Sans, Theme.FontSmall, "Ag"u8).Y;
        var footer = Kit.Px(Theme.ControlHeight + Theme.Space4) + line * 2f + Kit.Px(Theme.Space2);
        var listHeight = Math.Max(Kit.Px(60), ImGui.GetContentRegionAvail().Y - footer);
        var reading = dialog.Stage == DesignStage.Reading;
        ImGui.BeginChild("##designlist"u8, new float2(0, listHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        if (shelf.Listing)
            Kit.Paragraph(ui.Sans, Theme.FontBody, Theme.TextDim, "Reading your saved designs..."u8, width / Kit.Scale);
        else if (shelf.Entries.Length == 0)
            Kit.Paragraph(ui.Sans, Theme.FontBody, Theme.TextDim, "No saved designs yet. Use Save... in the editor to keep one."u8, width / Kit.Scale);
        else
        {
            var entries = shelf.Entries;
            for (var i = 0; i < entries.Length; i++)
                if (Row(entries[i], i, editor, ui, reading, ReferenceEquals(dialog.Picked, entries[i])))
                    dialog.Pick(entries[i], shelf, editor);
        }

        ImGui.EndChild();

        Kit.Space(Theme.Space2);
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new float2(width, line * 2f));
        if (reading)
        {
            Kit.Spinner(draw, at + new float2(Kit.Px(6), line * 0.5f + Kit.Px(1)), Kit.Px(6));
            Kit.ClippedText(draw, ui.Sans, Theme.FontSmall, at + new float2(Kit.Px(20), 0), Theme.Info, "Reading and checking the design..."u8, width - Kit.Px(20));
        }
        else if (dialog.Problem.Length > 0)
            Wrapped(draw, ui, at, Theme.Bad, dialog.Problem, width);
        Kit.Space(Theme.Space3);
        if (Kit.Button(reading ? "Stop"u8 : "Close"u8, ButtonKind.Secondary, 140))
            dialog.Back(shelf);
    }

    // One saved design: its name and what it is. True when it was clicked while it can be loaded.
    private static bool Row(DesignEntry entry, int index, ShipEditor editor, UiContext ui, bool locked, bool picked)
    {
        var draw = ImGui.GetWindowDrawList();
        var height = Kit.Px(RowHeight);
        var at = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var fits = DesignDialog.Fits(entry, editor);
        Kit.PushId((ulong)index + 1);
        var pressed = ImGui.InvisibleButton("##design"u8, new float2(width, height));
        var hot = fits && !locked && Kit.IsHot();
        Kit.PopId();
        var pad = Kit.Px(Theme.Space3);
        if (hot || picked)
            Kit.ChamferFill(draw, at, at + new float2(width, height), Kit.Color(Theme.PanelRaised), Kit.Px(4));
        if (picked || hot)
            Kit.ChamferOutline(draw, at, at + new float2(width, height), Kit.Color(picked ? Theme.Accent : Theme.LineStrong), Kit.Px(4));

        uint tone = fits ? Theme.TextMute : Theme.Bad;
        var detail = entry.State switch
        {
            DesignFileState.Unreadable => "unreadable"u8,
            _ when !fits => Kit.F($"does not fit: {entry.BlockSize:0.##} m blocks"),
            _ => Kit.F($"{entry.Blocks} blocks, {entry.BlockSize:0.##} m"),
        };
        var detailWidth = Kit.Measure(ui.Sans, Theme.FontSmall, detail).X;
        var middle = at.Y + (height - Kit.Measure(ui.SansMedium, Theme.FontBody, "Ag"u8).Y) * 0.5f;
        Kit.ClippedText(draw, ui.SansMedium, Theme.FontBody, new float2(at.X + pad, middle), fits ? Theme.Text : Theme.TextMute, entry.Name, Math.Max(0f, width - detailWidth - pad * 3));
        Kit.Text(draw, ui.Sans, Theme.FontSmall, new float2(at.X + width - detailWidth - pad, at.Y + (height - Kit.Measure(ui.Sans, Theme.FontSmall, "Ag"u8).Y) * 0.5f), tone, detail);
        return pressed && fits && !locked;
    }

    private static void Wrapped(ImDrawListPtr draw, UiContext ui, float2 at, uint color, ReadOnlySpan<byte> text, float width)
    {
        ImGui.PushFont(ui.Sans, Theme.FontSmall);
        draw.AddText(ui.Sans, ImGui.GetFontSize(), new float2(MathF.Round(at.X), MathF.Round(at.Y)), Kit.Color(color), text, width);
        ImGui.PopFont();
    }

    private static int CountScalars(ReadOnlySpan<byte> utf8)
    {
        var count = 0;
        foreach (var value in utf8)
            if ((value & 0xC0) != 0x80)
                count++;
        return count;
    }
}
