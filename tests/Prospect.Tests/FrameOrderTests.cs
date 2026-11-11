using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Settings;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The client's frame as <c>App.Run</c> orders it, headless: window events polled into the real
/// <see cref="InputDevices"/> and, through <see cref="UiPointerInput"/>, into a real Dear ImGui context as
/// <c>UiPlatform</c> feeds it; then <see cref="PointerRouter.Update"/> (capture, quarantine, interface frame,
/// ownership), the real <see cref="WorldView"/> reading the mouse, and an interface window drawn after it, as the
/// quick buttons are. A poll can hold several events at once, as GLFW delivers them. No window, no GPU: the world
/// view never touches its renderer while updating. Dear ImGui keeps one global context, so every test that needs
/// it lives in this class, whose tests run one at a time.
/// </summary>
public sealed class FrameOrderTests : IDisposable
{
    private static readonly float2 Screen = new(1280, 720);
    private static readonly float2 ButtonMin = new(1100, 20);
    private static readonly float2 ButtonSize = new(150, 32);
    private static readonly float2 OverButton = new(1175, 36);
    private static readonly float2 EmptySpace = new(90, 660);

    /// <summary>Dear ImGui as <c>UiContext</c> drives it, minus fonts and rendering.</summary>
    private sealed class HeadlessInterface(InputDevices devices) : IInterfacePointer
    {
        public bool Captured;
        public float2 Cursor;

        public void SetPointerCaptured(bool captured)
        {
            Captured = captured;
            UiPointerInput.Captured(ImGui.GetIO(), captured, Cursor);
            if (!captured)
                devices.PlacePointer(Cursor);
        }

        public void NewFrame()
        {
            var io = ImGui.GetIO();
            io.DisplaySize = Screen;
            io.DeltaTime = 1f / 60f;
            ImGui.NewFrame();
        }

        public bool PointerOverInterface => ImGui.GetIO().WantCaptureMouse;
    }

    private readonly ImGuiContextPtr _context;
    private readonly LoopbackLink _link = new();
    private readonly ClientSession _client;
    private readonly ServerHost _server;
    private readonly JobSystem _jobs = new(1, "TestJob");
    private readonly InputDevices _devices = new();
    private readonly HeadlessInterface _ui;
    private readonly PointerRouter _router;
    private readonly WorldView _world;
    private readonly SessionSnapshot _snapshot;
    private int _buttonClicks;
    private long _tick;
    private bool _surfaceOpen;

    public FrameOrderTests()
    {
        _context = ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.IniFilename = null;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;

        var content = GameContent.Load();
        _client = new ClientSession(_link.Client, content, "Pilot");
        _server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, [_link.Server]);
        for (var i = 0; i < 10; i++)
        {
            ((ITickHandler)_server).Tick(_tick);
            ((ITickHandler)_client).Tick(_tick++);
        }

        _snapshot = _client.AcquireSnapshot();
        Assert.Equal(SessionState.Active, _snapshot.State);
        Assert.True(_snapshot.OwnHull >= 0);

        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        _ui = new HeadlessInterface(_devices);
        _router = new PointerRouter(_devices, actions, _ui);
        _world = new WorldView(_devices, actions, new ClientSettings(), _jobs, null!, content.Designs);
        Frame();
        Frame();
    }

    public void Dispose()
    {
        _jobs.CompleteAll();
        _jobs.Dispose();
        _client.Dispose();
        _server.Dispose();
        _link.Dispose();
        ImGui.DestroyContext(_context);
    }

    private PlayerInputBridge Bridge => _client.Input;

    // One GLFW event each, delivered to both listeners exactly as the window callbacks do.
    private void Move(float2 to)
    {
        _ui.Cursor = to;
        _devices.OnCursorPos(new double2(to.X, to.Y));
        UiPointerInput.Position(ImGui.GetIO(), to, _ui.Captured);
    }

    private void Button(GlfwMouseButton button, bool press)
    {
        _devices.OnMouseButton(button, press ? GlfwButtonAction.Press : GlfwButtonAction.Release);
        UiPointerInput.Button(ImGui.GetIO(), (int)button, press, _ui.Captured);
    }

    private void Click(GlfwMouseButton button = GlfwMouseButton.Left)
    {
        Button(button, true);
        Button(button, false);
    }

    private void Wheel(float lines)
    {
        _devices.OnScroll(lines);
        UiPointerInput.Wheel(ImGui.GetIO(), new float2(0, lines), _ui.Captured);
    }

    private void Key(GlfwKey key, bool press) => _devices.OnKey(key, press ? GlfwKeyAction.Press : GlfwKeyAction.Release);

    /// <summary>One frame of <c>App.Run</c>: poll, route, world, interface.</summary>
    private void Frame(Action? poll = null, Func<bool>? shortcut = null)
    {
        _devices.BeginFrame();
        poll?.Invoke();
        var pointerFrame = new PointerFrame { InSpace = true, Focused = true, SurfaceOpen = _surfaceOpen, CommandView = _world.Mode == ViewMode.Command };
        _router.Update(pointerFrame);
        var handled = shortcut?.Invoke() ?? false;
        pointerFrame.SurfaceOpen = _surfaceOpen;
        _router.RefreshCapture(pointerFrame);
        var worldInput = !_surfaceOpen && !handled && !ImGui.GetIO().WantCaptureKeyboard;
        _router.BeginWorldMouse(worldInput);
        _world.Update(_snapshot, Bridge, 1f / 60f, worldInput, Screen, Screen, _router.PointerOverInterface);
        _router.EndWorldMouse();
        pointerFrame.CommandView = _world.Mode == ViewMode.Command;
        _router.RefreshCapture(pointerFrame);

        // The quick buttons, drawn after the world as ScreenHost draws them.
        ImGui.SetNextWindowPos(ButtonMin);
        ImGui.SetNextWindowSize(ButtonSize);
        ImGui.Begin("##quickbar"u8, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        ImGui.SetCursorScreenPos(ButtonMin);
        if (ImGui.InvisibleButton("##players"u8, ButtonSize))
            _buttonClicks++;
        ImGui.End();
        ImGui.Render();
    }

    /// <summary>The command view without Shift, the pointer shown and resting on empty space, the own hull selected.</summary>
    private void CommandView()
    {
        _world.SetMode(ViewMode.Command);
        Frame();
        Frame(() => Move(EmptySpace));
        Frame();
        Assert.False(_router.Captured);
        Assert.Equal(1, _world.Selection.Length);
    }

    /// <summary>The pilot view with Shift held, so the pointer is shown, resting on empty space.</summary>
    private void PilotWithShift()
    {
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Move(EmptySpace));
        Frame();
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.False(_router.Captured);
    }

    private Vector3 Nose => Vector3.Transform(-Vector3.UnitZ, _world.OwnOrientation);

    private PlayerControls Sent() => Bridge.Consume(out _, out _);

    [Fact]
    public void AMoveOntoAQuickButtonAndAClickInOnePollAreTheButtonsNotTheWorlds()
    {
        CommandView();
        Frame(() =>
        {
            Move(OverButton);
            Click();
        });
        Frame();

        // The world did not select behind the button; Dear ImGui completed the click on the next frame.
        Assert.Equal(1, _world.Selection.Length);
        Assert.Equal(1, _buttonClicks);

        // The same click on empty space is the world's: it clears the selection.
        Frame(() =>
        {
            Move(EmptySpace);
            Click();
        });
        Assert.Equal(0, _world.Selection.Length);
        Assert.Equal(1, _buttonClicks);
    }

    [Theory]
    [InlineData("ToggleControls")]
    [InlineData("TogglePlayers")]
    [InlineData("ToggleSocial")]
    [InlineData("ToggleChat")]
    public void MouseShortcutsUseCurrentHoverAndIgnoreCaptureTransitionClicks(string actionName)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $$$"""{"actions":{"{{{actionName}}}":["Mouse:Left"]},"axes":{}}""");
            var actions = ActionMap.Load(path);
            var action = Enum.Parse<InputAction>(actionName);
            var shortcuts = 0;
            bool Shortcut()
            {
                var pressed = actions.InterfaceShortcutPressed(_devices, action, ImGui.GetIO().WantTextInput, _ui.PointerOverInterface);
                if (pressed)
                    shortcuts++;
                return pressed;
            }

            // Shift and a click in one poll from the captured pilot view: the click belongs to neither owner.
            Frame(() =>
            {
                Key(GlfwKey.LeftShift, true);
                Move(EmptySpace);
                Click();
            }, Shortcut);
            Assert.Equal(0, shortcuts);
            _world.SetMode(ViewMode.Command);
            Frame();

            Frame(() =>
            {
                Move(OverButton);
                Click();
            }, Shortcut);
            Frame();
            Assert.Equal(0, shortcuts);
            Assert.Equal(1, _buttonClicks);

            // Leaving the button and clicking empty space in one poll must enable the shortcut immediately.
            Frame(() =>
            {
                Move(EmptySpace);
                Click();
            }, Shortcut);
            Assert.Equal(1, shortcuts);
            Assert.Equal(1, _world.Selection.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShortcutsOpenAndCloseSurfacesWithTheCorrectCursorInTheSameFrame()
    {
        Assert.True(_router.Captured);
        Frame(shortcut: () => { _surfaceOpen = true; return true; });
        Assert.False(_router.Captured);
        Frame(shortcut: () => { _surfaceOpen = false; return true; });
        Assert.True(_router.Captured);
    }

    [Fact]
    public void WheelAndMiddleDragOverAQuickButtonLeaveTheCommandCameraAlone()
    {
        CommandView();
        var distance = _world.CommandDistance;
        Frame(() =>
        {
            Move(OverButton);
            Wheel(3);
        });
        Assert.Equal(distance, _world.CommandDistance);

        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(OverButton + new float2(-60, 8)));
        Frame(() => Move(EmptySpace));
        Frame(() => Move(EmptySpace + new float2(40, -40)));
        Assert.True(_world.CommandFollowing);
        Frame(() => Button(GlfwMouseButton.Middle, false));

        // Over empty space the wheel is the world's.
        Frame(() => Wheel(3));
        Assert.NotEqual(distance, _world.CommandDistance);
    }

    [Fact]
    public void ShiftAndAClickInOnePollReachNeitherTheHiddenInterfaceNorTheWorld()
    {
        Frame(() => Move(OverButton));
        Frame();
        Assert.True(_router.Captured);

        // Hidden: a click over where the button is reaches nothing.
        Frame(() => Click());
        Frame();
        Assert.Equal(0, _buttonClicks);

        // Shift and a right press in the same poll: made while the pointer was hidden, so neither the button nor
        // the camera drag gets it, even while it stays held and moves.
        Frame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Click();
            Button(GlfwMouseButton.Right, true);
        });
        Frame(() => Move(OverButton + new float2(-80, 30)));
        Frame(() => Move(EmptySpace));
        Assert.False(_router.Captured);
        Assert.False(_world.FreeLook);
        Assert.Equal(0, _buttonClicks);
        Frame(() => Button(GlfwMouseButton.Right, false));

        // Nothing is stuck: the next click on the button is the interface's.
        Frame(() => Move(OverButton));
        Frame(() => Click());
        Frame();
        Assert.Equal(1, _buttonClicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShiftReleaseAndAClickInOnePollPressNoButton(bool releaseInSamePoll)
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        Frame(() =>
        {
            Button(GlfwMouseButton.Left, true);
            if (releaseInSamePoll)
                Button(GlfwMouseButton.Left, false);
            Key(GlfwKey.LeftShift, false);
        });
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Frame();
        Assert.True(_router.Captured);
        Assert.Equal(0, _buttonClicks);
        Assert.False(_world.FreeLook);
    }

    [Fact]
    public void CapturingCancelsMouseInputWithoutDiscardingQueuedKeyboardAndText()
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        var io = ImGui.GetIO();
        Frame(() =>
        {
            Button(GlfwMouseButton.Left, true);
            io.AddKeyEvent(ImGuiKey.F1, true);
            io.AddInputCharacter('x');
            Wheel(3);
            Button(GlfwMouseButton.Left, false);
            Key(GlfwKey.LeftShift, false);
        }, shortcut: () =>
        {
            // Text is available during the frame, then ImGui clears it at EndFrame.
            Assert.Equal((uint)'x', (uint)Assert.Single(io.InputQueueCharacters));
            return false;
        });
        Assert.True(_router.Captured);
        Assert.True(ImGui.IsKeyDown(ImGuiKey.F1));
        Assert.Equal(0f, io.MouseWheel);
        Assert.Equal(UiPointerInput.Absent, io.MousePos);
        Frame(() => io.AddKeyEvent(ImGuiKey.F1, false));
        Frame();
        Assert.False(ImGui.IsKeyDown(ImGuiKey.F1));
        Assert.Equal(0, _buttonClicks);
    }

    [Fact]
    public void CapturingCancelsAnActiveMousePressBeforeItsLaterRelease()
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Assert.True(ImGui.IsAnyItemActive());
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.False(ImGui.IsAnyItemActive());
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame();
        Assert.Equal(0, _buttonClicks);
        Frame(() => Click());
        Frame();
        Assert.Equal(1, _buttonClicks);
    }

    [Fact]
    public void SwitchingToCommandGivesTheNewViewNoneOfTheSwitchFramesMouse()
    {
        Frame(() => Move(EmptySpace));
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.True(_router.Captured);

        // N, a click and the wheel in one poll from the pilot view, where the world owned the mouse.
        Frame(() =>
        {
            Key(GlfwKey.N, true);
            Click();
            Wheel(3);
        });
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.Equal(150f, _world.CommandDistance);
        Assert.Equal(1, _world.Selection.Length);
    }

    [Fact]
    public void TheCommandKeyShowsThePointerInItsOwnFrameAndThePilotViewTakesItBack()
    {
        Frame(() => Move(EmptySpace));
        Assert.True(_router.Captured);

        // N without Shift: shown in the same frame, no release and recapture in between.
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.False(_router.Captured);
        Frame(() => Key(GlfwKey.N, false));
        Frame(() => Move(EmptySpace));
        Assert.False(_router.Captured);

        // The following frames are the world's: wheel zoom, middle-drag pan, a click on empty space.
        var distance = _world.CommandDistance;
        Frame(() => Wheel(2));
        Assert.NotEqual(distance, _world.CommandDistance);
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(EmptySpace + new float2(40, -40)));
        Frame(() => Button(GlfwMouseButton.Middle, false));
        Assert.False(_world.CommandFollowing);
        Assert.Equal(1, _world.Selection.Length);
        Frame(() => Click());
        Assert.Equal(0, _world.Selection.Length);

        // Shift in the command view changes nothing: the pointer stays shown either way.
        Frame(() => Key(GlfwKey.LeftShift, true));
        Assert.False(_router.Captured);
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.False(_router.Captured);

        // Back to the pilot view: captured again in the same frame.
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.True(_router.Captured);
        Frame(() => Key(GlfwKey.N, false));

        // With Shift held, returning to the pilot view leaves the pointer shown.
        Frame(() => Key(GlfwKey.N, true));
        Frame(() => Key(GlfwKey.N, false));
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.False(_router.Captured);
    }

    [Fact]
    public void ShiftPressedOrReleasedDuringACommandDragDoesNotEndIt()
    {
        CommandView();
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Move(EmptySpace + new float2(20, -20));
        });
        Frame(() =>
        {
            Key(GlfwKey.LeftShift, false);
            Move(EmptySpace + new float2(40, -40));
        });
        // The pan reached the world through both Shift changes and broke the follow on the own hull.
        Assert.False(_world.CommandFollowing);
        Frame(() => Button(GlfwMouseButton.Middle, false));
    }

    [Fact]
    public void ShiftKeepsItsCommandViewMeaningsWithoutCapturingThePointer()
    {
        CommandView();
        var focus = _world.CommandFocus;

        // Shift + left drag slides the plane, with the pointer shown throughout.
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Move(EmptySpace + new float2(0, -120)));
        Assert.False(_router.Captured);
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.NotEqual(focus.Y, _world.CommandFocus.Y);
        Assert.False(_router.Captured);
    }

    [Fact]
    public void PilotSpaceBoostsAndRAndFMoveVerticallyWithoutCommandFocus()
    {
        Frame();
        Bridge.Consume(out _, out _);
        Frame(() => Key(GlfwKey.Space, true));
        var sent = Sent();
        Assert.True(sent.Boost);
        Assert.Equal(0f, sent.Vertical);
        Frame(() => Key(GlfwKey.Space, false));

        Frame(() => Key(GlfwKey.R, true));
        sent = Sent();
        Assert.Equal(1f, sent.Vertical);
        Assert.False(sent.Boost);
        Frame(() => Key(GlfwKey.R, false));

        var following = _world.CommandFollowing;
        Frame(() => Key(GlfwKey.F, true));
        sent = Sent();
        Assert.Equal(-1f, sent.Vertical);
        Assert.False(sent.Boost);
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.Equal(following, _world.CommandFollowing);
        Assert.True(_router.Captured);
    }

    [Fact]
    public void AClickHeldThroughTheCommandKeyNeverBecomesACommandAction()
    {
        Frame(() => Move(EmptySpace));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.Equal(1, _world.Selection.Length);

        // Dragged and released in the command view: no box, no click that clears the selection.
        Frame(() => Move(EmptySpace + new float2(200, -150)));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _world.Selection.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingToPilotDoesNotTurnTheViewWithTheSwitchFramesMotion(bool alt)
    {
        CommandView();
        if (alt)
            Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() =>
        {
            Key(GlfwKey.N, true);
            Move(EmptySpace + new float2(300, 0));
        });
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.Equal(!alt, _router.Captured);
        var aim = Sent().Aim;
        Assert.True(Vector3.Distance(aim, Nose) < 1e-4f, $"aim {aim} nose {Nose}");
    }

    [Fact]
    public void TakingControlFromCommandEndsTheFramesCommandMouse()
    {
        CommandView();
        var distance = _world.CommandDistance;
        Frame(() =>
        {
            Key(GlfwKey.T, true);
            Wheel(3);
        });
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.Equal(distance, _world.CommandDistance);
        Assert.True(_router.Captured);
    }

    [Fact]
    public void ShiftAndARightDragOverTheWorldTurnTheCameraButNotTheHull()
    {
        PilotWithShift();
        Frame(() => Key(GlfwKey.W, true));
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);

        // Without the right button, the shown pointer turns nothing.
        Frame(() => Move(EmptySpace + new float2(150, 0)));
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);

        // Right press over the world, then drag: free look. The camera turns; under thrust the hull is not
        // told to align with it, and nothing is clicked or targeted.
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(400, 40)));
        var sent = Sent();
        Assert.True(_world.FreeLook);
        Assert.False(_router.Captured);
        Assert.True(sent.Forward > 0);
        Assert.False(sent.Align);
        Assert.True(Vector3.Distance(sent.Aim, Nose) > 0.05f, $"aim {sent.Aim} nose {Nose}");
        Assert.Equal(0u, _world.TargetHull);
        Assert.Equal(0, _buttonClicks);

        // Over the quick buttons mid-drag the drag stays the camera's and presses nothing.
        Frame(() => Move(OverButton));
        Assert.True(_world.FreeLook);

        // Letting go returns the view to the nose, as free look does.
        Frame(() => Button(GlfwMouseButton.Right, false));
        Frame();
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Assert.Equal(0, _buttonClicks);
    }

    [Fact]
    public void ARightDragBegunOnTheInterfaceNeverTurnsTheCamera()
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace));
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Frame(() => Button(GlfwMouseButton.Right, false));
        Assert.Equal(0, _buttonClicks);
    }

    [Fact]
    public void LettingGoOfShiftEndsTheCameraDragAndTheHeldButtonStaysMasked()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(200, 0)));
        Assert.True(_world.FreeLook);

        // Shift up with the button held: captured again, the drag ends and the held button is not free look.
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.True(_router.Captured);
        Frame();
        Assert.False(_world.FreeLook);

        // Shift again with the same press still held: the drag does not come back.
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
        Frame(() => Button(GlfwMouseButton.Right, false));

        // A fresh press is a drag again.
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Assert.True(_world.FreeLook);
        Frame(() => Button(GlfwMouseButton.Right, false));
    }

    [Theory]
    [InlineData(0x0000_0001_0000_0007UL, 0x0000_0002_0000_0007UL)]
    [InlineData(0x0000_0001_0000_0007UL, 0x0000_0001_0000_0008UL)]
    public void APressOnAnInvitationCannotAcceptItsReplacement(ulong shown, ulong replacement)
    {
        // Press Accept on one invitation; it is replaced before the release, the new card in the same place.
        var accepted = 0;
        void Card(ulong identity)
        {
            ImGui.GetIO().DisplaySize = Screen;
            ImGui.GetIO().DeltaTime = 1f / 60f;
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new float2(100, 100));
            ImGui.SetNextWindowSize(new float2(300, 100));
            ImGui.Begin("##social"u8, ImGuiWindowFlags.NoDecoration);
            Kit.PushId(identity);
            ImGui.SetCursorScreenPos(new float2(120, 140));
            if (ImGui.InvisibleButton("Accept"u8, new float2(120, 40)))
                accepted++;
            Kit.PopId();
            ImGui.End();
            ImGui.Render();
        }

        var io = ImGui.GetIO();
        Card(shown);
        io.AddMousePosEvent(150, 160);
        Card(shown);
        io.AddMouseButtonEvent(0, true);
        Card(shown);
        io.AddMouseButtonEvent(0, false);
        Card(replacement);
        Card(replacement);
        Assert.Equal(0, accepted);

        // The same card is accepted by the same press and release.
        io.AddMouseButtonEvent(0, true);
        Card(replacement);
        io.AddMouseButtonEvent(0, false);
        Card(replacement);
        Assert.Equal(1, accepted);
    }
}
