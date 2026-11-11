using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class BoxDesignTests
{
    private static readonly BlockCatalog Catalog = BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json"));
    private static readonly GridSize One = new(1, 1, 1);
    private static DesignBox Box(string type, int x = 0, GridSize? size = null) => new(new GridSize(x, 0, 0), size ?? One, Catalog.Get(type));
    private static BoxDesign Design(params DesignBox[] boxes) => new("test", 1, boxes, Catalog.Limits, "test.json");

    // Integer-only LCG and fixed slots make fixtures independent of runtime Random implementations.
    internal static DesignBox[] Generate(int count, uint seed)
    {
        var boxes = new DesignBox[count];
        string[] types = ["hull", "hull", "hull", "engine", "thruster", "generator", "gyro", "shield", "battery", "cargo", "quarters", "hyperdrive"];
        for (var i = 0; i < count; i++)
        {
            seed = unchecked(seed * 1664525 + 1013904223);
            var type = i == 0 ? "core" : types[(int)(seed % (uint)types.Length)];
            var size = type == "hull" ? new GridSize(8, 1 + (int)(seed >> 8 & 3), 8) : new GridSize(1 + (int)(seed >> 12 & 3), 2, 2);
            boxes[i] = new DesignBox(new GridSize(i % 50 * 8, i / 2500 * 8, i / 50 % 50 * 8), size, Catalog.Get(type));
        }
        return boxes;
    }

    [Fact]
    public void TransitSpeedSurvivesRulesAndBothHullEvaluators()
    {
        var content = GameContent.Load();
        var rules = Catalog.Rules with { FtlSpeed = 12345 };
        var changed = content.Pod.WithRules(rules);
        changed.Evaluate(changed.FullMask, out var flight, out _);
        Assert.Equal(12345, flight.FtlSpeed);
        Assert.Equal(12345, changed.Flight.FtlSpeed);
        new BoxHullSums(Design(Box("core"), Box("hyperdrive", 2))).Evaluate(rules, out var box, out _);
        Assert.Equal(12345, box.FtlSpeed);
        var settings = content.SettingCatalog.Build(null, ["jumpChargeSpeed=2"]);
        Assert.Equal(12345, rules.Under(content.SettingCatalog, settings).FtlSpeed);
        Assert.Equal(40000, content.WithSettings(settings).Pod.Flight.FtlSpeed);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(150000)]
    public void GeneratedDesignsAreDeterministicAndWithinBudget(int count)
    {
        var before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        var boxes = Generate(count, 1234); var generation = timer.Elapsed.TotalMilliseconds;
        timer.Restart(); var design = Design(boxes); var validation = timer.Elapsed.TotalMilliseconds;
        timer.Restart(); var sums = new BoxHullSums(design); sums.Evaluate(Catalog.Rules, out var flight, out var stats); var evaluation = timer.Elapsed.TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"BOX {count}: generate {generation:F2} ms; validate {validation:F2} ms; evaluate {evaluation:F2} ms; allocated {allocated} bytes; sums retained {sums.StorageBytes} bytes");
        Assert.True(generation + validation + evaluation < 15000, "15 second tooling budget");
        Assert.True(allocated < 256L * 1024 * 1024, "256 MiB allocation budget");
        Assert.Equal(count, stats.Blocks); Assert.True(flight.Mass > 0); Assert.True(flight.PowerGenerated > 0);
        Assert.Equal(boxes, Generate(count, 1234)); Assert.NotEqual(boxes[1], Generate(count, 1235)[1]);
    }

    [Fact]
    public void RunningSumsHaveNoLossHistoryDrift()
    {
        var design = Design(Generate(4096, 42)); var sums = new BoxHullSums(design);
        var alive = Enumerable.Repeat(true, 4096).ToArray(); var order = Enumerable.Range(0, 4096).ToArray();
        uint seed = 82;
        for (var i = order.Length - 1; i > 0; i--) { seed = unchecked(seed * 1664525 + 1013904223); var j = (int)(seed % (uint)(i + 1)); (order[i], order[j]) = (order[j], order[i]); }
        for (var i = 0; i < order.Length; i++)
        {
            sums.Lose(order[i]); sums.Lose(order[i]); alive[order[i]] = false;
            if (i % 137 != 0 && i != order.Length - 1) continue;
            sums.Evaluate(Catalog.Rules, out var actual, out var stats);
            var fresh = new BoxHullSums(design, alive); fresh.Evaluate(Catalog.Rules, out var expected, out var expectedStats);
            Assert.Equal(expected, actual); Assert.Equal(expectedStats, stats); Assert.Equal(fresh.Center, sums.Center);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.5, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 16)]
    public void SizeRulesPreserveUnitValuesAndScaleEachNumericStat(double exponent, double multiplier)
    {
        string[] names = ["mass", "hull", "thrust", "torque", "powerOutput", "powerDraw", "energyStorage", "shield", "shieldRecharge", "cargo", "crew", "ftlPower"];
        var type = new BlockType { Id = "test", Name = "test", Mass = 1, Hull = 1, SizeRules = names.ToDictionary(n => n, _ => exponent) };
        foreach (var name in names) { Assert.Equal(0.3f, BoxDesign.Scale(type, name, 0.3f, 1)); Assert.Equal(3 * multiplier, BoxDesign.Scale(type, name, 3, 4)); }
        Assert.Equal(12, BoxDesign.Scale(Catalog.Get("hull"), "mass", 3, 4));
    }

    [Fact]
    public void SolidBoxesMatchAnIndependentUnitCellReference()
    {
        var design = Design(Box("core", -4), Box("hull", 0, new GridSize(3, 2, 4)), Box("engine", 5, new GridSize(2, 3, 1)));
        double mass = 0, x = 0, y = 0, z = 0;
        var cells = new List<(double X, double Y, double Z, double Mass)>();
        foreach (var b in design.Blocks)
            for (var dx = 0; dx < b.Size.X; dx++) for (var dy = 0; dy < b.Size.Y; dy++) for (var dz = 0; dz < b.Size.Z; dz++)
                        cells.Add((b.At.X + dx, b.At.Y + dy, b.At.Z + dz, b.Type.Mass));
        foreach (var c in cells) { mass += c.Mass; x += c.X * c.Mass; y += c.Y * c.Mass; z += c.Z * c.Mass; }
        var centre = new Double3(x / mass, y / mass, z / mass); var inertia = Double3.Zero;
        foreach (var c in cells) { var rx = c.X - centre.X; var ry = c.Y - centre.Y; var rz = c.Z - centre.Z; inertia += new Double3(c.Mass * (ry * ry + rz * rz + 1.0 / 6), c.Mass * (rx * rx + rz * rz + 1.0 / 6), c.Mass * (rx * rx + ry * ry + 1.0 / 6)); }
        new BoxHullSums(design).Evaluate(Catalog.Rules, out var flight, out _);
        Assert.Equal(mass, flight.Mass); Assert.True((centre - design.Center).Length < 1e-12);
        Assert.True(Math.Abs(inertia.X - flight.Inertia.X) / inertia.X < 1e-6); Assert.True(Math.Abs(inertia.Y - flight.Inertia.Y) / inertia.Y < 1e-6); Assert.True(Math.Abs(inertia.Z - flight.Inertia.Z) / inertia.Z < 1e-6);
    }

    [Fact]
    public void InvalidDesignsNameTheirSourceAndBlock()
    {
        DesignBox[][] cases = [[Box("core"), Box("hull")], [Box("core", 4096)], [Box("core", size: new GridSize(0, 1, 1))], [Box("hull")], [Box("core", size: new GridSize(65, 1, 1))]];
        foreach (var boxes in cases) { var e = Assert.Throws<InvalidDataException>(() => Design(boxes)); Assert.Contains("test.json", e.Message); Assert.Contains("block", e.Message); }
        Assert.Throws<InvalidDataException>(() => new BoxDesign("test", 1, [Box("core"), Box("hull", 1)], new DesignLimits(64, 4096, 1), "test.json"));
        Design(Box("core"), Box("hull", 1));
    }

    [Fact]
    public void VersionTwoRoundTripsAndLiveLoaderRefusesLaterStageDesigns()
    {
        foreach (var boxes in new[] { new[] { Box("core", size: new GridSize(2, 1, 1)) }, Generate(65, 12) })
        {
            var design = Design(boxes); var path = Path.GetTempFileName();
            try
            {
                using (var stream = File.Create(path)) design.Write(stream);
                var read = BoxDesign.Load(path, Catalog); Assert.Equal(design.Blocks.ToArray(), read.Blocks.ToArray());
                Assert.Contains("stage 2", Assert.Throws<InvalidDataException>(() => HullDesign.Load(path, Catalog, Catalog.Rules)).Message);
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void LegacyFilesKeepExactLiveFiguresWhenVersioned()
    {
        foreach (var path in Directory.GetFiles(Path.GetDirectoryName(AppPaths.Content("ships", "fleet.json"))!, "*.json"))
        {
            if (Path.GetFileName(path) == "fleet.json") continue;
            var legacy = HullDesign.Load(path, Catalog, Catalog.Rules); var boxes = BoxDesign.Load(path, Catalog); var temp = Path.GetTempFileName();
            try
            {
                using (var stream = File.Create(temp)) boxes.Write(stream);
                var versioned = HullDesign.Load(temp, Catalog, Catalog.Rules);
                Assert.Equal(legacy.Flight, versioned.Flight); Assert.Equal(legacy.Stats, versioned.Stats);
            }
            finally { File.Delete(temp); }
        }
    }
    [Fact]
    public void FileValidationReportsMalformedVersionTwoBlocks()
    {
        string[] entries = [
            "{\"type\":\"core\",\"at\":[0,0,0],\"size\":[0,1,1]}",
            "{\"type\":\"core\",\"at\":[4096,0,0]}",
            "{\"type\":\"core\",\"at\":[0,0]}",
            "{\"type\":\"missing\",\"at\":[0,0,0]}",
            "{\"type\":\"hull\",\"at\":[0,0,0]}"
        ];
        var path = Path.GetTempFileName();
        try
        {
            foreach (var entry in entries)
            {
                File.WriteAllText(path, "{\"version\":2,\"name\":\"bad\",\"class\":\"ship\",\"blockSize\":1,\"color\":[1,1,1],\"blocks\":[" + entry + "]}");
                var error = Assert.Throws<InvalidDataException>(() => BoxDesign.Load(path, Catalog));
                Assert.Contains(path, error.Message); Assert.Contains("block", error.Message);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ContentRulesAndWorldMultipliersReachEvaluation()
    {
        var design = Design(Box("core"), Box("generator", 2, new GridSize(2, 2, 2)), Box("quarters", 5, new GridSize(2, 2, 2)), Box("shield", 8), Box("hyperdrive", 9));
        var sums = new BoxHullSums(design);
        sums.Evaluate(Catalog.Rules, out var plain, out var stats);
        Assert.Equal((float)(6 * Math.Pow(8, 1.25)), plain.PowerGenerated);
        Assert.Equal(5, stats.Crew);
        sums.Evaluate(Catalog.Rules with { HullStrength = 2, ShieldRechargeMultiplier = 3, FtlChargeMultiplier = 4 }, out var changed, out _);
        Assert.Equal(plain.HullPoints * 2, changed.HullPoints);
        Assert.Equal(plain.ShieldRecharge * 3, changed.ShieldRecharge);
        Assert.Equal(plain.FtlPower * 4, changed.FtlPower);
        Assert.Equal(12f, plain.FtlCalculateSeconds);
        Assert.Equal(GameContent.Load().Pod.Flight.FtlCalculateSeconds, plain.FtlCalculateSeconds);
        Assert.Equal(plain.FtlCalculateSeconds, changed.FtlCalculateSeconds);
        sums.Evaluate(Catalog.Rules with { FtlCalculateSeconds = 0 }, out var instant, out _);
        Assert.Equal(0, instant.FtlCalculateSeconds);
        Assert.Equal(plain.Inertia, changed.Inertia);
    }

    [Fact]
    public void LosingFirstDriveSelectsNextSurvivingKindAndKeepsOriginalPivot()
    {
        var design = Design(Box("core"), Box("hyperdrive", 3), Box("riftcutter", 8), Box("hyperdrive", 10));
        var sums = new BoxHullSums(design);
        sums.Evaluate(Catalog.Rules, out var first, out _);
        Assert.Equal(FtlKind.Hyperspace, first.Ftl); Assert.Equal(16, first.FtlPower);
        sums.Lose(1); sums.Evaluate(Catalog.Rules, out var next, out _);
        Assert.Equal(FtlKind.Rift, next.Ftl); Assert.Equal(10, next.FtlPower);
        Assert.NotEqual(design.Center, sums.Center);
        var original = design.Center;
        sums.Lose(2); sums.Lose(3);
        sums.Evaluate(Catalog.Rules, out var core, out _);
        Assert.Equal(original, design.Center);
        Assert.Equal((float)(3 * (original.X * original.X + 1.0 / 6)), core.Inertia.Y);
    }

}
