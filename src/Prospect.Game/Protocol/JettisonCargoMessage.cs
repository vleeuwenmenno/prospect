using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>Reliable flight cargo release, independent of an editor token.</summary>
public readonly record struct JettisonCargoMessage(uint Hull, int Rack, int Index, long Tick, JettisonMode Mode)
{
    /// <summary>Id, hull, rack, slot, observed hold tick and mode.</summary>
    public const int MaxWireSize = 1 + 4 + 4 + 1 + 8 + 1;

    /// <summary>Writes the complete request.</summary>
    public void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.JettisonCargo);
        writer.WriteUInt32(Hull); writer.WriteInt32(Rack); writer.WriteByte((byte)Index);
        writer.WriteInt64(Tick); writer.WriteByte((byte)Mode);
    }

    /// <summary>Rejects malformed requests before the server checks ownership and the live hold.</summary>
    public static bool TryRead(ref NetReader reader, out JettisonCargoMessage request)
    {
        request = new(reader.ReadUInt32(), reader.ReadInt32(), reader.ReadByte(), reader.ReadInt64(), (JettisonMode)reader.ReadByte());
        return reader.IsValid && reader.Remaining == 0 && request.Hull != 0 && request.Rack >= 0
            && request.Index < BlockStateStore.MaxContainers && request.Tick >= 0
            && request.Mode is JettisonMode.Abandon or JettisonMode.KeepClaim;
    }
}
