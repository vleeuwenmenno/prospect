using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class SocialFollowupTests
{
    // This wrapper uses the server's real network admission and receive budgets, without UDP scheduling noise.
    private sealed class NetworkLink(ITransport inner) : ITransport, ITransportHandler
    {
        private ITransportHandler _handler = null!;
        public string Description => "budgeted social test";
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => false;
        public int SocialSent, SocialBytes, ReliableSent, ReliableBytes, FleetBytes, Controls;
        public int ReceivedReliableBytes, ReceivedReliableMessages;
        public readonly char[] Characters = new char[128];
        public int CharacterCount;
        public bool FailSocial, Closed;
        public PlayerInputBridge? Producer;
        public int ProduceRemaining;
        public string ProduceText = "";
        public void ResetSent() => SocialSent = SocialBytes = ReliableSent = ReliableBytes = FleetBytes = Controls = 0;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget)
        {
            _handler = handler;
            ReceivedReliableBytes = ReceivedReliableMessages = 0;
            return inner.Poll(this, budget);
        }
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (channel == NetChannel.Reliable) { ReliableSent++; ReliableBytes += payload.Length; }
            if (payload[0] == (byte)MessageId.Social)
            {
                SocialSent++; SocialBytes += payload.Length;
                var reader = new NetReader(payload[1..]);
                if (SocialMessage.TryRead(ref reader, out var message) && message.Kind == SocialKind.Chat && CharacterCount < Characters.Length)
                    Characters[CharacterCount++] = message.Text[0];
                if (ProduceRemaining > 0)
                {
                    ProduceRemaining--;
                    Producer!.SendChat(ChatChannel.Everyone, ProduceText);
                }
                if (FailSocial) return false;
            }
            if (payload[0] == (byte)MessageId.FleetOrders) FleetBytes += payload.Length;
            if (payload[0] == (byte)MessageId.Control) Controls++;
            return inner.Send(peer, channel, payload);
        }
        public void Disconnect(int peer) { Closed = true; inner.Disconnect(peer); }
        public int GetRoundTripMs(int peer) => 0;
        public ulong GetPeerOrigin(int peer) => 1;
        public void Dispose() => inner.Dispose();
        public void OnConnected(ITransport transport, int peer) => _handler.OnConnected(this, peer);
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => _handler.OnDisconnected(this, peer, reason);
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (channel == NetChannel.Reliable) { ReceivedReliableBytes += payload.Length; ReceivedReliableMessages++; }
            _handler.OnMessage(this, peer, channel, payload);
        }
    }

    private sealed class Session : IDisposable
    {
        public readonly LoopbackLink Link = new();
        public readonly NetworkLink ServerNet, ClientNet;
        public readonly ServerHost Server;
        public readonly ClientSession Client;
        private long _serverTick, _clientTick;
        public Session()
        {
            var content = GameContent.Load();
            ServerNet = new NetworkLink(Link.Server); ClientNet = new NetworkLink(Link.Client);
            Server = new ServerHost(new ServerConfig(), content, ServerNet);
            Client = new ClientSession(ClientNet, content, "Pilot");
            for (var i = 0; i < 10; i++) { TickServer(); TickClient(); }
            Assert.Equal(SessionState.Active, Client.AcquireSnapshot().State);
            ClientNet.ResetSent();
        }
        public void TickServer() => ((ITickHandler)Server).Tick(_serverTick++);
        public void TickClient() => ((ITickHandler)Client).Tick(_clientTick++);
        public HullStore Hulls => (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server)!;
        public void Dispose() { Client.Dispose(); Server.Dispose(); Link.Dispose(); }
    }

    [Fact]
    public void ElevenMaximumLinesLeaveRoomForControlAndFullFleetAndKeepFifoAcrossCatchup()
    {
        using var s = new Session();
        var view = s.Client.AcquireSnapshot();
        var pod = view.Hulls[view.OwnHull].Id;
        for (var i = 0; i < 11; i++) Assert.True(s.Client.Input.SendChat(ChatChannel.Everyone, new string((char)('界' + i), 256)));
        var order = new HullOrder { Kind = OrderKind.MoveTo, Target = new Double3(10000, 0, 0) };
        s.Client.Input.RequestControl(0);
        s.Client.Input.IssueFleetOrder(pod, order);
        for (uint i = 1; i < FleetOrdersMessage.MaxOrders; i++) s.Client.Input.IssueFleetOrder(0xffff0000 + i, order);
        s.TickServer();
        var before = GC.GetAllocatedBytesForCurrentThread();
        s.TickClient();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(7, s.ClientNet.SocialSent);
        Assert.Equal(7 * SocialMessage.MaxWireSize, s.ClientNet.SocialBytes);
        Assert.Equal(FleetOrdersMessage.MaxWireSize, s.ClientNet.FleetBytes);
        Assert.Equal(1, s.ClientNet.Controls);
        Assert.InRange(s.ClientNet.ReliableBytes, 1, ServerHost.PlayerBytesPerTick);
        Assert.InRange(s.ClientNet.ReliableSent, 1, ServerHost.PlayerMessagesPerTick);

        // A producer can add controls during catch-up, but no newer server snapshot means no fresh allowance.
        s.Client.Input.RequestControl(pod);
        for (var i = 0; i < 6; i++) s.TickClient();
        Assert.Equal(7, s.ClientNet.SocialSent);
        Assert.Equal(1, s.ClientNet.Controls);
        s.TickServer();
        Assert.InRange(s.ServerNet.ReceivedReliableBytes, 1, ServerHost.PlayerBytesPerTick);
        Assert.InRange(s.ServerNet.ReceivedReliableMessages, 1, ServerHost.PlayerMessagesPerTick);
        var index = (int)(pod & 0xffff);
        Assert.False(s.Hulls.Info(index).Piloted);
        Assert.Equal(OrderKind.MoveTo, s.Hulls.Info(index).Order.Kind);
        Assert.Equal(1, s.Server.Status.PlayerCount);
        s.ClientNet.ResetSent();
        s.TickClient();
        Assert.Equal(4, s.ClientNet.SocialSent);
        Assert.Equal(4 * SocialMessage.MaxWireSize, s.ClientNet.SocialBytes);
        Assert.Equal(1, s.ClientNet.Controls);
        s.TickServer(); s.TickClient();
        Assert.True(s.Hulls.Info(index).Piloted);
        Assert.Equal(SessionState.Active, s.Client.AcquireSnapshot().State);
        Assert.Equal(4, s.Client.AcquireSnapshot().Social.ChatCount);
        Assert.Equal(11, s.ClientNet.CharacterCount);
        for (var i = 0; i < 11; i++) Assert.Equal((char)('界' + i), s.ClientNet.Characters[i]);
    }

    [Fact]
    public void ProducerRefillingDuringSendCannotExtendTheTickDrain()
    {
        using var s = new Session();
        s.ClientNet.Producer = s.Client.Input;
        s.ClientNet.ProduceText = new string('界', 256);
        s.ClientNet.ProduceRemaining = 100;
        s.Client.Input.SendChat(ChatChannel.Everyone, s.ClientNet.ProduceText);
        s.TickServer();
        var before = GC.GetAllocatedBytesForCurrentThread();
        s.TickClient();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(7, s.ClientNet.SocialSent);
        Assert.Equal(93, s.ClientNet.ProduceRemaining);
        s.ClientNet.ProduceRemaining = 0;
        s.TickServer(); s.ClientNet.ResetSent(); s.TickClient();
        Assert.Equal(1, s.ClientNet.SocialSent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSocialSendEndsTheClientOrServerConnection(bool serverFails)
    {
        using var s = new Session();
        if (serverFails) s.ServerNet.FailSocial = true;
        else s.ClientNet.FailSocial = true;
        s.Client.Input.SendChat(ChatChannel.Everyone, "failed delivery");
        s.TickServer(); s.TickClient();
        for (var i = 0; i < 3; i++) { s.TickServer(); s.TickClient(); }
        Assert.Equal(SessionState.Ended, s.Client.AcquireSnapshot().State);
        Assert.Equal(0, s.Server.Status.PlayerCount);
        Assert.Equal(0, s.Client.AcquireSnapshot().Social.PartyCount);
        Assert.True(serverFails ? s.ServerNet.Closed : s.ClientNet.Closed);
    }
}
