using System.Reflection;
using System.Text.Json.Nodes;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class ModuleScanTests
{
    internal sealed class Rig : IDisposable
    {
        internal readonly GameContent Content;
        internal readonly ServerHost Server;
        internal readonly ClientSession[] Clients;
        internal readonly HullStore Hulls;
        private readonly ManualTime _clock = new();
        internal long Tick;
        internal Rig(bool equipped = true, string quality = "Common")
        {
            using var copy = new ContentCopy();
            if (equipped)
            {
                var pod = JsonNode.Parse(copy.Read("ships/command_pod.json"))!;
                var blocks = pod["blocks"]!.AsArray();
                for (var i = 0; i < 3; i++) blocks.Add(new JsonObject { ["at"] = new JsonArray(-3 - i, 0, 0), ["type"] = new[] { "gasScanner", "asteroidScanner", "asteroidLocator" }[i] });
                copy.Write("ships/command_pod.json", pod.ToJsonString());
                var catalogue = JsonNode.Parse(copy.Read("blocks/blocks.json"))!;
                foreach (var id in new[] { "gasScanner", "asteroidScanner", "asteroidLocator" }) catalogue["blocks"]![id]!["module"]!["quality"] = quality;
                copy.Write("blocks/blocks.json", catalogue.ToJsonString());
            }
            Content = copy.Load();
            var links = new[] { new LoopbackLink(), new LoopbackLink() };
            Server = new ServerHost(new ServerConfig { MaxPlayers = 2, Clock = _clock }, Content, links[0].Server, links[1].Server);
            Clients = links.Select((l, i) => new ClientSession(l.Client, Content, $"Module{i}", clock: _clock)).ToArray();
            Hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
            Until(() => Clients.All(c => c.AcquireSnapshot().OwnHull >= 0));
        }
        internal uint Owner(int i = 0) => Clients[i].AcquireSnapshot().PlayerId;
        internal int Hull(int i = 0)
        {
            var snapshot = Clients[i].AcquireSnapshot();
            return (int)(snapshot.Hulls[snapshot.OwnHull].Id & 65535);
        }
        internal ModuleScanView Scan(int i = 0)
        {
            var connections = (Array)typeof(ServerHost).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
            foreach (var connection in connections)
                if ((uint)connection!.GetType().GetField("PlayerId")!.GetValue(connection)! == Owner(i))
                    return (ModuleScanView)connection.GetType().GetField("ModuleScan")!.GetValue(connection)!;
            return default;
        }
        internal void Step()
        {
            _clock.Advance(ManualTime.Tick);
            ((ITickHandler)Server).Tick(Tick);
            foreach (var client in Clients) ((ITickHandler)client).Tick(Tick);
            Tick++;
        }
        internal void Until(Func<bool> condition)
        {
            for (var n = 0; n < 4000 && !condition(); n++) { Step(); Thread.Sleep(1); }
            Assert.True(condition());
        }
        internal RockRef PlaceNearRock()
        {
            var sector = new SectorContent();
            Server.Galaxy.Generate(default, sector);
            Assert.True(sector.AsteroidCount > 0);
            var rock = sector.Asteroids[0];
            Hulls.State(Hull()).Position = Server.Galaxy.CenterOf(default) + rock.LocalPosition + new Double3(0, rock.Radius + 200, 0);
            return new(default, 0);
        }
        public void Dispose() { foreach (var client in Clients) client.Dispose(); Server.Dispose(); }
    }

    [Fact]
    public void AsteroidScanRequiresEquipmentRangeTimePowerAndCooldownAndKeepsPrivateKnowledge()
    {
        using var rig = new Rig();
        var rock = rig.PlaceNearRock();
        rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.AsteroidScanner, rock.Sector, rock.Index));
        rig.Until(() => rig.Scan().State == ModuleScanState.Scanning);
        var scan = rig.Scan();
        Assert.Equal(300, scan.CompletesTick - scan.StartedTick);
        Assert.Empty(rig.Server.ModuleKnowledge(rig.Owner()).ToArray());
        rig.Until(() => rig.Scan().State == ModuleScanState.Completed);
        var knowledge = rig.Server.ModuleKnowledge(rig.Owner());
        Assert.Equal(1, knowledge.Length);
        Assert.True(knowledge[0].Mass > 0);
        Assert.True(knowledge[0].Radius > 0);
        Assert.Empty(rig.Server.ModuleKnowledge(rig.Owner(1)).ToArray());
        rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.AsteroidScanner, rock.Sector, rock.Index));
        Assert.Equal(ModuleScanState.Cooldown, rig.Scan().State);
        rig.Until(() => rig.Tick > rig.Scan().CooldownUntilTick);
        rig.Hulls.State(rig.Hull()).Position += new Double3(10000, 0, 0);
        rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.AsteroidScanner, rock.Sector, rock.Index));
        rig.Until(() => rig.Scan().State == ModuleScanState.OutOfRange);
    }

    [Fact]
    public void NoEquipmentAndNoPowerAreExplicitRefusals()
    {
        using (var rig = new Rig(false))
        {
            rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.AsteroidLocator, default));
            Assert.Equal(ModuleScanState.NoEquipment, rig.Scan().State);
        }
        using (var rig = new Rig())
        {
            rig.Hulls.State(rig.Hull()).Energy = 0;
            rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.AsteroidLocator, default));
            Assert.Equal(ModuleScanState.NoPower, rig.Scan().State);
        }
    }

    [Fact]
    public void LocatorPublishesOnlyCoarseResultsAndQualityChangesTime()
    {
        using var rig = new Rig(quality: "Rare");
        rig.PlaceNearRock();
        rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.AsteroidLocator, default));
        rig.Until(() => rig.Scan().State == ModuleScanState.Scanning);
        Assert.Equal(84, rig.Scan().CompletesTick - rig.Scan().StartedTick);
        rig.Until(() => rig.Scan().State == ModuleScanState.Completed);
        var records = rig.Server.ModuleKnowledge(rig.Owner());
        Assert.True(records.Length > 0);
        foreach (var record in records)
        { Assert.Equal(0, record.Grade); Assert.Equal(0, record.Mass); Assert.Equal(0, record.Radius); }
    }

    [Fact]
    public void GasScanReturnsTheSampledCloudRegion()
    {
        using var rig = new Rig();
        var galaxy = rig.Server.Galaxy;
        GasCloudPlacement placement = default;
        var found = false;
        for (var x = -150; x < 150 && !found; x++)
            if (galaxy.GasClouds!.TryGetCloud(galaxy, new(x, 0, -77), out var cloud))
            { placement = new(new(x, 0, -77), cloud); found = true; }
        Assert.True(found);
        rig.Hulls.State(rig.Hull()).Position = GasCloudQuery.CenterOf(galaxy, placement);
        rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.GasScanner, placement.Anchor));
        rig.Until(() => rig.Scan().State == ModuleScanState.Completed);
        var record = rig.Server.ModuleKnowledge(rig.Owner())[0];
        Assert.Equal(placement.Anchor, record.Sector);
        Assert.InRange(record.Gas.Purity, 0, 1);
        Assert.True(record.Gas.Density > 0);
    }

    [Fact]
    public void SharedPowerAndDisclosureRulesAllocateNothing()
    {
        var equipment = GameContent.Load().Blocks.Get("gasScanner").Module;
        var selected = ModuleEquipment.Of(1, equipment);
        var record = new ModuleObservation { Family = ModuleFamily.GasScanner };
        record.Gas.Shares[0] = .92f; record.Gas.Shares[1] = .08f;
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            var energy = 10f;
            ModuleScanRules.Spend(ref energy, selected);
            ModuleScanRules.Disclose(ref record, selected);
        }
        Assert.Equal(0, probe.Bytes);
        var empty = 0f;
        Assert.False(ModuleScanRules.Spend(ref empty, selected));
        Assert.Equal(0, empty);
    }
}
