using System.Numerics;
using System.Runtime.InteropServices;

namespace Prospect.Game.World;

/// <summary>Names one hull's block state in a <see cref="BlockStateStore"/>. The generation makes a released handle detectably stale.</summary>
public readonly record struct BlockStateHandle(int Index, int Generation)
{
    /// <summary>False for the default handle, which names nothing.</summary>
    public bool IsValid => Generation != 0;
}

/// <summary>
/// What a hull adds to its design: which blocks it still has, how many it has lost, the running sums its
/// figures are read from and, where the hull is decided, the hit points each block has left and their total.
/// Unmanaged memory addressed by handle, sized to the hull's design.
/// <para>
/// A block state lasts as long as its hull is known, and one party owns it. On the server that is the
/// <see cref="HullStore"/> the hull lives in: it takes the state when the hull is created and gives it back when
/// the hull is destroyed. On a client it is the client's world, which keeps the state of every hull it has heard
/// of across snapshots; the store of hulls it predicts with is emptied and filled again at every snapshot and
/// only borrows those states. So a <see cref="HullInfo"/> carries the handle and one flag saying whether its
/// store gives the state back. The network replica has a separate store so incomplete recovery cannot mutate
/// a published or predicted hull; validated losses are copied to this state only with a matching pose.
/// </para>
/// <para>
/// A state is only read from outside. A block goes by <see cref="Lose"/>, or by <see cref="Hit"/> where there
/// are hit points, and that one call takes the block from the set, counts the loss and brings the sums and the
/// hit total up to date, so the four can never disagree.
/// </para>
/// <para>
/// Memory is taken when a state is created and kept for the next one when it is released; losing a block takes
/// none. The sums start as a copy of the design's intact sums, so creating a state looks at no block. A state
/// never moves while it is in use, so spans over it stay good when other states come and go: the memory is cut
/// from chunks that are never reallocated, in sizes of powers of two with a free list each, and a state too big
/// for a chunk gets a block of its own that is kept for the next big one. The table of handles is separate and
/// may grow when a state is created. Used from one thread.
/// </para>
/// </summary>
public sealed unsafe class BlockStateStore : IDisposable
{
    /// <summary>The smallest piece handed out is 256 bytes: the header, one word of blocks and one node of sums.</summary>
    private const int SmallestShift = 8;

    /// <summary>A chunk is 64 KiB, and is also the largest piece cut from one.</summary>
    private const int ChunkShift = 16;
    private const int ChunkBytes = 1 << ChunkShift;
    private const int Classes = ChunkShift - SmallestShift + 1;

    /// <summary>
    /// What sits in front of a state's memory; thirty-two bytes, so what follows stays aligned. After it come the
    /// alive words, then the sums, then the hit points if the state has them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct Header
    {
        public int Blocks;
        public uint Revision;
        public int Nodes;
        public int CoresLost;

        /// <summary>The hit points of every block left, added up; kept right from the blocks that are touched.</summary>
        public double HitTotal;
        public int HasHitPoints;
    }

    private struct Slot
    {
        public byte* Memory;

        /// <summary>The design the state was taken for: it knows what each block adds to the sums.</summary>
        public HullDesign? Design;
        public int Generation;

        /// <summary>The size class the memory came from, or -1 - the index of its own block in the big ones.</summary>
        public int Source;
        public bool Used;
    }

    /// <summary>A block of memory taken for one state too big for a chunk.</summary>
    private struct Big
    {
        public byte* Memory;
        public nuint Bytes;
        public bool Used;
    }

    private readonly nint[] _free = new nint[Classes];
    private nint[] _chunks = new nint[8];
    private int _chunkCount;
    private Big[] _big = new Big[4];
    private int _bigCount;
    private Slot[] _slots;
    private int _highWater;
    private int _count;

    /// <summary>A store with room for <paramref name="capacity"/> handles before its table grows.</summary>
    public BlockStateStore(int capacity = 64)
    {
        _slots = new Slot[Math.Max(capacity, 1)];
    }

    /// <summary>Block states in use.</summary>
    public int Count => _count;

    /// <summary>The bytes the state of one hull of a design takes, before the store rounds it up to a piece it hands out.</summary>
    public static long BytesFor(HullDesign design, bool hitPoints) =>
        sizeof(Header) + (long)BlockSet.WordCount(design.BlockCount) * sizeof(ulong) + design.Sums.StorageBytes + (hitPoints ? (long)design.BlockCount * sizeof(float) : 0) + (long)design.BlockCount * sizeof(int) + (long)design.Collision.IntactCounts.Length * sizeof(int);

    /// <summary>
    /// Takes the block state of an intact hull of a design: every block alive, none lost, and the design's
    /// intact sums, copied. With <paramref name="hitPoints"/> it also holds each block's hit points, full; a
    /// predicting client asks for none.
    /// </summary>
    public BlockStateHandle Create(HullDesign design, bool hitPoints)
    {
        var blocks = design.BlockCount;
        var words = BlockSet.WordCount(blocks);
        var bytes = (nuint)BytesFor(design, hitPoints);

        var index = -1;
        for (var i = 0; i < _highWater; i++)
        {
            if (_slots[i].Used)
                continue;
            index = i;
            break;
        }

        if (index < 0)
        {
            if (_highWater == _slots.Length)
                Array.Resize(ref _slots, _slots.Length * 2);
            index = _highWater++;
        }

        ref var slot = ref _slots[index];
        slot.Memory = Take(bytes, out slot.Source);
        slot.Design = design;
        slot.Generation = slot.Generation == int.MaxValue ? 1 : slot.Generation + 1;
        slot.Used = true;
        _count++;

        var header = (Header*)slot.Memory;
        *header = new Header { Blocks = blocks, Nodes = design.Sums.NodeCount, HasHitPoints = hitPoints ? 1 : 0 };
        BlockSet.Fill(new Span<ulong>(header + 1, words), blocks);
        design.Sums.Intact.CopyTo(SumsOf(header));
        design.Collision.IntactCounts.CopyTo(CollisionCountsOf(header));
        if (hitPoints)
        {
            design.IntactHitPoints.CopyTo(HitPointsOf(header));
            header->HitTotal = design.IntactHitTotal;
        }

        return new BlockStateHandle(index, slot.Generation);
    }

    /// <summary>Gives a block state back; its memory serves the next one of its size. A stale handle does nothing.</summary>
    public void Release(BlockStateHandle handle)
    {
        if (!IsLive(handle))
            return;

        ref var slot = ref _slots[handle.Index];
        if (slot.Source >= 0)
        {
            *(nint*)slot.Memory = _free[slot.Source];
            _free[slot.Source] = (nint)slot.Memory;
        }
        else
        {
            _big[-1 - slot.Source].Used = false;
        }

        slot.Memory = null;
        slot.Design = null;
        slot.Used = false;
        _count--;
    }

    /// <summary>True while the handle names a block state that has not been released.</summary>
    public bool IsLive(BlockStateHandle handle) =>
        (uint)handle.Index < (uint)_highWater && _slots[handle.Index].Used && _slots[handle.Index].Generation == handle.Generation;

    /// <summary>The blocks of the design the state was taken for.</summary>
    public int Blocks(BlockStateHandle handle) => Find(handle)->Blocks;

    /// <summary>The design the state was taken for.</summary>
    public HullDesign Design(BlockStateHandle handle)
    {
        Find(handle);
        return _slots[handle.Index].Design!;
    }

    /// <summary>The blocks the hull still has, as a <see cref="BlockSet"/>. The span stays good until the state is released.</summary>
    public ReadOnlySpan<ulong> Alive(BlockStateHandle handle) => AliveOf(Find(handle));

    /// <summary>
    /// Hull points left per block, or nothing for a state that was taken without them. A block that is gone has
    /// none. The span stays good until the state is released.
    /// </summary>
    public ReadOnlySpan<float> HitPoints(BlockStateHandle handle)
    {
        var header = Find(handle);
        return header->HasHitPoints != 0 ? HitPointsOf(header) : default;
    }

    /// <summary>
    /// The hit points of every block the hull has left, added up; zero for a state taken without hit points. It
    /// is kept right from the blocks that are touched, never found by adding every block up, and is exactly zero
    /// once the last block is gone.
    /// </summary>
    public double HitTotal(BlockStateHandle handle) => Find(handle)->HitTotal;

    /// <summary>
    /// How many blocks the hull has lost: a count that only grows, and so names every state the hull has been in.
    /// It is what says a loss happened; nothing compares block sets to find one.
    /// </summary>
    public uint Revision(BlockStateHandle handle) => Find(handle)->Revision;

    /// <summary>How many of the blocks whose loss destroys the hull are gone.</summary>
    public int CoresLost(BlockStateHandle handle) => Find(handle)->CoresLost;

    /// <summary>
    /// The hull's running sums, laid out by its design's <see cref="HullDesign.Sums"/>; read the hull's figures
    /// off them with <see cref="HullDesign.Figures"/>. The span stays good until the state is released.
    /// </summary>
    public ReadOnlySpan<HullSum> Sums(BlockStateHandle handle) => SumsOf(Find(handle));

    /// <summary>
    /// Takes a block from the hull: out of the set, counted as a loss, its hit points gone from the total and the
    /// sums brought up to date, all here. Returns false, and changes nothing, if the block was gone already.
    /// </summary>
    public bool Lose(BlockStateHandle handle, int block)
    {
        var header = Find(handle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)block, (uint)header->Blocks);
        var alive = AliveOf(header);
        if (!BlockSet.Has(alive, block))
            return false;

        var design = _slots[handle.Index].Design!;
        BlockSet.Remove(alive, block);
        LossesOf(header)[(int)header->Revision++] = block;
        if (BlockSet.Has(design.CoreSet, block))
            header->CoresLost++;

        if (header->HasHitPoints != 0)
        {
            // What is left is never negative, and nothing is exactly nothing: no rounding survives the last block.
            ref var points = ref HitPointsOf(header)[block];
            header->HitTotal = header->Revision == (uint)header->Blocks ? 0.0 : Math.Max(0.0, header->HitTotal - points);
            points = 0f;
        }

        design.Sums.Lose(SumsOf(header), alive, block);
        design.Collision.Lose(CollisionCountsOf(header), block);
        return true;
    }

    /// <summary>
    /// Takes hit points from a block, and the block from the hull if it has none left. The total falls by what
    /// the block lost. Returns true if the block was lost by this. Only for a state that has hit points.
    /// </summary>
    public bool Hit(BlockStateHandle handle, int block, float amount)
    {
        var header = Find(handle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)block, (uint)header->Blocks);
        if (header->HasHitPoints == 0)
            throw new InvalidOperationException("The block state was taken without hit points.");

        ref var points = ref HitPointsOf(header)[block];
        var before = points;
        points = Math.Max(0f, before - amount);
        header->HitTotal = Math.Max(0.0, header->HitTotal - ((double)before - points));
        return points <= 0 && Lose(handle, block);
    }

    /// <summary>
    /// Sets what a block the hull still has has left, and moves the total by the difference. Only for a state
    /// that has hit points; a block that is gone has none to set.
    /// </summary>
    public void SetHitPoints(BlockStateHandle handle, int block, float points)
    {
        var header = Find(handle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)block, (uint)header->Blocks);
        ArgumentOutOfRangeException.ThrowIfNegative(points);
        if (header->HasHitPoints == 0 || !BlockSet.Has(AliveOf(header), block))
            throw new InvalidOperationException("The block is gone, or the block state was taken without hit points.");

        ref var kept = ref HitPointsOf(header)[block];
        header->HitTotal = Math.Max(0.0, header->HitTotal + ((double)points - kept));
        kept = points;
    }

    /// <summary>Block ids in the order they were lost, indexed by the revision before each loss.</summary>
    public ReadOnlySpan<int> Losses(BlockStateHandle handle)
    {
        var header = Find(handle);
        return LossesOf(header)[..(int)header->Revision];
    }

    private static Span<int> LossesOf(Header* header) => new(
        (byte*)(header + 1) + BlockSet.WordCount(header->Blocks) * sizeof(ulong) + (nint)header->Nodes * sizeof(HullSum)
        + (header->HasHitPoints != 0 ? (nint)header->Blocks * sizeof(float) : 0), header->Blocks);

    internal ReadOnlySpan<int> CollisionCounts(BlockStateHandle handle) => CollisionCountsOf(Find(handle));

    private static Span<int> CollisionCountsOf(Header* header) => new(
        (byte*)(header + 1) + BlockSet.WordCount(header->Blocks) * sizeof(ulong) + (nint)header->Nodes * sizeof(HullSum)
        + (header->HasHitPoints != 0 ? (nint)header->Blocks * sizeof(float) : 0) + (nint)header->Blocks * sizeof(int), header->Blocks * 2 - 1);

    private static Span<ulong> AliveOf(Header* header) => new(header + 1, BlockSet.WordCount(header->Blocks));

    private static Span<HullSum> SumsOf(Header* header) => new((byte*)(header + 1) + BlockSet.WordCount(header->Blocks) * sizeof(ulong), header->Nodes);

    private static Span<float> HitPointsOf(Header* header) =>
        new((byte*)(header + 1) + BlockSet.WordCount(header->Blocks) * sizeof(ulong) + (nint)header->Nodes * sizeof(HullSum), header->Blocks);

    /// <summary>Frees every chunk. Spans and handles from the store are dead after this.</summary>
    public void Dispose()
    {
        for (var i = 0; i < _chunkCount; i++)
            NativeMemory.Free((void*)_chunks[i]);
        for (var i = 0; i < _bigCount; i++)
            NativeMemory.Free(_big[i].Memory);
        _chunkCount = 0;
        _bigCount = 0;
        _highWater = 0;
        _count = 0;
        Array.Clear(_free);
        Array.Clear(_slots);
    }

    private Header* Find(BlockStateHandle handle)
    {
        if (!IsLive(handle))
            throw new InvalidOperationException("The block state was released, or never taken.");
        return (Header*)_slots[handle.Index].Memory;
    }

    /// <summary>Memory for one state: a free piece of its size, a piece of a new chunk, or a block of its own.</summary>
    private byte* Take(nuint bytes, out int source)
    {
        if (bytes > ChunkBytes)
            return TakeBig(bytes, out source);

        var shift = Math.Max(SmallestShift, BitOperations.Log2((uint)bytes - 1) + 1);
        source = shift - SmallestShift;
        if (_free[source] == 0)
        {
            // A new chunk, cut whole into pieces of this size. Each free piece holds the address of the next.
            var chunk = (byte*)NativeMemory.Alloc(ChunkBytes);
            if (_chunkCount == _chunks.Length)
                Array.Resize(ref _chunks, _chunks.Length * 2);
            _chunks[_chunkCount++] = (nint)chunk;
            for (var at = ChunkBytes - (1 << shift); at >= 0; at -= 1 << shift)
            {
                *(nint*)(chunk + at) = _free[source];
                _free[source] = (nint)(chunk + at);
            }
        }

        var piece = (byte*)_free[source];
        _free[source] = *(nint*)piece;
        return piece;
    }

    private byte* TakeBig(nuint bytes, out int source)
    {
        // The smallest block left over from an earlier big state that still fits, so a station's memory serves
        // the next station and not a hull a tenth of its size.
        var best = -1;
        for (var i = 0; i < _bigCount; i++)
        {
            if (!_big[i].Used && _big[i].Bytes >= bytes && (best < 0 || _big[i].Bytes < _big[best].Bytes))
                best = i;
        }

        if (best < 0)
        {
            if (_bigCount == _big.Length)
                Array.Resize(ref _big, _big.Length * 2);
            best = _bigCount++;
            var rounded = (bytes + ChunkBytes - 1) & ~(nuint)(ChunkBytes - 1);
            _big[best] = new Big { Memory = (byte*)NativeMemory.Alloc(rounded), Bytes = rounded };
        }

        _big[best].Used = true;
        source = -1 - best;
        return _big[best].Memory;
    }
}
