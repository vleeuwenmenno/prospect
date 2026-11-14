using Prospect.Client.Screens;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Module scans take no managed memory where the rules forbid it: the tick that runs one, the worker that generates for
/// it and the interface that shows its result. None of these tests warms the code first, since the first scan of a
/// world must cost no more than the thousandth; what each of them found allocating is named beside it.
/// </summary>
public sealed class ModuleScanAllocationTests
{
    private static readonly GameContent Game = GameContent.Load();

    private static GasCloudPlacement FindCloud(Galaxy galaxy)
    {
        for (var x = -150; x < 150; x++)
            if (galaxy.GasClouds!.TryGetCloud(galaxy, new(x, 0, -77), out var cloud))
                return new(new(x, 0, -77), cloud);
        throw new InvalidOperationException("The default galaxy has a gas cloud on this row.");
    }

    /// <summary>
    /// The worker generates the sector a scan names, or the 27 around a pulse, into one buffer that it sizes for the
    /// largest sector when it is made. Found allocating: the first generation grew that buffer by 162 KB for the home
    /// sector and by 800 KB for a denser one, inside whatever process-wide measurement was running at the time.
    /// </summary>
    [Fact]
    public void TheSurveyWorkerTakesNoMemoryForAnyScanFromItsFirst()
    {
        var galaxy = Game.Galaxy;
        var densest = Sectors.Densest(galaxy);
        var cloud = FindCloud(galaxy);
        var dense = new SectorContent();
        galaxy.Generate(densest, dense);
        Assert.True(dense.AsteroidCount > 1000);
        var cases = new List<(string Name, ModuleScanRequest Request, Double3 Origin, double Range)>
        {
            ("asteroid, densest sector, first rock", new(ModuleFamily.AsteroidScanner, densest, 0), galaxy.CenterOf(densest), 3000),
            ("asteroid, densest sector, last rock", new(ModuleFamily.AsteroidScanner, densest, dense.AsteroidCount - 1), galaxy.CenterOf(densest), 3000),
            ("asteroid, home sector", new(ModuleFamily.AsteroidScanner, default, 0), galaxy.CenterOf(default), 3000),
            ("gas", new(ModuleFamily.GasScanner, cloud.Anchor), GasCloudQuery.CenterOf(galaxy, cloud), 1500),
        };
        // A pulse reaches up to 27 sectors; one from the middle of a sector walks the ones in range.
        foreach (var origin in new[] { default, densest, new SectorCoord(-1, -1, 1), new SectorCoord(3, 0, 1) })
            cases.Add(($"locator at {origin}", new(ModuleFamily.AsteroidLocator, default), galaxy.CenterOf(origin), 5000));
        cases.Add(("locator across the walls", new(ModuleFamily.AsteroidLocator, default), galaxy.CenterOf(densest) + new Double3(galaxy.SectorSize * 0.5, galaxy.SectorSize * 0.5, galaxy.SectorSize * 0.5), galaxy.SectorSize));

        // Each run is a worker made new, so a first scan is measured as a first scan again after a doubtful reading.
        var probe = new AllocationProbe();
        var first = "";
        var pulsesThatFound = 0;
        while (probe.Run())
        {
            first = "";
            pulsesThatFound = 0;
            using var worker = new ModuleSurveyWorker(galaxy, 1);
            var slot = worker.Slots[0];
            foreach (var (name, request, origin, range) in cases)
            {
                slot.Request = request; slot.Origin = origin; slot.Range = range; slot.Owner = 7;
                var before = probe.Bytes;
                probe.Start();
                worker.Execute(slot);
                probe.Stop();
                if (probe.Bytes > before && first.Length == 0) first = name;
                Assert.Equal(ModuleScanState.Idle, slot.Failure);
                // A pulse may reach no rock from a quiet place; the scans that name a target always find it.
                if (request.Family == ModuleFamily.AsteroidLocator) pulsesThatFound += slot.Count > 0 ? 1 : 0;
                else Assert.True(slot.Count > 0, $"{name}: the scan found nothing, so it measured nothing");
            }
        }

        Assert.True(pulsesThatFound >= 2, "the pulses found nothing, so they measured nothing");
        Assert.True(probe.Bytes == 0, $"the worker took {probe.Bytes} bytes, first at {first}");
    }

    /// <summary>
    /// A whole scan through a real server and client, three times over each family: the request the key makes, the
    /// worker's hand-off, every scanning tick, the publication, the cooldown and the next request. Every tick of the
    /// server and of the client is measured. Found allocating: the first scan took 88 bytes on the tick, in the
    /// default equality comparer that comparing <c>ModuleEquipment</c> builds on first use, and 88 more in the one
    /// that <c>Array.IndexOf</c> builds for connections.
    /// </summary>
    [Theory]
    [InlineData(ModuleFamily.GasScanner)]
    [InlineData(ModuleFamily.AsteroidScanner)]
    [InlineData(ModuleFamily.AsteroidLocator)]
    public void EveryTickOfARepeatedScanTakesNoMemory(ModuleFamily family)
    {
        var probe = new AllocationProbe();
        var completed = 0;
        while (probe.Run())
        {
            completed = 0;
            using var rig = new ModuleScanTests.Rig();
            var input = rig.Clients[0].Input;
            RockRef rock = default;
            var anchor = default(SectorCoord);
            if (family == ModuleFamily.GasScanner)
            {
                var cloud = FindCloud(rig.Server.Galaxy);
                anchor = cloud.Anchor;
                rig.Hulls.State(rig.Hull()).Position = GasCloudQuery.CenterOf(rig.Server.Galaxy, cloud);
            }
            else
                rock = rig.PlaceNearRock();
            // The hull is moved by hand, so the clients learn where it is before the first request.
            for (var i = 0; i < 20; i++) rig.Step();

            for (var round = 0; round < 3; round++)
            {
                var serial = rig.Scan().Serial;
                probe.Start();
                switch (family)
                {
                    case ModuleFamily.GasScanner: input.StartGasScan(anchor); break;
                    case ModuleFamily.AsteroidScanner: input.StartAsteroidScan(rock); break;
                    default: input.PulseAsteroidLocator(); break;
                }

                probe.Stop();
                for (var n = 0; !(rig.Scan().Serial != serial && rig.Scan().State == ModuleScanState.Completed); n++)
                {
                    Assert.True(n < 3000, $"{family} round {round}: the scan did not complete, it is {rig.Scan().State}");
                    if (rig.Scan().State == ModuleScanState.Preparing) Thread.Sleep(1);
                    probe.Start(); rig.Step(); probe.Stop();
                }

                completed++;
                // The cooldown, then a few ticks the client takes the result in.
                while (rig.Tick <= rig.Scan().CooldownUntilTick) { probe.Start(); rig.Step(); probe.Stop(); }
            }
        }

        Assert.Equal(3, completed);
        Assert.True(probe.Bytes == 0, $"{family}: the ticks took {probe.Bytes} bytes");
    }

    /// <summary>
    /// What the interface builds when a result arrives: the gas ledger learning clouds one after another, and the
    /// lookups a locator mark and a gas bar make. Found allocating: the first mark built its class table (56 bytes)
    /// in the frame, the first gas bar its colour table, and the ledger's kind dictionary grew when a cloud was new.
    /// </summary>
    [Fact]
    public void TheInterfaceTakesNoMemoryWhenAResultFirstArrives()
    {
        var galaxy = Game.Galaxy;
        Span<GasCloudPlacement> clouds = stackalloc GasCloudPlacement[64];
        var found = GasCloudQuery.Collect(galaxy, default, 12, clouds);
        Assert.True(found >= 3);
        var anchors = new SectorCoord[found];
        for (var i = 0; i < found; i++) anchors[i] = clouds[i].Anchor;
        var names = galaxy.GasComposition!.Names;
        var sink = 0f;
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            var look = ModuleLook.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "modules.json"));
            var view = new ModuleKnowledgeView();
            var ledger = new GasLedger();

            // A cloud at a time, each a rebuild that meets a kind it has not looked up.
            for (var i = 0; i < found; i++)
            {
                var record = new ModuleObservation { Owner = 1, Family = ModuleFamily.GasScanner, Sector = anchors[i], Index = i & 7, RecordedTick = 10 + i, Quality = ModuleQuality.Common };
                record.Gas.Shares[0] = 0.5f;
                record.Gas.Purity = 0.5f;
                record.Gas.Density = 0.5f;
                view.Records[view.Count++] = record;
                probe.Start();
                ledger.Update(view, galaxy);
                probe.Stop();
                sink += ledger.Share(ledger.At(0), 0) + (float)ledger.Kind(ledger.At(0));
            }

            probe.Start();
            for (var r = -1; r < galaxy.Resources.Length; r++)
                sink += look.ClassOf(galaxy, r);
            for (var g = 0; g < names.Count; g++)
                sink += look.GasColor(names, g);
            probe.Stop();
        }

        Assert.True(probe.Bytes == 0, $"the interface took {probe.Bytes} bytes");
        Assert.True(sink != -1f);
    }
}
