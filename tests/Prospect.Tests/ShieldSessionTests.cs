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
    [Fact]
    public void ABrokenShieldStaysOfflineUntilItsThresholdAndEveryClientAgrees()
    {
        // Collisions hit hard and drain the shield four times as fast, so one pass breaks it.
        var content = GameContent.Load();
        content = content.WithSettings(content.SettingCatalog.Build(null, ["collisionShields=Weak"]));
        var pod = content.Pod.Flight;
        using var link = new LoopbackLink();
        using var observerLink = new LoopbackLink();
        using var lateLink = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 3, Settings = content.Settings }, content, link.Server, observerLink.Server, lateLink.Server);
        using var pilot = new ClientSession(link.Client, content, "Pilot");
        using var observer = new ClientSession(observerLink.Client, content, "Observer");
        ClientSession? late = null;
        long tick = 0;
        void Step()
        {
            ((ITickHandler)server).Tick(tick++);
            ((ITickHandler)pilot).Tick(0);
            ((ITickHandler)observer).Tick(0);
            if (late is not null)
                ((ITickHandler)late).Tick(0);
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
            ((ITickHandler)pilot).Tick(0);

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

        // The observer sees the same break and the same hit, through snapshots alone.
        for (var i = 0; i < 6; i++)
            Step();
        Assert.True(Find(observer, own.Id, out var seen));
        Assert.True(seen.ShieldOffline);
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
        late = new ClientSession(lateLink.Client, content, "Late");
        try
        {
            var threshold = pod.ShieldCapacity * pod.ShieldRestartFraction;
            var restarted = -1;
            var lateSaw = false;
            for (var i = 0; i < 60 * 30 && restarted < 0; i++)
            {
                Step();
                var now = Own(pilot);
                if (!now.ShieldOffline)
                {
                    restarted = i;
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
            var seconds = (restarted + 6 + 120) * HullSim.Dt;
            Assert.True(seconds >= pod.ShieldDelay + threshold / pod.ShieldRecharge - 0.2f, $"back after {seconds} s");

            // Everyone agrees it is back.
            for (var i = 0; i < 6; i++)
                Step();
            Assert.True(Find(observer, own.Id, out seen));
            Assert.False(seen.ShieldOffline);
            Assert.True(Find(late, own.Id, out seen));
            Assert.False(seen.ShieldOffline);
            Assert.Null(server.Fault);
        }
        finally
        {
            late.Dispose();
        }
    }
}
