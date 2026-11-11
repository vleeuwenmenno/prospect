using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Prospect.Client.Input;
using Prospect.Client.Render;
using Prospect.Client.Settings;
using Prospect.Engine.Jobs;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;
using IJob = Brutal.Concurrency.Jobs.IJob;

namespace Prospect.Client.World;

internal enum ViewMode
{
    /// <summary>Flying the hull from behind it.</summary>
    Pilot,

    /// <summary>Looking down on the same space from a free camera, giving orders.</summary>
    Command,
}

/// <summary>
/// The render thread's side of the world: it turns simulation snapshots into a camera and a list of things to draw.
/// Nothing here is game state. Positions stay in doubles until they are made relative to the camera, which is the
/// origin of everything that reaches the GPU.
/// </summary>
internal sealed class WorldView
{
    public const int MaxSelection = 64;
    private const float FormationSpacing = 45f;
    private PickHull[] _pickHulls = [];
    private readonly PickSector[] _pickSectors = new PickSector[SlotCount];
    private PickHit[] _pickHits = [];
    private PickCycle _pilotCycle;
    private PickCycle _commandCycle;
    private Vector2 _pilotPointer;

    private const int RingXZ = 2;
    private const int RingY = 1;
    // Enough for the block around the camera plus the heart of a jump's destination, generated ahead of arrival.
    private const int SlotCount = (RingXZ * 2 + 1) * (RingXZ * 2 + 1) * (RingY * 2 + 1) + 27;
    private const float JumpGap = 3000f;
    private const float NearPlane = 0.25f;
    private const float PilotFov = 62f * MathF.PI / 180f;
    private const float CommandFov = 48f * MathF.PI / 180f;
    private const float MinCommandDistance = 25f;
    private const float MaxCommandDistance = 90000f;
    private const float FocusSeconds = 0.55f;
    private const float LookRadiansPerPixel = 0.0021f;
    internal const float LookRadiansPerSecond = 1.9f;
    private const float DustCell = 360f;
    private const float Exposure = 1.0f;

    /// <summary>An asteroid smaller than this on screen, in pixels of radius, is not drawn and cannot be picked.</summary>
    private const float RockMinPixels = 0.6f;

    // Which asteroid mesh to draw. By size on screen: the everyday mesh above RockNormalPixels of radius, a coarse
    // one below it and a speck below RockSpeckPixels. The two finest are for large asteroids seen from close by,
    // where the everyday mesh's facets would be tens of metres across and a ship would visibly touch down beside
    // the surface it collides with: an asteroid of at least the given radius switches to them once it stands
    // that tall in the view (1 is filling it).
    private const float RockNormalPixels = 90f;
    private const float RockSpeckPixels = 8f;
    private const float RockCloseRadius = 250f;
    private const float RockCloseNearness = 0.25f;
    private const float RockFineRadius = 1000f;
    private const float RockFineNearness = 0.35f;

    // Where a rift's or wormhole's opening sits, in hull radii: how far ahead of the nose it opens while the drive
    // works, how far behind an arriving hull it closes, and its radius when fully open. The lens bends the view out
    // to about LensReach opening radii, so an opening that far off the view still gets drawn.
    private const float OpeningAhead = 5f;
    private const float OpeningBehind = 2f;
    private const float OpeningRadius = 1.4f;
    private const float LensReach = 4.4f;

    /// <summary>Where another hull's rift or wormhole is drawn this frame, and how its hull is moved along it.</summary>
    private struct PassageState
    {
        public uint Id;
        public bool Seen;
        public Double3 Mouth;
        public Vector3 Axis;
        public Vector3 Up;
        public FtlKind Kind;
        public float Radius;
        public float Seed;
        public float Offset;
        public float Open;
        public float Clip;
    }

    /// <summary>A sector's generated contents and the job that fills them.</summary>
    private sealed class SectorSlot : IJob
    {
        public const int Empty = 0;
        public const int Generating = 1;
        public const int Ready = 2;

        public readonly SectorContent Content = new();

        /// <summary>Per asteroid, how far it has been pushed from its generated position, as of this frame.</summary>
        public Vector3[] Offsets = [];
        public bool Displaced;
        public Galaxy? Galaxy;
        public SectorCoord Coord;
        public volatile int State;

        public void Run()
        {
            Galaxy!.Generate(Coord, Content);
            if (Offsets.Length < Content.AsteroidCount)
                Offsets = new Vector3[Content.Asteroids.Length];
            State = Ready;
        }
    }

    private struct Candidate
    {
        public int Mesh;
        public MeshInstance Instance;
    }

    private readonly InputDevices _devices;
    private readonly ActionMap _actions;
    private readonly ClientSettings _settings;
    private readonly JobSystem _jobs;
    private readonly SceneRenderer _scene;
    // The designs as the session's world evaluates them; the meshes built from them are the same in every world.
    private HullDesign[] _designs;
    private readonly int _asteroidMeshBase;
    private readonly int _meshCount;
    private readonly int _cubeMesh;
    private readonly SectorSlot[] _slots = new SectorSlot[SlotCount];
    private readonly Candidate[] _candidates = new Candidate[SceneRenderer.MaxInstances];
    private readonly int[] _meshCounts;
    private readonly int[] _meshOffsets;
    private readonly DrawBatch[] _batches;
    private readonly uint[] _selected = new uint[MaxSelection];
    private int _selectedCount;
    private readonly RockRef[] _selectedRocks = new RockRef[MaxSelection];
    private int _selectedRockCount;
    private SessionSnapshot? _snapshot;
    private float _frameAlpha;
    private uint _followId;
    private Double3 _focusFrom;
    private float _focusBlend = 1f;
    private Double3 _focusTarget;
    private float _chaseZoom = 1f;
    private uint _ownId;
    private float _deniedAt = -10f;
    private float _time;
    private float2 _leftDragStart;
    private bool _leftDragging;
    private bool _leftMoved;
    private bool _leftSlidesPlane;

    private Galaxy? _galaxy;
    private SectorCoord _streamCenter;
    private SectorCoord _streamAhead;
    private bool _streamDirty = true;
    private SectorCoord _currentCoord = new(int.MinValue, 0, 0);

    // Camera.
    private Quaternion _chaseOrientation = Quaternion.Identity;
    private bool _chaseInitialised;
    private Vector3 _lookDirection = -Vector3.UnitZ;
    private Vector3 _cameraDirection = -Vector3.UnitZ;
    private Vector3 _chaseRight = Vector3.UnitX;
    private bool _freeLook;
    private float _commandBlend;
    private float _commandYaw;
    private float _commandPitch = -0.9f;
    private float _commandDistance = 240f;
    private Double3 _commandFocus;
    private float2 _rightDragStart;
    private bool _rightDragging;
    private bool _rightMoved;
    private Vector3 _right = Vector3.UnitX;
    private Vector3 _up = Vector3.UnitY;
    private Vector3 _forward = -Vector3.UnitZ;
    private float _tanHalfFov = MathF.Tan(PilotFov / 2);
    // The pilot's own jump as drawn: client-side only, worked out each frame from the simulation's stage and
    // progress. The axis is the hull's nose while the drive works, held once the hull is through. The hull is drawn
    // ahead of (or behind) where the simulation has it by the offset, along the axis, so it can be seen flying into
    // the opening and out of the far one; the camera follows it wherever the pilot has it looking, so it passes the
    // openings too. Through is how far the camera has gone past the current opening, eased so that a camera that
    // passes beside one rather than through it dissolves into the far side instead of cutting to it. Travel is how
    // far the walls of the tunnel or the throat have streamed past.
    private Vector3 _jumpAxis = -Vector3.UnitZ;
    private Vector3 _jumpUp = Vector3.UnitY;
    private float _jumpOffset;
    private float _jumpThrough;
    // Jumps of other hulls being drawn from outside, by hull id (0 for a free slot), and the time of the last draw.
    private readonly PassageState[] _passages = new PassageState[32];
    private float _passageTime;
    private bool _jumpThroughExit;
    private float _jumpTravel;
    private float _jumpCover;
    private float _jumpStretch;
    private Double3 _jumpEntry;
    private float _jumpEntryRadius;
    private float _jumpEntryOpen;
    private Double3 _jumpExit;
    private float _jumpExitRadius;
    private float _jumpExitOpen;
    private bool _jumpExitPlaced;
    private float _jumpChase;
    private float _jumpLight;
    private FtlKind _jumpKind;
    private float _jumpStage;
    // The origin's star, held while the drive works, and how far the light has turned to the destination's star.
    // The simulation moves the hull in one tick; the light on it turns while the tunnel or the throat hides the sky.
    private Vector3 _jumpFromSun = Vector3.UnitY;
    private Vector3 _jumpFromSunColor;
    private float _jumpSunBlend = 1f;

    // The render target in pixels, for the GPU and level of detail.
    private float2 _viewport = new(1, 1);

    // The window in interface units (the pointer's and ImGui's space). Equals the viewport except on high-density
    // displays that report window sizes in points, such as Retina screens.
    private float2 _screen = new(1, 1);

    public WorldView(InputDevices devices, ActionMap actions, ClientSettings settings, JobSystem jobs, SceneRenderer scene, HullDesign[] designs)
    {
        _devices = devices;
        _actions = actions;
        _settings = settings;
        _jobs = jobs;
        _scene = scene;
        var boost = actions.Bindings(InputAction.Boost);
        BoostKeyName = boost.IsEmpty ? "BOOST" : boost[0].DisplayName.ToUpperInvariant();
        _designs = designs;
        _asteroidMeshBase = AsteroidMesh(designs.Length, 0, 0);
        _meshCount = MeshCount(designs.Length);
        _cubeMesh = CubeMesh(designs.Length);
        _meshCounts = new int[_meshCount];
        _meshOffsets = new int[_meshCount];
        _batches = new DrawBatch[_meshCount];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new SectorSlot();
    }

    /// <summary>
    /// How finely each level of asteroid detail is meshed, finest first, in icosphere subdivisions: 81,920
    /// triangles for a giant seen from its surface down to 80 for a speck.
    /// </summary>
    public static ReadOnlySpan<int> AsteroidDetail => [6, 5, 4, 2, 1];

    private const int DetailFine = 0;
    private const int DetailClose = 1;
    private const int DetailNormal = 2;
    private const int DetailCoarse = 3;
    private const int DetailSpeck = 4;

    /// <summary>Meshes are numbered: one per hull design, then the asteroid shapes at each level of detail, then the block.</summary>
    public static int MeshCount(int designs) => designs + Galaxy.AsteroidShapes * AsteroidDetail.Length + 1;

    /// <summary>The single-block mesh, used to draw hulls that have lost blocks.</summary>
    public static int CubeMesh(int designs) => designs + Galaxy.AsteroidShapes * AsteroidDetail.Length;

    public static int AsteroidMesh(int designs, int shape, int detail) => designs + detail * Galaxy.AsteroidShapes + shape;

    public ViewMode Mode { get; private set; }

    /// <summary>The hulls selected in the command view, by id.</summary>
    public ReadOnlySpan<uint> Selection => _selected.AsSpan(0, _selectedCount);

    /// <summary>
    /// When set, the pilot's own jump is drawn with this drive's effect whatever drive the hull has. For looking
    /// at each effect from the command line.
    /// </summary>
    public FtlKind FtlPreview { get; set; }

    /// <summary>
    /// When set, the pilot view holds the camera this many degrees to the right of the hull's nose, as free look
    /// would. For captures, which cannot hold a mouse button.
    /// </summary>
    public float? LookDegrees { get; set; }

    /// <summary>The flight figures of the hull the player is flying.</summary>
    public FlightParams OwnFlight => _designs[Math.Min(Own.Design, _designs.Length - 1)].Flight;

    /// <summary>In the pilot view: the hull under the reticle, or zero.</summary>
    public uint HoveredHull { get; private set; }

    /// <summary>In the pilot view: the hull the player has targeted, or zero.</summary>
    public uint TargetHull { get; private set; }

    /// <summary>In the pilot view: the asteroid under the reticle and the targeted asteroid, if any.</summary>
    public RockRef? HoveredRock { get; private set; }
    public RockRef? TargetRock { get; private set; }

    /// <summary>True for a moment after the player tried to take control of a hull that is not theirs.</summary>
    public bool ControlDenied => _time - _deniedAt < 2.5f;

    /// <summary>The asteroids selected in the command view.</summary>
    public ReadOnlySpan<RockRef> SelectedRocks => _selectedRocks.AsSpan(0, _selectedRockCount);

    /// <summary>True while a selection box is being dragged; its corners are in window pixels.</summary>
    public bool BoxSelecting => _leftDragging && _leftMoved && !_leftSlidesPlane;
    public float2 BoxStart => _leftDragStart;
    public float2 BoxEnd => _devices.MousePosition;

    /// <summary>How far the camera has moved from the pilot view (0) to the command view (1).</summary>
    public float CommandBlend => _commandBlend;

    public Double3 CameraPosition { get; private set; }

    /// <summary>The player's hull as drawn this frame, interpolated between simulation ticks.</summary>
    public HullView Own { get; private set; }
    public Double3 OwnPosition { get; private set; }
    public Quaternion OwnOrientation { get; private set; } = Quaternion.Identity;

    /// <summary>
    /// Where the pilot plotted a jump to on the map. The jump waits for them: aimed at it, the boost key starts the
    /// drive. Cleared once the hull is in that sector, or flies another hull.
    /// </summary>
    public Double3? NavTarget { get; private set; }

    /// <summary>Whether a jump ordered from the map now goes to the hull being flown alone, which only plots it.</summary>
    public bool JumpPlotsOwnOnly => _selectedCount == 1 && _selected[0] == Own.Id;

    /// <summary>Whether the nose of the hull being flown is on <see cref="NavTarget"/>, close enough to jump.</summary>
    public bool NavAimed { get; private set; }

    /// <summary>When the boost key last asked for a jump while the nose was off the nav target, in seconds.</summary>
    public float NavRefusedAt { get; private set; } = -10f;

    /// <summary>The first key or button bound to boost, as the HUD names it.</summary>
    public string BoostKeyName { get; }

    /// <summary>Seconds since this view started, on the same clock as <see cref="NavRefusedAt"/>.</summary>
    public float Time => _time;

    /// <summary>How far the camera is past the opening of the pilot's own rift or wormhole that it is passing now.</summary>
    internal float JumpThrough => _jumpThrough;

    /// <summary>Where the player's hull is drawn: where the simulation has it, moved along its own jump.</summary>
    public Double3 OwnDrawnPosition => OwnPosition + _jumpAxis * _jumpOffset;

    /// <summary>True while the player holds the free-look button.</summary>
    public bool FreeLook => _freeLook && Mode == ViewMode.Pilot;

    /// <summary>The sector the player's hull is in.</summary>
    public SectorSummary Sector { get; private set; }
    public string SectorName { get; private set; } = "";
    public Galaxy? Galaxy => _galaxy;
    public Double3 CommandFocus => _commandFocus;
    public float CommandDistance => _commandDistance;
    public bool CommandFollowing => _followId != 0;

    public void SetMode(ViewMode mode)
    {
        _commandCycle.Reset();
        if (mode == Mode)
            return;
        // The router chose mouse permissions for the old mode; consume the switch frame and any held drag.
        if (_devices.ReadingWorldMouse)
        {
            _devices.SuppressMouse = true;
            _devices.CameraDrag = false;
        }
        _leftDragging = _rightDragging = false;
        Mode = mode;
        _lookDirection = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
        if (mode == ViewMode.Command)
        {
            // Start above and behind the hull, looking the way it faces, so the view pulls back rather than cuts.
            var forward = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
            _commandYaw = MathF.Atan2(-forward.X, -forward.Z);
            _commandPitch = -0.85f;
            _commandDistance = 150f;
            _commandFocus = OwnPosition;
            _followId = Own.Id;
            _focusBlend = 1f;
            if (_selectedCount == 0)
                Select(Own.Id, false);
        }
    }

    /// <summary>
    /// Advances the camera, reads the player's controls and keeps the surrounding sectors generated. Call once per
    /// frame while a session is active. <paramref name="inputEnabled"/> is false while a menu or the map has focus.
    /// </summary>
    public void Update(
        SessionSnapshot snapshot,
        PlayerInputBridge bridge,
        float dt,
        bool inputEnabled,
        float2 viewport,
        float2 screen,
        bool pointerOverUi
    )
    {
        _viewport = viewport;
        _screen = screen;
        _snapshot = snapshot;
        _designs = snapshot.Content.Designs;
        var galaxy = snapshot.Galaxy!;
        if (!ReferenceEquals(galaxy, _galaxy))
        {
            _galaxy = galaxy;
            _commandCycle.Reset();
            _pilotCycle.Reset();
            _pickHulls = new PickHull[snapshot.Hulls.Length];
            _pickHits = new PickHit[SlotCount * galaxy.MaxAsteroidsPerSector + snapshot.Hulls.Length];
            _streamDirty = true;
            _chaseInitialised = false;
            _currentCoord = new SectorCoord(int.MinValue, 0, 0);
        }

        ApplyRockDeltas(snapshot);
        _time += dt;

        // Where the hull is right now, between the last two simulation ticks.
        ref var own = ref snapshot.Hulls[snapshot.OwnHull];
        var alpha = TickAlpha(snapshot);
        _frameAlpha = alpha;
        Own = own;

        // A jump moves the hull further in one tick than it could fly; there is nothing in between to show.
        var jumped = own.Position.RelativeTo(own.PreviousPosition).LengthSquared() > JumpGap * JumpGap;
        OwnPosition = jumped ? own.Position : Double3.Lerp(own.PreviousPosition, own.Position, alpha);
        OwnOrientation = Quaternion.Slerp(own.PreviousOrientation, own.Orientation, alpha);

        if (own.Id != _ownId)
        {
            // The player is in a different hull: put the camera behind it and forget the old target and course.
            _ownId = own.Id;
            _chaseInitialised = false;
            TargetHull = 0;
            NavTarget = null;
            if (_followId != 0)
                FocusOn(own.Id);
        }

        var coord = galaxy.SectorOf(OwnPosition);
        if (coord != _currentCoord)
        {
            _currentCoord = coord;
            Sector = galaxy.Summarize(coord);
            SectorName = galaxy.NameOf(coord);
        }

        UpdateNav();

        if (!inputEnabled || _devices.SuppressMouse)
            _leftDragging = _rightDragging = false;
        var keyboard = inputEnabled;
        if (inputEnabled && _actions.Pressed(_devices, InputAction.ToggleCommand, true))
            SetMode(Mode == ViewMode.Pilot ? ViewMode.Command : ViewMode.Pilot);

        var controls = new PlayerControls();
        if (inputEnabled)
        {
            controls.ToggleDampeners = _actions.Pressed(_devices, InputAction.ToggleDampeners, true);
            if (_actions.Pressed(_devices, InputAction.Halt, true))
            {
                if (Mode == ViewMode.Pilot)
                    bridge.IssueOrder(default);
                else
                    OrderSelection(bridge, default, false, false);
            }
        }

        if (inputEnabled && Mode == ViewMode.Pilot)
        {
            controls.Strafe = _actions.Axis(_devices, InputAxis.Strafe, keyboard);
            controls.Vertical = _actions.Axis(_devices, InputAxis.Vertical, keyboard);
            controls.Forward = _actions.Axis(_devices, InputAxis.Forward, keyboard);
            controls.Roll = _actions.Axis(_devices, InputAxis.Roll, keyboard);
            controls.Boost = _actions.Held(_devices, InputAction.Boost, keyboard);

            // Pointer, arrow keys and right stick turn the view, not the hull.
            var invert = _settings.InvertPitch ? -1f : 1f;
            var pointer = _devices.CameraDelta * (LookRadiansPerPixel * _settings.MouseSensitivity);
            var yaw = pointer.X + _actions.Axis(_devices, InputAxis.Yaw, keyboard) * LookRadiansPerSecond * dt;
            var pitch = (-pointer.Y + _actions.Axis(_devices, InputAxis.Pitch, keyboard) * LookRadiansPerSecond * dt) * invert;
            _lookDirection = Vector3.Transform(_lookDirection, Quaternion.CreateFromAxisAngle(_up, -yaw));
            _lookDirection = Vector3.Normalize(Vector3.Transform(_lookDirection, Quaternion.CreateFromAxisAngle(_right, pitch)));

            // Holding the free-look button looks around without committing the hull to anything; letting go
            // returns the view to the nose. Otherwise the hull swings onto the view whenever it is under thrust.
            // A camera drag with the pointer shown (release key held) is free look too.
            var freeLook = _devices.MouseDown(GlfwMouseButton.Right) || _devices.CameraDrag;
            if (_devices.CameraScroll != 0)
                _chaseZoom = Math.Clamp(_chaseZoom * MathF.Pow(0.88f, _devices.CameraScroll), 0.5f, 8f);
            if (_freeLook && !freeLook)
                _lookDirection = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
            _freeLook = freeLook;

            UpdateTargeting(snapshot, bridge);

            var thrusting = controls.Strafe != 0 || controls.Vertical != 0 || controls.Forward != 0;
            controls.Aim = _lookDirection;
            controls.Align = thrusting && !freeLook;

            // A plotted jump starts when the pilot boosts with the nose on it. Off it, boost is boost.
            if (NavAimed && Own.Jump == JumpPhase.None && Own.Order.Kind != OrderKind.Jump && _actions.Pressed(_devices, InputAction.Boost, keyboard))
            {
                bridge.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = NavTarget!.Value });
                controls.Boost = false;
            }
            else if (NavTarget is not null && Own.Jump == JumpPhase.None && _actions.Pressed(_devices, InputAction.Boost, keyboard))
            {
                NavRefusedAt = _time;
            }
        }
        else if (inputEnabled)
        {
            // A hidden pointer hovers nothing: only a pointer the world may use marks what is under it.
            if (_devices.SuppressMouse)
            {
                HoveredHull = 0;
                HoveredRock = null;
            }
            else
            {
                _commandCycle.Observe(new Vector2(_devices.MousePosition.X, _devices.MousePosition.Y));
                var count = PickTargets(snapshot, _devices.MousePosition, false);
                HoveredHull = count == 0 ? 0 : _pickHits[0].Hull;
                HoveredRock = count == 0 ? null : _pickHits[0].Rock;
            }
            UpdateCommandControls(snapshot, bridge, dt, pointerOverUi);
        }

        if (LookDegrees is { } look && Mode == ViewMode.Pilot)
        {
            var nose = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
            var hullUp = Vector3.Transform(Vector3.UnitY, OwnOrientation);
            _lookDirection = Vector3.Transform(nose, Quaternion.CreateFromAxisAngle(hullUp, -look * MathF.PI / 180f));
        }

        // A jump never takes the view: the pilot looks wherever they like through all of it. The server holds the
        // hull's nose on the destination while the drive works; the camera keeps its normal orbit and zoom.

        bridge.Submit(controls);
        UpdateJump(dt);
        UpdateCamera(dt);
        // Which side of the opening the camera is on, from where the camera is this frame.
        UpdateThrough(dt);
        UpdateStream();
    }

    /// <summary>Builds this frame's instances and constants and records the scene.</summary>
    public void Draw(CommandBuffer commands, int frameIndex, SessionSnapshot snapshot, float time)
    {
        var galaxy = _galaxy!;
        // The same moment the camera was placed at. Asking the clock again here would put hulls a fraction of a
        // frame ahead of the camera, which at speed shows as the flown hull shivering against the view.
        var alpha = _frameAlpha;
        var camera = CameraPosition;
        var aspect = _viewport.X / _viewport.Y;
        var candidateCount = 0;
        Array.Clear(_meshCounts);
        var marks = _scene.FtlMarks(frameIndex);
        var markCount = 0;
        var composite = new CompositeConstants { Setup = new float4(Exposure, aspect, time, 0) };
        var openings = new FtlOpenings();
        var passageDt = Math.Clamp(time - _passageTime, 0f, 0.1f);
        _passageTime = time;

        // Hulls.
        for (var i = 0; i < snapshot.HullCount && candidateCount < _candidates.Length; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var design = _designs[Math.Min(hull.Design, _designs.Length - 1)];
            var leap = hull.Position.RelativeTo(hull.PreviousPosition).LengthSquared() > JumpGap * JumpGap;
            var position = (leap ? hull.Position : Double3.Lerp(hull.PreviousPosition, hull.Position, alpha)).RelativeTo(camera);
            // The pilot's own hull is drawn where the jump has carried it, and seen from the inside of its own jump.
            var inside = hull.IsOwn && _commandBlend < 0.5f;
            if (hull.IsOwn)
                position += _jumpAxis * _jumpOffset;
            var orientation = Quaternion.Slerp(hull.PreviousOrientation, hull.Orientation, alpha);

            // Seen from outside, a jumping hull is drawn where its passage has carried it, or not at all while it
            // is between the openings. Its own pilot rides through the tunnel with it.
            if (inside && hull.Jump != JumpPhase.None)
                AddOwnJumpMarks(design.Flight, position, marks, ref markCount);
            else if (!inside && !AddJump(hull, design.Flight, ref position, orientation, camera, aspect, passageDt, marks, ref markCount, ref composite, ref openings))
                continue;
            var material = inside ? 2f : 1f;
            if (!Visible(position, design.Flight.Radius, aspect, out _))
                continue;
            if (hull.Blocks == design.FullMask)
            {
                Add(ref candidateCount, Math.Min(hull.Design, _designs.Length - 1), position, orientation, 1f, design.Color, material);
                continue;
            }

            // A hull that has lost blocks is drawn from the ones it still has, each as its own cube.
            for (var b = 0; b < design.BlockOffsets.Length && candidateCount < _candidates.Length; b++)
            {
                if ((hull.Blocks & 1UL << b) != 0)
                    Add(ref candidateCount, _cubeMesh, position + Vector3.Transform(design.BlockOffsets[b], orientation), orientation, design.BlockSize, design.Color, material);
            }
        }

        EndPassages(camera, aspect, passageDt, ref composite, ref openings);

        // Rocks in the sectors around the camera: the camera's own sector first, so that if the instance buffer
        // ever fills it is the far rocks that go missing.
        var cameraSector = galaxy.SectorOf(camera);
        foreach (var slot in _slots)
        {
            if (slot.State == SectorSlot.Ready && ReferenceEquals(slot.Galaxy, galaxy) && slot.Coord == cameraSector)
                AddRocks(slot, galaxy, camera, aspect, ref candidateCount);
        }

        foreach (var slot in _slots)
        {
            if (slot.State == SectorSlot.Ready && ReferenceEquals(slot.Galaxy, galaxy) && slot.Coord != cameraSector)
                AddRocks(slot, galaxy, camera, aspect, ref candidateCount);
        }

        // Group instances by mesh so each mesh is one draw call: asteroids first, then hulls, so the jump lenses can
        // keep the world as it was before any hull was drawn over it.
        var batchCount = 0;
        var rockBatches = 0;
        var offset = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var mesh = 0; mesh < _meshCount; mesh++)
            {
                var rock = mesh >= _asteroidMeshBase && mesh < _cubeMesh;
                if (rock != (pass == 0))
                    continue;
                _meshOffsets[mesh] = offset;
                if (_meshCounts[mesh] > 0)
                    _batches[batchCount++] = new DrawBatch { Mesh = mesh, FirstInstance = offset, InstanceCount = _meshCounts[mesh] };
                offset += _meshCounts[mesh];
            }

            if (pass == 0)
                rockBatches = batchCount;
        }

        var instances = _scene.Instances(frameIndex);
        for (var i = 0; i < candidateCount; i++)
            instances[_meshOffsets[_candidates[i].Mesh]++] = _candidates[i].Instance;

        var f = 1f / _tanHalfFov;
        var sunColor = galaxy.StarClasses[Sector.StarClass].Color * 1.7f;
        var sunDirection = Sector.SunDirection;
        if (_jumpSunBlend < 1f)
        {
            sunColor = Vector3.Lerp(_jumpFromSunColor * 1.7f, sunColor, _jumpSunBlend);
            var turned = Vector3.Lerp(_jumpFromSun, sunDirection, _jumpSunBlend);
            if (turned.LengthSquared() > 1e-4f)
                sunDirection = Vector3.Normalize(turned);
        }
        var velocity = Own.Velocity * (1f - _commandBlend);
        var sectorSize = galaxy.SectorSize;
        var constants = new FrameConstants
        {
            // Rows of the camera-relative view-projection; written here as the columns GLSL expects.
            ViewProjection0 = new float4(_right.X * f / aspect, -_up.X * f, 0, _forward.X),
            ViewProjection1 = new float4(_right.Y * f / aspect, -_up.Y * f, 0, _forward.Y),
            ViewProjection2 = new float4(_right.Z * f / aspect, -_up.Z * f, 0, _forward.Z),
            ViewProjection3 = new float4(0, 0, NearPlane, 0),
            CameraRight = new float4(_right.X, _right.Y, _right.Z, 0),
            CameraUp = new float4(_up.X, _up.Y, _up.Z, 0),
            CameraForward = new float4(_forward.X, _forward.Y, _forward.Z, 0),
            Lens = new float4(_tanHalfFov, aspect, _viewport.X, _viewport.Y),
            SunDirection = new float4(sunDirection.X, sunDirection.Y, sunDirection.Z, 0),
            SunColor = new float4(sunColor.X, sunColor.Y, sunColor.Z, 0),
            Dust = new float4(Wrap(camera.X, DustCell), Wrap(camera.Y, DustCell), Wrap(camera.Z, DustCell), time),
            Drift = new float4(velocity.X, velocity.Y, velocity.Z, velocity.Length()),
            Grid = new float4(Wrap(camera.X, 1000), Wrap(camera.Z, 1000), Wrap(camera.X, sectorSize), Wrap(camera.Z, sectorSize)),
            GridPlane = new float4((float)(_commandFocus.Y - camera.Y), (float)sectorSize, _commandBlend, Exposure),
            Openings = openings,
        };

        // The player's own jump is seen from the inside, in the pilot view.
        if (_jumpStage > 0f && _commandBlend < 0.5f)
        {
            var flight = _designs[Math.Min(Own.Design, _designs.Length - 1)].Flight;
            var entry = _jumpEntry.RelativeTo(camera);
            var exit = _jumpExit.RelativeTo(camera);
            constants.Ftl = new float4((float)_jumpKind, _jumpStage, JumpProgress(Own, flight), (Own.Id & 0xFF) / 255f);
            constants.FtlAxis = new float4(_jumpAxis.X, _jumpAxis.Y, _jumpAxis.Z, _jumpTravel);
            constants.FtlUp = new float4(_jumpUp.X, _jumpUp.Y, _jumpUp.Z, _jumpKind == FtlKind.Hyperspace ? _jumpCover : _jumpThrough);
            constants.FtlEntry = new float4(entry.X, entry.Y, entry.Z, _jumpEntryRadius);
            constants.FtlExit = new float4(exit.X, exit.Y, exit.Z, _jumpExitRadius);
            constants.FtlShape = new float4(_jumpStretch, _jumpEntryOpen, _jumpExitOpen, _jumpLight);
        }

        // The lenses need the sky kept apart while a rift or a wormhole is in view, from outside or the pilot's own.
        var lensing = composite.Setup.W > 0f || (constants.Ftl.Y > 0f && constants.Ftl.X > (float)FtlKind.Hyperspace);
        _scene.Render(commands, frameIndex, constants, composite, _batches.AsSpan(0, rockBatches), _batches.AsSpan(rockBatches, batchCount - rockBatches), _commandBlend > 0.01f, markCount, lensing);
    }

    public float ExposureValue => Exposure;

    /// <summary>Adds one sector's visible asteroids to the frame's instances.</summary>
    private void AddRocks(SectorSlot slot, Galaxy galaxy, Double3 camera, float aspect, ref int candidateCount)
    {
        var content = slot.Content;
        if (content.AsteroidCount == 0 || !SectorInView(slot, camera, aspect, _viewport.Y, out var center, out var smallest))
            return;

        for (var i = 0; i < content.AsteroidCount && candidateCount < _candidates.Length; i++)
        {
            ref var rock = ref content.Asteroids[i];

            // Largest first: once one is too small to see from here, so are all that follow.
            if (rock.Radius < smallest)
                break;
            var position = center + rock.LocalPosition + (slot.Displaced ? slot.Offsets[i] : Vector3.Zero);
            if (!Visible(position, rock.Radius, aspect, out var pixels) || pixels < RockMinPixels)
                continue;

            // How much of the view the asteroid takes up, from 0 to 1 at its surface.
            var nearness = pixels * _tanHalfFov * 2f / _viewport.Y;
            var detail =
                rock.Radius >= RockFineRadius && nearness > RockFineNearness ? DetailFine
                : rock.Radius >= RockCloseRadius && nearness > RockCloseNearness ? DetailClose
                : pixels > RockNormalPixels ? DetailNormal
                : pixels > RockSpeckPixels ? DetailCoarse
                : DetailSpeck;

            // Bare rock is grey; ore shows through in its own colour, more strongly the richer the rock.
            var shade = 0.2f + 0.05f * (rock.Shape % 4);
            var tint = new Vector3(shade, shade * 0.95f, shade * 0.88f);
            if (rock.Resource >= 0)
                tint = Vector3.Lerp(tint, galaxy.Resources[rock.Resource].Color * 0.55f, 0.25f + 0.5f * rock.Grade);
            Add(ref candidateCount, _asteroidMeshBase + detail * Galaxy.AsteroidShapes + rock.Shape, position, rock.Rotation, rock.Radius, tint, 0f);
        }
    }

    /// <summary>
    /// Whether any of a sector's asteroids can be in view, where the sector's centre is relative to the camera,
    /// and the radius below which its asteroids are under <see cref="RockMinPixels"/> on a view
    /// <paramref name="height"/> pixels tall. A sector's asteroids are sorted largest first, so a loop over them
    /// can stop at the first one smaller than that: a distant field costs its few large rocks, not its thousands.
    /// </summary>
    private bool SectorInView(SectorSlot slot, Double3 camera, float aspect, float height, out Vector3 center, out float smallest)
    {
        center = _galaxy!.CenterOf(slot.Coord).RelativeTo(camera);
        smallest = 0f;

        // An asteroid that has been pushed may have left the sphere its sector was generated in.
        if (slot.Displaced)
            return true;

        var bounds = center + slot.Content.BoundsCenter;
        if (!Visible(bounds, slot.Content.BoundsRadius, aspect, out _))
            return false;
        var nearest = Math.Max(0f, bounds.Length() - slot.Content.BoundsRadius);
        smallest = RockMinPixels * nearest * _tanHalfFov / (height * 0.5f);
        return true;
    }

    /// <summary>Builds borrowed candidate views and uses the same ordered picker for every interaction.</summary>
    private int PickTargets(SessionSnapshot snapshot, float2 pointer, bool pilot)
    {
        var hullCount = 0;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if ((pilot && hull.IsOwn) || (!pilot && hull.Owner != snapshot.PlayerId) || hull.Jump == JumpPhase.Tunnel)
                continue;
            var design = _designs[Math.Min(hull.Design, _designs.Length - 1)];
            var position = TargetPicking.HullPosition(hull.PreviousPosition, hull.Position, _frameAlpha, JumpGap);
            if (hull.IsOwn) position += _jumpAxis * _jumpOffset;
            _pickHulls[hullCount++] = new PickHull(hull.Id, position,
                Quaternion.Slerp(hull.PreviousOrientation, hull.Orientation, _frameAlpha),
                design.BlockOffsets, design.BlockSize, hull.Blocks, design.Flight.Radius);
        }
        var sectorCount = 0;
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy)) continue;
            _pickSectors[sectorCount++] = new PickSector(slot.Coord, _galaxy!.CenterOf(slot.Coord),
                slot.Content.Asteroids, slot.Content.AsteroidCount, slot.Displaced ? slot.Offsets : null,
                slot.Content.BoundsCenter, slot.Content.BoundsRadius);
        }
        var nx = pointer.X / _screen.X * 2 - 1;
        var ny = pointer.Y / _screen.Y * 2 - 1;
        var ray = Vector3.Normalize(_forward + _right * (nx * _tanHalfFov * _screen.X / _screen.Y) - _up * (ny * _tanHalfFov));
        return TargetPicking.Pick(ray, CameraPosition, _forward, _screen.Y / (2 * _tanHalfFov),
            _pickHulls.AsSpan(0, hullCount), _pickSectors.AsSpan(0, sectorCount), _pickHits, RockMinPixels, NearPlane);
    }

    /// <summary>
    /// Projects one asteroid of a loaded sector: its centre in window coordinates, its radius there and its
    /// distance along the view. <paramref name="center"/> is the sector's centre relative to the camera.
    /// </summary>
    private bool RockOnScreen(SectorSlot slot, Vector3 center, int index, out float2 screen, out float pixels, out float depth)
    {
        ref var rock = ref slot.Content.Asteroids[index];
        var position = center + rock.LocalPosition + (slot.Displaced ? slot.Offsets[index] : Vector3.Zero);
        depth = Vector3.Dot(position, _forward);
        if (depth < NearPlane)
        {
            screen = default;
            pixels = 0;
            return false;
        }

        var scale = _screen.Y * 0.5f / (depth * _tanHalfFov);
        screen = new float2(_screen.X * 0.5f + Vector3.Dot(position, _right) * scale, _screen.Y * 0.5f - Vector3.Dot(position, _up) * scale);
        pixels = rock.Radius * scale;
        return true;
    }

    /// <summary>Projects a world position to window coordinates. Returns false when it is behind the camera.</summary>
    public bool Project(Double3 world, out float2 screen)
    {
        var relative = world.RelativeTo(CameraPosition);
        var depth = Vector3.Dot(relative, _forward);
        if (depth < NearPlane)
        {
            screen = default;
            return false;
        }

        var aspect = _screen.X / _screen.Y;
        var x = Vector3.Dot(relative, _right) / (depth * _tanHalfFov * aspect);
        var y = -Vector3.Dot(relative, _up) / (depth * _tanHalfFov);
        screen = new float2((x * 0.5f + 0.5f) * _screen.X, (y * 0.5f + 0.5f) * _screen.Y);
        return true;
    }

    /// <summary>
    /// A world position relative to the camera, in metres along the camera's right, up and forward axes. Unlike
    /// <see cref="Project"/> it has an answer behind the camera too.
    /// </summary>
    public Vector3 ToView(Double3 world)
    {
        var relative = world.RelativeTo(CameraPosition);
        return new Vector3(Vector3.Dot(relative, _right), Vector3.Dot(relative, _up), Vector3.Dot(relative, _forward));
    }

    /// <summary>The point on the command plane under a window position, if the view ray meets the plane.</summary>
    public bool GroundPoint(float2 pointer, out Double3 point)
    {
        var aspect = _screen.X / _screen.Y;
        var nx = pointer.X / _screen.X * 2f - 1f;
        var ny = pointer.Y / _screen.Y * 2f - 1f;
        var direction = Vector3.Normalize(_forward + _right * (nx * _tanHalfFov * aspect) - _up * (ny * _tanHalfFov));

        var height = (float)(_commandFocus.Y - CameraPosition.Y);
        var t = MathF.Abs(direction.Y) > 1e-5f ? height / direction.Y : -1f;
        if (t <= 0 || t > 400000f)
        {
            point = default;
            return false;
        }

        point = CameraPosition + direction * t;
        return true;
    }

    private static float TickAlpha(SessionSnapshot snapshot)
    {
        var elapsed = Stopwatch.GetTimestamp() - snapshot.PublishedAt;
        return Math.Clamp((float)(elapsed * TickLoop.SimulationHz / (double)Stopwatch.Frequency), 0f, 1f);
    }

    /// <summary>
    /// Pilot view: finds the hull under the reticle, targets it on the middle button, and on the switch key either
    /// takes control of the target or, with no target, leaves the hull in a drone.
    /// </summary>
    private void UpdateTargeting(SessionSnapshot snapshot, PlayerInputBridge bridge)
    {
        var center = _screen * 0.5f;
        var count = PickTargets(snapshot, center, true);
        var hovered = count == 0 ? default : _pickHits[0];
        HoveredHull = hovered.Hull;
        HoveredRock = hovered.Rock;
        if (TargetHull != 0 && !TryGetHull(TargetHull, out _)) TargetHull = 0;
        if (TargetRock is { } held && !TryGetRock(held, out _, out _)) TargetRock = null;
        // Pilot input moves the view while the reticle stays fixed in the middle of the window.
        _pilotPointer += new Vector2(_devices.MouseDelta.X, _devices.MouseDelta.Y);
        _pilotCycle.Observe(_pilotPointer);
        if (_devices.MousePressed(GlfwMouseButton.Middle))
        {
            var hit = _pilotCycle.Click(_pickHits.AsSpan(0, count), _pilotPointer, TargetHull, TargetRock);
            TargetHull = hit.Hull;
            TargetRock = hit.Rock;
        }

        if (!_actions.Pressed(_devices, InputAction.SwitchHull, true))
            return;

        if (TargetHull == 0)
        {
            // An asteroid is not something to climb into; only leave in a drone when nothing at all is targeted.
            if (TargetRock is null)
                bridge.RequestControl(0);
        }
        else if (TryGetHull(TargetHull, out var target) && target.Owner == snapshot.PlayerId)
        {
            bridge.RequestControl(TargetHull);
            TargetHull = 0;
        }
        else
        {
            _deniedAt = _time;
        }
    }

    /// <summary>
    /// Adds what a jumping hull shows from outside: hyperspace's light in the world, or for a rift or a wormhole the
    /// openings it flies into and comes out of, which the composite pass bends the view around and draws, with the
    /// hull moved along its passage. Everything is placed from the hull's own position, orientation, stage and
    /// progress, so every observer sees the same jump in the same place, each from where they are, and one who joins
    /// mid-jump sees it as it is. A hull whose jump ends while its opening is open (a cancel) backs out and the
    /// opening closes, rather than either vanishing in a frame. Returns whether the hull itself is drawn.
    /// </summary>
    private bool AddJump(
        in HullView hull,
        in FlightParams flight,
        ref Vector3 position,
        Quaternion orientation,
        Double3 camera,
        float aspect,
        float dt,
        Span<FtlMark> marks,
        ref int markCount,
        ref CompositeConstants composite,
        ref FtlOpenings openings
    )
    {
        var kind = hull.IsOwn && FtlPreview != FtlKind.None ? FtlPreview : flight.Ftl;
        var seed = (hull.Id & 0xFF) / 255f;
        var forward = Vector3.Transform(-Vector3.UnitZ, orientation);
        if (hull.Jump != JumpPhase.None && kind == FtlKind.Hyperspace)
        {
            var stage = EffectStage(hull.Jump);
            var working = stage == 1f;
            var at = position + forward * (flight.Radius * (working ? 3f : -1.5f));
            AddMark(marks, ref markCount, at, flight.Radius * (working ? 4f : 2.5f), kind, stage, JumpProgress(hull, flight), seed, forward);
            return hull.Jump != JumpPhase.Tunnel;
        }

        var index = PassageSlot(hull.Id, hull.Jump != JumpPhase.None && kind is FtlKind.Rift or FtlKind.Wormhole);
        if (index < 0)
            return hull.Jump != JumpPhase.Tunnel;
        ref var slot = ref _passages[index];
        var visible = true;
        if (hull.Jump != JumpPhase.None)
        {
            var passage = Passage(hull.Jump, JumpProgress(hull, flight), flight);
            slot.Mouth = camera + position + forward * passage.Mouth;
            slot.Axis = forward;
            slot.Up = Vector3.Transform(Vector3.UnitY, orientation);
            slot.Kind = kind;
            slot.Radius = flight.Radius;
            slot.Seed = seed;
            slot.Offset = passage.Offset;
            slot.Open = passage.Open;
            slot.Clip = passage.Clip;
            visible = passage.Visible;
        }
        else
        {
            slot.Offset *= MathF.Exp(-5f * dt);
            slot.Open *= MathF.Exp(-4f * dt);
        }

        slot.Seen = true;
        position += slot.Axis * slot.Offset;
        AddOpening(slot, camera, aspect, ref composite, ref openings);
        return visible;
    }

    /// <summary>The slot that follows a hull's passage, a free one for a new passage, or -1.</summary>
    private int PassageSlot(uint id, bool start)
    {
        var free = -1;
        for (var i = 0; i < _passages.Length; i++)
        {
            if (_passages[i].Id == id)
                return i;
            if (free < 0 && _passages[i].Id == 0)
                free = i;
        }

        if (!start || free < 0)
            return -1;
        _passages[free] = new PassageState { Id = id };
        return free;
    }

    /// <summary>
    /// Closes the openings of hulls that are no longer in the snapshot, and frees the slots of passages that have
    /// eased to nothing.
    /// </summary>
    private void EndPassages(Double3 camera, float aspect, float dt, ref CompositeConstants composite, ref FtlOpenings openings)
    {
        for (var i = 0; i < _passages.Length; i++)
        {
            ref var slot = ref _passages[i];
            if (slot.Id == 0)
                continue;
            if (!slot.Seen)
            {
                slot.Offset *= MathF.Exp(-5f * dt);
                slot.Open *= MathF.Exp(-4f * dt);
                AddOpening(slot, camera, aspect, ref composite, ref openings);
            }

            if (slot.Open < 0.01f && MathF.Abs(slot.Offset) < 0.01f)
                slot.Id = 0;
            slot.Seen = false;
        }
    }

    /// <summary>Hands a passage's opening to the composite pass, if it is open and could be in view.</summary>
    private void AddOpening(in PassageState slot, Double3 camera, float aspect, ref CompositeConstants composite, ref FtlOpenings openings)
    {
        if (slot.Open <= 0.001f)
            return;
        var opening = slot.Radius * OpeningRadius;
        var centre = slot.Mouth.RelativeTo(camera);
        if (!ProjectRelative(centre, opening, aspect, out var ndc, out var radius) || radius < 0.002f)
            return;
        var reach = radius * LensReach;
        if (MathF.Abs(ndc.X) * aspect - reach > aspect || MathF.Abs(ndc.Y) - reach > 1f)
            return;
        // The pilot's own opening, seen from the command view, always gets a slot; other hulls take those left.
        var count = (int)composite.Setup.W;
        if (count >= CompositeConstants.MaxLenses && slot.Id != Own.Id)
            return;
        var index = Math.Min(count, CompositeConstants.MaxLenses - 1);
        openings.Set(index, centre, opening, slot.Axis, slot.Up, slot.Kind, slot.Open, slot.Seed, slot.Clip, slot.Radius * 1.2f, slot.Radius * 3f);
        composite.Setup.W = index + 1;
    }

    /// <summary>A hull's passage through a rift or wormhole, as an observer draws it. See <see cref="Passage"/>.</summary>
    internal readonly record struct PassageShape(float Offset, float Mouth, float Open, float Clip, bool Visible);

    /// <summary>
    /// How a hull's jump through a rift or a wormhole is drawn from outside, from its replicated stage and
    /// progress alone. Late in the drive's work the hull accelerates along its nose into the opening ahead, which is
    /// hidden past (clip 1) and closes behind it before the drive fires; at the far end the opening grows behind
    /// where the hull will be, and over the last second of the transit and the arrival the hull comes out of it
    /// (hidden before it, clip -1) and slows to where the server has it. Offset is along the nose from the hull's
    /// position, Mouth where the opening is along it, in metres; Visible is false while it is between the openings.
    /// </summary>
    internal static PassageShape Passage(JumpPhase phase, float progress, in FlightParams flight)
    {
        var radius = flight.Radius;
        var ahead = radius * OpeningAhead;
        if (phase is JumpPhase.Align or JumpPhase.Charge)
        {
            var start = phase == JumpPhase.Charge ? progress : 0f;
            var through = ahead + radius * 2.2f;
            var move = Math.Clamp((start - 0.6f) / 0.4f, 0f, 1f);
            var offset = through / MathF.Pow(0.925f, 2.2f) * MathF.Pow(move, 2.2f);
            var open = (0.05f + 0.95f * Smooth(start)) * (1f - Smooth((offset - ahead - radius) / (radius * 2f)));
            return new PassageShape(offset, ahead, open, 1f, offset < ahead + radius * 1.5f);
        }

        var tunnel = Math.Max(flight.FtlTunnelSeconds, 0.1f);
        var arrive = Math.Max(flight.FtlArriveSeconds, 0.1f);
        var since = phase == JumpPhase.Tunnel ? progress * tunnel : tunnel + progress * arrive;
        const float Lead = 1f;
        var lead = Math.Min(Lead, tunnel);
        var u = Math.Clamp((since - (tunnel - lead)) / (lead + arrive), 0f, 1f);
        var behind = radius * (OpeningBehind + 2f);
        var exit = phase == JumpPhase.Tunnel ? Smooth(progress) : 1f - Smooth(progress);
        return new PassageShape(-behind * (1f - u) * (1f - u), -radius * OpeningBehind, exit, -1f, u > 0f);
    }

    /// <summary>
    /// The light of the pilot's own hyperspace jump, at the nose. A rift's or wormhole's openings are worked out in
    /// the composite pass from the frame constants.
    /// </summary>
    private void AddOwnJumpMarks(in FlightParams flight, Vector3 position, Span<FtlMark> marks, ref int markCount)
    {
        if ((FtlPreview != FtlKind.None ? FtlPreview : flight.Ftl) != FtlKind.Hyperspace)
            return;
        var stage = EffectStage(Own.Jump);
        var working = stage == 1f;
        var at = position + _jumpAxis * (flight.Radius * (working ? 3f : 1.5f));
        AddMark(marks, ref markCount, at, flight.Radius * (working ? 4f : 2.5f), FtlKind.Hyperspace, stage, JumpProgress(Own, flight), (Own.Id & 0xFF) / 255f, _jumpAxis);
    }

    /// <summary>
    /// Works out how the pilot's own jump is drawn this frame. Everything here eases from one stage into the next,
    /// so nothing in the view changes in a single frame: the world is hidden behind the tunnel or the throat at the
    /// moment the simulation moves the hull, and comes back from behind it.
    /// </summary>
    private void UpdateJump(float dt)
    {
        var own = Own;
        if (own.Jump == JumpPhase.None)
        {
            // A finished jump has already eased everything to rest. A cancelled one has not: the hull and the camera
            // back out of the opening and it closes, rather than everything snapping back in one frame.
            var fade = MathF.Exp(-5f * dt);
            var close = MathF.Exp(-2f * dt);
            _jumpOffset *= fade;
            _jumpCover *= fade;
            _jumpStretch *= fade;
            _jumpLight *= fade;
            _jumpEntryRadius *= close;
            _jumpEntryOpen *= close;
            _jumpExitRadius *= close;
            _jumpExitOpen *= close;
            _jumpExitPlaced = false;
            _jumpSunBlend = 1f;
            var left = Math.Max(Math.Abs(_jumpOffset), Math.Max(Math.Max(_jumpCover, _jumpStretch), Math.Max(_jumpEntryOpen, _jumpExitOpen)));
            if (left < 0.001f)
            {
                _jumpOffset = _jumpTravel = _jumpCover = _jumpStretch = _jumpLight = 0f;
                _jumpEntryRadius = _jumpExitRadius = _jumpEntryOpen = _jumpExitOpen = 0f;
                _jumpStage = 0f;
            }

            return;
        }

        var flight = OwnFlight;
        var kind = FtlPreview != FtlKind.None ? FtlPreview : flight.Ftl;
        var stage = EffectStage(own.Jump);
        var progress = SmoothJumpProgress(own, flight);
        _jumpKind = kind;
        _jumpStage = stage;
        var tunnel = Math.Max(flight.FtlTunnelSeconds, 0.1f);
        var total = tunnel + Math.Max(flight.FtlArriveSeconds, 0.1f);
        // Seconds since the drive fired, through the tunnel and the arrival.
        var since = stage switch
        {
            2f => progress * tunnel,
            3f => tunnel + progress * (total - tunnel),
            _ => 0f,
        };

        // The axis follows the nose until the hull has come out; turning after arrival must not swing the effect.
        if (stage < 3f)
        {
            _jumpAxis = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, OwnOrientation));
            var up = Vector3.Transform(Vector3.UnitY, OwnOrientation);
            _jumpUp = Vector3.Normalize(up - _jumpAxis * Vector3.Dot(up, _jumpAxis));
        }

        if (stage == 1f)
        {
            _jumpFromSun = Sector.SunDirection;
            _jumpFromSunColor = _galaxy!.StarClasses[Sector.StarClass].Color;
        }

        _jumpSunBlend = Smooth(since / 1.2f);

        // Seen from the command view the jump is drawn from outside, where the hull stays where it is.
        var inside = 1f - Smooth(_commandBlend / 0.5f);
        if (kind == FtlKind.Hyperspace)
            UpdateHyperspace(dt, stage, progress, since, tunnel);
        else
            UpdatePassage(dt, own, flight, stage, progress, since, tunnel, total);
        _jumpOffset *= inside;
    }

    /// <summary>Keeps the plotted jump current: gone once the hull is there, and whether the nose is on it.</summary>
    private void UpdateNav()
    {
        if (NavTarget is not { } nav || InSectorOf(Own, nav) || !HasJumpDrive(Own))
        {
            NavTarget = null;
            NavAimed = false;
            return;
        }

        var toward = nav.RelativeTo(OwnPosition);
        var nose = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
        NavAimed = toward.LengthSquared() > 1f && Vector3.Dot(nose, Vector3.Normalize(toward)) >= OwnFlight.FtlAlignCosine;
    }

    /// <summary>
    /// Eases <see cref="_jumpThrough"/> toward whether the camera is past the current opening: the entry while the
    /// drive works, the exit after. It counts as past only once the hull is too, so a camera that the pilot has
    /// looking back from ahead of the hull is not taken into the throat before the hull. Through an opening the
    /// two sides look alike and the easing does not show; beside one it is a short dissolve, never a cut.
    /// </summary>
    private void UpdateThrough(float dt)
    {
        var exit = _jumpStage >= 2f;
        var mouth = exit ? _jumpExit : _jumpEntry;
        var cameraPast = Vector3.Dot(CameraPosition.RelativeTo(mouth), _jumpAxis) > 0f;
        var hullPast = Vector3.Dot(OwnDrawnPosition.RelativeTo(mouth), _jumpAxis) > 0f;
        // A camera the pilot has zoomed far out may not reach an opening before its stage ends; it is taken through
        // anyway, as a dissolve, before the drive fires and before the arrival is over.
        var late = Own.Jump == JumpPhase.None
            || (Own.Jump == JumpPhase.Charge && JumpProgress(Own, OwnFlight) > 0.97f)
            || (Own.Jump == JumpPhase.Arrive && JumpProgress(Own, OwnFlight) > 0.6f);
        var target = (cameraPast && hullPast) || (late && hullPast) ? 1f : 0f;
        if (exit != _jumpThroughExit)
        {
            // A new opening: the camera starts on whichever side of it it is.
            _jumpThroughExit = exit;
            _jumpThrough = target;
            return;
        }

        _jumpThrough += (target - _jumpThrough) * (1f - MathF.Exp(-12f * dt));
    }

    /// <summary>
    /// Hyperspace: the stars stretch into lines from where they are late in the charge, the tunnel forms over them
    /// as the drive fires, and at the far end it thins out again over the destination's stars, which pull back to
    /// points. The tunnel's lines accelerate away from the nose and slow down again before it opens.
    /// </summary>
    private void UpdateHyperspace(float dt, float stage, float progress, float since, float tunnel)
    {
        const float MaxStretch = 1.4f;
        const float TopSpeed = 2.6f;
        _jumpOffset = 0f;
        _jumpEntryRadius = _jumpExitRadius = 0f;
        float speed;
        if (stage == 1f)
        {
            var build = Smooth((progress - 0.55f) / 0.45f);
            _jumpStretch = MaxStretch * build * build;
            _jumpCover = Smooth((progress - 0.7f) / 0.3f);
            var rush = Smooth((progress - 0.7f) / 0.3f);
            speed = TopSpeed * 0.45f * rush * rush;
        }
        else
        {
            _jumpStretch = MaxStretch * (1f - Smooth((since - (tunnel - 0.35f)) / 1.6f));
            _jumpCover = 1f - Smooth((since - (tunnel - 0.7f)) / 1.2f);
            var slow = Smooth((since - (tunnel - 1.1f)) / 1.6f);
            speed = TopSpeed * (0.45f + 0.55f * Smooth(since / 0.6f)) * (1f - slow);
        }

        _jumpTravel += speed * dt;
        _jumpLight = _jumpCover;
    }

    /// <summary>
    /// Rift and wormhole: the opening grows ahead of the nose through the charge. Late in the charge the hull
    /// accelerates into it and the camera follows through; inside is the throat, which hides the moment the
    /// simulation moves the hull. In the tunnel the opening at the far end grows ahead, the hull and the camera come
    /// out of it, and it closes behind them while the hull slows to where it has arrived.
    /// </summary>
    private void UpdatePassage(float dt, in HullView own, in FlightParams flight, float stage, float progress, float since, float tunnel, float total)
    {
        var radius = flight.Radius;
        var full = radius * OpeningRadius;
        var ahead = radius * OpeningAhead;
        _jumpCover = 0f;
        _jumpStretch = 0f;
        float speed;
        if (stage == 1f)
        {
            // How far the camera sits behind the hull, fixed for the jump so the openings can be placed for it.
            _jumpChase = Math.Max(3f, radius * 3.3f) * _chaseZoom;
            _jumpExitPlaced = false;
            _jumpExitRadius = 0f;
            _jumpExitOpen = 0f;

            // The hull starts to move at 60% of the charge and accelerates, so the camera has passed the far side of
            // the opening (and it has closed behind) by 97%, before the drive fires.
            var through = ahead + _jumpChase + full * 2.2f;
            var move = Math.Clamp((progress - 0.6f) / 0.4f, 0f, 1f);
            _jumpOffset = through / MathF.Pow(0.925f, 2.2f) * MathF.Pow(move, 2.2f);

            var open = Smooth(progress / 0.8f) * 0.95f + 0.05f;
            var behind = (_jumpOffset - _jumpChase - ahead - full) / full;
            var close = Smooth(behind);
            _jumpEntry = OwnPosition + _jumpAxis * ahead;
            _jumpEntryOpen = open * (1f - close);
            _jumpEntryRadius = full * _jumpEntryOpen;
            var rush = Smooth((progress - 0.62f) / 0.33f);
            speed = 1.4f * rush * rush;
            _jumpLight = Smooth((progress - 0.75f) / 0.2f);
        }
        else
        {
            // From the moment the drive fires, over the tunnel and the arrival: the hull comes from far behind where
            // it arrives, and slows to a stop there. The far opening is placed so the camera passes through its
            // middle just before the tunnel ends.
            const float Approach = 20f;
            var span = full * Approach;
            var t = since / total;
            _jumpOffset = -span * (1f - t) * (1f - t);
            var cross = Math.Max(0.1f, (tunnel - 0.25f) / total);
            if (!_jumpExitPlaced)
            {
                _jumpExitPlaced = true;
                _jumpEntryRadius = 0f;
                _jumpEntryOpen = 0f;
                _jumpExit = own.Position + _jumpAxis * (-span * (1f - cross) * (1f - cross) - _jumpChase);
            }

            _jumpExitOpen = stage == 2f ? Smooth((since - 0.3f) / 1.6f) * 0.95f + 0.05f : 1f - Smooth(progress);
            _jumpExitRadius = full * _jumpExitOpen;
            speed = 1.4f * (1f - Smooth((since - (tunnel - 0.6f)) / 1.0f));
            _jumpLight = 1f - Smooth((since - (tunnel - 0.6f)) / 0.9f);
        }

        _jumpTravel += speed * dt;
    }

    /// <summary>
    /// Like <see cref="JumpProgress"/>, but carried forward between simulation ticks so motion drawn from it does not
    /// step at the tick rate.
    /// </summary>
    private float SmoothJumpProgress(in HullView hull, in FlightParams flight)
    {
        var tick = _frameAlpha / TickLoop.SimulationHz;
        return Math.Clamp(
            hull.Jump switch
            {
                JumpPhase.Charge => Math.Min(
                    (hull.JumpTimer + flight.FtlPower * tick) / Math.Max(flight.FtlEnergy, 0.001f),
                    flight.FtlCalculateSeconds > 0f ? (hull.JumpCalculation + tick) / flight.FtlCalculateSeconds : 1f
                ),
                JumpPhase.Tunnel => (hull.JumpTimer + tick) / flight.FtlTunnelSeconds,
                JumpPhase.Arrive => (hull.JumpTimer + tick) / flight.FtlArriveSeconds,
                _ => 0f,
            },
            0f,
            1f
        );
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private static void AddMark(Span<FtlMark> marks, ref int count, Vector3 at, float size, FtlKind kind, float stage, float progress, float seed, Vector3 axis)
    {
        if (count < marks.Length)
        {
            marks[count++] = new FtlMark
            {
                PositionSize = new float4(at.X, at.Y, at.Z, size),
                Params = new float4((float)kind, stage, progress, seed),
                Axis = new float4(axis.X, axis.Y, axis.Z, 0),
            };
        }
    }

    /// <summary>Projects a camera-relative point, and a radius around it, to NDC and half screen heights.</summary>
    private bool ProjectRelative(Vector3 relative, float worldRadius, float aspect, out float2 ndc, out float radius)
    {
        var depth = Vector3.Dot(relative, _forward);
        if (depth < NearPlane)
        {
            (ndc, radius) = (default, 0f);
            return false;
        }

        var scale = 1f / (depth * _tanHalfFov);
        ndc = new float2(Vector3.Dot(relative, _right) * scale / aspect, -Vector3.Dot(relative, _up) * scale);
        radius = worldRadius * scale;
        return true;
    }

    /// <summary>The stage number the jump shaders use: 1 drive working, 2 in the tunnel, 3 arriving.</summary>
    private static float EffectStage(JumpPhase phase) =>
        phase switch
        {
            JumpPhase.Align or JumpPhase.Charge => 1f,
            JumpPhase.Tunnel => 2f,
            JumpPhase.Arrive => 3f,
            _ => 0f,
        };

    /// <summary>
    /// How far a hull is through the current stage of its jump, from 0 to 1. While the drive works that is the lesser
    /// of its charge and its calculation, both of which must be done before it fires.
    /// </summary>
    public static float JumpProgress(in HullView hull, in FlightParams flight) =>
        Math.Clamp(
            hull.Jump switch
            {
                JumpPhase.Charge => Math.Min(ChargeFraction(hull, flight), CalculationFraction(hull, flight)),
                JumpPhase.Tunnel => hull.JumpTimer / flight.FtlTunnelSeconds,
                JumpPhase.Arrive => hull.JumpTimer / flight.FtlArriveSeconds,
                _ => 0f,
            },
            0f,
            1f
        );

    /// <summary>How much of the energy its jump needs a hull's drive holds, from 0 to 1.</summary>
    public static float ChargeFraction(in HullView hull, in FlightParams flight) =>
        hull.Jump == JumpPhase.Charge ? Math.Clamp(hull.JumpTimer / Math.Max(flight.FtlEnergy, 0.001f), 0f, 1f) : 0f;

    /// <summary>How far a hull's drive has calculated its jump, from 0 to 1; a drive that needs no time is done.</summary>
    public static float CalculationFraction(in HullView hull, in FlightParams flight) =>
        hull.Jump != JumpPhase.Charge ? 0f
        : flight.FtlCalculateSeconds > 0f ? Math.Clamp(hull.JumpCalculation / flight.FtlCalculateSeconds, 0f, 1f)
        : 1f;

    /// <summary>Where a hull is drawn this frame.</summary>
    public Double3 HullPosition(in HullView hull) => hull.IsOwn ? OwnPosition : hull.Position;

    public bool IsSelected(uint id) => _selected.AsSpan(0, _selectedCount).Contains(id);

    /// <summary>Selects a hull, replacing the selection or adding to it. Adding a selected hull removes it.</summary>
    public void Select(uint id, bool additive)
    {
        _commandCycle.Reset();
        SelectCore(id, additive);
    }

    private void SelectCore(uint id, bool additive)
    {
        _selectedRockCount = 0;
        if (!additive)
        {
            _selected[0] = id;
            _selectedCount = 1;
            return;
        }

        var index = _selected.AsSpan(0, _selectedCount).IndexOf(id);
        if (index >= 0)
            _selected[index] = _selected[--_selectedCount];
        else if (_selectedCount < MaxSelection)
            _selected[_selectedCount++] = id;
    }

    public void ClearSelection()
    {
        _commandCycle.Reset();
        _selectedCount = 0;
        _selectedRockCount = 0;
    }

    /// <summary>Adds a hull to the selection if it is not in it already.</summary>
    public void AddToSelection(uint id)
    {
        _commandCycle.Reset();
        _selectedRockCount = 0;
        if (!IsSelected(id) && _selectedCount < MaxSelection)
            _selected[_selectedCount++] = id;
    }

    /// <summary>
    /// Sends the selected hulls to a point in formation, or the hull being flown if nothing is selected.
    /// </summary>
    public void OrderMove(PlayerInputBridge bridge, Double3 target)
    {
        if (_selectedCount == 0)
            bridge.IssueOrder(new HullOrder { Kind = OrderKind.MoveTo, Target = target });
        else
            OrderSelection(bridge, target, true, false);
    }

    /// <summary>
    /// Sends the selected hulls to a point in another sector by jump drive. Only hulls that can jump there (see
    /// <see cref="CanJumpTo"/>) are ordered; the rest keep their current orders. The hull being flown, if selected,
    /// is not ordered: the point becomes its <see cref="NavTarget"/>, and the pilot starts the jump by aiming at it
    /// and boosting. With nothing selected nothing is sent anywhere.
    /// </summary>
    public void OrderJump(PlayerInputBridge bridge, Double3 target) => OrderSelection(bridge, target, true, true);

    /// <summary>
    /// Counts the hulls a jump order would apply to, the selected ones: how many there are, how many can jump to
    /// <paramref name="target"/>, and how many are already in its sector.
    /// </summary>
    public void CountJumpReady(Double3 target, out int ships, out int ready, out int inSector)
    {
        ships = 0;
        ready = 0;
        inSector = 0;
        for (var i = 0; i < _selectedCount; i++)
        {
            if (TryGetHull(_selected[i], out var hull))
                Count(hull, target, ref ships, ref ready, ref inSector);
        }
    }

    private void Count(in HullView hull, Double3 target, ref int ships, ref int ready, ref int inSector)
    {
        ships++;
        if (InSectorOf(hull, target))
            inSector++;
        else if (HasJumpDrive(hull))
            ready++;
    }

    /// <summary>True when a hull has a working jump drive and is not already in the target's sector.</summary>
    public bool CanJumpTo(in HullView hull, Double3 target) => HasJumpDrive(hull) && !InSectorOf(hull, target);

    /// <summary>True when the hull still has a working faster-than-light drive; losing the drive block loses it.</summary>
    private bool HasJumpDrive(in HullView hull)
    {
        var design = _designs[Math.Min(hull.Design, _designs.Length - 1)];
        design.Evaluate(hull.Blocks, out var flight, out _);
        return flight.Ftl != FtlKind.None;
    }

    private bool InSectorOf(in HullView hull, Double3 target) => _galaxy is not null && _galaxy.SectorOf(HullPosition(hull)) == _galaxy.SectorOf(target);

    /// <summary>
    /// Sends exactly the selected hulls to a point in formation, and nothing when none is selected: unlike
    /// <see cref="OrderMove"/> there is no fallback to the hull being flown. The map uses it, so the hulls its unit
    /// list shows as selected are the ones ordered.
    /// </summary>
    public void OrderMoveSelected(PlayerInputBridge bridge, Double3 target)
    {
        PruneSelection();
        OrderSelection(bridge, target, true, false);
    }

    /// <summary>Drops selected hulls that are gone from the snapshot or no longer the player's.</summary>
    public void PruneSelection()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
            return;
        var kept = 0;
        for (var i = 0; i < _selectedCount; i++)
        {
            if (TryGetHull(_selected[i], out var hull) && hull.Owner == snapshot.PlayerId)
                _selected[kept++] = _selected[i];
        }

        _selectedCount = kept;
    }

    /// <summary>Selects every hull the player owns.</summary>
    public void SelectAllOwned()
    {
        _commandCycle.Reset();
        var snapshot = _snapshot;
        _selectedCount = 0;
        _selectedRockCount = 0;
        for (var i = 0; snapshot is not null && i < snapshot.HullCount && _selectedCount < MaxSelection; i++)
        {
            if (snapshot.Hulls[i].Owner == snapshot.PlayerId)
                _selected[_selectedCount++] = snapshot.Hulls[i].Id;
        }
    }

    /// <summary>Makes the command camera glide to a hull and then follow it.</summary>
    public void FocusOn(uint id)
    {
        if (_followId == id && _focusBlend >= 1f)
            return;
        _followId = id;
        _focusFrom = _commandFocus;
        _focusBlend = 0f;
    }

    /// <summary>Makes the command camera glide to a point and stay there.</summary>
    private void FocusOnPoint(Double3 point)
    {
        _followId = 0;
        _focusFrom = _commandFocus;
        _focusTarget = point;
        _focusBlend = 0f;
    }

    /// <summary>Targets the hull nearest the player's other than their own, as the middle button would.</summary>
    public void TargetNearestHull()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
            return;
        var best = float.MaxValue;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var distance = hull.Position.RelativeTo(OwnPosition).LengthSquared();
            if (hull.IsOwn || distance >= best)
                continue;
            best = distance;
            TargetHull = hull.Id;
            TargetRock = null;
        }
    }

    /// <summary>Finds a hull in this frame's snapshot by id.</summary>
    public bool TryGetHull(uint id, out HullView hull)
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
        {
            for (var i = 0; i < snapshot.HullCount; i++)
            {
                if (snapshot.Hulls[i].Id != id)
                    continue;
                hull = snapshot.Hulls[i];
                return true;
            }
        }

        hull = default;
        return false;
    }

    /// <summary>
    /// Orders every selected hull. A move sends them to the point in formation: a block of rows centred on it, so
    /// several hulls sent to one place do not try to occupy the same spot. A jump orders only the hulls that can
    /// jump there and leaves the others alone.
    /// </summary>
    private void OrderSelection(PlayerInputBridge bridge, Double3 target, bool move, bool jump)
    {
        var columns = (int)MathF.Ceiling(MathF.Sqrt(_selectedCount));
        var rows = (_selectedCount + columns - 1) / Math.Max(columns, 1);
        for (var i = 0; i < _selectedCount; i++)
        {
            var order = default(HullOrder);
            if (move)
            {
                var offset = new Vector3((i % columns - (columns - 1) * 0.5f) * FormationSpacing, 0, (i / columns - (rows - 1) * 0.5f) * FormationSpacing);
                if (jump && (!TryGetHull(_selected[i], out var hull) || !CanJumpTo(hull, target)))
                    continue;
                order = new HullOrder { Kind = jump ? OrderKind.Jump : OrderKind.MoveTo, Target = target + offset };
            }

            // The hull being flown takes its orders through the input stream; the rest go to the server directly.
            // Its jump is only plotted: the pilot starts it.
            if (_selected[i] == Own.Id && jump)
                NavTarget = order.Target;
            else if (_selected[i] == Own.Id)
                bridge.IssueOrder(order);
            else
                bridge.IssueFleetOrder(_selected[i], order);
        }
    }

    private void SelectAt(SessionSnapshot snapshot, float2 pointer, bool additive)
    {
        var count = PickTargets(snapshot, pointer, false);
        var hit = _commandCycle.Click(_pickHits.AsSpan(0, count), new Vector2(pointer.X, pointer.Y));
        if (hit.Hull != 0)
        {
            SelectCore(hit.Hull, additive);
            return;
        }
        var found = hit.Rock.HasValue;
        var picked = hit.Rock.GetValueOrDefault();

        if (!additive)
            _selectedRockCount = 0;
        _selectedCount = 0;
        if (!found)
            return;

        var existing = _selectedRocks.AsSpan(0, _selectedRockCount).IndexOf(picked);
        if (existing >= 0)
            _selectedRocks[existing] = _selectedRocks[--_selectedRockCount];
        else if (_selectedRockCount < MaxSelection)
            _selectedRocks[_selectedRockCount++] = picked;
    }

    private void SelectBox(SessionSnapshot snapshot, float2 a, float2 b, bool additive)
    {
        _commandCycle.Reset();
        var min = new float2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        var max = new float2(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        if (!additive)
            _selectedCount = 0;

        for (var i = 0; i < snapshot.HullCount && _selectedCount < MaxSelection; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Owner != snapshot.PlayerId || IsSelected(hull.Id) || !Project(HullPosition(hull), out var screen))
                continue;
            if (screen.X >= min.X && screen.X <= max.X && screen.Y >= min.Y && screen.Y <= max.Y)
                _selected[_selectedCount++] = hull.Id;
        }

        // A box always prefers the player's hulls; only a box with none in it selects the asteroids inside.
        if (!additive)
            _selectedRockCount = 0;
        if (_selectedCount > 0)
        {
            _selectedRockCount = 0;
            return;
        }

        // Largest first within each sector, and only those large enough to be drawn.
        var aspect = _screen.X / _screen.Y;
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy) || slot.Content.AsteroidCount == 0)
                continue;
            if (!SectorInView(slot, CameraPosition, aspect, _screen.Y, out var center, out var smallest))
                continue;
            for (var i = 0; i < slot.Content.AsteroidCount && _selectedRockCount < MaxSelection; i++)
            {
                if (slot.Content.Asteroids[i].Radius < smallest)
                    break;
                if (!RockOnScreen(slot, center, i, out var screen, out var pixels, out _) || pixels < RockMinPixels)
                    continue;
                var rock = new RockRef(slot.Coord, i);
                if (screen.X >= min.X && screen.X <= max.X && screen.Y >= min.Y && screen.Y <= max.Y && !_selectedRocks.AsSpan(0, _selectedRockCount).Contains(rock))
                    _selectedRocks[_selectedRockCount++] = rock;
            }
        }
    }

    /// <summary>Where an asteroid is this frame, and its radius. False if its sector is not loaded.</summary>
    public bool TryGetRock(RockRef rock, out Double3 position, out float radius)
    {
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || slot.Coord != rock.Sector || !ReferenceEquals(slot.Galaxy, _galaxy))
                continue;
            if ((uint)rock.Index >= (uint)slot.Content.AsteroidCount)
                break;
            ref var asteroid = ref slot.Content.Asteroids[rock.Index];
            position = _galaxy!.CenterOf(slot.Coord) + asteroid.LocalPosition + (slot.Displaced ? slot.Offsets[rock.Index] : Vector3.Zero);
            radius = asteroid.Radius;
            return true;
        }

        position = default;
        radius = 0;
        return false;
    }

    /// <summary>What an asteroid carries: the resource index (or -1 for bare rock) and its grade.</summary>
    public bool TryGetRockOre(RockRef rock, out int resource, out float grade)
    {
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || slot.Coord != rock.Sector || !ReferenceEquals(slot.Galaxy, _galaxy))
                continue;
            if ((uint)rock.Index >= (uint)slot.Content.AsteroidCount)
                break;
            resource = slot.Content.Asteroids[rock.Index].Resource;
            grade = slot.Content.Asteroids[rock.Index].Grade;
            return true;
        }

        resource = -1;
        grade = 0;
        return false;
    }

    /// <summary>How fast an asteroid is drifting this frame, in m/s.</summary>
    public float RockDrift(RockRef rock)
    {
        var snapshot = _snapshot;
        if (snapshot is null || _galaxy is null)
            return 0;
        for (var i = 0; i < snapshot.RockCount; i++)
        {
            if (snapshot.Rocks[i].Rock != rock)
                continue;
            snapshot.Rocks[i].Evaluate(snapshot.ServerTickPrecise + TickAlpha(snapshot), _galaxy.DriftDecay, out _, out var velocity);
            return velocity.Length();
        }

        return 0;
    }

    /// <summary>
    /// Projects a sphere: its centre in window coordinates, its radius in the same units and its distance along the view.
    /// </summary>
    public bool ProjectDisc(Double3 world, float radius, out float2 screen, out float pixels, out float depth)
    {
        depth = Vector3.Dot(world.RelativeTo(CameraPosition), _forward);
        pixels = 0;
        if (!Project(world, out screen))
            return false;
        pixels = radius / (depth * _tanHalfFov) * _screen.Y * 0.5f;
        return true;
    }

    /// <summary>Brings every loaded sector's asteroid offsets up to date with the motion records in the snapshot.</summary>
    private void ApplyRockDeltas(SessionSnapshot snapshot)
    {
        foreach (var slot in _slots)
        {
            if (!slot.Displaced)
                continue;
            Array.Clear(slot.Offsets);
            slot.Displaced = false;
        }

        if (_galaxy is null)
            return;
        var tick = snapshot.ServerTickPrecise + TickAlpha(snapshot);
        for (var i = 0; i < snapshot.RockCount; i++)
        {
            ref var delta = ref snapshot.Rocks[i];
            foreach (var slot in _slots)
            {
                if (slot.State != SectorSlot.Ready || slot.Coord != delta.Rock.Sector || !ReferenceEquals(slot.Galaxy, _galaxy))
                    continue;
                if ((uint)delta.Rock.Index < (uint)slot.Offsets.Length)
                {
                    delta.Evaluate(tick, _galaxy.DriftDecay, out slot.Offsets[delta.Rock.Index], out _);
                    slot.Displaced = true;
                }

                break;
            }
        }
    }

    private void UpdateCommandControls(SessionSnapshot snapshot, PlayerInputBridge bridge, float dt, bool pointerOverUi)
    {
        // Asteroids whose sector has streamed out drop out of the selection.
        for (var i = _selectedRockCount - 1; i >= 0; i--)
        {
            if (!TryGetRock(_selectedRocks[i], out _, out _))
                _selectedRocks[i] = _selectedRocks[--_selectedRockCount];
        }

        // Hulls that no longer exist drop out of the selection.
        for (var i = _selectedCount - 1; i >= 0; i--)
        {
            if (!TryGetHull(_selected[i], out _))
                _selected[i] = _selected[--_selectedCount];
        }

        var shift = _devices.KeyDown(GlfwKey.LeftShift) || _devices.KeyDown(GlfwKey.RightShift);
        var control = _devices.KeyDown(GlfwKey.LeftControl) || _devices.KeyDown(GlfwKey.RightControl);

        if (_actions.Pressed(_devices, InputAction.Follow, true))
        {
            if (_selectedCount == 0 && _selectedRockCount > 0 && TryGetRock(_selectedRocks[0], out var rockPosition, out _))
            {
                // An asteroid is somewhere to look at, not something to follow.
                FocusOnPoint(rockPosition);
            }
            else
            {
                FocusOn(_selectedCount > 0 ? _selected[0] : Own.Id);
            }
        }
        if (control && _devices.KeyPressed(GlfwKey.A))
            SelectAllOwned();

        // Taking control of the selected hull drops straight into its pilot view.
        if (_actions.Pressed(_devices, InputAction.SwitchHull, true) && _selectedCount > 0)
        {
            if (_selected[0] != Own.Id)
                bridge.RequestControl(_selected[0]);
            SetMode(ViewMode.Pilot);
        }

        if (!pointerOverUi && _devices.Scroll != 0)
            _commandDistance = Math.Clamp(_commandDistance * MathF.Pow(0.86f, _devices.Scroll), MinCommandDistance, MaxCommandDistance);

        // Left button: click picks a hull, drag draws a selection box, Shift and drag slides the plane up and down.
        if (_devices.MousePressed(GlfwMouseButton.Left) && !pointerOverUi)
        {
            _leftDragging = true;
            _leftMoved = false;
            _leftSlidesPlane = shift;
            _leftDragStart = _devices.MousePosition;
        }

        if (_leftDragging)
        {
            var moved = _devices.MousePosition - _leftDragStart;
            if (MathF.Abs(moved.X) + MathF.Abs(moved.Y) > 5f)
                _leftMoved = true;

            if (_leftSlidesPlane && _leftMoved)
            {
                _commandFocus += Vector3.UnitY * (-_devices.MouseDelta.Y * _commandDistance * 0.0018f);
                _followId = 0;
                _focusBlend = 1f;
            }

            if (!_devices.MouseDown(GlfwMouseButton.Left))
            {
                _leftDragging = false;
                if (_leftMoved && !_leftSlidesPlane)
                    SelectBox(snapshot, _leftDragStart, _devices.MousePosition, control);
                else if (!_leftMoved)
                    SelectAt(snapshot, _devices.MousePosition, shift || control);
            }
        }

        // Right button: drag to rotate the view, click to send the selection to the point under the pointer.
        if (_devices.MousePressed(GlfwMouseButton.Right) && !pointerOverUi)
        {
            _rightDragging = true;
            _rightMoved = false;
            _rightDragStart = _devices.MousePosition;
        }

        if (_rightDragging)
        {
            var moved = _devices.MousePosition - _rightDragStart;
            if (MathF.Abs(moved.X) + MathF.Abs(moved.Y) > 5f)
                _rightMoved = true;
            if (_rightMoved)
            {
                _commandYaw -= _devices.MouseDelta.X * 0.005f;
                _commandPitch = Math.Clamp(_commandPitch - _devices.MouseDelta.Y * 0.005f, -1.52f, -0.1f);
            }

            if (!_devices.MouseDown(GlfwMouseButton.Right))
            {
                _rightDragging = false;
                if (!_rightMoved && _selectedCount > 0 && GroundPoint(_devices.MousePosition, out var target))
                    OrderSelection(bridge, target, true, false);
            }
        }

        // W A S D and the arrow keys pan across the plane; Q and E lower and raise it.
        var strafe = Math.Clamp(_actions.Axis(_devices, InputAxis.Strafe, true) + _actions.Axis(_devices, InputAxis.Yaw, true), -1f, 1f);
        var forward = Math.Clamp(_actions.Axis(_devices, InputAxis.Forward, true) + _actions.Axis(_devices, InputAxis.Pitch, true), -1f, 1f);
        var vertical = _actions.Axis(_devices, InputAxis.Roll, true);

        if (_devices.MouseDown(GlfwMouseButton.Middle) && !pointerOverUi)
        {
            strafe -= _devices.MouseDelta.X * 0.12f;
            forward += _devices.MouseDelta.Y * 0.12f;
        }

        if (strafe != 0 || forward != 0 || vertical != 0)
        {
            var (sin, cos) = MathF.SinCos(_commandYaw);
            var flatForward = new Vector3(-sin, 0, -cos);
            var flatRight = new Vector3(cos, 0, -sin);
            var step = _commandDistance * 1.1f * dt;
            _commandFocus += (flatRight * strafe + flatForward * forward + Vector3.UnitY * vertical * 0.7f) * step;
            _followId = 0;
            _focusBlend = 1f;
        }
    }

    private void UpdateCamera(float dt)
    {
        var target = Mode == ViewMode.Command ? 1f : 0f;
        _commandBlend += Math.Clamp(target - _commandBlend, -dt / 0.45f, dt / 0.45f);
        var t = _commandBlend * _commandBlend * (3f - 2f * _commandBlend);

        // Chase: the camera looks where the player looks and sits behind the hull along that line. Its up follows
        // the hull's, so rolling the hull rolls the view. The hull turns onto this line when it is under thrust.
        if (!_chaseInitialised)
        {
            _chaseOrientation = OwnOrientation;
            _lookDirection = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
            _cameraDirection = _lookDirection;
            _chaseInitialised = true;
        }

        _chaseOrientation = Quaternion.Slerp(_chaseOrientation, OwnOrientation, 1f - MathF.Exp(-11f * dt));
        _cameraDirection = Vector3.Normalize(Vector3.Lerp(_cameraDirection, _lookDirection, 1f - MathF.Exp(-22f * dt)));
        var hullUp = Vector3.Transform(Vector3.UnitY, _chaseOrientation);
        var side = Vector3.Cross(_cameraDirection, hullUp);
        if (side.LengthSquared() > 0.01f)
            _chaseRight = Vector3.Normalize(side);
        var chaseUp = Vector3.Normalize(Vector3.Cross(_chaseRight, _cameraDirection));
        var chaseOrientation = Quaternion.CreateFromRotationMatrix(
            new Matrix4x4(
                _chaseRight.X, _chaseRight.Y, _chaseRight.Z, 0,
                chaseUp.X, chaseUp.Y, chaseUp.Z, 0,
                -_cameraDirection.X, -_cameraDirection.Y, -_cameraDirection.Z, 0,
                0, 0, 0, 1
            )
        );

        var speed = Own.Velocity.Length();
        var pullBack = 1f + Math.Min(speed / 720f, 1f) * 0.25f;
        var chaseDistance = Math.Max(3f, _designs[Math.Min(Own.Design, _designs.Length - 1)].Flight.Radius * 3.3f) * _chaseZoom;
        // Through a jump too the camera only follows the hull: where it is and where it looks stay the pilot's.
        var chasePosition = OwnDrawnPosition + chaseUp * (chaseDistance * 0.24f) - _cameraDirection * (chaseDistance * pullBack);

        if (_followId != 0)
        {
            if (TryGetHull(_followId, out var followed))
            {
                // Glide to a newly focused hull rather than cut to it; once there, stay locked on.
                _focusBlend = Math.Min(1f, _focusBlend + dt / FocusSeconds);
                var eased = _focusBlend * _focusBlend * (3f - 2f * _focusBlend);
                _commandFocus = Double3.Lerp(_focusFrom, HullPosition(followed), eased);
            }
            else
            {
                _followId = 0;
            }
        }
        else if (_focusBlend < 1f)
        {
            // Gliding to a fixed point, such as a selected asteroid.
            _focusBlend = Math.Min(1f, _focusBlend + dt / FocusSeconds);
            var eased = _focusBlend * _focusBlend * (3f - 2f * _focusBlend);
            _commandFocus = Double3.Lerp(_focusFrom, _focusTarget, eased);
        }
        var commandOrientation = Quaternion.CreateFromYawPitchRoll(_commandYaw, _commandPitch, 0);
        var commandPosition = _commandFocus + Vector3.Transform(new Vector3(0, 0, _commandDistance), commandOrientation);

        CameraPosition = Double3.Lerp(chasePosition, commandPosition, t);
        var orientation = Quaternion.Slerp(chaseOrientation, commandOrientation, t);
        _right = Vector3.Transform(Vector3.UnitX, orientation);
        _up = Vector3.Transform(Vector3.UnitY, orientation);
        _forward = Vector3.Transform(-Vector3.UnitZ, orientation);

        var pilotFov = PilotFov + Math.Min(speed / 720f, 1f) * 0.16f;
        _tanHalfFov = MathF.Tan((pilotFov + (CommandFov - pilotFov) * t) / 2f);
    }

    /// <summary>Keeps the block of sectors around the camera generated, on worker threads.</summary>
    private void UpdateStream()
    {
        var galaxy = _galaxy!;
        var center = galaxy.SectorOf(CameraPosition);
        if (center != _streamCenter)
        {
            _streamCenter = center;
            _streamDirty = true;
        }

        // While a jump is being prepared, the heart of the destination is generated too, so that it is already
        // there when the hull comes out.
        var ahead = Own.Order.Kind == OrderKind.Jump ? galaxy.SectorOf(Own.Order.Target) : center;
        if (ahead != _streamAhead)
        {
            _streamAhead = ahead;
            _streamDirty = true;
        }

        if (!_streamDirty)
            return;

        _streamDirty = false;
        for (var dx = -RingXZ; dx <= RingXZ; dx++)
            for (var dy = -RingY; dy <= RingY; dy++)
                for (var dz = -RingXZ; dz <= RingXZ; dz++)
                {
                    var coord = center.Offset(dx, dy, dz);
                    SectorSlot? free = null;
                    var present = false;
                    foreach (var slot in _slots)
                    {
                        var current = ReferenceEquals(slot.Galaxy, galaxy) && slot.State != SectorSlot.Empty;
                        if (current && slot.Coord == coord)
                        {
                            present = true;
                            break;
                        }

                        // A slot can be reused if it is not mid-generation and holds nothing inside the wanted block.
                        if (free is null && slot.State != SectorSlot.Generating && !(current && InRing(slot.Coord, center)))
                            free = slot;
                    }

                    if (present)
                        continue;

                    if (free is null)
                    {
                        // Every spare slot is still generating; try again next frame.
                        _streamDirty = true;
                        continue;
                    }

                    // The slot's push offsets belong to the sector it held; the next one starts with none.
                    if (free.Displaced)
                    {
                        Array.Clear(free.Offsets);
                        free.Displaced = false;
                    }

                    free.Galaxy = galaxy;
                    free.Coord = coord;
                    free.State = SectorSlot.Generating;
                    _jobs.Schedule(free, 2);
                }
    }

    private bool NearAhead(SectorCoord coord) =>
        Math.Abs(coord.X - _streamAhead.X) <= 1 && Math.Abs(coord.Y - _streamAhead.Y) <= 1 && Math.Abs(coord.Z - _streamAhead.Z) <= 1;

    private static bool InRing(SectorCoord coord, SectorCoord center) =>
        Math.Abs(coord.X - center.X) <= RingXZ && Math.Abs(coord.Y - center.Y) <= RingY && Math.Abs(coord.Z - center.Z) <= RingXZ;

    /// <summary>Sphere against the view frustum, also reporting the sphere's radius on screen in pixels.</summary>
    private bool Visible(Vector3 position, float radius, float aspect, out float pixels)
    {
        var depth = Vector3.Dot(position, _forward);
        pixels = 0;
        if (depth < -radius)
            return false;

        // The margin widens the frustum by the sphere's radius, measured across the slanted side planes.
        var margin = radius * 1.5f;
        var limitY = Math.Max(depth, 0f) * _tanHalfFov + margin;
        if (MathF.Abs(Vector3.Dot(position, _up)) > limitY || MathF.Abs(Vector3.Dot(position, _right)) > limitY * aspect + margin)
            return false;

        pixels = radius / (Math.Max(position.Length(), radius) * _tanHalfFov) * _viewport.Y * 0.5f;
        return true;
    }

    private void Add(ref int count, int mesh, Vector3 position, Quaternion rotation, float scale, Vector3 color, float material)
    {
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        _candidates[count++] = new Candidate
        {
            Mesh = mesh,
            Instance = new MeshInstance
            {
                // System.Numerics matrices transform row vectors, so the rotation's columns are its rows here.
                Row0 = new float4(m.M11 * scale, m.M21 * scale, m.M31 * scale, position.X),
                Row1 = new float4(m.M12 * scale, m.M22 * scale, m.M32 * scale, position.Y),
                Row2 = new float4(m.M13 * scale, m.M23 * scale, m.M33 * scale, position.Z),
                Tint = new float4(color.X, color.Y, color.Z, material),
            },
        };
        _meshCounts[mesh]++;
    }

    private static float Wrap(double value, double period) => (float)(value - Math.Floor(value / period) * period);
}
