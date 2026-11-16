using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    /// <summary>The look of a mounted module scanner of <paramref name="type"/>, or null for a part that is none or has no entry.</summary>
    public static ScannerLook? ScannerOf(BlockType type, PropulsionLook look) =>
        type.Mount is not null && type.Module.Family != ModuleFamily.None && look.Scanners.TryGetValue(type.Id, out var scanner) ? scanner : null;

    /// <summary>
    /// Builds a mounted scanner in the thin box it fills: a round base plate flush on the block face, its rim in the
    /// colour of the scanner's tier, and a head on it by the look's shape. Every figure is a share of the box, so a
    /// scanner on any block size is the same model. A pure function of the box, the look and the type, built on a worker.
    /// </summary>
    public static void BuildScanner(ScannerLook look, BlockType type, in PartBox box, List<MeshVertex> vertices, List<uint> indices)
    {
        var axis = Facings.Axis(box.Facing);
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var g = new Geometry(vertices, indices, new Frame(box.Center, Facings.Vector(box.Facing), Unit(u), Unit(v), box.Half[axis]));
        var small = MathF.Min(box.Half[u], box.Half[v]);
        var depth = 2f * box.Half[axis];
        var sides = look.Sides;
        var turn = MathF.PI / sides;
        var plating = Tag(look.BaseShade);
        var metal = Tag(look.HeadShade);
        var detail = Tag(look.DetailShade);
        var dark = Tag(look.PortShade);
        var tier = PaintRgb(ModuleLook.Current.Tier(type.Module.Quality).Rgb);

        var baseR = small * look.BaseRadius;
        var baseH = depth * look.BaseHeight;
        Span<Vector2> outer = stackalloc Vector2[sides];
        Span<Vector2> rim = stackalloc Vector2[sides];
        Polygon(outer, Vector2.Zero, baseR, turn);
        Polygon(rim, Vector2.Zero, baseR * look.RimShare, turn);

        // The plate: its wall in plating, a chamfer in the tier's colour, then a flat deck. Its back is on the block face.
        g.Loft(outer, 0f, outer, baseH * 0.55f, Vector2.Zero, plating, 1f);
        g.Loft(outer, baseH * 0.55f, rim, baseH, Vector2.Zero, tier, 1f);
        var headR = small * look.HeadRadius;
        var top = depth * look.HeadHeight;
        switch (look.Shape)
        {
            case ScannerLook.Dish:
                BuildDish(ref g, look, rim, baseH, small, headR, top, depth, sides, turn, plating, metal, detail, dark);
                break;
            case ScannerLook.Array:
                BuildArray(ref g, look, rim, baseH, headR, top, small, sides, turn, plating, metal, detail);
                break;
            default:
                BuildDome(ref g, look, rim, baseH, headR, top, depth, sides, turn, metal, detail, dark);
                break;
        }
    }

    // A shallow dish on a short mast: the bowl is concave and faces out, with a feed horn standing in its middle.
    private static void BuildDish(ref Geometry g, ScannerLook look, ReadOnlySpan<Vector2> rim, float baseH, float small, float headR, float top, float depth, int sides, float turn,
        float plating, float metal, float detail, float dark)
    {
        var stemR = small * look.StemRadius;
        var bowl = depth * look.BowlHeight;
        var stemTop = baseH + (bowl - baseH) * 0.45f;
        Span<Vector2> stem = stackalloc Vector2[sides];
        Span<Vector2> back = stackalloc Vector2[sides];
        Span<Vector2> lip = stackalloc Vector2[sides];
        Span<Vector2> inner = stackalloc Vector2[sides];
        Span<Vector2> centre = stackalloc Vector2[sides];
        Polygon(stem, Vector2.Zero, stemR, turn);
        Polygon(back, Vector2.Zero, stemR * 1.5f, turn);
        Polygon(lip, Vector2.Zero, headR, turn);
        Polygon(inner, Vector2.Zero, headR * 0.9f, turn);
        Polygon(centre, Vector2.Zero, headR * 0.28f, turn);

        g.Annulus(rim, stem, Vector2.Zero, baseH, 1f, plating);
        g.Loft(stem, baseH, stem, stemTop, Vector2.Zero, detail, 1f);
        // The back of the bowl widens from the mast to the rim, the rim's edge closes it, the bowl dips inside.
        g.Loft(back, stemTop, lip, top - (top - bowl) * 0.12f, Vector2.Zero, metal, 1f);
        g.Loft(lip, top - (top - bowl) * 0.12f, lip, top, Vector2.Zero, detail, 1f);
        g.Annulus(lip, inner, Vector2.Zero, top, 1f, detail);
        g.Loft(inner, top, centre, bowl, Vector2.Zero, metal, -1f);
        g.Cap(centre, Vector2.Zero, bowl, 1f, dark);
        // The feed horn: a thin spike from the bowl's middle to just under the rim's height.
        Span<Vector2> horn = stackalloc Vector2[sides];
        Span<Vector2> tip = stackalloc Vector2[sides];
        Polygon(horn, Vector2.Zero, headR * 0.1f, turn);
        Polygon(tip, Vector2.Zero, headR * 0.05f, turn);
        var hornTop = bowl + (top - bowl) * 0.85f;
        g.Loft(horn, bowl, tip, hornTop, Vector2.Zero, detail, 1f);
        g.Cap(tip, Vector2.Zero, hornTop, 1f, detail);
    }

    // A ring on the plate carrying thin masts of rising height: a phased array's pickets, the tallest at the head's height.
    private static void BuildArray(ref Geometry g, ScannerLook look, ReadOnlySpan<Vector2> rim, float baseH, float headR, float top, float small, int sides, float turn,
        float plating, float metal, float detail)
    {
        var ringTop = baseH * 1.8f;
        Span<Vector2> ring = stackalloc Vector2[sides];
        Polygon(ring, Vector2.Zero, headR * 0.82f, turn);
        Span<Vector2> inner = stackalloc Vector2[sides];
        Polygon(inner, Vector2.Zero, headR * 0.6f, turn);
        g.Annulus(rim, ring, Vector2.Zero, baseH, 1f, plating);
        g.Loft(ring, baseH, ring, ringTop, Vector2.Zero, metal, 1f);
        g.Annulus(ring, inner, Vector2.Zero, ringTop, 1f, detail);
        g.Loft(inner, ringTop, inner, baseH * 1.25f, Vector2.Zero, plating, -1f);
        g.Cap(inner, Vector2.Zero, baseH * 1.25f, 1f, plating);

        // Pickets round the middle, each a little taller than the last, with a hub spike in the centre at full height.
        var count = look.Spikes;
        var radius = small * look.SpikeRadius;
        Span<Vector2> post = stackalloc Vector2[6];
        Span<Vector2> postTip = stackalloc Vector2[6];
        for (var i = 0; i <= count; i++)
        {
            var angle = i * (MathF.Tau / count);
            var at = i == count ? Vector2.Zero : new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (headR * 0.4f);
            var height = i == count ? top : ringTop + (top - ringTop) * (0.45f + 0.4f * (i + 1f) / count);
            var thick = i == count ? radius * 1.35f : radius;
            Polygon(post, at, thick, angle);
            Polygon(postTip, at, thick * 0.55f, angle);
            g.Loft(post, baseH * 1.25f, postTip, height, at, detail, 1f);
            g.Cap(postTip, at, height, 1f, metal);
        }

    }

    // A domed sensor head: a bevelled shoulder, a flat top with a sunken dark intake in it.
    private static void BuildDome(ref Geometry g, ScannerLook look, ReadOnlySpan<Vector2> rim, float baseH, float headR, float top, float depth, int sides, float turn,
        float metal, float detail, float dark)
    {
        var shoulderH = baseH + (top - baseH) * 0.55f;
        Span<Vector2> foot = stackalloc Vector2[sides];
        Span<Vector2> shoulder = stackalloc Vector2[sides];
        Span<Vector2> crown = stackalloc Vector2[sides];
        Span<Vector2> port = stackalloc Vector2[sides];
        Polygon(foot, Vector2.Zero, headR, turn);
        Polygon(shoulder, Vector2.Zero, headR * 0.96f, turn);
        Polygon(crown, Vector2.Zero, headR * 0.6f, turn);
        Polygon(port, Vector2.Zero, headR * 0.4f, turn);
        var bowl = depth * look.BowlHeight;
        g.Annulus(rim, foot, Vector2.Zero, baseH, 1f, detail);
        g.Loft(foot, baseH, shoulder, shoulderH, Vector2.Zero, metal, 1f);
        g.Loft(shoulder, shoulderH, crown, top, Vector2.Zero, metal, 1f);
        g.Annulus(crown, port, Vector2.Zero, top, 1f, detail);
        g.Loft(port, top, port, bowl, Vector2.Zero, dark, -1f);
        g.Cap(port, Vector2.Zero, bowl, 1f, dark);
    }
}
