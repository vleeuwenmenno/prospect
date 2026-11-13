using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class DesignTransferTests
{
    private sealed class Wire(ITransport inner) : ITransport, ITransportHandler
    {
        internal int Designs;
        internal bool Refuse, Gap;
        private readonly byte[] _rewrite = new byte[65536];
        public string Description => inner.Description;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => inner.IsInProcess;
        private ITransportHandler? _handler;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget) { _handler = handler; return inner.Poll(this, budget); }
        public void OnConnected(ITransport transport, int peer) => _handler!.OnConnected(this, peer);
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => _handler!.OnDisconnected(this, peer, reason);
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) => _handler!.OnMessage(this, peer, channel, payload);
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (payload[0] == (byte)MessageId.DesignData && payload[1] != 255) Designs++;
            if (Gap && payload[0] == (byte)MessageId.Snapshot)
            {
                var reader = new NetReader(payload[1..]); SnapshotHeader.TryRead(ref reader, out var header);
                var writer = new NetWriter(_rewrite); header.Write(ref writer);
                for (var i = 0; i < header.HullCount; i++)
                {
                    HullRecord.TryRead(ref reader, out var record);
                    record.LossStart = record.Revision; record.LossCount = 0; record.Write(ref writer);
                }
                return inner.Send(peer, channel, writer.Written);
            }
            if (Refuse && channel == NetChannel.Bulk) return false;
            return inner.Send(peer, channel, payload);
        }
        public void Disconnect(int peer) => inner.Disconnect(peer);
        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);
        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);
        public void Dispose() { }
    }

    [Fact]
    public void ThreeHundredMixedBoxesLoadTransferCollideAndBuildAfterDamage()
    {
        var local = GameContent.Load();
        using var files = new ContentCopy();
        var blocks = Enumerable.Range(0, 300).Select(i => new
        {
            at = new[] { i * 4, 0, 0 },
            size = new[] { i % 2 + 1, 1, 1 },
            type = i switch { 0 => "core", 1 => "thruster", 2 => "gyro", 3 => "generator", 4 => "engine", _ => "hull" }
        });
        files.Write("ships/command_pod.json", JsonSerializer.Serialize(new
        {
            version = 2,
            name = "Three hundred",
            @class = "ship",
            blockSize = 1,
            color = new[] { 0.4, 0.5, 0.6 },
            blocks
        }));
        var fleet = System.Text.Json.Nodes.JsonNode.Parse(files.Read("ships/fleet.json"))!;
        fleet["startingFleet"] = new System.Text.Json.Nodes.JsonArray();
        files.Write("ships/fleet.json", fleet.ToJsonString());
        var authoritative = files.Load();
        Assert.Equal(300, authoritative.Pod.BlockCount);
        using var link = new LoopbackLink();
        var wire = new Wire(link.Server);
        using var server = new ServerHost(new ServerConfig(), authoritative, wire);
        using var client = new ClientSession(link.Client, local, "Admission");
        long tick = 0;
        void Step() { ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick++); }
        for (var i = 0; i < 1200 && client.AcquireSnapshot().HullCount == 0; i++) { TestWorkers.Settle(client, "the client"); Step(); }
        var snapshot = client.AcquireSnapshot();
        Assert.Equal(SessionState.Active, snapshot.State);
        Assert.True(wire.Designs > 1);
        var design = snapshot.Content.Pod;
        Assert.Equal(DesignBytes.Encode(authoritative.Pod), DesignBytes.Encode(design));
        Assert.Equal(authoritative.Pod.Stats, design.Stats);
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var own = (int)(snapshot.Hulls[snapshot.OwnHull].Id & 65535);
        var start = hulls.State(own).Position;
        // Drive the real server field with flight input, then ram its last box with a normal catalogue hull.
        var field = new HullField(hulls, authoritative.Designs);
        field.BeginRound();
        field.Step(own, new FlightInput { Forward = 127 });
        Assert.NotEqual(start, hulls.State(own).Position);
        hulls.State(own).Velocity = Vector3.Zero;
        hulls.State(own).Orientation = Quaternion.Identity;
        var end = design.BlockOffsets[299];
        var position = hulls.State(own).Position + new Double3(end.X + 3, end.Y, end.Z);
        var rammer = hulls.Create(999, (byte)authoritative.DroneDesign, authoritative.Designs[authoritative.DroneDesign], position);
        hulls.State(rammer.Index).Velocity = new Vector3(-200, 0, 0);
        for (var i = 0; i < 20 && BlockSet.Has(hulls.Blocks(own), 299); i++)
        {
            field.BeginRound(); field.Step(rammer.Index, default); field.Step(own, default);
        }
        Assert.False(BlockSet.Has(hulls.Blocks(own), 299));
        wire.Gap = true;
        for (var i = 0; i < 100; i++) Step();
        snapshot = client.AcquireSnapshot();
        var alive = snapshot.Hulls[snapshot.OwnHull].Blocks.ToArray();
        Assert.False(BlockSet.Has(alive, 299));
        var visuals = new HullVisuals();
        visuals.Update(snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.Content.Designs, 1, 0, false, FtlKind.None);
        Assert.False(visuals.Hulls[snapshot.OwnHull].Intact);
        Assert.False(BlockSet.Has(visuals.Hulls[snapshot.OwnHull].PickBlocks.Span, 299));
        foreach (var b in BlockSet.Each(alive))
        {
            var transform = WorldView.MeshTransform(design.BlockOffsets[b], Quaternion.Identity,
                design.BlockHalfExtents[b] * 2, design.Color, 0, 1);
            Assert.Equal(design.BlockHalfExtents[b].X * 2, transform.Row0.X);
            Assert.Equal(design.BlockOffsets[b].X, transform.Row0.W);
        }
        design.Evaluate(alive, out var flight, out var stats);
        Assert.Equal(hulls.Flight(own), flight);
        Assert.True(stats.Blocks < 300);
        var mesh = MeshBuilder.Hull(design);
        Assert.Equal(300 * 24, mesh.Vertices.Count);
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - (design.BlockOffsets[299].X + 1)) < 0.001f);
        var shell = new ShieldSurface();
        var output = new ShellVertex[GeometryAdmission.MaxVertices];
        var indices = new ushort[GeometryAdmission.MaxIndices];
        Assert.True(shell.Build(design, alive, design.ShellShape, output, indices, out _) is -1 or > 0);
        var ray = Vector3.UnitZ;
        var eye = design.BlockOffsets[298] - ray * 20;
        var pick = new PickHull(1, default, Quaternion.Identity, design.BlockOffsets, design.BlockSize,
            alive, design.Flight.Radius, HalfExtents: design.BlockHalfExtents);
        Assert.Equal(1, TargetPicking.Pick(ray, new(eye.X, eye.Y, eye.Z), ray, 1000, [pick], [], new PickHit[1]));
        for (var i = 0; i < 3; i++) shell.Build(design, alive, design.ShellShape, output, indices, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        shell.Build(design, alive, design.ShellShape, output, indices, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Null(client.Fault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealJoinUsesServerOrderAndDownloadedDamageGeometry(bool missing)
    {
        var local = GameContent.Load();
        using var copy = new ContentCopy();
        copy.Replace("ships/fleet.json", "\"mining_vessel\", \"survey_vessel\", \"drone\"", "\"survey_vessel\", \"mining_vessel\", \"drone\"");
        if (missing) copy.Replace("ships/command_pod.json", "{ \"at\": [-2, 0, 1], \"type\": \"cargo\" }", "{ \"at\": [-2, 0, 1], \"type\": \"hull\" }, { \"at\": [10, 10, 10], \"type\": \"hull\" }");
        var authoritative = copy.Load();
        Assert.Equal(local.Identity, authoritative.Identity);
        using var link = new LoopbackLink();
        var wire = new Wire(link.Server) { Refuse = true };
        using var server = new ServerHost(new ServerConfig(), authoritative, wire);
        using var client = new ClientSession(link.Client, local, "Designs");
        long tick = 0;
        void Step() { ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick++); }
        for (var i = 0; i < 10; i++) Step();
        Assert.Equal(SessionState.Downloading, client.AcquireSnapshot().State);
        wire.Refuse = false;
        // A decode that is running finishes before the next interval, so 500 of them are enough on any machine.
        for (var i = 0; i < 500 && client.AcquireSnapshot().HullCount == 0; i++) { TestWorkers.Settle(client, "the client"); Step(); }
        var snapshot = client.AcquireSnapshot();
        Assert.Equal(SessionState.Active, snapshot.State);
        Assert.NotSame(local, snapshot.Content);
        Assert.Equal(authoritative.StartingFleet, snapshot.Content.StartingFleet);
        for (var i = 0; i < authoritative.Designs.Length; i++)
        {
            Assert.Equal(DesignBytes.Encode(authoritative.Designs[i]), DesignBytes.Encode(snapshot.Content.Designs[i]));
            Assert.Equal(authoritative.Designs[i].Stats, snapshot.Content.Designs[i].Stats);
        }
        Assert.Equal(missing, wire.Designs > 0);
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var own = snapshot.Hulls[snapshot.OwnHull].Id;
        var index = (int)(own & 65535);
        // Suppress the loss journal to force recovery against the received design.
        wire.Gap = true;
        var lost = authoritative.Pod.BlockCount - 1;
        hulls.BlockStates.Lose(hulls.Info(index).BlockState, lost);
        for (var i = 0; i < 80; i++) Step();
        snapshot = client.AcquireSnapshot();
        Assert.Equal(1u, snapshot.Hulls[snapshot.OwnHull].Revision);
        Assert.False(BlockSet.Has(snapshot.Hulls[snapshot.OwnHull].Blocks.Span, lost));
        Assert.NotSame(local.Designs, snapshot.Content.Designs);
        Assert.Null(client.Fault);
    }

    [Fact]
    public void MalformedCatalogueAndOversizedChunksFailWithoutPublication()
    {
        var content = GameContent.Load();
        var cache = new DesignCatalogue(content);
        foreach (var oversized in new[] { false, true })
        {
            using var download = new DesignDownload(content, cache);
            var bytes = new byte[64]; var w = new NetWriter(bytes);
            w.WriteByte(255); w.WriteInt32(oversized ? int.MaxValue : 4); w.WriteInt32(0); w.WriteUInt16(4);
            w.WriteBytes(new byte[] { 1, 2, 0, 0 });
            var r = new NetReader(w.Written); download.Receive(ref r);
            Assert.True(download.Failed); Assert.Null(download.Result);
        }
    }

    [Fact]
    public void BadCanonicalHashNeverPublishesAndSlotsShareOneBudget()
    {
        var content = GameContent.Load();
        var cache = new DesignCatalogue(content);
        using var download = new DesignDownload(content, cache);
        var manifest = (byte[])cache.Manifest.Clone();
        var entry = 4 + content.StartingFleet.Length;
        manifest[entry + 4] ^= 1;
        void Receive(byte id, byte[] data)
        {
            var packet = new byte[data.Length + 11]; var writer = new NetWriter(packet);
            writer.WriteByte(id); writer.WriteInt32(data.Length); writer.WriteInt32(0);
            writer.WriteUInt16((ushort)data.Length); writer.WriteBytes(data);
            var reader = new NetReader(writer.Written); download.Receive(ref reader);
        }
        Receive(255, manifest);
        Receive(0, cache.Bytes[0]);
        using var link = new LoopbackLink();
        var buffer = new byte[4096];
        for (var i = 0; i < 100 && !download.Failed; i++) { TestWorkers.Settle(download, "the download"); download.Tick(link.Client, buffer, i); }
        Assert.True(download.Failed); Assert.Null(download.Result);

        var sender = new BlockSender(content.BlockTransfers);
        Assert.True(sender.QueueDesign(0, cache.Bytes[0], 0));
        Assert.False(sender.QueueDesign(1, cache.Bytes[1], 0));
        Assert.True(sender.QueueDesign(1, cache.Bytes[1], content.BlockTransfers.RequestTicks));
        Assert.False(sender.QueueDesign(2, cache.Bytes[2], content.BlockTransfers.RequestTicks * 2));
        Assert.False(sender.QueueDesign(0, cache.Bytes[0], content.BlockTransfers.RequestTicks * 3));
        var first = new BlockTransfer(32768) { Hull = uint.MaxValue, Design = 0, Data = new byte[4096] };
        var second = new BlockTransfer(32768) { Hull = 1, Count = 200 };
        var budget = 1024;
        first.Send(link.Server, 0, buffer, ref budget);
        second.Send(link.Server, 0, buffer, ref budget);
        Assert.Equal(0, budget); Assert.Equal(0, second.Offset);
    }

    [Fact]
    public void ConflictingHashMetadataFailsAndDisconnectJoinsAnInFlightDecode()
    {
        var content = GameContent.Load(); var cache = new DesignCatalogue(content);
        var manifest = (byte[])cache.Manifest.Clone(); var first = 4 + content.StartingFleet.Length;
        cache.Hashes[0].CopyTo(manifest, first + 36 + 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(manifest.AsSpan(first + 36), cache.Bytes[0].Length + 1);
        static void Feed(DesignDownload download, byte id, byte[] data)
        {
            var packet = new byte[11 + data.Length]; var writer = new NetWriter(packet);
            writer.WriteByte(id); writer.WriteInt32(data.Length); writer.WriteInt32(0);
            writer.WriteUInt16((ushort)data.Length); writer.WriteBytes(data);
            var reader = new NetReader(writer.Written); download.Receive(ref reader);
        }
        using (var invalid = new DesignDownload(content, cache))
        {
            Feed(invalid, 255, manifest);
            Assert.True(invalid.Failed); Assert.Null(invalid.Result);
        }
        manifest = (byte[])cache.Manifest.Clone(); manifest[first + 4] ^= 1;
        var pending = new DesignDownload(content, cache);
        Feed(pending, 255, manifest); Feed(pending, 0, cache.Bytes[0]);
        pending.Dispose();
        Assert.Null(typeof(DesignDownload).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pending));
        Assert.Null(pending.Result);
    }

    [Fact]
    public void ManifestAggregateBudgetIncludesCachedAndRepeatedEntries()
    {
        var content = GameContent.Load();
        var cache = new DesignCatalogue(content);
        using var download = new DesignDownload(content, cache);
        var manifest = new byte[4 + 255 * 36];
        var writer = new NetWriter(manifest);
        writer.WriteByte(255); writer.WriteByte(0); writer.WriteByte(0); writer.WriteByte(0);
        for (var i = 0; i < 255; i++)
        {
            writer.WriteInt32(cache.Bytes[1].Length); writer.WriteBytes(cache.Hashes[1]);
        }
        var packet = new byte[manifest.Length + 11];
        writer = new NetWriter(packet);
        writer.WriteByte(255); writer.WriteInt32(manifest.Length); writer.WriteInt32(0);
        writer.WriteUInt16((ushort)manifest.Length); writer.WriteBytes(manifest);
        var reader = new NetReader(writer.Written); download.Receive(ref reader);
        Assert.True(download.Failed);
        Assert.Null(download.Result);
        Assert.Null(typeof(DesignDownload).GetField("_designs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(download));
        using var files = new ContentCopy();
        files.Write("ships/fleet.json", JsonSerializer.Serialize(new
        {
            designs = Enumerable.Repeat("mining_vessel", 255).ToArray(),
            pod = "mining_vessel",
            drone = "mining_vessel",
            startingFleet = Array.Empty<string>()
        }));
        Assert.Throws<InvalidDataException>(() => files.Load());
    }

    [Fact]
    public void ManyCacheMissesCanFinishAfterOldSixHundredTickDeadline()
    {
        var local = GameContent.Load();
        using var files = new ContentCopy();
        var original = files.Read("ships/command_pod.json");
        var names = Enumerable.Range(0, 30).Select(i => "admission" + i).ToArray();
        for (var i = 0; i < names.Length; i++)
            files.Write("ships/" + names[i] + ".json", original.Replace("Command Pod", names[i]));
        files.Write("ships/fleet.json", JsonSerializer.Serialize(new { designs = names, pod = names[0], drone = names[1], startingFleet = Array.Empty<string>() }));
        var serverContent = files.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig(), serverContent, link.Server);
        using var client = new ClientSession(link.Client, local, "Many designs");
        var tick = 0;
        for (; tick < 2000 && client.AcquireSnapshot().HullCount == 0; tick++)
        {
            TestWorkers.Settle(client, "the client");
            ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick);
        }
        Assert.True(tick > 600);
        Assert.Equal(SessionState.Active, client.AcquireSnapshot().State);
        Assert.Equal(30, client.AcquireSnapshot().Content.Designs.Length);
        var slow = local.BlockTransfers with { BytesPerTick = 64 };
        Assert.True(slow.TransferTicks > slow.Transfers * (long)slow.MaxBytes / (64 - BlockTransfer.HeaderBytes));
    }

    [Fact]
    public void RefusedCatalogueExpiresAndLeavesNoClientWorker()
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig(), content, new Wire(link.Server) { Refuse = true });
        using var client = new ClientSession(link.Client, content, "Timeout");
        for (var tick = 0; tick < content.BlockTransfers.AdmissionTicks + 10; tick++)
        { ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick); }
        Assert.Equal(SessionState.Ended, client.AcquireSnapshot().State);
        Assert.Null(client.Fault);
    }
}
