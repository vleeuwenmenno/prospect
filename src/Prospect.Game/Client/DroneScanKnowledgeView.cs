using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>
/// The player's drone scan results as the server sent them: read only <see cref="Records"/>[0..<see cref="Count"/>].
/// <see cref="Revision"/> counts changes, so a reader may build what it draws from a record once.
/// </summary>
public sealed class DroneScanKnowledgeView
{
    public readonly DroneScanRecord[] Records = new DroneScanRecord[DroneScanRecordRules.KnowledgePerOwner];
    public int Count;
    public uint Revision;

    /// <summary>The result for a rock, or -1: the player's drones have not scanned it.</summary>
    public int Find(RockRef rock)
    {
        for (var i = 0; i < Count; i++)
            if (Records[i].Sector == rock.Sector && Records[i].Index == rock.Index) return i;
        return -1;
    }

    /// <summary>Copies another view when it has changed. Takes no memory.</summary>
    public void CopyFrom(DroneScanKnowledgeView source)
    {
        if (source.Revision == Revision && source.Count == Count) return;
        Count = source.Count;
        Revision = source.Revision;
        source.Records.AsSpan(0, Count).CopyTo(Records);
    }
}
