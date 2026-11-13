using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>Preallocated ordered transfer slots shared by sending and receiving endpoints.</summary>
internal sealed class BlockTransfer
{
    internal const int HeaderBytes = 1 + 4 + 1 + 4 + 4 + 4 + 2;
    internal readonly ulong[] Words;
    internal byte[]? Data;
    internal bool Visual;
    internal uint Hull, Revision;
    internal byte Design;
    internal int Count, Offset;
    internal long Deadline;

    internal BlockTransfer(int bytes) => Words = new ulong[bytes / 8];

    internal void Clear() { Hull = 0; Data = null; Visual = false; Offset = Count = 0; }

    internal bool Send(ITransport transport, int peer, Span<byte> buffer, ref int budget)
    {
        if (Data is not null)
        {
            const int header = 12;
            var size = Math.Min(Data.Length - Offset, Math.Min(budget, buffer.Length) - header);
            if (size <= 0) return false;
            var raw = new NetWriter(buffer);
            raw.WriteByte((byte)MessageId.DesignData); raw.WriteByte(Design);
            raw.WriteInt32(Data.Length); raw.WriteInt32(Offset); raw.WriteUInt16((ushort)size);
            raw.WriteBytes(Data.AsSpan(Offset, size));
            budget -= raw.Length;
            if (!transport.Send(peer, NetChannel.Bulk, raw.Written)) return false;
            Offset += size;
            if (Offset == Data.Length) Clear();
            return true;
        }
        var count = Math.Min(Count - Offset, Math.Min((budget - HeaderBytes) / 8, (buffer.Length - HeaderBytes) / 8));
        if (Hull == 0 || count <= 0) return false;
        var writer = new NetWriter(buffer);
        writer.WriteByte((byte)(Visual ? MessageId.VisualState : MessageId.BlockState));
        writer.WriteUInt32(Hull);
        writer.WriteByte(Design);
        writer.WriteUInt32(Revision);
        writer.WriteInt32(Count);
        writer.WriteInt32(Offset);
        writer.WriteUInt16((ushort)count);
        if (Visual) writer.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Words.AsSpan(Offset, count)));
        else for (var i = 0; i < count; i++) writer.WriteUInt64(Words[Offset + i]);
        budget -= writer.Length;
        if (!transport.Send(peer, NetChannel.Bulk, writer.Written)) return false;
        Offset += count;
        if (Offset == Count) Clear();
        return true;
    }
}
