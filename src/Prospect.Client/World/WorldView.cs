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
    public const int HullMesh = 0;
    public const int AsteroidMeshBase = 1;
    public const int AsteroidLodMeshBase = AsteroidMeshBase + Galaxy.AsteroidShapes;
    public const int MeshCount = AsteroidLodMeshBase + Galaxy.AsteroidShapes;

    private const int RingXZ = 2;
    private const int RingY = 1;
    private const int SlotCount = (RingXZ * 2 + 1) * (RingXZ * 2 + 1) * (RingY * 2 + 1);
    private const float NearPlane = 0.25f;
    private const float PilotFov = 62f * MathF.PI / 180f;
    private const float CommandFov = 48f * MathF.PI / 180f;
    private const float MinCommandDistance = 25f;
    private const float MaxCommandDistance = 90000f;
    private const float LookRadiansPerPixel = 0.0021f;
    private const float DustCell = 360f;
    private const float Exposure = 1.0f;

    /// <summary>A sector's generated contents and the job that fills them.</summary>
    private sealed class SectorSlot : IJob
    {
        public const int Empty = 0;
        public const int Generating = 1;
        public const int Ready = 2;

        public readonly SectorContent Content = new();
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
    private readonly Vector3 _hullColor;
    private readonly float _hullRadius;
    private readonly SectorSlot[] _slots = new SectorSlot[SlotCount];
    private readonly Candidate[] _candidates = new Candidate[SceneRenderer.MaxInstances];
    private readonly int[] _meshCounts = new int[MeshCount];
    private readonly int[] _meshOffsets = new int[MeshCount];
    private readonly DrawBatch[] _batches = new DrawBatch[MeshCount];

    private Galaxy? _galaxy;
    private SectorCoord _streamCenter;
    private bool _streamDirty = true;
    private SectorCoord _currentCoord = new(int.MinValue, 0, 0);

    // Camera.
    private Quaternion _chaseOrientation = Quaternion.Identity;
    private bool _chaseInitialised;
    private float _commandBlend;
    private float _commandYaw;
    private float _commandPitch = -0.9f;
    private float _commandDistance = 240f;
    private Double3 _commandFocus;
    private bool _commandFollow = true;
    private float2 _rightDragStart;
    private bool _rightDragging;
    private bool _rightMoved;
    private Vector3 _right = Vector3.UnitX;
    private Vector3 _up = Vector3.UnitY;
    private Vector3 _forward = -Vector3.UnitZ;
    private float _tanHalfFov = MathF.Tan(PilotFov / 2);
    private float2 _viewport = new(1, 1);

    public WorldView(InputDevices devices, ActionMap actions, ClientSettings settings, JobSystem jobs, SceneRenderer scene, HullDesign pod, float hullRadius)
    {
        _devices = devices;
        _actions = actions;
        _settings = settings;
        _jobs = jobs;
        _scene = scene;
        _hullColor = pod.Color;
        _hullRadius = hullRadius;
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new SectorSlot();
    }

    public ViewMode Mode { get; private set; }

    /// <summary>How far the camera has moved from the pilot view (0) to the command view (1).</summary>
    public float CommandBlend => _commandBlend;

    public Double3 CameraPosition { get; private set; }

    /// <summary>The player's hull as drawn this frame, interpolated between simulation ticks.</summary>
    public HullView Own { get; private set; }
    public Double3 OwnPosition { get; private set; }
    public Quaternion OwnOrientation { get; private set; } = Quaternion.Identity;

    /// <summary>The sector the player's hull is in.</summary>
    public SectorSummary Sector { get; private set; }
    public string SectorName { get; private set; } = "";
    public Galaxy? Galaxy => _galaxy;
    public Double3 CommandFocus => _commandFocus;
    public float CommandDistance => _commandDistance;
    public bool CommandFollowing => _commandFollow;

    public void SetMode(ViewMode mode)
    {
        if (mode == Mode)
            return;
        Mode = mode;
        if (mode == ViewMode.Command)
        {
            // Start above and behind the hull, looking the way it faces, so the view pulls back rather than cuts.
            var forward = Vector3.Transform(-Vector3.UnitZ, OwnOrientation);
            _commandYaw = MathF.Atan2(-forward.X, -forward.Z);
            _commandPitch = -0.85f;
            _commandDistance = 150f;
            _commandFocus = OwnPosition;
            _commandFollow = true;
        }
    }

    /// <summary>
    /// Advances the camera, reads the player's controls and keeps the surrounding sectors generated. Call once per
    /// frame while a session is active. <paramref name="inputEnabled"/> is false while a menu or the map has focus.
    /// </summary>
    public void Update(SessionSnapshot snapshot, PlayerInputBridge bridge, float dt, bool inputEnabled, float2 viewport, bool pointerOverUi)
    {
        _viewport = viewport;
        var galaxy = snapshot.Galaxy!;
        if (!ReferenceEquals(galaxy, _galaxy))
        {
            _galaxy = galaxy;
            _streamDirty = true;
            _chaseInitialised = false;
            _currentCoord = new SectorCoord(int.MinValue, 0, 0);
        }

        // Where the hull is right now, between the last two simulation ticks.
        ref var own = ref snapshot.Hulls[snapshot.OwnHull];
        var alpha = TickAlpha(snapshot);
        Own = own;
        OwnPosition = Double3.Lerp(own.PreviousPosition, own.Position, alpha);
        OwnOrientation = Quaternion.Slerp(own.PreviousOrientation, own.Orientation, alpha);

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
                bridge.IssueOrder(default);
        }

        if (inputEnabled && Mode == ViewMode.Pilot)
        {
            controls.Strafe = _actions.Axis(_devices, InputAxis.Strafe, keyboard);
            controls.Vertical = _actions.Axis(_devices, InputAxis.Vertical, keyboard);
            controls.Forward = _actions.Axis(_devices, InputAxis.Forward, keyboard);
            controls.Pitch = _actions.Axis(_devices, InputAxis.Pitch, keyboard) * (_settings.InvertPitch ? -1f : 1f);
            controls.Yaw = _actions.Axis(_devices, InputAxis.Yaw, keyboard);
            controls.Roll = _actions.Axis(_devices, InputAxis.Roll, keyboard);
            controls.Boost = _actions.Held(_devices, InputAction.Boost, keyboard);

            var look = _devices.MouseDelta * (LookRadiansPerPixel * _settings.MouseSensitivity);
            controls.LookYaw = look.X;
            controls.LookPitch = -look.Y * (_settings.InvertPitch ? -1f : 1f);
        }
        else if (inputEnabled)
        {
            UpdateCommandControls(bridge, dt, pointerOverUi);
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

        // Hulls.
        for (var i = 0; i < snapshot.HullCount && candidateCount < _candidates.Length; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            var position = Double3.Lerp(hull.PreviousPosition, hull.Position, alpha).RelativeTo(camera);
            if (!Visible(position, _hullRadius, aspect, out _))
                continue;
            var orientation = Quaternion.Slerp(hull.PreviousOrientation, hull.Orientation, alpha);
            Add(ref candidateCount, HullMesh, position, orientation, 1f, _hullColor, 1f);
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
                var position = center + rock.LocalPosition;
                if (!Visible(position, rock.Radius, aspect, out var pixels) || pixels < 0.6f)
                    continue;

                // Full detail only where it can be seen; distant rocks use the coarse mesh.
                var mesh = (pixels > 90f ? AsteroidMeshBase : AsteroidLodMeshBase) + rock.Shape;
                var shade = 0.2f + 0.05f * (rock.Shape % 4);
                Add(ref candidateCount, mesh, position, rock.Rotation, rock.Radius, new Vector3(shade, shade * 0.95f, shade * 0.88f), 0f);
            }
        }

        // Group instances by mesh so each mesh is one draw call.
        var batchCount = 0;
        var offset = 0;
        for (var mesh = 0; mesh < MeshCount; mesh++)
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

        _scene.Render(commands, frameIndex, constants, _batches.AsSpan(0, batchCount), _commandBlend > 0.01f);
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

    private void UpdateCommandControls(PlayerInputBridge bridge, float dt, bool pointerOverUi)
    {
        if (_actions.Pressed(_devices, InputAction.Follow, true))
            _commandFollow = true;

        if (!pointerOverUi && _devices.Scroll != 0)
            _commandDistance = Math.Clamp(_commandDistance * MathF.Pow(0.86f, _devices.Scroll), MinCommandDistance, MaxCommandDistance);

        // Right button: drag to orbit, click to order a move to the point under the pointer.
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
                    bridge.IssueOrder(new HullOrder { Kind = OrderKind.MoveTo, Target = target });
            }
        }

        // Keys and sticks pan the focus across the plane, turn the view and raise or lower the plane.
        var strafe = _actions.Axis(_devices, InputAxis.Strafe, true);
        var forward = _actions.Axis(_devices, InputAxis.Forward, true);
        var vertical = _actions.Axis(_devices, InputAxis.Vertical, true);
        _commandYaw -= _actions.Axis(_devices, InputAxis.Roll, true) * 1.7f * dt;

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
            _commandFollow = false;
        }
    }

    private void UpdateCamera(float dt)
    {
        var target = Mode == ViewMode.Command ? 1f : 0f;
        _commandBlend += Math.Clamp(target - _commandBlend, -dt / 0.45f, dt / 0.45f);
        var t = _commandBlend * _commandBlend * (3f - 2f * _commandBlend);

        // Chase: behind and above the hull, turning a little late so the hull visibly leads the camera.
        if (!_chaseInitialised)
        {
            _chaseOrientation = OwnOrientation;
            _chaseInitialised = true;
        }

        _chaseOrientation = Quaternion.Slerp(_chaseOrientation, OwnOrientation, 1f - MathF.Exp(-11f * dt));
        var speed = Own.Velocity.Length();
        var pullBack = 1f + Math.Min(speed / 720f, 1f) * 0.25f;
        var chasePosition = OwnPosition + Vector3.Transform(new Vector3(0, 2.6f, 11f * pullBack), _chaseOrientation);

        if (_commandFollow)
            _commandFocus = OwnPosition;
        var commandOrientation = Quaternion.CreateFromYawPitchRoll(_commandYaw, _commandPitch, 0);
        var commandPosition = _commandFocus + Vector3.Transform(new Vector3(0, 0, _commandDistance), commandOrientation);

        CameraPosition = Double3.Lerp(chasePosition, commandPosition, t);
        var orientation = Quaternion.Slerp(_chaseOrientation, commandOrientation, t);
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
