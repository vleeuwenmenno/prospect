using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>Frame proportions and container paint, loaded before mesh workers start.</summary>
internal sealed class RackLook
{
    /// <summary>Beam half-width as a share of a cargo unit.</summary>
    public float Beam { get; init; }
    /// <summary>Unpainted frame brightness.</summary>
    public float Shade { get; init; }
    /// <summary>Container clearance as a share of its smallest half-side.</summary>
    public float Gap { get; init; }
    /// <summary>Rib half-width as a share of the inset body half-side.</summary>
    public float Rib { get; init; }
    /// <summary>Container body RGB.</summary>
    public float[] Body { get; init; } = [];
    /// <summary>One RGB stripe for each ascending size.</summary>
    public float[][] Stripes { get; init; } = [];

    /// <summary>Refuses proportions or colours outside model bounds.</summary>
    public void Check()
    {
        if (!(Beam is > 0 and <= .1f) || !(Shade is > 0 and <= 1) || !(Gap is > 0 and <= .2f)
            || !(Rib is > 0 and <= .05f) || Stripes.Length != 8)
            throw new InvalidDataException("Invalid rack proportions.");
        CheckPaint(Body);
        foreach (var stripe in Stripes) CheckPaint(stripe);
    }
    private static void CheckPaint(float[] paint)
    {
        if (paint.Length != 3 || paint.Any(c => !float.IsFinite(c) || c < 0 || c > 1))
            throw new InvalidDataException("Invalid cargo paint.");
    }
}

internal static partial class PropulsionModels
{
    /// <summary>Open edge frame with side rails at each cargo-unit layer. Built only on model workers.</summary>
    public static void BuildRack(in PartBox box, RackLook look, CargoCatalog cargo, List<MeshVertex> vertices, List<uint> indices, int closed = 0, int mounted = 0)
    {
        var h = box.Half;
        var beam = MathF.Min(cargo.UnitSize * look.Beam, MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * .2f);
        for (var axis = 0; axis < 3; axis++)
        {
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            for (var k = 0; k < 4; k++)
            {
                var at = box.Center;
                at[u] += (k < 2 ? -1 : 1) * (h[u] - beam);
                at[v] += (k % 2 == 0 ? -1 : 1) * (h[v] - beam);
                Bar(vertices, indices, at, axis, h[axis], beam, beam, Tag(look.Shade));
            }
        }
        // Neighbours omit their covered faces; close those faces from inside the rack.
        Span<Vector2> rect = stackalloc Vector2[4];
        for (var side = 0; side < 6; side++)
        {
            var bit = 1 << side;
            if (((closed | mounted) & bit) == 0) continue;
            var axis = side >> 1;
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            var normal = Unit(axis) * ((side & 1) == 0 ? 1f : -1f);
            var g = new Geometry(vertices, indices, new Frame(box.Center, normal, Unit(u), Unit(v), h[axis]));
            Rectangle(rect, h[u] - beam, h[v] - beam);
            g.Cap(rect, Vector2.Zero, 2 * h[axis] - .1f * beam, -1, Tag(look.Shade));
            if ((mounted & bit) != 0) g.Cap(rect, Vector2.Zero, 2 * h[axis], 1, Tag(look.Shade));
        }
        // Bound cosmetic detail even for extreme boxes; cargo slots remain exact.
        var layers = Math.Clamp((int)(2 * h.Y / cargo.UnitSize), 1, 128);
        for (var layer = 1; layer < layers; layer++)
            for (var side = -1; side <= 1; side += 2)
                Bar(vertices, indices, box.Center + new Vector3(0, -h.Y + 2 * h.Y * layer / layers, side * (h.Z - beam)),
                    0, h.X, beam, beam, Tag(look.Shade));
    }

    /// <summary>Reusable ribbed container box with a size-coloured stripe, at a packed slot.</summary>
    public static void ContainerModel(in PartBox box, int size, RackLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var h = box.Half;
        var inset = MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * look.Gap;
        h -= new Vector3(inset);
        Bar(vertices, indices, box.Center, 0, h.X, h.Y, h.Z, Paint(look.Body));
        var rib = MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * look.Rib;
        for (var k = -1; k <= 1; k++)
            Bar(vertices, indices, box.Center + new Vector3(k * h.X * .75f, 0, 0), 0, rib,
                h.Y + rib, h.Z + rib, k == 0 ? Paint(look.Stripes[size]) : Tag(look.Shade));
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
        }
    }

    private static bool AddRack(HullDesign design, int block, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        if (design.Types[block].Cargo <= 0 || design.Shapes[block] != BlockShape.Box || look.Rack is null) return false;
        design.MountsOf(block, out var first, out var end);
        var mounted = 0;
        for (var m = first; m < end; m++) mounted |= 1 << (int)design.Mounts[m].Face;
        BuildRack(new PartBox(design.BlockOffsets[block], design.BlockHalfExtents[block], design.Boxes[block].Facing, design.BlockSize),
            look.Rack, look.Cargo, vertices, indices, design.TouchedFaces[block], mounted);
        return true;
    }
}
