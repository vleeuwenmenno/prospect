using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>Bounded block revisions, independent of poses until both describe the same state.</summary>
internal sealed class BlockReplica : IDisposable
{
    private struct Entry
    {
        internal uint Id;
        internal byte Design;
        internal BlockStateHandle State;
        internal bool Request;
        internal long Seen;
    }
    private readonly Entry[] _entries = new Entry[256];
    private readonly BlockStateStore _states = new(256);
    private readonly BlockTransfer[] _transfers;
    private GameContent _content;
    private readonly byte[] _acks = new byte[2 + 256 * 9];
    private int _cursor;

    internal BlockReplica(GameContent content)
    {
        _content = content;
        _transfers = new BlockTransfer[content.BlockTransfers.Transfers];
        for (var i = 0; i < _transfers.Length; i++) _transfers[i] = new BlockTransfer(content.BlockTransfers.MaxBytes);
    }

    /// <summary>
    /// Takes a catalogue that has grown at its end. The designs the replica knew keep their numbers, so every
    /// entry, revision and transfer it holds stays as it is.
    /// </summary>
    internal void Append(GameContent content) => _content = content;

    private int Find(uint id)
    {
        if (id == 0) return -1;
        for (var i = 0; i < _entries.Length; i++) if (_entries[i].Id == id) return i;
        return -1;
    }

    internal ReadOnlySpan<int> Losses(uint id) => _states.Losses(_entries[Find(id)].State);

    internal bool Accept(in HullRecord record, long tick)
    {
        if (record.Id == 0 || record.Design >= _content.Designs.Length) return false;
        var design = _content.Designs[record.Design];
        if (record.Revision > design.BlockCount || record.LossCount > HullRecord.MaxLosses
            || record.LossStart > record.Revision || record.LossCount > record.Revision - record.LossStart) return false;
        for (var j = 0; j < record.LossCount; j++)
        {
            if ((uint)record.Losses[j] >= design.BlockCount) return false;
            for (var k = 0; k < j; k++) if (record.Losses[j] == record.Losses[k]) return false;
        }
        var slot = Find(record.Id);
        if (slot < 0)
        {
            for (var i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Id != 0 && tick - _entries[i].Seen <= _content.BlockTransfers.TransferTicks) continue;
                slot = i;
                if (_entries[i].Id != 0) _states.Release(_entries[i].State);
                _entries[i] = new Entry { Id = record.Id, Design = record.Design, State = _states.Create(design, false) };
                break;
            }
        }
        if (slot < 0) return false;
        ref var entry = ref _entries[slot];
        if (entry.Design != record.Design) return false;
        entry.Seen = tick;
        var revision = _states.Revision(entry.State);
        if (record.Revision < revision) return false;
        if (record.LossStart <= revision && record.LossStart + record.LossCount >= revision)
        {
            var skip = (int)(revision - record.LossStart);
            for (var i = skip; i < record.LossCount; i++)
                if (!BlockSet.Has(_states.Alive(entry.State), record.Losses[i])) return false;
            for (var i = skip; i < record.LossCount; i++) _states.Lose(entry.State, record.Losses[i]);
        }
        entry.Request = _states.Revision(entry.State) != record.Revision;
        return !entry.Request;
    }

    internal void SendAcks(ITransport transport, long tick)
    {
        foreach (var transfer in _transfers) if (transfer.Hull != 0 && tick > transfer.Deadline) transfer.Clear();
        var writer = new NetWriter(_acks);
        writer.WriteByte((byte)MessageId.BlockAck);
        writer.WriteByte(0);
        byte count = 0;
        // At most one small datagram per tick leaves input and reliable gameplay budgets intact.
        for (var n = 0; n < _entries.Length && count < 64; n++)
        {
            ref var e = ref _entries[_cursor++ % _entries.Length];
            if (e.Id == 0 || tick - e.Seen > _content.BlockTransfers.TransferTicks) continue;
            writer.WriteUInt32(e.Id);
            writer.WriteUInt32(_states.Revision(e.State));
            writer.WriteByte(e.Request ? (byte)1 : (byte)0);
            count++;
        }
        _cursor %= _entries.Length;
        _acks[1] = count;
        if (count != 0) transport.Send(0, NetChannel.Unreliable, writer.Written);
    }

    internal void Receive(ref NetReader reader, long tick)
    {
        var id = reader.ReadUInt32();
        var design = reader.ReadByte();
        var revision = reader.ReadUInt32();
        var total = reader.ReadInt32();
        var offset = reader.ReadInt32();
        var count = reader.ReadUInt16();
        var slot = Find(id);
        if (!reader.IsValid || slot < 0 || design != _entries[slot].Design) return;
        var blocks = _content.Designs[design].BlockCount;
        if (revision > blocks || total != BlockSet.WordCount(blocks) || total > _content.BlockTransfers.MaxBytes / 8
            || offset < 0 || offset >= total || count == 0 || count > total - offset) return;
        BlockTransfer? transfer = null;
        foreach (var candidate in _transfers) if (candidate.Hull == id) transfer = candidate;
        if (transfer is not null && tick > transfer.Deadline)
        {
            transfer.Clear();
            transfer = null;
        }
        if (offset == 0)
        {
            if (transfer is null) foreach (var candidate in _transfers) if (candidate.Hull == 0) { transfer = candidate; break; }
            if (transfer is null) return;
            transfer.Hull = id; transfer.Design = design; transfer.Revision = revision; transfer.Count = total; transfer.Offset = 0;
            transfer.Deadline = tick + _content.BlockTransfers.TransferTicks;
        }
        if (transfer is null || transfer.Design != design || transfer.Count != total || transfer.Revision != revision || transfer.Offset != offset) return;
        for (var i = 0; i < count; i++) transfer.Words[offset + i] = reader.ReadUInt64();
        if (!reader.IsValid) { transfer.Clear(); return; }
        transfer.Offset += count;
        if (transfer.Offset != total) return;
        var words = transfer.Words.AsSpan(0, total);
        var alive = 0;
        foreach (var word in words) alive += BitOperations.PopCount(word);
        var tail = blocks % 64;
        if (alive != blocks - revision || (tail != 0 && (words[^1] >> tail) != 0)) { transfer.Clear(); return; }
        ref var entry = ref _entries[slot];
        if (revision >= _states.Revision(entry.State))
        {
            var old = _states.Alive(entry.State);
            for (var i = 0; i < total; i++) if ((words[i] & ~old[i]) != 0) { transfer.Clear(); return; }
            for (var i = 0; i < total; i++)
            {
                var gone = old[i] & ~words[i];
                for (; gone != 0; gone &= gone - 1) _states.Lose(entry.State, i * 64 + BitOperations.TrailingZeroCount(gone));
            }
            entry.Request = false;
        }
        transfer.Clear();
    }

    public void Dispose() => _states.Dispose();
}
