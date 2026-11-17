using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    /// <summary>The look of a mounted mining laser of <paramref name="type"/>, or null for a part that is none.</summary>
    public static LaserLook? LaserOf(BlockType type, PropulsionLook look) =>
        type.Mount is not null && type.Laser is not null ? look.Laser : null;

    /// <summary>
    /// Builds a mounted mining laser in the thin box it fills: a round base plate flush on the block face with its rim
    /// in the colour of the laser's tier, a housing with field coils, and a muzzle shroud round a recessed lens that
    /// gives off its own light. The lens is the outermost thing, level with the outer face, where the beam leaves
    /// (<see cref="LaserAim"/>). Every figure is a share of the box, so a laser on any block size is the same model. A
    /// pure function of the box, the look and the type, built on a worker.
    /// </summary>
    public static void BuildLaser(LaserLook look, BlockType type, in PartBox box, List<MeshVertex> vertices, List<uint> indices)
    {
        var axis = Facings.Axis(box.Facing);
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var g = new Geometry(vertices, indices, new Frame(box.Center, Facings.Vector(box.Facing), Unit(u), Unit(v), box.Half[axis]));
        var small = MathF.Min(box.Half[u], box.Half[v]);
        var depth = 2f * box.Half[axis];
        var sides = look.Sides;
        var turn = MathF.PI / sides;
        var plating = Tag(look.PlateShade);
        var metal = Tag(look.HousingShade);
        var detail = Tag(look.DetailShade);
        var dark = Tag(look.DarkShade);
        var coilPaint = PaintRgb(look.CoilColor);
        var lens = Emissive(look.LensColor);
        var tier = type.Laser is { } spec ? PaintRgb(ModuleLook.Current.Tier(spec.Quality).Rgb) : plating;

        Span<Vector2> plate = stackalloc Vector2[sides];
        Span<Vector2> rim = stackalloc Vector2[sides];
        Span<Vector2> housing = stackalloc Vector2[sides];
        Span<Vector2> coil = stackalloc Vector2[sides];
        Span<Vector2> muzzle = stackalloc Vector2[sides];
        Span<Vector2> mouth = stackalloc Vector2[sides];
        Span<Vector2> lensRing = stackalloc Vector2[sides];
        Polygon(plate, Vector2.Zero, small * look.PlateRadius, turn);
        Polygon(rim, Vector2.Zero, small * look.PlateRadius * look.RimShare, turn);
        Polygon(housing, Vector2.Zero, small * look.HousingRadius, turn);
        Polygon(coil, Vector2.Zero, small * look.CoilRadius, turn);
        Polygon(muzzle, Vector2.Zero, small * look.MuzzleRadius, turn);
        Polygon(mouth, Vector2.Zero, small * look.MuzzleRadius * 0.9f, turn);
        Polygon(lensRing, Vector2.Zero, small * look.LensRadius, turn);

        var plateTop = depth * look.PlateHeight;
        var muzzleStart = depth * look.MuzzleStart;
        var lensZ = depth * look.LensDepth;

        // The plate: its wall in plating, a chamfer in the tier's colour, then the deck the housing stands on.
        g.Loft(plate, 0f, plate, plateTop * 0.55f, Vector2.Zero, plating, 1f);
        g.Loft(plate, plateTop * 0.55f, rim, plateTop, Vector2.Zero, tier, 1f);
        g.Annulus(rim, housing, Vector2.Zero, plateTop, 1f, plating);

        // The housing, with the field coils wound round it.
        g.Loft(housing, plateTop, housing, muzzleStart, Vector2.Zero, metal, 1f);
        var span = muzzleStart - plateTop;
        var band = span / (look.Coils * 2f + 1f);
        for (var i = 0; i < look.Coils; i++)
        {
            var low = plateTop + band * (1f + 2f * i);
            var high = low + band;
            g.Annulus(coil, housing, Vector2.Zero, low, -1f, coilPaint);
            g.Loft(coil, low, coil, high, Vector2.Zero, coilPaint, 1f);
            g.Annulus(coil, housing, Vector2.Zero, high, 1f, coilPaint);
        }

        // The muzzle shroud flares from the housing to the outer face, its mouth a bright lip round the recessed lens.
        g.Annulus(muzzle, housing, Vector2.Zero, muzzleStart, -1f, metal);
        g.Loft(muzzle, muzzleStart, muzzle, depth, Vector2.Zero, plating, 1f);
        g.Annulus(muzzle, mouth, Vector2.Zero, depth, 1f, detail);
        g.Loft(mouth, lensZ, mouth, depth, Vector2.Zero, dark, -1f);
        g.Annulus(mouth, lensRing, Vector2.Zero, lensZ, 1f, dark);
        g.Cap(lensRing, Vector2.Zero, lensZ, 1f, lens);
    }
}
