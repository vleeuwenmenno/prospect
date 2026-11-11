using System.Text;
using Prospect.Client.Social;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The interface's route into the social system, through the same <see cref="SocialView"/> calls the screens make,
/// against a real server and real client sessions stepped tick by tick.
/// </summary>
public sealed class SocialInterfaceTests
{
    private sealed class Rig : IDisposable
    {
        public readonly GameContent Content = GameContent.Load();
        private readonly LoopbackLink[] _links;
        public readonly ClientSession[] Clients;
        private readonly ServerHost _server;
        private long _tick;

        public Rig(int count)
        {
            _links = new LoopbackLink[count];
            Clients = new ClientSession[count];
            var transports = new ITransport[count];
            for (var i = 0; i < count; i++)
            {
                _links[i] = new LoopbackLink();
                transports[i] = _links[i].Server;
                Clients[i] = new ClientSession(_links[i].Client, Content, $"Player{i}");
            }

            _server = new ServerHost(new ServerConfig { MaxPlayers = count }, Content, transports);
            Step(3);
        }

        public SessionSnapshot View(int i) => Clients[i].AcquireSnapshot();

        public PlayerInputBridge Bridge(int i) => Clients[i].Input;

        public void Step(int ticks = 65)
        {
            for (var t = 0; t < ticks; t++)
            {
                ((ITickHandler)_server).Tick(_tick);
                foreach (var client in Clients)
                    ((ITickHandler)client).Tick(_tick);
                _tick++;
            }
        }

        public void Dispose()
        {
            foreach (var client in Clients)
                client.Dispose();
            _server.Dispose();
            foreach (var link in _links)
                link.Dispose();
        }
    }

    private static string Text(in SocialMessage message) => new(message.Text[..message.Length]);

    [Fact]
    public void TypedChatIsCheckedLocallyThenReachesEveryoneExactlyAsTyped()
    {
        using var r = new Rig(2);
        var rules = r.Content.Social;
        var social = r.View(0).Social;

        // Format specifiers, ImGui id separators and text the font may lack arrive as text, untouched.
        const string line = "100% sure ##id ### %s %n 漢字 Ünïcødé";
        Assert.Equal(ChatSendResult.Sent, SocialView.SendChat(r.Bridge(0), social, ChatChannel.Everyone, Encoding.UTF8.GetBytes("  " + line + "  "), rules));
        r.Step();
        for (var i = 0; i < 2; i++)
        {
            var view = r.View(i).Social;
            Assert.Equal(1, view.ChatCount);
            Assert.Equal(line, Text(view.Chat[0]));
            Assert.Equal("Player0", SocialView.Name(view, view.Chat[0].Player).ToString());
        }

        // Refused before anything is queued: nothing reaches the server, so no line and no refusal arrive.
        Assert.Equal(ChatSendResult.Empty, SocialView.SendChat(r.Bridge(0), social, ChatChannel.Everyone, " \t "u8, rules));
        Assert.Equal(ChatSendResult.TooLong, SocialView.SendChat(r.Bridge(0), social, ChatChannel.Everyone, Encoding.UTF8.GetBytes(new string('x', rules.ChatChars + 1)), rules));
        Assert.Equal(ChatSendResult.TooLong, SocialView.SendChat(r.Bridge(0), social, ChatChannel.Everyone, Encoding.UTF8.GetBytes(new string('界', rules.ChatBytes / 3 + 1)), rules));
        Assert.Equal(ChatSendResult.NotInParty, SocialView.SendChat(r.Bridge(0), social, ChatChannel.Party, "secret"u8, rules));
        r.Step();
        Assert.Equal(1, r.View(1).Social.ChatCount);
        Assert.Equal(0, r.View(0).Social.RefusalCount);

        // The longest line the rules allow still goes out whole.
        var longest = new string('y', rules.ChatChars);
        Assert.Equal(ChatSendResult.Sent, SocialView.SendChat(r.Bridge(0), social, ChatChannel.Everyone, Encoding.UTF8.GetBytes(longest), rules));
        r.Step();
        Assert.Equal(longest, Text(r.View(1).Social.Chat[1]));
    }

    [Fact]
    public void AFullRequestQueueIsReportedAndNothingIsLost()
    {
        using var r = new Rig(1);
        var social = r.View(0).Social;
        var sent = 0;
        ChatSendResult result;
        while ((result = SocialView.SendChat(r.Bridge(0), social, ChatChannel.Everyone, "spam"u8, r.Content.Social)) == ChatSendResult.Sent)
            sent++;
        Assert.Equal(ChatSendResult.QueueFull, result);
        Assert.Equal(16, sent);
        Assert.False(SocialView.Describe(result).IsEmpty);
    }

    [Fact]
    public void AnsweringTheDisplayedInvitationNeverAcceptsItsReplacement()
    {
        using var r = new Rig(3);
        var leader = r.View(0).PlayerId;
        Assert.True(r.Bridge(0).Invite(r.View(2).PlayerId));
        r.Step();

        // What the panel drew: the inviter cannot be invited back, and the card's identity is kept.
        var shown = r.View(2).Social;
        Assert.Equal(leader, shown.Invitation.Player);
        var entry = shown.Roster[SocialView.FindRoster(shown, leader)];
        Assert.False(SocialView.CanInvite(shown, r.View(2).PlayerId, entry, r.Content.Social.PartySize));
        var displayed = shown.Invitation.InvitationId;
        Assert.True(SocialView.SecondsUntil(shown.Invitation.Tick, r.View(2).ServerTick) > 0);

        // It expires and is reissued before the player's click arrives.
        r.Step(r.Content.Social.InviteSeconds * TickLoop.SimulationHz);
        var expired = r.View(2).Social.RefusalCount;
        Assert.Equal(SocialRefusal.Expired, r.View(2).Social.LastRefusal);
        Assert.True(r.Bridge(0).Invite(r.View(2).PlayerId));
        r.Step();
        var replacement = r.View(2).Social.Invitation.InvitationId;
        Assert.NotEqual(displayed, replacement);

        Assert.True(r.Bridge(2).AcceptInvitation(displayed));
        r.Step();
        var after = r.View(2).Social;
        Assert.Equal(SocialRefusal.StaleInvitation, after.LastRefusal);
        Assert.Equal(expired + 1, after.RefusalCount);
        Assert.Equal(0, after.PartyCount);
        Assert.Equal(replacement, after.Invitation.InvitationId);

        // A second stale answer is a new refusal with the same reason; the interface still sees it as new.
        Assert.True(r.Bridge(2).DeclineInvitation(displayed));
        r.Step();
        Assert.Equal(expired + 2, r.View(2).Social.RefusalCount);

        Assert.True(r.Bridge(2).AcceptInvitation(replacement));
        r.Step();
        var party = r.View(2).Social;
        Assert.Equal(2, party.PartyCount);
        Assert.True(SocialView.IsMember(party, leader));
        Assert.False(SocialView.IsLeader(party, r.View(2).PlayerId));
        Assert.True(SocialView.IsLeader(r.View(0).Social, leader));
    }

    [Fact]
    public void OnlyTheLeaderIsOfferedInvitesAndRemovalReachesTheServer()
    {
        using var r = new Rig(3);
        var a = r.View(0).PlayerId;
        var b = r.View(1).PlayerId;
        var c = r.View(2).PlayerId;
        Assert.True(r.Bridge(0).Invite(b));
        r.Step();
        Assert.True(r.Bridge(1).AcceptInvitation(r.View(1).Social.Invitation.InvitationId));
        r.Step();

        var member = r.View(1).Social;
        var cEntry = member.Roster[SocialView.FindRoster(member, c)];
        Assert.False(SocialView.CanInvite(member, b, cEntry, r.Content.Social.PartySize));
        var leaderView = r.View(0).Social;
        Assert.True(SocialView.CanInvite(leaderView, a, leaderView.Roster[SocialView.FindRoster(leaderView, c)], r.Content.Social.PartySize));
        Assert.False(SocialView.CanInvite(leaderView, a, leaderView.Roster[SocialView.FindRoster(leaderView, b)], r.Content.Social.PartySize));

        // Party chat from the panel's channel reaches members only.
        Assert.Equal(ChatSendResult.Sent, SocialView.SendChat(r.Bridge(1), member, ChatChannel.Party, "regroup"u8, r.Content.Social));
        r.Step();
        Assert.Equal(1, r.View(0).Social.ChatCount);
        Assert.Equal(0, r.View(2).Social.ChatCount);

        Assert.True(r.Bridge(0).RemoveMember(b));
        r.Step();
        Assert.Equal(0, r.View(0).Social.PartyCount);
        Assert.Equal(0, r.View(1).Social.PartyCount);
    }
}
