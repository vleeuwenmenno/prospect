using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Manual real-session ticks exercise the authoritative and predicted jump gates without windows.</summary>
public sealed class JumpCalculationSessionTests
{
    private static GameContent ReplicationContent()
    {
        using var files = new ContentCopy();
        files.Replace("rules/exploration.json", "5000", "2000000");
        // These replication fixtures deliberately see across the whole jump, independent of gas.
        files.Replace("rules/exploration.json", "\"gasOpticalPenalty\": 1.0", "\"gasOpticalPenalty\": 0.0");
        var blocks = Path.Combine(files.Root, "blocks", "blocks.json");
        File.WriteAllText(blocks, File.ReadAllText(blocks).Replace("\"ftlMaxSectors\": 10", "\"ftlMaxSectors\": 25"));
        return files.Load();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public void PendingInputsPreserveCalculationAcrossReconciliationAndRetargetOnce(int lead)
    {
        var content = ReplicationContent();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, link.Server);
        using var client = new ClientSession(link.Client, content, "Pilot");
        long tick = 0;
        void Step()
        {
            ((ITickHandler)server).Tick(tick++);
            ((ITickHandler)client).Tick(0);
        }
        HullView Own()
        {
            var view = client.AcquireSnapshot();
            return view.Hulls[view.OwnHull];
        }
        for (var i = 0; i < 10; i++) Step();
        var target = Own().Position + new System.Numerics.Vector3(0, 0, -1e6f);
        var replacement = target + new System.Numerics.Vector3(0, 0, -1000);
        client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
        // The server's input credit keeps these extra inputs pending through later snapshots.
        for (var i = 0; i < lead; i++) ((ITickHandler)client).Tick(0);
        var previous = 0f;
        var resets = 0;
        var arrived = false;
        for (var i = 0; i < 1300; i++)
        {
            if (i == 300)
                client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = replacement });
            Step();
            var own = Own();
            if (own.Jump == JumpPhase.Charge)
            {
                if (own.JumpCalculation + 0.0001f < previous)
                {
                    resets++;
                    Assert.InRange(i, 300, 300 + lead);
                }
                previous = own.JumpCalculation;
                if (i == 299) Assert.InRange(previous, 4.9f, 5.2f);
                if (i == 600) Assert.InRange(previous, 4.9f, 5.2f);
            }
            if (own.Jump == JumpPhase.Tunnel)
            {
                Assert.True(i > 1000);
                Assert.True(own.Position.RelativeTo(replacement).Length() < 1);
                arrived = true;
                break;
            }
        }
        Assert.True(arrived);
        Assert.Equal(1, resets);
        var duration = Own().JumpTunnelSeconds;
        Assert.InRange(duration, 28.02f, 28.03f);
        for (var i = 0; i < (int)((duration - 0.25f) / HullSim.Dt); i++)
        {
            Step();
            Assert.Equal(JumpPhase.Tunnel, Own().Jump);
            Assert.Equal(duration, Own().JumpTunnelSeconds);
        }
        for (var i = 0; i < 40 && Own().Jump == JumpPhase.Tunnel; i++) Step();
        Assert.Equal(JumpPhase.Arrive, Own().Jump);
        Assert.Equal(duration, Own().JumpTunnelSeconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1e100")]
    public void InvalidCalculationDurationFailsAtContentLoad(string number)
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "content", "blocks", "blocks.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(source))!;
            json["rules"]!["ftlCalculateSeconds"] = System.Text.Json.Nodes.JsonNode.Parse(number);
            File.WriteAllText(path, json.ToJsonString());
            Assert.Contains("ftlCalculateSeconds", Assert.Throws<InvalidDataException>(() => BlockCatalog.Load(path)).Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1e100")]
    public void InvalidSpeedFailsAtContentLoad(string number)
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "content", "blocks", "blocks.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(source))!;
            json["rules"]!["ftlSpeed"] = System.Text.Json.Nodes.JsonNode.Parse(number);
            File.WriteAllText(path, json.ToJsonString());
            Assert.Contains("ftlSpeed", Assert.Throws<InvalidDataException>(() => BlockCatalog.Load(path)).Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AuthoritativeSlowChargeWaitsForEnergyAfterCalculationFinishes()
    {
        var content = ReplicationContent();
        content = content.WithSettings(content.SettingCatalog.Build(null, ["jumpChargeSpeed=0.25"]));
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1, Settings = content.Settings }, content, link.Server);
        using var client = new ClientSession(link.Client, content, "Pilot");
        long tick = 0;
        void Step(int count)
        {
            for (var i = 0; i < count; i++, tick++)
            {
                ((ITickHandler)server).Tick(tick);
                ((ITickHandler)client).Tick(0);
            }
        }
        HullView Own()
        {
            var view = client.AcquireSnapshot();
            return view.Hulls[view.OwnHull];
        }
        Step(10);
        var target = Own().Position + new System.Numerics.Vector3(0, 0, -1e6f);
        client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
        Step(780);
        Assert.Equal(JumpPhase.Charge, Own().Jump);
        Assert.Equal(12f, Own().JumpCalculation);
        Assert.InRange(Own().JumpTimer, 1, content.Pod.Flight.FtlEnergy - 1);
        var expectedTicks = (int)MathF.Ceiling(content.Pod.Flight.FtlEnergy / content.Pod.Flight.FtlPower / HullSim.Dt);
        Step(expectedTicks - 780 + 5);
        Assert.Equal(JumpPhase.Tunnel, Own().Jump);
        Assert.Equal(12f, Own().JumpCalculation);
        Step(12);
        Assert.Equal(JumpPhase.Tunnel, Own().Jump);
        Assert.True(Own().Position.RelativeTo(target).Length() < 1);
    }

    [Fact]
    public void OwnRemoteAndAutomaticFleetCarryCalculationThrustLeavesItAndHaltRestartsIt()
    {
        var content = ReplicationContent();
        using var link = new LoopbackLink();
        using var observerLink = new LoopbackLink();
        using var lateLink = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 3 }, content, link.Server, observerLink.Server, lateLink.Server);
        using var client = new ClientSession(link.Client, content, "Pilot");
        using var observer = new ClientSession(observerLink.Client, content, "Observer");
        long tick = 0;
        void Step(int count)
        {
            for (var i = 0; i < count; i++, tick++)
            {
                ((ITickHandler)server).Tick(tick);
                // The threaded client clock is stopped; keep its view-expiry clock at zero too.
                ((ITickHandler)client).Tick(0);
                ((ITickHandler)observer).Tick(0);
            }
        }
        HullView Own()
        {
            var view = client.AcquireSnapshot();
            return view.Hulls.Take(view.HullCount).Single(h => h.IsOwn);
        }
        HullView Seen(ClientSession session, uint id)
        {
            var view = session.AcquireSnapshot();
            return view.Hulls.Take(view.HullCount).Single(h => h.Id == id);
        }
        Step(30);
        var own = Own();
        var snapshot = client.AcquireSnapshot();
        var fleet = snapshot.Hulls.Take(snapshot.HullCount).First(h => h.Owner == snapshot.PlayerId && !h.IsOwn && content.Designs[h.Design].Flight.Ftl != FtlKind.None);
        var target = own.Position + new System.Numerics.Vector3(0, 0, -1e6f);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = target };
        client.Input.IssueOrder(order);
        client.Input.IssueFleetOrder(fleet.Id, new HullOrder { Kind = OrderKind.Jump, Target = fleet.Position + new System.Numerics.Vector3(0, 0, -1e6f) });
        Step(120);
        Assert.Equal(JumpPhase.Charge, Own().Jump);
        Assert.InRange(Own().JumpCalculation, 1.8f, 2.1f);
        Assert.InRange(Seen(observer, own.Id).JumpCalculation, 1.7f, 2.1f);
        Assert.InRange(Seen(client, fleet.Id).JumpCalculation, 1.7f, 2.1f);

        // Thrust and boost are not the cancel: the calculation carries on, on the server and in prediction.
        client.Input.Submit(new PlayerControls { Forward = 1, Boost = true });
        Step(12);
        Assert.Equal(JumpPhase.Charge, Own().Jump);
        Assert.InRange(Own().JumpCalculation, 2.0f, 2.4f);
        client.Input.Submit(default);
        client.Input.IssueOrder(default);
        Step(12);
        Assert.Equal(JumpPhase.None, Own().Jump);
        Assert.Equal(0, Own().JumpCalculation);
        client.Input.Submit(default);
        client.Input.IssueOrder(order);
        Step(60);
        Assert.InRange(Own().JumpCalculation, 0.8f, 1.1f);
        var replacement = new HullOrder { Kind = OrderKind.Jump, Target = target + new System.Numerics.Vector3(0, 0, -1000) };
        client.Input.IssueOrder(replacement);
        Step(60);
        Assert.InRange(Own().JumpCalculation, 0.8f, 1.1f);
        Step(570);
        Assert.Equal(JumpPhase.Charge, Own().Jump);
        Assert.InRange(Own().JumpCalculation, 10.3f, 10.6f);
        Assert.Equal(content.Pod.Flight.FtlEnergy, Own().JumpTimer);
        Assert.Equal(JumpPhase.Tunnel, Seen(client, fleet.Id).Jump);
        Step(100);
        Assert.Equal(JumpPhase.Tunnel, Own().Jump);
        Assert.Equal(12, Own().JumpCalculation);
        Assert.Equal(12, Seen(observer, own.Id).JumpCalculation);
        Assert.InRange(Own().JumpTunnelSeconds, 28.02f, 28.03f);
        Assert.Equal(Own().JumpTunnelSeconds, Seen(observer, own.Id).JumpTunnelSeconds);
        Assert.Equal(28, Seen(client, fleet.Id).JumpTunnelSeconds);
        Assert.Equal(28, Seen(observer, fleet.Id).JumpTunnelSeconds);
        using var late = new ClientSession(lateLink.Client, content, "Late");
        for (var i = 0; i < 30; i++)
        {
            Step(1);
            ((ITickHandler)late).Tick(0);
        }
        Assert.Equal(JumpPhase.Tunnel, Seen(late, own.Id).Jump);
        Assert.Equal(Own().JumpTunnelSeconds, Seen(late, own.Id).JumpTunnelSeconds);
        Assert.Equal(28, Seen(late, fleet.Id).JumpTunnelSeconds);

        Assert.True(Own().Position.RelativeTo(replacement.Target).Length() < 1);
    }
}
