using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>Which pool the body a carrying drone holds belongs to: the two are numbered separately and their ids collide.</summary>
public enum DroneCarryKind : byte
{
    /// <summary>A loose container of <see cref="FreeContainers"/>.</summary>
    Container = 0,

    /// <summary>A loose ore chunk of <see cref="OreChunks"/>.</summary>
    Ore = 1,
}

/// <summary>One drone carrying one body: the drone that pulls it and the body it pulls.</summary>
public struct DroneCarry
{
    /// <summary>The carrying drone's packed hull handle.</summary>
    public uint Drone;

    /// <summary>The id of the body it holds, in the pool <see cref="Kind"/> names.</summary>
    public uint Body;

    public DroneCarryKind Kind;
}

/// <summary>
/// Which drones carry what, for the hulls a client may see: one record a drone that pulls a body, so a client can draw
/// the beam from its belly tractor array to the body exactly where the body is drawn. A drone holds one body at a time
/// (<see cref="TaskOrder"/>), and the record names the drone, so no drone appears twice.
/// <para>
/// Sent reliably every six ticks, as loose cargo is, and not at all while nothing is carried: the carriers are not part
/// of a container's or a chunk's record, which are fixed-size for a whole set and would pay for the field whether
/// anything is carried or not. Not saved: a loaded world docks its drones.
/// </para>
/// </summary>
public static class DroneCarryMessage
{
    /// <summary>
    /// Most carries one message holds: a body is pulled only by drones on fetch orders, a drone is one order, and the
    /// server's order table holds no more than this.
    /// </summary>
    public const int MaxCarries = DroneBayRules.MaxOrders;

    /// <summary>Bytes a carry takes: the body, its pool and the drone that holds it.</summary>
    public const int RecordBytes = 4 + 1 + 4;

    /// <summary>Id, tick and count.</summary>
    public const int HeaderBytes = 1 + 8 + 2;

    /// <summary>The largest message: every drone that could be out on a fetch at once.</summary>
    public const int MaxWireSize = HeaderBytes + MaxCarries * RecordBytes;

    /// <summary>Writes a whole set; an empty one withdraws every carry a client held.</summary>
    public static void Write(ref NetWriter writer, long tick, ReadOnlySpan<DroneCarry> carries)
    {
        writer.WriteByte((byte)MessageId.DroneCarries);
        writer.WriteInt64(tick);
        writer.WriteUInt16((ushort)carries.Length);
        foreach (ref readonly var carry in carries)
        {
            writer.WriteUInt32(carry.Body);
            writer.WriteByte((byte)carry.Kind);
            writer.WriteUInt32(carry.Drone);
        }
    }

    /// <summary>
    /// Reads a set, refusing truncation, surplus bytes, an unknown pool, a body or drone with no identity, a drone
    /// claimed twice and a drone handle without its generation.
    /// </summary>
    public static bool TryRead(ref NetReader reader, scoped Span<DroneCarry> carries, out long tick, out int count)
    {
        tick = reader.ReadInt64();
        count = reader.ReadUInt16();
        if (!reader.IsValid || tick < 0 || count > MaxCarries || count > carries.Length || reader.Remaining != count * RecordBytes)
            return false;
        for (var i = 0; i < count; i++)
        {
            ref var carry = ref carries[i];
            carry.Body = reader.ReadUInt32();
            carry.Kind = (DroneCarryKind)reader.ReadByte();
            carry.Drone = reader.ReadUInt32();
            if (carry.Body == 0 || carry.Drone >> 16 == 0 || carry.Kind is not (DroneCarryKind.Container or DroneCarryKind.Ore))
                return false;
            for (var j = 0; j < i; j++) if (carries[j].Drone == carry.Drone) return false;
        }
        return reader.IsValid;
    }
}
