using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>One straight piece of a merged sensor outline, on the horizontal plane (x, z in world metres).</summary>
internal struct OutlineSegment
{
    public double AX, AZ, BX, BZ;

    /// <summary>The unit normal pointing out of the covered region.</summary>
    public float NX, NZ;

    /// <summary>The reach of the sensor this piece belongs to there, which sets how far the fade outside reaches.</summary>
    public float Reach;
}

/// <summary>
/// Geometry of sensor coverage on a plane. A sensor's region is star-shaped round its centre: <see cref="SensorCoverage.ReachSamples"/>
/// reach distances at evenly spaced angles from +x towards +z, joined by straight chords. Everything here is plain
/// arithmetic over caller-owned memory: it runs only when a selection, a sensor or its gas changes, never allocates,
/// and its output is bounded by the span it is given.
/// </summary>
internal static class SensorOutline
{
    /// <summary>The most regions one build takes; more are ignored.</summary>
    public const int MaxRings = 64;

    private const int MaxCuts = 8;
    private static readonly double[] Cos = Table(Math.Cos);
    private static readonly double[] Sin = Table(Math.Sin);

    private static double[] Table(Func<double, double> function)
    {
        var table = new double[SensorCoverage.ReachSamples];
        for (var k = 0; k < table.Length; k++)
            table[k] = function(k * (Math.Tau / table.Length));
        return table;
    }

    /// <summary>The unit direction of sample <paramref name="k"/> on the horizontal plane.</summary>
    public static Double3 Direction(int k) => new(Cos[k], 0, Sin[k]);

    /// <summary>
    /// True when a point lies inside the region round (<paramref name="cx"/>, <paramref name="cz"/>) whose boundary
    /// is the chords through <paramref name="samples"/>. A point within <paramref name="slack"/> metres outside the
    /// boundary also counts as inside (a negative slack demands that much depth).
    /// </summary>
    public static bool Inside(double cx, double cz, ReadOnlySpan<double> samples, double px, double pz, double slack)
    {
        var n = samples.Length;
        var dx = px - cx;
        var dz = pz - cz;
        var angle = Math.Atan2(dz, dx);
        if (angle < 0)
            angle += Math.Tau;
        var k = (int)(angle * (n / Math.Tau));
        if (k >= n)
            k = n - 1;
        var next = k + 1 == n ? 0 : k + 1;
        var ax = samples[k] * Cos[k];
        var az = samples[k] * Sin[k];
        var ex = samples[next] * Cos[next] - ax;
        var ez = samples[next] * Sin[next] - az;
        var length = Math.Sqrt(ex * ex + ez * ez);
        if (length < 1e-9)
            return false;
        // Counter-clockwise chords: the inside is to the left of each.
        return (ex * (dz - az) - ez * (dx - ax)) / length >= -slack;
    }

    /// <summary>
    /// The outline of the union of every group's regions: the parts of each region's boundary that no other region
    /// of its group covers, so overlapping regions give one outline with no line inside it, a region within another
    /// gives only the outer one, and regions apart give a loop each. Regions in different groups are never merged.
    /// <paramref name="samples"/> holds <paramref name="count"/> rings of reach distances; <paramref name="group"/>
    /// names each ring's group (empty: all one group). Returns how many segments were written; output beyond the
    /// span's length is dropped.
    /// </summary>
    public static int Build(ReadOnlySpan<double> x, ReadOnlySpan<double> z, ReadOnlySpan<double> samples, ReadOnlySpan<int> group, int count, Span<OutlineSegment> output)
    {
        const int n = SensorCoverage.ReachSamples;
        count = Math.Min(Math.Min(count, MaxRings), Math.Min(x.Length, z.Length));
        Span<double> widest = stackalloc double[MaxRings];
        for (var i = 0; i < count; i++)
        {
            var max = 0.0;
            for (var k = 0; k < n; k++)
                max = Math.Max(max, samples[i * n + k]);
            widest[i] = max;
        }

        Span<double> cuts = stackalloc double[MaxCuts + 2];
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            if (!(widest[i] > 0))
                continue;
            var ring = samples.Slice(i * n, n);
            for (var k = 0; k < n; k++)
            {
                var next = k + 1 == n ? 0 : k + 1;
                var ax = x[i] + ring[k] * Cos[k];
                var az = z[i] + ring[k] * Sin[k];
                var rx = x[i] + ring[next] * Cos[next] - ax;
                var rz = z[i] + ring[next] * Sin[next] - az;
                var length = Math.Sqrt(rx * rx + rz * rz);
                if (length < 1e-9)
                    continue;

                var cutCount = 0;
                cuts[cutCount++] = 0;
                for (var j = 0; j < count; j++)
                {
                    if (j == i || !(widest[j] > 0) || (group.Length > 0 && group[j] != group[i]))
                        continue;
                    if (!Near(ax, az, rx, rz, x[j], z[j], widest[j]))
                        continue;
                    var other = samples.Slice(j * n, n);
                    for (var m = 0; m < n && cutCount < MaxCuts + 1; m++)
                    {
                        var mn = m + 1 == n ? 0 : m + 1;
                        var cx = x[j] + other[m] * Cos[m];
                        var cz = z[j] + other[m] * Sin[m];
                        var sx = x[j] + other[mn] * Cos[mn] - cx;
                        var sz = z[j] + other[mn] * Sin[mn] - cz;
                        var denominator = rx * sz - rz * sx;
                        if (Math.Abs(denominator) < 1e-12 * length * Math.Sqrt(sx * sx + sz * sz))
                            continue;
                        var t = ((cx - ax) * sz - (cz - az) * sx) / denominator;
                        var u = ((cx - ax) * rz - (cz - az) * rx) / denominator;
                        if (t <= 1e-9 || t >= 1 - 1e-9 || u < 0 || u >= 1)
                            continue;
                        var at = cutCount++;
                        while (at > 1 && cuts[at - 1] > t)
                        {
                            cuts[at] = cuts[at - 1];
                            at--;
                        }

                        cuts[at] = t;
                    }
                }

                cuts[cutCount++] = 1;
                for (var c = 0; c + 1 < cutCount; c++)
                {
                    if (cuts[c + 1] - cuts[c] < 1e-9)
                        continue;
                    var middle = (cuts[c] + cuts[c + 1]) * 0.5;
                    var mx = ax + rx * middle;
                    var mz = az + rz * middle;
                    var covered = false;
                    for (var j = 0; j < count && !covered; j++)
                    {
                        if (j == i || !(widest[j] > 0) || (group.Length > 0 && group[j] != group[i]))
                            continue;
                        var dx = mx - x[j];
                        var dz = mz - z[j];
                        if (dx * dx + dz * dz > widest[j] * widest[j])
                            continue;
                        // A boundary two regions share is kept by the lower ring only, so it is drawn once.
                        covered = Inside(x[j], z[j], samples.Slice(j * n, n), mx, mz, j < i ? 1e-4 : -1e-4);
                    }

                    if (covered)
                        continue;
                    if (written == output.Length)
                        return written;
                    output[written++] = new OutlineSegment
                    {
                        AX = ax + rx * cuts[c],
                        AZ = az + rz * cuts[c],
                        BX = ax + rx * cuts[c + 1],
                        BZ = az + rz * cuts[c + 1],
                        NX = (float)(rz / length),
                        NZ = (float)(-rx / length),
                        Reach = (float)((ring[k] + ring[next]) * 0.5),
                    };
                }
            }
        }

        return written;
    }

    /// <summary>Whether a segment from (ax, az) along (rx, rz) passes within <paramref name="radius"/> of a point.</summary>
    private static bool Near(double ax, double az, double rx, double rz, double px, double pz, double radius)
    {
        var lengthSquared = rx * rx + rz * rz;
        var t = lengthSquared > 0 ? Math.Clamp(((px - ax) * rx + (pz - az) * rz) / lengthSquared, 0, 1) : 0;
        var dx = ax + rx * t - px;
        var dz = az + rz * t - pz;
        return dx * dx + dz * dz <= radius * radius;
    }
}
