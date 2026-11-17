using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    private readonly DroneScanKnowledgeView _droneScans = new();

    /// <summary>
    /// One drone scan result: taken only when it is the player's own and names resources the galaxy has. A rock already
    /// held is replaced; a new one takes the place of the oldest when the view is full, as the server's store does.
    /// </summary>
    public void OnDroneScanResult(ref NetReader reader)
    {
        if (!DroneScanResultMessage.TryRead(ref reader, out var record) || record.Owner != _playerId) return;
        var resources = _content.Galaxy.Resources.Length;
        if (record.Resource >= resources || record.Mix1 >= resources || record.Mix2 >= resources) return;
        for (var v = 0; v < record.Map.VeinCount; v++) if (record.Map.Veins[v].Resource >= resources) return;
        for (var p = 0; p < record.Map.PocketCount; p++) if (record.Map.Pockets[p].Resource >= resources) return;
        var at = _droneScans.Find(record.Rock);
        if (at < 0)
        {
            if (_droneScans.Count < _droneScans.Records.Length) at = _droneScans.Count++;
            else
            {
                at = 0;
                for (var i = 1; i < _droneScans.Count; i++)
                    if (_droneScans.Records[i].RecordedTick < _droneScans.Records[at].RecordedTick) at = i;
            }
        }
        _droneScans.Records[at] = record;
        _droneScans.Revision++;
    }

    public void CopyDroneScans(DroneScanKnowledgeView view) => view.CopyFrom(_droneScans);
}
