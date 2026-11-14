using Prospect.Game.Persistence;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private bool CaptureModules(WorldSnapshot buffer)
    {
        var count = 0;
        foreach (var owner in _moduleOwners) count += owner.Count;
        if (count > buffer.ModuleObservations.Length) return false;
        buffer.ModuleObservationCount = count;
        var cursor = 0;
        foreach (var owner in _moduleOwners)
        {
            owner.Records.AsSpan(0, owner.Count).CopyTo(buffer.ModuleObservations.AsSpan(cursor));
            cursor += owner.Count;
        }
        return true;
    }

    private void RestoreModules(WorldSnapshot world)
    {
        if ((uint)world.ModuleObservationCount > (uint)world.ModuleObservations.Length
            || world.ModuleObservationCount > _saveLimits.MaxModuleObservations)
            throw new InvalidDataException("Too many module observations.");
        var sector = new SectorContent();
        SectorCoord? previous = null;
        foreach (var record in world.ModuleObservations.AsSpan(0, world.ModuleObservationCount))
        {
            if (!ModuleObservationRules.Valid(record) || OwnerSlot(record.Owner) < 0
                || record.RecordedTick >= world.NextWorldTick || record.Resource >= Galaxy.Resources.Length)
                throw new InvalidDataException("Invalid saved module observation.");
            if (record.Family == ModuleFamily.GasScanner)
            {
                if (Galaxy.GasClouds is null || Galaxy.GasComposition is null
                    || !Galaxy.GasClouds.TryGetCloud(Galaxy, record.Sector, out _))
                    throw new InvalidDataException("A saved sample names a missing cloud.");
            }
            else
            {
                if (previous != record.Sector) { Galaxy.Generate(record.Sector, sector); previous = record.Sector; }
                if (record.Index >= sector.AsteroidCount)
                    throw new InvalidDataException("A saved analysis names a missing asteroid.");
            }
            var owner = ModuleOwnerFor(record.Owner, true);
            if (owner is null || owner.Count == owner.Records.Length || FindObservation(owner, record) != owner.Count)
                throw new InvalidDataException("Duplicate or excessive saved module knowledge.");
            owner.Records[owner.Count++] = record;
        }
    }
}
