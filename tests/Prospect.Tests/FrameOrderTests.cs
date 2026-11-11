using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.FreeType;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.Settings;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Engine.Jobs;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
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
        public bool PointerOverWindow(float2 position) => UiPointerInput.OverWindow(position);
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

    // The map with its unit list, drawn instead of the quick buttons while set, and what the map took from it.
    private bool _mapShown;
    private readonly MapPointer _map = new();
    private readonly uint[] _units = new uint[64];
    private int _unitCount;
    private int _mapRowLimit = int.MaxValue;
    private int _mapPresses;
    private int _mapReleases;
    private int _mapRightClicks;
    private float _mapWheel;
    private float2 _mapPan;

    // The course menu as the map opens it on a right click, and what its rows ordered.
    private static readonly float2 MenuSize = MapCourseMenu.Size(1f);
    private bool _menuOpen;
    private float2 _menuMin;
    private float _menuTime;
    private Double3 _menuTarget;
    private int _menuMoves;

    public FrameOrderTests()
    {
        _context = ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.IniFilename = null;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        // The spacing the game's style uses, so rows sit where the layout puts them.
        ImGui.GetStyle().WindowPadding = float2.Zero;
        ImGui.GetStyle().ItemSpacing = float2.Zero;

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

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1655f, 688f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    public void TheConsoleKeepsEveryHullPointDigitWithTheShippedFont(float width, float height, float scale)
    {
        var fonts = ImGui.GetIO().Fonts;
        fonts.SetFontLoader(ImGuiFreeType.GetFontLoader());
        var mono = fonts.AddFontFromFileTTF(AppPaths.Asset("fonts", "JetBrainsMono-Regular.ttf"), Theme.FontBody);
        var sans = fonts.AddFontFromFileTTF(AppPaths.Asset("fonts", "SpaceGrotesk-Medium.ttf"), Theme.FontBody);
        ImGui.GetStyle().FontScaleMain = scale;
        _ui.NewFrame();
        try
        {
            // The shipped Prospector with hullStrength=4 has 23,120 HP. Use the game's font loader and active
            // scale: measuring the font outside ImGui gives different advances and can overstate its width.
            var layout = FlightConsole.Layout(new float2(width, height), scale);
            var available = layout.Compact
                ? layout.GroupWidth - MathF.Round((FlightConsole.Slant + 2 * FlightConsole.Pad + FlightConsole.ColumnGap) * scale)
                    - Kit.Measure(sans, Theme.FontSmall, "HULL"u8).X
                : MathF.Round(FlightConsole.ReadingColumn * scale);
            Assert.InRange(Kit.Measure(mono, Theme.FontSmall, "23120 / 23120"u8).X, 1f, available);
        }
        finally
        {
            ImGui.Render();
        }
    }

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

        if (_mapShown)
        {
            DrawMapSurface();
            ImGui.Render();
            return;
        }

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

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    [InlineData(FtlKind.Hyperspace)]
    public void HiddenTransitHullsHaveNoWorldMarkerOrBoxSelectionButRemainOnTheMap(FtlKind drive)
    {
        CommandView();
        for (var i = 0; i < 60; i++)
            Frame();
        _world.FtlPreview = drive;
        ref var own = ref _snapshot.Hulls[_snapshot.OwnHull];
        own.Jump = JumpPhase.Tunnel;
        own.JumpTimer = _snapshot.Flight.FtlTunnelSeconds * 0.5f;
        Frame();
        Assert.False(_world.IsDrawn(own));
        Assert.True(_world.Project(_world.DrawnPosition(own), out var at));
        var start = at - new float2(20, 20);
        var end = at + new float2(20, 20);
        Frame(() => { Move(start); Button(GlfwMouseButton.Left, true); });
        Frame(() => Move(end));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Assert.DoesNotContain(own.Id, _world.Selection.ToArray());

        var count = MapUnitList.Collect(_snapshot, _units);
        Assert.Contains(own.Id, _units.AsSpan(0, count).ToArray());
        _world.Select(own.Id, false);
        Assert.True(_world.IsSelected(own.Id));

        own.Jump = JumpPhase.Arrive;
        own.JumpTimer = _snapshot.Flight.FtlArriveSeconds * 0.8f;
        Frame();
        Assert.True(_world.IsDrawn(own));
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

    // The map's layout at this test's screen, unscaled, under a sector panel ending where the game's does.
    private MapUnitLayout Units => MapUnitList.Layout(Screen, 1f, Math.Min(_unitCount, _mapRowLimit), 140f, Screen.X - 436f);

    private static readonly float2 MapSpot = new(820, 420);

    /// <summary>The map and its unit list as <c>ScreenHost.DrawMap</c> submits them, without the drawing.</summary>
    private void DrawMapSurface()
    {
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var units = Units;
        _map.Block(units.Min, units.Size);
        if (_menuOpen)
        {
            _menuTime += 1f / 60f;
            _map.Block(MenuShown, MenuSize);
        }

        _map.Begin(Screen);
        _mapPresses += _map.Pressed ? 1 : 0;
        _mapReleases += _map.BoxReleased ? 1 : 0;
        _mapRightClicks += _map.RightClicked ? 1 : 0;
        _mapWheel += _map.Wheel;
        _mapPan += _map.Pan;
        if (_map.RightClicked)
        {
            _menuMin = MapCourseMenu.Place(ImGui.GetIO().MousePos, MenuSize, Screen, 1f);
            _menuTime = 0;
            _menuOpen = true;
        }

        ImGui.End();

        MapUnitList.Begin(units, _map);
        MapUnitList.BeginRows(units, _map);
        for (var i = 0; i < Math.Min(_unitCount, _mapRowLimit); i++)
            MapUnitList.Row(_world, _units[i], units.Row);
        MapUnitList.EndRows();
        MapUnitList.End();

        if (!_menuOpen)
            return;
        var shown = MenuShown;
        var rowSize = MapCourseMenu.RowSize(MenuSize, 1f);
        MapCourseMenu.Begin(shown, MenuSize, _map);
        if (MapCourseMenu.Item("Move here"u8, MapCourseMenu.RowMin(shown, 0, 1f), rowSize))
        {
            _world.OrderMoveSelected(Bridge, _menuTarget);
            _menuMoves++;
            _menuOpen = false;
        }

        MapCourseMenu.Item("Jump here"u8, MapCourseMenu.RowMin(shown, 1, 1f), rowSize);
        MapCourseMenu.End();
    }

    private float2 MenuShown => MapCourseMenu.Shown(_menuMin, _menuTime, 1f);

    private float2 MenuRowCenter(int row) => MapCourseMenu.RowMin(MenuShown, row, 1f) + MapCourseMenu.RowSize(MenuSize, 1f) * 0.5f;

    /// <summary>Opens the map as a surface over the world, the pointer resting on the map.</summary>
    private void OpenMap()
    {
        _surfaceOpen = true;
        _mapShown = true;
        _map.Reset();
        Frame(() => Move(MapSpot));
        Frame();
        Frame();
        Assert.False(_router.Captured);
        Assert.Equal(3, _unitCount);
        _mapPresses = _mapReleases = _mapRightClicks = 0;
        _mapWheel = 0;
        _mapPan = default;
    }

    private float2 RowCenter(int row)
    {
        var units = Units;
        return units.RowsMin + new float2(units.Size.X * 0.5f, units.Row * (row + 0.5f));
    }

    [Fact]
    public void AClickInNewlyGrownUnitRowsSelectsTheHullWithoutReachingTheMap()
    {
        _mapRowLimit = 1;
        OpenMap();
        _world.ClearSelection();
        Frame(() =>
        {
            _mapRowLimit = 3;
            Move(RowCenter(2));
            Click();
        });
        Frame();
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Single(_world.Selection.ToArray());
        Assert.True(_world.IsSelected(_units[2]));
    }

    [Fact]
    public void APressAndReleaseInSeparatePollsOnANewlyGrownRowSelectsItsHull()
    {
        _mapRowLimit = 1;
        OpenMap();
        _world.ClearSelection();

        // The list grows to two rows in the poll that moves onto the second and presses; the release comes later.
        Frame(() =>
        {
            _mapRowLimit = 2;
            Move(RowCenter(1));
            Button(GlfwMouseButton.Left, true);
        });
        Frame();
        Assert.Empty(_world.Selection.ToArray());
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Single(_world.Selection.ToArray());
        Assert.True(_world.IsSelected(_units[1]));
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(default, _mapPan);
    }

    [Fact]
    public void AMapPressHeldWhileTheListGrowsUnderItStaysTheMapsAndPressesNoRow()
    {
        _mapRowLimit = 1;
        OpenMap();
        _world.ClearSelection();

        // Pressed on the map where the third row will be; the list grows under the held button, which is let go there.
        Frame(() =>
        {
            Move(RowCenter(2));
            Button(GlfwMouseButton.Left, true);
        });
        Assert.Equal(1, _mapPresses);
        Frame(() =>
        {
            _mapRowLimit = 3;
            Move(RowCenter(2) + new float2(0, 2));
        });
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _mapPresses);
        Assert.Equal(1, _mapReleases);
        Assert.Empty(_world.Selection.ToArray());
    }

    [Fact]
    public void AClickInThePollThatOpensTheMapReachesNeitherTheMapNorARow()
    {
        CommandView();
        var own = _world.Own.Id;
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var row = Array.IndexOf(_units, own, 0, _unitCount);

        // The map opens as a shortcut does, after the interface frame began: nothing of it was on screen.
        Frame(() =>
        {
            Move(RowCenter(row));
            Click();
        }, () =>
        {
            _surfaceOpen = true;
            _mapShown = true;
            _map.Reset();
            return true;
        });
        Frame();
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal([own], _world.Selection.ToArray());

        // The first click after the list is shown is the row's.
        Frame(() => Click());
        Frame();
        Assert.Empty(_world.Selection.ToArray());
        Assert.Equal(0, _mapPresses);
    }

    [Fact]
    public void ACourseMenuOpenedAndClickedInOnePollOrdersTheSelectionWithoutAMapClick()
    {
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        OpenMap();
        _world.SelectAllOwned();
        _menuTarget = _world.OwnPosition + new Vector3(800, 0, 0);
        Bridge.Consume(out _, out _);
        Bridge.ConsumeFleetOrders(fleet);

        // A right click on the map and a click on the menu it opens, in one poll.
        var menu = MapCourseMenu.Place(MapSpot, MenuSize, Screen, 1f);
        var move = MapCourseMenu.RowMin(menu, 0, 1f) + MapCourseMenu.RowSize(MenuSize, 1f) * 0.5f;
        Frame(() =>
        {
            Click(GlfwMouseButton.Right);
            Move(move);
            Click();
        });
        for (var i = 0; i < 4; i++)
            Frame();

        Assert.Equal(1, _mapRightClicks);
        Assert.Equal(1, _menuMoves);
        Assert.False(_menuOpen);
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(default, _mapPan);
        Bridge.Consume(out var hasOrder, out var order);
        Assert.True(hasOrder);
        Assert.Equal(OrderKind.MoveTo, order.Kind);
        Assert.Equal(2, Bridge.ConsumeFleetOrders(fleet));
    }

    [Fact]
    public void ACourseMenuMovedByARightClickTakesTheNextClickAtItsNewPlace()
    {
        OpenMap();
        _world.SelectAllOwned();
        _menuTarget = _world.OwnPosition + new Vector3(800, 0, 0);
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame();
        Assert.True(_menuOpen);
        var first = _menuMin;

        // Moved by a right click elsewhere on the map, then clicked at its new place, all in one poll.
        var elsewhere = new float2(700, 200);
        var moved = MapCourseMenu.Place(elsewhere, MenuSize, Screen, 1f);
        Assert.NotEqual(first, moved);
        var move = MapCourseMenu.RowMin(moved, 0, 1f) + MapCourseMenu.RowSize(MenuSize, 1f) * 0.5f;
        Frame(() =>
        {
            Move(elsewhere);
            Click(GlfwMouseButton.Right);
            Move(move);
            Click();
        });
        for (var i = 0; i < 4; i++)
            Frame();

        Assert.Equal(moved, _menuMin);
        Assert.Equal(2, _mapRightClicks);
        Assert.Equal(1, _menuMoves);
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
    }

    [Fact]
    public void UnitRowsChangedWhileTheCourseMenuIsOpenDecideWhatItOrders()
    {
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        OpenMap();
        _world.SelectAllOwned();
        _menuTarget = _world.OwnPosition + new Vector3(800, 0, 0);
        Bridge.Consume(out _, out _);
        Bridge.ConsumeFleetOrders(fleet);
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame();
        Assert.True(_menuOpen);

        // One fleet hull taken out in the list; the menu stays open.
        var own = _world.Own.Id;
        var dropped = Array.FindIndex(_units, 0, _unitCount, id => id != own);
        var kept = Array.FindLastIndex(_units, _unitCount - 1, _unitCount, id => id != own);
        Frame(() =>
        {
            Move(RowCenter(dropped));
            Click();
        });
        Frame();
        Assert.True(_menuOpen);
        Assert.Equal(2, _world.Selection.Length);

        // Move here, pressed and let go in separate polls: the hull being flown and the one fleet hull left.
        Frame(() =>
        {
            Move(MenuRowCenter(0));
            Button(GlfwMouseButton.Left, true);
        });
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _menuMoves);
        Bridge.Consume(out var hasOrder, out _);
        Assert.True(hasOrder);
        Assert.Equal(1, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(_units[kept], fleet[0].Hull);

        // Cleared with the menu open again: Move here orders nothing at all.
        _world.ClearSelection();
        Frame(() =>
        {
            Move(MapSpot);
            Click(GlfwMouseButton.Right);
        });
        Frame();
        Frame();
        Frame(() =>
        {
            Move(MenuRowCenter(0));
            Click();
        });
        Frame();
        Assert.Equal(2, _menuMoves);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
    }

    [Fact]
    public void AUnitRowClickAddsOrRemovesThatHullAndLeavesTheOthers()
    {
        OpenMap();
        _world.SelectAllOwned();
        Assert.Equal(3, _world.Selection.Length);
        var picked = _units[1];

        // A move onto the row and a click in one poll: the row's, not the map's.
        Frame(() =>
        {
            Move(RowCenter(1));
            Click();
        });
        Frame();
        Assert.Equal(2, _world.Selection.Length);
        Assert.False(_world.IsSelected(picked));
        Assert.True(_world.IsSelected(_units[0]));
        Assert.True(_world.IsSelected(_units[2]));

        // The rows did not move under the pointer: the same click puts it back.
        Frame(() => Click());
        Frame();
        Assert.Equal(3, _world.Selection.Length);
        Assert.True(_world.IsSelected(picked));

        // From an empty selection a row adds just its hull.
        _world.ClearSelection();
        Frame(() => Click());
        Frame();
        Assert.Equal(1, _world.Selection.Length);
        Assert.True(_world.IsSelected(picked));
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
    }

    [Fact]
    public void WheelRightClickAndMiddleDragOnTheUnitListNeverReachTheMap()
    {
        OpenMap();
        Frame(() =>
        {
            Move(RowCenter(0));
            Wheel(2);
        });
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(RowCenter(0) + new float2(200, 0)));
        Frame(() => Move(MapSpot));
        Frame(() => Button(GlfwMouseButton.Middle, false));
        Assert.Equal(0f, _mapWheel);
        Assert.Equal(0, _mapRightClicks);
        Assert.Equal(default, _mapPan);
        Assert.Equal(0, _mapPresses);

        // The same on the map is the map's.
        Frame(() => Wheel(2));
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(MapSpot + new float2(40, 10)));
        Frame(() => Button(GlfwMouseButton.Middle, false));
        Assert.Equal(2f, _mapWheel);
        Assert.Equal(1, _mapRightClicks);
        Assert.NotEqual(default, _mapPan);
    }

    [Fact]
    public void ADragBegunOnTheUnitListNeverBecomesAMapBoxAndAMapBoxPressesNoRow()
    {
        OpenMap();
        _world.SelectAllOwned();

        // Pressed on a row, let go on the map: no map gesture, and no row click either.
        Frame(() =>
        {
            Move(RowCenter(0));
            Button(GlfwMouseButton.Left, true);
        });
        Frame(() => Move(MapSpot));
        Frame(() => Move(MapSpot + new float2(60, 60)));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(3, _world.Selection.Length);

        // Pressed on the map, dragged across the list and let go on a row: the map's box, and no row click.
        Frame(() =>
        {
            Move(MapSpot);
            Button(GlfwMouseButton.Left, true);
        });
        Frame(() => Move(RowCenter(1) + new float2(30, 0)));
        Frame(() => Move(RowCenter(1)));
        Assert.True(_map.Boxing);
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _mapPresses);
        Assert.Equal(1, _mapReleases);
        Assert.True(_map.BoxMoved);
        Assert.Equal(3, _world.Selection.Length);
    }

    [Fact]
    public void MapMovesGoToExactlyTheSelectedHullsAndNowhereWhenNoneIsSelected()
    {
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        var target = _world.OwnPosition + new Vector3(800, 0, 0);
        var own = _world.Own.Id;
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var others = _units.AsSpan(0, _unitCount).ToArray().Where(id => id != own).ToArray();
        Assert.Equal(2, others.Length);
        Bridge.Consume(out _, out _);
        Bridge.ConsumeFleetOrders(fleet);

        // All three, then one fleet hull taken out of the list: the two left are ordered, the third is not.
        _world.SelectAllOwned();
        Assert.Equal(3, _world.Selection.Length);
        foreach (var id in _world.Selection)
        {
            Assert.True(_world.TryGetHull(id, out var hull));
            Assert.Equal(_snapshot.PlayerId, hull.Owner);
        }

        _world.Select(others[0], true);
        _world.OrderMoveSelected(Bridge, target);
        Bridge.Consume(out var hasOrder, out var order);
        Assert.True(hasOrder);
        Assert.Equal(OrderKind.MoveTo, order.Kind);
        Assert.Equal(1, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(others[1], fleet[0].Hull);
        Assert.Equal(OrderKind.MoveTo, fleet[0].Order.Kind);

        // The hull being flown taken out as well: only the one fleet hull is ordered.
        _world.Select(own, true);
        _world.OrderMoveSelected(Bridge, target);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(1, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(others[1], fleet[0].Hull);

        // Cleared: nothing is ordered, not even the hull being flown.
        _world.ClearSelection();
        _world.OrderMoveSelected(Bridge, target);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));

        // A hull that is gone is dropped from the selection, and does not make it fall back to the own hull.
        _world.Select(0xFFFF_FF00u, false);
        _world.OrderMoveSelected(Bridge, target);
        Assert.Equal(0, _world.Selection.Length);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));

        // Outside the map an empty selection still means the hull being flown.
        _world.OrderMove(Bridge, target);
        Bridge.Consume(out hasOrder, out order);
        Assert.True(hasOrder);
        Assert.Equal(OrderKind.MoveTo, order.Kind);
    }

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
    public void ANewRightPressOnTheInterfaceCannotInheritACameraDrag()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(200, 0)));
        Assert.True(_world.FreeLook);
        Frame(() => Move(OverButton));

        // Both edges arrive in one poll: the new UI press must not continue the old world drag.
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Button(GlfwMouseButton.Right, true);
        });
        Frame(() => Move(EmptySpace));
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Assert.Equal(0, _buttonClicks);

        Frame(() => Button(GlfwMouseButton.Right, false));
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(400, 0)));
        Assert.True(_world.FreeLook);
    }

    [Fact]
    public void ReleaseMoveOntoUiAndPressInOnePollNeverStartsAnotherCameraDrag()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Assert.True(_world.FreeLook);
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Move(OverButton);
            Button(GlfwMouseButton.Right, true);
        });
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Frame(() => Move(OverButton + new float2(20, 0)));
        Frame(() => Move(EmptySpace));
        Assert.False(_world.FreeLook);
    }

    [Fact]
    public void AUiPressFollowedByMotionInTheSamePollKeepsItsStartingPosition()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Move(OverButton);
            Button(GlfwMouseButton.Right, true);
            Move(EmptySpace + new float2(200, 0));
        });
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
    }

    [Fact]
    public void ANewRightPressOverSpaceCanStartAnotherCameraDragInTheSamePoll()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Assert.True(_world.FreeLook);
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Button(GlfwMouseButton.Right, true);
        });
        Frame(() => Move(EmptySpace + new float2(200, 0)));
        Assert.True(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) > 0.05f);
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
