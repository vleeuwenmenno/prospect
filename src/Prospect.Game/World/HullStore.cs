using System.Runtime.InteropServices;

namespace Prospect.Game.World;

/// <summary>Names a hull. The generation makes a handle to a destroyed hull detectably stale.</summary>
public readonly record struct HullHandle(int Index, int Generation)
{
    public bool IsValid => Generation != 0;

    /// <summary>The handle as one number, for the wire.</summary>
    public uint Packed => (uint)Generation << 16 | (uint)Index & 0xFFFF;
}

/// <summary>Per-hull data that is not part of its motion.</summary>
public struct HullInfo
{
    /// <summary>Authoritative visual damage, independent of physical block loss.</summary>
    public VisualDamageState Visual;

    public int Generation;
    public bool Alive;

    /// <summary>The player who owns the hull.</summary>
    public uint Owner;

    /// <summary>Which design the hull is built to.</summary>
    public byte Design;

    /// <summary>True while a player flies the hull directly; the rest are flown by the server under their orders.</summary>
    public bool Piloted;
    /// <summary>Prediction has a complete authoritative health baseline and may speculate on an isolated copy.</summary>
    public bool PredictDamage;

    /// <summary>True for a temporary escape drone, even after editing gives it a new design.</summary>
    public bool EscapeDrone;

    /// <summary>
    /// The hull's block state in the store's <see cref="BlockStateStore"/>: the blocks it still has, how many it
    /// has lost, the sums its figures are read from and, where the hull is decided, each block's hit points.
    /// </summary>
    public BlockStateHandle BlockState;

    /// <summary>
    /// True if the store took the block state when it created the hull and gives it back when the hull goes.
    /// False for a hull created over a state someone else keeps, which outlives the hull's time in this store.
    /// </summary>
    public bool OwnsBlockState;

    /// <summary>
    /// The block state's revision the hull's flight figures were last worked out for. When the state's revision
    /// has moved past it the hull has lost blocks since, and <see cref="HullField"/> fits the figures again.
    /// </summary>
    public uint FittedRevision;
    public HullOrder Order;

    /// <summary>Where the hull's power went in its last step, for the pilot's readout; never sent or saved.</summary>
    public PowerFlow Power;

    /// <summary>The owner's order counter at the time <see cref="Order"/> was set.</summary>
    public ushort OrderId;

    /// <summary>
    /// The tick in which the hull last struck another hull or was struck by one. A hull that was just knocked
    /// about is not where anyone would expect it, so the server tells of it at once.
    /// </summary>
    public long ContactTick;

    /// <summary>
    /// The tick in which the hull last took damage: an impact its shield paid for or its blocks took. A contact
    /// too slow to harm does not count. <see cref="long.MinValue"/> for a hull that never has; compare it, never
    /// subtract from it. Only a store that decides its hulls keeps it meaningfully.
    /// </summary>
    public long DamageTick;

    /// <summary>The round of the <see cref="HullField"/> in which the hull last stepped; zero if it never has.</summary>
    public long Round;

    /// <summary>
    /// How far the hull has come in the round <see cref="FlownRound"/>, by its own flight and by what struck it,
    /// whichever came first.
    /// </summary>
    public System.Numerics.Vector3 Flown;

    /// <summary>The round of the <see cref="HullField"/> that <see cref="Flown"/> was measured in.</summary>
    public long FlownRound;

    /// <summary>
    /// The hull's durable name on the server that decides it: given once when the hull comes into the world, never
    /// reused, kept across a save and across a repair or an edit, which replace the hull and its handle. Zero on a
    /// client, which knows hulls by handle only.
    /// </summary>
    public ulong Entity;
}

/// <summary>
/// Hull storage: parallel arrays in unmanaged memory, addressed by handle. The motion states sit together so the
/// simulation walks them in one contiguous pass; the garbage collector never sees any of it. What a hull is made
/// of sits apart, in a <see cref="BlockStateStore"/>, sized to its design. A store that decides its hulls has one
/// of its own and each hull's state lives and dies with the hull. A store of hulls that are only predicted is
/// given the store of whoever knows those hulls, and its hulls are created over states kept there.
/// </summary>
public sealed unsafe class HullStore : IDisposable
{
    public const int MaxHulls = 1 << 16;

    private HullState* _states;
    private HullInfo* _infos;
    private FlightParams* _flights;
    private readonly BlockStateStore _blockStates;
    private readonly bool _ownsBlockStates;
    private int _capacity;
    private int _highWater;
    private int _count;
    private long _blockTotal;

    /// <summary>
    /// A store with room for <paramref name="capacity"/> hulls before it grows. With no
    /// <paramref name="blockStates"/> it keeps the block states of its hulls itself; given one, it shares that
    /// store, which its owner disposes.
    /// </summary>
    public HullStore(int capacity = 256, BlockStateStore? blockStates = null)
    {
        _ownsBlockStates = blockStates is null;
        _blockStates = blockStates ?? new BlockStateStore(capacity);
        Resize(capacity);
    }

    /// <summary>Where the block states of the store's hulls are kept.</summary>
    public BlockStateStore BlockStates => _blockStates;

    /// <summary>Live hulls.</summary>
    public int Count => _count;

    /// <summary>One past the highest slot ever used; iterate 0..Slots and skip slots that are not alive.</summary>
    public int Slots => _highWater;

    /// <summary>
    /// The blocks of every live hull's design added up, lost blocks included: what a save of the store must have
    /// room for. Kept as hulls come and go, so asking costs nothing.
    /// </summary>
    public long BlockTotal => _blockTotal;

    /// <summary>
    /// Creates an intact hull of a design at a position, at rest, with full shield and battery. Its block state,
    /// with every block's hit points and the design's intact sums, is taken here and given back when the hull is
    /// destroyed.
    /// </summary>
    public HullHandle Create(uint owner, byte designIndex, HullDesign design, Double3 position) =>
        Restore(owner, designIndex, design, HullState.Spawn(position, design.Flight));

    /// <summary>
    /// Creates a hull of a design with the motion a save holds, over an intact block state of its own. The
    /// caller brings the block state to what was saved with <see cref="BlockStateStore.TryRestore"/> and reads the
    /// flight figures again with <see cref="HullField.RestoreFigures"/>; nothing here judges the saved motion.
    /// </summary>
    public HullHandle Restore(uint owner, byte designIndex, HullDesign design, in HullState state)
    {
        var handle = Create(owner, designIndex, state);
        _flights[handle.Index] = design.Flight;
        ref var info = ref _infos[handle.Index];
        info.BlockState = _blockStates.Create(design, hitPoints: true);
        info.OwnsBlockState = true;
        _blockTotal += design.BlockCount;
        return handle;
    }

    /// <summary>
    /// Creates a hull of a design at a position over a block state that is kept elsewhere in
    /// <see cref="BlockStates"/>: the hull has the blocks that state says, and destroying the hull or clearing
    /// the store leaves the state as it is. Its flight figures start as the intact design's; whoever keeps the
    /// state knows the figures that go with it and sets them.
    /// </summary>
    public HullHandle Create(uint owner, byte designIndex, HullDesign design, Double3 position, BlockStateHandle blockState)
    {
        if (_blockStates.Blocks(blockState) != design.BlockCount)
            throw new ArgumentException("The block state was taken for another design.", nameof(blockState));
        var handle = Create(owner, designIndex, HullState.Spawn(position, design.Flight));
        _flights[handle.Index] = design.Flight;
        ref var info = ref _infos[handle.Index];
        info.BlockState = blockState;
        info.FittedRevision = _blockStates.Revision(blockState);
        _blockTotal += design.BlockCount;
        return handle;
    }

    /// <summary>The hull's current flight figures, which fall as it loses blocks.</summary>
    public ref FlightParams Flight(int index) => ref _flights[index];

    /// <summary>
    /// The blocks the hull still has, as a <see cref="BlockSet"/> over its stored state. A block is taken through
    /// <see cref="BlockStates"/>, which keeps the count of losses and the sums in step.
    /// </summary>
    public ReadOnlySpan<ulong> Blocks(int index) => _blockStates.Alive(_infos[index].BlockState);

    /// <summary>Remaining hull points of each of the hull's blocks; nothing if its block state has none.</summary>
    public ReadOnlySpan<float> BlockHp(int index) => _blockStates.HitPoints(_infos[index].BlockState);

    /// <summary>How many blocks the hull has lost; see <see cref="BlockStateStore.Revision"/>.</summary>
    public uint Revision(int index) => _blockStates.Revision(_infos[index].BlockState);

    private HullHandle Create(uint owner, byte design, in HullState state)
    {
        var index = -1;
        for (var i = 0; i < _highWater; i++)
        {
            if (_infos[i].Alive)
                continue;
            index = i;
            break;
        }

        if (index < 0)
        {
            if (_highWater == _capacity)
            {
                if (_capacity >= MaxHulls)
                    throw new InvalidOperationException("Hull limit reached");
                Resize(Math.Min(_capacity * 2, MaxHulls));
            }

            index = _highWater++;
        }

        ref var info = ref _infos[index];
        var generation = info.Generation >= ushort.MaxValue ? 1 : info.Generation + 1;
        info = new HullInfo
        {
            Generation = generation,
            Alive = true,
            Owner = owner,
            Design = design,
            ContactTick = long.MinValue,
            DamageTick = long.MinValue,
        };
        _states[index] = state;
        _count++;
        return new HullHandle(index, generation);
    }

    /// <summary>Removes every hull and keeps the memory, for a store that is filled afresh each time it is used.</summary>
    public void Clear()
    {
        for (var i = 0; i < _highWater; i++)
        {
            if (_infos[i].Alive)
                Remove(ref _infos[i]);
        }

        _highWater = 0;
        _count = 0;
        _blockTotal = 0;
    }

    /// <summary>Removes a hull. Its block state goes with it if the store took it; a stale handle does nothing.</summary>
    public void Destroy(HullHandle handle)
    {
        if (!IsAlive(handle))
            return;
        Remove(ref _infos[handle.Index]);
        _count--;
    }

    private void Remove(ref HullInfo info)
    {
        info.Alive = false;

        // A hull over a state kept elsewhere may outlive it; only a store that decides its hulls reads the total.
        if (_blockStates.IsLive(info.BlockState))
            _blockTotal -= _blockStates.Blocks(info.BlockState);
        if (info.OwnsBlockState)
            _blockStates.Release(info.BlockState);
        info.BlockState = default;
        info.OwnsBlockState = false;
    }

    public bool IsAlive(HullHandle handle) =>
        (uint)handle.Index < (uint)_highWater && _infos[handle.Index].Alive && _infos[handle.Index].Generation == handle.Generation;

    public bool IsAliveAt(int index) => _infos[index].Alive;

    public HullHandle HandleAt(int index) => new(index, _infos[index].Generation);

    public ref HullState State(int index) => ref _states[index];

    public ref HullInfo Info(int index) => ref _infos[index];

    public void Dispose()
    {
        if (_infos == null)
            return;

        // A block state store that is shared outlives this one: what these hulls took from it goes back, and
        // what they only borrowed stays with its owner.
        if (!_ownsBlockStates)
            Clear();
        NativeMemory.Free(_states);
        NativeMemory.Free(_infos);
        NativeMemory.Free(_flights);
        _states = null;
        _infos = null;
        _flights = null;
        if (_ownsBlockStates)
            _blockStates.Dispose();
    }

    private void Resize(int capacity)
    {
        _states = (HullState*)NativeMemory.Realloc(_states, (nuint)(capacity * sizeof(HullState)));
        _infos = (HullInfo*)NativeMemory.Realloc(_infos, (nuint)(capacity * sizeof(HullInfo)));
        _flights = (FlightParams*)NativeMemory.Realloc(_flights, (nuint)(capacity * sizeof(FlightParams)));
        new Span<HullInfo>(_infos + _capacity, capacity - _capacity).Clear();
        _capacity = capacity;
    }
}
