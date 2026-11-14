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
    // Enough for the camera ring and a disjoint command-focus neighborhood.
    private const int SlotCount = (RingXZ * 2 + 1) * (RingXZ * 2 + 1) * (RingY * 2 + 1) + 27;
    private const float JumpGap = HullVisuals.JumpGap;
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

    // Where a rift's or wormhole's opening sits, in hull radii (see HullVisuals). The lens bends the view out to about
    // LensReach opening radii, so an opening that far off the view still gets drawn.
    private const float OpeningAhead = HullVisuals.OpeningAhead;
    private const float OpeningRadius = HullVisuals.OpeningRadius;
    private const float LensReach = 4.4f;

    /// <summary>A sector's generated contents and the job that fills them.</summary>
    internal sealed class SectorSlot : IJob
    {
        public const int Empty = 0;
        public const int Generating = 1;
        public const int Ready = 2;

        public readonly SectorContent Content = new();

        /// <summary>Per asteroid, how far it has been pushed from its generated position, as of this frame.</summary>
        public Vector3[] Offsets = [];
        /// <summary>Last rendered identities, owned by the render thread and cleared before slot reuse.</summary>
        internal bool Scanned;
        internal Vector3 CameraCenter;
        public readonly ulong[] Drawn = new ulong[(ushort.MaxValue + 1) / 64];
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
    private readonly DamageMeshes _damage;
    public int VisibleHulls { get; private set; }
    public int DrawnDents { get; private set; }
    public int DamageFallbacks { get; private set; }
    public int GeneratedRocks { get; private set; }
    public int EligibleRocks { get; private set; }
    public int VisibleRocks { get; private set; }
    public double DamageWorkerMs => _damage.LastBuildMs;
    private readonly CollisionSparks _sparks = new();
    private readonly CollisionDetail _collision = CollisionDetail.Load();
    private readonly SceneRenderer _scene;
    // The designs as the session's world evaluates them; the meshes built from them are the same in every world.
    private HullDesign[] _designs;
    private readonly int _asteroidMeshBase;
    private readonly int _meshCount;
    private readonly int _cubeMesh;
    private readonly SectorSlot[] _slots = new SectorSlot[SlotCount];
    private readonly record struct RockFrame(Double3 Camera, Vector3 Right, Vector3 Up, Vector3 Forward, float Tan, float2 Viewport, float2 Screen);
    private RockFrame _rockFrame;
    private bool _hasRockFrame;
    private readonly RockSelection _rockSelection = new(SceneRenderer.MaxInstances);
    private readonly SectorRequests _sectorRequests = new();
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
    private float _outOfSectorAt = -10f;
    private string? _switchKeyName;
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
    private JumpChord _jumpChord;
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

    // The ship editor's camera: an orbit about a point fixed to the hull, in the hull's own frame, so the ship
    // holds still on screen while it is built. Blend is how far the camera has moved from the flight view to it.
    private float _editYaw;
    private float _editPitch;
    private float _editDistance = 40f;
    private Vector3 _editPan;
    private float _editBlend;
    private bool _editShown;
    private bool _editLeftDown;
    private bool _editLeftMoved;
    private float2 _editLeftStart;
    private bool _editRightDown;
    private bool _editRightMoved;
    private float2 _editRightStart;
    private bool _editMiddleDown;
    private bool _editMiddleMoved;
    private float2 _editMiddleStart;

    // The block a left press began on, when it was selected: moving the pointer then drags the selection.
    private int _editGrab = -1;

    // A right press in the editor that began while a part was sized or turned: its click leaves placing alone.
    private bool _editRightReserved;

    // The part-size gesture (UpdatePartSizing), while the editor's SizingPart holds: the ghost face under the
    // pointer, and a face being dragged: its side, where the drag began on screen, the length it began from (moved
    // when a limit is pushed past, so turning back answers at once), the pixels one cell spans along the face's axis
    // on screen, whether it is followed up and down instead because that axis points along the view, and whether it
    // must start again from where the pointer is after the camera turned. The draft version it began on.
    private int _sizeFace = -1;
    private bool _sizeDragging;
    private int _sizeSide;
    private float2 _sizeStart;
    private float _sizeBase;
    private float2 _sizeCell;
    private bool _sizeEdgeOn;
    private bool _sizeRebase;
    private int _sizeVersion;
    private EditorTuning? _editTuning;

    // The rotation ring under the pointer while the part to place is turned (0 about x, 1 about y), or -1, and
    // where the rings stand: fixed when the turn starts, so a turn that shifts the ghost does not move them away.
    private int _ringHovered = -1;
    private Vector3 _ringMiddle;
    private float _ringRadius;

    // A left press on a ring: the ring, where it began, whether it has moved far enough to be a drag rather than a
    // click, the angle about the ring's axis the pointer was at last, how far it has turned since the press, the
    // quarter turns that has made, and whether the angle must be read afresh (after the camera turned, or while the
    // ring was too near edge-on to read) before it counts again.
    private bool _ringPress;
    private int _ringAxis;
    private float2 _ringPressAt;
    private bool _ringDragging;
    private float _ringLast;
    private float _ringAngle;
    private int _ringTurns;
    private bool _ringRebase;
    private int _ringCancels;

    // How the ghost of the part to place is drawn while it turns: client only, never the part placed.
    private readonly GhostMotion _ghostMotion = new();

    // Where the editor's camera glides to after a focus: the point it orbits and its distance. Any pan or zoom by
    // hand stops the glide.
    private bool _editFocusing;
    private Vector3 _editPanGoal;
    private float _editDistanceGoal;

    // How far the draft reaches from the pivot, worked out again when the draft changes, so the camera's limits
    // grow with a draft that grows past the ship.
    private float _editReach;
    private int _editReachVersion = -1;
    private BuildDraft? _editReachDraft;

    // After the editor closes, flight waits until every flight key held through it has been let go, so a key
    // pressed for the editor never becomes thrust the moment it closes.
    private bool _flightHeld;

    // An applied edit swaps the flown hull for a new one: the view keeps its target and course across exactly that
    // swap, as it would for the same ship.
    private uint _carryFrom;
    private uint _carryTo;
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
    // Where every hull is drawn this frame, with the jumps of hulls seen from outside; worked out in the update.
    private readonly HullVisuals _visuals = new();
    // Every shielded hull's shield as drawn: client side only, from the replicated shield state.
    private readonly ShieldVisuals _shields;
    private bool _ownInside = true;

    /// <summary>Where every hull is drawn this frame; for tests.</summary>
    internal HullVisuals Visuals => _visuals;

    /// <summary>Every hull's shield as drawn this frame; for tests.</summary>
    internal ShieldVisuals Shields => _shields;
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
    private bool _jumpChaseLatched;

    // The pilot's own jump as last seen, to tell a new attempt from the one before it, and what the previous attempt
    // had drawn when a new one began, eased out over the new one rather than dropped.
    private JumpPhase _jumpWas;
    private Double3 _jumpTarget;
    private float _jumpCalculated;
    private float _carryOffset, _carryOpen, _carryStretch, _carryCover;
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
        _damage = new DamageMeshes(jobs, _collision.MeshVertices);
        _scene = scene;
        var boost = actions.Bindings(InputAction.Boost);
        BoostKeyName = boost.IsEmpty ? "BOOST" : boost[0].DisplayName.ToUpperInvariant();
        _designs = designs;
        Editor = new ShipEditor(jobs);
        // Damaged shells are built on a worker, one at a time; nothing builds one in the frame.
        _shields = new ShieldVisuals(ShieldLook.Load(), jobs);
        // Every cloud the seed generates is known. Fog of war would hand a narrower source in here.
        _gasField = new GasCloudField(jobs, new GeneratedGasClouds(), _gasTiers.ViewRangeSectors);
        _asteroidMeshBase = AsteroidMesh(0, 0);
        _meshCount = MeshCount();
        _cubeMesh = CubeMesh();
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

    /// <summary>Ship slots are reserved for the largest admitted catalogue, keeping rocks and blocks stable.</summary>
    public const int ShipMeshSlots = 255;

    /// <summary>
    /// How many of the catalogue's first designs have their meshes in the renderer. A hull of a later design is
    /// neither drawn nor picked until its mesh is there. The application sets it every frame; everything by default.
    /// </summary>
    public int InstalledDesigns { get; set; } = int.MaxValue;

    /// <summary>Total reserved mesh slots.</summary>
    public static int MeshCount() => ShipMeshSlots + Galaxy.AsteroidShapes * AsteroidDetail.Length + BlockShapeCount;

    /// <summary>One single-block mesh for each <see cref="BlockShape"/>: the cube and the twelve wedges.</summary>
    private const int BlockShapeCount = 13;

    /// <summary>The single-block mesh, used to draw hulls that have lost blocks.</summary>
    public static int CubeMesh() => ShipMeshSlots + Galaxy.AsteroidShapes * AsteroidDetail.Length;

    /// <summary>
    /// The single-block mesh of a shape, a unit bounding box scaled per axis to the block: the cube for a box, the
    /// prism after it for each wedge. Every block mesh comes after the rocks, so none is drawn as one.
    /// </summary>
    public static int BlockMesh(BlockShape shape) => CubeMesh() + (int)shape;

    public static int AsteroidMesh(int shape, int detail) => ShipMeshSlots + detail * Galaxy.AsteroidShapes + shape;

    public ViewMode Mode { get; private set; }

    /// <summary>The ship editor: its draft, what is selected, and where the edit stands with the server.</summary>
    public ShipEditor Editor { get; }

    /// <summary>
    /// True while the pointer is shown over the world and the world uses it: the command view, and the ship editor.
    /// The pointer router treats both alike (<see cref="PointerFrame.CommandView"/>).
    /// </summary>
    public bool PointerView => Mode == ViewMode.Command || Editor.Shown;

    /// <summary>True while flight ignores flight keys that were held when the editor closed, until they are let go.</summary>
    public bool FlightHeld => _flightHeld;

    /// <summary>
    /// Whether an interface window is under the pointer this frame, whoever holds the button: set by the frame
    /// loop from <see cref="Input.PointerRouter.PointerOverWindow"/> before <see cref="Update"/>. The editor reads it
    /// only while the pointer carries something, to tell a drop on the ship from a drop on a panel.
    /// </summary>
    public bool PointerOverPanel { get; set; }

    /// <summary>
    /// True while the world carries a press the interface began: a part dragged out of the editor's part list. The
    /// interface item stays active and claims the keyboard meanwhile; the frame loop lets the world read input anyway.
    /// </summary>
    public bool HoldsInterfacePress => Editor.Carry == EditorCarry.Part;

    /// <summary>The point the editor's camera orbits, in the hull's own frame, and its distance from it, in metres.</summary>
    public Vector3 EditorFocus => _editPan;
    public float EditorDistance => _editDistance;

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

    /// <summary>True for a moment after the player tried to take control of an own hull in another sector.</summary>
    public bool ControlOutOfSector => _time - _outOfSectorAt < 2.5f;

    /// <summary>The name of the control bound first to taking control, for the notice that points at it.</summary>
    public string SwitchKeyName
    {
        get
        {
            if (_switchKeyName is null)
            {
                var bindings = _actions.Bindings(InputAction.SwitchHull);
                _switchKeyName = bindings.Length > 0 ? bindings[0].DisplayName : "the take-control key";
            }

            return _switchKeyName;
        }
    }

    /// <summary>
    /// True when the player may take control of the hull from where they fly: it is theirs and in the same sector as
    /// the hull they fly, as the snapshot shows both. The server checks the same rule on its own positions.
    /// </summary>
    public bool CanTakeControlOf(in HullView hull)
    {
        var snapshot = _snapshot;
        if (snapshot is null || _galaxy is null || hull.Owner != snapshot.PlayerId)
            return false;
        return _galaxy.InSameSector(Own.Position, hull.Position);
    }

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
    /// <summary>The camera's last drawn orientation, for scripted capture staging.</summary>
    public Vector3 CameraForward => _forward;
    /// <summary>The camera's last drawn up direction.</summary>
    public Vector3 CameraUp => _up;
    /// <summary>The camera's last drawn right direction.</summary>
    public Vector3 CameraRight => _right;
    /// <summary>Muted capture camera only; never creates or changes generated cloud descriptors.</summary>
    public string? GasView { get; set; }
    /// <summary>With the outside cloud camera: its distance from the cloud's centre, in the cloud's largest half-extents.</summary>
    public float? GasDistance { get; set; }
    /// <summary>Controlled shader inputs, applied only while devices are muted.</summary>
    public int GasFixture { get; set; }
    /// <summary>
    /// With <see cref="GasShaderFixture.OverlayCheck"/>: submit one opening behind the camera, which no pixel can see,
    /// so only the renderer's gas mode changes.
    /// </summary>
    public bool GasOverlayOpening { get; set; }
    /// <summary>Whether the last rendered frame deferred gas to composition, and how many shields it queued.</summary>
    public (bool Deferred, int Shields, bool HasGas) LastGasMode => _scene.LastGasMode;
    private GasCloudPlacement _gasCapture;
    // Far gas: how it is levelled, and the clouds around the camera, which a worker keeps current.
    private readonly GasTiers _gasTiers = GasTiers.Load();
    private readonly GasCloudField _gasField;
    /// <summary>The share of a cloud below which a level of detail is not worth a draw.</summary>
    private const float GasMinShare = 1f / 512f;
    /// <summary>How many gas draws the last frame queued at the full, the middle and the far level.</summary>
    public (int Full, int Middle, int Far) GasDraws { get; private set; }
    private Galaxy? _gasCaptureGalaxy;
    private SectorCoord _gasCaptureCoord;

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

    /// <summary>
    /// The context menu the command view's right click opens. Its rows come from <see cref="PlanJump"/>; choosing
    /// one goes through <see cref="OrderMoveSelected"/> or <see cref="OrderJump"/>.
    /// </summary>
    internal CourseMenu CourseMenu { get; } = new();

    /// <summary>
    /// What a jump to <paramref name="target"/> would do for the selected ships: who can go, who plots and who
    /// jumps by themselves, the distance against the drive's range, and the blind-jump risk. The same figures the
    /// server checks the jump with, worked out once for every screen that offers one.
    /// </summary>
    internal JumpPlan PlanJump(Double3 target) =>
        _snapshot is { } snapshot ? JumpPlan.Evaluate(snapshot, this, _selected.AsSpan(0, _selectedCount), target) : default;

    /// <summary>Whether the nose of the hull being flown is on <see cref="NavTarget"/>, close enough to jump.</summary>
    public bool NavAimed { get; private set; }

    /// <summary>True once the hull has left on the plotted jump, so landing clears the NAV point.</summary>
    private bool _navJumpMade;

    /// <summary>
    /// The server's refusal of the hull being flown's last plot or jump start, while it is news (see
    /// <see cref="NavRefusalAt"/>); <see cref="JumpRefusal.None"/> otherwise.
    /// </summary>
    public JumpRefusal NavRefusal { get; private set; }

    /// <summary>When <see cref="NavRefusal"/> arrived, in seconds on <see cref="Time"/>.</summary>
    public float NavRefusalAt { get; private set; } = -100f;

    // A plot sent to the server and not yet answered, and the server's last navigation state as it was seen, to
    // tell a new answer from the same one repeated.
    private Double3? _plotPending;
    private NavigationView _seenNavigation;

    /// <summary>When the boost key last asked for a jump while the nose was off the nav target, in seconds.</summary>
    public float NavRefusedAt { get; private set; } = -10f;

    /// <summary>The first key or button bound to boost, as the HUD names it.</summary>
    public string BoostKeyName { get; }

    /// <summary>Seconds since this view started, on the same clock as <see cref="NavRefusedAt"/>.</summary>
    public float Time => _time;

    /// <summary>How far the camera is past the opening of the pilot's own rift or wormhole that it is passing now.</summary>
    internal float JumpThrough => _jumpThrough;

    /// <summary>The opening of the pilot's own jump now: the entry while the drive works, the exit after.</summary>
    internal (Double3 Centre, Vector3 Axis, float Radius) OwnOpening =>
        _jumpStage >= 2f ? (_jumpExit, _jumpAxis, _jumpExitRadius) : (_jumpEntry, _jumpAxis, _jumpEntryRadius);

    /// <summary>Where the player's hull is drawn: where the simulation has it, moved along its own jump.</summary>
    public Double3 OwnDrawnPosition => OwnPosition + _jumpAxis * _jumpOffset;

    // Where the edited hull is when it is not the flown one: a new ship the drone's player is building. The camera,
    // the pointer ray and the draft all work from this pose, never the drone's.
    private bool _editedRemote;
    private Double3 _editedPosition;
    private Quaternion _editedRotation = Quaternion.Identity;
    private byte _editedDesign;

    /// <summary>Where the hull being edited is drawn this frame: the flown ship, or the new ship beside the drone.</summary>
    public Double3 EditedPosition => _editedRemote ? _editedPosition : OwnDrawnPosition;

    /// <summary>The orientation of the hull being edited this frame.</summary>
    public Quaternion EditedOrientation => _editedRemote ? _editedRotation : OwnOrientation;

    /// <summary>The design the edited hull has now, for the radius its camera works within.</summary>
    public byte EditedDesign => _editedRemote ? _editedDesign : Own.Design;

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
        CourseMenu.Close();
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
        // A skipped draw or resize must not let input combine old admission with another projection.
        if (_hasRockFrame && _rockFrame != new RockFrame(CameraPosition, _right, _up, _forward, _tanHalfFov, viewport, screen))
        {
            foreach (var slot in _slots) Array.Clear(slot.Drawn);
            _hasRockFrame = false;
        }
        _viewport = viewport;
        _screen = screen;
        _snapshot = snapshot;
        _designs = snapshot.Content.Designs;
        var galaxy = snapshot.Galaxy!;
        if (!ReferenceEquals(galaxy, _galaxy))
        {
            _galaxy = galaxy;
            // Each admitted session creates its own galaxy, even when its seed and hull IDs match the last one.
            _visuals.Clear();
            _shields.Clear();
            _damage.Clear();
            _sparks.Clear();
            _ownId = 0;
            ResetOwnJump();
            _commandCycle.Reset();
            _pilotCycle.Reset();
            _jumpChord.Clear();
            Editor.Reset();
            _editShown = false;
            _editBlend = 0f;
            _flightHeld = false;
            _carryFrom = _carryTo = 0;
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

        // Every hull's place this frame, once, for the drawing, the picking and the markers alike. The pilot sees
        // their own jump from inside until the command view has mostly taken over.
        _ownInside = _commandBlend < 0.5f;
        _visuals.Update(snapshot.Hulls.AsSpan(0, snapshot.HullCount), _designs, alpha, dt, _ownInside, FtlPreview);
        _shields.Update(snapshot.Hulls.AsSpan(0, snapshot.HullCount), _designs, snapshot.PublishedAt, dt);

        // The editor follows the server before anything reads input, so a grant or an end shows this frame.
        Editor.Track(snapshot, bridge, dt);
        UpdateEditedPose(snapshot);
        if (Editor.TakeReplacement(out var replaced, out var replacement))
            ReplaceHull(replaced, replacement);
        if (Editor.Shown != _editShown)
        {
            _editShown = Editor.Shown;
            if (_editShown)
                BeginEditing();
            else
                EndEditing();
        }

        if (own.Id != _ownId)
        {
            // The player is in a different hull: put the camera behind it and forget the old target and course;
            // unless an applied edit rebuilt the same ship, which keeps them and the camera where it is.
            var rebuilt = _ownId != 0 && _ownId == _carryFrom && own.Id == _carryTo;
            _carryFrom = _carryTo = 0;
            _ownId = own.Id;
            ResetOwnJump();
            _jumpAxis = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, OwnOrientation));
            _jumpUp = Vector3.Transform(Vector3.UnitY, OwnOrientation);
            if (!rebuilt)
            {
                _chaseInitialised = false;
                TargetHull = 0;
                NavTarget = null;
                _plotPending = null;
                _jumpChord.Clear();
            }

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
        ReconcileNavigation(snapshot);
        // Read whether or not the world has the controls: a menu over them is not the pilot letting go.
        var forwardPressed = _jumpChord.Observe(
            _actions.Axis(_devices, InputAxis.Forward, true) > 0f,
            _actions.Held(_devices, InputAction.Boost, true),
            Own,
            snapshot.ClientTick
        );

        if (!inputEnabled || _devices.SuppressMouse)
            _leftDragging = _rightDragging = false;
        var keyboard = inputEnabled;
        var editing = Editor.Shown;
        if (inputEnabled && !editing && _actions.Pressed(_devices, InputAction.ToggleCommand, true))
            SetMode(Mode == ViewMode.Pilot ? ViewMode.Command : ViewMode.Pilot);

        var controls = new PlayerControls();
        if (editing)
        {
            // The editor has the mouse and its own keys; the hull gets no controls at all, so nothing flies, fires,
            // orders or jumps while it is open.
            HoveredHull = 0;
            HoveredRock = null;
            UpdateEditorControls(inputEnabled, pointerOverUi, dt);
        }
        else if (inputEnabled)
        {
            controls.ToggleDampeners = _actions.Pressed(_devices, InputAction.ToggleDampeners, true);
            if (_actions.Pressed(_devices, InputAction.Halt, true))
            {
                _jumpChord.Clear();
                if (Mode == ViewMode.Pilot)
                    bridge.IssueOrder(default);
                else
                    OrderSelection(bridge, default, false, false);
            }
        }

        if (inputEnabled && !editing && Mode == ViewMode.Pilot)
        {
            controls.Strafe = _actions.Axis(_devices, InputAxis.Strafe, keyboard);
            controls.Vertical = _actions.Axis(_devices, InputAxis.Vertical, keyboard);
            controls.Forward = _actions.Axis(_devices, InputAxis.Forward, keyboard);
            controls.Roll = _actions.Axis(_devices, InputAxis.Roll, keyboard);
            controls.Boost = _actions.Held(_devices, InputAction.Boost, keyboard);
            if (_flightHeld)
            {
                // Keys held through the editor stay without effect until every one of them is up.
                if (controls.Strafe != 0 || controls.Vertical != 0 || controls.Forward != 0 || controls.Roll != 0 || controls.Boost)
                    controls = new PlayerControls();
                else
                    _flightHeld = false;
            }

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

            // A plotted jump starts when the pilot boosts with the nose on it, or pushes forward while boosting: the
            // two in either order are one start. Off it, boost is boost.
            var boostPressed = !_flightHeld && _actions.Pressed(_devices, InputAction.Boost, keyboard);
            if (!_actions.Pressed(_devices, InputAction.Halt, keyboard) && NavAimed && Own.Jump == JumpPhase.None && Own.Order.Kind != OrderKind.Jump && (boostPressed || (forwardPressed && controls.Boost)))
            {
                bridge.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = NavTarget!.Value });
                controls.Boost = false;
                _jumpChord.Start(snapshot.ClientTick);
            }
            else if (NavTarget is not null && Own.Jump == JumpPhase.None && Own.Order.Kind != OrderKind.Jump && boostPressed)
            {
                NavRefusedAt = _time;
            }

            // Forward thrust held from the start of the jump is not sent, so it does not fly the hull out of its own
            // arrival. No input cancels a jump (HullSim.InputEndsOrder); only halt does, and it clears the chord above.
            if (_jumpChord.Active)
                controls.Forward = 0f;

            var thrusting = controls.Strafe != 0 || controls.Vertical != 0 || controls.Forward != 0;
            controls.Aim = _lookDirection;
            controls.Align = thrusting && !freeLook;
        }
        else if (inputEnabled && !editing)
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

        if (LookDegrees is { } look && Mode == ViewMode.Pilot && !editing)
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
        if (_devices.Muted && GasView is { } gasView && gasView is not ("pilot" or "pilot-absent"))
        {
            var gasCoord = galaxy.SectorOf(OwnPosition);
            if (!ReferenceEquals(_gasCaptureGalaxy, galaxy) || _gasCaptureCoord != gasCoord)
            {
                // The cloud the own hull's sector anchors, as generation names it; nothing else is generated.
                _gasCaptureGalaxy = galaxy;
                _gasCaptureCoord = gasCoord;
                GasCloud captured = default;
                if (galaxy.GasClouds?.TryGetCloud(galaxy, gasCoord, out captured) != true)
                    throw new InvalidDataException("--gas-view needs a sector that anchors a cloud: use --sector -3,0,5 with the default seed.");
                _gasCapture = new GasCloudPlacement(gasCoord, captured);
                Console.WriteLine($"Gas scenario: seed={galaxy.Seed}, sector={gasCoord}, cloud={captured}");
            }

            var cloud = _gasCapture.Cloud;
            var offset = gasView is "inside" or "inside-absent" ? Vector3.Zero : gasView == "edge" ? new Vector3(cloud.HalfSize.X * 0.92f, 0, 0) : new Vector3(0, 0, cloud.HalfSize.Z * 2.4f);
            if (GasDistance is { } radii && gasView is "outside" or "absent")
                offset = new Vector3(0, 0, MathF.Max(cloud.HalfSize.X, MathF.Max(cloud.HalfSize.Y, cloud.HalfSize.Z)) * radii);
            CameraPosition = GasCloudQuery.CenterOf(galaxy, _gasCapture) + offset;
            _forward = gasView == "offscreen" ? Vector3.UnitZ : -Vector3.UnitZ;
            _right = gasView == "offscreen" ? -Vector3.UnitX : Vector3.UnitX;
            _up = Vector3.UnitY;
        }
        // Which side of the opening the camera is on, from where the camera is this frame.
        UpdateThrough(dt);
        UpdateStream();
    }

    /// <summary>Builds this frame's instances and constants and records the scene.</summary>
    public void Draw(CommandBuffer commands, int frameIndex, SessionSnapshot snapshot, float time)
    {
        // The overlay check compares two captures pixel for pixel: dust, gas drift and composition time stand still.
        var overlayCheck = _devices.Muted && GasFixture == GasShaderFixture.OverlayCheck;
        if (overlayCheck) time = GasShaderFixture.OverlayCheckTime;
        var galaxy = _galaxy!;
        // The same moment the camera was placed at. Asking the clock again here would put hulls a fraction of a
        // frame ahead of the camera, which at speed shows as the flown hull shivering against the view.
        var alpha = _frameAlpha;
        var camera = CameraPosition;
        var aspect = _viewport.X / _viewport.Y;
        var candidateCount = 0;
        Array.Clear(_meshCounts);
        _damage.Begin(snapshot.Hulls.AsSpan(0, snapshot.HullCount));
        _scene.BeginDamage();
        VisibleHulls = DrawnDents = DamageFallbacks = GeneratedRocks = VisibleRocks = 0;
        foreach (var slot in _slots)
            if (slot.State == SectorSlot.Ready && ReferenceEquals(slot.Galaxy, galaxy)) GeneratedRocks += slot.Content.AsteroidCount;
        _sparks.Begin(snapshot.Hulls.AsSpan(0, snapshot.HullCount));
        var marks = _scene.FtlMarks(frameIndex);
        var markCount = 0;
        var composite = new CompositeConstants { Setup = new float4(Exposure, aspect, time, 0) };
        var openings = new FtlOpenings();
        var clips = new FtlClips();
        var shieldInstances = _scene.ShieldInstances(frameIndex);
        var shieldVertices = _scene.ShieldVertices(frameIndex);
        var shieldIndices = _scene.ShieldIndices(frameIndex);
        _shields.Begin(_scene.ShieldDraws);

        // Hulls, where the update has them drawn this frame.
        var visuals = _visuals.Hulls;
        for (var i = 0; i < Math.Min(snapshot.HullCount, visuals.Length) && candidateCount < _candidates.Length; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            ref readonly var visual = ref visuals[i];

            // A hull whose design has no mesh yet is left out, never drawn as another design.
            if (hull.Design >= InstalledDesigns || hull.Design >= _designs.Length)
                continue;
            var design = _designs[hull.Design];
            // While the editor is open the draft is drawn in the ship's place, block by block, without its shield.
            if (_editShown && hull.Id == Editor.Hull && Editor.Draft is { } draft && Editor.Source is { } source)
            {
                AddDraft(ref candidateCount, draft, source, camera);
                continue;
            }

            var position = DrawnPosition(visual).RelativeTo(camera);
            var orientation = visual.Orientation;

            // The pilot's own hull is seen from the inside of its own jump; every other jump is seen from outside,
            // and a hull between a jump's ends is not drawn at all.
            if (visual.Inside && hull.Jump != JumpPhase.None)
                AddOwnJumpMarks(design.Flight, camera, marks, ref markCount);
            else if (!visual.Inside)
                AddHyperspaceMarks(hull, visual, design.Flight, camera, marks, ref markCount);
            var sparkPixels = design.Flight.Radius * _viewport.Y / (Math.Max(position.Length(), 1f) * _tanHalfFov);
            var sparkVisible = visual.Visible && Visible(position, design.Flight.Radius, aspect, out _)
                && _collision.Fade(position, design.Flight.Radius, sparkPixels) > 0;
            _sparks.Observe(hull, design, DrawnPosition(visual), orientation, sparkVisible, snapshot.ServerTickPrecise + alpha, _collision);
            var stretch = visual.Stretch;
            if (!visual.Visible || !Visible(position, design.Flight.Radius * stretch, aspect, out _))
                continue;
            // The material says what the mesh is and, above it in steps of four, which opening cuts it.
            // The pilot's own hull inside its jump takes no slot: its opening is cut from the frame's own jump.
            VisibleHulls++;
            var material = Material(visual.Inside, visual.Inside ? -1 : visual.Clip);
            // The shield goes where the hull goes, drawn out and cut with it by the same material.
            _shields.Add(hull.Id, position, orientation, stretch, material, shieldInstances, shieldVertices, shieldIndices);
            var pixels = design.Flight.Radius * _viewport.Y / (Math.Max(position.Length(), 1f) * _tanHalfFov);
            var detail = _collision.Fade(position, design.Flight.Radius * stretch, pixels);
            if (detail > 0 && hull.Visual.Count > 0)
            {
                var mesh = _damage.Get(hull.Id, hull.Revision, design, hull.Blocks.Span, hull.Visual, snapshot.Content.Rules.DentMaxDepth);
                var instance = MeshTransform(position, orientation, Vector3.One, design.Color, material, stretch);
                if (mesh is not null && _scene.AddDamage(frameIndex, mesh, instance, detail)) { DrawnDents += hull.Visual.Count; continue; }
                DamageFallbacks++;
            }
            if (visual.Intact)
            {
                Add(ref candidateCount, hull.Design, position, orientation, 1f, design.Color, material, stretch);
                continue;
            }

            // A hull that has lost blocks is drawn from the ones it still has, each as its own cube or prism.
            var alive = hull.Blocks.Span;
            for (var b = 0; b < design.BlockOffsets.Length && candidateCount < _candidates.Length; b++)
            {
                if (!BlockSet.Has(alive, b))
                    continue;
                var block = design.BlockOffsets[b];
                block.Z *= stretch;
                Add(ref candidateCount, _cubeMesh + (int)design.Shapes[b], position + Vector3.Transform(block, orientation), orientation, design.BlockHalfExtents[b] * 2f, design.Color, material, stretch);
            }
        }

        AddOpenings(camera, aspect, marks, ref markCount, ref composite, ref openings, ref clips);
        if (overlayCheck && GasOverlayOpening && composite.Setup.W < CompositeConstants.MaxLenses)
        {
            // Behind the camera and facing away: it switches the renderer to deferred gas without bending any pixel.
            openings.Set((int)composite.Setup.W, -_forward * 10f, 2, _forward, _up, FtlKind.Rift, 1, 0.3f);
            composite.Setup.W += 1;
        }

        PrepareRockInstances(camera, aspect, ref candidateCount);

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

        _scene.AddSparks(frameIndex, _sparks.Expand(camera, _right, _up, _forward, aspect, _tanHalfFov, _viewport.Y, snapshot.ServerTickPrecise + alpha, _collision));
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
            Clips = clips,
        };

        FillOwnJump(ref constants, camera);
        _scene.BeginGas(_gasTiers.MaxDraws);
        GasDraws = default;
        if (GasView is not ("absent" or "inside-absent" or "pilot-absent") && galaxy.GasClouds is { } gas)
            QueueGas(galaxy, gas, camera, aspect, time);

        if (_devices.Muted && GasFixture is > 0 and <= GasShaderFixture.Count)
        {
            var fixtureGas = GasShaderFixture.Apply(GasFixture, ref constants, ref composite);
            _scene.BeginGas();
            _scene.AddGas(fixtureGas, SceneRenderer.WholeView);
            GasDraws = (1, 0, 0);
        }

        // The lenses need the sky kept apart while a rift or a wormhole is in view, from outside or the pilot's own.
        var lensing = composite.Setup.W > 0f || (constants.Ftl.Y > 0f && constants.Ftl.X > (float)FtlKind.Hyperspace);
        // The overlay check needs the ordinary gas pass, which any queued shield (even off screen) replaces, so it
        // queues none in either capture; hulls in view are drawn without shields in both.
        _scene.QueueShields(overlayCheck ? 0 : _shields.Draws.Length, _shields.Look.Constants);
        _scene.Render(commands, frameIndex, constants, composite, _batches.AsSpan(0, rockBatches), _batches.AsSpan(rockBatches, batchCount - rockBatches), _commandBlend > 0.01f, markCount, lensing);
    }

    public float ExposureValue => Exposure;

    /// <summary>
    /// Queues every known cloud in view, once for each level of detail it shows at: one, or two while it crosses
    /// from a level to the next. Far first, so of a cloud's two draws the cheaper one is under the other.
    /// </summary>
    private void QueueGas(Galaxy galaxy, GasCloudModule gas, Double3 camera, float aspect, float time)
    {
        var sectorSize = (float)galaxy.SectorSize;
        var full = 0;
        var middle = 0;
        var distant = 0;
        foreach (ref readonly var placed in _gasField.Clouds(galaxy))
        {
            var cloud = placed.Cloud;
            var center = GasCloudQuery.CenterOf(galaxy, placed).RelativeTo(camera);
            var distance = center.Length();
            var largest = MathF.Max(cloud.HalfSize.X, MathF.Max(cloud.HalfSize.Y, cloud.HalfSize.Z));
            _gasTiers.Shares(distance, largest, sectorSize, out var near, out var mid, out var far);
            var reach = cloud.HalfSize.Length();
            if (near + mid + far < GasMinShare || !Visible(center, reach, aspect, out _)) continue;
            var view = new Vector3(Vector3.Dot(center, _right), Vector3.Dot(center, _up), Vector3.Dot(center, _forward));
            var bounds = SphereBounds(view, reach, _tanHalfFov, aspect);
            var draw = new GasConstants
            {
                Center = new float4(center.X, center.Y, center.Z, cloud.Seed),
                HalfSize = new float4(cloud.HalfSize.X, cloud.HalfSize.Y, cloud.HalfSize.Z, gas.ExtinctionOf(cloud, sectorSize)),
                Tint = new float4(gas.Tint.X, gas.Tint.Y, gas.Tint.Z, 0),
                Motion = new float4(time * gas.Motion, 0, gas.WispinessOf(cloud), distance),
                Shape = new float4(gas.Shape.X, gas.Shape.Y, gas.Shape.Z, gas.Shape.W),
                Look = new float4(gas.Look.X, gas.Look.Y, gas.Look.Z, gas.Look.W),
                Fine = _gasTiers.CloseUpFor(cloud.HalfSize.X),
            };
            if (far >= GasMinShare)
            {
                draw.Motion.Y = far;
                draw.Budget = new float4(_gasTiers.FarSamples, GasConstants.Far, 0, 0);
                draw.Tint.W = 2 - _gasTiers.FarDetail;
                _scene.AddGas(draw, bounds);
                distant++;
            }
            if (mid >= GasMinShare)
            {
                draw.Motion.Y = mid;
                draw.Budget = new float4(_gasTiers.MidSamples, GasConstants.Middle, 0, 0);
                draw.Tint.W = 0;
                _scene.AddGas(draw, bounds);
                middle++;
            }
            if (near >= GasMinShare)
            {
                draw.Motion.Y = near;
                draw.Budget = new float4(gas.Samples, GasConstants.Full, 0, 0);
                draw.Tint.W = 0;
                _scene.AddGas(draw, bounds);
                full++;
            }
        }
        GasDraws = (full, middle, distant);
    }

    /// <summary>
    /// The part of the view a sphere can show in: left, top, right and bottom as shares of the view, which reach
    /// past its edges when the sphere does. <paramref name="view"/> is the sphere's centre along the camera's
    /// right, up and forward. Never smaller than the sphere's outline, so a draw cut to it loses nothing.
    /// </summary>
    internal static float4 SphereBounds(Vector3 view, float radius, float tanHalfFov, float aspect)
    {
        Span(view.X, view.Z, radius, out var left, out var right);
        Span(view.Y, view.Z, radius, out var low, out var high);
        var x = 0.5f / (tanHalfFov * aspect);
        var y = 0.5f / tanHalfFov;
        // Up on screen is down in the view's shares.
        return new float4(Limit(left * x + 0.5f), Limit(0.5f - high * y), Limit(right * x + 0.5f), Limit(0.5f - low * y));

        static float Limit(float share) => Math.Clamp(share, -1f, 2f);

        // The tangents of the two lines through the camera that touch the sphere, in the plane of one screen axis
        // and the view direction. A side the sphere reaches behind the camera on is open.
        static void Span(float across, float ahead, float radius, out float from, out float to)
        {
            const float Open = 1e6f;
            const float Side = 1.55f;
            var squared = across * across + ahead * ahead;
            from = -Open;
            to = Open;
            if (squared <= radius * radius * 1.0001f) return;
            var middle = MathF.Atan2(across, ahead);
            var half = MathF.Asin(MathF.Min(radius / MathF.Sqrt(squared), 1f));
            // Wholly to one side and behind: nothing of it is in front of the camera.
            if (middle - half >= Side) { from = to = Open; return; }
            if (middle + half <= -Side) { from = to = -Open; return; }
            if (middle - half > -Side) from = MathF.Tan(middle - half);
            if (middle + half < Side) to = MathF.Tan(middle + half);
        }
    }

    /// <summary>
    /// The player's own jump, seen from the inside in the pilot view, as the frame constants give it to the
    /// composite pass and to the mesh pass, which cuts the pilot's own hull with it. Left at zero otherwise.
    /// </summary>
    private void FillOwnJump(ref FrameConstants constants, Double3 camera)
    {
        if (_jumpStage <= 0f || !_ownInside)
            return;
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

    /// <summary>
    /// The pilot's own opening as the mesh pass cuts their hull with it for the camera where it is now: the frame
    /// constants <see cref="Draw"/> uploads, read back by <see cref="FtlAperture"/>. Inactive when there is none.
    /// </summary>
    internal FtlAperture OwnAperture
    {
        get
        {
            var constants = new FrameConstants();
            FillOwnJump(ref constants, CameraPosition);
            var lens = new Vector4(_tanHalfFov, _viewport.X / Math.Max(_viewport.Y, 1f), _viewport.X, _viewport.Y);
            return new FtlAperture(Vector(constants.Ftl), Vector(constants.FtlAxis), Vector(constants.FtlUp), Vector(constants.FtlEntry),
                Vector(constants.FtlExit), Vector(constants.FtlShape), _forward, _right, _up, lens);

            static Vector4 Vector(float4 v) => new(v.X, v.Y, v.Z, v.W);
        }
    }

    /// <summary>The angle one pixel of the view spans, as the passes that cut the pilot's own hull take it.</summary>
    internal float PixelAngle => 2f / Math.Max(_viewport.Y, 1f) * _tanHalfFov;

    /// <summary>Muted field-overview isolation; null preserves the ordinary scene.</summary>
    internal SectorCoord? CaptureOnlySector { get; set; }

    /// <summary>Production scan and transform path, also used by matched CPU measurements.</summary>
    internal void PrepareRockInstances(Double3 camera, float aspect, ref int candidateCount)
    {
        PrepareRockSelection(camera, aspect, candidateCount);
        foreach (var selected in _rockSelection.Items)
            AddRock(_slots[selected.Slot], selected.Id.Index, selected.Pixels, _galaxy!, ref candidateCount);
    }

    /// <summary>Runs framebuffer admission once, after hull reservation, and publishes the same identities to picking.</summary>
    internal void PrepareRockSelection(Double3 camera, float aspect, int hullInstances)
    {
        _rockFrame = new RockFrame(camera, _right, _up, _forward, _tanHalfFov, _viewport, _screen);
        _hasRockFrame = true;
        EligibleRocks = 0;
        _rockSelection.Begin(_candidates.Length - hullInstances);
        if (hullInstances >= _candidates.Length)
        {
            foreach (var slot in _slots)
            {
                Array.Clear(slot.Drawn);
                slot.Scanned = slot.State == SectorSlot.Ready && ReferenceEquals(slot.Galaxy, _galaxy);
            }
            return;
        }
        for (var slotIndex = 0; slotIndex < _slots.Length; slotIndex++)
        {
            var slot = _slots[slotIndex];
            slot.Scanned = false;
            if (slot.State == SectorSlot.Ready && ReferenceEquals(slot.Galaxy, _galaxy) && (!_devices.Muted || CaptureOnlySector is not { } only || slot.Coord == only))
            {
                slot.Scanned = true;
                SelectRocks(slot, slotIndex, camera, aspect);
            }
        }
        foreach (var slot in _slots) Array.Clear(slot.Drawn);
        foreach (var selected in _rockSelection.Items)
            _slots[selected.Slot].Drawn[selected.Id.Index >> 6] |= 1UL << (selected.Id.Index & 63);
    }

    /// <summary>Numeric diagnostic rejection reason; retained does not imply passing GPU depth.</summary>
    internal int CaptureRockReason(RockRef rock, out float pixels)
    {
        pixels = 0;
        foreach (var slot in _slots)
        {
            if (!ReferenceEquals(slot.Galaxy, _galaxy) || slot.Coord != rock.Sector || slot.State == SectorSlot.Empty) continue;
            if (slot.State != SectorSlot.Ready || !slot.Scanned) return 1;
            if ((uint)rock.Index >= (uint)slot.Content.AsteroidCount) return 0;
            ref var asteroid = ref slot.Content.Asteroids[rock.Index];
            var position = _galaxy!.CenterOf(slot.Coord).RelativeTo(CameraPosition) + asteroid.LocalPosition + (slot.Displaced ? slot.Offsets[rock.Index] : Vector3.Zero);
            if (!Visible(position, asteroid.Radius, _viewport.X / _viewport.Y, out pixels)) return 2;
            if (pixels < RockMinPixels) return 3;
            return BlockSet.Has(slot.Drawn, rock.Index) ? 5 : 4;
        }
        return 0;
    }

    /// <summary>The retained identities from the last frame, borrowed by muted capture diagnostics.</summary>
    internal ReadOnlySpan<RockSelection.Entry> RetainedRocks => _rockSelection.Items;

    /// <summary>Whether all currently requested sectors are ready, for capture warm-up without an assumed delay.</summary>
    internal bool CaptureSectorsReady
    {
        get
        {
            if (_sectorRequests.Coordinates.IsEmpty) return false;
            foreach (var coord in _sectorRequests.Coordinates)
            {
                var ready = false;
                foreach (var slot in _slots)
                    if (slot.State == SectorSlot.Ready && ReferenceEquals(slot.Galaxy, _galaxy) && slot.Coord == coord)
                    {
                        ready = true;
                        break;
                    }
                if (!ready) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Muted capture harness hook for <c>--open command-menu*</c>: the command view looking down on the player's
    /// sector, the course menu open on a point ahead of the ship being flown, and the ship (or its fleet) selected.
    /// </summary>
    internal bool PrepareCourseMenuCapture(string view, PlayerInputBridge bridge)
    {
        if (!_devices.Muted || _snapshot is not { } snapshot)
            return false;
        var distance = view.EndsWith("close", StringComparison.Ordinal) ? 3000.0 : view.EndsWith("far", StringComparison.Ordinal) ? 450000.0 : 60000.0;
        SetCommandCapturePose(OwnPosition, distance < 10000 ? 7000f : 90000f, 0f, -0.95f);
        ClearSelection();
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Owner == snapshot.PlayerId && (view.EndsWith("fleet", StringComparison.Ordinal) ? !hull.IsOwn : hull.IsOwn))
                AddToSelection(hull.Id);
        }

        CourseMenu.Open(_screen * new float2(0.5f, 0.32f), OwnPosition + new Double3(0, 0, -distance), _time);
        return true;
    }

    /// <summary>Muted capture harness hook; angles are radians. Call before Update, never from ordinary input.</summary>
    internal bool SetCommandCapturePose(Double3 focus, float distance, float yaw, float pitch)
    {
        if (!_devices.Muted) return false;
        SetMode(ViewMode.Command);
        _followId = 0;
        _focusBlend = 1;
        _commandBlend = 1;
        _commandFocus = focus;
        _commandDistance = Math.Clamp(distance, MinCommandDistance, MaxCommandDistance);
        _commandYaw = yaw;
        _commandPitch = Math.Clamp(pitch, -1.52f, -0.1f);
        return true;
    }

    /// <summary>Adds one sector's visible asteroids to the frame's instances.</summary>
    private void SelectRocks(SectorSlot slot, int slotIndex, Double3 camera, float aspect)
    {
        var content = slot.Content;
        if (content.AsteroidCount == 0 || !SectorInView(slot, camera, aspect, _viewport.Y, out var center, out var smallest))
            return;

        slot.CameraCenter = center;
        for (var i = 0; i < content.AsteroidCount; i++)
        {
            ref var rock = ref content.Asteroids[i];

            // Largest first: once one is too small to see from here, so are all that follow.
            if (rock.Radius < smallest)
                break;
            var position = center + rock.LocalPosition + (slot.Displaced ? slot.Offsets[i] : Vector3.Zero);
            if (!Visible(position, rock.Radius, aspect, out var pixels) || pixels < RockMinPixels)
                continue;

            EligibleRocks++;
            _rockSelection.Offer(new RockRef(slot.Coord, i), slotIndex, pixels, BlockSet.Has(slot.Drawn, i));
        }
    }

    private void AddRock(SectorSlot slot, int i, float pixels, Galaxy galaxy, ref int candidateCount)
    {
        ref var rock = ref slot.Content.Asteroids[i];
        var position = slot.CameraCenter + rock.LocalPosition + (slot.Displaced ? slot.Offsets[i] : Vector3.Zero);
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
        VisibleRocks++;
        Add(ref candidateCount, _asteroidMeshBase + detail * Galaxy.AsteroidShapes + rock.Shape, position, rock.Rotation, rock.Radius, tint, 0f);
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

    /// <summary>Uses the last rendered rock identities with the camera input currently sees, before UpdateCamera.
    /// Slot reuse clears the mask; workers never write it. Hull interpolation and rock displacement use this update.
    /// The framebuffer admission owns the size cutoff; logical pixels only set the pointer grab radius.</summary>
    private int PickTargets(SessionSnapshot snapshot, float2 pointer, bool pilot)
    {
        var hullCount = 0;
        var visuals = _visuals.Hulls;
        for (var i = 0; i < Math.Min(snapshot.HullCount, visuals.Length); i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            ref readonly var visual = ref visuals[i];
            // Only what is drawn can be picked: not a hull between a jump's openings, nor the part past an opening,
            // and a hull drawn out by hyperspace is picked drawn out.
            if ((pilot && hull.IsOwn) || (!pilot && hull.Owner != snapshot.PlayerId) || !visual.Visible || !BlockSet.Any(visual.PickBlocks.Span))
                continue;

            // What is not drawn for want of a mesh is not picked either.
            if (hull.Design >= InstalledDesigns || hull.Design >= _designs.Length)
                continue;
            _pickHulls[hullCount++] = _visuals.Pick(hull.Id, visual, DrawnPosition(visual), _designs[hull.Design]);
            // The pilot's own hull, still drawn from inside its jump as the command view takes over, is cut as its
            // mesh pass cuts it.
            if (visual.Inside)
                _pickHulls[hullCount - 1] = _pickHulls[hullCount - 1] with { Aperture = OwnAperture };
        }
        var sectorCount = 0;
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy)) continue;
            _pickSectors[sectorCount++] = new PickSector(slot.Coord, _galaxy!.CenterOf(slot.Coord),
                slot.Content.Asteroids, slot.Content.AsteroidCount, slot.Displaced ? slot.Offsets : null,
                slot.Content.BoundsCenter, slot.Content.BoundsRadius, slot.Drawn);
        }
        var nx = pointer.X / _screen.X * 2 - 1;
        var ny = pointer.Y / _screen.Y * 2 - 1;
        var ray = Vector3.Normalize(_forward + _right * (nx * _tanHalfFov * _screen.X / _screen.Y) - _up * (ny * _tanHalfFov));
        return TargetPicking.Pick(ray, CameraPosition, _forward, _screen.Y / (2 * _tanHalfFov),
            _pickHulls.AsSpan(0, hullCount), _pickSectors.AsSpan(0, sectorCount), _pickHits, 0, NearPlane);
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
            if (CanTakeControlOf(target))
            {
                bridge.RequestControl(TargetHull);
                TargetHull = 0;
            }
            else
            {
                _outOfSectorAt = _time;
            }
        }
        else
        {
            _deniedAt = _time;
        }
    }

    /// <summary>
    /// Adds the light of another hull's hyperspace jump, or of the pilot's own seen from the command view: gathering
    /// ahead of the nose while the drive works, where the hull lunges into it, and at the far end, where the hull
    /// streaks in and settles. The light left behind when a drive fires is added with the openings.
    /// </summary>
    private void AddHyperspaceMarks(in HullView hull, in HullVisuals.HullVisual visual, in FlightParams flight, Double3 camera, Span<FtlMark> marks, ref int markCount)
    {
        var kind = hull.IsOwn && FtlPreview != FtlKind.None ? FtlPreview : flight.Ftl;
        if (hull.Jump == JumpPhase.None || kind != FtlKind.Hyperspace)
            return;
        var forward = Vector3.Transform(-Vector3.UnitZ, visual.Orientation);
        var stage = EffectStage(hull.Jump);
        var working = stage == 1f;
        var at = HyperspaceMarkPosition(visual.Anchor, forward, flight.Radius, working).RelativeTo(camera);
        var reach = working ? 0f : ArrivalReach(flight, _snapshot!.Content.Rules);
        AddMark(marks, ref markCount, at, flight.Radius * (working ? 4f : reach), kind, stage, HyperspaceMarkProgress(hull, flight), (hull.Id & 0xFF) / 255f, forward, reach);
    }

    /// <summary>
    /// Hands the composite pass the openings of the rifts and wormholes seen from outside, and the mesh pass the
    /// plane each one cuts its own hull at; adds the light hyperspace leaves behind where a drive has fired.
    /// </summary>
    private void AddOpenings(Double3 camera, float aspect, Span<FtlMark> marks, ref int markCount, ref CompositeConstants composite, ref FtlOpenings openings, ref FtlClips clips)
    {
        var passages = _visuals.Passages;
        for (var i = 0; i < passages.Length; i++)
        {
            ref readonly var slot = ref passages[i];
            if (slot.Id == 0)
                continue;
            if (slot.Kind == FtlKind.Hyperspace)
            {
                if (slot.Flash > 0f)
                    AddMark(marks, ref markCount, slot.Mouth.RelativeTo(camera), slot.Radius * 4f, FtlKind.Hyperspace, 3f, slot.Flash, slot.Seed, slot.Axis);
                continue;
            }

            if (slot.Clip != 0f)
                clips.Set(i, slot.Mouth.RelativeTo(camera), slot.Axis * slot.Clip);
            AddOpening(slot, camera, aspect, ref composite, ref openings);
        }
    }

    /// <summary>Hands a passage's opening to the composite pass, if it is open and could be in view.</summary>
    private void AddOpening(in HullVisuals.PassageState slot, Double3 camera, float aspect, ref CompositeConstants composite, ref FtlOpenings openings)
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
        openings.Set(index, centre, opening, slot.Axis, slot.Up, slot.Kind, slot.Open, slot.Seed);
        composite.Setup.W = index + 1;
    }

    /// <summary>
    /// The light of the pilot's own hyperspace jump, at the nose. A rift's or wormhole's openings are worked out in
    /// the composite pass from the frame constants.
    /// </summary>
    private void AddOwnJumpMarks(in FlightParams flight, Double3 camera, Span<FtlMark> marks, ref int markCount)
    {
        if ((FtlPreview != FtlKind.None ? FtlPreview : flight.Ftl) != FtlKind.Hyperspace)
            return;
        var stage = EffectStage(Own.Jump);
        var working = stage == 1f;
        var at = HyperspaceMarkPosition(OwnPosition, _jumpAxis, flight.Radius, working).RelativeTo(camera);
        var reach = working ? 0f : ArrivalReach(flight, _snapshot!.Content.Rules);
        AddMark(marks, ref markCount, at, flight.Radius * (working ? 4f : reach), FtlKind.Hyperspace, stage, HyperspaceMarkProgress(Own, flight), (Own.Id & 0xFF) / 255f, _jumpAxis, reach);
    }

    /// <summary>The arrival light keeps its short approach window even when transit lasts much longer.</summary>
    internal static float HyperspaceMarkProgress(in HullView hull, in FlightParams flight) =>
        hull.Jump == JumpPhase.Tunnel
            ? Math.Clamp(1f - HullVisuals.TransitClock(hull, flight).Left / Math.Max(flight.FtlTunnelSeconds, 0.1f), 0f, 1f)
            : JumpProgress(hull, flight);

    /// <summary>The world-space light origin, shared by pilot and observer views.</summary>
    internal static Double3 HyperspaceMarkPosition(Double3 anchor, Vector3 axis, float radius, bool working) =>
        anchor + axis * (radius * (working ? HullVisuals.HyperspaceLight : -1.5f));

    /// <summary>
    /// Notices a new attempt at a jump while the drive works: one from rest or from a previous jump still easing out,
    /// or an order that changed while the drive worked, which the server restarts within one tick, so the hull is
    /// never seen with none. A new attempt lays its passage out for the camera as it is now, and what the previous
    /// one had drawn eases out over it, as after a cancel, instead of vanishing in one frame.
    /// </summary>
    private void BeginAttempt(in HullView own, float stage, float dt)
    {
        var working = stage == 1f;
        var fresh = working
            && (_jumpWas is not (JumpPhase.Align or JumpPhase.Charge)
                || (_jumpWas == JumpPhase.Charge && own.Jump == JumpPhase.Align)
                || !own.Order.Target.Equals(_jumpTarget)
                || own.JumpCalculation + 0.5f < _jumpCalculated);
        if (fresh)
        {
            _jumpChaseLatched = false;
            _carryOffset = _jumpOffset;
            _carryOpen = _jumpEntryOpen;
            _carryStretch = _jumpStretch;
            _carryCover = _jumpCover;
        }
        else
        {
            var fade = MathF.Exp(-5f * dt);
            _carryOffset *= fade;
            _carryStretch *= fade;
            _carryCover *= fade;
            _carryOpen *= MathF.Exp(-2f * dt);
        }

        _jumpWas = own.Jump;
        _jumpTarget = own.Order.Target;
        _jumpCalculated = own.JumpCalculation;
    }

    /// <summary>A session or controlled-hull change cannot inherit another hull's passage.</summary>
    private void ResetOwnJump()
    {
        _jumpWas = JumpPhase.None;
        _carryOffset = _carryOpen = _carryStretch = _carryCover = 0f;
        _jumpOffset = _jumpThrough = _jumpTravel = _jumpCover = _jumpStretch = _jumpLight = _jumpStage = _jumpChase = 0f;
        _jumpEntryRadius = _jumpExitRadius = _jumpEntryOpen = _jumpExitOpen = 0f;
        _jumpEntry = _jumpExit = default;
        _jumpExitPlaced = _jumpThroughExit = _jumpChaseLatched = false;
        _jumpKind = FtlKind.None;
        _jumpSunBlend = 1f;
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
            _jumpChaseLatched = false;
            _jumpWas = JumpPhase.None;
            _carryOffset = _carryOpen = _carryStretch = _carryCover = 0f;
            _jumpSunBlend = 1f;
            var remaining = Math.Max(Math.Abs(_jumpOffset), Math.Max(Math.Max(_jumpCover, _jumpStretch), Math.Max(_jumpEntryOpen, _jumpExitOpen)));
            if (remaining < 0.001f)
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
        BeginAttempt(own, stage, dt);
        // Seconds since the drive fired and until the transit ends, carried forward between ticks like the progress.
        var (since, left) = stage >= 2f ? HullVisuals.TransitClock(own, flight, _frameAlpha / TickLoop.SimulationHz) : (0f, 0f);

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
            UpdateHyperspace(dt, stage, progress, since, left);
        else
            UpdatePassage(dt, own, flight, stage, progress, left);
        _jumpOffset *= inside;
    }

    /// <summary>
    /// Follows the server's answer to the plots and jump starts sent for the hull being flown. A new refusal is
    /// news for <see cref="NavRefusalAt"/>; a plot the server turned down gives the point back that it kept, so
    /// the NAV marker never shows a course the server does not hold. The server sends a state only when it
    /// changes, so a plot refused exactly as the last one was is not answered again; the screens check range
    /// before they plot, which keeps that to a point that moved after the check.
    /// </summary>
    private void ReconcileNavigation(SessionSnapshot snapshot)
    {
        var nav = snapshot.Exploration.Navigation;
        var changed = nav.ResultHull != _seenNavigation.ResultHull || nav.Refusal != _seenNavigation.Refusal
            || nav.HasPlot != _seenNavigation.HasPlot || nav.Target != _seenNavigation.Target || nav.Hull != _seenNavigation.Hull;
        _seenNavigation = nav;
        if (!changed)
            return;
        if (nav.ResultHull == Own.Id && nav.Refusal != JumpRefusal.None)
        {
            NavRefusal = nav.Refusal;
            NavRefusalAt = _time;
        }
        else if (nav.Refusal == JumpRefusal.None)
        {
            NavRefusal = JumpRefusal.None;
        }

        if (_plotPending is not { } pending)
            return;
        if (nav.HasPlot && nav.Hull == Own.Id && nav.Target == pending)
        {
            _plotPending = null;
            return;
        }

        if (nav.ResultHull == Own.Id && nav.Refusal != JumpRefusal.None)
        {
            _plotPending = null;
            NavTarget = nav.HasPlot && nav.Hull == Own.Id ? nav.Target : null;
        }
    }

    /// <summary>
    /// Keeps the plotted jump current: gone once the hull has made the jump (it left and has landed), never
    /// merely because the hull already is in the destination's sector, and whether the nose is on it.
    /// </summary>
    private void UpdateNav()
    {
        if (NavTarget is not null && Own.Jump is JumpPhase.Tunnel or JumpPhase.Arrive)
            _navJumpMade = true;
        if (NavTarget is not { } nav || !HasJumpDrive(Own) || (_navJumpMade && Own.Jump == JumpPhase.None))
        {
            NavTarget = null;
            _plotPending = null;
            _navJumpMade = false;
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
    private void UpdateHyperspace(float dt, float stage, float progress, float since, float left)
    {
        const float MaxStretch = 1.4f;
        const float TopSpeed = 2.6f;
        _jumpOffset = 0f;
        _jumpEntryRadius = _jumpExitRadius = 0f;
        float speed;
        if (stage == 1f)
        {
            var build = Smooth((progress - 0.55f) / 0.45f);
            _jumpStretch = Math.Max(MaxStretch * build * build, _carryStretch);
            _jumpCover = Math.Max(Smooth((progress - 0.7f) / 0.3f), _carryCover);
            var rush = Smooth((progress - 0.7f) / 0.3f);
            speed = TopSpeed * 0.45f * rush * rush;
        }
        else
        {
            _jumpStretch = MaxStretch * (1f - Smooth((0.35f - left) / 1.6f));
            _jumpCover = 1f - Smooth((0.7f - left) / 1.2f);
            var slow = Smooth((1.1f - left) / 1.6f);
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
    private void UpdatePassage(float dt, in HullView own, in FlightParams flight, float stage, float progress, float left)
    {
        var radius = flight.Radius;
        var full = radius * OpeningRadius;
        var ahead = radius * OpeningAhead;
        _jumpCover = 0f;
        _jumpStretch = 0f;
        // How far the camera sat behind the hull when the jump began, kept for the whole jump: the hull's path through
        // the openings, and where they are, are laid out for it once. Zooming afterwards moves only the camera.
        if (!_jumpChaseLatched)
        {
            _jumpChase = Math.Max(3f, radius * 3.3f) * _chaseZoom;
            _jumpChaseLatched = true;
        }

        float speed;
        if (stage == 1f)
        {
            _jumpExitPlaced = false;
            _jumpExitRadius = 0f;
            _jumpExitOpen = 0f;

            // The hull starts to move at 60% of the charge and accelerates, so the camera has passed the far side of
            // the opening (and it has closed behind) by 97%, before the drive fires.
            var through = ahead + _jumpChase + full * 2.2f;
            var move = Math.Clamp((progress - 0.6f) / 0.4f, 0f, 1f);
            _jumpOffset = Math.Max(through / MathF.Pow(0.925f, 2.2f) * MathF.Pow(move, 2.2f), _carryOffset);

            var open = Smooth(progress / 0.8f) * 0.95f + 0.05f;
            var behind = (_jumpOffset - _jumpChase - ahead - full) / full;
            var close = Smooth(behind);
            _jumpEntry = OwnPosition + _jumpAxis * ahead;
            _jumpEntryOpen = Math.Max(open * (1f - close), _carryOpen);
            _jumpEntryRadius = full * _jumpEntryOpen;
            var rush = Smooth((progress - 0.62f) / 0.33f);
            speed = 1.4f * rush * rush;
            _jumpLight = Smooth((progress - 0.75f) / 0.2f);
        }
        else
        {
            // From the moment the drive fires, over the tunnel and the arrival: the hull comes from far behind where
            // it arrives, and slows to a stop there. The far opening is placed so the camera passes through its
            // middle just before the tunnel ends. A transit longer than the drive's base one waits in the throat
            // first: what follows takes the base transit's length before the end, however far the jump, worked
            // out from the seconds left so it stays exact however long the transit is.
            var tunnel = Math.Clamp(flight.FtlTunnelSeconds, 0.1f, Math.Max(HullVisuals.TunnelSeconds(own, flight), 0.1f));
            var total = tunnel + Math.Max(flight.FtlArriveSeconds, 0.1f);
            var since = Math.Clamp(tunnel - left, 0f, total);
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
            speed = 1.4f * (1f - Smooth((0.6f - left) / 1.0f));
            _jumpLight = 1f - Smooth((0.6f - left) / 0.9f);
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
                JumpPhase.Tunnel => (hull.JumpTimer + tick) / HullVisuals.TunnelSeconds(hull, flight),
                JumpPhase.Arrive => (hull.JumpTimer + tick) / flight.FtlArriveSeconds,
                _ => 0f,
            },
            0f,
            1f
        );
    }

    private static float Smooth(float x) => HullVisuals.Smooth(x);

    /// <summary>
    /// A hull mesh's material as <c>mesh.vert</c> reads it: 1 for a hull, 2 for the pilot's own, plus four times one
    /// more than the passage slot whose clipping plane cuts it (-1: none).
    /// </summary>
    internal static float Material(bool own, int clip) => (own ? 2f : 1f) + 4f * (clip + 1);

    /// <summary>
    /// Half the size of a hyperspace mark in hull radii where its point and streak are drawn; <c>ftl_mark.frag</c>
    /// has the same number.
    /// </summary>
    internal const float MarkReach = FlightRules.MinArrivalReach;

    /// <summary>
    /// How far a hull's hyperspace arrival reaches, in its own radii: the half size of the mark at the arriving end,
    /// the same for the pilot's own jump and any other hull's. Bigger hulls reach further with their radius, heavier
    /// ones with their mass, which saturates toward the content's <see cref="FlightRules.FtlArrivalReach"/>, so no
    /// arrival fills more than a bounded part of the sky. The shader takes the pulse's brightness from the same
    /// number.
    /// </summary>
    internal static float ArrivalReach(in FlightParams flight, FlightRules rules)
    {
        var mass = float.IsFinite(flight.Mass) ? Math.Max(flight.Mass, 0f) : 0f;
        var limit = Math.Clamp(rules.FtlArrivalReach, MarkReach, FlightRules.MaxArrivalReach);
        return MarkReach + (limit - MarkReach) * (mass / (mass + rules.FtlArrivalPulseMass));
    }

    /// <summary>Adds a mark; <paramref name="reach"/> is an arrival's half size in hull radii, otherwise 0.</summary>
    private static void AddMark(Span<FtlMark> marks, ref int count, Vector3 at, float size, FtlKind kind, float stage, float progress, float seed, Vector3 axis, float reach = 0f)
    {
        if (count < marks.Length)
        {
            marks[count++] = new FtlMark
            {
                PositionSize = new float4(at.X, at.Y, at.Z, size),
                Params = new float4((float)kind, stage, progress, seed),
                Axis = new float4(axis.X, axis.Y, axis.Z, reach),
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
                JumpPhase.Tunnel => hull.JumpTimer / HullVisuals.TunnelSeconds(hull, flight),
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

    /// <summary>
    /// Where the simulation has a hull this frame, for the map, orders and ranges. A jump can draw it elsewhere; see
    /// <see cref="DrawnPosition(in HullView)"/>.
    /// </summary>
    public Double3 HullPosition(in HullView hull) => hull.IsOwn ? OwnPosition : hull.Position;

    /// <summary>Whether the hull is present in the world view; strategic maps retain hulls while they are in transit.</summary>
    public bool IsDrawn(in HullView hull)
    {
        var visuals = _visuals.Hulls;
        var snapshot = _snapshot;
        for (var i = 0; snapshot is not null && i < Math.Min(snapshot.HullCount, visuals.Length); i++)
            if (snapshot.Hulls[i].Id == hull.Id)
                return visuals[i].Visible;
        return false;
    }

    /// <summary>
    /// Where a hull is drawn in the world this frame, for markers that sit on it: moved along a jump the way the
    /// scene draws it. A hull not in this frame's snapshot is where the simulation has it.
    /// </summary>
    public Double3 DrawnPosition(in HullView hull)
    {
        var visuals = _visuals.Hulls;
        var snapshot = _snapshot;
        for (var i = 0; snapshot is not null && i < Math.Min(snapshot.HullCount, visuals.Length); i++)
        {
            if (snapshot.Hulls[i].Id == hull.Id)
                return DrawnPosition(visuals[i]);
        }

        return HullPosition(hull);
    }

    private Double3 DrawnPosition(in HullVisuals.HullVisual visual) => visual.Inside ? visual.Position + _jumpAxis * _jumpOffset : visual.Position;

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
    /// True when a hull can be sent on a jump to <paramref name="target"/> now: it has a working drive, is not in
    /// transit, the point is within the drive's range and not too close, and the destination is inside or outside its sector.
    /// </summary>
    public bool CanJumpTo(in HullView hull, Double3 target) =>
        _snapshot is { } snapshot && JumpPlan.Check(snapshot, this, hull, target, out _) == JumpBlock.None;

    /// <summary>True when the hull still has a working faster-than-light drive; losing the drive block loses it.</summary>
    private static bool HasJumpDrive(in HullView hull) => hull.Stats.Ftl != FtlKind.None;

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

    /// <summary>
    /// Whether the map's take-control key may be read at all: no menu, social surface or Controls screen owns the
    /// keyboard, and no text field is being typed in.
    /// </summary>
    public static bool MapSwitchKeyAvailable(bool sessionMenuOpen, bool socialSurfaceOpen, bool controlsOpen, bool typing) =>
        !sessionMenuOpen && !socialSurfaceOpen && !controlsOpen && !typing;

    /// <summary>
    /// The map's take-control shortcut: when the switch action is pressed and exactly one hull is selected, no
    /// asteroid, and that hull is the player's and not the one being flown, asks the server to put the player in it, if it is in the sector the player is in; otherwise it raises the out-of-sector notice.
    /// The map, the selection and the view stay as they are; the own-hull marks follow the next snapshot that
    /// names the new hull. Returns whether a request was sent.
    /// </summary>
    public bool SwitchToSelectedFromMap(PlayerInputBridge bridge, bool keyboardAvailable)
    {
        if (!_actions.Pressed(_devices, InputAction.SwitchHull, keyboardAvailable))
            return false;
        PruneSelection();
        if (_selectedCount != 1 || _selectedRockCount != 0 || _selected[0] == Own.Id)
            return false;
        if (!TryGetHull(_selected[0], out var hull))
            return false;
        if (!CanTakeControlOf(hull))
        {
            _outOfSectorAt = _time;
            return false;
        }

        bridge.RequestControl(_selected[0]);
        return true;
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
            // Its jump is only plotted, with the server told so it can check the range and answer: the pilot starts it.
            if (_selected[i] == Own.Id && jump)
            {
                NavTarget = order.Target;
                _navJumpMade = false;
                _plotPending = order.Target;
                bridge.PlotJump(Own.Id, order.Target);
            }
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
            if (hull.Owner != snapshot.PlayerId || IsSelected(hull.Id) || !IsDrawn(hull) || !Project(DrawnPosition(hull), out var screen))
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

        // Input uses the previous rendered camera and its retained identities, before UpdateCamera.
        // Do not apply another logical-pixel threshold to framebuffer-selected rocks.
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy) || slot.Content.AsteroidCount == 0)
                continue;
            var center = _galaxy!.CenterOf(slot.Coord).RelativeTo(CameraPosition);
            for (var i = 0; i < slot.Content.AsteroidCount && _selectedRockCount < MaxSelection; i++)
            {
                if (!BlockSet.Has(slot.Drawn, i) || !RockOnScreen(slot, center, i, out var screen, out _, out _))
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
            if (_selected[0] == Own.Id)
            {
                SetMode(ViewMode.Pilot);
            }
            else if (TryGetHull(_selected[0], out var chosen) && CanTakeControlOf(chosen))
            {
                bridge.RequestControl(_selected[0]);
                SetMode(ViewMode.Pilot);
            }
            else
            {
                _outOfSectorAt = _time;
            }
        }

        if (!pointerOverUi && _devices.Scroll != 0)
            _commandDistance = Math.Clamp(_commandDistance * MathF.Pow(0.86f, _devices.Scroll), MinCommandDistance, MaxCommandDistance);

        // A press outside the open course menu closes it. A left or middle press only closes it, and so reaches
        // nothing else; a right press goes on to begin the camera drag or a menu of its own at the new point.
        if (CourseMenu.IsOpen && !pointerOverUi)
        {
            if (_devices.MousePressed(GlfwMouseButton.Left) || _devices.MousePressed(GlfwMouseButton.Middle))
            {
                CourseMenu.Close();
                pointerOverUi = true;
            }
            else if (_devices.MousePressed(GlfwMouseButton.Right))
            {
                CourseMenu.Close();
            }
        }

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

        // Right button: drag to rotate the view, click to open the course menu at the point under the pointer.
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
                if (!_rightMoved && GroundPoint(_devices.MousePosition, out var target))
                    CourseMenu.Open(_devices.MousePosition, target, _time);
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

        // The editor's orbit, in the hull's own frame about a point fixed to it, eased in and out.
        _editBlend += Math.Clamp((Editor.Shown ? 1f : 0f) - _editBlend, -dt / EditBlendSeconds, dt / EditBlendSeconds);
        var edit = Smooth(_editBlend);
        if (edit > 0f)
        {
            var editOrientation = Quaternion.Concatenate(Quaternion.CreateFromYawPitchRoll(_editYaw, _editPitch, 0f), EditedOrientation);
            var focus = EditedPosition + Vector3.Transform(_editPan, EditedOrientation);
            CameraPosition = Double3.Lerp(CameraPosition, focus + Vector3.Transform(new Vector3(0f, 0f, _editDistance), editOrientation), edit);
            orientation = Quaternion.Slerp(orientation, editOrientation, edit);
        }

        _right = Vector3.Transform(Vector3.UnitX, orientation);
        _up = Vector3.Transform(Vector3.UnitY, orientation);
        _forward = Vector3.Transform(-Vector3.UnitZ, orientation);

        var pilotFov = PilotFov + Math.Min(speed / 720f, 1f) * 0.16f;
        var fov = pilotFov + (CommandFov - pilotFov) * t;
        _tanHalfFov = MathF.Tan((fov + (CommandFov - fov) * edit) / 2f);
    }

    /// <summary>Seconds the camera takes to move into the editor's orbit and back.</summary>
    private const float EditBlendSeconds = 0.35f;

    /// <summary>The editor opened: the view drops back to the pilot view and the orbit starts above and behind.</summary>
    private void BeginEditing()
    {
        SetMode(ViewMode.Pilot);
        var radius = EditRadius();
        _editYaw = 0.6f;
        _editPitch = -0.45f;
        _editDistance = Math.Clamp(radius * 2.6f, EditMinDistance(radius), EditMaxDistance(radius));
        _editPan = Vector3.Zero;
        _editFocusing = false;
        _editGrab = -1;
        _editLeftDown = _editRightDown = _editMiddleDown = false;
        ResetPartGestures();
        _editTuning ??= EditorTuning.Load();
        _freeLook = false;
        _leftDragging = _rightDragging = false;
    }

    /// <summary>The editor closed: the view returns behind the hull, and held flight keys wait to be let go.</summary>
    private void EndEditing()
    {
        _flightHeld = true;
        _lookDirection = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
        _editLeftDown = _editRightDown = _editMiddleDown = false;
        ResetPartGestures();
        _editFocusing = false;
    }

    /// <summary>
    /// The radius the editor's camera works within: the ship's, or the draft's reach from the pivot when the draft
    /// has grown past it.
    /// </summary>
    private float EditRadius()
    {
        var radius = _designs[Math.Min(EditedDesign, _designs.Length - 1)].Flight.Radius;
        if (Editor.Draft is not { } draft || Editor.Source is not { } source)
            return radius;
        if (!ReferenceEquals(draft, _editReachDraft) || draft.Version != _editReachVersion)
        {
            _editReachDraft = draft;
            _editReachVersion = draft.Version;
            _editReach = EditorGeometry.Reach(draft.Boxes, source.Pivot, source.BlockSize);
        }

        return Math.Max(radius, _editReach);
    }

    /// <summary>
    /// Glides the editor's camera to the selected blocks, or to the whole draft with nothing selected: it orbits
    /// their middle at a distance that fits them in view, from the angle it has now.
    /// </summary>
    internal void FocusEditor()
    {
        if (Editor.Draft is not { } draft || Editor.Source is not { } source)
            return;
        if (!EditorGeometry.Bounds(draft.Boxes, Editor.Selection, out var low, out var high))
            return;
        var radius = EditRadius();
        _editPanGoal = EditorGeometry.ToHull((low + high) * 0.5f, source.Pivot, source.BlockSize);
        var fit = Math.Max((high - low).Length() * 0.5f * source.BlockSize, source.BlockSize);
        // A sphere of radius r fills the narrower half-angle of the view at r / sin(angle); a little more leaves a margin.
        var aspect = _screen.Y > 0 ? _screen.X / _screen.Y : 1f;
        var half = MathF.Atan(MathF.Tan(CommandFov / 2f) * Math.Min(1f, aspect));
        _editDistanceGoal = Math.Clamp(fit / MathF.Sin(half) * 1.15f, EditMinDistance(radius), EditMaxDistance(radius));
        _editFocusing = true;
    }

    private static float EditMinDistance(float radius) => Math.Max(radius * 0.5f, 3f);

    private static float EditMaxDistance(float radius) => radius * 12f + 60f;

    /// <summary>
    /// An applied edit replaced <paramref name="from"/> with <paramref name="to"/>: the selection, the followed hull
    /// and, once the snapshot flies the new hull, the target and course carry over to it.
    /// </summary>
    internal void ReplaceHull(uint from, uint to)
    {
        for (var i = 0; i < _selectedCount; i++)
            if (_selected[i] == from)
                _selected[i] = to;
        if (_followId == from)
            _followId = to;
        // Only the flown ship's replacement is flown next; a new ship built from the drone leaves the pilot where they are.
        if (from != Own.Id)
            return;
        _carryFrom = from;
        _carryTo = to;
    }

    /// <summary>
    /// Finds where the edited hull is when it is not the one flown, from the same visuals that draw it, so the draft
    /// and the camera stay on it as it drifts.
    /// </summary>
    private void UpdateEditedPose(SessionSnapshot snapshot)
    {
        _editedRemote = false;
        // The pose is kept while the camera eases out of the editor, so it does not jump to the flown ship.
        if ((Editor.Stage == EditorStage.Closed && _editBlend <= 0f) || Editor.Hull == 0 || Editor.Hull == Own.Id)
            return;
        var visuals = _visuals.Hulls;
        for (var i = 0; i < Math.Min(snapshot.HullCount, visuals.Length); i++)
        {
            if (snapshot.Hulls[i].Id != Editor.Hull)
                continue;
            _editedRemote = true;
            _editedPosition = DrawnPosition(visuals[i]);
            _editedRotation = visuals[i].Orientation;
            _editedDesign = snapshot.Hulls[i].Design;
            return;
        }
    }

    /// <summary>
    /// The editor's mouse and keys. Right drag orbits and a right click stops placing (or drops what is carried);
    /// middle drag pans and a middle click selects (Shift adds or removes); the wheel zooms. A left click places,
    /// or selects when not placing; a left drag that starts on a selected block moves the selection. A part dragged
    /// from the part list follows the pointer until the button is let go. Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z), Ctrl+C,
    /// Ctrl+V and Delete edit; the Follow key focuses the camera. Holding S sizes the part to place and R turns it;
    /// the right button still orbits through both. Nothing starts over the interface.
    /// </summary>
    private void UpdateEditorControls(bool inputEnabled, bool pointerOverUi, float dt)
    {
        _ghostMotion.Step(dt);
        if (!inputEnabled)
        {
            _editLeftDown = _editRightDown = _editMiddleDown = false;
            _editGrab = -1;
            _ringHovered = -1;
            ResetSizeDrag();
            // An interrupted drag will not see its release; settle its visual remainder onto the actual block.
            if (_ringPress)
                _ghostMotion.Aim(_ringAxis, 0f, Editor.GhostAt, Editor.PartSize, Editor.PartShape, _editTuning?.TurnSeconds ?? 0f);
            ResetRingDrag();
            // A surface over the world ends sizing where it is: the gesture's key may not be seen let go.
            if (Editor.SizingPart)
                Editor.EndPartSizing();
            if (Editor.Carry is EditorCarry.Part or EditorCarry.Move)
                Editor.CancelCarry();
            // A part being turned waits, ghost and all, until a surface over the world gives the input back. Escape,
            // R and clicks reach it only with the world's input.
            if (!Editor.RotatingPart)
                Editor.ClearHover();
            GlideEditorCamera(dt);
            return;
        }

        var control = _devices.KeyDown(GlfwKey.LeftControl) || _devices.KeyDown(GlfwKey.RightControl);
        var shift = _devices.KeyDown(GlfwKey.LeftShift) || _devices.KeyDown(GlfwKey.RightShift);
        if (control && _devices.KeyPressed(GlfwKey.Z))
        {
            if (shift)
                Editor.Redo();
            else
                Editor.Undo();
        }
        else if (control && _devices.KeyPressed(GlfwKey.Y))
            Editor.Redo();
        else if (control && _devices.KeyPressed(GlfwKey.C))
            Editor.CopySelection();
        else if (control && _devices.KeyPressed(GlfwKey.V))
            Editor.BeginPaste();
        if (_devices.KeyPressed(GlfwKey.Delete))
            Editor.DeleteSelected();
        if (Editor.TakeFocusRequest() || (!control && _actions.Pressed(_devices, InputAction.Follow, true)))
            FocusEditor();

        // While the part is turned with its rings, or sized with S held, the left button and the wheel are theirs;
        // the right button still orbits, and its click leaves placing alone.
        if (UpdatePartRotation(pointerOverUi, control, shift) || UpdatePartSizing(pointerOverUi, control))
        {
            GlideEditorCamera(dt);
            return;
        }

        var radius = EditRadius();
        if (!pointerOverUi && _devices.Scroll != 0)
        {
            _editFocusing = false;
            _editDistance = Math.Clamp(_editDistance * MathF.Pow(0.88f, _devices.Scroll), EditMinDistance(radius), EditMaxDistance(radius));
        }

        // Right: drag to orbit, click to drop what is carried or to stop placing.
        UpdateOrbit(pointerOverUi, false);

        // Middle: a drag pans across the view, kept within reach of the draft; a click selects.
        if (_devices.MousePressed(GlfwMouseButton.Middle) && !pointerOverUi)
        {
            _editMiddleDown = true;
            _editMiddleMoved = false;
            _editMiddleStart = _devices.MousePosition;
        }

        var middleClick = false;
        if (_editMiddleDown)
        {
            var moved = _devices.MousePosition - _editMiddleStart;
            _editMiddleMoved |= MathF.Abs(moved.X) + MathF.Abs(moved.Y) > ClickSlop;
            if (!_devices.MouseDown(GlfwMouseButton.Middle))
            {
                _editMiddleDown = false;
                middleClick = !_editMiddleMoved;
            }
            else if (_editMiddleMoved)
            {
                _editFocusing = false;
                var metresPerPixel = _editDistance * 2f * _tanHalfFov / Math.Max(_screen.Y, 1f);
                var delta = (-_right * _devices.MouseDelta.X + _up * _devices.MouseDelta.Y) * metresPerPixel;
                _editPan += Vector3.Transform(delta, Quaternion.Conjugate(EditedOrientation));
                var reach = radius * 2f + 10f;
                if (_editPan.Length() > reach)
                    _editPan = Vector3.Normalize(_editPan) * reach;
            }
        }

        // What is under the pointer, before a click is let go, so the click acts on what the player sees. While
        // something is carried the pointer may be over a panel with the button held by the world or by the part
        // list; only a panel under the pointer hides the ghost then.
        var draft = Editor.Draft;
        var source = Editor.Source;
        var carrying = Editor.Carry != EditorCarry.None;
        var blocked = carrying ? PointerOverPanel : pointerOverUi;
        if (blocked || (_editRightDown && _editRightMoved) || (_editMiddleDown && _editMiddleMoved) || !Editor.CanInspect || draft is null || source is null)
            Editor.ClearHover();
        else
        {
            PointerInGrid(out var origin, out var direction);
            // A moved group is laid against what is behind it, never against itself.
            var skip = Editor.Carry == EditorCarry.Move ? Editor.SelectionMarks : default;
            var found = EditorGeometry.Pick(draft.Boxes, origin, direction, skip, out var hit);
            // A ghost held where sizing or a turn left it stays while the pointer is still or on the ghost, so a click
            // there places it as it is; once the pointer leaves it for the ship, placing follows the pointer again.
            if (Editor.GhostPinned && !carrying && Editor.HasGhost
                && (_devices.MouseDelta == default || GhostUnder(origin, direction, found ? hit.Distance : float.PositiveInfinity, out _)))
            {
            }
            else if (found)
                Editor.SetHover(hit);
            else
                Editor.ClearHover();
        }

        if (middleClick && !pointerOverUi)
            Editor.Pick(shift);

        // A part dragged out of the part list: the interface holds the button, so its release is read raw. Let go
        // over the ship it is placed if it fits; over a panel, or where it does not fit, nothing changes.
        if (Editor.Carry == EditorCarry.Part)
        {
            if (!_devices.RawMouseDown(GlfwMouseButton.Left))
            {
                if (PointerOverPanel)
                    Editor.CancelCarry();
                else
                    Editor.Drop();
            }

            GlideEditorCamera(dt);
            return;
        }

        // Left: a click places or selects; a drag from a selected block moves the selection; any other drag does
        // nothing, so a slip of the hand changes nothing.
        if (_devices.MousePressed(GlfwMouseButton.Left) && !pointerOverUi)
        {
            _editLeftDown = true;
            _editLeftMoved = false;
            _editLeftStart = _devices.MousePosition;
            _editGrab = Editor.Carry == EditorCarry.None && Editor.HasHover && Editor.IsSelected(Editor.Hover.Block) ? Editor.Hover.Block : -1;
        }

        if (_editLeftDown)
        {
            var moved = _devices.MousePosition - _editLeftStart;
            _editLeftMoved |= MathF.Abs(moved.X) + MathF.Abs(moved.Y) > ClickSlop;
            if (_editLeftMoved && _editGrab >= 0 && Editor.Carry == EditorCarry.None)
            {
                Editor.BeginMove(_editGrab);
                _editGrab = -1;
            }

            if (!_devices.MouseDown(GlfwMouseButton.Left))
            {
                _editLeftDown = false;
                _editGrab = -1;
                if (Editor.Carry == EditorCarry.Move)
                {
                    if (PointerOverPanel)
                        Editor.CancelCarry();
                    else
                        Editor.Drop();
                }
                else if (!_editLeftMoved && !pointerOverUi)
                    Editor.Click(shift);
            }
        }

        GlideEditorCamera(dt);
    }

    /// <summary>Pixels a press may move and still count as a click rather than a drag.</summary>
    private const float ClickSlop = 5f;

    /// <summary>
    /// The right button: a drag orbits the camera and a click drops what is carried or stops placing. A press that
    /// began while a part was sized or turned (<paramref name="reserved"/>) only orbits: its click changes nothing.
    /// A press over the interface starts nothing.
    /// </summary>
    private void UpdateOrbit(bool pointerOverUi, bool reserved)
    {
        if (_devices.MousePressed(GlfwMouseButton.Right) && !pointerOverUi)
        {
            _editRightDown = true;
            _editRightMoved = false;
            _editRightStart = _devices.MousePosition;
            _editRightReserved = reserved;
        }

        if (!_editRightDown)
            return;
        _editRightReserved |= reserved;
        var moved = _devices.MousePosition - _editRightStart;
        _editRightMoved |= MathF.Abs(moved.X) + MathF.Abs(moved.Y) > ClickSlop;
        if (_editRightMoved)
        {
            _editYaw -= _devices.MouseDelta.X * 0.006f;
            _editPitch = Math.Clamp(_editPitch - _devices.MouseDelta.Y * 0.006f, -1.5f, 1.5f);
        }

        if (_devices.MouseDown(GlfwMouseButton.Right))
            return;
        _editRightDown = false;
        if (_editRightMoved || _editRightReserved)
            return;
        if (Editor.Carry != EditorCarry.None)
            Editor.CancelCarry();
        else
            Editor.StopPlacing();
    }

    /// <summary>The camera and the ray under the pointer, in the draft's grid.</summary>
    private void PointerInGrid(out Vector3 origin, out Vector3 direction)
    {
        var source = Editor.Source!;
        var inverse = Quaternion.Conjugate(EditedOrientation);
        origin = EditorGeometry.ToGrid(Vector3.Transform(CameraPosition.RelativeTo(EditedPosition), inverse), source.Pivot, source.BlockSize);
        direction = Vector3.Transform(PointerRay(_devices.MousePosition), inverse);
    }

    /// <summary>
    /// The face of the ghost of the part to place that the ray enters, nearer than <paramref name="nearer"/>: a
    /// wedge's slant counts as the box side it stands for (<see cref="EditorGeometry.SlantEntry"/>). False when the
    /// ray misses it.
    /// </summary>
    private bool GhostUnder(Vector3 origin, Vector3 direction, float nearer, out int side)
    {
        var ghost = new DesignBox(Editor.GhostAt, Editor.PartSize, null!, Editor.PartShape);
        return EditorGeometry.Enter(ghost, origin, direction, out var distance, out side) && distance <= nearer;
    }

    /// <summary>The side of the ghost being sized that is highlighted (dragged, or under the pointer), or -1.</summary>
    internal int PartSizingFace => Editor.SizingPart ? _sizeDragging ? _sizeSide : _sizeFace : -1;

    /// <summary>True while a face of the ghost is dragged up and down, its axis pointing too nearly along the view.</summary>
    internal bool PartSizingEdgeOn => Editor.SizingPart && _sizeDragging && _sizeEdgeOn;

    /// <summary>
    /// The part-size gesture. S held with the ghost on a face holds the ghost still; the face of the ghost under the
    /// pointer is highlighted, and a left drag on it moves that face by whole cells, from one cell to the content's
    /// largest box side, while the opposite face stays put. The pointer is followed along the face's axis as it
    /// shows on screen at the press; an axis pointing too nearly along the view is followed up (grow) and down
    /// instead. Letting go of the button or of S keeps the size and place for the next click, which places it;
    /// Escape puts both back. The right button orbits meanwhile, holding a drag still until it is let go. Never
    /// starts over the interface, with Ctrl or a mouse button held, while something is carried, while selecting or
    /// turning, or on a damaged ship. True while it holds the controls.
    /// </summary>
    private bool UpdatePartSizing(bool pointerOverUi, bool control)
    {
        var allowed = Editor.CanEdit && Editor.Placing && Editor.Carry == EditorCarry.None && _editTuning is not null && Editor.Draft is not null;
        if (!Editor.SizingPart)
        {
            ResetSizeDrag();
            if (!allowed || control || pointerOverUi || _editLeftDown || _editRightDown || _editMiddleDown || !_devices.KeyPressed(GlfwKey.S)
                || !Editor.BeginPartSizing())
                return false;
            _sizeVersion = Editor.Draft!.Version;
        }

        if (!allowed || !_devices.KeyDown(GlfwKey.S) || Editor.Draft!.Version != _sizeVersion)
        {
            // Let go: the size and the place stay for the next click, which takes a press of its own.
            Editor.EndPartSizing();
            ResetSizeDrag();
            return false;
        }

        UpdateOrbit(pointerOverUi, true);
        if (_editRightDown)
        {
            // The camera turns: a face drag holds still and starts again from wherever the pointer is let go.
            _sizeRebase = _sizeDragging;
            if (!_sizeDragging)
                _sizeFace = -1;
            return true;
        }

        if (_sizeDragging && !_devices.RawMouseDown(GlfwMouseButton.Left))
            _sizeDragging = false;
        if (_sizeDragging)
        {
            if (_sizeRebase)
                StartSizeDrag(_sizeSide);
            else
                DragSizeFace();
            return true;
        }

        PointerInGrid(out var origin, out var direction);
        _sizeFace = !pointerOverUi && GhostUnder(origin, direction, float.PositiveInfinity, out var side) ? side : -1;
        if (_sizeFace >= 0 && _devices.MousePressed(GlfwMouseButton.Left))
            StartSizeDrag(_sizeFace);
        return true;
    }

    /// <summary>
    /// Starts dragging a face of the ghost, or starts again from where the pointer is: the length it has now, and
    /// how a cell along the face's axis shows on screen from here.
    /// </summary>
    private void StartSizeDrag(int side)
    {
        _sizeDragging = true;
        _sizeRebase = false;
        _sizeSide = side;
        _sizeStart = _devices.MousePosition;
        var axis = side >> 1;
        _sizeBase = EditorGeometry.Of(Editor.PartSize, axis);
        var tuning = _editTuning!;
        var size = Editor.PartSize;
        var normal = EditorGeometry.Normal(side);
        var face = GhostMotion.Middle(Editor.GhostAt, size) + normal * (EditorGeometry.Of(size, axis) * 0.5f);
        PointerInGrid(out var origin, out _);
        var view = face - origin;
        var sine = view.LengthSquared() > 1e-8f ? Vector3.Cross(normal, Vector3.Normalize(view)).Length() : 0f;
        var a = default(float2);
        var b = default(float2);
        _sizeEdgeOn = sine < MathF.Sin(tuning.EdgeOnDegrees * (MathF.PI / 180f))
            || !Project(EditorPoint(face), out a) || !Project(EditorPoint(face + normal), out b);
        if (_sizeEdgeOn)
        {
            _sizeCell = new float2(0f, -tuning.SizePixelsPerCell);
            return;
        }

        // A face far away still takes a fair stretch of the pointer per cell, so the hand's tremor does not size it.
        _sizeCell = b - a;
        var length = MathF.Sqrt(_sizeCell.X * _sizeCell.X + _sizeCell.Y * _sizeCell.Y);
        var shortest = tuning.SizePixelsPerCell * 0.5f;
        if (length < shortest)
            _sizeCell *= shortest / Math.Max(length, 1e-3f);
    }

    /// <summary>Moves the dragged face to the whole cell nearest where the pointer has taken it.</summary>
    private void DragSizeFace()
    {
        var moved = _devices.MousePosition - _sizeStart;
        var cells = (moved.X * _sizeCell.X + moved.Y * _sizeCell.Y) / (_sizeCell.X * _sizeCell.X + _sizeCell.Y * _sizeCell.Y);
        var limit = Editor.Content!.Blocks.Limits.MaxBoxSide;
        var length = _sizeBase + cells;
        // Past a limit the movement is dropped, so turning back answers at once.
        if (length > limit)
        {
            _sizeBase -= length - limit;
            length = limit;
        }
        else if (length < 1f)
        {
            _sizeBase += 1f - length;
            length = 1f;
        }

        var whole = Math.Clamp((int)MathF.Floor(length + 0.5f), 1, limit);
        if (whole != EditorGeometry.Of(Editor.PartSize, _sizeSide >> 1))
            Editor.SizePartFace(_sizeSide, whole);
    }

    /// <summary>Forgets the face under the pointer and any face drag.</summary>
    private void ResetSizeDrag()
    {
        _sizeFace = -1;
        _sizeDragging = false;
        _sizeRebase = false;
        _sizeEdgeOn = false;
    }

    /// <summary>Segments of a rotation ring, as it is hit and drawn.</summary>
    internal const int RingSegments = 48;

    /// <summary>The rotation ring under the pointer or held by a drag (0 about x, 1 about y), or -1.</summary>
    internal int RotationRingHovered => Editor.RotatingPart ? _ringHovered : -1;

    /// <summary>True while a rotation ring is held by a left drag.</summary>
    internal bool RotationRingDragged => Editor.RotatingPart && _ringPress && _ringDragging;

    /// <summary>
    /// The middle of the rings of the part being turned and their radius, in the draft's grid; false with no part
    /// being turned. They stand where the ghost was when the turn started, clear of it at any of its turns.
    /// </summary>
    internal bool PartRings(out Vector3 middle, out float radius)
    {
        middle = _ringMiddle;
        radius = _ringRadius;
        return Editor.RotatingPart && Editor.HasGhost;
    }

    /// <summary>
    /// How the ghost of the part to place is drawn this frame while it glides into a turn: its outline turned about
    /// its middle, then that middle shifted, in the draft's grid. Identity when it is not turning.
    /// </summary>
    internal void GhostPose(out Quaternion turn, out Vector3 shift)
    {
        if (!Editor.HasGhost)
        {
            turn = Quaternion.Identity;
            shift = Vector3.Zero;
            return;
        }

        _ghostMotion.Pose(Editor.GhostAt, Editor.PartSize, Editor.PartShape, out turn, out shift);
    }

    // The middle of the ghost and a radius that clears it at any turn: the half diagonal does not change as it turns.
    private bool GhostRings(out Vector3 middle, out float radius)
    {
        middle = default;
        radius = 0f;
        if (!Editor.HasGhost)
            return false;
        var size = Editor.PartSize;
        var half = new Vector3(size.X, size.Y, size.Z) * 0.5f;
        var at = Editor.GhostAt;
        middle = new Vector3(at.X - 0.5f, at.Y - 0.5f, at.Z - 0.5f) + half;
        radius = half.Length() + 0.6f;
        return true;
    }

    /// <summary>Point <paramref name="segment"/> of the ring about grid axis <paramref name="axis"/> (0 x, 1 y), in the grid.</summary>
    internal static Vector3 RingPoint(Vector3 middle, float radius, int axis, int segment) =>
        RingPointAt(middle, radius, axis, segment * MathF.Tau / RingSegments);

    /// <summary>
    /// The point of the ring about grid axis <paramref name="axis"/> (0 x, 1 y) at <paramref name="angle"/> along it,
    /// in the grid. The angle runs y to z about x, and x to z about y: for the y ring that is a turn the other way.
    /// </summary>
    internal static Vector3 RingPointAt(Vector3 middle, float radius, int axis, float angle)
    {
        var a = MathF.Cos(angle) * radius;
        var b = MathF.Sin(angle) * radius;
        return middle + (axis == 0 ? new Vector3(0f, a, b) : new Vector3(a, 0f, b));
    }

    /// <summary>
    /// Turning the part to place, before it is placed. R over a face starts it: two rings stand around the ghost,
    /// one about the grid's x axis and one about its y; together their quarter turns reach every orientation a block
    /// may have. A left drag on a ring turns the ghost with the pointer, the part itself in whole quarter turns as
    /// the drag passes each half way; the wheel over a ring turns a quarter either way, and a click on it a quarter
    /// forward, with Shift back. The ghost glides into each turn. Every turn counts at once; nothing confirms it.
    /// A left press on the ghost ends the turn and becomes placing's press, so its click places the part as it is
    /// drawn, where it is drawn. R again, a left or middle press anywhere else, or a press on a panel ends the turn
    /// keeping it, and that press goes no further; Escape, or choosing another part, shape or the select tool, puts
    /// back the shape and size from before R. The right button orbits meanwhile and its click changes nothing. A
    /// ring's press and release never place. True while it holds the controls.
    /// </summary>
    private bool UpdatePartRotation(bool pointerOverUi, bool control, bool shift)
    {
        var r = !control && _devices.KeyPressed(GlfwKey.R);
        if (!Editor.RotatingPart)
        {
            _ringHovered = -1;
            ResetRingDrag();
            // A turn put back shows the ghost as it was at once, not gliding out of the turn it no longer has.
            if (_ringCancels != Editor.RotationCancels)
            {
                _ringCancels = Editor.RotationCancels;
                _ghostMotion.Reset();
            }

            if (!r || Editor.SizingPart || _editLeftDown || _editRightDown || _editMiddleDown || pointerOverUi || !GhostRings(out _ringMiddle, out _ringRadius)
                || !Editor.BeginPartRotation())
                return false;
            _ghostMotion.Reset();
            _ringCancels = Editor.RotationCancels;
            return true;
        }

        if (r)
        {
            // R again ends the turn and keeps it; the ghost stays where it was turned for the next click.
            if (_ringPress)
                _ghostMotion.Aim(_ringAxis, 0f, Editor.GhostAt, Editor.PartSize, Editor.PartShape, _editTuning?.TurnSeconds ?? 0f);
            Editor.EndPartRotation(true);
            _ringHovered = -1;
            ResetRingDrag();
            return true;
        }

        UpdateOrbit(pointerOverUi, true);
        if (_ringPress)
        {
            DragRing(shift);
            return true;
        }

        // While the camera turns, the rings are only looked at: no wheel turns and no press starts.
        if (_editRightDown)
        {
            _ringHovered = -1;
            return true;
        }

        _ringHovered = pointerOverUi ? -1 : RingUnderPointer();
        var left = _devices.MousePressed(GlfwMouseButton.Left);
        if (_ringHovered >= 0)
        {
            if (_devices.Scroll != 0)
                TurnGhost(_ringHovered, _devices.Scroll > 0 ? 1 : -1);
            if (left)
            {
                // A click or a drag: which, the pointer's way from here tells.
                _ringPress = true;
                _ringAxis = _ringHovered;
                _ringPressAt = _devices.MousePosition;
                _ringDragging = false;
                _ringAngle = 0f;
                _ringTurns = 0;
                _ringRebase = true;
                DragRing(shift);
            }
        }
        else if (!pointerOverUi && left && PointerOnGhost())
        {
            // A press on the ghost: the turn is kept and the press is placing's from here, held on the ghost, so its
            // click places exactly what is drawn. Moved off as a drag, it places nothing, as any slip of the hand.
            Editor.EndPartRotation(true);
            _ringHovered = -1;
            _editLeftDown = true;
            _editLeftMoved = false;
            _editLeftStart = _devices.MousePosition;
            _editGrab = -1;
        }
        else if (!pointerOverUi && (left || _devices.MousePressed(GlfwMouseButton.Middle)))
        {
            // A press anywhere else ends the turn, keeping it, and goes no further: the ghost was not drawn there,
            // so nothing is placed or selected. The ghost follows the pointer again from the next frame.
            Editor.EndPartRotation(false);
            _ringHovered = -1;
        }

        return true;
    }

    /// <summary>True when the pointer's ray meets the ghost of the part to place before any block of the draft.</summary>
    private bool PointerOnGhost()
    {
        if (!Editor.HasGhost || Editor.Draft is not { } draft || Editor.Source is null)
            return false;
        PointerInGrid(out var origin, out var direction);
        var found = EditorGeometry.Pick(draft.Boxes, origin, direction, out var hit);
        return GhostUnder(origin, direction, found ? hit.Distance : float.PositiveInfinity, out _);
    }

    /// <summary>
    /// A ring held by the left button. Let go without moving, it was a click: a quarter turn forward, or back with
    /// Shift. Moved, it is a drag: the angle the pointer has gone round the ring's middle, read where the pointer's ray
    /// meets the ring's plane and summed a small step at a time so it never wraps, turns the part a quarter at each
    /// half way past a quarter, and the ghost follows the rest. Let go after a drag, the ghost glides onto the part's
    /// quarter turn and nothing else happens. While the camera turns, or the ring is too near edge-on to read, the
    /// angle waits and is read afresh after, so neither turns the part.
    /// </summary>
    private void DragRing(bool shift)
    {
        _ringHovered = _ringAxis;
        var seconds = _editTuning?.TurnSeconds ?? 0f;
        // Orbit motion also disqualifies a pending click, including movement delivered with the release.
        var moved = _devices.MousePosition - _ringPressAt;
        _ringDragging |= MathF.Abs(moved.X) + MathF.Abs(moved.Y) > ClickSlop;
        if (!_devices.RawMouseDown(GlfwMouseButton.Left))
        {
            if (!_ringDragging)
                TurnGhost(_ringAxis, shift ? -1 : 1);
            else
                _ghostMotion.Aim(_ringAxis, 0f, Editor.GhostAt, Editor.PartSize, Editor.PartShape, seconds);
            ResetRingDrag();
            return;
        }

        if (_editRightDown)
        {
            _ringRebase = true;
            return;
        }

        if (!RingAngle(_ringAxis, out var angle))
        {
            _ringRebase = true;
            return;
        }

        if (_ringRebase)
        {
            _ringLast = angle;
            _ringRebase = false;
        }

        var step = angle - _ringLast;
        if (step > MathF.PI)
            step -= MathF.Tau;
        else if (step < -MathF.PI)
            step += MathF.Tau;
        _ringLast = angle;
        _ringAngle += step;
        if (!_ringDragging)
            return;
        const float quarter = MathF.PI * 0.5f;
        while (_ringAngle - _ringTurns * quarter > quarter * 0.5f)
        {
            TurnGhost(_ringAxis, 1);
            _ringTurns++;
        }

        while (_ringAngle - _ringTurns * quarter < -quarter * 0.5f)
        {
            TurnGhost(_ringAxis, -1);
            _ringTurns--;
        }

        _ghostMotion.Aim(_ringAxis, _ringAngle - _ringTurns * quarter, Editor.GhostAt, Editor.PartSize, Editor.PartShape, seconds);
    }

    /// <summary>
    /// The pointer's angle about the axis of ring <paramref name="axis"/>, right-handed, where its ray meets the
    /// ring's plane. False when the plane is too near edge-on, behind the camera, or the pointer is too near the
    /// middle for its angle to mean anything.
    /// </summary>
    private bool RingAngle(int axis, out float angle)
    {
        angle = 0f;
        PointerInGrid(out var origin, out var direction);
        var across = direction[axis];
        var edgeOn = MathF.Sin((_editTuning?.EdgeOnDegrees ?? 12f) * (MathF.PI / 180f));
        if (MathF.Abs(across) < edgeOn)
            return false;
        var t = (_ringMiddle[axis] - origin[axis]) / across;
        if (t <= 0f)
            return false;
        var p = origin + direction * t - _ringMiddle;
        if (p.LengthSquared() < _ringRadius * _ringRadius * 0.01f)
            return false;
        // About x the angle runs y to z; about y it runs z to x, as a right-handed turn does.
        angle = axis == 0 ? MathF.Atan2(p.Z, p.Y) : MathF.Atan2(p.X, p.Z);
        return true;
    }

    /// <summary>Turns the part being turned and glides its ghost from where it was drawn into the new turn.</summary>
    private void TurnGhost(int axis, int turns)
    {
        var (at, size, shape) = (Editor.GhostAt, Editor.PartSize, Editor.PartShape);
        Editor.TurnPart(axis, turns);
        if (Editor.HasGhost)
            _ghostMotion.Turned(axis, turns, at, size, shape, Editor.GhostAt, Editor.PartSize, Editor.PartShape, _editTuning?.TurnSeconds ?? 0f);
    }

    /// <summary>Lets go of a ring press.</summary>
    private void ResetRingDrag()
    {
        _ringPress = false;
        _ringDragging = false;
        _ringRebase = false;
    }

    /// <summary>
    /// A left or middle press on a panel ends a turn, keeping it, as a press elsewhere in the world does. Called by
    /// the editor screen after the world, once the router has given the interface the raw mouse again: the world
    /// never sees a press over a panel. A ring held by a drag keeps the turn going.
    /// </summary>
    internal void EndPartRotationOnPanelPress()
    {
        // ImGui can defer a click across frames; this reads the poll's own press and where the pointer is with it.
        // The right button only orbits while a part is turned, so its press on a panel leaves the turn alone.
        if (!Editor.RotatingPart || _ringPress || !PointerOverPanel
            || !(_devices.MousePressed(GlfwMouseButton.Left) || _devices.MousePressed(GlfwMouseButton.Middle)))
            return;
        Editor.EndPartRotation(true);
        _ringHovered = -1;
    }

    /// <summary>The ring nearest the pointer within the tuning's reach, or -1. Allocates nothing.</summary>
    private int RingUnderPointer()
    {
        if (_editTuning is null || !PartRings(out var middle, out var radius))
            return -1;
        var pointer = _devices.MousePosition;
        var best = -1;
        var nearest = _editTuning.RotateRingPixels;
        for (var axis = 0; axis < 2; axis++)
        {
            for (var i = 0; i < RingSegments; i++)
            {
                if (!ProjectSegment(EditorPoint(RingPoint(middle, radius, axis, i)), EditorPoint(RingPoint(middle, radius, axis, i + 1)), out var a, out var b))
                    continue;
                var ab = b - a;
                var length = ab.X * ab.X + ab.Y * ab.Y;
                var t = length > 0f ? Math.Clamp(((pointer.X - a.X) * ab.X + (pointer.Y - a.Y) * ab.Y) / length, 0f, 1f) : 0f;
                var dx = a.X + ab.X * t - pointer.X;
                var dy = a.Y + ab.Y * t - pointer.Y;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance < nearest)
                {
                    nearest = distance;
                    best = axis;
                }
            }
        }

        return best;
    }

    /// <summary>Lets go of every part gesture the view holds: a face drag, a ring press and the ghost's glide.</summary>
    private void ResetPartGestures()
    {
        ResetSizeDrag();
        ResetRingDrag();
        _ringHovered = -1;
        _editRightReserved = false;
        _ghostMotion.Reset();
    }

    /// <summary>Moves the editor's camera a step towards a focus, easing out.</summary>
    private void GlideEditorCamera(float dt)
    {
        if (!_editFocusing)
            return;
        var t = 1f - MathF.Exp(-dt * 9f);
        _editPan = Vector3.Lerp(_editPan, _editPanGoal, t);
        _editDistance += (_editDistanceGoal - _editDistance) * t;
        if (Vector3.DistanceSquared(_editPan, _editPanGoal) < 1e-4f && MathF.Abs(_editDistance - _editDistanceGoal) < 0.01f)
        {
            _editPan = _editPanGoal;
            _editDistance = _editDistanceGoal;
            _editFocusing = false;
        }
    }

    /// <summary>Adds the draft's blocks in their own shapes where the hull is, each tinted for its role.</summary>
    private void AddDraft(ref int count, BuildDraft draft, HullDesign source, Double3 camera)
    {
        var hull = EditedPosition.RelativeTo(camera);
        var orientation = EditedOrientation;
        var boxes = draft.Boxes;
        var material = Material(false, -1);
        for (var i = 0; i < boxes.Length && count < _candidates.Length; i++)
        {
            ref readonly var box = ref boxes[i];
            var middle = EditorGeometry.ToHull(EditorGeometry.Middle(box), source.Pivot, source.BlockSize);
            var size = new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * source.BlockSize;
            var color = EditorGeometry.BlockColor(source.Color, box.Type);
            // A block the ship has lost is drawn dark red; a selected one lighter and bluer. The outlines say which
            // is which as well, so the colours never have to carry it alone.
            if (Editor.IsMissing(i))
                color = Vector3.Lerp(color, new Vector3(0.45f, 0.08f, 0.06f), 0.7f);
            if (Editor.IsSelected(i))
                color = Vector3.Lerp(color, new Vector3(0.6f, 0.8f, 1f), 0.4f);
            Add(ref count, _cubeMesh + (int)box.Shape, hull + Vector3.Transform(middle, orientation), orientation, size, color, material);
        }
    }

    /// <summary>A point of the draft's grid in the world, where the edited hull is drawn this frame.</summary>
    public Double3 EditorPoint(Vector3 grid)
    {
        var source = Editor.Source;
        return source is null ? EditedPosition : EditedPosition + Vector3.Transform(EditorGeometry.ToHull(grid, source.Pivot, source.BlockSize), EditedOrientation);
    }

    /// <summary>The direction of the view ray under a window position.</summary>
    public Vector3 PointerRay(float2 pointer)
    {
        var aspect = _screen.X / _screen.Y;
        var nx = pointer.X / _screen.X * 2f - 1f;
        var ny = pointer.Y / _screen.Y * 2f - 1f;
        return Vector3.Normalize(_forward + _right * (nx * _tanHalfFov * aspect) - _up * (ny * _tanHalfFov));
    }

    /// <summary>
    /// Projects a line between two world points to window coordinates, cut at the near plane. False when all of it
    /// is behind the camera.
    /// </summary>
    public bool ProjectSegment(Double3 from, Double3 to, out float2 a, out float2 b)
    {
        var va = ToView(from);
        var vb = ToView(to);
        a = b = default;
        if (va.Z < NearPlane && vb.Z < NearPlane)
            return false;
        if (va.Z < NearPlane)
            va = Vector3.Lerp(va, vb, (NearPlane - va.Z) / (vb.Z - va.Z));
        else if (vb.Z < NearPlane)
            vb = Vector3.Lerp(vb, va, (NearPlane - vb.Z) / (va.Z - vb.Z));
        a = ViewToScreen(va);
        b = ViewToScreen(vb);
        return true;
    }

    private float2 ViewToScreen(Vector3 view)
    {
        var aspect = _screen.X / _screen.Y;
        var x = view.X / (view.Z * _tanHalfFov * aspect);
        var y = -view.Y / (view.Z * _tanHalfFov);
        return new float2((x * 0.5f + 0.5f) * _screen.X, (y * 0.5f + 0.5f) * _screen.Y);
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

        // Preserve the existing jump-triggered refresh; destination prefetch is not enumerated here.
        var ahead = Own.Order.Kind == OrderKind.Jump ? galaxy.SectorOf(Own.Order.Target) : center;
        if (ahead != _streamAhead)
        {
            _streamAhead = ahead;
            _streamDirty = true;
        }

        _gasField.Update(galaxy, center);
        var focus = Mode == ViewMode.Command || _commandBlend > 0f ? galaxy.SectorOf(_commandFocus) : (SectorCoord?)null;
        if (_sectorRequests.Update(center, focus)) _streamDirty = true;
        if (!_streamDirty) return;
        _streamDirty = false;
        foreach (var coord in _sectorRequests.Coordinates)
        {
            var free = FindStreamSlot(_slots, galaxy, _sectorRequests, coord, out var present);
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

            Array.Clear(free.Drawn);
            free.Galaxy = galaxy;
            free.Coord = coord;
            free.State = SectorSlot.Generating;
            _jobs.Schedule(free, 2);
        }
    }

    /// <summary>Finds reusable storage without borrowing any worker-owned or desired sector.</summary>
    internal static SectorSlot? FindStreamSlot(SectorSlot[] slots, Galaxy galaxy, SectorRequests requests, SectorCoord coord, out bool present)
    {
        SectorSlot? free = null;
        present = false;
        foreach (var slot in slots)
        {
            var current = ReferenceEquals(slot.Galaxy, galaxy) && slot.State != SectorSlot.Empty;
            if (current && slot.Coord == coord)
            {
                present = true;
                return null;
            }
            if (free is null && slot.State != SectorSlot.Generating && !(current && requests.Contains(slot.Coord)))
                free = slot;
        }
        return free;
    }

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

    /// <summary>Adds one mesh instance; <paramref name="stretch"/> draws it out along its local z, the hull's nose.</summary>
    private void Add(ref int count, int mesh, Vector3 position, Quaternion rotation, float scale, Vector3 color, float material, float stretch = 1f)
        => Add(ref count, mesh, position, rotation, new Vector3(scale), color, material, stretch);

    private void Add(ref int count, int mesh, Vector3 position, Quaternion rotation, Vector3 scale, Vector3 color, float material, float stretch = 1f)
    {
        if (count == _candidates.Length) return;
        _candidates[count++] = new Candidate
        {
            Mesh = mesh,
            Instance = MeshTransform(position, rotation, scale, color, material, stretch),
        };
        _meshCounts[mesh]++;
    }

    /// <summary>The actual instance transform, shared by intact meshes and individual damaged boxes.</summary>
    internal static MeshInstance MeshTransform(Vector3 position, Quaternion rotation, Vector3 scale, Vector3 color, float material, float stretch)
    {
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        var along = scale.Z * stretch;
        return new MeshInstance
        {
            // System.Numerics matrices transform row vectors, so the rotation's columns are its rows here.
            Row0 = new float4(m.M11 * scale.X, m.M21 * scale.Y, m.M31 * along, position.X),
            Row1 = new float4(m.M12 * scale.X, m.M22 * scale.Y, m.M32 * along, position.Y),
            Row2 = new float4(m.M13 * scale.X, m.M23 * scale.Y, m.M33 * along, position.Z),
            Tint = new float4(color.X, color.Y, color.Z, material),
        };
    }

    private static float Wrap(double value, double period) => (float)(value - Math.Floor(value / period) * period);
}
