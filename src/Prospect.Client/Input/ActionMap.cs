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
    Count,
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
            _ => $"Pad {(GlfwGamepadButton)Code}",
        };
}

/// <summary>
/// Maps actions to physical controls. Bindings are data: <c>content/input/bindings.json</c> lists, per action, the
/// controls that trigger it.
/// </summary>
internal sealed class ActionMap
{
    private readonly InputBinding[][] _bindings = new InputBinding[(int)InputAction.Count][];

    private ActionMap()
    {
        Array.Fill(_bindings, []);
    }

    public ReadOnlySpan<InputBinding> Bindings(InputAction action) => _bindings[(int)action];

    /// <summary>True on the frame a control bound to the action went down.</summary>
    public bool Pressed(InputDevices devices, InputAction action, bool keyboardAvailable)
    {
        foreach (var binding in _bindings[(int)action])
        {
            var pressed = binding.Device switch
            {
                InputBinding.Source.Key => keyboardAvailable && devices.KeyPressed((GlfwKey)binding.Code),
                InputBinding.Source.MouseButton => devices.MousePressed((GlfwMouseButton)binding.Code),
                _ => devices.PadPressed((GlfwGamepadButton)binding.Code),
            };
            if (pressed)
                return true;
        }

        return false;
    }

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

        return map;
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
