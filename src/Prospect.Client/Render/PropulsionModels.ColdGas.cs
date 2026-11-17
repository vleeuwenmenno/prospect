using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// The cold gas thruster array a face-mounted part is built as, from the <c>array</c> entry of its model in
/// <c>content/visuals/propulsion.json</c>: a stepped, chamfered square plate with painted corner caps and a glowing
/// slot in each side, and on it a grid of short flared nozzle cups, one for each unit of the part's footprint
/// (<see cref="PropulsionModelLook.PerCell"/> units a cell: one, two by two or three by three). Every cup has a glowing
/// band round its foot and another inside its mouth; both brighten while the part fires. Lengths across the plate are
/// shares of one unit's pitch and heights are shares of the box's depth, so cups and caps are the same size on an
/// array of any size.
/// </summary>
internal sealed class ArrayLook
{
    /// <summary>The lower plate's thickness, and the height of the upper plate's top, both as shares of the box's depth.</summary>
    public float BaseHeight { get; init; }
    public float StepHeight { get; init; }

    /// <summary>How far the lower plate's corners are cut, and how far the upper plate stands in from its edge: shares of a unit's pitch.</summary>
    public float BaseChamfer { get; init; }
    public float StepInset { get; init; }

    /// <summary>The slope round the upper plate's top edge, as a share of that plate's thickness.</summary>
    public float StepBevel { get; init; }

    /// <summary>How far a corner cap runs along each side from the cut corner, as a share of a unit's pitch.</summary>
    public float CapLength { get; init; }

    /// <summary>The glowing slot in each side: its width as a share of a unit's pitch and its height as a share of the lower plate's.</summary>
    public float SlotWidth { get; init; }
    public float SlotHeight { get; init; }

    /// <summary>The panel ribs between the units on the upper plate: their width as a share of a unit's pitch; 0 for none.</summary>
    public float Seam { get; init; }

    /// <summary>A cup's radius at its foot, as a share of its radius at the mouth: below 1, so it flares.</summary>
    public float CupBase { get; init; }

    /// <summary>The glowing band round a cup's foot: where it starts and how tall it is, as shares of the cup's height.</summary>
    public float RingStart { get; init; }
    public float RingHeight { get; init; }

    /// <summary>The glowing band inside the mouth: how far down from the rim it reaches, and how deep the cup's floor lies, as shares of the cup's height.</summary>
    public float InnerDepth { get; init; }
    public float FloorDepth { get; init; }

    /// <summary>Shades of the lower plate and of the ribs (<see cref="PropulsionModels.Tag"/>).</summary>
    public float PlateShade { get; init; }
    public float SeamShade { get; init; }

    /// <summary>The paint of the corner caps and the colour of the glow, each RGB from 0 to 1.</summary>
    public float[] CapColor { get; init; } = [];
    public float[] GlowColor { get; init; } = [];

    /// <summary>
    /// How bright the foot bands and slots, and the bands inside the mouths, are while the part is idle, as shares of
    /// <see cref="GlowColor"/>; and how much of that colour the cups' bands gain on top at full thrust
    /// (<c>mesh.vert</c>, <see cref="PropulsionModels.Lit"/>).
    /// </summary>
    public float GlowIdle { get; init; }
    public float InnerIdle { get; init; }
    public float GlowFiring { get; init; }

    internal bool Valid
    {
        get
        {
            static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 1 && c[1] is >= 0 and <= 1 && c[2] is >= 0 and <= 1;
            static bool Shade(float v) => v is >= 0 and < 1;
            return BaseHeight is >= 0.05f and <= 0.5f && StepHeight > BaseHeight + 0.04f && StepHeight <= 0.8f
                && BaseChamfer is >= 0.02f and <= 0.3f && StepInset is >= 0.02f and <= 0.3f && StepBevel is >= 0 and <= 0.9f
                // Two caps and a slot share a side that is one pitch less two cuts long on the smallest array.
                && CapLength is >= 0 and <= 0.3f && SlotWidth is >= 0 and <= 0.8f && 2f * (BaseChamfer + CapLength) < 0.9f
                && SlotHeight is > 0 and <= 0.9f && Seam is >= 0 and <= 0.1f
                && CupBase is >= 0.4f and <= 1f && RingStart is >= 0 and <= 0.5f && RingHeight is > 0 and <= 0.5f && RingStart + RingHeight <= 0.9f
                && InnerDepth is > 0 and <= 0.6f && FloorDepth >= InnerDepth + 0.05f && FloorDepth <= 0.95f
                && Shade(PlateShade) && Shade(SeamShade) && Colour(CapColor) && Colour(GlowColor)
                && GlowIdle is > 0 and <= 1 && InnerIdle is >= 0 and <= 1 && GlowFiring is >= 0 and <= 8;
        }
    }
}

internal static partial class PropulsionModels
{
    /// <summary>The most nozzles along one side of an array: a mount is at most a cell wide and a unit a tenth of one.</summary>
    public const int MaxArraySide = 10;

    /// <summary>
    /// The normal length that carries a light which brightens with thrust to the mesh shader: 5.5 plus a colour of five
    /// bits a channel. Such a facet's normal points the way its nozzle does, not the way the facet lies, so
    /// <c>mesh.vert</c> can ask the hull's thrust whether that nozzle fires; a light needs no normal to be lit by.
    /// </summary>
    public static float Lit(ReadOnlySpan<float> rgb, float share)
    {
        int Level(float c) => Math.Clamp((int)MathF.Round(c * share * 31f), 0, 31);
        var code = (Level(rgb[0]) << 10) | (Level(rgb[1]) << 5) | Level(rgb[2]);
        return 5.5f + (code + 0.5f) / 32768f;
    }

    /// <summary>The normal length of a steady light of <paramref name="share"/> of a colour (<see cref="Emissive"/>'s range).</summary>
    public static float Glow(ReadOnlySpan<float> rgb, float share)
    {
        int Level(float c) => Math.Clamp((int)MathF.Round(c * share * 31f), 0, 31);
        var code = (Level(rgb[0]) << 10) | (Level(rgb[1]) << 5) | Level(rgb[2]);
        return 4.5f + (code + 0.5f) / 32768f;
    }

    /// <summary>How many nozzles an array in <paramref name="box"/> has along each side of its face: one for each unit of its footprint.</summary>
    public static void ArrayGrid(PropulsionModelLook model, in PartBox box, out int nu, out int nv)
    {
        var axis = Facings.Axis(box.Facing);
        var cells = box.CellSize > 0 ? box.CellSize : 1f;
        var perCell = model.PerCell > 0 ? model.PerCell : 1f;
        nu = Math.Clamp((int)MathF.Round(2f * box.Half[(axis + 1) % 3] / cells * perCell), 1, MaxArraySide);
        nv = Math.Clamp((int)MathF.Round(2f * box.Half[(axis + 2) % 3] / cells * perCell), 1, MaxArraySide);
    }

    // A cold gas array in the thin box a mount fills: the stepped plate, its caps, slots and ribs, and a cup for every
    // unit, each with a nozzle of its own in the list, so every cup shows its own plume. A pure function of the box.
    private static void BuildArray(ref Geometry g, PropulsionModelLook model, ArrayLook look, in PartBox box, FuelKind fuel, int block, bool back, List<ThrustNozzle>? nozzles)
    {
        var axis = Facings.Axis(box.Facing);
        var hu = box.Half[(axis + 1) % 3];
        var hv = box.Half[(axis + 2) % 3];
        var depth = 2f * box.Half[axis];
        ArrayGrid(model, box, out var nu, out var nv);
        var pitchU = 2f * hu / nu;
        var pitchV = 2f * hv / nv;
        var pitch = MathF.Min(pitchU, pitchV);

        var plateTag = Tag(look.PlateShade);
        var stepTag = Tag(model.HousingShade);
        var seamTag = Tag(look.SeamShade);
        var cupTag = Tag(model.NozzleShade);
        var liningTag = Tag(model.NozzleShade * 0.55f);
        var rimTag = Tag(model.DetailShade);
        var floorTag = Tag(model.ThroatShade);
        var capTag = Paint(look.CapColor);
        var slotTag = Glow(look.GlowColor, look.GlowIdle);
        var ringTag = Lit(look.GlowColor, look.GlowIdle);
        var innerTag = Lit(look.GlowColor, look.InnerIdle);

        // The lower plate: the whole footprint with its corners cut. Its walls are built a stretch at a time, so the
        // corners and the ends of each side can be capped in paint and the middle of each side can carry a slot.
        var baseTop = look.BaseHeight * depth;
        var cut = look.BaseChamfer * pitch;
        Span<Vector2> plate = stackalloc Vector2[8];
        Octagon(plate, hu, hv, cut);
        if (back)
            g.Cap(plate, Vector2.Zero, 0f, -1f, plateTag);
        var slotLow = baseTop * 0.5f * (1f - look.SlotHeight);
        var slotHigh = baseTop - slotLow;
        for (var k = 0; k < 8; k++)
        {
            var from = plate[k];
            var to = plate[(k + 1) % 8];
            if ((k & 1) == 0)
            {
                // A cut corner: painted from the face to the plate's top.
                Wall(ref g, from, to, 0f, baseTop, capTag);
                continue;
            }

            var length = (to - from).Length();
            var cap = MathF.Min(look.CapLength * pitch, 0.3f * length);
            var slot = MathF.Min(look.SlotWidth * pitch, 0.8f * (length - 2f * cap));
            var a = Along(from, to, cap / length);
            var b = Along(from, to, 0.5f - 0.5f * slot / length);
            var c = Along(from, to, 0.5f + 0.5f * slot / length);
            var d = Along(from, to, 1f - cap / length);
            if (cap > 0)
            {
                Wall(ref g, from, a, 0f, baseTop, capTag);
                Wall(ref g, d, to, 0f, baseTop, capTag);
            }

            Wall(ref g, a, b, 0f, baseTop, plateTag);
            Wall(ref g, c, d, 0f, baseTop, plateTag);
            Wall(ref g, b, c, 0f, slotLow, plateTag);
            Wall(ref g, b, c, slotLow, slotHigh, slotTag);
            Wall(ref g, b, c, slotHigh, baseTop, plateTag);
        }

        // The ledge between the two plates, painted over the cut corners like the caps below it.
        var inset = look.StepInset * pitch;
        var su = hu - inset;
        var sv = hv - inset;
        var stepCut = MathF.Max(cut - inset * (2f - MathF.Sqrt(2f)), 1e-4f);
        Span<Vector2> step = stackalloc Vector2[8];
        Octagon(step, su, sv, stepCut);
        for (var k = 0; k < 8; k++)
        {
            var k1 = (k + 1) % 8;
            g.Quad(new Vector3(plate[k], baseTop), new Vector3(plate[k1], baseTop), new Vector3(step[k1], baseTop), new Vector3(step[k], baseTop), Vector3.UnitZ, (k & 1) == 0 ? capTag : plateTag);
        }

        // The upper plate: sloped round its top edge, and flat where the cups stand.
        var stepTop = look.StepHeight * depth;
        var bevel = MathF.Min(look.StepBevel * (stepTop - baseTop), 0.5f * MathF.Min(su, sv));
        Span<Vector2> top = stackalloc Vector2[8];
        Octagon(top, su - bevel, sv - bevel, MathF.Max(stepCut - bevel * (2f - MathF.Sqrt(2f)), 1e-4f));
        g.Loft(step, baseTop, step, stepTop - bevel, Vector2.Zero, stepTag, 1f);
        if (bevel > 0)
            g.Loft(step, stepTop - bevel, top, stepTop, Vector2.Zero, stepTag, 1f);
        g.Cap(top, Vector2.Zero, stepTop, 1f, stepTag);

        // Ribs between the units: low, dark, and standing a little proud so they never fight the plate for depth.
        if (look.Seam > 0)
        {
            Span<Vector2> rib = stackalloc Vector2[4];
            var half = 0.5f * look.Seam * pitch;
            var lift = 0.03f * depth;
            for (var across = 0; across < 2; across++)
            {
                var count = across == 0 ? nu : nv;
                var reach = (across == 0 ? sv : su) - bevel;
                for (var i = 1; i < count; i++)
                {
                    var offset = across == 0 ? i * pitchU - hu : i * pitchV - hv;
                    var centre = across == 0 ? new Vector2(offset, 0f) : new Vector2(0f, offset);
                    Rectangle(rib, across == 0 ? half : reach, across == 0 ? reach : half);
                    for (var p = 0; p < 4; p++)
                        rib[p] += centre;
                    g.Loft(rib, stepTop, rib, stepTop + lift, centre, seamTag, 1f);
                    g.Cap(rib, centre, stepTop + lift, 1f, seamTag);
                }
            }
        }

        // The cups: short and flared, one a unit. The bands that glow carry the facing as their normal (Lit).
        var exit = 0.5f * pitch * model.ExitShare / (1f + model.Lip);
        var wall = exit * model.Lip;
        var mouth = exit + wall;
        var foot = mouth * look.CupBase;
        var height = depth - stepTop;
        var sides = model.Sides;
        var turn = MathF.PI / sides;
        Span<Vector2> a0 = stackalloc Vector2[sides];
        Span<Vector2> a1 = stackalloc Vector2[sides];
        for (var i = 0; i < nu; i++)
        {
            for (var j = 0; j < nv; j++)
            {
                var at = new Vector2((i + 0.5f) * pitchU - hu, (j + 0.5f) * pitchV - hv);
                float Outer(float t) => foot + (mouth - foot) * t;
                float Inner(float t) => MathF.Max(Outer(t) - wall, 0.2f * exit);

                // The foot collar, then the outer wall in three stretches: dark, the glowing band, dark.
                var collar = 0.6f * look.RingStart;
                Polygon(a0, at, Outer(0f) * 1.1f, turn);
                Polygon(a1, at, Outer(collar), turn);
                g.Loft(a0, stepTop, a0, stepTop + collar * height, at, rimTag, 1f);
                g.Annulus(a0, a1, at, stepTop + collar * height, 1f, rimTag);
                var ringEnd = look.RingStart + look.RingHeight;
                Ring(ref g, a0, a1, at, turn, Outer(collar), Outer(look.RingStart), stepTop + collar * height, stepTop + look.RingStart * height, cupTag, 1f);

                // The band stands straight, as wide as the cup is at the band's top, so it is a little proud at its
                // foot: a lit facet's normal is the facing with a lean to its own side, which a slope would outweigh.
                Polygon(a0, at, Outer(ringEnd), turn);
                Polygon(a1, at, Outer(look.RingStart), turn);
                g.Annulus(a0, a1, at, stepTop + look.RingStart * height, -1f, cupTag);
                g.Lit = true;
                Ring(ref g, a0, a1, at, turn, Outer(ringEnd), Outer(ringEnd), stepTop + look.RingStart * height, stepTop + ringEnd * height, ringTag, 1f);
                g.Lit = false;
                Ring(ref g, a0, a1, at, turn, Outer(ringEnd), mouth, stepTop + ringEnd * height, depth, cupTag, 1f);

                // The rim, the lining with its glowing band just inside the mouth, and the dark floor.
                Polygon(a0, at, mouth, turn);
                Polygon(a1, at, exit, turn);
                g.Annulus(a0, a1, at, depth, 1f, rimTag);
                var bandTop = 1f - 0.12f * look.InnerDepth;
                var bandLow = 1f - look.InnerDepth;
                var floor = 1f - look.FloorDepth;
                Ring(ref g, a0, a1, at, turn, Inner(bandTop), exit, stepTop + bandTop * height, depth, liningTag, -1f);
                g.Lit = true;
                Ring(ref g, a0, a1, at, turn, Inner(bandLow), Inner(bandTop), stepTop + bandLow * height, stepTop + bandTop * height, innerTag, -1f);
                g.Lit = false;
                Ring(ref g, a0, a1, at, turn, Inner(floor), Inner(bandLow), stepTop + floor * height, stepTop + bandLow * height, liningTag, -1f);
                Polygon(a0, at, Inner(floor), turn);
                g.Cap(a0, at, stepTop + floor * height, 1f, floorTag);

                nozzles?.Add(new ThrustNozzle(block, fuel, box.Facing, g.F.At(at.X, at.Y, depth), exit, height, model.PlumeSlot, nozzles.Count));
            }
        }
    }

    // One stretch of a round wall between two heights, facing out (side 1) or in toward the cup's middle (side -1).
    private static void Ring(ref Geometry g, Span<Vector2> low, Span<Vector2> high, Vector2 at, float turn, float lowRadius, float highRadius, float z0, float z1, float tag, float side)
    {
        if (z1 - z0 < 1e-6f)
            return;
        Polygon(low, at, lowRadius, turn);
        Polygon(high, at, highRadius, turn);
        g.Loft(low, z0, high, z1, at, tag, side);
    }

    // A flat stretch of the plate's outer wall from one point of its outline to the next, between two heights. The
    // outline runs counter-clockwise, so the wall faces to the right of the way it runs.
    private static void Wall(ref Geometry g, Vector2 from, Vector2 to, float z0, float z1, float tag)
    {
        var along = to - from;
        if (along.LengthSquared() < 1e-12f || z1 - z0 < 1e-6f)
            return;
        g.Quad(new Vector3(from, z0), new Vector3(to, z0), new Vector3(to, z1), new Vector3(from, z1), new Vector3(along.Y, -along.X, 0f), tag);
    }

    private static Vector2 Along(Vector2 from, Vector2 to, float t) => from + (to - from) * t;
}
