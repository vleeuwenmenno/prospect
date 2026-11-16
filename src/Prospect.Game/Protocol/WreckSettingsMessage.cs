using Prospect.Engine.Net;

namespace Prospect.Game.Protocol;

/// <summary>Reliable local-host request, or authoritative broadcast, of the two mutable wreck settings.</summary>
public readonly record struct WreckSettingsMessage(int Minutes, int PerSector)
{
    /// <summary>Message id followed by two signed integers; no hull snapshot bytes are used.</summary>
    public const int MaxWireSize = 9;
    /// <summary>Valid wire bounds, also used by the settings controls.</summary>
    public bool IsValid => Minutes is >= 0 and <= 1440 && PerSector is >= 1 and <= 255;
    /// <summary>Writes a complete message.</summary>
    public void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.WreckSettings);
        writer.WriteInt32(Minutes);
        writer.WriteInt32(PerSector);
    }
    /// <summary>Reads the payload after its message id, refusing invalid values.</summary>
    public static bool TryRead(ref NetReader reader, out WreckSettingsMessage value)
    {
        value = new(reader.ReadInt32(), reader.ReadInt32());
        return reader.IsValid && value.IsValid;
    }
}
