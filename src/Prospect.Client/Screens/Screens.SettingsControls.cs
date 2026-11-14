using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private enum ControlItemKind : byte
    {
        Caption,
        Chip,
        Remove,
        Add,
        Reset,
        Note,
    }

    /// <summary>One piece of an editable row, laid out before it is drawn so the row's height is known.</summary>
    private struct ControlItem
    {
        public ControlItemKind Kind;
        public ControlSlot Slot;
        public int Index;
        public float2 Min;
        public float2 Size;
        public string Text;
    }

    private bool _settingsControlsPage;
    private readonly ImInputString _settingsControlsSearch = new(128);
    private ControlsBrowser _settingsBrowser = null!;
    private ControlRemapper _remapper = null!;
    private ControlCapture _capture = null!;
    private RemapProposal? _proposal;
    private bool _resetAllAsk;
    private bool _suppressChip;
    private ControlSlot _suppressSlot;
    private int _suppressIndex;
    private readonly Dictionary<InputBinding, string> _chipText = [];
    private readonly ControlItem[] _controlItems = new ControlItem[32];

    private void InitializeSettingsControls()
    {
        _settingsBrowser = new ControlsBrowser(_controls);
        _remapper = new ControlRemapper(_app.Actions, _controls, _app.Settings);
        _capture = new ControlCapture(_app.Actions);
    }

    /// <summary>Stops a capture and drops any question, for leaving the page or the settings.</summary>
    private void LeaveSettingsControls()
    {
        _capture.Cancel();
        _proposal = null;
        _resetAllAsk = false;
        _settingsControlsPage = false;
        _suppressChip = false;
    }

    /// <summary>Shows the Controls page of Settings in a state for a scripted capture: <c>capture</c> waits for a control, <c>conflict</c> asks about one.</summary>
    public void PreviewSettingsControls(string? sample, string? search, string? tab)
    {
        _settingsControlsPage = true;
        _settingsControlsSearch.Clear();
        _settingsBrowser.Reset();
        if (search is not null)
        {
            _settingsControlsSearch.SetValue(search);
            _settingsBrowser.SetQuery(search);
        }

        SelectControlsTab(_settingsBrowser, tab);
        if (sample == "capture")
            _capture.Begin(ControlSlot.Of(InputAction.Boost), 0);
        else if (sample == "conflict")
            _proposal = _remapper.Propose(ControlSlot.Of(InputAction.Boost), 0, new InputBinding(InputBinding.Source.Key, (int)Brutal.GlfwApi.GlfwKey.Z));
    }

    private string ChipName(InputBinding binding)
    {
        if (!_chipText.TryGetValue(binding, out var text))
            _chipText[binding] = text = binding.DisplayName;
        return text;
    }

    private void BeginCapture(ControlSlot slot, int index)
    {
        _proposal = null;
        _resetAllAsk = false;
        _suppressChip = false;
        _capture.Begin(slot, index);
    }

    private void DrawSettingsControls(float fade)
    {
        _controls.Refresh();
        _settingsBrowser.Recount();

        // A press is taken before anything else reads actions: while waiting, the map answers nothing.
        switch (_capture.Update(_app.Devices))
        {
            case CaptureResult.Captured:
                _proposal = _remapper.Propose(_capture.Slot, _capture.Index, _capture.Binding);
                break;
            case CaptureResult.Cancelled when _capture.EndedByClick:
                _suppressChip = true;
                _suppressSlot = _capture.Slot;
                _suppressIndex = _capture.Index;
                break;
        }

        BeginRoot("##settingscontrols"u8);
        var top = Math.Max(_ui.DisplaySize.Y * 0.08f, HudTop);
        var width = Math.Min(Kit.Px(820), _ui.DisplaySize.X - Kit.Px(24));
        var min = new float2(MathF.Round((_ui.DisplaySize.X - width) * 0.5f), top + Slide(fade));
        var size = new float2(width, Math.Max(_ui.DisplaySize.Y - top - Kit.Px(Theme.Space3), Kit.Px(200)));
        BeginPanel("##settingscontrolspanel"u8, min, size);

        Kit.Caption("SETTINGS"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Controls"u8);
        Kit.Space(Theme.Space2);
        var back = Kit.Button("Back"u8, ButtonKind.Secondary, 120);
        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        if (Kit.Button("Reset all controls"u8, ButtonKind.Secondary, 190, enabled: _remapper.AnyChanged() && !_capture.Active))
        {
            _resetAllAsk = true;
            _proposal = null;
        }

        Kit.Space(Theme.Space2);
        DrawControlsNotice(width / Kit.Scale - Theme.Space6 * 2);

        DrawControlsSearchBox(_settingsBrowser, _settingsControlsSearch, "##settingscontrolssearch"u8);
        Kit.Space(Theme.Space2);
        DrawControlTabs(_settingsBrowser, ImGui.GetContentRegionAvail().X);
        Kit.Space(Theme.Space3);

        ImGui.BeginChild("##settingscontrolrows"u8, float2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        var rowWidth = ImGui.GetContentRegionAvail().X - Kit.Px(Theme.Space3);
        foreach (var row in _settingsBrowser.Catalog.Tabs[_settingsBrowser.Selected].Rows)
        {
            if (row.Kind == RowKind.Fixed)
                DrawReferenceRow(_settingsBrowser, row, rowWidth, fixedRow: true);
            else
                DrawEditableRow(row, rowWidth);
        }

        Kit.Space(Theme.Space3);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute,
            "Mouse gestures, Ctrl chords and the editor's S and R are fixed. Escape and the left mouse button cannot be bound. Sticks, triggers and the roll bumpers stay as they are; invert pitch is under Steering."u8,
            rowWidth / Kit.Scale);
        ImGui.EndChild();
        EndPanel();

        // Escape answers the open question first, then leaves the page.
        if (BackPressed())
        {
            if (_proposal is not null)
                _proposal = null;
            else if (_resetAllAsk)
                _resetAllAsk = false;
            else
                back = true;
        }

        if (back)
            LeaveSettingsControls();
        DrawHints(back: true);
        EndRoot();

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            _suppressChip = false;
        _capture.EndFrame();
    }

    /// <summary>What the page is waiting for, or asking: a capture, a conflict, or the reset question. One line of help otherwise.</summary>
    private void DrawControlsNotice(float width)
    {
        if (_capture.Active)
        {
            Kit.Paragraph(_ui.SansMedium, Theme.FontBody, Theme.Accent, Kit.F($"Press the new control for {_remapper.Label(_capture.Slot)}: a key, a mouse button or a gamepad button. Escape or a left click cancels."), width);
            Kit.Space(Theme.Space2);
            return;
        }

        if (_proposal is { } proposal)
        {
            var other = _remapper.Label(proposal.Conflicts[0]);
            var more = proposal.Conflicts.Length - 1;
            Kit.Paragraph(_ui.SansMedium, Theme.FontBody, Theme.Bad,
                more > 0
                    ? Kit.F($"{ChipName(proposal.Binding)} is already used by {other} and {more} more.")
                    : Kit.F($"{ChipName(proposal.Binding)} is already used by {other}."),
                width);
            Kit.Space(Theme.Space1);
            if (Kit.Button("Swap"u8, ButtonKind.Primary, 110, enabled: _remapper.CanSwap(proposal)) && _remapper.Resolve(proposal, ConflictChoice.Swap))
                _proposal = null;
            ImGui.SameLine(0, Kit.Px(Theme.Space2));
            if (Kit.Button("Clear the other"u8, ButtonKind.Secondary, 170, enabled: _remapper.CanClearOthers(proposal)) && _remapper.Resolve(proposal, ConflictChoice.ClearOther))
                _proposal = null;
            ImGui.SameLine(0, Kit.Px(Theme.Space2));
            if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 110))
                _proposal = null;
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute,
                "Swap gives the other action the control you are replacing. Clear the other unbinds it there and keeps the new control here."u8, width);
            Kit.Space(Theme.Space2);
            return;
        }

        if (_resetAllAsk)
        {
            Kit.Paragraph(_ui.SansMedium, Theme.FontBody, Theme.Text, "Put every control back to its default?"u8, width);
            Kit.Space(Theme.Space1);
            if (Kit.Button("Yes, reset all"u8, ButtonKind.Primary, 170))
            {
                _remapper.ResetAll();
                _resetAllAsk = false;
            }

            ImGui.SameLine(0, Kit.Px(Theme.Space2));
            if (Kit.Button("Keep them"u8, ButtonKind.Secondary, 140))
                _resetAllAsk = false;
            Kit.Space(Theme.Space2);
            return;
        }

        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Click a control to replace it, then press the new one. Changes apply at once and are saved with your settings."u8, width);
        Kit.Space(Theme.Space2);
    }

    /// <summary>
    /// A remappable row: its label on the left; on the right a button for each control it holds, a button to remove
    /// one, one to add one, and a reset when it differs from the default. Lays the pieces out first (they wrap to
    /// more lines when the width is short), then draws and handles them.
    /// </summary>
    private void DrawEditableRow(in ControlsCatalog.Row row, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var pad = Kit.Px(Theme.Space3);
        var labelWidth = MathF.Max(width * 0.34f - pad, Kit.Px(110));
        var regionX = labelWidth + pad * 2;
        var regionWidth = MathF.Max(width - regionX - pad, Kit.Px(120));
        var chipHeight = Kit.Px(30);
        var lineHeight = chipHeight + Kit.Px(Theme.Space1);
        var count = 0;
        var x = 0f;
        var y = 0f;

        void Place(ControlItemKind kind, ControlSlot slot, int index, string text, float itemWidth)
        {
            if (x > 0 && x + itemWidth > regionWidth)
            {
                x = 0;
                y += lineHeight;
            }

            if (count < _controlItems.Length)
                _controlItems[count++] = new ControlItem
                {
                    Kind = kind,
                    Slot = slot,
                    Index = index,
                    Text = text,
                    Min = new float2(regionX + x, y),
                    Size = new float2(itemWidth, chipHeight),
                };
            x += itemWidth + Kit.Px(Theme.Space1);
        }

        float TextWidth(ImFontPtr font, float fontSize, string text) => Kit.Measure(font, fontSize, Kit.F($"{text}")).X;
        float ChipWidth(string text) => TextWidth(_ui.Mono, Theme.FontSmall, text) + Kit.Px(Theme.Space3) * 2;
        var removeWidth = Kit.Px(24);
        var capturing = _capture.Active;

        void Controls(ControlSlot slot, string caption)
        {
            if (caption.Length > 0)
            {
                if (x > 0)
                {
                    x = 0;
                    y += lineHeight;
                }

                Place(ControlItemKind.Caption, slot, -1, caption, TextWidth(_ui.Sans, Theme.FontSmall, caption) + Kit.Px(Theme.Space2));
            }

            var held = _remapper.Count(slot);
            if (held == 0)
            {
                var name = capturing && _capture.Slot == slot ? "Press a control" : "Unbound";
                Place(ControlItemKind.Chip, slot, -1, name, ChipWidth(name));
            }
            for (var i = 0; i < held; i++)
            {
                var name = capturing && _capture.Slot == slot && _capture.Index == i ? "Press a control" : ChipName(_remapper.Get(slot, i));
                Place(ControlItemKind.Chip, slot, i, name, ChipWidth(name));
                if (_remapper.CanRemove(slot, i))
                    Place(ControlItemKind.Remove, slot, i, "x", removeWidth);
            }

            if (_remapper.CanAdd(slot) && held > 0)
                Place(ControlItemKind.Add, slot, -1, "+", Kit.Px(30));
        }

        var primary = row.Kind == RowKind.Action ? ControlSlot.Of((InputAction)row.Index) : ControlSlot.Of((InputAxis)row.Index, false);
        if (row.Kind == RowKind.Action)
            Controls(primary, "");
        else
        {
            Controls(ControlSlot.Of((InputAxis)row.Index, false), row.NegativeLabel);
            Controls(ControlSlot.Of((InputAxis)row.Index, true), row.PositiveLabel);
            if (row.Extra.Length > 0)
            {
                x = 0;
                y += lineHeight;
                Place(ControlItemKind.Note, primary, -1, row.Extra, TextWidth(_ui.Mono, Theme.FontCaption, row.Extra));
            }
        }

        var changed = !_remapper.IsDefault(primary) || (row.Kind == RowKind.Axis && !_remapper.IsDefault(ControlSlot.Of((InputAxis)row.Index, true)));
        if (changed)
            Place(ControlItemKind.Reset, primary, -1, "Reset", TextWidth(_ui.SansMedium, Theme.FontCaption, "Reset") + Kit.Px(Theme.Space4));

        var hit = _settingsBrowser.Highlighted(row);
        var dim = _settingsBrowser.Searching && !hit;
        var labelHeight = WrappedHeight(_ui.Sans, Theme.FontBody, Kit.F($"{row.Action}"), labelWidth);
        var height = Math.Max(labelHeight, y + chipHeight) + pad * 2 - Kit.Px(Theme.Space1);
        var max = position + new float2(width, height);
        if (hit)
        {
            draw.AddRectFilled(position, max, Kit.Color(Theme.PanelRaised, 0.9f));
            draw.AddRectFilled(position, new float2(position.X + Kit.Px(3), max.Y), Kit.Color(Theme.Accent));
        }

        WrappedText(draw, _ui.Sans, Theme.FontBody, position + new float2(pad, pad), dim ? Theme.TextMute : changed ? Theme.Accent : Theme.Text, Kit.F($"{row.Action}"), labelWidth);
        draw.AddLine(new float2(position.X, max.Y - 0.5f), new float2(max.X, max.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));

        ImGui.PushID(row.Index + (int)row.Kind * 1000);
        for (var i = 0; i < count; i++)
        {
            ref var item = ref _controlItems[i];
            var min = position + new float2(0, pad) + item.Min;
            ImGui.PushID(i);
            DrawControlItem(draw, in item, min, dim, row);
            ImGui.PopID();
        }

        ImGui.PopID();
        ImGui.SetCursorScreenPos(position);
        ImGui.Dummy(new float2(width, height));
    }

    private void DrawControlItem(ImDrawListPtr draw, in ControlItem item, float2 min, bool dim, in ControlsCatalog.Row row)
    {
        var max = min + item.Size;
        var chamfer = Kit.Px(4);
        switch (item.Kind)
        {
            case ControlItemKind.Caption:
                Kit.Text(draw, _ui.Sans, Theme.FontSmall, min + new float2(0, (item.Size.Y - Kit.Measure(_ui.Sans, Theme.FontSmall, Kit.F($"{item.Text}")).Y) * 0.5f), Theme.TextMute, Kit.F($"{item.Text}"));
                return;
            case ControlItemKind.Note:
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(0, Kit.Px(8)), Theme.TextMute, Kit.F($"{item.Text}"));
                return;
        }

        ImGui.SetCursorScreenPos(min);
        var pressed = ImGui.InvisibleButton("##item"u8, item.Size);
        var hot = Kit.IsHot();
        var t = Kit.Animate(ImGui.GetItemID(), hot ? 1f : 0f);
        // The click that ended a capture must not open it again on the same chip, nor may the press that did.
        var usable = pressed && !_capture.EndedThisFrame;
        switch (item.Kind)
        {
            case ControlItemKind.Chip:
                {
                    var waiting = _capture.Active && _capture.Slot == item.Slot && (_capture.Index == item.Index || (item.Index < 0 && _remapper.Count(item.Slot) == 0));
                    draw.AddRectFilled(min, max, Kit.Color(waiting ? Theme.AccentInk : Mix(Theme.Panel, Theme.PanelRaised, t)), chamfer);
                    var pulse = waiting ? 0.6f + 0.4f * MathF.Sin(_ui.Time * 6f) : t;
                    draw.AddRect(min, max, Kit.Color(waiting ? Theme.Accent : Theme.Line, waiting ? pulse : 1f), chamfer);
                    if (!waiting && t > 0.01f)
                        draw.AddRect(min, max, Kit.Color(Theme.Accent, t), chamfer);
                    var size = Kit.Measure(_ui.Mono, Theme.FontSmall, Kit.F($"{item.Text}"));
                    var color = waiting ? Theme.Accent : item.Text == "Unbound" ? Theme.TextMute : dim ? Theme.TextMute : Theme.Text;
                    Kit.Text(draw, _ui.Mono, Theme.FontSmall, min + (item.Size - size) * 0.5f, color, Kit.F($"{item.Text}"));
                    if (usable)
                    {
                        if (_suppressChip && _suppressSlot == item.Slot && _suppressIndex == item.Index)
                            _suppressChip = false;
                        else if (!waiting)
                            BeginCapture(item.Slot, item.Index);
                    }

                    break;
                }
            case ControlItemKind.Remove:
                {
                    var size = Kit.Measure(_ui.SansMedium, Theme.FontSmall, "x"u8);
                    Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, min + (item.Size - size) * 0.5f, Mix(Theme.TextMute, Theme.Bad, t), "x"u8);
                    if (usable)
                        _remapper.Remove(item.Slot, item.Index);
                    break;
                }
            case ControlItemKind.Add:
                {
                    draw.AddRect(min, max, Kit.Color(Mix(Theme.Line, Theme.Accent, t)), chamfer);
                    var size = Kit.Measure(_ui.SansMedium, Theme.FontBody, "+"u8);
                    Kit.Text(draw, _ui.SansMedium, Theme.FontBody, min + (item.Size - size) * 0.5f, Mix(Theme.TextDim, Theme.Accent, t), "+"u8);
                    if (usable)
                        BeginCapture(item.Slot, -1);
                    break;
                }
            case ControlItemKind.Reset:
                {
                    var size = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "Reset"u8);
                    Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + (item.Size - size) * 0.5f, Mix(Theme.TextDim, Theme.Accent, t), "Reset"u8);
                    if (usable)
                    {
                        _remapper.Reset(item.Slot);
                        if (row.Kind == RowKind.Axis)
                            _remapper.Reset(ControlSlot.Of((InputAxis)row.Index, true));
                    }

                    break;
                }
        }
    }

    private static uint Mix(uint from, uint to, float t) => Kit.Mix(from, to, t);
}
