using System.Reflection;
using Brutal.RakNetApi;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class FloodRegressionTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void ReliableCommandsSurviveInputBurstInOrderAndMalformedControlDoesNothing()
    {
        using var rig = new ScriptedServer(Content);
        var player = rig.Join(1);
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Server)!;
        var pod = hulls.HandleAt(0);
        Assert.True(hulls.Info(pod.Index).Piloted);
        rig.Net.Deliver(player, [(byte)MessageId.Control]);
        rig.Tick();
        Assert.True(hulls.Info(pod.Index).Piloted);
        for (uint i = 1; i <= 32; i++)
            rig.Input(player, i);
        rig.Net.Deliver(player, [(byte)MessageId.Control, 0, 0, 0, 0]);
        var bytes = new byte[128];
        var writer = new NetWriter(bytes);
        FleetOrdersMessage.Write(ref writer, [new FleetOrder { Hull = pod.Packed, Order = new HullOrder { Kind = OrderKind.MoveTo, Target = new Double3(10000, 0, 0) } }]);
        rig.Net.Deliver(player, bytes, writer.Length);
        rig.Tick();
        Assert.True(rig.Net[player].Open);
        Assert.False(hulls.Info(pod.Index).Piloted);
        Assert.Equal(OrderKind.MoveTo, hulls.Info(pod.Index).Order.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReliableOverflowDisconnectsWithoutBlockingOthersAndSlotReuseResetsAllowance(bool bytes)
    {
        using var rig = new ScriptedServer(Content);
        var flooder = rig.Join(1);
        var honest = rig.Join(2);
        var ping = new byte[bytes ? ServerHost.PlayerBytesPerTick + 1 : 9];
        ping[0] = (byte)MessageId.Ping;
        for (var i = 0; i < (bytes ? 1 : ServerHost.PlayerMessagesPerTick + 1); i++)
            rig.Net.Deliver(flooder, ping);
        rig.Ping(honest);
        rig.Tick();
        Assert.True(rig.Net[flooder].ClosedByServer);
        Assert.Equal(1, rig.Net[honest].Pongs);
        var replacement = rig.Net.Connect(3);
        rig.Hello(replacement);
        // The fixture owns the catalogue; admit it before gameplay without adding a budget-reset tick.
        rig.Net.Deliver(replacement, [(byte)MessageId.CatalogueReady]);
        rig.Net.Deliver(replacement, new byte[] { (byte)MessageId.Ping, 0, 0, 0, 0, 0, 0, 0, 0 });
        rig.Tick();
        Assert.Equal(1, rig.Net[replacement].Pongs);
        Assert.True(rig.Net[replacement].Open);
        for (var tick = 0; tick < 2; tick++)
        {
            for (var i = 0; i < ServerHost.PlayerMessagesPerTick; i++)
                rig.Net.Deliver(replacement, new byte[] { (byte)MessageId.Ping, 0, 0, 0, 0, 0, 0, 0, 0 });
            rig.Tick();
            Assert.Equal(1 + (tick + 1) * ServerHost.PlayerMessagesPerTick, rig.Net[replacement].Pongs);
            Assert.True(rig.Net[replacement].Open);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(119)]
    public void RetryDelayCountAndExhaustionAreDeterministic(int jitter)
    {
        var retry = new ConnectRetry();
        long now = 1000;
        for (var i = 1; i <= 8; i++)
        {
            Assert.True(retry.Schedule(now, 1000, jitter));
            Assert.Equal(i, retry.Count);
            var due = now + 120 + 40 * i + jitter;
            Assert.False(retry.TakeDue(due - 1));
            Assert.True(retry.TakeDue(due));
            Assert.False(retry.TakeDue(due));
            now = due;
        }
        Assert.False(retry.Schedule(now, 1000, jitter));
        Assert.False(retry.TakeDue(long.MaxValue));
        retry = new ConnectRetry();
        Assert.True(retry.Schedule(now, 1000, jitter));
        retry.Cancel();
        Assert.False(retry.TakeDue(long.MaxValue));
    }

    [Fact]
    public void NativeCooldownDispatchSchedulesEightRetriesThenDisconnects()
    {
        using var transport = RakNetTransport.Connect("127.0.0.1", TestNet.Port(27));
        var events = new ConnectionEvents();
        for (var i = 1; i <= 8; i++)
        {
            Dispatch(transport, events, DefaultMessageIDTypes.IpRecentlyConnected);
            var retry = RetryOf(transport);
            Assert.Equal(i, retry.Count);
            Assert.True(retry.TakeDue(long.MaxValue));
            Assert.Equal(0, events.Disconnected);
        }
        Dispatch(transport, events, DefaultMessageIDTypes.IpRecentlyConnected);
        Assert.Equal(1, events.Disconnected);
        Assert.Equal(DisconnectReason.ConnectFailed, events.Reason);
        Assert.False(RetryOf(transport).TakeDue(long.MaxValue));
    }

    [Theory]
    [InlineData(DefaultMessageIDTypes.ConnectionRequestAccepted)]
    [InlineData(DefaultMessageIDTypes.NoFreeIncomingConnections)]
    [InlineData(DefaultMessageIDTypes.ConnectionAttemptFailed)]
    [InlineData(DefaultMessageIDTypes.ConnectionBanned)]
    [InlineData(DefaultMessageIDTypes.InvalidPassword)]
    [InlineData(DefaultMessageIDTypes.IncompatibleProtocolVersion)]
    public void NativeAdmissionOrFailureCancelsPendingRetry(DefaultMessageIDTypes message)
    {
        using var transport = RakNetTransport.Connect("127.0.0.1", TestNet.Port(27));
        var events = new ConnectionEvents();
        Dispatch(transport, events, DefaultMessageIDTypes.IpRecentlyConnected);
        Assert.True(RetryOf(transport).TakeDue(long.MaxValue));
        Dispatch(transport, events, message);
        Assert.False(RetryOf(transport).TakeDue(long.MaxValue));
        var accepted = message == DefaultMessageIDTypes.ConnectionRequestAccepted;
        Assert.Equal(accepted ? 1 : 0, events.Connected);
        Assert.Equal(accepted ? 0 : 1, events.Disconnected);
        if (!accepted)
            Assert.Equal(message == DefaultMessageIDTypes.NoFreeIncomingConnections ? DisconnectReason.ServerFull : DisconnectReason.ConnectFailed, events.Reason);
    }

    private static ConnectRetry RetryOf(RakNetTransport transport) =>
        (ConnectRetry)typeof(RakNetTransport).GetField("_retry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;

    private unsafe delegate void PacketHandler(ITransportHandler handler, Packet* packet);

    private static unsafe void Dispatch(RakNetTransport transport, ITransportHandler handler, DefaultMessageIDTypes message)
    {
        // Exercise the production packet switch without relying on the OS to trigger RakNet's cooldown.
        var dispatch = typeof(RakNetTransport).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<PacketHandler>(transport);
        var id = (byte)message;
        var packet = new Packet { Data = &id, Length = 1 };
        dispatch(handler, &packet);
    }

    private sealed class ConnectionEvents : ITransportHandler
    {
        public int Connected;
        public int Disconnected;
        public DisconnectReason Reason;
        public void OnConnected(ITransport transport, int peer) => Connected++;
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason)
        {
            Disconnected++;
            Reason = reason;
        }
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) { }
    }

    private sealed class FailingTransport : ITransport
    {
        public bool ThrowOnInspect;
        public bool ThrowOnDispose;
        public bool Disposed;
        public string Description => "failure test";
        public int MaxPayloadSize => 1024;
        public bool IsInProcess => ThrowOnInspect ? throw new IOException("inspect") : false;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget) => true;
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload) => true;
        public void Disconnect(int peer) { }
        public int GetRoundTripMs(int peer) => 0;
        public ulong GetPeerOrigin(int peer) => 0;
        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new IOException("dispose");
        }
    }

    [Fact]
    public void ConstructorFailureReleasesListenerButNotCallerTransport()
    {
        var transport = new FailingTransport { ThrowOnInspect = true };
        var port = TestNet.Port(26);
        var config = new ServerConfig { Visibility = ServerVisibility.Unlisted, Port = port, Password = "secret" };
        Assert.Throws<IOException>(() => new ServerHost(config, Content, transport));
        Assert.False(transport.Disposed);
        using var listener = RakNetTransport.Listen(port, 2);
        Assert.Throws<IOException>(() => new ServerHost(config, Content, transport));
        Assert.False(transport.Disposed);
    }

    [Fact]
    public void ServerClearsKeyAndDisposesOtherTransportsWhenOneThrows()
    {
        var first = new FailingTransport { ThrowOnDispose = true };
        var second = new FailingTransport();
        var server = new ServerHost(new ServerConfig { Password = "secret" }, Content, first, second);
        var key = (byte[])typeof(ServerHost).GetField("_passwordKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        Assert.Contains(key, b => b != 0);
        Assert.Throws<IOException>(server.Dispose);
        Assert.All(key, b => Assert.Equal(0, b));
        Assert.True(second.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DirectClientDisposalReleasesPassword(bool start, bool throws)
    {
        var transport = new FailingTransport { ThrowOnDispose = throws };
        var client = new ClientSession(transport, Content, "Player", "secret");
        if (start) client.Start();
        if (throws) Assert.Throws<IOException>(client.Dispose);
        else client.Dispose();
        Assert.Null(typeof(ClientSession).GetField("_password", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
    }
}
