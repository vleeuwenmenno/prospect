using System.Text;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Social;

/// <summary>What became of a chat line the player tried to send.</summary>
internal enum ChatSendResult
{
    /// <summary>Queued for the server, which still cleans and may refuse it.</summary>
    Sent,

    /// <summary>Nothing but whitespace.</summary>
    Empty,

    /// <summary>Over the content's character or byte limit.</summary>
    TooLong,

    /// <summary>The party channel without a party.</summary>
    NotInParty,

    /// <summary>The bridge's request queue is full.</summary>
    QueueFull,
}

/// <summary>
/// The interface's reading of <see cref="SocialState"/> and its route to <see cref="PlayerInputBridge"/>: who is in
/// the party, who may be invited, what a refusal means, and sending a typed line. No drawing and no allocation, so
/// it is shared by the screens and the tests. The server still decides everything.
/// </summary>
internal static class SocialView
{
    /// <summary>The roster index of a player, or -1 when the roster does not hold them.</summary>
    public static int FindRoster(SocialState social, uint player)
    {
        for (var i = 0; i < social.RosterCount; i++)
        {
            if (social.Roster[i].Player == player)
                return i;
        }

        return -1;
    }

    /// <summary>The player's name as the roster has it, or empty when unknown.</summary>
    public static ReadOnlySpan<char> Name(SocialState social, uint player)
    {
        var index = FindRoster(social, player);
        return index < 0 ? default : social.Roster[index].Text[..social.Roster[index].Length];
    }

    /// <summary>True when the player is listed in the current party.</summary>
    public static bool IsMember(SocialState social, uint player)
    {
        for (var i = 0; i < social.PartyCount; i++)
        {
            if (social.Party[i].Player == player)
                return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="me"/> leads the current party.</summary>
    public static bool IsLeader(SocialState social, uint me) => social.PartyCount > 0 && social.Leader == me;

    /// <summary>
    /// True when the interface offers an invitation to a roster entry: someone else who is connected, not in the
    /// party and not the player whose invitation is waiting (they lead a party of their own), while the player is
    /// solo or leads a party with room. The server may still refuse: the target can be in another party, or
    /// already invited; the snapshot does not say.
    /// </summary>
    public static bool CanInvite(SocialState social, uint me, in SocialMessage entry, int partySize) =>
        entry.Value != 0
        && entry.Player != me
        && entry.Player != 0
        && entry.Player != social.Invitation.Player
        && !IsMember(social, entry.Player)
        && (social.PartyCount == 0 || (social.Leader == me && social.PartyCount < partySize));

    /// <summary>Whole seconds from <paramref name="nowTick"/> until <paramref name="untilTick"/>, never negative.</summary>
    public static int SecondsUntil(long untilTick, long nowTick) =>
        untilTick <= nowTick ? 0 : (int)((untilTick - nowTick + TickLoop.SimulationHz - 1) / TickLoop.SimulationHz);

    /// <summary>Seconds since a server tick, for the age of a chat line or a party status sample.</summary>
    public static float SecondsSince(long tick, long nowTick) => Math.Max(0, nowTick - tick) / (float)TickLoop.SimulationHz;

    /// <summary>
    /// Sends a typed line: UTF-8 from the text field, checked against the content's limits before it is queued, so
    /// the player is told at once rather than by a refusal. Nothing is allocated.
    /// </summary>
    public static ChatSendResult SendChat(PlayerInputBridge bridge, SocialState social, ChatChannel channel, ReadOnlySpan<byte> utf8, SocialRules rules)
    {
        if (utf8.Length > rules.ChatBytes)
            return ChatSendResult.TooLong;

        // The rules cap a line at 768 bytes, which is never more than 768 characters.
        Span<char> chars = stackalloc char[768];
        var count = Encoding.UTF8.GetChars(utf8, chars);
        var text = ((ReadOnlySpan<char>)chars[..count]).Trim();
        if (text.Length == 0)
            return ChatSendResult.Empty;
        if (text.Length > rules.ChatChars)
            return ChatSendResult.TooLong;
        if (channel == ChatChannel.Party && social.PartyCount == 0)
            return ChatSendResult.NotInParty;
        return bridge.SendChat(channel, text) ? ChatSendResult.Sent : ChatSendResult.QueueFull;
    }

    /// <summary>A sentence for the player about a send that did not go out; empty for <see cref="ChatSendResult.Sent"/>.</summary>
    public static ReadOnlySpan<byte> Describe(ChatSendResult result) =>
        result switch
        {
            ChatSendResult.Empty => "Type something to send."u8,
            ChatSendResult.TooLong => "Too long to send. Shorten the line."u8,
            ChatSendResult.NotInParty => "You are not in a party. Switch to Everyone."u8,
            ChatSendResult.QueueFull => "Too many requests waiting. Try again in a moment."u8,
            _ => default,
        };

    /// <summary>A sentence for the player about a refusal from the server.</summary>
    public static ReadOnlySpan<byte> Describe(SocialRefusal refusal) =>
        refusal switch
        {
            SocialRefusal.Malformed => "The server could not read that request."u8,
            SocialRefusal.Empty => "The server dropped an empty line."u8,
            SocialRefusal.TooLong => "The server refused a line as too long."u8,
            SocialRefusal.NotInParty => "You are not in a party."u8,
            SocialRefusal.AlreadyInParty => "Already in a party."u8,
            SocialRefusal.AlreadyInvited => "That player already has an invitation waiting."u8,
            SocialRefusal.PartyFull => "The party is full."u8,
            SocialRefusal.NotLeader => "Only the party leader can do that."u8,
            SocialRefusal.UnknownPlayer => "That player is not connected."u8,
            SocialRefusal.SelfInvite => "You cannot invite yourself."u8,
            SocialRefusal.NoInvitation => "There is no invitation to answer."u8,
            SocialRefusal.Expired => "The invitation expired."u8,
            SocialRefusal.NotMember => "That player is not in your party."u8,
            SocialRefusal.StaleInvitation => "That invitation was replaced. Answer the new one."u8,
            _ => default,
        };
}
