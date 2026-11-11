using Brutal.GlfwApi;
using Prospect.Client.Input;
using Xunit;

namespace Prospect.Tests;

public sealed class ControlsTests
{
    [Theory]
    [InlineData(GlfwKey.H, false)]
    [InlineData(GlfwKey.F1, true)]
    [InlineData(GlfwKey.Escape, true)]
    public void MenuShortcutsDoNotStealTypingButKeepFunctionKeysAndEscape(GlfwKey key, bool allowedWhileTyping)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $$$"""{"actions":{"ToggleControls":["Key:{{{key}}}"]},"axes":{}}""");
            var map = ActionMap.Load(path);
            var devices = new InputDevices();
            devices.OnKey(key, GlfwKeyAction.Press);
            Assert.True(map.InterfaceShortcutPressed(devices, InputAction.ToggleControls, false, true));
            Assert.Equal(allowedWhileTyping, map.InterfaceShortcutPressed(devices, InputAction.ToggleControls, true, true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SearchClickCannotTriggerRemappedControlsShortcut()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"actions":{"ToggleControls":["Mouse:Left"]},"axes":{}}""");
            var map = ActionMap.Load(path);
            var devices = new InputDevices();
            devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
            Assert.True(map.InterfaceShortcutPressed(devices, InputAction.ToggleControls, false, false));
            Assert.False(map.InterfaceShortcutPressed(devices, InputAction.ToggleControls, false, true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HeldInterfaceClickStaysBlockedInGameplayUntilRelease()
    {
        var devices = new InputDevices { ReadingWorldMouse = true };
        devices.OnKey(GlfwKey.W, GlfwKeyAction.Press);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        devices.SuppressMouse = true;
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        Assert.True(devices.KeyDown(GlfwKey.W));
        devices.SuppressMouse = false;
        Assert.False(devices.MouseDown(GlfwMouseButton.Left));
        devices.ReadingWorldMouse = false;
        Assert.True(devices.MouseDown(GlfwMouseButton.Left));
        devices.ReadingWorldMouse = true;
        devices.BeginFrame();
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Release);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        Assert.True(devices.MousePressed(GlfwMouseButton.Left));
    }

    [Fact]
    public void HelpUsesRemappedButtonsAxesAndSearchableCategories()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"actions":{"ToggleControls":["Key:F2"],"ReleasePointer":["Key:LeftAlt","Key:RightAlt"],"Boost":["Pad:A"]},
                 "axes":{"Forward":[{"axis":"Pad:RightY","invert":true}]}}
                """);
            var map = ActionMap.Load(path);
            var help = new ControlsCatalog(map);
            var controls = Assert.Single(help.Rows, r => r.Action == "Controls / help");
            Assert.True(controls.Matches("f2"));
            Assert.False(controls.Matches("F1"));
            Assert.True(controls.Matches("controls"));
            Assert.True(controls.Matches("general"));
            Assert.Contains(help.Rows, r => r.Matches("RightY") && r.Binding.Contains("inverted"));
            Assert.Contains(help.Rows, r => r.Action == "Boost" && r.Binding == "Pad A (Cross)");
            Assert.Equal(2, map.Bindings(InputAction.ReleasePointer).Length);
            Assert.Contains(help.Rows, r => r.Category == "Pilot" && r.Action.StartsWith("Show cursor") && r.Binding.StartsWith("Hold LeftAlt or RightAlt"));
            Assert.Contains(help.Rows, r => r.Category == "Pilot" && r.Action.StartsWith("Turn the camera") && r.Binding.Contains("right drag"));
            // The command view needs no key held to use the mouse.
            Assert.DoesNotContain(help.Rows, r => r.Category == "Command" && r.Binding.Contains("Hold"));
            Assert.Contains(help.Rows, r => r.Matches("mouse") && r.Matches("pilot"));
            Assert.DoesNotContain(help.Rows, r => r.Matches("no-such-control"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ActionMap Defaults() => ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));

    [Theory]
    [InlineData(GlfwKey.LeftShift)]
    [InlineData(GlfwKey.RightShift)]
    public void ShiftShowsTheCursorAndNeitherBoostsNorMovesVertically(GlfwKey key)
    {
        var map = Defaults();
        var devices = new InputDevices();
        devices.OnKey(key, GlfwKeyAction.Press);
        Assert.True(map.Held(devices, InputAction.ReleasePointer, true));
        Assert.False(map.Held(devices, InputAction.Boost, true));
        Assert.Equal(0f, map.Axis(devices, InputAxis.Vertical, true));
        Assert.Contains(new ControlsCatalog(map).Rows, r => r.Category == "Pilot" && r.Action.StartsWith("Show cursor")
            && r.Binding.StartsWith("Hold LeftShift or RightShift"));
    }

    [Fact]
    public void SpaceBoostsWithoutVerticalThrustAndRAndFMoveUpAndDownWithoutBoost()
    {
        var map = Defaults();
        var devices = new InputDevices();
        devices.OnKey(GlfwKey.Space, GlfwKeyAction.Press);
        Assert.True(map.Held(devices, InputAction.Boost, true));
        Assert.Equal(0f, map.Axis(devices, InputAxis.Vertical, true));
        Assert.False(map.Held(devices, InputAction.ReleasePointer, true));
        devices.OnKey(GlfwKey.Space, GlfwKeyAction.Release);

        devices.BeginFrame();
        devices.OnKey(GlfwKey.R, GlfwKeyAction.Press);
        Assert.Equal(1f, map.Axis(devices, InputAxis.Vertical, true));
        Assert.False(map.Held(devices, InputAction.Boost, true));
        devices.OnKey(GlfwKey.R, GlfwKeyAction.Release);

        devices.BeginFrame();
        devices.OnKey(GlfwKey.F, GlfwKeyAction.Press);
        Assert.Equal(-1f, map.Axis(devices, InputAxis.Vertical, true));
        Assert.False(map.Held(devices, InputAction.Boost, true));
        // F and Space also focus the camera, which only the command view reads.
        Assert.True(map.Pressed(devices, InputAction.Follow, true));
        Assert.Contains(new ControlsCatalog(map).Rows, r => r.Category == "Command" && r.Action.StartsWith("Focus on selection"));

        // Left Ctrl is free for the command view's selection modifiers.
        devices.BeginFrame();
        devices.OnKey(GlfwKey.F, GlfwKeyAction.Release);
        devices.OnKey(GlfwKey.LeftControl, GlfwKeyAction.Press);
        Assert.Equal(0f, map.Axis(devices, InputAxis.Vertical, true));

        // The gamepad keeps its bindings.
        Assert.Contains(map.Bindings(InputAction.Boost).ToArray(), b => b.DisplayName.StartsWith("Pad "));
        Assert.Contains(map.Bindings(InputAxis.Vertical).ToArray(), b => b.DisplayName.Contains("Trigger"));
    }

    [Theory]
    [InlineData("TogglePlayers", GlfwKey.Tab)]
    [InlineData("TogglePlayers", GlfwKey.P)]
    [InlineData("ToggleSocial", GlfwKey.O)]
    [InlineData("ToggleChat", GlfwKey.Enter)]
    public void SocialShortcutsAreBoundButLeaveKeysToAChatLineBeingTyped(string name, GlfwKey key)
    {
        var action = Enum.Parse<InputAction>(name);
        var map = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        var devices = new InputDevices();
        devices.OnKey(key, GlfwKeyAction.Press);
        Assert.True(map.InterfaceShortcutPressed(devices, action, false, false));
        Assert.False(map.InterfaceShortcutPressed(devices, action, true, false));
        var help = new ControlsCatalog(map);
        Assert.Contains(help.Rows, r => r.Category == "Social" && r.Matches(key.ToString()) && r.Action == ControlsCatalog.ActionLabels[(int)action]);
    }

    [Fact]
    public void RemappedPlayerMenuKeyIsTheOneThatOpensItAndTheOneShown()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"actions":{"TogglePlayers":["Key:F6"],"ToggleChat":["Mouse:Middle"]},"axes":{}}""");
            var map = ActionMap.Load(path);
            var devices = new InputDevices();
            devices.OnKey(GlfwKey.Tab, GlfwKeyAction.Press);
            Assert.False(map.InterfaceShortcutPressed(devices, InputAction.TogglePlayers, false, false));
            devices.OnKey(GlfwKey.F6, GlfwKeyAction.Press);
            // A function key still works from a focused chat field, as F1 does.
            Assert.True(map.InterfaceShortcutPressed(devices, InputAction.TogglePlayers, true, false));
            devices.OnMouseButton(GlfwMouseButton.Middle, GlfwButtonAction.Press);
            Assert.True(map.InterfaceShortcutPressed(devices, InputAction.ToggleChat, false, false));
            Assert.False(map.InterfaceShortcutPressed(devices, InputAction.ToggleChat, false, true));
            var help = new ControlsCatalog(map);
            Assert.Contains(help.Rows, r => r.Action == "Players on this server" && r.Binding == "F6");
            Assert.Contains(help.Rows, r => r.Action == "Party (social)" && r.Binding == "Unbound");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
