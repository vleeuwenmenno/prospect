using Prospect.Game.Protocol;

namespace Prospect.Game.Client;

/// <summary>Snapshot-owned social data; text is inline UTF-16 and arrays are never shared between snapshots.</summary>
public sealed class SocialState
{
    private readonly SocialMessage[] _pendingParty = new SocialMessage[16];
    private int _pendingCount, _expectedCount;
    private uint _pendingLeader;
    /// <summary>Current roster plus bounded recent departures; valid through RosterCount.</summary>
    public SocialMessage[] Roster = new SocialMessage[32];
    /// <summary>Number of roster entries.</summary>
    public int RosterCount;
    /// <summary>Recent chat in chronological order; valid through ChatCount.</summary>
    public readonly SocialMessage[] Chat = new SocialMessage[64];
    /// <summary>Number of recent chat lines.</summary>
    public int ChatCount;
    /// <summary>Party members; design indices resolve through SessionSnapshot.Content.Designs.</summary>
    public readonly SocialMessage[] Party = new SocialMessage[16];
    /// <summary>Number of party members, all connected. Departed members are removed.</summary>
    public int PartyCount;
    /// <summary>Current party leader, or zero.</summary>
    public uint Leader;
    /// <summary>Pending invitation; Player is zero when none exists, Tick is expiry.</summary>
    public SocialMessage Invitation;
    /// <summary>Most recent server refusal.</summary>
    public SocialRefusal LastRefusal;

    internal void Apply(in SocialMessage m)
    {
        switch (m.Kind)
        {
            case SocialKind.Roster:
                for (var i = 0; i < RosterCount; i++)
                    if (Roster[i].Player == m.Player) { Roster[i] = m; return; }
                if (RosterCount < Roster.Length) Roster[RosterCount++] = m;
                else
                    for (var i = 0; i < RosterCount; i++)
                        if (Roster[i].Value == 0) { Roster[i] = m; break; }
                break;
            case SocialKind.Line:
                if (ChatCount == Chat.Length) { Array.Copy(Chat, 1, Chat, 0, Chat.Length - 1); ChatCount--; }
                Chat[ChatCount++] = m;
                break;
            case SocialKind.PartyBegin:
                _pendingLeader = m.Leader; _pendingCount = 0; _expectedCount = m.Value;
                if (_expectedCount == 0) { PartyCount = 0; Leader = 0; }
                break;
            case SocialKind.Member:
                if (m.Leader == _pendingLeader && _pendingCount < _expectedCount && _expectedCount <= Party.Length)
                {
                    _pendingParty[_pendingCount++] = m;
                    if (_pendingCount == _expectedCount)
                    {
                        _pendingParty.AsSpan(0, _pendingCount).CopyTo(Party);
                        PartyCount = _pendingCount; Leader = _pendingLeader;
                    }
                }
                break;
            case SocialKind.Invitation: Invitation = m; break;
            case SocialKind.Refused:
                if (m.Value <= (byte)SocialRefusal.NotMember) LastRefusal = (SocialRefusal)m.Value;
                break;
        }
    }

    internal void Disconnect()
    {
        PartyCount = 0; Leader = 0; Invitation = default;
        for (var i = 0; i < RosterCount; i++) Roster[i].Value = 0;
    }

    internal void CopyTo(SocialState destination)
    {
        if (destination.Roster.Length != Roster.Length) destination.Roster = new SocialMessage[Roster.Length];
        Roster.AsSpan(0, RosterCount).CopyTo(destination.Roster);
        Chat.AsSpan(0, ChatCount).CopyTo(destination.Chat);
        Party.AsSpan(0, PartyCount).CopyTo(destination.Party);
        destination.RosterCount = RosterCount; destination.ChatCount = ChatCount;
        destination.PartyCount = PartyCount; destination.Leader = Leader;
        destination.Invitation = Invitation; destination.LastRefusal = LastRefusal;
    }
}
