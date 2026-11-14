using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Engine.Time;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The client side of cloud kinds without a GPU: the ice shard mesh, lightning timing against the server's bursts,
/// which clouds are drawn together, how a draw names its kind, and the bounds of the kind looks. No GLSL runs here.
/// </summary>
public sealed class GasKindVisualTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly GasTiers Tiers = GasTiers.Load();

    [Fact]
    public void IcicleMeshIsTheSharedShardWithItsTips()
    {
        var mesh = MeshBuilder.Icicle();
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(24, mesh.Indices.Count);
        Assert.Equal(1f, mesh.BoundingRadius);
        var tips = new HashSet<Vector3>();
        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = Point(mesh.Vertices[(int)mesh.Indices[i]].Position);
            var b = Point(mesh.Vertices[(int)mesh.Indices[i + 1]].Position);
            var c = Point(mesh.Vertices[(int)mesh.Indices[i + 2]].Position);
            var normal = Vector3.Cross(b - a, c - a);
            // Wound outward, as the rocks are, so back faces are culled.
            Assert.True(Vector3.Dot(normal, a + b + c) > 0);
            foreach (var corner in new[] { a, b, c })
            {
                tips.Add(corner);
                var length = corner.Length();
                Assert.Equal(length, AsteroidShape.RadiusAlong(AsteroidShape.Icicle, corner / length), 5);
            }
            // Every point of a face lies on the surface the simulation collides with.
            var middle = (a + b + c) / 3;
            Assert.Equal(middle.Length(), AsteroidShape.RadiusAlong(AsteroidShape.Icicle, Vector3.Normalize(middle)), 5);
        }
        Assert.Equal(6, tips.Count);
        Assert.Contains(new Vector3(1, 0, 0), tips);
        Assert.Contains(new Vector3(-1, 0, 0), tips);
        Assert.Equal(24, MeshBuilder.Asteroid(AsteroidShape.Icicle, 1).Vertices.Count);

        static Vector3 Point(Brutal.Numerics.float3 p) => new(p.X, p.Y, p.Z);
    }

    [Fact]
    public void LightningFlashesOnlyInTheServersBurstAndItsAfterglow()
    {
        var rules = Content.Exploration;
        var cloud = new GasCloud(Vector3.Zero, new Vector3(9000, 5850, 7650), 31.7f, GasCloudKind.Lightning);
        var look = Tiers.Lightning;
        var tail = rules.LightningBurstTicks + 6 * look.StrokeDecay * TickLoop.SimulationHz;
        var lit = 0;
        for (var tick = 0; tick < 3 * rules.LightningPeriodTicks; tick++)
        {
            GasSensors.LightningCycle(cloud, rules, tick, out var into);
            var flashing = WorldView.Flash(look, cloud, rules, tick + 0.5, null, out var stroke);
            if (flashing) lit++;
            // Light only within the burst the sensors lose, or the fading light of its last stroke.
            if (into + 0.5 > tail) Assert.False(flashing);
            if (!flashing) continue;
            Assert.InRange(stroke.W, 0.002f, 1f);
            Assert.InRange(MathF.Abs(stroke.X), 0, look.FlashSpread);
        }
        Assert.True(lit > 0);
        // The same moment gives the same light: every client of a world sees the same strokes.
        var pin = 0f;
        while (pin < 1 && !WorldView.Flash(look, cloud, rules, 0, pin, out _)) pin += 0.01f;
        Assert.True(WorldView.Flash(look, cloud, rules, 5.25, pin, out var first));
        Assert.True(WorldView.Flash(look, cloud, rules, 900.75, pin, out var second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void EveryBurstDrawsBranchingBoltsThatGrowAndFadeWithinItsWindow()
    {
        var rules = Content.Exploration;
        var look = Tiers.Lightning;
        var cloud = new GasCloud(Vector3.Zero, new Vector3(19000, 12350, 16150), 77.3f, GasCloudKind.Lightning);
        var bolts = new LightningBolts();
        var frame = new FrameConstants();
        var burst = rules.LightningBurstTicks / (float)TickLoop.SimulationHz;
        var life = LightningBolts.Lifetime(look);
        // From outside (the cloud 60 km ahead) and from inside, near its centre.
        foreach (var center in new[] { new Vector3(0, 0, -60000), new Vector3(300, -200, 150) })
        {
            for (var cycle = 0; cycle < 4; cycle++)
            {
                var drawn = 0;
                var near = false;
                var forks = false;
                var grew = false;
                for (var step = 0; step < 2 * rules.LightningPeriodTicks; step++)
                {
                    var tick = (cycle + 2) * (double)rules.LightningPeriodTicks + step * 0.5;
                    var seconds = LightningBolts.Clock(cloud, rules, tick, null, out var at);
                    bolts.Begin();
                    var offered = bolts.Collect(look, cloud, center, seconds, at, burst, 0, 0.56f);
                    bolts.Write(look, ref frame);
                    // Nothing outside the burst window and the bolts' own lifetime.
                    if (seconds > burst * 0.8f + life) Assert.Equal(0, offered);
                    if (bolts.Drawn == 0) continue;
                    drawn++;
                    for (var b = 0; b < bolts.Drawn; b++)
                    {
                        var path = frame.GasBoltHeads[3 * b + 1];
                        var bounds = frame.GasBoltHeads[3 * b];
                        Assert.InRange(path.Y, 1, LightningBolts.MaxSegments);
                        if (path.Z < 1e6f && path.Z > 0) grew = true;
                        near |= new Vector3(bounds.X, bounds.Y, bounds.Z).Length() < 2 * look.BoltCell + bounds.W;
                        for (var k = 0; k < (int)path.Y; k++)
                            forks |= frame.GasBoltSegments[2 * ((int)path.X + k) + 1].W > 0;
                    }
                }
                Assert.True(drawn > 0, $"burst {cycle} drew no bolt");
                Assert.True(forks && grew);
                // Inside the cloud a strike is always near enough to see.
                if (center.Length() < 1000) Assert.True(near, $"burst {cycle} had no bolt near the camera");
            }
        }

        // The same moment draws the same bolts; a different burst draws different ones.
        float[] Shape(long cycle, float seconds)
        {
            bolts.Begin();
            bolts.Collect(look, cloud, new Vector3(0, 0, -60000), seconds, cycle, burst, 0, 0.56f);
            bolts.Write(look, ref frame);
            return bolts.Drawn == 0 ? [] : [frame.GasBoltSegments[1].X, frame.GasBoltSegments[1].Y, frame.GasBoltSegments[3].Z];
        }
        var moment = 0f;
        while (moment < burst + life && Shape(5, moment).Length == 0) moment += 0.01f;
        Assert.NotEmpty(Shape(5, moment));
        Assert.Equal(Shape(5, moment), Shape(5, moment));
        Assert.NotEqual(Shape(5, moment), Shape(6, moment));

        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            bolts.Begin();
            bolts.Collect(look, cloud, new Vector3(300, -200, 150), 0.2f, 3, burst, 0, 0.56f);
            bolts.Write(look, ref frame);
        }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void OverlappingColdAndNormalCloudsArePairedOnce()
    {
        var galaxy = Content.Galaxy.WithSeed(1);
        GasCloudPlacement[] clouds =
        [
            new(new SectorCoord(0, 0, 0), new GasCloud(Vector3.Zero, new Vector3(9000, 5850, 7650), 3, GasCloudKind.Cold)),
            new(new SectorCoord(0, 0, 0), new GasCloud(new Vector3(12000, 0, 0), new Vector3(9000, 5850, 7650), 90, GasCloudKind.Normal)),
            // A second normal overlapping the same cold cloud: the cold one already has its partner.
            new(new SectorCoord(0, 0, 0), new GasCloud(new Vector3(-12000, 0, 0), new Vector3(9000, 5850, 7650), 80, GasCloudKind.Normal)),
            // Lightning never shares space, and a cold cloud far away has nothing to pair with.
            new(new SectorCoord(0, 0, 0), new GasCloud(new Vector3(0, 9000, 0), new Vector3(9000, 5850, 7650), 30, GasCloudKind.Lightning)),
            new(new SectorCoord(3, 0, 0), new GasCloud(Vector3.Zero, new Vector3(9000, 5850, 7650), 2, GasCloudKind.Cold)),
        ];
        var partners = new int[clouds.Length];
        GasCloudField.Pair(galaxy, clouds, partners);
        Assert.Equal([1, 0, -1, -1, -1], partners);

        var probe = new AllocationProbe();
        while (probe.Measuring()) GasCloudField.Pair(galaxy, clouds, partners);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void KindCodeCarriesKindAndSlot()
    {
        Assert.Equal(0f, GasConstants.KindCode(GasCloudKind.Normal, -1));
        Assert.Equal(1f, GasConstants.KindCode(GasCloudKind.Cold, -1));
        Assert.Equal(2f + 4 * 8, GasConstants.KindCode(GasCloudKind.Lightning, 7));
        // The shader's decoding: int(w + 0.5) & 3 is the kind, (int(w + 0.5) >> 2) - 1 the slot.
        foreach (var kind in new[] { GasCloudKind.Normal, GasCloudKind.Cold, GasCloudKind.Lightning })
            for (var slot = -1; slot < GasPartners.Capacity; slot++)
            {
                var code = (int)(GasConstants.KindCode(kind, slot) + 0.5f);
                Assert.Equal((int)kind, code & 3);
                Assert.Equal(slot, (code >> 2) - 1);
            }
    }

    [Fact]
    public void KindLooksOutOfRangeAreRefused()
    {
        var shipped = File.ReadAllText(AppPaths.Content("visuals", "gas.json"));
        var path = Path.Combine(Path.GetTempPath(), "prospect-gas-kinds-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, shipped);
            var loaded = GasTiers.Load(path);
            Assert.Equal(Tiers.Lightning.Strokes, loaded.Lightning.Strokes);
            Assert.True(loaded.Cold.Tint[2] > loaded.Cold.Tint[0]);
            foreach (var (old, bad) in new[]
            {
                ("\"edge\": 0.7", "\"edge\": 0.1"),
                ("\"halo\": 0.8", "\"halo\": 9"),
                ("\"sparkleCell\": 140", "\"sparkleCell\": 1"),
                ("\"tint\": [0.74, 1.0, 1.5]", "\"tint\": [0.74, 1.0]"),
                ("\"strokes\": 3", "\"strokes\": 0"),
                ("\"flashRadius\": 0.3", "\"flashRadius\": 3"),
                ("\"boltCellChance\": 0.1", "\"boltCellChance\": 1.5"),
                ("\"boltWidth\": 5", "\"boltWidth\": 0"),
                ("\"boltLengthMax\": 2600", "\"boltLengthMax\": 100"),
                ("\"boltDepth\": 3", "\"boltDepth\": 9"),
                ("\"boltGrow\": 0.16", "\"boltGrow\": 0"),
                ("\"farFlash\": 2.5", "\"farFlash\": -1"),
            })
            {
                Assert.Contains(old, shipped);
                File.WriteAllText(path, shipped.Replace(old, bad));
                Assert.Throws<InvalidDataException>(() => GasTiers.Load(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
