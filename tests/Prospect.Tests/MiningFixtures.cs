using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// What the mining laser tests share: the shipped content with a command pod that carries three mining lasers
/// (<see cref="Pod"/>), a rock to aim at, and a real server with real clients ticked by hand.
/// </summary>
internal static class MiningFixtures
{
    private static readonly Lazy<GameContent> Standard = new(() => Make());

    /// <summary>The content of <see cref="Make"/> with no change: a pod with a generator and lasers in groups 1 and 2.</summary>
    public static GameContent Content => Standard.Value;

    /// <summary>
    /// The shipped content with three mining lasers on the command pod's top faces: laser A in group 1 on plating at
    /// x = 2, laser B in group 2 on plating at x = -2, laser C in no group on plating at x = 1, and a wall of plating at x = 3
    /// that stands in the way of a beam leaning from A toward +X. A laser here draws <see cref="LaserPower"/>, less
    /// than the shipped one, so the old pod's 6 MW generator carries one of them and its battery two for a while;
    /// <paramref name="blocks"/> may change that like any other figure.
    /// </summary>
    public static GameContent Make(Action<JsonNode>? blocks = null, Action<ContentCopy>? other = null)
    {
        using var copy = new ContentCopy(fleet: true);
        copy.Write("ships/command_pod.json", PodJson());
        var node = JsonNode.Parse(copy.Read("blocks/blocks.json"))!;
        node["blocks"]!["miningLaser"]!["laser"]!["power"] = LaserPower;
        blocks?.Invoke(node);
        copy.Write("blocks/blocks.json", node.ToJsonString());

        other?.Invoke(copy);
        return copy.Load();
    }

    /// <summary>Megawatts a laser of these fixtures draws while it fires.</summary>
    public const float LaserPower = 3f;

    /// <summary>The pod's design file with the lasers, as <see cref="Make"/> describes it.</summary>
    public static string PodJson()
    {
        using var copy = new ContentCopy(fleet: true);
        var pod = JsonNode.Parse(copy.Read("ships/command_pod.json"))!;
        pod["version"] = 7;
        var blocks = pod["blocks"]!.AsArray();
        var mounts = pod["mounts"]!.AsArray();
        int Plate(int x, int y, int z)
        {
            blocks.Add(new JsonObject { ["at"] = new JsonArray(x, y, z), ["type"] = "hull" });
            return blocks.Count - 1;
        }

        void Laser(int on, int group)
        {
            var mount = new JsonObject { ["on"] = on, ["face"] = "up", ["at"] = new JsonArray(2, 2), ["type"] = "miningLaser" };
            if (group != 0) mount["group"] = group;
            mounts.Add(mount);
        }

        Laser(Plate(2, 0, 0), 1);
        Laser(Plate(-2, 0, 0), 2);
        Laser(Plate(1, 0, 0), 0);
        for (var y = 0; y <= 3; y++) Plate(3, y, 0);
        return pod.ToJsonString();
    }

    /// <summary>The place of the laser on the plating at grid <paramref name="x"/> in <see cref="HullDesign.Lasers"/>.</summary>
    public static int Part(HullDesign design, int x)
    {
        for (var k = 0; k < design.Lasers.Length; k++)
            if (design.Boxes[design.Mounts[design.Lasers[k]].Parent].At.X == x) return k;
        throw new InvalidOperationException($"No laser on the plating at x = {x}.");
    }

    /// <summary>Laser A, in group 1.</summary>
    public static int PartA(HullDesign design) => Part(design, 2);

    /// <summary>Laser B, in group 2.</summary>
    public static int PartB(HullDesign design) => Part(design, -2);

    /// <summary>Laser C, in no group.</summary>
    public static int PartC(HullDesign design) => Part(design, 1);

    /// <summary>The mask with these groups set.</summary>
    public static ushort Mask(params int[] groups)
    {
        ushort mask = 0;
        foreach (var group in groups) mask |= ActionGroups.Bit(group);
        return mask;
    }

    /// <summary>
    /// A scrapable rock near home of at least <paramref name="minRadius"/> metres that holds at least one ore, the
    /// nearest to the home sector and the smallest of its sector, so a ray finds it cheaply.
    /// </summary>
    public static (RockRef Rock, Asteroid Asteroid) FindRock(RockField rocks, float minRadius = 20f, float maxRadius = 400f)
    {
        for (var ring = 0; ring <= 6; ring++)
            for (var x = -ring; x <= ring; x++)
                for (var z = -ring; z <= ring; z++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring) continue;
                    var coord = new SectorCoord(x, 0, z);
                    var sector = rocks.Sector(coord);
                    for (var i = 0; i < sector.AsteroidCount; i++)
                    {
                        ref readonly var rock = ref sector.Asteroids[i];
                        if (RockEditRules.Scrapable(rock.Shape) && rock.Radius >= minRadius && rock.Radius <= maxRadius && rock.Resource >= 0 && rock.Grade > 0.2f)
                            return (new RockRef(coord, i), rock);
                    }
                }

        throw new InvalidOperationException("No rock near home.");
    }

    /// <summary>The distance from a rock's centre to its surface along a world direction.</summary>
    public static float Surface(Asteroid rock, Vector3 direction) =>
        rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Normalize(Vector3.Transform(direction, Quaternion.Conjugate(rock.Rotation))));

    /// <summary>
    /// Whether the edit list of a rock holds a scrape: a position is in a metre-radius pit when the rock's cut says
    /// it is inside one.
    /// </summary>
    public static int EditsOn(ServerHost server, RockRef rock)
    {
        var slot = server.RockEdits.Log.SlotOf(rock);
        return slot < 0 ? 0 : server.RockEdits.Log.EditsOf(slot);
    }

    /// <summary>The edits of a rock in the order they were made.</summary>
    public static List<RockScrape> EditList(ServerHost server, RockRef rock)
    {
        var edits = new List<RockScrape>();
        var log = server.RockEdits.Log;
        var slot = log.SlotOf(rock);
        if (slot < 0) return edits;
        for (var index = log.FirstOf(slot); index >= 0; index = log.NextOf(index)) edits.Add(log.EditAt(index));
        return edits;
    }

    /// <summary>A private field of a server.</summary>
    public static T Field<T>(ServerHost server, string name) =>
        (T)typeof(ServerHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
}

/// <summary>A real server and two real clients on in-process links, ticked by hand with a clock that moves only when the test says so.</summary>
internal sealed class MiningRig : IDisposable
{
    internal readonly GameContent Content;
    internal readonly ServerHost Server;
    internal readonly ClientSession[] Clients;
    internal readonly HullStore Hulls;
    internal readonly RockField Rocks;
    private readonly ManualTime _clock = new();
    private readonly LoopbackLink[] _links;
    internal long Tick;

    internal MiningRig(GameContent? content = null, int clients = 2)
    {
        Content = content ?? MiningFixtures.Content;
        _links = new LoopbackLink[clients];
        for (var i = 0; i < clients; i++) _links[i] = new LoopbackLink();
        Server = new ServerHost(new ServerConfig { MaxPlayers = clients, Clock = _clock }, Content, [.. _links.Select(l => l.Server)]);
        Clients = [.. _links.Select((l, i) => new ClientSession(l.Client, Content, $"Miner{i}", clock: _clock))];
        Hulls = MiningFixtures.Field<HullStore>(Server, "_hulls");
        Rocks = MiningFixtures.Field<RockField>(Server, "_rocks");
        Until(() => Clients.All(c => c.AcquireSnapshot().OwnHull >= 0 && c.AcquireSnapshot().Hulls[c.AcquireSnapshot().OwnHull].Id != 0));
    }

    internal SessionSnapshot Snap(int client = 0) => Clients[client].AcquireSnapshot();

    internal uint Own(int client = 0) { var s = Snap(client); return s.Hulls[s.OwnHull].Id; }

    internal int Slot(int client = 0) => (int)(Own(client) & 0xFFFF);

    internal HullDesign DesignOf(int client = 0) => Content.Designs[Hulls.Info(Slot(client)).Design];

    internal PlayerInputBridge Input(int client = 0) => Clients[client].Input;

    internal void Step(int count = 1)
    {
        for (var n = 0; n < count; n++)
        {
            _clock.Advance(ManualTime.Tick);
            ((ITickHandler)Server).Tick(Tick);
            foreach (var client in Clients) ((ITickHandler)client).Tick(Tick);
            Tick++;
        }
    }

    internal void Until(Func<bool> condition, int steps = 4000)
    {
        for (var n = 0; n < steps && !condition(); n++) { Step(); Thread.Sleep(1); }
        Assert.True(condition());
    }

    /// <summary>
    /// Puts a client's flown hull at rest, nose to -Z and turned to the world's axes, <paramref name="standoff"/>
    /// metres from the rock's surface on the line through the rock's centre that runs along <paramref name="aim"/>
    /// (a unit vector from the hull to the rock), and lets the clients learn of it.
    /// </summary>
    internal void Place(int client, RockRef rock, Vector3 aim, float standoff, Quaternion? orientation = null)
    {
        var asteroid = Rocks.Sector(rock.Sector).Asteroids[rock.Index];
        var centre = Content.Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition;
        ref var state = ref Hulls.State(Slot(client));
        state.Position = centre - aim * (MiningFixtures.Surface(asteroid, -aim) + standoff);
        state.Orientation = orientation ?? Quaternion.Identity;
        state.Velocity = Vector3.Zero;
        state.AngularVelocity = Vector3.Zero;
        Step(12);
    }

    /// <summary>Sends the pilot's firing intent: the groups, and a camera at the hull looking along <paramref name="direction"/>.</summary>
    internal void Aim(int client, ushort groups, Vector3 direction, uint? hull = null) =>
        Input(client).AimLasers(hull ?? Own(client), groups, Vector3.Zero, direction);

    /// <summary>The edits the server holds for a rock, counted.</summary>
    internal int Edits(RockRef rock) => MiningFixtures.EditsOn(Server, rock);

    public void Dispose()
    {
        foreach (var client in Clients) client.Dispose();
        Server.Dispose();
        foreach (var link in _links) link.Dispose();
    }
}
