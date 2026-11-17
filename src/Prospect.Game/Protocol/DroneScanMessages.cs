using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>What a player asks of a hull's drone scan.</summary>
public enum DroneScanAction : byte
{
    /// <summary>Send drones to their places around a rock for a scan of the named kind.</summary>
    Request = 1,

    /// <summary>The drones are in place: run the scan.</summary>
    Start = 2,

    /// <summary>Give the scan up; the drones fly around the rock and home.</summary>
    Cancel = 3,
}

/// <summary>A player's drone scan command for one of their hulls. Reliable; the server checks everything again.</summary>
public readonly record struct DroneScanCommandMessage(uint Hull, DroneScanAction Action, DroneScanKind Kind, RockRef Rock)
{
    /// <summary>Id, hull, action, kind, sector and index.</summary>
    public const int MaxWireSize = 1 + 4 + 1 + 1 + 12 + 4;

    public void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.DroneScan);
        writer.WriteUInt32(Hull); writer.WriteByte((byte)Action); writer.WriteByte((byte)Kind);
        writer.WriteInt32(Rock.Sector.X); writer.WriteInt32(Rock.Sector.Y); writer.WriteInt32(Rock.Sector.Z); writer.WriteInt32(Rock.Index);
    }

    /// <summary>Refuses a malformed command before the server looks at the hull: a request names a kind and a rock, the others neither.</summary>
    public static bool TryRead(ref NetReader reader, out DroneScanCommandMessage message)
    {
        var hull = reader.ReadUInt32(); var action = (DroneScanAction)reader.ReadByte(); var kind = (DroneScanKind)reader.ReadByte();
        var sector = new SectorCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        var index = reader.ReadInt32();
        message = new DroneScanCommandMessage(hull, action, kind, new RockRef(sector, index));
        if (!reader.IsValid || reader.Remaining != 0 || hull == 0) return false;
        return action switch
        {
            DroneScanAction.Request => kind is DroneScanKind.Survey or DroneScanKind.Detailed && index >= 0 && index <= ushort.MaxValue,
            DroneScanAction.Start or DroneScanAction.Cancel => kind == DroneScanKind.None && sector == default && index == 0,
            _ => false,
        };
    }
}

/// <summary>
/// The bytes of one drone scan record, the same on the wire and in a save: a fixed head, and for a detailed scan the
/// map with only the veins, points and pockets it has. Reading checks structure through
/// <see cref="DroneScanRecordRules.Valid"/>; a reader that knows the galaxy checks the resources.
/// </summary>
public static class DroneScanCodec
{
    /// <summary>Owner, kind, sector, index, tick, radius, three resources and three shares, vein hint.</summary>
    public const int HeadBytes = 4 + 1 + 12 + 4 + 8 + 4 + 12 + 12 + 1;

    public const int PointBytes = 2 * 3 + 2;
    public const int VeinBytes = 3;
    public const int PocketBytes = 2 * 3 + 2 + 1 + 1 + 1;

    /// <summary>Counts, then heat and surface cells.</summary>
    public const int MapFixedBytes = 2 + 2 * DroneScanning.HeatCells;

    public const int MaxBytes = HeadBytes + MapFixedBytes + RockInteriorRules.MaxVeins * (VeinBytes + RockInteriorRules.MaxVeinPoints * PointBytes)
        + RockInteriorRules.MaxPockets * PocketBytes;

    /// <summary>The exact bytes <see cref="Write"/> writes for a record.</summary>
    public static int Size(in DroneScanRecord record)
    {
        if (record.Kind != DroneScanKind.Detailed) return HeadBytes;
        if (record.Map.VeinCount > RockInteriorRules.MaxVeins || record.Map.PocketCount > RockInteriorRules.MaxPockets)
            throw new InvalidDataException("A drone scan's map holds more veins or pockets than a record may.");
        var size = HeadBytes + MapFixedBytes + record.Map.PocketCount * PocketBytes;
        for (var v = 0; v < record.Map.VeinCount; v++) size += VeinBytes + record.Map.Veins[v].Points * PointBytes;
        return size;
    }

    public static void Write(ref NetWriter writer, in DroneScanRecord record)
    {
        writer.WriteUInt32(record.Owner); writer.WriteByte((byte)record.Kind);
        writer.WriteInt32(record.Sector.X); writer.WriteInt32(record.Sector.Y); writer.WriteInt32(record.Sector.Z);
        writer.WriteInt32(record.Index); writer.WriteInt64(record.RecordedTick); writer.WriteSingle(record.Radius);
        writer.WriteInt32(record.Resource); writer.WriteInt32(record.Mix1); writer.WriteInt32(record.Mix2);
        writer.WriteSingle(record.Grade); writer.WriteSingle(record.Mix1Share); writer.WriteSingle(record.Mix2Share);
        writer.WriteByte(record.VeinHint);
        if (record.Kind != DroneScanKind.Detailed) return;
        ref readonly var map = ref record.Map;
        writer.WriteByte(map.VeinCount); writer.WriteByte(map.PocketCount);
        for (var v = 0; v < map.VeinCount; v++)
        {
            var vein = map.Veins[v];
            writer.WriteByte(vein.Points); writer.WriteByte((byte)vein.Resource); writer.WriteByte(vein.Grade);
            for (var k = 0; k < vein.Points; k++)
            {
                var p = map.Points[v * RockInteriorRules.MaxVeinPoints + k];
                writer.WriteUInt16((ushort)p.X); writer.WriteUInt16((ushort)p.Y); writer.WriteUInt16((ushort)p.Z); writer.WriteUInt16(p.R);
            }
        }
        for (var i = 0; i < map.PocketCount; i++)
        {
            var p = map.Pockets[i];
            writer.WriteUInt16((ushort)p.X); writer.WriteUInt16((ushort)p.Y); writer.WriteUInt16((ushort)p.Z); writer.WriteUInt16(p.R);
            writer.WriteByte((byte)p.Kind); writer.WriteByte((byte)p.Resource); writer.WriteByte(p.Grade);
        }
        for (var c = 0; c < DroneScanning.HeatCells; c++) writer.WriteByte(map.Heat[c]);
        for (var c = 0; c < DroneScanning.HeatCells; c++) writer.WriteByte(map.Surface[c]);
    }

    /// <summary>Reads one record; false for anything a server could not have written.</summary>
    public static bool Read(ref NetReader reader, out DroneScanRecord record)
    {
        record = default;
        record.Owner = reader.ReadUInt32(); record.Kind = (DroneScanKind)reader.ReadByte();
        record.Sector = new SectorCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        record.Index = reader.ReadInt32(); record.RecordedTick = reader.ReadInt64(); record.Radius = reader.ReadSingle();
        record.Resource = reader.ReadInt32(); record.Mix1 = reader.ReadInt32(); record.Mix2 = reader.ReadInt32();
        record.Grade = reader.ReadSingle(); record.Mix1Share = reader.ReadSingle(); record.Mix2Share = reader.ReadSingle();
        record.VeinHint = reader.ReadByte();
        if (!reader.IsValid) return false;
        if (record.Kind == DroneScanKind.Detailed)
        {
            ref var map = ref record.Map;
            map.VeinCount = reader.ReadByte(); map.PocketCount = reader.ReadByte();
            if (!reader.IsValid || map.VeinCount > RockInteriorRules.MaxVeins || map.PocketCount > RockInteriorRules.MaxPockets) return false;
            for (var v = 0; v < map.VeinCount; v++)
            {
                var vein = new DroneScanVein { Points = reader.ReadByte(), Resource = (sbyte)reader.ReadByte(), Grade = reader.ReadByte() };
                if (!reader.IsValid || vein.Points > RockInteriorRules.MaxVeinPoints) return false;
                map.Veins[v] = vein;
                for (var k = 0; k < vein.Points; k++)
                    map.Points[v * RockInteriorRules.MaxVeinPoints + k] = new DroneScanPoint
                    { X = (short)reader.ReadUInt16(), Y = (short)reader.ReadUInt16(), Z = (short)reader.ReadUInt16(), R = reader.ReadUInt16() };
            }
            for (var i = 0; i < map.PocketCount; i++)
                map.Pockets[i] = new DroneScanPocket
                {
                    X = (short)reader.ReadUInt16(),
                    Y = (short)reader.ReadUInt16(),
                    Z = (short)reader.ReadUInt16(),
                    R = reader.ReadUInt16(),
                    Kind = (RockPocketKind)reader.ReadByte(),
                    Resource = (sbyte)reader.ReadByte(),
                    Grade = reader.ReadByte(),
                };
            for (var c = 0; c < DroneScanning.HeatCells; c++) map.Heat[c] = reader.ReadByte();
            for (var c = 0; c < DroneScanning.HeatCells; c++) map.Surface[c] = reader.ReadByte();
        }
        return reader.IsValid && DroneScanRecordRules.Valid(record);
    }
}

/// <summary>One drone scan result for its owner, reliable, sent once per new or changed record.</summary>
public static class DroneScanResultMessage
{
    public const int MaxWireSize = 1 + DroneScanCodec.MaxBytes;

    public static void Write(ref NetWriter writer, in DroneScanRecord record)
    {
        writer.WriteByte((byte)MessageId.DroneScanResult);
        DroneScanCodec.Write(ref writer, record);
    }

    public static bool TryRead(ref NetReader reader, out DroneScanRecord record) =>
        DroneScanCodec.Read(ref reader, out record) && reader.Remaining == 0;
}
