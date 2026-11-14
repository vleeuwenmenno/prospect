using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The ship editor's backend through a real server and real client sessions, ticked by hand over in-process
/// links. What the renderer does in the game, saying which designs have their meshes installed, the tests do
/// through the same bridge call.
/// </summary>
public sealed class EditorSessionTests
{
    [Theory]
    [InlineData(20, false)]
    [InlineData(20, true)]
    [InlineData(80, false)]
    [InlineData(80, true)]
    public void NewShipClearanceUsesTheAppliedSectorBoundary(int km, bool outside)
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0, sectorKm: km);
        var own = rig.Own(0);
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Own(0) != own));
        ref var drone = ref rig.Hulls.State(Rig.Index(rig.Own(0)));
        drone.Orientation = Quaternion.Identity;
        drone.Position = new Double3(0, 0, -rig.Server.Galaxy.SectorSize / 2 + (outside ? 1 : 100));
        drone.Velocity = Vector3.Zero;
        var designs = rig.Content.Designs.Length;
        Assert.True(rig.Input(0).RequestNewShip("Boundary ship"));
        if (outside)
        {
            Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Refused, settle: true));
            Assert.Equal(EditorReason.NoClearSpot, rig.Snap(0).NewShip.Reason);
        }
        else
        {
            Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == designs + 1, settle: true));
            rig.Input(0).ReportInstalledDesigns(designs + 1);
            Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Created));
            var position = rig.Hulls.State(Rig.Index(rig.Snap(0).NewShip.Hull)).Position;
            Assert.Equal(default(SectorCoord), rig.Server.Galaxy.SectorOf(position));
        }
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void NewShipWaitsForDesignReadyGrantsRemoteEditAndApplyKeepsTheDrone()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var ship = rig.Own(0);
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Own(0) != ship));
        var drone = rig.Own(0);
        var count = rig.Hulls.Count;
        var designs = rig.Content.Designs.Length;
        Assert.True(rig.Input(0).RequestNewShip("  New ship  "));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true));
        Assert.Equal(NewShipStatus.Pending, rig.Snap(0).NewShip.Status);
        Assert.Equal(count, rig.Hulls.Count);
        Assert.Equal(drone, rig.Own(0));
        Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == designs + 1, settle: true));
        var root = rig.Snap(0).Content.Designs[designs];
        Assert.Equal("New ship", root.Name);
        Assert.Equal(1, root.BlockCount);
        Assert.Equal("core", root.Boxes[0].Type.Id);
        Assert.Equal(DesignBytes.Encode(root), DesignBytes.Encode(DesignBytes.Decode(DesignBytes.Encode(root), SHA256.HashData(DesignBytes.Encode(root)), rig.Content)));
        rig.Input(0).ReportInstalledDesigns(designs + 1);
        Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Created));
        var created = rig.Snap(0).NewShip.Hull;
        Assert.Equal((EditorPhase.Open, created), (rig.Editor(0).Phase, rig.Editor(0).Hull));
        Assert.Equal(count + 1, rig.Hulls.Count);
        Assert.Equal(drone, rig.Own(0));
        Assert.False(rig.Hulls.Info(Rig.Index(created)).Piloted);
        Assert.False(rig.Hulls.Info(Rig.Index(created)).EscapeDrone);
        Assert.True(rig.Until(() => Enumerable.Range(0, rig.Snap(0).HullCount).Any(i => rig.Snap(0).Hulls[i].Id == created)));
        var draft = BuildDraft.FromBlueprint(root, rig.Server.Content);
        Place(draft, rig.Content.Blocks.Get("hull"));
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.True(rig.Input(0).CommitEdit(rig.Editor(0).Token, export.Bytes));
        rig.Input(0).ReportInstalledDesigns(designs + 2);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true));
        Assert.Equal(drone, rig.Own(0));
        Assert.Equal(created, rig.Editor(0).ReplacedHull);
        Assert.False(rig.Hulls.Info(Rig.Index(rig.Editor(0).Hull)).Piloted);
        Assert.True(rig.Input(0).RequestNewShip("new SHIP"));
        Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Refused));
        Assert.Equal(EditorReason.DuplicateName, rig.Snap(0).NewShip.Reason);
        rig.Step(240);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 120; i++) while (allocation.Measuring()) rig.Step();
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void ThePlayerInTheDroneTakesControlOfAShipJustBuiltAfterCancellingOrApplyingItsEdit()
    {
        foreach (var apply in new[] { false, true })
        {
            using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
            rig.Input(0).RequestControl(0);
            var ship = rig.Own(0);
            Assert.True(rig.Until(() => rig.Own(0) != ship && rig.Snap(0).OwnIsDrone));
            var drone = rig.Own(0);
            var designs = rig.Content.Designs.Length;
            Assert.True(rig.Input(0).RequestNewShip("Scout"));
            Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == designs + 1, settle: true));
            rig.Input(0).ReportInstalledDesigns(designs + 1);
            Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Created));
            var created = rig.Snap(0).NewShip.Hull;
            Assert.True(rig.Until(() => Enumerable.Range(0, rig.Snap(0).HullCount).Any(i => rig.Snap(0).Hulls[i].Id == created)));
            var target = created;
            if (apply)
            {
                var draft = BuildDraft.FromBlueprint(rig.Snap(0).Content.Designs[designs], rig.Server.Content);
                Place(draft, rig.Content.Blocks.Get("hull"));
                var export = draft.Export();
                Assert.True(export.Ok, export.Problem);
                Assert.True(rig.Input(0).CommitEdit(rig.Editor(0).Token, export.Bytes));
                rig.Input(0).ReportInstalledDesigns(designs + 2);
                Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true));
                target = rig.Editor(0).Hull;
                Assert.NotEqual(created, target);
                Assert.Equal(drone, rig.Own(0));
            }
            else
            {
                rig.Input(0).CancelEdit();
                Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed, settle: true));
                Assert.Equal(drone, rig.Own(0));
            }

            Assert.True(rig.Until(() => Enumerable.Range(0, rig.Snap(0).HullCount).Any(i => rig.Snap(0).Hulls[i].Id == target)));
            rig.Input(0).RequestControl(target);
            Assert.True(rig.Until(() => rig.Own(0) == target), $"apply={apply}");
            Assert.True(rig.Hulls.Info(Rig.Index(target)).Piloted);
            Assert.False(rig.Snap(0).OwnIsDrone);
            // The drone the player left is removed with the switch.
            Assert.False(rig.Hulls.IsAlive(new HullHandle((int)(drone & 0xFFFF), (int)(drone >> 16))));
        }
    }

    [Fact]
    public void TheSnapshotSaysWhenTheOwnHullIsTheDroneAndNotWhenItIsAShip()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var ship = rig.Own(0);
        Assert.False(rig.Snap(0).OwnIsDrone);
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Own(0) != ship && rig.Snap(0).OwnIsDrone));

        // Back in the ship, the drone is gone and the flag with it.
        rig.Input(0).RequestControl(ship);
        Assert.True(rig.Until(() => rig.Own(0) == ship));
        Assert.False(rig.Snap(0).OwnIsDrone);
    }

    [Fact]
    public void NewShipRejectsAnAsteroidAndRechecksClearanceAfterReadiness()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var old = rig.Own(0);
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Own(0) != old));
        var drone = Rig.Index(rig.Own(0));
        var state = rig.Hulls.State(drone);
        var target = state.Position + Vector3.Transform(-Vector3.UnitZ, state.Orientation) * 50;
        var rocks = rig.Field<RockField>("_rocks");
        var sector = rig.Server.Galaxy.SectorOf(target);
        var contents = rocks.Sector(sector);
        var original = contents.AsteroidCount;
        var originalRocks = contents.Asteroids;
        contents.Asteroids = new Asteroid[original + 1];
        originalRocks.AsSpan(0, original).CopyTo(contents.Asteroids);
        contents.Asteroids[original] = new Asteroid { LocalPosition = target.RelativeTo(rig.Server.Galaxy.CenterOf(sector)), Radius = 2, Rotation = Quaternion.Identity };
        contents.AsteroidCount = original + 1;
        contents.Rocks.Reset(original + 1, rig.Server.Galaxy.SectorSize);
        for (var i = 0; i < contents.AsteroidCount; i++) contents.Rocks.Add(i, contents.Asteroids[i].LocalPosition, contents.Asteroids[i].Radius);
        Assert.True(rig.Input(0).RequestNewShip("Rock blocked"));
        Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Refused, settle: true));
        Assert.Equal(EditorReason.NoClearSpot, rig.Snap(0).NewShip.Reason);
        contents.Asteroids = originalRocks;
        contents.AsteroidCount = original;
        contents.Rocks.Reset(original, rig.Server.Galaxy.SectorSize);
        for (var i = 0; i < original; i++) contents.Rocks.Add(i, contents.Asteroids[i].LocalPosition, contents.Asteroids[i].Radius);
        var count = rig.Hulls.Count;
        Assert.True(rig.Input(0).RequestNewShip("Late obstacle"));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true));
        rig.Hulls.State(Rig.Index(old)).Position = target;
        rig.Input(0).ReportInstalledDesigns(rig.Server.Content.Designs.Length);
        Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Refused, settle: true));
        Assert.Equal(EditorReason.NoClearSpot, rig.Snap(0).NewShip.Reason);
        Assert.Equal(count, rig.Hulls.Count);
    }

    [Theory]
    [InlineData(false, 16, null, false, EditorReason.NotPiloting)]
    [InlineData(true, 1, null, false, EditorReason.ShipLimit)]
    [InlineData(true, 16, 0, false, EditorReason.CatalogueFull)]
    [InlineData(true, 16, null, true, EditorReason.NoClearSpot)]
    public void NewShipRefusesWithoutMutatingHulls(bool drone, int maxShips, int? spare, bool blocked, EditorReason expected)
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0, maxShips: maxShips, catalogueSpare: spare);
        var own = rig.Own(0);
        if (drone)
        {
            rig.Input(0).RequestControl(0);
            Assert.True(rig.Until(() => rig.Own(0) != own));
        }
        if (blocked)
        {
            var state = rig.Hulls.State(Rig.Index(rig.Own(0)));
            rig.Hulls.State(Rig.Index(own)).Position = state.Position + Vector3.Transform(-Vector3.UnitZ, state.Orientation) * 50;
        }
        var count = rig.Hulls.Count;
        Assert.True(rig.Input(0).RequestNewShip("New ship"));
        Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Refused, settle: true));
        Assert.Equal(expected, rig.Snap(0).NewShip.Reason);
        Assert.Equal(count, rig.Hulls.Count);
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void NewShipNamesAreBoundedAndRawBadNamesGetAReason()
    {
        ShipName name = default;
        Assert.True(name.Set("  Prospect  "));
        Assert.Equal("Prospect", new string(name.Text[..name.Length]));
        Assert.True(name.Set(new string('x', 32)));
        Assert.False(name.Set(new string('x', 33)));
        Assert.False(name.Set("   "));
        Assert.False(name.Set("bad\nname"));
        Assert.False(name.Set("bad\u200bname"));
        using var rig = new Rig(clients: 1, joined: 1);
        var own = rig.Own(0);
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Own(0) != own));
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.NewShip, 0));
        Assert.True(rig.Until(() => rig.Snap(0).NewShip.Status == NewShipStatus.Refused));
        Assert.Equal(EditorReason.BadName, rig.Snap(0).NewShip.Reason);
    }

    /// <summary>The server's end of a link, able to hold a client back, refuse sends and count what passes.</summary>
    private sealed class Wire(ITransport inner) : ITransport, ITransportHandler
    {
        internal bool Hold, RefuseReliable, RefuseBulk;
        internal int DesignChunksBeforeRefusal = -1;
        internal readonly int[] Sent = new int[256], Received = new int[256];
        internal EditorPhase LastPhase;
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
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (!payload.IsEmpty) Received[payload[0]]++;
            _handler!.OnMessage(this, peer, channel, payload);
        }
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if ((RefuseReliable && channel == NetChannel.Reliable) || (RefuseBulk && channel == NetChannel.Bulk)) return false;
            if (payload[0] == (byte)MessageId.DesignData && DesignChunksBeforeRefusal >= 0)
            {
                if (DesignChunksBeforeRefusal == 0) return false;
                DesignChunksBeforeRefusal--;
            }
            Sent[payload[0]]++;
            if (payload[0] == (byte)MessageId.EditorState) LastPhase = (EditorPhase)payload[1];
            return inner.Send(peer, channel, payload);
        }
        public void Disconnect(int peer) => inner.Disconnect(peer);
        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);
        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        private readonly ContentCopy _files = new();
        private readonly LoopbackLink[] _links;
        private readonly long[] _joined;
        internal readonly GameContent Content;
        internal readonly ServerHost Server;
        internal readonly Wire[] Wires;
        internal readonly ClientSession?[] Clients;
        internal readonly HullStore Hulls;
        internal long Tick;

        /// <summary>A server with a link for every client; the first <paramref name="joined"/> join at once.</summary>
        internal Rig(int clients = 2, int joined = 2, int quiet = 1, int peerWait = 1, int ready = 5, int commit = 0, int uploads = 2, int? catalogueSpare = null, int maxShips = 16, int? sectorKm = null)
        {
            _files.Write("net/editor.json", $$"""{ "maxOwnedShips": {{maxShips}}, "quietSeconds": {{quiet}}, "uploads": {{uploads}}, "uploadSeconds": 5, "readySeconds": {{ready}}, "peerWaitSeconds": {{peerWait}}, "commitSeconds": {{commit}} }""");
            if (catalogueSpare is { } spare)
            {
                long shipped = 0;
                foreach (var design in GameContent.Load().Designs) shipped += DesignBytes.Encode(design).Length;
                _files.Replace("net/blocks.json", "\"catalogueBytes\": 196608", $"\"catalogueBytes\": {shipped + spare}");
            }
            _files.Replace("rules/exploration.json", "5e3", "5");
            Content = _files.Load();
            if (sectorKm is { } km) Content = Content.WithSettings(Content.SettingCatalog.Build(null, [$"sectorSize={km}", "asteroidAmount=0"]));
            _links = new LoopbackLink[clients];
            Wires = new Wire[clients];
            Clients = new ClientSession?[clients];
            _joined = new long[clients];
            for (var i = 0; i < clients; i++)
            {
                _links[i] = new LoopbackLink();
                Wires[i] = new Wire(_links[i].Server) { Hold = i >= joined };
            }
            Server = new ServerHost(new ServerConfig(), Content, [.. Wires]);
            Hulls = Field<HullStore>("_hulls");
            for (var i = 0; i < joined; i++) Join(i);
            Assert.True(Until(() => Enumerable.Range(0, joined).All(i => Snap(i).HullCount > 0), settle: true), "the clients should be in space");
        }

        internal T Field<T>(string name) => (T)typeof(ServerHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server)!;

        /// <summary>Starts a client whose link the server has not looked at yet.</summary>
        internal void Join(int client)
        {
            Clients[client] = new ClientSession(_links[client].Client, Content, "Player " + client);
            _joined[client] = Tick;
            Wires[client].Hold = false;
        }

        internal void Step(int count = 1)
        {
            for (var n = 0; n < count; n++)
            {
                ((ITickHandler)Server).Tick(Tick);
                for (var i = 0; i < Clients.Length; i++)
                    if (Clients[i] is { } client) ((ITickHandler)client).Tick(Tick - _joined[i]);
                Tick++;
            }
        }

        /// <summary>
        /// Steps until a condition holds. With <paramref name="settle"/>, a check or decode that is running on a
        /// worker finishes before each interval is stepped (<see cref="TestWorkers"/>), so what the wait costs
        /// in intervals does not depend on how soon the machine gave the worker a processor.
        /// </summary>
        internal bool Until(Func<bool> condition, int steps = 2400, bool settle = false)
        {
            for (var i = 0; i < steps && !condition(); i++)
            {
                if (settle) Settle();
                Step();
            }
            return condition();
        }

        /// <summary>Waits, without stepping an interval, for the checks and decodes that are running right now.</summary>
        internal void Settle()
        {
            TestWorkers.Settle(Server);
            for (var i = 0; i < Clients.Length; i++)
                if (Clients[i] is { } client) TestWorkers.Settle(client, "client " + i);
        }

        internal SessionSnapshot Snap(int client) => Clients[client]!.AcquireSnapshot();
        internal EditorView Editor(int client) => Snap(client).Editor;
        internal PlayerInputBridge Input(int client) => Clients[client]!.Input;
        internal uint Own(int client) { var s = Snap(client); return s.Hulls[s.OwnHull].Id; }
        internal static int Index(uint hull) => (int)(hull & 0xFFFF);
        internal static HullHandle Handle(uint hull) => new((int)(hull & 0xFFFF), (int)(hull >> 16));

        /// <summary>Sends the server bytes of the test's choosing as a client, beside what its session sends.</summary>
        internal void Raw(int client, NetChannel channel, ReadOnlySpan<byte> payload) => Assert.True(_links[client].Client.Send(0, channel, payload));

        /// <summary>Asks to edit the client's own hull and waits for the server to allow it.</summary>
        internal uint Open(int client)
        {
            var serial = Editor(client).Serial;
            Input(client).RequestEdit(Own(client));
            Assert.True(Until(() => Editor(client).Serial != serial), "the server should answer a request to edit");
            var view = Editor(client);
            Assert.Equal((EditorPhase.Open, EditorReason.None), (view.Phase, view.Reason));
            Assert.NotEqual(0u, view.Token);
            return view.Token;
        }

        /// <summary>Waits for the next answer the client gets about its edit.</summary>
        internal EditorView Answer(int client, uint after, bool settle = false)
        {
            Assert.True(Until(() => Editor(client).Serial != after, settle: settle), "the server should answer");
            return Editor(client);
        }

        public void Dispose()
        {
            foreach (var client in Clients) client?.Dispose();
            Server.Dispose();
            foreach (var link in _links) link.Dispose();
            _files.Dispose();
        }
    }

    /// <summary>The client's own hull with <paramref name="extra"/> hull blocks added, as an editor would export it.</summary>
    private static byte[] Edited(SessionSnapshot snapshot, int extra = 1, BlockShape shape = BlockShape.Box)
    {
        var own = snapshot.Hulls[snapshot.OwnHull];
        var draft = BuildDraft.From(snapshot.Content.Designs[own.Design], own.Blocks.Span, snapshot.Content);
        var one = new GridSize(1, 1, 1);
        for (var added = 0; added < extra; added++)
        {
            var placed = false;
            for (var target = 0; target < draft.Boxes.Length && !placed; target++)
                for (var side = 0; side < 6 && !placed; side++)
                    placed = draft.Add(draft.Snap(target, side, one), one, snapshot.Content.Blocks.Get("hull"), shape) == DraftEdit.Done;
            Assert.True(placed);
        }
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        return export.Bytes!;
    }

    private static byte[] Request(EditorRequestKind kind, uint value, int total = 0, byte[]? hash = null)
    {
        var buffer = new byte[EditorRequestMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        new EditorRequestMessage { Kind = kind, Hull = value, Token = value, Total = total }.Write(ref writer, hash ?? new byte[32]);
        return writer.Written.ToArray();
    }

    private static byte[] Chunk(uint token, int offset, ReadOnlySpan<byte> bytes)
    {
        var buffer = new byte[EditorDataMessage.HeaderBytes + bytes.Length];
        var writer = new NetWriter(buffer);
        EditorDataMessage.Write(ref writer, token, offset, bytes);
        return buffer;
    }

    [Fact]
    public void HiddenConstructionIsNotAnnouncedUntilItsHullEntersCoverage()
    {
        using var rig = new Rig();
        var owner = rig.Snap(1).PlayerId;
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).Owner == owner)
                rig.Hulls.State(i).Position += new Double3(0, 1_000_000, 0);
        rig.Step(10);
        var noticed = rig.Wires[0].Sent[(byte)MessageId.DesignNotice];
        var token = rig.Open(1);
        Assert.True(rig.Input(1).CommitEdit(token, Edited(rig.Snap(1))));
        rig.Input(1).ReportInstalledDesigns(rig.Content.Designs.Length + 1);
        Assert.True(rig.Until(() => rig.Editor(1).Phase == EditorPhase.Applied, settle: true));
        Assert.Equal(noticed, rig.Wires[0].Sent[(byte)MessageId.DesignNotice]);
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).Owner == owner)
                rig.Hulls.State(i).Position -= new Double3(0, 1_000_000, 0);
        rig.Step(5);
        Assert.True(rig.Wires[0].Sent[(byte)MessageId.DesignNotice] > noticed);
    }

    [Fact]
    public void AFinishedJumpDoesNotBlockEditingWhileItsOldOrderIsStillBeingSent()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var hull = Rig.Index(rig.Own(0));
        var destination = rig.Hulls.State(hull).Position + new Double3(0, 0, -600);
        rig.Input(0).IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = destination });
        Assert.True(rig.Until(() => rig.Hulls.State(hull).Jump != JumpPhase.None));
        Assert.True(rig.Until(() => rig.Hulls.State(hull).Jump == JumpPhase.None && rig.Hulls.Info(hull).Order.Kind == OrderKind.None));
        Assert.True(rig.Hulls.State(hull).Position.RelativeTo(destination).Length() < 1);

        // No thrust or Halt: the client keeps retransmitting the original definition with its consumed id.
        var connection = rig.Field<Array>("_connections").GetValue(0)!;
        var pending = (HullOrder)connection.GetType().GetField("Order")!.GetValue(connection)!;
        var orderId = (ushort)connection.GetType().GetField("OrderId")!.GetValue(connection)!;
        Assert.Equal(OrderKind.Jump, pending.Kind);
        Assert.Equal(rig.Hulls.Info(hull).OrderId, orderId);
        rig.Open(0);
        rig.Step(6);
        Assert.Equal((EditorPhase.Open, EditorReason.None), (rig.Editor(0).Phase, rig.Editor(0).Reason));
    }

    [Fact]
    public void AJumpEffectiveBeforeMeshReadinessCancelsApplyBeforeReplacingTheHull()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var old = rig.Own(0);
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, Edited(rig.Snap(0))));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true));
        Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == rig.Content.Designs.Length + 1, settle: true));
        rig.Input(0).IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = rig.Hulls.State(Rig.Index(old)).Position + new Double3(600, 0, 0) });
        // The scheduled order must reach its interval before mesh readiness can replace the hull.
        Assert.True(rig.Until(() => rig.Hulls.State(Rig.Index(old)).Jump != JumpPhase.None));
        rig.Input(0).ReportInstalledDesigns(rig.Content.Designs.Length + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase is EditorPhase.Closed or EditorPhase.Applied));
        Assert.Equal(EditorReason.Jumping, rig.Editor(0).Reason);
        Assert.True(rig.Hulls.IsAlive(Rig.Handle(old)));
        Assert.Equal(old, rig.Own(0));
    }

    [Fact]
    public void EffectiveJumpAndReadinessInTheSamePollCancelReplacement()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var old = rig.Own(0);
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, Edited(rig.Snap(0))));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true));
        Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == rig.Content.Designs.Length + 1, settle: true));
        // Let previously scheduled neutral inputs expire without sampling more.
        for (var i = 0; i < 32; i++) ((ITickHandler)rig.Server).Tick(rig.Tick++);
        var connection = rig.Field<Array>("_connections").GetValue(0)!;
        var epoch = (uint)connection.GetType().GetField("ControlEpoch")!.GetValue(connection)!;
        var orderId = (ushort)(rig.Hulls.Info(Rig.Index(old)).OrderId + 1);
        var buffer = new byte[InputMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        InputMessage.Write(ref writer, [new InputRecord
        {
            TargetTick = rig.Tick,
            OrderId = orderId,
            Order = new HullOrder { Kind = OrderKind.Jump, Target = rig.Hulls.State(Rig.Index(old)).Position + new Double3(600, 0, 0) },
            Input = InputTimeline.Fallback,
        }], epoch);
        rig.Raw(0, NetChannel.Unreliable, writer.Written);
        writer = new NetWriter(buffer);
        DesignReadyMessage.Write(ref writer, (byte)rig.Content.Designs.Length, true);
        rig.Raw(0, NetChannel.Reliable, writer.Written);
        rig.Step();
        Assert.Equal(EditorReason.Jumping, rig.Editor(0).Reason);
        Assert.True(rig.Hulls.IsAlive(Rig.Handle(old)));
        Assert.Equal(old, rig.Own(0));
    }

    [Fact]
    public void KnownDesignCommitsBothRespectAndAdvanceTheCommitCooldown()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0, commit: 2);
        var bytes = Edited(rig.Snap(0));
        var original = DesignBytes.Encode(rig.Content.Pod);
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        rig.Input(0).ReportInstalledDesigns(rig.Content.Designs.Length + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true));
        Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));
        token = rig.Open(0);
        var serial = rig.Editor(0).Serial;
        Assert.True(rig.Input(0).CommitEdit(token, original));
        Assert.Equal(EditorReason.TooSoon, rig.Answer(0, serial).Reason);
        rig.Step(2 * TickLoop.SimulationHz);
        Assert.True(rig.Input(0).CommitEdit(token, original));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied));
        Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));

        // Returning to the edited design is also a cache hit, and must wait after the previous cache hit.
        var uploads = rig.Wires[0].Received[(byte)MessageId.EditorData];
        token = rig.Open(0);
        serial = rig.Editor(0).Serial;
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.Equal(EditorReason.TooSoon, rig.Answer(0, serial).Reason);
        rig.Step(2 * TickLoop.SimulationHz);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied));
        Assert.Equal(uploads, rig.Wires[0].Received[(byte)MessageId.EditorData]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEditedEscapeDroneStillCannotEjectAndKeepsItsCleanupAndRespawnRules(bool destroy)
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var ship = rig.Own(0);
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Own(0) != ship));
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, Edited(rig.Snap(0))));
        rig.Input(0).ReportInstalledDesigns(rig.Content.Designs.Length + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true));
        var drone = Rig.Handle(rig.Editor(0).Hull);
        Assert.True(rig.Until(() => rig.Own(0) == drone.Packed));
        Assert.True(rig.Hulls.Info(drone.Index).EscapeDrone);
        var count = rig.Hulls.Count;
        rig.Input(0).RequestControl(0);
        rig.Step(6);
        Assert.Equal(drone.Packed, rig.Own(0));
        Assert.Equal(count, rig.Hulls.Count);
        if (destroy)
        {
            rig.Hulls.State(drone.Index).Position += new Double3(10000, 0, 0);
            // Exercise the death handler directly: collision damage is covered separately, while this checks
            // which respawn branch the edited drone takes once destruction has been decided.
            foreach (var connection in rig.Field<Array>("_connections"))
            {
                if ((uint)connection!.GetType().GetField("PlayerId")!.GetValue(connection)! != rig.Snap(0).PlayerId) continue;
                typeof(ServerHost).GetMethod("LoseHull", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Server, [connection]);
                var replacement = (HullHandle)connection.GetType().GetField("Hull")!.GetValue(connection)!;
                var spawn = (Double3)typeof(ServerHost).GetProperty("SpawnPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Server)!;
                // Check before stepping: the original ship at spawn can push the new drone aside.
                Assert.Equal(spawn, rig.Hulls.State(replacement.Index).Position);
                Assert.True(rig.Hulls.Info(replacement.Index).EscapeDrone);
                break;
            }
            Assert.True(rig.Until(() => rig.Own(0) != drone.Packed));
        }
        else
        {
            rig.Input(0).RequestControl(ship);
            Assert.True(rig.Until(() => rig.Own(0) == ship));
            Assert.Equal(count - 1, rig.Hulls.Count);
        }
        Assert.False(rig.Hulls.IsAlive(drone));
    }

    [Fact]
    public void APartialRuntimeDesignRetriesAfterTheSenderExpiresAndLaterDesignsStillArrive()
    {
        using var rig = new Rig(peerWait: 0);
        var startup = rig.Content.Designs.Length;
        var bytes = Edited(rig.Snap(0), 100);
        Assert.True(bytes.Length > rig.Content.BlockTransfers.BytesPerTick);
        rig.Wires[1].DesignChunksBeforeRefusal = 1;
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        rig.Input(0).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true));
        Assert.True(rig.Until(() => rig.Wires[1].DesignChunksBeforeRefusal == 0));
        Assert.Equal(startup, rig.Snap(1).Content.Designs.Length);
        var requests = rig.Wires[1].Received[(byte)MessageId.DesignRequest];
        rig.Step(checked((int)rig.Content.BlockTransfers.AdmissionTicks + 2));
        rig.Wires[1].DesignChunksBeforeRefusal = -1;
        Assert.True(rig.Until(() => rig.Snap(1).Content.Designs.Length == startup + 1, settle: true), "partial download should restart after the sender expires");
        Assert.True(rig.Wires[1].Received[(byte)MessageId.DesignRequest] > requests);
        Assert.Equal(bytes, DesignBytes.Encode(rig.Snap(1).Content.Designs[startup]));
        rig.Input(1).ReportInstalledDesigns(startup + 1);

        Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));
        token = rig.Open(0);
        var next = Edited(rig.Snap(0));
        Assert.True(rig.Input(0).CommitEdit(token, next));
        rig.Input(0).ReportInstalledDesigns(startup + 2);
        Assert.True(rig.Until(() => rig.Snap(1).Content.Designs.Length == startup + 2, settle: true));
        Assert.Equal(next, DesignBytes.Encode(rig.Snap(1).Content.Designs[startup + 1]));
    }

    [Fact]
    public void AnEditedDesignReplacesTheHullOnceItsOwnerCanShowItAndTheOtherPlayerSeesItFlyAndTakeDamage()
    {
        using var rig = new Rig();
        var before = rig.Snap(0);
        var old = rig.Own(0);
        var startup = before.Content.Designs.Length;
        var bytes = Edited(before);
        var oldDesign = rig.Content.Designs[rig.Hulls.Info(Rig.Index(old)).Design];

        // The other player keeps flying through all of it.
        var other = Rig.Index(rig.Own(1));
        rig.Input(1).Submit(new PlayerControls { Forward = 1f });
        var otherStart = rig.Hulls.State(other).Position;

        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true), $"the design should be accepted, not {rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.True(rig.Wires[0].Received[(byte)MessageId.EditorData] >= 1);
        Assert.Equal(bytes.Length, rig.Editor(0).SentBytes);

        // The design is in both sessions' catalogues, checked and decoded, at the end and nowhere else.
        Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == startup + 1 && rig.Snap(1).Content.Designs.Length == startup + 1, settle: true));
        for (var client = 0; client < 2; client++)
        {
            var content = rig.Snap(client).Content;
            Assert.Equal(bytes, DesignBytes.Encode(content.Designs[startup]));
            for (var i = 0; i < startup; i++) Assert.Same(before.Content.Designs[i], rig.Snap(0).Content.Designs[i]);
        }

        // Nobody has said its mesh is installed: the old hull stays, and the other player still flies.
        rig.Step(180);
        Assert.Equal(EditorPhase.Publishing, rig.Editor(0).Phase);
        Assert.True(rig.Hulls.IsAlive(Rig.Handle(old)));
        Assert.Equal(old, rig.Own(0));
        Assert.Equal(SessionState.Active, rig.Snap(1).State);
        Assert.True(rig.Hulls.State(other).Position.RelativeTo(otherStart).Length() > 5f, "the other player should have flown on");

        // The owner's renderer reports the mesh; the other player's does not, and gets its moment and no more.
        rig.Hulls.State(Rig.Index(old)).Energy = 0f;
        var oldState = rig.Hulls.State(Rig.Index(old));
        rig.Input(0).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"the hull should be replaced, not {rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        var applied = rig.Editor(0);
        Assert.Equal(old, applied.ReplacedHull);
        Assert.NotEqual(old, applied.Hull);
        Assert.False(rig.Hulls.IsAlive(Rig.Handle(old)));
        var next = Rig.Handle(applied.Hull);
        Assert.True(rig.Hulls.IsAlive(next));
        ref var info = ref rig.Hulls.Info(next.Index);
        Assert.Equal((startup, true, rig.Snap(0).PlayerId), (info.Design, info.Piloted, info.Owner));
        var design = rig.Server.Content.Designs[startup];
        Assert.Equal(oldDesign.BlockCount + 1, design.BlockCount);
        Assert.Equal(design.BlockCount, BlockSet.Count(rig.Hulls.Blocks(next.Index)));

        // The grid did not move: the new centre is the old one shifted by the difference of the two centres.
        var shift = (design.Pivot - oldDesign.Pivot) * design.BlockSize;
        var expected = oldState.Position + Vector3.Transform(new Vector3((float)shift.X, (float)shift.Y, (float)shift.Z), oldState.Orientation);
        Assert.True(rig.Hulls.State(next.Index).Position.RelativeTo(expected).Length() < 1e-3f);
        Assert.Equal(oldState.Orientation, rig.Hulls.State(next.Index).Orientation);
        // Building is free, charge is not: the battery was empty and is not handed back full.
        Assert.True(rig.Hulls.State(next.Index).Energy < rig.Hulls.Flight(next.Index).EnergyStorage * 0.5f);
        Assert.Equal(rig.Hulls.Flight(next.Index).HullPoints, rig.Hulls.State(next.Index).Hull);

        // The owner flies the new hull, predicted on the new design.
        Assert.True(rig.Until(() => rig.Own(0) == applied.Hull));
        var mine = rig.Snap(0);
        Assert.Equal(startup, mine.Hulls[mine.OwnHull].Design);
        Assert.Equal(design.BlockCount, BlockSet.Count(mine.Hulls[mine.OwnHull].Blocks.Span));
        var from = rig.Hulls.State(next.Index).Position;
        rig.Input(0).Submit(new PlayerControls { Forward = 1f });
        rig.Step(120);
        Assert.True(rig.Hulls.State(next.Index).Position.RelativeTo(from).Length() > 5f, "the new hull should fly under its pilot");
        Assert.True(rig.Snap(0).PredictionError < 5f);
        rig.Input(0).Submit(default);

        // The other player is sent no hull of a design their renderer has not installed.
        rig.Step(180);
        var theirs = rig.Snap(1);
        for (var i = 0; i < theirs.HullCount; i++)
            Assert.True(theirs.Hulls[i].Id != applied.Hull && theirs.Hulls[i].Id != old && theirs.Hulls[i].Design < startup);
        rig.Input(1).ReportInstalledDesigns(startup + 1);
        HullView seen = default;
        Assert.True(rig.Until(() =>
        {
            var s = rig.Snap(1);
            for (var i = 0; i < s.HullCount; i++)
                if (s.Hulls[i].Id == applied.Hull) { seen = s.Hulls[i]; return true; }
            return false;
        }), "the other player should see the new hull once ready");
        Assert.Equal(startup, seen.Design);
        Assert.Equal(design.BlockCount, BlockSet.Count(seen.Blocks.Span));

        // Damage to the new hull reaches both.
        // The new block sits under the core; a drone comes up at it, with the shield out of the way.
        rig.Hulls.State(next.Index).Velocity = Vector3.Zero;
        rig.Hulls.State(next.Index).Shield = 0f;
        rig.Hulls.State(next.Index).ShieldOffline = true;
        var added = design.BlockCount - 1;
        Assert.Equal(new GridSize(0, -1, 0), design.Boxes[added].At);
        var state = rig.Hulls.State(next.Index);
        var down = Vector3.Transform(-Vector3.UnitY, state.Orientation);
        var target = state.Position + Vector3.Transform(design.BlockOffsets[added], state.Orientation);
        var drone = (byte)rig.Content.DroneDesign;
        rig.Hulls.BlockStates.SetHitPoints(rig.Hulls.Info(next.Index).BlockState, added, 0.01f);
        var rammer = rig.Hulls.Create(0, drone, rig.Server.Content.Designs[drone], target + down * 6f);
        rig.Hulls.State(rammer.Index).Velocity = down * -30f;
        Assert.True(rig.Until(() => rig.Hulls.IsAlive(next) && rig.Hulls.Revision(next.Index) > 0, 600), "the ram should take a block");
        Assert.True(rig.Hulls.Info(next.Index).DamageTick > 0);
        Assert.True(rig.Until(() =>
        {
            var a = rig.Snap(0); var b = rig.Snap(1);
            var own = a.OwnHull >= 0 && a.Hulls[a.OwnHull].Id == applied.Hull && a.Hulls[a.OwnHull].Revision > 0;
            var remote = false;
            for (var i = 0; i < b.HullCount; i++) remote |= b.Hulls[i].Id == applied.Hull && b.Hulls[i].Revision > 0;
            return own && remote;
        }, 600), "both players should see the lost block");
        Assert.Null(rig.Clients[0]!.Fault);
        Assert.Null(rig.Clients[1]!.Fault);
    }

    [Fact]
    public void AnEditedWedgeReachesBothPlayersInItsShapeAndTheOtherSeesTheNewHull()
    {
        using var rig = new Rig();
        var before = rig.Snap(0);
        var startup = before.Content.Designs.Length;
        var bytes = Edited(before, 2, BlockShape.WedgeYMinusPlus);

        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true), $"the wedges should be accepted, not {rig.Editor(0).Phase}/{rig.Editor(0).Reason}");

        // Both players decode the same design, wedges and all, and their figures agree with the server's.
        Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == startup + 1 && rig.Snap(1).Content.Designs.Length == startup + 1, settle: true));
        var served = rig.Server.Content.Designs[startup];
        for (var client = 0; client < 2; client++)
        {
            var design = rig.Snap(client).Content.Designs[startup];
            Assert.Equal(bytes, DesignBytes.Encode(design));
            Assert.Equal(2, design.Shapes.Count(shape => shape == BlockShape.WedgeYMinusPlus));
            Assert.Equal(served.Flight.Mass, design.Flight.Mass);
            Assert.Equal(served.Pivot, design.Pivot);
        }

        rig.Input(0).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"the hull should be replaced, not {rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        var applied = rig.Editor(0).Hull;
        rig.Input(1).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() =>
        {
            var s = rig.Snap(1);
            for (var i = 0; i < s.HullCount; i++)
                if (s.Hulls[i].Id == applied)
                    return s.Hulls[i].Design == startup && BlockSet.Count(s.Hulls[i].Blocks.Span) == served.BlockCount;
            return false;
        }), "the other player should see the wedge ship once ready");
        Assert.Null(rig.Clients[0]!.Fault);
        Assert.Null(rig.Clients[1]!.Fault);
    }

    [Fact]
    public void DamageJumpAndLeavingTheHullEndAnEditAtEveryStageAndTheBufferComesBack()
    {
        using var rig = new Rig(quiet: 1);
        var own = Rig.Index(rig.Own(0));
        var uploads = rig.Field<Array>("_uploads");
        bool Held() => uploads.Cast<object>().Any(u => (bool)u.GetType().GetField("Held")!.GetValue(u)!);

        // Recently damaged: refused with the wait; a second later allowed.
        rig.Hulls.Info(own).DamageTick = rig.Tick - 1;
        var serial = rig.Editor(0).Serial;
        rig.Input(0).RequestEdit(rig.Own(0));
        var refused = rig.Answer(0, serial);
        Assert.Equal((EditorPhase.Closed, EditorReason.Quiet, 1), (refused.Phase, refused.Reason, refused.WaitSeconds));
        rig.Step(TickLoop.SimulationHz);

        // Damage while the draft is open.
        rig.Open(0);
        serial = rig.Editor(0).Serial;
        rig.Hulls.Info(own).DamageTick = rig.Tick;
        Assert.Equal((EditorPhase.Closed, EditorReason.Damaged), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        // A commit for the edit that ended is not taken.
        serial = rig.Editor(0).Serial;
        Assert.True(rig.Input(0).CommitEdit(rig.Editor(0).Token, Edited(rig.Snap(0))));
        Assert.Equal(EditorReason.Unavailable, rig.Answer(0, serial).Reason);
        Assert.Equal(0, rig.Wires[0].Received[(byte)MessageId.EditorData]);
        rig.Step(TickLoop.SimulationHz);

        // Damage while the design is being checked: the check's result is thrown away and its buffer freed.
        var token = rig.Open(0);
        // A design large enough that its check takes far longer than the rest of the tick it starts in.
        var bytes = Edited(rig.Snap(0), 200);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Wires[0].LastPhase == EditorPhase.Checking), "the design should arrive");
        rig.Hulls.Info(own).DamageTick = rig.Tick;
        // Not settled: the edit must end while its check is still running, so the buffer is one a worker reads.
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed));
        Assert.Equal(EditorReason.Damaged, rig.Editor(0).Reason);
        Assert.True(rig.Until(() => !Held(), settle: true), "the buffer should come back when the check is done");
        Assert.Equal(rig.Content.Designs.Length, rig.Field<DesignCatalogue>("_catalogue").Count);
        rig.Step(TickLoop.SimulationHz);

        // Leaving the hull while the design is on its way.
        token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Receiving));
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed));
        Assert.Equal(EditorReason.HullChanged, rig.Editor(0).Reason);
        Assert.True(rig.Until(() => !Held(), settle: true));

        // Back in the pod: a jump order ends an edit, and a hull in a jump is not edited.
        Assert.True(rig.Until(() => rig.Snap(0).Hulls[rig.Snap(0).OwnHull].Design == rig.Content.DroneDesign));
        rig.Input(0).RequestControl(rig.Hulls.HandleAt(own).Packed);
        Assert.True(rig.Until(() => Rig.Index(rig.Own(0)) == own));
        rig.Open(0);
        rig.Input(0).IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = rig.Hulls.State(own).Position + new Vector3(0, 0, -50000) });
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed));
        Assert.Equal(EditorReason.Jumping, rig.Editor(0).Reason);
        serial = rig.Editor(0).Serial;
        rig.Input(0).RequestEdit(rig.Own(0));
        Assert.Equal(EditorReason.Jumping, rig.Answer(0, serial).Reason);
        rig.Input(0).IssueOrder(default);
        Assert.True(rig.Until(() => rig.Hulls.State(own).Jump == JumpPhase.None && rig.Hulls.Info(own).Order.Kind == OrderKind.None));

        // Damage while the clients get ready: the design stays in the catalogue, the hull is not replaced.
        token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        rig.Hulls.Info(own).DamageTick = rig.Tick;
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed));
        Assert.Equal(EditorReason.Damaged, rig.Editor(0).Reason);
        rig.Input(0).ReportInstalledDesigns(rig.Content.Designs.Length + 1);
        rig.Step(120);
        Assert.True(rig.Hulls.IsAlive(rig.Hulls.HandleAt(own)));
        Assert.Equal(own, Rig.Index(rig.Own(0)));
        Assert.Equal(rig.Content.PodDesign, rig.Hulls.Info(own).Design);
        Assert.False(Held());

        // Cancelling ends it too, and disconnecting mid-upload gives the buffer back.
        rig.Step(TickLoop.SimulationHz);
        token = rig.Open(0);
        rig.Input(0).CancelEdit();
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed && rig.Editor(0).Reason == EditorReason.Cancelled));
        token = rig.Open(0);
        var garbage = new byte[4096];
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, garbage.Length, SHA256.HashData(garbage)));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Receiving));
        Assert.True(Held());
        rig.Clients[0]!.RequestLeave();
        Assert.True(rig.Until(() => !Held()));
    }

    [Fact]
    public void OversizedMalformedReorderedAndForeignRequestsAreRefusedAndChangeNothing()
    {
        using var rig = new Rig();
        var catalogue = rig.Field<DesignCatalogue>("_catalogue");
        var startup = catalogue.Count;
        var bytes = Edited(rig.Snap(0));
        var hash = SHA256.HashData(bytes);

        // Another player's hull, a hull that does not exist, and half a request.
        var serial = rig.Editor(0).Serial;
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Begin, rig.Own(1)));
        Assert.Equal((EditorPhase.Closed, EditorReason.NotPiloting), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        serial = rig.Editor(0).Serial;
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Begin, 0xFFFF_0001));
        Assert.Equal(EditorReason.NotPiloting, rig.Answer(0, serial).Reason);
        rig.Raw(0, NetChannel.Reliable, [(byte)MessageId.Editor, (byte)EditorRequestKind.Commit, 1, 2]);
        rig.Raw(0, NetChannel.Reliable, [(byte)MessageId.Editor, 9, 0, 0, 0, 0]);
        // The same request on the wrong channel is not a request.
        rig.Raw(0, NetChannel.Unreliable, Request(EditorRequestKind.Begin, rig.Own(0)));
        rig.Step(10);
        Assert.Equal(EditorPhase.Closed, rig.Editor(0).Phase);

        // A commit and chunks without an edit, and for somebody else's edit.
        var token = rig.Open(0);
        rig.Raw(1, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, bytes.Length, hash));
        rig.Raw(1, NetChannel.Bulk, Chunk(token, 0, bytes));
        rig.Raw(0, NetChannel.Bulk, Chunk(token, 0, bytes.AsSpan(0, 16)));
        rig.Step(10);
        Assert.Equal(EditorPhase.Closed, rig.Editor(1).Phase);
        Assert.Equal(EditorPhase.Open, rig.Editor(0).Phase);

        // A stale token is told where the edit stands; a size no design may have is refused before any buffer is taken.
        serial = rig.Editor(0).Serial;
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token + 1, bytes.Length, hash));
        Assert.Equal((EditorPhase.Open, EditorReason.None), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        foreach (var size in new[] { 0, -1, rig.Content.BlockTransfers.MaxBytes + 1, int.MaxValue })
        {
            serial = rig.Editor(0).Serial;
            rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, size, hash));
            Assert.Equal((EditorPhase.Open, EditorReason.TooLarge), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        }
        Assert.False(rig.Input(0).CommitEdit(token, new byte[rig.Content.BlockTransfers.MaxBytes + 1]));
        Assert.False(rig.Input(0).CommitEdit(token, []));

        void Commit(byte[] design, byte[] digest)
        {
            rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, design.Length, digest));
            Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Receiving), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        }

        // Out of order, past the end, longer than a chunk may be, and with a count that is not its length.
        var half = bytes.Length / 2;
        byte[][] bad =
        [
            Chunk(token, half, bytes.AsSpan(half)),
            Chunk(token, 1, bytes.AsSpan(1, 8)),
            Chunk(token, 0, new byte[bytes.Length + 1]),
            Chunk(token, 0, new byte[EditorDataMessage.MaxChunk + 1]),
            [.. Chunk(token, 0, bytes.AsSpan(0, 8)), 1, 2, 3],
            [(byte)MessageId.EditorData, 1, 2],
        ];
        foreach (var chunk in bad)
        {
            Commit(bytes, hash);
            serial = rig.Editor(0).Serial;
            rig.Raw(0, NetChannel.Bulk, chunk);
            Assert.Equal((EditorPhase.Open, EditorReason.UploadFailed), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        }

        // A repeated first chunk after a good one is out of order as well.
        Commit(bytes, hash);
        serial = rig.Editor(0).Serial;
        rig.Raw(0, NetChannel.Bulk, Chunk(token, 0, bytes.AsSpan(0, half)));
        rig.Raw(0, NetChannel.Bulk, Chunk(token, 0, bytes.AsSpan(0, half)));
        Assert.Equal(EditorReason.UploadFailed, rig.Answer(0, serial).Reason);

        void Whole(byte[] design, byte[] digest, EditorReason expected)
        {
            Commit(design, digest);
            for (var at = 0; at < design.Length; at += EditorDataMessage.MaxChunk)
                rig.Raw(0, NetChannel.Bulk, Chunk(token, at, design.AsSpan(at, Math.Min(EditorDataMessage.MaxChunk, design.Length - at))));
            Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Open, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
            Assert.Equal(expected, rig.Editor(0).Reason);
        }

        // Bytes that are no design, a design under another hash, one with bytes after it, and one that is not canonical.
        var noise = new byte[3000];
        new Random(7).NextBytes(noise);
        Whole(noise, SHA256.HashData(noise), EditorReason.Invalid);
        Whole(bytes, SHA256.HashData(noise), EditorReason.Invalid);
        byte[] trailing = [.. bytes, 0];
        Whole(trailing, SHA256.HashData(trailing), EditorReason.Invalid);
        var overlapping = (byte[])bytes.Clone();
        // The last box's position is its last 24 bytes; put it on top of the first block.
        Array.Clear(overlapping, overlapping.Length - 24, 12);
        Whole(overlapping, SHA256.HashData(overlapping), EditorReason.Invalid);

        // A valid design that is not this hull's kind: another name, another class and block size.
        var pod = rig.Content.Pod;
        var renamed = DesignBytes.Encode(HullDesign.Of(new BoxDesign("Renamed", pod.BlockSize, pod.Boxes.ToArray(), rig.Content.Blocks.Limits, "test", pod.Class, pod.Color), rig.Content.Rules));
        Whole(renamed, SHA256.HashData(renamed), EditorReason.WrongKind);
        var drone = rig.Content.Designs[rig.Content.DroneDesign];
        var resized = DesignBytes.Encode(HullDesign.Of(new BoxDesign(pod.Name, 4f, pod.Boxes.ToArray(), rig.Content.Blocks.Limits, "test", pod.Class, pod.Color), rig.Content.Rules));
        Whole(resized, SHA256.HashData(resized), EditorReason.WrongKind);
        // The catalogue's own drone, named by its hash alone: no upload, and still not this hull's kind.
        serial = rig.Editor(0).Serial;
        var droneBytes = DesignBytes.Encode(drone);
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, droneBytes.Length, SHA256.HashData(droneBytes)));
        Assert.Equal((EditorPhase.Open, EditorReason.WrongKind), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));

        // Nothing of all that reached the catalogue or the hull, no buffer is held, and the real design still goes through.
        Assert.Equal(startup, catalogue.Count);
        Assert.Equal(rig.Content.PodDesign, rig.Hulls.Info(Rig.Index(rig.Own(0))).Design);
        Assert.DoesNotContain(rig.Field<Array>("_uploads").Cast<object>(), u => (bool)u.GetType().GetField("Held")!.GetValue(u)!);
        Commit(bytes, hash);
        for (var at = 0; at < bytes.Length; at += EditorDataMessage.MaxChunk)
            rig.Raw(0, NetChannel.Bulk, Chunk(token, at, bytes.AsSpan(at, Math.Min(EditorDataMessage.MaxChunk, bytes.Length - at))));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.Equal(startup + 1, catalogue.Count);
        Assert.Equal(bytes, catalogue.Bytes[startup]);
    }

    [Fact]
    public void TheSameDesignAgainIsRefusedAKnownDesignIsNotSentAndAFullCatalogueSaysSo()
    {
        using var rig = new Rig(catalogueSpare: 500);
        var catalogue = rig.Field<DesignCatalogue>("_catalogue");
        var startup = catalogue.Count;
        var pod = Rig.Index(rig.Own(0));

        // A damaged hull is repaired first: the design it has and what is left of it with a block more are both
        // refused, and the hull is neither replaced nor repaired.
        rig.Hulls.BlockStates.Lose(rig.Hulls.Info(pod).BlockState, 11);
        var same = DesignBytes.Encode(rig.Content.Pod);
        var token = rig.Open(0);
        var serial = rig.Editor(0).Serial;
        Assert.True(rig.Input(0).CommitEdit(token, same));
        Assert.Equal((EditorPhase.Open, EditorReason.RepairFirst), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        Assert.True(rig.Until(() => rig.Snap(0).Hulls[rig.Snap(0).OwnHull].Revision == 1));
        serial = rig.Editor(0).Serial;
        Assert.True(rig.Input(0).CommitEdit(token, Edited(rig.Snap(0))));
        Assert.Equal((EditorPhase.Open, EditorReason.RepairFirst), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        Assert.Equal(0, rig.Wires[0].Received[(byte)MessageId.EditorData]);
        Assert.Equal(1u, rig.Hulls.Revision(pod));
        Assert.Equal(pod, Rig.Index(rig.Own(0)));

        // Repaired, the design the hull has is no change.
        rig.Input(0).RepairEdit(token);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));
        token = rig.Open(0);
        serial = rig.Editor(0).Serial;
        Assert.True(rig.Input(0).CommitEdit(token, same));
        Assert.Equal((EditorPhase.Open, EditorReason.NoChange), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));

        // The whole ship with one block more: a new design.
        var bytes = Edited(rig.Snap(0));
        Assert.True(bytes.Length <= 500);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        rig.Input(0).ReportInstalledDesigns(startup + 1);
        rig.Input(1).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.Equal(startup + 1, catalogue.Count);
        var rebuilt = Rig.Handle(rig.Editor(0).Hull);
        Assert.Equal(0u, rig.Hulls.Revision(rebuilt.Index));
        Assert.Equal(rig.Content.Pod.BlockCount + 1, rig.Server.Content.Designs[startup].BlockCount);

        // The very same design again on the rebuilt hull is no change either.
        Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));
        token = rig.Open(0);
        serial = rig.Editor(0).Serial;
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.Equal(EditorReason.NoChange, rig.Answer(0, serial).Reason);
        rig.Input(0).CancelEdit();

        // The other player builds the same thing: the catalogue has it, so nothing is sent and no number is used.
        var uploaded = rig.Wires[1].Received[(byte)MessageId.EditorData];
        var theirs = rig.Own(1);
        // Their client has decoded the first player's design by now: only then can it say it is ready for it.
        Assert.True(rig.Until(() => rig.Snap(1).Content.Designs.Length == startup + 1, settle: true), "the other player should have the design");
        token = rig.Open(1);
        Assert.True(rig.Input(1).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(1).Phase == EditorPhase.Applied), $"{rig.Editor(1).Phase}/{rig.Editor(1).Reason}");
        Assert.Equal(uploaded, rig.Wires[1].Received[(byte)MessageId.EditorData]);
        Assert.Equal(startup + 1, catalogue.Count);
        Assert.Equal(theirs, rig.Editor(1).ReplacedHull);
        Assert.Equal(startup, rig.Hulls.Info(Rig.Index(rig.Editor(1).Hull)).Design);

        // Another design does not fit the catalogue's budget: refused whole, the edit stays open, nothing is cut short.
        Assert.True(rig.Until(() => rig.Own(1) == rig.Editor(1).Hull));
        var more = Edited(rig.Snap(1), 3);
        Assert.True(catalogue.TotalBytes + more.Length > rig.Content.BlockTransfers.CatalogueBytes);
        token = rig.Open(1);
        serial = rig.Editor(1).Serial;
        Assert.True(rig.Input(1).CommitEdit(token, more));
        Assert.Equal((EditorPhase.Open, EditorReason.CatalogueFull), (rig.Answer(1, serial).Phase, rig.Editor(1).Reason));
        Assert.Equal(uploaded, rig.Wires[1].Received[(byte)MessageId.EditorData]);
        Assert.Equal(startup + 1, catalogue.Count);
        Assert.Equal(startup + 1, rig.Server.Content.Designs.Length);
        // Going back to a design the catalogue has still works with the catalogue full.
        serial = rig.Editor(1).Serial;
        Assert.True(rig.Input(1).CommitEdit(token, DesignBytes.Encode(rig.Content.Pod)));
        Assert.True(rig.Until(() => rig.Editor(1).Phase == EditorPhase.Applied), $"{rig.Editor(1).Phase}/{rig.Editor(1).Reason}");
        Assert.Equal(rig.Content.PodDesign, rig.Hulls.Info(Rig.Index(rig.Editor(1).Hull)).Design);
    }

    [Fact]
    public void ADamagedHullIsRepairedBeforeAnyDesignNewOrCachedIsTakenAndAShieldThatIsDownIsNoDamage()
    {
        using var rig = new Rig(catalogueSpare: 4000);
        var catalogue = rig.Field<DesignCatalogue>("_catalogue");
        var startup = catalogue.Count;
        var built = rig.Content.Pod.Flight.HullPoints;
        bool Held() => rig.Field<Array>("_uploads").Cast<object>().Any(u => (bool)u.GetType().GetField("Held")!.GetValue(u)!);
        void Applied()
        {
            Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
            Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));
        }

        void Refused(uint token, byte[] fresh, byte[] cached)
        {
            var received = rig.Wires[0].Received[(byte)MessageId.EditorData];
            var count = catalogue.Count;
            var hull = rig.Own(0);
            var serial = rig.Editor(0).Serial;
            Assert.True(rig.Input(0).CommitEdit(token, fresh));
            Assert.Equal((EditorPhase.Open, EditorReason.RepairFirst), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
            // Named by its hash past the session, as a design the catalogue has: no upload is needed, still refused.
            serial = rig.Editor(0).Serial;
            rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, cached.Length, SHA256.HashData(cached)));
            Assert.Equal((EditorPhase.Open, EditorReason.RepairFirst), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
            rig.Step(10);
            Assert.Equal((received, count, false, hull), (rig.Wires[0].Received[(byte)MessageId.EditorData], catalogue.Count, Held(), rig.Own(0)));
        }

        // A shield that is down is no damage: a new design is taken.
        var pod = Rig.Index(rig.Own(0));
        rig.Hulls.State(pod).Shield = 0f;
        rig.Hulls.State(pod).ShieldOffline = true;
        var token = rig.Open(0);
        var first = Edited(rig.Snap(0));
        Assert.True(rig.Input(0).CommitEdit(token, first));
        rig.Input(0).ReportInstalledDesigns(startup + 2);
        rig.Input(1).ReportInstalledDesigns(startup + 2);
        Applied();
        Assert.Equal(startup + 1, catalogue.Count);

        // Hull points lost, no block: a new design and the catalogue's pod are both refused; a repair is taken.
        var ship = Rig.Index(rig.Own(0));
        rig.Hulls.State(ship).Hull -= 5f;
        Assert.Equal(0u, rig.Hulls.Revision(ship));
        token = rig.Open(0);
        var second = Edited(rig.Snap(0));
        var pods = DesignBytes.Encode(rig.Content.Pod);
        Refused(token, second, pods);
        rig.Input(0).RepairEdit(token);
        Applied();
        Assert.Equal(startup, rig.Hulls.Info(Rig.Index(rig.Own(0))).Design);

        // Whole again, it is edited: the catalogue's pod, with nothing sent.
        token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, pods));
        Applied();
        Assert.Equal(rig.Content.PodDesign, rig.Hulls.Info(Rig.Index(rig.Own(0))).Design);

        // A block lost: the same, and after the repair a new design goes through.
        ship = Rig.Index(rig.Own(0));
        Wear(rig, ship, lost: 11, worn: 1);
        Assert.True(rig.Hulls.State(ship).Hull < built);
        token = rig.Open(0);
        Refused(token, second, first);
        rig.Input(0).RepairEdit(token);
        Applied();
        token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, second));
        Applied();
        Assert.Equal(startup + 2, catalogue.Count);
        Assert.Equal(startup + 1, rig.Hulls.Info(Rig.Index(rig.Own(0))).Design);
    }

    [Fact]
    public void APlayerWhoJoinsWhileADesignIsAppliedGetsItWhicheverManifestTheyWereSent()
    {
        using var rig = new Rig(clients: 4, joined: 2, peerWait: 0);
        var startup = rig.Content.Designs.Length;

        // The first latecomer has asked for the manifest, which lists the designs of that moment, and is then held up.
        rig.Join(2);
        rig.Wires[2].RefuseBulk = true;
        Assert.True(rig.Until(() => rig.Wires[2].Received[(byte)MessageId.DesignRequest] > 0));
        rig.Step(5);

        var bytes = Edited(rig.Snap(0));
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");

        // The second joins now: its manifest lists the new design, and it fetches it as part of being let in.
        rig.Join(3);
        Assert.True(rig.Until(() => rig.Snap(3).HullCount > 0, settle: true), "the second latecomer should be let in");
        Assert.Equal(startup + 1, rig.Snap(3).Content.Designs.Length);
        Assert.Equal(bytes, DesignBytes.Encode(rig.Snap(3).Content.Designs[startup]));
        Assert.Equal(0, rig.Wires[3].Sent[(byte)MessageId.DesignNotice]);

        // The first gets its old manifest, is let in knowing the old designs, and is told of the new one then.
        Assert.Equal(SessionState.Downloading, rig.Snap(2).State);
        rig.Wires[2].RefuseBulk = false;
        Assert.True(rig.Until(() => rig.Snap(2).HullCount > 0 && rig.Snap(2).Content.Designs.Length == startup + 1, settle: true), "the first latecomer should catch up");
        Assert.Equal(1, rig.Wires[2].Sent[(byte)MessageId.DesignNotice]);
        Assert.Equal(bytes, DesignBytes.Encode(rig.Snap(2).Content.Designs[startup]));

        // Applied once the owner is ready. Whoever was let in with the design sees the hull at once; whoever
        // was told of it later sees it when their renderer has it.
        rig.Input(0).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        var hull = rig.Editor(0).Hull;
        bool Sees(int client)
        {
            var s = rig.Snap(client);
            for (var i = 0; i < s.HullCount; i++)
                if (s.Hulls[i].Id == hull && s.Hulls[i].Design == startup) return true;
            return false;
        }
        Assert.True(rig.Until(() => Sees(0) && Sees(3)));
        rig.Step(120);
        Assert.False(Sees(1));
        Assert.False(Sees(2));
        rig.Input(1).ReportInstalledDesigns(startup + 1);
        rig.Input(2).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() => Sees(1) && Sees(2)));
        for (var client = 0; client < 4; client++)
        {
            Assert.Equal(SessionState.Active, rig.Snap(client).State);
            Assert.Null(rig.Clients[client]!.Fault);
        }
    }

    [Fact]
    public void RefusedSendsAreTriedAgainAndAClientThatCannotPrepareTheDesignEndsItsOwnEdit()
    {
        using var rig = new Rig(ready: 2);
        var startup = rig.Content.Designs.Length;
        var old = rig.Own(0);
        var bytes = Edited(rig.Snap(0));

        // Nothing reliable reaches the owner, and no bulk data the other player, for a while.
        var token = rig.Open(0);
        rig.Wires[0].RefuseReliable = true;
        rig.Wires[1].RefuseReliable = rig.Wires[1].RefuseBulk = true;
        // The commit goes past the session's own check: it still believes the edit is open.
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        rig.Step(60);
        Assert.Equal(EditorPhase.Open, rig.Editor(0).Phase);
        Assert.Equal(startup, rig.Snap(1).Content.Designs.Length);
        rig.Wires[0].RefuseReliable = false;
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        rig.Wires[1].RefuseReliable = false;
        Assert.True(rig.Until(() => rig.Wires[1].Sent[(byte)MessageId.DesignNotice] == 1));
        rig.Step(90);
        Assert.Equal(startup, rig.Snap(1).Content.Designs.Length);
        rig.Wires[1].RefuseBulk = false;
        Assert.True(rig.Until(() => rig.Snap(1).Content.Designs.Length == startup + 1, settle: true), "the other player should get the design once bulk data flows");

        // The owner's renderer could not build the mesh: the edit ends, the old hull stays and is still flown.
        Assert.True(rig.Until(() => rig.Snap(0).Content.Designs.Length == startup + 1, settle: true));
        rig.Input(0).ReportInstalledDesigns(startup);
        rig.Input(0).ReportDesignFailure(startup + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.Equal(EditorReason.ClientFailed, rig.Editor(0).Reason);
        Assert.True(rig.Hulls.IsAlive(Rig.Handle(old)));
        Assert.Equal(old, rig.Own(0));
        Assert.Equal(SessionState.Active, rig.Snap(0).State);

        // A later build that works makes the design usable after all: the same bytes need no upload now.
        var uploaded = rig.Wires[0].Received[(byte)MessageId.EditorData];
        rig.Input(0).ReportInstalledDesigns(startup + 1);
        rig.Input(1).ReportInstalledDesigns(startup + 1);
        rig.Step(5);
        token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.Equal(uploaded, rig.Wires[0].Received[(byte)MessageId.EditorData]);

        // An owner whose client never says anything about the design is not waited for longer than the rules say.
        Assert.True(rig.Until(() => rig.Own(1) != 0 && rig.Snap(1).Content.Designs.Length == startup + 1));
        var theirs = rig.Own(1);
        token = rig.Open(1);
        Assert.True(rig.Input(1).CommitEdit(token, Edited(rig.Snap(1), 2)));
        Assert.True(rig.Until(() => rig.Editor(1).Phase == EditorPhase.Publishing, settle: true), $"{rig.Editor(1).Phase}/{rig.Editor(1).Reason}");
        Assert.True(rig.Until(() => rig.Editor(1).Phase == EditorPhase.Closed, 2 * TickLoop.SimulationHz + 30));
        Assert.Equal(EditorReason.ClientNotReady, rig.Editor(1).Reason);
        Assert.True(rig.Hulls.IsAlive(Rig.Handle(theirs)));
    }

    [Fact]
    public void TheServerHoldsOnlyAsManyDesignsAsItHasBuffersAndSpacesAPlayersCommits()
    {
        using var rig = new Rig(uploads: 1, commit: 1);
        var bytes = Edited(rig.Snap(0));
        var hash = SHA256.HashData(bytes);
        var other = Edited(rig.Snap(1), 2);

        // The first player announces a design and sends none of it: the one buffer is theirs.
        var token = rig.Open(0);
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, bytes.Length, hash));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Receiving));
        var began = rig.Tick;

        // The second is told the server is busy, keeps the edit, and nothing of theirs is sent.
        var theirs = rig.Open(1);
        var serial = rig.Editor(1).Serial;
        Assert.True(rig.Input(1).CommitEdit(theirs, other));
        Assert.Equal((EditorPhase.Open, EditorReason.Busy), (rig.Answer(1, serial).Phase, rig.Editor(1).Reason));
        rig.Step(30);
        Assert.Equal(0, rig.Wires[1].Received[(byte)MessageId.EditorData]);

        // A design that does not arrive in time gives the buffer back; the edit stays open.
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Open, 6 * TickLoop.SimulationHz));
        Assert.Equal(EditorReason.UploadTimedOut, rig.Editor(0).Reason);
        Assert.InRange(rig.Tick - began, 5 * TickLoop.SimulationHz, 5 * TickLoop.SimulationHz + 10);

        // Now the second player's design goes through.
        Assert.True(rig.Input(1).CommitEdit(theirs, other));
        Assert.True(rig.Until(() => rig.Editor(1).Phase == EditorPhase.Publishing, settle: true), $"{rig.Editor(1).Phase}/{rig.Editor(1).Reason}");

        // A commit right after a failed one is asked to wait; after the wait it is taken.
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, bytes.Length, hash));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Receiving));
        serial = rig.Editor(0).Serial;
        rig.Raw(0, NetChannel.Bulk, Chunk(token, 5, bytes.AsSpan(5, 5)));
        Assert.Equal(EditorReason.UploadFailed, rig.Answer(0, serial).Reason);
        serial = rig.Editor(0).Serial;
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Commit, token, bytes.Length, hash));
        var wait = rig.Answer(0, serial);
        Assert.Equal((EditorPhase.Open, EditorReason.TooSoon, 1), (wait.Phase, wait.Reason, wait.WaitSeconds));
        rig.Step(TickLoop.SimulationHz);
        Assert.True(rig.Input(0).CommitEdit(token, bytes));
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.Equal(rig.Content.Designs.Length + 2, rig.Field<DesignCatalogue>("_catalogue").Count);
    }

    [Fact]
    public void AnEditGoesThroughOverUdpOnRunningThreadsWithinAPlayersAllowance()
    {
        // The shipped rules, a real socket and every thread running: the chunks count against the builder's
        // allowance, the check runs beside a live tick, and the other player's session is told by the server.
        var content = GameContent.Load();
        var port = TestNet.Port(28);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, content, RakNetTransport.Listen(port, 8));
        server.Start();
        using var builder = GameSession.Join("127.0.0.1", port, content, "Builder");
        using var watcher = GameSession.Join("127.0.0.1", port, content, "Watcher");
        var first = TestNet.WaitFor(builder, s => s.State == SessionState.Active && s.OwnHull >= 0);
        TestNet.WaitFor(watcher, s => s.State == SessionState.Active && s.OwnHull >= 0);
        var startup = first.Content.Designs.Length;
        var old = first.Hulls[first.OwnHull].Id;
        var bytes = Edited(first, 150);
        Assert.True(bytes.Length > 4 * EditorDataMessage.MaxChunk);

        builder.Client.Input.RequestEdit(old);
        var open = TestNet.WaitFor(builder, s => s.Editor.Serial != 0);
        Assert.Equal((EditorPhase.Open, EditorReason.None), (open.Editor.Phase, open.Editor.Reason));
        Assert.True(builder.Client.Input.CommitEdit(open.Editor.Token, bytes));
        builder.Client.Input.ReportInstalledDesigns(startup + 1);
        watcher.Client.Input.ReportInstalledDesigns(startup + 1);
        var done = TestNet.WaitFor(builder, s => s.Editor.Phase is EditorPhase.Applied or EditorPhase.Closed || s.Editor.Reason != EditorReason.None, 30);
        Assert.Equal((EditorPhase.Applied, EditorReason.None, old), (done.Editor.Phase, done.Editor.Reason, done.Editor.ReplacedHull));
        var hull = done.Editor.Hull;

        var mine = TestNet.WaitFor(builder, s => s.OwnHull >= 0 && s.Hulls[s.OwnHull].Id == hull);
        Assert.Equal(startup, mine.Hulls[mine.OwnHull].Design);
        Assert.Equal(bytes, DesignBytes.Encode(mine.Content.Designs[startup]));
        var theirs = TestNet.WaitFor(watcher, s =>
        {
            for (var i = 0; i < s.HullCount; i++)
                if (s.Hulls[i].Id == hull && s.Hulls[i].Design == startup) return true;
            return false;
        });
        Assert.Equal(bytes, DesignBytes.Encode(theirs.Content.Designs[startup]));
        Assert.Equal(SessionState.Active, builder.Client.AcquireSnapshot().State);
        Assert.Null(server.Fault);
        Assert.Null(builder.Fault);
        Assert.Null(watcher.Fault);
    }

    /// <summary>Takes a block and half of another's hit points from a hull, as damage would, without the stamp of a hit.</summary>
    private static void Wear(Rig rig, int hull, int lost, int worn)
    {
        var blocks = rig.Hulls.Info(hull).BlockState;
        Assert.True(rig.Hulls.BlockStates.Lose(blocks, lost));
        rig.Hulls.BlockStates.SetHitPoints(blocks, worn, rig.Hulls.BlockStates.HitPoints(blocks)[worn] / 2);
        rig.Hulls.State(hull).Hull = (float)rig.Hulls.BlockStates.HitTotal(blocks);
    }

    [Fact]
    public void ARepairRebuildsTheHullAsItsBlueprintAndIsRefusedForAWholeHullAStaleEditFreshDamageAndTooSoon()
    {
        using var rig = new Rig(peerWait: 0, commit: 2);
        var catalogue = rig.Field<DesignCatalogue>("_catalogue");
        var startup = catalogue.Count;
        var old = rig.Own(0);
        var pod = Rig.Index(old);
        var design = rig.Content.Pod;
        var built = design.Flight.HullPoints;

        // A whole hull has nothing to repair, and a shield that is down is not the hull's damage.
        rig.Hulls.State(pod).Shield = 0f;
        rig.Hulls.State(pod).ShieldOffline = true;
        var token = rig.Open(0);
        var serial = rig.Editor(0).Serial;
        rig.Input(0).RepairEdit(token);
        Assert.Equal((EditorPhase.Open, EditorReason.NoChange), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        Assert.Equal(old, rig.Own(0));

        // The cargo block goes and a plate is worn to half. What the hull has left falls; what it was built with does not.
        Wear(rig, pod, lost: 11, worn: 1);
        rig.Hulls.State(pod).Shield = 100f;
        rig.Hulls.State(pod).ShieldOffline = false;
        rig.Hulls.State(pod).ShieldCooldown = 1000f;
        HullView own = default;
        Assert.True(rig.Until(() => { var s = rig.Snap(0); own = s.Hulls[s.OwnHull]; return own.Revision == 1 && own.Hull < built; }));
        Assert.True(rig.Hulls.Flight(pod).HullPoints < built);
        Assert.Equal(built, rig.Snap(0).Content.Designs[own.Design].Flight.HullPoints);
        Assert.True(rig.Snap(0).Content.Designs[own.Design].NeedsRepair(own.Revision, own.Hull));
        Assert.Equal(rig.Hulls.State(pod).Hull, own.Hull);

        // Another player naming the edit, and the owner naming one that is not theirs: each is sent the answer
        // they had last again, which says where they stand, and nothing is done.
        serial = rig.Editor(1).Serial;
        rig.Raw(1, NetChannel.Reliable, Request(EditorRequestKind.Repair, token));
        Assert.Equal(EditorPhase.Closed, rig.Answer(1, serial).Phase);
        serial = rig.Editor(0).Serial;
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Repair, token + 1));
        Assert.Equal((EditorPhase.Open, EditorReason.NoChange, token), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason, rig.Editor(0).Token));
        rig.Step(10);
        Assert.True(rig.Hulls.IsAlive(Rig.Handle(old)));
        Assert.Equal(1u, rig.Hulls.Revision(pod));

        rig.Hulls.Info(pod).Visual.Hit(design, 1, design.BlockHitPoints(1) * 0.2f, design.BlockOffsets[1], System.Numerics.Vector3.UnitX, 1);
        Assert.True(rig.Hulls.Info(pod).Visual.Count > 0);
        // The repair: the refusal above did not count as one, so it is taken at once.
        rig.Hulls.State(pod).Energy = 0f;
        var before = rig.Hulls.State(pod);
        rig.Input(0).RepairEdit(token);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        var applied = rig.Editor(0);
        Assert.Equal(old, applied.ReplacedHull);
        Assert.NotEqual(old, applied.Hull);
        Assert.False(rig.Hulls.IsAlive(Rig.Handle(old)));
        var next = Rig.Handle(applied.Hull);

        // The same design under the same number: no bytes were sent, no design was added or announced.
        Assert.Equal((rig.Content.PodDesign, true, rig.Snap(0).PlayerId), (rig.Hulls.Info(next.Index).Design, rig.Hulls.Info(next.Index).Piloted, rig.Hulls.Info(next.Index).Owner));
        Assert.Equal(startup, catalogue.Count);
        Assert.Equal(startup, rig.Server.Content.Designs.Length);
        Assert.Same(design, rig.Server.Content.Designs[rig.Content.PodDesign]);
        Assert.Equal(0, rig.Wires[0].Received[(byte)MessageId.EditorData]);
        Assert.Equal(0, rig.Wires[0].Sent[(byte)MessageId.DesignNotice] + rig.Wires[1].Sent[(byte)MessageId.DesignNotice]);

        // Every block back and whole, at the blueprint's hull points; the same place and heading.
        Assert.True(BlockSet.IsFull(rig.Hulls.Blocks(next.Index), design.BlockCount));
        Assert.Equal(0u, rig.Hulls.Revision(next.Index));
        Assert.Equal(0, rig.Hulls.Info(next.Index).Visual.Count);
        Assert.Equal(0u, rig.Hulls.Info(next.Index).Visual.Spark.Sequence);
        Assert.Equal((built, built), (rig.Hulls.State(next.Index).Hull, rig.Hulls.Flight(next.Index).HullPoints));
        for (var block = 0; block < design.BlockCount; block++)
            Assert.Equal(design.BlockHitPoints(block), rig.Hulls.BlockHp(next.Index)[block]);
        Assert.True(rig.Hulls.State(next.Index).Position.RelativeTo(before.Position).Length() < 1e-3f);
        Assert.Equal(before.Orientation, rig.Hulls.State(next.Index).Orientation);

        // Repair is free, charge is not: the shield and the battery are handed back as they were.
        Assert.Equal(100f, rig.Hulls.State(next.Index).Shield);
        Assert.True(rig.Hulls.State(next.Index).Energy < rig.Hulls.Flight(next.Index).EnergyStorage * 0.5f);

        // Both players see the whole ship, and its owner's client has nothing left to repair.
        Assert.True(rig.Until(() => rig.Own(0) == applied.Hull));
        var mine = rig.Snap(0);
        own = mine.Hulls[mine.OwnHull];
        Assert.Equal((rig.Content.PodDesign, 0u, built), (own.Design, own.Revision, own.Hull));
        Assert.False(mine.Content.Designs[own.Design].NeedsRepair(own.Revision, own.Hull));
        Assert.True(rig.Until(() =>
        {
            var s = rig.Snap(1);
            for (var i = 0; i < s.HullCount; i++)
                if (s.Hulls[i].Id == applied.Hull) return s.Hulls[i].Revision == 0 && BlockSet.IsFull(s.Hulls[i].Blocks.Span, design.BlockCount);
            return false;
        }), "the other player should see the repaired hull");

        // Repairs are spaced as commits are: a second one right away waits, and is taken after the wait.
        token = rig.Open(0);
        pod = next.Index;
        Wear(rig, pod, lost: 11, worn: 2);
        serial = rig.Editor(0).Serial;
        rig.Input(0).RepairEdit(token);
        var wait = rig.Answer(0, serial);
        Assert.Equal((EditorPhase.Open, EditorReason.TooSoon), (wait.Phase, wait.Reason));
        Assert.InRange(wait.WaitSeconds, 1, 2);
        Assert.Equal(1u, rig.Hulls.Revision(pod));
        rig.Step(2 * TickLoop.SimulationHz);

        // Damage since the edit began ends it: a repair is no way around the quiet time.
        var stamp = rig.Tick;
        rig.Hulls.Info(pod).DamageTick = stamp;
        serial = rig.Editor(0).Serial;
        // Sent past the session, so that the request and the damage reach the server in one tick.
        rig.Raw(0, NetChannel.Reliable, Request(EditorRequestKind.Repair, token));
        Assert.Equal((EditorPhase.Closed, EditorReason.Damaged), (rig.Answer(0, serial).Phase, rig.Editor(0).Reason));
        rig.Step(10);
        Assert.True(rig.Hulls.IsAlive(next));
        Assert.Equal(1u, rig.Hulls.Revision(pod));
        serial = rig.Editor(0).Serial;
        rig.Input(0).RepairEdit(token);
        Assert.Equal(EditorReason.Unavailable, rig.Answer(0, serial).Reason);

        // Once the hull has been left alone long enough it is repaired, and keeps the stamp of its last damage.
        rig.Step(TickLoop.SimulationHz);
        token = rig.Open(0);
        rig.Input(0).RepairEdit(token);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        var last = Rig.Handle(rig.Editor(0).Hull);
        Assert.Equal(next.Packed, rig.Editor(0).ReplacedHull);
        Assert.Equal((stamp, 0u, rig.Content.PodDesign), (rig.Hulls.Info(last.Index).DamageTick, rig.Hulls.Revision(last.Index), rig.Hulls.Info(last.Index).Design));
        Assert.Equal(startup, catalogue.Count);
        Assert.Null(rig.Clients[0]!.Fault);
        Assert.Null(rig.Clients[1]!.Fault);
    }

    [Fact]
    public void ARepairedEscapeDroneIsStillOneAndAJumpOrderedWhileTheRepairWaitsEndsIt()
    {
        using var rig = new Rig(clients: 1, joined: 1, ready: 2);
        var ship = rig.Own(0);

        // A jump ordered while the repair waits for the owner's client: the edit ends, the hull stays as it is.
        Wear(rig, Rig.Index(ship), lost: 11, worn: 1);
        var token = rig.Open(0);
        var connection = rig.Field<Array>("_connections").GetValue(0)!;
        var ready = (ulong[])connection.GetType().GetField("ReadyDesigns")!.GetValue(connection)!;
        BlockSet.Remove(ready, rig.Content.PodDesign);
        rig.Input(0).RepairEdit(token);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Publishing));
        rig.Input(0).IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = rig.Hulls.State(Rig.Index(ship)).Position + new Double3(600, 0, 0) });
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Closed));
        Assert.Equal(EditorReason.Jumping, rig.Editor(0).Reason);
        BlockSet.Add(ready, rig.Content.PodDesign);
        rig.Step(30);
        Assert.Equal(ship, rig.Own(0));
        Assert.Equal(1u, rig.Hulls.Revision(Rig.Index(ship)));
        rig.Input(0).IssueOrder(default);
        Assert.True(rig.Until(() => rig.Hulls.State(Rig.Index(ship)).Jump == JumpPhase.None && rig.Hulls.Info(Rig.Index(ship)).Order.Kind == OrderKind.None));

        // Out in the escape drone, which loses a block that is not its core and is repaired.
        rig.Input(0).RequestControl(0);
        Assert.True(rig.Until(() => rig.Own(0) != ship));
        var drone = Rig.Handle(rig.Own(0));
        var design = rig.Content.Designs[rig.Content.DroneDesign];
        var spare = Enumerable.Range(0, design.BlockCount).First(block => !BlockSet.Has(design.CoreSet, block));
        Assert.True(rig.Hulls.BlockStates.Lose(rig.Hulls.Info(drone.Index).BlockState, spare));
        token = rig.Open(0);
        rig.Input(0).RepairEdit(token);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        var repaired = Rig.Handle(rig.Editor(0).Hull);
        Assert.Equal(drone.Packed, rig.Editor(0).ReplacedHull);
        Assert.True(rig.Until(() => rig.Own(0) == repaired.Packed));
        Assert.Equal((rig.Content.DroneDesign, true, 0u), (rig.Hulls.Info(repaired.Index).Design, rig.Hulls.Info(repaired.Index).EscapeDrone, rig.Hulls.Revision(repaired.Index)));

        // Still the temporary drone it was: no stepping out of it, and it goes when its pilot is back in the ship.
        var count = rig.Hulls.Count;
        rig.Input(0).RequestControl(0);
        rig.Step(6);
        Assert.Equal((repaired.Packed, count), (rig.Own(0), rig.Hulls.Count));
        rig.Input(0).RequestControl(ship);
        Assert.True(rig.Until(() => rig.Own(0) == ship));
        Assert.False(rig.Hulls.IsAlive(repaired));
    }

    /// <summary>Adds a one-cell block of a type on the first free face of the draft.</summary>
    private static void Place(BuildDraft draft, BlockType type)
    {
        var one = new GridSize(1, 1, 1);
        for (var target = 0; target < draft.Boxes.Length; target++)
            for (var side = 0; side < 6; side++)
                if (draft.Add(draft.Snap(target, side, one), one, type) == DraftEdit.Done) return;
        Assert.Fail("the draft should have a free face");
    }

    /// <summary>Commits a draft of the client's own hull and waits until the client flies the rebuilt hull.</summary>
    private static int Build(Rig rig, BuildDraft draft, int designs)
    {
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, export.Bytes));
        rig.Input(0).ReportInstalledDesigns(designs);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));
        return Rig.Index(rig.Editor(0).Hull);
    }

    private static void AssertFinite(in HullState state)
    {
        Assert.True(double.IsFinite(state.Position.X) && double.IsFinite(state.Position.Y) && double.IsFinite(state.Position.Z));
        Assert.True(float.IsFinite(state.Velocity.Length()) && float.IsFinite(state.AngularVelocity.Length()) && float.IsFinite(state.Orientation.Length()));
        Assert.True(float.IsFinite(state.Energy) && float.IsFinite(state.Shield) && float.IsFinite(state.Hull) && float.IsFinite(state.Heat));
    }

    [Fact]
    public void AHullWithoutThrustTorqueOrPowerIsBuiltAndLiesStillAndAShieldWithNothingToFeedItGainsNothing()
    {
        using var rig = new Rig(clients: 1, joined: 1, peerWait: 0);
        var startup = rig.Content.Designs.Length;
        var content = rig.Content;

        // The pod stripped to its core and two plates: nothing that pushes, turns, makes or holds power.
        var draft = BuildDraft.FromBlueprint(content.Pod, content);
        Assert.Equal(DraftEdit.Done, draft.Delete([3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
        var hull = Build(rig, draft, startup + 1);
        var flight = rig.Hulls.Flight(hull);
        Assert.Equal((Vector3.Zero, Vector3.Zero, 0f, 0f, 0f, 0f), (flight.Acceleration, flight.TurnRate, flight.MaxSpeed, flight.PowerGenerated, flight.EnergyStorage, flight.ShieldCapacity));
        Assert.Equal((0f, 0f), (rig.Hulls.State(hull).Energy, rig.Hulls.State(hull).Shield));

        // Everything a pilot can ask of it, and an order for the autopilot: it lies where it is, with real numbers.
        var start = rig.Hulls.State(hull);
        rig.Input(0).Submit(new PlayerControls { Forward = 1f, Strafe = 1f, Vertical = -1f, Roll = 1f, Boost = true, Align = true, Aim = Vector3.UnitX });
        rig.Step(180);
        rig.Input(0).Submit(default);
        rig.Input(0).IssueOrder(new HullOrder { Kind = OrderKind.MoveTo, Target = start.Position + new Double3(0, 300, -500) });
        rig.Step(180);
        var state = rig.Hulls.State(hull);
        AssertFinite(state);
        Assert.Equal((start.Position, start.Orientation, Vector3.Zero, Vector3.Zero), (state.Position, state.Orientation, state.Velocity, state.AngularVelocity));
        Assert.Equal((0f, 0f, false, rig.Hulls.Flight(hull).HullPoints), (state.Energy, state.Shield, state.Boosting, state.Hull));
        var seen = rig.Snap(0);
        var own = seen.Hulls[seen.OwnHull];
        Assert.True(double.IsFinite(own.Position.X + own.Position.Y + own.Position.Z) && float.IsFinite(own.Velocity.Length()) && float.IsFinite(own.Hull) && float.IsFinite(seen.PredictionError));
        Assert.True(seen.PredictionError < 0.01f, $"prediction is off by {seen.PredictionError}");
        rig.Input(0).IssueOrder(default);
        rig.Step(6);

        // A shield emitter and a battery on it, and still no generator: the shield has a capacity and nothing to fill it.
        draft = BuildDraft.FromBlueprint(rig.Server.Content.Designs[startup], rig.Server.Content);
        Place(draft, content.Blocks.Get("shield"));
        Place(draft, content.Blocks.Get("battery"));
        hull = Build(rig, draft, startup + 2);
        flight = rig.Hulls.Flight(hull);
        Assert.True(flight.ShieldCapacity > 0 && flight.EnergyStorage > 0 && flight.ShieldRecharge > 0);
        Assert.Equal(0f, flight.PowerGenerated);
        rig.Step(600);
        state = rig.Hulls.State(hull);
        AssertFinite(state);
        Assert.Equal((0f, 0f), (state.Energy, state.Shield));

        // Charge put in the battery by hand is all there is: the shield takes what that pays for and no more.
        rig.Hulls.State(hull).Energy = 10f;
        Assert.True(rig.Until(() => rig.Hulls.State(hull).Energy <= 0f, 1200));
        var charged = rig.Hulls.State(hull).Shield;
        Assert.InRange(charged, 1f, flight.ShieldRecharge * 10f / flight.ShieldPower);
        rig.Step(600);
        state = rig.Hulls.State(hull);
        AssertFinite(state);
        Assert.Equal((0f, charged), (state.Energy, state.Shield));
        Assert.Null(rig.Clients[0]!.Fault);
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void ADroneLeftAfterALostHullStartsItsQuietTimeAtTheLoss()
    {
        using var rig = new Rig(quiet: 2);
        var pod = Rig.Handle(rig.Own(0));
        Assert.Equal(long.MinValue, rig.Hulls.Info(pod.Index).DamageTick);

        // The core goes at the first touch.
        rig.Hulls.BlockStates.SetHitPoints(rig.Hulls.Info(pod.Index).BlockState, 0, 0.01f);
        rig.Hulls.State(pod.Index).Shield = 0f;
        rig.Hulls.State(pod.Index).ShieldOffline = true;
        var drone = (byte)rig.Content.DroneDesign;
        var rammer = rig.Hulls.Create(0, drone, rig.Content.Designs[drone], rig.Hulls.State(pod.Index).Position + new Vector3(0, -6, 0));
        rig.Hulls.State(rammer.Index).Velocity = new Vector3(0, 60, 0);
        Assert.True(rig.Until(() => !rig.Hulls.IsAlive(pod), 600), "the pod should be destroyed");
        var lost = rig.Tick;
        Assert.True(rig.Until(() => rig.Snap(0).OwnHull >= 0 && rig.Snap(0).Hulls[rig.Snap(0).OwnHull].Design == drone));
        var own = Rig.Index(rig.Own(0));
        Assert.InRange(rig.Hulls.Info(own).DamageTick, lost - 2, lost);

        var serial = rig.Editor(0).Serial;
        rig.Input(0).RequestEdit(rig.Own(0));
        var refused = rig.Answer(0, serial);
        Assert.Equal((EditorPhase.Closed, EditorReason.Quiet), (refused.Phase, refused.Reason));
        Assert.InRange(refused.WaitSeconds, 1, 2);

        // Stepping out of a hull does not shake the damage off, and going back in finds it where it was.
        rig.Step(2 * TickLoop.SimulationHz);
        rig.Open(0);
    }

    [Fact]
    public void AWarmServerTickTakesNoMemoryWithAnEditOpenAndAfterOneIsApplied()
    {
        using var rig = new Rig();
        var startup = rig.Content.Designs.Length;
        var token = rig.Open(0);
        Assert.True(rig.Input(0).CommitEdit(token, Edited(rig.Snap(0))));
        rig.Input(0).ReportInstalledDesigns(startup + 1);
        rig.Input(1).ReportInstalledDesigns(startup + 1);
        Assert.True(rig.Until(() => rig.Editor(0).Phase == EditorPhase.Applied, settle: true), $"{rig.Editor(0).Phase}/{rig.Editor(0).Reason}");
        Assert.True(rig.Until(() => rig.Own(0) == rig.Editor(0).Hull));
        rig.Open(1);
        rig.Step(240);

        // The server alone, on this thread: its tick with the grown catalogue, an edited hull and an open edit.
        var server = (ITickHandler)rig.Server;
        for (var i = 0; i < 60; i++) server.Tick(rig.Tick++);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 120; i++)
            while (allocation.Measuring()) server.Tick(rig.Tick++);
        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(EditorPhase.Open, rig.Editor(1).Phase);

        // And the sessions' ticks, with the catalogue grown and an edit open.
        rig.Step(60);
        var sessions = new AllocationProbe();
        for (var i = 0; i < 120; i++)
            while (sessions.Measuring()) rig.Step();
        Assert.Equal(0, sessions.Bytes);
    }
}
