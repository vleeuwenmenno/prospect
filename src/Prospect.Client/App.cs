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
    /// <summary>Open the command view or the map once in space; for scripted screenshots.</summary>
    public string? OpenView { get; set; }
    public float BenchSeconds { get; set; }
    public string? CapturePath { get; set; }
    public float CaptureAfter { get; set; } = 1.5f;
    public int2? WindowSize { get; set; }
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
    private bool _captureRequested;
    private Benchmark? _benchmark;

    public App(AppOptions options)
    {
        _options = options;
        Settings = ClientSettings.Load();
        Jobs = new JobSystem(Math.Clamp(Environment.ProcessorCount - 3, 1, 16), "Job");

        Content = GameContent.Load();

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

        var meshJobs = new MeshJob[WorldView.MeshCount];
        meshJobs[WorldView.HullMesh] = new MeshJob(() => MeshBuilder.Hull(Content.Pod));
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
        {
            var seed = shape + 1;
            meshJobs[WorldView.AsteroidMeshBase + shape] = new MeshJob(() => MeshBuilder.Asteroid(seed, 4));
            meshJobs[WorldView.AsteroidLodMeshBase + shape] = new MeshJob(() => MeshBuilder.Asteroid(seed, 2));
        }

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

        _vsync = options.BenchSeconds > 0 ? false : Settings.VSync;
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

        var shaders = new Dictionary<string, byte[]>();
        foreach (var job in shaderJobs)
            shaders[job.FileName] = job.Result!;
        var meshes = new MeshData[meshJobs.Length];
        for (var i = 0; i < meshes.Length; i++)
            meshes[i] = meshJobs[i].Result!;
        _scene = new SceneRenderer(_gpu, _swapchain, shaders, meshes);
        World = new WorldView(Devices, Actions, Settings, Jobs, _scene, Content.Pod, meshes[WorldView.HullMesh].BoundingRadius);

        _screens = new ScreenHost(this, _ui);
        if (options.BenchSeconds > 0)
            _benchmark = new Benchmark(options.BenchSeconds);
    }

    public ClientSettings Settings { get; }
    public GameContent Content { get; }
    public WorldView World { get; }
    public JobSystem Jobs { get; }
    public InputDevices Devices { get; }
    public ActionMap Actions { get; }
    public FrameStats Stats { get; } = new();
    public Texture2D BrutalLogo { get; }
    public ImTextureRef BrutalLogoRef { get; }
    public string GpuLabel { get; }
    public GameSession? Session { get; private set; }
    public bool ShowPerformance { get; private set; }

    /// <summary>Process exit code: non-zero when a benchmark missed its targets.</summary>
    public int ExitCode { get; private set; }

    public void StartLocalGame()
    {
        LeaveSession();
        Session = GameSession.StartLocal(new ServerConfig { Name = "Local game", MaxPlayers = 1 }, Content, Settings.PlayerName);
        _screens.Show(Screen.Session);
    }

    public bool JoinGame(string host, ushort port, out string error)
    {
        LeaveSession();
        try
        {
            Session = GameSession.Join(host, port, Content, Settings.PlayerName);
        }
        catch (IOException e)
        {
            error = e.Message;
            return false;
        }

        error = "";
        _screens.Show(Screen.Session);
        return true;
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
        if (_options.StartLocalGame || _benchmark is not null)
            StartLocalGame();
        else if (_options.JoinHost is { } host)
        {
            if (!JoinGame(host, _options.JoinPort, out var error))
            {
                Logs.App.Error(error);
                _screens.Show(Screen.Join);
            }
        }
        else if (_options.StartScreen == Screen.Settings)
            _screens.OpenSettings(Screen.MainMenu);
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
                var viewport = new float2(_swapchain.Extent.Width, _swapchain.Extent.Height);
                World.Update(snapshot!, Session!.Client.Input, dt, worldInput, viewport, _ui.PointerOverInterface);
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
            Automate();

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
        Jobs.CompleteAll();
        _gpu.Device.WaitIdle();
        BrutalLogo.Dispose();
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
    private void Automate()
    {
        if (_sinceSplash < 0)
            return;
        _sinceSplash += Stats.LastFrameMs / 1000f;

        if (_options.OpenView is { } view && !_viewOpened && Session is not null && _sinceSplash > 0.6f)
        {
            _viewOpened = true;
            if (view == "command")
                World.SetMode(ViewMode.Command);
            else if (view == "info")
                _screens.OpenShipInfo();
            else
                _screens.OpenMap();
        }

        if (_options.CapturePath is { } path)
        {
            if (!_captureRequested && _sinceSplash >= _options.CaptureAfter)
            {
                _captureRequested = true;
                ShowPerformance |= Session is not null;
                RequestScreenshot(Path.GetFullPath(path));
            }
            else if (_captureRequested && _sinceSplash >= _options.CaptureAfter + 0.25f && !_screenshotJob.Busy && !_renderer.ScreenshotPending)
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
