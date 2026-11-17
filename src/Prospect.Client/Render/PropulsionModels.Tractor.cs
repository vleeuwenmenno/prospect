using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    // Shares of the head's length that its two parts must agree on.
    private const float TurretHeadBack = 0.4f;
    private const float TurretHeadFront = 0.6f;
    private const float TurretLensRecess = 0.07f;
    private const float TurretBarLift = 0.015f;

    /// <summary>Total height from the block face to the trunnion pivot of a tractor turret of half-width <paramref name="small"/>.</summary>
    public static float TractorPivotOffset(float small, TractorLook tractor) => tractor.Turret.PivotOffset(small);

    /// <summary>Height from the block face to the top of the bearing collar where the yoke sits.</summary>
    public static float TractorCollarOffset(float small, TractorLook tractor) => tractor.Turret.CollarOffset(small);

    /// <summary>Distance from the trunnion pivot along the aimed axis to the glowing bars of the emitter face.</summary>
    public static float TractorLensOffset(float small, TractorLook tractor) =>
        small * tractor.TurretBaseRadius * tractor.TurretHeadLength * (TurretHeadFront - TurretLensRecess + TurretBarLift);

    /// <summary>
    /// Builds the tractor turret's base (<see cref="BuildTurretBase"/>), its bearing collar banded in the colour of the
    /// tractor's tier (<c>modules.json</c>, as on the editor card).
    /// </summary>
    public static void BuildTractorBase(in PartBox box, TractorLook tractor, TractorSpec spec, List<MeshVertex> vertices, List<uint> indices) =>
        BuildTurretBase(box, tractor.Turret, PaintRgb(ModuleLook.Current.Tier(spec.Quality).Rgb), vertices, indices);

    /// <summary>Builds the tractor turret's yoke (<see cref="BuildTurretYoke"/>).</summary>
    public static void BuildTractorYoke(float small, TractorLook tractor, List<MeshVertex> vertices, List<uint> indices) =>
        BuildTurretYoke(small, tractor.Turret, vertices, indices);

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
