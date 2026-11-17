namespace Prospect.Game.World.RockEditing;

/// <summary>
/// The edits of every rock of a world, in the order they were made: the truth that the wire and the save carry, and
/// that bricks and meshes are rebuilt from. It only grows. One thread appends; any thread may read what is below the
/// counts it has read, since an entry never changes once its count is published. Nothing is allocated after
/// construction.
/// </summary>
public sealed class RockEditLog
{
    private readonly RockEditRules _rules;
    private readonly Dictionary<RockRef, int> _slots;
    private readonly RockRef[] _rocks;
    private readonly int[] _first;
    private readonly int[] _last;
    private readonly int[] _counts;
    private readonly RockScrape[] _edits;
    private readonly int[] _editRock;
    private readonly int[] _editNext;
    private int _rockCount;
    private int _editCount;

    /// <summary>Reserves room for everything the rules allow a world to hold.</summary>
    public RockEditLog(RockEditRules rules)
    {
        _rules = rules;
        _slots = new Dictionary<RockRef, int>(rules.MaxEditedRocks, EqualityComparer<RockRef>.Default);
        _rocks = new RockRef[rules.MaxEditedRocks];
        _first = new int[rules.MaxEditedRocks];
        _last = new int[rules.MaxEditedRocks];
        _counts = new int[rules.MaxEditedRocks];
        _edits = new RockScrape[rules.MaxEdits];
        _editRock = new int[rules.MaxEdits];
        _editNext = new int[rules.MaxEdits];
    }

    /// <summary>The rules the log's limits come from.</summary>
    public RockEditRules Rules => _rules;

    /// <summary>Edits made so far, over all rocks. Safe to read from any thread.</summary>
    public int Count => Volatile.Read(ref _editCount);

    /// <summary>Rocks that hold an edit. A rock's slot is its place in the order rocks were first edited. Safe to read from any thread.</summary>
    public int RockCount => Volatile.Read(ref _rockCount);

    /// <summary>The rock of a slot below a <see cref="RockCount"/> that was read.</summary>
    public RockRef RockAt(int slot) => _rocks[slot];

    /// <summary>An edit below a <see cref="Count"/> that was read.</summary>
    public ref readonly RockScrape EditAt(int index) => ref _edits[index];

    /// <summary>The slot of the rock an edit belongs to.</summary>
    public int RockOf(int index) => _editRock[index];

    /// <summary>The first edit of a rock.</summary>
    public int FirstOf(int slot) => _first[slot];

    /// <summary>
    /// The next edit of the same rock, or -1. A reader on another thread must also stop at the <see cref="Count"/> it
    /// read: the link to an edit appears just before that edit is counted.
    /// </summary>
    public int NextOf(int index) => _editNext[index];

    /// <summary>The slot of a rock, or -1 for one never edited. The appending thread only.</summary>
    public int SlotOf(RockRef rock) => _slots.TryGetValue(rock, out var slot) ? slot : -1;

    /// <summary>How many edits a rock holds. The appending thread only.</summary>
    public int EditsOf(int slot) => _counts[slot];

    /// <summary>The last edit of a rock. The appending thread only.</summary>
    public int LastOf(int slot) => _last[slot];

    /// <summary>Whether one more edit of a rock fits the limits on edits and edited rocks.</summary>
    public RockEditRefusal Check(RockRef rock) => Check(rock, 1);

    /// <summary>Whether <paramref name="edits"/> more edits of a rock fit the limits on edits and edited rocks.</summary>
    public RockEditRefusal Check(RockRef rock, int edits)
    {
        if (_editCount + edits > _edits.Length) return RockEditRefusal.WorldFull;
        var slot = SlotOf(rock);
        if (slot < 0) return _rockCount >= _rocks.Length ? RockEditRefusal.WorldFull : edits > _rules.MaxEditsPerRock ? RockEditRefusal.RockFull : RockEditRefusal.None;
        return _counts[slot] + edits > _rules.MaxEditsPerRock ? RockEditRefusal.RockFull : RockEditRefusal.None;
    }

    /// <summary>Appends an edit and returns its index, or -1 when <see cref="Check(RockRef)"/> would refuse it.</summary>
    public int Append(RockRef rock, in RockScrape edit)
    {
        if (Check(rock) != RockEditRefusal.None) return -1;
        var slot = SlotOf(rock);
        var index = _editCount;
        _edits[index] = edit;
        _editNext[index] = -1;
        if (slot < 0)
        {
            slot = _rockCount;
            _rocks[slot] = rock;
            _first[slot] = index;
            _counts[slot] = 0;
            _slots.Add(rock, slot);
            _editRock[index] = slot;
            Volatile.Write(ref _rockCount, slot + 1);
        }
        else
        {
            _editRock[index] = slot;
            _editNext[_last[slot]] = index;
        }

        _last[slot] = index;
        _counts[slot]++;
        Volatile.Write(ref _editCount, index + 1);
        return index;
    }
}
