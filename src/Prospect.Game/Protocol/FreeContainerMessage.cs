using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>A complete sensor-filtered set of loose containers, separate from the hull snapshot.</summary>
public static class FreeContainerMessage
{
    public const int RecordBytes = 104;
    public const int HeaderBytes = 11;
    public const int MaxWireSize = HeaderBytes + FreeContainers.Capacity * RecordBytes;

    /// <summary>Writes one fixed-size pose and payload record.</summary>
    public static void WriteRecord(ref NetWriter w, in FreeContainer c)
    {
        w.WriteUInt32(c.Id); w.WriteUInt32(c.ClaimOwner); w.WriteInt64(c.PickupReadyTick);
        w.WriteDouble(c.Position.X); w.WriteDouble(c.Position.Y); w.WriteDouble(c.Position.Z);
        Vector(ref w, c.Velocity);
        w.WriteSingle(c.Orientation.X); w.WriteSingle(c.Orientation.Y); w.WriteSingle(c.Orientation.Z); w.WriteSingle(c.Orientation.W);
        Vector(ref w, c.AngularVelocity);
        w.WriteByte((byte)c.Size); w.WriteByte(c.Resource < 0 ? byte.MaxValue : (byte)c.Resource);
        w.WriteDouble(c.Amount); w.WriteSingle(c.Hull);
        w.WriteUInt32(c.TractorHull); w.WriteUInt16((ushort)c.TractorMount); w.WriteUInt16((ushort)c.TractorRack);
        w.WriteByte(c.TractorStage); w.WriteByte(c.TractorPowered ? (byte)1 : (byte)0);
    }

    private static void Vector(ref NetWriter w, Vector3 v) { w.WriteSingle(v.X); w.WriteSingle(v.Y); w.WriteSingle(v.Z); }
    private static Vector3 Vector(ref NetReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    /// <summary>Reads a record; validation belongs to the enclosing message or saved-world contract.</summary>
    public static FreeContainer ReadRecord(ref NetReader r)
    {
        var c = new FreeContainer
        {
            Id = r.ReadUInt32(),
            ClaimOwner = r.ReadUInt32(),
            PickupReadyTick = r.ReadInt64(),
            Position = new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
            Velocity = Vector(ref r),
            Orientation = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            AngularVelocity = Vector(ref r),
            Size = r.ReadByte(),
            Resource = r.ReadByte()
        };
        if (c.Resource == byte.MaxValue) c.Resource = -1;
        c.Amount = r.ReadDouble(); c.Hull = r.ReadSingle();
        c.TractorHull = r.ReadUInt32(); c.TractorMount = r.ReadUInt16(); c.TractorRack = r.ReadUInt16();
        c.TractorStage = r.ReadByte(); var powered = r.ReadByte(); c.TractorPowered = powered == 1;
        if (powered > 1) c.TractorStage = byte.MaxValue;
        return c;
    }

    private static bool ValidClaim(GameContent content, in FreeContainer c) => c.TractorHull == 0
        ? c.TractorMount == 0 && c.TractorRack == 0 && c.TractorStage == 0 && !c.TractorPowered
        : c.TractorHull >> 16 != 0 && (uint)c.TractorMount < (uint)content.Blocks.Limits.MaxMounts
            && (uint)c.TractorRack < GeometryAdmission.MaxBlocks && c.TractorStage <= 1;

    /// <summary>Writes a full set, including an empty set to withdraw every earlier identity.</summary>
    public static void Write(ref NetWriter writer, long tick, ReadOnlySpan<FreeContainer> items)
    {
        writer.WriteByte((byte)MessageId.FreeContainers);
        writer.WriteInt64(tick); writer.WriteUInt16((ushort)items.Length);
        foreach (var c in items) WriteRecord(ref writer, c);
    }

    /// <summary>Rejects truncation, surplus bytes, duplicate identities and invalid physical or payload values.</summary>
    public static bool TryRead(ref NetReader reader, GameContent content, scoped Span<FreeContainer> items, out long tick, out int count)
    {
        tick = reader.ReadInt64(); count = reader.ReadUInt16();
        if (!reader.IsValid || tick < 0 || count > FreeContainers.Capacity || count > items.Length || reader.Remaining != count * RecordBytes) return false;
        for (var i = 0; i < count; i++)
        {
            var c = ReadRecord(ref reader);
            if (c.Id == 0 || !FreeContainers.Valid(content, c) || !ValidClaim(content, c)) return false;
            for (var j = 0; j < i; j++) if (items[j].Id == c.Id) return false;
            items[i] = c;
        }
        return reader.IsValid;
    }
}
