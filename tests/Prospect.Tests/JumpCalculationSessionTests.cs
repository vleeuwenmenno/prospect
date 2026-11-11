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
    [Fact]
    public void AuthoritativeSlowChargeWaitsForEnergyAfterCalculationFinishes()
    {
        var content = GameContent.Load();
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
    public void OwnRemoteAndAutomaticFleetCarryCalculationAndThrustRestartsIt()
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var observerLink = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, content, link.Server, observerLink.Server);
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

        client.Input.Submit(new PlayerControls { Forward = 1 });
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
        Assert.True(Own().Position.RelativeTo(replacement.Target).Length() < 1);
    }
}
