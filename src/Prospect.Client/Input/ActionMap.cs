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
internal struct AxisBinding
{
    public InputBinding? Negative;
    public InputBinding? Positive;
    public int PadAxis;
    public int PadNegativeAxis;
    public int PadPositiveAxis;
    public bool Invert;

    public readonly string DisplayName => Invert ? $"{SourceName} (inverted)" : SourceName;

    private readonly string SourceName =>
        PadAxis >= 0 ? $"Pad {(GlfwGamepadAxis)PadAxis}"
        : PadNegativeAxis >= 0 ? $"Pad {(GlfwGamepadAxis)PadNegativeAxis} - {(GlfwGamepadAxis)PadPositiveAxis}"
        : $"{Negative?.DisplayName} - {Positive?.DisplayName}";
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
            Source.MouseButton => $"Mouse {(GlfwMouseButton)Code}",
            _ => Code switch
            {
                0 => "Pad A (Cross)",
                1 => "Pad B (Circle)",
                2 => "Pad X (Square)",
                3 => "Pad Y (Triangle)",
                _ => $"Pad {(GlfwGamepadButton)Code}",
            },
        };
}

/// <summary>
/// Maps actions to physical controls. Bindings are data: <c>content/input/bindings.json</c> lists, per action, the
/// controls that trigger it.
/// </summary>
internal sealed class ActionMap
{
    private readonly InputBinding[][] _bindings = new InputBinding[(int)InputAction.Count][];
    private readonly AxisBinding[][] _axes = new AxisBinding[(int)InputAxis.Count][];

    private ActionMap()
    {
        Array.Fill(_bindings, []);
        Array.Fill(_axes, []);
    }

    public ReadOnlySpan<InputBinding> Bindings(InputAction action) => _bindings[(int)action];

    public ReadOnlySpan<AxisBinding> Bindings(InputAxis axis) => _axes[(int)axis];

    /// <summary>True while a control bound to the action is held down.</summary>
    public bool Held(InputDevices devices, InputAction action, bool keyboardAvailable)
    {
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

        return map;
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

    private static bool TryParse(string text, out InputBinding binding)
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
