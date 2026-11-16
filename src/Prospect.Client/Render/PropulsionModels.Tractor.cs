using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    /// <summary>Total height from the block face to the trunnion pivot of a tractor turret of half-width <paramref name="small"/>.</summary>
    public static float TractorPivotOffset(float small, TractorLook tractor)
    {
        var baseR = small * tractor.TurretBaseRadius;
        var baseH = baseR * tractor.TurretBaseHeight;
        var collarTop = baseH * 1.45f;
        return collarTop + baseR * tractor.TurretYokeHeight;
    }

    /// <summary>Height from the block face to the top of the bearing collar where the yoke sits.</summary>
    public static float TractorCollarOffset(float small, TractorLook tractor)
    {
        var baseR = small * tractor.TurretBaseRadius;
        return baseR * tractor.TurretBaseHeight * 1.45f;
    }

    /// <summary>Distance from the trunnion pivot along the aimed barrel axis to the recessed glowing lens.</summary>
    public static float TractorLensOffset(float small, TractorLook tractor)
    {
        var baseR = small * tractor.TurretBaseRadius;
        return baseR * tractor.TurretHeadLength * 0.64f;
    }

    /// <summary>
    /// Builds the round bolted base plate and bearing collar flush on the mount's block face (static hull mesh). Its
    /// chamfered rim is painted in the colour of the tractor's tier (<c>modules.json</c>, as on the editor card).
    /// </summary>
    public static void BuildTractorBase(PropulsionModelLook model, in PartBox box, TractorLook tractor, TractorSpec spec, List<MeshVertex> vertices, List<uint> indices)
    {
        var axis = Facings.Axis(box.Facing);
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var g = new Geometry(vertices, indices, new Frame(box.Center, Facings.Vector(box.Facing), Unit(u), Unit(v), box.Half[axis]));

        var small = MathF.Min(box.Half[u], box.Half[v]);
        var baseR = small * tractor.TurretBaseRadius;
        var baseH = baseR * tractor.TurretBaseHeight;
        var collarTop = baseH * 1.45f;

        var plating = Tag(model.HousingShade);
        var metal = Tag(model.NozzleShade);
        var detail = Tag(model.DetailShade);
        var tier = PaintRgb(ModuleLook.Current.Tier(spec.Quality).Rgb);

        const int sides = 24;
        var turn = MathF.PI / sides;
        Span<Vector2> outer = stackalloc Vector2[sides];
        Span<Vector2> rim = stackalloc Vector2[sides];
        Span<Vector2> top = stackalloc Vector2[sides];
        Span<Vector2> collar = stackalloc Vector2[sides];
        Span<Vector2> collarLip = stackalloc Vector2[sides];
        Polygon(outer, Vector2.Zero, baseR, turn);
        Polygon(rim, Vector2.Zero, baseR * 0.86f, turn);
        Polygon(top, Vector2.Zero, baseR * 0.62f, turn);
        Polygon(collar, Vector2.Zero, baseR * 0.5f, turn);
        Polygon(collarLip, Vector2.Zero, baseR * 0.56f, turn);

        // Round plate flush on the block face, a chamfered rim in the tier's colour, and a raised inner deck.
        g.Loft(outer, 0f, outer, baseH * 0.5f, Vector2.Zero, plating, 1f);
        g.Loft(outer, baseH * 0.5f, rim, baseH * 0.85f, Vector2.Zero, tier, 1f);
        g.Annulus(rim, top, Vector2.Zero, baseH * 0.85f, 1f, plating);
        g.Loft(top, baseH * 0.85f, top, baseH, Vector2.Zero, metal, 1f);
        g.Annulus(top, collarLip, Vector2.Zero, baseH, 1f, plating);

        // The bearing collar the yoke turns on.
        g.Loft(collarLip, baseH, collarLip, baseH * 1.12f, Vector2.Zero, detail, 1f);
        g.Annulus(collarLip, collar, Vector2.Zero, baseH * 1.12f, 1f, detail);
        g.Loft(collar, baseH * 1.12f, collar, collarTop, Vector2.Zero, metal, 1f);
        g.Cap(collar, Vector2.Zero, collarTop, 1f, detail);

        // Hex bolts on the rim's deck.
        var boltR = baseR * 0.74f;
        var boltRadius = baseR * 0.045f;
        var boltTop = baseH * 1.15f;
        Span<Vector2> bolt = stackalloc Vector2[6];
        for (var i = 0; i < tractor.TurretBolts; i++)
        {
            var angle = (i + 0.5f) * (MathF.Tau / tractor.TurretBolts);
            var centre = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * boltR;
            Polygon(bolt, centre, boltRadius, angle);
            g.Loft(bolt, baseH * 0.85f, bolt, boltTop, centre, detail, 1f);
            g.Cap(bolt, centre, boltTop, 1f, detail);
        }
    }

    /// <summary>A colour as the paint tag <c>mesh.vert</c> reads (see <see cref="Paint"/>).</summary>
    private static float PaintRgb(uint rgb)
    {
        Span<float> c = [((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f];
        return Paint(c);
    }

    /// <summary>Builds the azimuth-rotating yoke in canonical space (origin at collar top, +Z face normal, +X pitch axis).</summary>
    public static void BuildTractorYoke(float small, PropulsionModelLook model, TractorLook tractor, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var baseR = small * tractor.TurretBaseRadius;
        var yokeH = baseR * tractor.TurretYokeHeight;
        var spread = baseR * tractor.TurretYokeSpread;

        var plating = Tag(model.HousingShade);
        var metal = Tag(model.NozzleShade);
        var detail = Tag(model.DetailShade);

        // Turntable disc.
        const int sides = 16;
        Span<Vector2> disc = stackalloc Vector2[sides];
        Polygon(disc, Vector2.Zero, baseR * 0.52f, MathF.PI / sides);
        g.Loft(disc, 0f, disc, baseR * 0.08f, Vector2.Zero, metal, 1f);
        g.Cap(disc, Vector2.Zero, baseR * 0.08f, 1f, detail);
        g.Cap(disc, Vector2.Zero, 0f, -1f, detail);

        // Left and right yoke arms rising to the trunnion pivot at (0, 0, yokeH).
        var armW = baseR * 0.13f;
        var armD0 = baseR * 0.24f;
        var armD1 = baseR * 0.16f;
        var armTop = yokeH + baseR * 0.12f;
        Span<Vector2> foot = stackalloc Vector2[4];
        Span<Vector2> tip = stackalloc Vector2[4];
        Span<Vector2> hub = stackalloc Vector2[8];
        for (var side = -1; side <= 1; side += 2)
        {
            var x0 = side * (spread * 0.82f);
            var x1 = side * spread;
            Rectangle(foot, armW * 0.65f, armD0);
            Rectangle(tip, armW * 0.55f, armD1);
            for (var c = 0; c < 4; c++)
            {
                foot[c].X += x0;
                tip[c].X += x1;
            }
            var mid = new Vector2((x0 + x1) * 0.5f, 0f);
            g.Loft(foot, baseR * 0.04f, tip, armTop, mid, plating, 1f);
            g.Cap(tip, new Vector2(x1, 0f), armTop, 1f, detail);
            g.Cap(foot, new Vector2(x0, 0f), baseR * 0.04f, -1f, detail);

            // Trunnion hub cap on the outside of each yoke arm.
            var hubX = side * (spread + armW * 0.55f);
            var hubCenter = new Vector2(hubX, 0f);
            Polygon(hub, hubCenter, baseR * 0.11f, MathF.PI / 8f);
            g.Loft(hub, yokeH - baseR * 0.08f, hub, yokeH + baseR * 0.08f, hubCenter, detail, 1f);
            g.Cap(hub, hubCenter, yokeH + baseR * 0.08f, 1f, detail);
            g.Cap(hub, hubCenter, yokeH - baseR * 0.08f, -1f, detail);
        }
    }

    /// <summary>Builds the pitching emitter head in canonical space (origin at trunnion pivot, +Z aim axis).</summary>
    public static void BuildTractorHead(float small, PropulsionModelLook model, TractorLook tractor, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var baseR = small * tractor.TurretBaseRadius;
        var headLen = baseR * tractor.TurretHeadLength;
        var headR = baseR * tractor.TurretHeadRadius;

        var plating = Tag(model.HousingShade);
        var metal = Tag(model.NozzleShade);
        var dark = Tag(model.ThroatShade);
        var detail = Tag(model.DetailShade);
        var coilPaint = PaintRgb(tractor.TurretCoilColor);
        var lens = Emissive(tractor.TurretLensColor);

        const int sides = 16;
        var turn = MathF.PI / sides;
        Span<Vector2> rear = stackalloc Vector2[sides];
        Span<Vector2> breech = stackalloc Vector2[sides];
        Span<Vector2> barrel = stackalloc Vector2[sides];
        Span<Vector2> coil = stackalloc Vector2[sides];
        Span<Vector2> shroudBack = stackalloc Vector2[sides];
        Span<Vector2> shroudOut = stackalloc Vector2[sides];
        Span<Vector2> shroudIn = stackalloc Vector2[sides];
        Span<Vector2> lensRing = stackalloc Vector2[sides];
        Polygon(rear, Vector2.Zero, headR * 0.86f, turn);
        Polygon(breech, Vector2.Zero, headR * 1.2f, turn);
        Polygon(barrel, Vector2.Zero, headR * 0.9f, turn);
        Polygon(coil, Vector2.Zero, headR * 1.16f, turn);
        Polygon(shroudBack, Vector2.Zero, headR * 1.08f, turn);
        Polygon(shroudOut, Vector2.Zero, headR * 1.26f, turn);
        Polygon(shroudIn, Vector2.Zero, headR * 0.92f, turn);
        Polygon(lensRing, Vector2.Zero, headR * 0.7f, turn);

        var zBack = -0.28f * headLen;
        var zBreech = 0.12f * headLen;
        var zShroud = 0.5f * headLen;
        var zFlare = 0.58f * headLen;
        var zLens = TractorLensOffset(small, tractor);
        var zTip = 0.74f * headLen;

        // Breech housing about the trunnion: a rounded back, then a short step down to the barrel.
        g.Cap(rear, Vector2.Zero, zBack, -1f, dark);
        g.Loft(rear, zBack, breech, zBack + 0.07f * headLen, Vector2.Zero, plating, 1f);
        g.Loft(breech, zBack + 0.07f * headLen, breech, zBreech, Vector2.Zero, plating, 1f);
        g.Annulus(breech, barrel, Vector2.Zero, zBreech, 1f, metal);

        // Barrel with the field coils wound round it.
        g.Loft(barrel, zBreech, barrel, zShroud, Vector2.Zero, metal, 1f);
        var coilSpan = zShroud - zBreech;
        var coilWidth = coilSpan / (tractor.TurretCoilCount * 2f + 1f);
        for (var i = 0; i < tractor.TurretCoilCount; i++)
        {
            var za = zBreech + coilWidth * (1f + 2f * i);
            var zb = za + coilWidth;
            g.Annulus(coil, barrel, Vector2.Zero, za, -1f, coilPaint);
            g.Loft(coil, za, coil, zb, Vector2.Zero, coilPaint, 1f);
            g.Annulus(coil, barrel, Vector2.Zero, zb, 1f, coilPaint);
        }

        // The emitter shroud flares out round a lens set back from its mouth.
        g.Annulus(shroudBack, barrel, Vector2.Zero, zShroud, -1f, metal);
        g.Loft(shroudBack, zShroud, shroudOut, zFlare, Vector2.Zero, plating, 1f);
        g.Loft(shroudOut, zFlare, shroudOut, zTip, Vector2.Zero, plating, 1f);
        g.Annulus(shroudOut, shroudIn, Vector2.Zero, zTip, 1f, detail);
        g.Loft(shroudIn, zLens, shroudIn, zTip, Vector2.Zero, dark, -1f);
        g.Annulus(shroudIn, lensRing, Vector2.Zero, zLens, 1f, dark);
        g.Cap(lensRing, Vector2.Zero, zLens, 1f, lens);
    }
}
