using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

public static partial class WorldSnapshotCodec
{
    // Save version 4 owns this layout; it is deliberately not an alias for a network message.
    internal const int ModuleObservationBytes = 115;

    private static void WriteModule(ref NetWriter writer, in ModuleObservation record)
    {
        writer.WriteUInt32(record.Owner); writer.WriteByte((byte)record.Family); writer.WriteByte((byte)record.Quality);
        Write(ref writer, record.Sector); writer.WriteInt32(record.Index); writer.WriteInt64(record.RecordedTick); writer.WriteUInt32(record.ContentRevision);
        Write(ref writer, record.Position); writer.WriteSingle(record.Radius); writer.WriteSingle(record.Mass); writer.WriteSingle(record.Grade); writer.WriteInt32(record.Resource);
        for (var i = 0; i < GasComposition.Capacity; i++) writer.WriteSingle(record.Gas.Shares[i]);
        writer.WriteSingle(record.Gas.Purity); writer.WriteSingle(record.Gas.Density); writer.WriteByte(record.Gas.Region);
    }

    private static bool ReadModule(ref NetReader reader, out ModuleObservation record)
    {
        record = new ModuleObservation
        {
            Owner = reader.ReadUInt32(),
            Family = (ModuleFamily)reader.ReadByte(),
            Quality = (ModuleQuality)reader.ReadByte(),
            Sector = ReadSector(ref reader),
            Index = reader.ReadInt32(),
            RecordedTick = reader.ReadInt64(),
            ContentRevision = reader.ReadUInt32(),
            Position = ReadDouble3(ref reader),
            Radius = reader.ReadSingle(),
            Mass = reader.ReadSingle(),
            Grade = reader.ReadSingle(),
            Resource = reader.ReadInt32()
        };
        for (var i = 0; i < GasComposition.Capacity; i++) record.Gas.Shares[i] = reader.ReadSingle();
        record.Gas.Purity = reader.ReadSingle(); record.Gas.Density = reader.ReadSingle(); record.Gas.Region = reader.ReadByte();
        return reader.IsValid && ModuleObservationRules.Valid(record);
    }

}
