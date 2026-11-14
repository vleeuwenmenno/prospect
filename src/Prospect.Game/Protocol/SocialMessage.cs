using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Prospect.Engine.Net;

namespace Prospect.Game.Protocol;

/// <summary>Social requests precede the authoritative event kinds.</summary>
public enum SocialKind : byte
{
    /// <summary>Submit a chat line.</summary>
    Chat,
    /// <summary>Invite the target player.</summary>
    Invite,
    /// <summary>Accept the pending invitation.</summary>
    Accept,
    /// <summary>Decline the pending invitation.</summary>
    Decline,
    /// <summary>Leave the party.</summary>
    Leave,
    /// <summary>Remove the target member as leader.</summary>
    Remove,
    /// <summary>Publish one connected or departed roster entry.</summary>
    Roster,
    /// <summary>Deliver a server-stamped chat line.</summary>
    Line,
    /// <summary>Start an atomic party batch; Value gives its member count.</summary>
    PartyBegin,
    /// <summary>Supply one member of the pending party batch.</summary>
    Member,
    /// <summary>Replace or clear the pending invitation.</summary>
    Invitation,
    /// <summary>Report a refusal reason.</summary>
    Refused,
}

/// <summary>The recipients of a chat line.</summary>
public enum ChatChannel : byte
{
    /// <summary>Every admitted player.</summary>
    Everyone,
    /// <summary>Only members of the sender's party.</summary>
    Party,
}

/// <summary>A server refusal suitable for translation by the interface.</summary>
public enum SocialRefusal : byte
{
    /// <summary>No refusal.</summary>
    None,
    /// <summary>The message is invalid.</summary>
    Malformed,
    /// <summary>The cleaned line is empty.</summary>
    Empty,
    /// <summary>The line exceeds a content limit.</summary>
    TooLong,
    /// <summary>The operation requires a party.</summary>
    NotInParty,
    /// <summary>The player already belongs to a party.</summary>
    AlreadyInParty,
    /// <summary>The target already has a pending invitation.</summary>
    AlreadyInvited,
    /// <summary>The party has reached its content limit.</summary>
    PartyFull,
    /// <summary>Only the leader can perform this operation.</summary>
    NotLeader,
    /// <summary>The target is not connected.</summary>
    UnknownPlayer,
    /// <summary>The target is the requesting player; use Leave to leave a party.</summary>
    SelfInvite,
    /// <summary>There is no pending invitation.</summary>
    NoInvitation,
    /// <summary>The pending invitation expired.</summary>
    Expired,
    /// <summary>The target is not in this party.</summary>
    NotMember,
    /// <summary>The reply names an invitation that is no longer pending.</summary>
    StaleInvitation,
}

/// <summary>Inline UTF-16 storage; copying a message never shares mutable text.</summary>
[InlineArray(256)]
public struct SocialText
{
    private char _first;
}

/// <summary>A bounded reliable social envelope. Requests never carry trusted sender identity.</summary>
public struct SocialMessage
{
    /// <summary>Maximum encoded envelope, including the message id and UTF-8 length prefix.</summary>
    public const int MaxWireSize = 48 + 2 + 768;
    /// <summary>Fixed envelope bytes before the text length prefix.</summary>
    public const int FixedWireSize = 48;
    /// <summary>Operation or event.</summary>
    public SocialKind Kind;
    /// <summary>Target on requests; authoritative player id on events.</summary>
    public uint Player;
    /// <summary>Party leader on membership events.</summary>
    public uint Leader;
    /// <summary>Server tick or invitation expiry tick.</summary>
    public long Tick;
    /// <summary>Unique server-issued invitation identity, echoed by Accept and Decline; zero means none.</summary>
    public ulong InvitationId;
    /// <summary>Channel, connected flag, design index or refusal, according to kind.</summary>
    public byte Value;
    /// <summary>Party telemetry is outside personal coverage.</summary>
    public bool Unknown;
    /// <summary>Shield fraction.</summary>
    public float Shield;
    /// <summary>Hull fraction.</summary>
    public float Hull;
    /// <summary>Sector coordinates.</summary>
    public int X, Y, Z;
    /// <summary>Number of UTF-16 code units in Text.</summary>
    public int Length;
    /// <summary>Bounded text storage.</summary>
    public SocialText Text;

    /// <summary>Copies text if it fits; never truncates a submitted chat line.</summary>
    public bool SetText(ReadOnlySpan<char> text)
    {
        if (text.Length > 256) return false;
        text.CopyTo(Text);
        Length = text.Length;
        return true;
    }

    /// <summary>Writes an envelope into MaxWireSize bytes.</summary>
    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Social);
        writer.WriteByte((byte)Kind);
        writer.WriteUInt32(Player);
        writer.WriteUInt32(Leader);
        writer.WriteInt64(Tick);
        writer.WriteUInt64(InvitationId);
        writer.WriteByte(Value);
        writer.WriteByte(Unknown ? (byte)1 : (byte)0);
        writer.WriteSingle(Shield);
        writer.WriteSingle(Hull);
        writer.WriteInt32(X); writer.WriteInt32(Y); writer.WriteInt32(Z);
        writer.WriteString(Text[..Length]);
    }

    /// <summary>Reads transactionally with bounded storage, including hostile lengths and trailing bytes.</summary>
    public static bool TryRead(ref NetReader reader, out SocialMessage message)
    {
        message = default;
        message.Kind = (SocialKind)reader.ReadByte();
        message.Player = reader.ReadUInt32(); message.Leader = reader.ReadUInt32();
        message.Tick = reader.ReadInt64(); message.InvitationId = reader.ReadUInt64(); message.Value = reader.ReadByte();
        var unknown = reader.ReadByte();
        if (unknown > 1) return false;
        message.Unknown = unknown != 0;
        message.Shield = reader.ReadSingle(); message.Hull = reader.ReadSingle();
        message.X = reader.ReadInt32(); message.Y = reader.ReadInt32(); message.Z = reader.ReadInt32();
        message.Length = reader.ReadString(message.Text, 768);
        return reader.IsValid && reader.Remaining == 0 && message.Kind <= SocialKind.Refused
            && float.IsFinite(message.Shield) && float.IsFinite(message.Hull);
    }

    /// <summary>Removes control and Unicode formatting characters and trims whitespace without allocating.</summary>
    public SocialRefusal CleanChat(int chars, int bytes)
    {
        if (Length > chars || Encoding.UTF8.GetByteCount(Text[..Length]) > bytes) return SocialRefusal.TooLong;
        var n = 0;
        foreach (var rune in ((ReadOnlySpan<char>)Text[..Length]).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not UnicodeCategory.Control and not UnicodeCategory.Format)
                n += rune.EncodeToUtf16(Text[n..]);
        }
        var trimmed = ((ReadOnlySpan<char>)Text[..n]).Trim();
        trimmed.CopyTo(Text); Length = trimmed.Length;
        return Length == 0 ? SocialRefusal.Empty : SocialRefusal.None;
    }
}
