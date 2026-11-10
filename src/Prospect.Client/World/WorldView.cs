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
    private const float ClickRadiusPixels = 22f;

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
    private const float LookRadiansPerSecond = 1.9f;
    private const float DustCell = 360f;
    private const float Exposure = 1.0f;

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
    private readonly HullDesign[] _designs;
    private readonly int _asteroidMeshBase;
    private readonly int _asteroidLodMeshBase;
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
    private bool _jumpOrders;
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
    private float2 _viewport = new(1, 1);

    public WorldView(InputDevices devices, ActionMap actions, ClientSettings settings, JobSystem jobs, SceneRenderer scene, HullDesign[] designs)
    {
        _devices = devices;
        _actions = actions;
        _settings = settings;
        _jobs = jobs;
        _scene = scene;
        _designs = designs;
        _asteroidMeshBase = AsteroidMesh(designs.Length, 0, false);
        _asteroidLodMeshBase = AsteroidMesh(designs.Length, 0, true);
        _meshCount = MeshCount(designs.Length);
        _cubeMesh = CubeMesh(designs.Length);
        _meshCounts = new int[_meshCount];
        _meshOffsets = new int[_meshCount];
        _batches = new DrawBatch[_meshCount];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new SectorSlot();
    }

    /// <summary>Meshes are numbered: one per hull design, then the asteroid shapes, then their coarse versions.</summary>
    public static int MeshCount(int designs) => designs + Galaxy.AsteroidShapes * 2 + 1;

    /// <summary>The single-block mesh, used to draw hulls that have lost blocks.</summary>
    public static int CubeMesh(int designs) => designs + Galaxy.AsteroidShapes * 2;

    public static int AsteroidMesh(int designs, int shape, bool coarse) => designs + (coarse ? Galaxy.AsteroidShapes : 0) + shape;

    public ViewMode Mode { get; private set; }

    /// <summary>The hulls selected in the command view, by id.</summary>
    public ReadOnlySpan<uint> Selection => _selected.AsSpan(0, _selectedCount);

    /// <summary>
    /// When set, the pilot's own jump is drawn with this drive's effect whatever drive the hull has. For looking
    /// at each effect from the command line.
    /// </summary>
    public FtlKind FtlPreview { get; set; }

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
        if (mode == Mode)
            return;
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
    public void Update(SessionSnapshot snapshot, PlayerInputBridge bridge, float dt, bool inputEnabled, float2 viewport, bool pointerOverUi)
    {
        _viewport = viewport;
        _snapshot = snapshot;
        var galaxy = snapshot.Galaxy!;
        if (!ReferenceEquals(galaxy, _galaxy))
        {
            _galaxy = galaxy;
            _streamDirty = true;
            _chaseInitialised = false;
            _currentCoord = new SectorCoord(int.MinValue, 0, 0);
        }

        ApplyRockDeltas(snapshot);
        _time += dt;

        // Where the hull is right now, between the last two simulation ticks.
        ref var own = ref snapshot.Hulls[snapshot.OwnHull];
        var alpha = TickAlpha(snapshot);
        Own = own;

        // A jump moves the hull further in one tick than it could fly; there is nothing in between to show.
        var jumped = own.Position.RelativeTo(own.PreviousPosition).LengthSquared() > JumpGap * JumpGap;
        OwnPosition = jumped ? own.Position : Double3.Lerp(own.PreviousPosition, own.Position, alpha);
        OwnOrientation = Quaternion.Slerp(own.PreviousOrientation, own.Orientation, alpha);

        if (own.Id != _ownId)
        {
            // The player is in a different hull: put the camera behind it and forget the old target.
            _ownId = own.Id;
            _chaseInitialised = false;
            TargetHull = 0;
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
                    OrderSelection(bridge, default, false);
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
            var pointer = _devices.MouseDelta * (LookRadiansPerPixel * _settings.MouseSensitivity);
            var yaw = pointer.X + _actions.Axis(_devices, InputAxis.Yaw, keyboard) * LookRadiansPerSecond * dt;
            var pitch = (-pointer.Y + _actions.Axis(_devices, InputAxis.Pitch, keyboard) * LookRadiansPerSecond * dt) * invert;
            _lookDirection = Vector3.Transform(_lookDirection, Quaternion.CreateFromAxisAngle(_up, -yaw));
            _lookDirection = Vector3.Normalize(Vector3.Transform(_lookDirection, Quaternion.CreateFromAxisAngle(_right, pitch)));

            // Holding the free-look button looks around without committing the hull to anything; letting go
            // returns the view to the nose. Otherwise the hull swings onto the view whenever it is under thrust.
            var freeLook = _devices.MouseDown(GlfwMouseButton.Right);
            if (freeLook && _devices.Scroll != 0)
                _chaseZoom = Math.Clamp(_chaseZoom * MathF.Pow(0.88f, _devices.Scroll), 0.5f, 8f);
            if (_freeLook && !freeLook)
                _lookDirection = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
            _freeLook = freeLook;

            UpdateTargeting(snapshot, bridge);

            var thrusting = controls.Strafe != 0 || controls.Vertical != 0 || controls.Forward != 0;
            controls.Aim = _lookDirection;
            controls.Align = thrusting && !freeLook;
        }
        else if (inputEnabled)
        {
            UpdateCommandControls(snapshot, bridge, dt, pointerOverUi);
        }

        bridge.Submit(controls);
        UpdateCamera(dt);
        UpdateStream();
    }

    /// <summary>Builds this frame's instances and constants and records the scene.</summary>
    public void Draw(CommandBuffer commands, int frameIndex, SessionSnapshot snapshot, float time)
    {
        var galaxy = _galaxy!;
        var alpha = TickAlpha(snapshot);
        var camera = CameraPosition;
        var aspect = _viewport.X / _viewport.Y;
        var candidateCount = 0;
        Array.Clear(_meshCounts);
        var marks = _scene.FtlMarks(frameIndex);
        var markCount = 0;

        // Hulls.
        for (var i = 0; i < snapshot.HullCount && candidateCount < _candidates.Length; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var design = _designs[Math.Min(hull.Design, _designs.Length - 1)];
            var leap = hull.Position.RelativeTo(hull.PreviousPosition).LengthSquared() > JumpGap * JumpGap;
            var position = (leap ? hull.Position : Double3.Lerp(hull.PreviousPosition, hull.Position, alpha)).RelativeTo(camera);

            // A jumping hull shows its drive's mark: ahead of it while the drive spools, on it as it arrives.
            if (hull.Jump != JumpPhase.None && markCount < marks.Length)
            {
                var flight = design.Flight;
                var duration = hull.Jump switch
                {
                    JumpPhase.Spool => flight.FtlSpoolSeconds,
                    JumpPhase.Tunnel => flight.FtlTunnelSeconds,
                    _ => flight.FtlArriveSeconds,
                };
                var ahead = hull.Jump == JumpPhase.Spool ? Vector3.Transform(-Vector3.UnitZ, hull.Orientation) * (flight.Radius * 2.4f) : Vector3.Zero;
                var at = position + ahead;
                marks[markCount++] = new FtlMark
                {
                    PositionSize = new float4(at.X, at.Y, at.Z, flight.Radius * (flight.Ftl == FtlKind.Hyperspace ? 7f : 4.5f)),
                    Params = new float4((float)flight.Ftl, (float)hull.Jump, Math.Clamp(hull.JumpTimer / duration, 0f, 1f), (hull.Id & 0xFF) / 255f),
                };
            }

            // In the tunnel the hull is nowhere.
            if (hull.Jump == JumpPhase.Tunnel)
                continue;
            if (!Visible(position, design.Flight.Radius, aspect, out _))
                continue;
            var orientation = Quaternion.Slerp(hull.PreviousOrientation, hull.Orientation, alpha);
            if (hull.Blocks == design.FullMask)
            {
                Add(ref candidateCount, Math.Min(hull.Design, _designs.Length - 1), position, orientation, 1f, design.Color, 1f);
                continue;
            }

            // A hull that has lost blocks is drawn from the ones it still has, each as its own cube.
            for (var b = 0; b < design.BlockOffsets.Length && candidateCount < _candidates.Length; b++)
            {
                if ((hull.Blocks & 1UL << b) != 0)
                    Add(ref candidateCount, _cubeMesh, position + Vector3.Transform(design.BlockOffsets[b], orientation), orientation, design.BlockSize, design.Color, 1f);
            }
        }

        // Rocks in the sectors around the camera.
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, galaxy))
                continue;

            var center = galaxy.CenterOf(slot.Coord).RelativeTo(camera);
            var content = slot.Content;
            for (var i = 0; i < content.AsteroidCount && candidateCount < _candidates.Length; i++)
            {
                ref var rock = ref content.Asteroids[i];
                var position = center + rock.LocalPosition + (slot.Displaced ? slot.Offsets[i] : Vector3.Zero);
                if (!Visible(position, rock.Radius, aspect, out var pixels) || pixels < 0.6f)
                    continue;

                // Full detail only where it can be seen; distant rocks use the coarse mesh.
                var mesh = (pixels > 90f ? _asteroidMeshBase : _asteroidLodMeshBase) + rock.Shape;
                // Bare rock is grey; ore shows through in its own colour, more strongly the richer the rock.
                var shade = 0.2f + 0.05f * (rock.Shape % 4);
                var tint = new Vector3(shade, shade * 0.95f, shade * 0.88f);
                if (rock.Resource >= 0)
                    tint = Vector3.Lerp(tint, galaxy.Resources[rock.Resource].Color * 0.55f, 0.25f + 0.5f * rock.Grade);
                Add(ref candidateCount, mesh, position, rock.Rotation, rock.Radius, tint, 0f);
            }
        }

        // Group instances by mesh so each mesh is one draw call.
        var batchCount = 0;
        var offset = 0;
        for (var mesh = 0; mesh < _meshCount; mesh++)
        {
            _meshOffsets[mesh] = offset;
            if (_meshCounts[mesh] > 0)
                _batches[batchCount++] = new DrawBatch { Mesh = mesh, FirstInstance = offset, InstanceCount = _meshCounts[mesh] };
            offset += _meshCounts[mesh];
        }

        var instances = _scene.Instances(frameIndex);
        for (var i = 0; i < candidateCount; i++)
            instances[_meshOffsets[_candidates[i].Mesh]++] = _candidates[i].Instance;

        var f = 1f / _tanHalfFov;
        var sunColor = galaxy.StarClasses[Sector.StarClass].Color * 1.7f;
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
            SunDirection = new float4(Sector.SunDirection.X, Sector.SunDirection.Y, Sector.SunDirection.Z, 0),
            SunColor = new float4(sunColor.X, sunColor.Y, sunColor.Z, 0),
            Dust = new float4(Wrap(camera.X, DustCell), Wrap(camera.Y, DustCell), Wrap(camera.Z, DustCell), time),
            Drift = new float4(velocity.X, velocity.Y, velocity.Z, velocity.Length()),
            Grid = new float4(Wrap(camera.X, 1000), Wrap(camera.Z, 1000), Wrap(camera.X, sectorSize), Wrap(camera.Z, sectorSize)),
            GridPlane = new float4((float)(_commandFocus.Y - camera.Y), (float)sectorSize, _commandBlend, Exposure),
        };

        // The player's own jump is seen from the inside, in the pilot view.
        if (Own.Jump != JumpPhase.None && _commandBlend < 0.5f)
        {
            var flight = _designs[Math.Min(Own.Design, _designs.Length - 1)].Flight;
            var duration = Own.Jump switch
            {
                JumpPhase.Spool => flight.FtlSpoolSeconds,
                JumpPhase.Tunnel => flight.FtlTunnelSeconds,
                _ => flight.FtlArriveSeconds,
            };
            var kind = FtlPreview != FtlKind.None ? FtlPreview : flight.Ftl;
            constants.Ftl = new float4((float)kind, (float)Own.Jump, Math.Clamp(Own.JumpTimer / duration, 0f, 1f), 0);
        }

        _scene.Render(commands, frameIndex, constants, _batches.AsSpan(0, batchCount), _commandBlend > 0.01f, markCount);
    }

    public float ExposureValue => Exposure;

    /// <summary>Projects a world position to window pixels. Returns false when it is behind the camera.</summary>
    public bool Project(Double3 world, out float2 screen)
    {
        var relative = world.RelativeTo(CameraPosition);
        var depth = Vector3.Dot(relative, _forward);
        if (depth < NearPlane)
        {
            screen = default;
            return false;
        }

        var aspect = _viewport.X / _viewport.Y;
        var x = Vector3.Dot(relative, _right) / (depth * _tanHalfFov * aspect);
        var y = -Vector3.Dot(relative, _up) / (depth * _tanHalfFov);
        screen = new float2((x * 0.5f + 0.5f) * _viewport.X, (y * 0.5f + 0.5f) * _viewport.Y);
        return true;
    }

    /// <summary>The point on the command plane under a window position, if the view ray meets the plane.</summary>
    public bool GroundPoint(float2 pointer, out Double3 point)
    {
        var aspect = _viewport.X / _viewport.Y;
        var nx = pointer.X / _viewport.X * 2f - 1f;
        var ny = pointer.Y / _viewport.Y * 2f - 1f;
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
        var center = _viewport * 0.5f;
        var best = 0u;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.IsOwn)
                continue;
            var radius = _designs[Math.Min(hull.Design, _designs.Length - 1)].Flight.Radius;
            if (!ProjectDisc(hull.Position, radius, out var screen, out var pixels, out _))
                continue;
            var offset = screen - center;
            var distance = MathF.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
            if (distance > Math.Max(pixels * 1.5f, 46f) || distance >= bestDistance)
                continue;
            best = hull.Id;
            bestDistance = distance;
        }

        HoveredHull = best;
        if (TargetHull != 0 && !TryGetHull(TargetHull, out _))
            TargetHull = 0;

        // With no ship under the reticle, the nearest asteroid on the view line can be targeted instead.
        HoveredRock = null;
        if (best == 0)
        {
            var nearest = float.MaxValue;
            foreach (var slot in _slots)
            {
                if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy))
                    continue;
                for (var i = 0; i < slot.Content.AsteroidCount; i++)
                {
                    var rock = new RockRef(slot.Coord, i);
                    if (!TryGetRock(rock, out var position, out var rockRadius) || !ProjectDisc(position, rockRadius, out var screen, out var pixels, out var depth))
                        continue;
                    var offset = screen - center;
                    var reach = Math.Max(pixels, 12f);
                    if (offset.X * offset.X + offset.Y * offset.Y > reach * reach || depth >= nearest)
                        continue;
                    HoveredRock = rock;
                    nearest = depth;
                }
            }
        }

        if (TargetRock is { } held && !TryGetRock(held, out _, out _))
            TargetRock = null;
        if (_devices.MousePressed(GlfwMouseButton.Middle))
        {
            TargetHull = best;
            TargetRock = best == 0 ? HoveredRock : null;
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

    /// <summary>Where a hull is drawn this frame.</summary>
    public Double3 HullPosition(in HullView hull) => hull.IsOwn ? OwnPosition : hull.Position;

    public bool IsSelected(uint id) => _selected.AsSpan(0, _selectedCount).Contains(id);

    /// <summary>Selects a hull, replacing the selection or adding to it. Adding a selected hull removes it.</summary>
    public void Select(uint id, bool additive)
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
        _selectedCount = 0;
        _selectedRockCount = 0;
    }

    /// <summary>Adds a hull to the selection if it is not in it already.</summary>
    public void AddToSelection(uint id)
    {
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
            OrderSelection(bridge, target, true);
    }

    /// <summary>
    /// Sends the selected hulls (or the hull being flown) to a point in another sector by jump drive. A hull with
    /// no drive, or one already in that sector, flies there instead.
    /// </summary>
    public void OrderJump(PlayerInputBridge bridge, Double3 target)
    {
        if (_selectedCount == 0)
        {
            bridge.IssueOrder(new HullOrder { Kind = JumpOrFly(Own, target), Target = target });
            return;
        }

        _jumpOrders = true;
        OrderSelection(bridge, target, true);
        _jumpOrders = false;
    }

    private OrderKind JumpOrFly(in HullView hull, Double3 target)
    {
        var design = _designs[Math.Min(hull.Design, _designs.Length - 1)];
        design.Evaluate(hull.Blocks, out var flight, out _);
        var sameSector = _galaxy is not null && _galaxy.SectorOf(HullPosition(hull)) == _galaxy.SectorOf(target);
        return flight.Ftl != FtlKind.None && !sameSector ? OrderKind.Jump : OrderKind.MoveTo;
    }

    /// <summary>Selects every hull the player owns.</summary>
    public void SelectAllOwned()
    {
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
    /// several hulls sent to one place do not try to occupy the same spot.
    /// </summary>
    private void OrderSelection(PlayerInputBridge bridge, Double3 target, bool move)
    {
        var columns = (int)MathF.Ceiling(MathF.Sqrt(_selectedCount));
        var rows = (_selectedCount + columns - 1) / Math.Max(columns, 1);
        for (var i = 0; i < _selectedCount; i++)
        {
            var order = default(HullOrder);
            if (move)
            {
                var offset = new Vector3((i % columns - (columns - 1) * 0.5f) * FormationSpacing, 0, (i / columns - (rows - 1) * 0.5f) * FormationSpacing);
                var kind = _jumpOrders && TryGetHull(_selected[i], out var hull) ? JumpOrFly(hull, target) : OrderKind.MoveTo;
                order = new HullOrder { Kind = kind, Target = target + offset };
            }

            // The hull being flown takes its orders through the input stream; the rest go to the server directly.
            if (_selected[i] == Own.Id)
                bridge.IssueOrder(order);
            else
                bridge.IssueFleetOrder(_selected[i], order);
        }
    }

    private void SelectAt(SessionSnapshot snapshot, float2 pointer, bool additive)
    {
        var best = 0u;
        var bestDistance = ClickRadiusPixels * ClickRadiusPixels;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Owner != snapshot.PlayerId || !Project(HullPosition(hull), out var screen))
                continue;
            var offset = screen - pointer;
            var distance = offset.X * offset.X + offset.Y * offset.Y;
            if (distance >= bestDistance)
                continue;
            best = hull.Id;
            bestDistance = distance;
        }

        if (best != 0)
        {
            Select(best, additive);
            return;
        }

        // No hull under the pointer: pick the nearest asteroid whose disc the pointer is on.
        var found = false;
        var picked = default(RockRef);
        var nearest = float.MaxValue;
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy))
                continue;
            for (var i = 0; i < slot.Content.AsteroidCount; i++)
            {
                var rock = new RockRef(slot.Coord, i);
                if (!TryGetRock(rock, out var position, out var radius) || !ProjectDisc(position, radius, out var screen, out var pixels, out var depth))
                    continue;
                var offset = screen - pointer;
                var reach = Math.Max(pixels, 9f);
                if (offset.X * offset.X + offset.Y * offset.Y > reach * reach || depth >= nearest)
                    continue;
                found = true;
                picked = rock;
                nearest = depth;
            }
        }

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

        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy))
                continue;
            for (var i = 0; i < slot.Content.AsteroidCount && _selectedRockCount < MaxSelection; i++)
            {
                var rock = new RockRef(slot.Coord, i);
                if (_selectedRocks.AsSpan(0, _selectedRockCount).Contains(rock))
                    continue;
                if (!TryGetRock(rock, out var position, out var radius) || !ProjectDisc(position, radius, out var screen, out _, out _))
                    continue;
                if (screen.X >= min.X && screen.X <= max.X && screen.Y >= min.Y && screen.Y <= max.Y)
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

    /// <summary>Projects a sphere: its centre in window pixels, its radius in pixels and its distance along the view.</summary>
    public bool ProjectDisc(Double3 world, float radius, out float2 screen, out float pixels, out float depth)
    {
        depth = Vector3.Dot(world.RelativeTo(CameraPosition), _forward);
        pixels = 0;
        if (!Project(world, out screen))
            return false;
        pixels = radius / (depth * _tanHalfFov) * _viewport.Y * 0.5f;
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
                    OrderSelection(bridge, target, true);
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
        var chasePosition = OwnPosition + chaseUp * (chaseDistance * 0.24f) - _cameraDirection * (chaseDistance * pullBack);

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
