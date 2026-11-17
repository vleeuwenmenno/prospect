using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    // How far the lit lens stands off the dark disc behind it, in base radii.
    private const float LaserLensLift = 0.012f;

    /// <summary>How much wider than the head's body its hood is: the widest thing between the yoke's arms.</summary>
    public const float LaserHoodWidth = 1.05f;

    /// <summary>The look of a mounted mining laser of <paramref name="type"/>, or null for a part that is none.</summary>
    public static LaserLook? LaserOf(BlockType type, PropulsionLook look) =>
        type.Mount is not null && type.Laser is not null ? look.Laser : null;

    /// <summary>Distance from the trunnion along the aimed axis to the lens the beam leaves from.</summary>
    public static float LaserLensOffset(float small, LaserLook look) =>
        small * look.BaseRadius * (look.HeadFront + look.BarrelLength - look.LensRecess + LaserLensLift);

    /// <summary>
    /// Builds the part of a mounted mining laser that the hull mesh holds: the turret's base ring flush on the block
    /// face (<see cref="BuildTurretBase"/>), its bearing collar banded in the colour of the laser's tier. The yoke,
    /// the head and the lens are parts of their own, posed each frame. A pure function of the box, the look and the
    /// type, built on a worker.
    /// </summary>
    public static void BuildLaser(LaserLook look, BlockType type, in PartBox box, List<MeshVertex> vertices, List<uint> indices)
    {
        var tier = type.Laser is { } spec ? ModuleLook.Current.Tier(spec.Quality).Rgb : look.BodyColor;
        BuildTurretBase(box, look.Turret, PaintRgb(tier), vertices, indices);
    }

    /// <summary>
    /// Builds the pitching head in its own space (origin at the trunnion, +Z the way it aims, +X the pitch axis, -Y its
    /// top): a chunky chamfered body tapering to its back with accent panels over its top, an octagonal barrel with a
    /// light strip down each side and an accent band, and a muzzle ring round the recess the lens sits in. The lens
    /// itself is <see cref="BuildLaserLens"/>.
    /// </summary>
    public static void BuildLaserHead(float small, LaserLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var r = small * look.BaseRadius;
        var hw = r * look.HeadWidth;
        var hh = r * look.HeadHeight;
        var cut = 0.34f * hh;
        var body = PaintRgb(look.BodyColor);
        var dark = PaintRgb(look.DarkColor);
        var accent = PaintRgb(look.AccentColor);
        var light = Emissive(look.LightColor);

        Span<Vector2> rear = stackalloc Vector2[8];
        Span<Vector2> full = stackalloc Vector2[8];
        Span<Vector2> hood = stackalloc Vector2[8];
        Span<Vector2> nose = stackalloc Vector2[8];
        Octagon(rear, hw * 0.8f, hh * 0.72f, cut * 0.7f);
        Octagon(full, hw, hh, cut);
        Octagon(hood, hw * LaserHoodWidth, hh * 1.07f, cut * 1.05f);
        Octagon(nose, hw * 0.74f, hh * 0.74f, cut * 0.74f);

        var zBack = -r * look.HeadBack;
        var zFront = r * look.HeadFront;
        var span = zFront - zBack;
        var zRear = zBack + 0.26f * span;
        var zHood = zBack + 0.4f * span;
        var zShoulder = zBack + 0.8f * span;

        g.Cap(rear, Vector2.Zero, zBack, -1f, dark);
        g.Loft(rear, zBack, full, zRear, Vector2.Zero, dark, 1f);
        g.Loft(full, zRear, full, zHood, Vector2.Zero, body, 1f);
        // The hood: a band standing a little proud, accent paint over the top and its two chamfers (the top is -Y).
        Span<float> band = [dark, dark, dark, dark, accent, accent, accent, dark];
        g.Annulus(hood, full, Vector2.Zero, zHood, -1f, dark);
        g.Loft(hood, zHood, hood, zShoulder, Vector2.Zero, band, 1f);
        g.Annulus(hood, full, Vector2.Zero, zShoulder, 1f, dark);
        g.Loft(full, zShoulder, nose, zFront, Vector2.Zero, body, 1f);
        g.Cap(nose, Vector2.Zero, zFront, 1f, dark);

        // The barrel: eight flats, the top one (side 5) and its neighbours banded near the muzzle, a light strip on
        // each side (sides 3 and 7).
        var br = r * look.BarrelRadius;
        var mr = r * look.MuzzleRadius;
        var zMuzzle = zFront + r * (look.BarrelLength - look.MuzzleLength);
        var zEnd = zFront + r * look.BarrelLength;
        var zLens = zEnd - r * look.LensRecess;
        var zBand = zFront + 0.5f * (zMuzzle - zFront);
        const float turn = MathF.PI / 8f;
        Span<Vector2> barrel = stackalloc Vector2[8];
        Span<Vector2> sleeve = stackalloc Vector2[8];
        Span<Vector2> muzzle = stackalloc Vector2[8];
        Span<Vector2> lip = stackalloc Vector2[8];
        Span<Vector2> mouth = stackalloc Vector2[8];
        Polygon(barrel, Vector2.Zero, br, turn);
        Polygon(sleeve, Vector2.Zero, br * 1.1f, turn);
        Polygon(muzzle, Vector2.Zero, mr, turn);
        Polygon(lip, Vector2.Zero, mr * 0.92f, turn);
        Polygon(mouth, Vector2.Zero, mr * 0.8f, turn);
        g.Loft(barrel, zFront, barrel, zBand, Vector2.Zero, dark, 1f);
        Span<float> sleeveBand = [body, body, body, body, accent, accent, accent, body];
        g.Annulus(sleeve, barrel, Vector2.Zero, zBand, -1f, dark);
        g.Loft(sleeve, zBand, sleeve, zMuzzle, Vector2.Zero, sleeveBand, 1f);
        var inside = new Vector3(0f, 0f, 0.5f * (zFront + zMuzzle));
        var lift = r * 0.01f;
        for (var k = 3; k <= 7; k += 4)
        {
            var a = barrel[k];
            var b = barrel[(k + 1) % 8];
            g.Panel(new Vector3(a, zFront), new Vector3(b, zFront), new Vector3(b, zBand), new Vector3(a, zBand), 0.22f, 0.78f, 0.18f, 0.86f, inside, lift, light);
        }

        // The muzzle: a heavier ring, chamfered to its front, round the recess the lens glows in.
        var zLip = zEnd - 0.3f * (zEnd - zMuzzle);
        g.Annulus(muzzle, sleeve, Vector2.Zero, zMuzzle, -1f, dark);
        g.Loft(muzzle, zMuzzle, muzzle, zLip, Vector2.Zero, dark, 1f);
        g.Loft(muzzle, zLip, lip, zEnd, Vector2.Zero, body, 1f);
        g.Annulus(lip, mouth, Vector2.Zero, zEnd, 1f, body);
        g.Loft(mouth, zLens, mouth, zEnd, Vector2.Zero, dark, -1f);
        g.Cap(mouth, Vector2.Zero, zLens, 1f, Tag(0.02f));

        // Trunnion bosses fill the gap to the yoke's arms.
        Span<Vector2> boss = stackalloc Vector2[8];
        Polygon(boss, Vector2.Zero, r * look.YokeHub * 0.7f, turn);
        var reach = MathF.Max(r * (look.YokeSpread - look.YokeArm) - hw, 0f);
        for (var side = -1; side <= 1; side += 2)
        {
            var cap = new Geometry(vertices, indices, new Frame(new Vector3(side * hw, 0f, 0f), Vector3.UnitX * side, Vector3.UnitY, Vector3.UnitZ, 0f));
            cap.Loft(boss, 0f, boss, reach, Vector2.Zero, dark, 1f);
        }
    }

    /// <summary>
    /// Builds the lens in the head's space: a disc standing a little off the dark face inside the muzzle. A part of
    /// its own, because it is drawn as light whose brightness follows the beam and the laser's heat; its front is
    /// where the beam starts (<see cref="LaserLensOffset"/>).
    /// </summary>
    public static void BuildLaserLens(float small, LaserLook look, List<MeshVertex> vertices, List<uint> indices)
    {
        var g = new Geometry(vertices, indices, new Frame(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f));
        var r = small * look.BaseRadius;
        var zLens = r * (look.HeadFront + look.BarrelLength - look.LensRecess);
        var z = zLens + r * LaserLensLift;
        Span<Vector2> lens = stackalloc Vector2[8];
        Polygon(lens, Vector2.Zero, r * look.LensRadius, MathF.PI / 8f);
        g.Loft(lens, zLens, lens, z, Vector2.Zero, 1f, 1f);
        g.Cap(lens, Vector2.Zero, z, 1f, 1f);
    }
}
