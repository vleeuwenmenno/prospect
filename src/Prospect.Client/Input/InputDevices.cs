using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Prospect.Client.Input;

/// <summary>
/// The raw state of keyboard, mouse and one gamepad for the current frame, including which buttons changed since
/// the previous frame. Filled from GLFW callbacks during event polling.
/// </summary>
internal sealed unsafe class InputDevices
{
    public const int KeyCount = 512;
    public const int MouseButtonCount = 8;
    public const int PadButtonCount = 15;
    public const int PadAxisCount = 6;
    private const float StickDeadzone = 0.12f;
    private const int MaxJoysticks = 16;

    private readonly bool[] _keys = new bool[KeyCount];
    private readonly bool[] _keysPressed = new bool[KeyCount];
    private readonly bool[] _mouse = new bool[MouseButtonCount];
    private readonly bool[] _mouseBlocked = new bool[MouseButtonCount];
    private readonly bool[] _mousePressed = new bool[MouseButtonCount];
    private readonly bool[] _pad = new bool[PadButtonCount];
    private readonly bool[] _padPressed = new bool[PadButtonCount];
    private readonly float[] _padAxes = new float[PadAxisCount];
    private double2 _lastCursor;
    private bool _hasCursor;
    private int _padId = -1;

    internal InputDevices() { }

    public InputDevices(GlfwWindow window) : this()
    {
        window.OnKey += (_, key, _, action, _) => OnKey(key, action);
        window.OnMouseButton += (_, button, action, _) => OnMouseButton(button, action);
        window.OnCursorPos += (_, position) => OnCursorPos(position);
        window.OnScroll += (_, offset) => OnScroll((float)offset.Y);
        window.OnFocus += (_, focus) =>
        {
            // Keys released while another window had focus never send a release; drop everything held.
            if (focus == GlfwFocusEvent.GainFocus)
                return;
            Array.Clear(_keys);
            Array.Clear(_mouse);
            Array.Clear(_mouseBlocked);
            ResetPointer();
        };
    }

    internal void OnKey(GlfwKey key, GlfwKeyAction action)
    {
        var index = (int)key;
        if (Muted || (uint)index >= KeyCount || action == GlfwKeyAction.Repeat)
            return;
        var down = action == GlfwKeyAction.Press;
        if (down && !_keys[index])
            _keysPressed[index] = true;
        _keys[index] = down;
    }

    internal void OnMouseButton(GlfwMouseButton button, GlfwButtonAction action)
    {
        var index = (int)button;
        if (Muted || (uint)index >= MouseButtonCount)
            return;
        var down = action == GlfwButtonAction.Press;
        if (down && !_mouse[index])
        {
            // A new press starts unmasked; a mask belongs to one press and ends with it.
            _mousePressed[index] = true;
            _mouseBlocked[index] = false;
            if (button == GlfwMouseButton.Right)
                RightPressPosition = MousePosition;
        }

        _mouse[index] = down;
        if (!down)
            _mouseBlocked[index] = false;
    }

    internal void OnCursorPos(double2 position)
    {
        if (Muted)
            return;
        if (_hasCursor)
            MouseDelta += new float2((float)(position.X - _lastCursor.X), (float)(position.Y - _lastCursor.Y));
        _lastCursor = position;
        _hasCursor = true;
        MousePosition = new float2((float)position.X, (float)position.Y);
    }

    internal void OnScroll(float offset) => Scroll += Muted ? 0f : offset;

    /// <summary>
    /// Takes this frame's mouse from the world when the pointer changes owner: every button that is down or went
    /// down since the last poll stays masked for the world until it is released, and the wheel and motion of the
    /// poll are dropped. Events in one poll arrive before the change is decided, so they cannot be given to the
    /// right owner; dropping them is the one safe answer.
    /// </summary>
    public void QuarantineMouse()
    {
        for (var i = 0; i < MouseButtonCount; i++)
            _mouseBlocked[i] |= _mouse[i] || _mousePressed[i];
        // Interface shortcuts also read these edges, before the world's temporary mask is enabled.
        Array.Clear(_mousePressed);
        _scroll = 0;
        ResetPointer();
    }

    /// <summary>
    /// When set, every device is ignored. Scripted runs (benchmark, timed screenshot) set it so that whoever is
    /// using the machine cannot steer them, and so they never take the pointer.
    /// </summary>
    public bool Muted { get; set; }

    public float2 MousePosition { get; private set; }
    private float2 _mouseDelta;
    public float2 MouseDelta { get => SuppressMouse ? default : _mouseDelta; private set => _mouseDelta = value; }
    private float _scroll;
    public float Scroll { get => SuppressMouse ? 0 : _scroll; private set => _scroll = value; }

    /// <summary>Temporarily masks mouse input while the world updates behind interface interaction.</summary>
    public bool ReadingWorldMouse { get; set; }

    private bool _suppressMouse;
    /// <summary>Masks mouse motion and buttons for gameplay; held UI clicks stay blocked until released.</summary>
    public bool SuppressMouse
    {
        get => _suppressMouse;
        set
        {
            _suppressMouse = value;
            if (value)
                for (var i = 0; i < MouseButtonCount; i++)
                    _mouseBlocked[i] |= _mouse[i];
        }
    }

    /// <summary>
    /// Set by the pointer router while the pilot's camera drag holds (the release key shows the pointer and the
    /// right button went down over the world). Only <see cref="CameraDelta"/> and <see cref="CameraScroll"/> read
    /// through <see cref="SuppressMouse"/> for it; buttons and bound actions stay masked.
    /// </summary>
    public bool CameraDrag { get; set; }

    /// <summary>Mouse motion for the pilot camera: <see cref="MouseDelta"/>, or the motion of a camera drag.</summary>
    public float2 CameraDelta => CameraDrag ? _mouseDelta : MouseDelta;

    /// <summary>The wheel for the pilot camera: <see cref="Scroll"/>, or the wheel during a camera drag.</summary>
    public float CameraScroll => CameraDrag ? _scroll : Scroll;

    /// <summary>True while the button is physically down, whatever masks the world's read of it.</summary>
    public bool RawMouseDown(GlfwMouseButton button) => _mouse[(int)button];

    /// <summary>Where the latest right press began, before any later motion in the same poll.</summary>
    public float2 RightPressPosition { get; private set; }

    /// <summary>True when the button went down since the last poll and no change of owner has masked that press.</summary>
    public bool FreshPress(GlfwMouseButton button) => _mousePressed[(int)button] && !_mouseBlocked[(int)button];

    /// <summary>
    /// The first control that went down since the last poll, for the Controls page to bind: keyboard keys first, then
    /// mouse buttons, then gamepad buttons. Reads the raw edges, whatever masks the world's read of them.
    /// </summary>
    public bool TryFirstPress(out InputBinding binding)
    {
        for (var i = 0; i < KeyCount; i++)
        {
            if (_keysPressed[i])
            {
                binding = new InputBinding(InputBinding.Source.Key, i);
                return true;
            }
        }

        for (var i = 0; i < MouseButtonCount; i++)
        {
            if (_mousePressed[i])
            {
                binding = new InputBinding(InputBinding.Source.MouseButton, i);
                return true;
            }
        }

        for (var i = 0; i < PadButtonCount; i++)
        {
            if (_padPressed[i])
            {
                binding = new InputBinding(InputBinding.Source.PadButton, i);
                return true;
            }
        }

        binding = default;
        return false;
    }

    public bool PadConnected => _padId >= 0;
    public string PadName { get; private set; } = "";

    public bool KeyDown(GlfwKey key) => _keys[(int)key];

    public bool KeyPressed(GlfwKey key) => _keysPressed[(int)key];

    public bool MouseDown(GlfwMouseButton button) => !SuppressMouse && (!ReadingWorldMouse || !_mouseBlocked[(int)button]) && _mouse[(int)button];

    public bool MousePressed(GlfwMouseButton button) => !SuppressMouse && (!ReadingWorldMouse || !_mouseBlocked[(int)button]) && _mousePressed[(int)button];

    public bool PadDown(GlfwGamepadButton button) => _pad[(int)button];

    public bool PadPressed(GlfwGamepadButton button) => _padPressed[(int)button];

    /// <summary>A stick axis in -1..1 with the deadzone removed, or a trigger in 0..1.</summary>
    public float PadAxis(GlfwGamepadAxis axis) => _padAxes[(int)axis];

    /// <summary>
    /// Sets where the pointer is, without motion, when it is shown again: the platform may put it back where it
    /// was before it was hidden without sending a motion event, and the world must not act on a stale position.
    /// </summary>
    public void PlacePointer(float2 position)
    {
        if (Muted)
            return;
        MousePosition = position;
        _hasCursor = false;
        _mouseDelta = default;
    }

    /// <summary>
    /// Forgets the last pointer position, so the jump the pointer makes when it is captured or released does not
    /// register as movement.
    /// </summary>
    public void ResetPointer()
    {
        _hasCursor = false;
        MouseDelta = default;
    }

    /// <summary>Clears per-frame edges and deltas. Call before polling window events.</summary>
    public void BeginFrame()
    {
        Array.Clear(_keysPressed);
        Array.Clear(_mousePressed);
        Array.Clear(_padPressed);
        MouseDelta = default;
        Scroll = 0;
    }

    /// <summary>Samples the gamepad. Call after polling window events.</summary>
    public void PollGamepad()
    {
        if (_padId < 0 || !Glfw.JoystickIsGamepad(_padId))
        {
            var previous = _padId;
            _padId = -1;
            for (var id = 0; id < MaxJoysticks; id++)
            {
                if (!Glfw.JoystickIsGamepad(id))
                    continue;
                _padId = id;
                break;
            }

            if (_padId != previous)
                PadName = _padId >= 0 ? Glfw.GetGamepadName(_padId) ?? "Gamepad" : "";
        }

        if (Muted || _padId < 0 || !Glfw.GetGamepadState(_padId, out var state))
        {
            Array.Clear(_pad);
            Array.Clear(_padAxes);
            return;
        }

        for (var i = 0; i < PadButtonCount; i++)
        {
            var down = state.Buttons[i] != 0;
            if (down && !_pad[i])
                _padPressed[i] = true;
            _pad[i] = down;
        }

        for (var i = 0; i < PadAxisCount; i++)
        {
            var value = state.Axes[i];
            if (i >= (int)GlfwGamepadAxis.LeftTrigger)
            {
                // Triggers rest at -1 and reach +1 fully pressed.
                _padAxes[i] = Math.Clamp((value + 1f) * 0.5f, 0f, 1f);
                continue;
            }

            var magnitude = MathF.Abs(value);
            _padAxes[i] =
                magnitude <= StickDeadzone
                    ? 0f
                    : MathF.Sign(value) * Math.Min(1f, (magnitude - StickDeadzone) / (1f - StickDeadzone));
        }
    }
}
