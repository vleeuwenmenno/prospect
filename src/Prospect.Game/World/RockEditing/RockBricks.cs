using System.Numerics;
using System.Runtime.CompilerServices;

namespace Prospect.Game.World.RockEditing;

/// <summary>
/// The cut field of edited rocks, as sparse bricks: a cache worked out from a <see cref="RockEditLog"/>, never sent or
/// saved. A sample is the distance to the nearest scrape's surface (negative inside the cut, where rock was removed),
/// in <see cref="RockEditRules.StepsPerCell"/> steps a cell. A brick exists only where a sample is inside a cut or
/// within a cell of one, so rock next to a cut's wall always reads its distance to that wall.
/// The base rock is not in the field: the edited rock is what is inside the base shape and outside every cut, so an
/// unedited part of the surface stays exactly the shared facets.
/// <para>
/// What the bricks hold is a function of the set of edits alone, worked out with additions, multiplications,
/// divisions and square roots in a fixed order, so the server and every client hold the same bytes. Used from one
/// thread; nothing is allocated after construction.
/// </para>
/// </summary>
public sealed class RockBricks
{
    private const ulong Empty = ulong.MaxValue;
    private const int Bias = 32768;

    private readonly RockEditRules _rules;
    private readonly RockEditLog _log;
    private readonly int _brickUnits;
    private readonly int _cellUnits;
    private readonly double _stepsPerUnit;
    private readonly float _stepMetres;

    private readonly ulong[] _keys;
    private readonly int[] _values;
    private readonly int _mask;
    private readonly int _shift;

    private readonly sbyte[] _samples;
    private readonly ulong[] _brickKey;
    private readonly int[] _brickNext;
    private readonly sbyte[] _brickLeast;
    private int _free;
    private int _used;

    private readonly int[] _applied;
    private readonly int[] _appliedLast;
    private readonly int[] _firstBrick;
    private readonly int[] _brickCount;
    private readonly uint[] _version;
    private readonly int[] _bounds;
    private readonly sbyte[] _scratch = new sbyte[RockEditRules.SampleCount];

    /// <summary>A brick cache for the rocks of a log, holding at most <paramref name="maxBricks"/> bricks over all of them.</summary>
    public RockBricks(RockEditLog log, int maxBricks)
    {
        _log = log;
        _rules = log.Rules;
        _brickUnits = _rules.BrickUnits;
        _cellUnits = _rules.CellUnits;
        _stepsPerUnit = RockEditRules.StepsPerCell / (double)_cellUnits;
        _stepMetres = _rules.CellMetres / RockEditRules.StepsPerCell;
        var table = 16;
        while (table < maxBricks * 2) table <<= 1;
        _keys = new ulong[table];
        _values = new int[table];
        _mask = table - 1;
        _shift = 64 - BitOperations.Log2((uint)table);
        Array.Fill(_keys, Empty);
        _samples = new sbyte[(long)maxBricks * RockEditRules.SampleCount];
        _brickKey = new ulong[maxBricks];
        _brickNext = new int[maxBricks];
        _brickLeast = new sbyte[maxBricks];
        for (var i = 0; i < maxBricks; i++) _brickNext[i] = i + 1 < maxBricks ? i + 1 : -1;
        _free = maxBricks > 0 ? 0 : -1;
        var rocks = _rules.MaxEditedRocks;
        _applied = new int[rocks];
        _appliedLast = new int[rocks];
        _firstBrick = new int[rocks];
        _brickCount = new int[rocks];
        _version = new uint[rocks];
        _bounds = new int[rocks * 6];
        Array.Fill(_firstBrick, -1);
        Array.Fill(_appliedLast, -1);
    }

    /// <summary>The log the bricks are worked out from.</summary>
    public RockEditLog Log => _log;

    /// <summary>Bricks held over all rocks.</summary>
    public int Used => _used;

    /// <summary>Whether a brick could not be made because the pool was full. Never set where the server's limits are kept.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>How many of a rock's edits are in its bricks.</summary>
    public int Applied(int slot) => _applied[slot];

    /// <summary>Bricks a rock holds.</summary>
    public int BrickCount(int slot) => _brickCount[slot];

    /// <summary>Counts up whenever a rock's bricks change.</summary>
    public uint Version(int slot) => _version[slot];

    /// <summary>A rock's first brick, or -1; follow with <see cref="NextBrick"/>.</summary>
    public int FirstBrick(int slot) => _firstBrick[slot];

    /// <summary>The next brick of the same rock, or -1.</summary>
    public int NextBrick(int brick) => _brickNext[brick];

    /// <summary>A brick's place in its rock's grid of bricks.</summary>
    public void Coordinates(int brick, out int x, out int y, out int z)
    {
        var key = _brickKey[brick];
        x = (int)(key >> 32 & 0xFFFF) - Bias;
        y = (int)(key >> 16 & 0xFFFF) - Bias;
        z = (int)(key & 0xFFFF) - Bias;
    }

    /// <summary>Whether any of a brick's samples is inside a cut; one that only lies within a cell of a cut holds none.</summary>
    public bool HasCut(int brick) => _brickLeast[brick] < 0;

    /// <summary>A brick's samples, x fastest: <see cref="RockEditRules.BrickSamples"/> a side.</summary>
    public ReadOnlySpan<sbyte> Samples(int brick) => _samples.AsSpan(brick * RockEditRules.SampleCount, RockEditRules.SampleCount);

    /// <summary>The brick of a rock at a place in its grid, or -1.</summary>
    public int Find(int slot, int x, int y, int z)
    {
        var key = Key(slot, x, y, z);
        for (var i = Home(key); ; i = i + 1 & _mask)
        {
            var found = _keys[i];
            if (found == key) return _values[i];
            if (found == Empty) return -1;
        }
    }

    /// <summary>The edit of a rock that its bricks have not taken in yet, or -1; <paramref name="limit"/> is a count read from the log.</summary>
    public int NextPending(int slot, int limit)
    {
        var next = _applied[slot] == 0 ? _log.FirstOf(slot) : _log.NextOf(_appliedLast[slot]);
        return next >= 0 && next < limit ? next : -1;
    }

    /// <summary>
    /// Takes one edit of the log into its rock's bricks. The edits of a rock must be applied in the order they were
    /// made, each once: pass what <see cref="NextPending"/> gave.
    /// </summary>
    public void Apply(int index)
    {
        ref readonly var edit = ref _log.EditAt(index);
        var slot = _log.RockOf(index);
        var first = _applied[slot] == 0;
        _applied[slot]++;
        _appliedLast[slot] = index;

        Span<int> tight = stackalloc int[6];
        Span<int> wide = stackalloc int[6];
        Extent(edit, tight, wide);
        var bounds = _bounds.AsSpan(slot * 6, 6);
        for (var axis = 0; axis < 3; axis++)
        {
            bounds[axis] = first ? wide[axis] : Math.Min(bounds[axis], wide[axis]);
            bounds[axis + 3] = first ? wide[axis + 3] : Math.Max(bounds[axis + 3], wide[axis + 3]);
        }

        var changed = false;
        int x0 = Floor(wide[0]), y0 = Floor(wide[1]), z0 = Floor(wide[2]);
        int x1 = Floor(wide[3]), y1 = Floor(wide[4]), z1 = Floor(wide[5]);
        int tx0 = Floor(tight[0]), ty0 = Floor(tight[1]), tz0 = Floor(tight[2]);
        int tx1 = Floor(tight[3]), ty1 = Floor(tight[4]), tz1 = Floor(tight[5]);
        for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    if (Math.Abs(x) >= Bias - 1 || Math.Abs(y) >= Bias - 1 || Math.Abs(z) >= Bias - 1) continue;
                    var brick = Find(slot, x, y, z);
                    if (brick >= 0)
                    {
                        _brickLeast[brick] = RockCutMath.Lower(_samples.AsSpan(brick * RockEditRules.SampleCount, RockEditRules.SampleCount), x, y, z, edit, _cellUnits, _stepsPerUnit, ref changed);
                        continue;
                    }

                    // A new brick is made only where this edit comes within a cell: beyond its own bounds and a cell
                    // more, no sample is that near it.
                    if (x < tx0 || x > tx1 || y < ty0 || y > ty1 || z < tz0 || z > tz1) continue;
                    _scratch.AsSpan().Fill(RockEditRules.MaxSteps);
                    var ignored = false;
                    if (RockCutMath.Lower(_scratch, x, y, z, edit, _cellUnits, _stepsPerUnit, ref ignored) >= RockEditRules.StepsPerCell) continue;
                    if (_free < 0 || _brickCount[slot] >= _rules.MaxBricksPerRock)
                    {
                        Overflowed = true;
                        continue;
                    }

                    // The brick holds every edit of the rock that reaches it, not only this one.
                    for (var earlier = _log.FirstOf(slot); earlier != index; earlier = _log.NextOf(earlier))
                    {
                        ref readonly var other = ref _log.EditAt(earlier);
                        if (Reaches(other, x, y, z))
                            RockCutMath.Lower(_scratch, x, y, z, other, _cellUnits, _stepsPerUnit, ref ignored);
                    }

                    brick = _free;
                    _free = _brickNext[brick];
                    _used++;
                    var key = Key(slot, x, y, z);
                    _brickKey[brick] = key;
                    _brickNext[brick] = _firstBrick[slot];
                    _firstBrick[slot] = brick;
                    _brickCount[slot]++;
                    Insert(key, brick);
                    _scratch.CopyTo(_samples.AsSpan(brick * RockEditRules.SampleCount, RockEditRules.SampleCount));
                    sbyte least = RockEditRules.MaxSteps;
                    foreach (var sample in _scratch) least = Math.Min(least, sample);
                    _brickLeast[brick] = least;
                    changed = true;
                }

        if (changed) _version[slot]++;
    }

    /// <summary>
    /// The most bricks an edit could add to a rock (<paramref name="slot"/> -1 for a rock with no edits yet): the
    /// places its own bounds and a cell more touch that hold no brick. The server asks before it accepts an edit.
    /// </summary>
    public int MayAdd(int slot, in RockScrape edit)
    {
        Span<int> tight = stackalloc int[6];
        Span<int> wide = stackalloc int[6];
        Extent(edit, tight, wide);
        var count = 0;
        for (var z = Floor(tight[2]); z <= Floor(tight[5]); z++)
            for (var y = Floor(tight[1]); y <= Floor(tight[4]); y++)
                for (var x = Floor(tight[0]); x <= Floor(tight[3]); x++)
                    if (slot < 0 || Find(slot, x, y, z) < 0)
                        count++;
        return count;
    }

    /// <summary>Whether <paramref name="bricks"/> more bricks fit a rock and the pool.</summary>
    public RockEditRefusal Check(int slot, int bricks)
    {
        if ((slot < 0 ? 0 : _brickCount[slot]) + bricks > _rules.MaxBricksPerRock) return RockEditRefusal.RockFull;
        return _used + bricks > _brickKey.Length ? RockEditRefusal.WorldFull : RockEditRefusal.None;
    }

    /// <summary>Drops a rock's bricks, as if none of its edits had been applied.</summary>
    public void Clear(int slot)
    {
        for (var brick = _firstBrick[slot]; brick >= 0;)
        {
            var next = _brickNext[brick];
            Remove(_brickKey[brick]);
            _brickNext[brick] = _free;
            _free = brick;
            _used--;
            brick = next;
        }

        _firstBrick[slot] = -1;
        _brickCount[slot] = 0;
        _applied[slot] = 0;
        _appliedLast[slot] = -1;
        _version[slot]++;
    }

    /// <summary>
    /// Replaces a rock's bricks by those another cache of the same log worked out for it: how a replay done on a
    /// worker is installed. Returns false, leaving the rock without bricks, when the pool has no room for them.
    /// </summary>
    public bool CopyRock(RockBricks source, int slot)
    {
        Clear(slot);
        if (source._brickCount[slot] > _brickKey.Length - _used)
        {
            Overflowed = true;
            return false;
        }

        for (var from = source._firstBrick[slot]; from >= 0; from = source._brickNext[from])
        {
            var brick = _free;
            _free = _brickNext[brick];
            _used++;
            var key = source._brickKey[from];
            _brickKey[brick] = key;
            _brickNext[brick] = _firstBrick[slot];
            _firstBrick[slot] = brick;
            Insert(key, brick);
            source.Samples(from).CopyTo(_samples.AsSpan(brick * RockEditRules.SampleCount, RockEditRules.SampleCount));
            _brickLeast[brick] = source._brickLeast[from];
        }

        _brickCount[slot] = source._brickCount[slot];
        _applied[slot] = source._applied[slot];
        _appliedLast[slot] = source._appliedLast[slot];
        source._bounds.AsSpan(slot * 6, 6).CopyTo(_bounds.AsSpan(slot * 6, 6));
        return true;
    }

    /// <summary>The cut field of one rock, for contact and ray queries; empty for a rock without bricks.</summary>
    public RockCut CutOf(int slot) => slot >= 0 && _brickCount[slot] > 0 ? new RockCut(this, slot) : default;

    /// <summary>
    /// The cut field at a point of a rock's own frame, in metres: negative where rock was scraped away, positive in
    /// what is left, <see cref="float.PositiveInfinity"/> where no cut is near. <paramref name="gradient"/> is its
    /// slope there (unit length next to a cut, shorter where the field is capped): it points away from the cut.
    /// </summary>
    public float Sample(int slot, Vector3 point, out Vector3 gradient)
    {
        gradient = default;
        double ux = point.X * (double)RockScrape.Units, uy = point.Y * (double)RockScrape.Units, uz = point.Z * (double)RockScrape.Units;
        var bounds = _bounds.AsSpan(slot * 6, 6);
        if (!(ux >= bounds[0] && uy >= bounds[1] && uz >= bounds[2] && ux <= bounds[3] && uy <= bounds[4] && uz <= bounds[5]))
            return float.PositiveInfinity;
        int x = (int)Math.Floor(ux / _brickUnits), y = (int)Math.Floor(uy / _brickUnits), z = (int)Math.Floor(uz / _brickUnits);
        var brick = Find(slot, x, y, z);
        if (brick < 0) return float.PositiveInfinity;
        var steps = RockCutMath.Interpolate(_samples.AsSpan(brick * RockEditRules.SampleCount, RockEditRules.SampleCount),
            (ux - (double)x * _brickUnits) / _cellUnits, (uy - (double)y * _brickUnits) / _cellUnits, (uz - (double)z * _brickUnits) / _cellUnits, out gradient);
        gradient /= RockEditRules.StepsPerCell;
        return steps * _stepMetres;
    }

    /// <summary>A hash of a rock's bricks, the same wherever the same edits were applied in any order of bricks.</summary>
    public ulong Hash(int slot)
    {
        var total = 0UL;
        for (var brick = _firstBrick[slot]; brick >= 0; brick = _brickNext[brick])
        {
            var hash = 14695981039346656037UL ^ _brickKey[brick] & 0xFFFF_FFFF_FFFFUL;
            foreach (var sample in Samples(brick)) hash = (hash ^ (byte)sample) * 1099511628211UL;
            // Summed, so the order bricks were made in does not matter.
            total += hash * 0x9E3779B97F4A7C15UL;
        }

        return total ^ (ulong)_brickCount[slot];
    }

    private bool Reaches(in RockScrape edit, int x, int y, int z)
    {
        Span<int> tight = stackalloc int[6];
        Span<int> wide = stackalloc int[6];
        Extent(edit, tight, wide);
        long lx = (long)x * _brickUnits, ly = (long)y * _brickUnits, lz = (long)z * _brickUnits;
        return wide[0] <= lx + _brickUnits && wide[3] >= lx && wide[1] <= ly + _brickUnits && wide[4] >= ly && wide[2] <= lz + _brickUnits && wide[5] >= lz;
    }

    // The edit's own bounds and a cell more (where a sample can be within a cell of it, so where it can make a
    // brick) and the bounds of everything a sample of it can reach.
    private void Extent(in RockScrape edit, Span<int> tight, Span<int> wide)
    {
        var radius = (edit.RadiusMm * RockScrape.Units + 999) / 1000 + _cellUnits;
        tight[0] = Math.Min(edit.Ax, edit.Bx) - radius;
        tight[1] = Math.Min(edit.Ay, edit.By) - radius;
        tight[2] = Math.Min(edit.Az, edit.Bz) - radius;
        tight[3] = Math.Max(edit.Ax, edit.Bx) + radius;
        tight[4] = Math.Max(edit.Ay, edit.By) + radius;
        tight[5] = Math.Max(edit.Az, edit.Bz) + radius;
        for (var i = 0; i < 3; i++)
        {
            wide[i] = tight[i] + _cellUnits - _rules.ReachUnits;
            wide[i + 3] = tight[i + 3] - _cellUnits + _rules.ReachUnits;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Floor(int units) => units >= 0 ? units / _brickUnits : -((-units + _brickUnits - 1) / _brickUnits);

    private static ulong Key(int slot, int x, int y, int z) =>
        (ulong)(uint)slot << 48 | (ulong)(uint)(x + Bias) << 32 | (ulong)(uint)(y + Bias) << 16 | (uint)(z + Bias);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Home(ulong key) => (int)(key * 0x9E3779B97F4A7C15UL >> _shift);

    private void Insert(ulong key, int value)
    {
        var i = Home(key);
        while (_keys[i] != Empty) i = i + 1 & _mask;
        _keys[i] = key;
        _values[i] = value;
    }

    // Linear probing without tombstones: the entries after the hole move back while they may.
    private void Remove(ulong key)
    {
        var i = Home(key);
        while (_keys[i] != key)
        {
            if (_keys[i] == Empty) return;
            i = i + 1 & _mask;
        }

        for (var j = i + 1 & _mask; _keys[j] != Empty; j = j + 1 & _mask)
        {
            var home = Home(_keys[j]);
            var stays = i <= j ? i < home && home <= j : i < home || home <= j;
            if (stays) continue;
            _keys[i] = _keys[j];
            _values[i] = _values[j];
            i = j;
        }

        _keys[i] = Empty;
    }
}

/// <summary>The cut field of one edited rock, handed to contact and ray queries. The default value is a rock without edits.</summary>
public readonly struct RockCut
{
    private readonly RockBricks? _bricks;
    private readonly int _slot;

    internal RockCut(RockBricks bricks, int slot)
    {
        _bricks = bricks;
        _slot = slot;
    }

    /// <summary>Whether the rock has been scraped at all.</summary>
    public bool Any => _bricks is not null;

    /// <summary>Edge of a cell of the field in metres; zero for a rock without edits.</summary>
    public float CellMetres => _bricks?.Log.Rules.CellMetres ?? 0;

    /// <inheritdoc cref="RockBricks.Sample" />
    public float Sample(Vector3 point, out Vector3 gradient)
    {
        if (_bricks is null)
        {
            gradient = default;
            return float.PositiveInfinity;
        }

        return _bricks.Sample(_slot, point, out gradient);
    }
}

/// <summary>The arithmetic of the cut field: plain operations in a fixed order, the same bits on every machine.</summary>
public static class RockCutMath
{
    /// <summary>
    /// Lowers a brick's samples to the distance to one scrape wherever that is nearer than what they hold. Returns
    /// the least sample afterwards and sets <paramref name="changed"/> when any sample moved.
    /// </summary>
    public static sbyte Lower(Span<sbyte> samples, int brickX, int brickY, int brickZ, in RockScrape edit, int cellUnits, double stepsPerUnit, ref bool changed)
    {
        double abx = edit.Bx - edit.Ax, aby = edit.By - edit.Ay, abz = edit.Bz - edit.Az;
        var length = abx * abx + aby * aby + abz * abz;
        var radius = edit.RadiusMm * RockScrape.Units / 1000.0;
        var brick = cellUnits * RockEditRules.BrickCells;
        double ox = (double)brickX * brick - edit.Ax, oy = (double)brickY * brick - edit.Ay, oz = (double)brickZ * brick - edit.Az;
        sbyte least = RockEditRules.MaxSteps;
        var n = 0;
        for (var k = 0; k < RockEditRules.BrickSamples; k++)
        {
            var pz = oz + k * cellUnits;
            for (var j = 0; j < RockEditRules.BrickSamples; j++)
            {
                var py = oy + j * cellUnits;
                for (var i = 0; i < RockEditRules.BrickSamples; i++, n++)
                {
                    var px = ox + i * cellUnits;
                    var t = length > 0 ? (px * abx + py * aby + pz * abz) / length : 0;
                    if (t < 0) t = 0;
                    else if (t > 1) t = 1;
                    double cx = px - abx * t, cy = py - aby * t, cz = pz - abz * t;
                    var steps = Math.Floor((Math.Sqrt(cx * cx + cy * cy + cz * cz) - radius) * stepsPerUnit + 0.5);
                    var held = samples[n];
                    if (steps < held)
                    {
                        held = steps <= -RockEditRules.MaxSteps ? (sbyte)-RockEditRules.MaxSteps : (sbyte)steps;
                        if (held != samples[n])
                        {
                            samples[n] = held;
                            changed = true;
                        }
                    }

                    if (held < least) least = held;
                }
            }
        }

        return least;
    }

    /// <summary>
    /// The field inside a brick at a place given in cells (0 to <see cref="RockEditRules.BrickCells"/> an axis), in
    /// steps, and its slope in steps a cell. Linear on the six tetrahedra of each cell that share its main diagonal,
    /// which is the surface that is drawn: contact and drawing read one interpolant.
    /// </summary>
    public static float Interpolate(ReadOnlySpan<sbyte> samples, double x, double y, double z, out Vector3 gradient)
    {
        const int Side = RockEditRules.BrickSamples;
        var cx = Math.Clamp((int)x, 0, RockEditRules.BrickCells - 1);
        var cy = Math.Clamp((int)y, 0, RockEditRules.BrickCells - 1);
        var cz = Math.Clamp((int)z, 0, RockEditRules.BrickCells - 1);
        float fa = Math.Clamp((float)(x - cx), 0f, 1f), fb = Math.Clamp((float)(y - cy), 0f, 1f), fc = Math.Clamp((float)(z - cz), 0f, 1f);
        int a = 1, b = Side, c = Side * Side;
        int ia = 0, ib = 1, ic = 2;
        if (fa < fb)
        {
            (fa, fb) = (fb, fa);
            (a, b) = (b, a);
            (ia, ib) = (ib, ia);
        }

        if (fb < fc)
        {
            (fb, fc) = (fc, fb);
            (b, c) = (c, b);
            (ib, ic) = (ic, ib);
        }

        if (fa < fb)
        {
            (fa, fb) = (fb, fa);
            (a, b) = (b, a);
            (ia, ib) = (ib, ia);
        }

        var origin = cx + Side * (cy + Side * cz);
        float v0 = samples[origin], v1 = samples[origin + a], v2 = samples[origin + a + b], v3 = samples[origin + a + b + c];
        Span<float> slope = stackalloc float[3];
        slope[ia] = v1 - v0;
        slope[ib] = v2 - v1;
        slope[ic] = v3 - v2;
        gradient = new Vector3(slope[0], slope[1], slope[2]);
        return v0 + fa * (v1 - v0) + fb * (v2 - v1) + fc * (v3 - v2);
    }
}

/// <summary>The edits of a world and the bricks worked out from them, as one simulation side holds them.</summary>
public sealed class RockEdits
{
    /// <summary>Makes an empty set with room for everything the rules allow.</summary>
    public RockEdits(RockEditRules rules)
    {
        Log = new RockEditLog(rules);
        Bricks = new RockBricks(Log, rules.MaxBricks);
    }

    /// <summary>The edits, the truth.</summary>
    public RockEditLog Log { get; }

    /// <summary>The cut field worked out from the edits applied so far.</summary>
    public RockBricks Bricks { get; }

    /// <summary>The cut field of a rock, empty for one without edits. The thread that owns the bricks only.</summary>
    public RockCut CutOf(RockRef rock) => Log.RockCount == 0 ? default : Bricks.CutOf(Log.SlotOf(rock));
}
