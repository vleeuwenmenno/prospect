using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    /// <summary>True when blocks of <paramref name="type"/> in <paramref name="shape"/> are drawn as an ore hopper.</summary>
    public static bool IsHopper(BlockType type, BlockShape shape, PropulsionLook look) =>
        shape == BlockShape.Box && type.Hopper && look.Hopper is not null;

    /// <summary>True when blocks of <paramref name="type"/> in <paramref name="shape"/> are drawn as an ore bin.</summary>
    public static bool IsBin(BlockType type, BlockShape shape, PropulsionLook look) =>
        shape == BlockShape.Box && type.OreCapacity > 0 && look.Bin is not null;

    // A hopper or a bin of a design. Like a bay each closes its own faces, so a neighbour that leaves out the face it
    // covers never shows through; the hopper's intake is the block's facing, chosen when it was placed.
    private static bool AddOre(HullDesign design, int block, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var type = design.Types[block];
        var hopper = IsHopper(type, design.Shapes[block], look);
        if (!hopper && !IsBin(type, design.Shapes[block], look))
            return false;
        var box = new PartBox(design.BlockOffsets[block], design.BlockHalfExtents[block], design.Boxes[block].Facing, design.BlockSize > 0 ? design.BlockSize : 1f);
        if (hopper) BuildHopper(look.Hopper!, box, vertices, indices);
        else BuildBin(look.Bin!, box, vertices, indices);
        return true;
    }

    // Twelve square beams along the box's edges, flush with it: the four along axis a own the corners, the rest stop
    // against them. hb is half a beam's thickness.
    private static void EdgeBeams(List<MeshVertex> vertices, List<uint> indices, Vector3 centre, Vector3 half, int a, float hb, float tag)
    {
        var u = (a + 1) % 3;
        var v = (a + 2) % 3;
        for (var k = 0; k < 4; k++)
        {
            var at = centre;
            at[u] += (k is 0 or 3 ? 1f : -1f) * (half[u] - hb);
            at[v] += (k is 0 or 1 ? 1f : -1f) * (half[v] - hb);
            Bar(vertices, indices, at, a, half[a], hb, hb, tag);
        }

        for (var k = 0; k < 4; k++)
        {
            var end = k < 2 ? 1f : -1f;
            var side = k % 2 == 0 ? 1f : -1f;
            var alongU = centre;
            alongU[v] += side * (half[v] - hb);
            alongU[a] += end * (half[a] - hb);
            Bar(vertices, indices, alongU, u, half[u] - 2f * hb, hb, hb, tag);
            var alongV = centre;
            alongV[u] += side * (half[u] - hb);
            alongV[a] += end * (half[a] - hb);
            Bar(vertices, indices, alongV, v, half[v] - 2f * hb, hb, hb, tag);
        }
    }

    /// <summary>
    /// Builds an ore hopper filling <paramref name="box"/>: a painted body inset from five faces and square beams
    /// along the box's twelve edges, flush with it. The sixth face, the one <see cref="PartBox.Facing"/> names, is the
    /// intake: a rim round a pit whose dark floor lies well under it, a lower step inside the rim so the pit reads as
    /// a funnel, grate bars across the opening (along its shorter side, so they are short and stiff) and a light on
    /// the rim. Every piece is a closed axis-aligned brick, so a model is a pure function of the box and the look;
    /// built where hull meshes are, on a worker, never in a frame.
    /// </summary>
    public static void BuildHopper(HopperLook look, in PartBox box, List<MeshVertex> vertices, List<uint> indices)
    {
        var half = box.Half;
        var centre = box.Center;
        var a = Facings.Axis(box.Facing);
        var sign = Facings.Vector(box.Facing)[a] > 0 ? 1f : -1f;
        var u = (a + 1) % 3;
        var v = (a + 2) % 3;
        var small = MathF.Min(half.X, MathF.Min(half.Y, half.Z));
        var depth = half[a];
        var hu = half[u];
        var hv = half[v];

        var inset = look.Inset * small;
        var hb = look.Beam * small;
        var rimWidth = look.RimWidth * small;
        var stepWidth = look.StepWidth * small;
        var lift = look.LightLift * small;

        // Along the intake axis: the rim's top just under the cell's face, the pit's floor well under that (never
        // deeper than the block), the step half way between them.
        var rimTop = depth - lift;
        var floorTop = rimTop - MathF.Min(look.PitDepth * small, 2f * depth - inset - lift - 0.02f * small);
        var stepTop = 0.5f * (rimTop + floorTop);
        var bodyTop = floorTop - 0.02f * small;

        var beam = Tag(look.BeamShade);
        var rim = Tag(look.RimShade);
        var step = Tag(look.StepShade);
        var bar = Tag(look.BarShade);
        var pit = Tag(look.PitShade);
        var body = Paint(look.Body);
        var light = Emissive(RackLook.Rgb(look.Light));

        // A brick centred at (cu, cv, cd) in the intake frame (cd along the facing), half its sides along u, v and the intake axis.
        void Brick(float cu, float cv, float cd, float eu, float ev, float ed, float tag)
        {
            var at = centre;
            at[u] += cu;
            at[v] += cv;
            at[a] += sign * cd;
            var e = Vector3.Zero;
            e[u] = eu;
            e[v] = ev;
            e[a] = ed;
            Slab(vertices, indices, at, e, tag);
        }

        // The body, behind the plating's inset on five sides, up to the pit's floor, and the dark floor on top of it.
        Brick(0f, 0f, 0.5f * (bodyTop - depth + inset), hu - inset, hv - inset, 0.5f * (bodyTop + depth - inset), body);
        Brick(0f, 0f, 0.5f * (floorTop + bodyTop), hu - inset, hv - inset, 0.5f * (floorTop - bodyTop), pit);
        EdgeBeams(vertices, indices, centre, half, a, hb, beam);

        // The rings round the pit, inside the edge beams: each is four bricks standing on the floor.
        float ru = hu - 2f * hb, rv = hv - 2f * hb;
        float ou = ru - rimWidth, ov = rv - rimWidth;
        float iu = ou - stepWidth, iv = ov - stepWidth;
        void Ring(float outerU, float outerV, float innerU, float innerV, float low, float high, float tag)
        {
            var mid = 0.5f * (low + high);
            var e = 0.5f * (high - low);
            if (e <= 0f || innerU <= 0f || innerV <= 0f) return;
            for (var s = -1; s <= 1; s += 2)
            {
                Brick(s * 0.5f * (outerU + innerU), 0f, mid, 0.5f * (outerU - innerU), outerV, e, tag);
                Brick(0f, s * 0.5f * (outerV + innerV), mid, innerU, 0.5f * (outerV - innerV), e, tag);
            }
        }

        // The rim round the opening from the floor up to just under the cell's face, and the lower step inside it.
        Ring(ru, rv, ou, ov, floorTop, rimTop, rim);
        Ring(ou, ov, iu, iv, floorTop, stepTop, step);

        // Grate bars across the opening's shorter side, spread evenly along the longer, their tops level with the rim.
        var barHalf = 0.5f * look.BarWidth * small;
        var alongU = hu >= hv;
        var span = alongU ? ou : ov;
        for (var i = 0; i < look.Bars; i++)
        {
            var at = span * (2f * (i + 1) / (look.Bars + 1) - 1f);
            if (alongU)
                Brick(at, 0f, rimTop - barHalf, barHalf, ov, barHalf, bar);
            else
                Brick(0f, at, rimTop - barHalf, ou, barHalf, barHalf, bar);
        }

        // The intake light: a short strip on the rim's bar at the far end of v, filling the lift above the rim.
        Brick(0f, rv - 0.5f * rimWidth, depth - 0.5f * lift, 0.5f * look.LightLength * small, 0.5f * look.LightWidth * small, 0.5f * lift, light);
    }

    /// <summary>
    /// Builds an ore bin filling <paramref name="box"/>: a painted body inset from all six faces, square beams along
    /// the box's twelve edges, flush with it, and a painted band round the body's middle, across its longest side
    /// (the first axis on a tie). A pure function of the box and the look, built on a worker.
    /// </summary>
    public static void BuildBin(BinLook look, in PartBox box, List<MeshVertex> vertices, List<uint> indices)
    {
        var half = box.Half;
        var centre = box.Center;
        var small = MathF.Min(half.X, MathF.Min(half.Y, half.Z));
        var inset = look.Inset * small;
        var hb = look.Beam * small;
        var a = half.X >= half.Y && half.X >= half.Z ? 0 : half.Y >= half.Z ? 1 : 2;
        Slab(vertices, indices, centre, half - new Vector3(inset), Paint(look.Body));
        EdgeBeams(vertices, indices, centre, half, a, hb, Tag(look.BeamShade));

        // The band stands a little off the plating and stays behind the cell's faces.
        var band = half - new Vector3(inset - look.BandLift * small);
        band[a] = MathF.Min(look.BandWidth * small, half[a] - 2f * hb);
        Slab(vertices, indices, centre, band, Paint(look.Band));
    }
}
