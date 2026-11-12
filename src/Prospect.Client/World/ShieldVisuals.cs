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
/// from when the server says it happened, so a hit long over is never shown as new. Every new publication of
/// the session puts each effect back on the clock that publication gives; only frames that draw one publication
/// again move it on by themselves. Everything is sized for the most hulls a snapshot shows, once, so the frame
/// allocates nothing.
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
        /// <summary>Shown or gone: nothing changed, or the change has run its course.</summary>
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

        /// <summary>
        /// The state the observed change started from, which is drawn while that change waits. It is kept after
        /// the change has run its course, so an age corrected back below the change's length shows it part-way
        /// again. Equal to <see cref="Target"/> when no change was seen: at first sight, or once one was taken
        /// back.
        /// </summary>
        public bool From;

        /// <summary>
        /// Seconds from the target state's change to the moment drawn: taken from every new publication, whatever
        /// it reads, so a stalled frame or a corrected clock never leaves it wrong, and moved on locally only
        /// while one publication is drawn again.
        /// </summary>
        public float Age;

        /// <summary>The simulation tick the newest publication showed this hull at, on the hull's own timeline.</summary>
        public double Shown;

        /// <summary>Whether that timeline is the pilot's own prediction; a hull changes timelines when it changes hands.</summary>
        public bool Own;

        /// <summary>Seconds drawn past that publication on the local clock; the next publication takes them back.</summary>
        public float Ahead;

        /// <summary>
        /// A fade back in, over drawn simulation time, after a collapse was cut short by a corrected prediction;
        /// below 0 for none.
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
    private long _publication;

    private ShieldDraw[] _draws = [];
    private int _drawCount;
    private int _vertexCount;

    /// <summary>The shells added since <see cref="Begin"/>.</summary>
    public ReadOnlySpan<ShieldDraw> Draws => _draws.AsSpan(0, _drawCount);

    /// <summary>Forgets a departed session's hulls. The shells are kept until the designs change.</summary>
    public void Clear() => Array.Clear(_hulls);

    /// <summary>
    /// Follows every hull's shield through this frame: builds the shell of a hull that has changed, starts a ripple
    /// for each newly reported hit, and moves fades and collapses on. <paramref name="publication"/> names the
    /// publication the hulls come from (<see cref="SessionSnapshot.PublishedAt"/>): a new one puts every effect
    /// on the clock it gives, even when it reads the same ages as the last, and the drawn simulation time between
    /// the two is what its hulls' <see cref="HullView.ShownTick"/> say. Only a frame that draws the same
    /// publication again moves on by <paramref name="dt"/>, the frame's own length.
    /// </summary>
    public void Update(ReadOnlySpan<HullView> hulls, HullDesign[] designs, long publication, float dt)
    {
        _frame++;
        dt = Math.Clamp(dt, 0f, 0.1f);
        var fresh = publication != _publication;
        _publication = publication;
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
                shield.Target = shield.From = shown;
                shield.Age = hull.ShieldChangeAge;
                shield.Shown = hull.ShownTick;
                shield.Own = hull.IsOwn;
                shield.Correction = -1f;
                shield.Serial = hull.ShieldHit.Serial;
                if (hull.ShieldHit.Strength > 0 && hull.ShieldHitAge < _look.RippleSeconds)
                    Start(ref shield, hull.ShieldHit, hull.ShieldHitAge, radius);
            }
            else
            {
                // The simulation time drawn since the last frame. A new publication says where its hull is on
                // the hull's own timeline, however long the frames between took; what was drawn ahead of the
                // last one is part of that already.
                var elapsed = dt;
                if (!fresh)
                {
                    shield.Ahead += dt;
                }
                else
                {
                    // A hull the pilot takes or leaves is on another timeline from here on, and the two counts
                    // say nothing about each other: that one frame moves on by its own length.
                    if (hull.IsOwn == shield.Own)
                        elapsed = (float)((hull.ShownTick - shield.Shown) * HullSim.Dt) - shield.Ahead;
                    shield.Shown = hull.ShownTick;
                    shield.Own = hull.IsOwn;
                    shield.Ahead = 0f;
                }

                Ripples(ref shield, hull, radius, elapsed, fresh);
                Follow(ref shield, shown, hull.ShieldChangeAge, elapsed, fresh);
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
    /// <paramref name="published"/> seconds before the drawn moment. A <paramref name="fresh"/> publication sets
    /// the age, whether or not it reads as the last one did; a repeated one leaves it to the local clock. A change
    /// that flips back before the drawn moment reached it never happened; a restart that cuts a collapse short
    /// fades in over the <paramref name="elapsed"/> time.
    /// </summary>
    private void Follow(ref HullShield shield, bool shown, float published, float elapsed, bool fresh)
    {
        shield.Age = fresh ? published : shield.Age + elapsed;
        if (shield.Correction >= 0f)
        {
            // A new publication takes back any local advance, even one that finished the fade between publications.
            shield.Correction = Math.Max(0f, shield.Correction + elapsed / _look.FadeInSeconds);
        }

        if (shown == shield.Target)
            return;

        // What the new change starts from is what the last frame drew.
        switch (shield.Phase)
        {
            case Phase.Steady:
                shield.From = shield.Target;
                break;
            case Phase.Pending:
                // The waiting change is taken back before it showed: the state it started from is the target.
                break;
            case Phase.Collapsing:
                shield.From = true;
                shield.Correction = 0f;
                break;
            case Phase.Rising:
                // Partly in: it collapses from here on the new change's clock.
                shield.From = true;
                break;
        }

        shield.Target = shown;
    }

    /// <summary>
    /// Works out what the shield looks like now from its two states and the change's age alone, so an age that
    /// is corrected, forward or back across the change's end, is drawn as it reads.
    /// </summary>
    private void Draw(ref HullShield shield)
    {
        shield.Glow = 0f;
        shield.Collapse = 0f;
        if (shield.Target == shield.From || shield.Age >= (shield.Target ? _look.FadeInSeconds : _look.CollapseSeconds))
        {
            shield.Phase = Phase.Steady;
            shield.Glow = shield.Target ? (shield.Correction >= 0f ? Math.Min(shield.Correction, 1f) : 1f) : 0f;
        }
        else if (shield.Age < 0f)
        {
            shield.Phase = Phase.Pending;
            shield.Glow = shield.From ? (shield.Correction >= 0f ? Math.Min(shield.Correction, 1f) : 1f) : 0f;
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
    /// Moves every ripple on by the <paramref name="elapsed"/> drawn time, whichever hit is the newest, then
    /// starts one for a newly reported hit at the age it is published with. A <paramref name="fresh"/> publication
    /// that tells of the same hit again puts that hit's ripple on the age it gives.
    /// </summary>
    private void Ripples(ref HullShield shield, in HullView hull, float radius, float elapsed, bool fresh)
    {
        var life = _look.RippleSeconds;
        // The newest live ripple has an authoritative age: do not expire it on the predicted clock before Place.
        var place = fresh && hull.ShieldHit.Serial == shield.Serial;
        if (!place || shield.Ripple0.Serial != shield.Serial)
            Advance(ref shield.Ripple0, elapsed, life);
        if (!place || shield.Ripple1.Serial != shield.Serial)
            Advance(ref shield.Ripple1, elapsed, life);
        if (!place || shield.Ripple2.Serial != shield.Serial)
            Advance(ref shield.Ripple2, elapsed, life);
        if (!place || shield.Ripple3.Serial != shield.Serial)
            Advance(ref shield.Ripple3, elapsed, life);
        if (hull.ShieldHit.Serial != shield.Serial)
        {
            shield.Serial = hull.ShieldHit.Serial;
            if (hull.ShieldHit.Strength > 0 && hull.ShieldHitAge < life)
                Start(ref shield, hull.ShieldHit, hull.ShieldHitAge, radius);
        }
        else if (fresh)
        {
            Place(ref shield.Ripple0, shield.Serial, hull.ShieldHitAge, life);
            Place(ref shield.Ripple1, shield.Serial, hull.ShieldHitAge, life);
            Place(ref shield.Ripple2, shield.Serial, hull.ShieldHitAge, life);
            Place(ref shield.Ripple3, shield.Serial, hull.ShieldHitAge, life);
        }
    }

    /// <summary>Puts the ripple of the hit with this serial, if it is still spreading, on the published age.</summary>
    private static void Place(ref Ripple ripple, byte serial, float age, float life)
    {
        if (ripple.Strength <= 0f || ripple.Serial != serial)
            return;
        ripple.Age = age;
        if (age >= life || age < -life)
            ripple = default;
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
