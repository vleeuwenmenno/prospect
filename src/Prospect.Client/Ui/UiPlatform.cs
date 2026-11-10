using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;

namespace Prospect.Client.Ui;

/// <summary>
/// Feeds Dear ImGui from the game's window and input devices: display size, time, mouse, keyboard, text, gamepad
/// navigation, the mouse cursor shape and the clipboard. Written for this game rather than taken from BRUTAL's
/// general-purpose backend so that a frame costs no managed allocation.
/// </summary>
internal sealed unsafe class UiPlatform : IDisposable
{
    private static readonly ImGuiKey[] KeyMap = BuildKeyMap();
    private static byte* _clipboard;
    private static int _clipboardCapacity;
    private static GlfwWindow? _clipboardWindow;

    private readonly GlfwWindow _window;
    private readonly InputDevices _devices;
    private readonly GlfwCursor _arrowCursor;
    private readonly GlfwCursor _textCursor;
    private readonly GlfwKeyCallback _onKey;
    private readonly GlfwCharCallback _onChar;
    private readonly GlfwMouseButtonCallback _onMouseButton;
    private readonly GlfwCursorPosCallback _onCursorPos;
    private readonly GlfwCursorEnterCallback _onCursorEnter;
    private readonly GlfwScrollCallback _onScroll;
    private readonly GlfwWindowFocusCallback _onFocus;
    private long _lastFrame;
    private bool _textCursorShown;

    public UiPlatform(GlfwWindow window, InputDevices devices)
    {
        _window = window;
        _devices = devices;
        _clipboardWindow = window;
        _arrowCursor = Glfw.CreateCursor(GlfwStandardCursor.Arrow);
        _textCursor = Glfw.CreateCursor(GlfwStandardCursor.IBeam);

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        var platform = ImGui.GetPlatformIO();
        platform.Platform_GetClipboardTextFn = (nint)(delegate* unmanaged[Cdecl]<nint, byte*>)&GetClipboard;
        platform.Platform_SetClipboardTextFn = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, void>)&SetClipboard;

        _onKey = OnKey;
        _onChar = (_, codepoint) =>
        {
            if (!_devices.Muted)
                ImGui.GetIO().AddInputCharacter(codepoint);
        };
        _onMouseButton = (_, button, action, _) =>
        {
            if ((int)button < 5 && !_devices.Muted)
                ImGui.GetIO().AddMouseButtonEvent((int)button, action == GlfwButtonAction.Press);
        };
        _onCursorPos = (_, position) =>
        {
            if (!PointerCaptured && !_devices.Muted)
                ImGui.GetIO().AddMousePosEvent((float)position.X, (float)position.Y);
        };
        _onCursorEnter = (_, entered) =>
        {
            if (!entered)
                ImGui.GetIO().AddMousePosEvent(-float.MaxValue, -float.MaxValue);
        };
        _onScroll = (_, offset) =>
        {
            if (!_devices.Muted)
                ImGui.GetIO().AddMouseWheelEvent((float)offset.X, (float)offset.Y);
        };
        _onFocus = (_, focus) => ImGui.GetIO().AddFocusEvent(focus == GlfwFocusEvent.GainFocus);

        window.OnKey += _onKey;
        window.OnChar += _onChar;
        window.OnMouseButton += _onMouseButton;
        window.OnCursorPos += _onCursorPos;
        window.OnCursorEnter += _onCursorEnter;
        window.OnScroll += _onScroll;
        window.OnFocus += _onFocus;
    }

    /// <summary>
    /// True while the pointer is steering the ship instead of pointing at the interface. The interface then sees
    /// no pointer at all, so nothing under the hidden cursor reacts.
    /// </summary>
    public bool PointerCaptured { get; private set; }

    /// <summary>Hands the pointer to flight (hidden, unbounded, raw motion) or back to the interface.</summary>
    public void SetPointerCaptured(bool captured)
    {
        if (captured == PointerCaptured)
            return;
        PointerCaptured = captured;
        _window.SetInputMode(GlfwInputMode.Cursor, (int)(captured ? GlfwCursorMode.Disabled : GlfwCursorMode.Normal));
        if (Glfw.RawMotionSupported())
            _window.SetInputMode(GlfwInputMode.RawMouseMotion, captured ? 1 : 0);
        if (captured)
            ImGui.GetIO().AddMousePosEvent(-float.MaxValue, -float.MaxValue);
    }

    /// <summary>Updates ImGui's per-frame inputs. Call after window events have been polled.</summary>
    public void NewFrame()
    {
        var io = ImGui.GetIO();
        var size = _window.Size;
        var framebuffer = _window.FramebufferSize;
        io.DisplaySize = new float2(size.X, size.Y);
        if (size.X > 0 && size.Y > 0)
            io.DisplayFramebufferScale = new float2((float)framebuffer.X / size.X, (float)framebuffer.Y / size.Y);

        var now = Stopwatch.GetTimestamp();
        io.DeltaTime = _lastFrame == 0 ? 1f / 60f : Math.Max(1e-5f, (float)((now - _lastFrame) / (double)Stopwatch.Frequency));
        _lastFrame = now;

        UpdateGamepad(io);

        var wantText = ImGui.GetMouseCursor() == ImGuiMouseCursor.TextInput;
        if (wantText != _textCursorShown)
        {
            _textCursorShown = wantText;
            _window.SetCursor(wantText ? _textCursor : _arrowCursor);
        }
    }

    public void Dispose()
    {
        _window.OnKey -= _onKey;
        _window.OnChar -= _onChar;
        _window.OnMouseButton -= _onMouseButton;
        _window.OnCursorPos -= _onCursorPos;
        _window.OnCursorEnter -= _onCursorEnter;
        _window.OnScroll -= _onScroll;
        _window.OnFocus -= _onFocus;
        _clipboardWindow = null;
    }

    private void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        if (action == GlfwKeyAction.Repeat || _devices.Muted)
            return;

        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.Mod_Ctrl, (mods & GlfwModifier.Control) != 0);
        io.AddKeyEvent(ImGuiKey.Mod_Shift, (mods & GlfwModifier.Shift) != 0);
        io.AddKeyEvent(ImGuiKey.Mod_Alt, (mods & GlfwModifier.Alt) != 0);
        io.AddKeyEvent(ImGuiKey.Mod_Super, (mods & GlfwModifier.Super) != 0);

        var index = (int)key;
        if ((uint)index < (uint)KeyMap.Length && KeyMap[index] != ImGuiKey.None)
            io.AddKeyEvent(KeyMap[index], action == GlfwKeyAction.Press);
    }

    private void UpdateGamepad(ImGuiIOPtr io)
    {
        if (!_devices.PadConnected)
        {
            io.BackendFlags &= ~ImGuiBackendFlags.HasGamepad;
            return;
        }

        io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
        var devices = _devices;
        io.AddKeyEvent(ImGuiKey.GamepadFaceDown, devices.PadDown(GlfwGamepadButton.A));
        io.AddKeyEvent(ImGuiKey.GamepadFaceRight, devices.PadDown(GlfwGamepadButton.B));
        io.AddKeyEvent(ImGuiKey.GamepadFaceLeft, devices.PadDown(GlfwGamepadButton.X));
        io.AddKeyEvent(ImGuiKey.GamepadFaceUp, devices.PadDown(GlfwGamepadButton.Y));
        io.AddKeyEvent(ImGuiKey.GamepadDpadLeft, devices.PadDown(GlfwGamepadButton.DpadLeft));
        io.AddKeyEvent(ImGuiKey.GamepadDpadRight, devices.PadDown(GlfwGamepadButton.DpadRight));
        io.AddKeyEvent(ImGuiKey.GamepadDpadUp, devices.PadDown(GlfwGamepadButton.DpadUp));
        io.AddKeyEvent(ImGuiKey.GamepadDpadDown, devices.PadDown(GlfwGamepadButton.DpadDown));
        io.AddKeyEvent(ImGuiKey.GamepadL1, devices.PadDown(GlfwGamepadButton.LeftBumper));
        io.AddKeyEvent(ImGuiKey.GamepadR1, devices.PadDown(GlfwGamepadButton.RightBumper));

        var x = devices.PadAxis(GlfwGamepadAxis.LeftX);
        var y = devices.PadAxis(GlfwGamepadAxis.LeftY);
        io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickLeft, x < -0.3f, Math.Max(0f, -x));
        io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickRight, x > 0.3f, Math.Max(0f, x));
        io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickUp, y < -0.3f, Math.Max(0f, -y));
        io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickDown, y > 0.3f, Math.Max(0f, y));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* GetClipboard(nint context)
    {
        var text = Glfw.Clipboard ?? "";
        var needed = Encoding.UTF8.GetByteCount(text) + 1;
        if (needed > _clipboardCapacity)
        {
            NativeMemory.Free(_clipboard);
            _clipboard = (byte*)NativeMemory.Alloc((nuint)needed);
            _clipboardCapacity = needed;
        }

        var written = Encoding.UTF8.GetBytes(text, new Span<byte>(_clipboard, needed - 1));
        _clipboard[written] = 0;
        return _clipboard;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetClipboard(nint context, byte* text)
    {
        if (_clipboardWindow is not null)
            Glfw.SetClipboard(Marshal.PtrToStringUTF8((nint)text) ?? "");
    }

    private static ImGuiKey[] BuildKeyMap()
    {
        var map = new ImGuiKey[InputDevices.KeyCount];

        void Range(GlfwKey first, ImGuiKey to, int count)
        {
            for (var i = 0; i < count; i++)
                map[(int)first + i] = to + i;
        }

        Range(GlfwKey.A, ImGuiKey.A, 26);
        Range(GlfwKey.Number0, ImGuiKey._0, 10);
        Range(GlfwKey.F1, ImGuiKey.F1, 12);
        Range(GlfwKey.Kp0, ImGuiKey.Keypad0, 10);

        ReadOnlySpan<(GlfwKey From, ImGuiKey To)> singles =
        [
            (GlfwKey.Tab, ImGuiKey.Tab),
            (GlfwKey.Left, ImGuiKey.LeftArrow),
            (GlfwKey.Right, ImGuiKey.RightArrow),
            (GlfwKey.Up, ImGuiKey.UpArrow),
            (GlfwKey.Down, ImGuiKey.DownArrow),
            (GlfwKey.PageUp, ImGuiKey.PageUp),
            (GlfwKey.PageDown, ImGuiKey.PageDown),
            (GlfwKey.Home, ImGuiKey.Home),
            (GlfwKey.End, ImGuiKey.End),
            (GlfwKey.Insert, ImGuiKey.Insert),
            (GlfwKey.Delete, ImGuiKey.Delete),
            (GlfwKey.Backspace, ImGuiKey.Backspace),
            (GlfwKey.Space, ImGuiKey.Space),
            (GlfwKey.Enter, ImGuiKey.Enter),
            (GlfwKey.Escape, ImGuiKey.Escape),
            (GlfwKey.Apostrophe, ImGuiKey.Apostrophe),
            (GlfwKey.Comma, ImGuiKey.Comma),
            (GlfwKey.Minus, ImGuiKey.Minus),
            (GlfwKey.Period, ImGuiKey.Period),
            (GlfwKey.Slash, ImGuiKey.Slash),
            (GlfwKey.Semicolon, ImGuiKey.Semicolon),
            (GlfwKey.Equal, ImGuiKey.Equal),
            (GlfwKey.LeftBracket, ImGuiKey.LeftBracket),
            (GlfwKey.Backslash, ImGuiKey.Backslash),
            (GlfwKey.RightBracket, ImGuiKey.RightBracket),
            (GlfwKey.GraveAccent, ImGuiKey.GraveAccent),
            (GlfwKey.KpDecimal, ImGuiKey.KeypadDecimal),
            (GlfwKey.KpDivide, ImGuiKey.KeypadDivide),
            (GlfwKey.KpMultiply, ImGuiKey.KeypadMultiply),
            (GlfwKey.KpSubtract, ImGuiKey.KeypadSubtract),
            (GlfwKey.KpAdd, ImGuiKey.KeypadAdd),
            (GlfwKey.KpEnter, ImGuiKey.KeypadEnter),
            (GlfwKey.LeftShift, ImGuiKey.LeftShift),
            (GlfwKey.LeftControl, ImGuiKey.LeftCtrl),
            (GlfwKey.LeftAlt, ImGuiKey.LeftAlt),
            (GlfwKey.LeftSuper, ImGuiKey.LeftSuper),
            (GlfwKey.RightShift, ImGuiKey.RightShift),
            (GlfwKey.RightControl, ImGuiKey.RightCtrl),
            (GlfwKey.RightAlt, ImGuiKey.RightAlt),
            (GlfwKey.RightSuper, ImGuiKey.RightSuper),
        ];
        foreach (var (from, to) in singles)
            map[(int)from] = to;
        return map;
    }
}
