using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>
/// What the flown hull's ore bins hold, whole and reliable on change; never part of the hull snapshot, as the hold is
/// not. One record a bin of the hull's design, in the order of <see cref="HullDesign.Bins"/>.
/// </summary>
public static class OreBinsMessage
{
    /// <summary>A bin: its ore (255 for none), kilograms and grade.</summary>
    public const int RecordBytes = 1 + 8 + 4;

    /// <summary>Id, hull, design, tick and count.</summary>
    public const int HeaderBytes = 1 + 4 + 1 + 8 + 1;

    /// <summary>The largest message: every bin a design may have.</summary>
    public const int MaxWireSize = HeaderBytes + OreBins.MaxBins * RecordBytes;

    /// <summary>Writes one bin; the saved world uses the same record.</summary>
    public static void WriteRecord(ref NetWriter writer, in OreBin bin)
    {
        writer.WriteByte(bin.IsEmpty || bin.Resource < 0 ? byte.MaxValue : (byte)bin.Resource);
        writer.WriteDouble(bin.IsEmpty ? 0 : bin.Kg);
        writer.WriteSingle(bin.IsEmpty ? 0 : bin.Grade);
    }

    /// <summary>Reads one bin; whether a hull could hold it is <see cref="OreBins.Valid"/>'s to say.</summary>
    public static OreBin ReadRecord(ref NetReader reader)
    {
        var resource = reader.ReadByte();
        return new OreBin { Resource = resource == byte.MaxValue ? -1 : resource, Kg = reader.ReadDouble(), Grade = reader.ReadSingle() };
    }

    /// <summary>Writes the whole set.</summary>
    public static void Write(ref NetWriter writer, uint hull, byte design, long tick, ReadOnlySpan<OreBin> bins)
    {
        writer.WriteByte((byte)MessageId.OreBins);
        writer.WriteUInt32(hull); writer.WriteByte(design); writer.WriteInt64(tick); writer.WriteByte((byte)bins.Length);
        foreach (ref readonly var bin in bins) WriteRecord(ref writer, bin);
    }

    /// <summary>
    /// Refuses truncation, surplus bytes and records no bin could hold whatever its design: a number that is none,
    /// less than nothing, a grade outside 0 to 1, or an ore named for an empty bin. The client checks the set against
    /// the hull's design before it takes it.
    /// </summary>
    public static bool TryRead(ref NetReader reader, scoped Span<OreBin> bins, out uint hull, out byte design, out long tick, out int count)
    {
        hull = reader.ReadUInt32(); design = reader.ReadByte(); tick = reader.ReadInt64(); count = reader.ReadByte();
        if (!reader.IsValid || hull == 0 || tick < 0 || count > bins.Length || count > OreBins.MaxBins || reader.Remaining != count * RecordBytes) return false;
        for (var i = 0; i < count; i++)
        {
            var bin = ReadRecord(ref reader);
            if (bin.Kg == 0 ? bin.Resource != -1 || bin.Grade != 0
                : !(bin.Kg > 0) || !double.IsFinite(bin.Kg) || bin.Resource < 0 || bin.Resource >= Galaxy.MaxResources || !(bin.Grade > 0 && bin.Grade <= 1)) return false;
            bins[i] = bin;
        }

        return reader.IsValid;
    }
}
