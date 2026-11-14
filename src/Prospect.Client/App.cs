using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Prospect.Client.Diagnostics;
using Prospect.Client.Input;
using Prospect.Client.Platform;
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
using Prospect.Game.Persistence;
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

    /// <summary>Continue the world played last once the splash is over (<c>--start continue</c>).</summary>
    public bool ContinueWorld { get; set; }

    /// <summary>
    /// The folder of saved worlds (<c>--worlds</c>), or null for the player's own. A scripted run (a capture or a
    /// benchmark) without one saves nothing and reads no saved world, so it never touches the player's saves.
    /// </summary>
    public string? WorldsRoot { get; set; }

    /// <summary>What goes wrong with a world's final save (<c>--save-fault</c>); captures with their own <see cref="WorldsRoot"/> only.</summary>
    public StoreFault SaveFault { get; set; }
    public string? JoinHost { get; set; }
    public ushort JoinPort { get; set; }

    /// <summary>World settings for local games, each as <c>id=value</c>, in the order they were given.</summary>
    public List<string> WorldSettingArgs { get; } = [];

    /// <summary>How local games started in this run are hosted, as <c>--host</c> and its companions set it.</summary>
    public HostingOptions Hosting { get; } = new();

    /// <summary>
    /// The password for <c>--join</c>, or for the game <c>--host</c> starts; null for none. The application
    /// takes it from here when it starts and sets this to null: the launch options are not where it is kept.
    /// The command line or the environment it came from still holds it, and so does the text itself until the
    /// collector frees it; neither can be cleared from here.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The listing server's address, <c>off</c>, or null for the content's default.</summary>
    public string? Listing { get; set; }
    public string? AccountAuthority { get; set; }
    /// <summary>What the server browser shows instead of the listing server's answer; for scripted screenshots.</summary>
    public BrowserSample BrowserSample { get; set; }

    /// <summary>A fixed account view instead of the account service (<c>--account-sample</c>); captures and benchmarks only.</summary>
    public string? AccountSample { get; set; }

    /// <summary>Open the command view or the map once in space; for scripted screenshots.</summary>
    public string? OpenView { get; set; }
    /// <summary>Muted deterministic camera placement around a generated cloud.</summary>
    public string? GasView { get; set; }
    /// <summary>With the outside cloud camera: its distance in the cloud's largest half-extents, and where it moves to.</summary>
    public float? GasDistance { get; set; }
    public float? GasDistanceTo { get; set; }
    /// <summary>Muted production-shader regression fixture, zero when disabled.</summary>
    public int GasFixture { get; set; }
    public float BenchSeconds { get; set; }

    /// <summary>
    /// With a benchmark: how many times the window is resized while it measures (<c>--bench-resize</c>), and how
    /// many times the swapchain and the render targets are rebuilt at the size they have in between, so that
    /// rebuilding them is held to the same targets as any other frame.
    /// </summary>
    public int BenchResizes { get; set; }
    /// <summary>Opt-in private populated diagnostic, restricted to muted scripted runs.</summary>
    public ulong? RtsSeed { get; set; }
    public string? RtsScript { get; set; }
    public VisualBenchmarkScene? VisualBenchmark { get; set; }
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

    /// <summary>With <c>--capture</c>: accept party invitations as they arrive; for captures of the party interface.</summary>
    public bool AcceptInvites { get; set; }

    /// <summary>The sector a local game starts in; for scripted screenshots and benchmarks of a particular sector.</summary>
    public SectorCoord SpawnSector { get; set; }
    /// <summary>The sector whose centre the scripted own jumps (<c>--open jump*</c>) aim at; offset (6, 0, -4) from the current sector when unset.</summary>
    public SectorCoord? JumpTo { get; set; }

    internal Double3 JumpTarget(Galaxy galaxy, Double3 position)
    {
        var from = galaxy.SectorOf(position);
        return galaxy.CenterOf(JumpTo ?? new SectorCoord(from.X + 6, from.Y, from.Z - 4));
    }
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

    /// <summary>The window title of a capture or benchmark run, which a compositor rule can match to hide it.</summary>
    public const string ScriptedWindowTitle = "PROSPECT capture";

    /// <summary>
    /// The Wayland app id and X11 class of a capture or benchmark run. Agent windows begin with <c>agent.</c>, which
    /// the developer's compositor routes to a hidden workspace.
    /// </summary>
    public const string ScriptedAppId = "agent.prospect";
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
    private readonly CatalogueMeshes _catalogueMeshes;

    /// <summary>Whether the acquired catalogue can be drawn safely.</summary>
    public bool MeshesReady { get; private set; }

    /// <summary>A preparation failure shown instead of entering the world.</summary>
    public string? MeshError => _catalogueMeshes.Error;
    private readonly ScreenshotJob _screenshotJob = new();
    private int2 _framebufferSize;
    private int2 _windowedSize;
    private bool _swapchainDirty;
    private bool _targetsDirty;
    private bool _quit;
    private long _frameStart;
    private readonly SessionPump _pump;
    private readonly Func<int2> _pollFrame;
    private bool _vsync;

    private readonly PointerRouter _pointer;
    private ulong _acceptedInvitation;
    private float _sinceSplash = -1f;
    private long _splashFinishedAt;
    private bool _captureRequested;
    private int _capturesTaken;
    private Benchmark? _benchmark;
    private int2 _benchmarkSize;
    private int _benchmarkResizes;
    private PopulatedMeasurement? _populated;
    private readonly RtsCapture? _rts;
    private bool _benchmarkEditRequested, _benchmarkRepairRequested;

    // The run time at which a scripted diagnostic run reached its own end (all captures saved, or the benchmark
    // finished), or null before that. Closing the session afterwards takes more frames, which are not measured.
    private float? _diagnosticEnd;

    public App(AppOptions options)
    {
        _options = options;
        if (options.RtsScript is { } script) { _rts = new RtsCapture(script); options.CaptureCount = _rts.Count; }
        if (options.VisualBenchmark is not null) _populated = new PopulatedMeasurement();
        Settings = ClientSettings.Load();
        Jobs = new JobSystem(Math.Clamp(Environment.ProcessorCount - 3, 1, 16), "Job");

        _catalogueMeshes = new CatalogueMeshes(Jobs);
        Content = GameContent.Load();
        if (options.VisualBenchmark is { } scenario) Content = scenario.PrepareContent(Content);
        NewGameSettings = Content.SettingCatalog.Build(null, options.WorldSettingArgs, message => Logs.App.Warning(message));
        NewGameHosting = options.Hosting.Clone();
        if (options.JoinHost is null)
        {
            // A host's password becomes the next game's, where a hosting screen can change it; a joiner's is
            // handed to the one session it is for, in SplashFinished.
            if (options.Password is not null)
                NewGameHosting.Password = options.Password;
            options.Password = null;
        }

        // Saved worlds live under the user's data folder. A scripted run has none unless it is given a folder of
        // its own, so captures and benchmarks are reproducible and never touch the player's saves.
        var scriptedRun = options.BenchSeconds > 0 || options.CapturePath is not null;
        var worldsRoot = options.WorldsRoot ?? (scriptedRun ? null : AppPaths.UserData("worlds"));
        Worlds = new WorldShelf(worldsRoot is null ? null : new WorldLibrary(worldsRoot), SaveCompatibility.Of(Content));
        Worlds.Refresh();

        // Nothing is fetched until a screen asks: making the browser opens no connection.
        Listing = ListingConfig.Resolve(options.Listing);
        AccountAuthority = options.AccountAuthority is null ? Listing : ListingConfig.Resolve(options.AccountAuthority);
        Browser = new ServerBrowser(Listing);

        // One account for the life of the game, so joining and leaving games keeps the sign-in. A scripted run
        // never opens a browser; nothing is asked of the service until the player signs in.
        var scripted = options.BenchSeconds > 0 || options.CapturePath is not null;
        Accounts = options.AccountSample is { } accountSample
            ? ScreenHost.AccountSample(accountSample)
            : new AccountClient(AccountAuthority, scripted ? static _ => false : BrowserLauncher.Open);

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

        var meshJobs = new MeshJob?[WorldView.MeshCount()];

        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
        {
            var index = shape;
            for (var detail = 0; detail < WorldView.AsteroidDetail.Length; detail++)
            {
                var subdivisions = WorldView.AsteroidDetail[detail];
                meshJobs[WorldView.AsteroidMesh(shape, detail)] = new MeshJob(() => MeshBuilder.Asteroid(index, subdivisions));
            }
        }

        var galaxyJob = new GalaxyImageJob(Content.Galaxy.Shape);
        Jobs.Schedule(galaxyJob, 20);
        for (var shape = 0; shape <= (int)BlockShape.WedgeZMinusMinus; shape++)
        {
            var block = (BlockShape)shape;
            meshJobs[WorldView.BlockMesh(block)] = new MeshJob(() => MeshBuilder.Primitive(block));
        }
        foreach (var job in shaderJobs)
            Jobs.Schedule(job, 6);
        foreach (var job in meshJobs)
            if (job is not null)
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
        // A scripted run carries its own app id, class and title so the developer's compositor can keep it off
        // their screen (see "Checking the client without a person at the screen" in AGENTS.md). The hints must be
        // set before the window first maps; a rule cannot match a name given afterwards.
        var appId = scripted ? ScriptedAppId : "prospect";
        Glfw.PInvoke.WindowHintString(Glfw.Constants.GLFW_WAYLAND_APP_ID, appId);
        Glfw.PInvoke.WindowHintString(Glfw.Constants.GLFW_X11_CLASS_NAME, appId);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = scripted ? ScriptedWindowTitle : "PROSPECT", Size = _windowedSize });
        // BRUTAL's refresh handler reads every window property back from GLFW, the title too, and marshalling the
        // title makes a new string each time the compositor asks for a redraw (an expose, a resize, a change of
        // focus). The game draws every frame anyway, and size, position, focus and closing have callbacks of their
        // own, so the window takes no refresh callback at all.
        Glfw.PInvoke.SetWindowRefreshCallback(_window, null!);
        _window.SetSizeLimits(new int2(960, 540), new int2(-1, -1));
        if (Settings.Fullscreen && options.WindowSize is null)
            EnterFullscreen();

        _gpu = new GraphicsDevice(_window, options.Validation);
        _framebufferSize = _window.FramebufferSize;
        if (options.GasView is not null || options.GasFixture > 0) Logs.App.Info($"Gas capture {options.GasView}, fixture {options.GasFixture}: framebuffer {_framebufferSize.X}x{_framebufferSize.Y}");
        _swapchain = new Swapchain(_gpu, _framebufferSize, _vsync);
        _renderer = new FrameRenderer(_gpu, _swapchain);
        Devices = new InputDevices(_window) { Muted = options.BenchSeconds > 0 || options.CapturePath is not null };
        _ui = new UiContext(_window, Devices, _gpu, _swapchain, Settings.UiScale);
        // Input events reach GLFW and Dear ImGui functions nothing else calls (the pointer leaving the window, a
        // change of focus), and the first resize is the first time a render target is freed through the memory
        // allocator; bound now, their first call costs the frame loop nothing. See NativeBindings.
        var prelinked = NativeBindings.Prelink(typeof(Glfw).Assembly, typeof(ImGui).Assembly, typeof(VkDevice).Assembly, typeof(VmaAllocation).Assembly);
        Logs.App.Debug($"Bound {prelinked} native imports of GLFW, Dear ImGui, Vulkan and its memory allocator ahead of use");
        GpuLabel = _gpu.DeviceName.ToUpperInvariant();

        Jobs.CompleteAll();
        Actions = bindingsJob.Result!;
        _pointer = new PointerRouter(Devices, Actions, _ui);
        BrutalLogo = Texture2D.FromRgba(_gpu, logoJob.Width, logoJob.Height, logoJob.Pixels);
        BrutalLogoRef = _ui.Register(BrutalLogo);
        _galaxyImage = Texture2D.FromRgba(_gpu, GalaxyImage.Size, GalaxyImage.Size, galaxyJob.Pixels);
        GalaxyImageRef = _ui.Register(_galaxyImage);

        var shaders = new Dictionary<string, byte[]>();
        foreach (var job in shaderJobs)
            shaders[job.FileName] = job.Result!;
        var meshes = new MeshData[meshJobs.Length];
        var emptyShipSlot = new MeshData();
        for (var i = 0; i < meshes.Length; i++)
            meshes[i] = meshJobs[i]?.Result ?? emptyShipSlot;
        _scene = new SceneRenderer(_gpu, _swapchain, shaders, meshes);
        World = new WorldView(Devices, Actions, Settings, Jobs, _scene, Content.Designs);

        _screens = new ScreenHost(this, _ui);
        _pump = new SessionPump(Lifecycle, Worlds, scriptedRun, () => _screens.Show(Screen.Session));
        _pollFrame = PollFrame;
        if (options.BrowserSample != BrowserSample.None)
            _screens.UseBrowserSample(options.BrowserSample);
        if (options.BenchSeconds > 0)
            _benchmark = new Benchmark(options.BenchSeconds, options.BenchResizes);
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

    /// <summary>Account service configuration, separate from game advertisement.</summary>
    public ListingConfig? AccountAuthority { get; }

    /// <summary>
    /// The list of public games for a server browser. <see cref="ServerBrowser.Refresh"/> starts a fetch and
    /// returns at once; <see cref="ServerBrowser.State"/> and <see cref="ServerBrowser.List"/> are read every
    /// frame without waiting. Join an entry with <see cref="JoinGame(ListedGame, string?, out string)"/>.
    /// </summary>
    public ServerBrowser Browser { get; }

    /// <summary>
    /// The player's account and friends at the account service. Lives as long as the game; its token stays in
    /// memory and never reaches a game server. Read <see cref="AccountClient.View"/> every frame without waiting.
    /// </summary>
    public AccountClient Accounts { get; }
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
    /// <summary>
    /// The session the frame reads, or null. A session that is closing, saving or being opened is never this:
    /// see <see cref="Lifecycle"/>.
    /// </summary>
    public GameSession? Session => Lifecycle.Session;

    /// <summary>Opens, closes and replaces the play session, off the frame; every way a session ends goes through it.</summary>
    public SessionLifecycle Lifecycle { get; } = new();

    /// <summary>The saved worlds, read off the frame, for Continue and Load World.</summary>
    public WorldShelf Worlds { get; }

    /// <summary>True when saved worlds are on in this run: a private game is then a saved world.</summary>
    public bool SavesWorlds => Worlds.Library is not null;
    public bool ShowPerformance { get; private set; }

    /// <summary>True while the pointer is hidden and steers the view; the interface then cannot be pointed at.</summary>
    public bool PointerCaptured => _pointer.Captured;

    /// <summary>
    /// True while the pilot holds the release key (Shift by default) and the pointer is shown: the heads-up display
    /// may then be pointed at.
    /// </summary>
    public bool PointerReleased => _pointer.ReleaseHeld && !_pointer.Captured;

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
    /// also become the next game's. A private game is a new saved world when saved worlds are on: created with
    /// its name, seed, settings and spawn sector, and saved automatically from then on. An unlisted or public one
    /// is the same server with a UDP listener, which other players join by address; it is not saved, since a
    /// saved world cannot yet tell one remote player from another. Hosting for others needs a Discord sign-in
    /// (<see cref="NetworkPlay"/>): without one this returns false and the session that runs is left running.
    /// Otherwise the session that runs is closed (and saved) and the new one is opened off the frame; a failure
    /// there opens the New Game screen with the reason.
    /// </summary>
    public bool StartLocalGame(in WorldSettings settings, HostingOptions hosting, out string error)
    {
        NewGameSettings = Content.SettingCatalog.Sanitize(settings);
        NewGameHosting = hosting.Clone();

        var hosted = hosting.Visibility != ServerVisibility.Private;
        var saved = !hosted && Worlds.Library is not null;
        var raw = hosting.Name.Trim().Length > 0 ? hosting.Name : hosted ? $"{Settings.PlayerName}'s game" : saved ? "New world" : "Local game";
        if (!ServerNames.TryNormalize(raw, out var name))
        {
            error = $"A name is 1 to {ServerNames.MaxLength} characters.";
            return false;
        }

        if (hosting.Visibility == ServerVisibility.Public && Listing is null)
            Logs.App.Warning("The game is public but the listing server is turned off: it will not show in any server browser");

        var player = Settings.PlayerName;
        var chosen = NewGameSettings;
        var spawn = _options.SpawnSector;
        Func<CancellationToken, SessionStart> open;
        if (saved)
        {
            var library = Worlds.Library!;
            var autosave = Settings.AutosaveSeconds;
            var fault = _options.SaveFault;
            open = cancel => LocalWorlds.Create(library, Content, name, chosen, spawn, autosave, player, cancel, fault, AccountAuthority);
        }
        else
        {
            // Diagnostic scenes only ever run here, unsaved: Program refuses them with saved-world options.
            var config = new ServerConfig
            {
                Name = name,
                MaxPlayers = hosted ? Math.Clamp(hosting.MaxPlayers, 1, 256) : 1,
                Settings = chosen,
                GalaxySeed = _options.RtsSeed ?? new ServerConfig().GalaxySeed,
                VisualBenchmark = _options.VisualBenchmark,
                SpawnSector = spawn,
                Visibility = hosting.Visibility,
                Password = hosted && hosting.Password.Length > 0 ? hosting.Password : null,
                Port = hosting.Port,
                Listing = Listing,
            };
            open = _ => new SessionStart(GameSession.StartLocal(config, Content, player), "");
        }

        return NetworkPlay.TryEnter(Accounts, hosted, true, () => Lifecycle.Enter(name, null, open, failed => _screens.OpenNewGame(failed)), out error);
    }

    /// <summary>
    /// Continues the world played last. Which one that is is read off the frame; a world that is no longer
    /// there is reported, never made again. A failure shows on the main menu.
    /// </summary>
    public void ContinueWorld()
    {
        if (Worlds.Library is not { } library)
            return;
        var title = Worlds.LastWorld?.Name ?? "Last world";
        var player = Settings.PlayerName;
        var autosave = Settings.AutosaveSeconds;
        Lifecycle.Enter(
            title,
            Worlds.List.LastWorld,
            cancel => LocalWorlds.Continue(library, Content, autosave, player, cancel, _options.SaveFault, AccountAuthority),
            failed => _screens.ShowMenuError(failed)
        );
    }

    /// <summary>Opens a saved world from the list. A failure shows on the Load World screen.</summary>
    public void LoadWorld(WorldEntry world)
    {
        if (Worlds.Library is not { } library)
            return;
        var player = Settings.PlayerName;
        var autosave = Settings.AutosaveSeconds;
        var folder = world.FolderId;
        Lifecycle.Enter(world.Name, folder, cancel => LocalWorlds.Open(library, Content, folder, autosave, player, cancel, _options.SaveFault, AccountAuthority), failed => _screens.ShowLoadError(failed));
    }

    /// <summary>Joins a server by address, without a password.</summary>
    public bool JoinGame(string host, ushort port, out string error) => JoinGame(host, port, null, out error);

    /// <summary>
    /// Joins a server by address. <paramref name="password"/> is null or empty for none. A server that has a
    /// password and is not given one ends the session with <see cref="SessionEndReason.PasswordRequired"/>; a
    /// wrong one ends it with <see cref="SessionEndReason.WrongPassword"/>, and too many wrong ones with
    /// <see cref="SessionEndReason.TooManyAttempts"/>. A screen that sees one of these asks the player and
    /// calls <see cref="RejoinWithPassword"/>. Joining needs a Discord sign-in (<see cref="NetworkPlay"/>); a
    /// refused join, or an unusable password, leaves the session that runs as it was.
    /// </summary>
    public bool JoinGame(string host, ushort port, string? password, out string error)
    {
        if (!CheckPassword(ref password, out error))
            return false;
        var player = Settings.PlayerName;
        return NetworkPlay.TryEnter(
            Accounts,
            true,
            false,
            () => Lifecycle.Enter($"{host}:{port}", null, _ => new SessionStart(GameSession.Join(host, port, Content, player, password), ""), failed => _screens.ShowJoinError(failed)),
            out error
        );
    }

    /// <summary>
    /// Joins a game the player picked in the server browser, with a password when the entry says it needs one.
    /// Nothing is sent to a listed game before this, and nothing at all without a Discord sign-in.
    /// </summary>
    public bool JoinGame(ListedGame game, string? password, out string error)
    {
        if (!CheckPassword(ref password, out error))
            return false;
        var player = Settings.PlayerName;
        return NetworkPlay.TryEnter(
            Accounts,
            true,
            false,
            () => Lifecycle.Enter(game.Name, null, _ => new SessionStart(GameSession.Join(game, Content, player, password), ""), failed => _screens.ShowJoinError(failed)),
            out error
        );
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

    /// <summary>
    /// Leaves the session: a local world is frozen and saved before the client lets go, and the session is
    /// disposed off the frame. The menu shows how that goes. Cancels a session that is still being opened.
    /// </summary>
    public void LeaveSession()
    {
        Lifecycle.Leave();
        _screens.Show(Screen.MainMenu);
    }

    /// <summary>Asks the game to end: the session is closed and saved first, as by <see cref="LeaveSession"/>.</summary>
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
        else if (_options.ContinueWorld)
        {
            ContinueWorld();
        }
        else if (_options.JoinHost is { } host)
        {
            // Without a sign-in (and a sign-in never outlives the game) this opens the Join screen with the address
            // and password filled in, where the player signs in and presses Connect. Nothing joins by itself later.
            var password = _options.Password;
            _options.Password = null;
            if (!JoinGame(host, _options.JoinPort, password, out var error))
            {
                Logs.App.Error(error);
                _screens.OpenJoin(host, _options.JoinPort, password, error);
            }
        }
        else if (_options.StartScreen == Screen.Settings)
            _screens.OpenSettings(Screen.MainMenu);
        else if (_options.StartScreen == Screen.NewGame)
            _screens.OpenNewGame();
        else if (_options.StartScreen == Screen.LoadWorld)
            _screens.OpenLoadWorld();
        else
            _screens.Show(_options.StartScreen);
    }

    public void Run()
    {
        while (true)
        {
            // Closing the window, Quit and the end of a scripted run all close the session the same way: it is
            // saved, then released, and only then does the loop end. The window stays until then.
            if (_window.ShouldClose)
            {
                _window.ShouldClose = false;
                _quit = true;
            }

            // The session moves on whether or not there is anything to draw: a minimised window still saves,
            // closes, opens and quits. See SessionPump.
            var step = _pump.Next(ref _quit, _pollFrame);
            if (step == FrameStep.Exit)
                break;
            if (step == FrameStep.Skip)
            {
                // Minimised: nothing to draw into. Idle without spinning.
                Thread.Sleep(50);
                continue;
            }

            var frameStart = _frameStart;
            var framebuffer = _window.FramebufferSize;
            if (framebuffer != _framebufferSize || _renderer.NeedsRecreate || _swapchainDirty)
            {
                _renderer.Recreate(framebuffer, _vsync);
                _scene.Resize(_swapchain.Extent, _targetsDirty);
                _framebufferSize = framebuffer;
                _swapchainDirty = false;
                _targetsDirty = false;
                if (!_window.IsFullscreen)
                    _windowedSize = _window.Size;
            }

            Jobs.Pump();
            HandleGlobalActions();

            var dt = _lastFrameTimestamp == 0 ? 1f / 60f : Math.Min((float)Stopwatch.GetElapsedTime(_lastFrameTimestamp, frameStart).TotalSeconds, 0.1f);
            _lastFrameTimestamp = frameStart;

            // The world is drawn whenever the session has put the player's hull in space.
            var snapshot = Session?.Client.AcquireSnapshot();
            var designs = snapshot is { State: SessionState.Active } ? snapshot.Content.Designs : null;
            var prepared = _catalogueMeshes.Prepare(designs);
            if (prepared is not null)
            {
                _scene.ReplaceShipMeshes(prepared);
                _catalogueMeshes.Accept(designs!, prepared);
            }

            // A catalogue that grew during play keeps the world on screen: the meshes that are installed stay in
            // use while a worker prepares the longer catalogue, and hulls of the new designs are not drawn, nor
            // sent by the server, until this says they are installed. Only the first catalogue of a session is
            // waited for.
            var installed = _catalogueMeshes.Installed(designs);
            MeshesReady = installed > 0;
            World.InstalledDesigns = installed;
            if (designs is not null && Session is { } playing)
            {
                playing.Client.Input.ReportInstalledDesigns(installed);
                if (installed > 0 && installed < designs.Length && _catalogueMeshes.Error is not null)
                    playing.Client.Input.ReportDesignFailure(designs.Length);
            }

            var inSpace = MeshesReady && snapshot is { State: SessionState.Active, OwnHull: >= 0, Galaxy: not null };
            var focused = _window.IsFocused;

            // Whether the pointer is shown depends on what is open, the release key and focus; a frame in which
            // world input is merely blocked keeps it hidden. The router also starts the interface frame, so the
            // shortcuts and world below use this frame's pointer ownership. See PointerRouter.
            var pointerFrame = new PointerFrame
            {
                InSpace = inSpace,
                Focused = focused,
                SurfaceOpen = !_screens.WorldInputEnabled,
                CommandView = World.PointerView,
            };
            _pointer.Update(pointerFrame);
            _screens.HandleControlsShortcut(inSpace && focused);
            _screens.HandleSocialShortcuts(inSpace && focused);
            _screens.HandleSectorInfoShortcut(inSpace && focused);
            // B and Escape for the ship editor, before flight reads the keys; the editor itself opens only when the
            // server allows it, inside the world's update.
            _screens.HandleEditorShortcut(inSpace && focused, snapshot, Session?.Client.Input);
            // A shortcut can open or close a surface after NewFrame; its cursor change belongs to this frame.
            pointerFrame.SurfaceOpen = !_screens.WorldInputEnabled;
            _pointer.RefreshCapture(pointerFrame);
            // A part card held for a drag keeps an interface item active, which claims the keyboard; the world still
            // takes that one press over, as the editor's palette hands it (WorldView.HoldsInterfacePress).
            var worldInput = inSpace && _screens.WorldInputEnabled && focused && !_screens.ControlsHandled && !_screens.SocialHandled
                && !_screens.EditorHandled && (!_ui.WantsKeyboardInput || World.HoldsInterfacePress);
            // Opening or closing an interface consumes its frame, including any simultaneous mouse click.
            worldInput &= !Actions.Pressed(Devices, InputAction.Pause, true)
                && !Actions.Pressed(Devices, InputAction.ToggleMap, true)
                && !Actions.Pressed(Devices, InputAction.ShipInfo, true);

            if (inSpace)
            {
                // Pixels for the GPU; the window size is the pointer's and the interface's space (points on Retina).
                var viewport = new float2(_swapchain.Extent.Width, _swapchain.Extent.Height);
                var windowSize = _window.Size;
                var screen = windowSize.X > 0 && windowSize.Y > 0 ? new float2(windowSize.X, windowSize.Y) : viewport;
                _pointer.BeginWorldMouse(worldInput);
                World.PointerOverPanel = _pointer.PointerOverWindow;
                World.Update(snapshot!, Session!.Client.Input, dt, worldInput, viewport, screen, _pointer.PointerOverInterface);
                _pointer.EndWorldMouse();
                // The command key and taking control change the view inside the update; the pointer follows in
                // the same frame, quarantining whatever that poll still held.
                pointerFrame.CommandView = World.PointerView;
                _pointer.RefreshCapture(pointerFrame);
            }

            _screens.Draw(snapshot);
            // A menu opened during drawing releases the pointer before this frame is presented.
            if (!_screens.WorldInputEnabled)
                _pointer.SurfaceOpened();

            var worldDrawn = false;
            if (_renderer.Begin(out var commands))
            {
                if (inSpace)
                {
                    World.Draw(commands, _renderer.FrameIndex, snapshot!, _ui.Time);
                    worldDrawn = true;
                }
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
            Automate(MeshesReady ? snapshot : null, worldDrawn);

            if (!_vsync && Settings.FrameCap > 0 && _benchmark is null)
                PreciseSleep.Until(frameStart + Stopwatch.Frequency / Settings.FrameCap);
        }

        // Measurements end at the last frame of the diagnostic world, not after the session closed and saved. A
        // run that ended any other way reports an end just after its last frame, so a prefix is never complete.
        var measuredEnd = _diagnosticEnd ?? MathF.BitIncrement(_sinceSplash);
        if (_rts is not null && !_rts.Report(_gpu.ValidationEnabled, GraphicsDevice.ValidationErrorCount, measuredEnd, Volatile.Read(ref _screenshotJob.Saved), Volatile.Read(ref _screenshotJob.Failed)))
            ExitCode = 1;
        if (_populated is not null && !_populated.Report(_options.VisualBenchmark!, _gpu, _swapchain, measuredEnd))
            ExitCode = 1;
        if (!_window.IsFullscreen)
        {
            Settings.WindowWidth = _windowedSize.X;
            Settings.WindowHeight = _windowedSize.Y;
        }

        Settings.Save();
    }

    /// <summary>Starts a frame: the frame's clock, the statistics and the input. Returns the framebuffer's size.</summary>
    private int2 PollFrame()
    {
        _frameStart = Stopwatch.GetTimestamp();
        Stats.BeginFrame();
        Devices.BeginFrame();
        Glfw.PollEvents();
        Devices.PollGamepad();
        return _window.FramebufferSize;
    }

    public void Dispose()
    {
        Lifecycle.Dispose();
        Worlds.Wait();
        Browser.Dispose();
        Accounts.Dispose();
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
            AppPaths.CreatePrivateDirectory(directory);
            RequestScreenshot(Path.Combine(directory, $"prospect-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"));
        }
    }

    private void RequestScreenshot(string path)
    {
        if (_screenshotJob.Busy)
            return;
        _screenshotJob.Path = path;
        if (_options.GasFixture == GasShaderFixture.OverlayCheck)
            File.Delete(path + ".json");
        _renderer.RequestScreenshot();
    }

    private void StartScreenshotEncode(in Screenshot shot)
    {
        _screenshotJob.Busy = true;
        _screenshotJob.Shot = shot;
        _screenshotJob.Overlay = _options.GasFixture == GasShaderFixture.OverlayCheck;
        if (_screenshotJob.Overlay)
        {
            var mode = World.LastGasMode;
            _screenshotJob.CaptureIndex = _capturesTaken - 1;
            _screenshotJob.Deferred = mode.Deferred;
            _screenshotJob.HasGas = mode.HasGas;
            _screenshotJob.Shields = mode.Shields;
            _screenshotJob.Opening = World.GasOverlayOpening;
        }
        Jobs.Schedule(_screenshotJob, 8);
    }

    private void EnterFullscreen()
    {
        var monitor = Glfw.PrimaryMonitor;
        var mode = monitor.VideoMode;
        _window.SetFullscreen(monitor, new int2(mode.Width, mode.Height), mode.RefreshRate);
    }

    /// <summary>Drives the scripted runs: a timed screenshot (<c>--capture</c>) and the benchmark (<c>--bench</c>).</summary>
    private void Automate(SessionSnapshot? snapshot, bool worldDrawn)
    {
        if (_sinceSplash < 0)
            return;
        // Wall-clock time, so scripted captures line up with the simulation whatever the frame pacing.
        _sinceSplash = (float)Stopwatch.GetElapsedTime(_splashFinishedAt).TotalSeconds;

        // Nothing is sampled once the diagnostic run has ended: the frames that follow close the session.
        var measuring = _diagnosticEnd is null;
        if (_populated is not null && measuring && snapshot is { State: SessionState.Active })
            _populated.Record(_sinceSplash, Stats, snapshot, World, _scene, Session?.LocalServerStatus?.Loop.LastMs ?? 0, worldDrawn);
        if (_options.VisualBenchmark?.Activity == "repair" && measuring && snapshot is { OwnHull: >= 0 } && Session is not null)
        {
            if (!_benchmarkEditRequested && _sinceSplash > 2)
            {
                _benchmarkEditRequested = true;
                Session.Client.Input.RequestEdit(snapshot.Hulls[snapshot.OwnHull].Id);
            }
            if (!_benchmarkRepairRequested && _sinceSplash > 4 && snapshot.Editor.Phase == EditorPhase.Open)
            {
                _benchmarkRepairRequested = true;
                Session.Client.Input.RepairEdit(snapshot.Editor.Token);
            }
        }

        if (_options.OpenView is { } view && !_viewOpened && Session is not null && _sinceSplash > 0.6f)
        {
            _viewOpened = true;
            if (view == "command")
            {
                World.SetMode(ViewMode.Command);
                World.SelectAllOwned();
            }
            else if (view == "command-fog")
            {
                // The command view pulled back far enough to show the sensor rings and the fog beyond them.
                World.SelectAllOwned();
                World.SetCommandCapturePose(World.OwnPosition, 14000f, 0.6f, -0.95f);
            }
            else if (view == "controls")
                _screens.OpenControls();
            else if (view == "edit" && snapshot is { OwnHull: >= 0 })
                World.Editor.Request(snapshot.Hulls[snapshot.OwnHull], Session.Client.Input);
            else if (view == "info")
                _screens.OpenShipInfo();
            else if (view == "world-settings")
                _screens.RequestWorldSettings();
            else if (view == "menu")
                _screens.OpenSessionMenu();
            else if (view == "leave")
                LeaveSession();
            else if (view == "players")
                _screens.OpenSocial(false);
            else if (view == "social")
                _screens.OpenSocial(true);
            else if (view == "friends")
                _screens.OpenFriends();
            else if (view == "chat")
                _screens.OpenChat();
            else if (view == "hud")
            {
                // The world with only what is always shown: the navigation bar, the party list, the passive chat
                // lines and any pinned social window.
            }
            else if (view == "galaxy")
                _screens.OpenGalaxyOverview();
            else if (view == "map-menu")
                _screens.OpenMapMenuNear(3, 2);
            else if (view == "map-hover")
                _screens.OpenMapHover();
            else if (view == "map-card")
                _screens.OpenMapCard();
            else if (view == "map-tilt")
                _screens.OpenMapTilt();
            else if (view == "scan")
                _screens.OpenScan();
            else if (view == "jump-fleet-ahead" && snapshot is { OwnHull: >= 0 })
            {
                // The player's other hulls jump to points ahead of and above the camera, 6 km and 10 km away along
                // nearly the same line of sight, so the pilot sees their exit openings overlap.
                var forward = World.CameraForward;
                var up = World.CameraUp;
                var right = World.CameraRight;
                var lane = 0;
                for (var i = 0; i < snapshot.HullCount; i++)
                {
                    ref var hull = ref snapshot.Hulls[i];
                    if (hull.IsOwn || hull.Owner != snapshot.PlayerId)
                        continue;
                    var distance = 6000f + 4000f * lane;
                    var offset = forward * distance + up * (distance * 0.3f) + right * (4f * lane++);
                    Session.Client.Input.IssueFleetOrder(hull.Id, new HullOrder { Kind = OrderKind.Jump, Target = World.CameraPosition + new Double3(offset.X, offset.Y, offset.Z) });
                }
            }
            else if (view == "jump-fleet" && snapshot is { OwnHull: >= 0 })
            {
                // The player's other hulls make short jumps side by side to 6 km along -x from the pod, so another
                // player's client can watch them leave and arrive from the server's snapshots. The second player
                // to join a server starts 160 m along -x of the first.
                var from = snapshot.Hulls[snapshot.OwnHull].Position;
                var lane = 0;
                for (var i = 0; i < snapshot.HullCount; i++)
                {
                    ref var hull = ref snapshot.Hulls[i];
                    if (hull.IsOwn || hull.Owner != snapshot.PlayerId)
                        continue;
                    var target = from + new Double3(-6000, 0, -60 + 120 * lane++);
                    Session.Client.Input.IssueFleetOrder(hull.Id, new HullOrder { Kind = OrderKind.Jump, Target = target });
                }
            }
            else if (view.StartsWith("jump", StringComparison.Ordinal))
            {
                World.FtlPreview = view switch
                {
                    "jump-wormhole" => FtlKind.Wormhole,
                    "jump-rift" => FtlKind.Rift,
                    _ => FtlKind.None,
                };
                if (snapshot is { OwnHull: >= 0 })
                {
                    var position = snapshot.Hulls[snapshot.OwnHull].Position;
                    var target = _options.JumpTarget(snapshot.Galaxy!, position);
                    var eligibility = snapshot.Exploration.JumpEligibility(snapshot.Galaxy!, position, target, snapshot.Flight, snapshot.Content.Exploration);
                    if (eligibility.CanIssueOrder)
                        Session.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
                    else
                        Logs.App.Warning(eligibility.Reason switch
                        {
                            JumpRefusal.TooClose => "Scripted jump refused: destination is too close",
                            JumpRefusal.TooFar => "Scripted jump refused: destination is too far",
                            JumpRefusal.NoDrive => "Scripted jump refused: no working drive",
                            _ => "Scripted jump refused: invalid destination",
                        });
                }
            }
            else
                _screens.OpenMap();
        }

        World.GasView = Devices.Muted ? _options.GasView : null;
        World.GasFixture = Devices.Muted ? _options.GasFixture : 0;
        World.GasDistance = Devices.Muted ? _options.GasDistance : null;
        if (World.GasDistance is { } gasFrom && _options.GasDistanceTo is { } gasTo)
        {
            // Equal ratios per capture, so a sweep spends as many captures on each level of detail's hand-over.
            var span = Math.Max(_options.CaptureEvery * (_options.CaptureCount - 1), 1e-3f);
            World.GasDistance = gasFrom * MathF.Pow(gasTo / gasFrom, Math.Clamp((_sinceSplash - _options.CaptureAfter) / span, 0f, 1f));
        }
        // Advance only after the first image is saved, never from elapsed capture time.
        World.GasOverlayOpening = _options.GasFixture == GasShaderFixture.OverlayCheck
            && Volatile.Read(ref _screenshotJob.Saved) > 0;
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

        // Scripted captures of the party interface answer an invitation the way a click on Accept does: with the
        // identity of the invitation shown in this frame's snapshot.
        if (_options.AcceptInvites && Session is not null && snapshot is { State: SessionState.Active } && snapshot.Social.Invitation.Player != 0
            && snapshot.Social.Invitation.InvitationId != _acceptedInvitation)
        {
            _acceptedInvitation = snapshot.Social.Invitation.InvitationId;
            Session.Client.Input.AcceptInvitation(_acceptedInvitation);
        }

        var rtsReady = true;
        if (_rts is not null && measuring && snapshot is { State: SessionState.Active })
        {
            _rts.Record(World, _sinceSplash, Stats, (int)_swapchain.Extent.Width, (int)_swapchain.Extent.Height, worldDrawn);
            rtsReady = worldDrawn && _rts.Advance(World, _capturesTaken, _sinceSplash);
        }
        else if (_rts is not null)
            rtsReady = false;

        if (_options.CapturePath is { } path)
        {
            var count = _options.CaptureCount;
            var due = _options.CaptureAfter + _capturesTaken * _options.CaptureEvery;
            if (_capturesTaken < count && rtsReady && _sinceSplash >= due && !_screenshotJob.Busy && !_renderer.ScreenshotPending)
            {
                ShowPerformance |= Session is not null && _options.OpenView is not ("controls" or "players" or "social" or "friends" or "chat" or "hud");
                var file = Path.GetFullPath(path);
                if (count > 1)
                {
                    file = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}-{_capturesTaken:D2}{Path.GetExtension(file)}");
                    Console.WriteLine($"capture {_capturesTaken:D2} at {_sinceSplash:F2}s: {World.Own.Jump} {WorldView.JumpProgress(World.Own, World.OwnFlight):F3}");
                }

                // What the gas levels of detail drew in the frame before this capture; read by scripted checks.
                var gasDraws = World.GasDraws;
                if (gasDraws != default)
                    Console.WriteLine($"capture {_capturesTaken:D2} gas draws: full {gasDraws.Full}, middle {gasDraws.Middle}, far {gasDraws.Far}; composed {World.LastGasMode.Deferred}");
                _capturesTaken++;
                _captureRequested = true;
                RequestScreenshot(file);
            }
            else if (_captureRequested && _capturesTaken >= count && _sinceSplash >= due - _options.CaptureEvery + 0.25f && !_screenshotJob.Busy && !_renderer.ScreenshotPending)
            {
                EndDiagnosticRun();
            }
        }

        if (_benchmark is null || Session is null)
            return;

        if (_sinceSplash < BenchWarmupSeconds)
            return;

        _benchmark.Record(Stats, _swapchain.Rebuilds, _scene.TargetRebuilds);
        if (!_benchmark.Finished)
        {
            if (_benchmark.ResizeDue())
                ResizeForBenchmark();
            else if (_benchmark.RebuildDue())
                _swapchainDirty = _targetsDirty = true;
            return;
        }

        if (_populated is null)
            ExitCode = _benchmark.Report(_gpu, _swapchain) ? 0 : 1;
        EndDiagnosticRun();
    }

    /// <summary>
    /// Gives the window another size, as a compositor or a drag of its edge would. The next frame finds a
    /// framebuffer of another size and rebuilds the swapchain and the render targets, inside the measurement. A
    /// window manager may keep the window as it is (a tiled window under X11); the benchmark counts what happened.
    /// </summary>
    private void ResizeForBenchmark()
    {
        if (_benchmarkResizes == 0)
            _benchmarkSize = _window.Size;
        var size = _benchmarkSize;
        // Never the size it has: the last resize returns to the first size, the others take two smaller ones in
        // turn (or larger ones, at the smallest size a window may have).
        var last = ++_benchmarkResizes == _options.BenchResizes && _benchmarkResizes > 1;
        if (!last)
        {
            var step = _benchmarkResizes % 2 == 1 ? 2 : 1;
            var grow = size.X - 128 < 960 || size.Y - 72 < 540 ? 1 : -1;
            size = new int2(size.X + grow * 64 * step, size.Y + grow * 36 * step);
        }

        // Not through GlfwWindow.Size: it skips a size equal to the one it remembers, and on Wayland it remembers
        // the size before the last one set here, since GLFW reports no size for a resize the program asked for.
        Glfw.PInvoke.SetWindowSize(_window, size.X, size.Y);
    }

    /// <summary>
    /// Ends a scripted run at this frame: what it measured ends here, at the frame of its last sample, and the
    /// session is then closed as by Quit. Later calls while the session closes keep the first end.
    /// </summary>
    private void EndDiagnosticRun()
    {
        _diagnosticEnd ??= _sinceSplash;
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

        // PNGs written and PNGs that failed, for a scripted run's report and the gas overlay check; read with
        // Volatile.Read. With Overlay, a PNG counts as saved only once its metadata is written too.
        public int Saved;
        public int Failed;

        // The gas overlay check's metadata: the mode of the frame this shot was taken from, and this run's identity.
        public bool Overlay, Deferred, HasGas, Opening;
        public int CaptureIndex, Shields;
        private readonly Guid _run = Guid.NewGuid();

        public void Run()
        {
            var saved = false;
            try
            {
                var pixels = Shot.Pixels.AsSpan(0, Shot.Width * Shot.Height * 4);
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (Shot.IsBgra)
                        (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                    pixels[i + 3] = 255;
                }

                if (!Stb.WritePng(Path, Shot.Width, Shot.Height, 4, pixels))
                    Logs.App.Warning($"Could not write screenshot to {Path}");
                else if (Overlay && !TryWriteOverlayMetadata())
                    Logs.App.Warning($"Screenshot written to {Path}, but not its metadata: it counts as failed");
                else
                {
                    saved = true;
                    Logs.App.Info($"Screenshot saved to {Path}");
                }
            }
            finally
            {
                if (saved)
                    Interlocked.Increment(ref Saved);
                else
                    Interlocked.Increment(ref Failed);
                Shot.Release();
                Busy = false;
            }
        }

        /// <summary>
        /// Writes the overlay check's sidecar after the PNG: the hash is of the file as saved. A sidecar that cannot
        /// be written is removed, so the checker finds no evidence rather than stale or partial evidence.
        /// </summary>
        private bool TryWriteOverlayMetadata()
        {
            var sidecar = Path + ".json";
            try
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path)));
                var metadata = new { Run = _run, Index = CaptureIndex, Deferred, HasGas, Shields, Opening, Saved = true, Sha256 = hash };
                File.WriteAllText(sidecar, System.Text.Json.JsonSerializer.Serialize(metadata));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logs.App.Warning($"Could not write {sidecar}: {e.Message}");
                try
                {
                    File.Delete(sidecar);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    Logs.App.Warning($"Could not remove {sidecar}: {cleanup.Message}");
                }

                return false;
            }
        }
    }
}
