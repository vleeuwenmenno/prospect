using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

// The main engine: a square bolted mount plate, a ring collar and a faceted bell with a lit throat (BellLook).
internal static partial class PropulsionModels
{
    // How deep in the bell the throat's disc sits, as a share of the bell's length, and how far the glow drawn over
    // the lining stands off it, as a share of its radius.
    private const float BellThroatDepth = 0.06f;
    private const float BellGlowInset = 0.985f;

    // The lining narrows to its throat faster than the wall outside it: the throat's radius inside, as a share of the
    // radius the bell starts with outside.
    private const float BellLiningThroat = 0.6f;

    // The lining's radius at the share t of the bell's length, for a throat and an exit radius.
    private static float Lining(PropulsionModelLook model, float throat, float exit, float t) =>
        Bell(throat * BellLiningThroat, exit, model.BellCurve, t);

    // An engine with a bell look, filling its box: the mount plate over the whole mounting face, so neighbours meet it
    // flush, and a collar and a bell for every nozzle the face is split into. A box deeper than those need grows the
    // model's housing between plate and collar, so a deeper engine keeps its bell.
    private static void BuildEngine(ref Geometry g, PropulsionModelLook model, BellLook bell, in PartBox box, FuelKind fuel, int block, bool back, List<ThrustNozzle>? nozzles)
    {
        var axis = Facings.Axis(box.Facing);
        var hu = box.Half[(axis + 1) % 3];
        var hv = box.Half[(axis + 2) % 3];
        var depth = 2f * box.Half[axis];
        var cells = box.CellSize > 0 ? box.CellSize : 1f;
        var small = MathF.Min(hu, hv);

        // The face is split into nozzles: by cells for a cluster, otherwise into squares of the shorter side.
        int nu, nv;
        if (model.PerCell > 0)
        {
            nu = Math.Clamp((int)MathF.Round(2f * hu / cells * model.PerCell), 1, 8);
            nv = Math.Clamp((int)MathF.Round(2f * hv / cells * model.PerCell), 1, 8);
        }
        else
        {
            nu = Math.Clamp((int)MathF.Round(hu / small), 1, 8);
            nv = Math.Clamp((int)MathF.Round(hv / small), 1, 8);
        }
        var pitchU = 2f * hu / nu;
        var pitchV = 2f * hv / nv;
        var cell = 0.5f * MathF.Min(pitchU, pitchV);
        var exit = cell * model.ExitShare / (1f + model.Lip);

        // Along the box: plate, housing (only when there is depth to spare), collar, bell. The bell follows its exit,
        // never the box's depth; a shallow box squeezes the collar, then the plate.
        var nozzleLength = MathF.Min(model.NozzleLength * exit, model.MaxNozzleDepth * depth);
        var collar = MathF.Min(model.CollarLength * exit, 0.6f * (depth - nozzleLength));
        var plate = MathF.Min(bell.PlateDepth * small, 0.5f * (depth - nozzleLength - collar));
        var spare = depth - nozzleLength - collar - plate;
        var housing = spare > 0.35f * small;
        if (!housing)
            collar += spare;
        var bodyEnd = housing ? plate + spare : plate;
        var nozzleStart = depth - nozzleLength;

        var body = PaintRgb(bell.BodyColor);
        var plating = PaintRgb(bell.PlateColor);

        // The mount plate: a slab over the whole face.
        Span<Vector2> rect = stackalloc Vector2[4];
        Rectangle(rect, hu, hv);
        var slab = housing ? plate : 0.55f * plate;
        if (back)
            g.Cap(rect, Vector2.Zero, 0f, -1f, plating);
        g.Loft(rect, 0f, rect, slab, Vector2.Zero, plating, 1f);
        g.Cap(rect, Vector2.Zero, slab, 1f, plating);
        if (housing)
            AddHousing(ref g, model, hu, hv, small, plate, bodyEnd, MathF.Min(model.Bevel * small, 0.45f * spare), cells, body, plating);

        var collarRadius = MathF.Min(bell.CollarRadius * exit, cell / (1.09f + bell.BandLift));
        for (var i = 0; i < nu; i++)
        {
            for (var j = 0; j < nv; j++)
            {
                var at = new Vector2((i + 0.5f) * pitchU - hu, (j + 0.5f) * pitchV - hv);
                if (!housing)
                    AddEnginePlate(ref g, bell, at, 0.5f * pitchU, 0.5f * pitchV, slab, plate, collarRadius * 1.08f, plating, body);
                AddEngineCollar(ref g, bell, at, bodyEnd, nozzleStart, collarRadius, body, plating);
                AddEngineBell(ref g, model, bell, at, nozzleStart, depth, exit, body);
                nozzles?.Add(new ThrustNozzle(block, fuel, box.Facing, g.F.At(at.X, at.Y, depth), exit, nozzleLength, model.PlumeSlot, nozzles.Count, model.GlowSlot));
            }
        }
    }

    // The raised plate under one nozzle: its square of the slab less an inset, corners cut, bevelled at the top, with a
    // hexagonal bolt head in each corner between the cut and the collar's flange.
    private static void AddEnginePlate(ref Geometry g, BellLook bell, Vector2 at, float hu, float hv, float z0, float z1, float flange, float plating, float dark)
    {
        var half = MathF.Min(hu, hv);
        var inset = bell.PlateInset * half;
        var cut = 2f * inset;
        var bevel = MathF.Min(0.35f * (z1 - z0), 0.5f * inset);
        Span<Vector2> outer = stackalloc Vector2[8];
        Span<Vector2> top = stackalloc Vector2[8];
        Octagon(outer, hu - inset, hv - inset, cut);
        Octagon(top, hu - inset - bevel, hv - inset - bevel, MathF.Max(cut - bevel * (2f - MathF.Sqrt(2f)), 1e-4f));
        for (var k = 0; k < 8; k++)
        {
            outer[k] += at;
            top[k] += at;
        }
        g.Loft(outer, z0, outer, z1 - bevel, at, plating, 1f);
        g.Loft(outer, z1 - bevel, top, z1, at, plating, 1f);
        g.Cap(top, at, z1, 1f, plating);

        if (bell.Bolts != 4)
            return;
        // Along a corner's diagonal the plate ends at its cut; a bolt sits midway between that and the flange.
        var corner = (2f * (half - inset) - cut) / MathF.Sqrt(2f);
        var room = corner - flange;
        if (room < 0.04f * half)
            return;
        var radius = MathF.Min(0.32f * room, 0.1f * half);
        var along = 0.5f * (corner + flange) / MathF.Sqrt(2f);
        var height = z1 + MathF.Min(0.4f * (z1 - z0) + 0.2f * radius, radius);
        Span<Vector2> head = stackalloc Vector2[6];
        for (var k = 0; k < 4; k++)
        {
            var centre = at + new Vector2((k is 0 or 3 ? 1f : -1f) * (hu - half + along), (k is 0 or 1 ? 1f : -1f) * (hv - half + along));
            Polygon(head, centre, radius, MathF.PI / 6f);
            g.Loft(head, z1, head, height, centre, dark, 1f);
            g.Cap(head, centre, height, 1f, dark);
        }
    }

    // The ring collar between the plate and the bell: a flange on the plate, a drum, a band standing proud of it with
    // accent paint over its top and bottom and a light on each side, and a front ring the bell is seated in.
    private static void AddEngineCollar(ref Geometry g, BellLook bell, Vector2 at, float z0, float z1, float radius, float body, float plating)
    {
        var length = z1 - z0;
        if (length < 1e-5f)
            return;
        var sides = bell.CollarSides;
        // Side k is square across the angle (k + 1) steps round, so a quarter of the sides in is straight up.
        var turn = MathF.PI / sides;
        Span<Vector2> flange = stackalloc Vector2[sides];
        Span<Vector2> drum = stackalloc Vector2[sides];
        Span<Vector2> band = stackalloc Vector2[sides];
        Span<Vector2> ring = stackalloc Vector2[sides];
        Polygon(flange, at, radius * 1.08f, turn);
        Polygon(drum, at, radius * 0.94f, turn);
        Polygon(band, at, radius * (1f + bell.BandLift), turn);
        Polygon(ring, at, radius * 1.04f, turn);
        var zFlange = z0 + 0.14f * length;
        var zBand = z0 + 0.28f * length;
        var zBandEnd = z0 + 0.72f * length;
        var zRing = z0 + 0.84f * length;

        g.Loft(flange, z0, flange, zFlange, at, plating, 1f);
        g.Annulus(flange, drum, at, zFlange, 1f, plating);
        g.Loft(drum, zFlange, drum, zBand, at, body, 1f);

        var accent = PaintRgb(bell.AccentColor);
        var top = sides / 4 - 1;
        var wide = sides >= 16 ? 1 : 0;
        Span<float> tags = stackalloc float[sides];
        for (var k = 0; k < sides; k++)
        {
            var fromTop = Math.Min((k - top + sides) % sides, (top - k + sides) % sides);
            var fromBottom = Math.Abs(fromTop - sides / 2);
            tags[k] = fromTop <= wide || fromBottom <= wide ? accent : body;
        }
        g.Annulus(band, drum, at, zBand, -1f, body);
        g.Loft(band, zBand, band, zBandEnd, at, tags, 1f);
        g.Annulus(band, drum, at, zBandEnd, 1f, body);

        // A light on each side of the band: the sides square across the first axis.
        var light = Emissive(bell.LightColor);
        var inside = new Vector3(at, 0.5f * (zBand + zBandEnd));
        var lift = radius * 0.012f;
        for (var k = sides / 2 - 1; k < sides; k += sides / 2)
        {
            var a = band[k];
            var b = band[(k + 1) % sides];
            g.Panel(new Vector3(a, zBand), new Vector3(b, zBand), new Vector3(b, zBandEnd), new Vector3(a, zBandEnd), 0.2f, 0.8f, 0.14f, 0.86f, inside, lift, light);
        }

        g.Loft(drum, zBandEnd, drum, zRing, at, body, 1f);
        g.Annulus(ring, drum, at, zRing, -1f, plating);
        g.Loft(ring, zRing, ring, z1, at, plating, 1f);
        g.Cap(ring, at, z1, 1f, plating);
    }

    // The bell: faceted walls in a few rings, opening from the throat to the exit, accent paint on three sides of the
    // first ring, a lip, and inside a lining that is lit near the throat and dark toward the exit, closed by the
    // throat's lit disc.
    private static void AddEngineBell(ref Geometry g, PropulsionModelLook model, BellLook bell, Vector2 at, float z0, float z1, float exit, float body)
    {
        var sides = model.Sides;
        var rings = model.Rings;
        var turn = MathF.PI / sides;
        var throat = exit * model.ThroatShare;
        var wall = exit * model.Lip;
        var length = z1 - z0;
        var accent = PaintRgb(bell.AccentColor);
        Span<Vector2> a = stackalloc Vector2[sides];
        Span<Vector2> b = stackalloc Vector2[sides];
        Span<float> tags = stackalloc float[sides];
        // Straight up, and the two lower diagonals where the sides allow them (else straight down).
        var top = sides / 4 - 1;
        for (var k = 0; k < sides; k++)
        {
            var lower = sides % 8 == 0 ? k == 5 * sides / 8 - 1 || k == 7 * sides / 8 - 1 : k == 3 * sides / 4 - 1;
            tags[k] = k == top || lower ? accent : body;
        }
        for (var r = 0; r < rings; r++)
        {
            var t0 = (float)r / rings;
            var t1 = (float)(r + 1) / rings;
            Polygon(a, at, Bell(throat, exit, model.BellCurve, t0) + wall, turn);
            Polygon(b, at, Bell(throat, exit, model.BellCurve, t1) + wall, turn);
            if (r == 0 && rings > 1)
                g.Loft(a, z0 + length * t0, b, z0 + length * t1, at, tags, 1f);
            else
                g.Loft(a, z0 + length * t0, b, z0 + length * t1, at, body, 1f);
        }
        Polygon(a, at, exit + wall, turn);
        Polygon(b, at, exit, turn);
        g.Annulus(a, b, at, z1, 1f, body);

        // The lining, from the exit back to the throat: scorched metal, then lit, the throat's disc twice as bright.
        var idle = bell.ThroatColor;
        var lit = Emissive(idle);
        var disc = Emissive((Math.Min(((idle >> 16) & 0xFF) * 2, 255u) << 16) | (Math.Min(((idle >> 8) & 0xFF) * 2, 255u) << 8) | Math.Min((idle & 0xFF) * 2, 255u));
        var reach = bell.GlowReach;
        Polygon(a, at, Lining(model, throat, exit, reach), turn);
        g.Loft(a, z0 + length * reach, b, z1, at, Tag(model.NozzleShade * 0.55f), -1f);
        Polygon(b, at, Lining(model, throat, exit, BellThroatDepth), turn);
        g.Loft(b, z0 + length * BellThroatDepth, a, z0 + length * reach, at, lit, -1f);
        g.Cap(b, at, z0 + length * BellThroatDepth, 1f, disc);
    }

    /// <summary>
    /// Builds the light a bell's throat gives off under thrust, for a nozzle of exit radius 1 and length 1 with its
    /// throat end at the origin and its exit at +Z 1: the lit part of the lining and the throat's disc, a hair inside
    /// the lining itself. One model serves every nozzle of the look, scaled by its exit radius across and its length
    /// along; it is drawn as light whose brightness follows the nozzle's thrust.
    /// </summary>
    public static void BuildEngineGlow(PropulsionModelLook model, BellLook bell, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var sides = model.Sides;
        var turn = MathF.PI / sides;
        Span<Vector2> a = stackalloc Vector2[sides];
        Span<Vector2> b = stackalloc Vector2[sides];
        var deep = BellThroatDepth + 0.01f;
        Polygon(a, Vector2.Zero, Lining(model, model.ThroatShare, 1f, deep) * BellGlowInset, turn);
        Polygon(b, Vector2.Zero, Lining(model, model.ThroatShare, 1f, bell.GlowReach) * BellGlowInset, turn);
        g.Loft(a, deep, b, bell.GlowReach, Vector2.Zero, 1f, -1f);
        g.Cap(a, Vector2.Zero, deep, 1f, 1f);
    }
}
