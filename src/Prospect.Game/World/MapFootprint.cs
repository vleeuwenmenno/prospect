using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The outlines the galaxy map draws for what a chart shows of the natural world: where an asteroid field's rocks
/// can lie and where a gas cloud's envelope stands, seen from above at one layer. Pure arithmetic on the shared
/// generation data, so every client draws the same shapes; nothing here places a rock or reads a noise field.
/// Vertices are metres in the galaxy's horizontal plane (x and z) relative to an origin the caller names, so a
/// far sector keeps its precision in single floats. Each function writes at most <see cref="MaxVertices"/> points,
/// as a closed polygon, and returns how many.
/// </summary>
public static class MapFootprint
{
    /// <summary>How many points along a field's length each side of its outline is sampled at.</summary>
    public const int FieldSamples = 10;

    /// <summary>The vertices of a field's outline: both sides, the pointed ends shared.</summary>
    public const int FieldVertices = 2 * FieldSamples;

    /// <summary>The vertices of a cloud's outline.</summary>
    public const int CloudVertices = 24;

    /// <summary>The most vertices any outline here has.</summary>
    public const int MaxVertices = 24;

    /// <summary>
    /// The outline of a field's ribbon, the way <c>AsteroidFieldsModule</c> lays its rocks along it: the centre line
    /// and width are smoothstepped between the four sections, and the width tapers to a point at both ends.
    /// <paramref name="widthShare"/> narrows the ribbon about its centre line: 1 is the whole support, less is the
    /// denser core, since rocks fade out toward the edge.
    /// </summary>
    public static int Field(in ChartField field, float widthShare, Double3 origin, Span<Vector2> into)
    {
        if (into.Length < FieldVertices)
            throw new ArgumentException("An outline needs room for its vertices.", nameof(into));
        var length = field.HalfExtents.X;
        var width = field.HalfExtents.Z;
        var alongX = field.Along.X;
        var alongZ = field.Along.Z;
        var cx = (float)(field.Center.X - origin.X);
        var cz = (float)(field.Center.Z - origin.Z);
        var contour = field.Contour;
        var count = 0;
        for (var side = 0; side < 2; side++)
        {
            // One side runs from the first end to the last, the other back again; the two ends are one point.
            var first = side == 0 ? 0 : FieldSamples - 1;
            var last = side == 0 ? FieldSamples : 1;
            var step = side == 0 ? 1 : -1;
            for (var i = first; step > 0 ? i <= last : i >= last; i += step)
            {
                var t = i * ((FieldContour.Knots - 1) / (float)FieldSamples);
                var k = Math.Min((int)t, FieldContour.Knots - 2);
                var fraction = t - k;
                fraction = fraction * fraction * (3f - 2f * fraction);
                var centerZ = contour.CenterAt(k) + (contour.CenterAt(k + 1) - contour.CenterAt(k)) * fraction;
                var knotWidth = contour.WidthAt(k) + (contour.WidthAt(k + 1) - contour.WidthAt(k)) * fraction;
                var x = t * (2f / (FieldContour.Knots - 1)) - 1f;
                var x2 = x * x;
                var taper = 1f - x2 * x2;
                var z = centerZ + (side == 0 ? -1f : 1f) * knotWidth * taper * widthShare;
                into[count++] = new Vector2(cx + alongX * (x * length) - alongZ * (z * width), cz + alongZ * (x * length) + alongX * (z * width));
            }
        }

        return count;
    }

    /// <summary>
    /// The outline of a gas cloud's envelope across the layer whose slab runs from <paramref name="slabLow"/> to
    /// <paramref name="slabHigh"/> in y: an ellipse of the envelope's share of the cloud's box, as wide as the
    /// envelope is where the slab comes nearest the cloud's centre. Returns 0 where the slab misses the envelope.
    /// The map's lumps are a seeded bulge, not the renderer's noise, which only the renderer holds; the size and
    /// the fill share are the cloud's own. <paramref name="share"/> is that share of the box: the content's fill
    /// for the body, a larger one for the thin fringe round it.
    /// </summary>
    public static int Cloud(in GasCloud cloud, Double3 anchorCenter, float share, float lumpiness, double slabLow, double slabHigh, Double3 origin, Span<Vector2> into)
    {
        if (into.Length < CloudVertices)
            throw new ArgumentException("An outline needs room for its vertices.", nameof(into));
        var centerY = anchorCenter.Y + cloud.Center.Y;
        var nearest = centerY < slabLow ? slabLow - centerY : centerY > slabHigh ? centerY - slabHigh : 0.0;
        var vertical = cloud.HalfSize.Y * share;
        if (!(vertical > 0f) || nearest >= vertical)
            return 0;
        var ratio = (float)(nearest / vertical);
        var cut = MathF.Sqrt(1f - ratio * ratio);
        var radiusX = cloud.HalfSize.X * share * cut;
        var radiusZ = cloud.HalfSize.Z * share * cut;
        var cx = (float)(anchorCenter.X + cloud.Center.X - origin.X);
        var cz = (float)(anchorCenter.Z + cloud.Center.Z - origin.Z);

        // A lopsided edge: two seeded harmonics, small enough that the outline stays convex and inside the box.
        var bulge = Math.Clamp(lumpiness, 0f, 4f) * 0.04f;
        var phaseTwo = cloud.Seed * 1.7f;
        var phaseThree = cloud.Seed * 2.9f;
        for (var i = 0; i < CloudVertices; i++)
        {
            var angle = i * (MathF.Tau / CloudVertices);
            var lump = 1f + bulge * (0.6f * DetMath.Sin(2f * angle + phaseTwo) + 0.4f * DetMath.Sin(3f * angle + phaseThree));
            into[i] = new Vector2(cx + radiusX * lump * DetMath.Cos(angle), cz + radiusZ * lump * DetMath.Sin(angle));
        }

        return CloudVertices;
    }

    /// <summary>The share of a cloud's box its fringe reaches: where the swollen envelope ends, as the renderer's fringe does.</summary>
    public static float FringeShare(float fill) => Math.Min(1f, fill / MathF.Sqrt(GasCloudModule.MinSwell));
}
