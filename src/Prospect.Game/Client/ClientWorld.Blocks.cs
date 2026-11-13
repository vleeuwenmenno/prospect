using Prospect.Engine.Net;
using Prospect.Game.Protocol;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    private struct HeldPose
    {
        internal HullRecord Record;
        internal SnapshotHeader Header;
        internal long Until;
    }
    private readonly HeldPose[] _held = new HeldPose[256];
    private readonly byte[] _heldWire = new byte[18 + HullRecord.MaxWireSize];

    private void HoldPose(in HullRecord record, in SnapshotHeader header, long tick)
    {
        if (record.Id == 0 || record.Design >= _content.Designs.Length || record.Revision > _content.Designs[record.Design].BlockCount) return;
        for (var i = 0; i < _remotes.Length; i++)
            if (_remotes[i].Used && _remotes[i].Id == record.Id) _remotes[i].LastSeenTick = tick;
        var free = -1;
        for (var i = 0; i < _held.Length; i++)
        {
            if (_held[i].Record.Id == record.Id)
            {
                if (_held[i].Header.ServerTick >= header.ServerTick) return;
                free = i;
                break;
            }
            if (_held[i].Record.Id == 0 || tick > _held[i].Until) free = i;
        }
        if (free >= 0) _held[free] = new HeldPose { Record = record, Header = header, Until = tick + _content.BlockTransfers.TransferTicks };
    }

    private void ClearHeld(uint id)
    {
        for (var i = 0; i < _held.Length; i++) if (_held[i].Record.Id == id) _held[i] = default;
    }

    private void ReplayHeld(long tick)
    {
        for (var i = 0; i < _held.Length; i++)
        {
            var held = _held[i];
            if (held.Record.Id == 0) continue;
            if (tick > held.Until) { _held[i] = default; continue; }
            if (!_replica.Accept(held.Record, tick)) continue;
            held.Header.HullCount = 1;
            var writer = new NetWriter(_heldWire);
            held.Header.Write(ref writer);
            held.Record.Write(ref writer);
            var reader = new NetReader(writer.Written[1..]);
            OnSnapshot(ref reader, tick);
        }
    }
}
