using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private ulong _nextInvitationId;
    private Connection? Player(uint id)
    {
        foreach (var c in _connections)
            if (c.State == ConnectionState.Active && c.PlayerId == id) return c;
        return null;
    }

    private void SocialSend(Connection c, in SocialMessage message)
    {
        var writer = new NetWriter(_sendBuffer);
        message.Write(ref writer);
        // A transport that cannot queue reliable state must not leave a silently stale session alive.
        if (!c.Transport.Send(c.Peer, NetChannel.Reliable, writer.Written)) c.SocialFailed = true;
    }

    private void RefuseSocial(Connection c, SocialRefusal reason) => SocialSend(c, new SocialMessage { Kind = SocialKind.Refused, Value = (byte)reason });

    private void Roster(Connection recipient, Connection player, bool connected)
    {
        var m = new SocialMessage { Kind = SocialKind.Roster, Player = player.PlayerId, Value = (byte)(connected ? 1 : 0) };
        m.SetText(player.Name);
        SocialSend(recipient, m);
    }

    private void JoinSocial(Connection joined)
    {
        joined.SocialCredit = _content.Social.Burst;
        joined.SocialAt = _tick;
        foreach (var c in _connections)
        {
            if (c.State != ConnectionState.Active) continue;
            Roster(joined, c, true);
            if (c != joined) Roster(c, joined, true);
        }
    }

    private void LeaveSocial(Connection leaving)
    {
        LeaveParty(leaving);
        ClearInvites(leaving.PlayerId);
        leaving.Inviter = 0;
        foreach (var c in _connections)
            if (c.State == ConnectionState.Active && c != leaving) Roster(c, leaving, false);
    }

    private void ClearInvites(uint inviter)
    {
        foreach (var c in _connections)
        {
            if (c.State != ConnectionState.Active || c.Inviter != inviter) continue;
            c.Inviter = 0;
            SocialSend(c, new SocialMessage { Kind = SocialKind.Invitation });
        }
    }

    private int PartyCount(uint leader)
    {
        var count = 0;
        foreach (var c in _connections)
            if (c.State == ConnectionState.Active && c.Party == leader && leader != 0) count++;
        return count;
    }

    private void LeaveParty(Connection c)
    {
        var old = c.Party;
        if (old == 0) return;
        c.Party = 0;
        SocialSend(c, new SocialMessage { Kind = SocialKind.PartyBegin });
        ClearInvites(old);
        var count = PartyCount(old);
        uint next = 0;
        foreach (var member in _connections)
            if (member.State == ConnectionState.Active && member.Party == old && (next == 0 || member.PlayerId < next)) next = member.PlayerId;
        if (count < 2)
        {
            foreach (var member in _connections)
            {
                if (member.State != ConnectionState.Active || member.Party != old) continue;
                member.Party = 0;
                SocialSend(member, new SocialMessage { Kind = SocialKind.PartyBegin });
            }
        }
        else
        {
            if (old == c.PlayerId)
                foreach (var member in _connections)
                    if (member.State == ConnectionState.Active && member.Party == old) member.Party = next;
            SendParty(old == c.PlayerId ? next : old);
        }
        Logs.Sim.Info($"Player {c.PlayerId} left party {old}");
    }

    private void ReceiveSocial(Connection c, ref NetReader reader)
    {
        // This check precedes parsing and all replies, so rejected flood packets cost no allocation or fan-out.
        c.SocialCredit = Math.Min(_content.Social.Burst, c.SocialCredit + (_tick - c.SocialAt) * (double)_content.Social.PerSecond / TickLoop.SimulationHz);
        c.SocialAt = _tick;
        if (c.SocialCredit < 1) return;
        c.SocialCredit--;
        if (!SocialMessage.TryRead(ref reader, out var m) || m.Kind > SocialKind.Remove)
        { RefuseSocial(c, SocialRefusal.Malformed); return; }
        if (m.Kind == SocialKind.Chat)
        {
            if (m.Value > (byte)ChatChannel.Party) { RefuseSocial(c, SocialRefusal.Malformed); return; }
            var reason = m.CleanChat(_content.Social.ChatChars, _content.Social.ChatBytes);
            if (reason != SocialRefusal.None) { RefuseSocial(c, reason); return; }
            if (m.Value == (byte)ChatChannel.Party && c.Party == 0) { RefuseSocial(c, SocialRefusal.NotInParty); return; }
            m.Kind = SocialKind.Line; m.Player = c.PlayerId; m.Leader = 0; m.Tick = _tick;
            foreach (var recipient in _connections)
                if (recipient.State == ConnectionState.Active && (m.Value == (byte)ChatChannel.Everyone || recipient.Party == c.Party)) SocialSend(recipient, m);
            Logs.Sim.Debug($"Chat from player {c.PlayerId}, channel {m.Value}, {m.Length} characters");
            return;
        }
        if (m.Kind == SocialKind.Leave)
        {
            if (c.Party == 0) RefuseSocial(c, SocialRefusal.NotInParty);
            else LeaveParty(c);
            return;
        }
        if (m.Kind is SocialKind.Accept or SocialKind.Decline)
        {
            if (c.Party != 0) { RefuseSocial(c, SocialRefusal.AlreadyInParty); return; }
            if (c.Inviter == 0) { RefuseSocial(c, SocialRefusal.NoInvitation); return; }
            if (m.InvitationId != c.InvitationId) { RefuseSocial(c, SocialRefusal.StaleInvitation); return; }
            if (_tick >= c.InviteUntil) { c.Inviter = 0; SocialSend(c, new SocialMessage { Kind = SocialKind.Invitation }); RefuseSocial(c, SocialRefusal.Expired); return; }
            var inviter = Player(c.Inviter);
            c.Inviter = 0;
            SocialSend(c, new SocialMessage { Kind = SocialKind.Invitation });
            if (m.Kind == SocialKind.Decline) return;
            if (inviter is null) { RefuseSocial(c, SocialRefusal.UnknownPlayer); return; }
            if (inviter.Party != 0 && inviter.Party != inviter.PlayerId) { RefuseSocial(c, SocialRefusal.NotLeader); return; }
            if (PartyCount(inviter.Party) >= _content.Social.PartySize) { RefuseSocial(c, SocialRefusal.PartyFull); return; }
            inviter.Party = inviter.PlayerId; c.Party = inviter.PlayerId;
            ClearInvites(c.PlayerId);
            SendParty(c.Party);
            Logs.Sim.Info($"Player {c.PlayerId} joined party {c.Party}");
            return;
        }
        var target = Player(m.Player);
        if (target is null) { RefuseSocial(c, SocialRefusal.UnknownPlayer); return; }
        if (target == c) { RefuseSocial(c, SocialRefusal.SelfInvite); return; }
        if (m.Kind == SocialKind.Remove)
        {
            if (c.Party == 0) RefuseSocial(c, SocialRefusal.NotInParty);
            else if (c.Party != c.PlayerId) RefuseSocial(c, SocialRefusal.NotLeader);
            else if (target.Party != c.Party) RefuseSocial(c, SocialRefusal.NotMember);
            else LeaveParty(target);
            return;
        }
        if (c.Party != 0 && c.Party != c.PlayerId) { RefuseSocial(c, SocialRefusal.NotLeader); return; }
        if (target.Party != 0) { RefuseSocial(c, SocialRefusal.AlreadyInParty); return; }
        if (target.Inviter != 0 && _tick < target.InviteUntil) { RefuseSocial(c, SocialRefusal.AlreadyInvited); return; }
        if (PartyCount(c.Party) >= _content.Social.PartySize) { RefuseSocial(c, SocialRefusal.PartyFull); return; }
        // Never reuse an identity, including for invitations reissued by the same player in the same tick.
        if (_nextInvitationId == ulong.MaxValue) { RefuseSocial(c, SocialRefusal.Malformed); return; }
        target.InvitationId = ++_nextInvitationId;
        target.Inviter = c.PlayerId; target.InviteUntil = _tick + _content.Social.InviteSeconds * TickLoop.SimulationHz;
        SocialSend(target, new SocialMessage { Kind = SocialKind.Invitation, Player = c.PlayerId, Tick = target.InviteUntil, InvitationId = target.InvitationId });
        Logs.Sim.Info($"Player {c.PlayerId} invited {target.PlayerId}");
    }

    private void SendParty(uint leader)
    {
        foreach (var recipient in _connections)
        {
            if (recipient.State != ConnectionState.Active || recipient.Party != leader) continue;
            SocialSend(recipient, new SocialMessage { Kind = SocialKind.PartyBegin, Leader = leader, Value = (byte)PartyCount(leader) });
            foreach (var member in _connections)
            {
                if (member.State != ConnectionState.Active || member.Party != leader || !_hulls.IsAlive(member.Hull)) continue;
                var index = member.Hull.Index;
                ref var state = ref _hulls.State(index);
                ref var flight = ref _hulls.Flight(index);
                var built = _content.Designs[_hulls.Info(index).Design].Flight.HullPoints;
                var sector = _content.Galaxy.SectorOf(state.Position);
                SocialSend(recipient, new SocialMessage
                {
                    Kind = SocialKind.Member,
                    Player = member.PlayerId,
                    Leader = leader,
                    Tick = _tick,
                    Value = _hulls.Info(index).Design,
                    // A broken shield building its charge back protects nothing yet, so it shows as down.
                    Shield = flight.ShieldCapacity > 0 && !state.ShieldOffline ? Math.Clamp(state.Shield / flight.ShieldCapacity, 0, 1) : 0,
                    // Against the design as built: what the hull has left falls with its blocks, and a ship that
                    // has lost half of them is not whole again.
                    Hull = built > 0 ? Math.Clamp(state.Hull / built, 0, 1) : 0,
                    X = sector.X,
                    Y = sector.Y,
                    Z = sector.Z,
                });
            }
        }
    }

    private void TickSocial(long tick)
    {
        foreach (var c in _connections)
        {
            if (c.State != ConnectionState.Active) continue;
            if (c.Inviter != 0 && tick >= c.InviteUntil)
            {
                c.Inviter = 0;
                SocialSend(c, new SocialMessage { Kind = SocialKind.Invitation });
                RefuseSocial(c, SocialRefusal.Expired);
            }
            if (c.Party == c.PlayerId && tick % (TickLoop.SimulationHz / _content.Social.StatusHz) == 0) SendParty(c.Party);
        }
    }
}
