using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>Snapshot-owned chart and contact buffers. Only counted prefixes may be read.</summary>
public sealed class ExplorationView(int chartCapacity)
{
    public ScanView Scan;
    public readonly SectorChart[] Charts = new SectorChart[chartCapacity];
    public int ChartCount;
    public readonly SensorContact[] Contacts = new SensorContact[HullStore.MaxHulls];
    public int ContactCount;
    public bool ContactsComplete = true;
    public bool IsCharted(SectorCoord sector)
    {
        for (var i = 0; i < ChartCount; i++) if (Charts[i].Sector == sector) return true;
        return false;
    }

    /// <summary>Surveyed natural field locations only; requires a retained chart and a caller-owned buffer.</summary>
    public int ChartFields(Galaxy galaxy, SectorCoord sector, Span<ChartField> destination) =>
        IsCharted(sector) ? galaxy.AsteroidFields?.ChartFields(galaxy, sector, destination) ?? 0 : 0;

    /// <summary>Existing drive eligibility only. Blind risk and maximum drive range are not implemented.</summary>
    public JumpEligibility JumpEligibility(Galaxy galaxy, Double3 from, Double3 target, in FlightParams flight) =>
        new(flight.Ftl != FtlKind.None && flight.FtlPower > 0 && float.IsFinite(HullSim.JumpTravelSeconds(from, target, flight)),
            IsCharted(galaxy.SectorOf(target)), false);
}

public readonly record struct JumpEligibility(bool CanIssueOrder, bool Charted, bool BlindRiskImplemented);
