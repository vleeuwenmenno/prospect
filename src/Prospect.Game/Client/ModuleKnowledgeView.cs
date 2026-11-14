using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>Snapshot-owned personal observations. Read only Records[0..Count]; timestamps describe stale knowledge.</summary>
public sealed class ModuleKnowledgeView
{
    public ModuleScanView Scan;
    public readonly ModuleObservation[] Records = new ModuleObservation[ModuleScanRules.KnowledgePerOwner];
    public int Count;

    /// <summary>Finds a retained analysis or locator marker without querying hidden generated data.</summary>
    public int Find(ModuleFamily family, SectorCoord sector, int index)
    {
        for (var i = 0; i < Count; i++)
            if (Records[i].Family == family && Records[i].Sector == sector && Records[i].Index == index) return i;
        return -1;
    }
}
