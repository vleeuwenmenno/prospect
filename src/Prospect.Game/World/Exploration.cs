using System.Text.Json;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>Shared, content-owned exploration tuning. Additional equipment gives redundancy only.</summary>
public sealed record ExplorationRules(double SensorRadius, int ScanTicks, int ContactTicks, int ChartsPerOwner)
{
    public double GasOpticalPenalty { get; init; }
    /// <summary>Ticks after a scan of a sector ends, completed, refused for capacity or cancelled, before that sector can be scanned again by the same owner. Zero turns it off.</summary>
    public int ScanCooldownTicks { get; init; }
    public double JumpMinimum { get; init; }
    public double BlindSafeDistance { get; init; }
    public double BlindSafeSectors { get; init; }
    public ExplorationRules ForSectorSize(double size) => this with { BlindSafeDistance = BlindSafeSectors * size };
    public float BlindMaxDamage { get; init; }
    /// <summary>Hard per-owner storage bound.</summary>
    public const int MaxChartsPerOwner = 4096;
    /// <summary>The longest scan cooldown content may ask for, in seconds.</summary>
    public const double MaxScanCooldownSeconds = 3600;
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
        var cooldown = root.GetProperty("scanCooldownSeconds"u8).GetDouble();
        if (!double.IsFinite(cooldown) || cooldown < 0 || cooldown > MaxScanCooldownSeconds)
            throw new InvalidDataException("Invalid scan cooldown.");
        var charts = root.GetProperty("chartsPerOwner"u8).GetInt32();
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(scan) || scan <= 0
            || !double.IsFinite(contact) || contact <= 0 || scan * TickLoop.SimulationHz > int.MaxValue
            || contact * TickLoop.SimulationHz > int.MaxValue || charts <= 0 || charts > MaxChartsPerOwner)
            throw new InvalidDataException("Invalid exploration rules.");
        var minimum = root.GetProperty("jumpMinimum"u8).GetDouble();
        var safe = root.GetProperty("blindSafeSectors"u8).GetDouble();
        var damage = root.GetProperty("blindMaxDamage"u8).GetSingle();
        if (!double.IsFinite(minimum) || minimum <= 0 || !double.IsFinite(safe) || safe <= 0
            || !float.IsFinite(damage) || damage <= 0 || damage > 1)
            throw new InvalidDataException("Invalid jump rules.");
        var gasPenalty = root.GetProperty("gasOpticalPenalty"u8).GetDouble();
        if (!double.IsFinite(gasPenalty) || gasPenalty < 0 || gasPenalty > 10) throw new InvalidDataException("Invalid gas optical penalty.");
        var proximity = root.GetProperty("proximityRadius"u8).GetDouble();
        if (!double.IsFinite(proximity) || proximity <= 0 || proximity > radius) throw new InvalidDataException("Invalid proximity radius.");
        return new(radius, (int)Math.Ceiling(scan * TickLoop.SimulationHz),
            (int)Math.Ceiling(contact * TickLoop.SimulationHz), charts)
        { ScanCooldownTicks = (int)Math.Ceiling(cooldown * TickLoop.SimulationHz), GasOpticalPenalty = gasPenalty, ProximityRadius = proximity, JumpMinimum = minimum, BlindSafeSectors = safe, BlindSafeDistance = safe * 80000, BlindMaxDamage = damage };
    }
}

/// <summary>A permanent survey of immutable natural geography, under the saved world's generator and seed.</summary>
public readonly record struct SectorChart(uint Owner, SectorCoord Sector, long RecordedTick);

/// <summary>
/// A scan's state. <c>NoEquipment</c>, <c>ChartLimit</c> and <c>Cooldown</c> are refusals: the request changed nothing
/// but this state. <c>Cooldown</c> means the sector was scanned, or a scan of it was cancelled, too recently.
/// </summary>
public enum ScanState : byte { Idle, Scanning, Completed, Cancelled, NoEquipment, ChartLimit, Cooldown }

/// <summary>Authoritative progress and result of a sector scan.</summary>
public struct ScanView
{
    public ScanState State;
    public SectorCoord Sector;
    public long StartedTick;
    public long CompletesTick;

    /// <summary>The sector a scan last ended in; it cannot be scanned by this owner again before <see cref="CooldownUntilTick"/>.</summary>
    public SectorCoord CooldownSector;

    /// <summary>The world tick the cooldown ends; zero when no scan has ended.</summary>
    public long CooldownUntilTick;

    /// <summary>True while <paramref name="sector"/> is cooling down at <paramref name="tick"/>.</summary>
    public readonly bool CoolingDown(SectorCoord sector, long tick) => CooldownUntilTick > tick && CooldownSector == sector;
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

/// <summary>
/// Immutable surveyed field envelope, without mineral composition or current asteroid mutations. The contour is the
/// seeded ribbon the field's rocks lie in, which is what a chart shows of its shape.
/// </summary>
public readonly record struct ChartField(Double3 Center, System.Numerics.Vector3 HalfExtents, System.Numerics.Vector3 Along, FieldContour Contour = default);

/// <summary>
/// The four transverse sections of a field's ribbon, along its length from one end to the other: where each lies
/// across the field's axis and how wide it is, both as shares of the field's half-width.
/// </summary>
public readonly record struct FieldContour(float Center0, float Width0, float Center1, float Width1, float Center2, float Width2, float Center3, float Width3)
{
    /// <summary>The number of sections.</summary>
    public const int Knots = 4;

    /// <summary>Where section <paramref name="knot"/> lies across the axis.</summary>
    public float CenterAt(int knot) => knot switch { 0 => Center0, 1 => Center1, 2 => Center2, _ => Center3 };

    /// <summary>How wide section <paramref name="knot"/> is.</summary>
    public float WidthAt(int knot) => knot switch { 0 => Width0, 1 => Width1, 2 => Width2, _ => Width3 };
}
