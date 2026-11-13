using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class SocialTests
{
    private sealed class SilentPeer : ITransportHandler
    {
        public void OnConnected(ITransport transport, int peer) { }
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) { }
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) { }
    }

    private sealed class Rig : IDisposable
    {
        public readonly GameContent Content = GameContent.Load();
        public readonly LoopbackLink[] Links;
        public readonly ClientSession[] Clients;
        public readonly ServerHost Server;
        private long _tick;
        public Rig(int count = 3)
        {
            Links = new LoopbackLink[count]; Clients = new ClientSession[count];
            var transports = new ITransport[count];
            for (var i = 0; i < count; i++)
            {
                Links[i] = new LoopbackLink(); transports[i] = Links[i].Server;
                Clients[i] = new ClientSession(Links[i].Client, Content, $"Player{i}");
            }
            Server = new ServerHost(new ServerConfig { MaxPlayers = count }, Content, transports);
            Step(4); // CatalogueReady reaches the server before the initial roster is published.
            foreach (var c in Clients) Assert.Equal(SessionState.Active, c.AcquireSnapshot().State);
        }
        public SessionSnapshot View(int i) => Clients[i].AcquireSnapshot();
        public uint Id(int i) => View(i).PlayerId;
        public void Step(int ticks = 65)
        {
            for (var t = 0; t < ticks; t++)
            {
                ((ITickHandler)Server).Tick(_tick);
                foreach (var client in Clients) ((ITickHandler)client).Tick(_tick);
                _tick++;
            }
        }
        public void Send(int i, SocialMessage message)
        {
            Span<byte> bytes = stackalloc byte[SocialMessage.MaxWireSize];
            var writer = new NetWriter(bytes); message.Write(ref writer);
            Links[i].Client.Send(0, NetChannel.Reliable, writer.Written);
        }
        public void Request(int i, SocialKind kind, uint player = 0)
        { Send(i, new SocialMessage { Kind = kind, Player = player, InvitationId = View(i).Social.Invitation.InvitationId }); Step(); }
        public void Party(int a, int b)
        { Request(a, SocialKind.Invite, Id(b)); Request(b, SocialKind.Accept); }
        public void Refusal(int i, SocialKind kind, SocialRefusal reason, uint player = 0)
        { Request(i, kind, player); Assert.Equal(reason, View(i).Social.LastRefusal); }
        public void Dispose()
        {
            foreach (var c in Clients) c.Dispose();
            Server.Dispose();
            foreach (var link in Links) link.Dispose();
        }
    }

    [Fact]
    public void RosterChatIdentityAndPrivateRecipientsWorkAcrossRealSessions()
    {
        using var r = new Rig();
        Assert.Equal(3, r.View(0).Social.RosterCount);
        Assert.Equal(3, r.View(2).Social.RosterCount);
        var message = new SocialMessage { Kind = SocialKind.Chat, Player = r.Id(2) };
        message.SetText(" hello\n\t\u202E world "); r.Send(0, message); r.Step();
        for (var i = 0; i < 3; i++)
        {
            var line = r.View(i).Social.Chat[0];
            Assert.Equal(r.Id(0), line.Player);
            Assert.Equal("hello world", new string(line.Text[..line.Length]));
        }
        r.Party(0, 1);
        Assert.True(r.Clients[0].Input.SendChat(ChatChannel.Party, "secret")); r.Step();
        Assert.Equal(2, r.View(0).Social.ChatCount);
        Assert.Equal(2, r.View(1).Social.ChatCount);
        Assert.Equal(1, r.View(2).Social.ChatCount);
        r.Clients[1].RequestLeave(); r.Step();
        Assert.Equal(0, r.View(0).Social.PartyCount);
        Assert.Contains(r.View(0).Social.Roster.Take(r.View(0).Social.RosterCount), p => p.Player == r.Id(1) && p.Value == 0);
    }

    [Fact]
    public void PartyRefusalsExpiryRemovalHandoverAndDissolution()
    {
        using var r = new Rig(4);
        r.Refusal(0, SocialKind.Leave, SocialRefusal.NotInParty);
        r.Refusal(0, SocialKind.Accept, SocialRefusal.NoInvitation);
        r.Refusal(0, SocialKind.Invite, SocialRefusal.UnknownPlayer, uint.MaxValue);
        r.Refusal(0, SocialKind.Invite, SocialRefusal.SelfInvite, r.Id(0));
        r.Request(0, SocialKind.Invite, r.Id(1));
        r.Refusal(2, SocialKind.Invite, SocialRefusal.AlreadyInvited, r.Id(1));
        r.Request(1, SocialKind.Decline);
        Assert.Equal(0u, r.View(1).Social.Invitation.Player);
        r.Request(0, SocialKind.Invite, r.Id(1));
        r.Step(r.Content.Social.InviteSeconds * TickLoop.SimulationHz);
        Assert.Equal(0u, r.View(1).Social.Invitation.Player);
        Assert.Equal(SocialRefusal.Expired, r.View(1).Social.LastRefusal);
        r.Party(0, 1);
        r.Refusal(1, SocialKind.Invite, SocialRefusal.NotLeader, r.Id(2));
        r.Refusal(2, SocialKind.Invite, SocialRefusal.AlreadyInParty, r.Id(1));
        r.Refusal(1, SocialKind.Accept, SocialRefusal.AlreadyInParty);
        r.Refusal(0, SocialKind.Remove, SocialRefusal.NotMember, r.Id(2));
        r.Refusal(1, SocialKind.Remove, SocialRefusal.NotLeader, r.Id(0));
        r.Party(0, 2);
        Assert.Equal(3, r.View(2).Social.PartyCount);
        r.Request(0, SocialKind.Invite, r.Id(3));
        r.Request(0, SocialKind.Leave);
        Assert.Equal(r.Id(1), r.View(1).Social.Leader);
        Assert.Equal(0u, r.View(3).Social.Invitation.Player);
        r.Request(1, SocialKind.Remove, r.Id(2));
        Assert.Equal(0, r.View(1).Social.PartyCount);
        Assert.Equal(0, r.View(2).Social.PartyCount);
    }

    [Fact]
    public void FullPartyAndInvitedDisconnectAreCleanedUp()
    {
        using var r = new Rig(10);
        for (var i = 1; i < r.Content.Social.PartySize; i++)
        {
            if (i == r.Content.Social.PartySize - 1) r.Request(0, SocialKind.Invite, r.Id(8));
            r.Party(0, i);
        }
        r.Refusal(8, SocialKind.Accept, SocialRefusal.PartyFull);
        r.Refusal(0, SocialKind.Invite, SocialRefusal.PartyFull, r.Id(8));
        r.Request(8, SocialKind.Invite, r.Id(9));
        r.Clients[8].RequestLeave(); r.Step();
        Assert.Equal(0u, r.View(9).Social.Invitation.Player);
        r.Clients[0].RequestLeave(); r.Step();
        Assert.Equal(r.Id(1), r.View(1).Social.Leader);
        Assert.Equal(7, r.View(1).Social.PartyCount);
    }

    [Fact]
    public void ChatValidationRateLimitAndBoundedHistory()
    {
        using var r = new Rig();
        var message = new SocialMessage { Kind = SocialKind.Chat };
        message.SetText("\t\n\u202E "); r.Send(0, message); r.Step();
        Assert.Equal(SocialRefusal.Empty, r.View(0).Social.LastRefusal);
        message.Value = (byte)ChatChannel.Party; message.SetText("x"); r.Send(0, message); r.Step();
        Assert.Equal(SocialRefusal.NotInParty, r.View(0).Social.LastRefusal);
        message.Value = 5; r.Send(0, message); r.Step();
        Assert.Equal(SocialRefusal.Malformed, r.View(0).Social.LastRefusal);
        r.Step(300);
        message.Value = 0;
        for (var i = 0; i < 20; i++) r.Send(0, message);
        r.Step(1);
        Assert.Equal(r.Content.Social.Burst, r.View(1).Social.ChatCount);
        for (var i = 0; i < 70; i++) { r.Step(); r.Send(0, message); }
        r.Step(); Assert.Equal(64, r.View(1).Social.ChatCount);
        Assert.False(r.Clients[0].Input.SendChat(ChatChannel.Everyone, new string('a', 257)));
        Assert.Null(r.Server.Fault);
    }

    [Fact]
    public void StatusTracksShipSwitchLossAndSector()
    {
        using var r = new Rig(); r.Party(0, 1);
        var originalShip = r.View(0).Hulls[r.View(0).OwnHull].Id;
        r.Clients[0].Input.RequestControl(0); r.Step();
        var party = r.View(1).Social;
        var member = party.Party.Take(party.PartyCount).Single(m => m.Player == r.Id(0));
        Assert.Equal(r.Content.DroneDesign, member.Value);
        Assert.InRange(member.Hull, 0f, 1f); Assert.InRange(member.Shield, 0f, 1f);
        Assert.Equal(0, member.X); Assert.Equal(0, member.Y); Assert.Equal(0, member.Z);
        var own = r.View(0);
        r.Clients[0].Input.RequestControl(originalShip); r.Step();
        member = r.View(1).Social.Party.Take(2).Single(m => m.Player == r.Id(0));
        Assert.Equal(r.Content.PodDesign, member.Value);
        var connections = (Array)typeof(ServerHost).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Server)!;
        foreach (var connection in connections)
        {
            if ((uint)connection!.GetType().GetField("PlayerId")!.GetValue(connection)! != r.Id(0)) continue;
            typeof(ServerHost).GetMethod("LoseHull", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(r.Server, [connection]); break;
        }
        r.Step();
        member = r.View(1).Social.Party.Take(2).Single(m => m.Player == r.Id(0));
        Assert.Equal(r.Content.DroneDesign, member.Value);
    }

    [Fact]
    public void LateJoinGetsCurrentRosterAndInvitationTargetLeavingDoesNotReserveAnything()
    {
        var content = GameContent.Load();
        using var a = new LoopbackLink(); using var b = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig(), content, a.Server, b.Server);
        using var first = new ClientSession(a.Client, content, "First");
        for (var tick = 0; tick < 5; tick++)
        { ((ITickHandler)server).Tick(tick); ((ITickHandler)first).Tick(tick); }
        Assert.Equal(1, first.AcquireSnapshot().Social.RosterCount);
        using var last = new ClientSession(b.Client, content, "Last");
        for (var tick = 5; tick < 10; tick++)
        { ((ITickHandler)server).Tick(tick); ((ITickHandler)first).Tick(tick); ((ITickHandler)last).Tick(tick); }
        Assert.Equal(2, last.AcquireSnapshot().Social.RosterCount);
        Assert.Equal(2, first.AcquireSnapshot().Social.RosterCount);
        first.Input.Invite(last.AcquireSnapshot().PlayerId);
        for (var tick = 10; tick < 15; tick++)
        { ((ITickHandler)server).Tick(tick); ((ITickHandler)first).Tick(tick); ((ITickHandler)last).Tick(tick); }
        Assert.NotEqual(0u, last.AcquireSnapshot().Social.Invitation.Player);
        last.RequestLeave();
        for (var tick = 15; tick < 20; tick++)
        { ((ITickHandler)server).Tick(tick); ((ITickHandler)first).Tick(tick); ((ITickHandler)last).Tick(tick); }
        Assert.Equal(1, server.Status.PlayerCount);
        Assert.Equal(0, first.AcquireSnapshot().Social.PartyCount);
        Assert.Equal(0u, last.AcquireSnapshot().Social.Invitation.Player);
    }

    [Fact]
    public void SocialBeforeAdmissionIsRefusedAndMalformedActiveRequestsAreBounded()
    {
        var content = GameContent.Load();
        Span<byte> bytes = stackalloc byte[SocialMessage.MaxWireSize];
        foreach (var kind in Enum.GetValues<SocialKind>())
        {
            using var link = new LoopbackLink();
            using var server = new ServerHost(new ServerConfig(), content, link.Server);
            var writer = new NetWriter(bytes);
            new SocialMessage { Kind = kind }.Write(ref writer);
            link.Server.Poll(server);
            link.Client.Poll(new SilentPeer());
            Assert.True(link.Client.Send(0, NetChannel.Reliable, writer.Written));
            ((ITickHandler)server).Tick(0);
            Assert.False(link.Server.Send(0, NetChannel.Reliable, writer.Written));
            Assert.Equal(0, server.Status.PlayerCount);
        }
        using var r = new Rig();
        Span<byte> payload = stackalloc byte[SocialMessage.MaxWireSize + 10];
        foreach (var kind in Enum.GetValues<SocialKind>())
        {
            var writer = new NetWriter(payload);
            new SocialMessage { Kind = kind }.Write(ref writer);
            for (var length = 1; length < writer.Length; length++)
                r.Links[0].Client.Send(0, NetChannel.Reliable, payload[..length]);
            r.Step(300);
        }
        Assert.Equal(0, r.View(1).Social.ChatCount);
        Assert.Equal(0, r.View(1).Social.PartyCount);
        var oversized = new NetWriter(payload);
        new SocialMessage { Kind = SocialKind.Chat }.Write(ref oversized);
        var tail = new NetWriter(payload[SocialMessage.FixedWireSize..]);
        tail.WriteString(new string('x', 257));
        r.Links[0].Client.Send(0, NetChannel.Reliable, payload[..(SocialMessage.FixedWireSize + tail.Length)]);
        r.Step();
        Assert.Equal(SocialRefusal.Malformed, r.View(0).Social.LastRefusal);
    }

    [Fact]
    public void ContentLimitsUnicodeAndSnapshotCopiesHaveNoSteadyAllocation()
    {
        var message = new SocialMessage { Kind = SocialKind.Chat };
        message.SetText("éé"); Assert.Equal(SocialRefusal.TooLong, message.CleanChat(1, 768));
        message.SetText("éé"); Assert.Equal(SocialRefusal.TooLong, message.CleanChat(256, 3));
        message.SetText("😀\u202Ehi"); Assert.Equal(SocialRefusal.None, message.CleanChat(256, 768));
        Assert.Equal("😀hi", new string(message.Text[..message.Length]));
        var source = new SocialState(); var copy = new SocialState();
        source.Apply(new SocialMessage { Kind = SocialKind.PartyBegin, Leader = 1, Value = 2 });
        source.Apply(new SocialMessage { Kind = SocialKind.Member, Player = 1, Leader = 1 });
        Assert.Equal(0, source.PartyCount);
        source.Apply(new SocialMessage { Kind = SocialKind.Member, Player = 2, Leader = 1 });
        Assert.Equal(2, source.PartyCount);
        source.CopyTo(copy);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (allocation.Measuring())
                for (var j = 0; j < 100; j++) source.CopyTo(copy);
        Assert.Equal(0, allocation.Bytes);
        source.Party[0].Player = 50; Assert.Equal(1u, copy.Party[0].Player);
    }

    [Fact]
    public void UdpSessionsDeliverRosterChatAndPartyStatus()
    {
        var content = GameContent.Load();
        using var server = new ServerHost(new ServerConfig { Visibility = ServerVisibility.Unlisted, Port = TestNet.Port(31) }, content);
        server.Start();
        using var first = GameSession.Join("127.0.0.1", TestNet.Port(31), content, "First");
        var firstId = TestNet.WaitFor(first, s => s.State == SessionState.Active).PlayerId;
        using var second = GameSession.Join("127.0.0.1", TestNet.Port(31), content, "Second");
        var secondId = TestNet.WaitFor(second, s => s.Social.RosterCount == 2).PlayerId;
        TestNet.WaitFor(first, s => s.Social.RosterCount == 2);
        first.Client.Input.SendChat(ChatChannel.Everyone, "over UDP");
        TestNet.WaitFor(second, s => s.Social.ChatCount == 1);
        first.Client.Input.Invite(secondId);
        TestNet.WaitFor(second, s => s.Social.Invitation.Player == firstId);
        second.Client.Input.AcceptInvitation(second.Client.AcquireSnapshot().Social.Invitation.InvitationId);
        TestNet.WaitFor(first, s => s.Social.PartyCount == 2);
        TestNet.WaitFor(second, s => s.Social.PartyCount == 2);
        second.Client.RequestLeave();
        TestNet.WaitFor(first, s => s.Social.PartyCount == 0);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ActivePartyTickAndSnapshotPublicationAllocateNothing()
    {
        using var r = new Rig(); r.Party(0, 1); r.Step(300);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 300; i++)
            while (allocation.Measuring()) r.Step(1);
        Assert.Equal(0, allocation.Bytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReplyToDisplayedInvitationCannotActOnItsReplacement(bool decline, bool sameInviter)
    {
        using var r = new Rig();
        r.Request(0, SocialKind.Invite, r.Id(1));
        var displayed = r.View(1).Social.Invitation;
        if (sameInviter) r.Request(1, SocialKind.Decline);
        if (decline) Assert.True(r.Clients[1].Input.DeclineInvitation(displayed.InvitationId));
        else Assert.True(r.Clients[1].Input.AcceptInvitation(displayed.InvitationId));
        if (!sameInviter)
        {
            r.Links[0].Client.Disconnect(0);
            r.Clients[0].RequestLeave();
        }
        var newInviter = sameInviter ? 0 : 2;
        r.Send(newInviter, new SocialMessage { Kind = SocialKind.Invite, Player = r.Id(1) });
        r.Step();
        var replacement = r.View(1).Social.Invitation;
        Assert.Equal(r.Id(newInviter), replacement.Player);
        Assert.NotEqual(displayed.InvitationId, replacement.InvitationId);
        Assert.NotEqual(0ul, replacement.InvitationId);
        Assert.Equal(SocialRefusal.StaleInvitation, r.View(1).Social.LastRefusal);
        Assert.Equal(0, r.View(1).Social.PartyCount);
        Assert.True(r.Clients[1].Input.AcceptInvitation(replacement.InvitationId));
        r.Step();
        Assert.Equal(2, r.View(1).Social.PartyCount);
    }

    [Fact]
    public void MaximumWireSizeMatchesMaximumUtf8Text()
    {
        Span<byte> bytes = stackalloc byte[SocialMessage.MaxWireSize];
        var message = new SocialMessage { Kind = SocialKind.Chat };
        Assert.True(message.SetText(new string('界', 256)));
        var writer = new NetWriter(bytes); message.Write(ref writer);
        Assert.Equal(SocialMessage.MaxWireSize, writer.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(SocialMessage.TryRead(ref reader, out var read));
        Assert.Equal(256, read.Length);
    }

    [Fact]
    public void EveryKindRejectsTruncationAndNoiseWithoutAllocating()
    {
        Span<byte> bytes = stackalloc byte[SocialMessage.MaxWireSize];
        foreach (var kind in Enum.GetValues<SocialKind>())
        {
            var m = new SocialMessage { Kind = kind }; m.SetText(new string('x', 256));
            var w = new NetWriter(bytes); m.Write(ref w);
            for (var length = 0; length < w.Length - 1; length++)
            {
                var reader = new NetReader(bytes.Slice(1, length));
                Assert.False(SocialMessage.TryRead(ref reader, out _));
            }
            var good = new NetReader(w.Written[1..]); Assert.True(SocialMessage.TryRead(ref good, out _));
        }
        var random = new Random(5); random.NextBytes(bytes);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
        {
            while (allocation.Measuring())
                for (var j = 0; j < 100; j++) { var reader = new NetReader(bytes); SocialMessage.TryRead(ref reader, out _); }
        }
        Assert.Equal(0, allocation.Bytes);
    }
}
