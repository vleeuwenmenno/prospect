using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Prospect.Client.Diagnostics;
using Prospect.Client.Input;
using Prospect.Client.Render;
using Prospect.Client.Screens;
using Prospect.Client.Settings;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.IO;
using Prospect.Engine.Jobs;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using IJob = Brutal.Concurrency.Jobs.IJob;

namespace Prospect.Client;

/// <summary>Command-line options for the client.</summary>
internal sealed class AppOptions
{
    public bool Validation { get; set; }
    public Screen StartScreen { get; set; } = Screen.MainMenu;
    public bool StartLocalGame { get; set; }
    public string? JoinHost { get; set; }
    public ushort JoinPort { get; set; }

    /// <summary>World settings for local games, each as <c>id=value</c>, in the order they were given.</summary>
    public List<string> WorldSettingArgs { get; } = [];

    /// <summary>How local games started in this run are hosted, as <c>--host</c> and its companions set it.</summary>
    public HostingOptions Hosting { get; } = new();

    /// <summary>The password for <c>--join</c>, or for the game <c>--host</c> starts; null for none.</summary>
    public string? Password { get; set; }

    /// <summary>The listing server's address, <c>off</c>, or null for the content's default.</summary>
    public string? Listing { get; set; }
    /// <summary>What the server browser shows instead of the listing server's answer; for scripted screenshots.</summary>
    public BrowserSample BrowserSample { get; set; }

    /// <summary>Open the command view or the map once in space; for scripted screenshots.</summary>
    public string? OpenView { get; set; }
    public float BenchSeconds { get; set; }
    public string? CapturePath { get; set; }
    public float CaptureAfter { get; set; } = 1.5f;

    /// <summary>With more than one capture, the screenshots are numbered and taken this many seconds apart.</summary>
    public int CaptureCount { get; set; } = 1;
    public float CaptureEvery { get; set; } = 0.25f;

    /// <summary>In the pilot view, holds the camera this many degrees to the side of the hull's nose; for captures.</summary>
    public float? LookDegrees { get; set; }

    /// <summary>
    /// With <see cref="LookDegrees"/>, the angle the camera turns to, evenly over the time the captures take; for
    /// capturing how a marker moves as the view turns.
    /// </summary>
    public float? LookToDegrees { get; set; }

    /// <summary>Target the nearest other hull once in space; for captures of the target's markers.</summary>
    public bool TargetNearest { get; set; }

    /// <summary>The sector a local game starts in; for scripted screenshots and benchmarks of a particular sector.</summary>
    public SectorCoord SpawnSector { get; set; }
    public int2? WindowSize { get; set; }
}

/// <summary>
/// How a local game is hosted: for the host alone, or with a UDP listener so other players can join it. A screen
/// that lets the player choose reads and writes <see cref="App.NewGameHosting"/>, beside the world settings.
/// </summary>
internal sealed class HostingOptions
{
    /// <summary>Private: nobody can join. Unlisted: joined by address. Public: also shown in the server browser.</summary>
    public ServerVisibility Visibility { get; set; } = ServerVisibility.Private;

    /// <summary>
    /// The game's name, 1 to 48 characters (<see cref="ServerNames"/>). Empty for the default: "Local game" for
    /// a private one, the player's callsign and "game" for one that others can join.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The password others must know to join, or empty for none. Not used by a private game.</summary>
    public string Password { get; set; } = "";

    /// <summary>Player slots, the host included. A private game always has one.</summary>
    public int MaxPlayers { get; set; } = 8;

    /// <summary>The UDP port an unlisted or public game listens on.</summary>
    public ushort Port { get; set; } = ProtocolInfo.DefaultPort;

    /// <summary>A copy, so a screen can edit one without changing the game's until the player confirms.</summary>
    public HostingOptions Clone() => (HostingOptions)MemberwiseClone();
}

/// <summary>
/// The client application: window, renderer, interface and the main loop. The main thread polls input, builds the
/// interface and draws; simulation runs on the session's own threads and reaches this one only as snapshots.
/// </summary>
internal sealed class App : IDisposable
{
    public const string VersionLabel = "PROSPECT 0.1.0";
    private const float BenchWarmupSeconds = 2f;
    private static readonly float4 ClearColor = new(0.0196f, 0.0275f, 0.0392f, 1f);

    private readonly AppOptions _options;
    private readonly GlfwWindow _window;
    private readonly GraphicsDevice _gpu;
    private readonly Swapchain _swapchain;
    private readonly FrameRenderer _renderer;
    private readonly UiContext _ui;
    private readonly ScreenHost _screens;
    private readonly SceneRenderer _scene;
    private readonly Texture2D _galaxyImage;
    private long _lastFrameTimestamp;
    private bool _viewOpened;
    private readonly ScreenshotJob _screenshotJob = new();
    private int2 _framebufferSize;
    private int2 _windowedSize;
    private bool _swapchainDirty;
    private bool _quit;
    private bool _vsync;

    private bool _pointerCaptured;
    private float _sinceSplash = -1f;
    private long _splashFinishedAt;
    private bool _captureRequested;
    private int _capturesTaken;
    private Benchmark? _benchmark;

    public App(AppOptions options)
    {
        _options = options;
        Settings = ClientSettings.Load();
        Jobs = new JobSystem(Math.Clamp(Environment.ProcessorCount - 3, 1, 16), "Job");

        Content = GameContent.Load();
        NewGameSettings = Content.SettingCatalog.Build(null, options.WorldSettingArgs, message => Logs.App.Warning(message));
        NewGameHosting = options.Hosting.Clone();
        if (options.Password is not null && options.JoinHost is null)
            NewGameHosting.Password = options.Password;

        // Nothing is fetched until a screen asks: making the browser opens no connection.
        Listing = ListingConfig.Resolve(options.Listing);
        Browser = new ServerBrowser(Listing);

        // Decode the logo, parse the bindings, compile the shaders and build the meshes on workers while the
        // window and the GPU come up on this thread.
        var logoJob = new DecodeImageJob(AppPaths.Asset("branding", "brutal-logo-white.png"));
        var bindingsJob = new LoadBindingsJob(AppPaths.Content("input", "bindings.json"));
        Jobs.Schedule(logoJob, 4);
        Jobs.Schedule(bindingsJob);

        var shaderJobs = new List<ShaderJob>();
        foreach (var file in Directory.GetFiles(AppPaths.Asset("shaders")))
        {
            if (Path.GetExtension(file) is ".vert" or ".frag")
                shaderJobs.Add(new ShaderJob(Path.GetFileName(file)));
        }

        var designCount = Content.Designs.Length;
        var meshJobs = new MeshJob[WorldView.MeshCount(designCount)];
        for (var i = 0; i < designCount; i++)
        {
            var design = Content.Designs[i];
            meshJobs[i] = new MeshJob(() => MeshBuilder.Hull(design));
        }

        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
        {
            var index = shape;
            for (var detail = 0; detail < WorldView.AsteroidDetail.Length; detail++)
            {
                var subdivisions = WorldView.AsteroidDetail[detail];
                meshJobs[WorldView.AsteroidMesh(designCount, shape, detail)] = new MeshJob(() => MeshBuilder.Asteroid(index, subdivisions));
            }
        }

        var galaxyJob = new GalaxyImageJob(Content.Galaxy.Shape);
        Jobs.Schedule(galaxyJob, 20);
        meshJobs[WorldView.CubeMesh(designCount)] = new MeshJob(MeshBuilder.Cube);
        foreach (var job in shaderJobs)
            Jobs.Schedule(job, 6);
        foreach (var job in meshJobs)
            Jobs.Schedule(job, 3);
        Jobs.Pump();

        if (!Glfw.Init())
        {
            var code = Glfw.GetError(out var message);
            throw new InvalidOperationException($"Failed to initialize GLFW: {code}, {message}");
        }

        // Scripted runs never wait for the display: with the monitor asleep the compositor stops asking for
        // frames, and a run that presents in step with it would hang instead of taking its screenshot.
        _vsync = options.BenchSeconds <= 0 && options.CapturePath is null && Settings.VSync;
        _windowedSize = options.WindowSize ?? new int2(Settings.WindowWidth, Settings.WindowHeight);
        Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "PROSPECT", Size = _windowedSize });
        _window.SetSizeLimits(new int2(960, 540), new int2(-1, -1));
        if (Settings.Fullscreen && options.WindowSize is null)
            EnterFullscreen();

        _gpu = new GraphicsDevice(_window, options.Validation);
        _framebufferSize = _window.FramebufferSize;
        _swapchain = new Swapchain(_gpu, _framebufferSize, _vsync);
        _renderer = new FrameRenderer(_gpu, _swapchain);
        Devices = new InputDevices(_window) { Muted = options.BenchSeconds > 0 || options.CapturePath is not null };
        _ui = new UiContext(_window, Devices, _gpu, _swapchain, Settings.UiScale);
        GpuLabel = _gpu.DeviceName.ToUpperInvariant();

        Jobs.CompleteAll();
        Actions = bindingsJob.Result!;
        BrutalLogo = Texture2D.FromRgba(_gpu, logoJob.Width, logoJob.Height, logoJob.Pixels);
        BrutalLogoRef = _ui.Register(BrutalLogo);
        _galaxyImage = Texture2D.FromRgba(_gpu, GalaxyImage.Size, GalaxyImage.Size, galaxyJob.Pixels);
        GalaxyImageRef = _ui.Register(_galaxyImage);

        var shaders = new Dictionary<string, byte[]>();
        foreach (var job in shaderJobs)
            shaders[job.FileName] = job.Result!;
        var meshes = new MeshData[meshJobs.Length];
        for (var i = 0; i < meshes.Length; i++)
            meshes[i] = meshJobs[i].Result!;
        _scene = new SceneRenderer(_gpu, _swapchain, shaders, meshes);
        World = new WorldView(Devices, Actions, Settings, Jobs, _scene, Content.Designs);

        _screens = new ScreenHost(this, _ui);
        if (options.BrowserSample != BrowserSample.None)
            _screens.UseBrowserSample(options.BrowserSample);
        if (options.BenchSeconds > 0)
            _benchmark = new Benchmark(options.BenchSeconds);
    }

    public ClientSettings Settings { get; }
    public GameContent Content { get; }

    /// <summary>
    /// The world settings the next local game starts with: the content's defaults with whatever <c>--set</c>
    /// changed. A screen that lets the player choose them reads and writes this, or passes its own to
    /// <see cref="StartLocalGame(in WorldSettings)"/>. What each setting is called and may be set to is in
    /// <see cref="GameContent.SettingCatalog"/>.
    /// </summary>
    public WorldSettings NewGameSettings { get; set; }

    /// <summary>
    /// How the next local game is hosted: private unless <c>--host</c> said otherwise. A screen that lets the
    /// player host a game reads and writes this, or passes its own to
    /// <see cref="StartLocalGame(in WorldSettings, HostingOptions, out string)"/>.
    /// </summary>
    public HostingOptions NewGameHosting { get; set; }

    /// <summary>The listing server in use, or null when it is turned off.</summary>
    public ListingConfig? Listing { get; }

    /// <summary>
    /// The list of public games for a server browser. <see cref="ServerBrowser.Refresh"/> starts a fetch and
    /// returns at once; <see cref="ServerBrowser.State"/> and <see cref="ServerBrowser.List"/> are read every
    /// frame without waiting. Join an entry with <see cref="JoinGame(ListedGame, string?, out string)"/>.
    /// </summary>
    public ServerBrowser Browser { get; }
    public WorldView World { get; }
    public JobSystem Jobs { get; }
    public InputDevices Devices { get; }
    public ActionMap Actions { get; }
    public FrameStats Stats { get; } = new();
    public Texture2D BrutalLogo { get; }
    public ImTextureRef BrutalLogoRef { get; }

    /// <summary>The galaxy seen from above, the backdrop of the galaxy map.</summary>
    public ImTextureRef GalaxyImageRef { get; }
    public string GpuLabel { get; }
    public GameSession? Session { get; private set; }
    public bool ShowPerformance { get; private set; }

    /// <summary>Process exit code: non-zero when a benchmark missed its targets.</summary>
    public int ExitCode { get; private set; }

    /// <summary>Starts a local game with <see cref="NewGameSettings"/>, hosted as <see cref="NewGameHosting"/> says.</summary>
    public void StartLocalGame() => StartLocalGame(NewGameSettings);

    /// <summary>
    /// Starts a local game in a world with the given settings, which also become the next game's, hosted as
    /// <see cref="NewGameHosting"/> says. A game that cannot be hosted is logged and not started.
    /// </summary>
    public void StartLocalGame(in WorldSettings settings)
    {
        if (!StartLocalGame(settings, NewGameHosting, out var error))
        {
            Logs.App.Error(error);
            Console.Error.WriteLine(error);
        }
    }

    /// <summary>
    /// Starts a local game in a world with the given settings, hosted as <paramref name="hosting"/> says; both
    /// also become the next game's. A private game is the host alone. An unlisted or public one is the same
    /// game with a UDP listener, which other players join by address; a public one also announces itself to
    /// the listing server. Returns false with a sentence for the player when the name or the password cannot
    /// be used or the port cannot be opened; the session that was running has been left either way.
    /// </summary>
    public bool StartLocalGame(in WorldSettings settings, HostingOptions hosting, out string error)
    {
        LeaveSession();
        NewGameSettings = Content.SettingCatalog.Sanitize(settings);
        NewGameHosting = hosting.Clone();

        var hosted = hosting.Visibility != ServerVisibility.Private;
        var name = hosting.Name.Trim().Length > 0 ? hosting.Name : hosted ? $"{Settings.PlayerName}'s game" : "Local game";
        if (hosting.Visibility == ServerVisibility.Public && Listing is null)
            Logs.App.Warning("The game is public but the listing server is turned off: it will not show in any server browser");

        var config = new ServerConfig
        {
            Name = name,
            MaxPlayers = hosted ? Math.Clamp(hosting.MaxPlayers, 1, 256) : 1,
            Settings = NewGameSettings,
            SpawnSector = _options.SpawnSector,
            Visibility = hosting.Visibility,
            Password = hosted && hosting.Password.Length > 0 ? hosting.Password : null,
            Port = hosting.Port,
            Listing = Listing,
        };

        try
        {
            Session = GameSession.StartLocal(config, Content, Settings.PlayerName);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            error = e.Message;
            return false;
        }

        error = "";
        _screens.Show(Screen.Session);
        return true;
    }

    /// <summary>Joins a server by address, without a password.</summary>
    public bool JoinGame(string host, ushort port, out string error) => JoinGame(host, port, null, out error);

    /// <summary>
    /// Joins a server by address. <paramref name="password"/> is null or empty for none. A server that has a
    /// password and is not given one ends the session with <see cref="SessionEndReason.PasswordRequired"/>; a
    /// wrong one ends it with <see cref="SessionEndReason.WrongPassword"/>, and too many wrong ones with
    /// <see cref="SessionEndReason.TooManyAttempts"/>. A screen that sees one of these asks the player and
    /// calls <see cref="RejoinWithPassword"/>.
    /// </summary>
    public bool JoinGame(string host, ushort port, string? password, out string error)
    {
        LeaveSession();
        if (!CheckPassword(ref password, out error))
            return false;

        try
        {
            Session = GameSession.Join(host, port, Content, Settings.PlayerName, password);
        }
        catch (IOException e)
        {
            error = e.Message;
            return false;
        }

        _screens.Show(Screen.Session);
        return true;
    }

    /// <summary>
    /// Joins a game the player picked in the server browser, with a password when the entry says it needs one.
    /// Nothing is sent to a listed game before this.
    /// </summary>
    public bool JoinGame(ListedGame game, string? password, out string error)
    {
        LeaveSession();
        if (!CheckPassword(ref password, out error))
            return false;

        try
        {
            Session = GameSession.Join(game, Content, Settings.PlayerName, password);
        }
        catch (IOException e)
        {
            error = e.Message;
            return false;
        }

        _screens.Show(Screen.Session);
        return true;
    }

    /// <summary>
    /// Joins the server of the current remote session again, this time with a password: for the moment that
    /// session has ended because the server wants one or the one given was wrong.
    /// </summary>
    public bool RejoinWithPassword(string password, out string error)
    {
        if (Session is not { RemoteHost: { } host, RemotePort: var port })
        {
            error = "There is no server to join again.";
            return false;
        }

        return JoinGame(host, port, password, out error);
    }

    private static bool CheckPassword(ref string? password, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(password))
        {
            password = null;
            return true;
        }

        if (Passwords.IsUsable(password))
            return true;
        error = $"A password is 1 to {Passwords.MaxChars} characters.";
        return false;
    }

    public void LeaveSession()
    {
        if (Session is null)
            return;
        Session.Dispose();
        Session = null;
        _screens.Show(Screen.MainMenu);
    }

    public void Quit() => _quit = true;

    /// <summary>Applies vsync, fullscreen and interface-scale settings that changed, and saves them.</summary>
    public void ApplyVideoSettings()
    {
        if (_vsync != Settings.VSync && _benchmark is null)
        {
            _vsync = Settings.VSync;
            _swapchainDirty = true;
        }

        if (Settings.Fullscreen != _window.IsFullscreen)
        {
            if (Settings.Fullscreen)
                EnterFullscreen();
            else
                _window.SetWindowed(new int2(80, 80), _windowedSize);
        }

        _ui.SetUserScale(Settings.UiScale);
        Settings.Save();
    }

    /// <summary>Called once when the framework logo has finished showing.</summary>
    public void SplashFinished()
    {
        _sinceSplash = 0;
        _splashFinishedAt = Stopwatch.GetTimestamp();
        if (_benchmark is not null)
        {
            // The benchmark measures the game alone: never a listener, never the listing server.
            NewGameHosting = new HostingOptions();
            StartLocalGame();
        }
        else if (_options.StartLocalGame)
        {
            // A game that cannot be hosted (port busy, a name or password that cannot be used) opens the New
            // Game screen with the reason, where the player can change what is wrong.
            if (!StartLocalGame(NewGameSettings, NewGameHosting, out var error))
            {
                Logs.App.Error(error);
                _screens.OpenNewGame(error);
            }
        }
        else if (_options.JoinHost is { } host)
        {
            if (!JoinGame(host, _options.JoinPort, _options.Password, out var error))
            {
                Logs.App.Error(error);
                _screens.Show(Screen.Join);
            }
        }
        else if (_options.StartScreen == Screen.Settings)
            _screens.OpenSettings(Screen.MainMenu);
        else if (_options.StartScreen == Screen.NewGame)
            _screens.OpenNewGame();
        else
            _screens.Show(_options.StartScreen);
    }

    public void Run()
    {
        while (!_window.ShouldClose && !_quit)
        {
            var frameStart = Stopwatch.GetTimestamp();
            Stats.BeginFrame();

            Devices.BeginFrame();
            Glfw.PollEvents();
            Devices.PollGamepad();

            var framebuffer = _window.FramebufferSize;
            if (framebuffer.X == 0 || framebuffer.Y == 0)
            {
                // Minimised: nothing to draw into. Idle without spinning.
                Thread.Sleep(50);
                continue;
            }

            if (framebuffer != _framebufferSize || _renderer.NeedsRecreate || _swapchainDirty)
            {
                _renderer.Recreate(framebuffer, _vsync);
                _scene.Resize(_swapchain.Extent);
                _framebufferSize = framebuffer;
                _swapchainDirty = false;
                if (!_window.IsFullscreen)
                    _windowedSize = _window.Size;
            }

            Jobs.Pump();
            HandleGlobalActions();
            if (Session?.Fault is { } fault)
            {
                Logs.App.Error($"The session stopped unexpectedly: {fault}");
                LeaveSession();
            }

            var dt = _lastFrameTimestamp == 0 ? 1f / 60f : Math.Min((float)Stopwatch.GetElapsedTime(_lastFrameTimestamp, frameStart).TotalSeconds, 0.1f);
            _lastFrameTimestamp = frameStart;

            // The world is drawn whenever the session has put the player's hull in space.
            var snapshot = Session?.Client.AcquireSnapshot();
            var inSpace = snapshot is { State: SessionState.Active, OwnHull: >= 0, Galaxy: not null };
            var worldInput = inSpace && _screens.WorldInputEnabled && _window.IsFocused;
            var capture = worldInput && World.Mode == ViewMode.Pilot && !Devices.Muted;
            if (capture != _pointerCaptured)
            {
                _pointerCaptured = capture;
                _ui.SetPointerCaptured(capture);
                Devices.ResetPointer();
            }

            if (inSpace)
            {
                // Pixels for the GPU; the window size is the pointer's and the interface's space (points on Retina).
                var viewport = new float2(_swapchain.Extent.Width, _swapchain.Extent.Height);
                var windowSize = _window.Size;
                var screen = windowSize.X > 0 && windowSize.Y > 0 ? new float2(windowSize.X, windowSize.Y) : viewport;
                World.Update(snapshot!, Session!.Client.Input, dt, worldInput, viewport, screen, _ui.PointerOverInterface);
            }

            _ui.NewFrame();
            _screens.Draw(snapshot);

            if (_renderer.Begin(out var commands))
            {
                if (inSpace)
                    World.Draw(commands, _renderer.FrameIndex, snapshot!, _ui.Time);
                _renderer.BeginPresentPass(ClearColor);
                if (inSpace)
                    _scene.Present(commands, World.ExposureValue);
                _ui.Render(commands);
                _renderer.End();
            }
            else
            {
                _ui.Discard();
            }

            if (_renderer.TakeScreenshot(out var shot))
                StartScreenshotEncode(shot);

            Stats.EndFrame();
            Automate(dt);

            if (!_vsync && Settings.FrameCap > 0 && _benchmark is null)
                PreciseSleep.Until(frameStart + Stopwatch.Frequency / Settings.FrameCap);
        }

        if (!_window.IsFullscreen)
        {
            Settings.WindowWidth = _windowedSize.X;
            Settings.WindowHeight = _windowedSize.Y;
        }

        Settings.Save();
    }

    public void Dispose()
    {
        Session?.Dispose();
        Browser.Dispose();
        Jobs.CompleteAll();
        _gpu.Device.WaitIdle();
        BrutalLogo.Dispose();
        _galaxyImage.Dispose();
        _scene.Dispose();
        _ui.Dispose();
        _renderer.Dispose();
        _swapchain.Dispose();
        _gpu.Dispose();
        Jobs.Dispose();
        _window.Dispose();
        Glfw.Terminate();
    }

    private void HandleGlobalActions()
    {
        var keyboard = !_ui.WantsTextInput;
        if (Actions.Pressed(Devices, InputAction.TogglePerformance, keyboard))
            ShowPerformance = !ShowPerformance;

        if (Actions.Pressed(Devices, InputAction.ToggleFullscreen, keyboard))
        {
            Settings.Fullscreen = !_window.IsFullscreen;
            ApplyVideoSettings();
        }

        if (Actions.Pressed(Devices, InputAction.Screenshot, keyboard))
        {
            var directory = AppPaths.UserData("screenshots");
            Directory.CreateDirectory(directory);
            RequestScreenshot(Path.Combine(directory, $"prospect-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"));
        }
    }

    private void RequestScreenshot(string path)
    {
        if (_screenshotJob.Busy)
            return;
        _screenshotJob.Path = path;
        _renderer.RequestScreenshot();
    }

    private void StartScreenshotEncode(in Screenshot shot)
    {
        _screenshotJob.Busy = true;
        _screenshotJob.Shot = shot;
        Jobs.Schedule(_screenshotJob, 8);
    }

    private void EnterFullscreen()
    {
        var monitor = Glfw.PrimaryMonitor;
        var mode = monitor.VideoMode;
        _window.SetFullscreen(monitor, new int2(mode.Width, mode.Height), mode.RefreshRate);
    }

    /// <summary>Drives the scripted runs: a timed screenshot (<c>--capture</c>) and the benchmark (<c>--bench</c>).</summary>
    private void Automate(float dt)
    {
        if (_sinceSplash < 0)
            return;
        // Wall-clock time, so scripted captures line up with the simulation whatever the frame pacing.
        _sinceSplash = (float)Stopwatch.GetElapsedTime(_splashFinishedAt).TotalSeconds;

        if (_options.OpenView is { } view && !_viewOpened && Session is not null && _sinceSplash > 0.6f)
        {
            _viewOpened = true;
            if (view == "command")
            {
                World.SetMode(ViewMode.Command);
                World.SelectAllOwned();
            }
            else if (view == "info")
                _screens.OpenShipInfo();
            else if (view == "world-settings")
                _screens.RequestWorldSettings();
            else if (view == "menu")
                _screens.OpenSessionMenu();
            else if (view == "galaxy")
                _screens.OpenGalaxyOverview();
            else if (view == "map-menu")
                _screens.OpenMapMenuNear(3, 2);
            else if (view == "map-hover")
                _screens.OpenMapHover();
            else if (view.StartsWith("jump", StringComparison.Ordinal))
            {
                World.FtlPreview = view switch
                {
                    "jump-wormhole" => FtlKind.Wormhole,
                    "jump-rift" => FtlKind.Rift,
                    _ => FtlKind.None,
                };
                Session.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = World.Galaxy!.CenterOf(new SectorCoord(12, 0, -7)) });
            }
            else
                _screens.OpenMap();
        }

        if (_options.LookDegrees is { } look && Session is not null)
        {
            if (_options.LookToDegrees is { } lookTo)
            {
                var span = Math.Max(_options.CaptureEvery * (_options.CaptureCount - 1), 1e-3f);
                look += (lookTo - look) * Math.Clamp((_sinceSplash - _options.CaptureAfter) / span, 0f, 1f);
            }

            World.LookDegrees = look;
        }

        if (_options.TargetNearest && Session is not null && _sinceSplash > 0.6f && World.TargetHull == 0)
            World.TargetNearestHull();

        if (_options.CapturePath is { } path)
        {
            var count = _options.CaptureCount;
            var due = _options.CaptureAfter + _capturesTaken * _options.CaptureEvery;
            if (_capturesTaken < count && _sinceSplash >= due && !_screenshotJob.Busy && !_renderer.ScreenshotPending)
            {
                ShowPerformance |= Session is not null;
                var file = Path.GetFullPath(path);
                if (count > 1)
                {
                    file = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}-{_capturesTaken:D2}{Path.GetExtension(file)}");
                    Console.WriteLine($"capture {_capturesTaken:D2} at {_sinceSplash:F2}s: {World.Own.Jump} {WorldView.JumpProgress(World.Own, World.OwnFlight):F3}");
                }

                _capturesTaken++;
                _captureRequested = true;
                RequestScreenshot(file);
            }
            else if (_captureRequested && _capturesTaken >= count && _sinceSplash >= due - _options.CaptureEvery + 0.25f && !_screenshotJob.Busy && !_renderer.ScreenshotPending)
            {
                _quit = true;
            }
        }

        if (_benchmark is null || Session is null)
            return;

        if (_sinceSplash < BenchWarmupSeconds)
            return;

        _benchmark.Record(Stats);
        if (!_benchmark.Finished)
            return;

        ExitCode = _benchmark.Report(_gpu, _swapchain) ? 0 : 1;
        _quit = true;
    }

    private sealed class DecodeImageJob(string path) : IJob
    {
        public byte[] Pixels { get; private set; } = [];
        public int Width { get; private set; }
        public int Height { get; private set; }

        public void Run()
        {
            using var image = Stb.Load(path, 4);
            Width = image.Width;
            Height = image.Height;
            Pixels = image.AsSpan().ToArray();
        }
    }

    private sealed class GalaxyImageJob(GalaxyShape shape) : IJob
    {
        public byte[] Pixels { get; private set; } = [];

        public void Run() => Pixels = GalaxyImage.Generate(shape);
    }

    private sealed class ShaderJob(string fileName) : IJob
    {
        public string FileName => fileName;
        public byte[]? Result { get; private set; }

        public void Run() => Result = ShaderCompiler.Compile(fileName);
    }

    private sealed class MeshJob(Func<MeshData> build) : IJob
    {
        public MeshData? Result { get; private set; }

        public void Run() => Result = build();
    }

    private sealed class LoadBindingsJob(string path) : IJob
    {
        public ActionMap? Result { get; private set; }

        public void Run() => Result = ActionMap.Load(path);
    }

    /// <summary>Encodes a captured frame to PNG off the main thread.</summary>
    private sealed class ScreenshotJob : IJob
    {
        public volatile bool Busy;
        public string Path = "";
        public Screenshot Shot;

        public void Run()
        {
            try
            {
                var pixels = Shot.Pixels.AsSpan(0, Shot.Width * Shot.Height * 4);
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (Shot.IsBgra)
                        (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                    pixels[i + 3] = 255;
                }

                if (Stb.WritePng(Path, Shot.Width, Shot.Height, 4, pixels))
                    Logs.App.Info($"Screenshot saved to {Path}");
                else
                    Logs.App.Warning($"Could not write screenshot to {Path}");
            }
            finally
            {
                Shot.Release();
                Busy = false;
            }
        }
    }
}
