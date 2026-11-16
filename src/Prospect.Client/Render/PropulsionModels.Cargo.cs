using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// The loading deck's proportions, the clamp jaws and container paint, loaded before mesh workers start. Every
/// thickness is a share of one cargo unit; a thickness must fit the clearance a container keeps from its slot wall
/// (<see cref="Gap"/> of the smallest half-side), since containers fill the whole grid.
/// </summary>
internal sealed class RackLook
{
    /// <summary>Deck plate thickness.</summary>
    public float Deck { get; init; }
    /// <summary>Deck plate brightness.</summary>
    public float DeckShade { get; init; }
    /// <summary>Rail width.</summary>
    public float Rail { get; init; }
    /// <summary>Rail height above the deck.</summary>
    public float RailHeight { get; init; }
    /// <summary>Rail brightness.</summary>
    public float RailShade { get; init; }
    /// <summary>Corner guide post width.</summary>
    public float Post { get; init; }
    /// <summary>Corner guide post height above the deck, never past the far face.</summary>
    public float PostHeight { get; init; }
    /// <summary>Post brightness.</summary>
    public float PostShade { get; init; }
    /// <summary>Clamp jaw thickness.</summary>
    public float Jaw { get; init; }
    /// <summary>Jaw width as a share of the container side it grips.</summary>
    public float JawWidth { get; init; }
    /// <summary>Jaw height as a share of the container's height.</summary>
    public float JawHeight { get; init; }
    /// <summary>Jaw brightness.</summary>
    public float JawShade { get; init; }
    /// <summary>Guide light edge length.</summary>
    public float LightSize { get; init; }
    /// <summary>Cargo units between guide lights along a rail.</summary>
    public int LightEvery { get; init; }
    /// <summary>Guide light RGB, shown along the rails and on the posts.</summary>
    public float[] Light { get; init; } = [];
    /// <summary>Light RGB on a closed jaw.</summary>
    public float[] ClampLight { get; init; } = [];
    /// <summary>Container clearance as a share of its smallest half-side.</summary>
    public float Gap { get; init; }
    /// <summary>Rib half-width as a share of the inset body half-side.</summary>
    public float Rib { get; init; }
    /// <summary>Dark rib metal shade.</summary>
    public float RibShade { get; init; }
    /// <summary>Corner block half-size as a share of the container's smallest half-side.</summary>
    public float Corner { get; init; }
    /// <summary>How far a corner block stands off the body, as a share of its own half-size.</summary>
    public float CornerLift { get; init; }
    /// <summary>Corner block brightness.</summary>
    public float CornerShade { get; init; }
    /// <summary>Door seam width as a share of the smallest half-side.</summary>
    public float Seam { get; init; }
    /// <summary>Door seam and lock rod brightness.</summary>
    public float SeamShade { get; init; }
    /// <summary>Lock rod width as a share of the smallest half-side.</summary>
    public float Rod { get; init; }
    /// <summary>Lifting eye size as a share of the smallest half-side.</summary>
    public float Eye { get; init; }
    /// <summary>Lifting eye brightness.</summary>
    public float EyeShade { get; init; }
    /// <summary>How far the body takes its resource's colour: 0 is the plain body, 1 the resource colour.</summary>
    public float BodyTint { get; init; }
    /// <summary>Label plate thickness as a share of the clearance a container keeps.</summary>
    public float PlateDepth { get; init; }
    /// <summary>Stroke width as a share of a glyph's height.</summary>
    public float Stroke { get; init; }
    /// <summary>Brightness of the code's strokes on the resource plate.</summary>
    public float TextShade { get; init; }
    /// <summary>Plate RGB for an empty container.</summary>
    public float[] EmptyPlate { get; init; } = [];
    /// <summary>Plate RGB behind the size-class mark.</summary>
    public float[] SizePlate { get; init; } = [];
    /// <summary>Empty container body RGB.</summary>
    public float[] Body { get; init; } = [];
    /// <summary>One RGB stroke colour for each ascending size's class mark.</summary>
    public float[][] Stripes { get; init; } = [];

    /// <summary>The galaxy's resources, in index order: colour and code for each container plate. Set at load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Resource[] Galaxy { get; private set; } = [];

    /// <summary>The cargo catalogue, for size names. Set at load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CargoCatalog? Cargo { get; private set; }

    /// <summary>Gives the look what its container plates read: resources and size names.</summary>
    public void Attach(Resource[] resources, CargoCatalog cargo) { Galaxy = resources; Cargo = cargo; }

    /// <summary>Refuses proportions or colours outside model bounds.</summary>
    public void Check()
    {
        var room = .5f * Gap;
        if (!(Gap is > 0 and <= .2f) || !(Rib > 0) || Rib >= Gap || !(RibShade is >= 0 and <= 1) || Stripes.Length != 8)
            throw new InvalidDataException("Invalid rack proportions.");
        foreach (var thin in new[] { Deck, Rail, Post, Jaw })
            if (!(thin > 0 && thin <= room)) throw new InvalidDataException("A rack part is thicker than the clearance a container keeps.");
        if (!(RailHeight > 0 && RailHeight <= 1) || !(PostHeight >= RailHeight && PostHeight <= 4) || !(JawWidth is > 0 and <= 1) || !(JawHeight is > 0 and <= 1)
            || !(LightSize > 0 && LightSize <= Rail) || LightEvery is < 1 or > 16
            || !(DeckShade is > 0 and <= 1) || !(RailShade is > 0 and <= 1) || !(PostShade is > 0 and <= 1) || !(JawShade is > 0 and <= 1))
            throw new InvalidDataException("Invalid rack proportions.");
        if (!(Corner is > 0 and <= .4f) || !(CornerLift is > 0 and <= 1) || !(Seam is > 0 and <= .1f) || !(Rod is > 0 and <= .1f) || !(Eye is > 0 and <= .4f)
            || !(BodyTint is >= 0 and <= 1) || !(PlateDepth is > 0 and <= 1) || !(Stroke is > 0 and <= .3f)
            || !(CornerShade is > 0 and <= 1) || !(SeamShade is > 0 and <= 1) || !(EyeShade is > 0 and <= 1) || !(TextShade is >= 0 and <= 1))
            throw new InvalidDataException("Invalid container detail proportions.");
        CheckPaint(Body);
        CheckPaint(EmptyPlate);
        CheckPaint(SizePlate);
        CheckPaint(Light);
        CheckPaint(ClampLight);
        foreach (var stripe in Stripes) CheckPaint(stripe);
    }
    private static void CheckPaint(float[] paint)
    {
        if (paint.Length != 3 || paint.Any(c => !float.IsFinite(c) || c < 0 || c > 1))
            throw new InvalidDataException("Invalid cargo paint.");
    }

    /// <summary>An emissive colour for a light RGB.</summary>
    internal static uint Rgb(float[] c) => ((uint)MathF.Round(c[0] * 255) << 16) | ((uint)MathF.Round(c[1] * 255) << 8) | (uint)MathF.Round(c[2] * 255);
}

internal static partial class PropulsionModels
{
    /// <summary>The longest bound on guide lights along one rail, so cosmetic detail stays bounded for extreme boxes.</summary>
    public const int MaxRackLights = 32;

    private static void Slab(List<MeshVertex> vertices, List<uint> indices, Vector3 centre, Vector3 half, float tag) =>
        Bar(vertices, indices, centre, 0, half.X, half.Y, half.Z, tag);

    /// <summary>
    /// A loading deck: a solid plate on the face given by <c>box.Facing</c> (see <see cref="RackDeck"/>), two low rails
    /// along the longest axis of the deck, thin corner posts and guide lights. Every other side is open. Clamp jaws are
    /// not part of it: they belong to the containers (<see cref="ClampModel"/>). Built only on model workers.
    /// </summary>
    public static void BuildRack(in PartBox box, RackLook look, CargoCatalog cargo, List<MeshVertex> vertices, List<uint> indices, int closed = 0, int mounted = 0)
    {
        var h = box.Half;
        var centre = box.Center;
        var unit = cargo.UnitSize;
        var face = (int)box.Facing;
        var axis = face >> 1;
        var sign = (face & 1) == 0 ? 1f : -1f;
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        // The rails run along the longest axis of the deck, the other in-plane axis is across it.
        var along = h[u] >= h[v] ? u : v;
        var across = along == u ? v : u;
        var room = 2 * h[axis];
        var deck = MathF.Min(look.Deck * unit, room * .25f);
        var skin = .01f * unit;
        // Plate: outer face a hair inside the block's face, since a neighbour that covers part of it keeps its own.
        Vector3 At(float depth, float lift, float otherAlong, float otherAcross)
        {
            var p = centre;
            p[axis] += sign * (h[axis] - depth - lift);
            p[along] += otherAlong;
            p[across] += otherAcross;
            return p;
        }
        Vector3 Half(float height, float onAlong, float onAcross)
        {
            var e = Vector3.Zero;
            e[axis] = height * .5f; e[along] = onAlong; e[across] = onAcross;
            return e;
        }
        Slab(vertices, indices, At(skin + (deck - skin) * .5f, 0, 0, 0), Half(deck - skin, h[along], h[across]), Tag(look.DeckShade));
        var rail = look.Rail * unit * .5f;
        var railHeight = MathF.Min(look.RailHeight * unit, (room - deck) * .5f);
        for (var side = -1; side <= 1; side += 2)
            Slab(vertices, indices, At(deck, railHeight * .5f, 0, side * (h[across] - rail)), Half(railHeight, h[along], rail), Tag(look.RailShade));
        var post = look.Post * unit * .5f;
        var light = look.LightSize * unit * .5f;
        var postHeight = MathF.Max(railHeight, MathF.Min(look.PostHeight * unit, room - deck - 2 * light));
        var lightTag = Emissive(RackLook.Rgb(look.Light));
        for (var a = -1; a <= 1; a += 2)
            for (var c = -1; c <= 1; c += 2)
            {
                Slab(vertices, indices, At(deck, postHeight * .5f, a * (h[along] - post), c * (h[across] - post)), Half(postHeight, post, post), Tag(look.PostShade));
                Slab(vertices, indices, At(deck, postHeight + light, a * (h[along] - post), c * (h[across] - post)), Half(2 * light, light, light), lightTag);
            }
        // Lights sit on the rails, one every few units, bounded for long racks.
        var lights = Math.Clamp((int)(2 * h[along] / (unit * look.LightEvery)), 1, MaxRackLights);
        for (var side = -1; side <= 1; side += 2)
            for (var k = 0; k < lights; k++)
                Slab(vertices, indices, At(deck, railHeight + light * .5f, -h[along] + 2 * h[along] * (k + .5f) / lights, side * (h[across] - rail)),
                    Half(light, light, light), lightTag);

        // Neighbours omit their covered faces; close those side faces from inside the rack. The deck is its own closure.
        Span<Vector2> rect = stackalloc Vector2[4];
        for (var side = 0; side < 6; side++)
        {
            var bit = 1 << side;
            if (((closed | mounted) & bit) == 0 || side == face) continue;
            var a2 = side >> 1;
            var u2 = (a2 + 1) % 3;
            var v2 = (a2 + 2) % 3;
            var normal = Unit(a2) * ((side & 1) == 0 ? 1f : -1f);
            var g = new Geometry(vertices, indices, new Frame(box.Center, normal, Unit(u2), Unit(v2), h[a2]));
            Rectangle(rect, h[u2], h[v2]);
            g.Cap(rect, Vector2.Zero, 2 * h[a2] - skin, -1, Tag(look.DeckShade));
            if ((mounted & bit) != 0) g.Cap(rect, Vector2.Zero, 2 * h[a2], 1, Tag(look.DeckShade));
        }
    }

    /// <summary>
    /// The clamp jaws holding one container of <paramref name="shape"/> (cargo units) in its slot, centred on the slot,
    /// for a rack whose deck is on <paramref name="deck"/>. Four thin jaws grip the sides next to the deck, each with a
    /// light that shows it closed. An empty slot has none, which leaves the deck clean. Built only on model workers.
    /// </summary>
    public static void ClampModel(GridSize shape, BlockFacing deck, RackLook look, CargoCatalog cargo, List<MeshVertex> vertices, List<uint> indices)
    {
        var unit = cargo.UnitSize;
        var slot = new Vector3(shape.X, shape.Y, shape.Z) * (unit * .5f);
        var inset = MathF.Min(slot.X, MathF.Min(slot.Y, slot.Z)) * look.Gap;
        var body = slot - new Vector3(inset);
        var axis = (int)deck >> 1;
        var sign = ((int)deck & 1) == 0 ? 1f : -1f;
        var jaw = MathF.Min(look.Jaw * unit, inset);
        var height = MathF.Max(look.JawHeight * 2 * body[axis], jaw);
        var light = look.LightSize * unit * .5f;
        var tag = Tag(look.JawShade);
        var lit = Emissive(RackLook.Rgb(look.ClampLight));
        for (var k = 1; k <= 2; k++)
        {
            var w = (axis + k) % 3;
            var other = (axis + 3 - k) % 3;
            for (var side = -1; side <= 1; side += 2)
            {
                var centre = Vector3.Zero;
                centre[w] = side * (body[w] + jaw * .5f);
                // From the slot wall on the deck side up the container's side.
                centre[axis] = sign * (slot[axis] - height * .5f);
                var half = Vector3.Zero;
                half[w] = jaw * .5f; half[axis] = height * .5f; half[other] = look.JawWidth * body[other];
                Slab(vertices, indices, centre, half, tag);
                centre[axis] = sign * (slot[axis] - height - light);
                half = new Vector3(light);
                half[w] = MathF.Min(light, jaw * .5f);
                Slab(vertices, indices, centre, half, lit);
            }
        }
    }

    /// <summary>A container of <paramref name="size"/> with no resource, at a packed slot.</summary>
    public static void ContainerModel(in PartBox box, int size, RackLook look, List<MeshVertex> vertices, List<uint> indices) =>
        ContainerModel(box, size, -1, look, vertices, indices);

    /// <summary>
    /// A shipping container at a packed slot: a body tinted toward its resource, corner blocks, door seams and lock rods
    /// on its ends, lifting eyes on top, rib bands, a label plate in the resource's colour with its code and a plate
    /// with the size class, on both long sides. Every detail stands off the body by less than the clearance the slot
    /// leaves, so nothing reaches a neighbour. One model for racks and for free containers; built only on workers.
    /// </summary>
    public static void ContainerModel(in PartBox box, int size, int resource, RackLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var slot = box.Half;
        var inset = MathF.Min(slot.X, MathF.Min(slot.Y, slot.Z)) * look.Gap;
        var h = slot - new Vector3(inset);
        var centre = box.Center;
        var m = MathF.Min(h.X, MathF.Min(h.Y, h.Z));
        var pro = inset * .85f;
        var res = (uint)resource < (uint)look.Galaxy.Length ? look.Galaxy[resource] : null;
        var plain = new Vector3(look.Body[0], look.Body[1], look.Body[2]);
        var body = res is null ? plain : Vector3.Lerp(plain, res.Color, look.BodyTint);
        Span<float> rgb = stackalloc float[3];
        rgb[0] = body.X; rgb[1] = body.Y; rgb[2] = body.Z;
        Slab(vertices, indices, centre, h, Paint(rgb));

        // Corner blocks: the strong points a container is stacked and lashed by.
        var c = look.Corner * m;
        var lift = MathF.Min(c * look.CornerLift, pro);
        var cornerTag = Tag(look.CornerShade);
        for (var i = 0; i < 8; i++)
        {
            var sign = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            Slab(vertices, indices, centre + sign * (h - new Vector3(c - lift)), new Vector3(c), cornerTag);
        }

        // Rib bands round the body, near each end.
        var rib = MathF.Min(m * look.Rib, pro);
        var ribTag = Tag(look.RibShade);
        for (var sx = -1; sx <= 1; sx += 2)
            Slab(vertices, indices, centre + new Vector3(sx * h.X * .72f, 0, 0), new Vector3(rib, h.Y + rib, h.Z + rib), ribTag);

        // A door on each end: a seam down the middle and two lock rods.
        var seamTag = Tag(look.SeamShade);
        var seam = look.Seam * m;
        var rod = look.Rod * m;
        var doorDepth = MathF.Min(pro * .6f, seam * 1.5f);
        var doorHalf = MathF.Max(h.Y - 2 * c, c);
        for (var sx = -1; sx <= 1; sx += 2)
        {
            var face = centre + new Vector3(sx * (h.X + doorDepth * .25f), 0, 0);
            var depthHalf = doorDepth * .75f;
            Slab(vertices, indices, face, new Vector3(depthHalf, doorHalf, seam), seamTag);
            for (var r = -1; r <= 1; r += 2)
                Slab(vertices, indices, face + new Vector3(0, 0, r * h.Z * .5f), new Vector3(depthHalf, doorHalf, rod), seamTag);
        }

        // Lifting eyes: four, on the top face, each a pad with a loop standing on it.
        var eye = look.Eye * m;
        var eyeHeight = MathF.Min(pro, eye * 1.5f);
        var eyeTag = Tag(look.EyeShade);
        for (var sx = -1; sx <= 1; sx += 2)
            for (var sz = -1; sz <= 1; sz += 2)
            {
                var at = centre + new Vector3(sx * (h.X - 2.4f * c), h.Y, sz * (h.Z - 2.4f * c));
                Slab(vertices, indices, at + new Vector3(0, eyeHeight * .15f, 0), new Vector3(eye, eyeHeight * .35f, eye), eyeTag);
                for (var leg = -1; leg <= 1; leg += 2)
                    Slab(vertices, indices, at + new Vector3(leg * eye * .8f, eyeHeight * .5f, 0), new Vector3(eye * .2f, eyeHeight * .5f, eye * .2f), eyeTag);
                Slab(vertices, indices, at + new Vector3(0, eyeHeight * .9f, 0), new Vector3(eye, eyeHeight * .1f, eye * .2f), eyeTag);
            }

        // Plates on both long sides, each reading left to right as seen from outside.
        var plateDepth = MathF.Max(inset * look.PlateDepth, 1e-4f);
        var stroke = look.Stroke;
        var emptyRgb = look.EmptyPlate;
        var plateColour = res is null ? emptyRgb : new[] { res.Color.X, res.Color.Y, res.Color.Z };
        var code = res?.Code ?? "EMPTY";
        var sizeName = look.Cargo is { } cargo && (uint)size < (uint)cargo.Sizes.Length ? cargo.Sizes[size].Name : "";
        for (var side = -1; side <= 1; side += 2)
        {
            var normal = new Vector3(0, 0, side);
            var right = new Vector3(side, 0, 0);
            var up = Vector3.UnitY;
            var plane = centre + normal * h.Z;
            // The resource's plate, high on the side.
            var top = new Vector3(0, h.Y * .3f, 0);
            var topHalf = new Vector2(h.X * .66f, h.Y * .3f);
            Plate(vertices, indices, plane + top, topHalf, normal, right, up, plateDepth, Paint(plateColour));
            Label(vertices, indices, code, plane + top, topHalf * .86f, normal, right, up, plateDepth, stroke, Tag(look.TextShade));
            // The size class, small and low.
            var low = new Vector3(0, -h.Y * .46f, 0);
            var lowHalf = new Vector2(h.X * .4f, h.Y * .15f);
            Plate(vertices, indices, plane + low, lowHalf, normal, right, up, plateDepth, Paint(look.SizePlate));
            Label(vertices, indices, sizeName, plane + low, lowHalf * .8f, normal, right, up, plateDepth, stroke, Paint(look.Stripes[size]));
        }
    }

    // A thin slab lying on a side face: half width and height across it, plateDepth standing off it.
    private static void Plate(List<MeshVertex> vertices, List<uint> indices, Vector3 on, Vector2 half, Vector3 normal, Vector3 right, Vector3 up, float depth, float tag)
    {
        // From half a depth inside the body to the plate's top, one depth out.
        var centre = on + normal * (depth * .25f);
        var extent = Vector3.Abs(right) * half.X + Vector3.Abs(up) * half.Y + Vector3.Abs(normal) * (depth * .75f);
        Slab(vertices, indices, centre, extent, tag);
    }

    // Text as raised strokes on a plate: fitted into <paramref name="room"/> (half width, half height) about <paramref name="on"/>.
    private static void Label(List<MeshVertex> vertices, List<uint> indices, string text, Vector3 on, Vector2 room, Vector3 normal, Vector3 right, Vector3 up,
        float plateDepth, float stroke, float tag)
    {
        if (text.Length == 0) return;
        const float space = 1.2f;
        var unit = MathF.Min(room.Y * 2 / StrokeFont.Height, room.X * 2 / (text.Length * StrokeFont.Width + (text.Length - 1) * space));
        var half = unit * StrokeFont.Height * stroke * .5f;
        var total = text.Length * StrokeFont.Width + (text.Length - 1) * space;
        // Strokes stand on the plate's top (one depth out) and rise half a depth more.
        var raised = plateDepth * .25f;
        var origin = on - right * (total * unit * .5f) - up * (StrokeFont.Height * unit * .5f) + normal * (plateDepth + raised);
        for (var k = 0; k < text.Length; k++)
        {
            var left = k * (StrokeFont.Width + space);
            foreach (var line in StrokeFont.Of(text[k]).Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                Vector3 previous = default;
                var first = true;
                foreach (var point in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = origin + right * ((left + (point[0] - '0')) * unit) + up * ((point[1] - '0') * unit);
                    if (!first)
                    {
                        var direction = Vector3.Normalize(p - previous);
                        BeamBetween(vertices, indices, previous - direction * half, p + direction * half, normal, raised, half, tag);
                    }
                    previous = p;
                    first = false;
                }
            }
        }
    }

    /// <summary>Capture-only sample; no hull cargo state is inferred or changed.</summary>
    public static void RackPreview(in PartBox box, RackLook look, CargoCatalog cargo, List<MeshVertex> vertices, List<uint> indices)
    {
        var cells = new GridSize((int)MathF.Round(box.Half.X * 2 / box.CellSize), (int)MathF.Round(box.Half.Y * 2 / box.CellSize), (int)MathF.Round(box.Half.Z * 2 / box.CellSize));
        ReadOnlySpan<int> sample = [0, 1, 2, 3, 4];
        var capacity = CargoPacking.Capacity(cargo, cells, box.CellSize);
        if (capacity > 4096) return;
        Span<byte> occupied = stackalloc byte[capacity];
        Span<CargoPlacement> slots = stackalloc CargoPlacement[5];
        if (!CargoPacking.TryPack(cargo, cells, box.CellSize, sample, occupied, slots)) return;
        foreach (var slot in slots)
        {
            var s = cargo.Sizes[slot.Size].Shape;
            var half = new Vector3(s.X, s.Y, s.Z) * (cargo.UnitSize * .5f);
            var at = box.Center - box.Half + new Vector3(slot.At.X, slot.At.Y, slot.At.Z) * cargo.UnitSize + half;
            ContainerModel(new PartBox(at, half, BlockFacing.Back, box.CellSize), slot.Size, look, vertices, indices);
            var clamp = new List<MeshVertex>();
            var clampIndices = new List<uint>();
            ClampModel(s, box.Facing, look, cargo, clamp, clampIndices);
            foreach (var i in clampIndices) { var vertex = clamp[(int)i]; vertex.Position = new float3(vertex.Position.X + at.X, vertex.Position.Y + at.Y, vertex.Position.Z + at.Z); vertices.Add(vertex); indices.Add((uint)vertices.Count - 1); }
        }
    }

    private static bool AddRack(HullDesign design, int block, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        if (design.Types[block].Cargo <= 0 || design.Shapes[block] != BlockShape.Box || look.Rack is null) return false;
        design.MountsOf(block, out var first, out var end);
        var mounted = 0;
        for (var m = first; m < end; m++) mounted |= 1 << (int)design.Mounts[m].Face;
        BuildRack(new PartBox(design.BlockOffsets[block], design.BlockHalfExtents[block], RackDeck.Of(design, block), design.BlockSize),
            look.Rack, look.Cargo, vertices, indices, design.TouchedFaces[block], mounted);
        return true;
    }
}
