using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The debug scrape through a real server and real client sessions ticked by hand over in-process links: what the
/// server takes, what it refuses, and what every client that has the rock in view ends up holding.
/// </summary>
public sealed class RockEditSessionTests
{
    /// <summary>The server's end of a link, able to hold a client back so one can join later.</summary>
    private sealed class Wire(ITransport inner) : ITransport, ITransportHandler
    {
        internal bool Hold;
        private ITransportHandler? _handler;
        public string Description => inner.Description;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => inner.IsInProcess;

        public bool Poll(ITransportHandler handler, ReceiveBudget budget)
        {
            if (Hold) return true;
            _handler = handler;
            return inner.Poll(this, budget);
        }

        public void OnConnected(ITransport transport, int peer) => _handler!.OnConnected(this, peer);

        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => _handler!.OnDisconnected(this, peer, reason);

        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) => _handler!.OnMessage(this, peer, channel, payload);

        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload) => inner.Send(peer, channel, payload);

        public void Disconnect(int peer) => inner.Disconnect(peer);

        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);

        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);

        public void Dispose() { }
    }

    /// <summary>A clock that moves only when the test says so: one step is one tick, however fast the machine is.</summary>
    private sealed class StepClock : TimeProvider
    {
        private long _now = 1_000_000;
        public override long TimestampFrequency => 1_000_000_000;
        public override long GetTimestamp() => Volatile.Read(ref _now);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, (long)(by.TotalSeconds * TimestampFrequency));
    }

    private sealed class Rig : IDisposable
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly LoopbackLink[] _links;
        private readonly Wire[] _wires;
        private readonly long[] _joined;
        private readonly StepClock _time = new();
        internal readonly GameContent Content = GameContent.Load();
        internal readonly ServerHost Server;
        internal readonly ClientSession?[] Clients;
        internal readonly HullStore Hulls;
        internal readonly RockField Rocks;
        internal long Tick;

        internal Rig(int clients, int joined, bool debugFill)
        {
            _links = new LoopbackLink[clients];
            _wires = new Wire[clients];
            Clients = new ClientSession?[clients];
            _joined = new long[clients];
            for (var i = 0; i < clients; i++)
            {
                _links[i] = new LoopbackLink();
                _wires[i] = new Wire(_links[i].Server) { Hold = i >= joined };
            }

            Server = new ServerHost(new ServerConfig { Clock = _time, AllowDebugFill = debugFill }, Content, [.. _wires]);
            Hulls = (HullStore)typeof(ServerHost).GetField("_hulls", Hidden)!.GetValue(Server)!;
            Rocks = (RockField)typeof(ServerHost).GetField("_rocks", Hidden)!.GetValue(Server)!;
            for (var i = 0; i < joined; i++) Join(i);
            Assert.True(Until(() => Enumerable.Range(0, joined).All(i => Snap(i).HullCount > 0)), "the clients should be in space");
        }

        /// <summary>Starts a client whose link the server has not looked at yet.</summary>
        internal void Join(int client)
        {
            Clients[client] = new ClientSession(_links[client].Client, Content, "Player " + client, clock: _time);
            _joined[client] = Tick;
            _wires[client].Hold = false;
        }

        internal void Step(int count = 1)
        {
            for (var n = 0; n < count; n++)
            {
                ((ITickHandler)Server).Tick(Tick);
                for (var i = 0; i < Clients.Length; i++)
                    if (Clients[i] is { } client) ((ITickHandler)client).Tick(Tick - _joined[i]);
                Tick++;
                _time.Advance(TimeSpan.FromSeconds(TickLoop.SimulationDt));
            }
        }

        /// <summary>Steps until a condition holds, or the ticks or the seconds run out. A client's replay of a long list runs on a worker, which takes real time.</summary>
        internal bool Until(Func<bool> condition, int steps = 2400, double seconds = 30)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; i < steps && !condition() && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds < seconds; i++)
            {
                Step();
                Thread.Sleep(1);
            }

            return condition();
        }

        internal SessionSnapshot Snap(int client) => Clients[client]!.AcquireSnapshot();
        internal PlayerInputBridge Input(int client) => Clients[client]!.Input;
        internal uint Own(int client) { var s = Snap(client); return s.Hulls[s.OwnHull].Id; }
        internal RockEdits ServerEdits => Server.RockEdits;

        /// <summary>The client's own edit list and bricks, which only the world inside its session holds.</summary>
        internal (RockEditLog Log, RockEdits Edits, bool Settled) ClientEdits(int client)
        {
            var world = typeof(ClientSession).GetField("_world", Hidden)!.GetValue(Clients[client]!)!;
            var edits = (RockEdits)world.GetType().GetField("_rockEdits", Hidden)!.GetValue(world)!;
            var settled = (bool)world.GetType().GetProperty("RockEditsSettled", Hidden | BindingFlags.Public)!.GetValue(world)!;
            return (edits.Log, edits, settled);
        }

        /// <summary>Puts a client's flown hull, at rest, at a place given in the rock's own frame.</summary>
        internal void Place(int client, RockRef rock, Vector3 inRockFrame)
        {
            var asteroid = Rocks.Sector(rock.Sector).Asteroids[rock.Index];
            ref var state = ref Hulls.State((int)(Own(client) & 0xFFFF));
            state.Position = Content.Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition + Vector3.Transform(inRockFrame, asteroid.Rotation);
            state.Velocity = Vector3.Zero;
            state.AngularVelocity = Vector3.Zero;
        }

        /// <summary>
        /// Puts every hull a client owns, not only the one it flies, far from a rock: a player's sensors are all its
        /// hulls', and the rest of its fleet starts near home, which a rock may be within a few kilometres of.
        /// </summary>
        internal void PlaceFleet(int client, RockRef rock, Vector3 inRockFrame)
        {
            var asteroid = Rocks.Sector(rock.Sector).Asteroids[rock.Index];
            var owner = Hulls.Info((int)(Own(client) & 0xFFFF)).Owner;
            var n = 0;
            for (var i = 0; i < Hulls.Slots; i++)
            {
                if (!Hulls.IsAliveAt(i) || Hulls.Info(i).Owner != owner) continue;
                ref var state = ref Hulls.State(i);
                state.Position = Content.Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition + Vector3.Transform(inRockFrame + new Vector3(0, 0, 200f * n++), asteroid.Rotation);
                state.Velocity = Vector3.Zero;
                state.AngularVelocity = Vector3.Zero;
            }

            Assert.True(n > 0, "the player has a hull to place");
        }

        /// <summary>A scrapable rock of at least this radius, the nearest to home that has one.</summary>
        internal (RockRef Rock, Asteroid Asteroid) NearbyRock(float minRadius)
        {
            var order = new List<SectorCoord>();
            for (var x = -6; x <= 6; x++)
                for (var z = -6; z <= 6; z++)
                    order.Add(new SectorCoord(x, 0, z));
            foreach (var coord in order.OrderBy(c => Math.Abs(c.X) + Math.Abs(c.Z)))
            {
                var found = RockEditSaveTests.ScrapableRocks(Rocks, coord, minRadius);
                if (found.Count > 0) return found[0];
            }

            throw new InvalidOperationException("No rock near home.");
        }

        public void Dispose()
        {
            foreach (var client in Clients) client?.Dispose();
            Server.Dispose();
            foreach (var link in _links) link.Dispose();
        }
    }

    /// <summary>A debug scrape request of several rays along one line over a rock's surface, <paramref name="spacing"/> metres apart.</summary>
    private static RockScrapeRequest Request(RockRef rock, Asteroid asteroid, Vector3 direction, int rays, float spacing = 0.5f, float radiusMetres = 1f, float offset = 0f)
    {
        var request = new RockScrapeRequest { Rock = rock, RadiusMm = (ushort)(radiusMetres * 1000), Rays = rays };
        var tangent = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        for (var i = 0; i < rays; i++)
        {
            var d = Vector3.Normalize(direction * asteroid.Radius + tangent * (offset + i * spacing));
            request.Origins[i] = d * (asteroid.Radius * AsteroidShape.RadiusAlong(asteroid.Shape, d) + 10f);
            request.Directions[i] = -d;
        }

        return request;
    }

    private static Vector3 Up => Vector3.Normalize(new Vector3(0.3f, 1f, -0.4f));

    private static float Surface(Asteroid asteroid, Vector3 direction) => asteroid.Radius * AsteroidShape.RadiusAlong(asteroid.Shape, Vector3.Normalize(direction));

    [Fact]
    public void ADebugScrapeIsMadeByTheServerAndEveryEditAndBrickReachesTheClient()
    {
        using var rig = new Rig(1, 1, debugFill: true);
        var (rock, asteroid) = rig.NearbyRock(15f);
        rig.Place(0, rock, Up * (Surface(asteroid, Up) + 25f));
        rig.Step(3);

        var request = Request(rock, asteroid, Up, 17);
        rig.Input(0).DebugScrape(request);
        Assert.True(rig.Until(() => rig.ServerEdits.Log.Count > 0), "the server should take the request");
        var server = rig.ServerEdits;

        // Seventeen rays make sixteen scrapes, each from one ray's hit to the next, and the server holds them whole.
        Assert.Equal(16, server.Log.Count);
        var slot = server.Log.SlotOf(rock);
        Assert.Equal(16, server.Log.EditsOf(slot));
        var previous = default(RockScrape);
        var first = true;
        for (var index = server.Log.FirstOf(slot); index >= 0; index = server.Log.NextOf(index))
        {
            ref readonly var edit = ref server.Log.EditAt(index);
            Assert.Equal(request.RadiusMm, edit.RadiusMm);
            Assert.True(rig.Content.RockEdits.Valid(edit, asteroid.Radius));
            if (!first) Assert.True(edit.StartsAtEndOf(previous), "a stroke of rays is one chain");
            previous = edit;
            first = false;
        }

        // The cut is where the rays hit: the surface point under the first ray is inside a metre-radius capsule that
        // reaches 0.4 m under the surface.
        Assert.True(server.CutOf(rock).Sample(Vector3.Normalize(request.Directions[0] * -1) * Surface(asteroid, request.Origins[0]), out _) < -0.2f);

        // The client holds the same list, and once it has taken it into its bricks (a long list is replayed on a worker) the same bricks.
        Assert.True(rig.Until(() => rig.ClientEdits(0).Settled && rig.ClientEdits(0).Log.Count == 16), "the client should have the edits in its bricks");
        var (log, client, _) = rig.ClientEdits(0);
        Assert.Equal(1, log.RockCount);
        var clientSlot = log.SlotOf(rock);
        Assert.Equal(16, log.EditsOf(clientSlot));
        Assert.Equal(Walk(server.Log, slot), Walk(log, clientSlot));
        Assert.Equal(16, client.Bricks.Applied(clientSlot));
        Assert.Equal(server.Bricks.Hash(slot), client.Bricks.Hash(clientSlot));
        Assert.Equal(server.Bricks.BrickCount(slot), client.Bricks.BrickCount(clientSlot));
        Assert.False(client.Bricks.Overflowed);

        // The session's snapshot carries the same log for the renderer to read.
        Assert.Same(log, rig.Snap(0).RockEdits);
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void AShortScrapeIsReplayedInlineAndSettlesAtOnce()
    {
        using var rig = new Rig(1, 1, debugFill: true);
        var (rock, asteroid) = rig.NearbyRock(15f);
        rig.Place(0, rock, Up * (Surface(asteroid, Up) + 25f));
        rig.Step(3);

        // Three rays make two scrapes, below what a tick takes in on the simulation thread.
        rig.Input(0).DebugScrape(Request(rock, asteroid, Up, 3));
        Assert.True(rig.Until(() => rig.ServerEdits.Log.Count == 2));
        Assert.True(rig.Until(() => rig.ClientEdits(0).Log.Count == 2 && rig.ClientEdits(0).Settled));
        var (log, client, _) = rig.ClientEdits(0);
        Assert.Equal(rig.ServerEdits.Bricks.Hash(rig.ServerEdits.Log.SlotOf(rock)), client.Bricks.Hash(log.SlotOf(rock)));
    }

    [Fact]
    public void WithoutDebugFillNothingIsScraped()
    {
        using var rig = new Rig(1, 1, debugFill: false);
        var (rock, asteroid) = rig.NearbyRock(15f);
        rig.Place(0, rock, Up * (Surface(asteroid, Up) + 25f));
        rig.Step(3);

        rig.Input(0).DebugScrape(Request(rock, asteroid, Up, 5));
        rig.Step(120);
        Assert.Equal(0, rig.ServerEdits.Log.Count);
        Assert.Equal(0, rig.ServerEdits.Bricks.Used);
        Assert.Equal(0, rig.ClientEdits(0).Log.Count);
        Assert.Null(rig.Server.Fault);
    }

    [Theory]
    [InlineData(550f, true)]
    [InlineData(650f, false)]
    [InlineData(5000f, false)]
    public void ARockTooFarFromTheHullIsRefused(float distance, bool taken)
    {
        using var rig = new Rig(1, 1, debugFill: true);
        var (rock, asteroid) = rig.NearbyRock(15f);
        var range = rig.Content.RockEdits.DebugRangeMetres;
        Assert.Equal(600f, range);

        // The hull hangs straight above the first ray's hit, this far from it.
        var request = Request(rock, asteroid, Up, 3);
        var hit = Vector3.Normalize(-request.Directions[0]);
        rig.Place(0, rock, hit * (Surface(asteroid, hit) + distance));
        rig.Step(3);
        rig.Input(0).DebugScrape(request);
        rig.Step(30);
        Assert.Equal(taken ? 2 : 0, rig.ServerEdits.Log.Count);
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void ARayThatMissesTheRockOrANonsenseRequestScrapesNothing()
    {
        using var rig = new Rig(1, 1, debugFill: true);
        var (rock, asteroid) = rig.NearbyRock(15f);
        rig.Place(0, rock, Up * (Surface(asteroid, Up) + 25f));
        rig.Step(3);
        var wait = rig.Content.RockEdits.DebugIntervalTicks + 2;

        // A ray that points away from the rock hits nothing, and one refusal refuses the whole request.
        var request = Request(rock, asteroid, Up, 3);
        request.Directions[1] = -request.Directions[1];
        rig.Input(0).DebugScrape(request);
        rig.Step(wait);
        Assert.Equal(0, rig.ServerEdits.Log.Count);

        // A radius below the least one is refused; so is one above the largest.
        foreach (var radius in new[] { 0.1f, 2.5f })
        {
            rig.Input(0).DebugScrape(Request(rock, asteroid, Up, 3, radiusMetres: radius));
            rig.Step(wait);
            Assert.Equal(0, rig.ServerEdits.Log.Count);
        }

        // A rock the sector does not have is no rock to scrape.
        rig.Input(0).DebugScrape(Request(new RockRef(rock.Sector, 20_000), asteroid, Up, 3));
        rig.Step(wait);
        Assert.Equal(0, rig.ServerEdits.Log.Count);

        // And the same request, made right, is taken: the refusals above did not leave the player held back.
        rig.Input(0).DebugScrape(Request(rock, asteroid, Up, 3));
        Assert.True(rig.Until(() => rig.ServerEdits.Log.Count == 2));
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void TwoRequestsInsideTheIntervalTakeOnlyTheFirst()
    {
        using var rig = new Rig(1, 1, debugFill: true);
        var (rock, asteroid) = rig.NearbyRock(15f);
        rig.Place(0, rock, Up * (Surface(asteroid, Up) + 25f));
        rig.Step(3);
        var interval = rig.Content.RockEdits.DebugIntervalTicks;
        Assert.True(interval > 2, "the interval must be long enough to send two requests inside it");

        // The second request reaches the server a tick after the first, well inside the interval: only the first is made.
        var first = Request(rock, asteroid, Up, 3, offset: 0f);
        var second = Request(rock, asteroid, Up, 4, offset: 6f);
        rig.Input(0).DebugScrape(first);
        rig.Step(1);
        rig.Input(0).DebugScrape(second);
        rig.Step(interval + 3);
        Assert.Equal(2, rig.ServerEdits.Log.Count);

        // The refused one did not push the window back: asked for again after it, it is taken.
        rig.Step(interval);
        rig.Input(0).DebugScrape(second);
        Assert.True(rig.Until(() => rig.ServerEdits.Log.Count == 2 + 3));
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void AClientThatComesWithinSensorRangeLaterIsSentTheWholeList()
    {
        using var rig = new Rig(2, 2, debugFill: true);
        var (rock, asteroid) = rig.NearbyRock(15f);

        // The second player starts a hundred kilometres from the rock, far outside its sensors.
        rig.Place(0, rock, Up * (Surface(asteroid, Up) + 25f));
        rig.PlaceFleet(1, rock, new Vector3(100_000f, 0, 0));
        rig.Step(3);
        rig.Input(0).DebugScrape(Request(rock, asteroid, Up, 17));
        Assert.True(rig.Until(() => rig.ServerEdits.Log.Count == 16));
        Assert.True(rig.Until(() => rig.ClientEdits(0).Log.Count == 16 && rig.ClientEdits(0).Settled));

        // The far client is not told of a rock it cannot sense.
        rig.Step(60);
        Assert.Equal(0, rig.ClientEdits(1).Log.Count);

        // It flies to the rock, and gets every edit, in order, in a few ticks.
        rig.Place(1, rock, Up * (Surface(asteroid, Up) + 40f));
        Assert.True(rig.Until(() => rig.ClientEdits(1).Log.Count == 16 && rig.ClientEdits(1).Settled), "the second client should be sent the whole list");
        var server = rig.ServerEdits;
        var serverSlot = server.Log.SlotOf(rock);
        var (log, edits, _) = rig.ClientEdits(1);
        Assert.Equal(Walk(server.Log, serverSlot), Walk(log, log.SlotOf(rock)));
        Assert.Equal(server.Bricks.Hash(serverSlot), edits.Bricks.Hash(log.SlotOf(rock)));

        // And an edit made now reaches both.
        rig.Step(rig.Content.RockEdits.DebugIntervalTicks + 1);
        rig.Input(0).DebugScrape(Request(rock, asteroid, Up, 3, offset: 9f));
        Assert.True(rig.Until(() => rig.ClientEdits(1).Log.Count == 18 && rig.ClientEdits(0).Log.Count == 18));
        Assert.True(rig.Until(() => rig.ClientEdits(1).Settled && rig.ClientEdits(0).Settled));
        Assert.Equal(server.Bricks.Hash(serverSlot), rig.ClientEdits(0).Edits.Bricks.Hash(rig.ClientEdits(0).Log.SlotOf(rock)));
        Assert.Equal(server.Bricks.Hash(serverSlot), rig.ClientEdits(1).Edits.Bricks.Hash(rig.ClientEdits(1).Log.SlotOf(rock)));
    }

    [Fact]
    public void AClientThatJoinsLaterIsSentTheWholeList()
    {
        using var rig = new Rig(2, 1, debugFill: true);
        var (rock, asteroid) = rig.NearbyRock(15f);
        rig.Place(0, rock, Up * (Surface(asteroid, Up) + 25f));
        rig.Step(3);
        rig.Input(0).DebugScrape(Request(rock, asteroid, Up, 17));
        Assert.True(rig.Until(() => rig.ServerEdits.Log.Count == 16));

        // Another player joins after the edits were made, and is put beside the rock when its hull exists.
        rig.Join(1);
        Assert.True(rig.Until(() => rig.Snap(1).HullCount > 0), "the late client should be in space");
        rig.Place(1, rock, Up * (Surface(asteroid, Up) + 40f));
        Assert.True(rig.Until(() => rig.ClientEdits(1).Log.Count == 16 && rig.ClientEdits(1).Settled), "the late client should be sent the whole list");
        var server = rig.ServerEdits;
        var serverSlot = server.Log.SlotOf(rock);
        var (log, edits, _) = rig.ClientEdits(1);
        Assert.Equal(Walk(server.Log, serverSlot), Walk(log, log.SlotOf(rock)));
        Assert.Equal(server.Bricks.Hash(serverSlot), edits.Bricks.Hash(log.SlotOf(rock)));
        Assert.Null(rig.Server.Fault);
    }

    private static RockScrape[] Walk(RockEditLog log, int slot)
    {
        var edits = new List<RockScrape>();
        for (var index = log.FirstOf(slot); index >= 0 && edits.Count < log.EditsOf(slot); index = log.NextOf(index)) edits.Add(log.EditAt(index));
        return [.. edits];
    }
}
