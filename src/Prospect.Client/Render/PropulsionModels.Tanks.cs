using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// One round vessel of a tank's model: a capsule along <see cref="Axis"/>, its two hemispheres and its cylinder of one
/// radius. A cylinder of no length is a sphere. Never an ellipsoid: every point of its surface is
/// <see cref="Radius"/> from the segment of the axis that runs <see cref="Half"/> either side of
/// <see cref="Center"/>.
/// </summary>
internal readonly record struct TankVessel(Vector3 Center, int Axis, float Radius, float Half);

internal static partial class PropulsionModels
{
    /// <summary>The most vessels one tank block holds side by side: the bound of <see cref="TankLook.MaxVessels"/>.</summary>
    public const int MaxTankVessels = 32;

    /// <summary>The most clamp bands round one vessel.</summary>
    public const int MaxTankBands = 32;

    /// <summary>The look of a tank of <paramref name="type"/> in <paramref name="shape"/>, or null for a block drawn as its plain shape.</summary>
    public static TankFuelLook? TankOf(BlockType type, BlockShape shape, PropulsionLook look) =>
        shape == BlockShape.Box && type.Tank != FuelKind.None && look.Tanks is { } tanks ? tanks.Of(type.Tank) : null;

    /// <summary>True when a block of <paramref name="type"/> in <paramref name="shape"/> is drawn as a model: a thrust part's or a tank's.</summary>
    public static bool HasModel(BlockType type, BlockShape shape, PropulsionLook look) =>
        ModelOf(type, shape, look) is not null || TankOf(type, shape, look) is not null || (shape == BlockShape.Box && ScannerOf(type, look) is not null)
        || (shape == BlockShape.Box && type.Cargo > 0 && look.Rack is not null);

    /// <summary>
    /// Adds the model of one part of <paramref name="type"/> filling <paramref name="box"/>, alone: no neighbours,
    /// no nozzles listed. What <see cref="PartModels"/> builds for the editor and for far hulls drawn block by block,
    /// where every neighbour is a whole cube. Nothing for a type with no model.
    /// </summary>
    public static void BuildPart(BlockType type, in PartBox box, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        if (type.Cargo > 0 && look.Rack is { } rack)
            BuildRack(box, rack, look.Cargo, vertices, indices);
        else if (ModelOf(type, BlockShape.Box, look) is { } model)
        {
            if (type.Tractor is { } spec && look.Tractor is { } tractor)
                BuildTractorBase(model, box, tractor, spec, vertices, indices);
            else
                Build(model, box, type.Fuel, 0, true, vertices, indices, null);
        }
        else if (TankOf(type, BlockShape.Box, look) is { } paint)
            BuildTank(look.Tanks!, paint, box, 0, 0, vertices, indices);
        else if (ScannerOf(type, look) is { } scanner)
            BuildScanner(scanner, type, box, vertices, indices);
    }

    // A tank of a design: its frame is closed where a neighbour touches it, since the neighbour leaves out a face the
    // tank wholly covers, and on a face that carries mounted parts, which sit on it.
    private static bool AddTank(HullDesign design, int block, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var paint = TankOf(design.Types[block], design.Shapes[block], look);
        if (paint is null)
            return false;
        design.MountsOf(block, out var first, out var end);
        var mounted = 0;
        for (var m = first; m < end; m++)
            mounted |= 1 << (int)design.Mounts[m].Face;
        var box = new PartBox(design.BlockOffsets[block], design.BlockHalfExtents[block], design.Boxes[block].Facing, design.BlockSize > 0 ? design.BlockSize : 1f);
        BuildTank(look.Tanks!, paint, box, design.TouchedFaces[block], mounted, vertices, indices);
        return true;
    }

    /// <summary>
    /// Where the vessels of a tank of <paramref name="half"/> extents go, centred on its box, and the frame's beam
    /// thickness; returns how many. The rule, the same for any size:
    /// <list type="bullet">
    /// <item>The radius comes from the smallest side, never the longest, so a vessel is never stretched.</item>
    /// <item>A cube holds one sphere, its poles along <paramref name="facing"/>.</item>
    /// <item>A longer box holds a capsule along its longest side: a cylinder with two hemispheres of the same radius.</item>
    /// <item>A slab (two long sides) holds parallel capsules along its longest side, side by side across the middle
    /// side, as many as the middle side is smallest sides long (rounded, at most <see cref="TankLook.MaxVessels"/>).</item>
    /// </list>
    /// Ties between sides go to the lower axis. <paramref name="across"/> is the middle side, the vessels' order.
    /// Allocates nothing.
    /// </summary>
    public static int TankLayout(TankLook look, Vector3 half, BlockFacing facing, Span<TankVessel> into, out float beam, out int across)
    {
        // The axes from the longest side to the smallest, ties to the lower axis; a cube's poles along its facing.
        Span<int> order = [0, 1, 2];
        for (var i = 1; i < 3; i++)
            for (var j = i; j > 0 && half[order[j]] > half[order[j - 1]]; j--)
                (order[j], order[j - 1]) = (order[j - 1], order[j]);
        int a = order[0], b = order[1], c = order[2];
        if (half.X == half.Y && half.Y == half.Z)
        {
            a = Facings.Axis(facing);
            b = (a + 1) % 3;
            c = (a + 2) % 3;
        }

        across = b;
        var small = half[c];
        beam = look.Beam * 2f * small;
        var count = Math.Clamp((int)MathF.Round(half[b] / small), 1, Math.Min(look.MaxVessels, into.Length));
        var strip = half[b] / count;
        var room = MathF.Min(strip, small) - beam;
        var radius = look.TankShare * room;
        var length = MathF.Max(half[a] - beam - room, 0f);
        for (var k = 0; k < count; k++)
        {
            var centre = Vector3.Zero;
            centre[b] = -half[b] + (2 * k + 1) * strip;
            into[k] = new TankVessel(centre, a, radius, length);
        }

        return count;
    }

    /// <summary>
    /// Builds a tank's model filling <paramref name="box"/>: square beams along the box's twelve edges (and between
    /// the vessels of a slab), the vessels of <see cref="TankLayout"/> in their paint, a clamp band and four struts out
    /// to the frame at every band, and toward the facing a pipe stub on a cross brace. The rest of the box is open.
    /// <paramref name="closed"/> faces (bits as <see cref="HullDesign.ExteriorFaces"/>) get a plate inside the frame,
    /// facing in; <paramref name="mounted"/> faces get it facing both ways, for the parts mounted on them. A pure
    /// function of the box and the look, built where hull meshes are, on a worker; never in a frame.
    /// </summary>
    public static void BuildTank(TankLook look, TankFuelLook paint, in PartBox box, int closed, int mounted, List<MeshVertex> vertices, List<uint> indices)
    {
        Span<TankVessel> vessels = stackalloc TankVessel[MaxTankVessels];
        var count = TankLayout(look, box.Half, box.Facing, vessels, out var beam, out var b);
        var half = box.Half;
        var a = vessels[0].Axis;
        var c = 3 - a - b;
        var small = half[c];
        var strip = half[b] / count;
        var strut = look.Strut * small;
        var frame = Tag(look.FrameShade);
        var metal = Tag(look.StrutShade);
        var pipe = Tag(look.PipeShade);
        var plate = Tag(look.PlateShade);
        var body = Paint(paint.Body);
        var stripe = Paint(paint.Stripe);
        var hb = 0.5f * beam;

        // The frame: twelve edge beams, flush with the box's faces. The beams along x run the whole length and own the
        // corners; the others stop against them.
        for (var axis = 0; axis < 3; axis++)
        {
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            var reach = axis == 0 ? half[axis] : half[axis] - beam;
            for (var k = 0; k < 4; k++)
            {
                var at = Vector3.Zero;
                at[u] = (k is 0 or 3 ? 1f : -1f) * (half[u] - hb);
                at[v] = (k is 0 or 1 ? 1f : -1f) * (half[v] - hb);
                Bar(vertices, indices, box.Center + at, axis, reach, hb, hb, frame);
            }
        }

        // A slab's vessels each have a frame of their own: beams along the long side between them, top and bottom,
        // and across the two end faces.
        for (var j = 1; j < count; j++)
        {
            var line = -half[b] + j * 2f * strip;
            for (var k = 0; k < 2; k++)
            {
                var sign = k == 0 ? 1f : -1f;
                var along = Vector3.Zero;
                along[b] = line;
                along[c] = sign * (small - hb);
                Bar(vertices, indices, box.Center + along, a, half[a] - beam, hb, hb, frame);
                var across = Vector3.Zero;
                across[b] = line;
                across[a] = sign * (half[a] - hb);
                Bar(vertices, indices, box.Center + across, c, small - beam, hb, hb, frame);
            }
        }

        // Plates inside the frame where the box is closed, so a neighbour's left-out face never shows through.
        Span<Vector2> rect = stackalloc Vector2[4];
        for (var side = 0; side < 6; side++)
        {
            var bit = 1 << side;
            if (((closed | mounted) & bit) == 0)
                continue;
            var axis = side >> 1;
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            var normal = Unit(axis) * ((side & 1) == 0 ? 1f : -1f);
            var g = new Geometry(vertices, indices, new Frame(box.Center, normal, Unit(u), Unit(v), half[axis]));
            Rectangle(rect, half[u] - beam, half[v] - beam);
            g.Cap(rect, Vector2.Zero, 2f * half[axis] - 0.1f * beam, -1f, plate);
            if ((mounted & bit) != 0)
                g.Cap(rect, Vector2.Zero, 2f * half[axis], 1f, plate);
        }

        var f = Facings.Axis(box.Facing);
        var toward = Facings.Vector(box.Facing)[f];
        for (var k = 0; k < count; k++)
        {
            var vessel = vessels[k];
            var centre = box.Center + vessel.Center;
            // Where the strip's frame is on each side across the long axis: the box's edge beams, or a beam between
            // two vessels, centred on the line between them.
            var low = k == 0 ? strip - hb : strip;
            var high = k == count - 1 ? strip - hb : strip;
            AddVessel(vertices, indices, look, vessel, centre, b, c, low, high, small - hb, strut, body, stripe, metal);

            // The pipe stub and its cross brace, on the face the tank faces; a vessel whose strip does not reach that
            // face (one between others, across a slab) has none.
            if (f == b && k != (toward > 0 ? count - 1 : 0))
                continue;
            AddStub(vertices, indices, look, vessel, centre, b, c, f, toward, half, strip, low, high, hb, strut, pipe, metal);
        }
    }

    // One vessel: its faceted body, painted, with a stripe round its middle; clamp bands; four struts from each band
    // out to the strip's frame, diagonally across the vessel.
    private static void AddVessel(List<MeshVertex> vertices, List<uint> indices, TankLook look, in TankVessel vessel, Vector3 centre,
        int b, int c, float low, float high, float reachC, float strut, float body, float stripe, float metal)
    {
        var a = vessel.Axis;
        var r = vessel.Radius;
        var l = vessel.Half;
        var sides = look.Sides;
        var turn = MathF.PI / sides;
        var g = new Geometry(vertices, indices, new Frame(centre, Unit(a), Unit(b), Unit(c), 0f));

        // The profile from pole to pole: the hemispheres' rings, the cylinder split at the stripe's edges.
        Span<float> zs = stackalloc float[2 * 6 + 6];
        Span<float> rs = stackalloc float[2 * 6 + 6];
        var n = 0;
        var rings = look.CapRings;
        var stripeHalf = look.Stripe * r;
        for (var i = rings; i >= 1; i--)
        {
            zs[n] = -l - (i == rings ? r : r * MathF.Sin(MathF.PI / 2f * i / rings));
            rs[n++] = i == rings ? 0f : r * MathF.Cos(MathF.PI / 2f * i / rings);
        }
        zs[n] = -l;
        rs[n++] = r;
        if (stripeHalf > 0 && stripeHalf < l)
        {
            zs[n] = -stripeHalf;
            rs[n++] = r;
            zs[n] = stripeHalf;
            rs[n++] = r;
        }
        if (l > 0)
        {
            zs[n] = l;
            rs[n++] = r;
        }
        for (var i = 1; i <= rings; i++)
        {
            zs[n] = l + (i == rings ? r : r * MathF.Sin(MathF.PI / 2f * i / rings));
            rs[n++] = i == rings ? 0f : r * MathF.Cos(MathF.PI / 2f * i / rings);
        }

        Span<Vector2> ring0 = stackalloc Vector2[sides];
        Span<Vector2> ring1 = stackalloc Vector2[sides];
        for (var i = 0; i + 1 < n; i++)
        {
            Polygon(ring0, Vector2.Zero, rs[i], turn);
            Polygon(ring1, Vector2.Zero, rs[i + 1], turn);
            var middle = 0.5f * (zs[i] + zs[i + 1]);
            g.Loft(ring0, zs[i], ring1, zs[i + 1], Vector2.Zero, MathF.Abs(middle) <= stripeHalf ? stripe : body, 1f);
        }

        // Clamp bands: one round a sphere's equator, else at both ends of the cylinder and evenly between, no further
        // apart than the look's spacing.
        var bands = l <= 0 ? 1 : Math.Clamp((int)MathF.Ceiling(2f * l / (look.BandSpacing * r)) + 1, 2, MaxTankBands);
        var width = 0.5f * look.BandWidth * r;
        var outer = r * (1f + look.BandLift);
        for (var j = 0; j < bands; j++)
        {
            var z = bands == 1 ? 0f : -l + 2f * l * j / (bands - 1);
            if (width > 0)
            {
                Polygon(ring0, Vector2.Zero, outer, turn);
                Polygon(ring1, Vector2.Zero, 0.9f * r, turn);
                g.Loft(ring0, z - width, ring0, z + width, Vector2.Zero, metal, 1f);
                g.Annulus(ring0, ring1, Vector2.Zero, z - width, -1f, metal);
                g.Annulus(ring0, ring1, Vector2.Zero, z + width, 1f, metal);
            }

            // Four struts from inside the vessel through the band, out to the frame's beams at the strip's corners.
            for (var q = 0; q < 4; q++)
            {
                var target = new Vector2(q is 0 or 3 ? high : -low, q is 0 or 1 ? reachC : -reachC);
                var from = Vector2.Normalize(target) * (0.85f * r);
                var start = g.F.At(from.X, from.Y, z);
                var end = g.F.At(target.X, target.Y, z);
                Strut(vertices, indices, start, end, Unit(a), strut, metal);
            }
        }
    }

    // A pipe stub from the vessel to the face the tank faces, ending in a flange on a cross brace over the strip's
    // part of that face.
    private static void AddStub(List<MeshVertex> vertices, List<uint> indices, TankLook look, in TankVessel vessel, Vector3 centre,
        int b, int c, int f, float toward, Vector3 half, float strip, float low, float high, float hb, float strut, float pipe, float metal)
    {
        var a = vessel.Axis;
        var r = vessel.Radius;
        var direction = Unit(f) * toward;
        var face = half[f] - hb - toward * vessel.Center[f];
        var from = f == a ? vessel.Half + 0.9f * r : 0.9f * r;
        // The brace is two bars corner to corner, one a little thinner so their faces never lie in one plane.
        var u = (f + 1) % 3;
        var v = (f + 2) % 3;
        var faceCentre = centre + direction * face;
        Span<Vector2> corners = stackalloc Vector2[4];
        // The strip's corners on that face, at the beams' centres: across the vessels the strip's own edges.
        float ul = half[u] - hb, uh = half[u] - hb, vl = half[v] - hb, vh = half[v] - hb;
        if (u == b) { ul = low; uh = high; }
        if (v == b) { vl = low; vh = high; }
        corners[0] = new Vector2(-ul, -vl);
        corners[1] = new Vector2(uh, vh);
        corners[2] = new Vector2(-ul, vh);
        corners[3] = new Vector2(uh, -vl);
        for (var k = 0; k < 2; k++)
        {
            var p = faceCentre + Unit(u) * corners[2 * k].X + Unit(v) * corners[2 * k].Y;
            var q = faceCentre + Unit(u) * corners[2 * k + 1].X + Unit(v) * corners[2 * k + 1].Y;
            var depth = (k == 0 ? 0.6f : 0.45f) * hb;
            var across = Vector3.Normalize(Vector3.Cross(direction, q - p));
            BeamBetween(vertices, indices, p, q, across, strut, depth, metal);
        }

        // The stub and its flange, which stops at the brace's top.
        var g = new Geometry(vertices, indices, new Frame(centre, direction, Unit((f + 1) % 3), Unit((f + 2) % 3), 0f));
        Span<Vector2> ring = stackalloc Vector2[8];
        Span<Vector2> flange = stackalloc Vector2[8];
        var radius = look.Pipe * r;
        var top = face - 0.6f * hb;
        var thick = MathF.Min(0.6f * radius, 0.5f * MathF.Max(top - from, 0f));
        Polygon(ring, Vector2.Zero, radius, MathF.PI / 8f);
        Polygon(flange, Vector2.Zero, 1.8f * radius, MathF.PI / 8f);
        if (top - thick > from)
            g.Loft(ring, from, ring, top - thick, Vector2.Zero, pipe, 1f);
        g.Loft(flange, top - thick, flange, top, Vector2.Zero, pipe, 1f);
        g.Annulus(flange, ring, Vector2.Zero, top - thick, -1f, pipe);
        g.Cap(flange, Vector2.Zero, top, 1f, pipe);
    }

    // A square beam along a grid axis, centred on a point, reaching that far either side, with both ends capped.
    private static void Bar(List<MeshVertex> vertices, List<uint> indices, Vector3 centre, int axis, float reach, float hu, float hv, float tag)
    {
        var g = new Geometry(vertices, indices, new Frame(centre, Unit(axis), Unit((axis + 1) % 3), Unit((axis + 2) % 3), reach));
        Span<Vector2> rect = stackalloc Vector2[4];
        Rectangle(rect, hu, hv);
        g.Loft(rect, 0f, rect, 2f * reach, Vector2.Zero, tag, 1f);
        g.Cap(rect, Vector2.Zero, 0f, -1f, tag);
        g.Cap(rect, Vector2.Zero, 2f * reach, 1f, tag);
    }

    // A thin square strut between two points; its ends are buried in the vessel and the frame, so they are left open.
    private static void Strut(List<MeshVertex> vertices, List<uint> indices, Vector3 from, Vector3 to, Vector3 side, float half, float tag)
    {
        var along = to - from;
        var length = along.Length();
        if (length < 1e-5f)
            return;
        var n = along / length;
        var v = Vector3.Normalize(Vector3.Cross(n, side));
        var u = Vector3.Cross(v, n);
        var g = new Geometry(vertices, indices, new Frame(0.5f * (from + to), n, u, v, 0.5f * length));
        Span<Vector2> rect = stackalloc Vector2[4];
        Rectangle(rect, half, half);
        g.Loft(rect, 0f, rect, length, Vector2.Zero, tag, 1f);
    }

    // A flat bar between two points: <paramref name="width"/> across in the plane of <paramref name="across"/>, that
    // and <paramref name="depth"/> deep either side of it, both ends capped.
    private static void BeamBetween(List<MeshVertex> vertices, List<uint> indices, Vector3 from, Vector3 to, Vector3 across, float width, float depth, float tag)
    {
        var along = to - from;
        var length = along.Length();
        if (length < 1e-5f)
            return;
        var n = along / length;
        var v = Vector3.Normalize(Vector3.Cross(n, across));
        var u = Vector3.Cross(v, n);
        var g = new Geometry(vertices, indices, new Frame(0.5f * (from + to), n, u, v, 0.5f * length));
        Span<Vector2> rect = stackalloc Vector2[4];
        Rectangle(rect, width, depth);
        g.Loft(rect, 0f, rect, length, Vector2.Zero, tag, 1f);
        g.Cap(rect, Vector2.Zero, 0f, -1f, tag);
        g.Cap(rect, Vector2.Zero, length, 1f, tag);
    }

    /// <summary>
    /// The normal length that carries a paint to the mesh shader: 3.5 plus a colour of five bits a channel, centred in
    /// its step, so <c>mesh.vert</c> reads it back exactly. Bare metal (<see cref="Tag"/>) stays below 3.
    /// </summary>
    public static float Paint(ReadOnlySpan<float> rgb)
    {
        static int Level(float c) => Math.Clamp((int)MathF.Round(c * 31f), 0, 31);
        var code = (Level(rgb[0]) << 10) | (Level(rgb[1]) << 5) | Level(rgb[2]);
        return 3.5f + (code + 0.5f) / 32768f;
    }

    /// <summary>Encodes an emissive RGB colour in the normal length (<c>4.5..</c>), read by <c>mesh.vert</c>.</summary>
    public static float Emissive(uint rgb)
    {
        static int Level(uint c) => Math.Clamp((int)MathF.Round(c / 255f * 31f), 0, 31);
        var code = (Level((rgb >> 16) & 0xFF) << 10) | (Level((rgb >> 8) & 0xFF) << 5) | Level(rgb & 0xFF);
        return 4.5f + (code + 0.5f) / 32768f;
    }

    /// <summary>The paint a normal's length carries, as <c>mesh.vert</c> reads it; false for plating and bare metal.</summary>
    public static bool PaintOf(float length, out Vector3 rgb)
    {
        rgb = default;
        // At 4.5 and over the colour is light the part gives off (Emissive), not paint.
        if (length < 3.25f || length >= 4.5f)
            return false;
        var code = (int)MathF.Floor((length - 3.5f) * 32768f);
        rgb = new Vector3((code >> 10) & 31, (code >> 5) & 31, code & 31) / 31f;
        return true;
    }
}
