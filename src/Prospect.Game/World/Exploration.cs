using System.Text.Json;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>Shared, content-owned exploration tuning. Additional equipment gives redundancy only.</summary>
public sealed record ExplorationRules(double SensorRadius, int ScanTicks, int ContactTicks, int ChartsPerOwner)
{
    public double JumpMinimum { get; init; }
    public double BlindSafeDistance { get; init; }
    public float BlindMaxDamage { get; init; }
    /// <summary>Hard per-owner storage bound.</summary>
    public const int MaxChartsPerOwner = 4096;
    /// <summary>Collision prediction coverage even without working sensors.</summary>
    public double ProximityRadius { get; init; }

    public bool Covers(Double3 sensor, Double3 position)
    {
        var d = position - sensor;
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z <= SensorRadius * SensorRadius;
    }

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
            || contact * TickLoop.SimulationHz > int.MaxValue || charts <= 0 || charts > MaxChartsPerOwner)
            throw new InvalidDataException("Invalid exploration rules.");
        var minimum = root.GetProperty("jumpMinimum"u8).GetDouble();
        var safe = root.GetProperty("blindSafeDistance"u8).GetDouble();
        var damage = root.GetProperty("blindMaxDamage"u8).GetSingle();
        if (!double.IsFinite(minimum) || minimum <= 0 || !double.IsFinite(safe) || safe < minimum
            || !float.IsFinite(damage) || damage <= 0 || damage > 1)
            throw new InvalidDataException("Invalid jump rules.");
        var proximity = root.GetProperty("proximityRadius"u8).GetDouble();
        if (!double.IsFinite(proximity) || proximity <= 0 || proximity > radius) throw new InvalidDataException("Invalid proximity radius.");
        return new(radius, (int)Math.Ceiling(scan * TickLoop.SimulationHz),
            (int)Math.Ceiling(contact * TickLoop.SimulationHz), charts)
        { ProximityRadius = proximity, JumpMinimum = minimum, BlindSafeDistance = safe, BlindMaxDamage = damage };
    }
}

/// <summary>A permanent survey of immutable natural geography, under the saved world's generator and seed.</summary>
public readonly record struct SectorChart(uint Owner, SectorCoord Sector, long RecordedTick);

public enum ScanState : byte { Idle, Scanning, Completed, Cancelled, NoEquipment, ChartLimit }

/// <summary>Authoritative progress and result of a sector scan.</summary>
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

/// <summary>Immutable surveyed field envelope, without mineral composition or current asteroid mutations.</summary>
public readonly record struct ChartField(Double3 Center, System.Numerics.Vector3 HalfExtents, System.Numerics.Vector3 Along);
