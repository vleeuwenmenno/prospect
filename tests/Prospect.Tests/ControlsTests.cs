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
            Assert.Contains(help.Rows, r => r.Matches("mouse") && r.Matches("pilot"));
            Assert.DoesNotContain(help.Rows, r => r.Matches("no-such-control"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
