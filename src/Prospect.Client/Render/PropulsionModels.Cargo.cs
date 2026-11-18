using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// The rack's and the container's proportions and paint, loaded before mesh workers start. A rack is a closed box with
/// one open face (the block's facing); containers fill its whole grid, each in a slot it keeps a clearance from
/// (<see cref="Gap"/> of the slot's smallest half-side, the smallest being a one-unit container's). The clearance is
/// split: rack parts take the outer half of the smallest container's (<see cref="RackBudget"/>, in units), a
/// container's details its inner half of its own (<see cref="DetailShare"/>), so neither reaches into the other.
/// Every rack thickness is a share of one cargo unit. Paint and light colours are linear, as <c>mesh.vert</c> reads them.
/// </summary>
internal sealed class RackLook
{
    /// <summary>Share of a container's own clearance its details may stand off its body.</summary>
    public const float DetailShare = .5f;

    /// <summary>Wall thickness.</summary>
    public float Wall { get; init; }
    /// <summary>Wall panel RGB.</summary>
    public float[] WallPaint { get; init; } = [];
    /// <summary>RGB of the backing that shows in the seams between wall panels.</summary>
    public float[] SeamPaint { get; init; } = [];
    /// <summary>Cargo units between panel seams on a wall.</summary>
    public int PanelEvery { get; init; }
    /// <summary>Seam width, in units.</summary>
    public float Seam { get; init; }
    /// <summary>Depth of the framed rim band round the opening on the outer walls, in units.</summary>
    public float Rim { get; init; }
    /// <summary>
    /// Where the seam backing's inside face sits, as a share of the wall's thickness from its outer surface: what the
    /// seams between the wall panels show. It is under one, so the panels stand proud of it and every seam is a groove.
    /// </summary>
    public float SeamBed { get; init; }
    /// <summary>Rim band RGB.</summary>
    public float[] RimPaint { get; init; } = [];
    /// <summary>Hazard stripe RGB (with <see cref="RimPaint"/> between them).</summary>
    public float[] Hazard { get; init; } = [];
    /// <summary>Half width of a hazard stripe, in units.</summary>
    public float HazardWidth { get; init; }
    /// <summary>Distance between hazard stripes, in units.</summary>
    public float HazardPitch { get; init; }
    /// <summary>Accent RGB: the rim's corner plates and the containers' corner caps.</summary>
    public float[] Accent { get; init; } = [];
    /// <summary>Edge of a rim corner plate, in units.</summary>
    public float AccentSize { get; init; }
    /// <summary>Light strip RGB: the rim's edges, its strips, the rails' guide lights and the containers' strips.</summary>
    public float[] Light { get; init; } = [];
    /// <summary>Length of the rim's light strips as a share of their edge.</summary>
    public float LightLength { get; init; }
    /// <summary>Half width of a light strip, in units.</summary>
    public float LightWidth { get; init; }
    /// <summary>Rail width, in units.</summary>
    public float Rail { get; init; }
    /// <summary>Rail height off the inner wall, in units.</summary>
    public float RailHeight { get; init; }
    /// <summary>Rail brightness.</summary>
    public float RailShade { get; init; }
    /// <summary>Mag-lock jaw thickness, in units.</summary>
    public float Jaw { get; init; }
    /// <summary>Jaw width as a share of the container side it grips.</summary>
    public float JawWidth { get; init; }
    /// <summary>Jaw height as a share of the container's depth in the rack.</summary>
    public float JawHeight { get; init; }
    /// <summary>Jaw brightness.</summary>
    public float JawShade { get; init; }
    /// <summary>Lock light edge length, in units.</summary>
    public float LightSize { get; init; }
    /// <summary>Container clearance as a share of its smallest half-side.</summary>
    public float Gap { get; init; }
    /// <summary>The body's chamfer along its long edges, as a share of its smallest half-side.</summary>
    public float Chamfer { get; init; }
    /// <summary>Corner cap half-size as a share of the container's smallest half-side.</summary>
    public float Corner { get; init; }
    /// <summary>How far a corner cap stands off the body, as a share of its own half-size.</summary>
    public float CornerLift { get; init; }
    /// <summary>How far the body takes its resource's colour: 0 is the plain body, 1 the resource colour.</summary>
    public float BodyTint { get; init; }
    /// <summary>Container body RGB.</summary>
    public float[] Body { get; init; } = [];
    /// <summary>Side panel RGB.</summary>
    public float[] Panel { get; init; } = [];
    /// <summary>Side panel thickness as a share of the container's clearance.</summary>
    public float PanelDepth { get; init; }
    /// <summary>Height of the band above and below a side panel that carries a light strip, as a share of the smallest half-side.</summary>
    public float StripBand { get; init; }
    /// <summary>Length of a side light strip as a share of the container's length.</summary>
    public float StripLength { get; init; }
    /// <summary>Label plate thickness as a share of the container's clearance.</summary>
    public float PlateDepth { get; init; }
    /// <summary>Stroke width as a share of a glyph's height.</summary>
    public float Stroke { get; init; }
    /// <summary>Brightness of the resource's name or code on its plate.</summary>
    public float TextShade { get; init; }
    /// <summary>RGB of the size in units ("16u").</summary>
    public float[] SizeText { get; init; } = [];
    /// <summary>Plate RGB for an empty container.</summary>
    public float[] EmptyPlate { get; init; } = [];
    /// <summary>How containers are seen going into and out of racks.</summary>
    public CargoMotionLook? Motion { get; init; }

    /// <summary>The thickest any rack part may be, in units: the outer half of a one-unit container's clearance.</summary>
    public float RackBudget => .5f * (1 - DetailShare) * Gap;

    /// <summary>The galaxy's resources, in index order: colour, name and code for each container plate. Set at load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Resource[] Galaxy { get; private set; } = [];

    /// <summary>The cargo catalogue, for sizes in units. Set at load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CargoCatalog? Cargo { get; private set; }

    /// <summary>Gives the look what its container plates read: resources and sizes.</summary>
    public void Attach(Resource[] resources, CargoCatalog cargo) { Galaxy = resources; Cargo = cargo; }

    /// <summary>Refuses proportions or colours outside model bounds.</summary>
    public void Check()
    {
        if (!(Gap is > 0 and <= .2f)) throw new InvalidDataException("Invalid rack proportions.");
        var budget = RackBudget;
        foreach (var thin in new[] { Wall, Jaw })
            if (!(thin > 0 && thin <= budget)) throw new InvalidDataException("A rack part is thicker than the clearance a container keeps.");
        if (!(RailHeight > 0 && Wall + RailHeight <= budget)) throw new InvalidDataException("A rack's rails reach into the containers' clearance.");
        // The seam backing keeps to the wall's inner half, clear of the band the seams are cut through and of the
        // inside face the panels end on, so it never shares a plane with either.
        if (!(SeamBed is > .5f and < .95f)) throw new InvalidDataException("A rack's seam backing is not inside its wall.");
        if (PanelEvery is < 1 or > 16 || !(Seam > 0 && Seam <= .3f * PanelEvery) || !(Rim is > 0 and <= 2)
            || !(HazardWidth > 0 && HazardPitch >= 2 * HazardWidth && HazardPitch <= 4) || !(AccentSize is > 0 and <= 2)
            || !(LightLength is > 0 and <= 1) || !(LightWidth > 0 && LightWidth <= Rim) || !(Rail is > 0 and <= 1)
            || !(RailShade is > 0 and <= 1) || !(JawWidth is > 0 and <= 1) || !(JawHeight is > 0 and <= 1) || !(JawShade is > 0 and <= 1)
            || !(LightSize is > 0 and <= 1))
            throw new InvalidDataException("Invalid rack proportions.");
        if (!(Chamfer is >= 0 and <= .3f) || !(Corner is > 0 and <= .35f) || !(CornerLift is > 0 and <= 1) || !(BodyTint is >= 0 and <= 1)
            || !(PanelDepth > 0) || !(PlateDepth > 0) || PanelDepth + 1.5f * PlateDepth > DetailShare
            || !(StripBand is > 0 and <= .3f) || !(StripLength is > 0 and <= 1) || !(Stroke is > 0 and <= .3f) || !(TextShade is >= 0 and <= 1))
            throw new InvalidDataException("Invalid container detail proportions.");
        foreach (var paint in new[] { WallPaint, SeamPaint, RimPaint, Hazard, Accent, Light, Body, Panel, SizeText, EmptyPlate }) CheckPaint(paint);
        if (Motion is null) throw new InvalidDataException("A rack look needs its motion.");
        Motion.Check();
    }
    private static void CheckPaint(float[] paint)
    {
        if (paint.Length != 3 || paint.Any(c => !float.IsFinite(c) || c < 0 || c > 1))
            throw new InvalidDataException("Invalid cargo paint.");
    }

    /// <summary>An emissive colour for a light RGB.</summary>
    internal static uint Rgb(float[] c) => ((uint)MathF.Round(c[0] * 255) << 16) | ((uint)MathF.Round(c[1] * 255) << 8) | (uint)MathF.Round(c[2] * 255);
}

internal static partial class PropulsionModels
{
    /// <summary>The most hazard stripes along one rim edge, so cosmetic detail stays bounded for extreme boxes.</summary>
    public const int MaxHazardStripes = 48;

    /// <summary>The most wall panels along one side of a wall.</summary>
    public const int MaxWallPanels = 16;

    private static void Slab(List<MeshVertex> vertices, List<uint> indices, Vector3 centre, Vector3 half, float tag) =>
        Bar(vertices, indices, centre, 0, half.X, half.Y, half.Z, tag);

    // A box between two corners, in any order.
    private static void Span3(List<MeshVertex> vertices, List<uint> indices, Vector3 a, Vector3 b, float tag) =>
        Slab(vertices, indices, (a + b) * .5f, Vector3.Abs(b - a) * .5f, tag);

    /// <summary>
    /// A cargo rack: a closed box whose one open face is <c>box.Facing</c> (<see cref="OpenFaces"/>). Five walls of
    /// panels standing on a recessed backing that shows in the seams between them; round the opening, a band on the
    /// outer walls framed in the rim paint, with hazard stripes along the long edges, light strips along the short ones,
    /// accent plates at the corners and the walls' edges at the opening lit. Inside, rails run along the depth with
    /// guide lights at the opening, and a light strip crosses the back wall. Every part lies within the rack's share of
    /// the clearance (<see cref="RackLook.RackBudget"/>). A face a neighbour covers (<paramref name="closed"/>) is a
    /// plain wall; an open face that is covered or carries a mount is closed by one. No two faces of the model lie in
    /// one plane facing the same way, which would make them flicker against each other. Built only on model workers.
    /// </summary>
    public static void BuildRack(in PartBox box, RackLook look, CargoCatalog cargo, List<MeshVertex> vertices, List<uint> indices, int closed = 0, int mounted = 0)
    {
        var h = box.Half;
        var c = box.Center;
        var unit = cargo.UnitSize;
        var open = (int)box.Facing;
        var a = open >> 1;
        var s = (open & 1) == 0 ? 1f : -1f;
        var opening = ((closed | mounted) & (1 << open)) == 0;
        var budget = look.RackBudget * unit;
        var wall = MathF.Min(look.Wall * unit, budget);
        var skin = wall * .08f;
        // How far a wall keeps inside the block's faces: two skins, so that the faces of one wall that close it off
        // lie in planes of their own. A wall one skin inside ended in the same plane as the outside of the wall that
        // meets it in a corner, and the two flickered there.
        var edge = 2 * skin;
        var u = (a + 1) % 3;
        var v = (a + 2) % 3;
        // The opening's long edges run along `along`; the walls across it carry the hazard stripes.
        var along = h[u] >= h[v] ? u : v;
        var across = along == u ? v : u;
        var rim = opening ? MathF.Min(look.Rim * unit, h[a]) : 0;
        var lit = wall * .5f;
        var bed = wall * look.SeamBed;
        var wallTag = Paint(look.WallPaint);
        var seamTag = Paint(look.SeamPaint);
        var rimTag = Paint(look.RimPaint);
        var lightTag = Emissive(RackLook.Rgb(look.Light));
        var plane = c[a] + s * h[a];

        // A point on face f's plane, `depth` inward from the box's surface, at in-plane coordinates given on two axes.
        Vector3 On(int f, float depth, int p, float atP, int q, float atQ)
        {
            var b = f >> 1;
            var point = c;
            point[b] += ((f & 1) == 0 ? 1f : -1f) * (h[b] - depth);
            point[p] = atP; point[q] = atQ;
            return point;
        }

        for (var f = 0; f < 6; f++)
        {
            var b = f >> 1;
            if (f == open && opening) continue;
            var p = (b + 1) % 3;
            var q = (b + 2) % 3;
            // The wall spans its face short of the box's edges, and a side wall stops short of the lit edge at the
            // opening, so no two faces lie in one plane.
            var lo = c - h + new Vector3(edge);
            var hi = c + h - new Vector3(edge);
            if (b != a && opening)
            {
                if (s > 0) hi[a] = plane - lit; else lo[a] = plane + lit;
            }
            // A wall a neighbour covers is one plain slab, its inside flush with the walls that carry panels.
            if ((closed & 1 << f) != 0)
            {
                Span3(vertices, indices, On(f, skin, p, lo[p], q, lo[q]), On(f, wall, p, hi[p], q, hi[q]), seamTag);
                continue;
            }
            // The seam backing: the floor the seams show, a skin inside the panels' edges and ending on an inside face
            // of its own, so no face of it shares a plane with the panels, the band or the lit rim. Ended level with
            // the panels' insides, it lay in their plane and the two flickered in stripes wherever they overlapped.
            var bedLo = c - h + new Vector3(edge + skin);
            var bedHi = c + h - new Vector3(edge + skin);
            if (b != a && opening)
            {
                if (s > 0) bedHi[a] = plane - lit - skin; else bedLo[a] = plane + lit + skin;
            }
            Span3(vertices, indices, On(f, bed - skin, p, bedLo[p], q, bedLo[q]), On(f, bed, p, bedHi[p], q, bedHi[q]), seamTag);
            // The part of the face that is panels: all of it, less the rim band on a side wall.
            if (b != a && rim > 0)
            {
                if (s > 0) hi[a] = plane - rim; else lo[a] = plane + rim;
            }
            Panels(vertices, indices, look, unit, f, p, q, lo, hi, skin, wall, wallTag, On);
            if (b == a || rim <= 0) continue;

            // The rim band: framed, short of the lit edge in the opening's plane. It and its details stop short of the
            // wall's edges, clear of the faces of the walls that meet this one in a corner.
            var other = b == along ? across : along;
            var extent = h[other] - edge;
            var bandLo = plane - s * rim;
            var bandHi = plane - s * lit;
            var stripe = wall * .3f;
            Span3(vertices, indices, On(f, skin + stripe, a, bandLo, other, c[other] - extent), On(f, wall, a, bandHi, other, c[other] + extent), rimTag);
            // The walls' edges at the opening glow: the lit rim, which stops a wall's own skin short of the block's face
            // like every other part, so no face of the model lies in one of the block's own planes.
            Span3(vertices, indices, On(f, skin, a, plane - s * lit, other, c[other] - extent), On(f, wall, a, plane - s * edge, other, c[other] + extent), lightTag);
            var normal = Unit(b) * ((f & 1) == 0 ? 1f : -1f);
            var middle = (bandLo + bandHi) * .5f;
            var accent = MathF.Min(look.AccentSize * unit, MathF.Min(MathF.Abs(bandHi - bandLo), extent)) * .5f;
            if (b == across)
                HazardStripes(vertices, indices, look, unit, f, a, along, c[along] - extent + 2 * accent, c[along] + extent - 2 * accent, bandLo, bandHi, skin, stripe, normal, On);
            else
            {
                // A light strip along the short edge.
                var half = MathF.Min(look.LightWidth * unit, MathF.Abs(bandHi - bandLo) * .35f);
                var length = MathF.Min(extent * look.LightLength, extent - 2.5f * accent);
                Span3(vertices, indices, On(f, skin, a, middle - half, other, c[other] - length), On(f, skin + stripe, a, middle + half, other, c[other] + length), lightTag);
            }
            // Accent plates where the band meets the box's edges.
            for (var end = -1; end <= 1; end += 2)
            {
                var at = c[other] + end * (extent - accent);
                Span3(vertices, indices, On(f, skin, a, middle - accent, other, at - accent), On(f, skin + stripe, a, middle + accent, other, at + accent), Paint(look.Accent));
            }
        }

        if (!opening) return;
        // Inside: rails along the depth on the two walls across the opening, each with a guide light at the opening end.
        var rail = MathF.Min(look.Rail * unit, h[along] * .2f) * .5f;
        var railHeight = MathF.Min(look.RailHeight * unit, budget - wall);
        var back = c[a] - s * h[a];
        var light = MathF.Min(look.LightSize * unit, rim * .5f) * .5f;
        for (var side = 0; side < 2; side++)
        {
            var f = 2 * across + side;
            for (var k = -1; k <= 1; k += 2)
            {
                var at = c[along] + k * h[along] * .5f;
                // The rail stops where its guide light, as high as the rail, takes over at the opening.
                var lamp = plane - s * (lit + 2 * light);
                Span3(vertices, indices, On(f, wall, a, back + s * wall, along, at - rail), On(f, wall + railHeight, a, lamp, along, at + rail), Tag(look.RailShade));
                if (light > 0)
                    Span3(vertices, indices, On(f, wall, a, lamp, along, at - rail), On(f, wall + railHeight, a, plane - s * lit, along, at + rail), lightTag);
            }
        }
        // A light strip across the back wall's inside.
        var backFace = 2 * a + (s > 0 ? 1 : 0);
        var strip = MathF.Min(look.LightWidth * unit, h[across] * .2f);
        Span3(vertices, indices, On(backFace, wall, along, c[along] - h[along] * look.LightLength, across, c[across] - strip),
            On(backFace, wall + railHeight * .5f, along, c[along] + h[along] * look.LightLength, across, c[across] + strip), lightTag);
    }

    private delegate Vector3 FacePoint(int f, float depth, int p, float atP, int q, float atQ);

    // Wall panels over the box lo..hi on face f (in-plane axes p and q), from skin to the full wall depth, with seams
    // between them every few units that show the recessed backing through them.
    private static void Panels(List<MeshVertex> vertices, List<uint> indices, RackLook look, float unit, int f, int p, int q,
        Vector3 lo, Vector3 hi, float skin, float wall, float tag, FacePoint on)
    {
        var pitch = look.PanelEvery * unit;
        var seam = MathF.Min(look.Seam * unit, pitch * .3f) * .5f;
        var np = Math.Clamp((int)MathF.Round((hi[p] - lo[p]) / pitch), 1, MaxWallPanels);
        var nq = Math.Clamp((int)MathF.Round((hi[q] - lo[q]) / pitch), 1, MaxWallPanels);
        if (hi[p] - lo[p] <= 2 * seam || hi[q] - lo[q] <= 2 * seam) return;
        var sp = (hi[p] - lo[p]) / np;
        var sq = (hi[q] - lo[q]) / nq;
        for (var i = 0; i < np; i++)
            for (var j = 0; j < nq; j++)
                Span3(vertices, indices,
                    on(f, skin, p, lo[p] + i * sp + seam, q, lo[q] + j * sq + seam),
                    on(f, wall, p, lo[p] + (i + 1) * sp - seam, q, lo[q] + (j + 1) * sq - seam), tag);
    }

    // Diagonal hazard stripes in the rim band of face f: across the band (axis a, from bandLo to bandHi) and along the
    // edge (axis along, from start to end), raised from skin to skin + thick. Square-ended stripes are kept clear of
    // the band's edges by their own half width, so none pokes out of it.
    private static void HazardStripes(List<MeshVertex> vertices, List<uint> indices, RackLook look, float unit, int f, int a, int along,
        float start, float end, float bandLo, float bandHi, float skin, float thick, Vector3 normal, FacePoint on)
    {
        var width = look.HazardWidth * unit;
        var pitch = look.HazardPitch * unit;
        var depth = MathF.Abs(bandHi - bandLo);
        var margin = width * .75f;
        if (depth <= 2 * margin) return;
        var reach = depth - 2 * margin;
        var stripes = Math.Min((int)((end - start - reach - 2 * margin) / pitch) + 1, MaxHazardStripes);
        if (stripes <= 0) return;
        var used = (stripes - 1) * pitch + reach;
        var first = (start + end - used) * .5f;
        var toward = MathF.Sign(bandHi - bandLo);
        var tag = Paint(look.Hazard);
        for (var k = 0; k < stripes; k++)
        {
            var x = first + k * pitch;
            var from = on(f, skin + thick * .5f, a, bandLo + toward * margin, along, x);
            var to = on(f, skin + thick * .5f, a, bandHi - toward * margin, along, x + reach);
            BeamBetween(vertices, indices, from, to, normal, thick * .5f, width, tag);
        }
    }

    /// <summary>
    /// The mag-lock jaws holding one container of <paramref name="shape"/> (cargo units) in its slot, centred on the
    /// slot, for a rack whose back is <paramref name="back"/> (the face opposite its opening): four thin jaws grip the
    /// sides next to the back, in the container's half of the clearance. Their lights are a model of their own
    /// (<paramref name="lights"/>), lit by the stage the container is in (<see cref="World.CargoMotion"/>). An empty
    /// slot has none. Built only on model workers.
    /// </summary>
    public static void ClampModel(GridSize shape, BlockFacing back, RackLook look, CargoCatalog cargo, List<MeshVertex> vertices, List<uint> indices,
        List<MeshVertex>? lightVertices = null, List<uint>? lightIndices = null)
    {
        var unit = cargo.UnitSize;
        var slot = new Vector3(shape.X, shape.Y, shape.Z) * (unit * .5f);
        var inset = MathF.Min(slot.X, MathF.Min(slot.Y, slot.Z)) * look.Gap;
        var body = slot - new Vector3(inset);
        var axis = (int)back >> 1;
        var sign = ((int)back & 1) == 0 ? 1f : -1f;
        var jaw = MathF.Min(look.Jaw * unit, inset * RackLook.DetailShare);
        var height = MathF.Max(look.JawHeight * 2 * body[axis], jaw);
        var light = MathF.Min(look.LightSize * unit * .5f, height * .25f);
        var tag = Tag(look.JawShade);
        var lit = Emissive(RackLook.Rgb(look.Light));
        for (var k = 1; k <= 2; k++)
        {
            var w = (axis + k) % 3;
            var other = (axis + 3 - k) % 3;
            for (var side = -1; side <= 1; side += 2)
            {
                var centre = Vector3.Zero;
                centre[w] = side * (body[w] + jaw * .5f);
                // From the back's side of the container, no further out than its share of the clearance, along its side.
                centre[axis] = sign * (body[axis] + jaw - height * .5f);
                var half = Vector3.Zero;
                half[w] = jaw * .5f; half[axis] = height * .5f; half[other] = look.JawWidth * body[other];
                Slab(vertices, indices, centre, half, tag);
                centre[axis] = sign * (body[axis] + jaw - height - light);
                half = new Vector3(light);
                half[w] = MathF.Min(light, jaw * .5f);
                if (lightVertices is not null && lightIndices is not null) Slab(lightVertices, lightIndices, centre, half, lit);
                else Slab(vertices, indices, centre, half, lit);
            }
        }
    }

    /// <summary>A container of <paramref name="size"/> with no resource, at a packed slot.</summary>
    public static void ContainerModel(in PartBox box, int size, RackLook look, List<MeshVertex> vertices, List<uint> indices) =>
        ContainerModel(box, size, -1, look, vertices, indices);

    /// <summary>
    /// A cargo container at a packed slot, long along X: a dark body chamfered along its long edges, orange corner caps
    /// (its locking corners), and on both long sides a panel with the size in units ("16u") large, a plate in the
    /// resource's colour with its name (its code when the name does not fit; EMPTY for none) and hazard stripes, light
    /// strips above and below the panel, three light bars on each end and one along the top. Every detail stands off
    /// the body by at most the container's share of the clearance (<see cref="RackLook.DetailShare"/>), so nothing
    /// reaches a neighbour or the rack. One model for racks and for free containers; built only on workers.
    /// </summary>
    public static void ContainerModel(in PartBox box, int size, int resource, RackLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var slot = box.Half;
        var inset = MathF.Min(slot.X, MathF.Min(slot.Y, slot.Z)) * look.Gap;
        var h = slot - new Vector3(inset);
        var centre = box.Center;
        var m = MathF.Min(h.X, MathF.Min(h.Y, h.Z));
        var pro = inset * RackLook.DetailShare;
        var res = (uint)resource < (uint)look.Galaxy.Length ? look.Galaxy[resource] : null;
        var plain = new Vector3(look.Body[0], look.Body[1], look.Body[2]);
        var tinted = res is null ? plain : Vector3.Lerp(plain, res.Color, look.BodyTint);
        Span<float> rgb = stackalloc float[3];
        rgb[0] = tinted.X; rgb[1] = tinted.Y; rgb[2] = tinted.Z;
        var bodyTag = Paint(rgb);

        // The body: an octagonal prism along X, its long edges chamfered.
        var chamfer = look.Chamfer * m;
        var g = new Geometry(vertices, indices, new Frame(centre, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, h.X));
        Span<Vector2> outline = stackalloc Vector2[8];
        Octagon(outline, h.Y, h.Z, chamfer);
        g.Loft(outline, 0, outline, 2 * h.X, Vector2.Zero, bodyTag, 1);
        g.Cap(outline, Vector2.Zero, 0, -1, bodyTag);
        g.Cap(outline, Vector2.Zero, 2 * h.X, 1, bodyTag);

        // Corner caps: the locking corners a container is held and stacked by, a little longer along its length.
        var cap = look.Corner * m;
        var lift = MathF.Min(cap * look.CornerLift, pro);
        var accent = Paint(look.Accent);
        var capHalf = new Vector3(MathF.Min(cap * 1.4f, h.X * .45f), cap, cap);
        for (var i = 0; i < 8; i++)
        {
            var sign = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            Slab(vertices, indices, centre + sign * (h - capHalf + new Vector3(lift)), capHalf, accent);
        }

        var lightTag = Emissive(RackLook.Rgb(look.Light));
        var strip = pro * .5f;
        var band = look.StripBand * m;
        // The panel on each long side, between the corner caps and inside the strip bands.
        var panelDepth = MathF.Max(inset * look.PanelDepth, 1e-4f);
        var plateDepth = MathF.Max(inset * look.PlateDepth, 1e-4f);
        var panelHalf = new Vector2(MathF.Max(h.X - 2.2f * capHalf.X, h.X * .2f), MathF.Max(h.Y - chamfer - band, h.Y * .3f));
        var units = look.Cargo is { } cargo && (uint)size < (uint)cargo.Sizes.Length ? cargo.Sizes[size].Units : 0;
        var label = units > 0 ? units + "u" : "";
        var plateColour = res is null ? look.EmptyPlate : new[] { res.Color.X, res.Color.Y, res.Color.Z };
        var name = res is null ? "EMPTY" : res.Name.ToUpperInvariant();
        var hazard = Paint(look.Hazard);
        var wide = panelHalf.X >= 1.6f * panelHalf.Y;
        for (var side = -1; side <= 1; side += 2)
        {
            var normal = new Vector3(0, 0, side);
            var right = new Vector3(side, 0, 0);
            var up = Vector3.UnitY;
            var plane = centre + normal * h.Z;
            Plate(vertices, indices, plane, panelHalf, normal, right, up, panelDepth, Paint(look.Panel));
            var top = plane + normal * panelDepth;

            // Light strips above and below the panel.
            for (var row = -1; row <= 1; row += 2)
                Plate(vertices, indices, plane + up * (row * (panelHalf.Y + band * .5f)), new Vector2(h.X * look.StripLength, band * .2f),
                    normal, right, up, strip, lightTag);

            Vector3 sizeAt, plateAt, hazardAt;
            Vector2 sizeRoom, plateHalf, hazardHalf;
            if (wide)
            {
                // Long: the size on the left third, the plate on the right with hazard stripes under it.
                sizeAt = top - right * (panelHalf.X * .52f);
                sizeRoom = new Vector2(panelHalf.X * .4f, panelHalf.Y * .75f);
                plateAt = top + right * (panelHalf.X * .42f) + up * (panelHalf.Y * .22f);
                plateHalf = new Vector2(panelHalf.X * .52f, panelHalf.Y * .52f);
                hazardAt = top + right * (panelHalf.X * .42f) - up * (panelHalf.Y * .68f);
                hazardHalf = new Vector2(panelHalf.X * .5f, panelHalf.Y * .22f);
            }
            else
            {
                // Square: the size above, the plate below it, hazard stripes along the foot.
                sizeAt = top + up * (panelHalf.Y * .42f);
                sizeRoom = new Vector2(panelHalf.X * .8f, panelHalf.Y * .48f);
                plateAt = top - up * (panelHalf.Y * .38f);
                plateHalf = new Vector2(panelHalf.X * .9f, panelHalf.Y * .3f);
                hazardAt = top - up * (panelHalf.Y * .86f);
                hazardHalf = new Vector2(panelHalf.X * .85f, panelHalf.Y * .1f);
            }
            Label(vertices, indices, label, sizeAt, sizeRoom, normal, right, up, panelDepth * .5f, look.Stroke * 1.25f, Paint(look.SizeText));
            Plate(vertices, indices, plateAt, plateHalf, normal, right, up, plateDepth, Paint(plateColour));
            // The name when it can be read at the plate's height, else the code.
            var text = res is null || Fit(name, plateHalf) >= .35f * Fit(res.Code, plateHalf) ? name : res.Code;
            Label(vertices, indices, text, plateAt, plateHalf * .82f, normal, right, up, plateDepth, look.Stroke, Tag(look.TextShade));
            Stripes(vertices, indices, hazardAt, hazardHalf, normal, right, up, plateDepth, hazard);
        }

        // Three light bars across each end, and one along the top.
        for (var end = -1; end <= 1; end += 2)
        {
            var normal = new Vector3(end, 0, 0);
            var plane = centre + normal * h.X;
            for (var bar = -1; bar <= 1; bar++)
                Plate(vertices, indices, plane + Vector3.UnitY * (bar * h.Y * .18f), new Vector2(h.Z * .32f, h.Y * .045f),
                    normal, new Vector3(0, 0, -end), Vector3.UnitY, strip, lightTag);
        }
        Plate(vertices, indices, centre + Vector3.UnitY * h.Y, new Vector2(h.X * look.StripLength * .8f, MathF.Max(h.Z - chamfer, h.Z * .3f) * .12f),
            Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, strip, lightTag);
    }

    // The glyph scale a text is written at in a room (half width, half height), as Label fits it.
    private static float Fit(string text, Vector2 room) => text.Length == 0 ? 0
        : MathF.Min(room.Y * 2 / StrokeFont.Height, room.X * 2 / (text.Length * StrokeFont.Width + (text.Length - 1) * 1.2f));

    // Hazard stripes in a box on a side face: diagonal bars of the given half extents, raised by depth.
    private static void Stripes(List<MeshVertex> vertices, List<uint> indices, Vector3 on, Vector2 half, Vector3 normal, Vector3 right, Vector3 up, float depth, float tag)
    {
        var width = half.Y * .45f;
        var margin = width * .75f;
        var reach = 2 * half.Y - 2 * margin;
        if (reach <= 0) return;
        var pitch = width * 3.2f;
        var count = Math.Min((int)((2 * half.X - reach - 2 * margin) / pitch) + 1, MaxHazardStripes);
        if (count <= 0) return;
        var used = (count - 1) * pitch + reach;
        var lift = normal * (depth * .5f);
        for (var k = 0; k < count; k++)
        {
            var x = -used * .5f + k * pitch;
            var from = on + right * x - up * (half.Y - margin) + lift;
            var to = on + right * (x + reach) + up * (half.Y - margin) + lift;
            BeamBetween(vertices, indices, from, to, normal, depth * .5f, width, tag);
        }
    }

    // A thin slab lying on a side face: half width and height across it, standing `depth` off it.
    private static void Plate(List<MeshVertex> vertices, List<uint> indices, Vector3 on, Vector2 half, Vector3 normal, Vector3 right, Vector3 up, float depth, float tag)
    {
        // From half a depth inside the face to one depth out of it.
        var centre = on + normal * (depth * .25f);
        var extent = Vector3.Abs(right) * half.X + Vector3.Abs(up) * half.Y + Vector3.Abs(normal) * (depth * .75f);
        Slab(vertices, indices, centre, extent, tag);
    }

    // Text as raised strokes on a plate: fitted into <paramref name="room"/> (half width, half height) about <paramref name="on"/>.
    private static void Label(List<MeshVertex> vertices, List<uint> indices, string text, Vector3 on, Vector2 room, Vector3 normal, Vector3 right, Vector3 up,
        float plateDepth, float stroke, float tag)
    {
        if (text.Length == 0) return;
        const float space = 1.2f;
        var unit = MathF.Min(room.Y * 2 / StrokeFont.Height, room.X * 2 / (text.Length * StrokeFont.Width + (text.Length - 1) * space));
        var half = unit * StrokeFont.Height * stroke * .5f;
        var total = text.Length * StrokeFont.Width + (text.Length - 1) * space;
        // Strokes stand on the plate's top (one depth out) and rise half a depth more.
        var raised = plateDepth * .25f;
        var origin = on - right * (total * unit * .5f) - up * (StrokeFont.Height * unit * .5f) + normal * (plateDepth + raised);
        for (var k = 0; k < text.Length; k++)
        {
            var left = k * (StrokeFont.Width + space);
            foreach (var line in StrokeFont.Of(text[k]).Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                Vector3 previous = default;
                var first = true;
                foreach (var point in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = origin + right * ((left + (point[0] - '0')) * unit) + up * ((point[1] - '0') * unit);
                    if (!first)
                    {
                        var direction = Vector3.Normalize(p - previous);
                        BeamBetween(vertices, indices, previous - direction * half, p + direction * half, normal, raised, half, tag);
                    }
                    previous = p;
                    first = false;
                }
            }
        }
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
            var clamp = new List<MeshVertex>();
            var clampIndices = new List<uint>();
            ClampModel(s, Facings.Opposite(box.Facing), look, cargo, clamp, clampIndices);
            foreach (var i in clampIndices) { var vertex = clamp[(int)i]; vertex.Position = new float3(vertex.Position.X + at.X, vertex.Position.Y + at.Y, vertex.Position.Z + at.Z); vertices.Add(vertex); indices.Add((uint)vertices.Count - 1); }
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
