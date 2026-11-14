using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>The first byte of every message.</summary>
public enum MessageId : byte
{
    /// <summary>Client to server: the first message on a connection.</summary>
    Hello = 1,

    /// <summary>Server to client: the connection is accepted.</summary>
    Welcome = 2,

    /// <summary>Server to client: the connection is refused; the server closes it afterwards.</summary>
    Reject = 3,

    /// <summary>Client to server: a clock probe.</summary>
    Ping = 4,

    /// <summary>Server to client: the answer to a probe.</summary>
    Pong = 5,

    /// <summary>Client to server: the pilot's inputs for recent ticks.</summary>
    Input = 6,

    /// <summary>Server to client: the state of the hulls around the client.</summary>
    Snapshot = 7,

    /// <summary>Client to server, reliable: standing orders for hulls the player owns but is not flying.</summary>
    FleetOrders = 8,

    /// <summary>Server to client, reliable: asteroids whose motion changed, or all displaced ones on joining.</summary>
    RockDeltas = 9,

    /// <summary>
    /// Client to server, reliable: take direct control of a hull the player owns, or of a new drone when the hull
    /// id is zero.
    /// </summary>
    Control = 10,

    /// <summary>
    /// Server to client, reliable: the answer to a hello when the server has a password. Carries the server's
    /// salt and a fresh random challenge; see <see cref="Passwords"/>.
    /// </summary>
    Challenge = 11,

    /// <summary>Client to server, reliable: the proof that the client knows the password, for one challenge.</summary>
    Proof = 12,

    /// <summary>Reliable social requests and authoritative social events.</summary>
    Social = 13,
    /// <summary>Applied block revision, or a request for a complete block set.</summary>
    BlockAck = 14,
    /// <summary>A bounded chunk of a complete alive block set, on the bulk channel.</summary>
    BlockState = 15,
    /// <summary>Bounded visual state using the block transfer envelope.</summary>
    VisualState = 26,
    /// <summary>Admission catalogue or canonical design chunk on the shared bulk lane.</summary>
    DesignData = 16,
    /// <summary>Admission request for a missing canonical design.</summary>
    DesignRequest = 17,
    /// <summary>The client has validated the complete admission catalogue.</summary>
    CatalogueReady = 18,

    /// <summary>Client to server, reliable: begin or cancel an edit of the hull the player flies, or commit a design.</summary>
    Editor = 19,

    /// <summary>Client to server, on the bulk channel: the next bytes of a committed design.</summary>
    EditorData = 20,

    /// <summary>Server to client, reliable: where the client's edit stands.</summary>
    EditorState = 21,

    /// <summary>Server to client, reliable: a design was added to the catalogue after admission.</summary>
    DesignNotice = 22,

    /// <summary>Client to server, reliable: the client can show hulls of an announced design, or cannot.</summary>
    DesignReady = 23,
    /// <summary>Request a sector scan.</summary>
    Scan = 27,
    /// <summary>Personal scan progress and charts.</summary>
    Exploration = 28,
    /// <summary>Personal visibility and observed destruction.</summary>
    Visibility = 29,
    /// <summary>Client to server: plot the controlled hull's jump destination without starting.</summary>
    PlotJump = 30,
    /// <summary>Server to owner: accepted navigation plot and jump refusal feedback.</summary>
    Navigation = 31,
    /// <summary>Client to server: cancel the sender's running sector scan.</summary>
    CancelScan = 32,
    /// <summary>Client to server: start a module operation on the controlled hull.</summary>
    ModuleScan = 33,
    /// <summary>Server to owner: module progress, refusals and retained observations.</summary>
    ModuleSurvey = 34,
}

public enum RejectReason : byte
{
    VersionMismatch = 1,
    ServerFull = 2,
    BadHandshake = 3,

    /// <summary>The proof did not match: the password is wrong.</summary>
    WrongPassword = 4,

    /// <summary>
    /// Too many wrong passwords came from this address, or it is opening connections faster than the server
    /// takes them, or the server has too many connections waiting to be let in; the message says how long to wait.
    /// </summary>
    TooManyAttempts = 5,

    /// <summary>
    /// The client's content is not the server's: the same version of the game with other blocks, rules, designs
    /// or galaxy rules; see <see cref="ContentIdentity"/>.
    /// </summary>
    ContentMismatch = 6,
}

/// <summary>Who can find and join a game.</summary>
public enum ServerVisibility : byte
{
    /// <summary>The host alone. No network listener is opened and nobody can join.</summary>
    Private = 0,

    /// <summary>Joined by address only. Listens on the network; never announced.</summary>
    Unlisted = 1,

    /// <summary>Listens on the network and announces itself to the listing server, so it shows in the server browser.</summary>
    Public = 2,
}

/// <summary>The names a visibility goes by on a command line, in a file and on screen.</summary>
public static class Visibilities
{
    /// <summary>Reads <c>public</c>, <c>unlisted</c> or <c>private</c>, in any case.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out ServerVisibility visibility)
    {
        text = text.Trim();
        if (text.Equals("public", StringComparison.OrdinalIgnoreCase))
            visibility = ServerVisibility.Public;
        else if (text.Equals("unlisted", StringComparison.OrdinalIgnoreCase))
            visibility = ServerVisibility.Unlisted;
        else if (text.Equals("private", StringComparison.OrdinalIgnoreCase))
            visibility = ServerVisibility.Private;
        else
        {
            visibility = default;
            return false;
        }

        return true;
    }

    /// <summary>The visibility in lower case, as <see cref="TryParse"/> reads it.</summary>
    public static string Name(ServerVisibility visibility) =>
        visibility switch
        {
            ServerVisibility.Public => "public",
            ServerVisibility.Unlisted => "unlisted",
            _ => "private",
        };
}

public static class ProtocolInfo
{
    /// <summary>
    /// Bumped whenever the wire format changes, and whenever world generation does: an asteroid travels as its
    /// index in a list both ends generate for themselves, so two builds that generate differently would mean
    /// different rocks by the same number. Client and server must match exactly.
    /// </summary>
    public const uint Version = 48;

    public const ushort DefaultPort = 27420;
    public const int MaxNameBytes = 48;
    public const int MaxNameChars = 24;

    /// <summary>The game's version as text (<c>0.1.0</c>), which a public server tells the listing server.</summary>
    public static string GameVersion { get; } = typeof(ProtocolInfo).Assembly.GetName().Version?.ToString(3) ?? "0";
}

/// <summary>
/// The first message on a connection: the protocol version, the player's name and the identity of the client's
/// content. The version stays first in every version of the protocol, so two builds can always tell each other
/// that they differ.
/// </summary>
public struct HelloMessage
{
    public uint ProtocolVersion;
    public string PlayerName;

    /// <summary>What the client's shared content comes to. The server lets in only its own.</summary>
    public ContentIdentity Content;

    /// <summary>
    /// The most bytes a hello takes: the message id, the version, the name with its one-byte length and the
    /// content identity.
    /// </summary>
    public const int MaxWireSize = 1 + 4 + 1 + ProtocolInfo.MaxNameBytes + ContentIdentity.WireSize;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Hello);
        writer.WriteUInt32(ProtocolVersion);
        writer.WriteString(PlayerName);
        Content.Write(ref writer);
    }

    /// <summary>
    /// Reads the first field of a hello, the protocol version, and nothing else. A server reads a hello in
    /// steps so that a connection it is going to refuse anyway costs it no memory: the name is read, with
    /// <see cref="TryReadName"/>, only once everything that can be checked without it has passed, and the
    /// content identity, with <see cref="TryReadContent"/>, after the name.
    /// </summary>
    public static bool TryReadVersion(ref NetReader reader, out uint version)
    {
        version = reader.ReadUInt32();
        return reader.IsValid;
    }

    /// <summary>
    /// Reads the second field of a hello after <see cref="TryReadVersion"/>: the player's name, cleaned as
    /// <see cref="PlayerNames.Sanitize(ReadOnlySpan{char}, Span{char})"/> cleans it, into <paramref name="name"/>,
    /// which must hold <see cref="ProtocolInfo.MaxNameChars"/> characters. Allocates nothing.
    /// </summary>
    public static bool TryReadName(ref NetReader reader, Span<char> name, out int length)
    {
        Span<char> raw = stackalloc char[ProtocolInfo.MaxNameBytes];
        var read = reader.ReadString(raw, ProtocolInfo.MaxNameBytes);
        length = reader.IsValid ? PlayerNames.Sanitize(raw[..read], name) : 0;
        return reader.IsValid;
    }

    /// <summary>
    /// Reads the last field of a hello after <see cref="TryReadName"/>: the identity of the client's content.
    /// False when the hello ends before it does. Allocates nothing.
    /// </summary>
    public static bool TryReadContent(ref NetReader reader, out ContentIdentity content) => ContentIdentity.TryRead(ref reader, out content);
}

public struct WelcomeMessage
{
    public uint PlayerId;
    public ushort TickRate;
    public long ServerTick;
    public ulong GalaxySeed;
    public string ServerName;

    /// <summary>The hull the player controls.</summary>
    public uint HullId;

    /// <summary>The settings of the server's world, which the client plays by from here on.</summary>
    public WorldSettings Settings;

    /// <summary>Whether the game is public, unlisted or private, for display.</summary>
    public ServerVisibility Visibility;

    /// <summary>True when joining over the network takes a password.</summary>
    public bool PasswordProtected;

    /// <summary>How many players the server has room for.</summary>
    public ushort MaxPlayers;

    /// <summary>
    /// The most bytes a welcome takes: the fixed fields, the server's name with its two-byte length, the
    /// visibility, the password flag, the player slots and the world's settings.
    /// </summary>
    public const int MaxWireSize = 1 + 4 + 4 + 2 + 8 + 8 + 2 + ServerNames.MaxBytes + 1 + 1 + 2 + WorldSettingCatalog.MaxWireSize;

    /// <summary>Writes the message. The settings go out by id, as <paramref name="catalog"/> names them.</summary>
    public readonly void Write(ref NetWriter writer, WorldSettingCatalog catalog)
    {
        writer.WriteByte((byte)MessageId.Welcome);
        writer.WriteUInt32(HullId);
        writer.WriteUInt32(PlayerId);
        writer.WriteUInt16(TickRate);
        writer.WriteInt64(ServerTick);
        writer.WriteUInt64(GalaxySeed);
        writer.WriteString(ServerName);
        writer.WriteByte((byte)Visibility);
        writer.WriteBool(PasswordProtected);
        writer.WriteUInt16(MaxPlayers);
        catalog.Write(ref writer, Settings);
    }

    /// <summary>
    /// Reads the message. The settings are matched by id against <paramref name="catalog"/> and brought into
    /// range, so whatever the server sent they are settings this game can play by. The server's name is cleaned
    /// as any name from the network is.
    /// </summary>
    public static bool TryRead(ref NetReader reader, WorldSettingCatalog catalog, out WelcomeMessage message)
    {
        Span<char> name = stackalloc char[ServerNames.MaxBytes];
        message.HullId = reader.ReadUInt32();
        message.PlayerId = reader.ReadUInt32();
        message.TickRate = reader.ReadUInt16();
        message.ServerTick = reader.ReadInt64();
        message.GalaxySeed = reader.ReadUInt64();
        var length = reader.ReadString(name, ServerNames.MaxBytes);
        message.ServerName = reader.IsValid ? ServerNames.Clean(name[..length]) : "";
        var visibility = reader.ReadByte();
        message.Visibility = visibility <= (byte)ServerVisibility.Public ? (ServerVisibility)visibility : ServerVisibility.Unlisted;
        message.PasswordProtected = reader.ReadBool();
        message.MaxPlayers = reader.ReadUInt16();
        message.Settings = catalog.Read(ref reader);
        return reader.IsValid;
    }
}

/// <summary>The server's refusal of a connection, and for a refusal that passes, how long to wait.</summary>
public struct RejectMessage
{
    public RejectReason Reason;

    /// <summary>With <see cref="RejectReason.TooManyAttempts"/>: seconds until this address may try again.</summary>
    public ushort RetryAfterSeconds;

    public const int WireSize = 1 + 1 + 2;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Reject);
        writer.WriteByte((byte)Reason);
        writer.WriteUInt16(RetryAfterSeconds);
    }

    /// <summary>
    /// Reads a refusal. The reason is the first byte in every version of the protocol, so a server of another
    /// version can still say that the versions differ; the wait is read only if it is there.
    /// </summary>
    public static bool TryRead(ref NetReader reader, out RejectMessage message)
    {
        message.Reason = (RejectReason)reader.ReadByte();
        message.RetryAfterSeconds = reader.Remaining >= 2 ? reader.ReadUInt16() : (ushort)0;
        return reader.IsValid;
    }
}

/// <summary>
/// The server's answer to a hello when it has a password: its salt and a random challenge made for this one
/// connection. <see cref="Passwords"/> says what the client does with them.
/// </summary>
public static class ChallengeMessage
{
    public const int WireSize = 1 + Passwords.SaltSize + Passwords.ChallengeSize;

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<byte> salt, scoped ReadOnlySpan<byte> challenge)
    {
        writer.WriteByte((byte)MessageId.Challenge);
        writer.WriteBytes(salt[..Passwords.SaltSize]);
        writer.WriteBytes(challenge[..Passwords.ChallengeSize]);
    }

    /// <summary>Reads the message into the two spans. False unless it is exactly a salt and a challenge.</summary>
    public static bool TryRead(ref NetReader reader, scoped Span<byte> salt, scoped Span<byte> challenge)
    {
        if (reader.Remaining != Passwords.SaltSize + Passwords.ChallengeSize)
            return false;
        reader.ReadBytes(Passwords.SaltSize).CopyTo(salt);
        reader.ReadBytes(Passwords.ChallengeSize).CopyTo(challenge);
        return reader.IsValid;
    }
}

/// <summary>The client's proof that it knows the password, good for the one challenge it answers.</summary>
public static class ProofMessage
{
    public const int WireSize = 1 + Passwords.ProofSize;

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<byte> proof)
    {
        writer.WriteByte((byte)MessageId.Proof);
        writer.WriteBytes(proof[..Passwords.ProofSize]);
    }

    /// <summary>Reads the proof into <paramref name="proof"/>. False unless the message is exactly one proof.</summary>
    public static bool TryRead(ref NetReader reader, scoped Span<byte> proof)
    {
        if (reader.Remaining != Passwords.ProofSize)
            return false;
        reader.ReadBytes(Passwords.ProofSize).CopyTo(proof);
        return reader.IsValid;
    }
}

public struct PingMessage
{
    /// <summary>The client's clock when it sent the probe, echoed back unchanged.</summary>
    public long ClientTimestamp;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Ping);
        writer.WriteInt64(ClientTimestamp);
    }

    public static bool TryRead(ref NetReader reader, out PingMessage message)
    {
        message.ClientTimestamp = reader.ReadInt64();
        return reader.IsValid;
    }
}

public struct PongMessage
{
    public long ClientTimestamp;
    public long ServerTick;
    public ushort PlayerCount;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Pong);
        writer.WriteInt64(ClientTimestamp);
        writer.WriteInt64(ServerTick);
        writer.WriteUInt16(PlayerCount);
    }

    public static bool TryRead(ref NetReader reader, out PongMessage message)
    {
        message.ClientTimestamp = reader.ReadInt64();
        message.ServerTick = reader.ReadInt64();
        message.PlayerCount = reader.ReadUInt16();
        return reader.IsValid;
    }
}

public static class PlayerNames
{
    /// <summary>
    /// Reduces a name received from the network or typed by the player to printable characters of a bounded
    /// length, so it is safe to show in any UI and log.
    /// </summary>
    public static string Sanitize(ReadOnlySpan<char> raw)
    {
        Span<char> clean = stackalloc char[ProtocolInfo.MaxNameChars];
        return clean[..Sanitize(raw, clean)].ToString();
    }

    /// <summary>
    /// The same, into <paramref name="clean"/>, which must hold <see cref="ProtocolInfo.MaxNameChars"/>
    /// characters; returns how many were written. Allocates nothing, so a server can clean a name it has not yet
    /// decided to keep.
    /// </summary>
    public static int Sanitize(ReadOnlySpan<char> raw, Span<char> clean)
    {
        clean = clean[..ProtocolInfo.MaxNameChars];
        var count = 0;
        foreach (var c in raw.Trim())
        {
            if (count == clean.Length)
                break;
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'')
                clean[count++] = c;
        }

        // Cutting can leave a space at the end, and removing characters one at the start.
        var kept = clean[..count];
        var start = count - kept.TrimStart().Length;
        var length = kept.Trim().Length;
        if (length == 0)
        {
            Unnamed.CopyTo(clean);
            return Unnamed.Length;
        }

        kept.Slice(start, length).CopyTo(clean);
        return length;
    }

    private const string Unnamed = "Prospector";
}
