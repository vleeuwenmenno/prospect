using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Bounded manual replay of the collision prediction scheduling discrepancy.</summary>
public sealed class PredictionTimelineTests
{
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;
    private static string State(HullStore hulls, int i) => $"{i}: p={hulls.State(i).Position} v={hulls.State(i).Velocity} shield={hulls.State(i).Shield} hull={hulls.State(i).Hull} order={hulls.Info(i).Order.Kind} target={hulls.Info(i).Order.Target} shieldAge={hulls.State(i).ShieldHit.AgeTicks} revision={hulls.Revision(i)} contact={hulls.Info(i).ContactTick}";

    private sealed class Wire(ITransport inner) : ITransport, ITransportHandler
    {
        internal Action? Before, After;
        internal SnapshotHeader Header;
        internal string Records = "";
        private ITransportHandler? _handler;
        public string Description => inner.Description;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => inner.IsInProcess;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget) { _handler = handler; return inner.Poll(this, budget); }
        public void OnConnected(ITransport transport, int peer) => _handler!.OnConnected(this, peer);
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => _handler!.OnDisconnected(this, peer, reason);
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            var snapshot = payload.Length > 0 && payload[0] == (byte)MessageId.Snapshot;
            if (snapshot)
            {
                var reader = new NetReader(payload[1..]);
                SnapshotHeader.TryRead(ref reader, out Header);
                Records = "";
                for (var i = 0; i < Header.HullCount; i++)
                {
                    HullRecord.TryRead(ref reader, out var record);
                    Records += $" [{record.Id} p={record.State.Position} v={record.State.Velocity} shield={record.State.Shield}]";
                }
                Before?.Invoke();
            }
            _handler!.OnMessage(this, peer, channel, payload);
            if (snapshot) After?.Invoke();
        }
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload) => inner.Send(peer, channel, payload);
        public void Disconnect(int peer) => inner.Disconnect(peer);
        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);
        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);
        public void Dispose() { }
    }

    private sealed class DiagnosticRing
    {
        private readonly string[] _lines = new string[64];
        private int _next;
        public bool Frozen;
        public void WriteLine(string line) { if (!Frozen) _lines[_next++ % _lines.Length] = line; }
        public override string ToString() => string.Join("\n", Enumerable.Range(Math.Max(0, _next - 64), Math.Min(64, _next)).Select(i => _lines[i % 64]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Replay(bool batch, bool oldBalance)
    {
        using var files = new ContentCopy();
        if (oldBalance)
        {
            files.Write("blocks/blocks.json", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "prediction-old-blocks.json")));
            files.Replace("ships/command_pod.json", "\"sensor\"", "\"hull\"");
            files.Replace("ships/command_pod.json", "\"charting\"", "\"hull\"");
            files.Replace("ships/mining_vessel.json", "\"sensor\"", "\"hull\"");
            files.Replace("ships/survey_vessel.json", "\"sensor\"", "\"hull\"");
        }
        var content = files.Load();
        var settings = content.SettingCatalog.Build(null, ["collisionDamage=2", "collisionShields=Weak", "shieldRecharge=0.25", "hullStrength=2"]);
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1, Settings = settings }, content, link.Server);
        var wire = new Wire(link.Client);
        var clock = new ManualTime();
        using var client = new ClientSession(wire, content, "Pilot", clock: clock);
        var hulls = Field<HullStore>(server, "_hulls");
        var world = Field<ClientWorld>(client, "_world");
        long serverTick = 0, clientTick = 0;
        for (var n = 0; n < 2400 && client.AcquireSnapshot().HullCount < 2; n++)
        {
            clock.Advance(ManualTime.Tick); ((ITickHandler)server).Tick(serverTick++);
            ((ITickHandler)client).Tick(clientTick++);
            Thread.Sleep(1);
        }
        Assert.True(client.AcquireSnapshot().HullCount >= 2);
        // Normalize to one input per server tick before the measured schedule begins.
        for (var n = 0; n < 12; n++) { clock.Advance(ManualTime.Tick); ((ITickHandler)server).Tick(serverTick++); ((ITickHandler)client).Tick(clientTick++); }
        var trace = new DiagnosticRing();
        var connection = Field<Array>(server, "_connections").GetValue(0)!;
        var snap = client.AcquireSnapshot();
        var pod = snap.Hulls[snap.OwnHull];
        var parked = snap.Hulls.Take(snap.HullCount).First(h => !h.IsOwn);
        var direction = Vector3.Normalize(parked.Position.RelativeTo(pod.Position));
        client.Input.Submit(new PlayerControls { Strafe = direction.X, Forward = -direction.Z });
        var before = default(HullState);
        float worst = 0;
        var predictedLoss = false;
        var confirmedLoss = false;
        var firstFailure = -1L;
        wire.Before = () =>
        {
            before = Field<HullState>(world, "_predicted");
            var next = Field<uint>(world, "_nextSequence");
            trace.WriteLine($"SNAP tick={wire.Header.ServerTick} epoch={wire.Header.ControlEpoch} ack={wire.Header.AckedInput} horizon={Field<long>(world, "_predictionTick")} pending={next - wire.Header.AckedInput - 1} records={wire.Records} predictedBefore={before.Position} v={before.Velocity}");
        };
        wire.After = () =>
        {
            var after = Field<HullState>(world, "_predicted");
            var error = before.Position.RelativeTo(after.Position).Length();
            worst = Math.Max(worst, error);
            if (error >= .15f && firstFailure < 0) firstFailure = wire.Header.ServerTick;
            trace.WriteLine($"CORRECTION error={error:R} predictedAfter={after.Position} v={after.Velocity}");
            if (firstFailure >= 0) trace.Frozen = true;
        };
        for (var n = 0; n < 480; n++)
        {
            confirmedLoss |= hulls.Revision(0) > 0;
            var ack = Field<uint>(connection, "LastSimulatedSequence");
            trace.WriteLine($"SERVER tick={serverTick} before ack={ack} {State(hulls, 0)} {State(hulls, 1)}");
            clock.Advance(ManualTime.Tick); ((ITickHandler)server).Tick(serverTick++);
            trace.WriteLine($"SERVER after ack={Field<uint>(connection, "LastSimulatedSequence")} consumed={Field<uint>(connection, "LastSimulatedSequence") - ack} {State(hulls, 0)} {State(hulls, 1)}");
            var count = batch ? (n % 2 == 0 ? 2 : 0) : 1;
            for (var c = 0; c < count; c++)
            {
                var near = Field<HullStore>(world, "_near");
                trace.WriteLine($"CLIENT tick={clientTick} before next={Field<uint>(world, "_nextSequence")} {State(near, 0)} {(near.Slots > 1 ? State(near, 1) : "no near hull")}");
                ((ITickHandler)client).Tick(clientTick++);
                predictedLoss |= near.Revision(0) > hulls.Revision(0);
                var authoritativeBlocks = Field<BlockStateHandle>(world, "_ownBlocks");
                // Speculative damage must never mutate the authoritative publication state.
                Assert.True(world.BlockStates.Revision(authoritativeBlocks) <= hulls.Revision(0));
                trace.WriteLine($"CLIENT after next={Field<uint>(world, "_nextSequence")} {State(near, 0)} {(near.Slots > 1 ? State(near, 1) : "no near hull")}");
            }
        }
        trace.WriteLine($"RESULT worst={worst:R} firstFailure={firstFailure} serverTicks={serverTick} clientTicks={clientTick}");
        Assert.True(hulls.State(1).Shield < server.Content.Designs[parked.Design].Flight.ShieldCapacity);
        if (oldBalance) { Assert.True(predictedLoss); Assert.True(confirmedLoss); }
        Assert.True(worst < .15f, $"batch={batch} worst={worst:R} firstFailure={firstFailure}\n{trace}");
    }
}
