using Prospect.Client.Screens;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The command view's sensor shape: sampled gas reach, radar link groups and the merged outline per group.</summary>
public sealed class SensorShapeTests
{
    private const int N = SensorCoverage.ReachSamples;
    private static readonly GameContent Content = GameContent.Load();
    private static readonly Galaxy Seeded = Content.Galaxy.WithSeed(0x50524F5350454354);

    private static double[] Circle(double radius)
    {
        var ring = new double[N];
        Array.Fill(ring, radius);
        return ring;
    }

    private static OutlineSegment[] Union(double[] x, double[] z, double[][] rings, int[]? group = null)
    {
        var samples = rings.SelectMany(r => r).ToArray();
        var output = new OutlineSegment[SensorCoverage.MaxOutlineSegments];
        var count = SensorOutline.Build(x, z, samples, group ?? [], rings.Length, output);
        return output[..count];
    }

    /// <summary>How many separate closed lines the pieces form: pieces sharing an end belong to one.</summary>
    private static int Loops(OutlineSegment[] pieces)
    {
        var parent = Enumerable.Range(0, pieces.Length).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        static bool Meet(double ax, double az, double bx, double bz) => Math.Abs(ax - bx) < 0.01 && Math.Abs(az - bz) < 0.01;
        for (var a = 0; a < pieces.Length; a++)
            for (var b = a + 1; b < pieces.Length; b++)
                if (Meet(pieces[a].AX, pieces[a].AZ, pieces[b].AX, pieces[b].AZ) || Meet(pieces[a].AX, pieces[a].AZ, pieces[b].BX, pieces[b].BZ)
                    || Meet(pieces[a].BX, pieces[a].BZ, pieces[b].AX, pieces[b].AZ) || Meet(pieces[a].BX, pieces[a].BZ, pieces[b].BX, pieces[b].BZ))
                    parent[Find(a)] = Find(b);
        return Enumerable.Range(0, pieces.Length).Select(Find).Distinct().Count();
    }

    [Fact]
    public void TwoOverlappingCirclesGiveOneOutlineWithNothingInside()
    {
        double[] x = [0, 6000], z = [0, 0];
        var pieces = Union(x, z, [Circle(5000), Circle(5000)]);
        Assert.Equal(1, Loops(pieces));
        foreach (var piece in pieces)
        {
            var mx = (piece.AX + piece.BX) * 0.5;
            var mz = (piece.AZ + piece.BZ) * 0.5;
            // Every piece lies on the outside of the other's circle (a chord sits a few metres inside it).
            Assert.True(Math.Min(Math.Sqrt(mx * mx + mz * mz), Math.Sqrt((mx - 6000) * (mx - 6000) + mz * mz)) > 4990);
            Assert.True(Math.Sqrt(mx * mx + mz * mz) >= 4990 && Math.Sqrt((mx - 6000) * (mx - 6000) + mz * mz) >= 4990);
        }

        // Less than the two whole rings, more than one: both lose the part inside the other.
        Assert.InRange(pieces.Length, N, 2 * N - 1);
    }

    [Fact]
    public void DisjointCirclesGiveALoopEachAndAContainedOneGivesTheOuterOnly()
    {
        Assert.Equal(2, Loops(Union([0, 20000], [0, 0], [Circle(5000), Circle(5000)])));
        Assert.Equal(2 * N, Union([0, 20000], [0, 0], [Circle(5000), Circle(5000)]).Length);

        var contained = Union([0, 500], [0, 0], [Circle(5000), Circle(1000)]);
        Assert.Equal(1, Loops(contained));
        Assert.Equal(N, contained.Length);
        foreach (var piece in contained)
            Assert.InRange(Math.Sqrt(piece.AX * piece.AX + piece.AZ * piece.AZ), 4999, 5001);
    }

    [Fact]
    public void IdenticalRegionsAreDrawnOnceAndOtherGroupsAreNeverMerged()
    {
        Assert.Equal(N, Union([0, 0], [0, 0], [Circle(5000), Circle(5000)]).Length);
        // Overlapping, but told apart by group: each keeps its whole ring, lines inside each other included.
        Assert.Equal(2 * N, Union([0, 6000], [0, 0], [Circle(5000), Circle(5000)], [0, 1]).Length);
    }

    [Fact]
    public void NormalsPointOutOfTheRegion()
    {
        foreach (var piece in Union([0, 6000], [0, 0], [Circle(5000), Circle(5000)]))
        {
            var mx = (piece.AX + piece.BX) * 0.5 + piece.NX * 50;
            var mz = (piece.AZ + piece.BZ) * 0.5 + piece.NZ * 50;
            Assert.False(SensorOutline.Inside(0, 0, Circle(5000), mx, mz, 0) || SensorOutline.Inside(6000, 0, Circle(5000), mx, mz, 0));
        }
    }

    private static SensorCoverage Coverage(Double3[] positions, Galaxy? galaxy = null)
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
        coverage.Configure(galaxy ?? Content.Galaxy, Content.Exploration);
        return coverage;
    }

    [Fact]
    public void ShipsWithinTheLinkDistanceLinkInAChainAndTheRestStandApart()
    {
        // A-B and B-C are 8 km apart, A-C 16 km: one chain. D is far from all.
        var coverage = Coverage([new(0, 0, 0), new(8000, 0, 0), new(16000, 0, 0), new(60000, 0, 0)]);
        coverage.SelectActive(default);
        Assert.Equal(1, coverage.ActiveCount);
        coverage.SelectActive([1, 2, 3, 4]);
        coverage.LinkActive();
        Assert.Equal(4, coverage.ActiveCount);
        Assert.Equal(0, coverage.GroupOf(0));
        Assert.Equal(0, coverage.GroupOf(1));
        Assert.Equal(0, coverage.GroupOf(2));
        Assert.Equal(3, coverage.GroupOf(3));
        Assert.Equal(2, coverage.LinkCount);

        coverage.LinkDistance = 5000;
        coverage.LinkActive();
        Assert.Equal(0, coverage.LinkCount);
        Assert.Equal([0, 1, 2, 3], Enumerable.Range(0, 4).Select(coverage.GroupOf));

        coverage.LinkDistance = 0;
        coverage.LinkActive();
        Assert.Equal(0, coverage.LinkCount);
    }

    [Fact]
    public void OnlySelectedShipsShowAndNothingSelectedShowsTheFlownOne()
    {
        var coverage = Coverage([new(0, 0, 0), new(8000, 0, 0), new(60000, 0, 0)]);
        coverage.SelectActive(default);
        Assert.Equal(1, coverage.ActiveCount);
        Assert.Equal(0, coverage.ActiveIndex(0));
        coverage.SelectActive([3]);
        Assert.Equal(1, coverage.ActiveCount);
        Assert.Equal(2, coverage.ActiveIndex(0));
        coverage.SelectActive([2, 3, 99]);
        Assert.Equal(2, coverage.ActiveCount);
        // A selection holding no ship with a sensor shows nothing, not the flown ship.
        coverage.SelectActive([99]);
        Assert.Equal(0, coverage.ActiveCount);
    }

    [Fact]
    public void EachGroupGetsItsOwnMergedOutline()
    {
        // A, B overlap and link; C stands alone 50 km away.
        var coverage = Coverage([new(0, 0, 0), new(6000, 0, 0), new(50000, 0, 0)]);
        coverage.SelectActive([1, 2, 3]);
        coverage.LinkActive();
        coverage.Refresh(0, true);
        var pieces = coverage.Outline().ToArray();
        Assert.Equal(2, Loops(pieces));
        Assert.Contains(pieces, p => p.AX > 40000);
        // The pair is merged: nothing of either lies deep inside the other.
        foreach (var piece in pieces.Where(p => p.AX < 20000))
        {
            var mx = (piece.AX + piece.BX) * 0.5;
            var mz = (piece.AZ + piece.BZ) * 0.5;
            Assert.True(Math.Sqrt(mx * mx + mz * mz) >= 4990 && Math.Sqrt((mx - 6000) * (mx - 6000) + mz * mz) >= 4990);
        }

        // A link too short to join them leaves two whole, crossing rings.
        coverage.LinkDistance = 5000;
        coverage.LinkActive();
        Assert.Equal(3 * N, coverage.Outline().Length);
    }

    private static (Double3 Centre, GasCloudPlacement Placement) Cloud()
    {
        var anchor = new SectorCoord(133, 0, -77);
        Assert.True(Seeded.GasClouds!.TryGetCloud(Seeded, anchor, out var cloud));
        var placement = new GasCloudPlacement(anchor, cloud);
        return (GasCloudQuery.CenterOf(Seeded, placement), placement);
    }

    [Fact]
    public void DrawnSamplesAreTheServersReachInsideAndAtTheEdgeOfACloud()
    {
        var (centre, placement) = Cloud();
        var edge = centre + new Double3(placement.Cloud.HalfSize.X * (double)Seeded.GasClouds!.Shape.X, 0, 0);
        var directions = Enumerable.Range(0, N).Select(SensorOutline.Direction).ToArray();
        var cache = new SensorGasCache(Seeded, Content.Exploration.SensorRadius);
        foreach (var position in new[] { centre, edge })
        {
            var expected = new double[N];
            GasSensors.SampleReach(Seeded, cache.At(position), Content.Exploration, position, directions, expected);
            var coverage = Coverage([position], Seeded);
            coverage.SelectActive([1]);
            coverage.Refresh(position.Y, true);
            var drawn = new double[N];
            Assert.True(coverage.SampleReach(0, position.Y, drawn));
            Assert.Equal(expected, drawn);
            // The cloud really cut the range somewhere, and not by the same amount everywhere at the edge.
            Assert.True(drawn.Min() < Content.Exploration.SensorRadius);
        }

        var atEdge = new double[N];
        var edgeCoverage = Coverage([edge], Seeded);
        edgeCoverage.SelectActive([1]);
        edgeCoverage.Refresh(edge.Y, true);
        edgeCoverage.SampleReach(0, edge.Y, atEdge);
        Assert.True(atEdge.Max() - atEdge.Min() > 500);
    }

    [Fact]
    public void OffPlaneSamplesLieOnTheServersBoundaryOfThePlane()
    {
        var (centre, _) = Cloud();
        var plane = centre.Y + 300;
        var coverage = Coverage([centre], Seeded);
        coverage.SelectActive([1]);
        coverage.Refresh(plane, true);
        var drawn = new double[N];
        Assert.True(coverage.SampleReach(0, plane, drawn));
        var cache = new SensorGasCache(Seeded, Content.Exploration.SensorRadius);
        foreach (var k in new[] { 0, 17, 40, 71 })
        {
            var direction = SensorOutline.Direction(k);
            var inside = centre + new Double3(direction.X * (drawn[k] - 0.05), 300, direction.Z * (drawn[k] - 0.05));
            var outside = centre + new Double3(direction.X * (drawn[k] + 0.05), 300, direction.Z * (drawn[k] + 0.05));
            Assert.True(GasSensors.Covers(Seeded, cache.At(centre), Content.Exploration, centre, inside));
            Assert.False(GasSensors.Covers(Seeded, cache.At(centre), Content.Exploration, centre, outside));
        }
    }

    [Fact]
    public void ReusingAndRefreshingShapesAndOutlinesTakesNoMemory()
    {
        var (centre, _) = Cloud();
        var coverage = Coverage([centre, centre + new Double3(6000, 0, 0), centre + new Double3(60000, 0, 0)], Seeded);
        coverage.SelectActive([1, 2, 3]);
        coverage.LinkActive();
        coverage.Refresh(centre.Y, true);
        _ = coverage.Outline();

        // Built once: a collection expression passed in the loop would be the test's own allocation.
        uint[] all = [1, 2, 3];
        var probe = new AllocationProbe();
        for (var i = 0; i < 50; i++)
        {
            while (probe.Measuring())
            {
                coverage.SelectActive(all);
                coverage.LinkActive();
                coverage.Refresh(centre.Y, true);
                _ = coverage.Outline();
                _ = coverage.InsideShown(centre.X, centre.Z);
            }
        }

        Assert.Equal(0, probe.Bytes);

        // A sensor that moved meaningfully is sampled again and the outline rebuilt, still without allocating.
        var before = coverage.Outline().ToArray();
        for (var i = 0; i < 50; i++)
        {
            coverage.Positions[0] += new Double3(300, 0, 0);
            while (probe.Measuring())
            {
                coverage.LinkActive();
                coverage.Refresh(centre.Y, true);
                coverage.Refresh(centre.Y, true);
                _ = coverage.Outline();
            }
        }

        Assert.Equal(0, probe.Bytes);
        Assert.NotEqual(before.Length == 0 ? 0 : before[0].AX, coverage.Outline()[0].AX);
    }
}
