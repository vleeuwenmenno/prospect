using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// A shield broken by a real collision on a real server, as the pilot's predicting client, an observer and a
/// player who joins afterwards see it. The server and the clients are ticked by hand, so nothing waits.
/// </summary>
public sealed class ShieldSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void EnergyLimitedRestartAgreesWithPredictionAcrossSnapshots(int lead)
    {
        var content = GameContent.Load();
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
            var snapshot = client.AcquireSnapshot();
            return snapshot.Hulls[snapshot.OwnHull];
        }
        for (var i = 0; i < 20; i++) Step();
        var store = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var slot = -1;
        for (var i = 0; i < store.Slots; i++)
            if (store.IsAliveAt(i) && store.HandleAt(i).Packed == Own().Id) slot = i;
        Assert.True(slot >= 0);
        ref var state = ref store.State(slot);
        state.Shield = 0;
        state.ShieldOffline = true;
        state.ShieldCooldown = 0;
        state.Energy = 0;
        for (var i = 0; i < lead; i++) ((ITickHandler)client).Tick(0);
        var limited = false;
        var restarted = false;
        for (var i = 0; i < 1800; i++)
        {
            Step();
            if (i < 20) continue; // Let the first authoritative break reach prediction.
            var own = Own();
            var expected = store.State(slot);
            var order = store.Info(slot).Order;
            var ahead = (int)Math.Round(own.ShownTick - (tick - 1));
            Assert.InRange(ahead, 0, 16);
            for (var j = 0; j < ahead; j++)
                HullSim.Step(ref expected, ref order, default, store.Flight(slot));
            Assert.Equal(expected.Energy, own.Energy, 4);
            Assert.Equal(expected.Shield, own.Shield, 4);
            Assert.Equal(expected.ShieldOffline, own.ShieldOffline);
            limited |= own.ShieldOffline && own.Shield > 0 && own.Energy < 0.001f;
            restarted |= !own.ShieldOffline;
        }
        Assert.True(limited, "recovery must exercise an energy-limited offline phase");
        Assert.True(restarted);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ThePilotsPowerFlowShowsTheRestartWhileAFullBatteryHidesIt()
    {
        var content = GameContent.Load();
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
            var snapshot = client.AcquireSnapshot();
            return snapshot.Hulls[snapshot.OwnHull];
        }
        for (var i = 0; i < 20; i++) Step();

        // Settled with the shield full: only the base draw, and the surplus is turned away by the full battery.
        var pod = content.Pod.Flight;
        for (var i = 0; i < 60; i++) Step();
        var settled = Own();
        Assert.Equal(pod.IdlePower, settled.Power.Idle, 2);
        Assert.Equal(0f, settled.Power.Shield, 3);
        Assert.Equal(pod.PowerGenerated - pod.IdlePower, settled.Power.Net, 2);

        // Break the shield on the server and top the battery up each tick: the restart draw is in the flow though
        // the battery never moves, and the net turns negative because the restart outruns the generators.
        var store = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var slot = -1;
        for (var i = 0; i < store.Slots; i++)
            if (store.IsAliveAt(i) && store.HandleAt(i).Packed == Own().Id) slot = i;
        Assert.True(slot >= 0);
        ref var state = ref store.State(slot);
        state.Shield = 0;
        state.ShieldOffline = true;
        state.ShieldCooldown = 0;
        var seen = 0f;
        for (var i = 0; i < 120; i++)
        {
            store.State(slot).Energy = pod.EnergyStorage;
            Step();
            seen = Math.Max(seen, Own().Power.Shield);
        }

        Assert.True(seen > 0.9f * pod.ShieldRestartPower, $"the restart draw reached only {seen} MW of {pod.ShieldRestartPower}");
        Assert.True(Own().Power.Net < 0f || !Own().ShieldOffline);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ABrokenShieldStaysOfflineUntilItsThresholdAndEveryClientAgrees()
    {
        // Weak shields break in one pass; stronger blocks keep the emitter alive so it can recharge.
        var content = GameContent.Load();
        content = content.WithSettings(content.SettingCatalog.Build(null, ["collisionShields=Weak", "hullStrength=4"]));
        var pod = content.Pod.Flight;
        using var link = new LoopbackLink();
        using var observerLink = new LoopbackLink();
        using var lateLink = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 3, Settings = content.Settings }, content, link.Server, observerLink.Server, lateLink.Server);
        var clock = new ManualTime();
        using var pilot = new ClientSession(link.Client, content, "Pilot", clock: clock);
        using var observer = new ClientSession(observerLink.Client, content, "Observer", clock: clock);
        ClientSession? late = null;
        long tick = 0, pilotTick = 0, observerTick = 0, lateTick = 0;
        void Step()
        {
            clock.Advance(ManualTime.Tick);
            ((ITickHandler)server).Tick(tick++);
            ((ITickHandler)pilot).Tick(pilotTick++);
            ((ITickHandler)observer).Tick(observerTick++);
            if (late is not null)
                ((ITickHandler)late).Tick(lateTick++);
        }

        HullView Own(ClientSession session)
        {
            var view = session.AcquireSnapshot();
            return view.Hulls[view.OwnHull];
        }

        bool Find(ClientSession session, uint id, out HullView hull)
        {
            var view = session.AcquireSnapshot();
            for (var i = 0; i < view.HullCount; i++)
            {
                if (view.Hulls[i].Id == id)
                {
                    hull = view.Hulls[i];
                    return true;
                }
            }

            hull = default;
            return false;
        }

        for (var i = 0; i < 20; i++)
            Step();

        // The pilot's own vessel is sent straight through the pod, at speed.
        var own = Own(pilot);
        var snapshot = pilot.AcquireSnapshot();
        HullView vessel = default;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Owner == own.Owner && !snapshot.Hulls[i].IsOwn)
                vessel = snapshot.Hulls[i];
        }

        Assert.NotEqual(0u, vessel.Id);
        var through = own.Position + own.Position.RelativeTo(vessel.Position) * 3f;
        pilot.Input.IssueFleetOrder(vessel.Id, new HullOrder { Kind = OrderKind.MoveTo, Target = through });

        // Inputs run ahead of the server, so the prediction replays across the break.
        for (var i = 0; i < 4; i++)
            ((ITickHandler)pilot).Tick(pilotTick++);

        var broke = -1;
        for (var i = 0; i < 60 * 20 && broke < 0; i++)
        {
            Step();
            if (Own(pilot).ShieldOffline)
                broke = i;
        }

        Assert.True(broke >= 0, $"the shield never broke; it holds {Own(pilot).Shield}");
        own = Own(pilot);
        Assert.Equal(0f, own.Shield, 1f);
        Assert.True(own.ShieldHit.Strength > 0);
        Assert.InRange(own.ShieldHitAge, 0f, 0.5f);
        var serial = own.ShieldHit.Serial;

        // Prediction leads authority and the observer draws an interpolated past interval.
        // Compare the same displayed time instead of assuming six frames bridge both offsets.
        var observerTicks = 0;
        HullView seen = default;
        while (observerTicks < 32 && (!Find(observer, own.Id, out seen) || seen.ShownTick < own.ShownTick))
        {
            Step();
            observerTicks++;
        }
        Assert.True(Find(observer, own.Id, out seen));
        Assert.True(seen.ShownTick >= own.ShownTick);
        Assert.Equal(pod.ShieldCapacity, seen.Stats.ShieldCapacity);
        Assert.True(seen.ShieldOffline, $"pilot break at shown {own.ShownTick}, observer at {seen.ShownTick}, authority tick {tick}, observer shield {seen.Shield}");
        Assert.Equal(serial, seen.ShieldHit.Serial);

        // The observer times the break from the server's own count, against the past moment it draws.
        Assert.InRange(seen.ShieldChangeAge, -0.2f, 0.3f);
        Assert.InRange(Own(pilot).ShieldChangeAge, 0f, 0.3f);
        Assert.Equal(own.ShieldHit.X, seen.ShieldHit.X);

        // Each view says which moment it shows. From one publication to a later one the break has aged by the
        // simulation time between the two moments shown, however quickly the two were published.
        for (var i = 0; i < 10; i++)
            Step();
        Assert.True(Find(observer, own.Id, out var later));
        Assert.True(later.ShownTick > seen.ShownTick);
        Assert.Equal((later.ShownTick - seen.ShownTick) * HullSim.Dt, later.ShieldChangeAge - seen.ShieldChangeAge, 3);

        // Someone joining two seconds later is told the shield is down, but the hit is too old to show.
        for (var i = 0; i < 120; i++)
            Step();
        late = new ClientSession(lateLink.Client, content, "Late", clock: clock);
        try
        {
            var threshold = pod.ShieldCapacity * pod.ShieldRestartFraction;
            var restarted = -1;
            var restartedAt = 0d;
            var lateSaw = false;
            for (var i = 0; i < 60 * 30 && restarted < 0; i++)
            {
                Step();
                var now = Own(pilot);
                if (!now.ShieldOffline)
                {
                    restarted = i;
                    restartedAt = now.ShownTick;
                    Assert.True(now.Shield >= threshold, $"online at {now.Shield} of {threshold}");
                    Assert.InRange(now.ShieldChangeAge, 0f, 0.1f);
                }
                else if (Find(late, own.Id, out var lateView))
                {
                    if (!lateSaw)
                    {
                        Assert.True(lateView.ShieldHitAge > 1.5f, $"a late joiner sees a hit {lateView.ShieldHitAge} s old");
                        Assert.True(lateView.ShieldChangeAge > Prospect.Client.World.ShieldLook.MaxEffectSeconds, $"a late joiner sees a break {lateView.ShieldChangeAge} s old");
                    }
                    lateSaw = true;
                    Assert.True(lateView.ShieldOffline);
                }
            }

            Assert.True(lateSaw);
            Assert.True(restarted >= 0, "the shield never came back");

            // The pod may have been knocked about more than once; the last hit sets the delay, so the
            // restart is at least the delay plus the time to charge to the threshold after the break.
            var seconds = (restarted + observerTicks + 120) * HullSim.Dt;
            Assert.True(seconds >= pod.ShieldDelay + threshold / pod.ShieldRecharge - 0.2f, $"back after {seconds} s");

            // Both remote views must reach the interval where prediction first showed the restart.
            for (var i = 0; i < 32; i++)
            {
                if (Find(observer, own.Id, out seen) && seen.ShownTick >= restartedAt
                    && Find(late, own.Id, out var lateView) && lateView.ShownTick >= restartedAt) break;
                Step();
            }
            Assert.True(Find(observer, own.Id, out seen));
            Assert.True(seen.ShownTick >= restartedAt);
            Assert.False(seen.ShieldOffline);
            Assert.True(Find(late, own.Id, out seen));
            Assert.True(seen.ShownTick >= restartedAt);
            Assert.False(seen.ShieldOffline);
            Assert.Null(server.Fault);
        }
        finally
        {
            late.Dispose();
        }
    }
}
