using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>Bounded reliable module intents and owner-only observations.</summary>
public static class ModuleMessage
{
    public const int RequestWireSize = 18;
    public const int ObservationWireSize = 115;
    public const int MaxObservations = 8;
    public const int HeaderWireSize = 79;
    public const int MaxWireSize = HeaderWireSize + MaxObservations * ObservationWireSize;

    public static void WriteRequest(ref NetWriter writer, in ModuleScanRequest request)
    {
        writer.WriteByte((byte)MessageId.ModuleScan);
        writer.WriteByte((byte)request.Family); Sector(ref writer, request.Sector); writer.WriteInt32(request.Index);
    }

    public static bool ReadRequest(ref NetReader reader, out ModuleScanRequest request)
    {
        request = new((ModuleFamily)reader.ReadByte(), Sector(ref reader), reader.ReadInt32());
        return reader.IsValid && reader.Remaining == 0 && request.Family is >= ModuleFamily.GasScanner and <= ModuleFamily.AsteroidLocator
            && request.Index >= 0 && (request.Family == ModuleFamily.AsteroidScanner || request.Index == 0)
            && (request.Family != ModuleFamily.AsteroidLocator || request.Sector == default);
    }

    public static void Write(ref NetWriter writer, in ModuleScanView scan, scoped ReadOnlySpan<ModuleObservation> records)
    {
        writer.WriteByte((byte)MessageId.ModuleSurvey);
        writer.WriteUInt32(scan.Serial); writer.WriteUInt32(scan.Hull);
        writer.WriteByte((byte)scan.Family); writer.WriteByte((byte)scan.State); writer.WriteByte((byte)scan.Refusal); writer.WriteByte((byte)scan.Quality);
        Sector(ref writer, scan.Sector); writer.WriteInt32(scan.Index);
        writer.WriteInt64(scan.StartedTick); writer.WriteInt64(scan.CompletesTick); writer.WriteInt64(scan.CooldownUntilTick);
        Position(ref writer, scan.Origin); writer.WriteBool(scan.Partial); writer.WriteByte((byte)records.Length);
        foreach (var record in records) WriteObservation(ref writer, record);
    }

    public static bool Read(ref NetReader reader, scoped Span<ModuleObservation> records, out ModuleScanView scan, out int count)
    {
        scan = new ModuleScanView
        {
            Serial = reader.ReadUInt32(),
            Hull = reader.ReadUInt32(),
            Family = (ModuleFamily)reader.ReadByte(),
            State = (ModuleScanState)reader.ReadByte(),
            Refusal = (ModuleScanState)reader.ReadByte(),
            Quality = (ModuleQuality)reader.ReadByte(),
            Sector = Sector(ref reader),
            Index = reader.ReadInt32(),
            StartedTick = reader.ReadInt64(),
            CompletesTick = reader.ReadInt64(),
            CooldownUntilTick = reader.ReadInt64(),
            Origin = Position(ref reader)
        };
        var partial = reader.ReadByte(); scan.Partial = partial != 0;
        count = reader.ReadByte();
        if (!reader.IsValid || count > MaxObservations || count > records.Length || partial > 1
            || scan.Family > ModuleFamily.AsteroidLocator || scan.State > ModuleScanState.Unavailable || scan.Refusal > ModuleScanState.Unavailable
            || scan.Quality > ModuleQuality.AlienTech || scan.Index < 0 || scan.StartedTick < 0
            || scan.CompletesTick < scan.StartedTick || scan.CooldownUntilTick < 0 || !Finite(scan.Origin)) return false;
        for (var i = 0; i < count; i++) if (!ReadObservation(ref reader, out records[i])) return false;
        return reader.IsValid && reader.Remaining == 0;
    }

    public static void WriteObservation(ref NetWriter writer, in ModuleObservation record)
    {
        writer.WriteUInt32(record.Owner); writer.WriteByte((byte)record.Family); writer.WriteByte((byte)record.Quality);
        Sector(ref writer, record.Sector); writer.WriteInt32(record.Index); writer.WriteInt64(record.RecordedTick); writer.WriteUInt32(record.ContentRevision);
        Position(ref writer, record.Position); writer.WriteSingle(record.Radius); writer.WriteSingle(record.Mass); writer.WriteSingle(record.Grade); writer.WriteInt32(record.Resource);
        for (var i = 0; i < GasComposition.Capacity; i++) writer.WriteSingle(record.Gas.Shares[i]);
        writer.WriteSingle(record.Gas.Purity); writer.WriteSingle(record.Gas.Density); writer.WriteByte(record.Gas.Region);
    }

    public static bool ReadObservation(ref NetReader reader, out ModuleObservation record)
    {
        record = new ModuleObservation
        {
            Owner = reader.ReadUInt32(),
            Family = (ModuleFamily)reader.ReadByte(),
            Quality = (ModuleQuality)reader.ReadByte(),
            Sector = Sector(ref reader),
            Index = reader.ReadInt32(),
            RecordedTick = reader.ReadInt64(),
            ContentRevision = reader.ReadUInt32(),
            Position = Position(ref reader),
            Radius = reader.ReadSingle(),
            Mass = reader.ReadSingle(),
            Grade = reader.ReadSingle(),
            Resource = reader.ReadInt32()
        };
        for (var i = 0; i < GasComposition.Capacity; i++) record.Gas.Shares[i] = reader.ReadSingle();
        record.Gas.Purity = reader.ReadSingle(); record.Gas.Density = reader.ReadSingle(); record.Gas.Region = reader.ReadByte();
        return reader.IsValid && Valid(record);
    }

    /// <summary>Structural validation independent of a world's resource catalogue.</summary>
    public static bool Valid(in ModuleObservation record)
    {
        if (record.Owner == 0 || record.Family is < ModuleFamily.GasScanner or > ModuleFamily.AsteroidLocator
            || record.Quality > ModuleQuality.AlienTech || record.Index < 0 || record.Index > ushort.MaxValue
            || record.RecordedTick < 0 || record.ContentRevision != 0 || !Finite(record.Position)
            || !float.IsFinite(record.Radius) || record.Radius < 0 || !float.IsFinite(record.Mass) || record.Mass < 0
            || !Unit(record.Grade) || record.Resource is < -1 or >= 32 || record.Gas.Region >= 8
            || !Unit(record.Gas.Purity) || !Unit(record.Gas.Density)) return false;
        var sum = 0f;
        for (var i = 0; i < GasComposition.Capacity; i++)
        { if (!Unit(record.Gas.Shares[i])) return false; sum += record.Gas.Shares[i]; }
        if (sum > 1.00001f) return false;
        if (record.Family == ModuleFamily.GasScanner)
            return record.Index == record.Gas.Region && record.Resource == -1 && record.Radius == 0 && record.Mass == 0 && record.Grade == 0;
        if (sum != 0 || record.Gas.Purity != 0 || record.Gas.Density != 0 || record.Gas.Region != 0) return false;
        return record.Family != ModuleFamily.AsteroidLocator || record.Radius == 0 && record.Mass == 0 && record.Grade == 0;
    }

    private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
    private static bool Finite(Double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static void Sector(ref NetWriter writer, SectorCoord value)
    { writer.WriteInt32(value.X); writer.WriteInt32(value.Y); writer.WriteInt32(value.Z); }
    private static SectorCoord Sector(ref NetReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    private static void Position(ref NetWriter writer, Double3 value)
    { writer.WriteDouble(value.X); writer.WriteDouble(value.Y); writer.WriteDouble(value.Z); }
    private static Double3 Position(ref NetReader reader) => new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
}
