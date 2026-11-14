using System.Text.Json;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>Shared, content-owned exploration tuning. Additional equipment gives redundancy only.</summary>
public sealed record ExplorationRules(double SensorRadius, int ScanTicks, int ContactTicks, int ChartsPerOwner)
{
    internal static ExplorationRules Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var radius = root.GetProperty("sensorRadius"u8).GetDouble();
        var scan = root.GetProperty("scanSeconds"u8).GetDouble();
        var contact = root.GetProperty("contactSeconds"u8).GetDouble();
        var charts = root.GetProperty("chartsPerOwner"u8).GetInt32();
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(scan) || scan <= 0
            || !double.IsFinite(contact) || contact <= 0 || scan * TickLoop.SimulationHz > int.MaxValue
            || contact * TickLoop.SimulationHz > int.MaxValue || charts <= 0 || charts > ushort.MaxValue)
            throw new InvalidDataException("Invalid exploration rules.");
        return new(radius, (int)Math.Ceiling(scan * TickLoop.SimulationHz),
            (int)Math.Ceiling(contact * TickLoop.SimulationHz), charts);
    }
}

/// <summary>A permanent survey of immutable natural geography, under the saved world's generator and seed.</summary>
public readonly record struct SectorChart(uint Owner, SectorCoord Sector, long RecordedTick);

public enum ScanState : byte { Idle, Scanning, Completed, Cancelled, NoEquipment, ChartLimit }

public struct ScanView
{
    public ScanState State;
    public SectorCoord Sector;
    public long StartedTick;
    public long CompletesTick;
}

/// <summary>Known contact. A stale position is never extrapolated and is not a live target.</summary>
public struct SensorContact
{
    public uint Hull;
    public uint Owner;
    public byte Design;
    public Double3 Position;
    public long ObservedTick;
    public long LostTick;
    public bool Live;
}
