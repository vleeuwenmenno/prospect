using System.Numerics;
using System.Runtime.InteropServices;

namespace Prospect.Game.World;

/// <summary>A shield shell vertex in the hull's own frame and metres, laid out as the client's mesh vertex.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ShellVertex
{
    /// <summary>Where the vertex is.</summary>
    public Vector3 Position;

    /// <summary>The shell's outward unit normal there.</summary>
    public Vector3 Normal;
}

/// <summary>
/// The shape of every shield shell, as shares of the block size. Shared content (the block limits), so a server and
/// its clients build and admit the same shells.
/// </summary>
/// <param name="Roundness">The share of the margin grown round rather than square, above 0 up to 1.</param>
/// <param name="Blend">The radius gaps and creases are closed with: gaps under about four times it close.</param>
public readonly record struct ShellShape(float Roundness, float Blend)
{
    /// <summary>A round shell that closes gaps of about a cell.</summary>
    public static readonly ShellShape Default = new(1f, 0.3f);

    /// <summary>Whether the figures are ones the builder accepts.</summary>
    public bool IsValid => Roundness is >= 0.1f and <= 1f && Blend is >= 0f and <= 0.5f;
}

/// <summary>What one shell build took, against the room the builder has.</summary>
public struct ShellWork
{
    /// <summary>Cells looked at near the blocks, and how many of them the surface passes through.</summary>
    public int Cells, SurfaceCells;

    /// <summary>Field samples taken, and block distances worked out for them.</summary>
    public int Samples;
    public long Distances;

    /// <summary>Tree nodes visited finding the blocks near a sample.</summary>
    public long Visits;

    /// <summary>Cells drawn one by one, runs drawn whole, and the cells those runs stand for.</summary>
    public int LoneCells, Runs, RunCells;

    /// <summary>Vertices and indices written, and the distinct crossings found (some fall on no triangle).</summary>
    public int Vertices, Indices, Unique;

    /// <summary>Separate box-shaped parts triangulated with exact rounded-surface vertices and analytic normals.</summary>
    public int RoundedBoxes;

    /// <summary>Why the build stopped short, or null when it finished.</summary>
    public string? Overflow;
}

/// <summary>
/// The smooth shield shell, shared by the client that draws it and the admission that bounds it.
/// <para>
/// Target. Let U be the surviving blocks, A = U grown square by q = m(1 - roundness), R = m·roundness + blend, with
/// m the margin, and F(x) = max over |v| ≤ blend of dist(x + v, A). The target is T = {F ≤ R}: A grown round by R
/// and shrunk back by the blend, a morphological closing. It depends only on the union, never on how it is split
/// into blocks or their order; an isolated block keeps its own rounded offset. Opposing faces of A close when they
/// are under 2R apart; a finite gap also needs a neck wide enough to survive the shrinking. Openings wider than 2R
/// stay open.
/// </para>
/// <para>
/// Samples. The field sampled, Fc, is the same maximum over a fixed 53-point cover of the ball (<see cref="Field"/>),
/// clamped above the level. It is 1-Lipschitz and F - δ ≤ Fc ≤ F, with δ ≤ 0.4766 of the blend. It is not F: where
/// it falls short the shell stands further out, by up to δ in field value.
/// </para>
/// <para>
/// Extraction. Fc is sampled on a grid of h = a third of a block, aligned with block faces, and the shell is the
/// level set of its linear interpolation I over the six Kuhn tetrahedra of each cell, so |I - Fc| ≤ e = √3h/2 = 0.2887
/// block and F - δ - e ≤ I ≤ F + e on the shell, give or take rounding. These are field values, not distances: the
/// shell is not the target, but within those values of it. A cell whose samples are equal to the bit along an axis
/// joins its run of such cells, drawn as the level set of their interpolation over triangular prisms (linear across
/// a cross-section triangle and along the run): the cross-section's contour swept along the run. That interpolation
/// does not claim the field is flat or extruded; it has the same bound e (see <see cref="Invariant"/>). Each line of
/// a strip is cut wherever a neighbouring piece has a vertex on it, so the shell has no T-junctions.
/// </para>
/// <para>
/// Enclosure. Every point of U has a cube of half side q inside A around it, so Fc there is at most the blend plus
/// √(3K), with K = (h/2 - q)² for q ≤ h/4 and 4(h - q)³/(27h) above, which stays under R for every admitted
/// roundness (by 0.011 of a block at full roundness, more below it); the interpolation therefore keeps every block
/// inside the shell. Crossings are clamped 1e-4 of an edge in from its ends, well inside that.
/// </para>
/// <para>
/// Normals are the mesh's own: each distinct vertex sums the normals of the triangles around it, weighted by their
/// angles there. Flat stretches are found on the union's planes, not on single blocks, and cut canonically, so the
/// mesh, and with it every normal, is the same however the union is split into blocks.
/// </para>
/// <para>
/// Bounds. Cells are discovered in a band around each block's surface, never through a block's volume; every
/// table, list and counter has a fixed size, every distance and tree visit is charged before it is made, and a
/// build that would pass a limit stops, says which, and returns no shell. Nothing is allocated after construction.
/// </para>
/// </summary>
public sealed class ShieldSurface
{
    /// <summary>Samples along a block's side: a third of a block keeps the interpolation inside the margin.</summary>
    public const int Resolution = 3;

    private const int CornerOffsets = 8;
    private readonly int _maxCells;
    private readonly int _maxSamples;
    private readonly long _maxDistances;
    private readonly long _maxVisits;

    // Cells near blocks: key, and per cell its corner samples, whether the surface passes and how it is drawn.
    private readonly long[] _cellKeys;
    private readonly int[] _cellStamps;
    private readonly int[] _cellSlots;
    private readonly long[] _cellList;
    private readonly int[] _cellCorner;
    private readonly sbyte[] _cellAxis;
    private readonly int[] _runStart;
    private readonly int[] _runEnd;
    private readonly byte[] _invariant;
    private readonly int[] _bestRun;

    // Samples at cell corners: key and field value.
    private readonly long[] _sampleKeys;
    private readonly int[] _sampleStamps;
    private readonly float[] _sampleValues;

    // Vertex normals by edge, so each distinct vertex is worked out once.
    private readonly long[] _edgeA;
    private readonly long[] _edgeB;
    private readonly int[] _edgeStamps;
    private readonly Vector3[] _edgeNormals;
    private readonly Vector3[] _edgePositions;
    private readonly int[] _outSlots;
    private readonly int[] _slotOut;
    private readonly Vector3[] _edgeFaces;
    private readonly bool[] _slotFixed;
    private long _fixedKeys;
    private int[] _parent = new int[1024];
    private long[] _volume = new long[1024];
    private Vector3[] _partLow = new Vector3[1024];
    private Vector3[] _partHigh = new Vector3[1024];
    private bool[] _partWedge = new bool[1024];
    private int _outCount, _vertexLimit;
    private int _uniqueEdges;

    private readonly float[] _breakA;
    private readonly float[] _breakB;
    private readonly int[] _near = new int[256];
    private readonly float[] _nearDistance = new float[256];
    private Vector3[] _low = new Vector3[1024];
    private Vector3[] _high = new Vector3[1024];
    private ulong[] _alive = new ulong[BlockSet.WordCount(GeometryAdmission.MaxBlocks)];
    private int _stamp;

    // The build in hand.
    private HullDesign _design = null!;
    private int _aliveWords;
    private float _size, _h, _q, _r, _blend, _clamp;
    private Vector3 _shift;
    private Double3 _origin;
    private (int X, int Y, int Z) _base;
    private ShellWork _work;
    private static readonly Vector3[] Ball = BuildBall();

    private static ShieldSurface? _shared;
    private static readonly Lock SharedLock = new();

    /// <summary>
    /// Runs <paramref name="build"/> with the one builder this process keeps, made the first time it is asked for:
    /// admission and the client's shell worker take turns with it, so its scratch is held once. Never call it from a
    /// frame or a tick: a build takes milliseconds.
    /// </summary>
    public static T WithShared<T>(Func<ShieldSurface, T> build)
    {
        lock (SharedLock)
            return build(_shared ??= new ShieldSurface());
    }

    /// <summary>As <see cref="WithShared{T}(Func{ShieldSurface, T})"/>, with state passed in so nothing is captured.</summary>
    public static TResult WithShared<TState, TResult>(TState state, Func<ShieldSurface, TState, TResult> build)
    {
        lock (SharedLock)
            return build(_shared ??= new ShieldSurface(), state);
    }

    /// <summary>
    /// A builder with room for <paramref name="maxCells"/> cells near blocks, <paramref name="maxVertices"/> distinct
    /// vertices, and at most <paramref name="maxDistances"/> block distances and <paramref name="maxVisits"/> tree
    /// nodes visited a build (by default 400 and 4,096 a cell).
    /// </summary>
    public ShieldSurface(int maxCells = 1 << 18, long maxDistances = 0, int maxVertices = GeometryAdmission.MaxVertices, long maxVisits = 0)
    {
        _outSlots = new int[maxVertices];
        _maxCells = maxCells;
        _maxSamples = maxCells * 2;
        _maxDistances = maxDistances > 0 ? maxDistances : (long)maxCells * 400;
        _maxVisits = maxVisits > 0 ? maxVisits : (long)maxCells * 4096;
        var cellTable = Pow2(maxCells * 2);
        _cellKeys = new long[cellTable];
        _cellStamps = new int[cellTable];
        _cellSlots = new int[cellTable];
        _cellList = new long[maxCells];
        _cellCorner = new int[maxCells * CornerOffsets];
        _cellAxis = new sbyte[maxCells];
        _runStart = new int[maxCells];
        _runEnd = new int[maxCells];
        _invariant = new byte[maxCells];
        _bestRun = new int[maxCells];
        var sampleTable = Pow2(_maxSamples * 2);
        _sampleKeys = new long[sampleTable];
        _sampleStamps = new int[sampleTable];
        _sampleValues = new float[sampleTable];
        var edgeTable = Pow2(maxVertices * 2);
        _edgeA = new long[edgeTable];
        _edgeB = new long[edgeTable];
        _edgeStamps = new int[edgeTable];
        _edgeNormals = new Vector3[edgeTable];
        _edgePositions = new Vector3[edgeTable];
        _edgeFaces = new Vector3[edgeTable];
        _slotFixed = new bool[edgeTable];
        _slotOut = new int[edgeTable];
        _breakA = new float[1 << 16];
        _breakB = new float[1 << 16];
    }

    /// <summary>
    /// The bytes of every array this builder holds, element size times length, all taken at construction; the
    /// alive copy and the block corner arrays grow only for a design of more blocks than any before. Array headers
    /// and the objects themselves are not counted.
    /// </summary>
    public long ScratchBytes =>
        Bytes(_cellKeys) + Bytes(_cellStamps) + Bytes(_cellSlots) + Bytes(_cellList) + Bytes(_cellCorner) + Bytes(_cellAxis)
        + Bytes(_runStart) + Bytes(_runEnd) + Bytes(_invariant) + Bytes(_bestRun)
        + Bytes(_sampleKeys) + Bytes(_sampleStamps) + Bytes(_sampleValues)
        + Bytes(_edgeA) + Bytes(_edgeB) + Bytes(_edgeStamps) + Bytes(_edgeNormals) + Bytes(_edgePositions) + Bytes(_edgeFaces)
        + Bytes(_outSlots) + Bytes(_slotOut) + Bytes(_breakA) + Bytes(_breakB) + Bytes(_near) + Bytes(_nearDistance)
        + Bytes(_low) + Bytes(_high) + Bytes(_alive) + Bytes(_slotFixed) + Bytes(_parent) + Bytes(_volume) + Bytes(_partLow) + Bytes(_partHigh) + Bytes(_partWedge) + Bytes(_planes) + Bytes(_pieces) + Bytes(_grown) + Bytes(_faceEntries) + Bytes(_edges) + Bytes(_runs) + Bytes(_canon) + Bytes(_ring);

    private static long Bytes<T>(T[] array) where T : struct => (long)array.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    /// <summary>What the last build took.</summary>
    public ShellWork Work => _work;

    private static int Pow2(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// The cover of the unit ball: its centre, and the 26 directions to the neighbours of a cube's centre at the full
    /// and at half the radius. Every point of the ball is within 0.4766 of one of them. Normalised in scalars.
    /// </summary>
    private static Vector3[] BuildBall()
    {
        var list = new Vector3[53];
        var n = 1;
        for (var shell = 0; shell < 2; shell++)
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                        if (x != 0 || y != 0 || z != 0)
                        {
                            var scale = (shell == 0 ? 1f : 0.5f) / MathF.Sqrt(x * x + y * y + z * z);
                            list[n++] = new Vector3(x * scale, y * scale, z * scale);
                        }
        return list;
    }

    private static readonly float[] BallRadius = BuildRadii();

    private static float[] BuildRadii()
    {
        var radii = new float[53];
        for (var i = 1; i < radii.Length; i++) radii[i] = i <= 26 ? 1f : 0.5f;
        return radii;
    }

    /// <summary>
    /// Writes the shell of the blocks of <paramref name="design"/> in <paramref name="alive"/> as triangles wound
    /// counter-clockwise seen from outside: each distinct vertex once in <paramref name="vertices"/>, three indices a
    /// triangle in <paramref name="indices"/>. Returns the vertex count and gives the index count, or -1 when the shell
    /// does not fit either span or the builder's room; <see cref="Work"/> then says why. Nothing written then is a
    /// shell. Allocates nothing.
    /// </summary>
    public int Build(HullDesign design, ReadOnlySpan<ulong> alive, ShellShape shape, Span<ShellVertex> vertices, Span<ushort> indices, out int indexCount)
    {
        if (!shape.IsValid) throw new ArgumentOutOfRangeException(nameof(shape));
        indexCount = 0;
        Begin(design, alive, shape);
        _vertexLimit = Math.Min(vertices.Length, _outSlots.Length);
        var count = 0;
        if (!Boxes(indices, ref count))
        {
            _work.Overflow ??= "indices";
            return -1;
        }

        if (!Flats() || !Discover() || !Sample() || !Classify())
            return -1;
        for (var plane = 0; plane < _planeCount && count >= 0; plane++)
            count = Flat(plane, indices, count);
        if (count < 0)
        {
            _work.Overflow ??= "indices";
            return -1;
        }

        for (var c = 0; c < _work.Cells; c++)
        {
            if (_cellAxis[c] == Single)
                count = Lone(c, indices, count);
            else if (_cellAxis[c] >= 0 && _runStart[c] == Coordinate(_cellList[c], _cellAxis[c]))
                count = Run(c, indices, count);
            if (count < 0)
            {
                _work.Overflow ??= "indices";
                return -1;
            }
        }

        // Each distinct vertex once, with its gathered normal normalised once.
        for (var v = 0; v < _outCount; v++)
        {
            var slot = _outSlots[v];
            var sum = _edgeNormals[slot];
            var length = sum.Length();
            var normal = _slotFixed[slot] ? sum : length > 1e-12f ? sum / length : _edgeFaces[slot];
            vertices[v] = new ShellVertex { Position = _edgePositions[slot], Normal = normal };
        }

        indexCount = count;
        _work.Vertices = _outCount;
        _work.Indices = count;
        _work.Unique = _uniqueEdges;
        return _outCount;
    }

    private const sbyte Empty = -2;
    private const sbyte Single = -1;

    private void Begin(HullDesign design, ReadOnlySpan<ulong> alive, ShellShape shape)
    {
        _design = design;
        if (_alive.Length < alive.Length) _alive = new ulong[alive.Length];
        alive.CopyTo(_alive);
        _aliveWords = alive.Length;
        // The field is worked out in block units from the grid's own corner, so every split of a union, and every
        // centre of mass, gives the same samples to the bit.
        _size = design.BlockSize;
        _h = 1f / Resolution;
        var m = GeometryAdmission.Margin;
        _q = m * (1 - shape.Roundness);
        _blend = shape.Blend;
        _r = m * shape.Roundness + _blend;
        // Values past the level only place crossings; clamping above it changes neither side nor the bound.
        _clamp = _r + _h * MathF.Sqrt(3);
        _work = default;
        _uniqueEdges = 0;
        _outCount = 0;
        _fixedKeys = 0;
        _planeCount = 0;
        if (++_stamp == int.MaxValue)
        {
            _stamp = 1;
            Array.Clear(_cellStamps);
            Array.Clear(_sampleStamps);
            Array.Clear(_edgeStamps);
        }

        var low = (X: int.MaxValue, Y: int.MaxValue, Z: int.MaxValue);
        foreach (var box in design.Boxes)
            low = (Math.Min(low.X, box.At.X), Math.Min(low.Y, box.At.Y), Math.Min(low.Z, box.At.Z));
        var pad = (int)MathF.Ceiling(_q + _clamp + _blend) + 2;
        _base = (low.X - pad, low.Y - pad, low.Z - pad);
        _origin = design.Pivot;
        if (_low.Length < design.BlockCount)
        {
            _low = new Vector3[design.BlockCount];
            _high = new Vector3[design.BlockCount];
            _parent = new int[design.BlockCount];
            _volume = new long[design.BlockCount];
            _partLow = new Vector3[design.BlockCount];
            _partHigh = new Vector3[design.BlockCount];
            _partWedge = new bool[design.BlockCount];
        }

        // Faces in block units from the grid's corner: whole numbers, exact.
        var boxes = design.Boxes;
        for (var b = 0; b < boxes.Length; b++)
        {
            var box = boxes[b];
            _low[b] = new Vector3(box.At.X - _base.X, box.At.Y - _base.Y, box.At.Z - _base.Z);
            _high[b] = _low[b] + new Vector3(box.Size.X, box.Size.Y, box.Size.Z);
        }

        _shift = new Vector3((float)(_base.X - 0.5 - _origin.X), (float)(_base.Y - 0.5 - _origin.Y), (float)(_base.Z - 0.5 - _origin.Z));
    }

    private ReadOnlySpan<ulong> Alive => _alive.AsSpan(0, _aliveWords);

    /// <summary>A point in block units from the grid's corner, in the hull's frame and metres.</summary>
    private Vector3 Hull(Vector3 local) => new((local.X + _shift.X) * _size, (local.Y + _shift.Y) * _size, (local.Z + _shift.Z) * _size);

    // Keys pack three 20-bit grid indices.
    private static long Key(int i, int j, int k) => ((long)i << 40) | ((long)j << 20) | (uint)k;
    private static int Coordinate(long key, int axis) => (int)((key >> (40 - 20 * axis)) & 0xFFFFF);
    private static long With(long key, int axis, int value) => (key & ~(0xFFFFFL << (40 - 20 * axis))) | ((long)value << (40 - 20 * axis));
    private static long Step(long key, int axis, int by) => With(key, axis, Coordinate(key, axis) + by);

    /// <summary>A sample point in block units from the grid's corner: grid index i is i/3 there.</summary>
    private static Vector3 Local(long key) => new(
        Coordinate(key, 0) / (float)Resolution, Coordinate(key, 1) / (float)Resolution, Coordinate(key, 2) / (float)Resolution);

    /// <summary>A sample point in the hull's frame and metres.</summary>
    private Vector3 Point(long key) => Hull(Local(key));

    private static int Hash(long key, int mask) => (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15UL)) >> 32) & mask;

    // ---------------------------------------------------------------------------------------------------------------
    // Discovery: every cell whose corners could straddle the level lies within R of a block and is not inside one.
    // ---------------------------------------------------------------------------------------------------------------

    private bool Discover()
    {
        // A cell can straddle the level only with a corner within q + R of a block (the sample there is at least the
        // distance to the grown block), so cells reaching that far out of each block are enough.
        var reach = (int)MathF.Floor((_q + _r) / _h) + 1;
        // A wedge's slant moves out by q times its normal's support of the cube, at most q√2.
        var slanted = (int)MathF.Floor((_q * 1.5f + _r) / _h) + 1;
        var boxes = _design.Boxes;
        Span<int> at = stackalloc int[3];
        for (var b = 0; b < boxes.Length; b++)
        {
            if (!BlockSet.Has(Alive, b)) continue;
            var box = boxes[b];
            Span<int> lo = [Resolution * (box.At.X - _base.X), Resolution * (box.At.Y - _base.Y), Resolution * (box.At.Z - _base.Z)];
            Span<int> hi = [lo[0] + Resolution * box.Size.X, lo[1] + Resolution * box.Size.Y, lo[2] + Resolution * box.Size.Z];
            if (box.Shape == BlockShape.Box)
            {
                // Columns along z: inside the box's x, y range only the cells past its z faces.
                for (var i = lo[0] - reach; i < hi[0] + reach; i++)
                    for (var j = lo[1] - reach; j < hi[1] + reach; j++)
                    {
                        var through = i >= lo[0] && i < hi[0] && j >= lo[1] && j < hi[1];
                        if (!Interval(i, j, 2, lo[2] - reach, through ? lo[2] : hi[2] + reach)) return false;
                        if (through && !Interval(i, j, 2, hi[2], hi[2] + reach)) return false;
                    }
                continue;
            }

            // A wedge: columns along v; cells wholly inside the solid, or farther than the reach beyond the slant, are
            // left out, so neither the solid nor the empty half of its box is scanned.
            BlockShapes.Decode(box.Shape, out var e, out var u, out var v, out var su, out var sv);
            double su3 = hi[u] - lo[u], sv3 = hi[v] - lo[v];
            double cu = (lo[u] + hi[u]) * 0.5, cv = (lo[v] + hi[v]) * 0.5;
            var length = Math.Sqrt(su3 * su3 + sv3 * sv3);
            for (var ie = lo[e] - slanted; ie < hi[e] + slanted; ie++)
                for (var iu = lo[u] - slanted; iu < hi[u] + slanted; iu++)
                {
                    // In index units the solid is su·Sv·(u - cu) + sv·Su·(v - cv) <= 0 within the box.
                    var uFar = su > 0 ? iu + 1 : iu;
                    var uNear = su > 0 ? iu : iu + 1;
                    var start = lo[v] - slanted;
                    var end = hi[v] + slanted;
                    // Past the reach beyond the slant: lean - reach·length > 0 at the cell's nearest corner.
                    var lean = su * sv3 * (uNear - cu);
                    var limit = (slanted * length - lean) / (sv * su3) + cv;
                    if (sv > 0) end = Math.Min(end, (int)Math.Floor(limit) + 1);
                    else start = Math.Max(start, (int)Math.Ceiling(limit) - 1);
                    var inside = ie >= lo[e] && ie < hi[e] && iu >= lo[u] && iu < hi[u];
                    var solidFrom = int.MaxValue;
                    var solidTo = int.MinValue;
                    if (inside)
                    {
                        // Wholly inside: the cell's farthest corner still within the slant, and within the box.
                        var far = su * sv3 * (uFar - cu);
                        var bound = -far / (sv * su3) + cv;
                        if (sv > 0)
                        {
                            solidFrom = lo[v];
                            solidTo = Math.Min(hi[v], (int)Math.Floor(bound) - 1 + 1);
                        }
                        else
                        {
                            solidFrom = Math.Max(lo[v], (int)Math.Ceiling(bound));
                            solidTo = hi[v];
                        }
                    }

                    for (var iv = start; iv < end; iv++)
                    {
                        if (iv >= solidFrom && iv < solidTo) { iv = solidTo - 1; continue; }
                        at[e] = ie; at[u] = iu; at[v] = iv;
                        if (!AddCell(Key(at[0], at[1], at[2]))) return false;
                    }
                }
        }

        return true;
    }

    private bool Interval(int i, int j, int axis, int from, int to)
    {
        for (var k = from; k < to; k++)
            if (!AddCell(Key(i, j, k))) return false;
        return true;
    }

    private bool AddCell(long key)
    {
        if (key < 0 || (_planeCount > 0 && Skipped(key))) return true;
        var mask = _cellKeys.Length - 1;
        for (var slot = Hash(key, mask); ; slot = (slot + 1) & mask)
        {
            if (_cellStamps[slot] != _stamp)
            {
                if (_work.Cells == _maxCells) { _work.Overflow = "cells"; return false; }
                _cellStamps[slot] = _stamp;
                _cellKeys[slot] = key;
                _cellSlots[slot] = _work.Cells;
                _cellList[_work.Cells] = key;
                _cellAxis[_work.Cells] = Empty;
                _work.Cells++;
                return true;
            }
            if (_cellKeys[slot] == key) return true;
        }
    }

    private int FindCell(long key)
    {
        var mask = _cellKeys.Length - 1;
        for (var slot = Hash(key, mask); ; slot = (slot + 1) & mask)
        {
            if (_cellStamps[slot] != _stamp) return -1;
            if (_cellKeys[slot] == key) return _cellSlots[slot];
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Separate box-shaped parts: triangulated rounded boxes with analytic normals.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Arc segments a quarter turn takes on a rounded box. At three, an edge's chord falls short of its arc by at most
    /// 1 - 3/√10 < 0.0514 of the radius, and a corner's triangles by at most 0.0751 of it (worked out over the
    /// corner's nine triangles); every vertex is on the surface with its exact normal.
    /// </summary>
    private const int Arc = 3;

    /// <summary>
    /// Finds the parts of the union that are boxes standing apart, triangulates each rounded target and takes its blocks
    /// out of the sampling. Blocks are one part when their boxes come within 2(q + R + blend) of each other along every
    /// axis; parts further apart along some axis than that are further apart than 2R + 2·blend once grown by q, so the
    /// closing never joins them, and a convex part's closing is just the part grown by q, then round by q's
    /// complement ρ = m·roundness: a rounded box. A part is a box when it has no wedge and its blocks, which never
    /// overlap, fill its bounding box exactly. Only the union decides, so cubes filling a box and the box itself
    /// take this alike. False when the indices run out.
    /// </summary>
    private bool Boxes(Span<ushort> output, ref int count)
    {
        var boxes = _design.Boxes;
        var gap = 2 * (_q + _r + _blend) + 1e-3f;
        for (var b = 0; b < boxes.Length; b++)
            _parent[b] = b;
        var pad = new Vector3((gap + 0.01f) * _size);
        for (var b = 0; b < boxes.Length; b++)
        {
            if (!BlockSet.Has(Alive, b)) continue;
            var cursor = 0;
            var granted = Grant();
            var visits = granted;
            var low = Hull(_low[b]) - pad;
            var high = Hull(_high[b]) + pad;
            while (_design.Collision.Next(ref cursor, low, high, Alive, ref visits, out var id))
            {
                if (id <= b) continue;
                var near = true;
                for (var axis = 0; axis < 3 && near; axis++)
                    near = Math.Max(_low[id][axis] - _high[b][axis], _low[b][axis] - _high[id][axis]) <= gap;
                if (near) Join(b, id);
            }

            if (!Spend(granted, visits, cursor)) return false;
        }

        // Each part's bounding box, volume and whether it has a wedge, kept at its root.
        for (var b = 0; b < boxes.Length; b++)
        {
            if (!BlockSet.Has(Alive, b)) continue;
            var root = Find(b);
            _partLow[root] = new Vector3(float.MaxValue);
            _partHigh[root] = new Vector3(float.MinValue);
            _volume[root] = 0;
            _partWedge[root] = false;
        }

        for (var b = 0; b < boxes.Length; b++)
        {
            if (!BlockSet.Has(Alive, b)) continue;
            var root = Find(b);
            var box = boxes[b];
            _partLow[root] = Vector3.Min(_partLow[root], _low[b]);
            _partHigh[root] = Vector3.Max(_partHigh[root], _high[b]);
            _volume[root] += (long)box.Size.X * box.Size.Y * box.Size.Z;
            _partWedge[root] |= box.Shape != BlockShape.Box;
        }

        for (var b = 0; b < boxes.Length; b++)
        {
            if (!BlockSet.Has(Alive, b) || Find(b) != b || _partWedge[b]) continue;
            var size = _partHigh[b] - _partLow[b];
            if ((long)size.X * (long)size.Y * (long)size.Z != _volume[b]) continue;
            if (!RoundedBox(_partLow[b], _partHigh[b], output, ref count)) return false;
            _work.RoundedBoxes++;
            // Marked drawn: its blocks leave the sampling below.
            _volume[b] = -1;
        }

        for (var b = 0; b < boxes.Length; b++)
            if (BlockSet.Has(Alive, b) && _volume[Find(b)] == -1)
                _alive[b >> 6] &= ~(1UL << (b & 63));
        return true;
    }
    private int Find(int b)
    {
        while (_parent[b] != b)
        {
            _parent[b] = _parent[_parent[b]];
            b = _parent[b];
        }

        return b;
    }

    private void Join(int a, int b)
    {
        a = Find(a);
        b = Find(b);
        if (a != b) _parent[Math.Max(a, b)] = Math.Min(a, b);
    }

    /// <summary>
    /// A box from <paramref name="low"/> to <paramref name="high"/> in block units, grown by q, then round by ρ: six
    /// flat faces, twelve quarter cylinders of <see cref="Arc"/> strips and eight eighths of a sphere of
    /// <see cref="Arc"/>² triangles, every vertex on the exact surface with its exact normal. 80 vertices and 468
    /// indices at three segments. False when the indices or vertices run out.
    /// </summary>
    private bool RoundedBox(Vector3 low, Vector3 high, Span<ushort> output, ref int count)
    {
        var rho = (_r - _blend) * _size;
        var lo = Hull(low - new Vector3(_q));
        var hi = Hull(high + new Vector3(_q));
        var centre = (lo + hi) * 0.5f;
        var half = (hi - lo) * 0.5f;
        const int Grid = Arc + 1;
        // A corner's points by (n0, n1), n2 = Arc - n0 - n1: the direction (n0·sx, n1·sy, n2·sz), normalised.
        Span<Mark> marks = stackalloc Mark[8 * Grid * Grid];
        for (var corner = 0; corner < 8; corner++)
        {
            var sign = new Vector3((corner & 1) * 2 - 1, (corner >> 1 & 1) * 2 - 1, (corner >> 2 & 1) * 2 - 1);
            var at = centre + sign * half;
            for (var n0 = 0; n0 <= Arc; n0++)
                for (var n1 = 0; n0 + n1 <= Arc; n1++)
                {
                    var direction = Vector3.Normalize(new Vector3(n0, n1, Arc - n0 - n1) * sign);
                    var mark = FixedVertex(at + direction * rho, direction);
                    if (mark.Slot < 0) return false;
                    marks[(corner * Grid + n0) * Grid + n1] = mark;
                }
        }

        // The eighths of a sphere.
        for (var corner = 0; corner < 8; corner++)
            for (var n0 = 0; n0 < Arc; n0++)
                for (var n1 = 0; n0 + n1 < Arc; n1++)
                {
                    if (!Out3(output, ref count, centre, Corner(marks, corner, n0, n1), Corner(marks, corner, n0 + 1, n1), Corner(marks, corner, n0, n1 + 1))) return false;
                    if (n0 + n1 + 2 <= Arc && !Out3(output, ref count, centre, Corner(marks, corner, n0 + 1, n1), Corner(marks, corner, n0 + 1, n1 + 1), Corner(marks, corner, n0, n1 + 1))) return false;
                }

        // The quarter cylinders: along each axis, between the two corners that differ only in it.
        Span<int> n = stackalloc int[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            for (var side = 0; side < 4; side++)
            {
                var a = (side & 1) << u | (side >> 1 & 1) << v;
                var b = a | 1 << axis;
                for (var t = 0; t < Arc; t++)
                {
                    n[axis] = 0; n[u] = Arc - t; n[v] = t;
                    var a0 = Corner(marks, a, n[0], n[1]);
                    var b0 = Corner(marks, b, n[0], n[1]);
                    n[u] = Arc - t - 1; n[v] = t + 1;
                    var a1 = Corner(marks, a, n[0], n[1]);
                    var b1 = Corner(marks, b, n[0], n[1]);
                    if (!Out3(output, ref count, centre, a0, b0, b1) || !Out3(output, ref count, centre, a0, b1, a1)) return false;
                }
            }
        }

        // The flat faces: the four corners' points straight out along the face's axis.
        for (var axis = 0; axis < 3; axis++)
        {
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            n[axis] = Arc; n[u] = 0; n[v] = 0;
            for (var top = 0; top < 2; top++)
            {
                var c00 = Corner(marks, top << axis, n[0], n[1]);
                var c10 = Corner(marks, top << axis | 1 << u, n[0], n[1]);
                var c11 = Corner(marks, top << axis | 1 << u | 1 << v, n[0], n[1]);
                var c01 = Corner(marks, top << axis | 1 << v, n[0], n[1]);
                if (!Out3(output, ref count, centre, c00, c10, c11) || !Out3(output, ref count, centre, c00, c11, c01)) return false;
            }
        }

        return true;
    }

    private static Mark Corner(Span<Mark> marks, int corner, int n0, int n1) => marks[(corner * (Arc + 1) + n0) * (Arc + 1) + n1];

    /// <summary>A triangle of a rounded box, facing away from its centre.</summary>
    private bool Out3(Span<ushort> output, ref int count, Vector3 centre, Mark a, Mark b, Mark c)
    {
        count = Triangle(output, count, a, b, c, (a.Position + b.Position + c.Position) / 3 - centre);
        return count >= 0;
    }

    /// <summary>A vertex of its own with a normal given, not gathered from its triangles.</summary>
    private Mark FixedVertex(Vector3 position, Vector3 normal)
    {
        long a = long.MinValue + _fixedKeys++, b = 0;
        var mask = _edgeA.Length - 1;
        for (var slot = Hash(a * 31 + b, mask); ; slot = (slot + 1) & mask)
        {
            if (_edgeStamps[slot] == _stamp) continue;
            if (_uniqueEdges * 2 >= _edgeA.Length) { _work.Overflow = "vertices"; return new Mark(-1, position); }
            _edgeStamps[slot] = _stamp;
            _edgeA[slot] = a;
            _edgeB[slot] = b;
            _edgePositions[slot] = position;
            _edgeNormals[slot] = normal;
            _slotOut[slot] = -1;
            _slotFixed[slot] = true;
            _uniqueEdges++;
            return new Mark(slot, position);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Flat faces: their middles are drawn whole and never sampled.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A flat stretch of shell: on a box face across <see cref="Axis"/> facing <see cref="Sign"/>, in the cell layer
    /// <see cref="Layer"/>, over cells [U0, U1) x [W0, W1) of the two other axes, in that cyclic order.
    /// </summary>
    private struct Plane
    {
        public int Axis, Sign, Layer, U0, U1, W0, W1;
    }

    private readonly Plane[] _planes = new Plane[4096];
    private int _planeCount;
    private readonly Rect[] _pieces = new Rect[512];
    private readonly Rect[] _grown = new Rect[512];
    private readonly long[] _faceEntries = new long[6 * GeometryAdmission.MaxBlocks];
    private readonly float[] _edges = new float[1024];
    private readonly (float, float)[] _runs = new (float, float)[1024];
    private readonly Rect[] _canon = new Rect[512];
    private readonly Mark[] _ring = new Mark[1 << 16];

    /// <summary>A rectangle across a face, in block units from the grid's corner: X, Y low, Z, W high.</summary>
    private readonly record struct Rect(float X, float Y, float Z, float W);

    /// <summary>
    /// Finds where the shell is a plane for certain: over a box face, the points p whose square of half side D
    /// (the reach of every sample's field: the clamp, the blend, the cube growth and a cell) has, just below the face,
    /// only faces of boxes in this plane facing the same way, and above it, up to D, nothing at all. There the closest
    /// solid to every point of the band is straight below, so the sampled field is height - q + blend exactly, cover
    /// and all, and its level is the plane m off the face. Each such stretch, in whole cells and a cell in from its
    /// edges, is drawn as one fan and none of its cells is sampled; the ring of cells around it is, and the fan takes
    /// their vertices on its edges.
    /// </summary>
    /// <summary>
    /// The cell layers a stretch leaves unsampled out from its face: the layers the face's own band of discovery
    /// reaches (see <see cref="Discover"/>), the only ones a block alone could put cells in there.
    /// </summary>
    private int FlatDepth => (int)MathF.Floor((_q + _r) / _h) + 1;

    /// <summary>
    /// Finds the flat stretches, plane by plane of the union, never block by block, so one box and the same box built
    /// of smaller ones find the same stretches. The certificate must cover every point whose field any skipped sample
    /// depends on: the skipped layers reach <see cref="FlatDepth"/> cells out, and a sample's field reaches the clamp,
    /// the blend and the cube growth further. So across the face the square checked has half side that reach plus a
    /// cell, and out from the face the slab checked is the skipped layers plus that reach. False when the tree-visit
    /// budget runs out, which stops the build; a plane too busy to check is simply sampled.
    /// </summary>
    private bool Flats()
    {
        var across = _clamp + _blend + _q + _h;
        var outward = FlatDepth * _h + _clamp + _blend + _q;
        // Every box face, as its plane (axis, side, whole grid coordinate) and its block, sorted so a plane's faces meet.
        var boxes = _design.Boxes;
        var faces = 0;
        for (var b = 0; b < boxes.Length; b++)
        {
            if (!BlockSet.Has(Alive, b) || boxes[b].Shape != BlockShape.Box) continue;
            for (var axis = 0; axis < 3; axis++)
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    if (faces == _faceEntries.Length) return true;
                    var at = (int)(sign > 0 ? _high[b][axis] : _low[b][axis]);
                    _faceEntries[faces++] = ((long)((axis * 2 + (sign > 0 ? 1 : 0)) * (1 << 21) + at) << 20) | (uint)b;
                }
        }

        var entries = _faceEntries.AsSpan(0, faces);
        entries.Sort();
        for (var start = 0; start < faces;)
        {
            var plane = entries[start] >> 20;
            var end = start + 1;
            while (end < faces && entries[end] >> 20 == plane) end++;
            if (!FlatPlane(entries[start..end], across, outward)) return false;
            start = end;
        }

        return true;
    }

    /// <summary>What is left of the tree-visit budget for one query, up to a whole walk of the tree.</summary>
    private int Grant() => (int)Math.Max(0, Math.Min(2L * _design.BlockCount, _maxVisits - _work.Visits));

    /// <summary>Charges a query's visits; false, with the overflow named, when it ran out before the walk ended.</summary>
    private bool Spend(int granted, int left, int cursor)
    {
        _work.Visits += granted - left;
        if (left > 0 || cursor >= _design.Collision.NodeCount) return true;
        _work.Overflow ??= "visits";
        return false;
    }

    /// <summary>
    /// The flat stretches of one plane: where, within a square of half side <paramref name="reach"/> about a point,
    /// the plane's own faces cover everything just below it and nothing is above it out to <paramref name="outward"/>.
    /// The region is cut canonically (<see cref="Canonical"/>), so its rectangles depend on the region alone.
    /// </summary>
    private bool FlatPlane(ReadOnlySpan<long> faces, float reach, float outward)
    {
        var code = (int)(faces[0] >> 20 >> 21);
        var axis = code / 2;
        var sign = code % 2 == 1 ? 1 : -1;
        var u = (axis + 1) % 3;
        var w = (axis + 2) % 3;
        var first = (int)(faces[0] & 0xFFFFF);
        var face = sign > 0 ? _high[first][axis] : _low[first][axis];
        var bounds = new Rect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        foreach (var entry in faces)
        {
            var b = (int)(entry & 0xFFFFF);
            bounds = new Rect(Math.Min(bounds.X, _low[b][u]), Math.Min(bounds.Y, _low[b][w]), Math.Max(bounds.Z, _high[b][u]), Math.Max(bounds.W, _high[b][w]));
        }

        // Too narrow for a stretch to stay after the reach is taken off both sides.
        if (bounds.Z - bounds.X <= 2 * reach + 3 * _h || bounds.W - bounds.Y <= 2 * reach + 3 * _h) return true;
        var region = new Rect(bounds.X - reach, bounds.Y - reach, bounds.Z + reach, bounds.W + reach);

        // What below the plane is not its faces, grown by the reach: the region less every face of the plane.
        var pieces = 1;
        _pieces[0] = region;
        foreach (var entry in faces)
        {
            var b = (int)(entry & 0xFFFFF);
            if (!Subtract(_pieces, ref pieces, new Rect(_low[b][u], _low[b][w], _high[b][u], _high[b][w]))) return true;
        }

        var grown = 0;
        for (var i = 0; i < pieces; i++)
        {
            if (grown == _grown.Length) return true;
            var p = _pieces[i];
            _grown[grown++] = new Rect(p.X - reach, p.Y - reach, p.Z + reach, p.W + reach);
        }

        // And anything above the plane out to the slab's depth, grown by the reach, from the tree in metres.
        Span<float> lo = stackalloc float[3];
        Span<float> hi = stackalloc float[3];
        lo[axis] = sign > 0 ? face : face - outward; hi[axis] = sign > 0 ? face + outward : face;
        lo[u] = region.X; hi[u] = region.Z;
        lo[w] = region.Y; hi[w] = region.W;
        var low = Hull(new Vector3(lo[0], lo[1], lo[2])) - new Vector3(0.01f * _size);
        var high = Hull(new Vector3(hi[0], hi[1], hi[2])) + new Vector3(0.01f * _size);
        var cursor = 0;
        var granted = Grant();
        var visits = granted;
        var crowded = false;
        while (_design.Collision.Next(ref cursor, low, high, Alive, ref visits, out var c))
        {
            var above = sign > 0 ? _low[c][axis] < face + outward && _high[c][axis] > face : _high[c][axis] > face - outward && _low[c][axis] < face;
            if (!above) continue;
            if (grown == _grown.Length) { crowded = true; break; }
            _grown[grown++] = new Rect(_low[c][u] - reach, _low[c][w] - reach, _high[c][u] + reach, _high[c][w] + reach);
        }

        // Too much near to check: the plane is sampled as usual; the visits made are charged all the same.
        if (crowded) { _work.Visits += granted - visits; return true; }
        if (!Spend(granted, visits, cursor)) return false;

        // What is left of the plane's faces.
        pieces = 0;
        foreach (var entry in faces)
        {
            if (pieces == _pieces.Length) return true;
            var b = (int)(entry & 0xFFFFF);
            _pieces[pieces++] = new Rect(_low[b][u], _low[b][w], _high[b][u], _high[b][w]);
        }

        for (var i = 0; i < grown && pieces > 0; i++)
            if (!Subtract(_pieces, ref pieces, _grown[i])) return true;
        var cut = Canonical(pieces);
        for (var i = 0; i < cut && _planeCount < _planes.Length; i++)
        {
            // Whole cells, then a cell in from every side, so a sampled ring always parts two stretches.
            var p = _canon[i];
            var plane = new Plane
            {
                Axis = axis,
                Sign = sign,
                Layer = sign > 0 ? (int)MathF.Round(face * Resolution) : (int)MathF.Round(face * Resolution) - 1,
                U0 = (int)MathF.Ceiling(p.X * Resolution) + 1,
                U1 = (int)MathF.Floor(p.Z * Resolution) - 1,
                W0 = (int)MathF.Ceiling(p.Y * Resolution) + 1,
                W1 = (int)MathF.Floor(p.W * Resolution) - 1,
            };
            if (plane.U1 - plane.U0 < 2 || plane.W1 - plane.W0 < 2) continue;
            var overlaps = false;
            for (var j = 0; j < _planeCount && !overlaps; j++)
                overlaps = Overlap(plane, _planes[j]);
            if (!overlaps)
                _planes[_planeCount++] = plane;
        }

        return true;
    }

    /// <summary>
    /// Cuts the region the first <paramref name="pieces"/> of <see cref="_pieces"/> cover (they are disjoint) into
    /// rectangles that depend on the region alone: bands between the distinct edges across w, each band's covered runs
    /// along u merged, and neighbouring bands with the same runs merged. Writes them to <see cref="_canon"/>; 0 when
    /// there are too many to cut, and the plane is sampled instead.
    /// </summary>
    private int Canonical(int pieces)
    {
        if (pieces == 0 || 2 * pieces > _edges.Length) return 0;
        var edges = 0;
        for (var i = 0; i < pieces; i++)
        {
            _edges[edges++] = _pieces[i].Y;
            _edges[edges++] = _pieces[i].W;
        }

        var bands = _edges.AsSpan(0, edges);
        bands.Sort();
        var distinct = 1;
        for (var i = 1; i < edges; i++)
            if (bands[i] != bands[distinct - 1]) bands[distinct++] = bands[i];

        var count = 0;
        var openFrom = 0;
        var openRuns = 0;
        var openBand = -1;
        for (var band = 0; band + 1 < distinct; band++)
        {
            // This band's runs along u, merged where they touch.
            var runs = 0;
            for (var i = 0; i < pieces; i++)
            {
                var p = _pieces[i];
                if (p.Y > bands[band] || p.W < bands[band + 1]) continue;
                if (openFrom + openRuns + runs >= _runs.Length) return 0;
                _runs[openFrom + openRuns + runs++] = (p.X, p.Z);
            }

            var span = _runs.AsSpan(openFrom + openRuns, runs);
            span.Sort();
            var merged = 0;
            for (var i = 0; i < runs; i++)
            {
                if (merged > 0 && span[i].Item1 <= span[merged - 1].Item2)
                    span[merged - 1] = (span[merged - 1].Item1, Math.Max(span[merged - 1].Item2, span[i].Item2));
                else
                    span[merged++] = span[i];
            }

            // The same runs as the band open below: it goes on. Otherwise the open band is closed into rectangles.
            var same = openBand >= 0 && merged == openRuns;
            for (var i = 0; same && i < merged; i++)
                same = span[i] == _runs[openFrom + i];
            if (same) continue;
            if (openBand >= 0 && !Close(openFrom, openRuns, bands[openBand], bands[band], ref count)) return 0;
            for (var i = 0; i < merged; i++)
                _runs[i] = span[i];
            openFrom = 0;
            openRuns = merged;
            openBand = merged > 0 ? band : -1;
        }

        if (openBand >= 0 && !Close(openFrom, openRuns, bands[openBand], bands[distinct - 1], ref count)) return 0;
        return count;
    }

    private bool Close(int from, int runs, float low, float high, ref int count)
    {
        for (var i = 0; i < runs; i++)
        {
            if (count == _canon.Length) return false;
            _canon[count++] = new Rect(_runs[from + i].Item1, low, _runs[from + i].Item2, high);
        }

        return true;
    }

    /// <summary>Takes a rectangle out of every piece, leaving up to four each; false when the list is full.</summary>
    private static bool Subtract(Rect[] pieces, ref int n, Rect cover)
    {
        for (var r = n - 1; r >= 0; r--)
        {
            var p = pieces[r];
            float x0 = Math.Max(p.X, cover.X), y0 = Math.Max(p.Y, cover.Y), x1 = Math.Min(p.Z, cover.Z), y1 = Math.Min(p.W, cover.W);
            if (x0 >= x1 || y0 >= y1) continue;
            pieces[r] = pieces[--n];
            if (n + 4 > pieces.Length) return false;
            if (p.X < x0) pieces[n++] = new Rect(p.X, p.Y, x0, p.W);
            if (x1 < p.Z) pieces[n++] = new Rect(x1, p.Y, p.Z, p.W);
            if (p.Y < y0) pieces[n++] = new Rect(x0, p.Y, x1, y0);
            if (y1 < p.W) pieces[n++] = new Rect(x0, y1, x1, p.W);
        }
        return true;
    }

    /// <summary>The cells a stretch leaves unsampled: its layer and every layer out to the reach, over its cells.</summary>
    private void Volume(in Plane plane, Span<int> low, Span<int> high)
    {
        var u = (plane.Axis + 1) % 3;
        var w = (plane.Axis + 2) % 3;
        var depth = FlatDepth;
        low[plane.Axis] = plane.Sign > 0 ? plane.Layer : plane.Layer - depth + 1;
        high[plane.Axis] = plane.Sign > 0 ? plane.Layer + depth - 1 : plane.Layer;
        low[u] = plane.U0; high[u] = plane.U1 - 1;
        low[w] = plane.W0; high[w] = plane.W1 - 1;
    }

    private bool Overlap(in Plane a, in Plane b)
    {
        Span<int> al = stackalloc int[3];
        Span<int> ah = stackalloc int[3];
        Span<int> bl = stackalloc int[3];
        Span<int> bh = stackalloc int[3];
        Volume(a, al, ah);
        Volume(b, bl, bh);
        // A cell of margin each way, so two stretches never share a ring.
        for (var i = 0; i < 3; i++)
            if (al[i] > bh[i] + 2 || bl[i] > ah[i] + 2) return false;
        return true;
    }

    private bool Skipped(long key)
    {
        Span<int> low = stackalloc int[3];
        Span<int> high = stackalloc int[3];
        for (var i = 0; i < _planeCount; i++)
        {
            Volume(_planes[i], low, high);
            var inside = true;
            for (var axis = 0; axis < 3 && inside; axis++)
            {
                var c = Coordinate(key, axis);
                inside = c >= low[axis] && c <= high[axis];
            }
            if (inside) return true;
        }
        return false;
    }

    /// <summary>The stretch whose layer has this cell, or -1.</summary>
    private int PlaneAt(long key)
    {
        for (var i = 0; i < _planeCount; i++)
        {
            ref readonly var plane = ref _planes[i];
            var u = (plane.Axis + 1) % 3;
            var w = (plane.Axis + 2) % 3;
            if (Coordinate(key, plane.Axis) == plane.Layer && Coordinate(key, u) >= plane.U0 && Coordinate(key, u) < plane.U1
                && Coordinate(key, w) >= plane.W0 && Coordinate(key, w) < plane.W1)
                return i;
        }
        return -1;
    }

    /// <summary>A stretch's cells along an axis lying in its face.</summary>
    private static (int From, int To) Extent(in Plane plane, int axis) =>
        axis == (plane.Axis + 1) % 3 ? (plane.U0, plane.U1) : (plane.W0, plane.W1);

    private long PlaneKey(in Plane plane, int layer, int u, int w)
    {
        Span<int> at = stackalloc int[3];
        at[plane.Axis] = layer;
        at[(plane.Axis + 1) % 3] = u;
        at[(plane.Axis + 2) % 3] = w;
        return Key(at[0], at[1], at[2]);
    }

    /// <summary>
    /// Draws a stretch as a fan from its middle to the vertices on its four edges: every edge cut wherever the piece
    /// across it has a vertex, as a run's lines are.
    /// </summary>
    private int Flat(int index, Span<ushort> output, int count)
    {
        ref readonly var plane = ref _planes[index];
        var u = (plane.Axis + 1) % 3;
        var w = (plane.Axis + 2) % 3;
        var ring = 0;
        // Sides in turn: u = U0 along w, w = W1 along u, u = U1 back along w, w = W0 back along u.
        for (var side = 0; side < 4; side++)
        {
            var alongW = side % 2 == 0;
            var axis = alongW ? w : u;
            var fixedAt = side switch { 0 => plane.U0, 1 => plane.W1, 2 => plane.U1, _ => plane.W0 };
            var across = side switch { 0 => plane.U0 - 1, 1 => plane.W1, 2 => plane.U1, _ => plane.W0 - 1 };
            var start = alongW ? plane.W0 : plane.U0;
            var end = alongW ? plane.W1 : plane.U1;
            var low = alongW ? PlaneKey(plane, plane.Layer, fixedAt, start) : PlaneKey(plane, plane.Layer, start, fixedAt);
            var high = alongW ? PlaneKey(plane, plane.Layer + 1, fixedAt, start) : PlaneKey(plane, plane.Layer + 1, start, fixedAt);
            var neighbour = alongW ? PlaneKey(plane, plane.Layer, across, start) : PlaneKey(plane, plane.Layer, start, across);
            var vl = SampleValue(low);
            var vh = SampleValue(high);
            var fraction = Math.Clamp((_r - vl) / (vh - vl), 1e-4f, 1 - 1e-4f);
            var n = 0;
            _breakA[n++] = start;
            if (!Breaks(neighbour, axis, start, end, fraction, _breakA, ref n)) return -1;
            n = Finish(end, _breakA, n);
            if (n < 0) return -1;
            var backward = side >= 2;
            for (var i = 0; i < n - 1; i++)
            {
                if (ring == _ring.Length) return -1;
                _ring[ring++] = At(low, high, axis, _breakA[backward ? n - 1 - i : i]);
            }
        }

        // The middle on the plane the ring's vertices are on: the sampled crossing of the layer, which every edge
        // across it shares, not the target's height, which the interpolation need not reach.
        var middle = Vector3.Zero;
        var bottom = PlaneKey(plane, plane.Layer, plane.U0, plane.W0);
        var top = PlaneKey(plane, plane.Layer + 1, plane.U0, plane.W0);
        var vb = SampleValue(bottom);
        var vt = SampleValue(top);
        var crossing = Math.Clamp((_r - vb) / (vt - vb), 1e-4f, 1 - 1e-4f);
        middle[plane.Axis] = (plane.Layer + crossing) / Resolution;
        middle[u] = (plane.U0 + plane.U1) * 0.5f / Resolution;
        middle[w] = (plane.W0 + plane.W1) * 0.5f / Resolution;
        var centre = Centre(index, Hull(middle));
        var outward = Vector3.Zero;
        outward[plane.Axis] = plane.Sign;
        for (var i = 0; i < ring && count >= 0; i++)
            count = Triangle(output, count, centre, _ring[i], _ring[(i + 1) % ring], outward);
        return count;
    }

    /// <summary>A vertex of its own for a stretch's middle.</summary>
    private Mark Centre(int index, Vector3 position)
    {
        long a = -1 - index, b = -1;
        var mask = _edgeA.Length - 1;
        for (var slot = Hash(a * 31 + b, mask); ; slot = (slot + 1) & mask)
        {
            if (_edgeStamps[slot] != _stamp)
            {
                if (_uniqueEdges * 2 >= _edgeA.Length) { _work.Overflow = "vertices"; return default; }
                _edgeStamps[slot] = _stamp;
                _edgeA[slot] = a;
                _edgeB[slot] = b;
                _edgePositions[slot] = position;
                _edgeNormals[slot] = Vector3.Zero;
                _slotOut[slot] = -1;
                _slotFixed[slot] = false;
                _uniqueEdges++;
                return new Mark(slot, position);
            }
            if (_edgeA[slot] == a && _edgeB[slot] == b) return new Mark(slot, position);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Sampling.
    // ---------------------------------------------------------------------------------------------------------------

    private bool Sample()
    {
        for (var c = 0; c < _work.Cells; c++)
        {
            var key = _cellList[c];
            for (var corner = 0; corner < 8; corner++)
            {
                var at = Key(Coordinate(key, 0) + (corner & 1), Coordinate(key, 1) + (corner >> 1 & 1), Coordinate(key, 2) + (corner >> 2 & 1));
                var slot = SampleSlot(at);
                if (slot < 0) return false;
                _cellCorner[c * CornerOffsets + corner] = slot;
            }
        }

        return _work.Overflow is null;
    }

    private int SampleSlot(long key)
    {
        var mask = _sampleKeys.Length - 1;
        for (var slot = Hash(key, mask); ; slot = (slot + 1) & mask)
        {
            if (_sampleStamps[slot] != _stamp)
            {
                if (_work.Samples == _maxSamples) { _work.Overflow = "samples"; return -1; }
                _work.Samples++;
                _sampleStamps[slot] = _stamp;
                _sampleKeys[slot] = key;
                _sampleValues[slot] = Field(Local(key));
                return _work.Overflow is null ? slot : -1;
            }
            if (_sampleKeys[slot] == key) return slot;
        }
    }

    /// <summary>
    /// The field sampled at a point: the clamped maximum, over the fixed cover <see cref="Ball"/> of the blend ball,
    /// of the distance to the nearest grown block. It is one function everywhere, the maximum of 1-Lipschitz
    /// functions, so it is 1-Lipschitz itself, and F - δ ≤ it ≤ F for the closing field F, with δ ≤ 0.4766 of the
    /// blend for this cover. Blocks are tried nearest first, and one that cannot come nearer at an offset than the
    /// nearest found so far (its distance at the point less the offset, with a margin for rounding) is not tried
    /// there: the result is the same as trying every one. Each distance is charged before it is worked out; the build
    /// stops at the first one past the budget. NaN when it does.
    /// </summary>
    private float Field(Vector3 p)
    {
        // The tree is in metres about the pivot; a little more reach covers the change of frame's rounding.
        var reach = new Vector3((_clamp + _blend + _q + 0.01f) * _size);
        var at0 = Hull(p);
        var count = 0;
        var cursor = 0;
        var granted = Grant();
        var visits = granted;
        while (_design.Collision.Next(ref cursor, at0 - reach, at0 + reach, Alive, ref visits, out var id))
        {
            if (count == _near.Length)
            {
                _work.Visits += granted - visits;
                _work.Overflow = "neighbours";
                return float.NaN;
            }
            _near[count++] = id;
        }
        if (!Spend(granted, visits, cursor)) return float.NaN;
        if (count == 0) return _clamp;
        if (!Charge(count)) return float.NaN;

        // Nearest first, ties by block number, so the order never depends on how the tree was walked.
        for (var i = 0; i < count; i++)
        {
            var d = Distance(_near[i], p);
            var id = _near[i];
            var j = i;
            for (; j > 0 && (_nearDistance[j - 1] > d || (_nearDistance[j - 1] == d && _near[j - 1] > id)); j--)
            {
                _nearDistance[j] = _nearDistance[j - 1];
                _near[j] = _near[j - 1];
            }
            _nearDistance[j] = d;
            _near[j] = id;
        }

        // The centre of the cover is the point itself, already done.
        var best = Math.Min(_clamp, _nearDistance[0]);
        var slack = 1e-4f;
        for (var o = 1; o < Ball.Length; o++)
        {
            var offset = Ball[o];
            var radius = BallRadius[o] * _blend;
            var at = new Vector3(p.X + offset.X * _blend, p.Y + offset.Y * _blend, p.Z + offset.Z * _blend);
            var low = _clamp;
            for (var i = 0; i < count; i++)
            {
                if (_nearDistance[i] - radius > low + slack) break;
                if (!Charge(1)) return float.NaN;
                low = Math.Min(low, Distance(_near[i], at));
            }
            best = Math.Max(best, low);
        }

        return best;
    }

    private bool Charge(int distances)
    {
        if (_work.Distances + distances > _maxDistances)
        {
            _work.Overflow = "distances";
            return false;
        }

        _work.Distances += distances;
        return true;
    }

    /// <summary>
    /// The distance from a point to a block grown square by q, in plain scalar arithmetic in a fixed order, so every
    /// machine gets the same bits. A box's faces come from its whole grid coordinates by one formula, so two blocks
    /// sharing a face plane give it the same bits and a box split into cubes measures as the box does.
    /// </summary>
    private float Distance(int block, Vector3 p)
    {
        var lo = _low[block];
        var hi = _high[block];
        if (_design.Shapes[block] == BlockShape.Box)
        {
            var dx = MathF.Max(MathF.Max(lo.X - _q - p.X, p.X - hi.X - _q), 0f);
            var dy = MathF.Max(MathF.Max(lo.Y - _q - p.Y, p.Y - hi.Y - _q), 0f);
            var dz = MathF.Max(MathF.Max(lo.Z - _q - p.Z, p.Z - hi.Z - _q), 0f);
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        var c = Wedge(block, p);
        float ex = p.X - c.X, ey = p.Y - c.Y, ez = p.Z - c.Z;
        return MathF.Sqrt(ex * ex + ey * ey + ez * ez);
    }

    /// <summary>
    /// The nearest point of a wedge grown by a cube of half side q, in scalars: its box grown by q, cut by its slant
    /// moved out by the cube's support along the slant's normal (a prism and a cube add no other face directions),
    /// so the point is the box's nearest, or else the nearest on that slant's rectangle.
    /// </summary>
    private Vector3 Wedge(int block, Vector3 p)
    {
        BlockShapes.Decode(_design.Shapes[block], out var e, out var u, out var v, out var su, out var sv);
        Span<float> lo = [_low[block].X - _q, _low[block].Y - _q, _low[block].Z - _q];
        Span<float> hi = [_high[block].X + _q, _high[block].Y + _q, _high[block].Z + _q];
        Span<float> at = [p.X, p.Y, p.Z];
        Span<float> mid = [(lo[0] + hi[0]) * 0.5f, (lo[1] + hi[1]) * 0.5f, (lo[2] + hi[2]) * 0.5f];
        // The slant's unit normal from the block's own half extents; the cut is q times its support of the cube.
        var hu = (_high[block][u] - _low[block][u]) * 0.5f;
        var hv = (_high[block][v] - _low[block][v]) * 0.5f;
        var length = MathF.Sqrt(hv * hv + hu * hu);
        float nu = su * hv / length, nv = sv * hu / length;
        var cut = _q * (MathF.Abs(nu) + MathF.Abs(nv));
        Span<float> c = stackalloc float[3];
        for (var i = 0; i < 3; i++) c[i] = Math.Clamp(at[i], lo[i], hi[i]);
        if (nu * (c[u] - mid[u]) + nv * (c[v] - mid[v]) <= cut)
            return new Vector3(c[0], c[1], c[2]);
        // On the moved slant: along t = (-nv, nu) across, the box along e.
        float tu = -nv, tv = nu;
        var s = (at[u] - mid[u]) * tu + (at[v] - mid[v]) * tv;
        var low = Math.Max(Math.Min((lo[u] - mid[u] - cut * nu) / tu, (hi[u] - mid[u] - cut * nu) / tu),
            Math.Min((lo[v] - mid[v] - cut * nv) / tv, (hi[v] - mid[v] - cut * nv) / tv));
        var high = Math.Min(Math.Max((lo[u] - mid[u] - cut * nu) / tu, (hi[u] - mid[u] - cut * nu) / tu),
            Math.Max((lo[v] - mid[v] - cut * nv) / tv, (hi[v] - mid[v] - cut * nv) / tv));
        s = Math.Clamp(s, low, high);
        c[u] = mid[u] + nu * cut + tu * s;
        c[v] = mid[v] + nv * cut + tv * s;
        c[e] = Math.Clamp(at[e], lo[e], hi[e]);
        return new Vector3(c[0], c[1], c[2]);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Classification: which cells the surface passes, and along which axis, if any, their field cannot change.
    // ---------------------------------------------------------------------------------------------------------------

    private float Value(int cell, int corner) => _sampleValues[_cellCorner[cell * CornerOffsets + corner]];

    private bool Classify()
    {
        for (var c = 0; c < _work.Cells; c++)
        {
            var inside = 0;
            for (var corner = 0; corner < 8; corner++)
                inside += Value(c, corner) <= _r ? 1 : 0;
            if (inside is 0 or 8) continue;
            _work.SurfaceCells++;
            _cellAxis[c] = Single;
            _bestRun[c] = 0;
            byte axes = 0;
            for (var axis = 0; axis < 3; axis++)
                if (Invariant(c, axis)) axes |= (byte)(1 << axis);
            _invariant[c] = axes;
        }

        // A cell equal along two axes (a flat stretch) takes the one along which such cells run furthest, so the
        // strips beside a long edge or a flat fan run along it and meet it at their ends only.
        for (var axis = 0; axis < 3; axis++)
        {
            var bit = (byte)(1 << axis);
            for (var c = 0; c < _work.Cells; c++)
            {
                if (_cellAxis[c] == Empty || (_invariant[c] & bit) == 0) continue;
                var key = _cellList[c];
                var before = FindCell(Step(key, axis, -1));
                if (before >= 0 && _cellAxis[before] != Empty && (_invariant[before] & bit) != 0) continue;
                var start = Coordinate(key, axis);
                var end = start + 1;
                for (var next = FindCell(Step(key, axis, 1)); next >= 0 && _cellAxis[next] != Empty && (_invariant[next] & bit) != 0; next = FindCell(With(key, axis, end)))
                    end++;
                for (var k = start; k < end; k++)
                {
                    var member = FindCell(With(key, axis, k));
                    if (end - start > _bestRun[member])
                    {
                        _bestRun[member] = end - start;
                        _cellAxis[member] = (sbyte)axis;
                    }
                }
            }
        }

        // Runs: consecutive cells along their axis with that axis, in one column.
        for (var c = 0; c < _work.Cells; c++)
        {
            var axis = _cellAxis[c];
            if (axis < 0) continue;
            var key = _cellList[c];
            var before = FindCell(Step(key, axis, -1));
            if (before >= 0 && _cellAxis[before] == axis) continue;
            var start = Coordinate(key, axis);
            var end = start + 1;
            for (var next = FindCell(Step(key, axis, 1)); next >= 0 && _cellAxis[next] == axis; next = FindCell(With(key, axis, end)))
                end++;
            for (var k = start; k < end; k++)
            {
                var member = FindCell(With(key, axis, k));
                _runStart[member] = start;
                _runEnd[member] = end;
            }
            _work.Runs++;
            _work.RunCells += end - start;
        }

        return true;
    }

    /// <summary>
    /// Whether a cell's samples are equal along an axis, to the bit. Then the samples' interpolation over the
    /// triangular prisms of a run of such cells (each a cross-section triangle swept one step, linear across it and
    /// along it) does not change along the axis, and its level set is the swept contour. That interpolation is off
    /// by no more than the tetrahedra's: weighted squared distance to the corners is at most h²/2 across a triangle
    /// plus h²/4 along the step, so √3h/2 again. Its side faces are linear across, as a tetrahedron's face with
    /// those samples is, so pieces of either kind meet without gaps.
    /// </summary>
    private bool Invariant(int cell, int axis)
    {
        var bit = 1 << axis;
        for (var corner = 0; corner < 8; corner++)
            if ((corner & bit) == 0 && Value(cell, corner) != Value(cell, corner | bit)) return false;
        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Emission.
    // ---------------------------------------------------------------------------------------------------------------

    // Kuhn's six tetrahedra about the 0-7 diagonal; every face is split from its low corner to its high one.
    private static readonly byte[] Tets = [0, 1, 3, 7, 0, 3, 2, 7, 0, 2, 6, 7, 0, 6, 4, 7, 0, 4, 5, 7, 0, 5, 1, 7];

    private static long CornerKey(long cell, int corner) =>
        Key(Coordinate(cell, 0) + (corner & 1), Coordinate(cell, 1) + (corner >> 1 & 1), Coordinate(cell, 2) + (corner >> 2 & 1));

    private float SampleValue(long key)
    {
        var mask = _sampleKeys.Length - 1;
        for (var slot = Hash(key, mask); ; slot = (slot + 1) & mask)
        {
            if (_sampleStamps[slot] != _stamp) throw new InvalidOperationException("Shell sample missing.");
            if (_sampleKeys[slot] == key) return _sampleValues[slot];
        }
    }

    /// <summary>Where the level crosses the edge between two samples, worked out the same way from either side.</summary>
    private Vector3 EdgePoint(long a, long b)
    {
        if (b < a) (a, b) = (b, a);
        var va = SampleValue(a);
        var vb = SampleValue(b);
        var t = Math.Clamp((_r - va) / (vb - va), 1e-4f, 1 - 1e-4f);
        var pa = Local(a);
        return Hull(pa + (Local(b) - pa) * t);
    }

    /// <summary>A vertex being built: its slot among the distinct vertices, and where it is.</summary>
    private readonly record struct Mark(int Slot, Vector3 Position);

    /// <summary>The vertex where the level crosses an edge, one per edge however many pieces use it.</summary>
    private Mark Vertex(long a, long b)
    {
        if (b < a) (a, b) = (b, a);
        var mask = _edgeA.Length - 1;
        for (var slot = Hash(a * 31 + b, mask); ; slot = (slot + 1) & mask)
        {
            if (_edgeStamps[slot] != _stamp)
            {
                if (_uniqueEdges * 2 >= _edgeA.Length) { _work.Overflow = "vertices"; return default; }
                _edgeStamps[slot] = _stamp;
                _edgeA[slot] = a;
                _edgeB[slot] = b;
                _edgePositions[slot] = EdgePoint(a, b);
                _edgeNormals[slot] = Vector3.Zero;
                _slotOut[slot] = -1;
                _slotFixed[slot] = false;
                _uniqueEdges++;
                return new Mark(slot, _edgePositions[slot]);
            }
            if (_edgeA[slot] == a && _edgeB[slot] == b)
                return new Mark(slot, _edgePositions[slot]);
        }
    }

    /// <summary>
    /// Writes a triangle facing <paramref name="outward"/>, and adds its normal to each corner's, weighted by the
    /// corner's angle: every vertex's normal is then the mesh's own, the same whichever block order made it.
    /// </summary>
    private int Triangle(Span<ushort> output, int count, Mark a, Mark b, Mark c, Vector3 outward)
    {
        if (_work.Overflow is not null) return -1;
        if (count + 3 > output.Length)
        {
            _work.Overflow = "indices";
            return -1;
        }

        var face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        var area = face.LengthSquared();
        if (area == 0) return count;
        if (Vector3.Dot(face, outward) < 0)
        {
            (b, c) = (c, b);
            face = -face;
        }

        face /= MathF.Sqrt(area);
        int ia = Out(a.Slot, face), ib = Out(b.Slot, face), ic = Out(c.Slot, face);
        if (ia < 0 || ib < 0 || ic < 0) return -1;
        output[count] = (ushort)ia;
        output[count + 1] = (ushort)ib;
        output[count + 2] = (ushort)ic;
        if (!_slotFixed[a.Slot]) _edgeNormals[a.Slot] += face * Angle(b.Position - a.Position, c.Position - a.Position);
        if (!_slotFixed[b.Slot]) _edgeNormals[b.Slot] += face * Angle(c.Position - b.Position, a.Position - b.Position);
        if (!_slotFixed[c.Slot]) _edgeNormals[c.Slot] += face * Angle(a.Position - c.Position, b.Position - c.Position);
        return count + 3;
    }

    private static float Angle(Vector3 x, Vector3 y)
    {
        var cos = Vector3.Dot(x, y) / MathF.Sqrt(x.LengthSquared() * y.LengthSquared());
        return MathF.Acos(Math.Clamp(cos, -1f, 1f));
    }

    /// <summary>
    /// The written vertex of a distinct vertex, numbered in the order triangles first use it; the first triangle's
    /// normal stands in should its gathered normals cancel out. -1 when there is no more room for vertices.
    /// </summary>
    private int Out(int slot, Vector3 face)
    {
        if (_slotOut[slot] >= 0) return _slotOut[slot];
        if (_outCount == _vertexLimit)
        {
            _work.Overflow = "vertices";
            return -1;
        }

        _outSlots[_outCount] = slot;
        _slotOut[slot] = _outCount;
        _edgeFaces[slot] = face;
        return _outCount++;
    }

    /// <summary>A cell drawn on its own: the level of the interpolation over its six tetrahedra.</summary>
    private int Lone(int cell, Span<ushort> output, int count)
    {
        _work.LoneCells++;
        var key = _cellList[cell];
        Span<long> corner = stackalloc long[4];
        Span<bool> inside = stackalloc bool[4];
        Span<Mark> v = stackalloc Mark[3];
        for (var t = 0; t < 6; t++)
        {
            var n = 0;
            Vector3 ins = Vector3.Zero, outs = Vector3.Zero;
            for (var i = 0; i < 4; i++)
            {
                var c = Tets[t * 4 + i];
                corner[i] = CornerKey(key, c);
                inside[i] = Value(cell, c) <= _r;
                n += inside[i] ? 1 : 0;
                if (inside[i]) ins += Point(corner[i]);
                else outs += Point(corner[i]);
            }
            if (n is 0 or 4) continue;
            // From the inside corners' middle to the outside corners': the way the shell faces here.
            var into = outs / (4 - n) - ins / n;
            if (n is 1 or 3)
            {
                var lone = 0;
                for (var i = 0; i < 4; i++)
                    if (inside[i] == (n == 1)) lone = i;
                var k = 0;
                for (var i = 0; i < 4; i++)
                    if (i != lone) v[k++] = Vertex(corner[lone], corner[i]);
                count = Triangle(output, count, v[0], v[1], v[2], into);
            }
            else
            {
                // Two in, two out: the quad across the four mixed edges, in cyclic order.
                int a0 = -1, a1 = -1, b0 = -1, b1 = -1;
                for (var i = 0; i < 4; i++)
                {
                    if (inside[i]) { if (a0 < 0) a0 = i; else a1 = i; }
                    else { if (b0 < 0) b0 = i; else b1 = i; }
                }
                var p0 = Vertex(corner[a0], corner[b0]);
                var p1 = Vertex(corner[a0], corner[b1]);
                var p2 = Vertex(corner[a1], corner[b1]);
                var p3 = Vertex(corner[a1], corner[b0]);
                count = Triangle(output, count, p0, p1, p2, into);
                if (count >= 0) count = Triangle(output, count, p0, p2, p3, into);
            }
            if (count < 0) return -1;
        }
        return count;
    }

    /// <summary>
    /// A run of cells along an axis whose field does not change along it: the contour of its cross-section, swept
    /// from end to end, each line of it cut wherever a piece on its other side has a vertex on it.
    /// </summary>
    private int Run(int cell, Span<ushort> output, int count)
    {
        var key = _cellList[cell];
        var axis = _cellAxis[cell];
        var u = (axis + 1) % 3;
        var w = (axis + 2) % 3;
        var start = _runStart[cell];
        var end = _runEnd[cell];
        // Cross-section corners at the run's low end: (0,0), (1,0), (1,1), (0,1) in (u, w); the diagonal 0-2.
        Span<long> square = stackalloc long[4];
        Span<bool> inside = stackalloc bool[4];
        for (var i = 0; i < 4; i++)
        {
            var at = With(key, axis, start);
            if (i is 1 or 2) at = Step(at, u, 1);
            if (i >= 2) at = Step(at, w, 1);
            square[i] = at;
            inside[i] = SampleValue(at) <= _r;
        }

        Span<int> ends = stackalloc int[4];
        // Two triangles: (0,1,2) and (0,2,3). Edges are named by their corner pair; 0-2 is the diagonal.
        for (var tri = 0; tri < 2; tri++)
        {
            Span<int> corners = [0, tri == 0 ? 1 : 2, tri == 0 ? 2 : 3];
            var n = 0;
            Vector3 ins = Vector3.Zero, outs = Vector3.Zero;
            for (var i = 0; i < 3; i++)
            {
                n += inside[corners[i]] ? 1 : 0;
                if (inside[corners[i]]) ins += Point(square[corners[i]]);
                else outs += Point(square[corners[i]]);
            }
            if (n is 0 or 3) continue;
            var into = outs / (3 - n) - ins / n;
            // The segment joins the two edges with one end in and one out.
            var found = 0;
            for (var i = 0; i < 3; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % 3];
                if (inside[a] == inside[b]) continue;
                ends[found++] = a;
                ends[found++] = b;
            }
            into[axis] = 0;
            var lengthA = Line(key, axis, start, end, square, ends[0], ends[1], _breakA);
            var lengthB = Line(key, axis, start, end, square, ends[2], ends[3], _breakB);
            if (lengthA < 0 || lengthB < 0) { _work.Overflow ??= "breaks"; return -1; }
            count = Ladder(output, count, key, axis, square, ends[0], ends[1], lengthA, _breakA, ends[2], ends[3], lengthB, _breakB, into);
            if (count < 0) return -1;
        }
        return count;
    }

    /// <summary>
    /// The positions along the run, in grid steps, where the line through the crossing on cross-section edge (a, b)
    /// must have a vertex: the run's ends, and on a side shared with another column every edge where the piece there
    /// changes, plus that piece's face-diagonal crossing where it is not a run along the same axis.
    /// </summary>
    private int Line(long key, int axis, int start, int end, ReadOnlySpan<long> square, int a, int b, float[] breaks)
    {
        var n = 0;
        breaks[n++] = start;
        var diagonal = (a == 0 && b == 2) || (a == 2 && b == 0);
        if (!diagonal)
        {
            // The neighbouring column across this side, and the cross-section axis the side runs along.
            var u = (axis + 1) % 3;
            var w = (axis + 2) % 3;
            var lowA = Math.Min(a, b);
            var highA = Math.Max(a, b);
            var side = (lowA, highA) switch { (0, 1) => (w, -1, u), (1, 2) => (u, 1, w), (2, 3) => (w, 1, u), _ => (u, -1, w) };
            var across = Step(With(key, axis, start), side.Item1, side.Item2);
            // The crossing's fraction along the side, from its low corner: the shared face's diagonal meets the line there.
            var low = square[Math.Min(a, b) == 0 && Math.Max(a, b) == 3 ? 0 : Math.Min(a, b)];
            var high = square[Math.Min(a, b) == 0 && Math.Max(a, b) == 3 ? 3 : Math.Max(a, b)];
            if (high < low) (low, high) = (high, low);
            var vl = SampleValue(low);
            var vh = SampleValue(high);
            var fraction = Math.Clamp((_r - vl) / (vh - vl), 1e-4f, 1 - 1e-4f);
            if (!Breaks(across, axis, start, end, fraction, breaks, ref n)) return -1;
        }
        return Finish(end, breaks, n);
    }

    /// <summary>
    /// Adds the positions where the piece across a line has a vertex on it: a run along the same axis at its ends, a
    /// flat face at its ends, anything else at its cell's edges and its face diagonal's crossing.
    /// </summary>
    private bool Breaks(long across, int axis, int start, int end, float fraction, float[] breaks, ref int n)
    {
        for (var k = start; k < end; k++)
        {
            var cell = With(across, axis, k);
            var other = FindCell(cell);
            if (other >= 0 && _cellAxis[other] == axis)
            {
                if (_runEnd[other] < end) { if (n == breaks.Length) return false; breaks[n++] = _runEnd[other]; }
                k = Math.Max(k, _runEnd[other] - 1);
                continue;
            }
            var plane = other < 0 ? PlaneAt(cell) : -1;
            if (plane >= 0)
            {
                ref readonly var flat = ref _planes[plane];
                var (from, to) = Extent(flat, axis);
                if (n + 2 >= breaks.Length) return false;
                if (from > start) breaks[n++] = from;
                if (to < end) breaks[n++] = to;
                k = Math.Max(k, to - 1);
                continue;
            }
            if (n + 3 >= breaks.Length) return false;
            if (k > start) breaks[n++] = k;
            breaks[n++] = k + fraction;
            if (k + 1 < end) breaks[n++] = k + 1;
        }
        return true;
    }

    /// <summary>Closes a list of breaks with the end, sorts it and drops repeats; the count, or -1 when it is full.</summary>
    private static int Finish(int end, float[] breaks, int n)
    {
        if (n == breaks.Length) return -1;
        breaks[n++] = end;
        var span = breaks.AsSpan(0, n);
        span.Sort();
        var distinct = 1;
        for (var i = 1; i < n; i++)
            if (span[i] != span[distinct - 1]) span[distinct++] = span[i];
        return distinct;
    }

    /// <summary>The vertex of a line at a break: on a cell edge across the axis, or on a face diagonal.</summary>
    private Mark At(long key, int axis, ReadOnlySpan<long> square, int a, int b, float position) => At(square[a], square[b], axis, position);

    /// <summary>The vertex at a break of the line through the crossing on the edge from a to b, as it runs along the axis.</summary>
    private Mark At(long a, long b, int axis, float position)
    {
        var k = (int)MathF.Floor(position);
        var pa = With(a, axis, k);
        var pb = With(b, axis, k);
        if (position == k) return Vertex(pa, pb);
        // The shared face's diagonal: from its low corner at k to its high corner at k + 1.
        var low = pa < pb ? pa : pb;
        var high = pa < pb ? pb : pa;
        return Vertex(low, Step(high, axis, 1));
    }

    private int Ladder(Span<ushort> output, int count, long key, int axis, ReadOnlySpan<long> square,
        int a0, int a1, int na, float[] la, int b0, int b1, int nb, float[] lb, Vector3 outward)
    {
        int i = 0, j = 0;
        var va = At(key, axis, square, a0, a1, la[0]);
        var vb = At(key, axis, square, b0, b1, lb[0]);
        while (i < na - 1 || j < nb - 1)
        {
            if (j == nb - 1 || (i < na - 1 && la[i + 1] <= lb[j + 1]))
            {
                var next = At(key, axis, square, a0, a1, la[++i]);
                count = Triangle(output, count, va, next, vb, outward);
                va = next;
            }
            else
            {
                var next = At(key, axis, square, b0, b1, lb[++j]);
                count = Triangle(output, count, va, next, vb, outward);
                vb = next;
            }
            if (count < 0) return -1;
        }
        return count;
    }
}
