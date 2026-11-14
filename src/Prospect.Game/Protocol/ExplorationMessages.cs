using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

public static class ExplorationMessage
{
    public const int MaxCharts = 32;
    public const int ScanWireSize = 1;
    public const int ChartWireSize = 24;
    public const int MaxWireSize = 1 + 1 + 12 + 8 + 8 + 1 + MaxCharts * ChartWireSize;
    public const int VisibilityMaxWireSize = 1 + 8 + 2 + HullStore.MaxHulls / 8;

    public static void Write(ref NetWriter writer, in ScanView scan, scoped ReadOnlySpan<SectorChart> charts)
    {
        writer.WriteByte((byte)MessageId.Exploration);
        writer.WriteByte((byte)scan.State);
        Sector(ref writer, scan.Sector);
        writer.WriteInt64(scan.StartedTick);
        writer.WriteInt64(scan.CompletesTick);
        writer.WriteByte((byte)charts.Length);
        foreach (var chart in charts)
        {
            writer.WriteUInt32(chart.Owner);
            Sector(ref writer, chart.Sector);
            writer.WriteInt64(chart.RecordedTick);
        }
    }

    public static bool Read(ref NetReader reader, scoped Span<SectorChart> charts, out ScanView scan, out int count)
    {
        scan = new ScanView
        {
            State = (ScanState)reader.ReadByte(),
            Sector = Sector(ref reader),
            StartedTick = reader.ReadInt64(),
            CompletesTick = reader.ReadInt64()
        };
        count = reader.ReadByte();
        if ((byte)scan.State > (byte)ScanState.ChartLimit || count > charts.Length || count > MaxCharts
            || scan.StartedTick < 0 || scan.CompletesTick < scan.StartedTick) return false;
        for (var i = 0; i < count; i++)
        {
            charts[i] = new(reader.ReadUInt32(), Sector(ref reader), reader.ReadInt64());
            if (charts[i].Owner == 0 || charts[i].RecordedTick < 0) return false;
        }
        return reader.IsValid && reader.Remaining == 0;
    }

    private static void Sector(ref NetWriter writer, SectorCoord sector)
    { writer.WriteInt32(sector.X); writer.WriteInt32(sector.Y); writer.WriteInt32(sector.Z); }
    private static SectorCoord Sector(ref NetReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
}
