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
    /// <summary>Size-stripe half-width as a share of container half-length.</summary>
    public float Stripe { get; init; }
    /// <summary>Empty container body RGB.</summary>
    public float[] Body { get; init; } = [];
    /// <summary>One RGB body paint per resource index.</summary>
    public float[][] Resources { get; init; } = [];
    /// <summary>One RGB stripe for each ascending size.</summary>
    public float[][] Stripes { get; init; } = [];

    /// <summary>Refuses proportions or colours outside model bounds.</summary>
    public void Check()
    {
        var room = .5f * Gap;
        if (!(Gap is > 0 and <= .2f) || !(Rib > 0) || Rib >= Gap || !(RibShade is >= 0 and <= 1) || !(Stripe is > 0 and <= .35f) || Stripes.Length != 8)
            throw new InvalidDataException("Invalid rack proportions.");
        foreach (var thin in new[] { Deck, Rail, Post, Jaw })
            if (!(thin > 0 && thin <= room)) throw new InvalidDataException("A rack part is thicker than the clearance a container keeps.");
        if (!(RailHeight > 0 && RailHeight <= 1) || !(PostHeight >= RailHeight && PostHeight <= 4) || !(JawWidth is > 0 and <= 1) || !(JawHeight is > 0 and <= 1)
            || !(LightSize > 0 && LightSize <= Rail) || LightEvery is < 1 or > 16
            || !(DeckShade is > 0 and <= 1) || !(RailShade is > 0 and <= 1) || !(PostShade is > 0 and <= 1) || !(JawShade is > 0 and <= 1))
            throw new InvalidDataException("Invalid rack proportions.");
        CheckPaint(Body);
        CheckPaint(Light);
        CheckPaint(ClampLight);
        foreach (var paint in Resources) CheckPaint(paint);
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

    /// <summary>Reusable ribbed container box with resource body paint and a size-coloured stripe, at a packed slot.</summary>
    public static void ContainerModel(in PartBox box, int size, RackLook look, List<MeshVertex> vertices, List<uint> indices) =>
        ContainerModel(box, size, -1, look, vertices, indices);

    /// <summary>Reusable ribbed container box with resource body paint and a size-coloured stripe, at a packed slot.</summary>
    public static void ContainerModel(in PartBox box, int size, int resource, RackLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var h = box.Half;
        var inset = MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * look.Gap;
        h -= new Vector3(inset);
        var body = (uint)resource < (uint)look.Resources.Length ? look.Resources[resource] : look.Body;
        Bar(vertices, indices, box.Center, 0, h.X, h.Y, h.Z, Paint(body));
        var rib = MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * look.Rib;
        var ribTag = Tag(look.RibShade);
        Bar(vertices, indices, box.Center + new Vector3(-h.X * .72f, 0, 0), 0, rib, h.Y + rib, h.Z + rib, ribTag);
        Bar(vertices, indices, box.Center + new Vector3(h.X * .72f, 0, 0), 0, rib, h.Y + rib, h.Z + rib, ribTag);
        var stripeHalf = Math.Clamp(h.X * look.Stripe, rib * 1.5f, h.X * .35f);
        var stripeLift = rib * .6f;
        Bar(vertices, indices, box.Center, 0, stripeHalf, h.Y + stripeLift, h.Z + stripeLift, Paint(look.Stripes[size]));
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
