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

    /// <summary>The pilot's own hull's shield, seen from inside: its material is the own kind.</summary>
    public bool Own;
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

        /// <summary>The hit's serial, so the newest ripple can be put back on the published clock.</summary>
        public byte Serial;
    }

    /// <summary>Where a shield is between its last two states, as drawn now.</summary>
    private enum Phase : byte
    {
        /// <summary>Settled: shown or gone.</summary>
        Steady,

        /// <summary>A change the drawn moment has not reached yet: the old state is still drawn.</summary>
        Pending,
        Collapsing,
        Rising,
    }

    private struct HullShield
    {
        public uint Id;
        public bool Used;
        public long Frame;
        public int Shell;
        public byte Serial;

        /// <summary>Whether the newest published state has the shield up.</summary>
        public bool Target;

        /// <summary>The state the last finished change left, which is drawn while the next one waits.</summary>
        public bool Settled;

        /// <summary>
        /// Seconds from the target state's change to the moment drawn: taken from every new publication, so a
        /// stalled frame or a corrected clock never leaves it wrong, and moved on locally only in between.
        /// </summary>
        public float Age;

        /// <summary>The change age the newest publication gave, to tell a new publication from a repeated one.</summary>
        public float Published;

        /// <summary>The hit age the newest publication gave.</summary>
        public float HitPublished;

        /// <summary>
        /// A fade back in, on the local clock, after a collapse was cut short by a corrected prediction; below 0
        /// for none.
        /// </summary>
        public float Correction;
        public Phase Phase;

        // As drawn this frame.
        public float Glow;
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

            if (!known)
            {
                // First sight: as it is, with nothing to animate but a hit still young enough to be spreading.
                shield.Target = shield.Settled = shown;
                shield.Age = shield.Published = hull.ShieldChangeAge;
                shield.HitPublished = hull.ShieldHitAge;
                shield.Correction = -1f;
                shield.Serial = hull.ShieldHit.Serial;
                if (hull.ShieldHit.Strength > 0 && hull.ShieldHitAge < _look.RippleSeconds)
                    Start(ref shield, hull.ShieldHit, hull.ShieldHitAge, radius);
            }
            else
            {
                Ripples(ref shield, hull, radius, dt);
                Follow(ref shield, shown, hull.ShieldChangeAge, dt);
            }

            Draw(ref shield);
            var fraction = capacity > 0f ? Math.Clamp(hull.Shield / capacity, 0f, 1f) : 0f;
            shield.Unstable = shield.Glow > 0f ? 1f - SmoothStep(_look.UnstableFull, _look.UnstableBelow, fraction) : 0f;
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
        var glow = shield.Glow;
        if (glow <= 0f && shield.Collapse <= 0f)
            return;

        ref var shell = ref _shells[shield.Shell];
        if (shell.VertexCount == 0 || _vertexCount + shell.VertexCount > vertices.Length)
            return;

        _shellVertices.AsSpan(shield.Shell * ShieldShell.MaxVertices, shell.VertexCount).CopyTo(vertices[_vertexCount..]);
        // The material is the kind (1 a hull, 2 the pilot's own) plus four times one more than the clip slot.
        var own = material - 4f * MathF.Floor((material + 0.5f) * 0.25f) > 1.5f;
        _draws[_drawCount] = new ShieldDraw { FirstVertex = _vertexCount, VertexCount = shell.VertexCount, Own = own };
        _vertexCount += shell.VertexCount;

        var m = Matrix4x4.CreateFromQuaternion(orientation);
        instances[_drawCount++] = new ShieldInstance
        {
            // System.Numerics matrices transform row vectors, so the rotation's columns are its rows here.
            Row0 = new float4(m.M11, m.M21, m.M31 * stretch, position.X),
            Row1 = new float4(m.M12, m.M22, m.M32 * stretch, position.Y),
            Row2 = new float4(m.M13, m.M23, m.M33 * stretch, position.Z),
            Params = new float4(glow, shield.Unstable, shield.Collapse, material),
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
    /// Follows the shield to <paramref name="shown"/>, the newest published state, whose last change was
    /// <paramref name="published"/> seconds before the drawn moment. A publication that says something new resets
    /// the age; a repeated one leaves it to the local clock. A change that flips back before the drawn moment
    /// reached it never happened; a restart that cuts a collapse short fades in on the local clock.
    /// </summary>
    private void Follow(ref HullShield shield, bool shown, float published, float dt)
    {
        if (published != shield.Published)
        {
            shield.Published = published;
            shield.Age = published;
        }
        else
        {
            shield.Age += dt;
        }

        if (shield.Correction >= 0f)
        {
            shield.Correction += dt / _look.FadeInSeconds;
            if (shield.Correction >= 1f)
                shield.Correction = -1f;
        }

        if (shown == shield.Target)
            return;
        switch (shield.Phase)
        {
            case Phase.Collapsing when shown:
                shield.Settled = true;
                shield.Correction = 0f;
                break;
            case Phase.Rising when !shown:
                // Partly in: it collapses from here on the new change's clock.
                shield.Settled = true;
                break;
        }

        shield.Target = shown;
    }

    /// <summary>Works out what the shield looks like now from where it is between its two states.</summary>
    private void Draw(ref HullShield shield)
    {
        shield.Glow = 0f;
        shield.Collapse = 0f;
        if (shield.Target != shield.Settled && shield.Age >= (shield.Target ? _look.FadeInSeconds : _look.CollapseSeconds))
            shield.Settled = shield.Target;

        if (shield.Target == shield.Settled)
        {
            shield.Phase = Phase.Steady;
            shield.Glow = shield.Target ? (shield.Correction >= 0f ? shield.Correction : 1f) : 0f;
        }
        else if (shield.Age < 0f)
        {
            shield.Phase = Phase.Pending;
            shield.Glow = shield.Settled ? (shield.Correction >= 0f ? shield.Correction : 1f) : 0f;
        }
        else if (shield.Target)
        {
            shield.Phase = Phase.Rising;
            shield.Glow = shield.Age / _look.FadeInSeconds;
        }
        else
        {
            shield.Phase = Phase.Collapsing;
            shield.Correction = -1f;
            shield.Collapse = Math.Max(shield.Age / _look.CollapseSeconds, 1e-3f);
        }
    }

    /// <summary>
    /// Moves the ripples on, puts the newest back on the published clock when a publication says how old its hit
    /// is (the others shift with it), and starts one for a newly reported hit.
    /// </summary>
    private void Ripples(ref HullShield shield, in HullView hull, float radius, float dt)
    {
        var shift = dt;
        if (hull.ShieldHitAge != shield.HitPublished)
        {
            shield.HitPublished = hull.ShieldHitAge;
            if (hull.ShieldHit.Serial == shield.Serial && Newest(ref shield, shield.Serial) is { } age)
                shift = hull.ShieldHitAge - age;
        }

        Advance(ref shield.Ripple0, shift, _look.RippleSeconds);
        Advance(ref shield.Ripple1, shift, _look.RippleSeconds);
        Advance(ref shield.Ripple2, shift, _look.RippleSeconds);
        Advance(ref shield.Ripple3, shift, _look.RippleSeconds);
        if (hull.ShieldHit.Serial != shield.Serial)
        {
            shield.Serial = hull.ShieldHit.Serial;
            if (hull.ShieldHit.Strength > 0 && hull.ShieldHitAge < _look.RippleSeconds)
                Start(ref shield, hull.ShieldHit, hull.ShieldHitAge, radius);
        }
    }

    private static float? Newest(ref HullShield shield, byte serial) =>
        shield.Ripple0.Strength > 0f && shield.Ripple0.Serial == serial ? shield.Ripple0.Age
        : shield.Ripple1.Strength > 0f && shield.Ripple1.Serial == serial ? shield.Ripple1.Age
        : shield.Ripple2.Strength > 0f && shield.Ripple2.Serial == serial ? shield.Ripple2.Age
        : shield.Ripple3.Strength > 0f && shield.Ripple3.Serial == serial ? shield.Ripple3.Age
        : null;

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
            Serial = hit.Serial,
        };
    }

    private static bool Older(in Ripple a, in Ripple b) => a.Strength <= 0f ? b.Strength > 0f : b.Strength > 0f && a.Age > b.Age;

    private static void Advance(ref Ripple ripple, float dt, float life)
    {
        if (ripple.Strength <= 0f)
            return;
        ripple.Age += dt;
        if (ripple.Age >= life || ripple.Age < -life)
            ripple = default;
    }

    private float4 Packed(in Ripple ripple) => new(ripple.Point.X, ripple.Point.Y, ripple.Point.Z, ripple.Strength > 0f ? ripple.Age : _look.RippleSeconds);

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
