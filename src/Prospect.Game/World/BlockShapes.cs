using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Canonical triangular prisms; signs name the removed corner on cyclic transverse axes.</summary>
public enum BlockShape : byte
{
    /// <summary>A full rectangular solid.</summary>
    Box,
    /// <summary>Prisms extruded along X.</summary>
    WedgeXPlusPlus, WedgeXMinusPlus, WedgeXPlusMinus, WedgeXMinusMinus,
    /// <summary>Prisms extruded along Y.</summary>
    WedgeYPlusPlus, WedgeYMinusPlus, WedgeYPlusMinus, WedgeYMinusMinus,
    /// <summary>Prisms extruded along Z.</summary>
    WedgeZPlusPlus, WedgeZMinusPlus, WedgeZPlusMinus, WedgeZMinusMinus
}

/// <summary>Shared, allocation-free geometry in bounding-box-centred coordinates.</summary>
public static class BlockShapes
{
    private static readonly string[] Names = ["box", "wedge-x++", "wedge-x-+", "wedge-x+-", "wedge-x--", "wedge-y++", "wedge-y-+", "wedge-y+-", "wedge-y--", "wedge-z++", "wedge-z-+", "wedge-z+-", "wedge-z--"];
    /// <summary>Whether a wire value names a canonical shape.</summary>
    public static bool IsValid(BlockShape shape) => (byte)shape <= 12;
    /// <summary>The canonical JSON name.</summary>
    public static string Name(BlockShape shape) => IsValid(shape) ? Names[(byte)shape] : throw new InvalidDataException("Unknown block shape.");
    /// <summary>Reads a canonical JSON name.</summary>
    public static BlockShape Parse(string? name)
    {
        for (var i = 0; i < Names.Length; i++) if (Names[i] == name) return (BlockShape)i;
        throw new InvalidDataException("Unknown block shape.");
    }
    /// <summary>Extrusion axis and signs on u=(e+1)%3 and v=(e+2)%3.</summary>
    public static void Decode(BlockShape shape, out int e, out int u, out int v, out int su, out int sv)
    {
        if (shape == BlockShape.Box || !IsValid(shape)) throw new ArgumentOutOfRangeException(nameof(shape));
        var code = (int)shape - 1;
        e = code / 4; u = (e + 1) % 3; v = (e + 2) % 3;
        su = (code & 1) == 0 ? 1 : -1; sv = (code & 2) == 0 ? 1 : -1;
    }
    /// <summary>Unnormalised outward slant normal.</summary>
    public static Vector3 Slant(BlockShape shape, Vector3 half)
    {
        Decode(shape, out _, out var u, out var v, out var su, out var sv);
        var n = Vector3.Zero; n[u] = su * half[v]; n[v] = sv * half[u]; return n;
    }
    /// <summary>Actual vertices, in stable box-corner order; destination needs eight slots.</summary>
    public static int Vertices(BlockShape shape, Vector3 half, Span<Vector3> vertices)
    {
        var n = shape == BlockShape.Box ? Vector3.Zero : Slant(shape, half);
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            var p = new Vector3((i & 1) * 2 - 1, ((i >> 1) & 1) * 2 - 1, ((i >> 2) & 1) * 2 - 1) * half;
            if (Vector3.Dot(n, p) <= 0) vertices[count++] = p;
        }
        return count;
    }
    /// <summary>Maximum projection on a direction from the bounding centre.</summary>
    public static float Support(BlockShape shape, Vector3 half, Vector3 direction)
    {
        if (shape == BlockShape.Box) return Vector3.Dot(half, Vector3.Abs(direction));
        Span<Vector3> vertices = stackalloc Vector3[8];
        var count = Vertices(shape, half, vertices); var best = float.NegativeInfinity;
        for (var i = 0; i < count; i++) best = Math.Max(best, Vector3.Dot(vertices[i], direction));
        return best;
    }
    /// <summary>Exact closest point in the solid prism.</summary>
    public static Vector3 Closest(BlockShape shape, Vector3 half, Vector3 point)
    {
        var clamped = Vector3.Clamp(point, -half, half);
        if (shape == BlockShape.Box || Vector3.Dot(Slant(shape, half), clamped) <= 0) return clamped;
        Decode(shape, out _, out var u, out var v, out var su, out var sv);
        var a = Vector2.Multiply(new Vector2(-su, sv), new Vector2(half[u], half[v]));
        var b = -a; var edge = b - a;
        var t = Math.Clamp(Vector2.Dot(new Vector2(point[u], point[v]) - a, edge) / edge.LengthSquared(), 0, 1);
        var p = a + t * edge; clamped[u] = p.X; clamped[v] = p.Y; return clamped;
    }
    /// <summary>
    /// Whether a bounding-box corner belongs to the solid. Corner bits are 1 for +x, 2 for +y and 4 for +z; a box has
    /// all eight, a wedge loses the two on its removed transverse corner. Decided on signs alone, so exact at any size.
    /// </summary>
    public static bool HasCorner(BlockShape shape, int corner) => shape == BlockShape.Box || Lean(shape, corner) <= 0;

    /// <summary>A corner's position from the bounding centre, for a shape of half extents <paramref name="half"/>.</summary>
    public static Vector3 Corner(int corner, Vector3 half) =>
        new Vector3((corner & 1) * 2 - 1, ((corner >> 1) & 1) * 2 - 1, ((corner >> 2) & 1) * 2 - 1) * half;

    /// <summary>
    /// The side number (as <see cref="HullDesign.ExteriorFaces"/> numbers sides) that stands for a wedge's slant:
    /// its +su side across u. The other transverse +side, (v, +sv), has no face. -1 for a box.
    /// </summary>
    public static int SlantSide(BlockShape shape)
    {
        if (shape == BlockShape.Box) return -1;
        Decode(shape, out _, out var u, out _, out var su, out _);
        return 2 * u + (su < 0 ? 1 : 0);
    }

    /// <summary>
    /// The corners of one face, as corner numbers (see <see cref="HasCorner"/>), counter-clockwise seen from
    /// outside. Sides are numbered as <see cref="HullDesign.ExteriorFaces"/> numbers them; a wedge's slant is
    /// <see cref="SlantSide"/>, its caps are triangles and its side (v, +sv) returns 0. Needs four slots.
    /// </summary>
    public static int Face(BlockShape shape, int side, Span<int> corners)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)side, 5u, nameof(side));
        var count = 0;
        if (side == SlantSide(shape))
        {
            // The four corners on the slant: both ends of the two transverse edges next to the removed corner.
            Decode(shape, out var e, out var u, out var v, out var su, out var sv);
            Span<int> ring = stackalloc int[4];
            ring[0] = Bits(e, -1, u, su, v, -sv);
            ring[1] = Bits(e, -1, u, -su, v, sv);
            ring[2] = Bits(e, 1, u, -su, v, sv);
            ring[3] = Bits(e, 1, u, su, v, -sv);
            var normal = Vector3.Zero;
            normal[u] = su;
            normal[v] = sv;
            var a = Corner(ring[0], Vector3.One);
            var turn = Vector3.Dot(Vector3.Cross(Corner(ring[1], Vector3.One) - a, Corner(ring[2], Vector3.One) - a), normal);
            for (var i = 0; i < 4; i++)
                corners[i] = turn > 0 ? ring[i] : ring[3 - i];
            return 4;
        }

        var axis = side >> 1;
        var sign = (side & 1) == 0 ? 1 : -1;
        var t1 = (axis + 1) % 3;
        var t2 = (axis + 2) % 3;
        for (var i = 0; i < 4; i++)
        {
            // (-,-), (+,-), (+,+), (-,+) across the face is counter-clockwise seen from +axis; reversed from -axis.
            var k = sign > 0 ? i : 3 - i;
            var corner = Bits(axis, sign, t1, k is 1 or 2 ? 1 : -1, t2, k >= 2 ? 1 : -1);
            if (HasCorner(shape, corner))
                corners[count++] = corner;
        }

        return count >= 3 ? count : 0;
    }

    /// <summary>The outward unit normal of a face of a shape of half extents <paramref name="half"/>.</summary>
    public static Vector3 FaceNormal(BlockShape shape, Vector3 half, int side)
    {
        if (side == SlantSide(shape)) return Vector3.Normalize(Slant(shape, half));
        var normal = Vector3.Zero;
        normal[side >> 1] = (side & 1) == 0 ? 1 : -1;
        return normal;
    }

    private static int Lean(BlockShape shape, int corner)
    {
        Decode(shape, out _, out var u, out var v, out var su, out var sv);
        return su * (((corner >> u) & 1) * 2 - 1) + sv * (((corner >> v) & 1) * 2 - 1);
    }

    private static int Bits(int a, int sa, int b, int sb, int c, int sc) =>
        (sa > 0 ? 1 << a : 0) | (sb > 0 ? 1 << b : 0) | (sc > 0 ? 1 << c : 0);

    /// <summary>Positive right-handed quarter turn around a grid axis.</summary>
    public static BlockShape Rotate(BlockShape shape, int axis)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)axis, 2u);
        if (shape == BlockShape.Box) return shape;
        Decode(shape, out _, out var u, out var v, out var su, out var sv);
        var c = Vector3.Zero; c[u] = su; c[v] = sv;
        var a = (axis + 1) % 3; var b = (axis + 2) % 3;
        (c[a], c[b]) = (-c[b], c[a]);
        var e = c.X == 0 ? 0 : c.Y == 0 ? 1 : 2;
        return (BlockShape)(1 + 4 * e + (c[(e + 1) % 3] < 0 ? 1 : 0) + (c[(e + 2) % 3] < 0 ? 2 : 0));
    }
}
