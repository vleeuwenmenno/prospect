using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class VisualBenchmarkTests
{
    [Theory]
    [InlineData(30, "clean")]
    [InlineData(50, "dents")]
    public void PrivateSceneReplicatesPopulationAndRetainedDents(int count, string activity)
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1, VisualBenchmark = new(count, activity, "near") }, content, link.Server);
        using var client = new ClientSession(link.Client, content, "Benchmark");
        for (var tick = 0; tick < 1800; tick++) { ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick); }
        var snapshot = client.AcquireSnapshot();
        Assert.Equal(count, snapshot.HullCount);
        var dented = 0;
        for (var i = 0; i < snapshot.HullCount; i++) if (snapshot.Hulls[i].Visual.Count > 0) dented++;
        Assert.Equal(activity == "clean" ? 0 : count - 1, dented);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ImpactsPublishNewContactSequencesThroughSnapshots()
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1, VisualBenchmark = new(30, "impacts", "near") }, content, link.Server);
        using var client = new ClientSession(link.Client, content, "Contacts");
        var events = new HashSet<(uint, uint)>();
        var late = false;
        for (var tick = 0; tick < 600; tick++)
        {
            ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick);
            var snapshot = client.AcquireSnapshot();
            for (var i = 0; i < snapshot.HullCount; i++)
            {
                var hull = snapshot.Hulls[i];
                if (hull.Visual.Spark.Sequence == 0) continue;
                events.Add((hull.Id, hull.Visual.Spark.Sequence));
                late |= hull.Visual.Spark.Tick > 300;
            }
        }
        Assert.True(events.Count >= 10);
        Assert.True(late);
    }

    [Fact]
    public void ScriptedRepairUsesNormalIntentAndClearsOwnDents()
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1, VisualBenchmark = new(30, "repair", "near") }, content, link.Server);
        using var client = new ClientSession(link.Client, content, "Repair");
        uint old = 0;
        for (var tick = 0; tick < 400; tick++)
        {
            ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick);
            var snapshot = client.AcquireSnapshot();
            if (tick == 120)
            {
                old = snapshot.Hulls[snapshot.OwnHull].Id;
                Assert.True(snapshot.Hulls[snapshot.OwnHull].Visual.Count > 0);
                client.Input.RequestEdit(old);
            }
            if (tick == 180)
            {
                Assert.Equal(EditorPhase.Open, snapshot.Editor.Phase);
                client.Input.RepairEdit(snapshot.Editor.Token);
            }
        }
        var final = client.AcquireSnapshot();
        Assert.NotEqual(old, final.Hulls[final.OwnHull].Id);
        Assert.Equal(0, final.Hulls[final.OwnHull].Visual.Count);
    }

    [Fact]
    public void LargeConnectedPlatePassesAdmission()
    {
        var content = new VisualBenchmarkScene(30, "dents", "near", true).PrepareContent(GameContent.Load());
        Assert.Equal(910, content.Designs[^1].BlockCount);
    }

    [Fact]
    public void RefusesPublicAndUnboundedScenes()
    {
        Assert.Throws<InvalidDataException>(() => new VisualBenchmarkScene(51, "dents", "near").Validate());
        using var link = new LoopbackLink();
        Assert.Throws<InvalidDataException>(() => new ServerHost(new ServerConfig
        { Visibility = ServerVisibility.Public, VisualBenchmark = new(30, "dents", "near") }, GameContent.Load(), link.Server));
    }
}
