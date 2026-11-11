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

    /// <summary>The command view, Alt held, the pointer resting on empty space and the own hull selected.</summary>
    private void CommandViewWithAlt()
    {
        _world.SetMode(ViewMode.Command);
        Frame(() => Key(GlfwKey.LeftAlt, true));
        Frame(() => Move(EmptySpace));
        Frame();
        Assert.False(_router.Captured);
        Assert.Equal(1, _world.Selection.Length);
    }

    [Fact]
    public void AMoveOntoAQuickButtonAndAClickInOnePollAreTheButtonsNotTheWorlds()
    {
        CommandViewWithAlt();
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

            _world.SetMode(ViewMode.Command);
            Frame(() =>
            {
                Key(GlfwKey.LeftAlt, true);
                Move(EmptySpace);
                Click();
            }, Shortcut);
            Assert.Equal(0, shortcuts);
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
        CommandViewWithAlt();
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
    public void AltAndAClickInOnePollReachNeitherTheHiddenInterfaceNorTheWorld()
    {
        _world.SetMode(ViewMode.Command);
        Frame(() => Move(OverButton));
        Frame();
        Assert.True(_router.Captured);
        Assert.Equal(1, _world.Selection.Length);

        // Hidden: a click over where the button is reaches nothing.
        Frame(() => Click());
        Frame();
        Assert.Equal(0, _buttonClicks);

        // Alt and the click in the same poll: the click was made while the pointer was hidden.
        Frame(() =>
        {
            Key(GlfwKey.LeftAlt, true);
            Move(EmptySpace);
            Click();
        });
        Frame();
        Frame();
        Assert.False(_router.Captured);
        Assert.Equal(1, _world.Selection.Length);
        Assert.Equal(0, _buttonClicks);

        // Nothing is stuck: the next click is the world's.
        Frame(() => Click());
        Assert.Equal(0, _world.Selection.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AltReleaseAndAClickInOnePollPressNoButton(bool releaseInSamePoll)
    {
        CommandViewWithAlt();
        Frame(() => Move(OverButton));
        Frame(() =>
        {
            Button(GlfwMouseButton.Left, true);
            if (releaseInSamePoll)
                Button(GlfwMouseButton.Left, false);
            Key(GlfwKey.LeftAlt, false);
        });
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Frame();
        Assert.True(_router.Captured);
        Assert.Equal(0, _buttonClicks);
        Assert.Equal(1, _world.Selection.Length);
    }

    [Fact]
    public void CapturingCancelsMouseInputWithoutDiscardingQueuedKeyboardAndText()
    {
        CommandViewWithAlt();
        Frame(() => Move(OverButton));
        var io = ImGui.GetIO();
        Frame(() =>
        {
            Button(GlfwMouseButton.Left, true);
            io.AddKeyEvent(ImGuiKey.F1, true);
            io.AddInputCharacter('x');
            Wheel(3);
            Button(GlfwMouseButton.Left, false);
            Key(GlfwKey.LeftAlt, false);
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
        CommandViewWithAlt();
        Frame(() => Move(OverButton));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Assert.True(ImGui.IsAnyItemActive());
        Frame(() => Key(GlfwKey.LeftAlt, false));
        Assert.False(ImGui.IsAnyItemActive());
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame(() => Key(GlfwKey.LeftAlt, true));
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
    public void SwitchingToPilotWithAltDoesNotTurnTheViewWithTheSwitchFramesMotion()
    {
        CommandViewWithAlt();
        Frame(() =>
        {
            Key(GlfwKey.N, true);
            Move(EmptySpace + new float2(300, 0));
        });
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        var aim = Bridge.Consume(out _, out _).Aim;
        var nose = Vector3.Transform(-Vector3.UnitZ, _world.OwnOrientation);
        Assert.True(Vector3.Distance(aim, nose) < 1e-4f, $"aim {aim} nose {nose}");
    }

    [Fact]
    public void TakingControlFromCommandEndsTheFramesCommandMouse()
    {
        CommandViewWithAlt();
        var distance = _world.CommandDistance;
        Frame(() =>
        {
            Key(GlfwKey.T, true);
            Wheel(3);
        });
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.Equal(distance, _world.CommandDistance);
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
