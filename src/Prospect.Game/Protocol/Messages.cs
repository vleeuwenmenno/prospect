using Prospect.Engine.Net;

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
}

public enum RejectReason : byte
{
    VersionMismatch = 1,
    ServerFull = 2,
    BadHandshake = 3,
}

public static class ProtocolInfo
{
    /// <summary>Bumped whenever the wire format changes. Client and server must match exactly.</summary>
    public const uint Version = 3;

    public const ushort DefaultPort = 27420;
    public const int MaxNameBytes = 48;
    public const int MaxNameChars = 24;
}

public struct HelloMessage
{
    public uint ProtocolVersion;
    public string PlayerName;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Hello);
        writer.WriteUInt32(ProtocolVersion);
        writer.WriteString(PlayerName);
    }

    public static bool TryRead(ref NetReader reader, out HelloMessage message)
    {
        Span<char> name = stackalloc char[ProtocolInfo.MaxNameBytes];
        message.ProtocolVersion = reader.ReadUInt32();
        var length = reader.ReadString(name, ProtocolInfo.MaxNameBytes);
        message.PlayerName = reader.IsValid ? PlayerNames.Sanitize(name[..length]) : "";
        return reader.IsValid;
    }
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

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Welcome);
        writer.WriteUInt32(HullId);
        writer.WriteUInt32(PlayerId);
        writer.WriteUInt16(TickRate);
        writer.WriteInt64(ServerTick);
        writer.WriteUInt64(GalaxySeed);
        writer.WriteString(ServerName);
    }

    public static bool TryRead(ref NetReader reader, out WelcomeMessage message)
    {
        Span<char> name = stackalloc char[ProtocolInfo.MaxNameBytes];
        message.HullId = reader.ReadUInt32();
        message.PlayerId = reader.ReadUInt32();
        message.TickRate = reader.ReadUInt16();
        message.ServerTick = reader.ReadInt64();
        message.GalaxySeed = reader.ReadUInt64();
        var length = reader.ReadString(name, ProtocolInfo.MaxNameBytes);
        message.ServerName = reader.IsValid ? PlayerNames.Sanitize(name[..length]) : "";
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
        var count = 0;
        foreach (var c in raw.Trim())
        {
            if (count == clean.Length)
                break;
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'')
                clean[count++] = c;
        }

        var result = clean[..count].Trim();
        return result.IsEmpty ? "Prospector" : result.ToString();
    }
}
