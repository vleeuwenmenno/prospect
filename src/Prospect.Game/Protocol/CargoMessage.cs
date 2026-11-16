using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>A complete own-hull hold, reliable on change; never part of the full hull snapshot.</summary>
public static class CargoMessage
{
    public const int RecordBytes = 4 + 1 + 1 + 8 + 4 + 6;
    public const int HeaderBytes = 1 + 4 + 1 + 8 + 1;
    public const int MaxWireSize = HeaderBytes + BlockStateStore.MaxContainers * RecordBytes;

    public static void Write(ref NetWriter writer, uint hull, byte design, long tick, ReadOnlySpan<HoldContainer> hold)
    {
        writer.WriteByte((byte)MessageId.Cargo);
        writer.WriteUInt32(hull); writer.WriteByte(design); writer.WriteInt64(tick); writer.WriteByte((byte)hold.Length);
        foreach (var container in hold) WriteContainer(ref writer, container);
    }

    /// <summary>Writes a compact, fixed-size record.</summary>
    private static void WriteContainer(ref NetWriter writer, in HoldContainer c)
    {
        writer.WriteInt32(c.Rack); writer.WriteByte((byte)c.Size); writer.WriteByte(c.Resource < 0 ? byte.MaxValue : (byte)c.Resource);
        writer.WriteDouble(c.Amount); writer.WriteSingle(c.Hull);
        writer.WriteUInt16((ushort)c.At.X); writer.WriteUInt16((ushort)c.At.Y); writer.WriteUInt16((ushort)c.At.Z);
    }

    private static HoldContainer ReadContainer(ref NetReader reader)
    {
        var rack = reader.ReadInt32(); var size = reader.ReadByte(); var resource = reader.ReadByte();
        var amount = reader.ReadDouble(); var hp = reader.ReadSingle();
        return new(rack, size, resource == byte.MaxValue ? -1 : resource, amount, hp,
            new(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()));
    }

    public static bool TryRead(ref NetReader reader, scoped Span<HoldContainer> hold, out uint hull, out byte design, out long tick, out int count)
    {
        hull = reader.ReadUInt32(); design = reader.ReadByte(); tick = reader.ReadInt64(); count = reader.ReadByte();
        if (!reader.IsValid || hull == 0 || tick < 0 || count > hold.Length || reader.Remaining != count * RecordBytes) return false;
        for (var i = 0; i < count; i++)
        {
            var c = ReadContainer(ref reader);
            if (c.Rack < 0 || c.Size >= 8 || c.Resource >= Galaxy.MaxResources || !double.IsFinite(c.Amount)
                || c.Amount < 0 || (c.Resource == -1 && c.Amount != 0) || !float.IsFinite(c.Hull) || c.Hull <= 0) return false;
            hold[i] = c;
        }
        return reader.IsValid;
    }
}
