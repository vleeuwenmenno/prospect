using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

// What every posed turret is built from, whatever head it carries: the base ring in the hull mesh and the yoke that
// turns on it. The tractor beam and the mining laser give their figures as a TurretFrame and add a head of their own.
internal static partial class PropulsionModels
{
    private const float TurretSlotGlow = 0.55f;

    /// <summary>
    /// Builds a turret's base flush on the mount's block face (static hull mesh): a chamfered ring with a glow slot in
    /// the wall of every other side, starting with the one that looks along the face's second axis, and an accent
    /// panel on each side between them, a plinth, and the bearing collar the yoke turns on, painted
    /// <paramref name="collar"/> (the tier's colour). Every length is a share of the mount's half width.
    /// </summary>
    public static void BuildTurretBase(in PartBox box, in TurretFrame turret, float collar, List<MeshVertex> vertices, List<uint> indices)
    {
        var axis = Facings.Axis(box.Facing);
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var g = new Geometry(vertices, indices, new Frame(box.Center, Facings.Vector(box.Facing), Unit(u), Unit(v), box.Half[axis]));

        var small = MathF.Min(box.Half[u], box.Half[v]);
        var r = turret.Radius(small);
        var h = r * turret.BaseHeight;
        var body = PaintRgb(turret.BodyColor);
        var dark = PaintRgb(turret.DarkColor);
        var accent = PaintRgb(turret.AccentColor);
        // The slots glow lower than the emitter: the same colour at a share of its light.
        var lens = turret.SlotColor;
        var glow = Emissive(((uint)(((lens >> 16) & 0xFF) * TurretSlotGlow) << 16) | ((uint)(((lens >> 8) & 0xFF) * TurretSlotGlow) << 8) | (uint)((lens & 0xFF) * TurretSlotGlow));

        // Side 1 lies square across the face's second axis; an eight-sided ring then has its flats on the face's axes.
        var sides = turret.BaseSides;
        var turn = 0.5f * MathF.PI - 1.5f * MathF.Tau / sides;
        Span<Vector2> outer = stackalloc Vector2[sides];
        Span<Vector2> rim = stackalloc Vector2[sides];
        Span<Vector2> plinth = stackalloc Vector2[sides];
        Span<Vector2> ring = stackalloc Vector2[sides];
        Polygon(outer, Vector2.Zero, r, turn);
        Polygon(rim, Vector2.Zero, r * 0.84f, turn);
        Polygon(plinth, Vector2.Zero, r * 0.66f, turn);
        Polygon(ring, Vector2.Zero, r * 0.5f, turn);

        var wall = h * 0.55f;
        var plinthTop = h * 1.25f;
        var collarTop = h * TurretFrame.CollarTop;
        g.Loft(outer, 0f, outer, wall, Vector2.Zero, dark, 1f);
        g.Loft(outer, wall, rim, h, Vector2.Zero, body, 1f);
        g.Annulus(rim, plinth, Vector2.Zero, h, 1f, dark);
        g.Loft(plinth, h, plinth, plinthTop, Vector2.Zero, body, 1f);
        g.Annulus(plinth, ring, Vector2.Zero, plinthTop, 1f, body);
        g.Loft(ring, plinthTop, ring, collarTop, Vector2.Zero, collar, 1f);
        g.Cap(ring, Vector2.Zero, collarTop, 1f, dark);

        // Side k runs from corner k to the next: the odd ones carry the slots, the even ones the accent panels.
        var inside = new Vector3(0f, 0f, wall * 0.5f);
        var lift = r * 0.012f;
        var slots = 0;
        for (var k = 0; k < sides; k++)
        {
            var a = outer[k];
            var b = outer[(k + 1) % sides];
            if ((k & 1) != 0)
            {
                if (slots++ < turret.Slots)
                    g.Panel(new Vector3(a, 0f), new Vector3(b, 0f), new Vector3(b, wall), new Vector3(a, wall), 0.22f, 0.78f, 0.38f, 0.7f, inside, lift, glow);
                continue;
            }
            g.Panel(new Vector3(a, 0f), new Vector3(b, 0f), new Vector3(b, wall), new Vector3(a, wall), 0.06f, 0.94f, 0.3f, 1f, inside, lift, accent);
            g.Panel(new Vector3(a, wall), new Vector3(b, wall), new Vector3(rim[(k + 1) % sides], h), new Vector3(rim[k], h), 0.06f, 0.94f, 0f, 0.92f, inside, lift, accent);
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
    public static void BuildTurretYoke(float small, in TurretFrame turret, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var r = turret.Radius(small);
        var yokeH = r * turret.YokeHeight;
        var spread = r * turret.YokeSpread;
        var body = PaintRgb(turret.BodyColor);
        var dark = PaintRgb(turret.DarkColor);

        Span<Vector2> table = stackalloc Vector2[8];
        Span<Vector2> tableTop = stackalloc Vector2[8];
        Polygon(table, Vector2.Zero, r * 0.78f, MathF.PI / 8f);
        Polygon(tableTop, Vector2.Zero, r * 0.72f, MathF.PI / 8f);
        var tableH = r * TurretFrame.TableHeight;
        g.Cap(table, Vector2.Zero, 0f, -1f, dark);
        g.Loft(table, 0f, table, tableH * 0.6f, Vector2.Zero, dark, 1f);
        g.Loft(table, tableH * 0.6f, tableTop, tableH, Vector2.Zero, body, 1f);
        g.Cap(tableTop, Vector2.Zero, tableH, 1f, body);

        // The arms stand straight on the inside, so the head swings between them, and lean back from a deep foot.
        var arm = r * turret.Arm;
        var hubRadius = r * turret.Hub;
        Span<Vector2> foot = stackalloc Vector2[4];
        Span<Vector2> neck = stackalloc Vector2[4];
        Span<Vector2> tip = stackalloc Vector2[4];
        Span<Vector2> hub = stackalloc Vector2[8];
        Span<Vector2> hubTop = stackalloc Vector2[8];
        Polygon(hub, Vector2.Zero, hubRadius, MathF.PI / 8f);
        Polygon(hubTop, Vector2.Zero, hubRadius * 0.68f, MathF.PI / 8f);
        for (var side = -1; side <= 1; side += 2)
        {
            var footAt = new Vector2(side * (spread + arm * 0.25f), -r * 0.1f);
            var neckAt = new Vector2(side * spread, 0f);
            Rectangle(foot, arm * 1.25f, r * 0.27f);
            Rectangle(neck, arm, MathF.Max(r * 0.2f, hubRadius));
            Rectangle(tip, arm, MathF.Max(r * 0.1f, hubRadius * 0.5f));
            for (var c = 0; c < 4; c++)
            {
                foot[c] += footAt;
                neck[c] += neckAt;
                tip[c] += neckAt;
            }
            var neckZ = yokeH - r * 0.04f;
            var tipZ = yokeH + MathF.Max(r * 0.22f, hubRadius);
            g.Loft(foot, tableH * 0.5f, neck, neckZ, 0.5f * (footAt + neckAt), body, 1f);
            g.Loft(neck, neckZ, tip, tipZ, neckAt, body, 1f);
            g.Cap(tip, neckAt, tipZ, 1f, dark);

            // The trunnion's hub cap on the outside of the arm, its axis the pitch axis.
            var cap = new Geometry(vertices, indices, new Frame(new Vector3(side * (spread + arm), 0f, yokeH), Vector3.UnitX * side, Vector3.UnitY, Vector3.UnitZ, 0f));
            cap.Loft(hub, 0f, hub, r * 0.05f, Vector2.Zero, dark, 1f);
            cap.Loft(hub, r * 0.05f, hubTop, r * 0.085f, Vector2.Zero, dark, 1f);
            cap.Cap(hubTop, Vector2.Zero, r * 0.085f, 1f, body);
        }

        Bar(vertices, indices, new Vector3(0f, -r * TurretFrame.BraceBack, r * TurretFrame.BraceLift), 0, spread, r * TurretFrame.BraceHalfDepth, r * TurretFrame.BraceHalfHeight, dark);
    }
}
