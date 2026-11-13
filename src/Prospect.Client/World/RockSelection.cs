using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Fixed storage for the visible rocks left after hull instances are reserved.</summary>
internal sealed class RockSelection(int capacity)
{
    internal readonly record struct Entry(RockRef Id, int Slot, float Pixels, float Priority);
    private readonly Entry[] _items = new Entry[capacity];
    private int _count;
    private int _limit;
    private bool _heap;
    internal ReadOnlySpan<Entry> Items => _items.AsSpan(0, _count);
    internal void Begin(int limit)
    {
        _count = 0;
        _heap = false;
        _limit = Math.Clamp(limit, 0, _items.Length);
    }
    internal void Offer(RockRef id, int slot, float pixels, bool retained = false)
    {
        if (_limit == 0) return;
        // A two-percent radius preference prevents tiny camera movements trading equally useful rocks each frame.
        var entry = new Entry(id, slot, pixels, pixels * (retained ? 1.02f : 1f));
        if (_count < _limit)
        {
            _items[_count++] = entry;
            return;
        }
        if (!_heap)
        {
            // Most views fit. Build the heap only when a frame actually exceeds its budget.
            for (var parent = _count / 2 - 1; parent >= 0; parent--) Sift(parent, _items[parent]);
            _heap = true;
        }
        if (Compare(entry, _items[0]) <= 0) return;
        Sift(0, entry);
    }

    private void Sift(int hole, Entry entry)
    {
        while (hole * 2 + 1 < _count)
        {
            var child = hole * 2 + 1;
            if (child + 1 < _count && Compare(_items[child + 1], _items[child]) < 0) child++;
            if (Compare(entry, _items[child]) <= 0) break;
            _items[hole] = _items[child];
            hole = child;
        }
        _items[hole] = entry;
    }

    private static int Compare(Entry a, Entry b)
    {
        var order = a.Priority.CompareTo(b.Priority);
        if (order != 0) return order;
        order = b.Id.Sector.X.CompareTo(a.Id.Sector.X);
        if (order != 0) return order;
        order = b.Id.Sector.Y.CompareTo(a.Id.Sector.Y);
        if (order != 0) return order;
        order = b.Id.Sector.Z.CompareTo(a.Id.Sector.Z);
        return order != 0 ? order : b.Id.Index.CompareTo(a.Id.Index);
    }
}
