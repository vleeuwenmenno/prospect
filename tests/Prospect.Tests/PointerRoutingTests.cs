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
    public void AltShowsThePointerKeepsKeyboardFlightAndAHeldClickNeverReachesTheWorld()
    {
        var devices = new InputDevices();
        var actions = Bindings();
        var router = new PointerRouter(devices, actions, new Capture());
        router.Update(Pilot());

        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Press);
        devices.OnKey(GlfwKey.W, GlfwKeyAction.Press);
        router.Update(Pilot());
        Assert.False(router.Captured);

        // Clicking a quick button while Alt shows the pointer: the world sees no click, the keyboard still flies.
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        Assert.Equal(1f, actions.Axis(devices, InputAxis.Forward, true));
        router.EndWorldMouse();

        // Alt is let go with the button still down: hidden again at once, and the held button stays masked.
        devices.BeginFrame();
        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Release);
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
    public void RightAltIsAlsoBoundAndARemappedReleaseKeyIsHonoured()
    {
        var devices = new InputDevices();
        var router = new PointerRouter(devices, Bindings(), new Capture());
        devices.OnKey(GlfwKey.RightAlt, GlfwKeyAction.Press);
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
    public void CommandViewHidesThePointerAndGivesTheWorldNoMouseUntilAltShowsIt()
    {
        var devices = new InputDevices();
        var actions = Bindings();
        var capture = new Capture();
        var router = new PointerRouter(devices, actions, capture);
        router.Update(Command());
        Assert.True(router.Captured);

        // Hidden: no click, drag, wheel or motion reaches the world, but the keyboard still pans.
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        devices.OnKey(GlfwKey.D, GlfwKeyAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        Assert.Equal(0f, devices.Scroll);
        Assert.Equal(default, devices.MouseDelta);
        Assert.Equal(1f, actions.Axis(devices, InputAxis.Strafe, true));
        router.EndWorldMouse();

        // Alt shows the pointer where the platform puts it, and the world may now select there. The press made
        // while it was hidden stays masked until it is let go.
        devices.BeginFrame();
        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Press);
        router.Update(Command());
        Assert.False(router.Captured);
        devices.PlacePointer(new Brutal.Numerics.float2(300, 200));
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MouseDown(GlfwMouseButton.Left));
        router.EndWorldMouse();

        devices.BeginFrame();
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Release);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        router.Update(Command());
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MousePressed(GlfwMouseButton.Left));
        Assert.Equal(new Brutal.Numerics.float2(300, 200), devices.MousePosition);
        Assert.Equal(default, devices.MouseDelta);
        router.EndWorldMouse();

        // Over the fleet list or a quick button the same click is the interface's, not the world's.
        capture.PointerOverInterface = true;
        router.Update(Command());
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        router.EndWorldMouse();
        Assert.Equal([true, false], capture.Calls);
    }

    [Fact]
    public void LettingGoOfAltMidDragEndsTheDragUntilTheButtonIsReleased()
    {
        var devices = new InputDevices();
        var router = new PointerRouter(devices, Bindings(), new Capture());
        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Press);
        router.Update(Command());
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        // Alt goes up with the camera drag still held: hidden, and the world loses the button.
        devices.BeginFrame();
        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Release);
        router.Update(Command());
        Assert.True(router.Captured);
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        // Alt again with the same press still held: the drag does not come back.
        devices.BeginFrame();
        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Press);
        router.Update(Command());
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();
    }

    [Fact]
    public void SwitchingFromPilotToCommandKeepsThePointerHiddenAndMasksAHeldFreeLook()
    {
        var devices = new InputDevices();
        var capture = new Capture();
        var router = new PointerRouter(devices, Bindings(), capture);
        router.Update(Pilot());
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: true);
        Assert.True(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        devices.BeginFrame();
        router.Update(Command());
        Assert.True(router.Captured);
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        devices.BeginFrame();
        devices.OnKey(GlfwKey.RightAlt, GlfwKeyAction.Press);
        router.Update(Command());
        router.BeginWorldMouse(worldInput: true);
        Assert.False(devices.MouseDown(GlfwMouseButton.Right));
        router.EndWorldMouse();

        // Captured once, shown once for Alt: no release and recapture on the switch itself.
        Assert.Equal([true, false], capture.Calls);
    }

    [Fact]
    public void OpenSurfacesFocusLossAndMutedRunsShowThePointerWithoutGivingTheWorldTheMouse()
    {
        var devices = new InputDevices();
        var router = new PointerRouter(devices, Bindings(), new Capture());
        router.Update(Command());
        Assert.True(router.Captured);

        // Chat or a panel open over the command view: shown, and the world sees no click even with Alt.
        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Press);
        router.Update(Command(surfaceOpen: true));
        Assert.False(router.Captured);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        router.BeginWorldMouse(worldInput: false);
        Assert.False(devices.MousePressed(GlfwMouseButton.Left));
        router.EndWorldMouse();
        devices.OnKey(GlfwKey.LeftAlt, GlfwKeyAction.Release);

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
