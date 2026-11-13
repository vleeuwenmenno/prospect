using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// What running into a hull does to that hull: its share of the impulse and the damage to the blocks that were
/// touched. The hull that ran into it has already taken its own share in its step.
/// </summary>
internal struct HullImpulse
{
    /// <summary>The struck hull's slot in the store.</summary>
    public int Hull;

    /// <summary>
    /// How far to move it, to where it was when it was struck. A hull that has flown its tick goes back along
    /// its path. One that has not flown yet goes on by what the flight up to the impact, at the velocity it had
    /// then, adds to the tick it will fly at its new one.
    /// </summary>
    public Vector3 Shift;

    /// <summary>The change to its velocity, and to its angular velocity about its own axes.</summary>
    public Vector3 Velocity;
    public Vector3 AngularVelocity;

    /// <summary>
    /// The damage the blocks that were touched share after the shield. Which blocks they are is a
    /// <see cref="BlockSet"/> the field keeps beside the impulse, as a set has no fixed size.
    /// </summary>
    public float Damage;

    /// <summary>Closing speed used for visual feedback even when the contact does no damage.</summary>
    public float ContactSpeed;

    /// <summary>The struck box surface in the receiving hull's local coordinates.</summary>
    public Vector3 ShieldPoint;
}

/// <summary>
/// Working memory for finding what a hull touches: retained pairs and the sets of blocks
/// on either side of a contact. Sized once for the largest design it will be used with, so a step takes no memory.
/// One step uses it at a time; a <see cref="HullField"/> has its own.
/// </summary>
public sealed class ContactScratch
{
    /// <summary>Working memory for hulls of up to <paramref name="blocks"/> blocks.</summary>
    public ContactScratch(int blocks)
    {
        Blocks = blocks;
        PairA = new int[FlightRules.MaxCollisionContacts];
        PairB = new int[FlightRules.MaxCollisionContacts];
        Mine = new ulong[BlockSet.WordCount(blocks)];
        Theirs = new ulong[BlockSet.WordCount(blocks)];
    }

    /// <summary>Working memory for the largest of some designs.</summary>
    public ContactScratch(ReadOnlySpan<HullDesign> designs)
        : this(Largest(designs)) { }

    /// <summary>The most blocks a hull using this memory may have.</summary>
    public int Blocks { get; }

    // Fixed room for the largest allowed contact budget, independent of a design's block count.
    internal int[] PairA { get; }
    internal int[] PairB { get; }

    /// <summary>The stepping hull's blocks that touch, and the other hull's.</summary>
    internal ulong[] Mine { get; }
    internal ulong[] Theirs { get; }

    internal static int Largest(ReadOnlySpan<HullDesign> designs)
    {
        var blocks = 1;
        foreach (var design in designs)
            blocks = Math.Max(blocks, design.BlockCount);
        return blocks;
    }
}

/// <summary>
/// The hulls that can run into each other, and the one step that moves any of them. The server's field is its
/// hull store. A predicting client keeps a field of its own hull and the hulls near it as it last heard of them,
/// and steps that with the same code, so what it shows is what the server will most likely decide. The client's
/// field is not authoritative: it does not know how many hull points each block has left, so there damage only
/// shows in a hull's totals and no block is lost until the server says so. Used from one thread.
/// </summary>
public sealed class HullField
{
    /// <summary>The most hulls one hull can strike in a single step; any beyond that are struck a step later.</summary>
    private const int MaxContacts = 64;

    private readonly HullStore _hulls;
    private readonly bool _authoritative;
    private HullDesign[] _designs;
    private readonly HullImpulse[] _impulses = new HullImpulse[MaxContacts];

    // The blocks each recorded impulse touched: one set per impulse, each with room for the largest design the
    // field has room for. Replaced only by SetDesigns, between steps.
    private int _touchWords;
    private ulong[] _touching;
    private ContactScratch _scratch;
    private readonly int[] _lost = new int[MaxContacts];
    private int _impulseCount;
    private int _lostCount;
    private bool _collecting;
    private long _round;

    /// <summary>
    /// A field over <paramref name="hulls"/> of the given designs. <paramref name="reserveBlocks"/> takes working
    /// memory for designs of up to that many blocks at once, for a field that will be given larger designs later
    /// through <see cref="SetDesigns"/> and must not take memory then.
    /// </summary>
    public HullField(HullStore hulls, HullDesign[] designs, bool authoritative = true, int reserveBlocks = 0)
    {
        _hulls = hulls;
        _designs = designs;
        _authoritative = authoritative;
        var blocks = Math.Max(ContactScratch.Largest(designs), reserveBlocks);
        _touchWords = BlockSet.WordCount(blocks);
        _touching = new ulong[MaxContacts * _touchWords];
        _scratch = new ContactScratch(blocks);
    }

    public HullStore Hulls => _hulls;

    /// <summary>
    /// Gives the field a catalogue that has grown: the designs it had, at the numbers they had, and more after
    /// them. Call it between steps, never during one. Working memory is taken here only if a new design is larger
    /// than anything the field has room for, so a field that reserved room for the largest design allowed takes none.
    /// </summary>
    public void SetDesigns(HullDesign[] grown)
    {
        if (_collecting) throw new InvalidOperationException("A field's designs cannot change during a step.");
        var blocks = ContactScratch.Largest(grown);
        if (blocks > _scratch.Blocks)
        {
            _touchWords = BlockSet.WordCount(blocks);
            _touching = new ulong[MaxContacts * _touchWords];
            _scratch = new ContactScratch(blocks);
        }

        _designs = grown;
    }

    /// <summary>Every hull design, indexed by the design number the stored hulls carry.</summary>
    public HullDesign[] Designs => _designs;

    /// <summary>The slot of the hull being stepped, which does not run into itself, or -1 if it is not in the store.</summary>
    internal int Self { get; private set; } = -1;

    /// <summary>True when a stepping hull must stop striking others because their share can no longer be recorded.</summary>
    internal bool IsFull => _collecting && _impulseCount == _impulses.Length;

    /// <summary>
    /// True while a hull takes its first step of the round. The hulls around it fly during that same tick, so it
    /// is their path relative to each other that is searched for a contact. In a further step of the same round
    /// the others have had their tick, and stand still.
    /// </summary>
    internal bool IsFirstStep { get; private set; }

    /// <summary>
    /// Starts a round: one tick of the simulation, in which every hull is expected to step once. Hulls step one
    /// after another, so halfway through a round some have flown this tick and some have not; the round is how a
    /// stepping hull tells them apart, and meets each of them where the end of the tick will find it. A field
    /// whose rounds are never started steps each hull against the others exactly where they stand.
    /// This also owns the cosmetic clock: call exactly once per world tick, even when hulls are held or take
    /// several input steps, and before any step can hit another hull.
    /// </summary>
    public void BeginRound()
    {
        _round++;
        // Age every hull before contacts: held hulls and hulls struck before their own step share this clock.
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (_hulls.IsAliveAt(i))
                HullSim.AdvanceShieldClock(ref _hulls.State(i));
        }
    }

    /// <summary>True if the hull in a slot has stepped in this round, so its position is already the tick's end.</summary>
    internal bool HasStepped(int index) => _hulls.Info(index).Round == _round;

    /// <summary>
    /// Marks a hull that will not step in this round, such as one whose pilot's input has not arrived: it has
    /// had its tick and stays where it is, whatever its velocity says. Call it before any hull steps, so the
    /// others meet it there and not a tick's flight ahead.
    /// </summary>
    public void Hold(int index)
    {
        ref var info = ref _hulls.Info(index);
        info.Round = _round;
        Moved(ref info, Vector3.Zero);
    }

    /// <summary>Adds to how far a hull has come in this round, starting the count over if it is from an earlier one.</summary>
    private void Moved(ref HullInfo info, Vector3 by)
    {
        if (info.FlownRound != _round)
        {
            info.FlownRound = _round;
            info.Flown = Vector3.Zero;
        }

        info.Flown += by;
    }

    /// <summary>
    /// The slots of the hulls the last <see cref="Step"/> destroyed by running the stepped hull into them. They are
    /// still in the store; removing them is up to the caller.
    /// </summary>
    public ReadOnlySpan<int> Lost => _lost.AsSpan(0, _lostCount);

    /// <summary>
    /// Records what a contact does to the hull that was struck, and which of its blocks were
    /// <paramref name="touching"/>. Dropped when nobody is collecting.
    /// </summary>
    internal void Add(in HullImpulse impulse, ReadOnlySpan<ulong> touching)
    {
        if (!_collecting || _impulseCount >= _impulses.Length)
            return;
        touching.CopyTo(_touching.AsSpan(_impulseCount * _touchWords, _touchWords));
        _impulses[_impulseCount++] = impulse;
    }

    /// <summary>
    /// Advances the stored hull in a slot one tick: it collides block by block with asteroids and with the other
    /// stored hulls, and every hull it strikes takes its share of the impulse and its damage before this returns,
    /// so a pair is settled once however often either of them is stepped. A hull that loses blocks has its flight
    /// figures read again off its running sums. Returns true if the stepped hull was destroyed; the others it
    /// destroyed are in <see cref="Lost"/>.
    /// </summary>
    public bool Step(int index, in FlightInput input, RockField? rocks = null, double tick = 0, RockImpulses? impulses = null)
    {
        _impulseCount = 0;
        _lostCount = 0;
        _collecting = true;
        Self = index;

        ref var info = ref _hulls.Info(index);
        ref var own = ref _hulls.State(index);
        var from = own.Position;
        IsFirstStep = info.Round != _round;
        var body = Body(index);
        var destroyed = HullSim.Step(ref own, ref info.Order, input, _hulls.Flight(index), ref body, rocks, tick, impulses, this);
        if (body.Damaged)
            info.DamageTick = (long)tick;
        Refit(index);

        // What the hulls that step after this one need to know of it: that it has flown, and how far.
        Moved(ref info, own.Position.RelativeTo(from));
        info.Round = _round;

        _collecting = false;
        Self = -1;

        if (_impulseCount > 0)
            info.ContactTick = (long)tick;

        for (var i = 0; i < _impulseCount; i++)
        {
            ref var impulse = ref _impulses[i];
            ref var state = ref _hulls.State(impulse.Hull);
            ref var struckInfo = ref _hulls.Info(impulse.Hull);
            struckInfo.ContactTick = (long)tick;
            Moved(ref struckInfo, impulse.Shift);
            state.Position += impulse.Shift;
            state.Velocity += impulse.Velocity;
            state.AngularVelocity += impulse.AngularVelocity;
            var struck = Body(impulse.Hull);
            var touching = _touching.AsSpan(i * _touchWords, struck.Alive.Length);
            HullSim.ShieldContact(ref state, _hulls.Flight(impulse.Hull), struck.Design, touching, impulse.ContactSpeed, impulse.ShieldPoint);
            if (impulse.Damage <= 0)
                continue;

            // The hull that was run into takes damage as surely as the one that ran into it.
            struckInfo.DamageTick = (long)tick;
            if (HullSim.Damage(ref state, _hulls.Flight(impulse.Hull), ref struck, touching, BlockSet.Count(touching), impulse.Damage, impulse.ShieldPoint))
                _lost[_lostCount++] = impulse.Hull;
            Refit(impulse.Hull);
        }

        return destroyed;
    }

    private HullBody Body(int index)
    {
        ref var info = ref _hulls.Info(index);
        return new HullBody
        {
            Design = _designs[info.Design],
            Alive = _hulls.Blocks(index),
            States = _authoritative ? _hulls.BlockStates : null,
            State = info.BlockState,
            Scratch = _scratch,
            CollisionCounts = _hulls.BlockStates.CollisionCounts(info.BlockState),
        };
    }

    /// <summary>
    /// Reads a hull's flight figures again if it has lost blocks since they were last read. The loss is told by
    /// the block state's revision, which rose when the block was taken; block sets are not compared. The figures
    /// come off the hull's running sums, which were brought up to date when the block went, so this looks at no
    /// block, however many went in a step.
    /// </summary>
    internal void Refit(int index)
    {
        ref var info = ref _hulls.Info(index);
        var revision = _hulls.Revision(index);
        if (revision == info.FittedRevision)
            return;

        info.FittedRevision = revision;
        var hadShield = _hulls.Flight(index).ShieldCapacity > 0f;
        _designs[info.Design].Figures(_hulls.BlockStates.Sums(info.BlockState), out _hulls.Flight(index), out _);
        ref var flight = ref _hulls.Flight(index);
        ref var state = ref _hulls.State(index);

        // Losing the last emitter takes an online shield away: a change viewers see, with no hit of its own.
        if (hadShield && flight.ShieldCapacity <= 0f && !state.ShieldOffline)
            state.ShieldChangeTicks = 0;
        state.Shield = Math.Min(state.Shield, flight.ShieldCapacity);
        state.Energy = Math.Min(state.Energy, flight.EnergyStorage);
    }
}
