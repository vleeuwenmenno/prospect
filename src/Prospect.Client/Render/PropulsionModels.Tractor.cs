using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    // Shares of the base ring's height and of the head's length that the three turret parts must agree on.
    private const float TurretCollarTop = 1.45f;
    private const float TurretHeadBack = 0.4f;
    private const float TurretHeadFront = 0.6f;
    private const float TurretLensRecess = 0.07f;
    private const float TurretBarLift = 0.015f;
    private const float TurretSlotGlow = 0.55f;

    /// <summary>Total height from the block face to the trunnion pivot of a tractor turret of half-width <paramref name="small"/>.</summary>
    public static float TractorPivotOffset(float small, TractorLook tractor) =>
        TractorCollarOffset(small, tractor) + small * tractor.TurretBaseRadius * tractor.TurretYokeHeight;

    /// <summary>Height from the block face to the top of the bearing collar where the yoke sits.</summary>
    public static float TractorCollarOffset(float small, TractorLook tractor) =>
        small * tractor.TurretBaseRadius * tractor.TurretBaseHeight * TurretCollarTop;

    /// <summary>Distance from the trunnion pivot along the aimed axis to the glowing bars of the emitter face.</summary>
    public static float TractorLensOffset(float small, TractorLook tractor) =>
        small * tractor.TurretBaseRadius * tractor.TurretHeadLength * (TurretHeadFront - TurretLensRecess + TurretBarLift);

    /// <summary>
    /// Builds the turret's base flush on the mount's block face (static hull mesh): a chamfered octagonal ring with a
    /// glow slot in the wall of each flat that looks along the face and an accent panel on each corner between them,
    /// a plinth, and the bearing collar the yoke turns on, banded in the colour of the tractor's tier
    /// (<c>modules.json</c>, as on the editor card). Every length is a share of the mount's half width.
    /// </summary>
    public static void BuildTractorBase(in PartBox box, TractorLook tractor, TractorSpec spec, List<MeshVertex> vertices, List<uint> indices)
    {
        var axis = Facings.Axis(box.Facing);
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var g = new Geometry(vertices, indices, new Frame(box.Center, Facings.Vector(box.Facing), Unit(u), Unit(v), box.Half[axis]));

        var small = MathF.Min(box.Half[u], box.Half[v]);
        var r = small * tractor.TurretBaseRadius;
        var h = r * tractor.TurretBaseHeight;
        var body = PaintRgb(tractor.TurretBodyColor);
        var dark = PaintRgb(tractor.TurretDarkColor);
        var accent = PaintRgb(tractor.TurretAccentColor);
        // The slots glow lower than the emitter: the same colour at a share of its light.
        var lens = tractor.TurretLensColor;
        var glow = Emissive(((uint)(((lens >> 16) & 0xFF) * TurretSlotGlow) << 16) | ((uint)(((lens >> 8) & 0xFF) * TurretSlotGlow) << 8) | (uint)((lens & 0xFF) * TurretSlotGlow));
        var tier = PaintRgb(ModuleLook.Current.Tier(spec.Quality).Rgb);

        // Flats on the face's own axes: a corner every eighth of a turn, starting a sixteenth round.
        const float turn = MathF.PI / 8f;
        Span<Vector2> outer = stackalloc Vector2[8];
        Span<Vector2> rim = stackalloc Vector2[8];
        Span<Vector2> plinth = stackalloc Vector2[8];
        Span<Vector2> collar = stackalloc Vector2[8];
        Polygon(outer, Vector2.Zero, r, turn);
        Polygon(rim, Vector2.Zero, r * 0.84f, turn);
        Polygon(plinth, Vector2.Zero, r * 0.66f, turn);
        Polygon(collar, Vector2.Zero, r * 0.5f, turn);

        var wall = h * 0.55f;
        var plinthTop = h * 1.25f;
        var collarTop = h * TurretCollarTop;
        g.Loft(outer, 0f, outer, wall, Vector2.Zero, dark, 1f);
        g.Loft(outer, wall, rim, h, Vector2.Zero, body, 1f);
        g.Annulus(rim, plinth, Vector2.Zero, h, 1f, dark);
        g.Loft(plinth, h, plinth, plinthTop, Vector2.Zero, body, 1f);
        g.Annulus(plinth, collar, Vector2.Zero, plinthTop, 1f, body);
        g.Loft(collar, plinthTop, collar, collarTop, Vector2.Zero, tier, 1f);
        g.Cap(collar, Vector2.Zero, collarTop, 1f, dark);

        // Face k runs from corner k to the next: the odd ones are the flats, the even ones the corners between.
        var inside = new Vector3(0f, 0f, wall * 0.5f);
        var lift = r * 0.012f;
        var slots = 0;
        for (var k = 0; k < 8; k++)
        {
            var a = outer[k];
            var b = outer[(k + 1) % 8];
            if ((k & 1) != 0)
            {
                if (slots++ < tractor.TurretSlots)
                    g.Panel(new Vector3(a, 0f), new Vector3(b, 0f), new Vector3(b, wall), new Vector3(a, wall), 0.22f, 0.78f, 0.38f, 0.7f, inside, lift, glow);
                continue;
            }
            g.Panel(new Vector3(a, 0f), new Vector3(b, 0f), new Vector3(b, wall), new Vector3(a, wall), 0.06f, 0.94f, 0.3f, 1f, inside, lift, accent);
            g.Panel(new Vector3(a, wall), new Vector3(b, wall), new Vector3(rim[(k + 1) % 8], h), new Vector3(rim[k], h), 0.06f, 0.94f, 0f, 0.92f, inside, lift, accent);
        }
    }

    /// <summary>A colour as the paint tag <c>mesh.vert</c> reads (see <see cref="Paint"/>).</summary>
    private static float PaintRgb(uint rgb)
    {
        Span<float> c = [((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f];
        return Paint(c);
    }

    /// <summary>
    /// Builds the yoke that turns about the face normal, in its own space (origin at the collar's top, +Z the face
    /// normal, +X the pitch axis): a turntable, two arms rising to the trunnion with a hub cap outside each, and a
    /// brace between them behind the head.
    /// </summary>
    public static void BuildTractorYoke(float small, TractorLook tractor, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var r = small * tractor.TurretBaseRadius;
        var yokeH = r * tractor.TurretYokeHeight;
        var spread = r * tractor.TurretYokeSpread;
        var body = PaintRgb(tractor.TurretBodyColor);
        var dark = PaintRgb(tractor.TurretDarkColor);

        Span<Vector2> table = stackalloc Vector2[8];
        Span<Vector2> tableTop = stackalloc Vector2[8];
        Polygon(table, Vector2.Zero, r * 0.78f, MathF.PI / 8f);
        Polygon(tableTop, Vector2.Zero, r * 0.72f, MathF.PI / 8f);
        var tableH = r * 0.1f;
        g.Cap(table, Vector2.Zero, 0f, -1f, dark);
        g.Loft(table, 0f, table, tableH * 0.6f, Vector2.Zero, dark, 1f);
        g.Loft(table, tableH * 0.6f, tableTop, tableH, Vector2.Zero, body, 1f);
        g.Cap(tableTop, Vector2.Zero, tableH, 1f, body);

        // The arms stand straight on the inside, so the head swings between them, and lean back from a deep foot.
        var arm = r * 0.085f;
        Span<Vector2> foot = stackalloc Vector2[4];
        Span<Vector2> neck = stackalloc Vector2[4];
        Span<Vector2> tip = stackalloc Vector2[4];
        Span<Vector2> hub = stackalloc Vector2[8];
        Span<Vector2> hubTop = stackalloc Vector2[8];
        Polygon(hub, Vector2.Zero, r * 0.19f, MathF.PI / 8f);
        Polygon(hubTop, Vector2.Zero, r * 0.13f, MathF.PI / 8f);
        for (var side = -1; side <= 1; side += 2)
        {
            var footAt = new Vector2(side * (spread + arm * 0.25f), -r * 0.1f);
            var neckAt = new Vector2(side * spread, 0f);
            Rectangle(foot, arm * 1.25f, r * 0.27f);
            Rectangle(neck, arm, r * 0.2f);
            Rectangle(tip, arm, r * 0.1f);
            for (var c = 0; c < 4; c++)
            {
                foot[c] += footAt;
                neck[c] += neckAt;
                tip[c] += neckAt;
            }
            var neckZ = yokeH - r * 0.04f;
            var tipZ = yokeH + r * 0.22f;
            g.Loft(foot, tableH * 0.5f, neck, neckZ, 0.5f * (footAt + neckAt), body, 1f);
            g.Loft(neck, neckZ, tip, tipZ, neckAt, body, 1f);
            g.Cap(tip, neckAt, tipZ, 1f, dark);

            // The trunnion's hub cap on the outside of the arm, its axis the pitch axis.
            var cap = new Geometry(vertices, indices, new Frame(new Vector3(side * (spread + arm), 0f, yokeH), Vector3.UnitX * side, Vector3.UnitY, Vector3.UnitZ, 0f));
            cap.Loft(hub, 0f, hub, r * 0.05f, Vector2.Zero, dark, 1f);
            cap.Loft(hub, r * 0.05f, hubTop, r * 0.085f, Vector2.Zero, dark, 1f);
            cap.Cap(hubTop, Vector2.Zero, r * 0.085f, 1f, body);
        }

        Bar(vertices, indices, new Vector3(0f, -r * 0.29f, r * 0.17f), 0, spread, r * 0.06f, r * 0.09f, dark);
    }

    /// <summary>
    /// Builds the pitching emitter head in its own space (origin at the trunnion, +Z the way it aims, -Y its top when
    /// it looks along the hull): a chamfered box tapering to its back, an accent band behind a dark bezel, and inside
    /// the bezel the recessed emitter face. The glowing bars on that face are <see cref="BuildTractorBars"/>.
    /// </summary>
    public static void BuildTractorHead(float small, TractorLook tractor, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var r = small * tractor.TurretBaseRadius;
        var length = r * tractor.TurretHeadLength;
        var hw = r * tractor.TurretHeadWidth;
        var hh = r * tractor.TurretHeadHeight;
        var cut = 0.32f * hh;
        var body = PaintRgb(tractor.TurretBodyColor);
        var dark = PaintRgb(tractor.TurretDarkColor);
        var accent = PaintRgb(tractor.TurretAccentColor);

        Span<Vector2> rear = stackalloc Vector2[8];
        Span<Vector2> full = stackalloc Vector2[8];
        Span<Vector2> bezel = stackalloc Vector2[8];
        Span<Vector2> mouth = stackalloc Vector2[8];
        Octagon(rear, hw * 0.74f, hh * 0.68f, cut * 0.7f);
        Octagon(full, hw, hh, cut);
        Octagon(bezel, hw * 1.06f, hh * 1.1f, cut * 1.05f);
        Octagon(mouth, hw * 0.88f, hh * 0.8f, cut * 0.6f);

        var zBack = -TurretHeadBack * length;
        var zRear = -0.2f * length;
        var zBand = 0.22f * length;
        var zBezel = 0.38f * length;
        var zFront = TurretHeadFront * length;
        var zLens = zFront - TurretLensRecess * length;

        g.Cap(rear, Vector2.Zero, zBack, -1f, dark);
        g.Loft(rear, zBack, full, zRear, Vector2.Zero, dark, 1f);
        g.Loft(full, zRear, full, zBand, Vector2.Zero, body, 1f);
        // The band: accent paint over the top and down both sides (faces 3 to 7; the top is -Y), plating underneath.
        Span<float> band = [body, body, body, accent, accent, accent, accent, accent];
        g.Loft(full, zBand, full, zBezel, Vector2.Zero, band, 1f);
        g.Annulus(bezel, full, Vector2.Zero, zBezel, -1f, dark);
        g.Loft(bezel, zBezel, bezel, zFront, Vector2.Zero, dark, 1f);
        g.Annulus(bezel, mouth, Vector2.Zero, zFront, 1f, dark);
        g.Loft(mouth, zLens, mouth, zFront, Vector2.Zero, dark, -1f);
        g.Cap(mouth, Vector2.Zero, zLens, 1f, Tag(0.02f));

        // Trunnion bosses fill the gap to the yoke's arms.
        Span<Vector2> boss = stackalloc Vector2[8];
        Polygon(boss, Vector2.Zero, r * 0.16f, MathF.PI / 8f);
        var reach = MathF.Max(r * (tractor.TurretYokeSpread - 0.085f) - hw, 0f);
        for (var side = -1; side <= 1; side += 2)
        {
            var cap = new Geometry(vertices, indices, new Frame(new Vector3(side * hw, 0f, 0f), Vector3.UnitX * side, Vector3.UnitY, Vector3.UnitZ, 0f));
            cap.Loft(boss, 0f, boss, reach, Vector2.Zero, dark, 1f);
        }
    }

    /// <summary>
    /// Builds the bars of the emitter face in the head's space: flat strips across the recessed face, standing a
    /// little off it. A part of its own, because it is drawn as light whose brightness follows the beam.
    /// </summary>
    public static void BuildTractorBars(float small, TractorLook tractor, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var r = small * tractor.TurretBaseRadius;
        var length = r * tractor.TurretHeadLength;
        var hw = r * tractor.TurretHeadWidth * 0.88f * 0.82f;
        var room = 2f * r * tractor.TurretHeadHeight * 0.8f * 0.8f;
        var pitch = room / tractor.TurretBars;
        var zLens = (TurretHeadFront - TurretLensRecess) * length;
        var z = zLens + TurretBarLift * length;
        Span<Vector2> bar = stackalloc Vector2[4];
        for (var k = 0; k < tractor.TurretBars; k++)
        {
            var centre = new Vector2(0f, (k + 0.5f) * pitch - 0.5f * room);
            Rectangle(bar, hw, 0.3f * pitch);
            for (var c = 0; c < 4; c++)
                bar[c] += centre;
            g.Loft(bar, zLens, bar, z, centre, 1f, 1f);
            g.Cap(bar, centre, z, 1f, 1f);
        }
    }
}
