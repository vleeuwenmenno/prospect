using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Controlled latency, packet loss and clock drift through real sessions.</summary>
public sealed class InputDelayTests
{
    private sealed class Delayed(ITransport inner) : ITransport, ITransportHandler
    {
        private readonly List<(long At, bool Out, NetChannel Channel, byte[] Bytes)> _queue = [];
        private ITransportHandler _handler = null!;
        internal long Tick;
        internal bool Delay;
        internal int DelayTicks = 3, JitterTicks = 1, DropEvery = 100;
        internal int Inputs, Dropped, PeakQueue;
        internal uint Epoch;
        internal long Newest;
        public string Description => inner.Description;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => inner.IsInProcess;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget)
        {
            _handler = handler;
            inner.Poll(this, budget);
            for (var i = 0; i < _queue.Count;)
            {
                var item = _queue[i];
                if (item.At > Tick) { i++; continue; }
                _queue.RemoveAt(i);
                if (item.Out) inner.Send(0, item.Channel, item.Bytes);
                else handler.OnMessage(this, 0, item.Channel, item.Bytes);
            }
            return true;
        }
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (payload[0] == (byte)MessageId.Input)
            {
                var reader = new NetReader(payload[1..]);
                Span<InputRecord> records = stackalloc InputRecord[4];
                Assert.True(InputMessage.TryRead(ref reader, records, out var count, out var epoch));
                if (Epoch == epoch) Assert.True(records[count - 1].TargetTick > Newest);
                Epoch = epoch;
                Newest = records[count - 1].TargetTick;
                if (++Inputs % DropEvery == 0 && Delay) { Dropped++; return true; }
            }
            if (!Delay || channel != NetChannel.Unreliable) return inner.Send(peer, channel, payload);
            Enqueue(true, channel, payload);
            return true;
        }
        private void Enqueue(bool outgoing, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            _queue.Add((Tick + DelayTicks + Tick % (JitterTicks + 1), outgoing, channel, payload.ToArray()));
            PeakQueue = Math.Max(PeakQueue, _queue.Count);
        }
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (Delay && channel == NetChannel.Unreliable) Enqueue(false, channel, payload);
            else _handler.OnMessage(this, peer, channel, payload);
        }
        public void OnConnected(ITransport transport, int peer) => _handler.OnConnected(this, peer);
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => _handler.OnDisconnected(this, peer, reason);
        public void Disconnect(int peer) => inner.Disconnect(peer);
        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);
        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);
        public void Dispose() { }
    }

    [Fact]
    public void ControlsResumeAfterDelayedSnapshotsOvertakePausedSampling()
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig(), content, link.Server);
        var clock = new ManualTime();
        var wire = new Delayed(link.Client) { DelayTicks = 2, JitterTicks = 0, DropEvery = int.MaxValue };
        using var client = new ClientSession(wire, content, "Pilot", clock: clock);
        long tick = 0;
        void Step(bool sample = true)
        {
            clock.Advance(ManualTime.Tick);
            wire.Tick = tick;
            ((ITickHandler)server).Tick(tick);
            if (sample) ((ITickHandler)client).Tick(tick);
            else wire.Poll(client, ReceiveBudget.Unlimited);
            tick++;
        }
        for (var n = 0; n < 2400 && client.AcquireSnapshot().HullCount == 0; n++)
        {
            Step();
            Thread.Sleep(1);
        }
        Assert.True(client.AcquireSnapshot().HullCount > 0);
        wire.Delay = true;
        for (var n = 0; n < 180; n++) Step();
        Assert.InRange(client.AcquireSnapshot().RoundTripMs, 60, 90);
        for (var n = 0; n < 30; n++) Step(sample: false);
        client.Input.Submit(new PlayerControls { Roll = .5f });
        for (var n = 0; n < 30; n++) Step();
        var fallback = server.FallbackIntervals;
        for (var n = 0; n < 60; n++) Step();
        // RTT smoothing can change lead by one tick; resumed controls must still reach authority.
        Assert.InRange(server.FallbackIntervals - fallback, 0, 3);
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var snapshot = client.AcquireSnapshot();
        var own = (int)(snapshot.Hulls[snapshot.OwnHull].Id & 65535);
        Assert.True(hulls.State(own).AngularVelocity.LengthSquared() > 0.01f);
        Assert.Null(server.Fault);
        Assert.Null(client.Fault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmRealServerClientTicksAllocateNothing(bool damaged)
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig(), content, link.Server);
        var clock = new ManualTime();
        using var client = new ClientSession(link.Client, content, "Pilot", clock: clock);
        long tick = 0;
        for (; tick < 2400 && client.AcquireSnapshot().HullCount == 0; tick++)
        {
            Step();
            Thread.Sleep(1);
        }
        Assert.True(client.AcquireSnapshot().HullCount > 0);
        if (damaged)
        {
            var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            for (var i = 0; i < hulls.Slots; i++)
            {
                var blocks = hulls.Info(i).BlockState;
                for (var b = 0; b < hulls.BlockStates.Blocks(blocks); b++)
                    hulls.BlockStates.SetHitPoints(blocks, b, hulls.BlockStates.HitPoints(blocks)[b] * 0.9f);
                hulls.State(i).Hull = (float)hulls.BlockStates.HitTotal(blocks);
            }
        }
        for (var i = 0; i < 300; i++, tick++) Step();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 600; i++, tick++) Step();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
        Assert.Equal(0, allocated);
        Console.WriteLine($"Warm local server/client damaged={damaged}: {elapsed.TotalMilliseconds / 600:F4} ms per pair, {allocated} managed bytes in 600 pairs.");
        void Step()
        {
            clock.Advance(ManualTime.Tick);
            ((ITickHandler)server).Tick(tick);
            ((ITickHandler)client).Tick(tick);
        }
    }

    [Fact]
    public void HundredMillisecondLinkWithLossJitterAndDriftKeepsTargetsAndStorageBounded()
    {
        var content = GameContent.Load();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig(), content, link.Server);
        var clock = new ManualTime();
        var wire = new Delayed(link.Client);
        using var client = new ClientSession(wire, content, "Pilot", clock: clock);
        long tick = 0, local = 0;
        for (; tick < 2400 && client.AcquireSnapshot().HullCount == 0; tick++)
        {
            clock.Advance(ManualTime.Tick);
            wire.Tick = tick;
            ((ITickHandler)server).Tick(tick);
            ((ITickHandler)client).Tick(local++);
            Thread.Sleep(1);
        }
        Assert.True(client.AcquireSnapshot().HullCount > 0);
        wire.Delay = true;
        var world = (ClientWorld)typeof(ClientSession).GetField("_world", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        for (var elapsed = 0; elapsed < 1200; elapsed++, tick++)
        {
            clock.Advance(ManualTime.Tick);
            wire.Tick = tick;
            ((ITickHandler)server).Tick(tick);
            ((ITickHandler)client).Tick(local++);
            // One percent faster client sampling; no wall-clock dependency.
            if (elapsed % 100 == 99) ((ITickHandler)client).Tick(local++);
            var horizon = (long)typeof(ClientWorld).GetField("_predictionTick", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
            Assert.InRange(horizon - server.Status.Tick, -32, 31);
        }
        Assert.InRange(client.AcquireSnapshot().RoundTripMs, 80, 160);
        Assert.True(wire.Dropped >= 10);
        Assert.InRange(wire.PeakQueue, 1, 64);
        Assert.Null(server.Fault);
        Assert.Null(client.Fault);
    }
}
