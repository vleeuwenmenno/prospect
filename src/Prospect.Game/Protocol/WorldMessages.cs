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
        if (order.Kind != OrderKind.None)
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
        if (kind > (byte)OrderKind.Jump)
            return false;
        order.Kind = (OrderKind)kind;
        if (order.Kind != OrderKind.None && !WorldWire.TryReadPosition(ref reader, out order.Target))
            return false;
        return reader.IsValid;
    }
}

/// <summary>An order for one hull, as the command view issues it.</summary>
public struct FleetOrder
{
    public uint Hull;
    public HullOrder Order;
}

/// <summary>Client to server, reliable: orders for hulls the player owns. The server checks ownership.</summary>
public static class FleetOrdersMessage
{
    public const int MaxOrders = 64;

    /// <summary>Message id, count, and 64 hull ids with orders and three double coordinates.</summary>
    public const int MaxWireSize = 2 + MaxOrders * (4 + 1 + 24);

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<FleetOrder> orders)
    {
        writer.WriteByte((byte)MessageId.FleetOrders);
        writer.WriteByte((byte)orders.Length);
        foreach (var order in orders)
        {
            writer.WriteUInt32(order.Hull);
            writer.WriteByte((byte)order.Order.Kind);
            if (order.Order.Kind != OrderKind.None)
                WorldWire.WritePosition(ref writer, order.Order.Target);
        }
    }

    public static bool TryRead(ref NetReader reader, scoped Span<FleetOrder> orders, out int count)
    {
        count = reader.ReadByte();
        if (count > MaxOrders)
            return false;

        for (var i = 0; i < count; i++)
        {
            orders[i].Hull = reader.ReadUInt32();
            var kind = reader.ReadByte();
            if (kind > (byte)OrderKind.Jump)
                return false;
            orders[i].Order = new HullOrder { Kind = (OrderKind)kind };
            if (kind != (byte)OrderKind.None && !WorldWire.TryReadPosition(ref reader, out orders[i].Order.Target))
                return false;
        }

        return reader.IsValid;
    }
}

/// <summary>Server to client, reliable: asteroid motion records. See <see cref="RockDelta"/>.</summary>
public static class RockDeltasMessage
{
    public const int MaxDeltas = 32;
    public const int WireSize = 2 + MaxDeltas * 46;

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<RockDelta> deltas)
    {
        writer.WriteByte((byte)MessageId.RockDeltas);
        writer.WriteByte((byte)deltas.Length);
        foreach (var delta in deltas)
        {
            writer.WriteInt32(delta.Rock.Sector.X);
            writer.WriteInt32(delta.Rock.Sector.Y);
            writer.WriteInt32(delta.Rock.Sector.Z);
            writer.WriteUInt16((ushort)delta.Rock.Index);
            WorldWire.WriteVector(ref writer, delta.Offset);
            WorldWire.WriteVector(ref writer, delta.Velocity);
            writer.WriteInt64(delta.StartTick);
        }
    }

    public static bool TryRead(ref NetReader reader, scoped Span<RockDelta> deltas, out int count)
    {
        count = reader.ReadByte();
        if (count > MaxDeltas)
            return false;

        for (var i = 0; i < count; i++)
        {
            var sector = new SectorCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            deltas[i].Rock = new RockRef(sector, reader.ReadUInt16());
            if (!WorldWire.TryReadVector(ref reader, out deltas[i].Offset) || !WorldWire.TryReadVector(ref reader, out deltas[i].Velocity))
                return false;
            deltas[i].StartTick = reader.ReadInt64();
        }

        return reader.IsValid;
    }
}

/// <summary>A hull as it appears in a snapshot.</summary>
public struct HullRecord
{
    /// <summary>
    /// Id, owner, design, blocks, position, velocity, orientation, spin, heat, flags, the jump's four figures,
    /// energy, shield, hull and shield delay, the shield's newest hit and the order's target.
    /// </summary>
    public const int MaxWireSize = 4 + 4 + 1 + 8 + 24 + 12 + 16 + 12 + 4 + 1 + 4 + 4 + 4 + 8 + 16 + 6 + 24;

    public uint Id;
    public uint Owner;
    public byte Design;

    /// <summary>One bit per block of the design; a cleared bit is a destroyed block.</summary>
    public ulong Blocks;
    public HullState State;
    public HullOrder Order;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteUInt32(Id);
        writer.WriteUInt32(Owner);
        writer.WriteByte(Design);
        writer.WriteUInt64(Blocks);
        WorldWire.WritePosition(ref writer, State.Position);
        WorldWire.WriteVector(ref writer, State.Velocity);
        writer.WriteSingle(State.Orientation.X);
        writer.WriteSingle(State.Orientation.Y);
        writer.WriteSingle(State.Orientation.Z);
        writer.WriteSingle(State.Orientation.W);
        WorldWire.WriteVector(ref writer, State.AngularVelocity);
        writer.WriteSingle(State.Heat);
        writer.WriteByte((byte)((State.BoostLocked ? 1 : 0) | (byte)Order.Kind << 1 | (byte)State.Jump << 3 | (State.Boosting ? 1 << 6 : 0) | (State.ShieldOffline ? 1 << 7 : 0)));
        writer.WriteSingle(State.JumpTimer);
        writer.WriteSingle(State.JumpCalculation);
        writer.WriteSingle(State.JumpTunnelSeconds);
        writer.WriteDouble(State.JumpTunnelElapsed);
        writer.WriteSingle(State.Energy);
        writer.WriteSingle(State.Shield);
        writer.WriteSingle(State.Hull);
        writer.WriteSingle(State.ShieldCooldown);
        writer.WriteByte(State.ShieldHit.Serial);
        writer.WriteByte(State.ShieldHit.Strength);
        writer.WriteByte((byte)State.ShieldHit.X);
        writer.WriteByte((byte)State.ShieldHit.Y);
        writer.WriteByte((byte)State.ShieldHit.Z);
        writer.WriteByte(State.ShieldHit.AgeTicks);
        if (Order.Kind != OrderKind.None)
            WorldWire.WritePosition(ref writer, Order.Target);
    }

    public static bool TryRead(ref NetReader reader, out HullRecord record)
    {
        record = default;
        record.Id = reader.ReadUInt32();
        record.Owner = reader.ReadUInt32();
        record.Design = reader.ReadByte();
        record.Blocks = reader.ReadUInt64();
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
        record.State.Boosting = (flags & 1 << 6) != 0;
        record.State.ShieldOffline = (flags & 1 << 7) != 0;
        record.State.Jump = (JumpPhase)Math.Min(flags >> 3 & 7, (int)JumpPhase.Arrive);
        record.State.JumpTimer = reader.ReadSingle();
        ok &= float.IsFinite(record.State.JumpTimer);
        record.State.JumpCalculation = reader.ReadSingle();
        ok &= float.IsFinite(record.State.JumpCalculation) && record.State.JumpCalculation >= 0;
        record.State.JumpTunnelSeconds = reader.ReadSingle();
        record.State.JumpTunnelElapsed = reader.ReadDouble();
        ok &= float.IsFinite(record.State.JumpTunnelSeconds) && record.State.JumpTunnelSeconds >= 0
            && record.State.JumpTunnelSeconds <= HullSim.MaxJumpTravelSeconds;
        ok &= double.IsFinite(record.State.JumpTunnelElapsed) && record.State.JumpTunnelElapsed >= 0
            && record.State.JumpTunnelElapsed <= record.State.JumpTunnelSeconds + (double)HullSim.Dt;
        if (record.State.Jump is JumpPhase.Align or JumpPhase.Charge or JumpPhase.None)
            ok &= record.State.JumpTunnelElapsed == 0;
        record.State.Energy = reader.ReadSingle();
        record.State.Shield = reader.ReadSingle();
        record.State.Hull = reader.ReadSingle();
        record.State.ShieldCooldown = reader.ReadSingle();
        ok &= float.IsFinite(record.State.Energy) && float.IsFinite(record.State.Shield) && float.IsFinite(record.State.Hull) && float.IsFinite(record.State.ShieldCooldown);
        ok &= record.State.Shield >= 0 && record.State.ShieldCooldown >= 0;
        record.State.ShieldHit.Serial = reader.ReadByte();
        record.State.ShieldHit.Strength = reader.ReadByte();
        record.State.ShieldHit.X = (sbyte)reader.ReadByte();
        record.State.ShieldHit.Y = (sbyte)reader.ReadByte();
        record.State.ShieldHit.Z = (sbyte)reader.ReadByte();
        record.State.ShieldHit.AgeTicks = reader.ReadByte();
        ok &= record.State.ShieldHit.AgeTicks <= ShieldHit.MaxAgeTicks;
        var kind = flags >> 1 & 3;
        ok &= kind <= (int)OrderKind.Jump;
        if (kind != 0 && ok)
        {
            record.Order.Kind = (OrderKind)kind;
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
