using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    /// <summary>True when blocks of <paramref name="type"/> in <paramref name="shape"/> are drawn as a drone bay.</summary>
    public static bool IsBay(BlockType type, BlockShape shape, PropulsionLook look) =>
        shape == BlockShape.Box && type.Bay is not null && look.Bay is not null;

    // A bay of a design. Unlike a tank it closes its own faces, so a neighbour that leaves out the face it covers never
    // shows through; the hatch is the block's facing, chosen when it was placed, and is always drawn.
    private static bool AddBay(HullDesign design, int block, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        if (!IsBay(design.Types[block], design.Shapes[block], look))
            return false;
        var box = new PartBox(design.BlockOffsets[block], design.BlockHalfExtents[block], design.Boxes[block].Facing, design.BlockSize > 0 ? design.BlockSize : 1f);
        BuildBay(look.Bay!, box, vertices, indices);
        return true;
    }

    /// <summary>
    /// Builds a drone bay filling <paramref name="box"/>: a painted body inset from five faces and square beams along
    /// the box's twelve edges, flush with it. The sixth face, the one <see cref="PartBox.Facing"/> names, is the hatch:
    /// a dark recess closed by a raised rim, two door leaves that lie a little under the rim with a seam between them
    /// (the seam runs across the longer side, the first axis on a tie), and a light on the rim. Every piece is a closed
    /// axis-aligned brick, so a model is a pure function of the box and the look; built where hull meshes are, on a
    /// worker, never in a frame.
    /// </summary>
    public static void BuildBay(BayLook look, in PartBox box, List<MeshVertex> vertices, List<uint> indices)
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
        var lift = look.LightLift * small;
        var recess = look.DoorRecess * small;
        var halfSeam = 0.5f * look.Seam * small;

        // Where the hatch's pieces stand along the hatch axis: the plate behind the doors, the doors and the rim.
        var plateTop = depth - look.RimDepth * small;
        var rimTop = depth - lift;
        var doorTop = depth - recess;
        var bodyTop = plateTop - inset;

        var beam = Tag(look.BeamShade);
        var rim = Tag(look.RimShade);
        var plate = Tag(look.PlateShade);
        var body = Paint(look.Body);
        var door = Paint(look.Door);
        var light = Emissive(RackLook.Rgb(look.Light));

        // A brick centred at (cu, cv, cd) in the hatch frame (cd along the facing), half its sides along u, v and the hatch axis.
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

        // The body, behind the plating's inset on five sides, and the dark plate that closes the recess on the hatch side.
        Brick(0f, 0f, 0.5f * (bodyTop - depth + inset), hu - inset, hv - inset, 0.5f * (bodyTop + depth - inset), body);
        Brick(0f, 0f, 0.5f * (plateTop + bodyTop), hu - inset, hv - inset, 0.5f * (plateTop - bodyTop), plate);

        // Twelve edge beams, flush with the box: four along the hatch axis own the corners, the rest stop against them.
        for (var k = 0; k < 4; k++)
        {
            var at = centre;
            at[u] += (k is 0 or 3 ? 1f : -1f) * (hu - hb);
            at[v] += (k is 0 or 1 ? 1f : -1f) * (hv - hb);
            Bar(vertices, indices, at, a, depth, hb, hb, beam);
        }
        for (var k = 0; k < 4; k++)
        {
            var end = k < 2 ? 1f : -1f;
            var side = k % 2 == 0 ? 1f : -1f;
            var alongU = centre;
            alongU[v] += side * (hv - hb);
            alongU[a] += sign * end * (depth - hb);
            Bar(vertices, indices, alongU, u, hu - 2f * hb, hb, hb, beam);
            var alongV = centre;
            alongV[u] += side * (hu - hb);
            alongV[a] += sign * end * (depth - hb);
            Bar(vertices, indices, alongV, v, hv - 2f * hb, hb, hb, beam);
        }

        // The rim: four bars round the opening, inside the beams, from the plate up to just under the cell's face.
        float ru = hu - 2f * hb, rv = hv - 2f * hb;
        float ou = ru - rimWidth, ov = rv - rimWidth;
        var rimMid = 0.5f * (plateTop + rimTop);
        var rimHalf = 0.5f * (rimTop - plateTop);
        for (var s = -1; s <= 1; s += 2)
        {
            Brick(s * (ru - 0.5f * rimWidth), 0f, rimMid, 0.5f * rimWidth, rv, rimHalf, rim);
            Brick(0f, s * (rv - 0.5f * rimWidth), rimMid, ou, 0.5f * rimWidth, rimHalf, rim);
        }

        // The doors: two leaves filling the opening, split across its longer side (the first axis on a tie).
        var doorMid = 0.5f * (plateTop + doorTop);
        var doorHalf = 0.5f * (doorTop - plateTop);
        var splitU = hu >= hv;
        var leaf = 0.5f * ((splitU ? ou : ov) - halfSeam);
        for (var s = -1; s <= 1; s += 2)
        {
            var offset = s * (halfSeam + leaf);
            if (splitU)
                Brick(offset, 0f, doorMid, leaf, ov, doorHalf, door);
            else
                Brick(0f, offset, doorMid, ou, leaf, doorHalf, door);
        }

        // The hatch light: a short strip on the rim's bar at the far end of v, standing its own lift above the rim and
        // sunk the same into it, so its top is the cell's face and never past it.
        Brick(0f, rv - 0.5f * rimWidth, depth - lift, 0.5f * look.LightLength * small, 0.5f * look.LightWidth * small, lift, light);
    }
}
