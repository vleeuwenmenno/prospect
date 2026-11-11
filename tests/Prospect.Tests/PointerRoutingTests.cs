using Brutal.GlfwApi;
using Prospect.Client.Input;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The pointer's owner across frames, driven through the real devices, the real bindings and the router the frame
/// loop uses: what shows the cursor, what hides it again, and which clicks reach the world.
/// </summary>
public sealed class PointerRoutingTests
{
    private sealed class Capture : IInterfacePointer
    {
        public readonly List<bool> Calls = [];
        public void SetPointerCaptured(bool captured) => Calls.Add(captured);
        public void NewFrame() { }
        public bool PointerOverInterface { get; set; }
    }

    private static ActionMap Bindings() => ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));

    private static PointerFrame Pilot(bool surfaceOpen = false, bool focused = true) =>
        new() { InSpace = true, Focused = focused, SurfaceOpen = surfaceOpen };

    [Fact]
    public void FramesThatOnlyBlockWorldInputKeepThePointerHidden()
    {
        var devices = new InputDevices();
        var capture = new Capture();
        var router = new PointerRouter(devices, Bindings(), capture);
        router.Update(Pilot());
        Assert.True(router.Captured);

        // The frame Escape is pressed: world input is off (the press is consumed) but nothing is open yet, as on
        // the frame after a window closes while the interface still owns the keyboard. The pointer stays hidden.
        devices.OnKey(GlfwKey.Escape, GlfwKeyAction.Press);
        router.Update(Pilot());
        router.BeginWorldMouse(worldInput: false);
        router.EndWorldMouse();
        Assert.True(router.Captured);

        // The menu opened while the interface was drawn: shown in that same frame, and kept while it is open.
        router.SurfaceOpened();
        devices.BeginFrame();
        router.Update(Pilot(surfaceOpen: true));
        Assert.False(router.Captured);

        // Closed: hidden again on the next frame.
        devices.BeginFrame();
        router.Update(Pilot());
        Assert.True(router.Captured);
        Assert.Equal([true, false, true], capture.Calls);
    }

    [Fact]
    public void ShiftShowsThePointerKeepsKeyboardFlightAndAHeldClickNeverReachesTheWorld()
    {
        var devices = new InputDevices();
        var actions = Bindings();
        var router = new PointerRouter(devices, actions, new Capture());
        router.Update(Pilot());

        devices.OnKey(GlfwKey.LeftShift, GlfwKeyAction.Press);
        devices.OnKey(GlfwKey.W, GlfwKeyAction.Press);
        router.Update(Pilot());
        Assert.False(router.Captured);

        // Clicking a quick button while Shift shows the pointer: the world sees no click, the keyboard still flies.
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        Assert.Equal(1f, actions.Axis(devices, InputAxis.Forward, true));
        router.EndWorldMouse();

        // Shift is let go with the button still down: hidden again at once, and the held button stays masked.
        devices.BeginFrame();
        devices.OnKey(GlfwKey.LeftShift, GlfwKeyAction.Release);
        router.Update(Pilot());
        Assert.True(router.Captured);
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MouseDown(GlfwMouseButton.Left));
        router.EndWorldMouse();

        // A fresh press after letting go is the world's again.
        devices.BeginFrame();
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Release);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        router.Update(Pilot());
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MousePressed(GlfwMouseButton.Left));
        router.EndWorldMouse();
    }

    [Fact]
    public void RightShiftIsAlsoBoundAndARemappedReleaseKeyIsHonoured()
    {
        var devices = new InputDevices();
        var router = new PointerRouter(devices, Bindings(), new Capture());
        devices.OnKey(GlfwKey.RightShift, GlfwKeyAction.Press);
        router.Update(Pilot());
        Assert.False(router.Captured);

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"actions":{"ReleasePointer":["Key:CapsLock"]},"axes":{}}""");
            var remapped = new PointerRouter(devices, ActionMap.Load(path), new Capture());
            remapped.Update(Pilot());
            Assert.True(remapped.Captured);
            devices.OnKey(GlfwKey.CapsLock, GlfwKeyAction.Press);
            remapped.Update(Pilot());
            Assert.False(remapped.Captured);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static PointerFrame Command(bool surfaceOpen = false, bool focused = true) =>
        new() { InSpace = true, Focused = focused, SurfaceOpen = surfaceOpen, CommandView = true };

    [Fact]
    public void CommandViewShowsThePointerAndGivesTheWorldTheMouseWithoutShift()
    {
        var devices = new InputDevices();
        var actions = Bindings();
        var capture = new Capture();
        var router = new PointerRouter(devices, actions, capture);
        router.Update(Command());
        Assert.False(router.Captured);

        // Shown: clicks, the wheel and motion reach the world, and the keyboard still pans.
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        devices.OnScroll(2);
        devices.OnKey(GlfwKey.D, GlfwKeyAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MousePressed(GlfwMouseButton.Left));
        Assert.Equal(2f, devices.Scroll);
        Assert.Equal(1f, actions.Axis(devices, InputAxis.Strafe, true));
        router.EndWorldMouse();

        // Over the fleet list or a quick button the same click is the interface's, not the world's.
        devices.BeginFrame();
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Release);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        capture.PointerOverInterface = true;
        router.Update(Command());
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        router.EndWorldMouse();
        // Never captured at all, so never released either.
        Assert.Empty(capture.Calls);
    }

    [Fact]
    public void ShiftPressedOrReleasedMidDragInTheCommandViewKeepsTheDrag()
    {
        var devices = new InputDevices();
        var capture = new Capture();
        var router = new PointerRouter(devices, Bindings(), capture);
        router.Update(Command());
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        foreach (var action in new[] { GlfwKeyAction.Press, GlfwKeyAction.Release })
        {
            devices.BeginFrame();
            devices.OnKey(GlfwKey.LeftShift, action);
            router.Update(Command());
            Assert.False(router.Captured);
            router.BeginWorldMouse(worldInput: true);
            Assert.True(devices.MouseDown(GlfwMouseButton.Right));
            router.EndWorldMouse();
        }

        // Never captured at all, so never released either.
        Assert.Empty(capture.Calls);
    }

    [Fact]
    public void SwitchingFromPilotToCommandShowsThePointerAndMasksAHeldFreeLook()
    {
        var devices = new InputDevices();
        var capture = new Capture();
        var router = new PointerRouter(devices, Bindings(), capture);
        router.Update(Pilot());
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        // The view changed inside the world's update; the frame loop refreshes capture straight after.
        router.RefreshCapture(Command());
        Assert.False(router.Captured);
        devices.BeginFrame();
        router.Update(Command());
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        // A fresh press after letting go is the command view's.
        devices.BeginFrame();
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Release);
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Press);
        router.Update(Command());
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        // Back to the pilot view: captured again.
        router.RefreshCapture(Pilot());
        Assert.True(router.Captured);
        Assert.Equal([true, false, true], capture.Calls);
    }

    [Fact]
    public void ShiftRightDragInThePilotViewGivesOnlyTheCameraItsMotion()
    {
        var devices = new InputDevices();
        var actions = Bindings();
        var capture = new Capture();
        var router = new PointerRouter(devices, actions, capture);
        router.Update(Pilot());
        devices.BeginFrame();
        devices.OnKey(GlfwKey.LeftShift, GlfwKeyAction.Press);
        router.Update(Pilot());
        Assert.False(router.Captured);

        devices.BeginFrame();
        devices.OnCursorPos(new Brutal.Numerics.double2(100, 100));
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Press);
        devices.OnCursorPos(new Brutal.Numerics.double2(130, 90));
        router.Update(Pilot());
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.CameraDrag);
        Assert.Equal(new Brutal.Numerics.float2(30, -10), devices.CameraDelta);
        // Buttons, the steering motion and bound actions stay masked.
        Assert.False(devices.MouseDown(GlfwMouseButton.Right));
        Assert.Equal(default, devices.MouseDelta);
        router.EndWorldMouse();
        Assert.False(devices.CameraDrag);

        // With world input off (a shortcut consumed the frame) nothing turns.
        devices.BeginFrame();
        router.Update(Pilot());
        router.BeginWorldMouse(worldInput: false);
        Assert.False(devices.CameraDrag);
        router.EndWorldMouse();
    }

    [Fact]
    public void OpenSurfacesFocusLossAndMutedRunsShowThePointerWithoutGivingTheWorldTheMouse()
    {
        var devices = new InputDevices();
        var router = new PointerRouter(devices, Bindings(), new Capture());
        router.Update(Pilot());
        Assert.True(router.Captured);

        // Chat or a panel open over the command view: shown, and the world sees no click even with Shift.
        devices.OnKey(GlfwKey.LeftShift, GlfwKeyAction.Press);
        router.Update(Command(surfaceOpen: true));
        Assert.False(router.Captured);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: false);
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        router.EndWorldMouse();
        devices.OnKey(GlfwKey.LeftShift, GlfwKeyAction.Release);

        router.Update(Pilot());
        Assert.True(router.Captured);
        router.Update(Pilot(focused: false));
        Assert.False(router.Captured);
        router.Update(Command(focused: false));
        Assert.False(router.Captured);

        // Out of space (connecting, the menu screens) the pointer is always the interface's.
        router.Update(new PointerFrame { Focused = true });
        Assert.False(router.Captured);

        // A capture or benchmark run never takes the pointer from whoever is at the machine.
        var muted = new InputDevices { Muted = true };
        var scripted = new PointerRouter(muted, Bindings(), new Capture());
        scripted.Update(Pilot());
        Assert.False(scripted.Captured);
        scripted.Update(Command());
        Assert.False(scripted.Captured);
    }
}
