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

    /// <summary>Shared preview; pass the current live flight figures, not the intact design.</summary>
    public JumpEligibility JumpEligibility(Galaxy galaxy, Double3 from, Double3 target, in FlightParams flight, ExplorationRules rules) =>
        JumpRules.Evaluate(from, target, flight, rules, IsCharted(galaxy.SectorOf(target)));

    public JumpEligibility JumpEligibility(Galaxy galaxy, Double3 from, SectorCoord sector, in FlightParams flight, ExplorationRules rules) =>
        JumpEligibility(galaxy, from, galaxy.CenterOf(sector), flight, rules);

    public NavigationView Navigation;
}
