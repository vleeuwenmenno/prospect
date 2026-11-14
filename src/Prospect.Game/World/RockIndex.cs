using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// Finds the asteroids of one sector that are near a point, without looking at the rest. Asteroids are filed by
/// the cell their centre is in, on two grids: a fine one for the many small rocks and a coarse one for the large.
/// The few giants that fit neither are the first <see cref="GiantCount"/> asteroids of the sector, which is sorted
/// largest first, and are always looked at. Positions are the generated ones: an asteroid that has been pushed is
/// found through <see cref="RockField.MovedIn"/> instead.
/// </summary>
public sealed class RockIndex
{
    /// <summary>Cell edge of the fine grid, in metres. It holds asteroids up to half that in radius.</summary>
    public const float FineCell = 256f;

    /// <summary>Cell edge of the coarse grid. Asteroids larger than half of it are the giants.</summary>
    public const float CoarseCell = 2048f;

    private const int Levels = 2;
    private const int AxisBits = 10;
    private const int AxisMax = (1 << AxisBits) - 1;

    /// <summary>The widest sector the cell numbering can address.</summary>
    public const double MaxSectorSize = FineCell * AxisMax;

    private int[] _heads = [];
    private int[] _next = [];
    private int[] _keys = [];
    private int _shift;
    private float _half;
    private int _fineFiled, _coarseFiled;

    /// <summary>How many of the sector's first asteroids are too large for the grids.</summary>
    public int GiantCount { get; private set; }

    /// <summary>Empties the index and makes room for <paramref name="capacity"/> asteroids of a sector this wide.</summary>
    public void Reset(int capacity, double sectorSize)
    {
        var buckets = 16;
        var bits = 4;
        while (buckets < capacity * 2)
        {
            buckets <<= 1;
            bits++;
        }

        // Buffers only ever grow, so a slot that streams sectors of every size settles on its largest.
        if (_heads.Length < buckets)
            _heads = new int[buckets];
        else
            Array.Clear(_heads, 0, buckets);
        if (_next.Length < capacity)
        {
            _next = new int[capacity];
            _keys = new int[capacity];
        }

        _shift = 32 - bits;
        _half = (float)(sectorSize * 0.5);
        GiantCount = 0;
        _fineFiled = _coarseFiled = 0;
    }

    /// <summary>Files an asteroid. Asteroids must be added in order, largest first.</summary>
    public void Add(int rock, Vector3 position, float radius)
    {
        var level = LevelOf(radius);
        if (level >= Levels)
        {
            GiantCount = rock + 1;
            return;
        }

        if (level == 0) _fineFiled++;
        else _coarseFiled++;

        var key = Key(level, Cell(position.X, level), Cell(position.Y, level), Cell(position.Z, level));
        var bucket = Bucket(key);
        _keys[rock] = key;
        _next[rock] = _heads[bucket];
        _heads[bucket] = rock + 1;
    }

    private static int LevelOf(float radius) =>
        radius <= FineCell * 0.5f ? 0
        : radius <= CoarseCell * 0.5f ? 1
        : Levels;

    private int Cell(float coordinate, int level)
    {
        // Negative cells clamp to zero, so truncation and floor give the same clamped index.
        var cell = (int)((coordinate + _half) / (level == 0 ? FineCell : CoarseCell));
        return Math.Clamp(cell, 0, AxisMax);
    }

    private static int Key(int level, int x, int y, int z) => level << AxisBits * 3 | z << AxisBits * 2 | y << AxisBits | x;

    private int Bucket(int key) => (int)((uint)key * 2654435761u >> _shift);

    /// <summary>
    /// Walks the asteroids whose bounding sphere can touch a sphere at <paramref name="center"/>, given relative to
    /// the sector's centre. It may return a few that do not touch; it never misses one that does.
    /// </summary>
    public Query Near(Vector3 center, float radius) => new(this, center, radius);

    private struct CellRange
    {
        public int MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        public readonly long Visits => (long)(MaxX - MinX + 1) * (MaxY - MinY + 1) * (MaxZ - MinZ + 1);
    }

    private CellRange Cells(Vector3 center, float radius, int level)
    {
        var edge = level == 0 ? FineCell : CoarseCell;
        var reach = radius + edge * 0.5f;
        // Match Cell's operation order without twelve extra calls per placement in debug builds.
        return new CellRange
        {
            MinX = Math.Clamp((int)((center.X - reach + _half) / edge), 0, AxisMax),
            MaxX = Math.Clamp((int)((center.X + reach + _half) / edge), 0, AxisMax),
            MinY = Math.Clamp((int)((center.Y - reach + _half) / edge), 0, AxisMax),
            MaxY = Math.Clamp((int)((center.Y + reach + _half) / edge), 0, AxisMax),
            MinZ = Math.Clamp((int)((center.Z - reach + _half) / edge), 0, AxisMax),
            MaxZ = Math.Clamp((int)((center.Z + reach + _half) / edge), 0, AxisMax),
        };
    }

    /// <summary>Full grid-cell visits before candidate chains, for choosing a cheaper placement overlap search.</summary>
    internal long QueryCellVisits(Vector3 center, float radius) => Cells(center, radius, 0).Visits + Cells(center, radius, 1).Visits;

    /// <summary>Placement-only existence query; avoids iterator state transitions for every empty cell.</summary>
    internal bool PlacementOverlaps(Asteroid[] placed, int count, Vector3 center, float radius)
    {
        var blocker = -1;
        return PlacementOverlaps(placed, count, center, radius, ref blocker);
    }

    internal bool PlacementOverlaps(Asteroid[] placed, int count, Vector3 center, float radius, ref int blocker)
    {
        // Nearby retries commonly hit the same larger rock; avoid repeating the grid query.
        if (blocker >= 0 && Touches(placed[blocker], center, radius))
            return true;
        if (count == 0)
            return false;
        for (var i = 0; i < GiantCount; i++)
            if (Touches(placed[i], center, radius))
            {
                blocker = i;
                return true;
            }

        for (var level = Levels - 1; level >= 0; level--)
        {
            // Largest-first placement leaves the fine grid empty while large rocks are being placed.
            var filed = level == 0 ? _fineFiled : _coarseFiled;
            if (filed == 0)
                continue;
            var cells = Cells(center, radius, level);
            if (cells.Visits > filed)
            {
                var first = level == 0 ? GiantCount + _coarseFiled : GiantCount;
                for (var i = first; i < first + filed; i++)
                    if (Touches(placed[i], center, radius))
                    {
                        blocker = i;
                        return true;
                    }
                continue;
            }
            for (var z = cells.MinZ; z <= cells.MaxZ; z++)
                for (var y = cells.MinY; y <= cells.MaxY; y++)
                    for (var x = cells.MinX; x <= cells.MaxX; x++)
                    {
                        var key = level << AxisBits * 3 | z << AxisBits * 2 | y << AxisBits | x;
                        var bucket = (int)((uint)key * 2654435761u >> _shift);
                        for (var entry = _heads[bucket]; entry != 0; entry = _next[entry - 1])
                        {
                            var other = entry - 1;
                            if (_keys[other] != key)
                                continue;
                            // Keep the scalar operation order of Touches; debug generation avoids a call per candidate.
                            ref readonly var rock = ref placed[other];
                            var dx = center.X - rock.LocalPosition.X;
                            var dy = center.Y - rock.LocalPosition.Y;
                            var dz = center.Z - rock.LocalPosition.Z;
                            var apart = radius + rock.Radius;
                            if (dx * dx + dy * dy + dz * dz < apart * apart)
                            {
                                blocker = other;
                                return true;
                            }
                        }
                    }
        }
        return false;
    }

    private static bool Touches(in Asteroid rock, Vector3 position, float reach)
    {
        var dx = position.X - rock.LocalPosition.X;
        var dy = position.Y - rock.LocalPosition.Y;
        var dz = position.Z - rock.LocalPosition.Z;
        var apart = reach + rock.Radius;
        return dx * dx + dy * dy + dz * dz < apart * apart;
    }

    /// <summary>The walk started by <see cref="Near"/>. Lives on the stack; nothing is allocated.</summary>
    public struct Query
    {
        private readonly RockIndex _index;
        private readonly Vector3 _center;
        private readonly float _radius;
        private int _level;
        private int _giant;
        private int _chain;
        private int _key;
        private int _x;
        private int _y;
        private int _z;
        private int _minX;
        private int _minY;
        private int _maxX;
        private int _maxY;
        private int _maxZ;

        internal Query(RockIndex index, Vector3 center, float radius)
        {
            _index = index;
            _center = center;
            _radius = radius;
            _level = -1;
        }

        /// <summary>Moves to the next candidate. False when there are no more.</summary>
        public bool Next(out int rock)
        {
            if (_level < 0)
            {
                if (_giant < _index.GiantCount)
                {
                    rock = _giant++;
                    return true;
                }

                Enter(0);
            }

            while (_level < Levels)
            {
                // The rest of the chain in the current bucket; a bucket can hold other cells' asteroids too.
                while (_chain > 0)
                {
                    var candidate = _chain - 1;
                    _chain = _index._next[candidate];
                    if (_index._keys[candidate] == _key)
                    {
                        rock = candidate;
                        return true;
                    }
                }

                if (++_x > _maxX)
                {
                    _x = _minX;
                    if (++_y > _maxY)
                    {
                        _y = _minY;
                        if (++_z > _maxZ)
                        {
                            Enter(_level + 1);
                            continue;
                        }
                    }
                }

                OpenCell();
            }

            rock = -1;
            return false;
        }

        /// <summary>Sets up the block of cells a level has to offer: those a rock of that level could reach from.</summary>
        private void Enter(int level)
        {
            _level = level;
            if (level >= Levels)
                return;

            var reach = _radius + (level == 0 ? FineCell : CoarseCell) * 0.5f;
            _minX = _index.Cell(_center.X - reach, level);
            _maxX = _index.Cell(_center.X + reach, level);
            _minY = _index.Cell(_center.Y - reach, level);
            _maxY = _index.Cell(_center.Y + reach, level);
            _maxZ = _index.Cell(_center.Z + reach, level);
            _x = _minX;
            _y = _minY;
            _z = _index.Cell(_center.Z - reach, level);
            OpenCell();
        }

        private void OpenCell()
        {
            _key = Key(_level, _x, _y, _z);
            _chain = _index._heads[_index.Bucket(_key)];
        }
    }
}
