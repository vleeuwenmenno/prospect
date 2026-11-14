using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private static readonly string[] FrameCapLabels = ["Unlimited", "60", "120", "144", "240"];
    private static readonly int[] FrameCapValues = [0, 60, 120, 144, 240];
    private static readonly string[] UiScaleLabels = ["75%", "100%", "125%", "150%", "200%"];
    private static readonly float[] UiScaleValues = [0.75f, 1f, 1.25f, 1.5f, 2f];
    private static readonly string[] SensitivityLabels = ["50%", "75%", "100%", "125%", "150%", "200%"];
    private static readonly float[] SensitivityValues = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];
    private static readonly string[] AutosaveLabels = ["15 s", "30 s", "1 min", "2 min", "5 min"];
    private static readonly int[] AutosaveValues = [15, 30, 60, 120, 300];
    private static readonly string[] AxisLabels = ControlsCatalog.AxisLabels;
    private static readonly string[] ActionLabels = ControlsCatalog.ActionLabels;

    private readonly ImInputString _settingsName = new(ProtocolInfo.MaxNameBytes);
    private readonly string[] _bindingText = new string[(int)InputAction.Count];
    private readonly string[] _axisText = new string[(int)InputAxis.Count];

    private void InitializeSettings()
    {
        for (var i = 0; i < _bindingText.Length; i++)
        {
            var names = new List<string>();
            foreach (var binding in _app.Actions.Bindings((InputAction)i))
                names.Add(binding.DisplayName);
            _bindingText[i] = names.Count > 0 ? string.Join("  /  ", names) : "Unbound";
        }

        for (var i = 0; i < _axisText.Length; i++)
        {
            var names = new List<string>();
            foreach (var binding in _app.Actions.Bindings((InputAxis)i))
                names.Add(binding.DisplayName);
            _axisText[i] = names.Count > 0 ? string.Join("  /  ", names) : "Unbound";
        }
    }

    public void OpenSettings(Screen returnTo)
    {
        _settingsReturn = returnTo;
        _settingsName.SetValue(_app.Settings.PlayerName);
        Show(Screen.Settings);
    }

    private void LeaveSettings()
    {
        _tipResetAsk = _tipResetDone = false;
        _app.Settings.PlayerName = PlayerNames.Sanitize(_settingsName.ToString());
        _app.Settings.Save();
        InitializeJoin();
    }

    private void DrawSettings(float fade)
    {
        BeginRoot("##settings"u8);
        var settings = _app.Settings;
        const float rowWidth = 520;
        // Under the navigation bar at any scale.
        var top = Math.Max(_ui.DisplaySize.Y * 0.08f, HudTop);
        var min = new float2(Kit.Px(96), top + Slide(fade));
        var size = new float2(Kit.Px(rowWidth + Theme.Space6 * 2 + 14), Math.Min(Kit.Px(1240), Math.Min(_ui.DisplaySize.Y * 0.84f, _ui.DisplaySize.Y - top - Kit.Px(Theme.Space3))));
        BeginPanel("##settingspanel"u8, min, size);

        Kit.Caption("CONFIGURATION"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("Settings"u8);
        Kit.Space(Theme.Space5);

        Kit.Caption("VIDEO"u8);
        Kit.Space(Theme.Space2);
        var vsync = settings.VSync;
        if (Kit.Toggle("Vertical sync"u8, ref vsync, rowWidth))
        {
            settings.VSync = vsync;
            _app.ApplyVideoSettings();
        }

        var capIndex = Math.Max(0, Array.IndexOf(FrameCapValues, settings.FrameCap));
        if (Kit.Choice("Frame limit (vsync off)"u8, FrameCapLabels, ref capIndex, rowWidth))
            settings.FrameCap = FrameCapValues[capIndex];

        var fullscreen = settings.Fullscreen;
        if (Kit.Toggle("Fullscreen"u8, ref fullscreen, rowWidth))
        {
            settings.Fullscreen = fullscreen;
            _app.ApplyVideoSettings();
        }

        var scaleIndex = 1;
        for (var i = 0; i < UiScaleValues.Length; i++)
        {
            if (MathF.Abs(UiScaleValues[i] - settings.UiScale) < 0.01f)
                scaleIndex = i;
        }

        if (Kit.Choice("Interface scale"u8, UiScaleLabels, ref scaleIndex, rowWidth))
        {
            settings.UiScale = UiScaleValues[scaleIndex];
            _app.ApplyVideoSettings();
        }

        Kit.Space(Theme.Space5);
        Kit.Caption("STEERING"u8);
        Kit.Space(Theme.Space2);
        var sensitivityIndex = 2;
        for (var i = 0; i < SensitivityValues.Length; i++)
        {
            if (MathF.Abs(SensitivityValues[i] - settings.MouseSensitivity) < 0.01f)
                sensitivityIndex = i;
        }

        if (Kit.Choice("Pointer sensitivity"u8, SensitivityLabels, ref sensitivityIndex, rowWidth))
            settings.MouseSensitivity = SensitivityValues[sensitivityIndex];

        var invert = settings.InvertPitch;
        if (Kit.Toggle("Invert pitch"u8, ref invert, rowWidth))
            settings.InvertPitch = invert;

        Kit.Space(Theme.Space5);
        Kit.Caption("SAVED WORLDS"u8);
        Kit.Space(Theme.Space2);
        var autosaveIndex = 1;
        for (var i = 0; i < AutosaveValues.Length; i++)
        {
            if (AutosaveValues[i] == settings.AutosaveSeconds)
                autosaveIndex = i;
        }

        if (Kit.Choice("Save automatically every"u8, AutosaveLabels, ref autosaveIndex, rowWidth))
            settings.AutosaveSeconds = AutosaveValues[autosaveIndex];
        Kit.Space(Theme.Space1);
        ImGui.Indent(Kit.Px(Theme.Space3));
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "While the world has changed. A world is also saved when you leave it. Takes effect the next time a world is opened."u8, rowWidth - 24);
        ImGui.Unindent(Kit.Px(Theme.Space3));

        Kit.Space(Theme.Space5);
        Kit.Caption("PROFILE"u8);
        Kit.Space(Theme.Space2);
        Kit.TextField("CALLSIGN"u8, "##callsign"u8, _settingsName, 300);

        Kit.Space(Theme.Space5);
        DrawTipSettings(rowWidth);

        Kit.Space(Theme.Space5);
        Kit.Caption("CONTROLS"u8);
        Kit.Space(Theme.Space2);
        BindingRow("Look around", "Pointer", rowWidth);
        BindingRow("Free look (hold)", "Mouse Right", rowWidth);
        BindingRow("Target the ship under the reticle", "Mouse Middle", rowWidth);
        for (var i = 0; i < AxisLabels.Length; i++)
            BindingRow(AxisLabels[i], _axisText[i], rowWidth);
        for (var i = 0; i < ActionLabels.Length; i++)
            BindingRow(ActionLabels[i], _bindingText[i], rowWidth);
        Kit.Space(Theme.Space3);
        DrawGamepadStatus(rowWidth);

        Kit.Space(Theme.Space5);
        // Escape answers an open question first: it is "keep them hidden" before it is "leave".
        var back = Kit.Button("Back"u8, ButtonKind.Secondary, 120);
        if (BackPressed())
        {
            if (_tipResetAsk)
                _tipResetAsk = false;
            else
                back = true;
        }

        if (back)
            Show(_settingsReturn);

        EndPanel();
        DrawHints(back: true);
        EndRoot();
    }

    private void BindingRow(string action, string controls, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(Kit.Px(width), Kit.Px(30));
        var controlsSize = Kit.Measure(_ui.Mono, Theme.FontSmall, controls);
        Kit.Text(draw, _ui.Sans, Theme.FontBody, position + new float2(Kit.Px(Theme.Space3), Kit.Px(5)), Theme.TextDim, action);
        Kit.Text(draw, _ui.Mono, Theme.FontSmall, position + new float2(size.X - controlsSize.X - Kit.Px(Theme.Space3), Kit.Px(7)), Theme.Text, controls);
        draw.AddLine(position + new float2(0, size.Y - 0.5f), position + new float2(size.X, size.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));
        ImGui.Dummy(size);
    }

    /// <summary>Shows whether a gamepad is connected and, live, what its sticks and triggers report.</summary>
    private void DrawGamepadStatus(float width)
    {
        var devices = _app.Devices;
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var size = new float2(Kit.Px(width), Kit.Px(64));
        draw.AddRect(position, position + size, Kit.Color(Theme.Line));

        var dot = position + new float2(Kit.Px(Theme.Space4), Kit.Px(18));
        draw.AddCircleFilled(dot, Kit.Px(4), Kit.Color(devices.PadConnected ? Theme.Good : Theme.TextMute), 16);
        var textPosition = position + new float2(Kit.Px(Theme.Space4 + 14), Kit.Px(9));
        if (!devices.PadConnected)
        {
            Kit.Text(draw, _ui.Sans, Theme.FontBody, textPosition, Theme.TextDim, "No gamepad connected"u8);
            Kit.Text(draw, _ui.Sans, Theme.FontSmall, textPosition + new float2(0, Kit.Px(24)), Theme.TextMute, "Plug one in at any time; it is picked up automatically."u8);
        }
        else
        {
            Kit.Text(draw, _ui.Sans, Theme.FontBody, textPosition, Theme.Text, devices.PadName);
            Kit.Text(
                draw,
                _ui.Mono,
                Theme.FontCaption,
                textPosition + new float2(0, Kit.Px(26)),
                Theme.TextDim,
                Kit.F($"L {devices.PadAxis(GlfwGamepadAxis.LeftX):+0.00;-0.00} {devices.PadAxis(GlfwGamepadAxis.LeftY):+0.00;-0.00}   R {devices.PadAxis(GlfwGamepadAxis.RightX):+0.00;-0.00} {devices.PadAxis(GlfwGamepadAxis.RightY):+0.00;-0.00}   LT {devices.PadAxis(GlfwGamepadAxis.LeftTrigger):0.00}   RT {devices.PadAxis(GlfwGamepadAxis.RightTrigger):0.00}")
            );
        }

        ImGui.Dummy(size);
    }
}
