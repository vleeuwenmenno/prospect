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

    /// <summary>The change to its velocity, and to its angular velocity about its own axes.</summary>
    public Vector3 Velocity;
    public Vector3 AngularVelocity;

    /// <summary>The blocks that were touched, one bit per block, and the damage they share after the shield.</summary>
    public ulong Touching;
    public float Damage;
}

/// <summary>
/// The hulls that can run into each other, and the one step that moves any of them. The server's field is its
/// hull store. A predicting client keeps a field of its own hull and the hulls near it as it last heard of them,
/// and steps that with the same code, so what it shows is what the server will most likely decide. The client's
/// field is not authoritative: it does not know how many hull points each block has left, so there damage only
/// shows in a hull's totals and no block is lost until the server says so. Used from one thread.
/// </summary>
public sealed class HullField(HullStore hulls, HullDesign[] designs, bool authoritative = true)
{
    /// <summary>The most hulls one hull can strike in a single step; any beyond that are struck a step later.</summary>
    private const int MaxContacts = 64;

    private readonly HullImpulse[] _impulses = new HullImpulse[MaxContacts];
    private readonly int[] _lost = new int[MaxContacts];
    private int _impulseCount;
    private int _lostCount;
    private bool _collecting;

    public HullStore Hulls => hulls;

    /// <summary>Every hull design, indexed by the design number the stored hulls carry.</summary>
    public HullDesign[] Designs => designs;

    /// <summary>The slot of the hull being stepped, which does not run into itself, or -1 if it is not in the store.</summary>
    internal int Self { get; private set; } = -1;

    /// <summary>True when a stepping hull must stop striking others because their share can no longer be recorded.</summary>
    internal bool IsFull => _collecting && _impulseCount == _impulses.Length;

    /// <summary>
    /// The slots of the hulls the last <see cref="Step"/> destroyed by running the stepped hull into them. They are
    /// still in the store; removing them is up to the caller.
    /// </summary>
    public ReadOnlySpan<int> Lost => _lost.AsSpan(0, _lostCount);

    /// <summary>Records what a contact does to the hull that was struck. Dropped when nobody is collecting.</summary>
    internal void Add(in HullImpulse impulse)
    {
        if (_collecting && _impulseCount < _impulses.Length)
            _impulses[_impulseCount++] = impulse;
    }

    /// <summary>
    /// Advances the stored hull in a slot one tick: it collides block by block with asteroids and with the other
    /// stored hulls, and every hull it strikes takes its share of the impulse and its damage before this returns,
    /// so a pair is settled once however often either of them is stepped. A hull that loses blocks has its flight
    /// figures worked out again from what is left. Returns true if the stepped hull was destroyed; the others it
    /// destroyed are in <see cref="Lost"/>.
    /// </summary>
    public bool Step(int index, in FlightInput input, RockField? rocks = null, double tick = 0, RockImpulses? impulses = null)
    {
        _impulseCount = 0;
        _lostCount = 0;
        _collecting = true;
        Self = index;

        ref var info = ref hulls.Info(index);
        var body = Body(index);
        var destroyed = HullSim.Step(ref hulls.State(index), ref info.Order, input, hulls.Flight(index), ref body, rocks, tick, impulses, this);
        Refit(index, body.Alive);

        _collecting = false;
        Self = -1;

        if (_impulseCount > 0)
            info.ContactTick = (long)tick;

        for (var i = 0; i < _impulseCount; i++)
        {
            ref var impulse = ref _impulses[i];
            ref var state = ref hulls.State(impulse.Hull);
            hulls.Info(impulse.Hull).ContactTick = (long)tick;
            state.Velocity += impulse.Velocity;
            state.AngularVelocity += impulse.AngularVelocity;
            if (impulse.Damage <= 0)
                continue;

            var struck = Body(impulse.Hull);
            if (HullSim.Damage(ref state, hulls.Flight(impulse.Hull), ref struck, impulse.Touching, BitOperations.PopCount(impulse.Touching), impulse.Damage))
                _lost[_lostCount++] = impulse.Hull;
            Refit(impulse.Hull, struck.Alive);
        }

        return destroyed;
    }

    private HullBody Body(int index)
    {
        ref var info = ref hulls.Info(index);
        return new HullBody
        {
            Design = designs[info.Design],
            Alive = info.Blocks,
            BlockHp = authoritative ? hulls.BlockHp(index) : default,
        };
    }

    /// <summary>Records the blocks a hull has left and, if it lost any, works its flight figures out again.</summary>
    private void Refit(int index, ulong alive)
    {
        ref var info = ref hulls.Info(index);
        if (alive == info.Blocks)
            return;

        info.Blocks = alive;
        designs[info.Design].Evaluate(alive, out hulls.Flight(index), out _);
        ref var flight = ref hulls.Flight(index);
        ref var state = ref hulls.State(index);
        state.Shield = Math.Min(state.Shield, flight.ShieldCapacity);
        state.Energy = Math.Min(state.Energy, flight.EnergyStorage);
    }
}
