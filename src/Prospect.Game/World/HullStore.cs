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
    public int Generation;
    public bool Alive;

    /// <summary>The player who owns the hull.</summary>
    public uint Owner;

    /// <summary>Which design the hull is built to.</summary>
    public byte Design;

    /// <summary>True while a player flies the hull directly; the rest are flown by the server under their orders.</summary>
    public bool Piloted;
    public HullOrder Order;

    /// <summary>The owner's order counter at the time <see cref="Order"/> was set.</summary>
    public ushort OrderId;
}

/// <summary>
/// Hull storage: parallel arrays in unmanaged memory, addressed by handle. The motion states sit together so the
/// simulation walks them in one contiguous pass; the garbage collector never sees any of it.
/// </summary>
public sealed unsafe class HullStore : IDisposable
{
    public const int MaxHulls = 1 << 16;

    private HullState* _states;
    private HullInfo* _infos;
    private int _capacity;
    private int _highWater;
    private int _count;

    public HullStore(int capacity = 256)
    {
        Resize(capacity);
    }

    /// <summary>Live hulls.</summary>
    public int Count => _count;

    /// <summary>One past the highest slot ever used; iterate 0..Slots and skip slots that are not alive.</summary>
    public int Slots => _highWater;

    public HullHandle Create(uint owner, byte design, in HullState state)
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
        };
        _states[index] = state;
        _count++;
        return new HullHandle(index, generation);
    }

    public void Destroy(HullHandle handle)
    {
        if (!IsAlive(handle))
            return;
        _infos[handle.Index].Alive = false;
        _count--;
    }

    public bool IsAlive(HullHandle handle) =>
        (uint)handle.Index < (uint)_highWater && _infos[handle.Index].Alive && _infos[handle.Index].Generation == handle.Generation;

    public bool IsAliveAt(int index) => _infos[index].Alive;

    public HullHandle HandleAt(int index) => new(index, _infos[index].Generation);

    public ref HullState State(int index) => ref _states[index];

    public ref HullInfo Info(int index) => ref _infos[index];

    public void Dispose()
    {
        NativeMemory.Free(_states);
        NativeMemory.Free(_infos);
        _states = null;
        _infos = null;
    }

    private void Resize(int capacity)
    {
        _states = (HullState*)NativeMemory.Realloc(_states, (nuint)(capacity * sizeof(HullState)));
        _infos = (HullInfo*)NativeMemory.Realloc(_infos, (nuint)(capacity * sizeof(HullInfo)));
        new Span<HullInfo>(_infos + _capacity, capacity - _capacity).Clear();
        _capacity = capacity;
    }
}
