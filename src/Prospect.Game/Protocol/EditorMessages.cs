using System.Security.Cryptography;
using Prospect.Engine.Net;

namespace Prospect.Game.Protocol;

/// <summary>Where an edit of the hull a player flies stands, as the server decides it.</summary>
public enum EditorPhase : byte
{
    /// <summary>No edit. The reason says why the last one ended or why a request to begin was refused.</summary>
    Closed = 0,

    /// <summary>The server allows the hull to be edited and a design to be committed.</summary>
    Open = 1,

    /// <summary>A commit was accepted and its design is being sent to the server.</summary>
    Receiving = 2,

    /// <summary>The design has arrived and the server is checking it.</summary>
    Checking = 3,

    /// <summary>The design is in the server's catalogue and the clients are getting ready to show it.</summary>
    Publishing = 4,

    /// <summary>The hull was replaced by one of the new design, and the edit is over.</summary>
    Applied = 5,
}

/// <summary>Why an edit was refused or ended, or why a commit was turned down while the edit stays open.</summary>
public enum EditorReason : byte
{
    None = 0,

    /// <summary>The hull named is not the living hull the player flies.</summary>
    NotPiloting = 1,

    /// <summary>The hull is in a jump, or has been ordered to make one.</summary>
    Jumping = 2,

    /// <summary>The hull took damage too recently; the wait says how long until it may be edited.</summary>
    Quiet = 3,

    /// <summary>The player cancelled.</summary>
    Cancelled = 4,

    /// <summary>The hull took damage while it was being edited.</summary>
    Damaged = 5,

    /// <summary>The player left the hull, or it was destroyed.</summary>
    HullChanged = 6,

    /// <summary>The server is checking as many designs as it holds at once. The edit stays open; commit again.</summary>
    Busy = 7,

    /// <summary>The commit or repair follows the last one too closely; the wait says how long. The edit stays open.</summary>
    TooSoon = 8,

    /// <summary>The design is larger than a design may be. The edit stays open.</summary>
    TooLarge = 9,

    /// <summary>The design did not arrive as it was announced. The edit stays open.</summary>
    UploadFailed = 10,

    /// <summary>The design did not arrive in time. The edit stays open.</summary>
    UploadTimedOut = 11,

    /// <summary>The design is not one the server accepts. The edit stays open.</summary>
    Invalid = 12,

    /// <summary>The design's name, class, block size or colour is not the edited hull's. The edit stays open.</summary>
    WrongKind = 13,

    /// <summary>
    /// The design is the one the hull already has, or a repair was asked for a hull that has lost no block and
    /// no hull points. The edit stays open.
    /// </summary>
    NoChange = 14,

    /// <summary>The server's design catalogue has no room for another design. The edit stays open.</summary>
    CatalogueFull = 15,

    /// <summary>The player's own client was not ready to show the design in time.</summary>
    ClientNotReady = 16,

    /// <summary>The player's own client could not prepare the design.</summary>
    ClientFailed = 17,

    /// <summary>The world has no room for another hull.</summary>
    HullLimit = 18,

    /// <summary>
    /// The hull has lost blocks or hull points: it must be repaired before a design is committed for it. The
    /// edit stays open, and a repair is still taken.
    /// </summary>
    RepairFirst = 19,

    /// <summary>Set by the client alone, never sent: the session cannot take the request now.</summary>
    Unavailable = 20,
}

/// <summary>What a client asks of the editor, reliable. The server answers every one with an <see cref="EditorStateMessage"/>.</summary>
public enum EditorRequestKind : byte
{
    /// <summary>Begin editing the hull named, which must be the one the player flies.</summary>
    Begin = 0,

    /// <summary>End the edit the token names, whatever stage it is in.</summary>
    Cancel = 1,

    /// <summary>Announce a design for the edit the token names: its size and the SHA-256 of its canonical bytes.</summary>
    Commit = 2,

    /// <summary>
    /// Rebuild the hull of the edit the token names as its own design: every block back, and whole. Nothing is
    /// sent but the token; the server has the design.
    /// </summary>
    Repair = 3,
}

/// <summary>Client to server, reliable: one editor request.</summary>
public struct EditorRequestMessage
{
    /// <summary>Message id, kind, token, size and hash: the longest request, a commit.</summary>
    public const int MaxWireSize = 1 + 1 + 4 + 4 + SHA256.HashSizeInBytes;

    public EditorRequestKind Kind;

    /// <summary>With <see cref="EditorRequestKind.Begin"/>: the hull to edit.</summary>
    public uint Hull;

    /// <summary>With cancel, commit and repair: the edit, as the server named it when it allowed it.</summary>
    public uint Token;

    /// <summary>With commit: how many canonical bytes the design is.</summary>
    public int Total;

    public readonly void Write(ref NetWriter writer, scoped ReadOnlySpan<byte> hash)
    {
        writer.WriteByte((byte)MessageId.Editor);
        writer.WriteByte((byte)Kind);
        writer.WriteUInt32(Kind == EditorRequestKind.Begin ? Hull : Token);
        if (Kind != EditorRequestKind.Commit)
            return;
        writer.WriteInt32(Total);
        writer.WriteBytes(hash[..SHA256.HashSizeInBytes]);
    }

    /// <summary>Reads a request; a commit's hash goes into <paramref name="hash"/>. False unless it is exactly one request.</summary>
    public static bool TryRead(ref NetReader reader, scoped Span<byte> hash, out EditorRequestMessage message)
    {
        message = default;
        var kind = reader.ReadByte();
        var value = reader.ReadUInt32();
        if (!reader.IsValid || kind > (byte)EditorRequestKind.Repair)
            return false;
        message.Kind = (EditorRequestKind)kind;
        if (message.Kind == EditorRequestKind.Begin)
            message.Hull = value;
        else
            message.Token = value;
        if (message.Kind == EditorRequestKind.Commit)
        {
            message.Total = reader.ReadInt32();
            if (reader.Remaining != SHA256.HashSizeInBytes)
                return false;
            reader.ReadBytes(SHA256.HashSizeInBytes).CopyTo(hash);
        }

        return reader.IsValid && reader.Remaining == 0;
    }
}

/// <summary>
/// Client to server, on the bulk channel: the next bytes of a committed design. The server takes a chunk only at
/// exactly the offset it has reached, for the edit and the size the commit announced.
/// </summary>
public static class EditorDataMessage
{
    /// <summary>Message id, token, offset and count.</summary>
    public const int HeaderBytes = 1 + 4 + 4 + 2;

    /// <summary>The most design bytes in one chunk.</summary>
    public const int MaxChunk = 1024;

    public static void Write(ref NetWriter writer, uint token, int offset, scoped ReadOnlySpan<byte> bytes)
    {
        writer.WriteByte((byte)MessageId.EditorData);
        writer.WriteUInt32(token);
        writer.WriteInt32(offset);
        writer.WriteUInt16((ushort)bytes.Length);
        writer.WriteBytes(bytes);
    }

    /// <summary>Reads a chunk's header. False unless the message is that header and exactly the bytes it counts.</summary>
    public static bool TryReadHeader(ref NetReader reader, out uint token, out int offset, out int count)
    {
        token = reader.ReadUInt32();
        offset = reader.ReadInt32();
        count = reader.ReadUInt16();
        return reader.IsValid && count is >= 1 and <= MaxChunk && reader.Remaining == count;
    }
}

/// <summary>Server to client, reliable: where the client's edit stands. Sent in answer to every request and at every change.</summary>
public struct EditorStateMessage
{
    public const int WireSize = 1 + 1 + 1 + 4 + 4 + 4 + 2;

    public EditorPhase Phase;
    public EditorReason Reason;

    /// <summary>The edit this is about, as the server named it; zero when a request to begin was refused.</summary>
    public uint Token;

    /// <summary>The hull being edited, or with <see cref="EditorPhase.Applied"/> the new hull.</summary>
    public uint Hull;

    /// <summary>With <see cref="EditorPhase.Applied"/>: the hull that was replaced.</summary>
    public uint ReplacedHull;

    /// <summary>With <see cref="EditorReason.Quiet"/> and <see cref="EditorReason.TooSoon"/>: seconds to wait.</summary>
    public ushort WaitSeconds;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.EditorState);
        writer.WriteByte((byte)Phase);
        writer.WriteByte((byte)Reason);
        writer.WriteUInt32(Token);
        writer.WriteUInt32(Hull);
        writer.WriteUInt32(ReplacedHull);
        writer.WriteUInt16(WaitSeconds);
    }

    public static bool TryRead(ref NetReader reader, out EditorStateMessage message)
    {
        message = default;
        var phase = reader.ReadByte();
        var reason = reader.ReadByte();
        message.Token = reader.ReadUInt32();
        message.Hull = reader.ReadUInt32();
        message.ReplacedHull = reader.ReadUInt32();
        message.WaitSeconds = reader.ReadUInt16();
        if (!reader.IsValid || reader.Remaining != 0 || phase > (byte)EditorPhase.Applied || reason > (byte)EditorReason.RepairFirst)
            return false;
        message.Phase = (EditorPhase)phase;
        message.Reason = (EditorReason)reason;
        return true;
    }
}

/// <summary>
/// Server to client, reliable: a design was added to the server's catalogue after the client was let in. Designs
/// are announced in order of their numbers, each once, and never change afterwards.
/// </summary>
public static class DesignNoticeMessage
{
    public const int WireSize = 1 + 1 + 4 + SHA256.HashSizeInBytes;

    public static void Write(ref NetWriter writer, byte design, int size, scoped ReadOnlySpan<byte> hash)
    {
        writer.WriteByte((byte)MessageId.DesignNotice);
        writer.WriteByte(design);
        writer.WriteInt32(size);
        writer.WriteBytes(hash[..SHA256.HashSizeInBytes]);
    }

    public static bool TryRead(ref NetReader reader, out byte design, out int size, scoped Span<byte> hash)
    {
        design = reader.ReadByte();
        size = reader.ReadInt32();
        if (!reader.IsValid || reader.Remaining != SHA256.HashSizeInBytes)
            return false;
        reader.ReadBytes(SHA256.HashSizeInBytes).CopyTo(hash);
        return reader.IsValid;
    }
}

/// <summary>
/// Client to server, reliable: the client can show hulls of an announced design, or cannot. Ready means the design
/// was checked and decoded and its mesh is in the renderer; until then the server sends the client no hull of it.
/// </summary>
public static class DesignReadyMessage
{
    public const int WireSize = 1 + 1 + 1;

    public static void Write(ref NetWriter writer, byte design, bool ready)
    {
        writer.WriteByte((byte)MessageId.DesignReady);
        writer.WriteByte(design);
        writer.WriteBool(ready);
    }

    public static bool TryRead(ref NetReader reader, out byte design, out bool ready)
    {
        design = reader.ReadByte();
        var value = reader.ReadByte();
        ready = value == 1;
        return reader.IsValid && reader.Remaining == 0 && value <= 1;
    }
}
