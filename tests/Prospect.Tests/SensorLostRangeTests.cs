using Prospect.Client.Screens;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The outline of range lost to gas (never wider than the plain sphere's circle, however the gas shaped the reach),
/// and the union of many overlapping or concave regions, which the merged coverage outline must keep to its boundary.
/// </summary>
public sealed class SensorLostRangeTests
{
    private const int N = SensorCoverage.ReachSamples;
    private static readonly GameContent Content = GameContent.Load();
    private static readonly Galaxy Seeded = Content.Galaxy.WithSeed(0x50524F5350454354);

    /// <summary>A four-lobed reach that is far from convex: full range in broad lobes along the axes, a tenth of it between them.</summary>
    private static double[] Cross(double nominal)
    {
        var ring = new double[N];
        for (var k = 0; k < N; k++)
            ring[k] = Math.Abs(Math.Cos(2 * k * (Math.Tau / N))) > 0.7 ? nominal : nominal * 0.1;
        return ring;
    }

    private static double[] Flat(double value)
    {
        var ring = new double[N];
        Array.Fill(ring, value);
        return ring;
    }

    private static OutlineSegment[] Lost(double[] x, double[] z, double[] nominal, params double[][] rings)
    {
        var output = new OutlineSegment[SensorOutline.MaxRings * N];
        var count = SensorOutline.BuildLostRange(x, z, nominal, rings.SelectMany(r => r).ToArray(), rings.Length, output);
        return output[..count];
    }

    private static double From(double x, double z, double ax, double az) => Math.Sqrt((ax - x) * (ax - x) + (az - z) * (az - z));

    [Fact]
    public void LostRangeOfAConcaveReachStaysOnThePlainCircleAndNeverPastIt()
    {
        var ring = Cross(5000);
        var pieces = Lost([0], [0], [5000], ring);
        // Exactly the chords beside a cut direction, no matter how the reach folds in between.
        var expected = Enumerable.Range(0, N).Count(k => ring[k] < 4975 || ring[(k + 1) % N] < 4975);
        Assert.Equal(expected, pieces.Length);
        Assert.InRange(pieces.Length, 1, N - 1);
        foreach (var piece in pieces)
        {
            Assert.InRange(From(0, 0, piece.AX, piece.AZ), 5000 - 1e-6, 5000 + 1e-6);
            Assert.InRange(From(0, 0, piece.BX, piece.BZ), 5000 - 1e-6, 5000 + 1e-6);
            Assert.Equal(5000f, piece.Reach);
        }
    }

    [Fact]
    public void ASensorGasDidNotCutHasNoLostRangeAndATinyCutIsIgnored()
    {
        Assert.Empty(Lost([0], [0], [5000], Flat(5000)));
        Assert.Empty(Lost([0], [0], [5000], Flat(4990)));
        Assert.Equal(N, Lost([0], [0], [5000], Flat(1250)).Length);
    }

    [Fact]
    public void LostRangeOfSensorsApartIsOneCircleEachAndTheOffPlaneCircleIsTheSmallerOne()
    {
        // 1.26 km of 5 km reach in thick gas, as the command view showed it: every direction cut.
        var pieces = Lost([0, 40000], [0, 0], [5000, 3000], Flat(1260), Flat(1260));
        Assert.Equal(2 * N, pieces.Length);
        foreach (var piece in pieces)
        {
            var near = piece.AX < 20000 ? 0.0 : 40000.0;
            var nominal = piece.AX < 20000 ? 5000.0 : 3000.0;
            Assert.InRange(From(near, 0, piece.AX, piece.AZ), nominal - 1e-6, nominal + 1e-6);
        }
    }

    [Fact]
    public void LostRangeOfOverlappingSensorsIsTheOutlineOfTheUnionOfTheirCircles()
    {
        double[] x = [0, 6000], z = [0, 0];
        var pieces = Lost(x, z, [5000, 5000], Flat(1260), Flat(1260));
        // Each loses the part of its circle inside the other's disc: fewer than both whole, more than one.
        Assert.InRange(pieces.Length, N, 2 * N - 1);
        foreach (var piece in pieces)
        {
            var mx = (piece.AX + piece.BX) * 0.5;
            var mz = (piece.AZ + piece.BZ) * 0.5;
            Assert.True(From(0, 0, mx, mz) >= 4990 && From(6000, 0, mx, mz) >= 4990, "a piece lies inside the other sensor's range");
        }
    }

    [Fact]
    public void LostRangeOfIdenticalAndContainedSensorsIsOutlinedOnce()
    {
        Assert.Equal(N, Lost([0, 0], [0, 0], [5000, 5000], Flat(1260), Flat(1260)).Length);
        // A small circle wholly inside a larger one adds nothing; the larger one's own circle is the outline.
        var contained = Lost([0, 500], [0, 0], [5000, 1000], Flat(1260), Flat(300));
        Assert.Equal(N, contained.Length);
        foreach (var piece in contained)
            Assert.InRange(From(0, 0, piece.AX, piece.AZ), 4999, 5001);
    }

    [Fact]
    public void LostRangeOfConcaveReachesAmongOthersNeverPassesAnyPlainCircle()
    {
        // A scatter of concave and round reaches, overlapping in a cluster: every piece lies on its own sensor's
        // circle, which is the widest thing that sensor can ever show.
        var random = new Random(7);
        const int count = 24;
        var x = new double[count];
        var z = new double[count];
        var nominal = new double[count];
        var rings = new double[count][];
        for (var i = 0; i < count; i++)
        {
            x[i] = random.NextDouble() * 14000;
            z[i] = random.NextDouble() * 14000;
            nominal[i] = 2500 + random.NextDouble() * 2500;
            rings[i] = i % 2 == 0 ? Cross(nominal[i]) : Flat(nominal[i] * 0.3);
        }

        var pieces = Lost(x, z, nominal, rings);
        Assert.NotEmpty(pieces);
        foreach (var piece in pieces)
        {
            var onOwn = Enumerable.Range(0, count).Any(i => Math.Abs(From(x[i], z[i], piece.AX, piece.AZ) - nominal[i]) < 1e-6);
            Assert.True(onOwn, "a piece of the lost range is not on any sensor's plain circle");
            // And outside every disc, or on its edge: the union is outlined, nothing inside it.
            var mx = (piece.AX + piece.BX) * 0.5;
            var mz = (piece.AZ + piece.BZ) * 0.5;
            for (var i = 0; i < count; i++)
                Assert.True(From(x[i], z[i], mx, mz) > nominal[i] - 30, "a piece lies deep inside a plain disc");
        }
    }

    [Fact]
    public void LostRangeFromRealGasStaysOnThePlainCircleOnAnyPlane()
    {
        var anchor = new SectorCoord(133, 0, -77);
        Assert.True(Seeded.GasClouds!.TryGetCloud(Seeded, anchor, out var cloud));
        var centre = GasCloudQuery.CenterOf(Seeded, new GasCloudPlacement(anchor, cloud));
        foreach (var plane in new[] { centre.Y, centre.Y + 300, centre.Y - 500 })
        {
            var coverage = Coverage([centre, centre + new Double3(2500, 0, 0), centre + new Double3(60000, 0, 0)], Seeded);
            coverage.SelectActive([1, 2, 3]);
            coverage.LinkActive();
            coverage.Refresh(plane, true);
            var lost = coverage.LostRange(true).ToArray();
            Assert.True(lost.Length > 0, $"no lost range on plane {plane - centre.Y}");
            var radius = Content.Exploration.SensorRadius;
            var nominal = Math.Sqrt(radius * radius - (centre.Y - plane) * (centre.Y - plane));
            foreach (var piece in lost)
            {
                var onOne = new[] { centre.X, centre.X + 2500, centre.X + 60000 }.Any(cx => Math.Abs(From(cx, centre.Z, piece.AX, piece.AZ) - nominal) < 1e-3);
                Assert.True(onOne, $"plane {plane}: a piece of lost range is not on a plain circle of radius {nominal}");
            }

            // Inside the cloud nothing is cut at the plain range's own edge only by accident: the far sensor in clear
            // space has none, so no piece is near it.
            Assert.DoesNotContain(lost, p => p.AX > centre.X + 40000);
        }
    }

    private static SensorCoverage Coverage(Double3[] positions, Galaxy galaxy)
    {
        var coverage = new SensorCoverage
        {
            Positions = positions,
            Ids = Enumerable.Range(1, positions.Length).Select(i => (uint)i).ToArray(),
            Piloted = new bool[positions.Length],
            Count = positions.Length,
            LinkDistance = 10000,
        };
        coverage.Piloted[0] = true;
        coverage.Configure(galaxy, Content.Exploration);
        return coverage;
    }

    [Fact]
    public void ManyOverlappingSensorsInOneGroupKeepOnlyTheOuterBoundary()
    {
        // Thirty sensors packed within 150 m of each other with ranges differing by a few metres: their boundaries
        // run alongside one another, so one piece is crossed by many, and every crossing must be cut at or a piece
        // half inside another region is judged by its middle alone and drawn or dropped whole.
        const int count = 30;
        var random = new Random(11);
        var x = new double[count];
        var z = new double[count];
        var rings = new double[count][];
        for (var i = 0; i < count; i++)
        {
            x[i] = (random.NextDouble() - 0.5) * 150;
            z[i] = (random.NextDouble() - 0.5) * 150;
            rings[i] = Flat(4950 + random.NextDouble() * 100);
        }

        var output = new OutlineSegment[SensorCoverage.MaxOutlineSegments];
        var total = SensorOutline.Build(x, z, rings.SelectMany(r => r).ToArray(), [], count, output);
        Assert.InRange(total, 1, SensorCoverage.MaxOutlineSegments - 1);
        foreach (var piece in output[..total])
        {
            for (var t = 0; t <= 4; t++)
            {
                var px = piece.AX + (piece.BX - piece.AX) * t / 4;
                var pz = piece.AZ + (piece.BZ - piece.AZ) * t / 4;
                for (var i = 0; i < count; i++)
                    Assert.False(SensorOutline.Inside(x[i], z[i], rings[i], px, pz, -0.05), "outline runs inside another sensor's region");
            }
        }
    }

    [Fact]
    public void ConcaveRegionsOfOneGroupMergeToTheirUnionAndOtherGroupsStayWhole()
    {
        double[] x = [0, 2500], z = [0, 0];
        var star = Cross(5000);
        var samples = star.Concat(star).ToArray();
        var output = new OutlineSegment[SensorCoverage.MaxOutlineSegments];
        var merged = SensorOutline.Build(x, z, samples, [], 2, output);
        Assert.InRange(merged, 1, 2 * N - 1);
        foreach (var piece in output[..merged])
        {
            var mx = (piece.AX + piece.BX) * 0.5;
            var mz = (piece.AZ + piece.BZ) * 0.5;
            // A piece lies on its own region's boundary, so it is never half a metre deep in either region.
            Assert.False(SensorOutline.Inside(0, 0, star, mx, mz, -0.5), "a piece lies deep inside the first region");
            Assert.False(SensorOutline.Inside(2500, 0, star, mx, mz, -0.5), "a piece lies deep inside the second region");
        }

        // Told apart by group the same two regions keep every piece of both.
        var apart = SensorOutline.Build(x, z, samples, [0, 1], 2, output);
        Assert.Equal(2 * N, apart);
    }
}
