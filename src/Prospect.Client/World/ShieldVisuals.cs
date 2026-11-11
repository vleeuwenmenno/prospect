using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// One shield shell as the GPU draws it: the hull's camera-relative transform, how it looks, and its newest
/// ripples. Matches <c>shield.vert</c>.
/// </summary>
internal struct ShieldInstance
{
    public float4 Row0;
    public float4 Row1;
    public float4 Row2;

    /// <summary>
    /// x: how far the shield has come online, 0 to 1. y: how close it is to breaking, 0 to 1. z: how far its
    /// collapse has gone, 0 for none, up to 1. w: the hull's own material, as the hull's mesh is given it, so the
    /// shell is cut by exactly what cuts its hull.
    /// </summary>
    public float4 Params;

    /// <summary>Each ripple: the hull-local point it spreads from in metres, and its age in seconds.</summary>
    public float4 Ripple0;
    public float4 Ripple1;
    public float4 Ripple2;
    public float4 Ripple3;

    /// <summary>Each ripple's strength, 0 for none.</summary>
    public float4 Strengths;
}

/// <summary>One shell's run of vertices; the shell's instance has the same index as the draw.</summary>
internal struct ShieldDraw
{
    public int FirstVertex;
    public int VertexCount;
}

/// <summary>
/// The shields of the hulls in view, as drawn: client side only, worked out from what the simulation replicates.
/// Each hull's shell is built from the blocks it still has and kept while that hull keeps them. A shield is drawn
/// while it is online, fading in after the simulation brings it back; when it breaks or loses its last emitter it
/// collapses red and is gone. Every absorbed hit the snapshots report spreads a ripple from where it landed, aged
/// from when the server says it happened, so a hit long over is never shown as new. Everything is sized for
/// the most hulls a snapshot shows, once, so the frame allocates nothing.
/// </summary>
internal sealed class ShieldVisuals
{
    /// <summary>The most shells: one for every hull a snapshot can show (the client world's most views).</summary>
    public const int MaxShields = 64;
    public const int MaxRipples = 4;
    /// <summary>
    /// The vertices of all the shells in one frame. Every live design fits many times over (a 64-block hull's
    /// shell is a few thousand); past it, the rest of that frame's shells are left out.
    /// </summary>
    public const int MaxVertices = MaxShields * 2304;

    private readonly ShieldLook _look;

    /// <summary>Shields that look as <paramref name="look"/> says.</summary>
    public ShieldVisuals(ShieldLook look) => _look = look;

    /// <summary>How shields look; the renderer passes it on to the shader.</summary>
    public ShieldLook Look => _look;

    private struct Shell
    {
        public bool Used;
        public int Design;
        public ulong Blocks;
        public int VertexCount;
        public float Capacity;
        public long Frame;
    }

    private struct Ripple
    {
        public Vector3 Point;
        public float Strength;
        public float Age;
    }

    private struct HullShield
    {
        public uint Id;
        public bool Used;
        public long Frame;
        public int Shell;
        public byte Serial;
        /// <summary>Whether the shield is up as drawn now: its last change the drawn moment has reached.</summary>
        public bool Shown;
        public float Fade;

        /// <summary>
        /// A change the snapshots report that the drawn moment has not reached yet: a remote hull is drawn a little
        /// in the past. It applies once <see cref="PendingWait"/> seconds have passed.
        /// </summary>
        public bool Pending;
        public bool PendingShown;
        public float PendingWait;

        /// <summary>How far the collapse has gone, 0 to 1; below 0 for none.</summary>
        public float Collapse;
        public float Unstable;
        public Ripple Ripple0;
        public Ripple Ripple1;
        public Ripple Ripple2;
        public Ripple Ripple3;
    }

    private readonly Shell[] _shells = new Shell[MaxShields];
    private readonly MeshVertex[] _shellVertices = new MeshVertex[MaxShields * ShieldShell.MaxVertices];
    private readonly HullShield[] _hulls = new HullShield[MaxShields];
    private HullDesign[]? _designs;
    private long _frame;

    private ShieldDraw[] _draws = [];
    private int _drawCount;
    private int _vertexCount;

    /// <summary>The shells added since <see cref="Begin"/>.</summary>
    public ReadOnlySpan<ShieldDraw> Draws => _draws.AsSpan(0, _drawCount);

    /// <summary>Forgets a departed session's hulls. The shells are kept until the designs change.</summary>
    public void Clear() => Array.Clear(_hulls);

    /// <summary>
    /// Follows every hull's shield through this frame: builds the shell of a hull that has changed, starts a ripple
    /// for each newly reported hit, and moves fades and collapses on by <paramref name="dt"/>.
    /// </summary>
    public void Update(ReadOnlySpan<HullView> hulls, HullDesign[] designs, float dt)
    {
        _frame++;
        dt = Math.Clamp(dt, 0f, 0.1f);
        if (!ReferenceEquals(designs, _designs))
        {
            // Another world evaluates its designs under its own settings: build every shell again.
            _designs = designs;
            Array.Clear(_shells);
        }

        // Free departed IDs before newcomers need their slots; the previous snapshot may have filled the table.
        for (var i = 0; i < _hulls.Length; i++)
        {
            if (!_hulls[i].Used)
                continue;
            var present = false;
            for (var j = 0; j < hulls.Length && j < MaxShields; j++)
                present |= hulls[j].Id == _hulls[i].Id;
            if (!present)
                _hulls[i] = default;
        }

        for (var i = 0; i < hulls.Length && i < MaxShields; i++)
        {
            ref readonly var hull = ref hulls[i];
            var design = Math.Min((int)hull.Design, designs.Length - 1);
            var shell = ShellFor(designs, design, hull.Blocks);
            ref var shield = ref HullFor(hull.Id, out var known);
            shield.Frame = _frame;
            shield.Shell = shell;
            var capacity = _shells[shell].Capacity;
            var shown = capacity > 0f && !hull.ShieldOffline;
            var radius = designs[design].Flight.Radius;

            Advance(ref shield.Ripple0, dt, _look.RippleSeconds);
            Advance(ref shield.Ripple1, dt, _look.RippleSeconds);
            Advance(ref shield.Ripple2, dt, _look.RippleSeconds);
            Advance(ref shield.Ripple3, dt, _look.RippleSeconds);

            if (!known)
            {
                // First sight: as it is, with nothing to animate but a hit still young enough to be spreading.
                shield.Shown = shown;
                shield.Fade = shown ? 1f : 0f;
                shield.Collapse = -1f;
                shield.Serial = hull.ShieldHit.Serial;
                if (hull.ShieldHit.Strength > 0 && hull.ShieldHitAge < _look.RippleSeconds)
                    Start(ref shield, hull.ShieldHit, hull.ShieldHitAge, radius);
            }
            else
            {
                // What was already running moves on to now; changes found below are placed at their own age.
                if (shield.Collapse >= 0f)
                {
                    shield.Collapse += dt / _look.CollapseSeconds;
                    if (shield.Collapse >= 1f)
                        shield.Collapse = -1f;
                }

                if (shield.Shown)
                    shield.Fade = Math.Min(1f, shield.Fade + dt / _look.FadeInSeconds);

                if (hull.ShieldHit.Serial != shield.Serial)
                {
                    shield.Serial = hull.ShieldHit.Serial;
                    if (hull.ShieldHit.Strength > 0 && hull.ShieldHitAge < _look.RippleSeconds)
                        Start(ref shield, hull.ShieldHit, hull.ShieldHitAge, radius);
                }

                Follow(ref shield, shown, hull.ShieldChangeAge, dt);
            }

            var fraction = capacity > 0f ? Math.Clamp(hull.Shield / capacity, 0f, 1f) : 0f;
            shield.Unstable = shield.Shown ? 1f - SmoothStep(_look.UnstableFull, _look.UnstableBelow, fraction) : 0f;
        }

    }

    /// <summary>Starts this frame's shells; <see cref="Add"/> describes each one in <paramref name="draws"/>.</summary>
    public void Begin(ShieldDraw[] draws)
    {
        _draws = draws;
        _drawCount = 0;
        _vertexCount = 0;
    }

    /// <summary>
    /// Adds the shield of a hull that is drawn this frame, where and how its hull is drawn: camera-relative
    /// <paramref name="position"/>, <paramref name="orientation"/>, drawn out along its nose by
    /// <paramref name="stretch"/>, and with its hull's <paramref name="material"/>. A hull with no shield, or one
    /// that is offline and done collapsing, adds nothing. The shell goes into <paramref name="instances"/> and
    /// <paramref name="vertices"/>, this frame's memory for them.
    /// </summary>
    public void Add(uint id, Vector3 position, Quaternion orientation, float stretch, float material, Span<ShieldInstance> instances, Span<MeshVertex> vertices)
    {
        if (_drawCount >= Math.Min(_draws.Length, instances.Length))
            return;
        var index = Find(id);
        if (index < 0)
            return;
        ref var shield = ref _hulls[index];
        var glow = shield.Shown ? shield.Fade : 0f;
        if (glow <= 0f && shield.Collapse < 0f)
            return;

        ref var shell = ref _shells[shield.Shell];
        if (shell.VertexCount == 0 || _vertexCount + shell.VertexCount > vertices.Length)
            return;

        _shellVertices.AsSpan(shield.Shell * ShieldShell.MaxVertices, shell.VertexCount).CopyTo(vertices[_vertexCount..]);
        _draws[_drawCount] = new ShieldDraw { FirstVertex = _vertexCount, VertexCount = shell.VertexCount };
        _vertexCount += shell.VertexCount;

        var m = Matrix4x4.CreateFromQuaternion(orientation);
        instances[_drawCount++] = new ShieldInstance
        {
            // System.Numerics matrices transform row vectors, so the rotation's columns are its rows here.
            Row0 = new float4(m.M11, m.M21, m.M31 * stretch, position.X),
            Row1 = new float4(m.M12, m.M22, m.M32 * stretch, position.Y),
            Row2 = new float4(m.M13, m.M23, m.M33 * stretch, position.Z),
            Params = new float4(glow, shield.Unstable, shield.Collapse >= 0f ? Math.Max(shield.Collapse, 1e-3f) : 0f, material),
            Ripple0 = Packed(shield.Ripple0),
            Ripple1 = Packed(shield.Ripple1),
            Ripple2 = Packed(shield.Ripple2),
            Ripple3 = Packed(shield.Ripple3),
            Strengths = new float4(shield.Ripple0.Strength, shield.Ripple1.Strength, shield.Ripple2.Strength, shield.Ripple3.Strength),
        };
    }

    /// <summary>The shell a hull with these blocks is drawn with, built now if no hull had it in view.</summary>
    private int ShellFor(HullDesign[] designs, int design, ulong blocks)
    {
        var victim = -1;
        for (var i = 0; i < _shells.Length; i++)
        {
            ref var shell = ref _shells[i];
            if (shell.Used && shell.Design == design && shell.Blocks == blocks)
            {
                shell.Frame = _frame;
                return i;
            }

            // The shell longest out of use goes; one in use this frame never does, as there are no more hulls
            // in a frame than shells.
            if (victim < 0 || !shell.Used || (_shells[victim].Used && shell.Frame < _shells[victim].Frame))
                victim = i;
        }

        ref var built = ref _shells[victim];
        var hull = designs[design];
        hull.Evaluate(blocks & hull.FullMask, out var flight, out _);
        built = new Shell
        {
            Used = true,
            Design = design,
            Blocks = blocks,
            Capacity = flight.ShieldCapacity,
            Frame = _frame,
            // Built even without a shield left: a shield whose last emitter just went collapses on it.
            VertexCount = ShieldShell.Build(hull, blocks, _shellVertices.AsSpan(victim * ShieldShell.MaxVertices, ShieldShell.MaxVertices)),
        };
        return victim;
    }

    private ref HullShield HullFor(uint id, out bool known)
    {
        var free = -1;
        for (var i = 0; i < _hulls.Length; i++)
        {
            if (_hulls[i].Used && _hulls[i].Id == id)
            {
                known = true;
                return ref _hulls[i];
            }

            if (free < 0 && !_hulls[i].Used)
                free = i;
        }

        // Every slot is a hull of this snapshot only when it holds more hulls than shells, which it cannot.
        if (free < 0)
            free = 0;
        known = false;
        _hulls[free] = new HullShield { Id = id, Used = true, Collapse = -1f };
        return ref _hulls[free];
    }

    private int Find(uint id)
    {
        for (var i = 0; i < _hulls.Length; i++)
        {
            if (_hulls[i].Used && _hulls[i].Id == id && _hulls[i].Frame == _frame)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Follows the shield to <paramref name="shown"/>, the state the newest snapshot (or the prediction) has, whose
    /// last change was <paramref name="age"/> seconds before the drawn moment. A change still ahead of the drawn
    /// moment waits; one already over shows its end without its animation; one in between starts part-way.
    /// </summary>
    private void Follow(ref HullShield shield, bool shown, float age, float dt)
    {
        if (shield.Pending)
        {
            shield.PendingWait -= dt;
            if (shown != shield.PendingShown)
            {
                // The change was taken back before it was drawn (a prediction corrected, say): it never happened.
                shield.Pending = false;
            }
            else if (shield.PendingWait <= 0f)
            {
                shield.Pending = false;
                Apply(ref shield, shown, -shield.PendingWait);
                return;
            }
        }

        if (shown == (shield.Pending ? shield.PendingShown : shield.Shown))
            return;
        if (age < 0f)
        {
            shield.Pending = true;
            shield.PendingShown = shown;
            shield.PendingWait = -age;
            return;
        }

        Apply(ref shield, shown, age);
    }

    /// <summary>Draws a change that happened <paramref name="age"/> seconds ago.</summary>
    private void Apply(ref HullShield shield, bool shown, float age)
    {
        if (shown == shield.Shown)
            return;
        if (!shown)
        {
            shield.Shown = false;
            shield.Fade = 0f;
            shield.Collapse = age < _look.CollapseSeconds ? age / _look.CollapseSeconds : -1f;
            return;
        }

        // A shield back before its collapse was over is a corrected prediction, or a change too quick to see:
        // it fades in from nothing rather than snapping on, however old the change says it is.
        var collapsing = shield.Collapse >= 0f;
        shield.Shown = true;
        shield.Collapse = -1f;
        shield.Fade = age < _look.FadeInSeconds ? age / _look.FadeInSeconds : collapsing ? 0f : 1f;
    }

    /// <summary>Puts a new ripple in the place of the oldest, or of one that is over.</summary>
    private static void Start(ref HullShield shield, in ShieldHit hit, float age, float radius)
    {
        ref var slot = ref shield.Ripple0;
        if (Older(shield.Ripple1, slot))
            slot = ref shield.Ripple1;
        if (Older(shield.Ripple2, slot))
            slot = ref shield.Ripple2;
        if (Older(shield.Ripple3, slot))
            slot = ref shield.Ripple3;
        slot = new Ripple
        {
            Point = hit.Point(radius),
            Strength = 0.35f + 0.65f * (hit.Strength / 255f),
            Age = age,
        };
    }

    private static bool Older(in Ripple a, in Ripple b) => a.Strength <= 0f ? b.Strength > 0f : b.Strength > 0f && a.Age > b.Age;

    private static void Advance(ref Ripple ripple, float dt, float life)
    {
        if (ripple.Strength <= 0f)
            return;
        ripple.Age += dt;
        if (ripple.Age >= life)
            ripple = default;
    }

    private float4 Packed(in Ripple ripple) => new(ripple.Point.X, ripple.Point.Y, ripple.Point.Z, ripple.Strength > 0f ? ripple.Age : _look.RippleSeconds);

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
