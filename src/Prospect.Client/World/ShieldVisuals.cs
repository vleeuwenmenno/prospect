using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Jobs;
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

    /// <summary>
    /// How the shield shivers while it charges back up. x: how far the shell is squashed and stretched, as a share
    /// of its size; y: how strongly it shivers, 0 to 1; z: shivers a second; w: this hull's phase, so no two
    /// shiver in step.
    /// </summary>
    public float4 Recovery;
}

/// <summary>
/// One shell's run of vertices and of indices into them, counted from its first vertex; the shell's instance has the
/// same index as the draw.
/// </summary>
internal struct ShieldDraw
{
    public int FirstVertex;
    public int VertexCount;
    public int FirstIndex;
    public int IndexCount;

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
/// again move it on by themselves. While a shield charges back up, from its restart until it is full or stops, it
/// shivers, settling once the charge stops rising. Everything is sized for the most hulls a snapshot shows, once,
/// so the frame allocates nothing.
/// </summary>
internal sealed class ShieldVisuals
{
    /// <summary>The most shells: one for every hull a snapshot can show (the client world's most views).</summary>
    public const int MaxShields = 64;
    public const int MaxRipples = 4;
    /// <summary>
    /// The room each hull's shell has, the same for every design: admission refuses a design whose intact shell does
    /// not fit it, and a damaged hull whose own shell would not fit keeps its intact one.
    /// </summary>
    public const int ShellVertices = GeometryAdmission.MaxVertices;

    /// <summary>The indices each hull's shell has room for, three a triangle, 16 bits each.</summary>
    public const int ShellIndices = GeometryAdmission.MaxIndices;

    /// <summary>The vertices of all the shells in one frame: every shell, each in its own room, always fits.</summary>
    public const int MaxVertices = MaxShields * ShellVertices;

    /// <summary>The indices of all the shells in one frame.</summary>
    public const int MaxIndices = MaxShields * ShellIndices;

    private readonly ShieldLook _look;
    private readonly JobSystem? _jobs;

    /// <summary>
    /// Shields that look as <paramref name="look"/> says, whose damaged shells are built one at a time on
    /// <paramref name="jobs"/>. Without jobs a shell is built where it is asked for: for tests and tools only, as a
    /// build takes milliseconds.
    /// </summary>
    public ShieldVisuals(ShieldLook look, JobSystem? jobs = null) : this(look, jobs, ShellVertices)
    {
    }

    /// <summary>As above, with the room a damaged shell may take cut down, so tests can make one not fit.</summary>
    internal ShieldVisuals(ShieldLook look, JobSystem? jobs, int damagedRoom)
    {
        _look = look;
        _jobs = jobs;
        _job.Room = Math.Clamp(damagedRoom, 0, ShellVertices);
    }

    /// <summary>How shields look; the renderer passes it on to the shader.</summary>
    public ShieldLook Look => _look;

    private struct Shell
    {
        public bool Used;
        public int Design;

        /// <summary>How many blocks the hull it was built for had lost: a quick first test before the blocks are compared.</summary>
        public uint Revision;
        public int VertexCount;
        public int IndexCount;
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

        /// <summary>
        /// The shell drawn is not this hull's own yet: the intact shell, or the one of fewer losses it had, while its
        /// own is built or when its own would not fit. Both enclose what is left of it.
        /// </summary>
        public bool Waiting;

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

        /// <summary>
        /// The charge as last seen to change, on this timeline and at this capacity, and which way it moved then.
        /// Publications in between that read the same are not a change.
        /// </summary>
        public float Charge;
        public float ChargeCapacity;
        public sbyte Trend;

        /// <summary>
        /// Seconds of drawn time since the charge last rose for a second time running. It is kept a while past the
        /// settle time, so a correction that takes drawn time back by up to <see cref="ShieldLook.MaxEffectSeconds"/>
        /// does not wake a shield that had settled.
        /// </summary>
        public float Risen;

        // As drawn this frame.
        public float Glow;
        public float Collapse;
        public float Unstable;
        public float Recovery;
        public Ripple Ripple0;
        public Ripple Ripple1;
        public Ripple Ripple2;
        public Ripple Ripple3;
    }

    /// <summary>The words of one shell's owned block set, bounded by stage 2 geometry admission.</summary>
    private static readonly int ShellWords = BlockSet.WordCount(GeometryAdmission.MaxBlocks);

    private readonly Shell[] _shells = new Shell[MaxShields];

    // The blocks each shell was built from, one set per shell, copied: a view's blocks last only as long as its
    // snapshot.
    private readonly ulong[] _shellBlocks = new ulong[MaxShields * ShellWords];
    private readonly MeshVertex[] _shellVertices = new MeshVertex[MaxShields * ShellVertices];
    private readonly ushort[] _shellIndices = new ushort[MaxShields * ShellIndices];
    private readonly HullShield[] _hulls = new HullShield[MaxShields];
    private readonly BuildJob _job = new();
    private JobHandle _handle;
    private bool _building;
    private bool _built;
    private int _generation;
    private HullDesign[]? _designs;
    private long _frame;
    private long _publication;

    private ShieldDraw[] _draws = [];
    private int _drawCount;
    private int _vertexCount;
    private int _indexCount;

    /// <summary>The shells added since <see cref="Begin"/>.</summary>
    public ReadOnlySpan<ShieldDraw> Draws => _draws.AsSpan(0, _drawCount);

    /// <summary>
    /// Forgets a departed session's hulls. The shells are kept until the designs change; a shell still being built
    /// for one of them is thrown away when it is done.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_hulls);
        _generation++;
    }

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
            // Another world evaluates its designs under its own settings: build every shell again, and never put a
            // shell built for the old designs on a hull of the new ones.
            _designs = designs;
            Array.Clear(_shells);
            _generation++;
        }

        // A shell finished on the worker waits here until its hull is seen below; a stale one is dropped.
        if (_building && (_jobs is null || _jobs.IsComplete(_handle)))
        {
            _building = false;
            _built = _job.Generation == _generation;
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
            ref var shield = ref HullFor(hull.Id, out var known);
            var shell = known && !fresh && _shells[shield.Shell].Used && _shells[shield.Shell].Design == design
                ? shield.Shell : ShellFor(ref shield, known, designs, design, hull.Blocks.Span, hull.Revision);
            shell = Worker(ref shield, hull.Id, designs, design, hull.Blocks.Span, hull.Revision, shell);
            _shells[shell].Frame = _frame;
            shield.Frame = _frame;
            shield.Shell = shell;
            var capacity = hull.Stats.ShieldCapacity;
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
                shield.Charge = hull.Shield;
                shield.ChargeCapacity = capacity;
                shield.Risen = Settled;
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
                var onward = fresh && hull.IsOwn == shield.Own && hull.ShownTick > shield.Shown;
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
                Charging(ref shield, hull.Shield, capacity, elapsed, fresh, onward);
            }

            Draw(ref shield);
            var fraction = capacity > 0f ? Math.Clamp(hull.Shield / capacity, 0f, 1f) : 0f;
            shield.Unstable = shield.Glow > 0f ? 1f - SmoothStep(_look.UnstableFull, _look.UnstableBelow, fraction) : 0f;

            // Coming back online it shivers, and while it goes on charging up; it settles once the charge stops.
            var settling = 1f - SmoothStep(0f, _look.RecoverySettleSeconds, shield.Risen);
            shield.Recovery = shield.Glow > 0f && shield.Collapse <= 0f ? (shield.Phase == Phase.Rising ? 1f : settling) : 0f;
        }

        // A shell whose hull is gone is dropped, so the worker is free for the next.
        _built = false;

    }

    /// <summary>Starts this frame's shells; <see cref="Add"/> describes each one in <paramref name="draws"/>.</summary>
    public void Begin(ShieldDraw[] draws)
    {
        _draws = draws;
        _drawCount = 0;
        _vertexCount = 0;
        _indexCount = 0;
    }

    /// <summary>
    /// Adds the shield of a hull that is drawn this frame, where and how its hull is drawn: camera-relative
    /// <paramref name="position"/>, <paramref name="orientation"/>, drawn out along its nose by
    /// <paramref name="stretch"/>, and with its hull's <paramref name="material"/>. A hull with no shield, or one
    /// that is offline and done collapsing, adds nothing. The shell goes into <paramref name="instances"/>,
    /// <paramref name="vertices"/> and <paramref name="indices"/>, this frame's memory for them; a shell that would
    /// not fit what is left of either is left out whole, never cut short.
    /// </summary>
    public void Add(uint id, Vector3 position, Quaternion orientation, float stretch, float material, Span<ShieldInstance> instances, Span<MeshVertex> vertices, Span<ushort> indices)
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
        if (shell.VertexCount == 0 || shell.IndexCount == 0 || _vertexCount + shell.VertexCount > vertices.Length || _indexCount + shell.IndexCount > indices.Length)
            return;

        _shellVertices.AsSpan(shield.Shell * ShellVertices, shell.VertexCount).CopyTo(vertices[_vertexCount..]);
        _shellIndices.AsSpan(shield.Shell * ShellIndices, shell.IndexCount).CopyTo(indices[_indexCount..]);
        // The material is the kind (1 a hull, 2 the pilot's own) plus four times one more than the clip slot.
        var own = material - 4f * MathF.Floor((material + 0.5f) * 0.25f) > 1.5f;
        _draws[_drawCount] = new ShieldDraw { FirstVertex = _vertexCount, VertexCount = shell.VertexCount, FirstIndex = _indexCount, IndexCount = shell.IndexCount, Own = own };
        _vertexCount += shell.VertexCount;
        _indexCount += shell.IndexCount;

        var m = Matrix4x4.CreateFromQuaternion(orientation);
        var design = _designs![shell.Design];
        var strain = _look.RecoveryWobble * design.BlockSize / Math.Max(design.Flight.Radius, design.BlockSize);
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
            Recovery = new float4(strain * shield.Recovery, shield.Recovery, _look.RecoveryHz, (id * 0.618034f % 1f) * MathF.Tau),
        };
    }

    /// <summary>
    /// The shell a hull with these blocks is drawn with. Hulls of one design with the same blocks share a shell, so the
    /// blocks themselves are what is compared; the count of lost blocks only rules most shells out first. An intact
    /// hull takes its design's admitted shell at once. A damaged one with no shell of its own yet keeps the one it
    /// had, which was built from more of the same blocks, or else takes the intact shell; either encloses what is
    /// left, and <see cref="Worker"/> builds its own.
    /// </summary>
    private int ShellFor(ref HullShield shield, bool known, HullDesign[] designs, int design, ReadOnlySpan<ulong> blocks, uint revision)
    {
        if (blocks.Length > ShellWords)
            throw new ArgumentException("A shell exceeds the stage 2 geometry block budget.", nameof(blocks));
        var found = Cached(design, blocks, revision);
        if (found >= 0)
        {
            shield.Waiting = false;
            return found;
        }

        var hull = designs[design];
        var full = hull.FullSet;
        var intact = BlockSet.Equal(blocks, full);
        shield.Waiting = !intact || hull.IntactShell.IsEmpty;
        // The hull's own earlier shell, of fewer losses of the same design: blocks are only ever lost, never regained,
        // by one hull; a repaired or rebuilt ship is a new hull.
        if (shield.Waiting && known && _shells[shield.Shell].Used && _shells[shield.Shell].Design == design
            && Holds(_shellBlocks.AsSpan(shield.Shell * ShellWords, blocks.Length), blocks))
            return shield.Shell;
        found = Cached(design, full, 0);
        if (found >= 0)
            return found;
        var slot = Victim(-1);
        if (slot < 0)
            return shield.Shell;
        Store(slot, design, full, 0, hull.IntactShell.Span, hull.IntactIndices.Span);
        return slot;
    }

    /// <summary>Whether every block of <paramref name="blocks"/> is also in <paramref name="shell"/>.</summary>
    private static bool Holds(ReadOnlySpan<ulong> shell, ReadOnlySpan<ulong> blocks)
    {
        for (var i = 0; i < blocks.Length; i++)
            if ((blocks[i] & ~shell[i]) != 0)
                return false;
        return true;
    }

    private int Cached(int design, ReadOnlySpan<ulong> blocks, uint revision)
    {
        for (var i = 0; i < _shells.Length; i++)
        {
            ref var shell = ref _shells[i];
            if (shell.Used && shell.Design == design && shell.Revision == revision && BlockSet.Equal(_shellBlocks.AsSpan(i * ShellWords, blocks.Length), blocks))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// A slot to fill: an unused one, else the one longest out of use that no hull of the table still draws, else
    /// <paramref name="mine"/>, the slot of the hull the shell is for, when only that hull draws it. -1 when none.
    /// </summary>
    private int Victim(int mine)
    {
        var victim = -1;
        for (var i = 0; i < _shells.Length; i++)
        {
            if (!_shells[i].Used)
                return i;
            var drawn = false;
            for (var h = 0; h < _hulls.Length && !drawn; h++)
                drawn = _hulls[h].Used && _hulls[h].Shell == i && _hulls[h].Frame >= _frame - 1;
            if (!drawn && (victim < 0 || _shells[i].Frame < _shells[victim].Frame))
                victim = i;
        }

        if (victim >= 0 || mine < 0)
            return victim;
        var others = 0;
        for (var h = 0; h < _hulls.Length; h++)
            others += _hulls[h].Used && _hulls[h].Shell == mine ? 1 : 0;
        return others <= 1 ? mine : -1;
    }

    /// <summary>Puts a shell, vertices and indices together, in a slot; one that does not fit the slot is never stored.</summary>
    private void Store(int slot, int design, ReadOnlySpan<ulong> blocks, uint revision, ReadOnlySpan<ShellVertex> vertices, ReadOnlySpan<ushort> indices)
    {
        if (vertices.Length > ShellVertices || indices.Length > ShellIndices)
            throw new InvalidOperationException("A shield shell larger than its slot.");
        var words = _shellBlocks.AsSpan(slot * ShellWords, ShellWords);
        words.Clear();
        blocks.CopyTo(words);
        vertices.CopyTo(MemoryMarshal.Cast<MeshVertex, ShellVertex>(_shellVertices.AsSpan(slot * ShellVertices, ShellVertices)));
        indices.CopyTo(_shellIndices.AsSpan(slot * ShellIndices, ShellIndices));
        _shells[slot] = new Shell
        {
            Used = true,
            Design = design,
            Revision = revision,
            Frame = _frame,
            VertexCount = vertices.Length,
            IndexCount = indices.Length,
        };
    }

    /// <summary>
    /// Hands a hull waiting for its own shell to the worker when it is free, and puts a finished shell on the hull it
    /// was built for. One build runs at a time, always of a hull's newest blocks, so losses that come faster than
    /// builds are taken together. A shell that would not fit is the intact one, kept under the damaged blocks so it
    /// is not asked for again. A finished shell of blocks the hull has since lost more of still encloses it, and is
    /// drawn while the newer one is built. Returns the slot to draw.
    /// </summary>
    private int Worker(ref HullShield shield, uint id, HullDesign[] designs, int design, ReadOnlySpan<ulong> blocks, uint revision, int shell)
    {
        shell = Take(ref shield, id, designs, design, blocks, revision, shell);
        if (shield.Waiting && !_building && !_built)
        {
            _job.Start(_generation, id, designs[design], design, blocks, revision);
            if (_jobs is null)
            {
                _job.Run();
                _built = true;
                return Take(ref shield, id, designs, design, blocks, revision, shell);
            }

            _handle = _jobs.Schedule(_job, 4);
            _building = true;
        }

        return shell;
    }

    /// <summary>Puts the finished shell on its hull, if this is that hull and it is still of that design.</summary>
    private int Take(ref HullShield shield, uint id, HullDesign[] designs, int design, ReadOnlySpan<ulong> blocks, uint revision, int shell)
    {
        if (_built && _job.Hull == id)
        {
            _built = false;
            if (_job.Design == design && _job.Generation == _generation && Holds(_job.Blocks.AsSpan(0, _job.Words), blocks))
            {
                var done = _job.Words;
                var known = Cached(design, _job.Blocks.AsSpan(0, done), _job.Revision);
                var slot = known >= 0 ? known : Victim(shell);
                if (slot >= 0)
                {
                    if (known < 0 || _shells[known].VertexCount == 0)
                    {
                        var hull = designs[design];
                        if (_job.Count >= 0)
                            Store(slot, design, _job.Blocks.AsSpan(0, done), _job.Revision, _job.Output.AsSpan(0, _job.Count), _job.Indices.AsSpan(0, _job.IndexCount));
                        else
                            Store(slot, design, _job.Blocks.AsSpan(0, done), _job.Revision, hull.IntactShell.Span, hull.IntactIndices.Span);
                    }

                    shell = slot;
                    shield.Waiting = !(revision == _job.Revision && BlockSet.Equal(blocks, _job.Blocks.AsSpan(0, done)));
                }
            }
        }

        return shell;
    }

    /// <summary>
    /// The one shell build that runs at a time, reused: the blocks are copied in when it starts, and its output is
    /// kept until the render thread takes it.
    /// </summary>
    private sealed class BuildJob : IJob
    {
        public readonly ulong[] Blocks = new ulong[ShellWords];
        public readonly ShellVertex[] Output = new ShellVertex[ShellVertices];
        public readonly ushort[] Indices = new ushort[ShellIndices];
        public int IndexCount;
        public int Words;
        public int Generation;
        public uint Hull;
        public int Design;
        public uint Revision;
        public int Count;
        public int Room = ShellVertices;
        private HullDesign? _hull;

        public void Start(int generation, uint hull, HullDesign design, int index, ReadOnlySpan<ulong> blocks, uint revision)
        {
            Generation = generation;
            Hull = hull;
            _hull = design;
            Design = index;
            Revision = revision;
            Words = blocks.Length;
            blocks.CopyTo(Blocks);
            Count = -1;
            IndexCount = 0;
        }

        public void Run()
        {
            try
            {
                Count = ShieldSurface.WithShared(this, static (builder, job) =>
                    builder.Build(job._hull!, job.Blocks.AsSpan(0, job.Words), job._hull!.ShellShape, job.Output.AsSpan(0, job.Room), job.Indices, out job.IndexCount));
            }
            catch (Exception exception)
            {
                // A shell that cannot be built is drawn as the intact one; the hull is never left without a shield.
                Count = -1;
                Logs.App.Error($"Could not build a shield shell: {exception}");
            }
        }
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

    /// <summary>How long after the charge stopped rising a shield counts as long settled.</summary>
    private float Settled => _look.RecoverySettleSeconds + ShieldLook.MaxEffectSeconds;

    /// <summary>
    /// Follows the charge: a <paramref name="fresh"/> publication that reads it risen since it last changed, a
    /// second time running, means it is charging up. Only an <paramref name="onward"/> publication, later on the
    /// same timeline, at the same <paramref name="capacity"/>, can say so; any other starts afresh from what it
    /// reads. This is a visual heuristic: forward reconciliation can also raise charge. A charge that is full,
    /// starved or waiting out its delay does not rise, and the shield settles.
    /// </summary>
    private void Charging(ref HullShield shield, float charge, float capacity, float elapsed, bool fresh, bool onward)
    {
        shield.Risen = Math.Clamp(shield.Risen + elapsed, 0f, Settled);
        if (!fresh)
            return;
        if (!onward || capacity != shield.ChargeCapacity)
        {
            shield.Charge = charge;
            shield.ChargeCapacity = capacity;
            shield.Trend = 0;
            return;
        }

        // A change too small to tell from rounding is no change.
        var noise = capacity * 1e-4f;
        if (charge > shield.Charge + noise)
        {
            if (shield.Trend > 0)
                shield.Risen = 0f;
            shield.Trend = 1;
            shield.Charge = charge;
        }
        else if (charge < shield.Charge - noise)
        {
            shield.Trend = -1;
            shield.Charge = charge;
        }
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
    private void Start(ref HullShield shield, in ShieldHit hit, float age, float radius)
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
            Strength = _look.ContactStrength + (1f - _look.ContactStrength) * (hit.Strength / 255f),
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
