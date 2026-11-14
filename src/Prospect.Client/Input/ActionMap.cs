using System.Text.Json;
using Brutal.GlfwApi;
using Prospect.Engine.Diagnostics;

namespace Prospect.Client.Input;

/// <summary>Things the player can do that are bound to a key or button.</summary>
internal enum InputAction
{
    Pause,
    TogglePerformance,
    ToggleFullscreen,
    Screenshot,
    Boost,
    ToggleDampeners,
    ToggleCommand,
    ToggleMap,
    Halt,
    Follow,
    ShipInfo,
    SwitchHull,
    ToggleControls,
    ReleasePointer,
    TogglePlayers,
    ToggleSocial,
    ToggleChat,
    ToggleSectorInfo,
    ToggleEditor,
    MapLayerUp,
    MapLayerDown,
    MapTilt,
    ScanSector,
    Count,
}

/// <summary>Analogue controls: each reads -1..1 from a pair of keys, a stick or a pair of triggers.</summary>
internal enum InputAxis
{
    Strafe,
    Vertical,
    Forward,
    Pitch,
    Yaw,
    Roll,
    Count,
}

/// <summary>One source for an axis: two buttons, a stick axis, or two one-sided axes such as triggers.</summary>
internal record struct AxisBinding
{
    public InputBinding? Negative;
    public InputBinding? Positive;
    public int PadAxis;
    public int PadNegativeAxis;
    public int PadPositiveAxis;
    public bool Invert;

    public readonly string DisplayName => Invert ? $"{SourceName} (inverted)" : SourceName;

    /// <summary>True for a pair of buttons (keyboard keys or the like), the only kind the player can remap.</summary>
    public readonly bool IsPair => PadAxis < 0 && PadNegativeAxis < 0;

    /// <summary>True for a pair that has neither button, left that way by the player.</summary>
    public readonly bool IsEmptyPair => IsPair && Negative is null && Positive is null;

    private readonly string SourceName =>
        PadAxis >= 0 ? $"Pad {(GlfwGamepadAxis)PadAxis}"
        : PadNegativeAxis >= 0 ? $"Pad {(GlfwGamepadAxis)PadNegativeAxis} - {(GlfwGamepadAxis)PadPositiveAxis}"
        : $"{Negative?.DisplayName ?? "unbound"} - {Positive?.DisplayName ?? "unbound"}";
}

/// <summary>One physical control an action is bound to.</summary>
internal readonly record struct InputBinding(InputBinding.Source Device, int Code)
{
    public enum Source : byte
    {
        Key,
        MouseButton,
        PadButton,
    }

    /// <summary>The control's name as written in the bindings file, for example <c>Key:Escape</c>.</summary>
    public string DisplayName =>
        Device switch
        {
            Source.Key => ((GlfwKey)Code).ToString(),
            Source.MouseButton => $"Mouse {MouseName(Code)}",
            _ => Code switch
            {
                0 => "Pad A (Cross)",
                1 => "Pad B (Circle)",
                2 => "Pad X (Square)",
                3 => "Pad Y (Triangle)",
                _ => $"Pad {(GlfwGamepadButton)Code}",
            },
        };

    /// <summary>The three main buttons by their names; the enum also has numbered aliases, and "Number3" is no name for the middle one.</summary>
    private static string MouseName(int code) => code switch
    {
        0 => "Left",
        1 => "Right",
        2 => "Middle",
        _ => ((GlfwMouseButton)code).ToString(),
    };

    /// <summary>The control as the bindings file and the saved overrides write it, for example <c>Pad:LeftThumb</c>.</summary>
    public string FileName =>
        Device switch
        {
            Source.Key => $"Key:{(GlfwKey)Code}",
            Source.MouseButton => $"Mouse:{MouseName(Code)}",
            _ => $"Pad:{(GlfwGamepadButton)Code}",
        };
}

/// <summary>
/// Maps actions to physical controls. Bindings are data: <c>content/input/bindings.json</c> lists, per action, the
/// controls that trigger it. The player's remaps are applied on top (<see cref="ApplyOverrides"/>) and change the
/// map in place, so everything that holds it sees them at once; the defaults stay in a copy for resets.
/// </summary>
internal sealed class ActionMap
{
    private readonly InputBinding[][] _bindings = new InputBinding[(int)InputAction.Count][];
    private readonly AxisBinding[][] _axes = new AxisBinding[(int)InputAxis.Count][];
    private InputBinding[][] _defaultBindings = [];
    private AxisBinding[][] _defaultAxes = [];

    private ActionMap()
    {
        Array.Fill(_bindings, []);
        Array.Fill(_axes, []);
    }

    /// <summary>
    /// While set, every query answers "nothing is pressed": the Controls page sets it while it waits for a control
    /// to bind, so the press it takes reaches neither a shortcut nor the world nor the page's own Escape.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>Counts every change to a binding; a cached key name is stale when this moves.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after a binding changes, so names built from the bindings can be rebuilt.</summary>
    public event Action? Changed;

    private void Touch()
    {
        Version++;
        Changed?.Invoke();
    }

    public ReadOnlySpan<InputBinding> Bindings(InputAction action) => _bindings[(int)action];

    public ReadOnlySpan<AxisBinding> Bindings(InputAxis axis) => _axes[(int)axis];

    /// <summary>True while a control bound to the action is held down.</summary>
    public bool Held(InputDevices devices, InputAction action, bool keyboardAvailable)
    {
        if (Suspended)
            return false;
        foreach (var binding in _bindings[(int)action])
        {
            if (Down(devices, binding, keyboardAvailable))
                return true;
        }

        return false;
    }

    /// <summary>The axis value in -1..1, summed over everything bound to it.</summary>
    public float Axis(InputDevices devices, InputAxis axis, bool keyboardAvailable)
    {
        if (Suspended)
            return 0f;
        var value = 0f;
        foreach (var binding in _axes[(int)axis])
        {
            var part = 0f;
            if (binding.Negative is { } negative && Down(devices, negative, keyboardAvailable))
                part -= 1f;
            if (binding.Positive is { } positive && Down(devices, positive, keyboardAvailable))
                part += 1f;
            if (binding.PadAxis >= 0)
                part += devices.PadAxis((GlfwGamepadAxis)binding.PadAxis);
            if (binding.PadNegativeAxis >= 0)
                part += devices.PadAxis((GlfwGamepadAxis)binding.PadPositiveAxis) - devices.PadAxis((GlfwGamepadAxis)binding.PadNegativeAxis);
            value += binding.Invert ? -part : part;
        }

        return Math.Clamp(value, -1f, 1f);
    }

    private static bool Down(InputDevices devices, InputBinding binding, bool keyboardAvailable) =>
        binding.Device switch
        {
            InputBinding.Source.Key => keyboardAvailable && devices.KeyDown((GlfwKey)binding.Code),
            InputBinding.Source.MouseButton => devices.MouseDown((GlfwMouseButton)binding.Code),
            _ => devices.PadDown((GlfwGamepadButton)binding.Code),
        };

    /// <summary>True on the frame a control bound to the action went down.</summary>
    public bool Pressed(InputDevices devices, InputAction action, bool keyboardAvailable)
    {
        if (Suspended)
            return false;
        foreach (var binding in _bindings[(int)action])
        {
            if (Pressed(devices, binding, keyboardAvailable))
                return true;
        }

        return false;
    }

    /// <summary>Checks a menu shortcut without stealing text entry or clicks owned by the interface.</summary>
    public bool InterfaceShortcutPressed(InputDevices devices, InputAction action, bool typing, bool pointerOverInterface)
    {
        if (Suspended)
            return false;
        foreach (var binding in _bindings[(int)action])
        {
            if (binding.Device == InputBinding.Source.MouseButton && pointerOverInterface)
                continue;
            if (binding.Device == InputBinding.Source.Key && typing
                && (GlfwKey)binding.Code is not (GlfwKey.Escape or >= GlfwKey.F1 and <= GlfwKey.F25))
                continue;
            if (Pressed(devices, binding, true))
                return true;
        }
        return false;
    }

    private static bool Pressed(InputDevices devices, InputBinding binding, bool keyboardAvailable) => binding.Device switch
    {
        InputBinding.Source.Key => keyboardAvailable && devices.KeyPressed((GlfwKey)binding.Code),
        InputBinding.Source.MouseButton => devices.MousePressed((GlfwMouseButton)binding.Code),
        _ => devices.PadPressed((GlfwGamepadButton)binding.Code),
    };

    public static ActionMap Load(string path)
    {
        var map = new ActionMap();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty("actions"u8, out var actions))
            throw new InvalidDataException($"{path}: missing \"actions\"");

        foreach (var entry in actions.EnumerateObject())
        {
            if (!Enum.TryParse<InputAction>(entry.Name, out var action) || action == InputAction.Count)
            {
                Logs.App.Warning($"{path}: unknown action \"{entry.Name}\" ignored");
                continue;
            }

            var list = new List<InputBinding>();
            foreach (var item in entry.Value.EnumerateArray())
            {
                var text = item.GetString() ?? "";
                if (TryParse(text, out var binding))
                    list.Add(binding);
                else
                    Logs.App.Warning($"{path}: unknown control \"{text}\" for {entry.Name} ignored");
            }

            map._bindings[(int)action] = [.. list];
        }

        if (!document.RootElement.TryGetProperty("axes"u8, out var axes))
            throw new InvalidDataException($"{path}: missing \"axes\"");

        foreach (var entry in axes.EnumerateObject())
        {
            if (!Enum.TryParse<InputAxis>(entry.Name, out var axis) || axis == InputAxis.Count)
            {
                Logs.App.Warning($"{path}: unknown axis \"{entry.Name}\" ignored");
                continue;
            }

            var list = new List<AxisBinding>();
            foreach (var item in entry.Value.EnumerateArray())
            {
                if (TryParseAxis(item, out var binding))
                    list.Add(binding);
                else
                    Logs.App.Warning($"{path}: unusable binding for axis {entry.Name} ignored: {item.GetRawText()}");
            }

            map._axes[(int)axis] = [.. list];
        }

        map._defaultBindings = [.. map._bindings.Select(b => b.ToArray())];
        map._defaultAxes = [.. map._axes.Select(a => a.ToArray())];
        return map;
    }

    /// <summary>The bindings the content file gives the action, whatever the player changed.</summary>
    public ReadOnlySpan<InputBinding> DefaultBindings(InputAction action) => _defaultBindings[(int)action];

    /// <summary>True when the action is bound as the content file says.</summary>
    public bool IsDefault(InputAction action) => _bindings[(int)action].AsSpan().SequenceEqual(_defaultBindings[(int)action]);

    /// <summary>Replaces what an action is bound to. At most <see cref="MaxBindings"/> are kept; duplicates collapse.</summary>
    public void SetBindings(InputAction action, ReadOnlySpan<InputBinding> bindings)
    {
        var list = new List<InputBinding>(Math.Min(bindings.Length, MaxBindings));
        foreach (var binding in bindings)
            if (list.Count < MaxBindings && !list.Contains(binding))
                list.Add(binding);
        if (_bindings[(int)action].AsSpan().SequenceEqual(CollectionsMarshalSpan(list)))
            return;
        _bindings[(int)action] = [.. list];
        Touch();
    }

    private static ReadOnlySpan<InputBinding> CollectionsMarshalSpan(List<InputBinding> list) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

    /// <summary>The most controls one action holds: a hand-edited file cannot give it more.</summary>
    public const int MaxBindings = 4;

    /// <summary>
    /// One half of an axis's first pair of buttons: the minus key or the plus key. Null when the player cleared it or
    /// the axis has no pair. Sticks, triggers and a second pair are not remappable and are left as they are.
    /// </summary>
    public InputBinding? AxisKey(InputAxis axis, bool positive) => AxisKey(_axes[(int)axis], positive);

    /// <summary>The same half as the content file gives it.</summary>
    public InputBinding? DefaultAxisKey(InputAxis axis, bool positive) => AxisKey(_defaultAxes[(int)axis], positive);

    private static InputBinding? AxisKey(AxisBinding[] list, bool positive)
    {
        foreach (var binding in list)
            if (binding.IsPair)
                return positive ? binding.Positive : binding.Negative;
        return null;
    }

    /// <summary>Binds one half of an axis's first pair of buttons, or clears it with null; makes the pair if there is none.</summary>
    public void SetAxisKey(InputAxis axis, bool positive, InputBinding? value)
    {
        var list = _axes[(int)axis];
        var index = Array.FindIndex(list, binding => binding.IsPair);
        if (index < 0)
        {
            if (value is null)
                return;
            list = [.. list, new AxisBinding { PadAxis = -1, PadNegativeAxis = -1, PadPositiveAxis = -1 }];
            index = list.Length - 1;
            _axes[(int)axis] = list;
        }
        else if ((positive ? list[index].Positive : list[index].Negative) == value)
            return;

        var pair = list[index];
        if (positive)
            pair.Positive = value;
        else
            pair.Negative = value;
        list[index] = pair;
        Touch();
    }

    /// <summary>
    /// Applies the player's saved changes over the content file's bindings: an action by its name, an axis's keys as
    /// <c>Strafe.Negative</c> and <c>Strafe.Positive</c>, each a list of controls written as in the bindings file
    /// (empty means the player cleared it). An unknown name or an unreadable control is skipped and logged once.
    /// </summary>
    public void ApplyOverrides(IReadOnlyDictionary<string, List<string>>? overrides)
    {
        if (overrides is null)
            return;
        foreach (var (name, values) in overrides)
        {
            var list = new List<InputBinding>();
            var readable = values is not null;
            foreach (var text in values ?? [])
            {
                if (TryParse(text, out var binding))
                    list.Add(binding);
                else
                {
                    readable = false;
                    Logs.App.Warning($"Saved control \"{text}\" for {name} ignored");
                }
            }

            if (!readable)
                continue;
            if (Enum.TryParse<InputAction>(name, out var action) && action != InputAction.Count)
            {
                SetBindings(action, [.. list]);
                continue;
            }

            var dot = name.IndexOf('.');
            if (dot > 0 && Enum.TryParse<InputAxis>(name.AsSpan(0, dot), out var axis) && axis != InputAxis.Count
                && name.AsSpan(dot + 1) is "Negative" or "Positive" && list.Count <= 1)
                SetAxisKey(axis, name.AsSpan(dot + 1) is "Positive", list.Count == 0 ? null : list[0]);
            else
                Logs.App.Warning($"Saved control \"{name}\" is not one this version knows; kept, not applied");
        }
    }

    /// <summary>Puts every action and axis back as the content file has them.</summary>
    public void ResetAll()
    {
        var changed = false;
        for (var i = 0; i < _bindings.Length; i++)
            if (!_bindings[i].AsSpan().SequenceEqual(_defaultBindings[i]))
            {
                _bindings[i] = [.. _defaultBindings[i]];
                changed = true;
            }

        for (var i = 0; i < _axes.Length; i++)
            if (!_axes[i].AsSpan().SequenceEqual(_defaultAxes[i]))
            {
                _axes[i] = [.. _defaultAxes[i]];
                changed = true;
            }

        if (changed)
            Touch();
    }

    private static bool TryParseAxis(JsonElement item, out AxisBinding binding)
    {
        binding = new AxisBinding { PadAxis = -1, PadNegativeAxis = -1, PadPositiveAxis = -1 };
        binding.Invert = item.TryGetProperty("invert"u8, out var invert) && invert.GetBoolean();

        if (item.TryGetProperty("axis"u8, out var stick))
            return TryParsePadAxis(stick.GetString(), out binding.PadAxis);

        if (item.TryGetProperty("negativeAxis"u8, out var low) && item.TryGetProperty("positiveAxis"u8, out var high))
            return TryParsePadAxis(low.GetString(), out binding.PadNegativeAxis) && TryParsePadAxis(high.GetString(), out binding.PadPositiveAxis);

        if (
            item.TryGetProperty("negative"u8, out var negative)
            && item.TryGetProperty("positive"u8, out var positive)
            && TryParse(negative.GetString() ?? "", out var negativeBinding)
            && TryParse(positive.GetString() ?? "", out var positiveBinding)
        )
        {
            binding.Negative = negativeBinding;
            binding.Positive = positiveBinding;
            return true;
        }

        return false;
    }

    private static bool TryParsePadAxis(string? text, out int axis)
    {
        axis = -1;
        if (text is null || !text.StartsWith("Pad:", StringComparison.Ordinal))
            return false;
        if (!Enum.TryParse<GlfwGamepadAxis>(text.AsSpan(4), out var parsed) || (uint)parsed >= InputDevices.PadAxisCount)
            return false;
        axis = (int)parsed;
        return true;
    }

    /// <summary>Reads a control written as the bindings file writes it, such as <c>Key:Escape</c>; false when it is not one.</summary>
    internal static bool TryParse(string text, out InputBinding binding)
    {
        binding = default;
        var separator = text.IndexOf(':');
        if (separator < 0)
            return false;

        var name = text.AsSpan(separator + 1);
        switch (text.AsSpan(0, separator))
        {
            case "Key" when Enum.TryParse<GlfwKey>(name, out var key) && (uint)key < InputDevices.KeyCount:
                binding = new InputBinding(InputBinding.Source.Key, (int)key);
                return true;
            case "Mouse"
                when Enum.TryParse<GlfwMouseButton>(name, out var button) && (uint)button < InputDevices.MouseButtonCount:
                binding = new InputBinding(InputBinding.Source.MouseButton, (int)button);
                return true;
            case "Pad"
                when Enum.TryParse<GlfwGamepadButton>(name, out var pad) && (uint)pad < InputDevices.PadButtonCount:
                binding = new InputBinding(InputBinding.Source.PadButton, (int)pad);
                return true;
            default:
                return false;
        }
    }
}
