using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Client.Render;

/// <summary>The drawn base mesh of one rock shape at its finest level, as the patch mesher needs it. Built once a shape, on first use.</summary>
internal sealed class RockBaseMesh
{
    private static readonly RockBaseMesh?[] Shapes = new RockBaseMesh?[Galaxy.RockShapes];

    /// <summary>Vertex positions for a rock of unit radius: the same values <see cref="MeshBuilder.Asteroid"/> draws.</summary>
    public readonly Vector3[] Points;

    /// <summary>Smooth vertex normals, accumulated as <see cref="MeshBuilder.Asteroid"/> does.</summary>
    public readonly Vector3[] Normals;

    /// <summary>Three vertex indices a triangle, in the order of <see cref="AsteroidMesh"/>'s finest sphere.</summary>
    public readonly uint[] Indices;

    private RockBaseMesh(Vector3[] points, Vector3[] normals)
    {
        Indices = AsteroidMesh.Sphere(AsteroidMesh.Subdivisions).Indices;
        Points = points;
        Normals = normals;
    }

    /// <summary>
    /// Keeps the arrays the finest drawn mesh of a shape was just built from, so the first scraped rock of that shape
    /// costs the worker no memory. <see cref="MeshBuilder.Asteroid"/> calls it at start.
    /// </summary>
    public static void Adopt(int shape, Vector3[] points, Vector3[] normals)
    {
        if ((uint)shape >= (uint)Shapes.Length) return;
        lock (Shapes)
        {
            Shapes[shape] ??= new RockBaseMesh(points, normals);
        }
    }

    private RockBaseMesh(int shape)
    {
        var sphere = AsteroidMesh.Sphere(AsteroidMesh.Subdivisions);
        Indices = sphere.Indices;
        Points = new Vector3[sphere.Points.Length];
        for (var i = 0; i < Points.Length; i++) Points[i] = AsteroidShape.Point(shape, sphere.Points[i]);
        Normals = new Vector3[Points.Length];
        for (var i = 0; i < Indices.Length; i += 3)
        {
            var (a, b, c) = ((int)Indices[i], (int)Indices[i + 1], (int)Indices[i + 2]);
            var face = Vector3.Cross(Points[b] - Points[a], Points[c] - Points[a]);
            Normals[a] += face;
            Normals[b] += face;
            Normals[c] += face;
        }

        for (var i = 0; i < Normals.Length; i++) Normals[i] = Vector3.Normalize(Normals[i]);
    }

    /// <summary>The base mesh of a shape. Call from workers: the first call for a shape builds it.</summary>
    public static RockBaseMesh Of(int shape)
    {
        var found = Volatile.Read(ref Shapes[shape]);
        if (found is not null) return found;
        lock (Shapes)
        {
            return Shapes[shape] ??= new RockBaseMesh(shape);
        }
    }
}

/// <summary>
/// Meshes what scrapes changed of one rock, on a worker: the patch drawn in place of the base facets the cuts reach.
/// <para>
/// The edited rock is what is inside the base shape and outside every cut. So the patch is two parts: the reached
/// base facets less what lies inside a cut, on their own planes with the base mesh's own normals, and the cuts'
/// surface (marching tetrahedra on the cut field's interpolant) where it lies inside the base shape. Both are clipped
/// against the same planes inside each tetrahedron, so they meet along one line, and the facets that are not reached
/// stay the shared mesh, drawn in the runs this leaves. There is no second copy of the unedited surface to disagree
/// with the first. Every polygon is grown by a fraction of a millimetre so the T-junctions between neighbours never
/// show a gap.
/// </para>
/// One instance serves one job at a time. Its buffers only grow; a rebuild takes no memory once they have.
/// </summary>
internal sealed class RockPatchMesher
{
    private const int Cells = RockEditRules.BrickCells;
    private const int Side = RockEditRules.BrickSamples;
    private const int MaxPolygon = 24;
    private const byte AnyCut = 1, AnyRock = 2;

    private struct Facet
    {
        public int Id;
        public Vector3 A, B, C;

        // Signed distance to the facet's plane is N.q - D, positive outside the rock.
        public Vector3 N;
        public float D;

        // The facet's cone from the rock's centre: inside where E.q + F >= 0 for all three.
        public Vector3 E0, E1, E2;
        public float F0, F1, F2;

        // For the base normal at a point of the facet.
        public Vector3 V0, V1;
        public float D00, D01, D11, Inverse;
    }

    /// <summary>The patch as a triangle list, positions for a rock of unit radius. The first <see cref="VertexCount"/> are meaningful.</summary>
    public MeshVertex[] Vertices;

    /// <inheritdoc cref="Vertices" />
    public int VertexCount;

    /// <summary>
    /// The base triangles still drawn by the shared mesh, as runs: first triangle and count, two numbers a run. The
    /// first <see cref="RunCount"/> runs are meaningful.
    /// </summary>
    public int[] Runs = new int[16384];

    /// <inheritdoc cref="Runs" />
    public int RunCount;

    /// <summary>Base triangles the patch draws in place of the shared mesh.</summary>
    public int Hidden { get; private set; }


    /// <summary>Bricks the last build read.</summary>
    public int Bricks { get; private set; }

    private readonly ulong[] _affected = new ulong[(81920 + 63) / 64];
    private int[] _affectedIds = new int[81920];
    private int _affectedCount;
    private int[] _candidates = new int[16384];
    private int _candidateCount;
    private Facet[] _facets = new Facet[16384];
    private int[] _cellFacets = new int[4096];
    private readonly byte[] _cellFlags = new byte[Cells * Cells * Cells];
    private readonly byte[] _node2 = new byte[64];
    private readonly byte[] _node4 = new byte[8];
    private readonly sbyte[] _field = new sbyte[RockEditRules.SampleCount];
    private Capsule[] _capsules = new Capsule[4096];

    // Above this many facets a brick, a cell asks its node for the few that can reach it.
    private const int NodeThreshold = 48;
    private int[] _nodeFacets = new int[16384];
    private int _nodeFacetCount;
    private readonly int[] _nodeFirst = new int[64], _nodeLength = new int[64];
    private int _capsuleCount;

    private RockBaseMesh _base = null!;
    private RockBricks _bricks = null!;
    private int _slot;
    private float _radius;
    private float _cell;
    private float _grow;

    /// <summary>Scales how far polygons are grown. Zero in tests that add the patch up as a closed surface.</summary>
    internal float GrowScale = 1f;
    private int _maxVertices;
    private bool _overflow;
    private Vector3 _squash;
    private double _originX, _originY, _originZ;

    /// <summary>
    /// A mesher with room for <paramref name="vertices"/> vertices from the start. Its other buffers are sized for
    /// rocks far larger than a cell's facets; only a rock of a few metres, whose every facet a brick reaches, makes
    /// them grow.
    /// </summary>
    public RockPatchMesher(int vertices = 1 << 14) => Vertices = new MeshVertex[Math.Max(3, vertices)];

    /// <summary>
    /// Builds the patch of one rock from its bricks. False when it would need more than <paramref name="maxVertices"/>
    /// vertices: the rock is then drawn without its patch. <paramref name="mergeGap"/> is the longest run of unreached
    /// facets between two reached ones that the patch takes over too, to draw the rest in fewer runs.
    /// </summary>
    public bool Build(RockBricks bricks, int slot, int shape, float radius, int maxVertices, int mergeGap)
    {
        _base = RockBaseMesh.Of(shape);
        _bricks = bricks;
        _slot = slot;
        _radius = radius;
        _cell = bricks.Log.Rules.CellMetres;
        _squash = AsteroidShape.SquashOf(shape);
        _maxVertices = maxVertices;
        _overflow = false;
        VertexCount = 0;
        RunCount = 0;
        Hidden = 0;
        Bricks = 0;

        // Wide enough to cover what rounding a position to single precision at the rock's size can open.
        _grow = (1e-4f + radius * 4e-7f) * GrowScale;
        for (var i = 0; i < _affectedCount; i++) _affected[_affectedIds[i] >> 6] = 0;
        _affectedCount = 0;

        // First which facets a cut reaches at all, then the pieces: a facet may cross several bricks and be cut in one.
        // A brick that only lies near a cut changes nothing that is drawn: to the mesher it is not there.
        for (var brick = bricks.FirstBrick(slot); brick >= 0; brick = bricks.NextBrick(brick))
            if (bricks.HasCut(brick)) Brick(brick, false);
        for (var brick = bricks.FirstBrick(slot); brick >= 0 && !_overflow; brick = bricks.NextBrick(brick))
        {
            if (!bricks.HasCut(brick)) continue;
            Brick(brick, true);
            Bricks++;
        }

        // What the reached facets have outside every brick is theirs unchanged.
        var reached = _affectedCount;
        for (var i = 0; i < reached && !_overflow; i++) Outside(_affectedIds[i]);
        if (!_overflow) Hide(mergeGap);
        return !_overflow;
    }

    private void Brick(int brick, bool emit)
    {
        _bricks.Coordinates(brick, out var bx, out var by, out var bz);
        var size = (double)_cell * Cells;
        _originX = bx * size;
        _originY = by * size;
        _originZ = bz * size;
        var samples = _bricks.Samples(brick);
        Classify(samples);
        if (emit)
        {
            samples.CopyTo(_field);
            Capsules((float)size);
            Array.Fill(_nodeFirst, -1);
            _nodeFacetCount = 0;
        }

        Candidates((float)size);
        if (_facets.Length < _candidateCount) _facets = new Facet[Math.Max(_candidateCount, _facets.Length * 2)];
        for (var i = 0; i < _candidateCount; i++) _facets[i] = Record(_candidates[i]);

        Span<Vector3> polygon = stackalloc Vector3[MaxPolygon];
        var box = (float)size;
        for (var i = 0; i < _candidateCount && !_overflow; i++)
        {
            ref readonly var facet = ref _facets[i];
            polygon[0] = facet.A;
            polygon[1] = facet.B;
            polygon[2] = facet.C;
            if (emit && !Marked(facet.Id)) continue;
            var n = ClipBox(polygon, 3, Vector3.Zero, new Vector3(box));
            if (n < 3) continue;
            if (emit) Surface(facet, polygon[..n], Cells, 0, 0, 0);
            else if (!Marked(facet.Id) && Touches(polygon[..n], Cells, 0, 0, 0)) Mark(facet.Id);
        }

        for (var z = 0; emit && z < Cells && !_overflow; z++)
            for (var y = 0; y < Cells; y++)
                for (var x = 0; x < Cells; x++)
                    if (_cellFlags[x + Cells * (y + Cells * z)] == (AnyCut | AnyRock))
                        Walls(x, y, z);
    }

    // Per cell, and per node of two and four cells a side: whether any sample is inside a cut, and whether any is not.
    private void Classify(ReadOnlySpan<sbyte> samples)
    {
        Array.Clear(_node2);
        Array.Clear(_node4);
        for (var z = 0; z < Cells; z++)
            for (var y = 0; y < Cells; y++)
                for (var x = 0; x < Cells; x++)
                {
                    byte flags = 0;
                    for (var corner = 0; corner < 8; corner++)
                        flags |= samples[x + (corner & 1) + Side * (y + (corner >> 1 & 1) + Side * (z + (corner >> 2)))] < 0 ? AnyCut : AnyRock;
                    _cellFlags[x + Cells * (y + Cells * z)] = flags;
                    _node2[(x >> 1) + 4 * ((y >> 1) + 4 * (z >> 1))] |= flags;
                    _node4[(x >> 2) + 2 * ((y >> 2) + 2 * (z >> 2))] |= flags;
                }
    }

    private byte Flags(int size, int x, int y, int z) => size switch
    {
        1 => _cellFlags[x + Cells * (y + Cells * z)],
        2 => _node2[(x >> 1) + 4 * ((y >> 1) + 4 * (z >> 1))],
        4 => _node4[(x >> 2) + 2 * ((y >> 2) + 2 * (z >> 2))],
        _ => AnyCut | AnyRock,
    };

    private struct Capsule
    {
        public Vector3 A, B;
        public float Radius;
    }

    // The rock's scrapes that reach the brick, in the brick's frame: a wall's normal comes from the nearest one, so
    // a groove is shaded as the smooth cylinder it is and not by the steps of the sampled field.
    private void Capsules(float size)
    {
        var log = _bricks.Log;
        _capsuleCount = 0;
        var reach = _cell * 2;
        var index = log.FirstOf(_slot);
        for (var n = _bricks.Applied(_slot); n > 0; n--, index = log.NextOf(index))
        {
            ref readonly var edit = ref log.EditAt(index);
            var a = new Vector3((float)(edit.Ax / (double)RockScrape.Units - _originX), (float)(edit.Ay / (double)RockScrape.Units - _originY), (float)(edit.Az / (double)RockScrape.Units - _originZ));
            var b = new Vector3((float)(edit.Bx / (double)RockScrape.Units - _originX), (float)(edit.By / (double)RockScrape.Units - _originY), (float)(edit.Bz / (double)RockScrape.Units - _originZ));
            var radius = edit.Radius + reach;
            var low = Vector3.Min(a, b) - new Vector3(radius);
            var high = Vector3.Max(a, b) + new Vector3(radius);
            if (low.X > size || low.Y > size || low.Z > size || high.X < 0 || high.Y < 0 || high.Z < 0) continue;
            if (_capsuleCount == _capsules.Length) Array.Resize(ref _capsules, _capsuleCount * 2);
            _capsules[_capsuleCount++] = new Capsule { A = a, B = b, Radius = edit.Radius };
        }
    }

    // Out of the rock at a point of a cut's wall: toward the axis of the scrape whose surface it is.
    private Vector3 WallNormal(Vector3 point)
    {
        var best = float.PositiveInfinity;
        var normal = Vector3.UnitY;
        for (var i = 0; i < _capsuleCount; i++)
        {
            ref readonly var capsule = ref _capsules[i];
            var along = capsule.B - capsule.A;
            var length = along.LengthSquared();
            var t = length > 0 ? Math.Clamp(Vector3.Dot(point - capsule.A, along) / length, 0f, 1f) : 0f;
            var toAxis = capsule.A + along * t - point;
            var distance = toAxis.Length();
            if (distance - capsule.Radius >= best || distance < 1e-6f) continue;
            best = distance - capsule.Radius;
            normal = toAxis / distance;
        }

        return normal;
    }

    // The facets whose cone from the rock's centre may reach the brick: down the sphere's subdivision, keeping a
    // triangle unless the whole brick lies outside one of its cone's three planes.
    private void Candidates(float size)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var c = 0; c < 8; c++)
            corners[c] = new Vector3((float)(_originX + (c & 1) * size), (float)(_originY + (c >> 1 & 1) * size), (float)(_originZ + (c >> 2) * size)) / _squash;
        _candidateCount = 0;
        Descend(0, 0, 20, corners);
    }

    private void Descend(int level, int first, int count, ReadOnlySpan<Vector3> corners, bool node = false)
    {
        var (points, indices) = AsteroidMesh.Sphere(level);
        for (var t = first; t < first + count; t++)
        {
            var a = points[indices[t * 3]];
            var b = points[indices[t * 3 + 1]];
            var c = points[indices[t * 3 + 2]];
            if (Beyond(Vector3.Cross(a, b), corners) || Beyond(Vector3.Cross(b, c), corners) || Beyond(Vector3.Cross(c, a), corners)) continue;
            if (level < AsteroidMesh.Subdivisions)
            {
                Descend(level + 1, t * 4, 4, corners, node);
                continue;
            }

            if (!node)
            {
                if (_candidateCount == _candidates.Length) Array.Resize(ref _candidates, _candidates.Length * 2);
                _candidates[_candidateCount++] = t;
                continue;
            }

            // A node's facets are named by their place among the brick's, which the descent listed in rising order.
            var place = _candidates.AsSpan(0, _candidateCount).BinarySearch(t);
            if (place < 0) continue;
            if (_nodeFacetCount == _nodeFacets.Length) Array.Resize(ref _nodeFacets, _nodeFacetCount * 2);
            _nodeFacets[_nodeFacetCount++] = place;
        }
    }

    // The brick's facets whose cone may reach the node of two cells a side that holds a cell: found once a node,
    // by the same descent, so a cell of a small rock looks at tens of facets and not at the brick's thousands.
    private ReadOnlySpan<int> NodeFacets(int x, int y, int z)
    {
        var node = (x >> 1) + 4 * ((y >> 1) + 4 * (z >> 1));
        if (_nodeFirst[node] < 0)
        {
            _nodeFirst[node] = _nodeFacetCount;
            var size = _cell * 2;
            Span<Vector3> corners = stackalloc Vector3[8];
            for (var c = 0; c < 8; c++)
                corners[c] = new Vector3(
                    (float)(_originX + ((x >> 1) + (c & 1)) * size),
                    (float)(_originY + ((y >> 1) + (c >> 1 & 1)) * size),
                    (float)(_originZ + ((z >> 1) + (c >> 2)) * size)) / _squash;
            Descend(0, 0, 20, corners, true);
            _nodeLength[node] = _nodeFacetCount - _nodeFirst[node];
        }

        return _nodeFacets.AsSpan(_nodeFirst[node], _nodeLength[node]);
    }

    private static bool Beyond(Vector3 plane, ReadOnlySpan<Vector3> corners)
    {
        var length = plane.Length();
        foreach (var corner in corners)
            if (Vector3.Dot(plane, corner) >= -1e-5f * length * corner.Length())
                return false;
        return true;
    }

    // A facet's planes and corners in the brick's own frame, metres from the brick's origin.
    private Facet Record(int id)
    {
        var a = _base.Points[_base.Indices[id * 3]];
        var b = _base.Points[_base.Indices[id * 3 + 1]];
        var c = _base.Points[_base.Indices[id * 3 + 2]];
        double r = _radius;
        var facet = new Facet
        {
            Id = id,
            A = Local(a),
            B = Local(b),
            C = Local(c),
        };
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        facet.N = normal;
        facet.D = (float)((normal.X * (double)a.X + normal.Y * (double)a.Y + normal.Z * (double)a.Z) * r - (normal.X * _originX + normal.Y * _originY + normal.Z * _originZ));
        Cone(a, b, out facet.E0, out facet.F0);
        Cone(b, c, out facet.E1, out facet.F1);
        Cone(c, a, out facet.E2, out facet.F2);
        facet.V0 = facet.B - facet.A;
        facet.V1 = facet.C - facet.A;
        facet.D00 = Vector3.Dot(facet.V0, facet.V0);
        facet.D01 = Vector3.Dot(facet.V0, facet.V1);
        facet.D11 = Vector3.Dot(facet.V1, facet.V1);
        var determinant = facet.D00 * facet.D11 - facet.D01 * facet.D01;
        facet.Inverse = determinant > 0 ? 1f / determinant : 0;
        return facet;
    }

    private Vector3 Local(Vector3 unit) => new((float)(unit.X * (double)_radius - _originX), (float)(unit.Y * (double)_radius - _originY), (float)(unit.Z * (double)_radius - _originZ));

    // The plane through the rock's centre and one edge. Two facets that share the edge get the same plane, negated.
    private void Cone(Vector3 from, Vector3 to, out Vector3 normal, out float offset)
    {
        normal = Vector3.Normalize(Vector3.Cross(from, to));
        offset = (float)(normal.X * _originX + normal.Y * _originY + normal.Z * _originZ);
    }

    // Whether a facet's part in one node of the brick lies in a cell that holds a sample inside a cut.
    private bool Touches(ReadOnlySpan<Vector3> polygon, int size, int x, int y, int z)
    {
        if ((Flags(size, x, y, z) & AnyCut) == 0) return false;
        if (size == 1) return true;
        var half = size / 2;
        Span<Vector3> part = stackalloc Vector3[MaxPolygon];
        Bounds(polygon, out var least, out var most);
        for (var child = 0; child < 8; child++)
        {
            int cx = x + (child & 1) * half, cy = y + (child >> 1 & 1) * half, cz = z + (child >> 2) * half;
            Vector3 low = new Vector3(cx, cy, cz) * _cell, high = new Vector3(cx + half, cy + half, cz + half) * _cell;
            if (Apart(least, most, low, high)) continue;
            if (Within(least, most, low, high))
            {
                // Most facets of a small rock lie wholly inside one child: nothing to clip.
                if (Touches(polygon, half, cx, cy, cz)) return true;
                continue;
            }

            polygon.CopyTo(part);
            var n = ClipBox(part, polygon.Length, low, high);
            if (n >= 3 && Touches(part[..n], half, cx, cy, cz)) return true;
        }

        return false;
    }

    // The part of a facet inside one node of the brick, less what lies inside a cut.
    private void Surface(in Facet facet, ReadOnlySpan<Vector3> polygon, int size, int x, int y, int z)
    {
        var flags = Flags(size, x, y, z);
        if ((flags & AnyRock) == 0) return;
        if ((flags & AnyCut) == 0)
        {
            EmitBase(facet, polygon);
            return;
        }

        if (size == 1)
        {
            SurfaceCell(facet, polygon, x, y, z);
            return;
        }

        var half = size / 2;
        Span<Vector3> part = stackalloc Vector3[MaxPolygon];
        Bounds(polygon, out var least, out var most);
        for (var child = 0; child < 8; child++)
        {
            int cx = x + (child & 1) * half, cy = y + (child >> 1 & 1) * half, cz = z + (child >> 2) * half;
            Vector3 low = new Vector3(cx, cy, cz) * _cell, high = new Vector3(cx + half, cy + half, cz + half) * _cell;
            if (Apart(least, most, low, high)) continue;
            if (Within(least, most, low, high))
            {
                Surface(facet, polygon, half, cx, cy, cz);
                continue;
            }

            polygon.CopyTo(part);
            var n = ClipBox(part, polygon.Length, low, high);
            if (n >= 3) Surface(facet, part[..n], half, cx, cy, cz);
        }
    }

    private static void Bounds(ReadOnlySpan<Vector3> polygon, out Vector3 least, out Vector3 most)
    {
        least = most = polygon[0];
        for (var i = 1; i < polygon.Length; i++)
        {
            least = Vector3.Min(least, polygon[i]);
            most = Vector3.Max(most, polygon[i]);
        }
    }

    private static bool Apart(Vector3 least, Vector3 most, Vector3 low, Vector3 high) =>
        most.X < low.X || most.Y < low.Y || most.Z < low.Z || least.X > high.X || least.Y > high.Y || least.Z > high.Z;

    private static bool Within(Vector3 least, Vector3 most, Vector3 low, Vector3 high) =>
        least.X >= low.X && least.Y >= low.Y && least.Z >= low.Z && most.X <= high.X && most.Y <= high.Y && most.Z <= high.Z;

    // One cell the cut's surface passes through: the facet's part in each of the six tetrahedra, where the field is not negative.
    private void SurfaceCell(in Facet facet, ReadOnlySpan<Vector3> polygon, int x, int y, int z)
    {
        var origin = new Vector3(x, y, z) * _cell;
        Span<Vector3> part = stackalloc Vector3[MaxPolygon];
        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Span<int> strides = [1, Side, Side * Side];
        var at = x + Side * (y + Side * z);
        for (var tetra = 0; tetra < 6; tetra++)
        {
            Order(tetra, out var a, out var b, out var c);
            float v0 = _field[at], v1 = _field[at + strides[a]], v2 = _field[at + strides[a] + strides[b]], v3 = _field[at + strides[a] + strides[b] + strides[c]];
            if (v0 < 0 && v1 < 0 && v2 < 0 && v3 < 0) continue;
            polygon.CopyTo(part);

            // The tetrahedron is where the cell's coordinates are in this order: first >= second >= third.
            var n = Clip(part, polygon.Length, axes[a] - axes[b], -Vector3.Dot(axes[a] - axes[b], origin));
            if (n >= 3) n = Clip(part, n, axes[b] - axes[c], -Vector3.Dot(axes[b] - axes[c], origin));
            if (n < 3) continue;
            if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
            {
                // The field is linear here: v0 plus a slope along each axis, in steps a cell.
                var slope = (axes[a] * (v1 - v0) + axes[b] * (v2 - v1) + axes[c] * (v3 - v2)) / _cell;
                n = Clip(part, n, slope, v0 - Vector3.Dot(slope, origin));
                if (n < 3) continue;
            }

            EmitBase(facet, part[..n]);
        }
    }

    // The six orders of the three axes: tetrahedron t runs from the cell's corner along a, then b, then c.
    private static void Order(int tetra, out int a, out int b, out int c)
    {
        a = tetra >> 1;
        b = (a + 1 + (tetra & 1)) % 3;
        c = 3 - a - b;
    }

    // The cut's surface through one cell, where it lies inside the base shape.
    private void Walls(int x, int y, int z)
    {
        var origin = new Vector3(x, y, z) * _cell;

        // The facets whose cone reaches the cell, and whether the cell is wholly under every one of their planes.
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var corner = 0; corner < 8; corner++)
            corners[corner] = origin + new Vector3(corner & 1, corner >> 1 & 1, corner >> 2) * _cell;
        var near = 0;
        var deep = true;
        var outside = true;
        var few = _candidateCount <= NodeThreshold;
        var listed = few ? default : NodeFacets(x, y, z);
        for (var k = 0; k < (few ? _candidateCount : listed.Length); k++)
        {
            var i = few ? k : listed[k];
            ref readonly var facet = ref _facets[i];
            if (Outside(corners, facet.E0, facet.F0) || Outside(corners, facet.E1, facet.F1) || Outside(corners, facet.E2, facet.F2)) continue;
            if (near == _cellFacets.Length) Array.Resize(ref _cellFacets, near * 2);
            _cellFacets[near++] = i;
            foreach (var corner in corners)
            {
                var distance = Vector3.Dot(facet.N, corner) - facet.D;
                deep &= distance < -1e-4f;
                outside &= distance > 1e-4f;
            }
        }

        if (near == 0 || outside) return;

        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Span<int> strides = [1, Side, Side * Side];
        Span<Vector3> wall = stackalloc Vector3[MaxPolygon];
        Span<Vector3> part = stackalloc Vector3[MaxPolygon];
        Span<Vector3> point = stackalloc Vector3[4];
        Span<float> value = stackalloc float[4];
        Span<int> cut = stackalloc int[2];
        Span<int> rock = stackalloc int[2];
        var at = x + Side * (y + Side * z);
        for (var tetra = 0; tetra < 6; tetra++)
        {
            Order(tetra, out var a, out var b, out var c);
            point[0] = origin;
            point[1] = point[0] + axes[a] * _cell;
            point[2] = point[1] + axes[b] * _cell;
            point[3] = point[2] + axes[c] * _cell;
            value[0] = _field[at];
            value[1] = _field[at + strides[a]];
            value[2] = _field[at + strides[a] + strides[b]];
            value[3] = _field[at + strides[a] + strides[b] + strides[c]];
            var mask = 0;
            for (var k = 0; k < 4; k++)
                if (value[k] < 0) mask |= 1 << k;
            if (mask is 0 or 15) continue;

            // Where the field crosses zero in this tetrahedron: a triangle or a quadrilateral, flat since it is linear.
            var n = 0;
            if (BitOperations.PopCount((uint)mask) != 2)
            {
                var lone = BitOperations.TrailingZeroCount(BitOperations.PopCount((uint)mask) == 1 ? mask : ~mask & 15);
                for (var k = 0; k < 4; k++)
                    if (k != lone) wall[n++] = Cross(point, value, lone, k);
            }
            else
            {
                int cuts = 0, rocks = 0;
                for (var k = 0; k < 4; k++)
                    if ((mask >> k & 1) != 0) cut[cuts++] = k;
                    else rock[rocks++] = k;
                wall[n++] = Cross(point, value, cut[0], rock[0]);
                wall[n++] = Cross(point, value, cut[0], rock[1]);
                wall[n++] = Cross(point, value, cut[1], rock[1]);
                wall[n++] = Cross(point, value, cut[1], rock[0]);
            }

            // Seen from outside the rock the wall winds anticlockwise: the rock's outside is where the field falls.
            var slope = axes[a] * (value[1] - value[0]) + axes[b] * (value[2] - value[1]) + axes[c] * (value[3] - value[2]);
            // The whole polygon's area decides: two of its corners coincide where a sample is exactly zero.
            var facing = Vector3.Cross(wall[1] - wall[0], wall[2] - wall[0]);
            if (n == 4) facing += Vector3.Cross(wall[2] - wall[0], wall[3] - wall[0]);
            if (Vector3.Dot(facing, slope) > 0) wall[..n].Reverse();

            if (deep)
            {
                EmitWall(wall[..n]);
                continue;
            }

            for (var i = 0; i < near; i++)
            {
                ref readonly var facet = ref _facets[_cellFacets[i]];
                wall[..n].CopyTo(part);
                var m = Clip(part, n, facet.E0, facet.F0);
                if (m >= 3) m = Clip(part, m, facet.E1, facet.F1);
                if (m >= 3) m = Clip(part, m, facet.E2, facet.F2);
                if (m >= 3) m = Clip(part, m, -facet.N, facet.D);
                if (m >= 3) EmitWall(part[..m]);
            }
        }
    }

    private static bool Outside(ReadOnlySpan<Vector3> corners, Vector3 normal, float offset)
    {
        foreach (var corner in corners)
            if (Vector3.Dot(normal, corner) + offset >= 0)
                return false;
        return true;
    }

    // Where the field is zero on an edge of a tetrahedron, always worked out from the lower corner: the two cells and
    // the tetrahedra that share the edge get the same point.
    private static Vector3 Cross(ReadOnlySpan<Vector3> point, ReadOnlySpan<float> value, int i, int j)
    {
        if (i > j) (i, j) = (j, i);
        return point[i] + (point[j] - point[i]) * (value[i] / (value[i] - value[j]));
    }

    // What a reached facet has outside every brick: its part in each brick-sized box that holds no brick.
    private void Outside(int id)
    {
        var a = _base.Points[_base.Indices[id * 3]] * _radius;
        var b = _base.Points[_base.Indices[id * 3 + 1]] * _radius;
        var c = _base.Points[_base.Indices[id * 3 + 2]] * _radius;
        var size = (double)_cell * Cells;
        var low = Vector3.Min(a, Vector3.Min(b, c));
        var high = Vector3.Max(a, Vector3.Max(b, c));
        int x0 = (int)Math.Floor(low.X / size), y0 = (int)Math.Floor(low.Y / size), z0 = (int)Math.Floor(low.Z / size);
        int x1 = (int)Math.Floor(high.X / size), y1 = (int)Math.Floor(high.Y / size), z1 = (int)Math.Floor(high.Z / size);
        Span<Vector3> polygon = stackalloc Vector3[MaxPolygon];
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        var reach = (float)size * 0.8660254f;
        for (var z = z0; z <= z1 && !_overflow; z++)
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    // Most boxes of a large facet's bounds are nowhere near its plane.
                    var centre = new Vector3((float)((x + 0.5) * size), (float)((y + 0.5) * size), (float)((z + 0.5) * size));
                    if (MathF.Abs(Vector3.Dot(normal, centre - a)) > reach) continue;
                    var held = _bricks.Find(_slot, x, y, z);
                    if (held >= 0 && _bricks.HasCut(held)) continue;
                    _originX = x * size;
                    _originY = y * size;
                    _originZ = z * size;
                    var facet = Record(id);
                    polygon[0] = facet.A;
                    polygon[1] = facet.B;
                    polygon[2] = facet.C;
                    var n = ClipBox(polygon, 3, Vector3.Zero, new Vector3((float)size));
                    if (n >= 3) EmitBase(facet, polygon[..n]);
                }
    }

    private bool Marked(int id) => (_affected[id >> 6] >> (id & 63) & 1) != 0;

    private void Mark(int id)
    {
        if (Marked(id)) return;
        _affected[id >> 6] |= 1UL << (id & 63);
        if (_affectedCount == _affectedIds.Length) Array.Resize(ref _affectedIds, _affectedCount * 2);
        _affectedIds[_affectedCount++] = id;
    }

    // The runs of base triangles the shared mesh still draws. A short stretch of unreached facets between two reached
    // ones is drawn by the patch as it is, so the rest takes fewer draws.
    private void Hide(int mergeGap)
    {
        var total = _base.Indices.Length / 3;
        _affectedIds.AsSpan(0, _affectedCount).Sort();
        var reached = _affectedCount;
        var next = 0;
        for (var i = 0; i < reached; i++)
        {
            var id = _affectedIds[i];
            if (i > 0 && id - next <= mergeGap)
            {
                for (var filler = next; filler < id && !_overflow; filler++) Whole(filler);
                Hidden += id - next;
            }
            else if (id > next)
            {
                AddRun(next, id - next);
            }

            Hidden++;
            next = id + 1;
        }

        if (next < total) AddRun(next, total - next);
    }

    private void AddRun(int first, int count)
    {
        if (RunCount * 2 == Runs.Length) Array.Resize(ref Runs, Runs.Length * 2);
        Runs[RunCount * 2] = first;
        Runs[RunCount * 2 + 1] = count;
        RunCount++;
    }

    // A base triangle as the shared mesh has it, vertex for vertex.
    private void Whole(int id)
    {
        if (!Room(3)) return;
        for (var k = 0; k < 3; k++)
        {
            var index = _base.Indices[id * 3 + k];
            var p = _base.Points[index];
            var n = _base.Normals[index];
            Vertices[VertexCount++] = new MeshVertex { Position = new float3(p.X, p.Y, p.Z), Normal = new float3(n.X, n.Y, n.Z) };
        }
    }

    private bool Room(int vertices)
    {
        if (VertexCount + vertices > _maxVertices)
        {
            _overflow = true;
            return false;
        }

        if (VertexCount + vertices > Vertices.Length) Array.Resize(ref Vertices, Math.Max(Vertices.Length * 2, VertexCount + vertices));
        return true;
    }

    // A piece of a base facet: on the facet's plane, shaded with the base mesh's own normal at each corner.
    private void EmitBase(in Facet facet, ReadOnlySpan<Vector3> polygon)
    {
        if (!Grown(polygon, out var grown)) return;
        var na = _base.Normals[_base.Indices[facet.Id * 3]];
        var nb = _base.Normals[_base.Indices[facet.Id * 3 + 1]];
        var nc = _base.Normals[_base.Indices[facet.Id * 3 + 2]];
        Span<MeshVertex> corner = stackalloc MeshVertex[MaxPolygon];
        for (var i = 0; i < polygon.Length; i++)
        {
            var q = grown[i];
            var v2 = q - facet.A;
            float d20 = Vector3.Dot(v2, facet.V0), d21 = Vector3.Dot(v2, facet.V1);
            var wb = (facet.D11 * d20 - facet.D01 * d21) * facet.Inverse;
            var wc = (facet.D00 * d21 - facet.D01 * d20) * facet.Inverse;

            // Not made unit length: the shared mesh's normal is linear over the facet, and so must this be.
            var normal = na * (1 - wb - wc) + nb * wb + nc * wc;
            corner[i] = new MeshVertex { Position = Unit(q), Normal = new float3(normal.X, normal.Y, normal.Z) };
        }

        Fan(corner[..polygon.Length]);
    }

    // A piece of a cut's surface, shaded as the scrape it belongs to.
    private void EmitWall(ReadOnlySpan<Vector3> polygon)
    {
        if (!Grown(polygon, out var grown)) return;
        Span<MeshVertex> corner = stackalloc MeshVertex[MaxPolygon];
        for (var i = 0; i < polygon.Length; i++)
        {
            var normal = WallNormal(polygon[i]);
            corner[i] = new MeshVertex { Position = Unit(grown[i]), Normal = new float3(normal.X, normal.Y, normal.Z) };
        }

        Fan(corner[..polygon.Length]);
    }

    private readonly Vector3[] _grown = new Vector3[MaxPolygon];

    // The polygon pushed out from its middle by a hair, or false for one with no area to speak of.
    private bool Grown(ReadOnlySpan<Vector3> polygon, out ReadOnlySpan<Vector3> grown)
    {
        grown = default;
        var middle = Vector3.Zero;
        foreach (var p in polygon) middle += p;
        middle /= polygon.Length;
        var area = Vector3.Zero;
        for (var i = 1; i + 1 < polygon.Length; i++) area += Vector3.Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
        if (area.LengthSquared() < 1e-14f) return false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var out_ = polygon[i] - middle;
            var length = out_.Length();
            _grown[i] = length > 1e-9f ? polygon[i] + out_ * (_grow / length) : polygon[i];
        }

        grown = _grown.AsSpan(0, polygon.Length);
        return true;
    }

    private float3 Unit(Vector3 q) => new((float)((_originX + q.X) / _radius), (float)((_originY + q.Y) / _radius), (float)((_originZ + q.Z) / _radius));

    private void Fan(ReadOnlySpan<MeshVertex> corner)
    {
        if (!Room((corner.Length - 2) * 3)) return;
        for (var i = 1; i + 1 < corner.Length; i++)
        {
            Vertices[VertexCount++] = corner[0];
            Vertices[VertexCount++] = corner[i];
            Vertices[VertexCount++] = corner[i + 1];
        }
    }

    private static int ClipBox(Span<Vector3> polygon, int n, Vector3 low, Vector3 high)
    {
        n = Clip(polygon, n, Vector3.UnitX, -low.X);
        if (n >= 3) n = Clip(polygon, n, -Vector3.UnitX, high.X);
        if (n >= 3) n = Clip(polygon, n, Vector3.UnitY, -low.Y);
        if (n >= 3) n = Clip(polygon, n, -Vector3.UnitY, high.Y);
        if (n >= 3) n = Clip(polygon, n, Vector3.UnitZ, -low.Z);
        if (n >= 3) n = Clip(polygon, n, -Vector3.UnitZ, high.Z);
        return n;
    }

    // Keeps the part of a convex polygon where normal.p + offset >= 0, in place. Returns the corners left.
    private static int Clip(Span<Vector3> polygon, int n, Vector3 normal, float offset)
    {
        Span<Vector3> result = stackalloc Vector3[MaxPolygon];
        var count = 0;
        var previous = polygon[n - 1];
        var before = Vector3.Dot(normal, previous) + offset;
        for (var i = 0; i < n; i++)
        {
            var current = polygon[i];
            var here = Vector3.Dot(normal, current) + offset;
            if (before >= 0 != here >= 0 && count < MaxPolygon)
                result[count++] = previous + (current - previous) * (before / (before - here));
            if (here >= 0 && count < MaxPolygon)
                result[count++] = current;
            previous = current;
            before = here;
        }

        result[..count].CopyTo(polygon);
        return count;
    }
}
