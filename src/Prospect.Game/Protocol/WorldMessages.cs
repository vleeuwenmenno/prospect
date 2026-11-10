using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>One simulated tick of a client's hull as sent to the server.</summary>
public struct InputRecord
{
    /// <summary>The client's tick number for this input; strictly increasing.</summary>
    public uint Sequence;
    public FlightInput Input;

    /// <summary>The client's order counter in force during this tick.</summary>
    public ushort OrderId;
}

/// <summary>
/// Client to server, every tick, unreliable. Carries the newest inputs with a little history so a lost packet
/// costs nothing, plus the definition of the client's current order.
/// </summary>
public static class InputMessage
{
    public const int MaxRecords = 4;

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<InputRecord> records, ushort orderId, in HullOrder order)
    {
        writer.WriteByte((byte)MessageId.Input);
        writer.WriteByte((byte)records.Length);
        foreach (var record in records)
        {
            writer.WriteUInt32(record.Sequence);
            record.Input.Write(ref writer);
            writer.WriteUInt16(record.OrderId);
        }

        writer.WriteUInt16(orderId);
        writer.WriteByte((byte)order.Kind);
        if (order.Kind == OrderKind.MoveTo)
            WorldWire.WritePosition(ref writer, order.Target);
    }

    public static bool TryRead(ref NetReader reader, scoped Span<InputRecord> records, out int count, out ushort orderId, out HullOrder order)
    {
        count = reader.ReadByte();
        orderId = 0;
        order = default;
        if (count > MaxRecords)
            return false;

        for (var i = 0; i < count; i++)
        {
            records[i].Sequence = reader.ReadUInt32();
            records[i].Input = FlightInput.Read(ref reader);
            records[i].OrderId = reader.ReadUInt16();
        }

        orderId = reader.ReadUInt16();
        var kind = reader.ReadByte();
        if (kind > (byte)OrderKind.MoveTo)
            return false;
        order.Kind = (OrderKind)kind;
        if (order.Kind == OrderKind.MoveTo && !WorldWire.TryReadPosition(ref reader, out order.Target))
            return false;
        return reader.IsValid;
    }
}

/// <summary>A hull as it appears in a snapshot.</summary>
public struct HullRecord
{
    public const int MaxWireSize = 4 + 4 + 24 + 12 + 16 + 12 + 4 + 1 + 16 + 24;

    public uint Id;
    public uint Owner;
    public HullState State;
    public HullOrder Order;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteUInt32(Id);
        writer.WriteUInt32(Owner);
        WorldWire.WritePosition(ref writer, State.Position);
        WorldWire.WriteVector(ref writer, State.Velocity);
        writer.WriteSingle(State.Orientation.X);
        writer.WriteSingle(State.Orientation.Y);
        writer.WriteSingle(State.Orientation.Z);
        writer.WriteSingle(State.Orientation.W);
        WorldWire.WriteVector(ref writer, State.AngularVelocity);
        writer.WriteSingle(State.Heat);
        writer.WriteByte((byte)((State.BoostLocked ? 1 : 0) | (Order.Kind == OrderKind.MoveTo ? 2 : 0)));
        writer.WriteSingle(State.Energy);
        writer.WriteSingle(State.Shield);
        writer.WriteSingle(State.Hull);
        writer.WriteSingle(State.ShieldCooldown);
        if (Order.Kind == OrderKind.MoveTo)
            WorldWire.WritePosition(ref writer, Order.Target);
    }

    public static bool TryRead(ref NetReader reader, out HullRecord record)
    {
        record = default;
        record.Id = reader.ReadUInt32();
        record.Owner = reader.ReadUInt32();
        var ok = WorldWire.TryReadPosition(ref reader, out record.State.Position);
        ok &= WorldWire.TryReadVector(ref reader, out record.State.Velocity);
        var q = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var lengthSquared = q.LengthSquared();
        ok &= float.IsFinite(lengthSquared) && lengthSquared > 0.5f && lengthSquared < 2f;
        record.State.Orientation = ok ? Quaternion.Normalize(q) : Quaternion.Identity;
        ok &= WorldWire.TryReadVector(ref reader, out record.State.AngularVelocity);
        record.State.Heat = Math.Clamp(reader.ReadSingle(), 0f, 1f);
        var flags = reader.ReadByte();
        record.State.BoostLocked = (flags & 1) != 0;
        record.State.Energy = reader.ReadSingle();
        record.State.Shield = reader.ReadSingle();
        record.State.Hull = reader.ReadSingle();
        record.State.ShieldCooldown = reader.ReadSingle();
        ok &= float.IsFinite(record.State.Energy) && float.IsFinite(record.State.Shield) && float.IsFinite(record.State.Hull) && float.IsFinite(record.State.ShieldCooldown);
        if ((flags & 2) != 0)
        {
            record.Order.Kind = OrderKind.MoveTo;
            ok &= WorldWire.TryReadPosition(ref reader, out record.Order.Target);
        }

        return ok && reader.IsValid;
    }
}

/// <summary>
/// Server to client, unreliable: the header of a world snapshot. It is followed by <see cref="HullCount"/> hull
/// records.
/// </summary>
public struct SnapshotHeader
{
    public long ServerTick;

    /// <summary>The last input of the receiving client that the server has simulated.</summary>
    public uint AckedInput;

    /// <summary>The receiving client's own hull.</summary>
    public uint OwnHull;
    public byte HullCount;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Snapshot);
        writer.WriteInt64(ServerTick);
        writer.WriteUInt32(AckedInput);
        writer.WriteUInt32(OwnHull);
        writer.WriteByte(HullCount);
    }

    public static bool TryRead(ref NetReader reader, out SnapshotHeader header)
    {
        header.ServerTick = reader.ReadInt64();
        header.AckedInput = reader.ReadUInt32();
        header.OwnHull = reader.ReadUInt32();
        header.HullCount = reader.ReadByte();
        return reader.IsValid;
    }
}

internal static class WorldWire
{
    /// <summary>No coordinate in the galaxy is anywhere near this large; anything beyond it is corrupt or hostile.</summary>
    private const double MaxCoordinate = 1e13;

    public static void WritePosition(ref NetWriter writer, Double3 value)
    {
        writer.WriteDouble(value.X);
        writer.WriteDouble(value.Y);
        writer.WriteDouble(value.Z);
    }

    public static bool TryReadPosition(ref NetReader reader, out Double3 value)
    {
        value = new Double3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        return Math.Abs(value.X) < MaxCoordinate && Math.Abs(value.Y) < MaxCoordinate && Math.Abs(value.Z) < MaxCoordinate;
    }

    public static void WriteVector(ref NetWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    public static bool TryReadVector(ref NetReader reader, out Vector3 value)
    {
        value = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
