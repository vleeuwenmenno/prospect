using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

public static partial class WorldSnapshotCodec
{
    // Save layout is deliberately independent of network message layout.
    internal const int CargoRecordBytes = 4 + 1 + 1 + 8 + 4 + 6;

    private static void WriteCargo(ref NetWriter writer, in HoldContainer c)
    {
        writer.WriteInt32(c.Rack); writer.WriteByte((byte)c.Size); writer.WriteByte(c.Resource < 0 ? byte.MaxValue : (byte)c.Resource);
        writer.WriteDouble(c.Amount); writer.WriteSingle(c.Hull);
        writer.WriteUInt16((ushort)c.At.X); writer.WriteUInt16((ushort)c.At.Y); writer.WriteUInt16((ushort)c.At.Z);
    }

    private static HoldContainer ReadCargo(ref NetReader reader)
    {
        var rack = reader.ReadInt32(); var size = reader.ReadByte(); var resource = reader.ReadByte();
        var amount = reader.ReadDouble(); var hp = reader.ReadSingle();
        return new(rack, size, resource == byte.MaxValue ? -1 : resource, amount, hp,
            new(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()));
    }
}
