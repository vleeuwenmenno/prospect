using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>Per-admitted-peer acknowledgements and bounded full-state transfers.</summary>
internal sealed class BlockSender
{
    private readonly uint[] _ids = new uint[256], _revisions = new uint[256];
    private readonly BlockTransfer[] _transfers;
    private readonly BlockTransferRules _rules;
    private readonly bool[] _designQueued = new bool[256];
    private long _nextRequest;
    private int _replace;

    internal BlockSender(BlockTransferRules rules)
    {
        _rules = rules;
        _transfers = new BlockTransfer[rules.Transfers];
        for (var i = 0; i < _transfers.Length; i++) _transfers[i] = new BlockTransfer(rules.MaxBytes);
    }

    /// <summary>
    /// Queues a design's canonical bytes, or the manifest as id 255. During admission each goes out once. With
    /// <paramref name="repeat"/>, for a design added after admission, it may be asked for again once it is no
    /// longer on its way: the request spacing and the transfer slots bound that as they bound everything else.
    /// </summary>
    internal bool QueueDesign(byte id, byte[] bytes, long tick, bool repeat = false)
    {
        if ((!repeat && _designQueued[id]) || tick < _nextRequest || bytes.Length > _rules.MaxBytes) return false;
        foreach (var transfer in _transfers)
            if (transfer.Data is not null && transfer.Design == id) return false;
        foreach (var transfer in _transfers)
        {
            if (transfer.Visual) transfer.Clear();
            if (transfer.Hull != 0) continue;
            _designQueued[id] |= !repeat;
            transfer.Hull = uint.MaxValue; transfer.Design = id; transfer.Data = bytes;
            transfer.Offset = 0; transfer.Deadline = tick + _rules.AdmissionTicks;
            _nextRequest = tick + _rules.RequestTicks;
            return true;
        }
        return false;
    }

    internal uint Acknowledged(uint id)
    {
        for (var i = 0; i < _ids.Length; i++) if (_ids[i] == id) return _revisions[i];
        return 0;
    }

    internal void Receive(ref NetReader reader, HullStore hulls, long tick, ReadOnlySpan<ulong> visible)
    {
        var count = reader.ReadByte();
        if (!reader.IsValid || count > 32 || reader.Remaining != count * 13) return;
        var entries = reader;
        // Resolve loss recovery before cosmetic requests, regardless of acknowledgement order.
        for (var pass = 0; pass < 2; pass++)
        {
            reader = entries;
            for (var n = 0; n < count; n++)
            {
                var id = reader.ReadUInt32();
                var revision = reader.ReadUInt32();
                var request = reader.ReadByte();
                var visualRevision = reader.ReadUInt32();
                var index = (int)(id & 65535);
                if (!reader.IsValid) return;
                if ((index / 64 >= visible.Length || !BlockSet.Has(visible, index)) || request > 3 || !hulls.IsAlive(new HullHandle(index, (int)(id >> 16))) || revision > hulls.Revision(index)) continue;
                var slot = -1;
                for (var i = 0; i < _ids.Length; i++) if (_ids[i] == id) { slot = i; break; }
                if (slot < 0) { slot = _replace++ % _ids.Length; _replace %= _ids.Length; _ids[slot] = id; _revisions[slot] = 0; }
                _revisions[slot] = Math.Max(_revisions[slot], revision);
                var loss = (request & 1) != 0;
                if (request == 0 || loss != (pass == 0) || tick < _nextRequest) continue;
                var active = false;
                foreach (var transfer in _transfers)
                    if (transfer.Hull == id && (!transfer.Visual || !loss)) active = true;
                if (active) continue;
                BlockTransfer? available = null;
                foreach (var transfer in _transfers)
                {
                    if (transfer.Visual && loss) transfer.Clear();
                    if (transfer.Hull == 0) available = transfer;
                }
                if (available is null) continue;
                if ((request & 1) == 0 && (request & 2) != 0)
                {
                    ref var visual = ref hulls.Info(index).Visual;
                    if (visualRevision >= visual.Version || available.Words.Length < 21) continue;
                    var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(available.Words.AsSpan(0, 21));
                    bytes.Clear();
                    var writer = new NetWriter(bytes);
                    visual.Write(ref writer);
                    available.Visual = true;
                    available.Hull = id;
                    available.Design = hulls.Info(index).Design;
                    available.Revision = visual.Version;
                    available.Count = 21;
                    available.Offset = 0;
                    available.Deadline = tick + _rules.TransferTicks;
                    _nextRequest = tick + _rules.RequestTicks;
                    continue;
                }
                var words = hulls.Blocks(index);
                if (words.Length > available.Words.Length) continue;
                words.CopyTo(available.Words);
                available.Hull = id;
                available.Design = hulls.Info(index).Design;
                available.Revision = hulls.Revision(index);
                available.Count = words.Length;
                available.Offset = 0;
                available.Deadline = tick + _rules.TransferTicks;
                _nextRequest = tick + _rules.RequestTicks;
            }
        }
    }

    internal void Send(ITransport transport, int peer, Span<byte> buffer, long tick, ReadOnlySpan<ulong> visible)
    {
        var budget = _rules.BytesPerTick;
        foreach (var transfer in _transfers)
        {
            if (transfer.Hull != 0 && tick > transfer.Deadline) transfer.Clear();
            if (transfer.Hull is not (0 or uint.MaxValue) && ((int)(transfer.Hull & 65535) / 64 >= visible.Length || !BlockSet.Has(visible, (int)(transfer.Hull & 65535)))) transfer.Clear();
            transfer.Send(transport, peer, buffer, ref budget);
        }
    }
}
